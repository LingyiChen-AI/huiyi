using System.Text.RegularExpressions;

namespace HuiYi;

/// The other side's finished lines before they are recorded and translated: tidied, and joined to
/// the line before when a pause cut one sentence in two. Ported from Cicada's HeardText.swift.
public static class HeardText
{
    private static readonly HashSet<char> Lead =
        ['.', ',', ';', ':', '!', '?', '…', '、', '，', '。', '；', '：', '！', '？', '-', '–', '—'];

    /// Speech recognition leaves stray marks where it hears breaths and hesitations: "in..........",
    /// ", , that". They start lines, and a translator turns them into "，……" or worse.
    public static string Clean(string text)
    {
        var s = text;
        s = Regex.Replace(s, @"\s*[.。]{2,}|\s*…+", "…");
        s = Regex.Replace(s, @"…(?:[\s,，]*…)+", "…");
        s = Regex.Replace(s, @"([,，])(?:\s*[,，])+", "$1");
        s = Regex.Replace(s, @"[,，]\s*…|…\s*[,，]", "…");
        s = Regex.Replace(s, @"\s+([,，.。…?？!！;；:：])", "$1");
        s = Regex.Replace(s, @"\s{2,}", " ");
        var i = 0;
        while (i < s.Length && (Lead.Contains(s[i]) || char.IsWhiteSpace(s[i]))) i++;
        return s[i..].Trim();
    }

    /// The longest pause, in seconds, that still joins two pieces of one sentence.
    public const double JoinGap = 1.5;
    /// A line this long takes nothing more: past it a translation is slow and the line hard to read.
    public const int JoinWords = 40;

    public static bool IsCjk(string s) => s.Any(c => c is >= '぀' and <= '鿿' or >= '가' and <= '힯');

    /// Whether `next` carries on `previous`, from the same voice, rather than starting a sentence.
    /// `cut`: the recognizer ended `previous` at a short pause, so its closing "." is a guess, not the
    /// speaker's: it is ignored when `next` starts in lower case.
    public static bool Continues(string previous, string next, double gap, bool cut)
    {
        if (gap < -0.5 || gap > JoinGap || previous.Length == 0 || next.Length == 0) return false;
        var size = IsCjk(previous) ? previous.Length / 2 : previous.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (size >= JoinWords) return false;
        return previous[^1] switch
        {
            '?' or '!' or '？' or '！' => false,
            '.' or '。' => cut && char.IsLower(next[0]),
            _ => true,
        };
    }

    public static string Join(string previous, string next)
    {
        var head = previous;
        if (head.EndsWith('.') && !head.EndsWith('…')) head = head[..^1];
        if (head.EndsWith('。')) head = head[..^1];
        return IsCjk(previous) ? head + next : head + " " + next;
    }
}
