using HuiYi.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HuiYi.App;

/// A bounded, searchable editor keeps large meetings out of the records window's header.
public sealed class SpeakersWindow : Window
{
    public string SessionId { get; }
    private readonly TextBox search;
    private readonly StackPanel list = new();
    private sealed class SpeakerRow(int voice, Grid view, TextBox name, TextBlock label, TextBlock badge)
    {
        public int Voice = voice;
        public int Number;
        public Grid View = view;
        public TextBox Name = name;
        public TextBlock Label = label;
        public TextBlock Badge = badge;
        public string Saved = name.Text;
    }
    private readonly Dictionary<int, SpeakerRow> rows = [];
    private readonly AppModel model;
    private readonly TextBlock count;
    private bool closed;
    private readonly TextBlock empty;

    public SpeakersWindow(AppModel model, string sessionId)
    {
        SessionId = sessionId;
        this.model = model;
        // As tall as its people (two voices shouldn't sit over an empty page), scrolling past 70 % of the screen.
        Width = 540;
        SizeToContent = SizeToContent.Height;
        MinWidth = 420;
        MinHeight = 240;
        MaxHeight = SystemParameters.WorkArea.Height * .7;
        Background = Theme.Surface;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI, Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        UiText.Set(this, "Title", () => L.T("管理发言人"));
        var root = new DockPanel { Margin = new Thickness(22, 20, 22, 16) };
        var heading = new StackPanel();
        DockPanel.SetDock(heading, Dock.Top);
        count = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink };
        heading.Children.Add(count);
        heading.Children.Add(UiText.Bind(new TextBlock
        {
            FontSize = 12, Foreground = Theme.Grey, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16)
        }, ("Text", () => L.T("回车或离开输入框保存，留空恢复默认名称。"))));
        search = UiText.Bind(new TextBox { Style = (Style)FindResource("Field"), Margin = new Thickness(0, 0, 0, 14) },
            ("Tag", () => L.T("搜索发言人或姓名")));
        search.TextChanged += (_, _) => Filter();
        heading.Children.Add(search);
        root.Children.Add(heading);
        var close = UiText.Bind(new Button
        {
            Style = (Style)FindResource("Btn"), HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(22, 6, 22, 6)
        }, ("Content", () => L.T("关闭")));
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        empty = UiText.Bind(new TextBlock { Foreground = Theme.Grey, Margin = new Thickness(0, 20, 0, 0), Visibility = Visibility.Collapsed },
            ("Text", () => L.T("没有匹配的发言人")));
        list.Children.Add(empty);
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        RefreshSpeakers();
        model.Store.Changed += StoreChanged;
        PropertyChangedEventManager.AddHandler(L.Instance, LanguageChanged, "Item[]");
        Closing += (_, _) => { foreach (var row in rows.Values.ToList()) Save(row); };
        Closed += (_, _) => { closed = true; model.Store.Changed -= StoreChanged; };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void StoreChanged() => Dispatcher.BeginInvoke(new Action(() => { if (!closed) RefreshSpeakers(); }));

    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshSpeakers();

    private void RefreshSpeakers()
    {
        if (closed) return;
        var session = model.Store.Snapshot(SessionId) ?? new Session();
        var voices = SpeakerLabels.VoiceNumbers(session.Lines);
        var names = SpeakerLabels.Names(session);
        foreach (var voice in rows.Keys.Where(v => !voices.ContainsKey(v)).ToList())
        {
            list.Children.Remove(rows[voice].View);
            rows.Remove(voice);
        }
        var index = 1;
        foreach (var (voice, number) in voices.OrderBy(v => v.Value))
        {
            if (!rows.TryGetValue(voice, out var row))
            {
                row = CreateRow(voice, names.GetValueOrDefault(number, ""));
                rows.Add(voice, row);
            }
            var currentName = names.GetValueOrDefault(number, "");
            if (!row.Name.IsKeyboardFocusWithin && row.Name.Text.Trim() == row.Saved)
                row.Name.Text = row.Saved = currentName;
            row.Number = number;
            row.Badge.Text = number.ToString();
            ((Border)row.View.Children[0]).Background = Theme.Speaker(number);
            UiText.Set(row.Label, "Text", () => L.F("发言人 {0}", number));
            row.Name.ToolTip = session.VoiceNames?.ContainsKey(voice.ToString()) != true
                && session.VoiceGuesses?.GetValueOrDefault(voice.ToString()) is { } guess ? guess.Evidence : null;
            if (list.Children.IndexOf(row.View) != index)
            {
                list.Children.Remove(row.View);
                list.Children.Insert(index, row.View);
            }
            index++;
        }
        UiText.Set(count, "Text", () => L.F("发言人 · {0} 人", voices.Count));
        Filter();
    }

    private SpeakerRow CreateRow(int voice, string current)
    {
        var view = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        view.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        view.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        view.ColumnDefinitions.Add(new ColumnDefinition());
        var badge = new TextBlock { Foreground = System.Windows.Media.Brushes.White,
            FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        view.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
            HorizontalAlignment = HorizontalAlignment.Left, Child = badge });
        var label = new TextBlock { FontSize = 12, Foreground = Theme.Grey, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        view.Children.Add(label);
        var name = UiText.Bind(new TextBox
        {
            Style = (Style)FindResource("Field"), Text = current, FontSize = 13, VerticalAlignment = VerticalAlignment.Center
        }, ("Tag", () => L.T("起个名字")));
        Grid.SetColumn(name, 2);
        view.Children.Add(name);
        var row = new SpeakerRow(voice, view, name, label, badge);
        name.LostKeyboardFocus += (_, _) => { Save(row); Filter(); };
        name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Save(row);
            e.Handled = true;
        };
        return row;
    }

    private void Save(SpeakerRow row)
    {
        var session = model.Store.Snapshot(SessionId);
        if (session == null || !SpeakerLabels.VoiceNumbers(session.Lines).ContainsKey(row.Voice)) return;
        var value = row.Name.Text.Trim();
        if (value == row.Saved) return;
        row.Saved = value;
        model.RenameVoice(SessionId, row.Voice, value);
    }

    private void Filter()
    {
        var query = search.Text.Trim();
        foreach (var row in rows.Values)
            row.View.Visibility = row.Name.IsKeyboardFocusWithin || query.Length == 0 || row.Name.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
                || L.F("发言人 {0}", row.Number).Contains(query, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        empty.Visibility = rows.Values.All(r => r.View.Visibility == Visibility.Collapsed) ? Visibility.Visible : Visibility.Collapsed;
    }
}
