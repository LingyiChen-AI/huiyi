using System.Diagnostics;
using HuiYi.Localization;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace HuiYi.Audio;

/// Turns whatever WASAPI hands us (usually 48 kHz float stereo) into 16 kHz mono float, the
/// recognizer's format: downmix, then a box-filtered linear resample (good enough for speech).
public sealed class Resampler(int inputRate, int outputRate = 16000)
{
    private readonly double step = (double)inputRate / outputRate;
    private double pos;
    private float last;

    public float[] Process(float[] mono)
    {
        if (inputRate == outputRate) return mono;
        // Low-pass a little before dropping samples: average over the step.
        var width = Math.Max(1, (int)Math.Round(step));
        var smooth = new float[mono.Length];
        float acc = 0;
        for (var i = 0; i < mono.Length; i++)
        {
            acc += mono[i];
            if (i >= width) acc -= mono[i - width];
            smooth[i] = acc / Math.Min(i + 1, width);
        }
        var output = new List<float>((int)(mono.Length / step) + 2);
        while (pos < mono.Length)
        {
            var i = (int)pos;
            var frac = (float)(pos - i);
            var a = smooth[i];
            var b = i + 1 < smooth.Length ? smooth[i + 1] : smooth[^1];
            output.Add(a + (b - a) * frac);
            pos += step;
        }
        pos -= mono.Length;
        if (smooth.Length > 0) last = smooth[^1];
        return output.ToArray();
    }

    public static float[] ToMonoFloat(byte[] buffer, int bytes, WaveFormat format)
    {
        var channels = format.Channels;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible { SubFormat: var sf } && sf == NAudio.MediaFoundation.AudioSubtypes.MFAudioFormat_Float)
        {
            var frames = bytes / 4 / channels;
            var mono = new float[frames];
            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++) sum += BitConverter.ToSingle(buffer, (f * channels + c) * 4);
                mono[f] = sum / channels;
            }
            return mono;
        }
        if (format.BitsPerSample == 16)
        {
            var frames = bytes / 2 / channels;
            var mono = new float[frames];
            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++) sum += BitConverter.ToInt16(buffer, (f * channels + c) * 2) / 32768f;
                mono[f] = sum / channels;
            }
            return mono;
        }
        if (format.BitsPerSample == 32)
        {
            var frames = bytes / 4 / channels;
            var mono = new float[frames];
            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++) sum += BitConverter.ToInt32(buffer, (f * channels + c) * 4) / 2147483648f;
                mono[f] = sum / channels;
            }
            return mono;
        }
        throw new NotSupportedException($"audio format {format}");
    }
}

/// An audio source that delivers 16 kHz mono float chunks on its own thread.
public interface IAudioSource : IDisposable
{
    event Action<float[]>? Samples;
    event Action<string>? Failed;
    void Start();
    void Stop();
    /// A file read faster than real time may wait for the decoder; live audio never can.
    bool CanWait => false;
}

/// What the PC plays: WASAPI loopback on the default output device. Follows the default device
/// when it changes (headphones plugged in) by restarting.
public sealed class LoopbackSource : IAudioSource
{
    public event Action<float[]>? Samples;
    public event Action<string>? Failed;
    private WasapiLoopbackCapture? capture;
    private Resampler? resampler;
    private volatile bool running;

    public void Start()
    {
        running = true;
        Open();
    }

    private void Open()
    {
        try
        {
            capture = new WasapiLoopbackCapture();
            var format = capture.WaveFormat;
            resampler = new Resampler(format.SampleRate);
            Log.Debug($"loopback: {format.SampleRate} Hz {format.Channels} ch {format.Encoding} on {new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName}");
            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded == 0) return;
                var mono = Resampler.ToMonoFloat(e.Buffer, e.BytesRecorded, format);
                Samples?.Invoke(resampler.Process(mono));
            };
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) Log.Debug($"loopback: stopped {e.Exception.Message}");
                if (running)
                {
                    // Device change or an error: reopen on the (new) default device.
                    Task.Delay(500).ContinueWith(_ => { if (running) Reopen(); });
                }
            };
            capture.StartRecording();
        }
        catch (Exception e)
        {
            Log.Debug($"loopback: start failed {e.Message}");
            Failed?.Invoke(L.T("无法采集系统声音：") + e.Message);
        }
    }

    private void Reopen()
    {
        try { capture?.Dispose(); } catch { }
        Open();
    }

    public void Stop()
    {
        running = false;
        try { capture?.StopRecording(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        capture?.Dispose();
    }
}

/// The microphone (default capture device, or one chosen by endpoint id). The device is picked
/// again whenever capture drops (a USB conference mic unplugged and back), like LoopbackSource.
public sealed class MicSource : IAudioSource
{
    public event Action<float[]>? Samples;
    public event Action<string>? Failed;
    private readonly Func<string?> pick;
    private WasapiCapture? capture;
    private volatile bool running;

    public MicSource(string? deviceId = null) : this(() => deviceId) { }
    public MicSource(Func<string?> pick) => this.pick = pick;

    public void Start()
    {
        running = true;
        Open();
    }

    private void Open()
    {
        try
        {
            var en = new MMDeviceEnumerator();
            var id = pick();
            var device = (id == null ? null
                : en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault(d => d.ID == id))
                ?? en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            capture = new WasapiCapture(device, true, 20);
            var format = capture.WaveFormat;
            var resampler = new Resampler(format.SampleRate);
            Log.Debug($"mic: {format.SampleRate} Hz {format.Channels} ch on {device.FriendlyName}");
            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded == 0) return;
                Samples?.Invoke(resampler.Process(Resampler.ToMonoFloat(e.Buffer, e.BytesRecorded, format)));
            };
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) Log.Debug($"mic: stopped {e.Exception.Message}");
                if (running) Task.Delay(1000).ContinueWith(_ => { if (running) Reopen(); });
            };
            capture.StartRecording();
        }
        catch (Exception e)
        {
            Log.Debug($"mic: start failed {e.Message}");
            Failed?.Invoke(L.T("无法打开麦克风：") + e.Message);
        }
    }

    private void Reopen()
    {
        try { capture?.Dispose(); } catch { }
        Open();
    }

    public void Stop()
    {
        running = false;
        try { capture?.StopRecording(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        capture?.Dispose();
    }
}

/// Both ends of a meeting: what the PC plays (the far side) and the microphone (me, or a room
/// round one mic). ListenEngine hears them as two channels, each recognized on its own, so two
/// people talking at once both land in the transcript. `MuteMic`: the microphone is silenced while
/// it returns true (my words while I hold the interpreting key become a 我 line of their own).
/// Not a source to read samples from: Start/Stop only pass through.
public sealed class BothEnds(IAudioSource pc, IAudioSource? mic, Func<bool>? muteMic = null) : IAudioSource
{
    public IAudioSource Pc { get; } = pc;
    public IAudioSource? Mic { get; } = mic;
    public Func<bool>? MuteMic { get; } = muteMic;
#pragma warning disable CS0067 // the channels raise their own
    public event Action<float[]>? Samples;
    public event Action<string>? Failed;
#pragma warning restore CS0067
    public void Start() { Pc.Start(); Mic?.Start(); }
    public void Stop() { Pc.Stop(); Mic?.Stop(); }
    public void Dispose() { Pc.Dispose(); Mic?.Dispose(); }
}

/// Keeps a source on the wall clock: WASAPI loopback sends nothing while nothing plays, so its
/// sample count would fall behind the microphone's (and a sentence cut off by a pause would never
/// see the quiet that ends it). Silence fills any gap longer than `Slack`, on arrival and from a
/// timer, so sample n is always about n / 16000 s after Start.
public sealed class SteadySource(IAudioSource inner, Func<double>? clock = null) : IAudioSource
{
    public event Action<float[]>? Samples;
    public event Action<string>? Failed;
    private const int Rate = 16000;
    /// How far behind the clock the source may run before silence is put in (0.25 s).
    public const int Slack = Rate / 4;
    private readonly object gate = new();
    private readonly Stopwatch watch = new();
    private Timer? timer;
    private long sent;

    private double Now => clock?.Invoke() ?? watch.Elapsed.TotalSeconds;

    public void Start()
    {
        watch.Restart();
        sent = 0;
        inner.Samples += OnSamples;
        inner.Failed += OnFailed;
        inner.Start();
        if (clock == null) timer = new Timer(_ => Tick(), null, 100, 100);
    }

    private void OnFailed(string m) => Failed?.Invoke(m);

    private void OnSamples(float[] chunk)
    {
        lock (gate)
        {
            // The gap before this chunk arrived: the chunk itself ends now.
            Fill((long)(Now * Rate) - chunk.Length);
            sent += chunk.Length;
            Samples?.Invoke(chunk);
        }
    }

    /// Called from the timer (or a test): silence up to the clock when nothing came in.
    public void Tick()
    {
        lock (gate) Fill((long)(Now * Rate) - Slack / 2);
    }

    private void Fill(long upTo)
    {
        var gap = upTo - sent;
        if (gap <= Slack) return;
        var quiet = new float[gap];
        sent += gap;
        Samples?.Invoke(quiet);
    }

    public void Stop()
    {
        timer?.Dispose();
        timer = null;
        inner.Stop();
    }

    public void Dispose()
    {
        Stop();
        inner.Samples -= OnSamples;
        inner.Dispose();
    }
}

/// Tells the microphone hearing the speakers from someone speaking in the room, by what was said:
/// the PC's audio over the same moment is recognized too, and an echo says the same words (a
/// room's reverb and a dull speaker blur the loudness curve, but not the words). Measured on
/// simulated rooms: loudness matching could not tell a reverberant echo from a voice in the room.
public static class EchoCheck
{
    /// Seconds the speakers' sound may take to reach the microphone (output latency plus the room).
    public const double MaxLag = 0.4;
    /// Share of the microphone's words found, in order, in what the PC played, above which it is an echo.
    public const double Threshold = 0.6;

    /// How much of `heard` (the microphone's line) appears, in order, in `played` (the PC's words
    /// over the same moment): longest common subsequence over letters and digits, 0–1.
    public static double Overlap(string heard, string played)
    {
        var a = Letters(heard);
        var b = Letters(played);
        if (a.Length == 0 || b.Length == 0) return 0;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length] / (double)a.Length;
    }

    /// A short line (under 8 letters) has few words to match, and an echo of it is often misheard:
    /// it needs less. Measured: my own short lines over a video matched at most 33 %.
    public const double ShortThreshold = 0.4;

    public static bool IsEcho(string heard, string played) =>
        Overlap(heard, played) >= (Letters(heard).Length < 8 ? ShortThreshold : Threshold);

    /// Whether anything was playing at all (peak 20 ms loudness).
    public static bool Playing(float[] played)
    {
        const int frame = 320;
        for (var i = 0; i + frame <= played.Length; i += frame)
        {
            double sum = 0;
            for (var j = 0; j < frame; j++) { var v = played[i + j]; sum += v * v; }
            if (Math.Sqrt(sum / frame) >= 0.01) return true;
        }
        return false;
    }

    private static string Letters(string s) =>
        new([.. s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}

/// An audio file played through the pipeline at real speed (tests and the self-check), or as fast
/// as possible.
public sealed class FileSource(string path, bool realTime = true) : IAudioSource
{
    public bool CanWait => !realTime;
    public event Action<float[]>? Samples;
    public event Action<string>? Failed;
    public event Action? Finished;
    private CancellationTokenSource? cts;

    public void Start()
    {
        cts = new CancellationTokenSource();
        var token = cts.Token;
        Task.Run(async () =>
        {
            try
            {
                using var reader = new MediaFoundationReader(path);
                var format = reader.WaveFormat;
                var resampler = new Resampler(format.SampleRate);
                var chunk = new byte[format.AverageBytesPerSecond / 10 / format.BlockAlign * format.BlockAlign];
                int n;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                double fed = 0;
                while (!token.IsCancellationRequested && (n = reader.Read(chunk, 0, chunk.Length)) > 0)
                {
                    var mono = Resampler.ToMonoFloat(chunk, n, format);
                    Samples?.Invoke(resampler.Process(mono));
                    fed += (double)mono.Length / format.SampleRate;
                    if (realTime)
                    {
                        var ahead = fed - clock.Elapsed.TotalSeconds;
                        if (ahead > 0) await Task.Delay(TimeSpan.FromSeconds(ahead), token);
                    }
                }
                // A little silence so the last sentence ends.
                for (var i = 0; i < 20 && !token.IsCancellationRequested; i++)
                {
                    Samples?.Invoke(new float[1600]);
                    if (realTime) await Task.Delay(100, token);
                }
                Finished?.Invoke();
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Failed?.Invoke(e.Message);
            }
        }, token);
    }

    public void Stop() => cts?.Cancel();
    public void Dispose() => Stop();
}
