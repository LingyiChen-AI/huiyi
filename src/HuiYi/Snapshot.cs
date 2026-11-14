using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HuiYi.App;

/// Developer switch --snapshot <folder>: renders every window to PNG off screen, then quits. For
/// checking the UI without taking over the desktop (and the capture-proof panels render too).
public static class Snapshot
{
    public static async Task Run(AppModel model, string folder)
    {
        Directory.CreateDirectory(folder);
        // Show what the app looks like set up: translation on, past the first-run wizard.
        model.Settings.TranslationOn = true;
        model.Settings.OnboardingDone = true;
        var latest = model.Store.All.FirstOrDefault(s => s.Lines.Count > 0);
        if (latest != null && latest.Notes == null) model.Store.SetNotes(latest.Id, Sample());
        // The glossary at work: a line it corrected (underlined), and a term the AI found misheard.
        if (latest != null)
            model.Store.Mutate(latest.Id, s =>
            {
                if (s.Lines.FirstOrDefault(l => l.Src.Contains("压测")) is { } line) line.Fixes = [new LineFix { From = "鸭测", To = "压测" }];
                if (s.Notes is { } n)
                {
                    n.Terms.RemoveAll(t => t.Name == "压测");
                    n.Terms.Insert(0, new NoteTerm { Name = "压测", Note = "压力测试", Heard = ["鸭测"] });
                }
            });

        Save(new SetupWindow(model), folder, "setup");
        var main = new MainWindow(model);
        Save(main, folder, "main-notes", keepOpen: true);
        SaveElement((FrameworkElement)main.NotesHost.Content, folder, "main-notes-terms", Brushes.White);
        main.ShowTab(notes: false);
        Save(main, folder, "main-lines");
        Save(TermFixWindow.ForSnapshot("网管", 2), folder, "term-fix");
        if (latest != null) Save(new SpeakersWindow(model, latest.Id), folder, "speakers");
        Save(new GroupNameWindow(model.Store), folder, "group-new");
        Save(new MediaImportWindow(model), folder, "media-import");
        // The AI page needs a service to show (snapshots work on a copy of the settings too).
        if (model.Settings.Providers.Count == 0)
        {
            model.Settings.Providers.Add(ProviderPreset.Find("ucloud").Create());
            model.Settings.Providers.Add(ProviderPreset.Find("deepseek").Create());
        }
        var settings = new SettingsWindow(model);
        Save(settings, folder, "settings", keepOpen: true);
        settings.Pages.SelectedIndex = 4;
        Save(settings, folder, "settings-general", keepOpen: true);
        settings.Pages.SelectedIndex = 1;
        Save(settings, folder, "settings-ai", keepOpen: true);
        // An open drop-down lives in its own popup: render its list on its own.
        foreach (var (box, name) in new[] { (settings.PresetBox, "dropdown-platforms"), (settings.ModelBox, "dropdown-models") })
        {
            box.IsDropDownOpen = true;
            settings.UpdateLayout();
            settings.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (box.Template.FindName("PART_Popup", box) is System.Windows.Controls.Primitives.Popup { Child: FrameworkElement list })
                SaveElement(list, folder, name, new SolidColorBrush(Color.FromRgb(0xF5, 0xF6, 0xFA)));
            box.IsDropDownOpen = false;
        }
        settings.Pages.SelectedIndex = 2;
        Save(settings, folder, "settings-speak", keepOpen: true);
        settings.Close();
        var setup = new SetupWindow(model);
        setup.ShowStep(3);
        Save(setup, folder, "setup-speak");

        // The template center: a built-in template, then one of the user's being edited.
        var templates = new TemplateWindow(model, "weekly");
        Save(templates, folder, "templates", keepOpen: true);
        templates.CopyForSnapshot();
        Save(templates, folder, "templates-mine");

        // The glossary, with the demo meeting's words (a temp copy of the settings folder).
        model.Glossary.Import("Priya\t普丽雅|普利亚\t\t支付组，负责接口联调\n网关\t网管\tgateway\t支付请求的入口服务\t支付项目\n"
                              + "回归测试\t\tregression test\t\n压测\t鸭测\tload test\t压力测试\nKubernetes\t酷伯奈提斯\t\t\t支付项目\n");
        model.Glossary.SetTemplates("支付项目", ["weekly", "requirements"]);
        var glossary = new GlossaryWindow(model);
        glossary.TryBox.Text = "我周五前改好网管配置，跟普丽雅确认后跑一轮鸭测。";
        Save(glossary, folder, "glossary");
        Save(GroupScopeWindow.ForSnapshot("支付项目", model.Templates, model.Glossary.TemplatesOf("支付项目")), folder, "glossary-scope");

        // Notes in a work template, with owners, deadlines and risks.
        // The open meeting's own notes when they use sections (the --demo one does), else a sample.
        var weekly = latest?.Notes is { Sections.Count: > 0 } own ? own
            : NotesPrompt.Parse(WeeklySample, [0, 65, 300, 420, 610, 900], 0, NoteTemplates.Find("weekly", []))!;
        var notesStart = latest?.Notes is { Sections.Count: > 0 } ? latest.StartedAt.ToLocalTime() : DateTime.Today.AddHours(14);
        // Laid out on its own, not in a window: a window can't be taller than the screen.
        var sheet = new System.Windows.Controls.Border
        {
            Width = 760, Padding = new Thickness(24), Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF6, 0xFA)),
            Child = NotesRenderer.Build(weekly, notesStart, 14, cards: true),
        };
        sheet.Measure(new Size(760, double.PositiveInfinity));
        sheet.Arrange(new Rect(sheet.DesiredSize));
        sheet.UpdateLayout();
        SaveElement(sheet, folder, "notes-weekly", sheet.Background);

        var panel = new NotesWindow(model);
        if (latest != null)
        {
            panel.Preview(latest.Id, notesTab: false);
            Save(panel, folder, "panel-lines", keepOpen: true);
            panel.Preview(latest.Id, notesTab: true);
            Save(panel, folder, "panel-notes");
        }
        Save(SpeakerNameWindow.ForSnapshot(2, "Daniel"), folder, "speaker-name");
        // The tray menu's look: its items in a window (a real context menu needs the tray to open).
        var items = new System.Windows.Controls.StackPanel();
        foreach (var (text, on, enabled) in new[] { ("开始收听", false, true), ("打开实时面板", false, false), ("翻译字幕", true, true), ("-", false, true),
                     ("会议记录", false, true), ("纪要模板", false, true), ("设置", false, true), ("-", false, true), ("退出会译", false, true) })
            items.Children.Add(text == "-" ? new System.Windows.Controls.Separator()
                : new System.Windows.Controls.MenuItem { Header = text, IsChecked = on, IsEnabled = enabled });
        var menuHost = new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight, Background = Brushes.White,
            Content = new System.Windows.Controls.Border { Padding = new Thickness(4), Width = 160, Child = items },
        };
        Save(menuHost, folder, "tray-menu");
        var ready = new NotesWindow(model);
        Prepare(ready);
        ready.ShowReady();
        Save(ready, folder, "panel-ready");
        Save(UpdateWindow.ForSnapshot(model, App.Updates, "0.1.5",
            ["单击托盘图标先打开面板，点「开始会议」才开始录制", "在实时面板里点发言人的名字就能改名，纪要跟着变",
             "新的删除按钮和确认弹窗", "发现新版本时先问你，点「立即更新」才下载"]), folder, "update");
        Save(Dialog.ForSnapshot("删除「11月4日 支付项目周会」？", "逐字稿和纪要会一起删掉，删除后无法恢复。", "删除", true), folder, "dialog-delete");

        var subs = new SubtitleWindow(model);
        subs.Width = 900;
        Prepare(subs);
        subs.Show(new MeetingController.Subtitle("超时的问题定位到了，是网关的配置不对。",
            "We found the timeout: the gateway was misconfigured.", false));
        Save(subs, folder, "subtitle");
        await Task.Delay(100);
    }

    private static void Prepare(Window w)
    {
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -30000;
        w.Top = -30000;
        if (!w.IsVisible) w.Show();
        w.UpdateLayout();
    }

    private static void Save(Window w, string folder, string name, bool keepOpen = false)
    {
        Prepare(w);
        w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        // A transparent window shows on the desktop: give it a neutral backdrop.
        var back = w.AllowsTransparency ? new SolidColorBrush(Color.FromRgb(0x6B, 0x70, 0x80)) : w.Background;
        SaveElement((FrameworkElement)w.Content, folder, name, back);
        if (!keepOpen) w.Close();
    }

    private static void SaveElement(FrameworkElement root, string folder, string name, Brush back)
    {
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(back, null, new Rect(0, 0, width + 40, height + 40));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(20, 20, width, height));
        }
        // HUIYI_SNAPSHOT_SCALE=2: sharp captures for a 1080p film or a retina README.
        var scale = double.TryParse(Environment.GetEnvironmentVariable("HUIYI_SNAPSHOT_SCALE"), out var s) && s > 0 ? s : 1;
        var bmp = new RenderTargetBitmap((int)Math.Ceiling((width + 40) * scale), (int)Math.Ceiling((height + 40) * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var f = File.Create(Path.Combine(folder, name + ".png"))) enc.Save(f);
    }

    private const string WeeklySample = """
        {"title": "支付项目周会", "summary": "支付网关改造按计划推进，登录模块回归通过；超时问题定位到网关配置，需在周五前修复。上线日期维持 11月10日（周一）。",
         "topics": [
           {"start": 0, "title": "本周进展", "summary": "各模块进度回顾。", "points": [{"text": "登录模块回归测试全部通过", "evidence": ["L1"]}, {"text": "支付接口联调完成 80%", "evidence": ["L2"]}]},
           {"start": 420, "title": "超时问题", "summary": "定位到网关配置。", "points": [{"text": "网关超时阈值配置过低", "evidence": ["L4"]}, {"text": "改完后需要压测一轮", "evidence": ["L5"]}]}],
         "progress": [{"text": "登录模块回归测试全部通过", "evidence": ["L1"]}, {"text": "支付接口联调完成 80%", "evidence": ["L2"]}],
         "next": [{"text": "完成支付接口联调", "evidence": ["L3"]}, {"text": "准备上线检查清单", "evidence": ["L5"]}],
         "risks": [{"text": "测试环境不稳定，可能拖慢压测", "owner": "发言人2", "evidence": ["L4"]}],
         "conclusions": [{"text": "上线日期维持 11月10日（周一）", "evidence": ["L6"]}],
         "todos": [{"text": "修改网关超时配置", "owner": "发言人2", "due": "11月6日（周五）", "evidence": ["L4"]},
                   {"text": "组织一轮压测并出报告", "owner": "你", "due": "11月9日（周日）", "evidence": ["L5"]},
                   {"text": "同步市场部发布说明", "owner": "发言人1", "evidence": ["L6"]}]}
        """;

    /// Notes for the snapshot meeting, so the notes views have something to show.
    private static MeetingNotes Sample() => new()
    {
        Title = "新版本上线计划",
        Summary = "确认登录模块已修复、支付超时问题定位到网关配置；上线时间定在下周一，发布说明需市场部共同审阅。",
        Topics =
        [
            new NoteTopic { Start = 0, Title = "测试进度", Summary = "回顾各模块的修复情况。", Points = ["登录模块已修复并完成回归", "支付模块超时问题已定位"] },
            new NoteTopic { Start = 33, Title = "上线安排", Summary = "确定时间与分工。", Points = ["周三前改完网关配置", "周四再测一轮", "下周一上线"] },
        ],
        Conclusions = [new NoteItem { Text = "上线时间定在下周一", Support = 2 }],
        Todos =
        [
            new NoteItem { Text = "联系市场部审阅发布说明", Owner = "发言人 1", Support = 1 },
            new NoteItem { Text = "整理测试报告发到群里", Owner = "发言人 2", Support = 1 },
        ],
        Terms = [new NoteTerm { Name = "网关", Note = "支付请求的入口服务" }],
        Covered = 16,
        UpdatedAt = DateTime.UtcNow,
        Final = true,
    };
}
