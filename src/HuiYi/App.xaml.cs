using HuiYi.Localization;
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
    public static Updates Updates { get; } = new();
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
        if (Array.IndexOf(e.Args, "--export-icon") is var at and >= 0 && at + 1 < e.Args.Length)
        {
            Icons.SaveIco(e.Args[at + 1]);
            Shutdown();
            return;
        }
        if (e.Args.Contains("--snapshot"))
        {
            // Snapshots work on a copy of the meetings: they must never touch the user's records,
            // and they can run beside the real app.
            var copy = Path.Combine(Path.GetTempPath(), "HuiYi-snapshot");
            if (Directory.Exists(copy)) Directory.Delete(copy, true);
            // --demo: made-up meetings (for the README's screenshots) instead of a copy of the real ones.
            if (e.Args.Contains("--demo")) Demo.Write(Path.Combine(copy, "Sessions"));
            else CopyFolder(Path.Combine(Paths.Roaming, "Sessions"), Path.Combine(copy, "Sessions"));
            Paths.Roaming = copy;
        }
        // --data <folder>: settings, meetings and the glossary in that folder (tests, demos), running
        // beside the real app; the models stay shared.
        var dataAt = Array.IndexOf(e.Args, "--data");
        var data = dataAt >= 0 && dataAt + 1 < e.Args.Length ? e.Args[dataAt + 1] : null;
        if (data != null)
        {
            Directory.CreateDirectory(data);
            Paths.Roaming = Path.GetFullPath(data);
        }
        single = new Mutex(true, e.Args.Contains("--snapshot") ? @"Local\HuiYi.snapshot"
            : data != null ? @"Local\HuiYi.data." + Paths.Roaming.ToLowerInvariant().Replace('\\', '_').Replace(':', '_') : @"Local\HuiYi.single", out var first);
        if (!first)
        {
            // Already running (the desktop shortcut double-clicked again): ask that one to show the
            // meeting records, and leave quietly.
            if (!e.Args.Contains("--background") && EventWaitHandle.TryOpenExisting(OpenSignal, out var signal))
            {
                AllowSetForegroundWindow(-1); // let the running copy bring its window to the front
                signal.Set();
                signal.Dispose();
            }
            Shutdown();
            return;
        }
        if (!e.Args.Contains("--snapshot") && data == null) ListenForOpen();
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Debug($"app: unhandled {ex.Exception}");
            ex.Handled = true;
        };
        Model = new AppModel(preview: e.Args.Contains("--snapshot"));
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
        if (Arg("--ui-language") is { } language) L.SetLanguage(language);
        L.Changed += RefreshLanguage;
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
        // Opened from the shortcut: show the meeting records. Started with Windows: stay in the tray.
        else if (!background) OpenMain();
        if (e.Args.Contains("--listen")) _ = ToggleListening();
        StartUpdateChecks();
    }

    private void RefreshLanguage()
    {
        UpdateTrayText();
        mainWindow?.Refresh();
        if (tray?.ContextMenu is { } menu) FillMenu(menu);
    }

    private const string OpenSignal = @"Local\HuiYi.open";

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// A second launch sets this signal instead of starting another copy.
    private void ListenForOpen()
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSignal);
        var thread = new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(() =>
                {
                    if (!Model.Settings.OnboardingDone || !Model.Models.Ready) OpenSetup();
                    else OpenMain();
                });
        }) { IsBackground = true, Name = "open-signal" };
        thread.Start();
    }

    private System.Windows.Threading.DispatcherTimer? updateTimer;
    private string? toldAbout;

    /// Half a minute after start, then every six hours: check GitHub, download quietly, say so once.
    private void StartUpdateChecks()
    {
        if (!Updates.Installed) return;
        updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Interval = TimeSpan.FromHours(6);
            // Found a newer version: ask once per version per run (下次提醒 means "next time I start").
            if (await Updates.Check() && Updates.AvailableVersion != toldAbout)
            {
                toldAbout = Updates.AvailableVersion;
                UpdateWindow.ShowFor(Model, Updates);
            }
        };
        updateTimer.Start();
    }

    /// Stops everything cleanly, then lets Velopack swap in the new version and start it.
    public void RestartToUpdate()
    {
        if (Updates.ReadyVersion == null) return;
        Model.Shutdown();
        tray?.Dispose();
        single?.ReleaseMutex();
        single = null;
        Updates.ApplyAndRestart();
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
        Item(listening ? L.T("结束本场会议") : L.T("开始收听"), () => _ = ToggleListening());
        Item(Model.Settings.Display == DisplayMode.Notes ? L.T("打开实时面板") : L.T("打开悬浮字幕"), ShowLive, listening);
        var translate = Item(L.T("翻译字幕"), () => Model.SetTranslation(!Model.Settings.TranslationOn));
        translate.IsCheckable = false;
        translate.IsChecked = Model.Settings.TranslationOn;
        menu.Items.Add(new Separator());
        Item(L.T("会议记录"), OpenMain);
        Item(L.T("纪要模板"), () => OpenTemplates());
        Item(L.T("词库"), () => OpenGlossary());
        Item(L.T("设置"), OpenSettings);
        menu.Items.Add(new Separator());
        if (Updates.ReadyVersion is { } next) Item(L.F("重启并更新到 {0}", next), RestartToUpdate);
        else if (Updates.AvailableVersion is { } found) Item(L.F("发现新版本 {0}", found), () => UpdateWindow.ShowFor(Model, Updates));
        Item(L.F("退出{0}", Brand.Name), Quit);
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
        if (Model.Settings.TrayClickStarts || !Model.Models.Ready) await ToggleListening();
        else ShowReady();
    }

    /// The live panel, waiting for 开始会议.
    public void ShowReady()
    {
        subtitles?.Hide();
        notesWindow ??= new NotesWindow(Model);
        notesWindow.ShowReady();
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
        UpdateTrayText();
        if (!live)
        {
            subtitles?.Hide();
            notesWindow?.OnStopped();
        }
        mainWindow?.Refresh();
    }

    private void UpdateTrayText()
    {
        if (tray == null) return;
        tray.ToolTipText = Model.IsListening ? L.F("{0} · 收听中（单击打开面板，右键结束会议）", Brand.Name)
            : Model.Settings.TrayClickStarts ? L.F("{0} · 单击开始收听", Brand.Name) : L.F("{0} · 单击打开面板", Brand.Name);
    }

    public void OpenMain()
    {
        mainWindow ??= new MainWindow(Model);
        mainWindow.Closed += (_, _) => mainWindow = null;
        Bring(mainWindow);
    }

    private TemplateWindow? templateWindow;
    private GlossaryWindow? glossaryWindow;

    public void OpenGlossary()
    {
        glossaryWindow ??= new GlossaryWindow(Model);
        glossaryWindow.Closed += (_, _) => glossaryWindow = null;
        Bring(glossaryWindow);
    }

    public void OpenTemplates(string? select = null)
    {
        templateWindow ??= new TemplateWindow(Model, select);
        templateWindow.Closed += (_, _) => templateWindow = null;
        Bring(templateWindow);
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
        ShowToast(L.T("面板已收起，会议仍在记录。单击通知区域的图标可重新打开。"));
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
            var why = p.Reason is MeetingEndWatch.Reason.AppLeft a ? L.F("{0} 的会议似乎已结束", a.Name) : L.T("已经有一阵没人说话");
            ShowToast(L.F("{0}，稍后将自动结束本场记录。如需继续，请打开实时面板点「不结束，继续」。", why));
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
        Updates.ApplyOnExit();
        tray?.Dispose();
        single?.ReleaseMutex();
        Shutdown();
    }
}
