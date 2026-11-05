using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using HuiYi.Speak;

namespace HuiYi.App;

/// The live notes window: subtitles as a running transcript, and the notes the AI keeps. A panel
/// that never takes focus from the meeting app and stays out of screen captures unless the user
/// lets it show (then it wears a badge).
public partial class NotesWindow : Window
{
    private readonly AppModel model;
    private readonly ObservableCollection<LineVM> lines = [];
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? sessionId;
    private DateTime startedAt;
    private MeetingEndWatch.Pending? pending;
    private bool placed;

    public NotesWindow(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        LinesList.ItemsSource = lines;
        SourceInitialized += (_, _) =>
        {
            Native.MakeNonActivating(this);
            ApplyCapturable();
        };
        Header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        model.LineChanged += OnLine;
        // Speaker names (the user's or the AI's) change labels on lines already shown.
        model.Store.Changed += () => Dispatcher.BeginInvoke(() => OnLine(""));
        model.Subtitle += OnSubtitle;
        model.NotesChanged += RenderNotes;
        model.SpeakChanged += RenderSpeak;
        model.StateChanged += RenderSpeak;
        model.StateChanged += RenderTranslate;
        clock.Tick += (_, _) => Tick();
        RenderTranslate();
    }

    /// The translation switch follows the setting (it can change from the tray or settings too);
    /// switched off, the transcript shows only what was said.
    private void RenderTranslate()
    {
        TranslateSwitch.IsChecked = model.Settings.TranslationOn;
        OnLine("");
    }

    private void Translate_Click(object sender, RoutedEventArgs e) => model.SetTranslation(TranslateSwitch.IsChecked == true);

    public void ApplyCapturable()
    {
        Native.SetCapturable(this, !model.Settings.HideFromCaptures);
        Shared.Visibility = model.Settings.HideFromCaptures ? Visibility.Visible : Visibility.Collapsed;
    }

    /// Top right of the work area, fully on screen (shorter than the screen when it must be).
    private void PlaceTopRight()
    {
        var area = SystemParameters.WorkArea;
        Height = Math.Min(Height, area.Height - 48);
        Left = area.Right - Width - 24;
        Top = area.Top + 24;
    }

    /// Opened from the tray with no meeting going: the panel waits, and nothing records until
    /// 开始会议 (unless the user chose 单击直接开始收听 in settings).
    public void ShowReady()
    {
        var first = !placed;
        if (first)
        {
            PlaceTopRight();
            placed = true;
        }
        RenderFrom();
        ReadyPanel.Visibility = Visibility.Visible;
        // Nothing behind the button: not the last meeting's transcript, not its notes.
        LinesScroll.Visibility = Visibility.Hidden;
        NotesScroll.Visibility = Visibility.Hidden;
        LiveDot.Visibility = Visibility.Collapsed;
        StateText.Text = "未开始";
        StateText.Foreground = Theme.NightMuted;
        OnAirPill.Background = Theme.NightRaised;
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        clock.Stop();
        StopBtn.Visibility = Visibility.Collapsed;
        StartBtn.Visibility = Visibility.Collapsed;
        EndBanner.Visibility = Visibility.Collapsed;
        Show();
        if (first) PlaceTopRight();
    }

    private void RenderFrom() =>
        ReadyText.Text = model.Settings.ListenMic
            ? "点「开始会议」后，会译同时收听电脑里的声音和你的麦克风，实时出字幕、整理纪要。"
            : "点「开始会议」后，会译开始收听电脑里的声音，实时出字幕、整理纪要。";

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (model.IsListening) return;
        await ((App)Application.Current).ToggleListening();
    }

    public void ShowNoFocus()
    {
        ReadyPanel.Visibility = Visibility.Collapsed;
        StartBtn.Visibility = Visibility.Collapsed;
        LinesScroll.Visibility = TabNotes.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        NotesScroll.Visibility = TabNotes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        var first = !placed;
        if (first)
        {
            PlaceTopRight();
            placed = true;
        }
        if (model.LiveSessionId != sessionId)
        {
            // A new meeting: start from an empty page.
            sessionId = model.LiveSessionId;
            startedAt = DateTime.Now;
            lines.Clear();
            LivePanel.Visibility = Visibility.Collapsed;
            EndBanner.Visibility = Visibility.Collapsed;
            TabLines.IsChecked = true;
        }
        LiveDot.Visibility = Visibility.Visible;
        StateText.Text = "收听中";
        StateText.Foreground = OnAirText;
        OnAirPill.Background = OnAirFill;
        StopBtn.Visibility = Visibility.Visible;
        clock.Start();
        RenderNotes();
        RenderSpeak();
        Show();
        // Windows may place a tool window on its own the first time it appears: put it back.
        if (first) PlaceTopRight();
    }

    /// Developer snapshot: show a stored meeting's lines and notes as the live panel would.
    public void Preview(string id, bool notesTab)
    {
        sessionId = id;
        if (model.Store.Snapshot(id) is { } s) LineVM.Sync(lines, s, model.Settings.TranslationOn);
        EmptyHint.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        model.Notes.Show(id);
        RenderNotes();
        TabNotes.IsChecked = notesTab;
        TabLines.IsChecked = !notesTab;
        ClockText.Text = "12:34";
    }

    // Listening: a red recording dot on the panel's own indigo, not the app's green.
    private static readonly System.Windows.Media.Brush OnAirFill = Frozen(0x2C, 0x31, 0x48);
    private static readonly System.Windows.Media.Brush OnAirText = Frozen(0xE8, 0xEA, 0xF2);

    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var x = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        x.Freeze();
        return x;
    }

    public void OnStopped()
    {
        clock.Stop();
        LiveDot.Visibility = Visibility.Collapsed;
        StateText.Text = model.Ai.HasKey ? "已结束 · 正在整理纪要" : "已结束";
        StateText.Foreground = Theme.NightMuted;
        OnAirPill.Background = Theme.NightRaised;
        StopBtn.Visibility = Visibility.Collapsed;
        StartBtn.Visibility = Visibility.Visible;
        LivePanel.Visibility = Visibility.Collapsed;
        EndBanner.Visibility = Visibility.Collapsed;
        // The notes are being rewritten over the whole meeting: show them.
        if (IsVisible && model.Ai.HasKey) TabNotes.IsChecked = true;
    }

    private void Tick()
    {
        ClockText.Text = TranscriptExport.Clock((DateTime.Now - startedAt).TotalSeconds);
        if (pending != null)
        {
            var left = Math.Max(0, (int)(pending.Deadline - DateTime.Now).TotalSeconds);
            var why = pending.Reason is MeetingEndWatch.Reason.AppLeft a ? $"看起来 {a.Name} 的会议已经结束" : "已经有一阵没人说话了";
            EndText.Text = $"{why}，{left} 秒后自动结束本场记录。";
        }
    }

    public void ShowEndPending(MeetingEndWatch.Pending? p)
    {
        pending = p;
        EndBanner.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        Tick();
    }

    /// A click on a speaker's name: name them for the whole meeting.
    private void Who_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LineVM { Renamable: true } vm } chip || sessionId is not { } id) return;
        e.Handled = true;
        var number = vm.Number!.Value;
        var voice = vm.Voice!.Value;
        var current = vm.Who == $"发言人 {number}" ? "" : vm.Who;
        // Screen pixels → DIPs, so the box lands under the name at any display scale.
        var at = chip.PointToScreen(new Point(0, chip.ActualHeight + 2));
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target) at = target.TransformFromDevice.Transform(at);
        SpeakerNameWindow.Ask(at, number, current, name =>
        {
            model.RenameVoice(id, voice, name);
            if (model.Store.Snapshot(id) is { } s) LineVM.Sync(lines, s, model.Settings.TranslationOn);
        });
    }

    private void OnLine(string id)
    {
        if (sessionId == null) return;
        var atBottom = LinesScroll.VerticalOffset >= LinesScroll.ScrollableHeight - 30;
        if (model.Store.Snapshot(sessionId) is { } s) LineVM.Sync(lines, s, model.Settings.TranslationOn);
        EmptyHint.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (atBottom) LinesScroll.ScrollToEnd();
    }

    private void OnSubtitle(MeetingController.Subtitle s)
    {
        if (!IsVisible) return;
        // Only the line being said shows here; finished lines are in the list.
        if (s.Live)
        {
            var atBottom = LinesScroll.VerticalOffset >= LinesScroll.ScrollableHeight - 30;
            LivePanel.Visibility = Visibility.Visible;
            EmptyHint.Visibility = Visibility.Collapsed;
            var last = lines.LastOrDefault(l => !l.IsMe);
            var src = s.Source;
            if (last != null && src.StartsWith(last.Src)) src = src[last.Src.Length..].TrimStart();
            LiveSrc.Text = src;
            var dst = s.Translation;
            if (last?.Dst is { } d && dst.StartsWith(d)) dst = dst[d.Length..].TrimStart();
            LiveDst.Text = model.Settings.TranslationOn ? dst : "";
            LiveDst.Visibility = LiveDst.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            LiveWho.Text = "正在说…";
            if (atBottom) LinesScroll.ScrollToEnd();
        }
        else
        {
            LivePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderNotes()
    {
        var n = model.Notes;
        NotesHost.Content = n.Notes.IsEmpty
            ? new System.Windows.Controls.TextBlock
            {
                FontSize = 13, Foreground = Theme.NightMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
                Text = model.Ai.HasKey ? "聊上几句后，AI 会在这里整理纪要，之后大约每 45 秒刷新一次。"
                                         : "在「设置 › AI 服务」里接入一个大模型，开会时就会自动整理纪要。",
            }
            : NotesRenderer.Build(n.Notes, (n.ShownId is { } shown ? model.Store.Get(shown)?.StartedAt : null) ?? DateTime.Now,
                names: n.ShownId is { } named && model.Store.Snapshot(named) is { } s ? SpeakerLabels.Names(s) : null);
        NotesState.Text = n.Status switch
        {
            NotesEngine.State.Updating => "纪要整理中…",
            NotesEngine.State.RateLimited => "请求太多，稍后再试",
            NotesEngine.State.InvalidKey => "AI 服务的 key 不可用",
            NotesEngine.State.Failed => "纪要这次没刷新",
            _ => "",
        };
    }

    private void RenderSpeak()
    {
        var on = model.Settings.SpeakEnabled;
        SpeakBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;
        var key = model.Settings.SpeakKey == SpeakKey.RightAlt ? "右 Alt" : "右 Ctrl";
        var state = model.Speak?.Status ?? SpeakEngine.State.Idle;
        SpeakDot.Fill = state == SpeakEngine.State.Idle ? Theme.NightMuted : Theme.Primary;
        SpeakText.Text = state switch
        {
            SpeakEngine.State.Recording => "录音中 · 松开按键后翻译",
            SpeakEngine.State.Working => "正在翻译…",
            SpeakEngine.State.Speaking => "正在播报给对方",
            _ => !VirtualCable.Installed ? "同声传译：需要先安装虚拟麦克风（设置 › 同声传译）"
                : $"同声传译：按住 {key} 说，松开后译成{Languages.ChineseName(model.Speak?.Target() ?? "en")}播报",
        };
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // Waiting for 开始会议: the tabs don't uncover the last meeting behind the button.
        if (LinesScroll == null || ReadyPanel.Visibility == Visibility.Visible) return;
        var notes = TabNotes.IsChecked == true;
        LinesScroll.Visibility = notes ? Visibility.Collapsed : Visibility.Visible;
        NotesScroll.Visibility = notes ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (model.IsListening) await ((App)Application.Current).ToggleListening();
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        if (model.IsListening) ((App)Application.Current).PanelHidden();
    }

    private void KeepGoing_Click(object sender, RoutedEventArgs e)
    {
        model.KeepGoing();
        ShowEndPending(null);
    }
}
