using HuiYi;

namespace HuiYi.Tests;

public class TemplateTests
{
    private static readonly DateTime Start = new(2026, 11, 4, 14, 0, 0, DateTimeKind.Local);   // a Wednesday

    private static List<TranscriptLine> Lines() =>
    [
        new() { Id = "1", T = 0, Speaker = "them", Src = "我们看一下这周进度" },
        new() { Id = "2", T = 65, Speaker = "them", Src = "登录模块周五前修完" },
        new() { Id = "3", T = 300, Speaker = "me", Src = "好，我来跟进" },
    ];

    [Fact]
    public void BuiltInTemplatesAreWellFormed()
    {
        Assert.True(NoteTemplates.BuiltIn.Length >= 15);
        Assert.Equal(NoteTemplates.BuiltIn.Length, NoteTemplates.BuiltIn.Select(t => t.Id).Distinct().Count());
        foreach (var t in NoteTemplates.BuiltIn)
        {
            Assert.True(t.BuiltIn, t.Id);
            Assert.NotEmpty(t.Sections);
            Assert.Equal(t.Sections.Count, t.Sections.Select(s => s.Key).Distinct().Count());
            Assert.All(t.Sections, s => Assert.Matches("^[a-z_0-9]+$", s.Key));
        }
        Assert.Equal("general", NoteTemplates.Find("nope", []).Id);
    }

    [Fact]
    public void EveryTemplateBuildsAPromptWithItsPartsAndTheRealDate()
    {
        foreach (var t in NoteTemplates.BuiltIn)
        {
            var p = NotesPrompt.Build(new MeetingNotes(), Lines(), 0, "Simplified Chinese", final: true, template: t, meetingStart: Start);
            Assert.Contains("2026年11月4日（周三）14:00", p);
            foreach (var s in t.Sections) Assert.Contains($"\"{s.Key}\": [", p);
            Assert.Equal(t.Topics, p.Contains("\"topics\""));
            Assert.Equal(t.Fishbone && t.Topics, p.Contains("\"fishbone\""));
            if (t.Sections.Any(s => s.Due)) Assert.Contains("\"due\"", p);
            Assert.Contains("never leave a relative word alone", p, StringComparison.OrdinalIgnoreCase);
            if (t.Focus.Length > 0) Assert.Contains("- Focus: " + t.Focus, p);
            Assert.DoesNotContain("__", p);
        }
    }

    [Fact]
    public void ParsesATemplatesPartsWithOwnersAndDeadlines()
    {
        var weekly = NoteTemplates.Find("weekly", []);
        const string raw = """
        {"title": "项目周会", "summary": "回顾了进度。",
         "topics": [{"start": 0, "title": "进度", "summary": "", "points": [{"text": "登录模块", "evidence": ["L2"]}]}],
         "progress": [{"text": "接口联调完成", "evidence": ["L1"]}],
         "next": ["修复登录模块"],
         "risks": [{"text": "测试环境不稳定", "owner": "发言人1", "evidence": ["L2"]}],
         "conclusions": [],
         "todos": [{"text": "修完登录模块", "owner": "你", "due": "11月6日（周五）", "evidence": ["L2", "L3"]}, {"text": "整理报告", "owner": null, "due": null}]}
        """;
        var n = NotesPrompt.Parse(raw, [0, 65, 300], 0, weekly)!;
        Assert.Equal("weekly", n.TemplateId);
        Assert.Equal(weekly.Sections.Select(s => s.Key), n.Sections!.Select(s => s.Key));
        Assert.Equal("修复登录模块", n.Sections!.First(s => s.Key == "next").Items[0].Text);   // a plain string still counts
        Assert.Equal("发言人1", n.Sections!.First(s => s.Key == "risks").Items[0].Owner);
        Assert.Equal("warn", n.Sections!.First(s => s.Key == "risks").Style);
        Assert.Equal(2, n.Todos.Count);
        Assert.Equal("11月6日（周五）", n.Todos[0].Due);
        Assert.Equal(65, n.Todos[0].T);
        Assert.Null(n.Todos[1].Due);
        Assert.Null(n.Todos[1].Owner);
        Assert.Empty(n.Conclusions);
    }

    [Fact]
    public void NotesWithoutPartsStillShowDecisionsAndToDos()
    {
        var old = new MeetingNotes { Summary = "s", Todos = [new NoteItem { Text = "x" }] };
        Assert.Equal(["conclusions", "todos"], old.Parts().Select(p => p.Key));
        Assert.Single(old.Parts()[1].Items);
    }

    [Fact]
    public void TimesAreRealClockTimes()
    {
        Assert.Equal("14:05", NotesPrompt.ClockAt(Start, 300));
        Assert.Equal("15:01", NotesPrompt.ClockAt(Start, 3665));
        Assert.Equal("2026年11月4日（周三）14:00", NotesPrompt.When(Start));
    }

    [Fact]
    public void MarkdownUsesTheTemplateRealTimesAndADeadlineTable()
    {
        var weekly = NoteTemplates.Find("weekly", []);
        var notes = NotesPrompt.Parse("""
            {"title": "周会", "summary": "回顾进度。", "topics": [{"start": 300, "title": "进度", "points": [{"text": "登录模块", "evidence": ["L2"]}]}],
             "risks": [{"text": "环境不稳", "owner": "发言人1"}], "todos": [{"text": "修完登录", "owner": "你", "due": "11月6日（周五）"}]}
            """, [0, 65, 300], 0, weekly)!;
        var s = new Session { Id = "s", Title = "会议 11月4日 14:00", StartedAt = Start.ToUniversalTime(), EndedAt = Start.AddMinutes(30).ToUniversalTime(), Lines = Lines(), Notes = notes };
        var md = TranscriptExport.Markdown(s);
        Assert.Contains("2026年11月4日（周三）14:00 – 14:30", md);
        Assert.Contains("**14:05 进度**", md);
        Assert.Contains("- 登录模块（14:01）", md);
        Assert.Contains("### 风险与问题", md);
        Assert.Contains("| 修完登录 | 你 | 11月6日（周五） |", md);
        Assert.Contains("**14:01 发言人** 登录模块周五前修完", md);
    }

    [Fact]
    public void MyTemplatesAreCopiesAndKeepTheirParts()
    {
        var copy = NoteTemplates.Find("retro", []).CopyAsMine("我的复盘");
        Assert.False(copy.BuiltIn);
        Assert.StartsWith("my-", copy.Id);
        copy.Sections[0].Title = "改过的";
        Assert.NotEqual("改过的", NoteTemplates.Find("retro", []).Sections[0].Title);   // the built-in is untouched
        Assert.Equal(copy.Id, NoteTemplates.Find(copy.Id, [copy]).Id);
    }
}
