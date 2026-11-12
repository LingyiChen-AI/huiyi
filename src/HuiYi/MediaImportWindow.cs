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
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Grey };
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 1, Height = 5, Margin = new(0,16,0,16), Visibility = Visibility.Hidden };
    private readonly Button file = new() { Margin = new(0,0,8,0), Padding = new(16,8,16,8) };
    private readonly Button download = new() { Padding = new(16,8,16,8) };
    private readonly Button cancel = new() { Padding = new(16,8,16,8) };
    private CancellationTokenSource? cancellation;
    public string? ImportedId { get; private set; }

    public MediaImportWindow(AppModel model)
    {
        this.model = model;
        Width = 520; Height = 330; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;
        Foreground = Theme.Ink;
        UiText.Set(this, "Title", () => L.T("导入音视频"));
        var panel = new StackPanel { Margin = new(24) };
        var heading = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new(0,0,0,12) };
        UiText.Set(heading, "Text", () => L.T("导入音视频"));
        panel.Children.Add(heading);
        link.Style = (Style)FindResource("Field");
        UiText.Set(link, "Tag", () => L.T("粘贴视频链接，或选择本地文件"));
        panel.Children.Add(link);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0,12,0,16) };
        file.Style = (Style)FindResource("Btn");
        download.Style = (Style)FindResource("PrimaryBtn");
        UiText.Set(file, "Content", () => L.T("选择本地文件"));
        UiText.Set(download, "Content", () => L.T("识别链接"));
        actions.Children.Add(file); actions.Children.Add(download);
        panel.Children.Add(actions); panel.Children.Add(status); panel.Children.Add(progress);
        cancel.Style = (Style)FindResource("Btn");
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        UiText.Set(cancel, "Content", () => L.T("关闭"));
        panel.Children.Add(cancel);
        Content = panel;
        UiText.Set(status, "Text", () => L.T("使用本地模型识别，完成后可生成会议纪要"));
        file.Click += async (_, _) =>
        {
            var picker = new OpenFileDialog
            {
                Filter = L.T("音视频文件") + "|*.wav;*.mp3;*.m4a;*.mp4;*.wma;*.aac;*.flac;*.wmv;*.avi|" + L.T("所有文件") + "|*.*"
            };
            if (picker.ShowDialog(this) == true) await Run(picker.FileName, false);
        };
        download.Click += async (_, _) => await Run(link.Text.Trim(), true);
        cancel.Click += (_, _) => { if (cancellation != null) cancellation.Cancel(); else Close(); };
        Closing += (_, e) => { if (cancellation != null) { e.Cancel = true; cancellation.Cancel(); } };
    }

    private async Task Run(string input, bool remote)
    {
        if (cancellation != null) return;
        cancellation = new();
        var token = cancellation.Token;
        file.IsEnabled = download.IsEnabled = link.IsEnabled = false;
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
            file.IsEnabled = download.IsEnabled = link.IsEnabled = true;
            progress.IsIndeterminate = false;
            progress.Visibility = Visibility.Hidden;
            UiText.Set(cancel, "Content", () => L.T("关闭"));
        }
        if (ImportedId != null) { DialogResult = true; }
    }
}
