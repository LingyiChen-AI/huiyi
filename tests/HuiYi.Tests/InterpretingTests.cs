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
    public void NotesCallANamedSpeakerByTheirName()
    {
        var names = new Dictionary<int, string> { [1] = "测试" };
        var notes = new MeetingNotes
        {
            Summary = "发言人1回顾年轻时不敢提问，发言人 2 补充了背景。",
            Topics = [new NoteTopic { Title = "提问", Summary = "发言人1的经历", Points = ["发言人1：年轻时不敢问"] }],
            Todos = [new NoteItem { Text = "跟进", Owner = "发言人1" }],
        };
        var shown = SpeakerLabels.WithNames(notes, names);
        Assert.Equal("测试回顾年轻时不敢提问，发言人 2 补充了背景。", shown.Summary);   // unnamed 2 stays
        Assert.Equal("测试的经历", shown.Topics[0].Summary);
        Assert.Equal("测试：年轻时不敢问", shown.Topics[0].Points[0]);
        Assert.Equal("测试", shown.Todos[0].Owner);
        Assert.Equal("发言人1回顾年轻时不敢提问，发言人 2 补充了背景。", notes.Summary); // stored notes untouched
        Assert.Equal("发言人12 说", SpeakerLabels.WithNames("发言人12 说", names));      // 12 is not 1
    }

    [Fact]
    public void ALineBeforeItsVoiceHasANumberReads发言人()
    {
        var line = new TranscriptLine { Id = "l1", Speaker = "them", Src = "我们先看一下这周的进度" };
        Assert.Equal("发言人", SpeakerLabels.Who(line, new Dictionary<string, int>(), new Dictionary<int, string>()));
    }
}
