using System.IO;
using System.Text.Json;

namespace HuiYi.App;

/// Made-up meetings for screenshots (--snapshot <folder> --demo): a bilingual project sync with the
/// Singapore team and a few others in the list. Written into the snapshot's own temp data folder;
/// never anyone's real records.
public static class Demo
{
    private static readonly DateTime Today = DateTime.Today;

    public static void Write(string sessionsDir)
    {
        Directory.CreateDirectory(sessionsDir);
        Save(sessionsDir, Weekly());
        Save(sessionsDir, Simple("产品评审：会议助手 2.0 需求", Today.AddHours(10.5), 42,
            ["实时纪要要能按模板输出，周会和评审会的格式不一样。", "待办要带负责人和截止日期，最好能直接导出。"],
            "确认 2.0 的三个核心需求：模板化纪要、待办导出、多语言字幕；下周二前出交互稿。"));
        Save(sessionsDir, Simple("客户拜访：东京分公司", Today.AddDays(-1).AddHours(16), 35,
            ["会議の議事録を自動で作れると助かります。", "日本語と英語が混ざる会議が多いです。"],
            "客户最关心日英混合会议的识别和自动纪要；希望月底前安排一次试用。", "ja"));
        Save(sessionsDir, Simple("技术方案评审：字幕延迟优化", Today.AddDays(-2).AddHours(15), 51,
            ["首字延迟现在大概八百毫秒，目标是压到五百以内。", "断句可以先出草稿，整句识别完再替换。"],
            "确定先出草稿再整句替换的方案，首字延迟目标 500 ms；周五前出压测数据。"));
        Save(sessionsDir, Simple("1on1 · 新同学入职沟通", Today.AddDays(-6).AddHours(11), 28,
            ["第一周先熟悉代码和发布流程。", "有问题随时在群里问，不用攒着。"],
            "对齐了入职第一个月的目标和导师安排。"));
    }

    private static void Save(string dir, Session s)
    {
        var folder = Path.Combine(dir, s.Id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "session.json"), JsonSerializer.Serialize(s, Json.Options));
    }

    private static string IdOf(DateTime local) => local.ToString("yyyyMMdd-HHmmss");

    private static TranscriptLine Them(int n, double t, int voice, string en, string zh) =>
        new() { Id = $"L{n}", T = t, End = t + 9, Speaker = "them", Voice = voice, Src = en, SrcLang = "en", Dst = zh, DstLang = "zh-CN" };

    private static TranscriptLine Me(int n, double t, string zh, string en) =>
        new() { Id = $"L{n}", T = t, End = t + 7, Speaker = "me", Src = zh, SrcLang = "zh-CN", Dst = en, DstLang = "en" };

    /// The meeting the screenshots open on.
    private static Session Weekly()
    {
        var start = Today.AddHours(14);
        List<TranscriptLine> lines =
        [
            Them(1, 8, 1, "Morning everyone. Let's start with the login module — the regression tests all passed yesterday.",
                "大家早。先说登录模块，昨天回归测试全部通过了。"),
            Them(2, 260, 2, "The payment API integration is about eighty percent done. We still need to wire up the refund flow.",
                "支付接口联调完成了大约八成，退款流程还没接上。"),
            Me(3, 545, "退款流程这周能接完吗？我们下周一就要上线了。", "Can the refund flow be done this week? We go live next Monday."),
            Them(4, 790, 2, "Should be fine. And we found the timeout issue: the gateway timeout is set far too low.",
                "应该没问题。另外超时问题找到了：网关的超时阈值设得太低。"),
            Them(5, 1030, 1, "I can fix the gateway config by Friday, then we run a load test over the weekend.",
                "我周五前改好网关配置，周末跑一轮压测。"),
            Me(6, 1240, "好，上线日期就维持下周一，压测报告我来整理。", "OK, launch stays next Monday, and I'll write up the load test report."),
            Them(7, 1510, 2, "One risk though: the staging environment has been unstable all week.",
                "不过有个风险：测试环境这一周都不太稳定。"),
            Them(8, 1735, 1, "I'll ask the infra team to look at it today. Let's also sync the release notes with marketing.",
                "我今天找运维看一下。另外发布说明也要和市场部同步。"),
        ];
        var template = NoteTemplates.Find("weekly", []);
        var notes = NotesPrompt.Parse(WeeklyNotes(), lines.Select(l => l.T).ToList(), 0, template)!;
        notes.TemplateId = "weekly";
        notes.Covered = lines.Count;
        notes.UpdatedAt = start.AddMinutes(32).ToUniversalTime();
        notes.Final = true;
        return new Session
        {
            Id = IdOf(start), Title = Session.DefaultTitle(start.ToUniversalTime()),
            StartedAt = start.ToUniversalTime(), EndedAt = start.AddMinutes(32).ToUniversalTime(),
            Lines = lines, Notes = notes, TemplateId = "weekly",
            VoiceNames = new() { ["1"] = "Daniel", ["2"] = "Priya" },
        };
    }

    /// Dates in the notes are real ones, counted from today, as the AI writes them.
    private static string WeeklyNotes()
    {
        static string Day(DateTime d) => $"{d:M月d日}（{"日一二三四五六"[(int)d.DayOfWeek]}）".Replace("（", "（周");
        var friday = Today.AddDays(((int)DayOfWeek.Friday - (int)Today.DayOfWeek + 7) % 7);
        var sunday = friday.AddDays(2);
        var monday = friday.AddDays(3);
        return $$"""
            {"title": "支付项目周会", "summary": "支付接口联调完成约八成，登录模块回归通过；超时问题定位到网关配置，Daniel 周五前修复，周末压测。上线日期维持{{Day(monday)}}。",
             "topics": [
               {"start": 0, "title": "本周进展", "summary": "登录与支付两条线的进度。", "points": [{"text": "登录模块回归测试全部通过", "evidence": ["L1"]}, {"text": "支付接口联调完成约 80%，退款流程待接", "evidence": ["L2"]}]},
               {"start": 790, "title": "超时问题", "summary": "原因找到，修复后压测。", "points": [{"text": "网关超时阈值设得过低", "evidence": ["L4"]}, {"text": "修复后周末跑一轮压测", "evidence": ["L5"]}]},
               {"start": 1510, "title": "风险与发布", "summary": "测试环境不稳，发布说明待同步。", "points": [{"text": "测试环境本周持续不稳定", "evidence": ["L7"]}, {"text": "发布说明需与市场部同步", "evidence": ["L8"]}]}],
             "progress": [{"text": "登录模块回归测试全部通过", "evidence": ["L1"]}, {"text": "支付接口联调完成约 80%", "evidence": ["L2"]}],
             "next": [{"text": "接完退款流程", "evidence": ["L3"]}, {"text": "周末完成压测", "due": "{{Day(sunday)}}", "evidence": ["L5"]}],
             "risks": [{"text": "测试环境不稳定，可能拖慢压测", "owner": "Priya", "evidence": ["L7"]}],
             "conclusions": [{"text": "上线日期维持{{Day(monday)}}", "evidence": ["L6"]}],
             "todos": [{"text": "修改网关超时配置", "owner": "Daniel", "due": "{{Day(friday)}}", "evidence": ["L5"]},
                       {"text": "整理压测报告", "owner": "你", "due": "{{Day(sunday)}}", "evidence": ["L6"]},
                       {"text": "找运维排查测试环境", "owner": "Daniel", "due": "{{Today:M月d日}}（今天）", "evidence": ["L8"]},
                       {"text": "与市场部同步发布说明", "owner": "Priya", "evidence": ["L8"]}],
             "terms": [{"name": "压测", "note": "压力测试：模拟大量请求，看系统能否扛住"}]}
            """;
    }

    private static Session Simple(string title, DateTime start, int minutes, string[] said, string summary, string lang = "zh-CN")
    {
        var lines = said.Select((t, i) => new TranscriptLine
        {
            Id = $"L{i + 1}", T = 20 + i * 40, End = 30 + i * 40, Speaker = "them", Voice = 1 + i % 2, Src = t, SrcLang = lang,
        }).ToList();
        return new Session
        {
            Id = IdOf(start), Title = title, Renamed = true,
            StartedAt = start.ToUniversalTime(), EndedAt = start.AddMinutes(minutes).ToUniversalTime(),
            Lines = lines,
            Notes = new MeetingNotes { Title = title, Summary = summary, Covered = lines.Count, UpdatedAt = start.AddMinutes(minutes).ToUniversalTime(), Final = true },
        };
    }
}
