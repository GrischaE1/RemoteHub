using System.Runtime.InteropServices;

namespace RemoteHub.Agent;

/// <summary>
/// The app of an app tab as Windows has it, for the helper (LocalSystem, in the admin's session): which windows are the
/// app's – their process is in the tab's job, which the service made (app-tabs-design §6.2) – and moving, maximising and
/// bringing them to the front. Windows are moved asynchronously, so an app that hangs never holds up the capture.
/// </summary>
internal sealed class AppTabWindows : IDisposable
{
    private IntPtr _job;
    // Whether a process is in the job, by id: asked again after a while, since an id can be reused.
    private readonly Dictionary<uint, (bool InJob, long At)> _processes = [];
    private const long ProcessCacheMs = 2000;

    private AppTabWindows(IntPtr job) => _job = job;

    /// <summary>The job by its name (query access only), or null when it can't be opened.</summary>
    public static AppTabWindows? Open(string jobName)
    {
        var job = OpenJobObjectW(0x4 /* JOB_OBJECT_QUERY */, false, jobName);
        return job == IntPtr.Zero ? null : new AppTabWindows(job);
    }

    /// <summary>The window's process is in the tab's job.</summary>
    public bool Owns(IntPtr window)
    {
        if (GetWindowThreadProcessId(window, out var pid) == 0 || pid == 0) return false;
        var now = Environment.TickCount64;
        if (_processes.TryGetValue(pid, out var known) && now - known.At < ProcessCacheMs) return known.InJob;
        var inJob = false;
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process != IntPtr.Zero)
        {
            try { inJob = IsProcessInJob(process, _job, out var result) && result; }
            finally { CloseHandle(process); }
        }
        if (_processes.Count > 256) _processes.Clear();
        _processes[pid] = (inJob, now);
        return inJob;
    }

    /// <summary>Processes in the job now (−1 when it can't be read).</summary>
    public int ActiveProcesses()
    {
        var info = new JOBOBJECT_BASIC_ACCOUNTING_INFORMATION();
        return QueryInformationJobObject(_job, 1 /* JobObjectBasicAccountingInformation */, ref info, Marshal.SizeOf(info), out _) ? (int)info.ActiveProcesses : -1;
    }

    /// <summary>A window that may be the app's main one: visible, not owned by another window, with a title.</summary>
    public static bool MainCandidate(IntPtr window) =>
        IsWindowVisible(window) && GetWindow(window, 4 /* GW_OWNER */) == IntPtr.Zero && GetWindowTextLengthW(window) > 0;

    public static bool Exists(IntPtr window) => IsWindow(window);
    public static bool IsMaximized(IntPtr window) => IsZoomed(window);
    public static bool Resizable(IntPtr window) => (GetWindowLongPtrW(window, -16 /* GWL_STYLE */) & 0x40000 /* WS_THICKFRAME */) != 0;

    /// <summary>Moves the window (and maximises it there when asked), without waiting for it.</summary>
    public static void Apply(AppTabView.Move move)
    {
        const uint noZOrder = 0x4, noActivate = 0x10, async = 0x4000;
        var r = move.To;
        // A maximised window keeps its monitor: restored first, moved, then maximised on the new one.
        if (IsZoomed(move.Handle)) ShowWindowAsync(move.Handle, 9 /* SW_RESTORE */);
        SetWindowPos(move.Handle, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, noZOrder | noActivate | async);
        if (move.Maximize) ShowWindowAsync(move.Handle, 3 /* SW_MAXIMIZE */);
    }

    /// <summary>The window that has the keyboard, and its top-level window.</summary>
    public static IntPtr Foreground() => GetAncestor(GetForegroundWindow(), 2 /* GA_ROOT */);

    /// <summary>Gives the keyboard to <paramref name="window"/>: allowed because the helper sent the last input (§6.3).</summary>
    public static bool BringToFront(IntPtr window) => SetForegroundWindow(window);

    /// <summary>Asks the window to close, as its close button would.</summary>
    public static void Close(IntPtr window) => PostMessageW(window, 0x10 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);

    public void Dispose()
    {
        if (_job != IntPtr.Zero) CloseHandle(_job);
        _job = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenJobObjectW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLengthW(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr w, IntPtr l);
}
