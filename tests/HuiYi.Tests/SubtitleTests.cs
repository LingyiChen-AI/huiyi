using Xunit;

namespace HuiYi.Tests;

/// The floating subtitle's streams: which finished lines stay on screen.
public class SubtitleTests
{
    private static TranscriptLine Line(string id, double t, double end, string speaker = "them") =>
        new() { Id = id, T = t, End = end, Speaker = speaker, Src = id };

    [Fact]
    public void StitchKeepsWordsThePartialDroppedFromItsStart()
    {
        // Real partials from a recording: each re-hears only the last few seconds.
        var shown = HeardText.Stitch("", "我不知道为什么发生了什么可能。");
        shown = HeardText.Stitch(shown, "为什么发生了什么？可能就是一个时间到这一步了。");
        Assert.Equal("我不知道为什么发生了什么？可能就是一个时间到这一步了。", shown);
        shown = HeardText.Stitch(shown, "到这一步了，对，量变完成了质变了。");
        Assert.Equal("我不知道为什么发生了什么？可能就是一个时间到这一步了，对，量变完成了质变了。", shown);
    }

    [Fact]
    public void StitchTakesAGrowingPartialAsItIs()
    {
        Assert.Equal("我告诉你又是一个时机。", HeardText.Stitch("我告诉你又。", "我告诉你又是一个时机。"));
        Assert.Equal("so we will ship it on Monday", HeardText.Stitch("so we will ship", "so we will ship it on Monday"));
    }

    [Fact]
    public void StitchSkipsAMisheardFirstWord()
    {
        Assert.Equal("我从一个这过去的二十多年，三十年",
            HeardText.Stitch("我从一个这过去的", "了这过去的二十多年，三十年"));
    }

    [Fact]
    public void StitchStartsOverWhenNothingOverlaps()
    {
        Assert.Equal("完全不同的一句话。", HeardText.Stitch("我告诉你又是一个时机。", "完全不同的一句话。"));
        Assert.Equal("", HeardText.Stitch("我告诉你", ""));
    }

    [Fact]
    public void StitchWorksOnEnglish()
    {
        Assert.Equal("we looked at the gateway timeout and it was the config",
            HeardText.Stitch("we looked at the gateway timeout", "the gateway timeout and it was the config"));
    }

    [Fact]
    public void KeepsUpToThreeLinesEndingWithTheLastShown()
    {
        var lines = new[] { Line("a", 0, 4), Line("b", 5, 9), Line("c", 10, 14), Line("d", 15, 19) };
        Assert.Equal(["b", "c", "d"], MeetingController.Recent(lines, "d").Select(l => l.Id));
    }

    [Fact]
    public void StartsAfreshAfterAPause()
    {
        var lines = new[] { Line("a", 0, 4), Line("b", 20, 24), Line("c", 25, 28) };
        Assert.Equal(["b", "c"], MeetingController.Recent(lines, "c").Select(l => l.Id));
    }

    [Fact]
    public void LeavesOutWhatISaidIntoTheRoom()
    {
        var lines = new[] { Line("a", 0, 4), Line("me", 5, 6, "me"), Line("c", 7, 9) };
        Assert.Equal(["a", "c"], MeetingController.Recent(lines, "c").Select(l => l.Id));
    }

    [Fact]
    public void EndsAtTheLastShownLineEvenWhenALaterOneWasInsertedAfterIt()
    {
        var lines = new[] { Line("a", 0, 4), Line("b", 5, 9), Line("c", 10, 14) };
        Assert.Equal(["a", "b"], MeetingController.Recent(lines, "b").Select(l => l.Id));
        Assert.Empty(MeetingController.Recent(lines, "gone"));
    }
}
