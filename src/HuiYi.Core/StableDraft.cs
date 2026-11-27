namespace HuiYi;

/// The subtitle's translation only grows: words once read are not swapped for others. People talk in
/// long runs joined by commas, so a clause after a comma counts as a piece too. The recognizer puts a
/// full stop after every partial, so a piece has ended only when more follows it; and it keeps
/// redoing the punctuation (and now and then a word) of what came before, so the text is lined up by
/// its words, not character by character.
public static class StableDraft
{
    /// A piece ended must have at least this many letters: shorter ones translate badly on their own.
    public const int MinPiece = 6;
    /// The recognizer may redo a word of what was kept (大局 → 大吉大利); lining up at a piece end further
    /// off than this many letters would drop words or say them twice, so it starts afresh instead.
    public const int MaxDrift = 2;

    private static bool Ends(char c) => c is '。' or '！' or '？' or '；' or '，' or '、' or '.' or '!' or '?' or ';' or ',';

    public static int Letters(string text) => text.Count(char.IsLetterOrDigit);

    /// Where the ended pieces of `text` stop (just after the last end with words after it, holding at
    /// least MinPiece letters); 0 when all of it is still being said.
    public static int EndedUpTo(string text)
    {
        var lastWord = -1;
        for (var i = text.Length - 1; i >= 0; i--)
            if (char.IsLetterOrDigit(text[i])) { lastWord = i; break; }
        for (var i = lastWord - 1; i >= 0; i--)
            if (Ends(text[i]))
                return Letters(text[..i]) >= MinPiece ? i + 1 : 0;
        return 0;
    }

    /// Where `text` goes on after the words of `before`: right after them when they are all there,
    /// punctuation aside; else (a word heard again differently) at the piece end nearest to where they
    /// would end, if it is close. -1 when it can't be lined up.
    public static int After(string before, string text)
    {
        var want = new string(before.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (want.Length == 0) return -1;
        var matched = 0;
        var i = 0;
        var same = true;
        for (; i < text.Length && matched < want.Length; i++)
        {
            if (!char.IsLetterOrDigit(text[i])) continue;
            if (char.ToLowerInvariant(text[i]) != want[matched]) same = false;
            matched++;
        }
        if (matched < want.Length) return -1;
        if (same) return Skip(text, i);
        // Nearest piece end to where the kept words would stop.
        var best = -1;
        var bestDrift = int.MaxValue;
        for (var j = 0; j < text.Length; j++)
        {
            if (!Ends(text[j])) continue;
            var drift = Math.Abs(Letters(text[..j]) - want.Length);
            if (drift < bestDrift) { bestDrift = drift; best = j + 1; }
        }
        return best >= 0 && bestDrift <= MaxDrift ? Skip(text, best) : -1;
    }

    /// What `final` says after the words of `translatedFrom`: "" when nothing, null when it can't be
    /// lined up.
    public static string? Rest(string translatedFrom, string final)
    {
        var at = After(translatedFrom, final);
        if (at < 0) return null;
        var rest = final[at..];
        return rest.Any(char.IsLetterOrDigit) ? rest : "";
    }

    /// How far (in letters, a short word) a piece end may be from where the kept words would stop, for
    /// the last resort to line up there. More, and a draft lacking a word ("degrees") passed for whole.
    public const int MaxLooseDrift = 4;

    /// The last resort when a line ends and its kept words can't be lined up: what follows the piece end
    /// nearest to as many letters in as they had. A word may be dropped or said twice, but what was
    /// read stays on screen. Null when no piece end is that close: a draft of half the line (a slow AI
    /// answered while the speaker went on) would otherwise pass for all of it and stay (issue #8).
    public static string? RestLoosely(string translatedFrom, string final)
    {
        var want = Letters(translatedFrom);
        var best = -1;
        var bestDrift = int.MaxValue;
        for (var j = 0; j < final.Length; j++)
        {
            if (!Ends(final[j])) continue;
            var drift = Math.Abs(Letters(final[..j]) - want);
            if (drift < bestDrift) { bestDrift = drift; best = j + 1; }
        }
        if (best < 0 || bestDrift > MaxLooseDrift) return null;
        var rest = final[Skip(final, best)..];
        return rest.Any(char.IsLetterOrDigit) ? rest : "";
    }

    private static int Skip(string text, int at)
    {
        while (at < text.Length && (Ends(text[at]) || char.IsWhiteSpace(text[at]))) at++;
        return at;
    }

    public static string Join(string before, string next, string target) =>
        before.Length == 0 ? next : next.Length == 0 ? before : before + Languages.Joiner(target) + next;
}
