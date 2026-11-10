using HuiYi.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HuiYi.Speak;

namespace HuiYi.App;

/// First run as a four-page wizard: the on-device models, a try with whatever the PC plays, the
/// Ling key (optional), and speaking into the meeting (optional, needs the virtual mic).
public partial class SetupWindow : Window
{
    private static readonly string[] StepNames = ["本机模型", "试听", "AI 服务", "同声传译"];
    private readonly AppModel model;
    private readonly StackPanel[] pages;
    private CancellationTokenSource? download;
    private int page;

    public SetupWindow(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        pages = [Page0, Page1, Page2, Page3];
        PropertyChangedEventManager.AddHandler(L.Instance, LanguageChanged, "Item[]");
        UiText.Set(this, "Title", () => L.F("{0} · 开始使用", L.T("会译")));
        Heading.Text = Brand.Name;
        Icon = Icons.Mark(64, false);
        Logo.Source = Icons.Mark(80, true);
        SpeakOn.IsChecked = model.Settings.SpeakEnabled;
        model.Subtitle += OnSubtitle;
        model.Ai.Changed += () => Dispatcher.BeginInvoke(RenderKey);
        FillPresets();
        Activated += (_, _) => RenderCable();
        Closed += (_, _) =>
        {
            model.Subtitle -= OnSubtitle;
            download?.Cancel();
        };
        RenderModels();
        RenderKey();
        RenderCable();
        Go(0);
        if (!model.Models.Ready) _ = Download();
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        Heading.Text = Brand.Name;
        RenderSteps();
    }

    /// Developer snapshot: open on a given step.
    public void ShowStep(int step) => Go(step);

    private void Go(int to)
    {
        page = Math.Clamp(to, 0, pages.Length - 1);
        for (var i = 0; i < pages.Length; i++) pages[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
        BackBtn.Visibility = page == 0 ? Visibility.Collapsed : Visibility.Visible;
        UiText.Set(NextBtn, "Content", () => page == pages.Length - 1 ? L.T("完成") : page >= 2 ? L.T("下一步（可跳过）") : L.T("下一步"));
        RenderSteps();
        RenderModels();
    }

    /// The left rail: done steps get a tick, the current one is bright.
    private void RenderSteps()
    {
        Steps.Children.Clear();
        for (var i = 0; i < StepNames.Length; i++)
        {
            var current = i == page;
            var done = i < page || (i == 0 && model.Models.Ready);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
            var dot = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 10, 0),
                Background = current ? Theme.Primary : done ? Theme.OnAir : Theme.NightRaised,
                Child = new TextBlock
                {
                    Text = done && !current ? "✓" : (i + 1).ToString(), Foreground = Brushes.White, FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            row.Children.Add(dot);
            row.Children.Add(new TextBlock
            {
                Text = L.T(StepNames[i]), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                Foreground = current ? Brushes.White : Theme.NightMuted, FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
            });
            Steps.Children.Add(row);
        }
    }

    private void RenderModels()
    {
        var ready = model.Models.Ready;
        if (ready) ModelProgress.Value = 1;
        if (ready) UiText.Set(ModelText, "Text", () => L.T("✓ 模型已就绪"));
        else if (download == null) UiText.Set(ModelText, "Text", () => L.T("尚未下载"));
        ModelBtn.Visibility = ready || download != null ? Visibility.Collapsed : Visibility.Visible;
        TryBtn.IsEnabled = ready;
        // The first step can't be skipped: nothing works without the models.
        NextBtn.IsEnabled = page != 0 || ready;
        UiText.Set(DoneHint, "Text", () => page == 0 && !ready ? L.T("下载完成后才能继续") : "");
    }

    private async Task Download()
    {
        download = new CancellationTokenSource();
        RenderModels();
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            ModelProgress.Value = (double)p.Done / p.Total;
            UiText.Set(ModelText, "Text", () => L.F("下载中  {0} / {1} MB", p.Done / 1_048_576, p.Total / 1_048_576));
        });
        try
        {
            await model.Models.Download(progress, download.Token);
            UiText.Set(ModelText, "Text", () => L.T("正在加载模型…"));
            await model.EnsureEngines();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            UiText.Set(ModelText, "Text", () => L.T("下载失败：") + (e.InnerException?.Message ?? e.Message) + L.T("。请检查网络后点「重新下载」。"));
        }
        download = null;
        RenderModels();
        RenderSteps();
    }

    private void Model_Click(object sender, RoutedEventArgs e) => _ = Download();

    private async void Try_Click(object sender, RoutedEventArgs e)
    {
        await ((App)Application.Current).ToggleListening();
        UiText.Set(TryBtn, "Content", () => model.IsListening ? L.T("停止收听") : L.T("开始收听"));
        TryBtn.Style = (Style)FindResource(model.IsListening ? "OnAirBtn" : "PrimaryBtn");
        if (model.IsListening) UiText.Set(TryDst, "Text", () => L.T("收听中…现在播放一段视频"));
    }

    private void OnSubtitle(MeetingController.Subtitle s)
    {
        if (s.Source.Length == 0) return;
        UiText.Set(TryDst, "Text", () => s.Translation.Length > 0 ? (s.Translation.Length > 80 ? "…" + s.Translation[^80..] : s.Translation) : L.T("（译文稍后出现）"));
        TrySrc.Text = s.Source.Length > 120 ? "…" + s.Source[^120..] : s.Source;
    }

    /// Platforms offered here: those with a model to start from (the rest are set up in settings).
    private static readonly ProviderPreset[] Quick = ProviderPreset.All.Where(p => p.Models.Length > 0).ToArray();

    private ProviderPreset Chosen => Quick.FirstOrDefault(p => p.Id == (SetupPreset.SelectedItem as ComboBoxItem)?.Tag as string) ?? Quick[0];

    private void FillPresets()
    {
        foreach (var p in Quick) SetupPreset.Items.Add(UiText.Bind(new ComboBoxItem { Content = L.T(p.Name), Tag = p.Id }, ("Content", () => L.T(p.Name))));
        var current = model.Settings.ActiveProvider()?.Preset;
        SetupPreset.SelectedItem = SetupPreset.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current)
                                   ?? SetupPreset.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == "ucloud");
    }

    private void SetupPreset_Changed(object sender, SelectionChangedEventArgs e) => RenderKey();

    private void RenderKey()
    {
        var ai = model.Ai;
        UiText.Set(KeyText, "Text", () => ai.Current?.Preset != Chosen.Id ? L.T(Chosen.Hint ?? "") : ai.State switch
        {
            AiClient.KeyState.Ok => L.F("✓ {0} 可用", ai.Describe),
            AiClient.KeyState.Checking => L.T("验证中…"),
            AiClient.KeyState.Invalid => L.T("这个 key 不可用"),
            AiClient.KeyState.Unreachable => ai.CheckError ?? L.T("暂时连不上，请检查网络"),
            AiClient.KeyState.Unchecked => L.T("已保存"),
            _ => L.T(Chosen.Hint ?? ""),
        });
        KeyText.Foreground = ai.Current?.Preset == Chosen.Id && ai.State is AiClient.KeyState.Invalid or AiClient.KeyState.Unreachable
            ? Theme.Warn : Theme.Grey;
    }

    /// Sets up (or updates) the chosen platform as the service in use, with its first model.
    private async void Key_Click(object sender, RoutedEventArgs e)
    {
        var k = KeyBox.Password.Trim();
        if (k.Length == 0 && !Chosen.KeyOptional) return;
        var s = model.Settings;
        var p = s.Providers.FirstOrDefault(x => x.Preset == Chosen.Id);
        if (p == null)
        {
            p = Chosen.Create();
            s.Providers.Add(p);
        }
        if (k.Length > 0) AIKey.Save(p.Id, k);
        s.ActiveProviderId = p.Id;
        model.SaveSettings();
        KeyBox.Clear();
        KeyBtn.IsEnabled = false;
        model.Ai.Refresh();
        await model.Ai.Check();
        KeyBtn.IsEnabled = true;
        RenderKey();
    }

    private void GetKey_Click(object sender, RoutedEventArgs e) => Native.Open(Chosen.KeyUrl);

    private void RenderCable()
    {
        if (installing) return;
        var ok = VirtualCable.Installed;
        UiText.Set(CableText, "Text", () => ok ? L.T("✓ 已安装 VB-CABLE 虚拟麦克风") : L.T("还没有虚拟麦克风"));
        UiText.Set(CableHelp, "Text", () => ok ? "" : L.T("从 VB-Audio 官网下载免费的 VB-CABLE，校验后打开它的安装程序，需要管理员权限。"));
        CableHelp.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        CableBtn.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        CableBtn.IsEnabled = true;
        UiText.Set(CableBtn, "Content", () => L.T("一键安装"));
    }

    private bool installing;

    private async void Cable_Click(object sender, RoutedEventArgs e)
    {
        installing = true;
        CableBtn.IsEnabled = false;
        UiText.Set(CableBtn, "Content", () => L.T("安装中…"));
        var message = await model.InstallCable(new Progress<string>(m => CableHelp.Text = m));
        installing = false;
        RenderCable();
        if (message != null)
        {
            CableHelp.Visibility = Visibility.Visible;
            CableHelp.Text = message;
        }
    }

    private void Speak_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        model.Settings.SpeakEnabled = SpeakOn.IsChecked == true;
        model.SaveSettings();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Go(page - 1);

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (page < pages.Length - 1)
        {
            Go(page + 1);
            return;
        }
        model.Settings.OnboardingDone = true;
        model.SaveSettings();
        Close();
        if (model.IsListening) ((App)Application.Current).ShowLive();
        else ((App)Application.Current).OpenMain();
    }
}
