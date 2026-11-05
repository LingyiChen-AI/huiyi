using HuiYi.Audio;
using Xunit;

namespace HuiYi.Tests;

public class MixSourceTests
{
    private sealed class Fake : IAudioSource
    {
        public event Action<float[]>? Samples;
        public event Action<string>? Failed;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void Send(float[] x) => Samples?.Invoke(x);
        public void Fail(string m) => Failed?.Invoke(m);
    }

    private static (MixSource Mix, Fake Pc, Fake Mic, List<float> Out) Make(Func<bool>? mute = null)
    {
        var pc = new Fake();
        var mic = new Fake();
        var mix = new MixSource(pc, mic, mute);
        var output = new List<float>();
        mix.Samples += x => output.AddRange(x);
        mix.Start();
        return (mix, pc, mic, output);
    }

    private static float[] Fill(int n, float v) => Enumerable.Repeat(v, n).ToArray();

    [Fact]
    public void MyVoiceAndTheOtherSideBothLand()
    {
        var (_, pc, mic, output) = Make();
        // A quiet far end (below the duck level) and my voice at the same moment: added together.
        pc.Send(Fill(160, 0.005f));
        mic.Send(Fill(160, 0.2f));
        Assert.Equal(160, output.Count);
        Assert.All(output, x => Assert.Equal(0.205f, x, 4));
    }

    [Fact]
    public void WithNoMicrophoneThePcPassesStraightThrough()
    {
        var pc = new Fake();
        var mix = new MixSource(pc, null);
        var output = new List<float>();
        mix.Samples += x => output.AddRange(x);
        mix.Start();
        pc.Send(Fill(100, 0.3f));
        Assert.Equal(100, output.Count);
    }

    [Fact]
    public void WhenTheMicrophoneStallsThePcSoundIsNotHeldBack()
    {
        var (_, pc, _, output) = Make();
        pc.Send(Fill(4000, 0.1f));
        Assert.Empty(output);
        pc.Send(Fill(4000, 0.1f));
        // Only 0.3 s waits for the microphone; the rest comes out alone.
        Assert.Equal(8000 - 4800, output.Count);
    }

    [Fact]
    public void TheMicrophoneIsTurnedDownWhileTheOtherSideTalks()
    {
        var (_, pc, mic, output) = Make();
        pc.Send(Fill(160, 0.3f));
        mic.Send(Fill(160, 0.2f));
        // The far voice, plus only a trace of its echo off the speakers.
        Assert.All(output, x => Assert.Equal(0.3f + 0.2f * 0.15f, x, 4));
        output.Clear();
        // Long after they stop, the microphone is back at full level.
        mic.Send(Fill(16000, 0.2f));
        Assert.Equal(0.2f, output[^1], 4);
    }

    [Fact]
    public void WhileIHoldTheInterpretingKeyTheMicrophoneIsMuted()
    {
        var holding = true;
        var (_, _, mic, output) = Make(() => holding);
        mic.Send(Fill(160, 0.2f));
        Assert.All(output, x => Assert.Equal(0f, x));
        holding = false;
        output.Clear();
        mic.Send(Fill(160, 0.2f));
        Assert.All(output, x => Assert.Equal(0.2f, x, 4));
    }

    [Fact]
    public void ABrokenMicrophoneDoesNotEndTheMeeting()
    {
        var (mix, _, mic, _) = Make();
        var failed = false;
        mix.Failed += _ => failed = true;
        mic.Fail("unplugged");
        Assert.False(failed);
    }
}
