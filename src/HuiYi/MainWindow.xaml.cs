using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace HuiYi.App;

public sealed record SessionRow(string Id, string Title, string Meta, string Group);

/// The meeting records: every meeting with its notes and bilingual transcript. Rename, write the
/// notes again, export to Markdown, delete.
public partial class MainWindow : Window
{
    private readonly AppModel model;
    private readonly ObservableCollection<LineVM> lines = [];
    private string? selected;

    public MainWindow(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        Title = $"{Brand.Name} · 会议记录";
        Icon = Icons.Mark(64, false);
        Logo.Source = Icons.Mark(52, false);
        AppName.Text = Brand.Name;
        LinesList.ItemsSource = lines;
        model.Store.Changed += () => Dispatcher.BeginInvoke(RefreshList);
        model.NotesChanged += () =>
        {
            if (selected != null) ShowDetail(selected, keepTab: true);
        };
        Refresh();
    }

    public void Refresh()
    {
        ListenBtn.Content = model.IsListening ? "● 收听中 · 点击结束" : "开始收听";
        ListenBtn.Style = (Style)FindResource(model.IsListening ? "OnAirBtn" : "PrimaryBtn");
        RefreshList();
    }

    /// 今天 / 昨天 / 本周 / 更早, for the sidebar's group headings.
    private static string GroupOf(DateTime startedUtc)
    {
        var d = startedUtc.ToLocalTime().Date;
        var today = DateTime.Today;
        if (d == today) return "今天";
        if (d == today.AddDays(-1)) return "昨天";
        if (d > today.AddDays(-7)) return "最近 7 天";
        return d.Year == today.Year ? $"{d.Month} 月" : $"{d.Year} 年 {d.Month} 月";
    }

    private void RefreshList()
    {
        var q = SearchBox.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var rows = model.Store.All
            .Where(s => q.Length == 0 || s.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || s.Lines.Any(l => l.Src.Contains(q, StringComparison.OrdinalIgnoreCase) || (l.Dst?.Contains(q) ?? false)))
            .Select(s => new SessionRow(s.Id, (s.Id == model.LiveSessionId ? "● " : "") + s.DisplayTitle,
                $"{s.StartedAt.ToLocalTime():HH:mm} · {Minutes(s)} · {s.Lines.Count} 句", GroupOf(s.StartedAt)))
            .ToList();
        var keep = selected;
        var view = new System.Windows.Data.ListCollectionView(rows);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(SessionRow.Group)));
        SessionList.ItemsSource = view;
        EmptyText.Visibility = rows.Count == 0 && q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var row = rows.FirstOrDefault(r => r.Id == keep) ?? rows.FirstOrDefault();
        if (row == null)
        {
            Detail.Visibility = Visibility.Collapsed;
            return;
        }
        // A new list drops the selection; selecting the same meeting again keeps its tab.
        SessionList.SelectedItem = row;
        ShowDetail(row.Id, keepTab: true);
    }

    private static string Minutes(Session s)
    {
        var m = (int)Math.Round(s.Duration.TotalMinutes);
        return m < 1 ? "不到 1 分钟" : $"{m} 分钟";
    }

    private void Session_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionRow r) ShowDetail(r.Id, keepTab: false);
    }

    private void ShowDetail(string id, bool keepTab)
    {
        var s = model.Store.Get(id);
        if (s == null) { Detail.Visibility = Visibility.Collapsed; return; }
        var switched = selected != id;
        selected = id;
        Detail.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        if (!TitleBox.IsKeyboardFocused) TitleBox.Text = s.DisplayTitle;
        MetaText.Text = $"{s.StartedAt.ToLocalTime():yyyy年M月d日 HH:mm} · 时长 {Minutes(s)} · {s.Lines.Count} 句{(s.Id == model.LiveSessionId ? " · 正在记录" : "")}";
        if (switched) lines.Clear();
        if (model.Store.Snapshot(id) is { } snap)
        {
            LineVM.Sync(lines, snap);
            RenderSpeakers(snap, switched);
        }
        RenderTemplateBox(s);
        var generating = model.Notes.GeneratingId == id;
        NotesBtn.Content = generating ? "整理中…" : s.Notes is { IsEmpty: false } ? "重新整理纪要" : "生成纪要";
        NotesBtn.IsEnabled = !generating && s.Id != model.LiveSessionId;
        NotesState.Text = generating ? "AI 正在通读整场会议…" : s.Notes?.UpdatedAt is { } at ? $"纪要更新于 {at.ToLocalTime():M月d日 HH:mm}" : "";
        NotesHost.Content = s.Notes is { IsEmpty: false } n
            ? NotesRenderer.Build(n, s.StartedAt, 14, cards: true)
            : new Border
            {
                Style = (Style)FindResource("Panel"),
                Child = new TextBlock
                {
                    Style = (Style)FindResource("Helper"), FontSize = 13, Margin = new Thickness(0),
                    Text = model.Ai.HasKey ? "这场会议还没有纪要，点右上角「生成纪要」。"
                                             : "在「设置 › AI 服务」里接入一个大模型后可以生成纪要。完整的双语内容在「逐字稿」里。",
                },
            };
        if (!keepTab && switched)
        {
            if (s.Notes is { IsEmpty: false }) TabNotes.IsChecked = true;
            else TabLines.IsChecked = true;
        }
    }

    /// 发言人 1、发言人 2…, each with its name: type one to name that voice in this meeting (empty: back
    /// to the number). The AI's guesses show as the name until the user changes it.
    private void RenderSpeakers(Session s, bool rebuild)
    {
        var voices = SpeakerLabels.VoiceNumbers(s.Lines).OrderBy(kv => kv.Value).ToList();
        // Don't pull the box away from someone typing a name.
        if (!rebuild && SpeakersPanel.IsKeyboardFocusWithin) return;
        SpeakersPanel.Children.Clear();
        SpeakersPanel.Visibility = voices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var names = SpeakerLabels.Names(s);
        foreach (var (voice, number) in voices)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 4) };
            row.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Theme.Speaker(number), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = $"发言人 {number}", Foreground = Theme.Grey2, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            var guess = s.VoiceNames?.ContainsKey(voice.ToString()) == true ? null : s.VoiceGuesses?.GetValueOrDefault(voice.ToString());
            var box = new TextBox
            {
                Text = names.GetValueOrDefault(number, ""), Width = 110, Padding = new Thickness(4, 2, 4, 2), FontSize = 12,
                BorderBrush = Theme.Line, ToolTip = guess != null ? $"AI 根据「{guess.Evidence}」猜的名字，可以改" : "输入名字，回车保存",
            };
            void Save()
            {
                var t = box.Text.Trim();
                if (t != names.GetValueOrDefault(number, "")) model.Store.SetVoiceName(s.Id, voice, t);
            }
            box.LostFocus += (_, _) => Save();
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                Save();
                Keyboard.ClearFocus();
            };
            row.Children.Add(box);
            SpeakersPanel.Children.Add(row);
        }
    }

    public void ShowTab(bool notes)
    {
        TabNotes.IsChecked = notes;
        TabLines.IsChecked = !notes;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (NotesScroll == null) return;
        var notes = TabNotes.IsChecked == true;
        NotesScroll.Visibility = notes ? Visibility.Visible : Visibility.Collapsed;
        LinesScroll.Visibility = notes ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => RefreshList();

    private async void Listen_Click(object sender, RoutedEventArgs e)
    {
        await ((App)Application.Current).ToggleListening();
        Refresh();
    }

    private bool fillingTemplates;

    /// The meeting's template: its own pick, else the default (marked so).
    private void RenderTemplateBox(Session s)
    {
        if (TemplateBox.IsDropDownOpen) return;
        fillingTemplates = true;
        TemplateBox.Items.Clear();
        var chosen = model.TemplateFor(s).Id;
        foreach (var t in model.Templates)
        {
            var item = new ComboBoxItem
            {
                Content = t.Id == model.Settings.DefaultTemplateId ? $"{t.Name}（默认）" : t.Name, Tag = t.Id, ToolTip = t.Description,
            };
            TemplateBox.Items.Add(item);
            if (t.Id == chosen) TemplateBox.SelectedItem = item;
        }
        fillingTemplates = false;
    }

    /// A new template for this meeting: rewrite its notes in that shape (a live meeting picks it up
    /// at its next update).
    private void Template_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (fillingTemplates || selected == null || TemplateBox.SelectedItem is not ComboBoxItem { Tag: string tid }) return;
        var s = model.Store.Get(selected);
        if (s == null || model.TemplateFor(s).Id == tid) return;
        model.Store.SetTemplate(selected, tid == model.Settings.DefaultTemplateId ? null : tid);
        if (selected == model.LiveSessionId)
        {
            NotesState.Text = "下一次更新起按新模板整理";
            model.Notes.UpdateNow();
            return;
        }
        if (model.Ai.HasKey && s.Lines.Count > 0)
        {
            model.Notes.Generate(selected);
            ShowDetail(selected, keepTab: true);
            TabNotes.IsChecked = true;
        }
    }

    private void Templates_Click(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).OpenTemplates(selected is { } id ? model.TemplateFor(model.Store.Get(id)).Id : null);

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (selected == null) return;
        if (!model.Ai.HasKey)
        {
            ((App)Application.Current).OpenSettings();
            return;
        }
        model.Notes.Generate(selected);
        ShowDetail(selected, keepTab: true);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (selected == null || model.Store.Get(selected) is not { } s) return;
        var name = string.Concat(s.DisplayTitle.Split(Path.GetInvalidFileNameChars()));
        var dlg = new SaveFileDialog { FileName = name + ".md", Filter = "Markdown|*.md|文本|*.txt" };
        if (dlg.ShowDialog(this) == true)
        {
            File.WriteAllText(dlg.FileName, TranscriptExport.Markdown(s));
            Native.Open(Path.GetDirectoryName(dlg.FileName)!);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (selected == null || selected == model.LiveSessionId) return;
        if (MessageBox.Show(this, "确定删除这场会议？逐字稿和纪要会一起删掉，无法恢复。", Brand.Name, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        model.Store.Delete(selected);
        selected = null;
        RefreshList();
    }

    private void Title_LostFocus(object sender, RoutedEventArgs e) => SaveTitle();

    private void Title_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveTitle();
        Keyboard.ClearFocus();
    }

    private void SaveTitle()
    {
        if (selected == null || model.Store.Get(selected) is not { } s) return;
        var t = TitleBox.Text.Trim();
        if (t.Length > 0 && t != s.DisplayTitle) model.Store.Rename(selected, t);
    }
}
