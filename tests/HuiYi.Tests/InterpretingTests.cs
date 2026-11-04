using HuiYi;
using HuiYi.Speak;

namespace HuiYi.Tests;

public class InterpretingTests
{
    [Theory]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)", true)]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", true)]
    [InlineData("麦克风 (Realtek(R) Audio)", false)]
    [InlineData("Cable Modem Headset", false)]
    public void TellsTheVirtualCableFromRealMics(string name, bool cable) =>
        Assert.Equal(cable, VirtualCable.IsCable(name));

    [Fact]
    public void ALiveMeetingIsMeasuredInUtc()
    {
        // Stored in UTC; measured against local time it read as 8 hours long in China.
        var s = new Session { StartedAt = DateTime.UtcNow.AddMinutes(-3) };
        Assert.InRange(s.Duration.TotalMinutes, 2.9, 3.1);
        s.EndedAt = s.StartedAt.AddMinutes(45);
        Assert.Equal(45, s.Duration.TotalMinutes, 3);
    }

    [Fact]
    public void ALineBeforeItsVoiceHasANumberReads发言人()
    {
        var line = new TranscriptLine { Id = "l1", Speaker = "them", Src = "我们先看一下这周的进度" };
        Assert.Equal("发言人", SpeakerLabels.Who(line, new Dictionary<string, int>(), new Dictionary<int, string>()));
    }
}
