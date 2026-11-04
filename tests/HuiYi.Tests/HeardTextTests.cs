using HuiYi;

namespace HuiYi.Tests;

/// Ported from Cicada's HeardTextTests.swift (lines from a recorded Bloomberg interview).
public class HeardTextTests
{
    [Theory]
    [InlineData("in..........", "in…")]
    [InlineData(", , that", "that")]
    [InlineData(",...... has translated at its end into infrastructure...", "has translated at its end into infrastructure…")]
    [InlineData(". , that is causing a product twice.", "that is causing a product twice.")]
    [InlineData("that.......... in increased production", "that… in increased production")]
    [InlineData("would.. would be oversupplied.", "would… would be oversupplied.")]
    [InlineData(", and......... in alternative infrastructure routes that let's.................", "and… in alternative infrastructure routes that let's…")]
    [InlineData("whether it's in the UALE, , Saudi Arab, or the wider.", "whether it's in the UALE, Saudi Arab, or the wider.")]
    [InlineData(",, and there should be more diversified.", "and there should be more diversified.")]
    [InlineData(", you... lucrative", "you… lucrative")]
    [InlineData("of people..", "of people…")]
    [InlineData("to over 1.4 million a day.", "to over 1.4 million a day.")]
    [InlineData("The U.S. said no.", "The U.S. said no.")]
    [InlineData("，，我们周四上线。。。", "我们周四上线…")]
    [InlineData("........", "")]
    public void CleanStrayMarks(string input, string expected) => Assert.Equal(expected, HeardText.Clean(input));

    [Fact]
    public void JoinOnlyWhatCarriesOn()
    {
        Assert.True(HeardText.Continues("that… in increased production that you would have expected at this",
            "because I think as soon as this crisis is over,", 0.2, true));
        Assert.True(HeardText.Continues("in, diesel and gasoline crisis in Russia that is combined with the Middle",
            "that is causing a product twice.", 0.1, false));
        Assert.True(HeardText.Continues("So, I think the long term headwinds for global energy markets are going to be.",
            "in the, and in the getting the tanker market back", 0.3, true));
        Assert.False(HeardText.Continues("It's not just rhetoric.", "it costs a lot more to transport.", 0.2, false));
        Assert.False(HeardText.Continues("are going to be.", "So the tanker market", 0.2, true));
        Assert.False(HeardText.Continues("Is it the headlines that are moving markets?", "or is it more", 0.1, true));
        Assert.False(HeardText.Continues("at this", "because I think", 2.4, true));
        Assert.False(HeardText.Continues(string.Concat(Enumerable.Repeat("word ", 40)), "and more", 0.1, true));
        Assert.True(HeardText.Continues(string.Concat(Enumerable.Repeat("word ", 39)), "and five or six more words", 0.1, true));
        Assert.True(HeardText.Continues("我们下周", "再看这个方案", 0.3, true));
        Assert.False(HeardText.Continues("我们下周再看。", "这个方案", 0.3, true));
    }

    [Theory]
    [InlineData("are going to be.", "in the tanker market", "are going to be in the tanker market")]
    [InlineData("at this", "because I think", "at this because I think")]
    [InlineData("between…", "loading a ship.", "between… loading a ship.")]
    [InlineData("我们下周", "再看", "我们下周再看")]
    public void JoinedText(string a, string b, string expected) => Assert.Equal(expected, HeardText.Join(a, b));
}
