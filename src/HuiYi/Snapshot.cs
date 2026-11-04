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
        var latest = model.Store.All.FirstOrDefault(s => s.Lines.Count > 0);
        if (latest != null && latest.Notes == null) model.Store.SetNotes(latest.Id, Sample());

        Save(new SetupWindow(model), folder, "setup");
        var main = new MainWindow(model);
        Save(main, folder, "main-notes", keepOpen: true);
        main.ShowTab(notes: false);
        Save(main, folder, "main-lines");
        var settings = new SettingsWindow(model);
        Save(settings, folder, "settings", keepOpen: true);
        settings.Pages.SelectedIndex = 1;
        Save(settings, folder, "settings-ai", keepOpen: true);
        // An open drop-down lives in its own popup: render its list on its own.
        settings.NotesLangBox.IsDropDownOpen = true;
        settings.UpdateLayout();
        settings.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (settings.NotesLangBox.Template.FindName("PART_Popup", settings.NotesLangBox) is System.Windows.Controls.Primitives.Popup { Child: FrameworkElement list })
            SaveElement(list, folder, "dropdown", new SolidColorBrush(Color.FromRgb(0xF5, 0xF6, 0xFA)));
        settings.NotesLangBox.IsDropDownOpen = false;
        settings.Close();

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
