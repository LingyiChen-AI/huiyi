using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace HuiYi.App;

/// The Windows calls WPF doesn't wrap: windows that never take focus, windows kept out of screen
/// captures, the global push-to-talk key, and launch at login.
internal static class Native
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WDA_NONE = 0x0;
    private const uint WDA_MONITOR = 0x1;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    /// A panel that never takes focus from the meeting app and stays off the taskbar and Alt-Tab.
    public static void MakeNonActivating(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// Kept out of screen shares and recordings (Windows 10 2004+; older systems show it black).
    public static void SetCapturable(Window w, bool capturable)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        if (capturable) { SetWindowDisplayAffinity(hwnd, WDA_NONE); return; }
        if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)) SetWindowDisplayAffinity(hwnd, WDA_MONITOR);
    }

    // MARK: Push-to-talk key

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    public const int VK_RCONTROL = 0xA3, VK_RMENU = 0xA5;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public int vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    /// Watches one key system-wide (listen-only: the key still reaches other apps). Down fires once
    /// per press, not on auto-repeat. Must be created on a thread with a message loop (the UI thread).
    public sealed class KeyWatch : IDisposable
    {
        private readonly HookProc proc;
        private IntPtr hook;
        private bool down;
        public int Key { get; set; }
        public event Action? Down;
        public event Action? Up;

        public KeyWatch(int key)
        {
            Key = key;
            proc = Callback;
            using var module = Process.GetCurrentProcess().MainModule!;
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(module.ModuleName), 0);
            if (hook == IntPtr.Zero) Log.Debug($"key: hook failed {Marshal.GetLastWin32Error()}");
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (info.vkCode == Key)
                {
                    var msg = (int)wParam;
                    if (msg is WM_KEYDOWN or WM_SYSKEYDOWN && !down)
                    {
                        down = true;
                        Down?.Invoke();
                    }
                    else if (msg is WM_KEYUP or WM_SYSKEYUP && down)
                    {
                        down = false;
                        Up?.Invoke();
                    }
                }
            }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }

    // MARK: Launch at login

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "HuiYi";

    public static void SetLaunchAtLogin(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            if (on) key.SetValue(RunName, $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue(RunName, false);
        }
        catch (Exception e)
        {
            Log.Debug($"login item: {e.Message}");
        }
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) { Log.Debug($"open: {e.Message}"); }
    }
}
