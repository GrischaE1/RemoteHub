using System.Diagnostics;
using System.Text;

namespace RemoteHub.Services;

/// <summary>
/// How long the start takes, step by step, from the process start until the main window has drawn its first frame.
/// Written once to the diagnostic log ("Started in … ms"); the start-time test reads that line (budget: 3 s).
/// </summary>
public static class StartupTiming
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly StringBuilder Steps = new();
    private static long _last;
    private static bool _done;

    /// <summary>Time from the process start until this class was first used (runtime, WPF, App start).</summary>
    private static readonly long BeforeMs = (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;

    public static void Mark(string step)
    {
        if (_done) return;
        var now = Clock.ElapsedMilliseconds;
        Steps.Append(Steps.Length == 0 ? "" : ", ").Append(step).Append(' ').Append(now - _last);
        _last = now;
    }

    /// <summary>The window is on screen: logs the total once.</summary>
    public static void Finish(string detail)
    {
        if (_done) return;
        Mark("first frame");
        _done = true;
        DiagLog.Write($"Started in {BeforeMs + Clock.ElapsedMilliseconds} ms ({detail}; process start {BeforeMs}, {Steps})");
    }
}
