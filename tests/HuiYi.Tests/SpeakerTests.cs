using HuiYi;

namespace HuiYi.Tests;

/// Ported from Cicada's SpeakerTests.swift (the clustering, labelling and name-guess rules).
public class SpeakerTests
{
    private static float[] Unit(int i, float noise = 0)
    {
        var v = Enumerable.Repeat(noise, 256).ToArray();
        v[i] = 1;
        var n = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / n).ToArray();
    }

    [Fact]
    public void ClustererJoinsStartsAndAbstains()
    {
        var c = new VoiceClusterer();
        Assert.Equal(0, c.Label(Unit(0), 3));
        Assert.Equal(0, c.Label(Unit(0, 0.02f), 1));   // similar: joins
        Assert.Null(c.Label(Unit(1), 1));              // new voice, too short to start one
        Assert.Equal(1, c.Label(Unit(1), 2.5));        // long enough: a new voice
        Assert.Equal(2, c.Sums.Count);
    }

    /// A voice like `main` (cosine ≈ `sim` with it), as a unit vector.
    private static float[] Near(int main, int other, float sim)
    {
        var v = new float[256];
        v[main] = sim;
        v[other] = MathF.Sqrt(1 - sim * sim);
        return v;
    }

    [Fact]
    public void StrayVoiceIsFoldedIntoTheMainVoiceItSoundsLike()
    {
        // Exact vectors can't show what averaging does to real voiceprints (a stray's centre ends up
        // nearer the host than its lines were), so fold here at 0.4.
        var c = new VoiceClusterer(mergeAt: 0.4f);
        for (var i = 0; i < 4; i++) c.Label(Unit(0), 3);                 // the host: voice 0
        Assert.Equal(1, c.Label(Near(0, 5, 0.45f), 3));                   // a line with music under it: voice 1
        Assert.Equal(1, c.Label(Near(0, 5, 0.45f), 3));
        Assert.Empty(c.FoldStrays());                                     // still fresh: could be a newcomer
        for (var i = 0; i < 4; i++) c.Label(Unit(2), 3);                  // the guest: voice 2
        for (var i = 0; i < 6; i++) c.Label(Unit(0), 3);
        // Ten lines without voice 1: folded into the host it resembles (0.45), not the guest (0).
        Assert.Equal([(1, 0)], c.FoldStrays());
        Assert.Empty(c.FoldStrays());
        Assert.Equal(2, c.Label(Unit(2), 3));                             // the guest is untouched
    }

    [Fact]
    public void MainVoicesAndUnlikeStraysAreNeverMerged()
    {
        var c = new VoiceClusterer(mergeAt: 0.4f);
        // Two people whose centres are alike (0.5, above the fold level): both talk a lot, so they stay apart.
        for (var i = 0; i < 5; i++) c.Label(Unit(0), 3);
        for (var i = 0; i < 5; i++) c.Label(Near(0, 1, 0.5f), 3);
        Assert.Equal(2, c.Sums.Count);
        // A stray voice unlike anyone (0.3, below it) stays its own.
        c.Label(Near(0, 7, 0.3f), 3);
        for (var i = 0; i < 12; i++) c.Label(Unit(0), 3);
        Assert.Empty(c.FoldStrays());
    }

    [Fact]
    public void MergedVoiceIsNeverClosestAgain()
    {
        var c = new VoiceClusterer();
        c.Label(Unit(0), 3);
        c.Label(Unit(1), 3);
        c.Merge(1, 0);
        Assert.Equal(0, c.Label(Unit(1), 1));
    }

    private static TranscriptLine Line(int id, int? voice, bool me = false, string src = "a full line") => new()
    {
        Id = $"l{id}", T = id, Speaker = me ? "me" : "them", Src = src, SrcLang = "en", DstLang = "zh-CN", Voice = voice,
    };

    [Fact]
    public void ASingleVoiceIsNumberedAfterTwoLines()
    {
        // A video with one presenter: 发言人 1 from the second line on, its first line too.
        var lines = Enumerable.Range(0, 3).Select(i => Line(i, 0)).Concat([Line(3, null, me: true)]).ToList();
        Assert.Empty(SpeakerLabels.Numbers(lines.Take(1).ToList()));
        var n = SpeakerLabels.Numbers(lines);
        Assert.Equal(1, n["l0"]);
        Assert.Equal(1, n["l2"]);
        Assert.False(n.ContainsKey("l3"));
        Assert.Equal(1, SpeakerLabels.ShownCount(lines));
    }

    [Fact]
    public void NumbersFollowWhoReachedTwoLinesFirst()
    {
        var lines = Enumerable.Range(0, 2).Select(i => Line(i, 9))
            .Concat(Enumerable.Range(2, 2).Select(i => Line(i, 4)))
            .Concat([Line(4, 2), Line(5, null)]).ToList();
        var n = SpeakerLabels.Numbers(lines);
        Assert.Equal(1, n["l0"]);
        Assert.Equal(2, n["l2"]);
        Assert.False(n.ContainsKey("l4"));   // one line: not yet
        Assert.False(n.ContainsKey("l5"));   // no voice: 对方
        // A voice that spoke first but reached two lines later doesn't take an earlier number.
        var late = new[] { Line(0, 3) }.Concat(Enumerable.Range(1, 2).Select(i => Line(i, 9)))
            .Concat(Enumerable.Range(3, 2).Select(i => Line(i, 4))).Concat([Line(5, 3)]).ToList();
        var m = SpeakerLabels.Numbers(late);
        Assert.Equal(1, m["l1"]);
        Assert.Equal(2, m["l3"]);
        Assert.Equal(3, m["l0"]);
        // Short noises ("Yeah.") never make a voice count.
        Assert.Empty(SpeakerLabels.Numbers([Line(0, 5, src: "Yeah."), Line(1, 5, src: "Okay.")]));
    }

    [Theory]
    [InlineData("Yeah.", false)]
    [InlineData("Okay, sounds good", true)]
    [InlineData("好的好", false)]
    [InlineData("我们周四上线", true)]
    public void OnlySubstantiveLinesCount(string text, bool ok) => Assert.Equal(ok, SpeakerLabels.Substantive(text));

    private static Session Named(Dictionary<string, string>? voiceNames)
    {
        var lines = Enumerable.Range(0, 5).Select(i => Line(i, 9)).Concat(Enumerable.Range(5, 5).Select(i => Line(i, 4))).ToList();
        lines.Add(Line(10, null, me: true));
        return new Session { Id = "s", Title = "t", StartedAt = DateTime.UtcNow, Lines = lines, VoiceNames = voiceNames };
    }

    [Fact]
    public void NamesFollowVoicesNotNumbers()
    {
        var s = Named(new() { ["4"] = "George", ["7"] = "Nobody" });
        var names = SpeakerLabels.Names(s);
        Assert.Equal(new Dictionary<int, string> { [2] = "George" }, names);
        Assert.Equal("George", SpeakerLabels.Label(2, names));
        Assert.Equal("发言人 1", SpeakerLabels.Label(1, names));
        Assert.Equal("George", SpeakerLabels.Who(s.Lines[5], SpeakerLabels.Numbers(s.Lines), names));
        Assert.Equal("我", SpeakerLabels.Who(s.Lines[10], SpeakerLabels.Numbers(s.Lines), names));
        Assert.Empty(SpeakerLabels.Names(Named(null)));
        // An empty name turns down Ling's guess.
        var turned = Named(new() { ["4"] = "" });
        turned.VoiceGuesses = new() { ["4"] = new VoiceGuess("Guess", "00:05 “x”") };
        Assert.Empty(SpeakerLabels.Names(turned));
    }

    [Fact]
    public void NameGuessPromptAndParse()
    {
        var s = Named(null);
        var content = SpeakerNameGuess.Content(s) ?? "";
        Assert.Contains("[L1 00:00 发言人1] a full line", content);
        Assert.Contains("[L6 00:05 发言人2] a full line", content);
        Assert.Contains("[L11 00:10 你] a full line", content);
        const string reply = """{"speakers": [{"speaker": 2, "name": " George ", "evidence": "L6"}, {"speaker": 7, "name": "X"}, {"speaker": 1, "name": ""}]}""";
        var g = SpeakerNameGuess.Parse(reply, s);
        Assert.Single(g);
        Assert.Equal(new VoiceGuess("George", "00:05 “a full line”"), g[4]);
        // One name for two voices: the one with more lines keeps it.
        var more = Named(null);
        more.Lines.AddRange(Enumerable.Range(11, 3).Select(i => Line(i, 4)));
        Assert.Equal([4], SpeakerNameGuess.Parse("""{"speakers": [{"speaker": 1, "name": "Ann", "evidence": "L1"}, {"speaker": 2, "name": "Ann", "evidence": "L6"}]}""", more).Keys.Order().ToArray());
        Assert.Empty(SpeakerNameGuess.Parse("not json", s));
        // One voice on show is worth naming too ("我是小凡…" in a video); no voice on show is not.
        Assert.Contains("发言人1", SpeakerNameGuess.Content(new Session { Lines = Enumerable.Range(0, 6).Select(i => Line(i, 1)).ToList() }));
        Assert.Null(SpeakerNameGuess.Content(new Session { Lines = [Line(0, 1), Line(1, null)] }));
    }

    /// The end of a recorded Bloomberg interview: voice 0 is the guest, voice 1 the anchor, then an
    /// advert break with other voices.
    private static Session Bloomberg()
    {
        (int? V, string T)[] rows =
        [
            (0, "We have to invest in alternative routes to the Strait of Hormuz."), (0, "That is something that financial markets are not used to doing."),
            (1, "Let's talk about financing a little bit."), (1, "I was watching you speak yesterday as well in an interview."),
            (1, "It is still important, and you see the need to still come here."), (1, "lucrative investment opportunities, whether it's in the UAE."),
            (0, "Yes, I think that what we're going to see is a big investment in infrastructure."), (0, "We need to invest in these infrastructure routes to harden them."),
            (0, "And I think the question will be, how do we invest in them?"), (0, "We saw that in the '70s."), (0, "I think we're going to see it again."),
            (1, "Okay, Amos, thank you so much for your time this morning, Amos Hockstein, managing partner."),
            (null, ".."), (1, "And now, coming up, we will be discussing the outlook for oil markets."), (1, "That is next live from Adipec here in Abu Dhabi."),
            (null, "This is Bloomberg."), (2, "It's a multi trillion dollar industry."), (3, "Welcome to Bloomer this weekend."),
            (null, "I'm David Brewer."), (null, "I'm Lisa Mattay."), (null, "I'm Christina Raffini."),
            (4, "We're tracking breaking news today from Wall Street to Washington."),
        ];
        return new Session { Id = "s", Lines = rows.Select((r, i) => Line(i, r.V, src: r.T)).ToList() };
    }

    [Fact]
    public void ANameNeedsItsSpeakerNearTheEvidence()
    {
        var s = Bloomberg();
        Assert.Equal(new Dictionary<int, int> { [0] = 1, [1] = 2 }, SpeakerLabels.VoiceNumbers(s.Lines));
        const string reply = """{"speakers": [{"speaker": 1, "name": "Amos Hochstein", "evidence": "L12"}, {"speaker": 2, "name": "Christina Raffini", "evidence": "L21"}]}""";
        var (found, refuted) = SpeakerNameGuess.Read(reply, s);
        Assert.Equal([0], found.Keys.ToArray());
        Assert.Equal("Amos Hochstein", found[0].Name);
        Assert.StartsWith("00:11 “Okay, Amos", found[0].Evidence);
        Assert.Equal(new Dictionary<int, string> { [1] = "Christina Raffini" }, refuted);
        Assert.Equal("Anchor", SpeakerNameGuess.Parse("""{"speakers": [{"speaker": 2, "name": "Anchor", "evidence": "L12, L14"}]}""", s)[1].Name);
        Assert.Empty(SpeakerNameGuess.Parse("""{"speakers": [{"speaker": 2, "name": "Anchor"}]}""", s));
        Assert.Empty(SpeakerNameGuess.Parse("""{"speakers": [{"speaker": 2, "name": "Anchor", "evidence": "L99"}]}""", s));
        Assert.True(SpeakerNameGuess.Parse("""{"speakers": [{"speaker": 1, "name": "Amos", "evidence": "L13"}]}""", s).ContainsKey(0));
        Assert.Empty(SpeakerNameGuess.Parse("""{"speakers": [{"speaker": 1, "name": "Amos", "evidence": "L14"}]}""", s));
    }

    [Fact]
    public void NotesPromptUsesSpeakerLabels()
    {
        var s = Named(new() { ["4"] = "George" });
        var p = NotesPrompt.Build(new MeetingNotes(), s.Lines, 0, "Simplified Chinese", final: true, SpeakerLabels.Names(s));
        Assert.Contains("[L1 00:00 发言人1] a full line", p);
        Assert.Contains("[L6 00:05 George] a full line", p);
        Assert.Contains("[L11 00:10 你]", p);
        Assert.Contains("a name like \\\"George\\\"".Replace("\\\"", "\""), p);
        Assert.DoesNotContain("__", p);
    }
}
