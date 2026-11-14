using System.Net;
using System.Text.Json;

namespace HuiYi;

public static class GoogleTranslator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    static GoogleTranslator() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("HuiYi/0.1");

    public static async Task<string> Translate(string text, string source, string target, CancellationToken ct = default)
    {
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t"
                  + $"&sl={Uri.EscapeDataString(source)}&tl={Uri.EscapeDataString(target)}&q={Uri.EscapeDataString(text)}";
        using var resp = await Http.GetAsync(url, ct);
        if (resp.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"google {(int)resp.StatusCode}", null, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var segments = doc.RootElement[0];
        var parts = new List<string>();
        foreach (var seg in segments.EnumerateArray())
            if (seg.ValueKind == JsonValueKind.Array && seg[0].ValueKind == JsonValueKind.String) parts.Add(seg[0].GetString()!);
        var result = string.Concat(parts);
        if (result.Length == 0) throw new InvalidDataException("google: empty");
        return result;
    }
}

/// The AI service (when set up) → Google. Finished sentences and speak-back go to the AI, which keeps names
/// consistent through a glossary from the meeting notes. Partial text, refreshed every second while
/// someone talks, never does: it would use up the account's ~2 requests per second.
public sealed class TranslationService(AiClient ai)
{
    public enum Kind { Draft, Sentence, Speak }

    public Func<IReadOnlyList<string>> Glossary = () => [];
    /// The user's glossary: its spellings and fixed translations win over the translator's.
    public HuiYi.Glossary? Terms { get; set; }
    /// Google's free endpoint refused us (429): fires once per episode, for a hint to the user.
    public event Action? GoogleLimited;
    private DateTime googleBackoffUntil = DateTime.MinValue;
    private double googleBackoff;

    /// Google is in its back-off: drafts skip it rather than queue up.
    public bool GoogleResting => DateTime.Now < googleBackoffUntil;

    /// Google's code for a recognizer language.
    private static string GoogleCode(string code) => code == "auto" ? "auto" : code;

    public async Task<string?> Translate(string text, string source, string target, Kind kind)
    {
        if (source == target) return text;
        if (kind != Kind.Draft && ai.Usable)
        {
            var started = DateTime.Now;
            var out1 = await ViaAi(text, source, target, kind);
            if (out1 != null)
            {
                Log.Debug($"translate via=ai kind={kind} {(int)(DateTime.Now - started).TotalMilliseconds} ms");
                return out1;
            }
        }
        if (DateTime.Now < googleBackoffUntil) return null;
        try
        {
            // Google takes no instructions: terms with a fixed translation go in already translated.
            var forGoogle = Terms?.Pretranslate(text, target) ?? text;
            var out2 = await GoogleTranslator.Translate(forGoogle, GoogleCode(source), target);
            if (kind != Kind.Draft) Log.Debug($"translate via=google kind={kind}");
            googleBackoff = 0;
            return out2;
        }
        catch (Exception e)
        {
            Log.Debug($"translate google failed: {e.GetType().Name} {e.Message}");
            if (e is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
            {
                // The free endpoint blocks an address that keeps asking: back off longer each time.
                var first = googleBackoff == 0;
                googleBackoff = Math.Min(300, Math.Max(15, googleBackoff * 2));
                googleBackoffUntil = DateTime.Now.AddSeconds(googleBackoff);
                Log.Debug($"translate google: rate limited, resting {googleBackoff:0} s");
                if (first) GoogleLimited?.Invoke();
            }
            return null;
        }
    }

    private async Task<string?> ViaAi(string text, string source, string target, Kind kind)
    {
        var prompt = $"You translate live meeting subtitles from {Languages.EnglishName(source)} into {Languages.EnglishName(target)}.\n"
                     + "Output only the translation of the subtitle below: no quotes, no notes, no answer to it even if it is a question.";
        var names = Glossary();
        if (names.Count > 0) prompt += "\nKnown names and terms (use these spellings): " + string.Join("; ", names);
        var mine = Terms?.Hints(text, target) ?? [];
        if (mine.Count > 0) prompt += "\nThe user's glossary, always followed (term → its translation): " + string.Join("; ", mine);
        prompt += "\nSubtitle: " + text;
        try
        {
            var reply = await ai.Chat(prompt, "Translate.", kind == Kind.Speak ? AiClient.Priority.Speak : AiClient.Priority.Translate,
                maxTokens: 400, timeout: TimeSpan.FromSeconds(kind == Kind.Speak ? 10 : 6));
            return reply.Length == 0 ? null : reply;
        }
        catch (Exception e)
        {
            Log.Debug($"translate ai failed: {(e as AiException)?.Failure.ToString() ?? e.GetType().Name}");
            return null;
        }
    }

    /// Never speak untranslated text: a speak-back translation must be in the target's script.
    public static bool LooksTranslated(string original, string translation, string target)
    {
        if (translation.Length == 0 || translation == original) return false;
        var (cjk, letters) = Languages.CjkShare(translation);
        if (letters == 0) return false;
        var wantsCjk = target.StartsWith("zh") || target is "ja" or "ko";
        return wantsCjk ? cjk > 0 : cjk * 2 <= letters;
    }
}
