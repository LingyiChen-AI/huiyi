using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HuiYi;

/// A free machine translator: no account, no key. `Code` turns one of our language codes
/// ("zh-CN", "en", "yue", "auto"…) into the service's, or null when it can't take that language.
public sealed record MachineEngine(
    string Id, string Name,
    Func<string, bool, string?> Code,
    Func<string, string, string, CancellationToken, Task<string>> Run);

/// The free translators the subtitles can use for the line being said (and for whole lines when no
/// AI service is set up). All are the public endpoints their own web pages and browser extensions
/// call; any of them may refuse or rate-limit us, so "auto" tries them in turn, fastest first.
public static class MachineTranslators
{
    public const string Auto = "auto";
    /// Not a free translator: the user's AI service for the line being said too (free ones
    /// stand in when it fails or none is set up).
    public const string Ai = "ai";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    static MachineTranslators() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");

    /// In the order "auto" tries them: measured from China, TranSmart ~0.4 s, Volcano ~0.5 s,
    /// Google ~0.3 s when it isn't refusing, Yandex ~1.2 s, Bing 2–5 s.
    public static readonly MachineEngine[] All =
    [
        new("transmart", "腾讯交互翻译", TranSmartCode, TranSmart),
        new("volcengine", "火山翻译", VolcCode, Volcengine),
        new("google", "Google 翻译", GoogleCode, Google),
        new("yandex", "Yandex 翻译", YandexCode, Yandex),
        new("bing", "必应翻译", BingCode, Bing),
    ];

    public static MachineEngine? ById(string id) => All.FirstOrDefault(e => e.Id == id);

    private static async Task<string> Read(HttpResponseMessage resp, string who, CancellationToken ct)
    {
        if (resp.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"{who} {(int)resp.StatusCode}", null, resp.StatusCode);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

    // MARK: Google (translate.googleapis.com, the gtx client)

    private static string? GoogleCode(string code, bool source) => code;

    private static async Task<string> Google(string text, string sl, string tl, CancellationToken ct)
    {
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t"
                  + $"&sl={Uri.EscapeDataString(sl)}&tl={Uri.EscapeDataString(tl)}&q={Uri.EscapeDataString(text)}";
        using var resp = await Http.GetAsync(url, ct);
        using var doc = JsonDocument.Parse(await Read(resp, "google", ct));
        var parts = new List<string>();
        foreach (var seg in doc.RootElement[0].EnumerateArray())
            if (seg.ValueKind == JsonValueKind.Array && seg[0].ValueKind == JsonValueKind.String) parts.Add(seg[0].GetString()!);
        return string.Concat(parts);
    }

    // MARK: Tencent TranSmart (transmart.qq.com)

    private static string? TranSmartCode(string code, bool source) => code switch
    {
        "zh-CN" => "zh",
        "zh-TW" => "zh-TW",
        // Cantonese isn't one of its languages, and detected as Chinese it reads it wrong (聽日,
        // tomorrow, came out "every day"): left to Volcano, which gets it right.
        "yue" => null,
        "auto" => source ? "auto" : null,
        _ => code,
    };

    private static readonly string TranSmartClient = $"browser-chrome-130.0.0-Windows-{Guid.NewGuid()}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

    private static async Task<string> TranSmart(string text, string sl, string tl, CancellationToken ct)
    {
        var body = new
        {
            header = new { fn = "auto_translation", client_key = TranSmartClient },
            type = "plain",
            model_category = "normal",
            source = new { lang = sl, text_list = new[] { text } },
            target = new { lang = tl },
        };
        using var resp = await Http.PostAsync("https://transmart.qq.com/api/imt", Json(body), ct);
        using var doc = JsonDocument.Parse(await Read(resp, "transmart", ct));
        if (!doc.RootElement.TryGetProperty("auto_translation", out var list))
            throw new InvalidDataException("transmart: " + (doc.RootElement.TryGetProperty("header", out var h) && h.TryGetProperty("ret_code", out var rc) ? rc.GetString() : "no translation"));
        return string.Concat(list.EnumerateArray().Select(x => x.GetString()));
    }

    // MARK: Volcano Translate (ByteDance; the browser extension's endpoint)

    private static string? VolcCode(string code, bool source) => code switch
    {
        "zh-CN" => "zh",
        "zh-TW" => "zh-Hant",
        // Given "yue" it hands the text back untranslated; left to detect, it reads Cantonese fine.
        "yue" or "auto" => source ? "" : null,
        _ => code,
    };

    private static async Task<string> Volcengine(string text, string sl, string tl, CancellationToken ct)
    {
        var body = new { source_language = sl, target_language = tl, text };
        using var resp = await Http.PostAsync("https://translate.volcengine.com/crx/translate/v1/", Json(body), ct);
        using var doc = JsonDocument.Parse(await Read(resp, "volcengine", ct));
        return doc.RootElement.TryGetProperty("translation", out var t) ? t.GetString() ?? "" : throw new InvalidDataException("volcengine: no translation");
    }

    // MARK: Yandex (the Android app's endpoint)

    private static string? YandexCode(string code, bool source) => code switch
    {
        "zh-CN" => "zh",
        // No Traditional Chinese to write in; read, it is Chinese.
        "zh-TW" => source ? "zh" : null,
        "yue" => "zh",
        "auto" => source ? "" : null,
        _ => code,
    };

    private static async Task<string> Yandex(string text, string sl, string tl, CancellationToken ct)
    {
        var url = $"https://translate.yandex.net/api/v1/tr.json/translate?id={Guid.NewGuid():N}-0-0&srv=android";
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["text"] = text, ["lang"] = sl.Length == 0 ? tl : $"{sl}-{tl}" });
        using var resp = await Http.PostAsync(url, form, ct);
        using var doc = JsonDocument.Parse(await Read(resp, "yandex", ct));
        return string.Concat(doc.RootElement.GetProperty("text").EnumerateArray().Select(x => x.GetString()));
    }

    // MARK: Bing (the web page's own endpoint: tokens read from the page, good for about an hour)

    private static string? BingCode(string code, bool source) => code switch
    {
        "zh-CN" => "zh-Hans",
        "zh-TW" => "zh-Hant",
        "pt" => "pt",
        "auto" => source ? "auto-detect" : null,
        _ => code,
    };

    private static (string Ig, string Key, string Token, DateTime Until)? bingSession;
    private static readonly SemaphoreSlim BingGate = new(1, 1);

    private static async Task<(string Ig, string Key, string Token)> BingSession(CancellationToken ct)
    {
        await BingGate.WaitAsync(ct);
        try
        {
            if (bingSession is { } s && DateTime.Now < s.Until) return (s.Ig, s.Key, s.Token);
            using var resp = await Http.GetAsync("https://www.bing.com/translator", ct);
            var page = await Read(resp, "bing", ct);
            var ig = Regex.Match(page, "IG:\"([^\"]+)\"");
            var abuse = Regex.Match(page, @"params_AbusePreventionHelper\s*=\s*\[(\d+),""([^""]+)""");
            if (!ig.Success || !abuse.Success) throw new InvalidDataException("bing: no session on the page");
            bingSession = (ig.Groups[1].Value, abuse.Groups[1].Value, abuse.Groups[2].Value, DateTime.Now.AddMinutes(50));
            return (ig.Groups[1].Value, abuse.Groups[1].Value, abuse.Groups[2].Value);
        }
        finally { BingGate.Release(); }
    }

    private static async Task<string> Bing(string text, string sl, string tl, CancellationToken ct)
    {
        var (ig, key, token) = await BingSession(ct);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["fromLang"] = sl, ["to"] = tl, ["text"] = text, ["token"] = token, ["key"] = key,
        });
        using var resp = await Http.PostAsync($"https://www.bing.com/ttranslatev3?isVertical=1&IG={ig}&IID=translator.5028", form, ct);
        var body = await Read(resp, "bing", ct);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            // An expired session answers with an error object: fetch a new one next time.
            bingSession = null;
            throw new InvalidDataException("bing: " + body[..Math.Min(80, body.Length)]);
        }
        return doc.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
    }
}
