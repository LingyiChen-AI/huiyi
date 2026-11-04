using HuiYi;
using static HuiYi.MeetingEndWatch;

namespace HuiYi.Tests;

/// Ported from Cicada's MeetingEndTests.swift.
public class MeetingEndTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Dictionary<string, string> Zoom = new() { ["zoom.exe"] = "Zoom" };
    private static readonly Dictionary<string, string> None = new();

    private static MeetingEndWatch Watch(Silence silence = Silence.Prompt, bool followApps = true) =>
        new(new Config { Silence = silence, FollowApps = followApps }, T0);

    private static DateTime At(double s) => T0.AddSeconds(s);

    [Fact]
    public void HangUpEndsAfterTheGrace()
    {
        var w = Watch();
        Assert.Null(w.Tick(At(10), Zoom, true));
        Assert.Null(w.Tick(At(20), None, true));
        Assert.Equal(new Pending(new Reason.AppLeft("Zoom"), At(50)), w.Waiting);
        Assert.Null(w.Tick(At(49), None, true));
        Assert.Equal(new Reason.AppLeft("Zoom"), w.Tick(At(50), None, true));
    }

    [Fact]
    public void RejoiningWithinTheGraceKeepsGoing()
    {
        var w = Watch();
        w.Tick(At(10), Zoom, true);
        w.Tick(At(20), None, true);
        Assert.Null(w.Tick(At(30), Zoom, true));
        Assert.Null(w.Waiting);
    }

    [Fact]
    public void SpeechDoesNotCancelAHangUp()
    {
        var w = Watch();
        w.Tick(At(10), Zoom, true);
        w.Tick(At(20), None, true);
        w.Heard(At(25));
        Assert.Equal(new Reason.AppLeft("Zoom"), w.Tick(At(50), None, true));
    }

    [Fact]
    public void KeepGoingForgetsTheHangUp()
    {
        var w = Watch();
        w.Tick(At(10), Zoom, true);
        w.Tick(At(20), None, true);
        w.KeepGoing(At(30));
        Assert.Null(w.Tick(At(100), None, true));
        Assert.Null(w.Waiting);
    }

    [Fact]
    public void SilencePromptsThenEnds()
    {
        var w = Watch();
        w.Heard(At(5));
        Assert.Null(w.Tick(At(604), None, true));
        Assert.Null(w.Waiting);
        Assert.Null(w.Tick(At(605), None, true));
        Assert.Equal(new Pending(new Reason.SilenceReason(), At(665)), w.Waiting);
        Assert.Equal(new Reason.SilenceReason(), w.Tick(At(665), None, true));
    }

    [Fact]
    public void WordsCancelTheSilencePrompt()
    {
        var w = Watch();
        w.Tick(At(600), None, true);
        Assert.IsType<Reason.SilenceReason>(w.Waiting?.Reason);
        w.Heard(At(620));
        Assert.Null(w.Waiting);
        Assert.Null(w.Tick(At(700), None, true));
    }

    [Fact]
    public void SilenceEndModeEndsAtOnceAndOffNever()
    {
        Assert.Equal(new Reason.SilenceReason(), Watch(Silence.End).Tick(At(600), None, true));
        var off = Watch(Silence.Off);
        Assert.Null(off.Tick(At(99_999), None, true));
        Assert.Null(off.Waiting);
    }

    [Fact]
    public void SilenceDoesNotCountDuringACall()
    {
        var w = Watch();
        Assert.Null(w.Tick(At(5_000), Zoom, true));
        Assert.Null(w.Waiting);
        var plain = Watch(followApps: false);
        Assert.Null(plain.Tick(At(600), Zoom, true));
        Assert.IsType<Reason.SilenceReason>(plain.Waiting?.Reason);
    }

    [Fact]
    public void NothingSaidStopsQuietlyAfterTheSilence()
    {
        var w = Watch();
        Assert.Null(w.Tick(At(599), None, false));
        Assert.Equal(new Reason.NobodySpoke(), w.Tick(At(600), None, false));
        Assert.Null(w.Waiting);
        Assert.Equal(new Reason.NobodySpoke(), Watch(Silence.End).Tick(At(600), None, false));
        Assert.Null(Watch(Silence.Off).Tick(At(5_000), None, false));
        Assert.Null(Watch().Tick(At(5_000), Zoom, false));
    }

    [Fact]
    public void NothingSaidKeepsListening()
    {
        var w = Watch();
        w.Tick(At(10), Zoom, false);
        Assert.Null(w.Tick(At(500), None, false));
        Assert.Null(w.Waiting);
        w.Heard(At(5_001));
        Assert.Null(w.Tick(At(5_002), None, true));
        Assert.Null(w.Waiting);
    }
}
