using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HuiYi;

/// One recognized line. Field names match Cicada's session.json so records stay interchangeable.
public sealed class TranscriptLine
{
    public string Id { get; set; } = "";
    /// Seconds from the start of the session.
    public double T { get; set; }
    /// "them" (system audio) or "me" (microphone).
    public string Speaker { get; set; } = "them";
    public string Src { get; set; } = "";
    public string? Dst { get; set; }
    public string SrcLang { get; set; } = "auto";
    public string DstLang { get; set; } = "zh-CN";
    public int? Voice { get; set; }
    /// Seconds from the start of the session at which the line ended (not in Cicada's format).
    public double? End { get; set; }
}

public sealed class Session
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public List<TranscriptLine> Lines { get; set; } = [];
    public bool HasAudio { get; set; }
    public MeetingNotes? Notes { get; set; }
    public bool? Renamed { get; set; }
    /// Names the user gave voices, keyed by voice id. An empty name means "not the AI's guess": the
    /// voice shows as 说话人 N.
    public Dictionary<string, string>? VoiceNames { get; set; }
    /// Names the AI read from the conversation, keyed by voice id; shown until the user says otherwise.
    public Dictionary<string, VoiceGuess>? VoiceGuesses { get; set; }

    [JsonIgnore] public TimeSpan Duration => (EndedAt ?? DateTime.Now) - StartedAt;

    /// The user's own title, else the date and the name the notes gave the meeting, else "会议 11月1日 14:30".
    [JsonIgnore]
    public string DisplayTitle
    {
        get
        {
            if (Renamed ?? !Title.StartsWith("会议 ")) return Title;
            var name = Notes?.Title;
            if (string.IsNullOrEmpty(name)) return Title;
            var local = StartedAt.ToLocalTime();
            return (local.Year == DateTime.Now.Year ? local.ToString("M月d日") : local.ToString("yyyy年M月d日")) + " " + name;
        }
    }

    public static string DefaultTitle(DateTime date) => "会议 " + date.ToLocalTime().ToString("M月d日 HH:mm", CultureInfo.InvariantCulture);
}

/// Dates as Swift's `.iso8601` strategy writes and reads them: UTC, whole seconds, "Z".
public sealed class Iso8601Converter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new Iso8601Converter(), new JsonStringEnumConverter() },
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// One folder per meeting under %APPDATA%\HuiYi\Sessions\<id>\session.json. Thread-safe; the
/// live session is saved at most every 2 s.
public sealed class SessionStore
{
    private readonly object gate = new();
    private readonly List<Session> sessions = [];
    private readonly string root;
    private Timer? saveTimer;
    private readonly HashSet<string> dirty = [];

    public string? LiveId { get; private set; }
    public event Action? Changed;

    public SessionStore(string? rootFolder = null)
    {
        root = rootFolder ?? Paths.Sessions;
        Directory.CreateDirectory(root);
        Load();
    }

    public string Folder(string id) => Path.Combine(root, id);

    public IReadOnlyList<Session> All
    {
        get { lock (gate) return sessions.ToList(); }
    }

    public Session? Get(string id)
    {
        lock (gate) return sessions.FirstOrDefault(s => s.Id == id);
    }

    /// A copy of a meeting's lines, safe to read while the live meeting keeps adding to it.
    public List<TranscriptLine> LinesOf(string id)
    {
        lock (gate) return sessions.FirstOrDefault(s => s.Id == id)?.Lines.ToList() ?? [];
    }

    private void Load()
    {
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                var file = Path.Combine(dir, "session.json");
                if (!File.Exists(file)) continue;
                var s = JsonSerializer.Deserialize<Session>(File.ReadAllText(file), Json.Options);
                if (s == null) continue;
                s.EndedAt ??= s.Lines.Count > 0 ? s.StartedAt.AddSeconds(s.Lines[^1].T + 5) : s.StartedAt;
                sessions.Add(s);
            }
            catch (Exception e)
            {
                Log.Debug($"store: could not read {Path.GetFileName(dir)}: {e.GetType().Name}");
            }
        }
        sessions.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
    }

    public Session Begin()
    {
        var now = DateTime.UtcNow;
        var s = new Session
        {
            Id = now.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
            Title = Session.DefaultTitle(now),
            StartedAt = now,
        };
        lock (gate)
        {
            // Two meetings in one second: keep ids unique.
            while (sessions.Any(x => x.Id == s.Id)) s.Id += "b";
            sessions.Insert(0, s);
            LiveId = s.Id;
        }
        Directory.CreateDirectory(Folder(s.Id));
        Changed?.Invoke();
        return s;
    }

    /// `at`: when the meeting really ended, for one that ended by itself after a hang-up or silence.
    public void End(DateTime? at = null)
    {
        string? id;
        lock (gate)
        {
            id = LiveId;
            LiveId = null;
            var s = id == null ? null : sessions.FirstOrDefault(x => x.Id == id);
            if (s == null) return;
            var now = DateTime.UtcNow;
            s.EndedAt = at is { } a ? new DateTime(Math.Min(now.Ticks, Math.Max(s.StartedAt.Ticks, a.ToUniversalTime().Ticks)), DateTimeKind.Utc) : now;
            if (s.Lines.Count == 0)
            {
                // Nothing was said: don't keep an empty meeting around.
                sessions.Remove(s);
                try { Directory.Delete(Folder(s.Id), true); } catch { }
                id = null;
            }
        }
        if (id != null) SaveNow(id);
        Changed?.Invoke();
    }

    public void Mutate(string id, Action<Session> change)
    {
        lock (gate)
        {
            var s = sessions.FirstOrDefault(x => x.Id == id);
            if (s == null) return;
            change(s);
            dirty.Add(id);
        }
        ScheduleSave();
        Changed?.Invoke();
    }

    public void SetNotes(string id, MeetingNotes notes) => Mutate(id, s => s.Notes = notes);

    public void Rename(string id, string title) => Mutate(id, s =>
    {
        s.Title = title.Trim();
        s.Renamed = true;
    });

    /// The user named a voice ("" = plain 说话人 N, turning down the AI's guess).
    public void SetVoiceName(string id, int voice, string name) => Mutate(id, s =>
    {
        s.VoiceNames ??= new();
        s.VoiceNames[voice.ToString()] = name.Trim();
    });

    /// A snapshot of a meeting safe to read on any thread (lines copied).
    public Session? Snapshot(string id)
    {
        lock (gate)
        {
            var s = sessions.FirstOrDefault(x => x.Id == id);
            return s == null ? null : new Session
            {
                Id = s.Id, Title = s.Title, StartedAt = s.StartedAt, EndedAt = s.EndedAt, Lines = s.Lines.ToList(),
                HasAudio = s.HasAudio, Notes = s.Notes, Renamed = s.Renamed,
                VoiceNames = s.VoiceNames == null ? null : new(s.VoiceNames),
                VoiceGuesses = s.VoiceGuesses == null ? null : new(s.VoiceGuesses),
            };
        }
    }

    public void Delete(string id)
    {
        lock (gate) sessions.RemoveAll(s => s.Id == id);
        try { Directory.Delete(Folder(id), true); } catch { }
        Changed?.Invoke();
    }

    private void ScheduleSave()
    {
        lock (gate)
        {
            saveTimer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            saveTimer.Change(2000, Timeout.Infinite);
        }
    }

    public void Flush()
    {
        string[] ids;
        lock (gate)
        {
            ids = dirty.ToArray();
            dirty.Clear();
        }
        foreach (var id in ids) SaveNow(id);
    }

    public void SaveNow(string id)
    {
        string json;
        lock (gate)
        {
            var s = sessions.FirstOrDefault(x => x.Id == id);
            if (s == null) return;
            json = JsonSerializer.Serialize(s, Json.Options);
            dirty.Remove(id);
        }
        try
        {
            Directory.CreateDirectory(Folder(id));
            var file = Path.Combine(Folder(id), "session.json");
            // Write then swap, so a crash mid-save never leaves half a file.
            File.WriteAllText(file + ".tmp", json);
            File.Move(file + ".tmp", file, true);
        }
        catch (Exception e)
        {
            Log.Debug($"store: save failed {e.GetType().Name}");
        }
    }
}

/// Plain text and Markdown exports of a meeting.
public static class TranscriptExport
{
    public static string Clock(double t)
    {
        var s = Math.Max(0, (int)t);
        return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60:00}:{s % 60:00}";
    }

    public static string Markdown(Session s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {s.DisplayTitle}");
        sb.AppendLine();
        sb.AppendLine($"{s.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {Clock(s.Duration.TotalSeconds)}");
        sb.AppendLine();
        if (s.Notes is { IsEmpty: false } n)
        {
            sb.AppendLine("## 纪要");
            sb.AppendLine();
            sb.AppendLine(n.Summary);
            sb.AppendLine();
            foreach (var t in n.Topics)
            {
                sb.AppendLine($"### {Clock(t.Start)} {t.Title}");
                if (!string.IsNullOrEmpty(t.Summary)) sb.AppendLine(t.Summary);
                foreach (var p in t.Points) sb.AppendLine($"- {p}");
                sb.AppendLine();
            }
            if (n.Conclusions.Count > 0)
            {
                sb.AppendLine("### 结论");
                foreach (var c in n.Conclusions) sb.AppendLine($"- {c.Text}");
                sb.AppendLine();
            }
            if (n.Todos.Count > 0)
            {
                sb.AppendLine("### 待办");
                foreach (var c in n.Todos) sb.AppendLine($"- [ ] {c.Text}{(c.Owner != null ? $"（{c.Owner}）" : "")}");
                sb.AppendLine();
            }
        }
        sb.AppendLine("## 原文");
        sb.AppendLine();
        var numbers = SpeakerLabels.Numbers(s.Lines);
        var names = SpeakerLabels.Names(s);
        foreach (var l in s.Lines)
        {
            var who = SpeakerLabels.Who(l, numbers, names);
            sb.AppendLine($"**{Clock(l.T)} {who}** {l.Src}  ");
            if (!string.IsNullOrEmpty(l.Dst)) sb.AppendLine($"> {l.Dst}");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
