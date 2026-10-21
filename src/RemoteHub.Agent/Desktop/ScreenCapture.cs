using System.Runtime.InteropServices;

namespace RemoteHub.Agent;

/// <summary>Pixels of one monitor, top-down BGRA, pitch = width × 4.</summary>
internal sealed class FrameBuffer
{
    public byte[] Pixels = [];
    public int Width, Height;

    public void Ensure(int width, int height)
    {
        if (Pixels.Length < width * height * 4) Pixels = new byte[width * height * 4];
        (Width, Height) = (width, height);
    }
}

internal enum CaptureResult { Changed, Unchanged, Failed }

/// <summary>Mouse pointer image: BGRA with straight alpha.</summary>
internal sealed record CursorImage(int Width, int Height, int HotX, int HotY, byte[] Bgra);

/// <summary>What the desktop helper reads from the screen (the real one or a test double).</summary>
internal interface IScreenSource : IDisposable
{
    IReadOnlyList<DesktopMonitor> Monitors();

    /// <summary>Follows the input desktop (sign-in screen, UAC prompt, lock screen). True if it changed.</summary>
    bool SyncDesktop();

    /// <summary>True while the input desktop is the secure one (sign-in screen, lock screen, UAC prompt).</summary>
    bool SecureDesktop { get; }

    /// <summary>Waits up to <paramref name="timeoutMs"/> for a new image of the monitor.</summary>
    CaptureResult Capture(DesktopMonitor monitor, int timeoutMs, FrameBuffer target);

    /// <summary>Pointer position relative to the monitor, and its image when it changed since the last call.</summary>
    RemoteCursor Cursor(DesktopMonitor monitor, out CursorImage? changedShape);

    /// <summary>Sets the monitor to the display mode closest to (not larger than) this size – temporarily.</summary>
    void FitResolution(DesktopMonitor monitor, int width, int height) { }

    /// <summary>Back to the resolution Windows had before <see cref="FitResolution"/>.</summary>
    void RestoreResolution() { }

    /// <summary>How the last picture was taken (shown in RemoteHub's connection info).</summary>
    string CaptureMethod => "";

    /// <summary>The last resolution change (asked → now), "" when none.</summary>
    string FitInfo => "";

    /// <summary>For the log: the input desktop's name as last seen, and the error when it couldn't be opened.</summary>
    string DesktopDescription => "";

    /// <summary>Windows' lock screen (LogonUI) is still running in this session; it ends when the session is unlocked.</summary>
    bool LockScreenUp => false;

    /// <summary>Something for the agent's log since the last call (capture method changed and why), "" when nothing.</summary>
    string TakeLogNote() => "";

    /// <summary>Capture with GDI only (the GPU picture stood still earlier in this connection).</summary>
    void PreferGdi() { }

    /// <summary>
    /// On: the display is required – a display Windows switched off (idle, 60 s on the lock screen) comes back and stays
    /// on while connected. Off: Windows' own timeouts again. For the calling thread (the capture thread).
    /// </summary>
    void KeepDisplayOn(bool on) { }

    /// <summary>
    /// The picture stays black: switches a dark display on the way input does (a one-pixel pointer move and back) and
    /// asks the monitors to power on. Returns what happened, for the log.
    /// </summary>
    string WakeDisplay() => "";
}

/// <summary>
/// The screen of the session the helper runs in: DXGI Desktop Duplication (GPU, only changed frames), GDI as fallback
/// (VMs without a GPU, rotated monitors, remote sessions). Follows desktop switches – the helper runs as LocalSystem in
/// the session, so the sign-in screen and UAC prompts can be captured too.
/// </summary>
internal sealed unsafe class WindowsScreen : IScreenSource
{
    private readonly DesktopThread _desktop = new();
    private DxgiDuplication? _dxgi;
    private string? _dxgiDevice;
    private bool _dxgiFailed;
    private GdiScreen? _gdi;
    private bool _initialFromGdi;
    private IntPtr _lastCursor;

    public bool SecureDesktop => _desktop.IsSecure;

    private readonly System.Diagnostics.Stopwatch _lockCheck = new();
    private bool _lockScreenUp;

    /// <summary>Checked at most every 250 ms. Unknown counts as unlocked – then the user is asked.</summary>
    public bool LockScreenUp
    {
        get
        {
            if (_lockCheck.IsRunning && _lockCheck.ElapsedMilliseconds < 250) return _lockScreenUp;
            _lockCheck.Restart();
            try
            {
                var mine = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                var logonUi = System.Diagnostics.Process.GetProcessesByName("LogonUI");
                try { _lockScreenUp = logonUi.Any(p => p.SessionId == mine); }
                finally { foreach (var p in logonUi) p.Dispose(); }
            }
            catch { _lockScreenUp = false; }
            return _lockScreenUp;
        }
    }

    public string DesktopDescription => $"input desktop \"{_desktop.Name}\"{(_desktop.LastError != 0 ? $", couldn't open it (error {_desktop.LastError})" : "")}";

    /// <summary><see cref="DesktopMonitor.Name"/> of the picture of all monitors (one GDI copy of the whole desktop).</summary>
    public const string AllMonitorsName = "*all*";

    private string _method = "";
    public string CaptureMethod => _method;
    public string FitInfo => DisplayModes.LastFit;

    public IReadOnlyList<DesktopMonitor> Monitors() => DisplayMonitors.List();

    public bool SyncDesktop()
    {
        if (!_desktop.Sync()) return false;
        // A new desktop: the duplication of the old one is invalid.
        ResetDxgi();
        _dxgiFailed = _dxgiStalled; // once the GPU picture stood still, GDI for the rest of the connection
        _probePrimed = false;
        _dxgiQuiet.Restart();
        _lastCursor = IntPtr.Zero;
        return true;
    }

    public CaptureResult Capture(DesktopMonitor monitor, int timeoutMs, FrameBuffer target)
    {
        // Desktop Duplication is per monitor: the picture of all of them is one GDI copy of the whole desktop. The secure
        // desktop (sign-in, lock and UAC screens) is always copied with GDI: duplicating it with the GPU is refused or
        // stalls on some drivers, and speed doesn't matter there.
        if (!_dxgiFailed && monitor.Name != AllMonitorsName && !SecureDesktop)
        {
            try
            {
                if (_dxgi is null || _dxgiDevice != monitor.Name)
                {
                    ResetDxgi();
                    _dxgi = DxgiDuplication.TryCreate(monitor);
                    _dxgiDevice = monitor.Name;
                    _initialFromGdi = false;
                    // a new duplication gets its own quiet second and reference copy (StoodStill)
                    _dxgiQuiet.Restart();
                    _probePrimed = false;
                    if (_dxgi is null) _dxgiFailed = true;
                }
                if (_dxgi is not null)
                {
                    _method = "Desktop Duplication (GPU)";
                    var r = _dxgi.Capture(timeoutMs, target);
                    if (r == CaptureResult.Unchanged && !_dxgi.HasImage && !_initialFromGdi)
                    {
                        // Duplication only delivers what changes after it started: the first picture of a still screen comes from GDI.
                        _initialFromGdi = true;
                        _gdi ??= new GdiScreen();
                        return _gdi.Capture(monitor, target);
                    }
                    // A change the GPU reported makes GDI's reference copy stale: it is taken again after the next quiet
                    // second, so only a change during a second the GPU stayed quiet counts as standing still (lab D-VID-03:
                    // changes reported before the quiet second were counted, and 15-60 fps runs fell to GDI's 4 fps).
                    if (r == CaptureResult.Changed) { _dxgiQuiet.Restart(); _probePrimed = false; }
                    else if (r == CaptureResult.Unchanged && StoodStill(monitor, target)) return CaptureResult.Changed;
                    if (r != CaptureResult.Failed) return r;
                    ResetDxgi(); // access lost (desktop switch, resolution change): new duplication next time
                    return CaptureResult.Unchanged;
                }
            }
            catch (Exception)
            {
                ResetDxgi();
                _dxgiFailed = true;
            }
        }
        _gdi ??= new GdiScreen();
        _method = "GDI";
        var result = _gdi.Capture(monitor, target);
        if (result == CaptureResult.Unchanged && timeoutMs > 0) Thread.Sleep(Math.Min(timeoutMs, 15));
        return result;
    }

    // ---- GPU picture that stands still (seen on servers without a monitor): Desktop Duplication reports "unchanged"
    // while the screen changes. After a second without a GPU frame GDI takes a reference copy; if after another second
    // still without one GDI sees a change, GDI is used for the rest of the connection (this helper). Any GPU frame in
    // between drops the reference copy.
    private readonly System.Diagnostics.Stopwatch _dxgiQuiet = System.Diagnostics.Stopwatch.StartNew();
    private FrameBuffer? _probe;
    private bool _probePrimed;
    private bool _dxgiStalled;
    private string _logNote = "";

    public string TakeLogNote() { var n = _logNote; _logNote = ""; return n; }

    public void KeepDisplayOn(bool on) =>
        SetThreadExecutionState(on ? 0x80000003u /* ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED */ : 0x80000000u /* ES_CONTINUOUS */);

    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);

    public string WakeDisplay()
    {
        SetThreadExecutionState(0x80000003u);
        // Windows switches a display that went dark back on for input, not for a power request alone.
        var nudge = stackalloc InputNative.INPUT[2];
        nudge[0] = new InputNative.INPUT { type = 0, mi = new InputNative.MOUSEINPUT { dx = 1, dwFlags = InputNative.MOVE, dwExtraInfo = InputNative.Tag } };
        nudge[1] = new InputNative.INPUT { type = 0, mi = new InputNative.MOUSEINPUT { dx = -1, dwFlags = InputNative.MOVE, dwExtraInfo = InputNative.Tag } };
        var sent = InputNative.SendInput(2, nudge, sizeof(InputNative.INPUT));
        var error = sent == 2 ? 0 : Marshal.GetLastPInvokeError();
        PostMessageW((IntPtr)0xFFFF, 0x0112 /* WM_SYSCOMMAND */, (IntPtr)0xF170 /* SC_MONITORPOWER */, (IntPtr)(-1) /* on */);
        return sent == 2 ? "pointer nudged, monitors asked to power on" : $"pointer nudge refused (error {error}), monitors asked to power on";
    }

    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public void PreferGdi()
    {
        ResetDxgi();
        _dxgiFailed = _dxgiStalled = true;
    }

    private bool StoodStill(DesktopMonitor monitor, FrameBuffer target)
    {
        if (_dxgiQuiet.ElapsedMilliseconds < 1000) return false;
        _dxgiQuiet.Restart();
        _gdi ??= new GdiScreen();
        _probe ??= new FrameBuffer();
        if (!_probePrimed)
        {
            _gdi.Capture(monitor, _probe); // reference copy
            _probePrimed = true;
            return false;
        }
        if (_gdi.Capture(monitor, _probe) != CaptureResult.Changed) return false;
        // The screen changed, the GPU said nothing: GDI from now on (this desktop).
        ResetDxgi();
        _dxgiFailed = _dxgiStalled = true;
        _logNote = "the GPU picture (Desktop Duplication) stood still while the screen changed – using GDI for this connection";
        _gdi.Capture(monitor, target);
        _method = "GDI";
        return true;
    }

    private string? _resized;

    public void FitResolution(DesktopMonitor monitor, int width, int height)
    {
        if (_resized is not null && _resized != monitor.Name) RestoreResolution();
        if (DisplayModes.Fit(monitor.Name, width, height)) _resized = monitor.Name;
    }

    public void RestoreResolution()
    {
        if (_resized is null) return;
        DisplayModes.Restore(_resized);
        _resized = null;
    }

    public RemoteCursor Cursor(DesktopMonitor monitor, out CursorImage? changedShape)
    {
        changedShape = null;
        var info = new DesktopNative.CURSORINFO { cbSize = sizeof(DesktopNative.CURSORINFO) };
        if (!DesktopNative.GetCursorInfo(ref info)) return new RemoteCursor(0, 0, false);
        var visible = (info.flags & 1) != 0 && info.hCursor != IntPtr.Zero;
        if (visible && info.hCursor != _lastCursor)
        {
            _lastCursor = info.hCursor;
            changedShape = CursorShapes.Render(info.hCursor);
        }
        return new RemoteCursor(info.ptScreenPos.X - monitor.X, info.ptScreenPos.Y - monitor.Y, visible);
    }

    private void ResetDxgi()
    {
        _dxgi?.Dispose();
        _dxgi = null;
        _dxgiDevice = null;
    }

    public void Dispose()
    {
        RestoreResolution();
        ResetDxgi();
        _gdi?.Dispose();
        _desktop.Dispose();
    }
}

/// <summary>
/// Display modes of a monitor. Changes are made with CDS_FULLSCREEN: temporary, not written to the registry – Windows
/// returns to the user's own resolution when the helper ends, even if it crashes.
/// </summary>
internal static unsafe class DisplayModes
{
    public static IReadOnlyList<(int Width, int Height, int Bits, int Hz)> List(string device)
    {
        var modes = new HashSet<(int, int, int, int)>();
        var dm = New();
        for (var i = 0; EnumDisplaySettingsW(device, i, ref dm); i++) modes.Add(((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmBitsPerPel, (int)dm.dmDisplayFrequency));
        return [.. modes];
    }

    /// <summary>
    /// The best listed mode for a window of <paramref name="width"/> × <paramref name="height"/>: of the modes that fit,
    /// the one that fills the window best when scaled to it (fewest black bars), then the one with more pixels.
    /// </summary>
    internal static (int Width, int Height)? Choose(IEnumerable<(int Width, int Height)> modes, int width, int height)
    {
        var list = modes.Distinct().ToList();
        if (list.Count == 0) return null;
        if (list.Contains((width, height))) return (width, height);
        var fitting = list.Where(m => m.Width <= width && m.Height <= height).ToList();
        if (fitting.Count == 0) return list.OrderBy(m => m.Width * m.Height).First();
        return fitting.OrderByDescending(m => Math.Round(Filled(m.Width, m.Height, width, height), 2)).ThenByDescending(m => (long)m.Width * m.Height).First();
    }

    /// <summary>Share of the window a w × h picture covers when scaled into it (1 = no black bars).</summary>
    internal static double Filled(int w, int h, int windowWidth, int windowHeight)
    {
        var scale = Math.Min(windowWidth / (double)w, windowHeight / (double)h);
        return w * scale * h * scale / ((double)windowWidth * windowHeight);
    }

    /// <summary>The last change: what was asked, what the monitor runs at now (shown in RemoteHub's connection info).</summary>
    public static string LastFit { get; private set; } = "";

    public static bool Fit(string device, int width, int height)
    {
        var current = New();
        if (!EnumDisplaySettingsW(device, -1 /* ENUM_CURRENT_SETTINGS */, ref current)) { LastFit = $"{width}×{height} asked – the monitor's mode can't be read"; return false; }
        if (current.dmPelsWidth == width && current.dmPelsHeight == height) { LastFit = $"{width}×{height}"; return true; }
        var modes = List(device).Where(m => m.Bits == current.dmBitsPerPel).Select(m => (m.Width, m.Height)).ToList();
        // Many displays (virtual ones, most GPUs with scaling) take any size: the exact one first, then the window's shape
        // a little smaller – the same shape means no black bars. Only then the nearest listed mode.
        for (var k = 1.0; k >= 0.5; k -= 0.05)
        {
            int w = (int)(width * k) & ~1, h = (int)(height * k) & ~1;
            if (w < 640 || h < 480) break;
            if (Apply(device, current, w, h, test: true) && Apply(device, current, w, h, test: false)) return Done(device, width, height, k < 1 ? "same shape, smaller" : "exact");
        }
        if (Choose(modes, width, height) is not { } best) { LastFit = $"{width}×{height} asked – no display mode fits" + Sizes(device); return false; }
        if (best.Width == current.dmPelsWidth && best.Height == current.dmPelsHeight) return Done(device, width, height, "closest listed mode");
        return Apply(device, current, best.Width, best.Height, test: false) ? Done(device, width, height, "closest listed mode")
            : Fail(device, width, height, best);
    }

    private static bool Apply(string device, DEVMODEW current, int w, int h, bool test)
    {
        var dm = current;
        dm.dmPelsWidth = (uint)w;
        dm.dmPelsHeight = (uint)h;
        dm.dmFields = 0x00080000 | 0x00100000; // DM_PELSWIDTH | DM_PELSHEIGHT
        return ChangeDisplaySettingsExW(device, ref dm, IntPtr.Zero, test ? 0x2u /* CDS_TEST */ : 0x4u /* CDS_FULLSCREEN */, IntPtr.Zero) == 0;
    }

    private static bool Done(string device, int width, int height, string how)
    {
        var now = New();
        LastFit = (EnumDisplaySettingsW(device, -1, ref now)
            ? $"{width}×{height} asked → {now.dmPelsWidth}×{now.dmPelsHeight} ({how})"
            : $"{width}×{height} asked ({how})") + Sizes(device);
        return true;
    }

    /// <summary>For the log: the sizes the display offers (largest first).</summary>
    private static string Sizes(string device)
    {
        var sizes = List(device).Select(m => (m.Width, m.Height)).Distinct().OrderByDescending(m => m.Width * m.Height).Take(10).Select(m => $"{m.Width}×{m.Height}").ToList();
        return sizes.Count == 0 ? "" : "; display modes: " + string.Join(", ", sizes);
    }

    private static bool Fail(string device, int width, int height, (int Width, int Height) best)
    {
        LastFit = $"{width}×{height} asked – {best.Width}×{best.Height} was refused by the display" + Sizes(device);
        return false;
    }

    public static void Restore(string device) => ChangeDisplaySettingsExW(device, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

    private static DEVMODEW New() => new() { dmSize = (ushort)sizeof(DEVMODEW) };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        public fixed char dmDeviceName[32];
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        public fixed char dmFormName[32];
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    /// <summary>Pixels of the largest mode the display offers (0 if unknown) – a GPU without a monitor offers only 640×480.</summary>
    public static long LargestArea(string device) => List(device).Select(m => (long)m.Width * m.Height).DefaultIfEmpty(0).Max();

    /// <summary>The display (\\.\DISPLAYn) of the agent's virtual monitor (<see cref="VirtualDisplay"/>), or null.</summary>
    public static string? VirtualMonitor()
    {
        var dd = new DISPLAY_DEVICEW { cb = sizeof(DISPLAY_DEVICEW) };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref dd, 0); i++, dd = new DISPLAY_DEVICEW { cb = sizeof(DISPLAY_DEVICEW) })
        {
            if ((dd.StateFlags & 0x1 /* DISPLAY_DEVICE_ATTACHED_TO_DESKTOP */) == 0) continue;
            var description = new string(dd.DeviceString);
            if (description.Contains("Virtual Display Driver", StringComparison.OrdinalIgnoreCase)) return new string(dd.DeviceName);
        }
        return null;
    }

    /// <summary>
    /// Makes <paramref name="device"/> the primary monitor (taskbar, new windows) – for the virtual monitor, so nothing
    /// opens on an invisible 640×480 output. The whole layout is moved so that it sits at 0,0 (the monitor at 0,0 is the
    /// primary one); Windows picks another primary again when the virtual monitor goes away.
    /// </summary>
    /// <returns>Null on success, else which step failed (Win32 error).</returns>
    public static string? MakePrimary(string device)
    {
        const uint activeOnly = 0x2; // QDC_ONLY_ACTIVE_PATHS
        var r = GetDisplayConfigBufferSizes(activeOnly, out var pathCount, out var modeCount);
        if (r != 0) return $"buffer sizes: {r}";
        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        r = QueryDisplayConfig(activeOnly, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (r != 0) return $"query: {r}";
        int? virt = null;
        for (var i = 0; i < pathCount; i++)
        {
            var name = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                type = 1, // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                size = sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME),
                adapterId = paths[i].sourceAdapterId,
                id = paths[i].sourceId,
            };
            if (DisplayConfigGetDeviceInfo(ref name) != 0) continue;
            if (new string(name.viewGdiDeviceName).Equals(device, StringComparison.OrdinalIgnoreCase)) virt = (int)paths[i].sourceModeInfoIdx;
        }
        if (virt is not { } v || v >= modeCount) return "not among the active displays";
        var (dx, dy) = (-modes[v].positionX, -modes[v].positionY);
        for (var i = 0; i < modeCount; i++)
            if (modes[i].infoType == 1) // DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE
            {
                modes[i].positionX += dx;
                modes[i].positionY += dy;
            }
        const uint apply = 0x80, supplied = 0x20, allowChanges = 0x400, save = 0x200;
        r = SetDisplayConfig(pathCount, paths, modeCount, modes, apply | supplied | allowChanges | save);
        return r == 0 ? null : $"apply: {r}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint Low; public int High; }

    /// <summary>DISPLAYCONFIG_PATH_INFO (source and target info flattened).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public LUID sourceAdapterId;
        public uint sourceId, sourceModeInfoIdx, sourceStatusFlags;
        public LUID targetAdapterId;
        public uint targetId, targetModeInfoIdx;
        public int outputTechnology, rotation, scaling;
        public uint refreshNumerator, refreshDenominator;
        public int scanLineOrdering, targetAvailable;
        public uint targetStatusFlags, flags;
    }

    /// <summary>DISPLAYCONFIG_MODE_INFO with the source-mode view of the union (position of a display).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public int infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public uint width;
        [FieldOffset(20)] public uint height;
        [FieldOffset(24)] public int pixelFormat;
        [FieldOffset(28)] public int positionX;
        [FieldOffset(32)] public int positionY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public int type, size;
        public LUID adapterId;
        public uint id;
        public fixed char viewGdiDeviceName[32];
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint modeCount, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int SetDisplayConfig(uint pathCount, [In] DISPLAYCONFIG_PATH_INFO[] paths, uint modeCount, [In] DISPLAYCONFIG_MODE_INFO[] modes, uint flags);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICEW
    {
        public int cb;
        public fixed char DeviceName[32];
        public fixed char DeviceString[128];
        public uint StateFlags;
        public fixed char DeviceID[128];
        public fixed char DeviceKey[128];
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DISPLAY_DEVICEW dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODEW dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsExW(string device, ref DEVMODEW dm, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsExW(string device, IntPtr dm, IntPtr hwnd, uint flags, IntPtr param);
}

/// <summary>Keeps the calling thread on the input desktop ("Default", or "Winlogon" for the sign-in screen and UAC).</summary>
internal sealed class DesktopThread : IDisposable
{
    private IntPtr _current;
    private string _name = "";

    public string Name => _name;
    public bool IsSecure => _name.Length > 0 && !_name.Equals("Default", StringComparison.OrdinalIgnoreCase);

    /// <summary>For the log: the last time the input desktop couldn't be opened (Win32 error), 0 if it could.</summary>
    public int LastError { get; private set; }

    /// <summary>True if the thread moved to another desktop.</summary>
    public bool Sync()
    {
        const uint access = 0x01FF | 0x000F0000; // all desktop rights incl. switch/journal + standard rights
        var input = DesktopNative.OpenInputDesktop(0, false, access);
        LastError = input == IntPtr.Zero ? Marshal.GetLastPInvokeError() : 0;
        if (input == IntPtr.Zero) return false;
        var name = DesktopNative.DesktopName(input);
        if (name == _name)
        {
            DesktopNative.CloseDesktop(input);
            return false;
        }
        if (!DesktopNative.SetThreadDesktop(input))
        {
            DesktopNative.CloseDesktop(input);
            return false;
        }
        if (_current != IntPtr.Zero) DesktopNative.CloseDesktop(_current);
        _current = input;
        _name = name;
        return true;
    }

    public void Dispose()
    {
        // The handle stays valid as long as the thread uses it; the process ends with the session.
        _current = IntPtr.Zero;
    }
}

internal static class DisplayMonitors
{
    public static unsafe IReadOnlyList<DesktopMonitor> List()
    {
        var found = new List<(IntPtr Handle, DesktopNative.MONITORINFOEX Info)>();
        DesktopNative.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var info = new DesktopNative.MONITORINFOEX { cbSize = sizeof(DesktopNative.MONITORINFOEX) };
            if (DesktopNative.GetMonitorInfo(h, ref info)) found.Add((h, info));
            return true;
        }, IntPtr.Zero);
        // Primary first, then left to right.
        return found
            .OrderByDescending(m => (m.Info.dwFlags & 1) != 0).ThenBy(m => m.Info.rcMonitor.Left).ThenBy(m => m.Info.rcMonitor.Top)
            .Select((m, i) =>
            {
                var r = m.Info.rcMonitor;
                return new DesktopMonitor(i, m.Info.DeviceName, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, (m.Info.dwFlags & 1) != 0);
            })
            .ToList();
    }
}

/// <summary>Screen copy with GDI (works everywhere, slower). "Changed" is found by comparing with the last image.</summary>
internal sealed unsafe class GdiScreen : IDisposable
{
    private IntPtr _dc, _bitmap, _bits;
    private int _w, _h;
    private byte[] _last = [];

    public CaptureResult Capture(DesktopMonitor m, FrameBuffer target)
    {
        int w = m.Width, h = m.Height;
        if (w <= 0 || h <= 0) return CaptureResult.Failed;
        if (_bitmap == IntPtr.Zero || _w != w || _h != h)
        {
            Free();
            _dc = DesktopNative.CreateCompatibleDC(IntPtr.Zero);
            _bitmap = DesktopNative.CreateTopDownDib(_dc, w, h, out _bits);
            DesktopNative.SelectObject(_dc, _bitmap);
            (_w, _h) = (w, h);
            _last = [];
        }
        var screen = DesktopNative.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return CaptureResult.Failed;
        try
        {
            if (!DesktopNative.BitBlt(_dc, 0, 0, w, h, screen, m.X, m.Y, 0x00CC0020 | 0x40000000)) return CaptureResult.Failed; // SRCCOPY | CAPTUREBLT
        }
        finally { DesktopNative.ReleaseDC(IntPtr.Zero, screen); }
        DesktopNative.GdiFlush();
        var bytes = new ReadOnlySpan<byte>((void*)_bits, w * h * 4);
        if (_last.Length == bytes.Length && bytes.SequenceEqual(_last)) return CaptureResult.Unchanged;
        if (_last.Length != bytes.Length) _last = new byte[bytes.Length];
        bytes.CopyTo(_last);
        target.Ensure(w, h);
        bytes.CopyTo(target.Pixels);
        return CaptureResult.Changed;
    }

    private void Free()
    {
        if (_bitmap != IntPtr.Zero) DesktopNative.DeleteObject(_bitmap);
        if (_dc != IntPtr.Zero) DesktopNative.DeleteDC(_dc);
        _bitmap = _dc = IntPtr.Zero;
    }

    public void Dispose() => Free();
}

/// <summary>DXGI Desktop Duplication of one monitor: Windows hands over only frames that changed.</summary>
internal sealed unsafe class DxgiDuplication : IDisposable
{
    private IntPtr _device, _context, _duplication, _staging;
    private int _w, _h;
    private bool _haveImage;

    /// <summary>A desktop image has arrived since the duplication started.</summary>
    public bool HasImage => _haveImage;

    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);

    private DxgiDuplication() { }

    /// <summary>Null if the monitor can't be duplicated (no GPU output for it, rotated, unsupported).</summary>
    public static DxgiDuplication? TryCreate(DesktopMonitor monitor)
    {
        var d = new DxgiDuplication();
        try { return d.Open(monitor) ? d : Fail(d); }
        catch { return Fail(d); }

        static DxgiDuplication? Fail(DxgiDuplication d) { d.Dispose(); return null; }
    }

    private bool Open(DesktopMonitor monitor)
    {
        var iidFactory = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (DesktopNative.CreateDXGIFactory1(ref iidFactory, out var factory) < 0) return false;
        try
        {
            for (uint a = 0; ; a++)
            {
                IntPtr adapter;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(factory, 12))(factory, a, &adapter) < 0) return false; // EnumAdapters1
                try
                {
                    for (uint o = 0; ; o++)
                    {
                        IntPtr output;
                        if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(adapter, 7))(adapter, o, &output) < 0) break; // EnumOutputs
                        try
                        {
                            DesktopNative.DXGI_OUTPUT_DESC desc;
                            if (((delegate* unmanaged[Stdcall]<IntPtr, DesktopNative.DXGI_OUTPUT_DESC*, int>)Slot(output, 7))(output, &desc) < 0) continue; // GetDesc
                            var name = new string(desc.DeviceName);
                            if (!name.Equals(monitor.Name, StringComparison.OrdinalIgnoreCase)) continue;
                            if (desc.Rotation is not (0 or 1)) return false; // rotated: GDI handles it
                            return Duplicate(adapter, output);
                        }
                        finally { Marshal.Release(output); }
                    }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
    }

    private bool Duplicate(IntPtr adapter, IntPtr output)
    {
        if (DesktopNative.D3D11CreateDevice(adapter, 0 /* UNKNOWN: the given adapter */, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out _device, out _, out _context) < 0) return false;
        var iidOutput1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        if (Marshal.QueryInterface(output, in iidOutput1, out var output1) < 0) return false;
        try
        {
            IntPtr dup;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(output1, 22))(output1, _device, &dup) < 0) return false; // DuplicateOutput
            _duplication = dup;
            return true;
        }
        finally { Marshal.Release(output1); }
    }

    public CaptureResult Capture(int timeoutMs, FrameBuffer target)
    {
        DesktopNative.DXGI_OUTDUPL_FRAME_INFO info;
        IntPtr resource;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, DesktopNative.DXGI_OUTDUPL_FRAME_INFO*, IntPtr*, int>)Slot(_duplication, 8))(_duplication, (uint)Math.Max(0, timeoutMs), &info, &resource); // AcquireNextFrame
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) return CaptureResult.Unchanged;
        if (hr < 0) return CaptureResult.Failed;
        try
        {
            // No new desktop image (only the pointer moved, or the first frame before anything was drawn – it is black).
            if (info.LastPresentTime == 0) return CaptureResult.Unchanged;
            var iidTexture = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            if (Marshal.QueryInterface(resource, in iidTexture, out var texture) < 0) return CaptureResult.Failed;
            try
            {
                DesktopNative.TEXTURE2D_DESC desc;
                ((delegate* unmanaged[Stdcall]<IntPtr, DesktopNative.TEXTURE2D_DESC*, void>)Slot(texture, 10))(texture, &desc); // GetDesc
                int w = (int)desc.Width, h = (int)desc.Height;
                EnsureStaging(w, h, desc.Format);
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Slot(_context, 47))(_context, _staging, texture); // CopyResource
            }
            finally { Marshal.Release(texture); }
        }
        finally
        {
            Marshal.Release(resource);
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_duplication, 14))(_duplication); // ReleaseFrame
        }

        DesktopNative.MAPPED map;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, DesktopNative.MAPPED*, int>)Slot(_context, 14))(_context, _staging, 0, 1, 0, &map) < 0)
            return CaptureResult.Failed;
        try
        {
            target.Ensure(_w, _h);
            fixed (byte* dst = target.Pixels)
                for (var y = 0; y < _h; y++)
                    Buffer.MemoryCopy((byte*)map.pData + (long)y * map.RowPitch, dst + (long)y * _w * 4, _w * 4, _w * 4);
        }
        finally { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(_context, 15))(_context, _staging, 0); }
        _haveImage = true;
        return CaptureResult.Changed;
    }

    private void EnsureStaging(int w, int h, int format)
    {
        if (_staging != IntPtr.Zero && _w == w && _h == h) return;
        if (_staging != IntPtr.Zero) Marshal.Release(_staging);
        _staging = IntPtr.Zero;
        var desc = new DesktopNative.TEXTURE2D_DESC
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = format, SampleCount = 1,
            Usage = 3 /* STAGING */, CPUAccessFlags = 0x20000 /* READ */,
        };
        IntPtr created;
        if (((delegate* unmanaged[Stdcall]<IntPtr, DesktopNative.TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)Slot(_device, 5))(_device, &desc, IntPtr.Zero, &created) < 0)
            throw new InvalidOperationException("CreateTexture2D failed.");
        _staging = created;
        (_w, _h) = (w, h);
    }

    private static IntPtr Slot(IntPtr com, int index) => (*(IntPtr**)com)[index];

    public void Dispose()
    {
        foreach (var p in new[] { _staging, _duplication, _context, _device })
            if (p != IntPtr.Zero) Marshal.Release(p);
        _staging = _duplication = _context = _device = IntPtr.Zero;
    }
}

/// <summary>Turns a Windows cursor into BGRA with alpha – also monochrome and inverting cursors (drawn on black and white).</summary>
internal static unsafe class CursorShapes
{
    public static CursorImage? Render(IntPtr cursor)
    {
        if (!DesktopNative.GetIconInfo(cursor, out var ii)) return null;
        try
        {
            int w = 32, h = 32;
            var bm = new DesktopNative.BITMAP();
            if (ii.hbmColor != IntPtr.Zero && DesktopNative.GetObject(ii.hbmColor, sizeof(DesktopNative.BITMAP), ref bm) > 0) (w, h) = (bm.bmWidth, bm.bmHeight);
            else if (ii.hbmMask != IntPtr.Zero && DesktopNative.GetObject(ii.hbmMask, sizeof(DesktopNative.BITMAP), ref bm) > 0) (w, h) = (bm.bmWidth, bm.bmHeight / 2);
            w = Math.Clamp(w, 1, 256);
            h = Math.Clamp(h, 1, 256);
            var onBlack = Draw(cursor, w, h, 0x000000);
            var onWhite = Draw(cursor, w, h, 0xFFFFFF);
            var bgra = new byte[w * h * 4];
            for (var i = 0; i < w * h; i++)
            {
                int a = 255 - Math.Max(0, Math.Max(onWhite[i * 4] - onBlack[i * 4], Math.Max(onWhite[i * 4 + 1] - onBlack[i * 4 + 1], onWhite[i * 4 + 2] - onBlack[i * 4 + 2])));
                if (onBlack[i * 4] > onWhite[i * 4]) a = 255; // inverting pixel: show it opaque (black on white works on most backgrounds)
                bgra[i * 4 + 3] = (byte)a;
                for (var c = 0; c < 3; c++) bgra[i * 4 + c] = a == 0 ? (byte)0 : (byte)Math.Min(255, onBlack[i * 4 + c] * 255 / a);
            }
            return new CursorImage(w, h, Math.Min(ii.xHotspot, w - 1), Math.Min(ii.yHotspot, h - 1), bgra);
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) DesktopNative.DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) DesktopNative.DeleteObject(ii.hbmMask);
        }
    }

    private static byte[] Draw(IntPtr cursor, int w, int h, int color)
    {
        var dc = DesktopNative.CreateCompatibleDC(IntPtr.Zero);
        var bitmap = DesktopNative.CreateTopDownDib(dc, w, h, out var bits);
        try
        {
            DesktopNative.SelectObject(dc, bitmap);
            var px = (uint*)bits;
            for (var i = 0; i < w * h; i++) px[i] = (uint)color;
            DesktopNative.DrawIconEx(dc, 0, 0, cursor, w, h, 0, IntPtr.Zero, 3 /* DI_NORMAL */);
            DesktopNative.GdiFlush();
            return new ReadOnlySpan<byte>((void*)bits, w * h * 4).ToArray();
        }
        finally
        {
            DesktopNative.DeleteObject(bitmap);
            DesktopNative.DeleteDC(dc);
        }
    }
}

internal static class DesktopNative
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] public struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [StructLayout(LayoutKind.Sequential)] public struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public unsafe struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public int dwFlags;
        public fixed char szDevice[32];
        public string DeviceName { get { fixed (char* p = szDevice) return new string(p); } }
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct DXGI_OUTPUT_DESC
    {
        public fixed char DeviceName[32];
        public RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime, LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced, ProtectedContentMaskedOut;
        public POINT PointerPosition;
        public int PointerVisible;
        public uint TotalMetadataBufferSize, PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)] public struct MAPPED { public IntPtr pData; public uint RowPitch, DepthPitch; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    public static IntPtr CreateTopDownDib(IntPtr dc, int width, int height, out IntPtr bits)
    {
        var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        var bitmap = CreateDIBSection(dc, ref bmi, 0, out bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero) throw new InvalidOperationException($"Not enough memory for a {width} × {height} image.");
        return bitmap;
    }

    public static unsafe string DesktopName(IntPtr desktop)
    {
        var buffer = stackalloc char[256];
        return GetUserObjectInformation(desktop, 2 /* UOI_NAME */, buffer, 512, out _) ? new string(buffer) : "";
    }

    public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int cx, int cy, int step, IntPtr brush, int flags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern unsafe bool GetUserObjectInformation(IntPtr obj, int index, char* info, int length, out int needed);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] public static extern bool GdiFlush();
    [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr obj, int size, ref BITMAP bitmap);
    [DllImport("dxgi.dll")] public static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, int flags, IntPtr featureLevels, int levels, int sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr context);
}
