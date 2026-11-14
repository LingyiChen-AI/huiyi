using HuiYi.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace HuiYi.App;

/// Picked a misheard word in the transcript: write it right, and the glossary corrects it from now
/// on (and, if asked, everywhere in this meeting already). A card in the app's own dialog look.
public sealed class TermFixWindow : Window
{
    private readonly TextBox right;
    private readonly CheckBox everywhere;
    private bool confirmed;

    /// Asks how `heard` should be written. Returns the spelling and whether to correct this meeting
    /// too, or null when cancelled. `count`: how often `heard` occurs in this meeting.
    public static (string Text, bool Everywhere)? Ask(Window owner, string heard, int count)
    {
        var w = new TermFixWindow(heard, count) { Owner = owner };
        w.ShowDialog();
        return w.confirmed && w.right.Text.Trim().Length > 0 ? (w.right.Text.Trim(), w.everywhere.IsChecked == true) : null;
    }

    private TermFixWindow(string heard, int count)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        Title = L.T("改正并加入词库");

        var ink = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x35));
        var muted = new SolidColorBrush(Color.FromRgb(0x5A, 0x5F, 0x6B));
        var body = new StackPanel { Width = 380 };
        body.Children.Add(new TextBlock { Text = L.T("改正并加入词库"), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = ink });
        body.Children.Add(new TextBlock
        {
            Text = L.F("识别成「{0}」的，以后都改成下面的写法。", heard), FontSize = 13, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12), Foreground = muted,
        });
        right = new TextBox { Text = heard, Style = (Style)Application.Current.FindResource("Field"), Tag = L.T("正确写法") };
        body.Children.Add(right);
        everywhere = new CheckBox
        {
            Content = L.F("同时改正这场会议里的 {0} 处「{1}」", count, heard), IsChecked = count > 0, IsEnabled = count > 0,
            Style = (Style)Application.Current.FindResource("Switch"), Margin = new Thickness(0, 14, 0, 0),
        };
        body.Children.Add(everywhere);

        var ok = new Button
        {
            Content = L.T("加入词库"), Style = (Style)Application.Current.FindResource("PrimaryBtn"),
            Padding = new Thickness(18, 6, 18, 6), IsDefault = true,
        };
        ok.Click += (_, _) => { confirmed = true; Close(); };
        right.TextChanged += (_, _) => ok.IsEnabled = right.Text.Trim().Length > 0;
        var cancel = new Button
        {
            Content = L.T("取消"), Style = (Style)Application.Current.FindResource("Btn"), Padding = new Thickness(18, 6, 18, 6),
            Margin = new Thickness(0, 0, 8, 0), IsCancel = true,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        body.Children.Add(buttons);

        Content = new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(22, 20, 22, 18), Margin = new Thickness(16),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xEE)), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.18, Color = Color.FromRgb(0x1B, 0x14, 0x60) },
            Child = body,
        };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) DragMove(); };
        Loaded += (_, _) => { right.Focus(); right.SelectAll(); };
    }

    /// Developer snapshot: the card as it would open.
    public static Window ForSnapshot(string heard, int count) => new TermFixWindow(heard, count);
}
