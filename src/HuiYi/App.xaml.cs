using System.IO;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;

namespace HuiYi.App;

/// Lives in the notification area (tray). Left click starts or stops listening; the menu has the
/// rest. One instance per user.
public partial class App : Application
{
    public static AppModel Model { get; private set; } = null!;
    private TaskbarIcon? tray;
    private Mutex? single;
    private SubtitleWindow? subtitles;
    private NotesWindow? notesWindow;
    private MainWindow? mainWindow;
    private SettingsWindow? settingsWindow;
    private SetupWindow? setupWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--snapshot"))
        {
            // Snapshots work on a copy of the meetings: they must never touch the user's records,
            // and they can run beside the real app.
            var copy = Path.Combine(Path.GetTempPath(), "HuiYi-snapshot");
            if (Directory.Exists(copy)) Directory.Delete(copy, true);
            CopyFolder(Path.Combine(Paths.Roaming, "Sessions"), Path.Combine(copy, "Sessions"));
            Paths.Roaming = copy;
        }
        single = new Mutex(true, e.Args.Contains("--snapshot") ? @"Local\HuiYi.snapshot" : @"Local\HuiYi.single", out var first);
        if (!first)
        {
            MessageBox.Show($"{Brand.Name} 已在运行，图标在任务栏右下角的通知区域。", Brand.Name);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Debug($"app: unhandled {ex.Exception}");
            ex.Handled = true;
        };
        Model = new AppModel();
        Model.StateChanged += UpdateTray;
        Model.Toast += ShowToast;
        Model.EndPending += OnEndPending;
        Model.Subtitle += s => subtitles?.Show(s);
        CreateTray();
        Log.Debug("app: started");

        var background = e.Args.Contains("--background");
        // Developer switches, for checking the app without clicking: --listen, --show main|settings|setup,
        // --feed <audio> (listen to a file instead of the PC, silently).
        string? Arg(string name) => Array.IndexOf(e.Args, name) is var i and >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null;
        var show = Arg("--show");
        Model.FeedFile = Arg("--feed");
        if (Arg("--snapshot") is { } shots)
        {
            // --snapshot <folder>: render every window to PNG off screen, then quit.
            Dispatcher.BeginInvoke(async () =>
            {
                try { await Snapshot.Run(Model, shots); }
                catch (Exception ex) { Log.Debug($"snapshot failed {ex}"); }
                Quit();
            });
            return;
        }
        if (show == "main") OpenMain();
        else if (show == "settings") OpenSettings();
        else if (show == "setup" || !Model.Settings.OnboardingDone || !Model.Models.Ready) OpenSetup();
        else if (!background) ShowToast($"{Brand.Name} 已就绪：单击通知区域的图标即可开始收听");
        if (e.Args.Contains("--listen")) _ = ToggleListening();
    }

    private void CreateTray()
    {
        tray = new TaskbarIcon
        {
            Icon = Icons.Tray(false),
            ToolTipText = $"{Brand.Name} · {Brand.Tagline}",
            NoLeftClickDelay = true,
            MenuActivation = H.NotifyIcon.Core.PopupActivationMode.RightClick,
        };
        tray.TrayLeftMouseUp += async (_, _) => await TrayClick();
        var menu = new ContextMenu();
        FillMenu(menu);
        // The items depend on whether we are listening: refill them each time the menu opens.
        menu.Opened += (_, _) => FillMenu(menu);
        tray.ContextMenu = menu;
        tray.ForceCreate(false);
    }

    private void FillMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        MenuItem Item(string header, Action click, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => click();
            menu.Items.Add(mi);
            return mi;
        }
        var listening = Model.IsListening;
        Item(listening ? "结束本场会议" : "开始收听", () => _ = ToggleListening());
        Item(Model.Settings.Display == DisplayMode.Notes ? "打开实时面板" : "打开悬浮字幕", ShowLive, listening);
        menu.Items.Add(new Separator());
        Item("会议记录…", OpenMain);
        Item("设置…", OpenSettings);
        menu.Items.Add(new Separator());
        Item($"退出{Brand.Name}", Quit);
    }

    private DateTime lastTrayClick = DateTime.MinValue;

    /// A click on the tray icon starts listening; while listening it only brings the panel back
    /// (after 收起 that is what people reach for). Ending a meeting takes 结束会议 or the menu, so a
    /// click never ends one by surprise, and a double click doesn't end one and start the next.
    private async Task TrayClick()
    {
        if (DateTime.Now - lastTrayClick < TimeSpan.FromSeconds(1)) return;
        lastTrayClick = DateTime.Now;
        if (Model.IsListening)
        {
            ShowLive();
            return;
        }
        await ToggleListening();
    }

    public async Task ToggleListening()
    {
        if (Model.IsListening)
        {
            // The notes window stays: it shows the full notes being written for the meeting.
            Model.StopListening();
            subtitles?.Hide();
            return;
        }
        if (!Model.Models.Ready)
        {
            OpenSetup();
            return;
        }
        if (await Model.StartListening()) ShowLive();
    }

    public void ShowLive()
    {
        if (Model.Settings.Display == DisplayMode.Notes)
        {
            subtitles?.Hide();
            notesWindow ??= new NotesWindow(Model);
            notesWindow.ShowNoFocus();
        }
        else
        {
            notesWindow?.Hide();
            subtitles ??= new SubtitleWindow(Model);
            subtitles.ShowNoFocus();
        }
    }

    /// The display mode changed in settings while listening.
    public void SwitchDisplay()
    {
        if (Model.IsListening) ShowLive();
        notesWindow?.ApplyCapturable();
        subtitles?.ApplyCapturable();
    }

    private void UpdateTray()
    {
        if (tray == null) return;
        var live = Model.IsListening;
        tray.Icon = Icons.Tray(live);
        tray.ToolTipText = live ? $"{Brand.Name} · 收听中（单击打开面板，右键结束会议）" : $"{Brand.Name} · 单击开始收听";
        if (!live)
        {
            subtitles?.Hide();
            notesWindow?.OnStopped();
        }
        mainWindow?.Refresh();
    }

    public void OpenMain()
    {
        mainWindow ??= new MainWindow(Model);
        mainWindow.Closed += (_, _) => mainWindow = null;
        Bring(mainWindow);
    }

    public void OpenSettings()
    {
        settingsWindow ??= new SettingsWindow(Model);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        Bring(settingsWindow);
    }

    public void OpenSetup()
    {
        setupWindow ??= new SetupWindow(Model);
        setupWindow.Closed += (_, _) => setupWindow = null;
        Bring(setupWindow);
    }

    private static void Bring(Window w)
    {
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    private bool hintedHidden;

    /// The panel was put away mid-meeting: say once where it went and that recording goes on.
    public void PanelHidden()
    {
        if (hintedHidden) return;
        hintedHidden = true;
        ShowToast("面板已收起，会议仍在记录。单击通知区域的图标可重新打开。");
    }

    private void ShowToast(string message)
    {
        Log.Debug("toast shown");
        tray?.ShowNotification(Brand.Name, message);
    }

    private void OnEndPending(MeetingEndWatch.Pending? p)
    {
        notesWindow?.ShowEndPending(p);
        if (p != null && Model.Settings.Display == DisplayMode.Subtitles)
        {
            var why = p.Reason is MeetingEndWatch.Reason.AppLeft a ? $"{a.Name} 的会议似乎已结束" : "已经有一阵没人说话";
            ShowToast($"{why}，稍后将自动结束本场记录。如需继续，请打开实时面板点「不结束，继续」。");
        }
    }

    private static void CopyFolder(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), true);
    }

    public void Quit()
    {
        Model.Shutdown();
        tray?.Dispose();
        single?.ReleaseMutex();
        Shutdown();
    }
}
