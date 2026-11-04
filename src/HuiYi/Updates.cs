using Velopack;
using Velopack.Sources;

namespace HuiYi.App;

/// New versions come from the GitHub repository's releases. Checked a little after start and every
/// six hours, downloaded quietly; applied when the user says so or when the app quits. Only an
/// installed copy (Setup.exe) updates itself: a dev build or the unzipped package just says so.
public sealed class Updates
{
    public const string Repo = "https://github.com/LingyiChen-AI/huiyi";

    private readonly UpdateManager? manager;
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

    /// The downloaded version waiting to be applied, if any.
    public string? ReadyVersion => ready?.TargetFullRelease.Version.ToString();

    public string Status { get; private set; } = "";

    /// Checks GitHub and downloads a newer version. Returns true when one is ready to apply.
    public async Task<bool> Check()
    {
        if (manager == null || !Installed)
        {
            Status = "当前不是安装版（开发版或解压版），不能自动更新";
            Changed?.Invoke();
            return false;
        }
        if (checking) return ready != null;
        checking = true;
        Status = "正在检查更新…";
        Changed?.Invoke();
        try
        {
            var info = await manager.CheckForUpdatesAsync();
            if (info == null)
            {
                Status = ready != null ? $"新版本 {ReadyVersion} 已下载，重启后生效" : "已是最新版本";
                return ready != null;
            }
            var version = info.TargetFullRelease.Version;
            Status = $"正在下载新版本 {version}…";
            Changed?.Invoke();
            var shown = -1;
            await manager.DownloadUpdatesAsync(info, percent =>
            {
                // Velopack reports often; tell the UI only when the whole percent moves.
                if (percent == shown) return;
                shown = percent;
                Status = $"正在下载新版本 {version}… {percent}%";
                Changed?.Invoke();
            });
            ready = info;
            Status = $"新版本 {ReadyVersion} 已下载，重启后生效";
            Log.Debug($"updates: {ReadyVersion} ready");
            return true;
        }
        catch (Exception e)
        {
            Log.Debug($"updates: check failed {e.Message}");
            Status = "检查更新失败，稍后会自动再试";
            return false;
        }
        finally
        {
            checking = false;
            Changed?.Invoke();
        }
    }

    /// Applies the downloaded version and starts it. The caller has already stopped the meeting.
    public void ApplyAndRestart()
    {
        if (manager == null || ready == null) return;
        manager.ApplyUpdatesAndRestart(ready.TargetFullRelease);
    }

    /// On quit: apply a downloaded version once we have exited, without starting it again.
    public void ApplyOnExit()
    {
        if (manager == null || ready == null) return;
        try { manager.WaitExitThenApplyUpdates(ready.TargetFullRelease, silent: true, restart: false); }
        catch (Exception e) { Log.Debug($"updates: apply on exit failed {e.Message}"); }
    }
}
