using System.Diagnostics;
using System.Windows.Threading;

namespace HuiYi.App;

/// Writes to the debug log when the UI thread is too busy to answer a click: a worker asks it for an
/// empty step every quarter second and times how long the answer takes. A report of "every click
/// waits a few seconds" then shows in the log how long and how often, and what was opened just before.
public static class UiWatch
{
    /// Below this a click still feels immediate.
    public const int ReportMs = 400;

    public static void Start(Dispatcher ui)
    {
        var thread = new Thread(() =>
        {
            var answered = new ManualResetEventSlim();
            while (!ui.HasShutdownStarted)
            {
                answered.Reset();
                var sw = Stopwatch.StartNew();
                var paused = GC.GetTotalPauseDuration();
                var gen2 = GC.CollectionCount(2);
                ui.BeginInvoke(DispatcherPriority.Input, () => answered.Set());
                // Waiting in steps: a long hang still gets one line, written as soon as it ends.
                while (!answered.Wait(1000) && !ui.HasShutdownStarted) { }
                var ms = sw.ElapsedMilliseconds;
                // The garbage collector stops every thread: its share tells memory apart from work.
                if (ms >= ReportMs)
                    Log.Debug($"ui: busy {ms} ms (gc {(GC.GetTotalPauseDuration() - paused).TotalMilliseconds:0} ms, {GC.CollectionCount(2) - gen2} full; heap {GC.GetTotalMemory(false) / 1_048_576} MB)");
                Thread.Sleep(250);
            }
        }) { IsBackground = true, Name = "ui watch", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    private static readonly int TimeAbove = int.TryParse(Environment.GetEnvironmentVariable("HUIYI_UI_MS"), out var ms) ? ms : 150;

    /// Times a step that runs on the UI thread, when it is slow enough to be felt (HUIYI_UI_MS lowers the bar).
    public static void Time(string what, Action step)
    {
        var sw = Stopwatch.StartNew();
        step();
        if (sw.ElapsedMilliseconds >= TimeAbove) Log.Debug($"ui: {what} took {sw.ElapsedMilliseconds} ms");
    }
}
