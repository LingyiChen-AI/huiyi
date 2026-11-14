using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HuiYi.Localization;

namespace HuiYi.App;

/// The glossary: names, products and terms the user wants heard, written and translated their way.
/// Left the list (searchable), right one term with a box to try the corrections on.
public partial class GlossaryWindow : Window
{
    private readonly AppModel model;
    private GlossaryTerm current = new();
    private bool loading;
    private bool dirty;

    public GlossaryWindow(AppModel model, string? addText = null)
    {
        this.model = model;
        InitializeComponent();
        model.Glossary.Changed += OnGlossaryChanged;
        L.Changed += Refresh;
        Closed += (_, _) =>
        {
            model.Glossary.Changed -= OnGlossaryChanged;
            L.Changed -= Refresh;
        };
        var first = model.Glossary.Terms.FirstOrDefault();
        if (addText != null) Show(new GlossaryTerm { Text = addText });
        else if (first != null) Show(first);
        else Show(new GlossaryTerm());
    }

    private void OnGlossaryChanged() => Dispatcher.BeginInvoke(() => { RenderList(); RenderTry(); });

    private void Refresh()
    {
        RenderList();
        RenderTitle();
        RenderTry();
    }

    private bool IsNew => model.Glossary.Terms.All(t => t.Id != current.Id);

    private void Show(GlossaryTerm t)
    {
        current = t.Clone();
        loading = true;
        TextBox.Text = current.Text;
        MisheardBox.Text = string.Join("，", current.Misheard);
        TranslationBox.Text = GlossaryTerm.FormatTranslations(current.Translations);
        NoteBox.Text = current.Note;
        EnabledBox.IsChecked = current.Enabled;
        ExactBox.IsChecked = current.ExactOnly;
        loading = false;
        dirty = false;
        SavedText.Text = "";
        RenderTitle();
        RenderList();
        RenderTry();
        if (current.Text.Length == 0) Dispatcher.BeginInvoke(() => TextBox.Focus());
    }

    private void RenderTitle()
    {
        TitleText.Text = current.Text.Length > 0 ? current.Text : L.T("新词条");
        DeleteBtn.Visibility = IsNew ? Visibility.Collapsed : Visibility.Visible;
        SaveBtn.IsEnabled = TextBox.Text.Trim().Length > 0;
    }

    private void RenderList()
    {
        var all = model.Glossary.Terms;
        var q = SearchBox.Text.Trim();
        var shown = all.Where(t => q.Length == 0 || t.Text.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || t.Misheard.Any(m => m.Contains(q, StringComparison.OrdinalIgnoreCase))
                                   || t.Note.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Text, StringComparer.CurrentCulture).ToList();
        ListHost.Children.Clear();
        if (all.Count == 0)
            ListHost.Children.Add(new TextBlock
            {
                Text = L.T("还没有词条。把常被认错的人名、产品名、术语加进来，以后的会议都会自动纠正。"),
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Theme.Grey2, Margin = new Thickness(6, 12, 8, 0),
            });
        foreach (var t in shown) ListHost.Children.Add(Row(t));
        CountText.Text = L.F("共 {0} 个词条", all.Count);
    }

    private UIElement Row(GlossaryTerm t)
    {
        var selected = t.Id == current.Id;
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = t.Text, FontSize = 13, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis, Foreground = t.Enabled ? Theme.Ink : Theme.Grey2,
        });
        var detail = new List<string>();
        if (!t.Enabled) detail.Add(L.T("已停用"));
        if (t.Misheard.Count > 0) detail.Add(L.F("常被听成 {0}", string.Join("、", t.Misheard)));
        if (t.TranslationFor("en") is { } en) detail.Add(en);
        if (detail.Count > 0)
            body.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", detail), FontSize = 11, Foreground = Theme.Grey2,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0),
            });
        var row = new Border
        {
            Background = selected ? Theme.PrimarySoft : Brushes.Transparent, CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 1, 6, 1), Cursor = Cursors.Hand, Child = body,
        };
        row.MouseEnter += (_, _) => { if (t.Id != current.Id) row.Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF6, 0xFA)); };
        row.MouseLeave += (_, _) => { if (t.Id != current.Id) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, _) => { if (ConfirmLeave()) Show(t); };
        return row;
    }

    /// Unsaved edits: save them rather than lose them.
    private bool ConfirmLeave()
    {
        if (!dirty || TextBox.Text.Trim().Length == 0) return true;
        Save();
        return true;
    }

    private GlossaryTerm FromFields() => new()
    {
        Id = current.Id,
        Text = TextBox.Text.Trim(),
        Misheard = GlossaryTerm.ParseList(MisheardBox.Text),
        Translations = GlossaryTerm.ParseTranslations(TranslationBox.Text),
        Note = NoteBox.Text.Trim(),
        Enabled = EnabledBox.IsChecked == true,
        ExactOnly = ExactBox.IsChecked == true,
    };

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || !IsLoaded && sender != TextBox) return;
        dirty = true;
        SavedText.Text = "";
        SaveBtn.IsEnabled = TextBox.Text.Trim().Length > 0;
        RenderTry();
    }

    private void Save()
    {
        var t = FromFields();
        if (t.Text.Length == 0) return;
        model.Glossary.Save(t);
        current = model.Glossary.Terms.FirstOrDefault(x => x.Id == t.Id) ?? model.Glossary.Terms.First(x => x.Text == t.Text);
        dirty = false;
        SavedText.Text = L.T("已保存，之后识别、翻译和纪要都会用上");
        RenderTitle();
        RenderList();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmLeave()) Show(new GlossaryTerm());
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (IsNew) return;
        if (!Dialog.Confirm(this, L.F("删除词条「{0}」？", current.Text), L.T("之后的会议不再按它纠正和翻译，已经记下的内容不受影响。"), L.T("删除"), danger: true)) return;
        model.Glossary.Remove(current.Id);
        dirty = false;
        var next = model.Glossary.Terms.FirstOrDefault();
        Show(next ?? new GlossaryTerm());
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => RenderList();

    // ── try it: the glossary as it would be after saving this term ──

    private void Try_Changed(object sender, TextChangedEventArgs e) => RenderTry();

    private void RenderTry()
    {
        if (TryResult == null) return;
        var text = TryBox.Text;
        if (text.Trim().Length == 0)
        {
            TryResult.Text = "";
            TryFixes.Text = L.T("比如输入「我和毕丽静都没想到」，看它会不会被改对。");
            return;
        }
        var terms = model.Glossary.Terms.Where(t => t.Id != current.Id).ToList();
        var edited = FromFields();
        if (edited.Text.Length > 0) terms.Add(edited);
        var (fixedText, fixes) = new TermFixer(terms).Fix(text);
        TryResult.Text = fixedText;
        TryFixes.Text = fixes.Count == 0 ? L.T("没有需要改的。")
            : string.Join("；", fixes.Select(f => L.F("{0} → {1}", f.From, f.To)));
    }

    // ── import / export ──

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("导入词库"), Filter = L.T("文本文件") + " (*.txt;*.tsv;*.csv)|*.txt;*.tsv;*.csv|" + L.T("所有文件") + " (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var (added, updated) = model.Glossary.Import(System.IO.File.ReadAllText(dialog.FileName));
            SavedText.Text = L.F("导入完成：新增 {0} 个，更新 {1} 个", added, updated);
            if (IsNew && model.Glossary.Terms.FirstOrDefault() is { } first && TextBox.Text.Trim().Length == 0) Show(first);
            SavedText.Text = L.F("导入完成：新增 {0} 个，更新 {1} 个", added, updated);
        }
        catch (Exception ex)
        {
            Dialog.Confirm(this, L.T("导入失败"), ex.Message, L.T("知道了"));
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("导出词库"), FileName = L.T("会译词库") + ".txt", Filter = L.T("文本文件") + " (*.txt)|*.txt",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            System.IO.File.WriteAllText(dialog.FileName, model.Glossary.Export(), new System.Text.UTF8Encoding(true));
            SavedText.Text = L.F("已导出到 {0}", dialog.FileName);
        }
        catch (Exception ex)
        {
            Dialog.Confirm(this, L.T("导出失败"), ex.Message, L.T("知道了"));
        }
    }
}
