using HuiYi.Audio;
using HuiYi.Speech;

namespace HuiYi;

/// Wires one meeting together: listening → lines in the record → translation → notes, and the live
/// subtitle (last line + the line being said). UI-free: events fire on worker threads.
public sealed class MeetingController
{
    /// `LineId`: the finished line shown (for whose voice it is), nil while nothing is.
    public sealed record Subtitle(string Source, string Translation, bool Live, string? LineId = null);

    public event Action<string>? LineChanged;      // line id (added or updated)
    public event Action<Subtitle>? SubtitleChanged;
    public event Action? StateChanged;
    public event Action<string>? Error;
    public event Action<MeetingEndWatch.Pending?>? EndPending;

    public string? SessionId { get; private set; }
    public bool IsListening => SessionId != null;
    /// The language the other side spoke last (for speak-back's "auto" target).
    public string? TheirLanguage { get; private set; }

    private readonly AppSettings settings;
    private readonly SessionStore store;
    private readonly TranslationService translator;
    private readonly NotesEngine notes;
    private readonly ListenEngine listen;
    private readonly Func<IReadOnlyList<string>> systemLanguages;
    private readonly SpeakerTagger? speakers;
    private readonly SpeakerNamer? namer;
    private readonly object gate = new();

    private DateTime startedAt;
    private string? lastThemId;
    private bool lastCut;
    private double lastEnd;
    private string partial = "";
    private string partialLang = "";
    private string partialTranslation = "";
    private int partialEpoch;
    private bool draftInFlight;
    private string lastDraftSent = "";
    private DateTime lastDraftAt = DateTime.MinValue;
    private MeetingEndWatch? endWatch;
    private Timer? endTimer;

    public MeetingController(AppSettings settings, SessionStore store, TranslationService translator, NotesEngine notes,
        ListenEngine listen, Func<IReadOnlyList<string>> systemLanguages, SpeakerTagger? speakers = null, SpeakerNamer? namer = null)
    {
        this.speakers = speakers;
        this.namer = namer;
        if (speakers != null) speakers.Merged += Relabel;
        this.settings = settings;
        this.store = store;
        this.translator = translator;
        this.notes = notes;
        this.listen = listen;
        this.systemLanguages = systemLanguages;
        listen.Partial += OnPartial;
        listen.Final += OnFinal;
        listen.Failed += m => Error?.Invoke(m);
        translator.Glossary = () => notes.Glossary;
    }

    /// The language subtitles go into for speech in `source`.
    public string TargetFor(string? source) =>
        settings.Target == "auto" ? Languages.FollowSystemTarget(source, systemLanguages()) : settings.Target;

    /// The language the notes are written in.
    public string NotesLanguage() =>
        Languages.EnglishName(settings.NotesLanguage != "auto" ? settings.NotesLanguage
            : settings.Target != "auto" ? settings.Target : Languages.MyLanguage(systemLanguages()));

    public void Start(IAudioSource source)
    {
        if (IsListening) return;
        var s = store.Begin();
        lock (gate)
        {
            SessionId = s.Id;
            startedAt = s.StartedAt;
            lastThemId = null;
            partial = partialTranslation = "";
        }
        speakers?.Reset();
        notes.Language = NotesLanguage;
        notes.Begin(s.Id);
        listen.Start(source);
        endWatch = new MeetingEndWatch(EndConfig(), DateTime.Now);
        endTimer = new Timer(_ => TickEnd(), null, 5000, 5000);
        Log.Debug($"meeting: started {s.Id}");
        StateChanged?.Invoke();
    }

    public void Stop(DateTime? at = null)
    {
        if (!IsListening) return;
        endTimer?.Dispose();
        endTimer = null;
        endWatch = null;
        listen.Stop();
        var id = SessionId;
        lock (gate) SessionId = null;
        store.End(at);
        if (id != null && store.Get(id) != null)
        {
            notes.Finish();
            namer?.Guess(id, force: true);
        }
        Log.Debug($"meeting: ended {id}");
        SubtitleChanged?.Invoke(new Subtitle("", "", false));
        StateChanged?.Invoke();
        EndPending?.Invoke(null);
    }

    private MeetingEndWatch.Config EndConfig() => new()
    {
        FollowApps = settings.FollowMeetingApps,
        Silence = settings.SilenceEnd,
        SilenceAfter = TimeSpan.FromMinutes(Math.Max(1, settings.SilenceMinutes)),
    };

    /// "继续记录": the end prompt was dismissed.
    public void KeepGoing()
    {
        endWatch?.KeepGoing(DateTime.Now);
        EndPending?.Invoke(null);
    }

    private void TickEnd()
    {
        var w = endWatch;
        var id = SessionId;
        if (w == null || id == null) return;
        w.Settings = EndConfig();
        var said = store.Get(id)?.Lines.Count > 0;
        var before = w.Waiting;
        var reason = w.Tick(DateTime.Now, MicUsers.InCall(), said);
        if (reason != null)
        {
            Log.Debug($"meeting: ended by itself ({reason.GetType().Name})");
            Stop(reason is MeetingEndWatch.Reason.AppLeft ? DateTime.Now.AddSeconds(-30) : null);
            return;
        }
        if (!Equals(before, w.Waiting)) EndPending?.Invoke(w.Waiting);
    }

    // MARK: Listening

    private void OnPartial(string text, string lang)
    {
        int epoch;
        lock (gate)
        {
            partial = text;
            partialLang = lang;
            if (text.Length == 0) partialTranslation = "";
            epoch = partialEpoch;
        }
        if (text.Length > 0) endWatch?.Heard(DateTime.Now);
        Render();
        TranslateDraft(text, lang, epoch);
    }

    private void TranslateDraft(string text, string lang, int epoch)
    {
        if (!settings.TranslationOn || text.Length < 2 || translator.GoogleResting) return;
        var target = TargetFor(lang);
        if (Languages.SameLanguage(lang, target)) return;
        lock (gate)
        {
            // One draft at a time, at most one a second: the free endpoint blocks eager clients.
            if (draftInFlight || text == lastDraftSent || DateTime.Now - lastDraftAt < TimeSpan.FromSeconds(1)) return;
            draftInFlight = true;
            lastDraftSent = text;
            lastDraftAt = DateTime.Now;
        }
        _ = Task.Run(async () =>
        {
            var outText = await translator.Translate(text, lang, target, TranslationService.Kind.Draft);
            bool again;
            lock (gate)
            {
                draftInFlight = false;
                if (outText != null && epoch == partialEpoch && partial.Length > 0) partialTranslation = outText;
                // The line grew while this one was out: translate what is there now.
                again = partial.Length > 0 && partial != lastDraftSent && epoch == partialEpoch;
            }
            Render();
            if (again) TranslateDraft(partial, partialLang, epoch);
        });
    }

    private void OnFinal(HeardSegment seg)
    {
        var id = SessionId;
        if (id == null) return;
        endWatch?.Heard(DateTime.Now);
        TheirLanguage = seg.Lang;
        var target = TargetFor(seg.Lang);
        // Two voices never share a line: hear whose this is first (tens of milliseconds). A line with
        // hardly a letter (a breath, a click) gets no voiceprint.
        int? voice = settings.SpeakerVoices && speakers != null && seg.Samples != null && seg.Text.Count(char.IsLetter) >= 2
            ? speakers.Voice(seg.Samples) : null;
        // Seconds since the meeting started, from the audio clock (it started with the meeting).
        var t = seg.Start;
        string lineId;
        bool joined;
        lock (gate)
        {
            partial = "";
            partialTranslation = "";
            lastDraftSent = "";
            partialEpoch++;
            var prev = lastThemId == null ? null : store.Get(id)?.Lines.FirstOrDefault(l => l.Id == lastThemId);
            joined = prev != null && prev.SrcLang == seg.Lang && (voice == null || prev.Voice == null || voice == prev.Voice)
                     && HeardText.Continues(prev.Src, seg.Text, seg.Start - lastEnd, lastCut);
            lastCut = seg.Cut;
            lastEnd = seg.End;
            if (joined)
            {
                lineId = prev!.Id;
                var text = HeardText.Join(prev.Src, seg.Text);
                store.Mutate(id, _ =>
                {
                    prev.Src = text;
                    prev.End = seg.End;
                });
            }
            else
            {
                lineId = Guid.NewGuid().ToString("N")[..12];
                var line = new TranscriptLine
                {
                    Id = lineId, T = Math.Round(t, 2), End = Math.Round(seg.End, 2), Speaker = "them",
                    Src = seg.Text, SrcLang = seg.Lang, DstLang = target, Voice = voice,
                };
                store.Mutate(id, s => s.Lines.Add(line));
                lastThemId = lineId;
            }
        }
        LineChanged?.Invoke(lineId);
        Render();
        TranslateLine(id, lineId);
        // Once two voices are on show (and every 20 lines after), Ling may know their names.
        if (voice != null) namer?.Guess(id);
    }

    private void TranslateLine(string sessionId, string lineId, int attempt = 0)
    {
        var line = store.Get(sessionId)?.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line == null || !settings.TranslationOn) return;
        var src = line.Src;
        if (Languages.SameLanguage(line.SrcLang, line.DstLang)) return;
        _ = Task.Run(async () =>
        {
            var outText = await translator.Translate(src, line.SrcLang, line.DstLang, TranslationService.Kind.Sentence);
            if (outText == null)
            {
                // No translator answered: try again later so the record ends up bilingual.
                if (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20 * (attempt + 1)));
                    if (line.Dst == null && line.Src == src) TranslateLine(sessionId, lineId, attempt + 1);
                }
                return;
            }
            // The line grew while this was out: its newer translation wins.
            if (line.Src != src) return;
            store.Mutate(sessionId, _ => line.Dst = outText);
            LineChanged?.Invoke(lineId);
            Render();
        });
    }

    /// A stray voice was folded into a main one: its lines (and a name given to it, if the main
    /// voice has none) move over, so the transcript shows one person where it showed two.
    private void Relabel(int from, int into)
    {
        if (SessionId is not { } id) return;
        store.Mutate(id, s =>
        {
            foreach (var l in s.Lines)
                if (l.Speaker == "them" && l.Voice == from) l.Voice = into;
            static void Move<T>(Dictionary<string, T>? d, int from, int into)
            {
                if (d == null || !d.Remove(from.ToString(), out var v)) return;
                d.TryAdd(into.ToString(), v);
            }
            Move(s.VoiceNames, from, into);
            Move(s.VoiceGuesses, from, into);
        });
        LineChanged?.Invoke("");
    }

    /// Something I said into the room (speak-back), recorded as "me".
    public void AddMine(string src, string srcLang, string? dst, string dstLang)
    {
        var id = SessionId;
        if (id == null) return;
        var line = new TranscriptLine
        {
            Id = Guid.NewGuid().ToString("N")[..12], T = Math.Round((DateTime.UtcNow - startedAt).TotalSeconds, 2),
            Speaker = "me", Src = src, SrcLang = srcLang, Dst = dst, DstLang = dstLang,
        };
        store.Mutate(id, s => s.Lines.Add(line));
        endWatch?.Heard(DateTime.Now);
        LineChanged?.Invoke(line.Id);
    }

    /// The subtitle: the last finished line and the one being said, each with its translation.
    private void Render()
    {
        var id = SessionId;
        if (id == null) return;
        TranscriptLine? last;
        string p, pt, pl;
        lock (gate)
        {
            last = lastThemId == null ? null : store.Get(id)?.Lines.FirstOrDefault(l => l.Id == lastThemId);
            p = partial;
            pt = partialTranslation;
            pl = partialLang;
        }
        var srcJoin = Languages.Joiner(pl.Length > 0 ? pl : last?.SrcLang ?? "en");
        var source = string.Join(srcJoin.Length == 0 ? " " : srcJoin, new[] { last?.Src ?? "", p }.Where(x => x.Length > 0));
        var tgt = TargetFor(pl.Length > 0 ? pl : last?.SrcLang);
        var tgtJoin = Languages.Joiner(tgt);
        var translation = string.Join(tgtJoin, new[] { last?.Dst ?? "", pt }.Where(x => x.Length > 0));
        SubtitleChanged?.Invoke(new Subtitle(source, translation, p.Length > 0, last?.Id));
    }
}
