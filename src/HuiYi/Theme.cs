using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace HuiYi.App;

/// The same colours as App.xaml, for code-built views.
public static class Theme
{
    public static readonly Brush Ink = Frozen(0x1E, 0x22, 0x35);
    public static readonly Brush Primary = Frozen(0x4F, 0x5B, 0xD5);
    public static readonly Brush PrimarySoft = Frozen(0xEC, 0xEE, 0xFC);
    public static readonly Brush OnAir = Frozen(0x22, 0xB0, 0x7D);
    public static readonly Brush Warn = Frozen(0xD9, 0x82, 0x2B);
    public static readonly Brush Grey = Frozen(0x64, 0x6B, 0x80);
    public static readonly Brush Grey2 = Frozen(0x9A, 0xA1, 0xB2);
    public static readonly Brush Line = Frozen(0xE3, 0xE6, 0xEE);
    public static readonly Brush Surface = Frozen(0xFF, 0xFF, 0xFF);
    public static readonly Brush NightText = Frozen(0xE8, 0xEA, 0xF2);
    public static readonly Brush NightMuted = Frozen(0x8E, 0x94, 0xA8);
    public static readonly Brush NightRaised = Frozen(0x25, 0x29, 0x39);

    /// 发言人 1, 2…: told apart at a glance, readable on the dark panel and on white.
    private static readonly Brush[] Speakers =
    [
        Frozen(0x5B, 0x8D, 0xEF), Frozen(0x2F, 0xB3, 0x8A), Frozen(0xB0, 0x7C, 0xE8),
        Frozen(0xE0, 0x9B, 0x3D), Frozen(0x3B, 0xB4, 0xC9), Frozen(0xE0, 0x6C, 0x9A),
    ];

    public static Brush Speaker(int number) => Speakers[(number - 1) % Speakers.Length];

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var x = new SolidColorBrush(Color.FromRgb(r, g, b));
        x.Freeze();
        return x;
    }
}

/// Draws meeting notes. `cards`: each part on its own card (the records window); otherwise compact
/// text for the dark live panel.
public static class NotesRenderer
{
    public static UIElement Build(MeetingNotes n, double fontSize = 14, bool cards = false)
    {
        var ink = cards ? Theme.Ink : Theme.NightText;
        var muted = cards ? Theme.Grey : Theme.NightMuted;
        var root = new StackPanel();
        if (n.IsEmpty) return root;

        TextBlock Text(string s, double size, FontWeight weight, Brush brush, Thickness margin) => new()
        {
            Text = s, FontSize = size, FontWeight = weight, Foreground = brush, Margin = margin, TextWrapping = TextWrapping.Wrap,
        };
        TextBlock Overline(string s) => new()
        {
            Text = s, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = cards ? Theme.Primary : Theme.NightMuted,
            Margin = new Thickness(0, 0, 0, 6),
        };
        // One block: a card in the records window, a plain group in the panel.
        Panel Block(Brush? fill = null)
        {
            var inner = new StackPanel();
            if (!cards)
            {
                inner.Margin = new Thickness(0, 0, 0, 16);
                root.Children.Add(inner);
                return inner;
            }
            root.Children.Add(new Border
            {
                Background = fill ?? Theme.Surface, BorderBrush = Theme.Line, BorderThickness = new Thickness(fill == null ? 1 : 0),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 10), Child = inner,
            });
            return inner;
        }

        if (n.Summary.Length > 0)
        {
            var b = Block(Theme.PrimarySoft);
            b.Children.Add(Overline("一句话总结"));
            b.Children.Add(Text(n.Summary, fontSize + (cards ? 1 : 0), FontWeights.Normal, ink, new Thickness(0)));
        }
        var topics = n.Topics.Count == 0 ? null : Block();
        if (topics != null)
        {
            topics.Children.Add(Overline($"议题 · {n.Topics.Count}"));
            for (var ti = 0; ti < n.Topics.Count; ti++)
            {
                var t = n.Topics[ti];
                var head = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, ti == 0 ? 0 : 12, 0, 3), FontSize = fontSize + 1 };
                head.Inlines.Add(new Run(t.Title) { FontWeight = FontWeights.SemiBold, Foreground = ink });
                head.Inlines.Add(new Run("  " + TranscriptExport.Clock(t.Start)) { Foreground = muted, FontSize = fontSize - 3 });
                topics.Children.Add(head);
                if (t.Summary.Length > 0) topics.Children.Add(Text(t.Summary, fontSize - 1, FontWeights.Normal, muted, new Thickness(0, 0, 0, 3)));
                foreach (var p in t.Points)
                    topics.Children.Add(Text("–  " + p, fontSize, FontWeights.Normal, ink, new Thickness(2, 1, 0, 1)));
                if (t.Fishbone is { } f)
                {
                    topics.Children.Add(Text("为什么：" + f.Question, fontSize - 1, FontWeights.SemiBold, muted, new Thickness(2, 6, 0, 2)));
                    foreach (var br in f.Branches)
                        topics.Children.Add(Text($"·  {br.Label}：{br.Detail}", fontSize - 1, FontWeights.Normal,
                            br.Support == 0 ? muted : ink, new Thickness(12, 1, 0, 1)));
                }
            }
        }
        void Items(string title, List<NoteItem> items, bool todo)
        {
            if (items.Count == 0) return;
            var b = Block();
            b.Children.Add(Overline(title));
            foreach (var i in items)
            {
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2), FontSize = fontSize };
                tb.Inlines.Add(new Run(todo ? "□  " : "●  ") { Foreground = todo ? muted : Theme.OnAir, FontSize = fontSize - 3 });
                tb.Inlines.Add(new Run(i.Text) { Foreground = ink });
                if (i.Owner != null) tb.Inlines.Add(new Run("   @" + i.Owner) { Foreground = cards ? Theme.Primary : Theme.NightMuted, FontSize = fontSize - 2 });
                b.Children.Add(tb);
            }
        }
        Items("已经定下的", n.Conclusions, false);
        Items("待办事项", n.Todos, true);
        if (n.Terms.Count > 0)
        {
            var b = Block();
            b.Children.Add(Overline("人名与术语"));
            foreach (var t in NoteTerm.Ordered(n.Terms))
            {
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2), FontSize = fontSize - 1 };
                tb.Inlines.Add(new Run(t.Name) { FontWeight = FontWeights.SemiBold, Foreground = ink });
                if (t.Note.Length > 0) tb.Inlines.Add(new Run("  " + t.Note) { Foreground = muted });
                if (t.Heard.Count > 0) tb.Inlines.Add(new Run("  · 识别成 " + string.Join("、", t.Heard)) { Foreground = muted });
                b.Children.Add(tb);
            }
        }
        return root;
    }
}
