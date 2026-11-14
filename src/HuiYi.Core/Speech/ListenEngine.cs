using HuiYi.Localization;
using System.Collections.Concurrent;
using System.Diagnostics;
using HuiYi.Audio;
using SherpaOnnx;

namespace HuiYi.Speech;

/// A finished stretch of speech. `Start`/`End`: seconds of audio since listening began. `Cut`: the
/// line was ended because it ran long, not at a pause, so the next one may carry it on.
/// `Samples`: the stretch's audio (16 kHz mono), for telling whose voice it is. `NewSpeaker`: it was
/// cut off the stretch before it because the voice changed there, so it never joins that line.
/// `Channel`: where it was heard (0: what the PC plays, 1: the microphone).
/// `Fixes`: what the user's glossary corrected in it (shown underlined in the transcript).
public sealed record HeardSegment(string Text, string Lang, double Start, double End, bool Cut, float[]? Samples = null,
    bool NewSpeaker = false, int Channel = 0, IReadOnlyList<LineFix>? Fixes = null);

/// Audio → Silero VAD → SenseVoice. A sentence ends at a pause (or after 12 s of unbroken speech);
/// while someone is talking, the speech so far is decoded again every ~0.6 s for the live (partial)
/// line. With `BothEnds`, what the PC plays and the microphone are heard as two channels, each
/// with its own VAD on its own thread, sharing the recognizer: two people talking at once (the far
/// side in the video, me in the room) are two lines, never one garbled one. Events fire on the
/// channels' threads.
public sealed class ListenEngine : IDisposable
{
    public event Action<string, string>? Partial;   // text, lang ("" clears)
    public event Action<HeardSegment>? Final;
    public event Action<string>? Failed;
    /// Every chunk heard, 16 kHz mono, per channel, on that channel's thread (for keeping the recording).
    public event Action<int, float[]>? Audio;

    public bool IsRunning { get; private set; }
    /// Where another person starts talking inside a stretch of speech (sample offsets; empty: one
    /// voice, or speakers aren't told apart). Each part becomes its own line: in a quick exchange
    /// with no pause the VAD hands over both people at once.
    /// Gets the stretch and its recognition (with sentence ends).
    public Func<float[], Recognized, IReadOnlyList<int>>? Turns { get; set; }
    /// Seconds of audio processed since Start (the first channel's clock).
    public double FedSeconds => channels.Count == 0 ? 0 : Interlocked.Read(ref channels[0].Fed) / (double)Recognizer.SampleRate;

    private const float MaxSpeech = 12f;
    private static readonly TimeSpan PartialEvery = TimeSpan.FromMilliseconds(600);
    private const int HistorySeconds = 30;
    /// The live line decodes only the last few seconds: a long stretch (a reverberant room has no
    /// pauses) would cost more on every refresh, and the finished line is decoded whole anyway.
    private const int PartialSeconds = 6;

    private readonly Recognizer recognizer;
    private readonly ModelStore models;
    private List<Channel> channels = [];
    private TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource cancellation = new();
    private readonly object partialGate = new();
    /// Completes after queued audio and the trailing sentence have been decoded.
    public Task Completion => completion.Task;
    /// File EOF closes input without dropping queued speech.
    public void CompleteInput() { foreach (var c in channels) c.Queue.CompleteAdding(); }
    public void CancelInput() { cancellation.Cancel(); CompleteInput(); }

    private sealed class Channel(int index, IAudioSource source)
    {
        public readonly int Index = index;
        public readonly IAudioSource Source = source;
        public readonly BlockingCollection<float[]> Queue = new(new ConcurrentQueue<float[]>(), 2000);
        /// The last 30 s heard, as a ring; the microphone channel reads the PC's to tell its echo.
        public readonly float[] History = new float[HistorySeconds * Recognizer.SampleRate];
        public long Fed;
        public Thread? Worker;
        public Action<float[]>? Feed;
        public string Partial = "", PartialLang = "";
    }

    public ListenEngine(Recognizer recognizer, ModelStore models)
    {
        this.recognizer = recognizer;
        this.models = models;
    }

    public void Start(IAudioSource audio)
    {
        if (IsRunning) Stop();
        IsRunning = true;
        completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellation = new();
        var token = cancellation.Token;
        var both = audio as BothEnds;
        var list = new List<Channel> { new(0, both?.Pc ?? audio) };
        if (both?.Mic != null) list.Add(new Channel(1, both.Mic));
        channels = list;
        var running = list.Count;
        var done = completion;
        foreach (var c in list)
        {
            c.Worker = new Thread(() =>
            {
                try { Run(c, token, c.Source.CanWait); }
                finally
                {
                    c.Queue.CompleteAdding();
                    if (Interlocked.Decrement(ref running) == 0) done.TrySetResult();
                }
            }) { IsBackground = true, Name = "listen-" + c.Index, Priority = ThreadPriority.AboveNormal };
            c.Worker.Start();
            var mute = c.Index == 1 ? both?.MuteMic : null;
            c.Feed = samples =>
            {
                // While I hold the interpreting key my words become a 我 line of their own.
                if (mute?.Invoke() == true) samples = new float[samples.Length];
                Feed(c, samples);
            };
            c.Source.Samples += c.Feed;
            c.Source.Failed += OnFailed;
        }
        foreach (var c in list)
        {
            if (c.Index == 1)
            {
                // A missing or broken microphone must not stop the meeting: the PC's side carries on.
                c.Source.Failed -= OnFailed;
                c.Source.Failed += m => Log.Debug($"listen: microphone left out ({m})");
            }
            c.Source.Start();
        }
    }

    private void OnFailed(string message) => Failed?.Invoke(message);

    private void Feed(Channel c, float[] samples)
    {
        var q = c.Queue;
        if (q.IsAddingCompleted) return;
        // A stalled decoder must never back up live audio: drop rather than block. A file read
        // faster than real time waits instead, so tests hear all of it.
        if (c.Source.CanWait)
        {
            try { q.Add(samples, cancellation.Token); }
            catch (InvalidOperationException) { }
            catch (OperationCanceledException) { }
        }
        else
        {
            try { if (!q.TryAdd(samples)) Debug.WriteLine("listen: queue full, dropped audio"); }
            catch (InvalidOperationException) { }
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        foreach (var c in channels)
        {
            if (c.Feed != null) c.Source.Samples -= c.Feed;
            c.Source.Failed -= OnFailed;
            c.Source.Dispose();
            c.Queue.CompleteAdding();
        }
        // Each channel finishes its last sentence; the recognizer must outlive them.
        foreach (var c in channels) c.Worker?.Join(10000);
    }

    /// The live line: whichever channel is talking now; when one goes quiet, the other's shows.
    private void SetPartial(Channel c, string text, string lang)
    {
        lock (partialGate)
        {
            c.Partial = text;
            c.PartialLang = lang;
            if (text.Length > 0) { Partial?.Invoke(text, lang); return; }
            var other = channels.FirstOrDefault(o => o != c && o.Partial.Length > 0);
            if (other != null) Partial?.Invoke(other.Partial, other.PartialLang);
            else Partial?.Invoke("", "");
        }
    }

    private void Run(Channel c, CancellationToken token, bool offline)
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = models.Vad;
        config.SileroVad.Threshold = 0.5f;
        // A reply often follows after only ~0.2–0.3 s: 0.3 s of quiet ends a line, so it starts its own.
        config.SileroVad.MinSilenceDuration = 0.3f;
        config.SileroVad.MinSpeechDuration = 0.25f;
        config.SileroVad.MaxSpeechDuration = MaxSpeech;
        config.SileroVad.WindowSize = 512;
        config.SampleRate = Recognizer.SampleRate;
        config.NumThreads = 1;
        config.Provider = "cpu";
        VoiceActivityDetector vad;
        try { vad = new VoiceActivityDetector(config, 60); }
        catch (Exception e)
        {
            Log.Debug($"listen: VAD failed {e.Message}");
            Failed?.Invoke(L.T("语音检测模型加载失败"));
            return;
        }

        var rate = Recognizer.SampleRate;
        var history = c.History;
        long fed = 0;
        long speechStart = -1;
        // Silero's own 12 s limit does not hold when there is never a pause (a reverberant room, a
        // video over music): a stretch running past it is handed on here, and what the VAD later
        // hands over up to `consumed` is left out.
        long consumed = 0, runStart = -1;
        var lastPartial = Stopwatch.StartNew();
        var partialShown = false;
        string lastPartialText = "";

        try
        {
            foreach (var chunk in c.Queue.GetConsumingEnumerable(token))
            {
                Audio?.Invoke(c.Index, chunk);
                for (var i = 0; i < chunk.Length; i++) history[(fed + i) % history.Length] = chunk[i];
                fed += chunk.Length;
                Interlocked.Exchange(ref c.Fed, fed);
                vad.AcceptWaveform(chunk);

                while (!vad.IsEmpty())
                {
                    var seg = vad.Front();
                    var samples = seg.Samples;
                    long start = seg.Start;
                    vad.Pop();
                    if (start + samples.Length <= consumed + rate / 5) continue;
                    if (start < consumed)
                    {
                        samples = samples[(int)(consumed - start)..];
                        start = consumed;
                    }
                    var cut = samples.Length >= (MaxSpeech - 0.5f) * rate;
                    var said = Finish(c, samples, start, cut);
                    // The live line continues from where this piece ended.
                    speechStart = vad.IsSpeechDetected() ? start + samples.Length : -1;
                    runStart = speechStart;
                    lastPartialText = "";
                    if (!said && partialShown) SetPartial(c, "", "");
                    partialShown = false;
                }

                if (vad.IsSpeechDetected())
                {
                    if (runStart < 0) runStart = Math.Max(consumed, fed - (long)(0.6 * rate));
                    if (fed - runStart >= (MaxSpeech + 1) * rate)
                    {
                        Finish(c, Slice(history, runStart, fed), runStart, true);
                        consumed = runStart = fed;
                        speechStart = fed;
                        lastPartialText = "";
                    }
                }
                else runStart = -1;

                if (!offline && vad.IsSpeechDetected())
                {
                    // Silero needs ~0.3 s of speech to say so: start the live line a little earlier.
                    if (speechStart < 0) speechStart = Math.Max(0, fed - (long)(0.6 * rate));
                    // Both ends talking at once share the recognizer: refresh half as often.
                    var every = channels.Count > 1 && channels.Any(o => o != c && o.Partial.Length > 0) ? PartialEvery * 2 : PartialEvery;
                    if (lastPartial.Elapsed >= every && fed - speechStart >= rate / 2)
                    {
                        lastPartial.Restart();
                        var slice = Slice(history, Math.Max(speechStart, fed - PartialSeconds * rate), fed);
                        var r = recognizer.Recognize(slice);
                        var text = HeardText.Clean(r.Text);
                        if (text.Length > 0 && text != lastPartialText)
                        {
                            lastPartialText = text;
                            partialShown = true;
                            SetPartial(c, text, r.Lang);
                        }
                    }
                }
                else
                {
                    speechStart = -1;
                }
            }
            // Listening stopped: whatever was being said still counts.
            vad.Flush();
            while (!vad.IsEmpty())
            {
                var seg = vad.Front();
                var samples = seg.Samples;
                var start = seg.Start;
                vad.Pop();
                Finish(c, samples, start, false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Debug($"listen: worker {c.Index} failed {e}");
            Failed?.Invoke(L.T("识别出错：") + e.Message);
        }
        finally
        {
            vad.Dispose();
        }
    }

    private static float[] Slice(float[] history, long from, long to)
    {
        from = Math.Max(from, to - history.Length);
        var n = (int)Math.Max(0, to - from);
        var outp = new float[n];
        for (var i = 0; i < n; i++) outp[i] = history[(from + i) % history.Length];
        return outp;
    }

    /// The microphone hearing the speakers: the PC played the same words a moment before, so it is
    /// the far side again, not someone in the room.
    private bool IsEcho(Channel c, long start, int length, string heard)
    {
        if (c.Index == 0 || channels.Count < 2) return false;
        var pc = channels[0];
        var rate = Recognizer.SampleRate;
        var from = Math.Max(0, start - (long)(EchoCheck.MaxLag * rate));
        var to = start + length + rate / 4;
        // The PC's channel may be behind on its own thread (it decodes too): wait for it, up to 2 s.
        // A finished line on the microphone can wait; a half-heard echo would pass as new words.
        for (var i = 0; i < 40 && IsRunning && !pc.Queue.IsCompleted && Interlocked.Read(ref pc.Fed) < to; i++) Thread.Sleep(50);
        to = Math.Min(to, Interlocked.Read(ref pc.Fed));
        if (to - from > pc.History.Length || to - from < rate / 2) return false;
        var played = Slice(pc.History, from, to);
        if (!EchoCheck.Playing(played)) return false;
        var said = HeardText.Clean(recognizer.Recognize(played).Text);
        var overlap = EchoCheck.Overlap(heard, said);
        Log.Debug($"listen: microphone {length / (double)rate:F1} s, {overlap:P0} of its words also played on the PC");
        return EchoCheck.IsEcho(heard, said);
    }

    /// Recognizes a stretch of speech, cut where the speaker changes, and hands on each part with
    /// words in it. True when anything was said.
    private bool Finish(Channel c, float[] samples, long start, bool cut)
    {
        var rate = Recognizer.SampleRate;
        var watch = Stopwatch.StartNew();
        var whole = recognizer.Recognize(samples);
        IReadOnlyList<int> points = [];
        if (Turns != null && samples.Length >= 2 * TurnFinder.MinSentence * rate)
        {
            try { points = Turns(samples, whole); }
            catch (Exception e) { Log.Debug($"listen: turn split failed {e.Message}"); }
        }
        var edges = new List<int> { 0 };
        edges.AddRange(points);
        edges.Add(samples.Length);
        var said = false;
        for (var k = 0; k + 1 < edges.Count; k++)
        {
            var piece = points.Count == 0 ? samples : samples[edges[k]..edges[k + 1]];
            // One part: the whole decode serves; parts are decoded on their own.
            var r = points.Count == 0 ? whole : recognizer.Recognize(piece);
            var text = HeardText.Clean(r.Text);
            var last = k + 2 == edges.Count;
            Log.Debug($"listen: final [{c.Index}] {piece.Length / (double)rate:F1} s audio, {text.Length} chars, {r.Lang}" +
                      (points.Count > 0 ? $", part {k + 1}/{edges.Count - 1} (speaker changed)" : "") +
                      $", {watch.ElapsedMilliseconds} ms{(cut && last ? " (cut)" : "")}");
            if (!text.Any(char.IsLetter)) continue;
            // Each part on its own: one stretch on the microphone can be the speakers' echo and then
            // me answering over it.
            if (IsEcho(c, start + edges[k], piece.Length, text))
            {
                Log.Debug($"listen: {piece.Length / (double)rate:F1} s on the microphone is the speakers' echo, left out");
                continue;
            }
            said = true;
            Final?.Invoke(new HeardSegment(text, r.Lang, (start + edges[k]) / (double)rate, (start + edges[k + 1]) / (double)rate,
                cut && last, piece, NewSpeaker: k > 0, Channel: c.Index, Fixes: LineFix.Kept(r.Fixes, text)));
        }
        return said;
    }

    public void Dispose() => Stop();
}
