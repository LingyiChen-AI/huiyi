namespace HuiYi;

/// The floating subtitle's translation only grows: words once read are not swapped for others. The
/// recognizer puts a full stop after every partial, so a sentence counts as ended only when more
/// follows it.
public static class StableDraft
{
    private static bool Ends(char c) => c is '。' or '！' or '？' or '；' or '.' or '!' or '?' or ';';

    /// Where the ended sentences of `text` stop (just after the last end with words after it); 0 when
    /// the whole of it is still the sentence being said.
    public static int EndedUpTo(string text)
    {
        var lastWord = -1;
        for (var i = text.Length - 1; i >= 0; i--)
            if (char.IsLetterOrDigit(text[i])) { lastWord = i; break; }
        for (var i = lastWord - 1; i >= 0; i--)
            if (Ends(text[i])) return i + 1;
        return 0;
    }

    /// What `final` says after the words of `translatedFrom` (punctuation and spacing aside): "" when
    /// nothing, null when `final` doesn't begin with them (the recognizer heard those words again
    /// differently).
    public static string? Rest(string translatedFrom, string final)
    {
        var want = new string(translatedFrom.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (want.Length == 0) return null;
        var matched = 0;
        var i = 0;
        for (; i < final.Length && matched < want.Length; i++)
        {
            if (!char.IsLetterOrDigit(final[i])) continue;
            if (char.ToLowerInvariant(final[i]) != want[matched]) return null;
            matched++;
        }
        if (matched < want.Length) return null;
        var rest = final[i..].TrimStart(' ', '，', ',', '。', '.', '、', '！', '!', '？', '?', '；', ';');
        return rest.Any(char.IsLetterOrDigit) ? rest : "";
    }

    public static string Join(string before, string next, string target) =>
        before.Length == 0 ? next : next.Length == 0 ? before : before + Languages.Joiner(target) + next;
}
