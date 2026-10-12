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

    /// <summary>Ctrl+Alt+Del in this session: null when sent, else why not.</summary>
    string? SecureAttention() => RemoteHub.Agent.SecureAttention.Send();

    /// <summary>
    /// Whether input may go to the desktop that has the input now (argument: it's the secure desktop – sign-in screen,
    /// lock screen, UAC). Checked for every event after following the input desktop.
    /// </summary>
    Func<bool, bool> Allowed { set { } }
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

    /// <summary>Nothing goes through until the helper knows the session's rights.</summary>
    public Func<bool, bool> Allowed { private get; set; } = _ => false;

    public void Mouse(DesktopMonitor monitor, RemoteMouse input)
    {
        lock (_lock)
        {
            _desktop.Sync();
            if (!Allowed(_desktop.IsSecure))
            {
                Interlocked.Increment(ref _notAllowed);
                // Only let go of a button pressed before (no stuck button), without moving the pointer.
                if (input.Action == RemoteMouseAction.Up && _buttonsDown.Remove(input.Button))
                {
                    var (up, upData) = Button(input.Button, false);
                    if (up != 0) Send(new InputNative.INPUT { type = 0, mi = new InputNative.MOUSEINPUT { dwFlags = up, mouseData = upData } });
                }
                return;
            }
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
            // Not allowed here: only the release of a key pressed before goes through (no stuck key).
            if (!Allowed(_desktop.IsSecure) && !(up && _keysDown.Contains((input.ScanCode, extended))))
            {
                Interlocked.Increment(ref _notAllowed);
                return;
            }
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

    private static void Send(InputNative.INPUT input)
    {
        if (InputNative.SendInput(1, &input, sizeof(InputNative.INPUT)) == 1) Interlocked.Increment(ref _injected);
        else
        {
            Interlocked.Increment(ref _refused);
            _lastRefusal = Marshal.GetLastPInvokeError();
        }
    }

    private static int _injected, _refused, _lastRefusal, _notAllowed;

    /// <summary>For the log: what happened to the viewer's input in this helper so far.</summary>
    public static string Stats =>
        $"{_injected} input(s) sent to Windows, {_refused} refused{(_refused > 0 ? $" (last error {_lastRefusal})" : "")}, {_notAllowed} not allowed on this desktop";

    public void Dispose()
    {
        ReleaseAll();
        _desktop.Dispose();
    }
}

/// <summary>
/// Ctrl+Alt+Del for the session (sign-in screen, lock screen), sent by the desktop helper in that session. Windows only
/// accepts it when the policy "Disable or enable software Secure Attention Sequence" (SoftwareSASGeneration) allows
/// services – and doesn't pick up a value switched on just for a moment. So while the agent is installed it is set to
/// "Services" (1) if no policy configures it; a marker file records that the agent set it, and uninstalling removes it
/// only then. A configured policy is respected.
/// </summary>
internal static class SecureAttention
{
    private const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string Value = "SoftwareSASGeneration";
    private static readonly object Gate = new();

    /// <summary>Exists while the value is the agent's own (not a policy).</summary>
    private static string Marker => Path.Combine(AgentConfig.Folder, "sas.agent");

    /// <summary>The marker of earlier versions, which set the value for a few seconds only.</summary>
    private static string OldMarker => Path.Combine(AgentConfig.Folder, "sas.pending");

    /// <summary>The value now (for the log): null if not set.</summary>
    public static int? Current()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Key);
            return key?.GetValue(Value) is int v ? v : null;
        }
        catch { return null; }
    }

    /// <summary>Service start: allows services to send Ctrl+Alt+Del unless a policy configures it. Null, or what happened (for the log).</summary>
    public static string? Ensure()
    {
        lock (Gate)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(Key, writable: true);
                if (key is null) return null;
                // A value an earlier version left behind is ours as well.
                if (File.Exists(OldMarker))
                {
                    File.WriteAllText(Marker, "");
                    File.Delete(OldMarker);
                }
                var configured = key.GetValue(Value);
                if (configured is not null && !File.Exists(Marker)) return null; // a policy: left as it is
                if (configured is 1) return null;
                File.WriteAllText(Marker, "");
                key.SetValue(Value, 1, RegistryValueKind.DWord);
                InputNative.RefreshPolicyEx(true, InputNative.RP_FORCE);
                return "Ctrl+Alt+Del: allowed services to send it (SoftwareSASGeneration = 1)";
            }
            catch (Exception ex) { return "Ctrl+Alt+Del: could not allow it: " + ex.Message; }
        }
    }

    /// <summary>Null on success, otherwise why not.</summary>
    public static string? Send()
    {
        if (Ensure() is { } failed && failed.Contains("could not")) return failed;
        if (Current() is int v && v is not (1 or 3)) return "Ctrl+Alt+Del from software is turned off by a policy on this device.";
        try
        {
            // SendSAS returns nothing; what it leaves as the last error (and who called it) goes to the log.
            Marshal.SetLastPInvokeError(0);
            InputNative.SendSAS(false);
            var error = Marshal.GetLastPInvokeError();
            LastDetail = $"last error {error}{(error != 0 ? $" ({new System.ComponentModel.Win32Exception(error).Message})" : "")}, {Caller()}";
            return null;
        }
        catch (Exception ex) { return "Ctrl+Alt+Del could not be sent: " + ex.Message; }
    }

    /// <summary>For the log: what the last <see cref="Send"/> left behind (error code, caller).</summary>
    public static string LastDetail { get; private set; } = "";

    /// <summary>The calling process as Windows sees it: account, session, service group, TCB privilege.</summary>
    private static string Caller()
    {
        try
        {
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            var service = me.Groups?.Any(g => g.Value == "S-1-5-6") == true;
            var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            var thread = System.Security.Principal.WindowsIdentity.GetCurrent(true) is { } t ? $", thread impersonates {t.Name}" : "";
            return $"caller {me.Name}, session {session}, SERVICE group {(service ? "yes" : "no")}, Tcb {Privilege("SeTcbPrivilege")}{thread}";
        }
        catch (Exception ex) { return "caller unknown: " + ex.Message; }
    }

    private static string Privilege(string name)
    {
        try
        {
            using var p = new System.Diagnostics.Process { StartInfo = new("whoami.exe", "/priv /fo csv /nh") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true } };
            p.Start();
            var line = p.StandardOutput.ReadToEnd().Split('\n').FirstOrDefault(l => l.Contains(name));
            p.WaitForExit(3000);
            return line is null ? "missing" : line.Trim().Split(',').Last().Trim('"', '\r');
        }
        catch { return "?"; }
    }

    /// <summary>Uninstall: removes the value if the agent set it.</summary>
    public static void Remove()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(Marker) && !File.Exists(OldMarker)) return;
                using (var key = Registry.LocalMachine.OpenSubKey(Key, writable: true)) key?.DeleteValue(Value, throwOnMissingValue: false);
                File.Delete(Marker);
                File.Delete(OldMarker);
            }
            catch { /* left as it is */ }
        }
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

    [DllImport("user32.dll", SetLastError = true)] public static extern unsafe uint SendInput(uint count, INPUT* inputs, int size);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("sas.dll", SetLastError = true)] public static extern void SendSAS(bool asUser);
    [DllImport("userenv.dll")] public static extern bool RefreshPolicyEx(bool machine, uint options);
    public const uint RP_FORCE = 1;
}
