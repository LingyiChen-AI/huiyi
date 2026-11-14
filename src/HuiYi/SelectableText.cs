using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace HuiYi.App;

/// Read-only document text that keeps the notes' inline styling and supports native selection/copy.
public sealed class SelectableText : RichTextBox
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string),
        typeof(SelectableText), new PropertyMetadata("", (d, _) => ((SelectableText)d).Render()));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// Words the glossary corrected in `Text`: drawn with a dashed underline, the tooltip saying what
    /// was heard.
    public static readonly DependencyProperty MarksProperty = DependencyProperty.Register(nameof(Marks), typeof(IReadOnlyList<HuiYi.LineFix>),
        typeof(SelectableText), new PropertyMetadata(null, (d, _) => ((SelectableText)d).Render()));

    public IReadOnlyList<HuiYi.LineFix>? Marks { get => (IReadOnlyList<HuiYi.LineFix>?)GetValue(MarksProperty); set => SetValue(MarksProperty, value); }

    private static readonly TextDecorationCollection Dashed = MakeDashed();

    private static TextDecorationCollection MakeDashed()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x4F, 0x5B, 0xD5)), 1.2) { DashStyle = new DashStyle([2, 2], 0) };
        pen.Freeze();
        var c = new TextDecorationCollection { new TextDecoration(TextDecorationLocation.Underline, pen, 1, TextDecorationUnit.FontRecommended, TextDecorationUnit.FontRecommended) };
        c.Freeze();
        return c;
    }

    private void Render()
    {
        Document.Blocks.Clear();
        var text = Text ?? "";
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var marks = Marks?.Where(m => m.To.Length > 0).OrderByDescending(m => m.To.Length).ToList();
        if (marks is not { Count: > 0 })
        {
            paragraph.Inlines.Add(new Run(text));
            Document.Blocks.Add(paragraph);
            return;
        }
        var i = 0;
        var plain = new System.Text.StringBuilder();
        while (i < text.Length)
        {
            var hit = marks.FirstOrDefault(m => string.CompareOrdinal(text, i, m.To, 0, m.To.Length) == 0);
            if (hit == null) { plain.Append(text[i++]); continue; }
            if (plain.Length > 0) { paragraph.Inlines.Add(new Run(plain.ToString())); plain.Clear(); }
            var froms = marks.Where(m => m.To == hit.To).Select(m => m.From).Distinct();
            paragraph.Inlines.Add(new Run(hit.To)
            {
                TextDecorations = Dashed,
                ToolTip = HuiYi.Localization.L.F("识别为「{0}」，已按词库改成「{1}」", string.Join("、", froms), hit.To),
            });
            i += hit.To.Length;
        }
        if (plain.Length > 0) paragraph.Inlines.Add(new Run(plain.ToString()));
        Document.Blocks.Add(paragraph);
    }

    public SelectableText()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsDocumentEnabled = true;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Document.PagePadding = new Thickness(0);
        Document.Blocks.Clear();
        MinHeight = 0;
        IsUndoEnabled = false;
    }

    public static UIElement MakeSelectable(UIElement element)
    {
        if (element is Panel panel)
        {
            for (var i = 0; i < panel.Children.Count; i++)
            {
                var child = panel.Children[i];
                var replacement = MakeSelectable(child);
                if (ReferenceEquals(child, replacement)) continue;
                panel.Children.RemoveAt(i);
                panel.Children.Insert(i, replacement);
            }
        }
        else if (element is Border border && border.Child is { } child)
        {
            border.Child = null;
            border.Child = MakeSelectable(child);
        }
        if (element is not TextBlock text) return element;
        var box = new SelectableText
        {
            FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight,
            Foreground = text.Foreground, Margin = text.Margin, MaxWidth = text.MaxWidth,
            HorizontalAlignment = text.HorizontalAlignment, VerticalAlignment = text.VerticalAlignment,
            ToolTip = text.ToolTip,
        };
        // Horizontal stacks and auto-sized grid columns measure with infinite width.
        // RichTextBox otherwise asks for the whole row and squeezes adjacent text.
        if (text.TextWrapping == TextWrapping.NoWrap)
        {
            var formatted = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentUICulture,
                text.FlowDirection, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                text.FontSize, text.Foreground, 1);
            // WPF reserves a five-pixel document gutter on either side of RichTextBox.
            box.Width = Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 16;
            box.HorizontalAlignment = HorizontalAlignment.Left;
        }
        Grid.SetColumn(box, Grid.GetColumn(text));
        Grid.SetRow(box, Grid.GetRow(text));
        var paragraph = new Paragraph { Margin = new Thickness(0), TextAlignment = text.TextAlignment };
        if (!double.IsNaN(text.LineHeight)) paragraph.LineHeight = text.LineHeight;
        paragraph.LineStackingStrategy = text.LineStackingStrategy;
        var inlines = text.Inlines.ToArray();
        text.Inlines.Clear();
        foreach (var container in inlines.OfType<InlineUIContainer>())
        {
            var child = container.Child;
            container.Child = null;
            container.Child = MakeSelectable(child);
        }
        paragraph.Inlines.AddRange(inlines);
        box.Document.Blocks.Add(paragraph);
        return box;
    }

    public static IEnumerable<SelectableText> Descendants(DependencyObject root)
    {
        if (root is SelectableText text)
        {
            yield return text;
            foreach (var container in text.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<InlineUIContainer>())
                foreach (var found in Descendants(container.Child)) yield return found;
            yield break;
        }
        var children = root is ItemsControl items
            ? items.Items.Cast<object>().Select(i => items.ItemContainerGenerator.ContainerFromItem(i)).OfType<DependencyObject>().ToList()
            : LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().ToList();
        if (children.Count == 0 && root is Visual)
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) children.Add(VisualTreeHelper.GetChild(root, i));
        foreach (var child in children)
            foreach (var found in Descendants(child)) yield return found;
    }

    /// Keep text offsets independent of runs, which WPF splits when applying highlights.
    public List<TextRange> Highlight(string query)
    {
        var matches = new List<TextRange>();
        new TextRange(Document.ContentStart, Document.ContentEnd)
            .ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent);
        if (query.Length == 0) return matches;
        var content = new System.Text.StringBuilder();
        var segments = new List<(int Offset, TextPointer Start)>();
        var pointer = Document.ContentStart;
        while (pointer != null && pointer.CompareTo(Document.ContentEnd) < 0)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var text = pointer.GetTextInRun(LogicalDirection.Forward);
                segments.Add((content.Length, pointer));
                content.Append(text);
                pointer = pointer.GetPositionAtOffset(text.Length)!;
            }
            else
            {
                if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.EmbeddedElement)
                    content.Append('\uFFFC');
                pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
            }
        }
        var value = content.ToString();
        var offset = 0;
        while ((offset = value.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var first = segments.Last(s => s.Offset <= offset);
            var last = segments.Last(s => s.Offset < offset + query.Length);
            var range = new TextRange(first.Start.GetPositionAtOffset(offset - first.Offset)!,
                last.Start.GetPositionAtOffset(offset + query.Length - last.Offset)!);
            range.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Gold);
            matches.Add(range);
            offset += query.Length;
        }
        return matches;
    }
}
