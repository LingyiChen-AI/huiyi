using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace HuiYi.App;

/// 悬浮字幕: the translation of what is being said, large, with the original under it and the
/// speaker above. Never takes focus; drag it anywhere. Clears after a quiet spell.
public partial class SubtitleWindow : Window
{
    private readonly AppModel model;
    private readonly DispatcherTimer quiet = new() { Interval = TimeSpan.FromSeconds(6) };
    private bool placed;

    public SubtitleWindow(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            Native.MakeNonActivating(this);
            ApplyCapturable();
        };
        Bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        quiet.Tick += (_, _) =>
        {
            quiet.Stop();
            Idle();
        };
        Idle();
    }

    private void Idle()
    {
        WhoText.Text = "收听中 · 有人说话时字幕会出现在这里";
        Bar.BorderBrush = Theme.Primary;
        TranslationText.Text = "";
        SourceText.Text = "";
        TranslationText.Visibility = Visibility.Collapsed;
        SourceText.Visibility = Visibility.Collapsed;
    }

    public void ApplyCapturable()
    {
        Native.SetCapturable(this, !model.Settings.HideFromCaptures);
        Shared.Visibility = model.Settings.HideFromCaptures ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowNoFocus()
    {
        if (!placed)
        {
            var area = SystemParameters.WorkArea;
            Width = Math.Min(1000, area.Width * 0.66);
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Bottom - 200;
            placed = true;
        }
        TranslationText.FontSize = model.Settings.SubtitleFontSize;
        Show();
    }

    public void Show(MeetingController.Subtitle s)
    {
        if (!IsVisible) return;
        if (s.Source.Length == 0 && s.Translation.Length == 0)
        {
            Idle();
            return;
        }
        var (who, number) = model.SpeakerOf(s.LineId);
        WhoText.Text = s.Live ? $"{who} · 正在说" : who;
        Bar.BorderBrush = number is { } n ? Theme.Speaker(n) : Theme.Primary;
        var translation = model.Settings.TranslationOn ? Tail(s.Translation, 90) : "";
        TranslationText.Text = translation;
        TranslationText.Visibility = translation.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SourceText.Text = Tail(s.Source, 140);
        SourceText.Visibility = Visibility.Visible;
        quiet.Stop();
        if (!s.Live) quiet.Start();
    }

    /// Long text keeps its end: the newest words matter.
    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];

    private async void Stop_Click(object sender, RoutedEventArgs e) => await ((App)Application.Current).ToggleListening();
}
