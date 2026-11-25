using System.Text.Json.Nodes;

namespace HuiYi.Tests;

/// Notes replies an AI wrote that do not parse, shaped like the ones DeepSeek wrote (issue #4).
public class JsonMendTests
{
    private static string Topic(int start, string title) =>
        "{\"start\":" + start + ",\"title\":\"" + title + "\",\"summary\":\"议题小结\",\"points\":[\"要点一\",\"要点二\"],\"evidence\":[\"L1\"]," +
        "\"fishbone\":{\"question\":\"为什么\",\"branches\":[{\"label\":\"原因\",\"detail\":\"细节\",\"t\":" + start + ",\"evidence\":[\"L2\",\"L3\"]}]}}";

    private const string Tail = ",\"conclusions\":[{\"text\":\"按时上线\",\"evidence\":[\"L4\"]}],\"todos\":[],\"terms\":[{\"name\":\"回归\",\"note\":\"再测一遍\",\"heard\":[]}]}";

    [Fact]
    public void ValidJsonIsLeftAlone()
    {
        var o = JsonMend.Object("{\"title\":\"周会\",\"topics\":[" + Topic(0, "上线") + "]" + Tail, out var how);
        Assert.NotNull(o);
        Assert.Null(how);
    }

    [Fact]
    public void ARepliesClosedAfterTheThirdTopicKeepsAllItsTopicsAndConclusions()
    {
        // The third topic left open, the topics and the whole reply closed with a made-up key in
        // between, then two more topics, the conclusions and the terms after it.
        var third = Topic(300, "三")[..^1];
        var broken = "{\"title\":\"周会\",\"summary\":\"总结\",\"topics\":[" + Topic(0, "一") + "," + Topic(120, "二") + "," + third +
                     "],\"topics_placeholder\":null}," + Topic(420, "四") + "," + Topic(600, "五") + "]" + Tail;
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => JsonNode.Parse(broken));
        var o = JsonMend.Object(broken, out var how);
        Assert.NotNull(o);
        Assert.Equal("reopened", how);
        var topics = o!["topics"]!.AsArray();
        Assert.Equal(["一", "二", "三", "四", "五"], topics.Select(t => t!["title"]!.GetValue<string>()));
        Assert.Equal("要点二", topics[2]!["points"]![1]!.GetValue<string>());
        Assert.Equal("按时上线", o["conclusions"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("回归", o["terms"]![0]!["name"]!.GetValue<string>());
        Assert.False(o.ContainsKey("topics_placeholder"));
    }

    [Fact]
    public void SmallSlipsGoToTheLibrary()
    {
        // A missing comma between two topics and a trailing comma.
        var broken = "{\"title\":\"周会\",\"topics\":[" + Topic(0, "一") + " " + Topic(60, "二") + ",]" + Tail;
        var o = JsonMend.Object(broken, out var how);
        Assert.Equal("repaired", how);
        Assert.Equal(2, o!["topics"]!.AsArray().Count);
        // A topic left open before the next one begins.
        var open = "{\"title\":\"周会\",\"topics\":[" + Topic(0, "一")[..^1] + "," + Topic(60, "二") + "]" + Tail;
        var t = JsonMend.Object(open, out var openHow);
        Assert.Equal("repaired", openHow);
        Assert.Equal(["一", "二"], t!["topics"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()));
        // A bracket of the wrong kind: the one it skipped is put in.
        var wrong = "{\"title\":\"周会\",\"topics\":[{\"title\":\"一\",\"points\":[\"a\",\"b\"}]" + Tail;
        var w = JsonMend.Object(wrong, out _);
        Assert.Equal("b", w!["topics"]![0]!["points"]![1]!.GetValue<string>());
    }

    [Fact]
    public void BracketsInsideStringsAreText()
    {
        var o = JsonMend.Object("{\"summary\":\"讨论了 [灰度] 和 {回滚} 方案，\\\"再议\\\"\",\"topics\":[]", out var how);
        Assert.NotNull(o);
        Assert.Equal("讨论了 [灰度] 和 {回滚} 方案，\"再议\"", o!["summary"]!.GetValue<string>());
        Assert.NotNull(how);
    }

    [Fact]
    public void WhatIsNotJsonStaysUnread()
    {
        Assert.Null(JsonMend.Object("这次会议主要讨论了上线计划。", out _));
        Assert.Null(JsonMend.Object("[1, 2, 3]", out _));
    }

    [Fact]
    public void NotesInACodeFenceAreReadMendedToo()
    {
        var third = Topic(300, "三")[..^1];
        var reply = "```json\n{\"title\":\"周会\",\"summary\":\"总结\",\"topics\":[" + Topic(0, "一") + "," + third +
                    "]}," + Topic(420, "四") + "]" + Tail + "\n```";
        var notes = NotesPrompt.Parse(reply, [0, 60, 120, 180], 0);
        Assert.NotNull(notes);
        Assert.Equal(3, notes!.Topics.Count);
        Assert.Single(notes.Conclusions!);
    }
}
