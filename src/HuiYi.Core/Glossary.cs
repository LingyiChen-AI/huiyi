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
    /// A word list of its own ("支付项目", "医疗"): a group can be limited to some notes templates'
    /// meetings. Empty: 通用, every meeting.
    public string Group { get; set; } = "";

    public GlossaryTerm Clone() => new()
    {
        Id = Id, Text = Text, Misheard = [.. Misheard], Translations = new(Translations), Note = Note,
        ExactOnly = ExactOnly, Enabled = Enabled, Group = Group,
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
    /// Per group, the notes templates whose meetings use it (none listed: every meeting).
    private Dictionary<string, List<string>> scopes = [];
    /// Correctors per template id ("" when none is known), built on first use.
    private readonly Dictionary<string, TermFixer> fixers = [];
    public event Action? Changed;

    /// The template of the meeting being heard now (the app sets it); groups limited to other
    /// templates stay out of corrections, translations and notes.
    public Func<string?> CurrentTemplate { get; set; } = () => null;

    public Glossary(string? path = null)
    {
        this.path = path ?? Path.Combine(Paths.Roaming, "glossary.json");
        Load();
    }

    private string ScopesPath => Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-groups.json");

    /// The groups in use, in the order first met.
    public IReadOnlyList<string> Groups { get { lock (gate) return terms.Select(t => t.Group).Where(g => g.Length > 0).Distinct().ToList(); } }

    /// The templates a group is limited to (empty: every meeting).
    public IReadOnlyList<string> TemplatesOf(string group) { lock (gate) return scopes.TryGetValue(group, out var l) ? [.. l] : []; }

    public void SetTemplates(string group, IEnumerable<string> templateIds)
    {
        lock (gate)
        {
            var list = templateIds.Distinct().ToList();
            if (list.Count == 0) scopes.Remove(group); else scopes[group] = list;
            Commit();
        }
        Changed?.Invoke();
    }

    /// Whether a term counts in a meeting with this template.
    private bool Active(GlossaryTerm t, string? template) =>
        t.Enabled && (t.Group.Length == 0 || !scopes.TryGetValue(t.Group, out var only) || only.Count == 0
                      || (template != null && only.Contains(template)));

    private TermFixer FixerFor(string? template)
    {
        lock (gate)
        {
            var key = template ?? "";
            if (!fixers.TryGetValue(key, out var f)) fixers[key] = f = new TermFixer(terms.Where(t => Active(t, template)));
            return f;
        }
    }

    public IReadOnlyList<GlossaryTerm> Terms { get { lock (gate) return terms.Select(t => t.Clone()).ToList(); } }

    private void Load()
    {
        try
        {
            if (File.Exists(path))
                terms = JsonSerializer.Deserialize<List<GlossaryTerm>>(File.ReadAllText(path), Json.Options) ?? [];
            if (File.Exists(ScopesPath))
                scopes = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(ScopesPath), Json.Options) ?? [];
        }
        catch (Exception e)
        {
            Log.Debug($"glossary: can't read {e.Message}");
            terms = [];
        }
    }

    private void Commit()
    {
        fixers.Clear();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Write(path, JsonSerializer.Serialize(terms, Json.Options));
            // Scopes of groups no term uses any more are forgotten.
            foreach (var g in scopes.Keys.Where(g => terms.All(t => t.Group != g)).ToList()) scopes.Remove(g);
            Write(ScopesPath, JsonSerializer.Serialize(scopes, Json.Options));
        }
        catch (Exception e) { Log.Debug($"glossary: can't save {e.Message}"); }
    }

    private static void Write(string file, string json)
    {
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, file, true);
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

    /// Learns a spelling: the term gains the misheard spellings it lacks, or is added. From the
    /// transcript ("改正并加入词库") and from the AI's notes ("加入词库").
    public void Learn(string text, IEnumerable<string> misheard, string note = "")
    {
        text = text.Trim();
        if (text.Length == 0) return;
        var heard = misheard.Select(m => m.Trim()).Where(m => m.Length > 0 && m != text).Distinct().ToList();
        lock (gate)
        {
            var known = terms.FirstOrDefault(t => t.Text == text);
            if (known == null) terms.Add(new GlossaryTerm { Text = text, Misheard = heard, Note = note.Trim() });
            else
            {
                foreach (var m in heard) if (!known.Misheard.Contains(m)) known.Misheard.Add(m);
                if (known.Note.Length == 0) known.Note = note.Trim();
                known.Enabled = true;
            }
            // A spelling learned as right is no longer anyone's misheard one.
            foreach (var t in terms) if (t.Text != text) t.Misheard.Remove(text);
            Commit();
        }
        Changed?.Invoke();
    }

    /// Whether the glossary already has this spelling with all these misheard ones.
    public bool Knows(string text, IEnumerable<string> misheard)
    {
        lock (gate)
            return terms.FirstOrDefault(t => t.Text == text) is { } known && misheard.All(m => m == text || known.Misheard.Contains(m));
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

    /// How often `part` occurs in `text` (not overlapping).
    public static int CountOf(string text, string part)
    {
        if (part.Length == 0) return 0;
        int n = 0, at = 0;
        while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0) { n++; at += part.Length; }
        return n;
    }

    /// Recognized text with the glossary's spellings (for the current meeting's template).
    public string Fix(string text) => FixerFor(CurrentTemplate()).Fix(text).Text;
    public (string Text, IReadOnlyList<TermFix> Fixes) FixWithDetails(string text) => FixerFor(CurrentTemplate()).Fix(text);
    public (string Text, IReadOnlyList<TermFix> Fixes) FixWithDetails(string text, string? template) => FixerFor(template).Fix(text);

    /// Words that sound close to a term but not close enough to be replaced ("犯成" for 帆船):
    /// what the AI check looks at.
    public IReadOnlyList<(string Heard, string Term)> Suspects(string text, string? template) => FixerFor(template).Suspects(text);

    /// The terms active for this template (all of them, enabled).
    public List<GlossaryTerm> ActiveTerms(string? template) { lock (gate) return terms.Where(t => Active(t, template)).Select(t => t.Clone()).ToList(); }

    /// The active terms that occur in `text` (by their spelling), longest first.
    public List<GlossaryTerm> In(string text) => In(text, CurrentTemplate());

    public List<GlossaryTerm> In(string text, string? template)
    {
        lock (gate)
            return terms.Where(t => Active(t, template) && t.Text.Length > 0 && text.Contains(t.Text, StringComparison.OrdinalIgnoreCase))
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

    /// For the notes: every active term the transcript mentions, with its note.
    public List<string> ForNotes(string transcript, string? template) =>
        In(transcript, template).Take(40).Select(t => t.Note.Length > 0 ? $"{t.Text} ({t.Note})" : t.Text).ToList();

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
        sb.AppendLine("# 会译词库：正确写法<TAB>常被听成（| 分隔）<TAB>译法（gateway; ja=ゲートウェイ）<TAB>说明<TAB>分组");
        lock (gate)
            foreach (var t in terms)
                sb.AppendLine(string.Join('\t', t.Text, string.Join('|', t.Misheard), GlossaryTerm.FormatTranslations(t.Translations),
                    t.Note.Replace('\t', ' ').Replace('\n', ' '), t.Group));
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
                var group = cols.Length > 4 ? cols[4].Trim() : "";
                var known = terms.FirstOrDefault(t => t.Text == spelling);
                if (known == null)
                {
                    terms.Add(new GlossaryTerm { Text = spelling, Misheard = misheard.Where(m => m != spelling).ToList(), Translations = tr, Note = note, Group = group });
                    added++;
                    continue;
                }
                var before = JsonSerializer.Serialize(known, Json.Options);
                foreach (var m in misheard) if (m != spelling && !known.Misheard.Contains(m)) known.Misheard.Add(m);
                foreach (var kv in tr) known.Translations.TryAdd(kv.Key, kv.Value);
                if (known.Note.Length == 0) known.Note = note;
                if (known.Group.Length == 0) known.Group = group;
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

    /// Runs that sound close to a term but were not replaced: a two-character term with the
    /// common blurs, or a longer one with one character off. Each window is reported once.
    public IReadOnlyList<(string Heard, string Term)> Suspects(string text)
    {
        var found = new List<(string, string)>();
        if (alike.Count == 0 || text.Length == 0) return found;
        var sounds = text.Select(c => Pinyin.IsHan(c) ? Pinyin.Of(c) : []).ToArray();
        foreach (var (term, termSounds, _) in alike)
        {
            var n = term.Length;
            for (var i = 0; i + n <= text.Length; i++)
            {
                var window = text.Substring(i, n);
                if (window == term || spellings.Contains(window)) continue;
                // Per character: alike (loosely the same sound), near (same initial or final), or off.
                int alikeCount = 0, near = 0, off = 0;
                for (var k = 0; k < n; k++)
                {
                    if (sounds[i + k].Length == 0) { off = 9; break; }
                    if (Pinyin.Alike(sounds[i + k], termSounds[k], true)) alikeCount++;
                    else if (Pinyin.Near(sounds[i + k], termSounds[k])) near++;
                    else off++;
                }
                // Every character at least near, and at most one of them only near.
                var close = off == 0 && near <= 1 && alikeCount >= 1;
                if (close && !found.Contains((window, term))) found.Add((window, term));
            }
        }
        return found;
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

    private static readonly string[] Initials = ["zh", "ch", "sh", "b", "p", "m", "f", "d", "t", "n", "l", "g", "k", "h", "j", "q", "x", "r", "z", "c", "s", "y", "w"];

    /// A syllable's initial and final ("chuan" → "ch", "uan").
    public static (string Initial, string Final) Split(string s)
    {
        var i = Initials.FirstOrDefault(s.StartsWith) ?? "";
        return (i, s[i.Length..]);
    }

    /// Sounds near: the common readings share the initial, blurs forgiven (成 cheng / 船 chuan,
    /// 拉 la / 娜 na). Only the first reading: rare ones (式 te) and shared finals (碗 wan / 谈 tan)
    /// flagged ordinary words on a two-hour test transcript.
    public static bool Near(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        var (xi, _) = Split(Loose(a[0]));
        var (yi, _) = Split(Loose(b[0]));
        return xi.Length > 0 && xi == yi;
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

/// The optional AI check (设置 › 词库): a line with words that sound close to a glossary term goes
/// to the AI, which may only change those words into the glossary's spellings. Anything else it
/// changes is not taken.
public static class GlossaryAiCheck
{
    public static string Prompt(string line, IReadOnlyList<(string Heard, string Term)> suspects, IEnumerable<GlossaryTerm> terms)
    {
        var names = terms.Select(t => t.Note.Length > 0 ? $"{t.Text} ({t.Note})" : t.Text).Take(60);
        return "A line of a meeting transcript, from speech recognition, which mishears names and terms.\n"
               + "The user's glossary: " + string.Join("; ", names) + "\n"
               + "Possibly misheard here: " + string.Join("; ", suspects.Select(s => $"{s.Heard} → {s.Term}?")) + "\n"
               + "Return the line with only the misheard glossary words corrected to the glossary's spelling. "
               + "Change nothing else: not punctuation, not other words. If nothing was misheard, return the line unchanged.\n"
               + "Line: " + line;
    }

    /// The AI's corrections, if every change it made turns a stretch into a glossary term; null when
    /// it changed anything else (or nothing).
    public static List<LineFix>? Accept(string before, string after, IEnumerable<string> termTexts)
    {
        after = after.Trim().Trim('"', '“', '”', '「', '」');
        if (after.Length == 0 || after == before) return null;
        var spellings = termTexts.Where(t => t.Length > 0).ToList();
        var fixes = new List<LineFix>();
        foreach (var (i1, i2, j1, j2) in Changes(before, after))
        {
            // The change must sit inside an occurrence of a term in the corrected line.
            var hit = spellings.SelectMany(t => Occurrences(after, t).Select(p => (Start: p, End: p + t.Length, Term: t)))
                .Where(o => o.Start <= j1 && j2 <= o.End).OrderBy(o => o.End - o.Start).FirstOrDefault();
            if (hit.Term == null) return null;
            // What was heard: the changed part plus the unchanged characters of the term around it.
            var from = before.Substring(i1 - (j1 - hit.Start), (i2 - i1) + (j1 - hit.Start) + (hit.End - j2));
            // A word for a word: dropping or adding words around the term is not a correction.
            if (Math.Abs(from.Length - hit.Term.Length) > 1) return null;
            if (!fixes.Any(f => f.From == from && f.To == hit.Term)) fixes.Add(new LineFix { From = from, To = hit.Term });
        }
        return fixes.Count > 0 ? fixes : null;
    }

    private static IEnumerable<int> Occurrences(string text, string part)
    {
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + 1, StringComparison.Ordinal))
            yield return at;
    }

    /// The differing stretches of two strings (by a longest common subsequence), as
    /// (before start, before end, after start, after end).
    public static List<(int, int, int, int)> Changes(string a, string b)
    {
        var n = a.Length; var m = b.Length;
        var l = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                l[i, j] = a[i] == b[j] ? l[i + 1, j + 1] + 1 : Math.Max(l[i + 1, j], l[i, j + 1]);
        var result = new List<(int, int, int, int)>();
        int x = 0, y = 0, sx = -1, sy = -1;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[x] == b[y])
            {
                if (sx >= 0) { result.Add((sx, x, sy, y)); sx = -1; }
                x++; y++;
                continue;
            }
            if (sx < 0) { sx = x; sy = y; }
            if (y < m && (x == n || l[x, y + 1] >= l[x + 1, y])) y++; else x++;
        }
        if (sx >= 0) result.Add((sx, n, sy, m));
        return result;
    }
}
