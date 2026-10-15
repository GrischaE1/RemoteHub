using System.Runtime.InteropServices;

namespace RemoteHub.Agent;

/// <summary>
/// While the privacy screen is up: no screen snips. The privacy screen is kept out of every capture – also out of a snip –
/// and Windows shows a snip's frozen picture full-screen above every topmost window: the screen at the device would show
/// what the overlay hides. So the snip keys (Print Screen, Win+Shift+S) and the window switchers (Alt+Tab, Win+Tab) are dropped – the admin's and the device's own –
/// and a snipping tool that opens anyway (started from the Start menu, a timer) is closed as soon as it shows a window.
/// </summary>
/// <remarks>
/// A low-level keyboard hook and an out-of-context WinEvent hook on their own thread, like <see cref="InputBlocker"/>.
/// </remarks>
internal static unsafe class SnipGuard
{
    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_QUIT = 0x0012, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const uint EVENT_OBJECT_SHOW = 0x8002, WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int VK_SNAPSHOT = 0x2C, VK_S = 0x53, VK_TAB = 0x09, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    /// <summary>The programs that show a snip's frozen picture full-screen (lower case, without the folder).</summary>
    internal static readonly string[] SnipPrograms = ["screenclippinghost.exe", "snippingtool.exe", "screensketch.exe"];

    private static readonly object Lock = new();
    private static Thread? _thread;
    private static uint _threadId;
    private static Action<string>? _noted;

    /// <summary>Starts guarding (again a no-op while it runs); <paramref name="noted"/> gets what was stopped, for the log.</summary>
    public static void Start(Action<string> noted)
    {
        lock (Lock)
        {
            _noted = noted;
            if (_thread is not null) return;
            using var ready = new ManualResetEventSlim();
            var t = new Thread(() =>
            {
                var keyboard = SetWindowsHookExW(WH_KEYBOARD_LL, &KeyboardProc, GetModuleHandleW(null), 0);
                var shown = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, &WindowShown, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
                MSG msg;
                PeekMessageW(&msg, IntPtr.Zero, 0, 0, 0); // the queue exists before Stop can post to it
                _threadId = GetCurrentThreadId();
                ready.Set();
                if (keyboard == IntPtr.Zero) Note("Privacy screen: the snip keys can't be blocked (no keyboard hook)");
                if (shown == IntPtr.Zero) Note("Privacy screen: snipping tools can't be watched (no WinEvent hook)");
                while (GetMessageW(&msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(&msg); DispatchMessageW(&msg); }
                if (keyboard != IntPtr.Zero) UnhookWindowsHookEx(keyboard);
                if (shown != IntPtr.Zero) UnhookWinEvent(shown);
            }) { IsBackground = true, Name = "Privacy screen: snip guard" };
            t.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
            _thread = t;
        }
    }

    public static void Stop()
    {
        lock (Lock)
        {
            _noted = null;
            if (_thread is not { } t) return;
            PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
            t.Join(TimeSpan.FromSeconds(2));
            (_thread, _threadId) = (null, 0);
        }
    }

    /// <summary>
    /// Whether a key press is dropped: a snip (Print Screen with any modifier, Win+Shift+S), or Alt+Tab and Win+Tab –
    /// their window thumbnails are drawn above the privacy screen, too.
    /// </summary>
    internal static bool IsSnipKey(int vk, bool win, bool shift, bool alt) =>
        vk == VK_SNAPSHOT || vk == VK_S && win && shift || vk == VK_TAB && (alt || win);

    /// <summary>Whether a program (its full path or file name) is one of the snipping tools.</summary>
    internal static bool IsSnipProgram(string path) => SnipPrograms.Contains(Path.GetFileName(path).ToLowerInvariant());

    private static void Note(string text)
    {
        try { _noted?.Invoke(text); } catch { /* the session is gone */ }
    }

    [UnmanagedCallersOnly]
    private static nint KeyboardProc(int code, nint wParam, nint lParam)
    {
        // The press is dropped, its release goes through (nothing stays held). Returns at once: Windows drops a slow hook.
        if (code >= 0 && (uint)wParam is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            var vk = (int)((KBDLLHOOKSTRUCT*)lParam)->vkCode;
            var win = (GetAsyncKeyState(VK_LWIN) | GetAsyncKeyState(VK_RWIN)) < 0;
            var alt = (((KBDLLHOOKSTRUCT*)lParam)->flags & 0x20) != 0 || GetAsyncKeyState(VK_MENU) < 0; // LLKHF_ALTDOWN
            if (IsSnipKey(vk, win, GetAsyncKeyState(VK_SHIFT) < 0, alt)) return 1;
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static void WindowShown(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero || GetAncestor(hwnd, 2 /* GA_ROOT */) != hwnd) return;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return;
            var process = OpenProcess(0x1000 | 0x0001, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE
            if (process == IntPtr.Zero) return;
            try
            {
                var buffer = stackalloc char[1024];
                var size = 1024;
                if (!QueryFullProcessImageNameW(process, 0, buffer, ref size)) return;
                var path = new string(buffer, 0, size);
                if (!IsSnipProgram(path)) return;
                var closed = TerminateProcess(process, 1);
                Note(closed ? $"Privacy screen: {Path.GetFileName(path)} was closed – a snip would show the screen at the device"
                    : $"Privacy screen: {Path.GetFileName(path)} couldn't be closed (error {Marshal.GetLastPInvokeError()})");
            }
            finally { CloseHandle(process); }
        }
        catch { /* never let an exception cross into Windows */ }
    }

    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int id, delegate* unmanaged<int, nint, nint, nint> proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(IntPtr hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, delegate* unmanaged<IntPtr, uint, IntPtr, int, int, uint, uint, void> proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetMessageW(MSG* msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(MSG* msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(MSG* msg);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(MSG* msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint msg, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char* name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
