using System.Net.Http;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace HuiYi.App;

/// New versions come from the GitHub repository's releases. Checked a little after start and every
/// six hours; nothing is downloaded until the user says 立即更新 in the 发现新版本 box. Only an
/// installed copy (Setup.exe or the portable zip) updates itself: a dev build just says so.
public sealed class Updates
{
    public const string Repo = "https://github.com/LingyiChen-AI/huiyi";

    private readonly UpdateManager? manager;
    private UpdateInfo? available;
    private UpdateInfo? ready;
    private bool checking;

    public event Action? Changed;

    public Updates()
    {
        try { manager = new UpdateManager(new GithubSource(Repo, null, false)); }
        catch (Exception e) { Log.Debug($"updates: unavailable {e.Message}"); }
    }

    public bool Installed => manager?.IsInstalled == true;

    public string Version => manager?.CurrentVersion?.ToString()
                             ?? typeof(Updates).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// A newer version on GitHub, found by the last check (not downloaded yet, perhaps).
    public string? AvailableVersion => available?.TargetFullRelease.Version.ToString();

    /// Downloaded and waiting to be applied.
    public string? ReadyVersion => ready?.TargetFullRelease.Version.ToString();

    public string Status { get; private set; } = "";

    /// Asks GitHub whether there is a newer version. Downloads nothing. True when there is one.
    public async Task<bool> Check()
    {
        if (manager == null || !Installed)
        {
            Status = "当前不是安装版（开发版），不能自动更新";
            Changed?.Invoke();
            return false;
        }
        if (checking) return available != null;
        checking = true;
        Status = "正在检查更新…";
        Changed?.Invoke();
        try
        {
            var info = await manager.CheckForUpdatesAsync();
            available = info ?? available;
            Status = info != null ? $"发现新版本 {AvailableVersion}" : available != null ? $"发现新版本 {AvailableVersion}" : "已是最新版本";
            if (info != null) Log.Debug($"updates: {AvailableVersion} available");
            return available != null;
        }
        catch (Exception e)
        {
            Log.Debug($"updates: check failed {e.Message}");
            Status = "检查更新失败，请检查网络后再试";
            return false;
        }
        finally
        {
            checking = false;
            Changed?.Invoke();
        }
    }

    /// Downloads the available version, reporting 0–100. True when it's ready to apply.
    public async Task<bool> Download(Action<int> progress)
    {
        if (manager == null || available == null) return false;
        if (ready?.TargetFullRelease.Version == available.TargetFullRelease.Version) return true;
        try
        {
            var shown = -1;
            await manager.DownloadUpdatesAsync(available, percent =>
            {
                // Velopack reports often; pass on only whole-percent moves.
                if (percent == shown) return;
                shown = percent;
                progress(percent);
            });
            ready = available;
            Status = $"新版本 {ReadyVersion} 已下载";
            Log.Debug($"updates: {ReadyVersion} downloaded");
            return true;
        }
        catch (Exception e)
        {
            Log.Debug($"updates: download failed {e.Message}");
            Status = "下载失败，请检查网络后再试";
            return false;
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    /// What's new in the available version, as short lines: from the package's notes (written by the
    /// release workflow), else from the GitHub release page.
    public async Task<List<string>> Notes()
    {
        if (available == null) return [];
        var lines = Bullets(available.TargetFullRelease.NotesMarkdown);
        if (lines.Count > 0) return lines;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("HuiYi-Updater");
            var json = await http.GetStringAsync($"https://api.github.com/repos/LingyiChen-AI/huiyi/releases/tags/v{AvailableVersion}");
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("body", out var body) ? Bullets(body.GetString()) : [];
        }
        catch (Exception e)
        {
            Log.Debug($"updates: notes unavailable {e.Message}");
            return [];
        }
    }

    /// The "- …" lines of the notes, leaving out the download section.
    private static List<string> Bullets(string? markdown)
    {
        var outp = new List<string>();
        if (string.IsNullOrWhiteSpace(markdown)) return outp;
        var section = "";
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#")) section = line.TrimStart('#').Trim();
            else if (line.StartsWith("- ") && section != "下载") outp.Add(line[2..].Replace("**", ""));
        }
        return outp;
    }

    /// Applies the downloaded version and starts it. The caller has already stopped the meeting.
    public void ApplyAndRestart()
    {
        if (manager == null || ready == null) return;
        manager.ApplyUpdatesAndRestart(ready.TargetFullRelease);
    }

    /// On quit: apply a version the user already downloaded, once we have exited.
    public void ApplyOnExit()
    {
        if (manager == null || ready == null) return;
        try { manager.WaitExitThenApplyUpdates(ready.TargetFullRelease, silent: true, restart: false); }
        catch (Exception e) { Log.Debug($"updates: apply on exit failed {e.Message}"); }
    }
}
