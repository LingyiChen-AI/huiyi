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

/// The microphone (default capture device, or one chosen by name).
public sealed class MicSource(string? deviceName = null) : IAudioSource
{
    public event Action<float[]>? Samples;
    public event Action<string>? Failed;
    private WasapiCapture? capture;

    public void Start()
    {
        try
        {
            var en = new MMDeviceEnumerator();
            var device = deviceName == null ? en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                : en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault(d => d.FriendlyName == deviceName)
                  ?? en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            capture = new WasapiCapture(device, true, 20);
            var format = capture.WaveFormat;
            var resampler = new Resampler(format.SampleRate);
            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded == 0) return;
                Samples?.Invoke(resampler.Process(Resampler.ToMonoFloat(e.Buffer, e.BytesRecorded, format)));
            };
            capture.StartRecording();
        }
        catch (Exception e)
        {
            Log.Debug($"mic: start failed {e.Message}");
            Failed?.Invoke("无法打开麦克风：" + e.Message);
        }
    }

    public void Stop()
    {
        try { capture?.StopRecording(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        capture?.Dispose();
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
