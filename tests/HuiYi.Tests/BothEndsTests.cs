using HuiYi.Audio;
using Xunit;

namespace HuiYi.Tests;

public class BothEndsTests
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

    // ── SteadySource: the PC's channel stays on the wall clock ──

    [Fact]
    public void WhileNothingPlaysThePcChannelStillMovesOn()
    {
        var now = 0.0;
        var inner = new Fake();
        var steady = new SteadySource(inner, () => now);
        var got = new List<float>();
        steady.Samples += x => got.AddRange(x);
        steady.Start();
        inner.Send(new float[1600]);            // 0.1 s, on time
        now = 0.1;
        Assert.Equal(1600, got.Count);
        now = 2.0;                              // two seconds of nothing from loopback
        steady.Tick();
        Assert.InRange(got.Count, 2.0 * 16000 - SteadySource.Slack, 2.0 * 16000);
        Assert.All(got.Skip(1600), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void SoundAfterAPauseLandsAtItsTime()
    {
        var now = 0.0;
        var inner = new Fake();
        var steady = new SteadySource(inner, () => now);
        var got = new List<float>();
        steady.Samples += x => got.AddRange(x);
        steady.Start();
        now = 3.0;
        inner.Send(Enumerable.Repeat(0.5f, 1600).ToArray());   // a voice arrives at 3 s
        Assert.Equal(3 * 16000, got.Count);
        Assert.Equal(0.5f, got[^1]);
        Assert.Equal(0f, got[3 * 16000 - 1600 - 1]);
    }

    [Fact]
    public void ASteadySourceIsNotPaddedWhenOnTime()
    {
        var now = 0.0;
        var inner = new Fake();
        var steady = new SteadySource(inner, () => now);
        var got = new List<float>();
        steady.Samples += x => got.AddRange(x);
        steady.Start();
        for (var i = 1; i <= 50; i++)
        {
            now = i * 0.02;
            inner.Send(new float[320]);
            steady.Tick();
        }
        Assert.Equal(50 * 320, got.Count);
    }

    // ── EchoCheck: the speakers heard by the microphone, or someone in the room ──

    [Fact]
    public void TheSameWordsFromTheSpeakersAreAnEcho()
    {
        // Heard through a room the recognizer gets a word or two wrong; still the same sentence.
        Assert.True(EchoCheck.IsEcho("我想对于他来说可能到现在还是有点蒙", "这在一年前对我来说都是不可想象的。我想对于他来说，可能到现在还是有点懵。"));
        Assert.True(EchoCheck.IsEcho("the payment api is about eighty percent", "The payment API is about eighty percent done."));
    }

    [Fact]
    public void MeTalkingOverAVideoIsNotAnEcho()
    {
        Assert.False(EchoCheck.IsEcho("退款流程这周能接完吗？我们下周一就要上线了。", "我想对于他来说，可能到现在还是有点懵。所以我非常感谢他接受我的邀请。"));
        Assert.False(EchoCheck.IsEcho("Can the refund flow be done this week?", "Morning everyone. The login module passed all regression tests yesterday."));
    }

    [Fact]
    public void WithNothingPlayingItIsNeverAnEcho()
    {
        Assert.False(EchoCheck.Playing(new float[16000]));
        Assert.False(EchoCheck.IsEcho("你好", ""));
        Assert.True(EchoCheck.Playing(Enumerable.Repeat(0.1f, 16000).ToArray()));
    }

    [Fact]
    public void BothEndsHandsOverItsChannels()
    {
        var pc = new Fake();
        var mic = new Fake();
        var muted = true;
        var both = new BothEnds(pc, mic, () => muted);
        Assert.Same(pc, both.Pc);
        Assert.Same(mic, both.Mic);
        Assert.True(both.MuteMic!());
    }
}
