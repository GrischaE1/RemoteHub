using System.Runtime.InteropServices;

namespace RemoteHub.Agent;

/// <summary>
/// The privacy screen's block of the device's own mouse and keyboard: low-level hooks that drop every event the agent
/// didn't inject itself (<see cref="InputNative.Tag"/>). Clicks on the banner still go through, so the person at the
/// device can always disconnect; moving the pointer stays possible to get there. Ctrl+Alt+Del (and Win+L) can't be
/// blocked – Windows handles them before any hook.
/// </summary>
/// <remarks>
/// The hooks run on their own thread with nothing else on it: Windows removes a low-level hook whose thread doesn't
/// answer in time, and the helper's UI thread can wait (a paste waits for its data).
/// </remarks>
internal static unsafe class InputBlocker
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const uint WM_QUIT = 0x0012, WM_MOUSEMOVE = 0x0200;

    private static readonly object Lock = new();
    private static Thread? _thread;
    private static uint _threadId;
    private static volatile int _blocked;
    // The banner's rectangle and that of an open dialog (screen pixels): local clicks there still go through.
    private static volatile (int Left, int Top, int Right, int Bottom)[] _allowed = [];

    public static bool Active => _thread is not null;

    /// <summary>For the log: how many local events were dropped.</summary>
    public static int Blocked => _blocked;

    /// <summary>Starts blocking; null when it works, else why not.</summary>
    public static string? Start()
    {
        lock (Lock)
        {
            if (_thread is not null) return null;
            string? error = null;
            using var ready = new ManualResetEventSlim();
            var t = new Thread(() =>
            {
                var module = GetModuleHandleW(null);
                var keyboard = SetWindowsHookExW(WH_KEYBOARD_LL, &KeyboardProc, module, 0);
                var mouse = SetWindowsHookExW(WH_MOUSE_LL, &MouseProc, module, 0);
                if (keyboard == IntPtr.Zero || mouse == IntPtr.Zero)
                {
                    error = $"the input hooks couldn't be set (error {Marshal.GetLastPInvokeError()})";
                    if (keyboard != IntPtr.Zero) UnhookWindowsHookEx(keyboard);
                    if (mouse != IntPtr.Zero) UnhookWindowsHookEx(mouse);
                    ready.Set();
                    return;
                }
                MSG msg;
                PeekMessageW(&msg, IntPtr.Zero, 0, 0, 0); // the thread's queue exists before Stop can post to it
                _threadId = GetCurrentThreadId();
                ready.Set();
                while (GetMessageW(&msg, IntPtr.Zero, 0, 0) > 0) { }
                UnhookWindowsHookEx(keyboard);
                UnhookWindowsHookEx(mouse);
            }) { IsBackground = true, Name = "Privacy input block" };
            t.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
            if (error is not null || _threadId == 0) return error ?? "the input block didn't start";
            _thread = t;
            return null;
        }
    }

    public static void Stop()
    {
        lock (Lock)
        {
            if (_thread is not { } t) return;
            PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
            t.Join(TimeSpan.FromSeconds(2));
            (_thread, _threadId) = (null, 0);
        }
    }

    /// <summary>Where the banner (and an open dialog of the helper) are now, in screen pixels; local clicks there go through.</summary>
    public static void AllowClicks((int Left, int Top, int Right, int Bottom)[] areas) => _allowed = areas;

    /// <summary>Whether a local mouse event at this point goes through: moves always, clicks and the wheel only on the helper's own windows.</summary>
    internal static bool LetMouseThrough(uint message, int x, int y, (int Left, int Top, int Right, int Bottom)[] areas)
    {
        if (message == WM_MOUSEMOVE) return true;
        foreach (var a in areas)
            if (x >= a.Left && x < a.Right && y >= a.Top && y < a.Bottom) return true;
        return false;
    }

    [UnmanagedCallersOnly]
    private static nint KeyboardProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && ((KBDLLHOOKSTRUCT*)lParam)->dwExtraInfo != InputNative.Tag)
        {
            Interlocked.Increment(ref _blocked);
            return 1;
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static nint MouseProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var m = (MSLLHOOKSTRUCT*)lParam;
            if (m->dwExtraInfo != InputNative.Tag && !LetMouseThrough((uint)wParam, m->X, m->Y, _allowed))
            {
                Interlocked.Increment(ref _blocked);
                return 1;
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public int X, Y; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int id, delegate* unmanaged<int, nint, nint, nint> proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(IntPtr hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(MSG* msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(MSG* msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint msg, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}

/// <summary>
/// While the banner lets the admin's clicks through: watches for the device's own mouse and reports the first event
/// from it, so the banner becomes the user's to click again. A low-level hook on its own thread, like
/// <see cref="InputBlocker"/>; it only runs while it is needed.
/// </summary>
/// <remarks>
/// Only a physical mouse counts. A full-screen RDP client (Hyper-V's VM connection, mstsc) moves the cursor itself after
/// each of the admin's moves, to where the remote pointer is; taken for the user's mouse, that made the banner clickable
/// again before every one of the admin's clicks, and they couldn't reach the client's connection bar below it.
/// </remarks>
internal static unsafe class LocalPointerWatch
{
    private const int WH_MOUSE_LL = 14;
    private const uint WM_QUIT = 0x0012, WM_MOUSEMOVE = 0x0200;
    private const uint LLMHF_INJECTED = 0x01, LLMHF_LOWER_IL_INJECTED = 0x02;

    private static readonly object Lock = new();
    private static Thread? _thread;
    private static uint _threadId;
    private static Action? _moved;
    private static long _adminAt; // where the admin's pointer is: X in the low 32 bits, Y in the high ones

    /// <summary>Where the admin's pointer is now (screen pixels): a move to exactly there isn't the user's.</summary>
    public static void AdminAt(int x, int y) => Interlocked.Exchange(ref _adminAt, (long)y << 32 | (uint)x);

    /// <summary>
    /// Whether a mouse event comes from the device's own mouse: not injected – neither by the agent nor by another program
    /// (an RDP client placing the cursor) – and, for a move, not one to where the admin's pointer already is.
    /// </summary>
    internal static bool FromUsersMouse(uint message, int x, int y, uint flags, IntPtr extraInfo, (int X, int Y) admin) =>
        extraInfo != InputNative.Tag && (flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) == 0 &&
        !(message == WM_MOUSEMOVE && x == admin.X && y == admin.Y);

    public static void Start(Action moved)
    {
        lock (Lock)
        {
            _moved = moved;
            if (_thread is not null) return;
            using var ready = new ManualResetEventSlim();
            var t = new Thread(() =>
            {
                var hook = SetWindowsHookExW(WH_MOUSE_LL, &MouseProc, GetModuleHandleW(null), 0);
                MSG msg;
                PeekMessageW(&msg, IntPtr.Zero, 0, 0, 0); // the queue exists before Stop can post to it
                _threadId = GetCurrentThreadId();
                ready.Set();
                if (hook == IntPtr.Zero) return; // no hook: the banner stays click-through until the admin's pointer leaves it
                while (GetMessageW(&msg, IntPtr.Zero, 0, 0) > 0) { }
                UnhookWindowsHookEx(hook);
            }) { IsBackground = true, Name = "Banner: local mouse" };
            t.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
            _thread = t;
        }
    }

    public static void Stop()
    {
        lock (Lock)
        {
            _moved = null;
            if (_thread is not { } t) return;
            PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
            t.Join(TimeSpan.FromSeconds(2));
            (_thread, _threadId) = (null, 0);
        }
    }

    [UnmanagedCallersOnly]
    private static nint MouseProc(int code, nint wParam, nint lParam)
    {
        // Only report – never block, and return at once (Windows drops a slow low-level hook).
        if (code >= 0)
        {
            var m = (MSLLHOOKSTRUCT*)lParam;
            var admin = Interlocked.Read(ref _adminAt);
            if (FromUsersMouse((uint)wParam, m->X, m->Y, m->flags, m->dwExtraInfo, ((int)admin, (int)(admin >> 32))) &&
                Interlocked.Exchange(ref _moved, null) is { } moved)
                try { moved(); } catch { /* the UI is gone */ }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public int X, Y; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int id, delegate* unmanaged<int, nint, nint, nint> proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(IntPtr hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(MSG* msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(MSG* msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint msg, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
