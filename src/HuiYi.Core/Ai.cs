using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HuiYi;

/// API keys, one file per AI service under %APPDATA%\HuiYi\keys, DPAPI-encrypted for this Windows
/// user. Never in settings, logs or exports.
public static class AIKey
{
    private static readonly Dictionary<string, string?> Cache = new();
    private static readonly byte[] Entropy = "HuiYi.ai-key"u8.ToArray();
    private static string Folder => Path.Combine(Paths.Roaming, "keys");
    private static string FileOf(string providerId) => Path.Combine(Folder, providerId + ".key");

    public static string? Read(string providerId)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(providerId, out var hit)) return hit;
            string? key = null;
            try
            {
                if (File.Exists(FileOf(providerId)))
                {
                    var plain = ProtectedData.Unprotect(File.ReadAllBytes(FileOf(providerId)), Entropy, DataProtectionScope.CurrentUser);
                    key = Encoding.UTF8.GetString(plain).Trim();
                    if (key.Length == 0) key = null;
                }
            }
            catch
            {
                Log.Debug("ai key: could not read a key file");
            }
            Cache[providerId] = key;
            return key;
        }
    }

    public static bool Save(string providerId, string key)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(FileOf(providerId), ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));
            lock (Cache) Cache[providerId] = key;
            return true;
        }
        catch
        {
            Log.Debug("ai key: save failed");
            return false;
        }
    }

    public static void Remove(string providerId)
    {
        try { File.Delete(FileOf(providerId)); } catch { }
        lock (Cache) Cache[providerId] = null;
    }

    /// "sk-…3f9a" for display.
    public static string Masked(string key) => key.Length <= 8 ? "••••" : key[..3] + new string('•', 12) + key[^4..];
}

public enum AiFailure { NoKey, InvalidKey, RateLimited, Hijacked, Timeout, Http, Network, Parse }

public sealed class AiException(AiFailure failure, int code = 0) : Exception($"ai: {failure}{(code > 0 ? $" {code}" : "")}")
{
    public AiFailure Failure { get; } = failure;
}

/// The AI service in use (see `AiProvider`): any OpenAI-compatible chat completions API. One shared
/// instance: request starts go through one gate in priority order (speak-back, translation, notes),
/// spaced as the platform needs (Ling allows ~2 a second). Deep thinking is turned off where the
/// platform has it, and the content goes in the system message with a fixed user instruction, which
/// keeps models translating and summarising instead of answering what was said.
public sealed class AiClient
{
    public enum Priority { Speak = 0, Translate = 1, Notes = 2 }
    public enum KeyState { None, Unchecked, Checking, Ok, Invalid, Unreachable }

    private static readonly string[] HijackPrefixes = ["根据现有检索证据", "暂未找到足够可靠的信息", "根据检索"];
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// The service in use, from settings; nil when none is set up.
    public Func<AiProvider?> Provider = () => null;

    public KeyState State { get; private set; } = KeyState.Unchecked;
    public double CheckSeconds { get; private set; }
    public string? CheckError { get; private set; }
    public DateTime? RateLimitedAt { get; private set; }
    public event Action? Changed;

    public AiProvider? Current => Provider() is { } p && p.BaseUrl.Length > 0 && p.Model.Length > 0 ? p : null;
    public string? Key => Current is { } p ? AIKey.Read(p.Id) : null;
    /// A service is set up and can be called (Ollama needs no key).
    public bool HasKey => Current is { } p && (p.Info.KeyOptional || AIKey.Read(p.Id) != null);
    public bool Usable => HasKey && State != KeyState.Invalid;
    public bool HasMingVoices => Current?.Info.HasMingVoices == true && Usable;
    /// "DeepSeek · deepseek-chat", for the UI.
    public string Describe => Current is { } p ? $"{p.Name} · {p.Model}" : "未设置";

    public AiClient() => Refresh();

    /// The service or its key changed: start over from "not checked".
    public void Refresh()
    {
        State = !HasKey ? KeyState.None : KeyState.Unchecked;
        CheckError = null;
        Changed?.Invoke();
    }

    private void SetState(KeyState s)
    {
        State = s;
        Changed?.Invoke();
    }

    /// Checks the service with the cheapest possible request to its model.
    public async Task Check()
    {
        if (Current is not { } p || !HasKey) { SetState(KeyState.None); return; }
        SetState(KeyState.Checking);
        var started = DateTime.Now;
        var body = Body(p, new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }), 1);
        try
        {
            await Post(p, "chat/completions", body, TimeSpan.FromSeconds(20));
            CheckSeconds = (DateTime.Now - started).TotalSeconds;
            CheckError = null;
            SetState(KeyState.Ok);
        }
        catch (AiException e) when (e.Failure == AiFailure.InvalidKey) { CheckError = null; SetState(KeyState.Invalid); }
        catch (AiException e) when (e.Failure == AiFailure.RateLimited) { SetState(KeyState.Ok); }
        catch (AiException e) when (e.Failure == AiFailure.Http)
        {
            // Often a wrong model name (404/400): say so rather than "unreachable".
            CheckError = e.Message.Contains("404") || e.Message.Contains("400") ? "模型名可能不对，请检查或获取模型列表" : $"服务返回错误（{e.Message[4..]}）";
            SetState(KeyState.Unreachable);
        }
        catch
        {
            CheckError = "连不上这个服务，请检查 Base URL、网络或代理";
            SetState(KeyState.Unreachable);
        }
        Log.Debug($"ai: check {p.Preset} → {State}");
    }

    /// The models the service offers (GET /models), for picking one.
    public static async Task<List<string>> ListModels(AiProvider p)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseOf(p), "models"));
        if (AIKey.Read(p.Id) is { } key) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var resp = await Http.SendAsync(req, cts.Token);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new AiException(AiFailure.InvalidKey);
        if (!resp.IsSuccessStatusCode) throw new AiException(AiFailure.Http, (int)resp.StatusCode);
        var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        var list = (root?["data"] as JsonArray ?? root?["models"] as JsonArray ?? [])
            .Select(m => m?["id"]?.GetValue<string>() ?? m?["name"]?.GetValue<string>())
            .Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct().ToList();
        // Chat models first: leave out what is plainly for images, video, speech or embeddings.
        string[] other = ["image", "video", "embed", "rerank", "tts", "asr", "whisper", "dall-e", "seedream", "seedance", "speech",
            "audio", "vision-exp", "ocr", "bge", "kling", "happyhorse", "vidu", "tripo", "wan2", "wan3", "easydoc", "-custom",
            ":batch", "-batch", "moderation"];
        return list.Where(id => !other.Any(o => id.Contains(o, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Uri BaseOf(AiProvider p) => new(p.BaseUrl.TrimEnd('/') + "/");

    private static JsonObject Body(AiProvider p, JsonArray messages, int maxTokens, bool json = false)
    {
        var body = new JsonObject
        {
            ["model"] = p.Model,
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.2,
            ["stream"] = false,
        };
        // Only the parameters this platform knows: others reject what they don't.
        if (p.Info.Extras?.Invoke() is { } extras)
            foreach (var (k, v) in extras) body[k] = v?.DeepClone();
        if (json) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        return body;
    }

    /// One chat completion. `content` goes in the system message, `instruction` is the fixed user
    /// message. Notes retry 429s with backoff; translation and speak-back fail fast so the caller can
    /// fall back to another translator.
    public async Task<string> Chat(string content, string instruction, Priority priority, bool json = false,
        int maxTokens = 1024, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (Current is not { } p || !HasKey) throw new AiException(AiFailure.NoKey);
        if (State == KeyState.Invalid) throw new AiException(AiFailure.InvalidKey);
        var body = Body(p, new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = content },
            new JsonObject { ["role"] = "user", ["content"] = instruction }), maxTokens, json);
        var attempts = priority == Priority.Notes ? 4 : 1;
        var delay = 1.0;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            await Acquire(priority, p.Info.Spacing, ct);
            var started = DateTime.Now;
            try
            {
                var root = await Post(p, "chat/completions", body, timeout ?? TimeSpan.FromSeconds(30), ct);
                var choice = root["choices"]?[0];
                var text = choice?["message"]?["content"]?.GetValue<string>() ?? throw new AiException(AiFailure.Parse);
                var finish = choice?["finish_reason"]?.GetValue<string>();
                var trimmed = StripThinking(text).Trim();
                if (p.Info.ChecksHijack && (finish == null || HijackPrefixes.Any(trimmed.StartsWith)))
                {
                    Log.Debug($"ai: {priority} search-style reply dropped");
                    throw new AiException(AiFailure.Hijacked);
                }
                Log.Debug($"ai: {p.Preset} {priority} ok {(int)(DateTime.Now - started).TotalMilliseconds} ms");
                RateLimitedAt = null;
                return trimmed;
            }
            catch (AiException e) when (e.Failure == AiFailure.RateLimited && attempt < attempts)
            {
                var wait = delay + Random.Shared.NextDouble() * 0.5;
                Log.Debug($"ai: {priority} 429, retry in {wait:F1} s");
                await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                delay *= 2;
            }
            catch (AiException e) when (e.Failure == AiFailure.RateLimited)
            {
                RateLimitedAt = DateTime.Now;
                throw;
            }
        }
        throw new AiException(AiFailure.RateLimited);
    }

    /// Reasoning models that can't be told to skip thinking put it in <think>…</think>: drop it.
    internal static string StripThinking(string text)
    {
        var end = text.IndexOf("</think>", StringComparison.Ordinal);
        return text.TrimStart().StartsWith("<think>", StringComparison.Ordinal) && end >= 0 ? text[(end + 8)..] : text;
    }

    /// Ming's voice for `text` (Ant Ling only) as raw 24 kHz mono 16-bit PCM chunks, streamed.
    /// `language` is Ming's `language_id`, needed for Japanese, Korean, German, French and Spanish.
    public async IAsyncEnumerable<byte[]> Speech(string text, string voice, string? language,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Current is not { Info.HasMingVoices: true } p || AIKey.Read(p.Id) is not { } key) throw new AiException(AiFailure.NoKey);
        await Acquire(Priority.Speak, p.Info.Spacing, ct);
        var body = new JsonObject
        {
            ["model"] = "Ming-3.0-tts-0.5B", ["input"] = text, ["voice"] = voice,
            ["response_format"] = "pcm", ["sample_rate"] = 24000, ["stream"] = true, ["stream_format"] = "sse",
        };
        if (language != null) body["language_id"] = language;
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseOf(p), "audio/speech"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        switch (resp.StatusCode)
        {
            case HttpStatusCode.OK: break;
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                SetState(KeyState.Invalid);
                throw new AiException(AiFailure.InvalidKey);
            case HttpStatusCode.TooManyRequests: throw new AiException(AiFailure.RateLimited);
            default: throw new AiException(AiFailure.Http, (int)resp.StatusCode);
        }
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));
        while (await reader.ReadLineAsync(cts.Token) is { } line)
        {
            if (!line.StartsWith("data:")) continue;
            JsonNode? obj;
            try { obj = JsonNode.Parse(line[5..]); } catch { continue; }
            if (obj?["type"]?.GetValue<string>() == "speech.audio.done") break;
            if (obj?["audio"]?.GetValue<string>() is { } b64) yield return Convert.FromBase64String(b64);
        }
    }

    private async Task<JsonNode> Post(AiProvider p, string path, JsonObject body, TimeSpan timeout, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseOf(p), path));
        if (AIKey.Read(p.Id) is { } key) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiException(AiFailure.Timeout);
        }
        catch (HttpRequestException e)
        {
            Log.Debug($"ai: network error {e.HttpRequestError}");
            throw new AiException(AiFailure.Network);
        }
        using (resp)
        {
            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK: break;
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    if (State != KeyState.Invalid) SetState(KeyState.Invalid);
                    throw new AiException(AiFailure.InvalidKey);
                case HttpStatusCode.TooManyRequests:
                    throw new AiException(AiFailure.RateLimited);
                default:
                    Log.Debug($"ai: HTTP {(int)resp.StatusCode}");
                    throw new AiException(AiFailure.Http, (int)resp.StatusCode);
            }
            var text = await resp.Content.ReadAsStringAsync(ct);
            try { return JsonNode.Parse(text) ?? throw new AiException(AiFailure.Parse); }
            catch (JsonException) { throw new AiException(AiFailure.Parse); }
        }
    }

    // Start gate: request starts spaced as the platform allows, the most urgent waiting one first.
    private readonly object gate = new();
    private DateTime lastStart = DateTime.MinValue;
    private readonly List<(Priority P, long Seq, TaskCompletionSource Tcs)> waiting = [];
    private long seq;
    private bool pumping;
    private TimeSpan spacing = TimeSpan.FromSeconds(0.25);

    private Task Acquire(Priority p, double seconds, CancellationToken ct)
    {
        lock (gate)
        {
            spacing = TimeSpan.FromSeconds(seconds);
            if (waiting.Count == 0 && DateTime.Now - lastStart >= spacing)
            {
                lastStart = DateTime.Now;
                return Task.CompletedTask;
            }
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting.Add((p, ++seq, tcs));
            Pump();
            return tcs.Task.WaitAsync(ct);
        }
    }

    private void Pump()
    {
        if (pumping || waiting.Count == 0) return;
        pumping = true;
        var wait = spacing - (DateTime.Now - lastStart);
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        _ = Task.Delay(wait).ContinueWith(_ =>
        {
            lock (gate)
            {
                pumping = false;
                if (waiting.Count == 0) return;
                var next = waiting.OrderBy(w => w.P).ThenBy(w => w.Seq).First();
                waiting.Remove(next);
                lastStart = DateTime.Now;
                next.Tcs.TrySetResult();
                Pump();
            }
        });
    }
}
