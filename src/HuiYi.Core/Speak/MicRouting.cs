using HuiYi.Localization;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NAudio.CoreAudioApi;

namespace HuiYi.Speak;

/// Windows' default audio devices. There is no public API to change them; every audio switcher
/// (SoundSwitch, EarTrumpet…) uses the undocumented IPolicyConfig the Sound control panel itself uses.
public static class DefaultDevices
{
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultPeriod, IntPtr period, IntPtr minimum);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient;

    public static string? DefaultMicId()
    {
        try { return new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID; }
        catch { return null; }
    }

    /// Makes `id` the default for all three roles (games/apps, multimedia, communications).
    public static bool SetDefault(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            var ok = true;
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
                ok &= policy.SetDefaultEndpoint(id, role) == 0;
            Marshal.ReleaseComObject(policy);
            return ok;
        }
        catch (Exception e)
        {
            Log.Debug($"devices: set default failed {e.Message}");
            return false;
        }
    }

    public static (string Id, string Name)? DefaultSpeaker()
    {
        try
        {
            var d = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            return (d.ID, d.FriendlyName);
        }
        catch { return null; }
    }

    /// Active speakers, the virtual cable left out.
    public static List<(string Id, string Name)> Speakers()
    {
        try
        {
            return new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Where(d => !VirtualCable.IsCable(d.FriendlyName))
                .Select(d => (d.ID, d.FriendlyName)).ToList();
        }
        catch { return []; }
    }

    /// VB-CABLE's setup makes "CABLE Input" the default speaker: the user then hears nothing, and the
    /// meeting's sound never reaches our loopback. Puts a real speaker back (`preferred` if still there).
    public static void KeepRealSpeaker(string? preferred = null)
    {
        if (DefaultSpeaker() is not { } current || !VirtualCable.IsCable(current.Name)) return;
        var real = Speakers();
        var back = real.FirstOrDefault(s => s.Id == preferred).Id ?? real.FirstOrDefault().Id;
        if (back == null) return;
        var ok = SetDefault(back);
        Log.Debug($"devices: default speaker was the cable, moved back ({(ok ? "ok" : "failed")})");
    }

    /// Active microphones, the virtual cable left out.
    public static List<(string Id, string Name)> Mics()
    {
        try
        {
            return new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Where(d => !VirtualCable.IsCable(d.FriendlyName))
                .Select(d => (d.ID, d.FriendlyName)).ToList();
        }
        catch { return []; }
    }

    public static bool Active(string id) => Mics().Any(m => m.Id == id);
}

/// The virtual microphone: VB-Audio Virtual Cable. What we play into "CABLE Input" comes out of
/// "CABLE Output", the microphone the meeting app hears. Never bundled (it is someone else's
/// driver): on request we download the official pack and check its pinned SHA-256 first.
public static class VirtualCable
{
    public const string Download = "https://vb-audio.com/Cable/";
    public const string Pack = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip";
    public const string PackSha256 = "bc44e8974191a5a2427d6c979e2dab08dbb09925f3f435dfa2f0ba3e45b6eb90";

    public static bool IsCable(string name) =>
        name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CABLE ", StringComparison.Ordinal);

    private static MMDevice? Find(DataFlow flow, string name)
    {
        try
        {
            return new MMDeviceEnumerator().EnumerateAudioEndPoints(flow, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    /// Where we play: the cable's speaker end.
    public static MMDevice? Input() => Find(DataFlow.Render, "CABLE Input");

    /// What the meeting app records: the cable's microphone end.
    public static MMDevice? Output() => Find(DataFlow.Capture, "CABLE Output");

    public static bool Installed => Input() != null;

    public enum InstallResult { Installed, NeedsRestart, Cancelled, Failed }

    /// Downloads the official driver pack, checks it, and runs its setup as administrator (the user sees
    /// UAC and VB-Audio's own licence dialog). Returns once setup has closed.
    public static async Task<(InstallResult Result, string? Error)> Install(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "HuiYi-vbcable-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var zip = Path.Combine(dir, "pack.zip");
            progress?.Report(L.T("正在下载 VB-CABLE…"));
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
            {
                var bytes = await http.GetByteArrayAsync(Pack, ct);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (hash != PackSha256)
                {
                    Log.Debug($"vbcable: checksum mismatch {hash}");
                    return (InstallResult.Failed, L.T("下载的安装包校验不通过，已停止安装"));
                }
                await File.WriteAllBytesAsync(zip, bytes, ct);
            }
            var unpacked = Path.Combine(dir, "pack");
            ZipFile.ExtractToDirectory(zip, unpacked);
            var setup = Path.Combine(unpacked, "VBCABLE_Setup_x64.exe");
            if (!File.Exists(setup)) return (InstallResult.Failed, L.T("安装包里没有找到安装程序"));

            var speakerBefore = DefaultDevices.DefaultSpeaker()?.Id;
            progress?.Report(L.T("请在弹出的窗口里点「Install Driver」…"));
            Process? p;
            try
            {
                p = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, Verb = "runas", WorkingDirectory = unpacked });
            }
            catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                return (InstallResult.Cancelled, null); // UAC declined
            }
            if (p == null) return (InstallResult.Failed, L.T("安装程序没有启动"));
            await p.WaitForExitAsync(ct);

            // The endpoints can take a moment to appear after the driver loads.
            for (var i = 0; i < 10 && !Installed; i++) await Task.Delay(500, ct);
            // Windows may switch the default speaker to the new cable a moment after it appears.
            for (var i = 0; i < 6; i++)
            {
                DefaultDevices.KeepRealSpeaker(speakerBefore);
                await Task.Delay(500, ct);
            }
            Log.Debug($"vbcable: setup exited, installed={Installed}");
            return Installed ? (InstallResult.Installed, null) : (InstallResult.NeedsRestart, null);
        }
        catch (OperationCanceledException)
        {
            return (InstallResult.Cancelled, null);
        }
        catch (Exception e)
        {
            Log.Debug($"vbcable: install failed {e.Message}");
            return (InstallResult.Failed, e.Message);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

/// While interpreting is on, the system microphone is the cable, so whatever meeting app uses
/// "default" hears only the interpreted voice; my own words are recorded from my real mic.
public sealed class MicRouting(AppSettings settings)
{
    /// The microphone I speak into, never the cable.
    public string? MyMicId()
    {
        if (settings.SpeakMicId is { } chosen && DefaultDevices.Active(chosen)) return chosen;
        if (settings.SpeakSavedMicId is { } saved && DefaultDevices.Active(saved)) return saved;
        var current = DefaultDevices.DefaultMicId();
        if (current != null && DefaultDevices.Active(current)) return current;
        return DefaultDevices.Mics().FirstOrDefault().Id;
    }

    public bool Routed => VirtualCable.Output() is { } cable && DefaultDevices.DefaultMicId() == cable.ID;

    /// Puts the default mic where the settings say it should be. Safe to call any time.
    public void Apply()
    {
        DefaultDevices.KeepRealSpeaker();
        var want = settings.SpeakEnabled && settings.SpeakRouteMic;
        var cable = VirtualCable.Output();
        var current = DefaultDevices.DefaultMicId();
        if (want && cable != null)
        {
            if (current == cable.ID) return;
            if (current != null && settings.SpeakSavedMicId != current)
            {
                settings.SpeakSavedMicId = current;
                settings.Save();
            }
            var ok = DefaultDevices.SetDefault(cable.ID);
            Log.Debug($"routing: default mic → CABLE Output ({(ok ? "ok" : "failed")})");
        }
        else Restore();
    }

    /// Gives the default mic back, if we took it and nobody has changed it since.
    public void Restore()
    {
        if (settings.SpeakSavedMicId is not { } saved) return;
        var cable = VirtualCable.Output();
        if (cable != null && DefaultDevices.DefaultMicId() == cable.ID && DefaultDevices.Active(saved))
        {
            var ok = DefaultDevices.SetDefault(saved);
            Log.Debug($"routing: default mic restored ({(ok ? "ok" : "failed")})");
        }
        settings.SpeakSavedMicId = null;
        settings.Save();
    }
}
