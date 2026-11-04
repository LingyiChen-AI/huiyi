using Microsoft.Win32;

namespace HuiYi;

/// Which meeting apps are using a microphone right now, from Windows' privacy bookkeeping
/// (CapabilityAccessManager\ConsentStore\microphone): an app in use has LastUsedTimeStop = 0.
/// A call ending looks like the app letting go of the mic. Only known meeting apps count.
public static class MicUsers
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// Executable (lower case) or package family prefix → the name shown to the user.
    public static readonly Dictionary<string, string> MeetingApps = new()
    {
        ["zoom.exe"] = "Zoom",
        ["ms-teams.exe"] = "Teams",
        ["teams.exe"] = "Teams",
        ["msteams_"] = "Teams",
        ["wemeetapp.exe"] = "腾讯会议",
        ["feishu.exe"] = "飞书",
        ["lark.exe"] = "Lark",
        ["dingtalk.exe"] = "钉钉",
        ["webex.exe"] = "Webex",
        ["ciscocollabhost.exe"] = "Webex",
        ["slack.exe"] = "Slack",
        ["discord.exe"] = "Discord",
        ["skype.exe"] = "Skype",
        ["wechat.exe"] = "微信",
        ["weixin.exe"] = "微信",
    };

    /// Meeting apps using a microphone now (id → name).
    public static Dictionary<string, string> InCall()
    {
        var result = new Dictionary<string, string>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Root);
            if (root == null) return result;
            // Packaged apps sit directly under microphone; desktop apps under NonPackaged.
            Scan(root, result, packaged: true);
            using var np = root.OpenSubKey("NonPackaged");
            if (np != null) Scan(np, result, packaged: false);
        }
        catch (Exception e)
        {
            Log.Debug($"mic users: {e.GetType().Name}");
        }
        return result;
    }

    private static void Scan(RegistryKey key, Dictionary<string, string> result, bool packaged)
    {
        foreach (var name in key.GetSubKeyNames())
        {
            if (name == "NonPackaged") continue;
            var id = packaged ? name.ToLowerInvariant() : name.Split('#').Last().ToLowerInvariant();
            var app = MeetingApps.FirstOrDefault(kv => packaged ? id.StartsWith(kv.Key) : id == kv.Key);
            if (app.Key == null) continue;
            using var sub = key.OpenSubKey(name);
            if (sub?.GetValue("LastUsedTimeStop") is long stop && stop == 0
                && sub.GetValue("LastUsedTimeStart") is long start && start > 0)
                result[id] = app.Value;
        }
    }
}
