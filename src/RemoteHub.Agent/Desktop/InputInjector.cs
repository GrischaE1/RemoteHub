using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RemoteHub.Agent;

/// <summary>Where mouse and keyboard input from RemoteHub goes (the real desktop or a test double).</summary>
internal interface IInputSink
{
    void Mouse(DesktopMonitor monitor, RemoteMouse input);
    void Key(RemoteKey input);
    /// <summary>Lets go of every key and button RemoteHub still holds (end of session, lost connection).</summary>
    void ReleaseAll();
}

/// <summary>
/// SendInput on the input desktop. Keys are sent as scan codes, so the device's own keyboard layout applies (like RDP).
/// Mouse positions are pixels of the shown monitor, turned into absolute coordinates on the whole virtual screen.
/// </summary>
internal sealed unsafe class WindowsInput : IInputSink, IDisposable
{
    private readonly DesktopThread _desktop = new();
    private readonly HashSet<(ushort, bool)> _keysDown = [];
    private readonly HashSet<RemoteMouseButton> _buttonsDown = [];
    private readonly object _lock = new();

    public void Mouse(DesktopMonitor monitor, RemoteMouse input)
    {
        lock (_lock)
        {
            _desktop.Sync();
            int vx = InputNative.GetSystemMetrics(76), vy = InputNative.GetSystemMetrics(77);   // SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN
            int vw = InputNative.GetSystemMetrics(78), vh = InputNative.GetSystemMetrics(79);   // SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN
            if (vw <= 1 || vh <= 1) return;
            var x = Math.Clamp(monitor.X + input.X, monitor.X, monitor.X + monitor.Width - 1);
            var y = Math.Clamp(monitor.Y + input.Y, monitor.Y, monitor.Y + monitor.Height - 1);
            var m = new InputNative.MOUSEINPUT
            {
                dx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1)),
                dy = (int)Math.Round((y - vy) * 65535.0 / (vh - 1)),
                dwFlags = InputNative.MOVE | InputNative.ABSOLUTE | InputNative.VIRTUALDESK,
            };
            switch (input.Action)
            {
                case RemoteMouseAction.Down:
                case RemoteMouseAction.Up:
                    var down = input.Action == RemoteMouseAction.Down;
                    (var flag, var data) = Button(input.Button, down);
                    if (flag == 0) return;
                    m.dwFlags |= flag;
                    m.mouseData = data;
                    if (down) _buttonsDown.Add(input.Button); else _buttonsDown.Remove(input.Button);
                    break;
                case RemoteMouseAction.Wheel:
                    m.dwFlags |= InputNative.WHEEL;
                    m.mouseData = input.Delta;
                    break;
                case RemoteMouseAction.HorizontalWheel:
                    m.dwFlags |= InputNative.HWHEEL;
                    m.mouseData = input.Delta;
                    break;
            }
            Send(new InputNative.INPUT { type = 0, mi = m });
        }
    }

    public void Key(RemoteKey input)
    {
        lock (_lock)
        {
            _desktop.Sync();
            var extended = (input.Flags & RemoteKeyFlags.Extended) != 0;
            var up = (input.Flags & RemoteKeyFlags.Up) != 0;
            var k = new InputNative.KEYBDINPUT
            {
                wScan = input.ScanCode,
                dwFlags = InputNative.SCANCODE | (extended ? InputNative.EXTENDEDKEY : 0) | (up ? InputNative.KEYUP : 0),
            };
            Send(new InputNative.INPUT { type = 1, ki = k });
            if (up) _keysDown.Remove((input.ScanCode, extended)); else _keysDown.Add((input.ScanCode, extended));
        }
    }

    public void ReleaseAll()
    {
        lock (_lock)
        {
            foreach (var (scan, extended) in _keysDown.ToList())
                Send(new InputNative.INPUT { type = 1, ki = new InputNative.KEYBDINPUT { wScan = scan, dwFlags = InputNative.SCANCODE | InputNative.KEYUP | (extended ? InputNative.EXTENDEDKEY : 0) } });
            foreach (var b in _buttonsDown.ToList())
            {
                var (flag, data) = Button(b, false);
                if (flag != 0) Send(new InputNative.INPUT { type = 0, mi = new InputNative.MOUSEINPUT { dwFlags = flag, mouseData = data } });
            }
            _keysDown.Clear();
            _buttonsDown.Clear();
        }
    }

    private static (uint Flag, int Data) Button(RemoteMouseButton button, bool down) => button switch
    {
        RemoteMouseButton.Left => (down ? 0x0002u : 0x0004u, 0),
        RemoteMouseButton.Right => (down ? 0x0008u : 0x0010u, 0),
        RemoteMouseButton.Middle => (down ? 0x0020u : 0x0040u, 0),
        RemoteMouseButton.X1 => (down ? 0x0080u : 0x0100u, 1),
        RemoteMouseButton.X2 => (down ? 0x0080u : 0x0100u, 2),
        _ => (0u, 0),
    };

    private static void Send(InputNative.INPUT input) => InputNative.SendInput(1, &input, sizeof(InputNative.INPUT));

    public void Dispose()
    {
        ReleaseAll();
        _desktop.Dispose();
    }
}

/// <summary>
/// Ctrl+Alt+Del for the session (sign-in screen, lock screen). Windows only lets services send it when the policy
/// "Disable or enable software Secure Attention Sequence" allows services. If that policy isn't configured at all, the
/// agent allows services for the moment of the call and removes the value again; a configured policy is respected.
/// </summary>
internal static class SecureAttention
{
    private const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string Value = "SoftwareSASGeneration";

    /// <summary>Null on success, otherwise why not.</summary>
    public static string? Send()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Key, writable: true);
            var configured = key?.GetValue(Value);
            if (configured is int v && v is not (1 or 3)) return "Ctrl+Alt+Del from software is turned off by a policy on this device.";
            if (configured is null && key is not null) key.SetValue(Value, 1, RegistryValueKind.DWord);
            try { InputNative.SendSAS(false); }
            finally { if (configured is null) key?.DeleteValue(Value, throwOnMissingValue: false); }
            return null;
        }
        catch (Exception ex) { return "Ctrl+Alt+Del could not be sent: " + ex.Message; }
    }
}

internal static class InputNative
{
    public const uint MOVE = 0x0001, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000, WHEEL = 0x0800, HWHEEL = 0x1000;
    public const uint EXTENDEDKEY = 0x0001, KEYUP = 0x0002, SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy, mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll")] public static extern unsafe uint SendInput(uint count, INPUT* inputs, int size);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("sas.dll")] public static extern void SendSAS(bool asUser);
}
