namespace HuiYi;

/// Where the app keeps its files: %APPDATA%\HuiYi (settings, key, meetings) and
/// %LOCALAPPDATA%\HuiYi (models, debug log), so meetings roam and models don't.
public static class Paths
{
    public static string Roaming { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HuiYi");
    public static string Local { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HuiYi");

    public static string Sessions => Path.Combine(Roaming, "Sessions");
    public static string Models => Path.Combine(Local, "models");
    public static string DebugLog => Path.Combine(Local, "debug.log");
}

/// Debug log: timings, states and failures. Never transcript text, translations or the key.
public static class Log
{
    private static readonly object Gate = new();
    public static Action<string>? Echo;

    public static void Debug(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        Echo?.Invoke(line);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Paths.Local);
                var info = new FileInfo(Paths.DebugLog);
                // Keep it small: start over past 5 MB.
                if (info.Exists && info.Length > 5_000_000) info.Delete();
                File.AppendAllText(Paths.DebugLog, line + Environment.NewLine);
            }
            catch { /* logging must never break the app */ }
        }
    }
}
