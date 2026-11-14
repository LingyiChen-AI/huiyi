using HuiYi.Localization;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace HuiYi.App;

/// The meeting records: every meeting with its notes and bilingual transcript. Rename, write the
/// notes again, export to Markdown, delete.
public partial class MainWindow : Window
{
    private readonly AppModel model;
    private readonly ObservableCollection<LineVM> lines = [];
    private string? selected;
    private bool refreshingList;
    private int searchRevision;
    private readonly List<(SelectableText Box, System.Windows.Documents.TextRange Range, bool Notes)> searchMatches = [];
    private int searchIndex = -1;
    private string appliedQuery = "";
    private string? appliedSession;
    private readonly System.Windows.Threading.DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };

    public MainWindow(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); RefreshList(); };
        Closed += (_, _) => searchTimer.Stop();
        UiText.Set(this, "Title", () => L.F("{0} · 会议记录", L.T("会译")));
        Icon = Icons.Mark(64, false);
        Logo.Source = Icons.Mark(52, false);
        AppName.Text = Brand.Name;
        // Roomy by default, but never bigger than the screen it opens on.
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width * 0.92);
        Height = Math.Min(Height, area.Height * 0.92);
        LinesList.ItemsSource = lines;
        PropertyChangedEventManager.AddHandler(L.Instance, LanguageChanged, "Item[]");
        model.Store.Changed += () => Dispatcher.BeginInvoke(RefreshList);
        model.NotesChanged += RefreshSelectedDetails;
        // 加入词库 buttons in the notes show what the glossary knows.
        model.Glossary.Changed += () => Dispatcher.BeginInvoke(RefreshSelectedDetails);
        Refresh();
    }

    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    public void Refresh()
    {
        AppName.Text = Brand.Name;
        UiText.Set(ListenBtn, "Content", () => model.IsListening ? L.T("● 收听中 · 点击结束") : L.T("开始收听"));
        ListenBtn.Style = (Style)FindResource(model.IsListening ? "OnAirBtn" : "PrimaryBtn");
        LiveBtn.Visibility = model.IsListening ? Visibility.Visible : Visibility.Collapsed;
        TranslateOn.IsChecked = model.Settings.TranslationOn;
        RefreshList();
    }

    private void Live_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowLive();

    private void Settings_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenSettings();

    private void Translate_Click(object sender, RoutedEventArgs e) => model.SetTranslation(TranslateOn.IsChecked == true);

    private void RefreshList()
    {
        var q = SearchBox.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var rows = model.Store.All
            .Where(InLibraryScope)
            .Where(s => SessionSearch.Matches(s, q) || (q.Length > 0 && DisplayTitle(s).Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Select(s => new SessionRow(s.Id, (s.Id == model.LiveSessionId ? "● " : "") + DisplayTitle(s),
                L.F("{0:MM-dd HH:mm} · {1} · {2} 句", s.StartedAt.ToLocalTime(), Minutes(s), s.Lines.Count))
                { Archived = model.Store.IsArchived(s.Id) })
            .ToList();
        var keep = selected;
        refreshingList = true;
        BuildMeetingTree(rows, q);
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderLibraryEmpty(q);
        var row = rows.FirstOrDefault(r => r.Id == keep) ?? rows.FirstOrDefault();
        if (row == null)
        {
            selected = null;
            lines.Clear();
            refreshingList = false;
            searchRevision++;
            foreach (var box in searchMatches.Select(m => m.Box).Distinct()) box.Highlight("");
            searchMatches.Clear();
            SearchNavigation.Visibility = Visibility.Collapsed;
            Detail.Visibility = Visibility.Collapsed;
            return;
        }
        // A new list drops the selection; selecting the same meeting again keeps its tab.
        row.IsSelected = true;
        refreshingList = false;
        ShowDetail(row.Id, keepTab: true);
    }

    private static string Minutes(Session s)
    {
        var m = (int)Math.Round(s.Duration.TotalMinutes);
        return m < 1 ? L.T("不到 1 分钟") : L.F("{0} 分钟", m);
    }

    private void Session_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!refreshingList && e.NewValue is SessionRow r) ShowDetail(r.Id, keepTab: false);
    }

    private void ShowDetail(string id, bool keepTab)
    {
        var s = model.Store.Get(id);
        if (s == null) { Detail.Visibility = Visibility.Collapsed; return; }
        var switched = selected != id;
        if (switched) LibraryError.Visibility = Visibility.Collapsed;
        selected = id;
        Detail.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        if (!TitleBox.IsKeyboardFocused) TitleBox.Text = DisplayTitle(s);
        UiText.Set(MetaText, "Text", () => L.F("{0:yyyy年M月d日 HH:mm} · 时长 {1} · {2} 句{3}", s.StartedAt.ToLocalTime(), Minutes(s), s.Lines.Count, (s.Id == model.LiveSessionId ? " · 正在记录" : "")) + LibraryMeta(s.Id));
        if (switched) lines.Clear();
        if (model.Store.Snapshot(id) is { } snap)
        {
            LineVM.Sync(lines, snap);
            RenderSpeakers(snap);
        }
        RenderTemplateBox(s);
        var generating = model.Notes.GeneratingId == id;
        UiText.Set(NotesBtn, "Content", () => generating ? L.T("整理中…") : s.Notes is { IsEmpty: false } ? L.T("重新整理纪要") : L.T("生成纪要"));
        NotesBtn.IsEnabled = !generating && s.Id != model.LiveSessionId;
        UiText.Set(NotesBtn, "ToolTip", () => generating ? L.T("AI 正在通读整场会议…") : s.Notes?.UpdatedAt is { } at ? L.F("纪要更新于 {0:M月d日 HH:mm}", at.ToLocalTime()) : "");
        NotesHost.Content = s.Notes is { IsEmpty: false } n
            ? NotesRenderer.Build(n, s.StartedAt, 14, cards: true, names: SpeakerLabels.Names(s), localizeHeadings: model.TemplateFor(s).BuiltIn,
                termExtra: LearnButton)
            : new Border
            {
                Style = (Style)FindResource("Panel"),
                Child = UiText.Bind(new TextBlock
                {
                    Style = (Style)FindResource("Helper"), FontSize = 13, Margin = new Thickness(0),
                    Text = model.Ai.HasKey ? L.T("这场会议还没有纪要，点右上角「生成纪要」。")
                                             : L.T("在「设置 › AI 服务」里接入一个大模型后可以生成纪要。完整的双语内容在「逐字稿」里。"),
                }, ("Text", () => model.Ai.HasKey ? L.T("这场会议还没有纪要，点右上角「生成纪要」。")
                                             : L.T("在「设置 › AI 服务」里接入一个大模型后可以生成纪要。完整的双语内容在「逐字稿」里。"))),
            };
        if (!keepTab && switched)
        {
            if (s.Notes is { IsEmpty: false }) TabNotes.IsChecked = true;
            else TabLines.IsChecked = true;
        }
        QueueSearch();
    }

    private SpeakersWindow? speakersWindow;

    private void RenderSpeakers(Session session)
    {
        var voices = SpeakerLabels.VoiceNumbers(session.Lines);
        SpeakersPanel.Visibility = voices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UiText.Set(SpeakersCount, "Text", () => L.F("发言人 · {0} 人", voices.Count));
        var names = SpeakerLabels.Names(session);
        UiText.Set(SpeakersPreview, "Text", () => string.Join(" · ", voices.OrderBy(v => v.Value).Take(3)
            .Select(v => names.GetValueOrDefault(v.Value) ?? L.F("发言人 {0}", v.Value)))
            + (voices.Count > 3 ? $"  +{voices.Count - 3}" : ""));
    }

    private void Speakers_Click(object sender, RoutedEventArgs e)
    {
        if (selected == null) return;
        if (speakersWindow is { IsVisible: true } open && open.SessionId == selected)
        {
            open.Activate();
            return;
        }
        speakersWindow?.Close();
        speakersWindow = new SpeakersWindow(model, selected) { Owner = this };
        speakersWindow.Show();
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

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        searchTimer.Stop();
        if (SearchBox.Text.Trim().Length == 0) RefreshList();
        else searchTimer.Start();
    }

    private void QueueSearch()
    {
        var revision = ++searchRevision;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (revision != searchRevision) return;
            var query = SearchBox.Text.Trim();
            var relocate = query != appliedQuery || selected != appliedSession;
            var previousIndex = searchIndex;
            if (query.Length == 0)
            {
                foreach (var box in searchMatches.Select(m => m.Box).Distinct()) box.Highlight("");
                searchMatches.Clear();
                searchIndex = -1;
                appliedQuery = query;
                appliedSession = selected;
                SearchNavigation.Visibility = Visibility.Collapsed;
                return;
            }
            searchMatches.Clear();
            searchIndex = -1;
            // Generate transcript containers even when the notes tab is currently displayed.
            var previous = LinesScroll.Visibility;
            LinesScroll.Visibility = Visibility.Visible;
            LinesScroll.UpdateLayout();
            foreach (var box in SelectableText.Descendants(NotesHost))
                foreach (var range in box.Highlight(query)) searchMatches.Add((box, range, true));
            foreach (var box in SelectableText.Descendants(LinesList))
                foreach (var range in box.Highlight(query)) searchMatches.Add((box, range, false));
            LinesScroll.Visibility = previous;
            appliedQuery = query;
            appliedSession = selected;
            SearchNavigation.Visibility = Visibility.Visible;
            if (searchMatches.Count > 0 && relocate) MoveSearch(1);
            else
            {
                searchIndex = searchMatches.Count == 0 ? -1 : Math.Clamp(previousIndex, 0, searchMatches.Count - 1);
                SearchCount.Text = searchMatches.Count == 0 ? "0 / 0" : $"{searchIndex + 1} / {searchMatches.Count}";
            }
        }));
    }

    private void MoveSearch(int direction)
    {
        if (searchMatches.Count == 0) return;
        searchIndex = (searchIndex + direction + searchMatches.Count) % searchMatches.Count;
        var match = searchMatches[searchIndex];
        ShowTab(match.Notes);
        UpdateLayout();
        var rect = match.Range.Start.GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward);
        if (rect.IsEmpty) match.Box.BringIntoView();
        else match.Box.BringIntoView(rect);
        SearchCount.Text = $"{searchIndex + 1} / {searchMatches.Count}";
    }

    private void PreviousMatch_Click(object sender, RoutedEventArgs e) => MoveSearch(-1);
    private void NextMatch_Click(object sender, RoutedEventArgs e) => MoveSearch(1);

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (searchTimer.IsEnabled)
        {
            searchTimer.Stop();
            RefreshList();
            e.Handled = true;
            return;
        }
        MoveSearch(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
        e.Handled = true;
    }

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
            var item = UiText.Bind(new ComboBoxItem
            {
                Content = t.Id == model.Settings.DefaultTemplateId ? L.F("{0}（默认）", t.BuiltIn ? L.T(t.Name) : t.Name) : t.BuiltIn ? L.T(t.Name) : t.Name, Tag = t.Id, ToolTip = t.BuiltIn ? L.T(t.Description) : t.Description,
            }, ("Content", () => t.Id == model.Settings.DefaultTemplateId ? L.F("{0}（默认）", t.BuiltIn ? L.T(t.Name) : t.Name) : t.BuiltIn ? L.T(t.Name) : t.Name));
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
            UiText.Set(NotesBtn, "ToolTip", () => L.T("下一次更新起按新模板整理"));
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

    private void Glossary_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenGlossary();

    /// Under a name or term the AI found misheard: add it to the glossary, or say it is there.
    private UIElement? LearnButton(NoteTerm t)
    {
        if (t.Heard.Count == 0 || t.Name.Trim().Length == 0) return null;
        if (model.Glossary.Knows(t.Name, t.Heard))
            return new TextBlock { Text = L.T("✓ 已在词库"), FontSize = 11, Foreground = Theme.OnAir, Margin = new Thickness(0, 4, 0, 0) };
        var b = new Button
        {
            Content = L.T("＋ 加入词库"), Style = (Style)FindResource("GhostBtn"), FontSize = 11, Padding = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-2, 2, 0, 0),
            ToolTip = L.F("以后识别成「{0}」时自动改成「{1}」", string.Join("、", t.Heard), t.Name),
        };
        b.Click += (_, _) =>
        {
            model.Glossary.Learn(t.Name, t.Heard, t.Note);
            model.Say(L.F("「{0}」已加入词库", t.Name));
        };
        return b;
    }

    /// Right-click on a transcript line: copy, or correct the selected word and add it to the glossary.
    private void Src_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not SelectableText box) return;
        var picked = box.Selection.Text.Trim();
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = L.T("复制"), Command = ApplicationCommands.Copy, CommandTarget = box });
        var usable = picked.Length is > 0 and <= 30 && !picked.Contains('\n');
        var fix = new MenuItem
        {
            Header = usable ? L.F("改正「{0}」并加入词库…", picked) : L.T("选中认错的字，可以改正并加入词库"),
            IsEnabled = usable,
        };
        fix.Click += (_, _) => FixWord(picked);
        menu.Items.Add(fix);
        box.ContextMenu = menu;
    }

    private void FixWord(string heard)
    {
        if (selected is not { } id || model.Store.Get(id) is not { } s) return;
        var count = s.Lines.Sum(l => Glossary.CountOf(l.Src, heard));
        if (TermFixWindow.Ask(this, heard, count) is not { } answer) return;
        if (answer.Text == heard)
        {
            // Written right already: keep the spelling, so sound-alikes are corrected to it.
            model.Glossary.Learn(heard, []);
            model.Say(L.F("「{0}」已加入词库", heard));
            return;
        }
        model.Glossary.Learn(answer.Text, [heard]);
        var changed = answer.Everywhere ? model.CorrectMeeting(id, heard, answer.Text) : 0;
        model.Say(changed > 0 ? L.F("已加入词库，这场会议改正了 {0} 处", changed) : L.F("「{0}」已加入词库", answer.Text));
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
        if (selected == null || sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (label, extension) in new[] { ("Word 文档 (.docx)", ".docx"), ("Markdown (.md)", ".md"), ("文本 (.txt)", ".txt") })
        {
            var item = UiText.Bind(new MenuItem(), ("Header", () => L.T(label)));
            item.Click += (_, _) => ExportTo(extension);
            menu.Items.Add(item);
        }
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void ExportTo(string extension)
    {
        if (selected == null || model.Store.Snapshot(selected) is not { } s) return;
        var name = string.Concat(s.DisplayTitle.Split(Path.GetInvalidFileNameChars()));
        var filter = extension == ".docx" ? L.T("Word 文档 (.docx)") : extension == ".md" ? "Markdown (.md)" : L.T("文本 (.txt)");
        var dlg = new SaveFileDialog { FileName = name + extension, Filter = filter + "|*" + extension, DefaultExt = extension, AddExtension = true };
        if (dlg.ShowDialog(this) != true) return;
        var temporary = dlg.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (extension == ".docx") File.WriteAllBytes(temporary, WordExport.Create(s));
            else File.WriteAllText(temporary, TranscriptExport.Markdown(s));
            File.Move(temporary, dlg.FileName, overwrite: true);
            Native.Open(Path.GetDirectoryName(dlg.FileName)!);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Debug($"export: failed {error.GetType().Name}");
            Dialog.Confirm(this, L.T("导出失败"), L.T("请检查文件是否正被占用，以及保存位置是否可写。"), L.T("确定"));
        }
        finally { if (File.Exists(temporary)) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }

    private void DeleteSession(string id)
    {
        if (id == model.Store.LiveId || model.Store.Get(id) is not { } session) return;
        if (!Dialog.Confirm(this, L.F("删除「{0}」？", session.DisplayTitle), L.T("逐字稿和纪要会一起删掉，删除后无法恢复。"), L.T("删除"), danger: true)) return;
        LibraryAction(() => model.Store.Delete(id));
        if (selected == id && model.Store.Get(id) == null) selected = null;
        RefreshList();
    }

    private void HeaderToolbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (NotesTools == null) return;
        var compact = e.NewSize.Width < 650;
        Grid.SetRow(NotesTools, compact ? 1 : 0);
        Grid.SetColumn(NotesTools, compact ? 0 : 1);
        Grid.SetColumnSpan(NotesTools, compact ? 2 : 1);
        NotesTools.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(0);
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
        if (t.Length > 0 && t != DisplayTitle(s)) model.Store.Rename(selected, t);
    }

    private static string DisplayTitle(Session session)
    {
        if ((session.Renamed ?? !session.Title.StartsWith("会议 ")) || L.Language == "zh-CN") return session.DisplayTitle;
        var date = session.StartedAt.ToLocalTime();
        var culture = System.Globalization.CultureInfo.GetCultureInfo(L.Language);
        return !string.IsNullOrEmpty(session.Notes?.Title)
            ? date.ToString("m", culture) + " " + session.Notes.Title
            : L.F("会议 {0}", date.ToString("g", culture));
    }
}
