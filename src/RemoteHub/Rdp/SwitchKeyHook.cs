using System.Runtime.InteropServices;
using System.Windows.Threading;
using RemoteHub.Services;

namespace RemoteHub.Rdp;

/// <summary>
/// Catches the session switch keys (<see cref="SwitchHotkeys"/>) while an RDP session has the keyboard, before the RDP
/// control sees them. The control's focus-release hotkeys never raised OnFocusReleased in the lab (R-RDP-12), and with
/// "Send Windows key combinations" (KeyboardHookMode 1) the control's own low-level hook sends every Ctrl+Alt
/// combination to the remote session. A low-level hook installed after the control's is called before it, so the
/// owner (re)installs this one whenever a session gains the keyboard.
///
/// Windows calls the hook on the thread that installed it (the UI thread) while it waits for messages, and removes a
/// hook that takes too long: the callback only decides and swallows; the switch itself runs later on the dispatcher.
/// </summary>
internal sealed class SwitchKeyHook : IDisposable
{
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    private const uint LLKHF_EXTENDED = 0x01, LLKHF_UP = 0x80;

    private readonly Func<RdpSession?> _focusedSession;
    private readonly Action<RdpSession, int> _switch;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    // Kept in a field: the delegate handed to Windows must live as long as the hook.
    private readonly LowLevelKeyboardProc _proc;
    private readonly uint _processId = (uint)Environment.ProcessId;
    private IntPtr _hook;
    private bool _disposed;

    // Modifier state as this hook saw it. GetAsyncKeyState can't be trusted here: a key the RDP control's own hook
    // swallowed never updates it.
    private bool _ctrl, _leftAlt, _shift;
    /// <summary>The switch key last swallowed: its auto-repeats and key-up are swallowed too, without switching again.</summary>
    private int _swallowedVk;

    /// <param name="focusedSession">The RDP session that has the keyboard focus now, if any (called on the UI thread).</param>
    /// <param name="switchFrom">Switches away from that session: -1 previous, +1 next (run on the dispatcher).</param>
    public SwitchKeyHook(Func<RdpSession?> focusedSession, Action<RdpSession, int> switchFrom)
    {
        _focusedSession = focusedSession;
        _switch = switchFrom;
        _proc = OnKey;
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>Installs the hook, or moves it to the front of the chain (ahead of a hook the RDP control installed since).</summary>
    public void Install()
    {
        Remove();
        if (_disposed) return; // sessions closing during shutdown must not install it again
        _ctrl = IsDown(VK_LCONTROL) || IsDown(VK_RCONTROL);
        _leftAlt = IsDown(VK_LMENU);
        _shift = IsDown(VK_LSHIFT) || IsDown(VK_RSHIFT);
        _hook = SetWindowsHookExW(13 /* WH_KEYBOARD_LL */, _proc, GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero) DiagLog.Write($"Session switch key hook not installed (error {Marshal.GetLastPInvokeError()})");
    }

    public void Remove()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _swallowedVk = 0;
    }

    public void Dispose()
    {
        _disposed = true;
        Remove();
    }

    private unsafe IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && Swallow((KBDLLHOOKSTRUCT*)lParam)) return 1;
        }
        catch (Exception ex)
        {
            // An exception must not unwind into Windows' hook dispatch: the key just goes on as usual.
            DiagLog.Write("Session switch key hook: " + ex.Message);
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private unsafe bool Swallow(KBDLLHOOKSTRUCT* k)
    {
        var vk = (int)k->vkCode;
        var down = (k->flags & LLKHF_UP) == 0;
        switch (vk)
        {
            // Injected input may report the generic codes; an extended VK_MENU is the right Alt key.
            case VK_CONTROL or VK_LCONTROL or VK_RCONTROL: _ctrl = down; return false;
            case VK_LMENU: _leftAlt = down; return false;
            case VK_MENU: if ((k->flags & LLKHF_EXTENDED) == 0) _leftAlt = down; return false;
            case VK_RMENU: return false;
            case VK_SHIFT or VK_LSHIFT or VK_RSHIFT: _shift = down; return false;
        }
        if (vk == _swallowedVk)
        {
            if (!down) _swallowedVk = 0;
            return true;
        }
        if (!down) return false;
        var direction = SwitchHotkeys.Match(vk, _ctrl, _leftAlt, _shift, SwitchHotkeys.Current);
        if (direction == 0 || !OursInForeground() || _focusedSession() is not { } from) return false;
        _swallowedVk = vk;
        _dispatcher.BeginInvoke(() => _switch(from, direction));
        return true;
    }

    private bool OursInForeground()
    {
        var w = GetForegroundWindow();
        return w != IntPtr.Zero && GetWindowThreadProcessId(w, out var pid) != 0 && pid == _processId;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int id, LowLevelKeyboardProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
}
