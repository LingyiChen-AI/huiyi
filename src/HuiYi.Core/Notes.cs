using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HuiYi;

/// Meeting notes kept with the session (session.json › notes). Written by the AI while the meeting
/// runs and rewritten once over the whole transcript when it ends, in the shape a template asks for.
/// `Conclusions` and `Todos` stay filled as well, so the record keeps Cicada's format.
public sealed class MeetingNotes
{
    public string? Title { get; set; }
    public string Summary { get; set; } = "";
    public List<NoteTopic> Topics { get; set; } = [];
    public List<NoteItem> Conclusions { get; set; } = [];
    public List<NoteItem> Todos { get; set; } = [];
    public List<NoteTerm> Terms { get; set; } = [];
    /// The template's parts in its order (decisions and to-dos included), as written.
    public List<NoteSection>? Sections { get; set; }
    public string? TemplateId { get; set; }
    public int Covered { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool Final { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Summary.Length == 0 && Topics.Count == 0 && (Sections?.All(s => s.Items.Count == 0) ?? true);

    /// The parts to show, in order: the template's, or decisions and to-dos for older notes.
    public List<NoteSection> Parts() => Sections is { Count: > 0 } s ? s :
    [
        new() { Key = "conclusions", Title = "已经定下的", Style = "check", Items = Conclusions },
        new() { Key = "todos", Title = "待办事项", Style = "todo", Items = Todos },
    ];
}

public sealed class NoteSection
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Style { get; set; } = "dot";
    public List<NoteItem> Items { get; set; } = [];
}

public sealed class NoteTopic
{
    public double Start { get; set; }
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Points { get; set; } = [];
    public Fishbone? Fishbone { get; set; }
    public List<double?>? PointTimes { get; set; }

    public double? PointTime(int i) => PointTimes != null && i < PointTimes.Count ? PointTimes[i] : null;
}

public sealed class Fishbone
{
    public string Question { get; set; } = "";
    public List<FishboneBranch> Branches { get; set; } = [];
}

public sealed class FishboneBranch
{
    public string Label { get; set; } = "";
    public string Detail { get; set; } = "";
    public double? T { get; set; }
    public int Support { get; set; }
}

public sealed class NoteItem
{
    public string Text { get; set; } = "";
    public string? Owner { get; set; }
    /// The deadline as a real date ("11月6日（周三）"), resolved from what was said against the meeting's date.
    public string? Due { get; set; }
    public int Support { get; set; }
    public double? T { get; set; }
}

public sealed class NoteTerm
{
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
    public List<string> Heard { get; set; } = [];

    /// Corrected spellings first: what a reader can't work out alone.
    public static List<NoteTerm> Ordered(IEnumerable<NoteTerm> terms)
    {
        var list = terms.ToList();
        return list.Where(t => t.Heard.Count > 0).Concat(list.Where(t => t.Heard.Count == 0)).ToList();
    }
}

/// The notes prompt (shaped by a template) and the lenient parser. The approach (content in the
/// system message, evidence ids checked against the transcript, one topic at a time) follows
/// Cicada's Notes.swift.
public static class NotesPrompt
{
    private static readonly string[] Weekdays = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    /// "2026年11月4日（周三）14:05", the meeting's start as the AI should date things from.
    public static string When(DateTime localStart) =>
        $"{localStart:yyyy年M月d日}（{Weekdays[(int)localStart.DayOfWeek]}）{localStart:HH:mm}";

    /// A moment of the meeting as a real clock time: the start plus seconds in ("14:05").
    public static string ClockAt(DateTime localStart, double seconds) =>
        localStart.AddSeconds(seconds).ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string Build(MeetingNotes notes, IReadOnlyList<TranscriptLine> lines, int from, string language, bool final,
        IReadOnlyDictionary<int, string>? names = null, NoteTemplate? template = null, DateTime? meetingStart = null, string? transcript = null)
    {
        template ??= NoteTemplates.BuiltIn[0];
        var start = (meetingStart ?? DateTime.Now).ToLocalTime();
        var numbers = SpeakerLabels.Numbers(lines);
        names ??= new Dictionary<int, string>();
        string Line(int i)
        {
            var l = lines[i];
            // A named voice goes as its name: notes then say "George" rather than 发言人2.
            var who = l.Speaker == "me" ? "你"
                : numbers.TryGetValue(l.Id, out var n) ? (names.TryGetValue(n, out var nm) ? nm : $"发言人{n}")
                : numbers.Count == 0 ? "对方" : "未识别";
            return $"[L{i + 1} {TranscriptExport.Clock(l.T)} {who}] {l.Src}";
        }
        var current = "{}";
        if (!notes.IsEmpty)
        {
            var compact = new JsonObject
            {
                ["title"] = notes.Title,
                ["summary"] = notes.Summary,
            };
            if (template.Topics)
                compact["topics"] = JsonSerializer.SerializeToNode(notes.Topics.Select(t => new { start = t.Start, title = t.Title, summary = t.Summary, points = t.Points, fishbone = t.Fishbone }), Json.Options);
            foreach (var part in notes.Parts())
                compact[part.Key] = JsonSerializer.SerializeToNode(part.Items.Select(i => new { text = i.Text, owner = i.Owner, due = i.Due }), Json.Options);
            if (template.Terms) compact["terms"] = JsonSerializer.SerializeToNode(notes.Terms, Json.Options);
            current = compact.ToJsonString(Json.Options);
        }
        var context = Math.Max(0, from - 2);
        var p = new StringBuilder();
        p.Append($"You keep notes for a meeting {(final ? "that has just ended. Rewrite them from the whole transcript" : "that is still going on. Update them with the new lines")}.\n");
        p.Append($"The meeting started {When(start)} (local time). Notes template: {template.Name}.\n");
        p.Append($"Write the notes in {language}. Return JSON only.\n\n");
        if (!final) p.Append($"Current notes (JSON):\n{current}\n\n");
        if (context < from)
            p.Append("Already covered (context only):\n" + string.Join("\n", Enumerable.Range(context, from - context).Select(Line)) + "\n\n");
        p.Append($"{(final ? "Transcript" : "New lines")} (id, mm:ss since the start, speaker 对方=the other side / 你=me / a name or 发言人N=one person on the other side, told apart by voice and possibly wrong / 未识别=someone on the other side not identified, text). Speech recognition makes mistakes, often in names: correct them from context, and write a public figure's name as the news spells it.\n");
        p.Append(transcript ?? string.Join("\n", Enumerable.Range(from, lines.Count - from).Select(Line)));
        if (template.Topics)
        {
            if (final && lines.Count > 0)
            {
                var span = lines[^1].T - lines[0].T;
                var most = Math.Max(1, Math.Min(8, (int)(span / 180) + 1));
                p.Append($"\n\nThis meeting ran {TranscriptExport.Clock(span)}: use at most {most} topic{(most == 1 ? "" : "s")}.");
            }
            else if (!final && notes.Topics.Count > 0 && lines.Count > 0 && lines[^1].T - notes.Topics[^1].Start < 120)
            {
                var c = notes.Topics[^1];
                p.Append($"\n\nThe current topic \"{c.Title}\" started at {TranscriptExport.Clock(c.Start)}, under 2 minutes ago: do not start a new topic in this update; add the new lines to it.");
            }
            else if (!final && notes.Topics.Count == 0)
            {
                p.Append("\n\nThese are the first lines: make one topic.");
            }
        }

        var owner = numbers.Count == 0 ? "\"对方\" or \"你\" or null"
            : "the person's label from the transcript (a name like \"George\", or \"发言人2\"), \"你\", or null when unclear";
        var due = "a real date resolved against the meeting date, like \"11月6日（周三）\" or \"11月30日前\", or null when no deadline was said";

        // The shape: title and summary, then the template's parts.
        p.Append("\n\nReturn the full notes as one JSON object with exactly these keys:\n");
        p.Append("{\"title\": \"the meeting's name: what it is about, a few words\",\n");
        p.Append($" \"summary\": \"{(template.Topics ? "2-3 sentences on the whole meeting so far" : "3-5 sentences on the whole meeting so far")}\",\n");
        if (template.Topics)
        {
            var fish = template.Fishbone
                ? ",\n             \"fishbone\": null or {\"question\": \"the problem or question\", \"branches\": [{\"label\": \"2-6 words\", \"detail\": \"one short line\", \"t\": seconds, \"evidence\": [\"L5\"]}]}"
                : "";
            p.Append(" \"topics\": [{\"start\": seconds, \"title\": \"short title\", \"summary\": \"one sentence\", \"points\": [{\"text\": \"key point\", \"evidence\": [\"L4\"]}], \"evidence\": [\"L3\"]" + fish + "}],\n");
        }
        foreach (var s in template.Sections)
        {
            var fields = "\"text\": \"...\"" + (s.Owner ? $", \"owner\": {owner}" : "") + (s.Due ? $", \"due\": {due}" : "") + ", \"evidence\": [\"L7\"]";
            p.Append($" \"{s.Key}\": [{{{fields}}}],\n");
        }
        if (template.Terms)
            p.Append(" \"terms\": [{\"name\": \"correct spelling\", \"note\": \"who or what, a few words\", \"heard\": [\"how it was misrecognised\"]}],\n");
        p.Length -= 2;
        p.Append("}\n");

        p.Append("Rules (be brief: these notes are read at a glance during the meeting):\n");
        if (template.Focus.Length > 0) p.Append($"- Focus: {template.Focus}\n");
        p.Append("- title: a name for the whole meeting, like a calendar entry (at most 12 Chinese characters or 6 words; no date, no quotes, not \"会议\" alone). Keep the current title unless the meeting's subject has clearly changed.\n");
        p.Append($"- summary: at most {(template.Topics ? 3 : 5)} short sentences.\n");
        if (template.Topics)
        {
            p.Append($"- Topics in time order, at most 8 in total. A topic is a stretch of the meeting, usually 3-10 minutes, not a single remark: start a new one only when the subject clearly changes and the current topic started at least 2 minutes before the newest line{(final ? "" : "; add at most one new topic per update")}. Otherwise add to the current one, and merge small neighbouring topics. Keep earlier topics as they are unless new lines correct them. The last topic is the one being discussed now.\n");
            p.Append("- At most 4 points per topic, each one short phrase. Merge rather than repeat.\n");
            if (template.Fishbone) p.Append("- Add a fishbone only to a topic that discusses causes, reasons or factors (3-5 branches); otherwise null.\n");
        }
        foreach (var s in template.Sections)
            p.Append($"- {s.Key} ({s.Title}): {s.Instruction}. At most 8 items, each one short line; [] when the meeting said nothing for it.\n");
        if (template.Sections.Any(s => s.Owner))
            p.Append("- owner: only someone the transcript names or clearly implies; never guess.\n");
        p.Append($"- Dates and times: whenever an item mentions when (a deadline, a milestone, the next meeting, \"tomorrow\", \"周三前\", \"下周一\", \"月底\"), write it as a real date resolved against the meeting date {start:yyyy年M月d日}（{Weekdays[(int)start.DayOfWeek]}）, e.g. \"11月6日（周三）\"; keep a clock time if one was said (\"11月6日 15:00\"). Never leave a relative word alone, and never invent a date that was not said.\n");
        p.Append("- Every topic, point, branch and item needs evidence: the ids of the lines that say it. Never state anything the transcript does not say.\n");
        if (template.Terms)
            p.Append("- terms: at most 10 in total, replacing less useful ones rather than adding: first names the transcript misspells (heard), then the people, places and jargon a reader would not know. Leave out what everyone knows unless it was misheard. name is the correct spelling in the transcript's language; heard lists only the wrong forms the transcript used; note is a few words.\n");
        p.Append($"- Write the meeting title and every summary, title, point, branch and item in {language}, even when the transcript is in another language. Only term names keep the transcript's spelling.\n");
        p.Append("- Write about what was said, never about the transcript or recognition quality.\n");
        p.Append("- Use empty arrays when there is nothing. No markdown, no text outside the JSON.");
        return p.ToString();
    }

    /// Whether the notes' own words are in `language`: Chinese, Japanese and Korean need a fair
    /// share of CJK, other languages must not be mostly CJK.
    public static bool Written(MeetingNotes notes, string language)
    {
        var text = notes.Summary + string.Concat(notes.Topics.Select(t => t.Title));
        if (text.Length < 8) text += string.Concat(notes.Parts().SelectMany(s => s.Items).Select(i => i.Text).Take(5));
        var (cjk, letters) = Languages.CjkShare(text);
        if (letters == 0) return true;
        var wantsCjk = new[] { "Chinese", "Japanese", "Korean" }.Any(language.Contains);
        return wantsCjk ? cjk * 5 >= letters : cjk * 2 < letters;
    }

    /// Lenient: missing keys become empty, evidence ids are checked against the transcript.
    public static MeetingNotes? Parse(string raw, IReadOnlyList<double> times, double startedAt, NoteTemplate? template = null)
    {
        template ??= NoteTemplates.BuiltIn[0];
        var a = raw.IndexOf('{');
        var b = raw.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        JsonObject? root;
        try { root = JsonNode.Parse(raw[a..(b + 1)]) as JsonObject; }
        catch { return null; }
        if (root == null) return null;

        static string Str(JsonNode? v) => v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s.Trim() : "";
        static double? Num(JsonNode? v)
        {
            if (v is not JsonValue jv) return null;
            if (jv.TryGetValue<double>(out var d)) return d;
            if (jv.TryGetValue<string>(out var s) && double.TryParse(s, out d)) return d;
            return null;
        }
        HashSet<int> LineNumbers(JsonNode? v)
        {
            var set = new HashSet<int>();
            if (v is not JsonArray arr) return set;
            foreach (var x in arr)
            {
                var id = Str(x);
                var digits = new string(id.SkipWhile(c => !char.IsDigit(c)).ToArray());
                if (int.TryParse(digits, out var n) && n >= 1 && n <= times.Count) set.Add(n);
            }
            return set;
        }
        int Support(JsonNode? v) => LineNumbers(v).Count;
        double? Time(JsonNode? v)
        {
            var nums = LineNumbers(v);
            return nums.Count == 0 ? null : times[nums.Min() - 1];
        }

        var n = new MeetingNotes { Summary = Str(root["summary"]), TemplateId = template.Id };
        var title = Str(root["title"]).Trim('"', '“', '”', '「', '」', '《', '》');
        n.Title = title.Length == 0 ? null : title;
        foreach (var tn in root["topics"] as JsonArray ?? [])
        {
            if (tn is not JsonObject t) continue;
            var tt = Str(t["title"]);
            if (tt.Length == 0) continue;
            Fishbone? fish = null;
            if (t["fishbone"] is JsonObject f)
            {
                var branches = new List<FishboneBranch>();
                foreach (var bn in f["branches"] as JsonArray ?? [])
                {
                    if (bn is not JsonObject br) continue;
                    var label = Str(br["label"]);
                    if (label.Length > 0)
                        branches.Add(new FishboneBranch { Label = label, Detail = Str(br["detail"]), T = Num(br["t"]), Support = Support(br["evidence"]) });
                }
                if (branches.Count >= 2) fish = new Fishbone { Question = Str(f["question"]), Branches = branches };
            }
            var points = new List<string>();
            var pointTimes = new List<double?>();
            foreach (var pn in t["points"] as JsonArray ?? [])
            {
                var text = pn is JsonObject po ? Str(po["text"]) : Str(pn);
                if (text.Length == 0) continue;
                points.Add(text);
                pointTimes.Add(pn is JsonObject po2 ? Time(po2["evidence"]) : null);
            }
            n.Topics.Add(new NoteTopic
            {
                Start = Num(t["start"]) ?? startedAt,
                Title = tt,
                Summary = Str(t["summary"]),
                Points = points,
                Fishbone = fish,
                PointTimes = pointTimes.Any(x => x != null) ? pointTimes : null,
            });
        }
        List<NoteItem> Items(string key)
        {
            var list = new List<NoteItem>();
            foreach (var x in root[key] as JsonArray ?? [])
            {
                // Plain strings are fine too: some models flatten simple lists.
                if (x is JsonValue)
                {
                    var plain = Str(x);
                    if (plain.Length > 0) list.Add(new NoteItem { Text = plain });
                    continue;
                }
                if (x is not JsonObject i) continue;
                var text = Str(i["text"]);
                var owner = Str(i["owner"]);
                var due = Str(i["due"]);
                if (text.Length > 0)
                    list.Add(new NoteItem
                    {
                        Text = text, Owner = owner.Length == 0 || owner == "null" ? null : owner,
                        Due = due.Length == 0 || due == "null" ? null : due,
                        Support = Support(i["evidence"]), T = Time(i["evidence"]),
                    });
            }
            return list;
        }
        n.Sections = template.Sections.Select(s => new NoteSection { Key = s.Key, Title = s.Title, Style = s.Style, Items = Items(s.Key) }).ToList();
        n.Conclusions = n.Sections.FirstOrDefault(s => s.Key == "conclusions")?.Items ?? [];
        n.Todos = n.Sections.FirstOrDefault(s => s.Key == "todos")?.Items ?? [];
        foreach (var x in root["terms"] as JsonArray ?? [])
        {
            if (x is not JsonObject t) continue;
            var name = Str(t["name"]);
            if (name.Length == 0) continue;
            var heard = (t["heard"] as JsonArray ?? []).Select(Str).Where(h => h.Length > 0 && h != name).ToList();
            n.Terms.Add(new NoteTerm { Name = name, Note = Str(t["note"]), Heard = heard });
        }
        // The model keeps every term it ever had: bound the glossary, keeping the misheard ones.
        if (n.Terms.Count > 20) n.Terms = NoteTerm.Ordered(n.Terms).Take(20).ToList();
        return n.IsEmpty ? null : n;
    }

    /// The model echoes points it already had without evidence: keep the times they had.
    public static void CarryTimes(MeetingNotes old, MeetingNotes next)
    {
        var known = new Dictionary<string, double>();
        foreach (var t in old.Topics)
            for (var i = 0; i < t.Points.Count; i++)
                if (t.PointTime(i) is { } x) known[t.Points[i]] = x;
        foreach (var t in next.Topics)
        {
            var times = t.Points.Select((p, i) =>
            {
                var c = new[] { t.PointTime(i), known.TryGetValue(p, out var k) ? k : (double?)null }.Where(v => v != null).ToList();
                return c.Count == 0 ? null : c.Min();
            }).ToList();
            t.PointTimes = times.Any(v => v != null) ? times : null;
        }
        var items = new Dictionary<string, double>();
        foreach (var i in old.Parts().SelectMany(s => s.Items))
            if (i.T is { } t && !items.ContainsKey(i.Text)) items[i.Text] = t;
        foreach (var i in next.Parts().SelectMany(s => s.Items))
            if (items.TryGetValue(i.Text, out var t)) i.T = i.T is { } x ? Math.Min(x, t) : t;
    }
}

/// Keeps the live session's notes up to date: every ~45 s with new speech, one AI call sends the
/// current notes plus the new lines and gets the whole updated notes back as JSON. A failed or odd
/// reply never replaces good notes. UI-free; the notes window listens to `Changed`.
public sealed class NotesEngine
{
    public enum State { Idle, Updating, NoKey, InvalidKey, RateLimited, Failed }

    public MeetingNotes Notes { get; private set; } = new();
    public State Status { get; private set; } = State.Idle;
    public string? ShownId { get; private set; }
    public string? GeneratingId { get; private set; }
    public event Action? Changed;

    /// The language notes are written in (English name).
    public Func<string> Language = () => "Simplified Chinese";
    /// The user's glossary: notes use its spellings.
    public Glossary? Terms { get; set; }

    /// The template a meeting's notes follow: the one picked for it, else the default.
    public Func<Session, NoteTemplate> Template = _ => NoteTemplates.BuiltIn[0];

    private readonly AiClient ai;
    private readonly SessionStore store;
    private string? sessionId;
    private Timer? timer;
    private int inFlight;
    private string? pendingFinal;
    private DateTime lastAttempt = DateTime.MinValue;
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(45);

    public NotesEngine(AiClient ai, SessionStore store)
    {
        this.ai = ai;
        this.store = store;
    }

    /// Names for the translation glossary: "江充 (heard as 江松, 江冲)".
    public IReadOnlyList<string> Glossary => Notes.Terms.Take(30)
        .Select(t => t.Heard.Count == 0 ? $"{t.Name} ({t.Note})" : $"{t.Name} ({t.Note}; misheard as {string.Join(", ", t.Heard)})").ToList();

    public void Begin(string id)
    {
        sessionId = id;
        ShownId = id;
        Notes = store.Get(id)?.Notes ?? new MeetingNotes();
        Status = ai.HasKey ? State.Idle : State.NoKey;
        lastAttempt = DateTime.Now;
        timer?.Dispose();
        timer = new Timer(_ => Tick(), null, 5000, 5000);
        Changed?.Invoke();
    }

    public void Finish()
    {
        timer?.Dispose();
        timer = null;
        var id = sessionId;
        sessionId = null;
        if (id != null) _ = Update(id, final: true);
    }

    public void Show(string id)
    {
        ShownId = id;
        Notes = store.Get(id)?.Notes ?? new MeetingNotes();
        Changed?.Invoke();
    }

    public void Generate(string id) => _ = Update(id, final: true);

    public void UpdateNow()
    {
        if (sessionId is { } id) _ = Update(id, final: false);
    }

    private void Tick()
    {
        if (Status is State.NoKey or State.InvalidKey && ai.Usable) Status = State.Idle;
        if (Status == State.Idle && !ai.HasKey) { Status = State.NoKey; Changed?.Invoke(); }
        var id = sessionId;
        if (id == null || Volatile.Read(ref inFlight) == 1) return;
        var s = store.Get(id);
        if (s == null || s.Lines.Count <= Notes.Covered) return;
        // First notes as soon as there is something to say, then every 45 s.
        var due = Notes.IsEmpty ? s.Lines.Count >= 3 || DateTime.Now - lastAttempt > TimeSpan.FromSeconds(20)
                                : DateTime.Now - lastAttempt >= Interval;
        if (due) _ = Update(id, final: false);
    }

    public async Task Update(string id, bool final)
    {
        if (Interlocked.CompareExchange(ref inFlight, 1, 0) == 1)
        {
            // Never drop the end-of-meeting pass: queue it.
            if (final) { pendingFinal = id; GeneratingId = id; }
            return;
        }
        try
        {
            var session = store.Get(id);
            if (session == null || session.Lines.Count == 0) return;
            if (!ai.HasKey) { Status = State.NoKey; return; }
            lastAttempt = DateTime.Now;
            Status = State.Updating;
            if (final) GeneratingId = id;
            Changed?.Invoke();

            var lines = store.LinesOf(id);
            var template = Template(session);
            var based = final ? new MeetingNotes() : (id == ShownId ? Notes : session.Notes ?? new MeetingNotes());
            // Notes written with another template start over: their parts don't fit this one.
            if (!based.IsEmpty && (based.TemplateId ?? NoteTemplates.DefaultId) != template.Id) based = new MeetingNotes();
            var from = final ? 0 : Math.Min(based.Covered, lines.Count);
            var lang = Language();
            var names = SpeakerLabels.Names(new Session { Lines = lines, VoiceNames = session.VoiceNames, VoiceGuesses = session.VoiceGuesses });
            var started = DateTime.Now;
            // Avoid constructing an unbounded live prompt from accumulated notes or transcript.
            var estimate = lines.Skip(Math.Max(0, from - 2)).Sum(l => (long)l.Src.Length + 100)
                + (final ? 0 : JsonSerializer.Serialize(based, Json.Options).Length);
            // The user's glossary: the words this meeting mentions, written their way.
            var mine = Terms?.ForNotes(string.Join("\n", lines.Select(l => l.Src))) ?? [];
            var glossary = mine.Count == 0 ? ""
                : "\n\nThe user's glossary — always write these exactly so; a word that sounds like one of them is that word: " + string.Join("; ", mine);
            var prompt = estimate <= NotesBatch.MaxPromptChars
                ? NotesPrompt.Build(based, lines, from, lang, final, names, template, session.StartedAt) + glossary : null;
            MeetingNotes? next = null;
            if (prompt == null || prompt.Length > NotesBatch.MaxPromptChars)
            {
                next = await NotesBatch.Generate(lines, lang, names, template, session.StartedAt,
                    p => ai.Chat(p + glossary, $"Update the notes. Write them in {lang}. Keep the entire JSON under 4500 characters.",
                        AiClient.Priority.Notes, json: true, maxTokens: 2000, timeout: TimeSpan.FromSeconds(90)),
                    finalChat: p => ai.Chat(p + glossary, $"Update the notes. Write them in {lang}.",
                        AiClient.Priority.Notes, json: true, maxTokens: 6000, timeout: TimeSpan.FromSeconds(90)));
            }
            else
            {
                for (var attempt = 1; attempt <= (final ? 2 : 1); attempt++)
                {
                    var raw = await ai.Chat(prompt, $"Update the notes. Write them in {lang}.", AiClient.Priority.Notes,
                        json: true, maxTokens: 6000, timeout: TimeSpan.FromSeconds(90));
                    var parsed = NotesPrompt.Parse(raw, lines.Select(l => l.T).ToList(), lines.FirstOrDefault()?.T ?? 0, template);
                    if (parsed == null) break;
                    if (NotesPrompt.Written(parsed, lang)) { next = parsed; break; }
                    Log.Debug($"notes: reply not in {lang} (attempt {attempt}), dropped");
                }
            }
            if (next == null)
            {
                Log.Debug("notes: no usable reply, kept the previous notes");
                Status = State.Failed;
                return;
            }
            NotesPrompt.CarryTimes(based, next);
            if (next.Title == null) next.Title = (id == ShownId ? Notes : session.Notes)?.Title;
            next.Covered = lines.Count;
            next.UpdatedAt = DateTime.UtcNow;
            next.Final = final;
            if (id == ShownId) Notes = next;
            store.SetNotes(id, next);
            Status = State.Idle;
            Log.Debug($"notes: {(final ? "final" : "live")} update {lines.Count - from} new lines → {next.Topics.Count} topics in {(int)(DateTime.Now - started).TotalSeconds} s");
        }
        catch (AiException e)
        {
            Status = e.Failure switch
            {
                AiFailure.InvalidKey => State.InvalidKey,
                AiFailure.RateLimited => State.RateLimited,
                AiFailure.NoKey => State.NoKey,
                _ => State.Failed,
            };
            Log.Debug($"notes: update failed {e.Failure}");
        }
        catch (Exception e)
        {
            Status = State.Failed;
            Log.Debug($"notes: update failed {e.GetType().Name}");
        }
        finally
        {
            if (final && GeneratingId == id) GeneratingId = null;
            Volatile.Write(ref inFlight, 0);
            Changed?.Invoke();
            if (pendingFinal is { } nextId)
            {
                pendingFinal = null;
                _ = Update(nextId, final: true);
            }
        }
    }
}
