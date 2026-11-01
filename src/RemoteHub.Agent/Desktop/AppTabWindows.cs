using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace RemoteHub.Agent;

/// <summary>
/// The app of an app tab as Windows has it, for the helper (LocalSystem, in the admin's session): which windows are the
/// app's – their process is in the tab's job, which the service made (app-tabs-design §6.2), or, for a Store app, they carry
/// its AppUserModelID (§6.6) – and moving, maximising and bringing them to the front. Windows are moved asynchronously, so
/// an app that hangs never holds up the capture.
/// </summary>
internal sealed partial class AppTabWindows : IDisposable
{
    private IntPtr _job;
    // A window's AppUserModelID, by handle: read again after a while (a handle can be reused).
    private readonly Dictionary<IntPtr, (string? Id, long At)> _appIds = [];
    [ThreadStatic] private static bool _comReady;
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

    /// <summary>
    /// The window carries <paramref name="appId"/> as its AppUserModelID – a Store app's window drawn by another process
    /// (ApplicationFrameHost for a UWP app). Read through the window's property store, at most every 2 s per window.
    /// </summary>
    public bool Carries(IntPtr window, string appId)
    {
        var now = Environment.TickCount64;
        if (!_appIds.TryGetValue(window, out var known) || now - known.At >= ProcessCacheMs)
        {
            if (_appIds.Count > 512) _appIds.Clear();
            _appIds[window] = known = (ReadAppId(window), now);
        }
        return string.Equals(known.Id, appId, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>The window's AppUserModelID, read now (null when it has none).</summary>
    public static string? ReadAppIdOf(IntPtr window) => ReadAppId(window);

    private static unsafe string? ReadAppId(IntPtr window)
    {
        if (!_comReady)
        {
            _ = CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */); // already in an apartment is fine too
            _comReady = true;
        }
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(window, in iid, out var raw) != 0 || raw == 0) return null;
        try
        {
            var store = (IPropertyStore)Wrappers.GetOrCreateObjectForComInstance(raw, CreateObjectFlags.UniqueInstance);
            var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 }; // PKEY_AppUserModel_ID
            var value = new PropVariant();
            if (store.GetValue(&key, &value) != 0) return null;
            try { return value.Type == 31 /* VT_LPWSTR */ && value.Pointer != 0 ? new string((char*)value.Pointer) : null; }
            finally { PropVariantClear(&value); }
        }
        finally { Marshal.Release(raw); }
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

    /// <summary>
    /// Gives the keyboard to <paramref name="window"/> (§6.3). Windows lets only the process that sent the last input move
    /// the foreground, and that may be another tab's helper – a click in red's tab, then typing in green's (lab D-APP-04,
    /// where the keys then reached no app). So this helper sends input first: the pointer nudged a pixel and back, as
    /// <see cref="WindowsScreen.WakeDisplay"/> does. Still refused, the call borrows the foreground window's input state.
    /// </summary>
    public static unsafe bool BringToFront(IntPtr window)
    {
        var nudge = stackalloc InputNative.INPUT[2];
        nudge[0] = new InputNative.INPUT { type = 0, mi = new InputNative.MOUSEINPUT { dx = 1, dwFlags = InputNative.MOVE, dwExtraInfo = InputNative.Tag } };
        nudge[1] = new InputNative.INPUT { type = 0, mi = new InputNative.MOUSEINPUT { dx = -1, dwFlags = InputNative.MOVE, dwExtraInfo = InputNative.Tag } };
        InputNative.SendInput(2, nudge, sizeof(InputNative.INPUT));
        if (SetForegroundWindow(window)) return true;
        var theirs = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var mine = GetCurrentThreadId();
        if (theirs == 0 || theirs == mine || !AttachThreadInput(mine, theirs, true)) return false;
        try { return SetForegroundWindow(window); }
        finally { AttachThreadInput(mine, theirs, false); }
    }

    /// <summary>Asks the window to close, as its close button would.</summary>
    public static void Close(IntPtr window) => PostMessageW(window, 0x10 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);

    public void Dispose()
    {
        if (_job != IntPtr.Zero) CloseHandle(_job);
        _job = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type, Reserved1, Reserved2, Reserved3;
        public nint Pointer, Pointer2;
    }

    [GeneratedComInterface, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal unsafe partial interface IPropertyStore
    {
        [PreserveSig] int GetCount(uint* count);
        [PreserveSig] int GetAt(uint index, void* key);
        [PreserveSig] int GetValue(void* key, void* value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr window, in Guid iid, out nint store);
    [DllImport("ole32.dll")] private static extern unsafe int PropVariantClear(PropVariant* value);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint model);
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
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool on);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr w, IntPtr l);
}
