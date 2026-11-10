using HuiYi.Localization;
using System.Text;

namespace HuiYi;

public sealed record Lang(string Name, string Code);

/// Language codes are the translation endpoint's ("zh-CN", "en", "ja"…), as in Cicada.
public static class Languages
{
    /// What the on-device recognizer (SenseVoice) can hear.
    public static readonly Lang[] Source =
    [
        new("中文", "zh-CN"),
        new("English", "en"),
        new("日本語", "ja"),
        new("한국어", "ko"),
        new("粤语", "yue"),
    ];

    public static readonly Lang[] Target =
    [
        new("简体中文", "zh-CN"),
        new("繁體中文", "zh-TW"),
        new("English", "en"),
        new("日本語", "ja"),
        new("한국어", "ko"),
        new("Español", "es"),
        new("Français", "fr"),
        new("Deutsch", "de"),
        new("Português", "pt"),
    ];

    /// Maps a system language tag ("zh-Hans-CN", "en-US", "zh-TW") to a translation code.
    public static string TranslationCode(string systemTag)
    {
        var parts = systemTag.Split('-');
        var lang = parts[0].ToLowerInvariant();
        if (lang != "zh") return lang;
        if (parts.Any(p => p.Equals("Hant", StringComparison.OrdinalIgnoreCase))) return "zh-TW";
        if (parts.Any(p => p.Equals("Hans", StringComparison.OrdinalIgnoreCase))) return "zh-CN";
        if (parts.Any(p => p is "TW" or "HK" or "MO")) return "zh-TW";
        return "zh-CN";
    }

    /// The reader's own language under "follow system": Chinese when the system list has it,
    /// otherwise the first system language. Subtitles, notes and speak-back all start from it.
    public static string MyLanguage(IReadOnlyList<string> prefs) =>
        Supported(prefs).FirstOrDefault(c => c.StartsWith("zh")) ?? Supported(prefs).FirstOrDefault() ?? "zh-CN";

    /// What "follow system" translates into: my language, unless that is what they speak; then the
    /// next system language, or between Chinese and English when the system has no other.
    public static string FollowSystemTarget(string? source, IReadOnlyList<string> prefs)
    {
        var mine = MyLanguage(prefs);
        if (source == null || !SameLanguage(source, mine)) return mine;
        return Supported(prefs).FirstOrDefault(c => !SameLanguage(c, mine))
               ?? (mine.StartsWith("zh") ? "en" : "zh-CN");
    }

    /// The system languages we can translate into, in the system's order. A language added for a
    /// keyboard or by accident (Albanian, say) must never become what the subtitles are written in.
    private static IEnumerable<string> Supported(IReadOnlyList<string> prefs) =>
        prefs.Select(TranslationCode).Where(c => Target.Any(t => t.Code == c));

    /// "zh-CN" and "zh-TW" are one spoken language: Chinese speech is never "translated" into Chinese.
    public static bool SameLanguage(string a, string b)
    {
        static string Base(string c) => c.StartsWith("zh") || c == "yue" ? "zh" : c.Split('-')[0];
        return Base(a) == Base(b);
    }

    public static string TargetName(string code) =>
        Target.FirstOrDefault(l => l.Code == code)?.Name ?? Source.FirstOrDefault(l => l.Code == code)?.Name ?? code;

    /// The language's name inside Chinese UI copy ("系统里没有英语语音").
    public static string ChineseName(string code) => L.T(code switch
    {
        "zh-CN" => "简体中文",
        "zh-TW" => "繁体中文",
        "yue" => "粤语",
        "en" => "英语",
        "ja" => "日语",
        "ko" => "韩语",
        "es" => "西班牙语",
        "fr" => "法语",
        "de" => "德语",
        "pt" => "葡萄牙语",
        _ => code,
    });

    /// CJK text is joined without spaces.
    public static string Joiner(string code) => code.StartsWith("zh") || code is "ja" or "yue" ? "" : " ";

    public static string EnglishName(string code) => code switch
    {
        "zh-CN" => "Simplified Chinese",
        "zh-TW" => "Traditional Chinese",
        "yue" => "Cantonese",
        "en" => "English",
        "ja" => "Japanese",
        "ko" => "Korean",
        "es" => "Spanish",
        "fr" => "French",
        "de" => "German",
        "pt" => "Portuguese",
        "auto" => "the spoken language",
        _ => code,
    };

    /// The language a recognized line is in, from SenseVoice's tag when there is one, else from its
    /// script: kana is Japanese, hangul Korean, other CJK Chinese, the rest English.
    public static string FromRecognizer(string? tag, string text)
    {
        switch (tag?.Trim('<', '|', '>'))
        {
            case "zh": return "zh-CN";
            case "en": return "en";
            case "ja": return "ja";
            case "ko": return "ko";
            case "yue": return "yue";
        }
        int kana = 0, hangul = 0, han = 0, latin = 0;
        foreach (var ch in text)
        {
            if (ch is >= '぀' and <= 'ヿ') kana++;
            else if (ch is >= '가' and <= '힯') hangul++;
            else if (ch is >= '一' and <= '鿿') han++;
            else if (char.IsLetter(ch)) latin++;
        }
        if (kana > 0 && kana * 4 >= han) return "ja";
        if (hangul > 0) return "ko";
        if (han > 0 && han * 2 >= latin / 4) return "zh-CN";
        return "en";
    }

    /// CJK share of the letters in `text`, for checking a reply is in the language asked for.
    public static (int Cjk, int Letters) CjkShare(string text)
    {
        int cjk = 0, letters = 0;
        foreach (var r in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(r)) continue;
            letters++;
            if (r.Value is >= 0x3040 and <= 0x9FFF or >= 0xAC00 and <= 0xD7AF) cjk++;
        }
        return (cjk, letters);
    }
}
