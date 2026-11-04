using System.Collections.Concurrent;
using System.Diagnostics;
using HuiYi.Audio;
using SherpaOnnx;

namespace HuiYi.Speech;

/// A finished stretch of speech. `Start`/`End`: seconds of audio since listening began. `Cut`: the
/// line was ended because it ran long, not at a pause, so the next one may carry it on.
/// `Samples`: the stretch's audio (16 kHz mono), for telling whose voice it is.
public sealed record HeardSegment(string Text, string Lang, double Start, double End, bool Cut, float[]? Samples = null);

/// What the PC plays → Silero VAD → SenseVoice. A sentence ends at a pause (or after 12 s of
/// unbroken speech); while someone is talking, the speech so far is decoded again every ~0.6 s for
/// the live (partial) line. Runs on its own thread; events fire there.
public sealed class ListenEngine : IDisposable
{
    public event Action<string, string>? Partial;   // text, lang ("" clears)
    public event Action<HeardSegment>? Final;
    public event Action<string>? Failed;

    public bool IsRunning { get; private set; }
    /// Seconds of audio processed since Start.
    public double FedSeconds => Interlocked.Read(ref total) / (double)Recognizer.SampleRate;

    private const float MaxSpeech = 12f;
    private static readonly TimeSpan PartialEvery = TimeSpan.FromMilliseconds(600);
    private const int HistorySeconds = 30;

    private readonly Recognizer recognizer;
    private readonly ModelStore models;
    private BlockingCollection<float[]>? queue;
    private Thread? worker;
    private IAudioSource? source;
    private long total;

    public ListenEngine(Recognizer recognizer, ModelStore models)
    {
        this.recognizer = recognizer;
        this.models = models;
    }

    public void Start(IAudioSource audio)
    {
        if (IsRunning) Stop();
        IsRunning = true;
        Interlocked.Exchange(ref total, 0);
        queue = new BlockingCollection<float[]>(new ConcurrentQueue<float[]>(), 2000);
        var q = queue;
        worker = new Thread(() => Run(q)) { IsBackground = true, Name = "listen", Priority = ThreadPriority.AboveNormal };
        worker.Start();
        source = audio;
        source.Samples += Feed;
        source.Failed += OnFailed;
        source.Start();
    }

    private void OnFailed(string message) => Failed?.Invoke(message);

    private void Feed(float[] samples)
    {
        if (queue is not { IsAddingCompleted: false } q) return;
        // A stalled decoder must never back up live audio: drop rather than block. A file read
        // faster than real time waits instead, so tests hear all of it.
        if (source?.CanWait == true)
        {
            try { q.Add(samples); } catch (InvalidOperationException) { }
        }
        else if (!q.TryAdd(samples)) Debug.WriteLine("listen: queue full, dropped audio");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        if (source != null)
        {
            source.Samples -= Feed;
            source.Failed -= OnFailed;
            source.Dispose();
            source = null;
        }
        queue?.CompleteAdding();
        worker?.Join(3000);
        worker = null;
    }

    private void Run(BlockingCollection<float[]> q)
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = models.Vad;
        config.SileroVad.Threshold = 0.5f;
        config.SileroVad.MinSilenceDuration = 0.5f;
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
            Failed?.Invoke("语音检测模型加载失败");
            return;
        }

        var rate = Recognizer.SampleRate;
        var history = new float[HistorySeconds * rate];   // ring buffer of the last 30 s
        long fed = 0;
        long speechStart = -1;
        var lastPartial = Stopwatch.StartNew();
        var partialShown = false;
        string lastPartialText = "";

        float[] Slice(long from, long to)
        {
            from = Math.Max(from, to - history.Length);
            var n = (int)(to - from);
            var outp = new float[n];
            for (var i = 0; i < n; i++) outp[i] = history[(from + i) % history.Length];
            return outp;
        }

        try
        {
            foreach (var chunk in q.GetConsumingEnumerable())
            {
                for (var i = 0; i < chunk.Length; i++) history[(fed + i) % history.Length] = chunk[i];
                fed += chunk.Length;
                Interlocked.Exchange(ref total, fed);
                vad.AcceptWaveform(chunk);

                while (!vad.IsEmpty())
                {
                    var seg = vad.Front();
                    var samples = seg.Samples;
                    var start = seg.Start;
                    vad.Pop();
                    var watch = Stopwatch.StartNew();
                    var r = recognizer.Recognize(samples);
                    var text = HeardText.Clean(r.Text);
                    var cut = samples.Length >= (MaxSpeech - 0.5f) * rate;
                    Log.Debug($"listen: final {samples.Length / (double)rate:F1} s audio, {text.Length} chars, {r.Lang}, decoded in {watch.ElapsedMilliseconds} ms{(cut ? " (cut)" : "")}");
                    // The live line continues from where this piece ended.
                    speechStart = vad.IsSpeechDetected() ? start + samples.Length : -1;
                    lastPartialText = "";
                    if (text.Any(char.IsLetter))
                        Final?.Invoke(new HeardSegment(text, r.Lang, start / (double)rate, (start + samples.Length) / (double)rate, cut, samples));
                    else if (partialShown)
                    {
                        partialShown = false;
                        Partial?.Invoke("", "");
                    }
                    partialShown = false;
                }

                if (vad.IsSpeechDetected())
                {
                    // Silero needs ~0.3 s of speech to say so: start the live line a little earlier.
                    if (speechStart < 0) speechStart = Math.Max(0, fed - (long)(0.6 * rate));
                    if (lastPartial.Elapsed >= PartialEvery && fed - speechStart >= rate / 2)
                    {
                        lastPartial.Restart();
                        var r = recognizer.Recognize(Slice(speechStart, fed));
                        var text = HeardText.Clean(r.Text);
                        if (text.Length > 0 && text != lastPartialText)
                        {
                            lastPartialText = text;
                            partialShown = true;
                            Partial?.Invoke(text, r.Lang);
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
                var r = recognizer.Recognize(samples);
                var text = HeardText.Clean(r.Text);
                if (text.Any(char.IsLetter))
                    Final?.Invoke(new HeardSegment(text, r.Lang, start / (double)rate, (start + samples.Length) / (double)rate, false, samples));
            }
        }
        catch (Exception e)
        {
            Log.Debug($"listen: worker failed {e}");
            Failed?.Invoke("识别出错：" + e.Message);
        }
        finally
        {
            vad.Dispose();
        }
    }

    public void Dispose() => Stop();
}
