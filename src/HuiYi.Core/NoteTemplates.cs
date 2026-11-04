using System.Text.Json;

namespace HuiYi;

/// One part of the notes a template asks for: a heading, what the AI should put there, and whether
/// each item carries an owner and a due date (to-dos) . `Key` is the JSON key the AI fills in;
/// "conclusions" and "todos" map onto the notes' own decision and to-do lists.
public sealed class TemplateSection
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Instruction { get; set; } = "";
    public bool Owner { get; set; }
    public bool Due { get; set; }
    /// How the items look: "check" (settled), "todo" (to do), "dot" (plain), "warn" (risks, open questions).
    public string Style { get; set; } = "dot";

    public TemplateSection Clone() => (TemplateSection)MemberwiseClone();
}

/// What a meeting's notes look like: which parts, what to focus on. Built-in ones are read-only;
/// the user's own live in %APPDATA%\HuiYi\templates.json.
public sealed class NoteTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "我的模板";
    public string Description { get; set; } = "";
    /// What to pay most attention to, said to the AI before the rules.
    public string Focus { get; set; } = "";
    public bool Topics { get; set; } = true;
    public bool Fishbone { get; set; }
    public bool Terms { get; set; } = true;
    public List<TemplateSection> Sections { get; set; } = [];
    public bool BuiltIn { get; set; }

    public NoteTemplate CopyAsMine(string name) => new()
    {
        Id = "my-" + Guid.NewGuid().ToString("N")[..8],
        Name = name, Category = "我的模板", Description = Description, Focus = Focus,
        Topics = Topics, Fishbone = Fishbone, Terms = Terms, Sections = Sections.Select(s => s.Clone()).ToList(),
    };
}

public static class NoteTemplates
{
    public const string DefaultId = "general";

    private static TemplateSection Decisions(string title = "决策与结论") => new()
    {
        Key = "conclusions", Title = title, Style = "check",
        Instruction = "会上明确定下来、大家达成一致的事项，每条一句；没有明确定下的不要写",
    };

    private static TemplateSection Todos(string title = "待办事项") => new()
    {
        Key = "todos", Title = title, Style = "todo", Owner = true, Due = true,
        Instruction = "有人认领的具体行动，每条以动词开头；负责人写实际去做的人，截止时间写成具体日期",
    };

    private static TemplateSection S(string key, string title, string instruction, string style = "dot", bool owner = false, bool due = false) =>
        new() { Key = key, Title = title, Instruction = instruction, Style = style, Owner = owner, Due = due };

    private static NoteTemplate T(string id, string name, string category, string description, string focus,
        bool topics, bool fishbone, params TemplateSection[] sections) => new()
    {
        Id = id, Name = name, Category = category, Description = description, Focus = focus,
        Topics = topics, Fishbone = fishbone, Sections = [.. sections], BuiltIn = true,
    };

    public static readonly NoteTemplate[] BuiltIn =
    [
        T("general", "通用会议", "通用", "适合大多数会议：概要、议题时间线、决策、待办。",
            "抓住讨论了什么、定下了什么、接下来谁做什么、什么时候完成。",
            true, true, Decisions(), Todos()),
        T("decision", "决策会", "通用", "围绕一个或几个待定事项，记录选项、权衡和最终决定。",
            "这场会是为了做决定：让可选方案、各自的利弊和最终拍板一目了然。",
            false, false,
            S("background", "议题背景", "要决定的是什么事，为什么现在要定"),
            S("options", "可选方案", "讨论过的每个方案，各用一句话写清主要优缺点"),
            Decisions("最终决定"),
            S("dissent", "保留意见", "提出过但没有改变决定的反对意见或顾虑", "warn"),
            Todos()),
        T("brainstorm", "头脑风暴", "通用", "收集所有点子，归类并标出值得深入的。",
            "不要漏掉任何一个不同的想法，哪怕还不成熟；相近的归到一起；标出大家最感兴趣的。",
            false, false,
            S("ideas", "点子清单", "提出过的每个不同想法，每条一句，尽量按主题归类"),
            S("promising", "值得深入", "大家想继续推进的想法，以及原因"),
            Todos("下一步")),

        T("weekly", "项目周会", "项目与研发", "进展、风险、决策、待办一目了然，适合周会 / 例会。",
            "按工作线对照计划看进度；尽早暴露风险和阻塞；每条待办都要有负责人和截止日期。",
            true, false,
            S("progress", "本周进展", "上次会议以来完成了什么，按工作线或人分条写"),
            S("next", "下周计划", "接下来计划做什么，按工作线或人分条写，有时间点就写具体日期", due: true),
            S("risks", "风险与问题", "风险、阻塞或延期，每条写清影响", "warn", owner: true),
            Decisions(), Todos()),
        T("standup", "每日站会", "项目与研发", "按人记录昨天、今天和阻塞，五分钟看完。",
            "按人记录：做了什么、要做什么、被什么卡住。越简短越好。",
            false, false,
            S("yesterday", "昨日完成", "每个人完成了什么，负责人写这个人", owner: true),
            S("today", "今日计划", "每个人今天要做什么，负责人写这个人", owner: true),
            S("blockers", "阻塞问题", "卡住某人的问题，以及谁能帮忙解决", "warn", owner: true),
            Todos("会后跟进")),
        T("kickoff", "项目启动会", "项目与研发", "目标、范围、里程碑、分工和风险。",
            "把目标、做与不做的范围、带日期的里程碑、谁负责什么都定清楚。",
            false, false,
            S("goals", "目标与成功标准", "项目要达成什么，怎么衡量成功"),
            S("scope", "范围", "做哪些，明确不做哪些"),
            S("milestones", "里程碑", "每个里程碑以及它的具体日期", due: true),
            S("roles", "角色分工", "谁负责什么", owner: true),
            S("risks", "风险与依赖", "风险和外部依赖", "warn"),
            Todos()),
        T("requirements", "需求评审", "项目与研发", "确认了哪些需求、还有哪些要澄清、改动和风险。",
            "把已经达成一致的和还没定的分开；记录需求的改动以及改动原因。",
            true, false,
            S("summary_req", "需求概述", "需求是什么、给谁用，几句话说清"),
            S("confirmed", "已确认的需求点", "大家认可的需求点", "check"),
            S("open", "待澄清问题", "还需要回答的问题，以及该由谁回答", "warn", owner: true, due: true),
            S("changes", "变更与影响", "会上对需求的改动，以及影响到哪些地方"),
            S("risks", "风险", "对进度、质量或可行性的风险", "warn"),
            Decisions(), Todos()),
        T("design", "技术方案评审", "项目与研发", "方案要点、备选对比、风险依赖和评审结论。",
            "记录推荐方案、备选方案以及没选它们的原因、评审人的顾虑。",
            true, true,
            S("proposal", "方案要点", "推荐方案的关键设计点"),
            S("alternatives", "备选方案对比", "讨论过的备选方案，以及选或不选的原因"),
            S("concerns", "评审意见", "评审人提出的顾虑、问题和建议", "warn", owner: true),
            S("risks", "风险与依赖", "技术风险和依赖", "warn"),
            Decisions("评审结论"), Todos()),
        T("retro", "复盘会", "项目与研发", "做得好的、待改进的、根本原因和改进行动。",
            "具体、对事不对人；每条改进行动都要对应到它要解决的问题。",
            false, true,
            S("goal_result", "目标与结果", "原计划是什么，实际结果如何"),
            S("good", "做得好的", "做得好、要保持的地方", "check"),
            S("bad", "待改进", "出了问题或让人难受的地方", "warn"),
            S("causes", "根本原因", "问题背后的根本原因，而不只是表面现象"),
            Todos("改进行动")),

        T("sales", "客户拜访 / 销售沟通", "业务与管理", "客户需求、痛点、顾虑、预算与决策链、下一步。",
            "留意客户的需求、痛点、异议、预算、时间表和谁拍板；记下我们答应了客户什么。",
            false, false,
            S("customer", "客户背景", "客户是谁、现状如何、在场有哪些人"),
            S("needs", "需求与痛点", "客户需要什么，现在哪里不好用"),
            S("concerns", "顾虑与异议", "客户提出的反对意见、疑虑和条件", "warn"),
            S("budget", "预算、时间与决策链", "预算、时间要求和由谁决定（就会上说到的写）", due: true),
            S("competitors", "竞品信息", "提到的竞争对手或替代方案"),
            S("promises", "我方承诺", "我们答应客户的事", "check", owner: true, due: true),
            Todos("下一步")),
        T("business", "经营分析会", "业务与管理", "关键指标、问题分析、决策和跟进事项。",
            "数字一律照会上原话写；把事实（数字）、分析和决策分开。",
            true, true,
            S("metrics", "关键指标", "提到的关键数字，原样写出，带上周期和对比"),
            S("issues", "问题与原因", "业务上的问题以及讨论出的原因", "warn"),
            Decisions(), Todos()),
        T("oneonone", "一对一沟通", "业务与管理", "近况、话题、反馈和需要的支持。",
            "保持私下谈话的语气；记录双向的反馈和对方需要的支持。",
            false, false,
            S("status", "近况", "对方最近的状态和工作情况"),
            S("feedback", "反馈", "双方互相给出的反馈"),
            S("growth", "成长与支持", "职业发展话题，以及需要的帮助或资源"),
            Todos()),

        T("interview", "面试记录", "人事", "候选人背景、各维度表现、亮点和疑虑、建议。",
            "根据候选人说的内容公正评价，每条都要有依据；不要编造分数。",
            false, false,
            S("background", "候选人背景", "候选人自己介绍的经历和背景"),
            S("assessment", "能力评估", "每个维度一条（如专业能力、沟通、解决问题、动机），附上依据"),
            S("strengths", "亮点", "突出的优势，附上依据", "check"),
            S("doubts", "疑虑", "需要后续核实的顾虑或短板", "warn"),
            S("recommendation", "结论建议", "面试官明确说出的建议；没说就留空")),

        T("lecture", "培训 / 课程笔记", "学习与内容", "核心观点、知识点、案例、金句和问答。",
            "按学习笔记来写：主要观点、概念和步骤、例子、值得记住的话。",
            true, false,
            S("ideas", "核心观点", "讲者希望大家记住的主要观点"),
            S("knowledge", "知识点", "讲到的概念、定义、方法或步骤"),
            S("cases", "案例", "用来说明的例子和故事"),
            S("quotes", "金句", "值得记住的话，尽量贴近原话"),
            S("qa", "问答", "听众的提问和回答")),
        T("podcast", "访谈 / 播客", "学习与内容", "嘉宾背景、主要观点、精彩引用和问答。",
            "记下每位发言人的观点和最精彩的话，分清谁说的。",
            true, false,
            S("guests", "嘉宾", "嘉宾是谁，按介绍时的说法"),
            S("views", "主要观点", "表达的主要观点，以及是谁的观点"),
            S("quotes", "精彩引用", "印象深刻的话，尽量贴近原话，注明谁说的"),
            S("qa", "问答要点", "关键的问题和回答")),
    ];

    private static string File => Path.Combine(Paths.Roaming, "templates.json");

    public static List<NoteTemplate> LoadMine()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<List<NoteTemplate>>(System.IO.File.ReadAllText(File), Json.Options) ?? [];
        }
        catch
        {
            Log.Debug("templates: unreadable");
        }
        return [];
    }

    public static void SaveMine(List<NoteTemplate> mine)
    {
        try
        {
            Directory.CreateDirectory(Paths.Roaming);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(mine.Where(t => !t.BuiltIn).ToList(), Json.Options));
        }
        catch
        {
            Log.Debug("templates: save failed");
        }
    }

    /// Built-in first, then the user's.
    public static IReadOnlyList<NoteTemplate> All(IEnumerable<NoteTemplate> mine) => [.. BuiltIn, .. mine];

    public static NoteTemplate Find(string? id, IEnumerable<NoteTemplate> mine) =>
        All(mine).FirstOrDefault(t => t.Id == id) ?? BuiltIn[0];
}
