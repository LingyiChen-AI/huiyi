using HuiYi.Audio;
using HuiYi.Speech;
using NAudio.Wave;

namespace HuiYi;

/// Wires one meeting together: listening → lines in the record → translation → notes, and the live
/// subtitle (last line + the line being said). UI-free: events fire on worker threads.
public sealed class MeetingController
{
    /// `LineId`: the finished line shown (for whose voice it is), nil while nothing is.
    /// Source and Translation are the running streams (recent lines and the one being said);
    /// Partial and PartialTranslation are only the line being said.
    public sealed record Subtitle(string Source, string Translation, bool Live, string? LineId = null,
        string Partial = "", string PartialTranslation = "");

    public event Action<string>? LineChanged;      // line id (added or updated)
    public event Action<Subtitle>? SubtitleChanged;
    public event Action? StateChanged;
    public event Action<string>? Error;
    public event Action<MeetingEndWatch.Pending?>? EndPending;
    /// The after-meeting check changed the record: session id, lines relabelled, lines split.
    public event Action<string, int, int>? Redone;

    /// Runs the after-meeting check (MeetingRedo with the app's models); null leaves records as heard.
    /// Its arguments: the recording read a stretch at a time, the lines, and a step callback.
    public Func<Func<double, double, float[]>, IReadOnlyList<TranscriptLine>, Action<int, int>, MeetingRedo.Plan?>? Redo { get; set; }
    /// One after-meeting check at a time: two meetings ending close together queue up.
    private static readonly SemaphoreSlim RedoGate = new(1, 1);
    /// The glossary's AI check (off unless the user turns it on): corrections for a line just heard,
    /// or null. Runs in the background; the line is updated when it answers.
    public Func<string, Task<List<LineFix>?>>? CheckLine { get; set; }
    /// One recording per channel (the PC, the microphone), checked separately after the meeting.
    private readonly NAudio.Wave.WaveFileWriter?[] recording = new NAudio.Wave.WaveFileWriter?[2];
    private readonly string?[] recordingPath = new string?[2];
    private readonly object recordingGate = new();
    /// Lines heard on the microphone in this meeting (by id): the after-meeting check runs them
    /// against the microphone's recording, the rest against the PC's.
    private readonly HashSet<string> micLines = [];
    /// Lines arrive from two channels' threads: one at a time.
    private readonly object finalGate = new();

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
    /// Per channel: the line a following piece may join, how it ended.
    private readonly string?[] lastThemId = new string?[2];
    private readonly bool[] lastCut = new bool[2];
    private readonly double[] lastEnd = new double[2];
    /// The line heard last on either channel: the subtitle shows it.
    private string? lastShownId;
    private string partial = "";
    private string partialLang = "";
    private string partialTranslation = "";
    /// The text partialTranslation was made from (the line goes on while it is out).
    private string partialTranslatedFrom = "";
    /// The ended sentences of the line being said, translated once and kept: the next drafts translate
    /// only what follows, so a sentence already read isn't reworded every second.
    private string frozenSrc = "", frozenDst = "";
    /// A line just finished whose own translation hasn't come yet: the subtitle keeps showing the
    /// draft of it meanwhile, instead of dropping it for a second.
    private readonly Dictionary<string, string> standIns = [];
    /// Lines whose translation on screen stays (the draft, with any words it lacked translated and
    /// added on). Their own translation, a second later, goes into the record but not on screen:
    /// swapped in, the sentence just read turned into other words saying the same thing.
    private readonly HashSet<string> settled = [];
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
            Array.Clear(lastThemId);
            lastShownId = null;
            micLines.Clear();
            standIns.Clear();
            settled.Clear();
            partial = partialTranslation = partialTranslatedFrom = frozenSrc = frozenDst = "";
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
        if (id != null && audio.Any(a => a != null)) _ = Task.Run(() => RedoSpeakers(id, audio));
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
                for (var ch = 0; ch < 2; ch++)
                {
                    recordingPath[ch] = Path.Combine(Paths.Recordings, id + (ch == 0 ? ".wav" : "-mic.wav"));
                    recording[ch] = new NAudio.Wave.WaveFileWriter(recordingPath[ch], new NAudio.Wave.WaveFormat(Recognizer.SampleRate, 16, 1));
                }
        }
        catch (Exception e)
        {
            Log.Debug($"meeting: can't keep the recording {e.Message}");
            lock (recordingGate) for (var ch = 0; ch < 2; ch++) { recording[ch]?.Dispose(); recording[ch] = null; }
        }
    }

    private void Record(int channel, float[] chunk)
    {
        lock (recordingGate)
        {
            if (channel is < 0 or > 1 || recording[channel] is not { } writer) return;
            var bytes = new byte[chunk.Length * 2];
            for (var i = 0; i < chunk.Length; i++)
            {
                var v = (short)Math.Clamp(chunk[i] * 32767f, short.MinValue, short.MaxValue);
                bytes[2 * i] = (byte)v;
                bytes[2 * i + 1] = (byte)(v >> 8);
            }
            writer.Write(bytes, 0, bytes.Length);
        }
    }

    /// Closes the recordings; per channel its path, or null when none was kept (or nothing was heard
    /// there: an empty microphone file is deleted at once).
    private string?[] StopRecording()
    {
        lock (recordingGate)
        {
            var paths = new string?[2];
            for (var ch = 0; ch < 2; ch++)
            {
                if (recording[ch] is not { } writer) continue;
                var empty = writer.Length == 0;
                writer.Dispose();
                recording[ch] = null;
                if (empty) { try { File.Delete(recordingPath[ch]!); } catch { } }
                else paths[ch] = recordingPath[ch];
            }
            return paths;
        }
    }

    /// Who said what, checked again over the whole meeting, each channel against its own
    /// recording (so my words are never re-heard as the video's); then the recordings are deleted.
    private void RedoSpeakers(string id, string?[] paths)
    {
        HashSet<string> mic;
        lock (finalGate) mic = [.. micLines];
        RedoGate.Wait();
        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        try
        {
            // Background work: whatever the user does meanwhile comes first.
            thread.Priority = ThreadPriority.BelowNormal;
            for (var ch = 0; ch < paths.Length; ch++)
                if (paths[ch] is { } path) RedoSpeakers(id, path, ch == 1 ? mic.Contains : l => !mic.Contains(l));
        }
        finally
        {
            thread.Priority = priority;
            RedoGate.Release();
        }
    }

    private void RedoSpeakers(string id, string path, Func<string, bool> ofChannel)
    {
        try
        {
            var snap = store.Snapshot(id);
            var lines = snap?.Lines.Where(l => ofChannel(l.Id)).ToList();
            if (lines == null || Redo == null || lines.Count(l => l.Speaker == "them") < 4) return;
            using var recording = new RecordingReader(path);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var said = watch.Elapsed;
            var paused = TimeSpan.Zero;
            void Step(int done, int total)
            {
                // A new meeting is being heard: it needs the recognizer; carry on once it ends.
                if (IsListening)
                {
                    var at = watch.Elapsed;
                    Log.Debug($"redo: paused at {done}/{total} while a meeting is heard");
                    while (IsListening) Thread.Sleep(1000);
                    paused += watch.Elapsed - at;
                }
                if (watch.Elapsed - said < TimeSpan.FromSeconds(60)) return;
                said = watch.Elapsed;
                Log.Debug($"redo: {done}/{total} steps, {watch.Elapsed.TotalSeconds:F0} s");
            }
            var plan = Redo(recording.Slice, lines, Step);
            Log.Debug($"redo: {recording.Seconds:F0} s checked in {watch.ElapsedMilliseconds} ms" +
                      (paused > TimeSpan.Zero ? $" ({paused.TotalSeconds:F0} s of it paused)" : "") +
                      $", {plan?.Relabelled ?? 0} relabelled, {plan?.Split ?? 0} split");
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
        string shown;
        lock (gate)
        {
            partial = shown = HeardText.Stitch(partial, text);
            partialLang = lang;
            if (text.Length == 0) partialTranslation = partialTranslatedFrom = frozenSrc = frozenDst = "";
            epoch = partialEpoch;
        }
        if (text.Length > 0) endWatch?.Heard(DateTime.Now);
        Render();
        TranslateDraft(shown, lang, epoch);
    }

    private void TranslateDraft(string text, string lang, int epoch)
    {
        if (!settings.TranslationOn || text.Length < 2 || translator.MachineResting) return;
        var target = TargetFor(lang);
        if (Languages.SameLanguage(lang, target)) return;
        string keptSrc, keptDst, ended, going, nextSrc;
        lock (gate)
        {
            // One draft at a time, at most one a second: the free endpoint blocks eager clients.
            if (draftInFlight || text == lastDraftSent || DateTime.Now - lastDraftAt < TimeSpan.FromSeconds(1)) return;
            // Where the kept pieces end in the text now (its punctuation, a word or two, may have been
            // redone). Lost entirely: translate afresh.
            var at = frozenSrc.Length == 0 ? 0 : StableDraft.After(frozenSrc, text);
            if (at < 0)
            {
                frozenSrc = frozenDst = "";
                at = 0;
            }
            keptSrc = frozenSrc;
            keptDst = frozenDst;
            var rest = text[at..];
            var cut = StableDraft.EndedUpTo(rest);
            ended = rest[..cut];
            going = rest[cut..];
            nextSrc = text[..(at + cut)];
            if (!rest.Any(char.IsLetterOrDigit)) return;
            draftInFlight = true;
            lastDraftSent = text;
            lastDraftAt = DateTime.Now;
        }
        _ = Task.Run(async () =>
        {
            // Sentences that just ended, then the one being said: shown together, in one change.
            string? endedOut = ended.Any(char.IsLetterOrDigit) ? await translator.Translate(ended, lang, target, TranslationService.Kind.Draft) : "";
            string? goingOut = endedOut != null && going.Any(char.IsLetterOrDigit)
                ? await translator.Translate(going, lang, target, TranslationService.Kind.Draft) : "";
            bool again;
            lock (gate)
            {
                draftInFlight = false;
                if (endedOut != null && epoch == partialEpoch && partial.Length > 0 && frozenSrc == keptSrc)
                {
                    frozenSrc = nextSrc;
                    frozenDst = StableDraft.Join(keptDst, endedOut, target);
                    partialTranslation = StableDraft.Join(frozenDst, goingOut ?? "", target);
                    partialTranslatedFrom = goingOut != null ? text : frozenSrc;
                }
                // The line grew while this one was out: translate what is there now.
                again = partial.Length > 0 && partial != lastDraftSent && epoch == partialEpoch;
            }
            Render();
            if (again) TranslateDraft(partial, partialLang, epoch);
        });
    }

    private void OnFinal(HeardSegment seg)
    {
        lock (finalGate) OnFinalOne(seg);
    }

    private void OnFinalOne(HeardSegment seg)
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
        string draft, draftFrom, endedSrc, endedDst;
        lock (gate)
        {
            draft = partialTranslation;
            draftFrom = partialTranslatedFrom;
            endedSrc = frozenSrc;
            endedDst = frozenDst;
            partial = "";
            partialTranslation = partialTranslatedFrom = frozenSrc = frozenDst = "";
            lastDraftSent = "";
            partialEpoch++;
            // A piece joins only the line before it on its own channel: the PC and the microphone
            // are two people, never one line.
            var ch = Math.Clamp(seg.Channel, 0, 1);
            var prev = lastThemId[ch] == null ? null : store.Get(id)?.Lines.FirstOrDefault(l => l.Id == lastThemId[ch]);
            joined = prev != null && !seg.NewSpeaker && prev.SrcLang == seg.Lang && (voice == null || prev.Voice == null || voice == prev.Voice)
                     && HeardText.Continues(prev.Src, seg.Text, seg.Start - lastEnd[ch], lastCut[ch]);
            lastCut[ch] = seg.Cut;
            lastEnd[ch] = seg.End;
            if (joined)
            {
                lineId = prev!.Id;
                var text = HeardText.Join(prev.Src, seg.Text);
                store.Mutate(id, _ =>
                {
                    prev.Src = text;
                    prev.End = seg.End;
                    prev.Fixes = LineFix.Merge(prev.Fixes, seg.Fixes);
                });
            }
            else
            {
                lineId = Guid.NewGuid().ToString("N")[..12];
                var line = new TranscriptLine
                {
                    Id = lineId, T = Math.Round(t, 2), End = Math.Round(seg.End, 2), Speaker = "them",
                    Src = seg.Text, SrcLang = seg.Lang, DstLang = target, Voice = voice, Fixes = seg.Fixes?.ToList(),
                };
                // Two channels hand lines over out of order: keep the transcript in time order.
                store.Mutate(id, s =>
                {
                    var at = s.Lines.Count;
                    while (at > 0 && s.Lines[at - 1].T > line.T) at--;
                    s.Lines.Insert(at, line);
                });
                lastThemId[ch] = lineId;
                if (ch == 1) micLines.Add(lineId);
            }
        }
        string? missing = null;
        string? expect = null;
        lock (gate)
        {
            lastShownId = lineId;
            // What the draft lacks of the line, null when the recognizer heard it again differently.
            var rest = draft.Length > 0 ? StableDraft.Rest(draftFrom, seg.Text) : null;
            // The sentence being said came out differently once finished: keep the sentences that had
            // ended, and translate the rest of the line after them.
            if (rest == null && endedDst.Length > 0 && StableDraft.Rest(endedSrc, seg.Text) is { } afterEnded)
            {
                draft = endedDst;
                rest = afterEnded;
            }
            if (rest != null && settings.TranslationOn)
            {
                // A piece joined on: the line's translation as on screen plus the draft of the piece.
                // The earlier part must have words on screen that stay (kept, or its own translation).
                var before = joined ? standIns.GetValueOrDefault(lineId) ?? store.Get(id)?.Lines.FirstOrDefault(l => l.Id == lineId)?.Dst : null;
                var stays = !joined || (before is { Length: > 0 } && (settled.Contains(lineId) || !standIns.ContainsKey(lineId)));
                standIns[lineId] = expect = StableDraft.Join(before ?? "", draft, target);
                if (stays)
                {
                    settled.Add(lineId);
                    if (rest.Length > 0) missing = rest;
                }
                else settled.Remove(lineId);
            }
            else
            {
                // No draft to keep: the line's own translation shows when it comes.
                if (draft.Length > 0 && settings.TranslationOn)
                {
                    var before = joined ? standIns.GetValueOrDefault(lineId) ?? store.Get(id)?.Lines.FirstOrDefault(l => l.Id == lineId)?.Dst : null;
                    standIns[lineId] = StableDraft.Join(before ?? "", draft, target);
                }
                settled.Remove(lineId);
            }
        }
        LineChanged?.Invoke(lineId);
        Render();
        if (missing != null) _ = AddMissing(id, lineId, missing, seg.Lang, target, expect!);
        TranslateLine(id, lineId);
        if (CheckLine is { } check) _ = Check(check, id, lineId, seg.Text);
        // Once two voices are on show (and every 20 lines after), the AI may know their names.
        if (voice != null) namer?.Guess(id);
    }

    /// Asks the AI check about a piece just heard; its corrections go into the line (wherever the
    /// misheard words still are), marked like the glossary's own, and the line is translated again.
    private async Task Check(Func<string, Task<List<LineFix>?>> check, string sessionId, string lineId, string heard)
    {
        try
        {
            if (await check(heard) is not { Count: > 0 } fixes) return;
            var changed = false;
            store.Mutate(sessionId, s =>
            {
                if (s.Lines.FirstOrDefault(l => l.Id == lineId) is not { } line) return;
                foreach (var f in fixes)
                {
                    if (!line.Src.Contains(f.From)) continue;
                    line.Src = line.Src.Replace(f.From, f.To);
                    line.Fixes = LineFix.Merge(line.Fixes, [f]);
                    changed = true;
                }
            });
            if (!changed) return;
            // Corrected words: the draft on screen says the old ones, the new translation must show.
            lock (gate) settled.Remove(lineId);
            Log.Debug($"glossary: the AI check corrected {fixes.Count} word(s) in a line");
            LineChanged?.Invoke(lineId);
            Render();
            TranslateLine(sessionId, lineId);
        }
        catch (Exception e) { Log.Debug($"glossary: AI check failed {e.Message}"); }
    }

    /// The words the draft lacked when the line ended, translated and added after what is on screen.
    /// Failing, the line's own translation shows instead when it comes.
    private async Task AddMissing(string sessionId, string lineId, string missing, string lang, string target, string expect)
    {
        var outText = await translator.Translate(missing, lang, target, TranslationService.Kind.Draft);
        lock (gate)
        {
            if (!standIns.TryGetValue(lineId, out var now) || now != expect || !settled.Contains(lineId)) return;
            if (outText != null) standIns[lineId] = StableDraft.Join(now, outText, target);
            else
            {
                settled.Remove(lineId);
                if (store.Get(sessionId)?.Lines.FirstOrDefault(l => l.Id == lineId)?.Dst != null) standIns.Remove(lineId);
            }
        }
        LineChanged?.Invoke(lineId);
        Render();
    }

    /// The translation of a line as the live views show it while the meeting runs: what was on screen
    /// (the draft, grown), not the line's own translation that came later in other words. Null: the
    /// line's own.
    public string? Shown(string lineId)
    {
        lock (gate) return standIns.GetValueOrDefault(lineId);
    }

    private void TranslateLine(string sessionId, string lineId, int attempt = 0)
    {
        var line = store.Get(sessionId)?.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line == null || !settings.TranslationOn) return;
        var src = line.Src;
        if (Languages.SameLanguage(line.SrcLang, line.DstLang)) return;
        string? shown;
        lock (gate) shown = standIns.GetValueOrDefault(lineId);
        _ = Task.Run(async () =>
        {
            var outText = await translator.Translate(src, line.SrcLang, line.DstLang, TranslationService.Kind.Sentence, shown);
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
            bool kept;
            lock (gate)
            {
                kept = settled.Contains(lineId);
                if (!kept) standIns.Remove(lineId);
            }
            LineChanged?.Invoke(lineId);
            if (!kept) Render();
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

    /// The subtitle: two running streams, what was said and its translation — the last few lines
    /// heard plus the one being said. The subtitle window shows the end of each. The AI's translation
    /// of a line comes a second or so after the line: until then the translation stream keeps the
    /// lines before it rather than going blank, and the new translation is added on, not swapped in.
    private void Render()
    {
        var id = SessionId;
        if (id == null) return;
        List<TranscriptLine> recent = [];
        string p, pt, pl;
        lock (gate)
        {
            if (lastShownId != null && store.Get(id)?.Lines is { } lines)
                recent = Recent(lines, lastShownId);
            p = partial;
            pt = partialTranslation;
            pl = partialLang;
        }
        var last = recent.Count > 0 ? recent[^1] : null;
        var srcJoin = Languages.Joiner(pl.Length > 0 ? pl : last?.SrcLang ?? "en");
        var source = string.Join(srcJoin, recent.Select(l => l.Src).Append(p).Where(x => x.Length > 0));
        var tgtJoin = Languages.Joiner(TargetFor(pl.Length > 0 ? pl : last?.SrcLang));
        string translation;
        lock (gate)
            translation = string.Join(tgtJoin, recent.Select(l => standIns.GetValueOrDefault(l.Id) ?? l.Dst ?? "").Append(pt).Where(x => x.Length > 0));
        SubtitleChanged?.Invoke(new Subtitle(source, translation, p.Length > 0, last?.Id, p, pt));
    }


    /// The lines still on screen: the one last shown and up to two heard just before it, back to
    /// the first pause of more than ten seconds (after a pause the subtitle starts afresh).
    internal static List<TranscriptLine> Recent(IReadOnlyList<TranscriptLine> lines, string lastId)
    {
        var at = -1;
        for (var i = lines.Count - 1; i >= 0; i--)
            if (lines[i].Id == lastId) { at = i; break; }
        if (at < 0) return [];
        var recent = new List<TranscriptLine> { lines[at] };
        for (var i = at - 1; i >= 0 && recent.Count < 3; i--)
        {
            var l = lines[i];
            if (l.Speaker != "them") continue;
            var gap = recent[0].T - (l.End ?? l.T);
            if (gap > 10) break;
            recent.Insert(0, l);
        }
        return recent;
    }
}
