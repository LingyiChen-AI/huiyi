using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using HuiYi.Localization;

namespace HuiYi.Speech;

public static class VideoDownload
{
    public sealed record DownloadedAudio(string Path, string Title);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly SemaphoreSlim InstallGate = new(1);

    public static async Task<DownloadedAudio> Download(string url, string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new ArgumentException(L.T("请输入有效的视频链接"));
        if (Find("ffmpeg.exe") == null)
            throw new InvalidOperationException(L.T("请先安装 FFmpeg 并将其加入 PATH，再导入视频链接"));
        var executable = Find("yt-dlp.exe") ?? await Install(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var result = await Run(executable, url, directory, direct: false, token).ConfigureAwait(false);
        if (result.Code != 0 && ShouldRetryDirect(uri, result.Error))
            result = await Run(executable, url, directory, direct: true, token).ConfigureAwait(false);
        var output = Path.Combine(directory, "media.wav");
        if (result.Code != 0 || !File.Exists(output))
            throw new InvalidOperationException(FailureMessage(result.Error));
        var title = uri.Host;
        try { title = System.Text.Json.JsonSerializer.Deserialize<string>(result.Output.Trim()) ?? title; }
        catch (System.Text.Json.JsonException) { }
        return new(output, title);
    }

    internal static bool ShouldRetryDirect(Uri uri, string error) =>
        (uri.Host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)
         || uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)
         || uri.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase))
        && System.Text.RegularExpressions.Regex.IsMatch(error, @"\b(?:HTTP Error|HTTP|server)\D{0,8}412\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static string FailureMessage(string error)
    {
        var status = System.Text.RegularExpressions.Regex.Match(error, @"\bHTTP(?: Error)?\s+(\d{3})\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (status.Success) return L.F("网站下载请求失败（HTTP {0}），请稍后重试或下载文件后导入", status.Groups[1].Value);
        if (error.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) || error.Contains("ffprobe", StringComparison.OrdinalIgnoreCase))
            return L.T("音轨转换失败，请检查 FFmpeg 是否安装完整");
        return L.T("视频下载失败，请检查链接或网络");
    }

    private static async Task<(int Code, string Output, string Error)> Run(string executable, string url, string directory, bool direct, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        if (direct)
        {
            // Some Bilibili proxy exits receive 412 while the same public video works directly.
            start.ArgumentList.Add("--proxy");
            start.ArgumentList.Add("");
        }
        foreach (var argument in new[] { "--ignore-config", "--no-playlist", "--no-progress", "--no-warnings", "--quiet",
            "--no-cache-dir", "--encoding", "utf-8", "--format", "bestaudio/best", "--extract-audio", "--audio-format", "wav",
            "--output", Path.Combine(directory, "media.%(ext)s"), "--print", "after_move:%(title)j", "--", url }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException(L.T("视频下载失败，请检查链接或网络"));
        using var registration = token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string? Find(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator).Select(p => Path.Combine(p.Trim('"'), name)).FirstOrDefault(File.Exists);

    private static async Task<string> Install(CancellationToken token)
    {
        await InstallGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HuiYi", "tools");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, "yt-dlp.exe");
            if (File.Exists(target)) return target;
            const string release = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
            var sums = await Http.GetStringAsync(release + "SHA2-256SUMS", token).ConfigureAwait(false);
            var expected = sums.Split('\n').Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .First(parts => parts.Length == 2 && parts[1].TrimStart('*') == "yt-dlp.exe")[0];
            var temporary = target + ".tmp";
            try
            {
                using var response = await Http.GetAsync(release + "yt-dlp.exe", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (var file = File.Create(temporary))
                    await response.Content.CopyToAsync(file, token).ConfigureAwait(false);
                await using (var file = File.OpenRead(temporary))
                {
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
                    if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("yt-dlp checksum mismatch");
                }
                File.Move(temporary, target, true);
                return target;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { InstallGate.Release(); }
    }
}
