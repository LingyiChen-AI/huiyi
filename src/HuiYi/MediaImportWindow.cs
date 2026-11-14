using System.IO;
using System.Windows;
using System.Windows.Controls;
using HuiYi.Localization;
using HuiYi.Speech;
using Microsoft.Win32;

namespace HuiYi.App;

public sealed class MediaImportWindow : Window
{
    private readonly AppModel model;
    private readonly TextBox link = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Grey, FontSize = 12, Margin = new(2, 14, 0, 0), Visibility = Visibility.Collapsed };
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 1, Height = 5, Margin = new(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button file = new() { Padding = new(22, 8, 22, 8), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button download = new() { Padding = new(16, 7, 16, 7), Margin = new(8, 0, 0, 0), IsEnabled = false };
    private readonly Button cancel = new() { Padding = new(18, 6, 18, 6), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
    private readonly Border drop = new();
    private readonly System.Windows.Shapes.Rectangle outline = new()
    {
        Stroke = Theme.Line, StrokeThickness = 1.5, StrokeDashArray = [4, 3], RadiusX = 12, RadiusY = 12, IsHitTestVisible = false,
    };
    private CancellationTokenSource? cancellation;
    public string? ImportedId { get; private set; }

    /// Media the recognizer can read (Media Foundation): the picker's filter and what a drop accepts.
    private static readonly string[] Kinds = [".wav", ".mp3", ".m4a", ".mp4", ".wma", ".aac", ".flac", ".wmv", ".avi", ".mov", ".mkv", ".webm"];

    public MediaImportWindow(AppModel model)
    {
        this.model = model;
        // One column: the file (what most people import) as the big target, a link below it.
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)FindResource("Ground");
        Foreground = Theme.Ink;
        UiText.Set(this, "Title", () => L.T("导入音视频"));
        var panel = new StackPanel { Margin = new(24, 22, 24, 20) };
        var heading = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold };
        UiText.Set(heading, "Text", () => L.T("导入音视频"));
        panel.Children.Add(heading);
        var sub = new TextBlock { Style = (Style)FindResource("Helper"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 16) };
        UiText.Set(sub, "Text", () => L.T("使用本地模型识别，完成后可生成会议纪要"));
        panel.Children.Add(sub);

        // The drop target: a dashed card that lights up while a file is dragged over it.
        var inside = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        inside.Children.Add(new TextBlock
        {
            Text = "\uE8B5", FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 28,
            Foreground = Theme.Primary, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 0, 0, 12),
        });
        file.Style = (Style)FindResource("PrimaryBtn");
        UiText.Set(file, "Content", () => L.T("选择本地文件"));
        inside.Children.Add(file);
        var hint = new TextBlock { FontSize = 12, Foreground = Theme.Grey2, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new(0, 10, 0, 0) };
        UiText.Set(hint, "Text", () => L.T("也可以把文件拖到这里 · 支持 WAV、MP3、M4A、MP4、FLAC 等"));
        inside.Children.Add(hint);
        drop.Child = inside;
        drop.Background = System.Windows.Media.Brushes.White;
        drop.CornerRadius = new CornerRadius(12);
        drop.Padding = new Thickness(20, 24, 20, 22);
        drop.AllowDrop = true;
        drop.DragEnter += (_, e) => Hover(e, true);
        drop.DragOver += (_, e) => Hover(e, true);
        drop.DragLeave += (_, _) => Light(false);
        drop.Drop += async (_, e) =>
        {
            Light(false);
            if (Dropped(e) is { } path) await Run(path, false);
        };
        // The dashed outline over the card (a Border can't dash).
        var target = new Grid();
        target.Children.Add(drop);
        target.Children.Add(outline);
        panel.Children.Add(target);

        // Or a link.
        var or = new Grid { Margin = new(0, 18, 0, 10) };
        or.ColumnDefinitions.Add(new ColumnDefinition());
        or.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        or.ColumnDefinitions.Add(new ColumnDefinition());
        var orText = new TextBlock { FontSize = 12, Foreground = Theme.Grey2, Margin = new(10, 0, 10, 0) };
        UiText.Set(orText, "Text", () => L.T("或粘贴视频链接"));
        Grid.SetColumn(orText, 1);
        foreach (var col in new[] { 0, 2 })
        {
            var line = new Border { Height = 1, Background = Theme.Line, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(line, col);
            or.Children.Add(line);
        }
        or.Children.Add(orText);
        panel.Children.Add(or);
        var linkRow = new DockPanel();
        download.Style = (Style)FindResource("Btn");
        UiText.Set(download, "Content", () => L.T("识别链接"));
        DockPanel.SetDock(download, Dock.Right);
        linkRow.Children.Add(download);
        link.Style = (Style)FindResource("Field");
        UiText.Set(link, "Tag", () => L.T("B 站、YouTube 等视频页面的链接"));
        link.TextChanged += (_, _) => download.IsEnabled = cancellation == null && link.Text.Trim().Length > 0;
        link.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && download.IsEnabled) await Run(link.Text.Trim(), true); };
        linkRow.Children.Add(link);
        panel.Children.Add(linkRow);

        panel.Children.Add(status);
        panel.Children.Add(progress);
        cancel.Style = (Style)FindResource("Btn");
        UiText.Set(cancel, "Content", () => L.T("关闭"));
        panel.Children.Add(cancel);
        Content = panel;
        file.Click += async (_, _) =>
        {
            var picker = new OpenFileDialog
            {
                Filter = L.T("音视频文件") + "|" + string.Join(";", Kinds.Select(k => "*" + k)) + "|" + L.T("所有文件") + "|*.*"
            };
            if (picker.ShowDialog(this) == true) await Run(picker.FileName, false);
        };
        download.Click += async (_, _) => await Run(link.Text.Trim(), true);
        cancel.Click += (_, _) => { if (cancellation != null) cancellation.Cancel(); else Close(); };
        Closing += (_, e) => { if (cancellation != null) { e.Cancel = true; cancellation.Cancel(); } };
    }

    private void Light(bool on)
    {
        drop.Background = on ? Theme.PrimarySoft : System.Windows.Media.Brushes.White;
        outline.Stroke = on ? Theme.Primary : Theme.Line;
    }

    private void Hover(DragEventArgs e, bool on)
    {
        var ok = cancellation == null && Dropped(e) != null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        Light(ok && on);
    }

    /// The one media file being dropped, or null.
    private static string? Dropped(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files && File.Exists(files[0])
        && Kinds.Contains(Path.GetExtension(files[0]).ToLowerInvariant()) ? files[0] : null;

    private async Task Run(string input, bool remote)
    {
        if (cancellation != null) return;
        cancellation = new();
        var token = cancellation.Token;
        file.IsEnabled = download.IsEnabled = link.IsEnabled = false;
        status.Visibility = Visibility.Visible;
        drop.AllowDrop = false;
        UiText.Set(cancel, "Content", () => L.T("取消"));
        progress.IsIndeterminate = true;
        progress.Visibility = Visibility.Visible;
        progress.Value = 0;
        string? directory = null;
        try
        {
            UiText.Set(status, "Text", () => L.T("正在准备识别模型…"));
            if (!await model.EnsureEngines()) throw new InvalidOperationException(L.T("请先完成识别模型下载"));
            token.ThrowIfCancellationRequested();
            var path = input;
            string? title = null;
            if (remote)
            {
                UiText.Set(status, "Text", () => L.T("正在下载音轨，首次使用会下载 yt-dlp…"));
                directory = Path.Combine(Path.GetTempPath(), "HuiYi-import-" + Guid.NewGuid().ToString("N"));
                var downloaded = await VideoDownload.Download(input, directory, token);
                path = downloaded.Path;
                title = downloaded.Title;
            }
            UiText.Set(status, "Text", () => L.T("正在识别音轨…"));
            progress.IsIndeterminate = false;
            var reporter = new Progress<double>(value => progress.Value = value);
            var session = await Task.Run(() => MediaImport.Recognize(path, model.Recognizer!, model.Models,
                model.Settings.SpeakerVoices, reporter, token));
            if (title != null) session.Title = title;
            session.TemplateId = model.Settings.DefaultTemplateId;
            token.ThrowIfCancellationRequested();
            ImportedId = await Task.Run(() => model.Store.Import(session));
        }
        catch (OperationCanceledException) { UiText.Set(status, "Text", () => L.T("已取消导入")); }
        catch (Exception error)
        {
            Log.Debug($"import: failed {error.GetType().Name}");
            // Decoder failures may include a private file path. Keep that out of the debug log.
            UiText.Set(status, "Text", () => L.T("导入失败") + " · " + error.Message);
        }
        finally
        {
            if (directory != null) { try { Directory.Delete(directory, true); } catch { } }
            cancellation.Dispose(); cancellation = null;
            file.IsEnabled = link.IsEnabled = true;
            download.IsEnabled = link.Text.Trim().Length > 0;
            drop.AllowDrop = true;
            progress.IsIndeterminate = false;
            progress.Visibility = Visibility.Collapsed;
            UiText.Set(cancel, "Content", () => L.T("关闭"));
        }
        if (ImportedId != null) { DialogResult = true; }
    }
}
