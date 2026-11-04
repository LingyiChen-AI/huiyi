using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HuiYi;

/// The user's Ling API key: only in %APPDATA%\HuiYi\ling-api-key, encrypted with DPAPI for
/// this Windows user. Never in settings, logs or exports.
public static class AIKey
{
    private static string? cached;
    private static bool loaded;
    private static readonly byte[] Entropy = "HuiYi.ling"u8.ToArray();
    private static string File => Path.Combine(Paths.Roaming, "ling-api-key");

    public static string? Read()
    {
        if (loaded) return cached;
        loaded = true;
        try
        {
            if (System.IO.File.Exists(File))
            {
                var plain = ProtectedData.Unprotect(System.IO.File.ReadAllBytes(File), Entropy, DataProtectionScope.CurrentUser);
                var key = Encoding.UTF8.GetString(plain).Trim();
                cached = key.Length == 0 ? null : key;
            }
        }
        catch
        {
            Log.Debug("ai key: could not read the key file");
            cached = null;
        }
        return cached;
    }

    public static bool Save(string key)
    {
        try
        {
            Directory.CreateDirectory(Paths.Roaming);
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser);
            System.IO.File.WriteAllBytes(File, data);
            cached = key;
            loaded = true;
            return true;
        }
        catch
        {
            Log.Debug("ai key: save failed");
            return false;
        }
    }

    public static void Remove()
    {
        try { System.IO.File.Delete(File); } catch { }
        cached = null;
        loaded = true;
    }

    /// "sk-…3f9a" for display.
    public static string Masked(string key) => key.Length <= 8 ? "sk-…" : key[..3] + new string('•', 12) + key[^4..];
}

public enum LingFailure { NoKey, InvalidKey, RateLimited, Hijacked, Timeout, Http, Network, Parse }

public sealed class LingException(LingFailure failure, int code = 0) : Exception($"ling: {failure}{(code > 0 ? $" {code}" : "")}")
{
    public LingFailure Failure { get; } = failure;
}

/// Ling (ant-ling.com), OpenAI-compatible. One shared instance: the account allows about 2 requests
/// per second across every model, so request starts go through one gate (≥ 0.6 s apart) in priority
/// order: speak-back, translation, notes. `thinking` is always disabled, and the content goes in the
/// system message with a fixed user instruction (otherwise the gateway answers questions with a
/// search essay). Ported from Cicada's Ling.swift.
public sealed class LingClient
{
    public enum Priority { Speak = 0, Translate = 1, Notes = 2 }
    public enum KeyState { None, Unchecked, Checking, Ok, Invalid, Unreachable }

    public static readonly string[] Models = ["Ling-3.1-flash", "Ling-3.0-flash"];
    private static readonly Uri Base = new("https://api.ant-ling.com/v1/");
    private static readonly string[] HijackPrefixes = ["根据现有检索证据", "暂未找到足够可靠的信息", "根据检索"];
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public KeyState State { get; private set; }
    public double CheckSeconds { get; private set; }
    public DateTime? RateLimitedAt { get; private set; }
    public Func<string> Model = () => "Ling-3.1-flash";
    public event Action? Changed;

    public string? Key => AIKey.Read();
    public bool HasKey => Key != null;
    public bool Usable => HasKey && State != KeyState.Invalid;

    public LingClient() => State = AIKey.Read() == null ? KeyState.None : KeyState.Unchecked;

    public async Task SetKey(string raw)
    {
        var k = raw.Trim();
        if (k.Length == 0) return;
        AIKey.Save(k);
        await Check();
    }

    public void RemoveKey()
    {
        AIKey.Remove();
        SetState(KeyState.None);
    }

    private void SetState(KeyState s)
    {
        State = s;
        Changed?.Invoke();
    }

    public async Task Check()
    {
        if (Key is not { } key) { SetState(KeyState.None); return; }
        SetState(KeyState.Checking);
        var started = DateTime.Now;
        var body = new JsonObject
        {
            ["model"] = "Ling-3.0-tiny",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
            ["max_tokens"] = 1,
            ["thinking"] = new JsonObject { ["type"] = "disabled" },
        };
        try
        {
            await Post("chat/completions", body, key, TimeSpan.FromSeconds(15));
            CheckSeconds = (DateTime.Now - started).TotalSeconds;
            SetState(KeyState.Ok);
        }
        catch (LingException e) when (e.Failure == LingFailure.InvalidKey) { SetState(KeyState.Invalid); }
        catch (LingException e) when (e.Failure == LingFailure.RateLimited) { SetState(KeyState.Ok); }
        catch { SetState(KeyState.Unreachable); }
        Log.Debug($"ling: key check → {State}");
    }

    /// One chat completion. `content` goes in the system message, `instruction` is the fixed user
    /// message. Notes retry 429s with backoff; translation and speak-back fail fast so the caller can
    /// fall back to another translator.
    public async Task<string> Chat(string content, string instruction, Priority priority, bool json = false,
        int maxTokens = 1024, TimeSpan? timeout = null, string? model = null, CancellationToken ct = default)
    {
        if (Key is not { } key) throw new LingException(LingFailure.NoKey);
        if (State == KeyState.Invalid) throw new LingException(LingFailure.InvalidKey);
        var body = new JsonObject
        {
            ["model"] = model ?? Model(),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = content },
                new JsonObject { ["role"] = "user", ["content"] = instruction }),
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.2,
            ["thinking"] = new JsonObject { ["type"] = "disabled" },
            ["stream"] = false,
        };
        if (json) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        var attempts = priority == Priority.Notes ? 4 : 1;
        var delay = 1.0;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            await Acquire(priority, ct);
            var started = DateTime.Now;
            try
            {
                var root = await Post("chat/completions", body, key, timeout ?? TimeSpan.FromSeconds(30), ct);
                var choice = root["choices"]?[0];
                var text = choice?["message"]?["content"]?.GetValue<string>() ?? throw new LingException(LingFailure.Parse);
                var finish = choice?["finish_reason"]?.GetValue<string>();
                var trimmed = text.Trim();
                if (finish == null || HijackPrefixes.Any(trimmed.StartsWith))
                {
                    Log.Debug($"ling: {priority} search-style reply dropped");
                    throw new LingException(LingFailure.Hijacked);
                }
                Log.Debug($"ling: {priority} ok {(int)(DateTime.Now - started).TotalMilliseconds} ms");
                RateLimitedAt = null;
                return trimmed;
            }
            catch (LingException e) when (e.Failure == LingFailure.RateLimited && attempt < attempts)
            {
                var wait = delay + Random.Shared.NextDouble() * 0.5;
                Log.Debug($"ling: {priority} 429, retry in {wait:F1} s");
                await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                delay *= 2;
            }
            catch (LingException e) when (e.Failure == LingFailure.RateLimited)
            {
                RateLimitedAt = DateTime.Now;
                throw;
            }
        }
        throw new LingException(LingFailure.RateLimited);
    }

    /// Ming's voice for `text` as raw 24 kHz mono 16-bit PCM chunks, streamed. `language` is Ming's
    /// `language_id`, needed for Japanese, Korean, German, French and Spanish.
    public async IAsyncEnumerable<byte[]> Speech(string text, string voice, string? language,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Key is not { } key) throw new LingException(LingFailure.NoKey);
        await Acquire(Priority.Speak, ct);
        var body = new JsonObject
        {
            ["model"] = "Ming-3.0-tts-0.5B", ["input"] = text, ["voice"] = voice,
            ["response_format"] = "pcm", ["sample_rate"] = 24000, ["stream"] = true, ["stream_format"] = "sse",
        };
        if (language != null) body["language_id"] = language;
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, "audio/speech"));
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
                throw new LingException(LingFailure.InvalidKey);
            case HttpStatusCode.TooManyRequests: throw new LingException(LingFailure.RateLimited);
            default: throw new LingException(LingFailure.Http, (int)resp.StatusCode);
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

    private async Task<JsonNode> Post(string path, JsonObject body, string key, TimeSpan timeout, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
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
            throw new LingException(LingFailure.Timeout);
        }
        catch (HttpRequestException e)
        {
            Log.Debug($"ling: network error {e.HttpRequestError}");
            throw new LingException(LingFailure.Network);
        }
        using (resp)
        {
            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK: break;
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    if (State != KeyState.Invalid) SetState(KeyState.Invalid);
                    throw new LingException(LingFailure.InvalidKey);
                case HttpStatusCode.TooManyRequests:
                    throw new LingException(LingFailure.RateLimited);
                default:
                    Log.Debug($"ling: HTTP {(int)resp.StatusCode}");
                    throw new LingException(LingFailure.Http, (int)resp.StatusCode);
            }
            var text = await resp.Content.ReadAsStringAsync(ct);
            try { return JsonNode.Parse(text) ?? throw new LingException(LingFailure.Parse); }
            catch (JsonException) { throw new LingException(LingFailure.Parse); }
        }
    }

    // Start gate: request starts ≥ 0.6 s apart, the most urgent waiting request first.
    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(0.6);
    private readonly object gate = new();
    private DateTime lastStart = DateTime.MinValue;
    private readonly List<(Priority P, long Seq, TaskCompletionSource Tcs)> waiting = [];
    private long seq;
    private bool pumping;

    private Task Acquire(Priority p, CancellationToken ct)
    {
        lock (gate)
        {
            if (waiting.Count == 0 && DateTime.Now - lastStart >= Spacing)
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
        var wait = Spacing - (DateTime.Now - lastStart);
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
