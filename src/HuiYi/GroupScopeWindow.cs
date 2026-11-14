using HuiYi.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace HuiYi.App;

/// Where a glossary group counts: every meeting, or only meetings with some notes templates (a
/// project's word list in its weekly and review meetings). A card in the app's dialog look.
public sealed class GroupScopeWindow : Window
{
    private readonly RadioButton all;
    private readonly List<(CheckBox Box, string Id)> boxes = [];
    private bool confirmed;

    /// The templates chosen (empty: every meeting), or null when cancelled.
    public static List<string>? Ask(Window owner, string group, IReadOnlyList<NoteTemplate> templates, IReadOnlyList<string> current)
    {
        var w = new GroupScopeWindow(group, templates, current) { Owner = owner };
        w.ShowDialog();
        if (!w.confirmed) return null;
        return w.all.IsChecked == true ? [] : w.boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id).ToList();
    }

    private GroupScopeWindow(string group, IReadOnlyList<NoteTemplate> templates, IReadOnlyList<string> current)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        Title = L.F("「{0}」在哪些会议里用", group);

        var body = new StackPanel { Width = 380 };
        body.Children.Add(new TextBlock { Text = L.F("「{0}」在哪些会议里用", group), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink });
        body.Children.Add(new TextBlock
        {
            Text = L.T("只在选中模板的会议里按这组词纠正、翻译和写纪要；其他会议不受影响。"), FontSize = 13, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12), Foreground = Theme.Grey,
        });
        all = new RadioButton { Content = L.T("所有会议都用"), GroupName = "scope", IsChecked = current.Count == 0, Margin = new Thickness(0, 0, 0, 6) };
        var some = new RadioButton { Content = L.T("只在这些模板的会议里用："), GroupName = "scope", IsChecked = current.Count > 0 };
        body.Children.Add(all);
        body.Children.Add(some);
        var list = new StackPanel { Margin = new Thickness(22, 6, 0, 0) };
        foreach (var t in templates)
        {
            var box = new CheckBox { Content = t.BuiltIn ? L.T(t.Name) : t.Name, IsChecked = current.Contains(t.Id), Margin = new Thickness(0, 3, 0, 3) };
            box.Checked += (_, _) => some.IsChecked = true;
            boxes.Add((box, t.Id));
            list.Children.Add(box);
        }
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var ok = new Button { Content = L.T("保存"), Style = (Style)Application.Current.FindResource("PrimaryBtn"), Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
        ok.Click += (_, _) => { confirmed = true; Close(); };
        var cancel = new Button
        {
            Content = L.T("取消"), Style = (Style)Application.Current.FindResource("Btn"), Padding = new Thickness(18, 6, 18, 6),
            Margin = new Thickness(0, 0, 8, 0), IsCancel = true,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        body.Children.Add(buttons);

        Content = new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(22, 20, 22, 18), Margin = new Thickness(16),
            BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.18, Color = Color.FromRgb(0x1B, 0x14, 0x60) },
            Child = body,
        };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not CheckBox and not RadioButton) DragMove(); };
    }

    /// Developer snapshot.
    public static Window ForSnapshot(string group, IReadOnlyList<NoteTemplate> templates, IReadOnlyList<string> current) =>
        new GroupScopeWindow(group, templates, current);
}
