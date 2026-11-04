using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HuiYi.App;

/// The little box for naming a speaker from the live panel. The panel never takes focus (it would
/// steal it from the meeting app), so typing needs a window of its own: it opens under the name,
/// takes the keyboard, and closes on Enter, Esc or a click elsewhere.
public sealed class SpeakerNameWindow : Window
{
    private readonly TextBox box;
    private readonly Action<string> save;
    private bool done;

    /// `at`: where to open, in screen DIPs. `save` gets the new name ("" puts 发言人 N back).
    public static void Ask(Point at, int number, string current, Action<string> save) =>
        new SpeakerNameWindow(at, number, current, save).Show();

    /// Developer snapshot: the box as it opens, not shown.
    public static Window ForSnapshot(int number, string current) => new SpeakerNameWindow(new Point(0, 0), number, current, _ => { });

    private SpeakerNameWindow(Point at, int number, string current, Action<string> save)
    {
        this.save = save;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        WindowStartupLocation = WindowStartupLocation.Manual;

        var night = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x33));
        var line = new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x55));
        var muted = new SolidColorBrush(Color.FromRgb(0x8E, 0x94, 0xA8));

        box = new TextBox
        {
            Style = (Style)Application.Current.FindResource("NightField"), Tag = "例如：张三、Daniel",
            Text = current, FontSize = 14, Margin = new Thickness(0, 10, 0, 0),
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Commit(box.Text.Trim());
            else if (e.Key == Key.Escape) Close();
        };

        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        var ok = new Button { Content = "保存", Style = (Style)Application.Current.FindResource("PrimaryBtn"), Padding = new Thickness(16, 4, 16, 4) };
        ok.Click += (_, _) => Commit(box.Text.Trim());
        var cancel = new Button { Content = "取消", Style = (Style)Application.Current.FindResource("NightBtn"), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        DockPanel.SetDock(ok, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        if (current.Length > 0)
        {
            var reset = new Button
            {
                Content = $"恢复为发言人 {number}", Style = (Style)Application.Current.FindResource("GhostBtn"),
                Foreground = muted, Margin = new Thickness(-6, 0, 0, 0),
            };
            reset.Click += (_, _) => Commit("");
            DockPanel.SetDock(reset, Dock.Left);
            buttons.Children.Add(reset);
        }

        var body = new StackPanel { Width = 280 };
        body.Children.Add(new TextBlock
        {
            Text = $"给「发言人 {number}」起个名字", FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF8)),
        });
        body.Children.Add(box);
        body.Children.Add(new TextBlock
        {
            Text = "这场会议里这个人说的话都会显示这个名字，纪要里也会用它。", FontSize = 11, Foreground = muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });
        body.Children.Add(buttons);
        Content = new Border
        {
            Background = night, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(8), Child = body,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.35 },
        };

        // Under the name, kept on screen.
        var area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left, Math.Min(at.X - 8, area.Right - 320));
        Top = Math.Max(area.Top, Math.Min(at.Y - 4, area.Bottom - 200));

        Loaded += (_, _) =>
        {
            Activate();
            box.Focus();
            box.SelectAll();
        };
        // Closing deactivates the window, and a second Close() during closing throws: close once.
        Closing += (_, _) => closing = true;
        Deactivated += (_, _) => { if (!done && !closing) Close(); };
    }

    private bool closing;

    private void Commit(string name)
    {
        if (closing) return;
        done = true;
        save(name);
        Close();
    }
}
