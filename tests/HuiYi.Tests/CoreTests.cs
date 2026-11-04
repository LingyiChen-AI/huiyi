using System.Text.Json;
using HuiYi;

namespace HuiYi.Tests;

public class LanguageTests
{
    [Theory]
    [InlineData("zh-Hans-CN", "zh-CN")]
    [InlineData("zh-Hant-TW", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("en-US", "en")]
    [InlineData("ja", "ja")]
    public void SystemTags(string tag, string code) => Assert.Equal(code, Languages.TranslationCode(tag));

    [Fact]
    public void FollowSystemPicksTheOtherLanguage()
    {
        string[] zhEn = ["zh-Hans-CN", "en-US"];
        Assert.Equal("zh-CN", Languages.FollowSystemTarget("en", zhEn));
        Assert.Equal("en", Languages.FollowSystemTarget("zh-CN", zhEn));
        Assert.Equal("en", Languages.FollowSystemTarget("zh-CN", ["zh-Hans-CN"]));
        Assert.Equal("zh-CN", Languages.FollowSystemTarget("en", ["en-US"]));
        Assert.Equal("en", Languages.FollowSystemTarget("yue", zhEn));
    }

    [Fact]
    public void UnsupportedSystemLanguagesAreSkipped()
    {
        // This machine's list: Chinese, then Albanian (which we don't translate into).
        string[] zhSq = ["zh-Hans-CN", "sq"];
        Assert.Equal("en", Languages.FollowSystemTarget("zh-CN", zhSq));
        Assert.Equal("zh-CN", Languages.FollowSystemTarget("en", zhSq));
        Assert.Equal("zh-CN", Languages.MyLanguage(["sq"]));
        Assert.Equal("ja", Languages.MyLanguage(["sq", "ja-JP"]));
    }

    [Theory]
    [InlineData("我们下周再看这个方案", "zh-CN")]
    [InlineData("来週もう一度見ましょう", "ja")]
    [InlineData("다음 주에 다시 봅시다", "ko")]
    [InlineData("Let's look at it next week", "en")]
    public void ScriptFallback(string text, string code) => Assert.Equal(code, Languages.FromRecognizer(null, text));

    [Fact]
    public void RecognizerTagWins() => Assert.Equal("yue", Languages.FromRecognizer("<|yue|>", "我哋下個禮拜再睇"));
}

public class NotesTests
{
    [Fact]
    public void ParseIsLenientAndChecksEvidence()
    {
        const string raw = """
        Here you go: {"title": "「发布计划」", "summary": "定了发布日期。",
          "topics": [{"start": 3, "title": "发布", "summary": "三月三日", "points": [{"text": "3月3日发布", "evidence": ["L2", "L9"]}, "设计周五签字"]}],
          "conclusions": [{"text": "锁定日期", "evidence": ["L2"]}], "todos": [], "terms": [{"name": "Atlas", "note": "产品", "heard": ["Atlus", "Atlas"]}]}
        """;
        var n = NotesPrompt.Parse(raw, [0, 4.5], 0)!;
        Assert.Equal("发布计划", n.Title);
        Assert.Single(n.Topics);
        Assert.Equal(["3月3日发布", "设计周五签字"], n.Topics[0].Points);
        Assert.Equal(4.5, n.Topics[0].PointTime(0));
        Assert.Null(n.Topics[0].PointTime(1));
        Assert.Equal(1, n.Conclusions[0].Support);
        Assert.Equal(["Atlus"], n.Terms[0].Heard);
    }

    [Fact]
    public void ParseRejectsJunk()
    {
        Assert.Null(NotesPrompt.Parse("no json here", [], 0));
        Assert.Null(NotesPrompt.Parse("{\"summary\": \"\", \"topics\": []}", [], 0));
    }

    [Fact]
    public void WrittenChecksTheScript()
    {
        var zh = new MeetingNotes { Summary = "讨论了发布 Atlas 的日期。" };
        var en = new MeetingNotes { Summary = "They discussed the launch date." };
        Assert.True(NotesPrompt.Written(zh, "Simplified Chinese"));
        Assert.False(NotesPrompt.Written(en, "Simplified Chinese"));
        Assert.True(NotesPrompt.Written(en, "English"));
        Assert.False(NotesPrompt.Written(zh, "English"));
    }

    [Fact]
    public void PromptNumbersLinesAndAsksForTheLanguage()
    {
        var lines = new List<TranscriptLine>
        {
            new() { T = 1, Speaker = "them", Src = "Hello" },
            new() { T = 65, Speaker = "me", Src = "你好" },
        };
        var p = NotesPrompt.Build(new MeetingNotes(), lines, 0, "Simplified Chinese", final: false);
        Assert.Contains("[L1 00:01 对方] Hello", p);
        Assert.Contains("[L2 01:05 你] 你好", p);
        Assert.Contains("in Simplified Chinese, even when", p);
        Assert.Contains("add at most one new topic per update", p);
        Assert.DoesNotContain("__", p);
    }
}

public class SessionFormatTests
{
    /// A session.json as Cicada (Swift, .iso8601 dates) writes it reads here and writes back the same way.
    [Fact]
    public void ReadsAndWritesCicadaSessions()
    {
        const string swift = """
        {"id":"20261101-143000","title":"会议 11月1日 14:30","startedAt":"2026-11-01T06:30:00Z","endedAt":"2026-11-01T07:00:00Z",
         "lines":[{"id":"a","t":1.5,"speaker":"them","src":"Hello","dst":"你好","srcLang":"en","dstLang":"zh-CN"}],
         "hasAudio":false,"notes":{"summary":"s","topics":[],"conclusions":[],"todos":[],"terms":[],"covered":1,"final":true,"updatedAt":"2026-11-01T07:00:01Z"}}
        """;
        var s = JsonSerializer.Deserialize<Session>(swift, Json.Options)!;
        Assert.Equal(new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc), s.StartedAt.ToUniversalTime());
        Assert.Equal("你好", s.Lines[0].Dst);
        Assert.True(s.Notes!.Final);
        var back = JsonSerializer.Serialize(s, Json.Options);
        Assert.Contains("\"startedAt\":\"2026-11-01T06:30:00Z\"", back);
        Assert.Contains("\"srcLang\":\"en\"", back);
    }

    [Fact]
    public void StoreDropsEmptyMeetingsAndKeepsSpokenOnes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "huiyi-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SessionStore(dir);
            var empty = store.Begin();
            store.End();
            Assert.Null(store.Get(empty.Id));
            var s = store.Begin();
            store.Mutate(s.Id, x => x.Lines.Add(new TranscriptLine { Id = "1", Src = "hi" }));
            store.End();
            var reloaded = new SessionStore(dir);
            Assert.Equal("hi", reloaded.Get(s.Id)!.Lines[0].Src);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(5, "00:05")]
    [InlineData(65, "01:05")]
    [InlineData(3725, "1:02:05")]
    public void Clock(double t, string text) => Assert.Equal(text, TranscriptExport.Clock(t));
}

public class SpeakSafetyTests
{
    [Theory]
    [InlineData("我们周四上线", "We go live on Thursday", "en", true)]
    [InlineData("我们周四上线", "我们周四上线", "en", false)]      // untranslated
    [InlineData("我们周四上线", "我们周四上线吧", "en", false)]    // still Chinese
    [InlineData("We go live Thursday", "我们周四上线", "zh-CN", true)]
    [InlineData("We go live Thursday", "We go live", "ja", false)]
    public void NeverSpeaksUntranslatedText(string src, string dst, string target, bool ok) =>
        Assert.Equal(ok, TranslationService.LooksTranslated(src, dst, target));
}
