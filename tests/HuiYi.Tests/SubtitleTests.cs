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

    [Fact]
    public void APieceHasEndedOnlyWhenMoreFollowsIt()
    {
        // The recognizer ends every partial with a full stop.
        Assert.Equal(0, StableDraft.EndedUpTo("其实在某种。"));
        // People talk in runs joined by commas: a clause is a piece too.
        Assert.Equal("接着加练个胸吧，今天力量训练滑滑水，".Length, StableDraft.EndedUpTo("接着加练个胸吧，今天力量训练滑滑水，主要做有氧。"));
        Assert.Equal("One more. Two more.".Length, StableDraft.EndedUpTo("One more. Two more. Three"));
        // Too short to translate on its own: wait for more.
        Assert.Equal(0, StableDraft.EndedUpTo("嗯，当然他也。"));
    }

    [Fact]
    public void WhatTheDraftLacksIsWhatComesAfterItsWords()
    {
        // The words count, punctuation aside: what is left gets translated and added on.
        Assert.Equal("更丰富了。", StableDraft.Rest("这个地方呈现的它会", "这个地方呈现的，它会更丰富了。"));
        Assert.Equal("", StableDraft.Rest("所以他就有得有失嗯当然他也", "所以他就有得有失。嗯，当然他也。"));
        Assert.Equal("he said.", StableDraft.Rest("so,", "So, he said."));
        Assert.Null(StableDraft.Rest("", "什么"));
    }

    [Fact]
    public void AWordHeardAgainDifferentlyLinesUpAtTheNearestPieceEnd()
    {
        // 大局 became 大吉大利 inside what was kept: carry on after that piece, not from the start.
        Assert.Equal("最近训练。", StableDraft.Rest("等会儿爬坡玩两把大局，", "等会儿爬坡玩两把大吉大利，最近训练。"));
        // 他 became 它 with no piece end near: can't line up.
        Assert.Null(StableDraft.Rest("这个地方呈现的他会", "这个地方呈现的它会更丰富了。"));
    }

    [Fact]
    public void LooselyTheDraftLinesUpOnlyAtAPieceEndAboutAWordAway()
    {
        // A word or so off a piece end: carry on after it, what was read stays.
        Assert.Equal("最近训练。", StableDraft.RestLoosely("等会儿爬坡玩两把大局呀", "等会儿爬坡玩两把大吉大利，最近训练。"));
        // Issue #8: a slow AI's draft was of the line's first half heard differently, and the only piece
        // end is the line's own: it said nothing was missing, and the half translation stayed for good.
        Assert.Null(StableDraft.RestLoosely("The PVC can also entirely run in landmarkll.",
            "The PVC can also entirely run in land mode or even be fed simply via a USB stick."));
        Assert.Null(StableDraft.RestLoosely("It's the same one used on all of the other 256 by 20.",
            "It's the same one used on all of their other 256 by 256 millimeter machines."));
        Assert.Null(StableDraft.RestLoosely("The bed only goes up to 100.", "The bed only goes up to 110 degrees."));
    }

    [Fact]
    public void ALongLiveLineIsCutForGoodOnlyAtAClauseEnd()
    {
        // 我们今天，先聊工作。然后 — each piece and when it starts (s).
        var heard = new HuiYi.Speech.Recognized("我们今天，先聊工作。然后", "zh-CN",
            ["我", "们", "今", "天", "，", "先", "聊", "工", "作", "。", "然", "后"],
            [0.0f, 0.3f, 0.6f, 0.9f, 1.1f, 1.4f, 1.7f, 2.0f, 2.3f, 2.5f, 3.2f, 3.5f]);
        // The last clause end before 3.3 s: after 。, where 然 starts.
        Assert.Equal((10, 3.2), HuiYi.Speech.ListenEngine.KeepUpTo(heard, 3.3) is { } a ? (a.Chars, Math.Round(a.Seconds, 1)) : default);
        // Before 3 s only the comma qualifies.
        Assert.Equal((5, 1.4), HuiYi.Speech.ListenEngine.KeepUpTo(heard, 3.0) is { } b ? (b.Chars, Math.Round(b.Seconds, 1)) : default);
        // No clause end yet: nothing to keep.
        Assert.Null(HuiYi.Speech.ListenEngine.KeepUpTo(heard, 1.0));
        // Ending the line there: a sentence end first, only then a comma.
        Assert.Equal(10, HuiYi.Speech.ListenEngine.KeepUpTo(heard, 3.3, sentences: true)?.Chars);
        Assert.Null(HuiYi.Speech.ListenEngine.KeepUpTo(heard, 3.0, sentences: true));
    }

    [Fact]
    public void ALineBeingSaidEndsOnceItIsASentenceLong()
    {
        // As Cicada: 24 Chinese characters, or 10 English words.
        Assert.False(HuiYi.Speech.ListenEngine.LongLine("我们今天先聊工作，然后再说招聘的事情。"));
        Assert.True(HuiYi.Speech.ListenEngine.LongLine("我们今天先聊工作，然后再说招聘的事情，最后看看预算够不够。"));
        Assert.False(HuiYi.Speech.ListenEngine.LongLine("We talked about the plan for next week."));
        Assert.True(HuiYi.Speech.ListenEngine.LongLine("We talked about the plan for next week and then about the budget."));
    }
}
