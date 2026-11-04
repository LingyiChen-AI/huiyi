using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HuiYi.App;

/// Draws meeting notes. In the records window (`cards`): an overview card, the topics as a timeline,
/// then cards for decisions, to-dos and names. In the dark live panel: the same structure, compact.
public static class NotesRenderer
{
    private sealed class Look(bool cards, double size, DateTime start, IReadOnlyDictionary<int, string>? names)
    {
        public bool Cards = cards;
        /// Speaker number → the name given to it, so a named owner keeps their voice's colour.
        public IReadOnlyDictionary<int, string> Names = names ?? new Dictionary<int, string>();
        public double Size = size;
        /// The meeting's local start: every time shown is a real clock time.
        public DateTime Start = start;
        public string At(double seconds) => NotesPrompt.ClockAt(Start, seconds);
        public Brush Ink = cards ? Theme.Ink : Theme.NightText;
        public Brush Muted = cards ? Theme.Grey : Theme.NightMuted;
        public Brush Faint = cards ? Theme.Grey2 : Frozen(0x6E, 0x74, 0x88);
        public Brush Rule = cards ? Theme.Line : Frozen(0x33, 0x38, 0x4C);
        public Brush Soft = cards ? Theme.PrimarySoft : Frozen(0x2A, 0x2F, 0x45);
        public Brush Accent = cards ? Theme.Primary : Frozen(0x9C, 0xA5, 0xF2);
        public Brush ChipBack = cards ? Frozen(0xF2, 0xF3, 0xF7) : Frozen(0x2A, 0x2E, 0x40);
    }

    /// `start`: when the meeting began (UTC or local), for real clock times.
    /// `names`: the meeting's speaker names (SpeakerLabels.Names): "发言人1" in the notes reads as the
    /// name given since, and owners keep their voice's colour.
    public static UIElement Build(MeetingNotes n, DateTime start, double fontSize = 14, bool cards = false,
        IReadOnlyDictionary<int, string>? names = null)
    {
        if (names is { Count: > 0 }) n = SpeakerLabels.WithNames(n, names);
        var k = new Look(cards, fontSize, start.ToLocalTime(), names);
        var root = new StackPanel();
        if (n.IsEmpty) return root;

        Overview(root, n, k);
        if (n.Topics.Count > 0) Timeline(root, n, k);
        // The template's parts in its order; each style has its own mark.
        foreach (var part in n.Parts().Where(p => p.Items.Count > 0))
            Section(root, k, part.Title, part.Items.Count, panel =>
            {
                for (var i = 0; i < part.Items.Count; i++)
                {
                    var it = part.Items[i];
                    var mark = part.Style switch
                    {
                        "check" => Check(k),
                        "todo" => Box(k),
                        "warn" => Warn(k),
                        _ => Dot(k),
                    };
                    panel.Children.Add(Item(k, mark, it.Text, it.Owner, it.T, top: i == 0 ? 0 : 7, due: it.Due));
                }
            });
        if (n.Terms.Count > 0)
            Section(root, k, "人名与术语", n.Terms.Count, panel =>
            {
                var wrap = new WrapPanel();
                foreach (var t in NoteTerm.Ordered(n.Terms)) wrap.Children.Add(Term(k, t));
                panel.Children.Add(wrap);
            });
        return root;
    }

    // MARK: Parts

    /// The summary, with a coloured edge, and a line of counts under it.
    private static void Overview(StackPanel root, MeetingNotes n, Look k)
    {
        var inner = new StackPanel();
        if (n.Summary.Length > 0)
        {
            inner.Children.Add(Label(k, "概要"));
            inner.Children.Add(Para(k, n.Summary, k.Size + (k.Cards ? 1 : 0), k.Ink));
        }
        var counts = new List<string>();
        if (n.Topics.Count > 0) counts.Add($"{n.Topics.Count} 个议题");
        if (n.Conclusions.Count > 0) counts.Add($"{n.Conclusions.Count} 项结论");
        if (n.Todos.Count > 0) counts.Add($"{n.Todos.Count} 项待办");
        var dated = n.Todos.Count(t => t.Due != null);
        if (dated > 0) counts.Add($"{dated} 项有截止日期");
        if (counts.Count > 0)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var c in counts) row.Children.Add(Chip(k, c, k.Accent, k.Cards ? Theme.Surface : k.ChipBack));
            inner.Children.Add(row);
        }
        root.Children.Add(new Border
        {
            Background = k.Soft,
            BorderBrush = k.Accent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(6),
            Padding = k.Cards ? new Thickness(18, 14, 18, 14) : new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, k.Cards ? 14 : 16),
            Child = inner,
        });
    }

    /// Topics in time order: a time pill and a line joining them, the topic beside it.
    private static void Timeline(StackPanel root, MeetingNotes n, Look k)
    {
        var list = new StackPanel();
        for (var i = 0; i < n.Topics.Count; i++)
        {
            var t = n.Topics[i];
            var last = i == n.Topics.Count - 1;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(k.Cards ? 64 : 58) });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            // Left: time pill, then the line down to the next topic.
            var rail = new Grid();
            rail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rail.RowDefinitions.Add(new RowDefinition());
            var pill = Chip(k, k.At(t.Start), k.Accent, k.Soft, small: !k.Cards);
            pill.HorizontalAlignment = HorizontalAlignment.Left;
            rail.Children.Add(pill);
            if (!last)
            {
                var line = new Rectangle { Width = 1.5, Fill = k.Rule, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(18, 4, 0, 0) };
                Grid.SetRow(line, 1);
                rail.Children.Add(line);
            }
            row.Children.Add(rail);

            // Right: title, one line, the points.
            var body = new StackPanel { Margin = new Thickness(0, 0, 0, last ? 0 : (k.Cards ? 22 : 18)) };
            body.Children.Add(new TextBlock
            {
                Text = t.Title, FontSize = k.Size + 2, FontWeight = FontWeights.SemiBold, Foreground = k.Ink,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0),
            });
            if (t.Summary.Length > 0) body.Children.Add(Para(k, t.Summary, k.Size - 1, k.Muted, top: 3));
            for (var p = 0; p < t.Points.Count; p++)
                body.Children.Add(Item(k, Dot(k), t.Points[p], null, t.PointTime(p), top: p == 0 ? 8 : 4));
            if (t.Fishbone is { } f) body.Children.Add(Causes(k, f));
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            list.Children.Add(row);
        }
        Section(root, k, "议题", n.Topics.Count, panel => panel.Children.Add(list));
    }

    /// "Why": the question, then each cause; weakly supported ones fade.
    private static UIElement Causes(Look k, Fishbone f)
    {
        var box = new StackPanel();
        box.Children.Add(new TextBlock
        {
            Text = "原因分析 · " + f.Question, FontSize = k.Size - 1, FontWeight = FontWeights.SemiBold, Foreground = k.Ink,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });
        foreach (var b in f.Branches)
        {
            var tb = Para(k, "", k.Size - 1, b.Support == 0 ? k.Faint : k.Ink, top: 3);
            tb.Inlines.Add(new Run(b.Label) { FontWeight = FontWeights.SemiBold });
            if (b.Detail.Length > 0) tb.Inlines.Add(new Run("　" + b.Detail) { Foreground = b.Support == 0 ? k.Faint : k.Muted });
            box.Children.Add(tb);
        }
        return new Border
        {
            Background = k.ChipBack, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 9, 12, 10),
            Margin = new Thickness(0, 10, 0, 0), Child = box,
        };
    }

    /// A section: a small heading with its count, then its content (a card in the records window).
    private static void Section(StackPanel root, Look k, string title, int count, Action<StackPanel> fill)
    {
        var inner = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, k.Cards ? 12 : 8) };
        head.Children.Add(Label(k, title, bottom: 0));
        head.Children.Add(new TextBlock { Text = count.ToString(), FontSize = 11, Foreground = k.Faint, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        inner.Children.Add(head);
        fill(inner);
        if (k.Cards)
            root.Children.Add(new Border
            {
                Background = Theme.Surface, BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(18, 14, 18, 16), Margin = new Thickness(0, 0, 0, 12), Child = inner,
            });
        else
        {
            inner.Margin = new Thickness(0, 4, 0, 18);
            root.Children.Add(inner);
        }
    }

    /// One line of a list: a mark, the text, owner and deadline chips, and when it was said on the right.
    private static UIElement Item(Look k, UIElement mark, string text, string? owner, double? t, double top = 6, string? due = null)
    {
        var g = new Grid { Margin = new Thickness(0, top, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (mark is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Top;
            fe.Margin = new Thickness(0, (k.Size * 1.6 - fe.Height) / 2, 0, 0);
        }
        g.Children.Add(mark);
        var line = Para(k, text, k.Size, k.Ink);
        if (owner != null)
        {
            line.Inlines.Add(new Run("  "));
            line.Inlines.Add(new InlineUIContainer(Chip(k, owner, OwnerBrush(owner, k), k.ChipBack, small: true)) { BaselineAlignment = BaselineAlignment.Center });
        }
        if (due != null)
        {
            line.Inlines.Add(new Run(" "));
            line.Inlines.Add(new InlineUIContainer(Chip(k, "截止 " + due, Theme.Warn, k.Cards ? Frozen(0xFB, 0xF0, 0xE2) : Frozen(0x3A, 0x2F, 0x22), small: true))
                { BaselineAlignment = BaselineAlignment.Center });
        }
        Grid.SetColumn(line, 1);
        g.Children.Add(line);
        if (t is { } time)
        {
            var when = new TextBlock
            {
                Text = k.At(time), FontSize = 11, Foreground = k.Faint, ToolTip = "会上提到的时间",
                Margin = new Thickness(12, (k.Size * 1.6 - 14) / 2, 0, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(when, 2);
            g.Children.Add(when);
        }
        return g;
    }

    /// A name or term: bold name, what it is, and how recognition misheard it.
    private static UIElement Term(Look k, NoteTerm t)
    {
        var tb = new TextBlock { FontSize = k.Size - 1, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
        tb.Inlines.Add(new Run(t.Name) { FontWeight = FontWeights.SemiBold, Foreground = k.Ink });
        if (t.Note.Length > 0) tb.Inlines.Add(new Run("  " + t.Note) { Foreground = k.Muted });
        if (t.Heard.Count > 0) tb.Inlines.Add(new Run("\n识别成 " + string.Join("、", t.Heard)) { Foreground = k.Faint, FontSize = 11 });
        return new Border
        {
            Background = k.ChipBack, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 8, 8), Child = tb,
        };
    }

    // MARK: Small pieces

    private static TextBlock Label(Look k, string text, double bottom = 6) => new()
    {
        Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = k.Accent, Margin = new Thickness(0, 0, 0, bottom),
    };

    /// Body text with room between lines (CJK reads better at ~1.6).
    private static TextBlock Para(Look k, string text, double size, Brush brush, double top = 0) => new()
    {
        Text = text, FontSize = size, Foreground = brush, TextWrapping = TextWrapping.Wrap,
        LineHeight = Math.Round(size * 1.6), LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        Margin = new Thickness(0, top, 0, 0),
    };

    private static Border Chip(Look k, string text, Brush fore, Brush back, bool small = false) => new()
    {
        Background = back, CornerRadius = new CornerRadius(10), Padding = small ? new Thickness(7, 0, 7, 1) : new Thickness(9, 2, 9, 2),
        Margin = small ? new Thickness(0) : new Thickness(0, 0, 6, 0),
        Child = new TextBlock { Text = text, FontSize = small ? 11 : 12, Foreground = fore, FontWeight = FontWeights.SemiBold },
    };

    private static FrameworkElement Dot(Look k) => new Ellipse
    {
        Width = 6, Height = 6, Fill = k.Accent, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0),
    };

    private static FrameworkElement Check(Look k)
    {
        var g = new Grid { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left };
        g.Children.Add(new Ellipse { Fill = Theme.OnAir });
        g.Children.Add(new Path
        {
            Data = Geometry.Parse("M 4.5 8.2 L 7 10.6 L 11.6 5.6"), Stroke = Brushes.White, StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        });
        return g;
    }

    /// Risks and open questions: an amber exclamation.
    private static FrameworkElement Warn(Look k)
    {
        var g = new Grid { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left };
        g.Children.Add(new Ellipse { Fill = Theme.Warn });
        g.Children.Add(new TextBlock
        {
            Text = "!", Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0),
        });
        return g;
    }

    private static FrameworkElement Box(Look k) => new Border
    {
        Width = 15, Height = 15, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1.6),
        BorderBrush = k.Accent, HorizontalAlignment = HorizontalAlignment.Left,
    };

    /// An owner reads in the colour their voice has in the transcript.
    private static Brush OwnerBrush(string owner, Look k)
    {
        var m = Regex.Match(owner, @"(?:发言人|说话人)\s*(\d+)");
        if (m.Success) return Theme.Speaker(int.Parse(m.Groups[1].Value));
        foreach (var (number, name) in k.Names)
            if (name.Length > 0 && name == owner) return Theme.Speaker(number);
        return owner is "你" or "我" ? Theme.OnAir : k.Accent;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var x = new SolidColorBrush(Color.FromRgb(r, g, b));
        x.Freeze();
        return x;
    }
}
