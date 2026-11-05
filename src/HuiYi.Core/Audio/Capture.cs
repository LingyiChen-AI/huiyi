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
            Failed?.Invoke("无法采集系统声音：" + e.Message);
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
            Failed?.Invoke("无法打开麦克风：" + e.Message);
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

/// What the PC plays and the microphone, heard as one stream, the way a meeting app records a
/// call: the other side (from the PC) and this end (me, or a room round one mic) both land in the
/// transcript. The microphone's steady clock drives the output; the PC's sound, which WASAPI only
/// sends while something plays, is queued and laid over it. With no working microphone the PC's
/// sound still comes through, a little later.
public sealed class MixSource(IAudioSource pc, IAudioSource? mic, Func<bool>? muteMic = null) : IAudioSource
{
    public event Action<float[]>? Samples;
    public event Action<string>? Failed;
    private readonly object gate = new();
    private readonly Queue<float> fromPc = new();
    private int duck;

    /// The PC's sound waits at most this long for microphone samples to lie over (0.3 s).
    private const int MaxWait = 4800;
    /// While the PC plays a voice, the microphone also hears it from the speakers a moment later:
    /// it is turned down then (and for half a second after), so the other side is not heard twice.
    private const float Loud = 0.01f, DuckGain = 0.15f;
    private const int DuckTail = 8000;

    public void Start()
    {
        pc.Samples += OnPc;
        pc.Failed += OnFailed;
        pc.Start();
        if (mic == null) return;
        mic.Samples += OnMic;
        // A missing or broken microphone must not stop the meeting: the PC's side carries on.
        mic.Failed += m => Log.Debug($"mix: microphone left out ({m})");
        mic.Start();
    }

    private void OnFailed(string message) => Failed?.Invoke(message);

    private void OnPc(float[] chunk)
    {
        lock (gate)
        {
            if (mic == null)
            {
                Samples?.Invoke(chunk);
                return;
            }
            if (Rms(chunk) > Loud) duck = DuckTail;
            foreach (var x in chunk) fromPc.Enqueue(x);
            // No microphone samples coming (it dropped, or is reopening): let the PC's sound out alone.
            if (fromPc.Count > MaxWait)
            {
                var alone = new float[fromPc.Count - MaxWait];
                for (var i = 0; i < alone.Length; i++) alone[i] = fromPc.Dequeue();
                Samples?.Invoke(alone);
            }
        }
    }

    private void OnMic(float[] chunk)
    {
        lock (gate)
        {
            var gain = muteMic?.Invoke() == true ? 0f : 1f;
            var output = new float[chunk.Length];
            for (var i = 0; i < chunk.Length; i++)
            {
                var g = duck > 0 ? gain * DuckGain : gain;
                if (duck > 0) duck--;
                var p = fromPc.Count > 0 ? fromPc.Dequeue() : 0f;
                output[i] = Math.Clamp(chunk[i] * g + p, -1f, 1f);
            }
            Samples?.Invoke(output);
        }
    }

    private static float Rms(float[] x)
    {
        if (x.Length == 0) return 0;
        double sum = 0;
        foreach (var v in x) sum += v * v;
        return (float)Math.Sqrt(sum / x.Length);
    }

    public void Stop()
    {
        pc.Stop();
        mic?.Stop();
    }

    public void Dispose()
    {
        pc.Dispose();
        mic?.Dispose();
    }
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
