using System.Threading;

namespace RemoteHub.Tests;

/// <summary>Waiting for a thread a test started itself (STA work: the clipboard, OLE, WPF).</summary>
internal static class TestThread
{
    /// <summary>
    /// Waits at most <paramref name="timeoutSeconds"/> for <paramref name="thread"/>: a clipboard or OLE call that never
    /// returns fails the test instead of holding up the whole run until the CI job's time limit. A thread still stuck
    /// is made a background thread, so it doesn't keep the test host alive either.
    /// </summary>
    public static void Join(Thread thread, string what, int timeoutSeconds = 30)
    {
        if (thread.Join(TimeSpan.FromSeconds(timeoutSeconds))) return;
        thread.IsBackground = true;
        Assert.Fail($"{what} didn't finish within {timeoutSeconds} s");
    }
}
