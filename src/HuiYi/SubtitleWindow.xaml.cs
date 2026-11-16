using HuiYi.Localization;
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
        UiText.Set(WhoText, "Text", () => L.T("收听中 · 有人说话时字幕会出现在这里"));
        Bar.BorderBrush = Theme.Primary;
        TranslationText.Text = "";
        SourceText.Text = "";
        Fit();
    }

    /// Two lines for each stream, at the chosen size; the translation's only while translating.
    private void Fit()
    {
        var size = model.Settings.SubtitleFontSize;
        TranslationText.FontSize = size;
        TranslationText.LineHeight = Math.Round(size * 1.4);
        TranslationBox.Height = TranslationText.LineHeight * 2;
        TranslationBox.Visibility = model.Settings.TranslationOn ? Visibility.Visible : Visibility.Collapsed;
        SourceText.LineHeight = 21;
        // With no translation the original is all there is: give it a third line.
        SourceBox.Height = SourceText.LineHeight * (model.Settings.TranslationOn ? 2 : 3);
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
        Fit();
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
        var (_, number) = model.SpeakerOf(s.LineId);
        UiText.Set(WhoText, "Text", () => s.Live
            ? L.F("{0} · 正在说", model.SpeakerOf(s.LineId).Who) : model.SpeakerOf(s.LineId).Who);
        Bar.BorderBrush = number is { } n ? Theme.Speaker(n) : Theme.Primary;
        Fit();
        // The boxes show the end of each stream; what is far out of sight needn't be laid out.
        TranslationText.Text = model.Settings.TranslationOn ? Tail(s.Translation, 400) : "";
        SourceText.Text = Tail(s.Source, 400);
        // The original sits right under the translation while it fits, and shows its end once it doesn't.
        SourceText.VerticalAlignment = VerticalAlignment.Top;
        SourceBox.UpdateLayout();
        if (SourceText.ActualHeight > SourceBox.Height + 1) SourceText.VerticalAlignment = VerticalAlignment.Bottom;
        quiet.Stop();
        if (!s.Live) quiet.Start();
    }

    private static string Tail(string s, int max) => s.Length <= max ? s : s[^max..];

    private async void Stop_Click(object sender, RoutedEventArgs e) => await ((App)Application.Current).ToggleListening();
}
