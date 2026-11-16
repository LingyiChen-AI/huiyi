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

    /// The line being said, as shown: the recognizer re-hears only the last few seconds, so each new
    /// partial drops words from its start. Laid over what is already shown where they overlap, those
    /// words stay on screen instead of vanishing from the middle of the subtitle. With no overlap
    /// found the new partial is taken as it is.
    public static string Stitch(string shown, string next)
    {
        if (shown.Length == 0 || next.Length == 0) return next;
        var (s, sAt) = Letters(shown);
        var (n, nAt) = Letters(next);
        var size = IsCjk(next) ? 4 : 8;
        if (n.Length < size) return next;
        // The overlap may begin a word or two into the new partial (its first word misheard).
        for (var skip = 0; skip <= 3 && skip + size <= n.Length; skip++)
        {
            var at = s.LastIndexOf(n.Substring(skip, size), StringComparison.Ordinal);
            if (at < 0) continue;
            return at == 0 && skip == 0 ? next : shown[..sAt[at]] + next[nAt[skip]..];
        }
        return next;
    }

    /// Letters and digits only, lower case, with where each came from in the text.
    private static (string Text, int[] At) Letters(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var at = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsLetterOrDigit(text[i])) continue;
            sb.Append(char.ToLowerInvariant(text[i]));
            at.Add(i);
        }
        return (sb.ToString(), at.ToArray());
    }

    public static string Join(string previous, string next)
    {
        var head = previous;
        if (head.EndsWith('.') && !head.EndsWith('…')) head = head[..^1];
        if (head.EndsWith('。')) head = head[..^1];
        return IsCjk(previous) ? head + next : head + " " + next;
    }
}
