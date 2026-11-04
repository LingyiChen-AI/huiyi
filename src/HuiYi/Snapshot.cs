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

        Save(new SetupWindow(model), folder, "setup");
        var main = new MainWindow(model);
        Save(main, folder, "main-notes", keepOpen: true);
        main.ShowTab(notes: false);
        Save(main, folder, "main-lines");
        // The AI page needs a service to show (snapshots work on a copy of the settings too).
        if (model.Settings.Providers.Count == 0)
        {
            model.Settings.Providers.Add(ProviderPreset.Find("ucloud").Create());
            model.Settings.Providers.Add(ProviderPreset.Find("deepseek").Create());
        }
        var settings = new SettingsWindow(model);
        Save(settings, folder, "settings", keepOpen: true);
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
        var bmp = new RenderTargetBitmap(width + 40, height + 40, 96, 96, PixelFormats.Pbgra32);
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
