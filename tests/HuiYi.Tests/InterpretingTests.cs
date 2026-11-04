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
    public void ALongLineIsCutWhereTheVoiceChanges()
    {
        // 10 s: one voice for the first 6 s, another after. The fake voiceprint of a window is the
        // share of it that falls in the second voice.
        const int rate = 16000, change = 6 * rate;
        var samples = new float[10 * rate];
        float[]? Embed(float[] w)
        {
            // Find the window by content: each window is a slice of `samples`, which we stamp with positions.
            var start = (int)w[0];
            var b = Enumerable.Range(start, w.Length).Count(i => i >= change) / (float)w.Length;
            return [1 - b, b];
        }
        for (var i = 0; i < samples.Length; i++) samples[i] = i;   // each sample holds its own index
        var points = TurnSplitter.Points(samples, Embed);
        Assert.Single(points);
        Assert.InRange(points[0], change - rate / 2, change + rate / 2);

        // One voice throughout: left whole.
        Assert.Empty(TurnSplitter.Points(samples, _ => [1f, 0f]));
        // Too short to bother.
        Assert.Empty(TurnSplitter.Points(new float[3 * rate], Embed));
    }

    [Fact]
    public void AReplyAfterASentenceEndIsCutOffAndGivenToItsVoice()
    {
        // 6 s: voice 0 asks for 2.5 s, voice 1 answers. The recognizer found sentence ends at 1.2 s
        // (inside the question) and 2.5 s (the turn). Samples hold their own index, so the fake
        // voiceprint knows which voice a slice is mostly in.
        const int rate = 16000, turn = (int)(2.5 * rate);
        var samples = Enumerable.Range(0, 6 * rate).Select(i => (float)i).ToArray();
        float[]? Embed(float[] w)
        {
            var start = (int)w[0];
            var b = Enumerable.Range(start, w.Length).Count(i => i >= turn) / (float)w.Length;
            return [1 - b, b];
        }
        int? Nearest(float[] v, float sim, float margin) => v[0] >= 0.8f ? 0 : v[1] >= 0.8f ? 1 : null;
        var finder = new TurnFinder(Embed, Nearest);
        Assert.Equal([turn], finder.Points(samples, [1.2, 2.5]));
        // The same sentence ends within one voice: nothing to cut.
        var one = new TurnFinder(_ => [1f, 0f], (_, _, _) => 0);
        Assert.Empty(one.Points(samples, [1.2, 2.5]));
    }

    [Fact]
    public void ALineBeforeItsVoiceHasANumberReads发言人()
    {
        var line = new TranscriptLine { Id = "l1", Speaker = "them", Src = "我们先看一下这周的进度" };
        Assert.Equal("发言人", SpeakerLabels.Who(line, new Dictionary<string, int>(), new Dictionary<int, string>()));
    }
}
