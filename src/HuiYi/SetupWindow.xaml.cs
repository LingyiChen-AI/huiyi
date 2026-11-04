using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HuiYi.Speak;

namespace HuiYi.App;

/// First run as a four-page wizard: the on-device models, a try with whatever the PC plays, the
/// Ling key (optional), and speaking into the meeting (optional, needs the virtual mic).
public partial class SetupWindow : Window
{
    private static readonly string[] StepNames = ["本机模型", "试听", "AI 纪要", "同传发言"];
    private readonly AppModel model;
    private readonly StackPanel[] pages;
    private CancellationTokenSource? download;
    private int page;

    public SetupWindow(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        pages = [Page0, Page1, Page2, Page3];
        Title = $"{Brand.Name} · 开始使用";
        Heading.Text = Brand.Name;
        Icon = Icons.Mark(64, false);
        Logo.Source = Icons.Mark(80, true);
        SpeakOn.IsChecked = model.Settings.SpeakEnabled;
        model.Subtitle += OnSubtitle;
        model.Ling.Changed += () => Dispatcher.BeginInvoke(RenderKey);
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

    private void Go(int to)
    {
        page = Math.Clamp(to, 0, pages.Length - 1);
        for (var i = 0; i < pages.Length; i++) pages[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
        BackBtn.Visibility = page == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextBtn.Content = page == pages.Length - 1 ? "完成" : page >= 2 ? "下一步（可跳过）" : "下一步";
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
                Text = StepNames[i], FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                Foreground = current ? Brushes.White : Theme.NightMuted, FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
            });
            Steps.Children.Add(row);
        }
    }

    private void RenderModels()
    {
        var ready = model.Models.Ready;
        if (ready) ModelProgress.Value = 1;
        if (ready) ModelText.Text = "✓ 模型已就绪";
        else if (download == null) ModelText.Text = "尚未下载";
        ModelBtn.Visibility = ready || download != null ? Visibility.Collapsed : Visibility.Visible;
        TryBtn.IsEnabled = ready;
        // The first step can't be skipped: nothing works without the models.
        NextBtn.IsEnabled = page != 0 || ready;
        DoneHint.Text = page == 0 && !ready ? "下载完成后才能继续" : "";
    }

    private async Task Download()
    {
        download = new CancellationTokenSource();
        RenderModels();
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            ModelProgress.Value = (double)p.Done / p.Total;
            ModelText.Text = $"下载中  {p.Done / 1_048_576} / {p.Total / 1_048_576} MB";
        });
        try
        {
            await model.Models.Download(progress, download.Token);
            ModelText.Text = "正在加载模型…";
            await model.EnsureEngines();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            ModelText.Text = "下载失败：" + (e.InnerException?.Message ?? e.Message) + "。请检查网络后点「重新下载」。";
        }
        download = null;
        RenderModels();
        RenderSteps();
    }

    private void Model_Click(object sender, RoutedEventArgs e) => _ = Download();

    private async void Try_Click(object sender, RoutedEventArgs e)
    {
        await ((App)Application.Current).ToggleListening();
        TryBtn.Content = model.IsListening ? "停止收听" : "开始收听";
        TryBtn.Style = (Style)FindResource(model.IsListening ? "OnAirBtn" : "PrimaryBtn");
        if (model.IsListening) TryDst.Text = "收听中…现在播放一段视频";
    }

    private void OnSubtitle(MeetingController.Subtitle s)
    {
        if (s.Source.Length == 0) return;
        TryDst.Text = s.Translation.Length > 0 ? (s.Translation.Length > 80 ? "…" + s.Translation[^80..] : s.Translation) : "（译文稍后出现）";
        TrySrc.Text = s.Source.Length > 120 ? "…" + s.Source[^120..] : s.Source;
    }

    private void RenderKey()
    {
        KeyText.Text = model.Ling.State switch
        {
            LingClient.KeyState.Ok => "✓ 验证通过",
            LingClient.KeyState.Checking => "验证中…",
            LingClient.KeyState.Invalid => "这个 key 不可用",
            LingClient.KeyState.Unreachable => "暂时连不上 Ling，请检查网络",
            LingClient.KeyState.Unchecked => "已保存",
            _ => "",
        };
    }

    private async void Key_Click(object sender, RoutedEventArgs e)
    {
        var k = KeyBox.Password.Trim();
        if (k.Length == 0) return;
        KeyBtn.IsEnabled = false;
        await model.Ling.SetKey(k);
        KeyBox.Clear();
        KeyBtn.IsEnabled = true;
    }

    private void GetKey_Click(object sender, RoutedEventArgs e) => Native.Open("https://chat.ant-ling.com/platform/api");

    private void RenderCable()
    {
        var ok = VirtualCable.Installed;
        CableText.Text = ok ? "✓ 已检测到 VB-CABLE" : "未检测到虚拟麦克风（装好后回到这里）";
        CableBtn.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Cable_Click(object sender, RoutedEventArgs e) => Native.Open(VirtualCable.Download);

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
