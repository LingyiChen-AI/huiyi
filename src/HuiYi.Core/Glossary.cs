using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HuiYi;

/// One word the user wants heard, written and translated their way: a person's name, a product,
/// an in-house term the recognizer gets wrong.
public sealed class GlossaryTerm
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    /// How it is written ("伊丽静", "Kubernetes", "网关").
    public string Text { get; set; } = "";
    /// What the recognizer writes instead ("毕丽静", "伊利静"): always replaced.
    public List<string> Misheard { get; set; } = [];
    /// Its fixed translation per language code ("en" → "gateway").
    public Dictionary<string, string> Translations { get; set; } = [];
    public string Note { get; set; } = "";
    /// Only the listed misheard spellings are replaced, never sound-alikes.
    public bool ExactOnly { get; set; }
    public bool Enabled { get; set; } = true;

    public GlossaryTerm Clone() => new()
    {
        Id = Id, Text = Text, Misheard = [.. Misheard], Translations = new(Translations), Note = Note,
        ExactOnly = ExactOnly, Enabled = Enabled,
    };

    /// "gateway；ja=ゲートウェイ": a bare value is English, others carry their language code.
    public static Dictionary<string, string> ParseTranslations(string s)
    {
        var d = new Dictionary<string, string>();
        foreach (var part in s.Split([';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Regex.Match(part, @"^([a-zA-Z]{2,3}(?:-[A-Za-z]{2,4})?)\s*[=＝:：]\s*(.+)$");
            if (m.Success) d[m.Groups[1].Value.ToLowerInvariant()] = m.Groups[2].Value.Trim();
            else d["en"] = part;
        }
        return d;
    }

    public static string FormatTranslations(Dictionary<string, string> d) =>
        string.Join("; ", d.OrderBy(kv => kv.Key == "en" ? 0 : 1).ThenBy(kv => kv.Key)
            .Select(kv => kv.Key == "en" ? kv.Value : $"{kv.Key}={kv.Value}"));

    public static List<string> ParseList(string s) =>
        s.Split([',', '，', '、', '|', ';', '；', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct().ToList();

    /// Its translation for a target language ("en-US" finds "en").
    public string? TranslationFor(string target)
    {
        if (Translations.Count == 0 || target.Length == 0) return null;
        var t = target.ToLowerInvariant();
        if (Translations.TryGetValue(t, out var v)) return v;
        var head = t.Split('-')[0];
        return Translations.FirstOrDefault(kv => kv.Key.Split('-')[0] == head).Value;
    }
}

/// The user's glossary, kept in %APPDATA%\HuiYi\glossary.json for every meeting. Recognized text is
/// corrected against it (TermFixer), translations keep its spellings and fixed translations, and
/// notes are written with its words. Thread-safe.
public sealed class Glossary
{
    private readonly object gate = new();
    private readonly string path;
    private List<GlossaryTerm> terms = [];
    private TermFixer fixer = new([]);
    public event Action? Changed;

    public Glossary(string? path = null)
    {
        this.path = path ?? Path.Combine(Paths.Roaming, "glossary.json");
        Load();
    }

    public IReadOnlyList<GlossaryTerm> Terms { get { lock (gate) return terms.Select(t => t.Clone()).ToList(); } }

    private void Load()
    {
        try
        {
            if (File.Exists(path))
                terms = JsonSerializer.Deserialize<List<GlossaryTerm>>(File.ReadAllText(path), Json.Options) ?? [];
        }
        catch (Exception e)
        {
            Log.Debug($"glossary: can't read {e.Message}");
            terms = [];
        }
        fixer = new TermFixer(terms);
    }

    private void Commit()
    {
        fixer = new TermFixer(terms);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(terms, Json.Options));
            File.Move(tmp, path, true);
        }
        catch (Exception e) { Log.Debug($"glossary: can't save {e.Message}"); }
    }

    /// Adds or replaces a term (by id; a new term with the same text merges into the old one).
    public void Save(GlossaryTerm term)
    {
        term = term.Clone();
        term.Text = term.Text.Trim();
        term.Misheard = term.Misheard.Select(m => m.Trim()).Where(m => m.Length > 0 && m != term.Text).Distinct().ToList();
        if (term.Text.Length == 0) return;
        lock (gate)
        {
            var at = terms.FindIndex(t => t.Id == term.Id);
            if (at < 0) at = terms.FindIndex(t => t.Text == term.Text);
            if (at >= 0) { term.Id = terms[at].Id; terms[at] = term; }
            else terms.Add(term);
            Commit();
        }
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        lock (gate)
        {
            if (terms.RemoveAll(t => t.Id == id) == 0) return;
            Commit();
        }
        Changed?.Invoke();
    }

    /// Recognized text with the glossary's spellings.
    public string Fix(string text) { TermFixer f; lock (gate) f = fixer; return f.Fix(text).Text; }
    public (string Text, IReadOnlyList<TermFix> Fixes) FixWithDetails(string text) { TermFixer f; lock (gate) f = fixer; return f.Fix(text); }

    /// The enabled terms that occur in `text` (by their spelling), longest first.
    public List<GlossaryTerm> In(string text)
    {
        lock (gate)
            return terms.Where(t => t.Enabled && t.Text.Length > 0 && text.Contains(t.Text, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.Text.Length).Select(t => t.Clone()).ToList();
    }

    /// For the AI translator: "网关 → gateway" (or "伊丽静 (keep this spelling)") for each term in the text.
    public List<string> Hints(string text, string target) =>
        In(text).Select(t => t.TranslationFor(target) is { } tr ? $"{t.Text} → {tr}" : $"{t.Text} (keep this spelling)").ToList();

    /// For a translator that takes no instructions (Google): each term with a fixed translation for
    /// the target is put in already translated, so it comes back as written.
    public string Pretranslate(string text, string target)
    {
        foreach (var t in In(text))
            if (t.TranslationFor(target) is { } tr) text = Replace(text, t.Text, tr);
        return text;
    }

    /// For the notes: every enabled term the transcript mentions, with its note.
    public List<string> ForNotes(string transcript) =>
        In(transcript).Take(40).Select(t => t.Note.Length > 0 ? $"{t.Text} ({t.Note})" : t.Text).ToList();

    private static string Replace(string text, string from, string to)
    {
        if (TermFixer.IsLatin(from))
            return Regex.Replace(text, @"(?<![A-Za-z0-9])" + Regex.Escape(from) + @"(?![A-Za-z0-9])", to.Replace("$", "$$"), RegexOptions.IgnoreCase);
        return text.Replace(from, to);
    }

    // ── import / export: one term per line ──
    //   正确写法<TAB>常被听成（用 | 分隔）<TAB>译法<TAB>说明    (lines starting with # are comments)
    //   "正确写法=常被听成1|常被听成2" also works, and a line with only the spelling.

    public string Export()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 会译词库：正确写法<TAB>常被听成（| 分隔）<TAB>译法（gateway; ja=ゲートウェイ）<TAB>说明");
        lock (gate)
            foreach (var t in terms)
                sb.AppendLine(string.Join('\t', t.Text, string.Join('|', t.Misheard), GlossaryTerm.FormatTranslations(t.Translations), t.Note.Replace('\t', ' ').Replace('\n', ' ')));
        return sb.ToString();
    }

    /// Reads exported lines into the glossary: new spellings are added, known ones gain the
    /// misheard spellings and translations they did not have. Returns (added, updated).
    public (int Added, int Updated) Import(string text)
    {
        int added = 0, updated = 0;
        lock (gate)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                string[] cols = line.Contains('\t') ? line.Split('\t')
                    : line.Split('=', 2) is { Length: 2 } eq ? [eq[0], eq[1]] : [line];
                var spelling = cols[0].Trim();
                if (spelling.Length == 0) continue;
                var misheard = cols.Length > 1 ? GlossaryTerm.ParseList(cols[1]) : [];
                var tr = cols.Length > 2 ? GlossaryTerm.ParseTranslations(cols[2]) : [];
                var note = cols.Length > 3 ? cols[3].Trim() : "";
                var known = terms.FirstOrDefault(t => t.Text == spelling);
                if (known == null)
                {
                    terms.Add(new GlossaryTerm { Text = spelling, Misheard = misheard.Where(m => m != spelling).ToList(), Translations = tr, Note = note });
                    added++;
                    continue;
                }
                var before = JsonSerializer.Serialize(known, Json.Options);
                foreach (var m in misheard) if (m != spelling && !known.Misheard.Contains(m)) known.Misheard.Add(m);
                foreach (var kv in tr) known.Translations.TryAdd(kv.Key, kv.Value);
                if (known.Note.Length == 0) known.Note = note;
                if (JsonSerializer.Serialize(known, Json.Options) != before) updated++;
            }
            if (added + updated > 0) Commit();
        }
        if (added + updated > 0) Changed?.Invoke();
        return (added, updated);
    }
}

/// A correction made to recognized text: `From` at `At` became `To`.
public sealed record TermFix(string From, string To, int At);

/// Corrects recognized text against the glossary, in one pass so a correction is never corrected
/// again: first the misheard spellings the user listed (and, for Latin words, any letter case),
/// then Chinese sound-alikes — a run of characters read like a term but written otherwise. Two-
/// character terms must sound exactly the same (tones aside); longer ones may differ the way
/// speakers and recognizers blur Mandarin (z/zh, c/ch, s/sh, n/l, -n/-ng).
public sealed class TermFixer
{
    private sealed record Rule(string From, string To);
    private readonly List<Rule> exact;          // longest first
    private readonly List<(string Text, string[][] Sounds, bool Loose)> alike;   // longest first

    public TermFixer(IEnumerable<GlossaryTerm> terms)
    {
        var on = terms.Where(t => t.Enabled && t.Text.Trim().Length > 0).ToList();
        exact = on.SelectMany(t => t.Misheard.Where(m => m.Length > 0 && m != t.Text).Select(m => new Rule(m, t.Text))
                    // A Latin term in another case ("kubernetes", "KUBERNETES") is still that term.
                    .Concat(IsLatin(t.Text) ? [new Rule(t.Text, t.Text)] : []))
            .OrderByDescending(r => r.From.Length).ToList();
        alike = on.Where(t => !t.ExactOnly && t.Text.Length >= 2 && t.Text.All(Pinyin.IsHan))
            .Select(t => (t.Text, t.Text.Select(c => Pinyin.Of(c)).ToArray(), t.Text.Length >= 3))
            .Where(x => x.Item2.All(s => s.Length > 0))
            .OrderByDescending(x => x.Text.Length).ToList();
        spellings = on.Select(t => t.Text).ToHashSet();
    }

    private readonly HashSet<string> spellings;

    public static bool IsLatin(string s) => s.Any(char.IsAsciiLetter) && !s.Any(Pinyin.IsHan);

    public (string Text, IReadOnlyList<TermFix> Fixes) Fix(string text)
    {
        if (text.Length == 0 || (exact.Count == 0 && alike.Count == 0)) return (text, []);
        var sounds = text.Select(c => Pinyin.IsHan(c) ? Pinyin.Of(c) : []).ToArray();
        var sb = new StringBuilder(text.Length + 8);
        var fixes = new List<TermFix>();
        var i = 0;
        while (i < text.Length)
        {
            var hit = Exact(text, i) ?? Alike(text, sounds, i);
            if (hit is { } h)
            {
                var from = text.Substring(i, h.Length);
                if (from != h.To) fixes.Add(new TermFix(from, h.To, sb.Length));
                sb.Append(h.To);
                i += h.Length;
                continue;
            }
            sb.Append(text[i]);
            i++;
        }
        return (sb.ToString(), fixes);
    }

    private (int Length, string To)? Exact(string text, int i)
    {
        foreach (var r in exact)
        {
            if (i + r.From.Length > text.Length) continue;
            if (IsLatin(r.From))
            {
                if (string.Compare(text, i, r.From, 0, r.From.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
                // Whole words only: "API" inside "rapid" or "APIs" is not the term.
                if (i > 0 && char.IsAsciiLetterOrDigit(text[i - 1])) continue;
                var end = i + r.From.Length;
                if (end < text.Length && char.IsAsciiLetterOrDigit(text[end])) continue;
                return (r.From.Length, r.To);
            }
            if (string.CompareOrdinal(text, i, r.From, 0, r.From.Length) == 0) return (r.From.Length, r.To);
        }
        return null;
    }

    private (int Length, string To)? Alike(string text, string[][] sounds, int i)
    {
        foreach (var (term, termSounds, loose) in alike)
        {
            var n = term.Length;
            if (i + n > text.Length) continue;
            var window = text.Substring(i, n);
            if (window == term) return (n, term);
            // Another term of the user's, written right: leave it.
            if (spellings.Contains(window)) continue;
            var ok = true;
            for (var k = 0; k < n && ok; k++)
                ok = sounds[i + k].Length > 0 && Pinyin.Alike(sounds[i + k], termSounds[k], loose);
            if (ok) return (n, term);
        }
        return null;
    }
}

/// Toneless pinyin for Chinese characters (mozillazg/pinyin-data, MIT), loaded on first use.
public static class Pinyin
{
    private static readonly Lazy<Dictionary<char, string[]>> Table = new(Load);

    public static bool IsHan(char c) => c is >= '㐀' and <= '䶿' or >= '一' and <= '鿿';

    /// All readings of a character, most common first; empty when not a known character.
    public static string[] Of(char c) => Table.Value.TryGetValue(c, out var r) ? r : [];

    /// Whether any reading of one matches any reading of the other; `loose` forgives the
    /// common blurs (zh/z ch/c sh/s, l/n, -ng/-n).
    public static bool Alike(string[] a, string[] b, bool loose)
    {
        foreach (var x in a)
            foreach (var y in b)
                if (x == y || (loose && Loose(x) == Loose(y))) return true;
        return false;
    }

    public static string Loose(string s)
    {
        if (s.StartsWith("zh")) s = "z" + s[2..];
        else if (s.StartsWith("ch")) s = "c" + s[2..];
        else if (s.StartsWith("sh")) s = "s" + s[2..];
        else if (s.StartsWith('l')) s = "n" + s[1..];
        if (s.EndsWith("ng") && !s.EndsWith("ong")) s = s[..^1];
        return s;
    }

    private static Dictionary<char, string[]> Load()
    {
        var d = new Dictionary<char, string[]>(30000);
        using var stream = typeof(Pinyin).Assembly.GetManifestResourceStream("HuiYi.pinyin.txt");
        if (stream == null) return d;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
            if (line.Length > 1) d[line[0]] = line[1..].Split(',');
        return d;
    }
}
