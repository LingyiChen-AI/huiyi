using HuiYi.Speech;
using NAudio.Wave;
using Xunit;

namespace HuiYi.Tests;

/// The after-meeting check reads the recording a stretch at a time instead of loading it whole.
public sealed class RecordingReaderTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"huiyi-rec-{Guid.NewGuid():N}.wav");

    public void Dispose()
    {
        try { File.Delete(path); } catch { }
    }

    /// Three seconds where each sample holds its own second (0.1, 0.2, 0.3), as the meeting writes it.
    private void Write()
    {
        using var w = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(Recognizer.SampleRate, 16, 1));
        for (var s = 0; s < 3; s++)
        {
            var v = (short)((s + 1) * 0.1 * 32767);
            var bytes = new byte[Recognizer.SampleRate * 2];
            for (var i = 0; i < Recognizer.SampleRate; i++) { bytes[2 * i] = (byte)v; bytes[2 * i + 1] = (byte)(v >> 8); }
            w.Write(bytes, 0, bytes.Length);
        }
    }

    [Fact]
    public void ReadsTheStretchAsked()
    {
        Write();
        using var r = new RecordingReader(path);
        Assert.Equal(3, r.Seconds, 3);
        var mid = r.Slice(1.5, 2.5);
        Assert.Equal(Recognizer.SampleRate, mid.Length);
        Assert.Equal(0.2, mid[0], 2);
        Assert.Equal(0.3, mid[^1], 2);
        // Out of order reads are fine: each seeks.
        Assert.Equal(0.1, r.Slice(0, 0.5)[0], 2);
    }

    [Fact]
    public void ClipsToTheRecording()
    {
        Write();
        using var r = new RecordingReader(path);
        Assert.Equal(Recognizer.SampleRate / 2, r.Slice(2.5, 10).Length);
        Assert.Empty(r.Slice(5, 6));
        Assert.Empty(r.Slice(2, 1));
    }

    [Fact]
    public void SlicesMatchTheWholeFileRead()
    {
        Write();
        float[] whole;
        using (var reader = new NAudio.Wave.WaveFileReader(path))
        {
            var p = reader.ToSampleProvider();
            whole = new float[reader.SampleCount];
            p.Read(whole, 0, whole.Length);
        }
        using var r = new RecordingReader(path);
        Assert.Equal(whole[8000..40000], r.Slice(0.5, 2.5));
    }
}
