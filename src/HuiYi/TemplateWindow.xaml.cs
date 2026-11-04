using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HuiYi.App;

/// 模板中心: the notes templates. Built-in ones can be read, set as default or copied; the user's own
/// can be changed in every part (name, focus, options, sections).
public partial class TemplateWindow : Window
{
    private static readonly (string Style, string Name)[] Styles =
        [("dot", "普通列表"), ("check", "已确定（对勾）"), ("todo", "待办（方框）"), ("warn", "风险 / 问题（提醒）")];

    private readonly AppModel model;
    private NoteTemplate current;
    /// The user's template being edited (a working copy until 保存).
    private NoteTemplate? draft;

    public TemplateWindow(AppModel model, string? select = null)
    {
        this.model = model;
        InitializeComponent();
        Title = $"{Brand.Name} · 模板中心";
        Icon = Icons.Mark(64, false);
        current = NoteTemplates.Find(select ?? model.Settings.DefaultTemplateId, model.MyTemplates);
        RenderList();
        Show(current);
    }

    // MARK: List

    private void RenderList()
    {
        ListHost.Children.Clear();
        foreach (var group in model.Templates.GroupBy(t => t.Category))
        {
            ListHost.Children.Add(new TextBlock
            {
                Text = group.Key, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Theme.Grey2, Margin = new Thickness(6, 12, 0, 4),
            });
            foreach (var t in group) ListHost.Children.Add(Row(t));
        }
    }

    private UIElement Row(NoteTemplate t)
    {
        var selected = t.Id == current.Id;
        var name = new DockPanel();
        if (t.Id == model.Settings.DefaultTemplateId)
        {
            var badge = new Border
            {
                Background = Theme.PrimarySoft, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1),
                Child = new TextBlock { Text = "默认", FontSize = 10, Foreground = Theme.Primary, FontWeight = FontWeights.SemiBold },
            };
            DockPanel.SetDock(badge, Dock.Right);
            name.Children.Add(badge);
        }
        name.Children.Add(new TextBlock { Text = t.Name, FontSize = 13, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
        var body = new StackPanel();
        body.Children.Add(name);
        body.Children.Add(new TextBlock
        {
            Text = t.Description.Length > 0 ? t.Description : $"{t.Sections.Count} 个部分", FontSize = 11, Foreground = Theme.Grey2,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0),
        });
        var row = new Border
        {
            Background = selected ? Theme.PrimarySoft : Brushes.Transparent, CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 1, 6, 1), Cursor = Cursors.Hand, Child = body,
        };
        row.MouseEnter += (_, _) => { if (t.Id != current.Id) row.Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF6, 0xFA)); };
        row.MouseLeave += (_, _) => { if (t.Id != current.Id) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, _) => Show(t);
        return row;
    }

    // MARK: Detail

    private void Show(NoteTemplate t)
    {
        current = t;
        draft = t.BuiltIn ? null : Clone(t);
        var mine = draft != null;
        NameText.Text = t.Name;
        NameText.Visibility = mine ? Visibility.Collapsed : Visibility.Visible;
        NameBox.Text = t.Name;
        NameBox.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
        CategoryText.Text = t.BuiltIn ? $"内置 · {t.Category}" : "我的模板";
        SavedText.Text = "";
        var isDefault = t.Id == model.Settings.DefaultTemplateId;
        DefaultBtn.Content = isDefault ? "✓ 当前默认" : "设为默认";
        DefaultBtn.IsEnabled = !isDefault;
        // Only one filled button at a time: 保存 when editing your own, 设为默认 on a built-in one.
        DefaultBtn.Style = (Style)FindResource(mine ? "Btn" : "PrimaryBtn");
        SaveBtn.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
        DeleteBtn.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
        CopyBtn.Content = mine ? "复制一份" : "复制为我的模板";
        RenderList();
        RenderDetail();
    }

    private static NoteTemplate Clone(NoteTemplate t) => new()
    {
        Id = t.Id, Name = t.Name, Category = t.Category, Description = t.Description, Focus = t.Focus,
        Topics = t.Topics, Fishbone = t.Fishbone, Terms = t.Terms, Sections = t.Sections.Select(s => s.Clone()).ToList(),
    };

    private void RenderDetail()
    {
        DetailHost.Children.Clear();
        var t = draft ?? current;
        var mine = draft != null;

        // About
        var about = Card("说明");
        if (mine) about.Children.Add(Label("适用场景", 2));
        about.Children.Add(mine ? Field(t.Description, v => t.Description = v, "一句话说明这个模板适合什么会议")
                                : Body(t.Description));
        about.Children.Add(mine ? Label("关注重点", 14, "告诉 AI 这类会议最该抓住什么") : Small("关注重点（告诉 AI 这类会议最该抓住什么）", 12));
        about.Children.Add(mine ? Field(t.Focus, v => t.Focus = v, "例如：每条风险都要写清影响和负责人", multi: true)
                                : Body(t.Focus.Length > 0 ? t.Focus : "—", Theme.Grey));

        // Options
        var opts = Card("固定部分");
        opts.Children.Add(Body("概要总是会写；以下部分可以选。", Theme.Grey2, 12));
        opts.Children.Add(Toggle("议题时间线", "按时间列出议题和要点，每条标出在会上的真实时间", t.Topics, v => t.Topics = v, mine));
        opts.Children.Add(Toggle("原因分析", "讨论原因时整理成问题和几条原因", t.Fishbone, v => t.Fishbone = v, mine));
        opts.Children.Add(Toggle("人名与术语", "纠正识别错的人名，解释陌生术语", t.Terms, v => t.Terms = v, mine));

        // Sections
        var parts = Card($"纪要包含 · {t.Sections.Count} 个部分");
        for (var i = 0; i < t.Sections.Count; i++) parts.Children.Add(SectionRow(t, i, mine));
        if (mine)
        {
            var add = new Button { Content = "＋ 添加一个部分", Style = (Style)FindResource("Btn"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            add.Click += (_, _) =>
            {
                var n = 1;
                while (t.Sections.Any(s => s.Key == $"part{n}")) n++;
                t.Sections.Add(new TemplateSection { Key = $"part{n}", Title = "新的部分", Instruction = "要写什么，一句话说清" });
                RenderDetail();
            };
            parts.Children.Add(add);
        }
        DetailHost.Children.Add(new TextBlock
        {
            Text = "纪要里所有时间都写真实时间：议题和要点标会上的钟点（如 14:05），截止日期、里程碑、下次会议换算成具体日期（如 11月6日（周三））。",
            Style = (Style)FindResource("Helper"), Margin = new Thickness(2, 0, 0, 12),
        });
    }

    private UIElement SectionRow(NoteTemplate t, int index, bool mine)
    {
        var s = t.Sections[index];
        var box = new StackPanel();
        if (!mine)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = s.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            foreach (var tag in Tags(s)) head.Children.Add(TagChip(tag));
            box.Children.Add(head);
            box.Children.Add(Body(s.Instruction, Theme.Grey, 12, top: 3));
        }
        else
        {
            var head = new DockPanel();
            var del = new Button { Content = "删除", Style = (Style)FindResource("DeleteBtn"), Padding = new Thickness(8, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            del.Click += (_, _) => { t.Sections.RemoveAt(index); RenderDetail(); };
            DockPanel.SetDock(del, Dock.Right);
            head.Children.Add(del);
            var up = new Button { Content = "↑", Style = (Style)FindResource("GhostBtn"), Foreground = Theme.Grey2, IsEnabled = index > 0, ToolTip = "上移" };
            up.Click += (_, _) => { (t.Sections[index - 1], t.Sections[index]) = (t.Sections[index], t.Sections[index - 1]); RenderDetail(); };
            DockPanel.SetDock(up, Dock.Right);
            head.Children.Add(up);
            var title = Field(s.Title, v => s.Title = v, "这一部分的标题", width: 260, strong: true);
            title.Background = Brushes.White;
            head.Children.Add(title);
            box.Children.Add(head);
            var what = Field(s.Instruction, v => s.Instruction = v, "AI 在这里写什么（一句话）", top: 8);
            what.Background = Brushes.White;
            box.Children.Add(what);
            var opts = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            var style = new ComboBox { MinWidth = 170, Margin = new Thickness(0, 0, 16, 0) };
            foreach (var (key, name) in Styles)
            {
                var item = new ComboBoxItem { Content = name, Tag = key };
                style.Items.Add(item);
                if (key == s.Style) style.SelectedItem = item;
            }
            style.SelectionChanged += (_, _) => s.Style = (style.SelectedItem as ComboBoxItem)?.Tag as string ?? "dot";
            opts.Children.Add(style);
            opts.Children.Add(Switch("带负责人", s.Owner, v => s.Owner = v));
            opts.Children.Add(Switch("带截止日期", s.Due, v => s.Due = v));
            box.Children.Add(opts);
        }
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFB)), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 12, 12), Margin = new Thickness(0, 8, 0, 0), Child = box,
        };
    }

    private static IEnumerable<string> Tags(TemplateSection s)
    {
        if (s.Style == "todo") yield return "待办";
        if (s.Style == "check") yield return "已确定";
        if (s.Style == "warn") yield return "需关注";
        if (s.Owner) yield return "负责人";
        if (s.Due) yield return "截止日期";
    }

    // MARK: Pieces

    private StackPanel Card(string title)
    {
        var inner = new StackPanel();
        inner.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Overline") });
        DetailHost.Children.Add(new Border { Style = (Style)FindResource("Panel"), Child = inner });
        return inner;
    }

    private static TextBlock Body(string text, Brush? brush = null, double size = 14, double top = 0) => new()
    {
        Text = text, FontSize = size, Foreground = brush ?? Theme.Ink, TextWrapping = TextWrapping.Wrap,
        LineHeight = Math.Round(size * 1.6), LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Margin = new Thickness(0, top, 0, 0),
    };

    private static TextBlock Small(string text, double top) => new()
    {
        Text = text, FontSize = 12, Foreground = Theme.Grey2, Margin = new Thickness(0, top, 0, 4),
    };

    private static UIElement TagChip(string text) => new Border
    {
        Background = Theme.PrimarySoft, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 0, 7, 1), Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = Theme.Primary },
    };

    private static UIElement Label(string text, double top, string? help = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, top, 0, 6) };
        row.Children.Add(new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink });
        if (help != null)
            row.Children.Add(new TextBlock { Text = help, FontSize = 12, Foreground = Theme.Grey2, Margin = new Thickness(8, 1, 0, 0) });
        return row;
    }

    private TextBox Field(string value, Action<string> set, string hint, bool multi = false, double width = double.NaN, double top = 0, bool strong = false)
    {
        var box = new TextBox
        {
            Style = (Style)FindResource("Field"), Tag = hint,
            Text = value, Width = width, Margin = new Thickness(0, top, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
            TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap, AcceptsReturn = multi, MinHeight = multi ? 64 : 0,
            VerticalContentAlignment = multi ? VerticalAlignment.Top : VerticalAlignment.Center,
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        };
        if (!double.IsNaN(width)) box.HorizontalAlignment = HorizontalAlignment.Left;
        box.TextChanged += (_, _) => set(box.Text);
        return box;
    }

    private CheckBox Switch(string label, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = label, IsChecked = value, Style = (Style)FindResource("Switch"), Margin = new Thickness(0, 4, 18, 0), FontSize = 13 };
        c.Click += (_, _) => set(c.IsChecked == true);
        return c;
    }

    private UIElement Toggle(string title, string help, bool value, Action<bool> set, bool editable)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var sw = new CheckBox { IsChecked = value, Style = (Style)FindResource("Switch"), IsEnabled = editable, Margin = new Thickness(0) };
        sw.Click += (_, _) => set(sw.IsChecked == true);
        DockPanel.SetDock(sw, Dock.Right);
        row.Children.Add(sw);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        text.Children.Add(new TextBlock { Text = help, FontSize = 12, Foreground = Theme.Grey2, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        row.Children.Add(text);
        return row;
    }

    /// Developer snapshot: show the current template as an editable copy (in the snapshot's own data).
    public void CopyForSnapshot() => Copy_Click(this, new RoutedEventArgs());

    // MARK: Actions

    private void Default_Click(object sender, RoutedEventArgs e)
    {
        model.Settings.DefaultTemplateId = current.Id;
        model.SaveSettings();
        Show(current);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var source = draft ?? current;
        var copy = source.CopyAsMine(source.BuiltIn ? source.Name + "（我的）" : source.Name + " 副本");
        model.MyTemplates.Add(copy);
        model.SaveTemplates();
        Show(copy);
        SavedText.Text = "已复制，可以修改了";
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var blank = new NoteTemplate
        {
            Id = "my-" + Guid.NewGuid().ToString("N")[..8], Name = "新模板", Description = "",
            Sections =
            [
                new() { Key = "conclusions", Title = "决策与结论", Style = "check", Instruction = "会上明确定下来的事项，每条一句" },
                new() { Key = "todos", Title = "待办事项", Style = "todo", Owner = true, Due = true, Instruction = "有人认领的具体行动，写清负责人和截止日期" },
            ],
        };
        model.MyTemplates.Add(blank);
        model.SaveTemplates();
        Show(blank);
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (draft == null) return;
        draft.Name = NameBox.Text.Trim() is { Length: > 0 } n ? n : "未命名模板";
        // Every part needs a title and a key the AI can fill.
        draft.Sections = draft.Sections.Where(s => s.Title.Trim().Length > 0).ToList();
        var i = model.MyTemplates.FindIndex(t => t.Id == draft.Id);
        if (i >= 0) model.MyTemplates[i] = draft;
        model.SaveTemplates();
        var saved = draft;
        Show(saved);
        SavedText.Text = "✓ 已保存，之后整理纪要就按它来";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (draft == null) return;
        if (!Dialog.Confirm(this, $"删除模板「{current.Name}」？", "正在用这个模板的会议会改用默认模板，已经写好的纪要不受影响。", "删除", danger: true)) return;
        model.MyTemplates.RemoveAll(t => t.Id == current.Id);
        model.SaveTemplates();
        Show(NoteTemplates.Find(model.Settings.DefaultTemplateId, model.MyTemplates));
    }
}
