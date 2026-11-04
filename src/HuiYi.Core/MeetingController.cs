using HuiYi.Audio;
using HuiYi.Speech;
using NAudio.Wave;

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
    /// The after-meeting check changed the record: session id, lines relabelled, lines split.
    public event Action<string, int, int>? Redone;

    /// Runs the after-meeting check (MeetingRedo with the app's models); null leaves records as heard.
    public Func<float[], IReadOnlyList<TranscriptLine>, MeetingRedo.Plan?>? Redo { get; set; }
    private NAudio.Wave.WaveFileWriter? recording;
    private string? recordingPath;
    private readonly object recordingGate = new();

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
        listen.Audio += Record;
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
        StartRecording(s.Id);
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
        var audio = StopRecording();
        if (id != null && store.Get(id) != null)
        {
            notes.Finish();
            namer?.Guess(id, force: true);
        }
        Log.Debug($"meeting: ended {id}");
        if (id != null && audio != null) _ = Task.Run(() => RedoSpeakers(id, audio));
        SubtitleChanged?.Invoke(new Subtitle("", "", false));
        StateChanged?.Invoke();
        EndPending?.Invoke(null);
    }

    // MARK: The recording for the after-meeting check

    private void StartRecording(string id)
    {
        if (Redo == null || !settings.RedoSpeakers || !settings.SpeakerVoices) return;
        try
        {
            Directory.CreateDirectory(Paths.Recordings);
            lock (recordingGate)
            {
                recordingPath = Path.Combine(Paths.Recordings, id + ".wav");
                recording = new NAudio.Wave.WaveFileWriter(recordingPath, new NAudio.Wave.WaveFormat(Recognizer.SampleRate, 16, 1));
            }
        }
        catch (Exception e)
        {
            Log.Debug($"meeting: can't keep the recording {e.Message}");
            recording = null;
        }
    }

    private void Record(float[] chunk)
    {
        lock (recordingGate)
        {
            if (recording == null) return;
            var bytes = new byte[chunk.Length * 2];
            for (var i = 0; i < chunk.Length; i++)
            {
                var v = (short)Math.Clamp(chunk[i] * 32767f, short.MinValue, short.MaxValue);
                bytes[2 * i] = (byte)v;
                bytes[2 * i + 1] = (byte)(v >> 8);
            }
            recording.Write(bytes, 0, bytes.Length);
        }
    }

    /// Closes the recording; its path, or null when none was kept.
    private string? StopRecording()
    {
        lock (recordingGate)
        {
            if (recording == null) return null;
            recording.Dispose();
            recording = null;
            return recordingPath;
        }
    }

    /// Who said what, checked again over the whole meeting; then the recording is deleted.
    private void RedoSpeakers(string id, string path)
    {
        try
        {
            var snap = store.Snapshot(id);
            if (snap == null || Redo == null || snap.Lines.Count(l => l.Speaker == "them") < 4) return;
            float[] audio;
            using (var reader = new NAudio.Wave.WaveFileReader(path))
            {
                var provider = reader.ToSampleProvider();
                var all = new List<float>((int)(reader.SampleCount));
                var buffer = new float[16000];
                int n;
                while ((n = provider.Read(buffer, 0, buffer.Length)) > 0) all.AddRange(buffer.AsSpan(0, n).ToArray());
                audio = [.. all];
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var plan = Redo(audio, snap.Lines);
            Log.Debug($"redo: {audio.Length / (double)Recognizer.SampleRate:F0} s checked in {watch.ElapsedMilliseconds} ms, " +
                      $"{plan?.Relabelled ?? 0} relabelled, {plan?.Split ?? 0} split");
            if (plan == null || plan.Lines.Count == 0) return;
            var added = new List<string>();
            store.Mutate(id, s =>
            {
                var rebuilt = new List<TranscriptLine>();
                foreach (var l in s.Lines)
                {
                    if (!plan.Lines.TryGetValue(l.Id, out var pieces) || pieces.Count == 0)
                    {
                        rebuilt.Add(l);
                        continue;
                    }
                    if (pieces.Count == 1)
                    {
                        l.Voice = pieces[0].Voice;
                        rebuilt.Add(l);
                        continue;
                    }
                    // The line held two people: one line each, translated again.
                    for (var k = 0; k < pieces.Count; k++)
                    {
                        var p = pieces[k];
                        var line = k == 0 ? l : new TranscriptLine { Id = Guid.NewGuid().ToString("N")[..12], Speaker = "them" };
                        line.T = p.Start;
                        line.End = p.End;
                        line.Src = p.Text;
                        line.SrcLang = p.Lang;
                        line.DstLang = TargetFor(p.Lang);
                        line.Dst = null;
                        line.Voice = p.Voice;
                        rebuilt.Add(line);
                        added.Add(line.Id);
                    }
                }
                s.Lines = rebuilt;
            });
            if (settings.TranslationOn) foreach (var lineId in added) TranslateLine(id, lineId);
            notes.Generate(id);
            Redone?.Invoke(id, plan.Relabelled, plan.Split);
        }
        catch (Exception e)
        {
            Log.Debug($"redo: failed {e.Message}");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private MeetingEndWatch.Config EndConfig() => new()
    {
        FollowApps = settings.FollowMeetingApps,
        Silence = settings.SilenceEnd,
        SilenceAfter = TimeSpan.FromMinutes(Math.Max(1, settings.SilenceMinutes)),
    };

    /// Translation was switched on mid-meeting: translate the last few lines of the other side, so
    /// what is on screen reads in both languages at once.
    public void TranslationResumed()
    {
        if (SessionId is not { } id) return;
        foreach (var l in store.LinesOf(id).Where(l => l.Speaker == "them" && l.Dst == null).TakeLast(6))
        {
            // The target may have changed since the line was heard.
            store.Mutate(id, _ => l.DstLang = TargetFor(l.SrcLang));
            TranslateLine(id, l.Id);
        }
    }

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
            joined = prev != null && !seg.NewSpeaker && prev.SrcLang == seg.Lang && (voice == null || prev.Voice == null || voice == prev.Voice)
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
        // Once two voices are on show (and every 20 lines after), the AI may know their names.
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
