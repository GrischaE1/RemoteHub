using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteHub.Agent;

/// <summary>A rectangle in screen pixels (right and bottom exclusive).</summary>
internal readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    public ScreenRect Inflate(int by) => new(Left - by, Top - by, Right + by, Bottom + by);
    public ScreenRect Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);
    public ScreenRect Intersect(ScreenRect o) => new(Math.Max(Left, o.Left), Math.Max(Top, o.Top), Math.Min(Right, o.Right), Math.Min(Bottom, o.Bottom));
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    /// <summary>What is left of this rectangle outside <paramref name="cut"/>: up to four rectangles.</summary>
    public IEnumerable<ScreenRect> Minus(ScreenRect cut)
    {
        var i = Intersect(cut);
        if (i.IsEmpty) { yield return this; yield break; }
        if (i.Top > Top) yield return this with { Bottom = i.Top };                                 // above
        if (i.Bottom < Bottom) yield return this with { Top = i.Bottom };                           // below
        if (i.Left > Left) yield return new ScreenRect(Left, i.Top, i.Left, i.Bottom);              // left
        if (i.Right < Right) yield return new ScreenRect(i.Right, i.Top, Right, i.Bottom);          // right
    }

    /// <summary>What is left of <paramref name="rect"/> outside all of <paramref name="cuts"/>.</summary>
    public static List<ScreenRect> Subtract(ScreenRect rect, IEnumerable<ScreenRect> cuts)
    {
        var left = new List<ScreenRect> { rect };
        foreach (var c in cuts)
        {
            if (left.Count == 0) break;
            left = left.SelectMany(r => r.Minus(c)).Where(r => !r.IsEmpty).ToList();
        }
        return left;
    }
}

/// <summary>
/// A visible top-level window (not minimized, not cloaked), as far as hiding apps cares.
/// </summary>
/// <param name="App">Its app (<see cref="AppIds"/>); "" for the shell, the helper itself and what isn't an app.</param>
/// <param name="Opaque">Covers what is below it in the picture: not layered, not click-through, not kept out of captures.</param>
/// <param name="Shell">A shell surface that shows other apps' content (thumbnails, Alt+Tab, Task View, notifications).</param>
/// <param name="Unresolved">A Store app's frame whose app couldn't be found out.</param>
/// <param name="Exe">The full path of its program, normalised (<see cref="AppIds.Normalize"/>); "" if unknown.</param>
internal sealed record AppWindow(IntPtr Handle, string App, ScreenRect Bounds, bool Opaque, bool Shell = false, bool Unresolved = false, string Exe = "");

/// <summary>A running app as the user picks it: one row per app, however many windows it has.</summary>
/// <param name="Exe">The full path of its program, so a policy entry naming a Store app by path finds it too.</param>
internal sealed record RunningApp(string Id, string Name, int Windows, IntPtr Icon, string Exe = "");

/// <summary>Which app a window belongs to (<see cref="AppIds"/>), its program's path, and whether that is a Store frame whose app couldn't be found out.</summary>
internal readonly record struct AppIdentity(string App, string Exe = "", bool Unresolved = false)
{
    public static readonly AppIdentity None = new("");
}

/// <summary>The windows of the desktop the calling thread is on (the real ones, or a test double).</summary>
internal interface IAppWindows
{
    /// <summary>The visible top-level windows, topmost first.</summary>
    IReadOnlyList<AppWindow> Visible();

    /// <summary>Apps with a window the user can switch to (as in Alt+Tab), for the picker.</summary>
    IReadOnlyList<RunningApp> Running();

    /// <summary>The app of the window that has the keyboard (<see cref="AppIdentity.None"/> if none or not an app).</summary>
    AppIdentity ForegroundApp();

    /// <summary>The app that put what is on the clipboard there (<see cref="AppIdentity.None"/> if unknown).</summary>
    AppIdentity ClipboardOwnerApp();
}

/// <summary>
/// How an app is named: the full path of its program, lower case (<c>c:\program files\keepass\keepass.exe</c>), or for a
/// Store app <c>aumid:</c> and its AppUserModelID. Worked out by the helper (LocalSystem) from the process – never from
/// a window's title or class, which any program can set.
/// </summary>
internal static class AppIds
{
    public const string Packaged = "aumid:";

    private const string PackagesFolder = @"\windowsapps\";

    /// <summary>
    /// One spelling per app, so a policy entry and the path Windows reports compare equal: lower case, no quotes,
    /// backslashes, <c>..</c> and doubled separators resolved.
    /// </summary>
    public static string Normalize(string id)
    {
        var s = id.Trim().Trim('"').Trim();
        if (s.StartsWith(Packaged, StringComparison.OrdinalIgnoreCase)) return s.ToLowerInvariant();
        s = s.Replace('/', '\\');
        if (s.Length > 6 && s.StartsWith(@"\\?\", StringComparison.Ordinal) && s[5] == ':') s = s[4..];
        try { if (Path.IsPathFullyQualified(s)) s = Path.GetFullPath(s); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { /* kept as written: matches only itself */ }
        return s.ToLowerInvariant();
    }

    /// <summary>
    /// For a program in a Store package's folder (<c>…\WindowsApps\Name_Version_Arch_Resource_Publisher\…</c>): the
    /// package family and the path inside the package, e.g. <c>microsoft.windowsnotepad_8wekyb3d8bbwe\notepad\notepad.exe</c>;
    /// "" for anything else. The folder carries the version, so the path an admin copied stops matching with the
    /// app's next update – this key doesn't change with it.
    /// </summary>
    internal static string PackageKey(string path)
    {
        var at = path.IndexOf(PackagesFolder, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return "";
        var rest = path.AsSpan(at + PackagesFolder.Length);
        var slash = rest.IndexOf('\\');
        if (slash <= 0 || slash == rest.Length - 1) return "";
        var parts = rest[..slash].ToString().Split('_');
        // A package full name: Name_Version_Architecture_ResourceId_PublisherId (the resource id is mostly empty);
        // names can't contain '_', so anything else isn't a package folder.
        if (parts.Length != 5 || parts[0].Length == 0 || parts[1].Length == 0 || parts[4].Length == 0) return "";
        return string.Concat(parts[0], "_", parts[4], rest[slash..].ToString()).ToLowerInvariant();
    }

    /// <summary>An entry naming a Store app: by AppUserModelID, or by a program in its package's folder.</summary>
    internal static bool IsPackaged(string id) => id.StartsWith(Packaged, StringComparison.Ordinal) || PackageKey(id).Length > 0;

    /// <summary>
    /// Whether a window of <paramref name="app"/> is to be hidden: listed by its app id (an <c>aumid:</c> entry, or the
    /// path for a classic program), by its program's path – which is how an admin names a Store app they found with
    /// Task Manager or <c>Get-Process</c> – or by that path in any version of the same package. A Store frame whose
    /// app couldn't be found out counts as hidden while any Store app is (fails closed). <paramref name="hidden"/>
    /// holds normalised entries.
    /// </summary>
    public static bool IsHidden(IReadOnlySet<string> hidden, string app, string exe, bool unresolved = false)
    {
        if (hidden.Count == 0) return false;
        if (unresolved) return hidden.Any(IsPackaged);
        if (app.Length == 0) return false; // the shell, the helper: never an app, even if their program is listed
        if (hidden.Contains(app) || exe.Length > 0 && hidden.Contains(exe)) return true;
        var key = PackageKey(exe);
        return key.Length > 0 && hidden.Any(h => PackageKey(h) == key);
    }

    public static bool IsHidden(IReadOnlySet<string> hidden, AppIdentity a) => IsHidden(hidden, a.App, a.Exe, a.Unresolved);
}

internal sealed unsafe class WindowsAppWindows : IAppWindows
{
    private const int GW_HWNDNEXT = 2, GW_OWNER = 4, GW_CHILD = 5, GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    /// <summary>
    /// Shell windows that show live thumbnails or text of other apps: taskbar previews, Alt+Tab, Task View, Snap
    /// Assist. Hidden completely while any app is hidden. Checked per Windows build in the lab (design §7.5).
    /// </summary>
    internal static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "TaskListThumbnailWnd", "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "ForegroundStaging", "TaskSwitcherWnd", "TaskSwitcherOverlayWnd",
    };

    private readonly uint _self = (uint)Environment.ProcessId;
    private readonly Dictionary<uint, (string App, string Exe, long At)> _processes = [];

    public IReadOnlyList<AppWindow> Visible()
    {
        var list = new List<AppWindow>();
        var h = GetTopWindow(IntPtr.Zero);
        for (var n = 0; h != IntPtr.Zero && n < 10_000; n++, h = GetWindow(h, GW_HWNDNEXT))
        {
            if (!IsWindowVisible(h) || IsIconic(h) || Cloaked(h) || Bounds(h) is not { IsEmpty: false } bounds) continue;
            var ex = (long)GetWindowLongPtrW(h, GWL_EXSTYLE);
            GetWindowThreadProcessId(h, out var pid);
            var cls = ClassName(h);
            var (app, exe) = Identify(h, pid, cls, out var unresolved);
            var shell = ShellClasses.Contains(cls) || cls == "Windows.UI.Core.CoreWindow" && exe.EndsWith(@"\shellexperiencehost.exe", StringComparison.Ordinal);
            var opaque = pid != _self && (ex & (WS_EX_LAYERED | WS_EX_TRANSPARENT)) == 0 && GetWindowDisplayAffinity(h, out var affinity) && affinity == 0;
            list.Add(new AppWindow(h, app, bounds, opaque, shell, unresolved, exe));
        }
        return list;
    }

    public IReadOnlyList<RunningApp> Running()
    {
        var apps = new Dictionary<string, (string Name, int Count, IntPtr Icon, string Exe)>();
        var h = GetTopWindow(IntPtr.Zero);
        for (var n = 0; h != IntPtr.Zero && n < 10_000; n++, h = GetWindow(h, GW_HWNDNEXT))
        {
            if (!IsWindowVisible(h) || Cloaked(h)) continue;
            var ex = (long)GetWindowLongPtrW(h, GWL_EXSTYLE);
            var owned = GetWindow(h, GW_OWNER) != IntPtr.Zero;
            // What Alt+Tab lists: no tool windows, unowned – unless the window asks for a taskbar button.
            if ((ex & WS_EX_APPWINDOW) == 0 && ((ex & WS_EX_TOOLWINDOW) != 0 || (ex & WS_EX_NOACTIVATE) != 0 || owned)) continue;
            var title = Title(h);
            if (title.Length == 0) continue;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == _self) continue;
            var (app, exe) = Identify(h, pid, ClassName(h), out _);
            if (app.Length == 0) continue;
            if (apps.TryGetValue(app, out var a)) apps[app] = a with { Count = a.Count + 1, Icon = a.Icon != IntPtr.Zero ? a.Icon : Icon(h) };
            else apps[app] = (DisplayName(app, exe, title), 1, Icon(h), exe);
        }
        return apps.Select(a => new RunningApp(a.Key, a.Value.Name, a.Value.Count, a.Value.Icon, a.Value.Exe))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public AppIdentity ForegroundApp() => AppOf(GetForegroundWindow());

    public AppIdentity ClipboardOwnerApp()
    {
        var owner = GetClipboardOwner();
        return owner != IntPtr.Zero ? AppOf(owner) : ForegroundApp();
    }

    private AppIdentity AppOf(IntPtr h)
    {
        if (h == IntPtr.Zero) return AppIdentity.None;
        // A child window (clipboard owners often are one): the app of its top-level window.
        var root = GetAncestor(h, 2 /* GA_ROOT */);
        if (root != IntPtr.Zero) h = root;
        GetWindowThreadProcessId(h, out var pid);
        var (app, exe) = Identify(h, pid, ClassName(h), out var unresolved);
        return new AppIdentity(app, exe, unresolved);
    }

    /// <summary>The app of a window, and its program's path ("" for both when it isn't an app).</summary>
    private (string App, string Exe) Identify(IntPtr h, uint pid, string cls, out bool unresolved)
    {
        unresolved = false;
        if (pid == _self) return ("", "");
        var (app, exe) = Process(pid);
        // A Store app's window is a frame of ApplicationFrameHost; the app runs in the CoreWindow inside it.
        if (exe.EndsWith(@"\applicationframehost.exe", StringComparison.Ordinal) && cls == "ApplicationFrameWindow")
        {
            var core = FindWindowExW(h, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (core != IntPtr.Zero && GetWindowThreadProcessId(core, out var inner) != 0 && inner != pid) return Process(inner);
            unresolved = true;
            return ("", exe);
        }
        // Explorer: its folder windows are an app; the taskbar, the desktop and the rest of the shell are not.
        if (exe.EndsWith(@"\explorer.exe", StringComparison.Ordinal)) return (cls == "CabinetWClass" ? app : "", exe);
        return (app, exe);
    }

    private (string App, string Exe) Process(uint pid) { lock (_processes) return ProcessNow(pid); }

    private (string App, string Exe) ProcessNow(uint pid)
    {
        var now = Environment.TickCount64;
        if (_processes.TryGetValue(pid, out var known) && now - known.At < 5000) return (known.App, known.Exe);
        if (_processes.Count > 2000) _processes.Clear();
        var exe = "";
        var app = "";
        var p = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (p != IntPtr.Zero)
        {
            try
            {
                var buffer = new char[1024];
                var size = buffer.Length;
                fixed (char* b = buffer)
                    if (QueryFullProcessImageNameW(p, 0, b, ref size)) exe = AppIds.Normalize(new string(b, 0, size));
                var length = 0;
                if (GetApplicationUserModelId(p, ref length, null) == 122 /* INSUFFICIENT_BUFFER */ && length > 0)
                {
                    var aumid = new char[length];
                    fixed (char* a = aumid)
                        if (GetApplicationUserModelId(p, ref length, a) == 0) app = AppIds.Packaged + AppIds.Normalize(new string(a, 0, Math.Max(0, length - 1)));
                }
                if (app.Length == 0) app = exe;
            }
            finally { CloseHandle(p); }
        }
        _processes[pid] = (app, exe, now);
        return (app, exe);
    }

    private static string DisplayName(string app, string exe, string title)
    {
        if (app.StartsWith(AppIds.Packaged, StringComparison.Ordinal)) return title;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(info.ProductName)) return info.ProductName.Trim();
        }
        catch { /* no version resource */ }
        return Path.GetFileNameWithoutExtension(exe);
    }

    private static IntPtr Icon(IntPtr h)
    {
        foreach (var kind in new nint[] { 2 /* ICON_SMALL2 */, 0 /* ICON_SMALL */, 1 /* ICON_BIG */ })
            if (SendMessageTimeoutW(h, 0x007F /* WM_GETICON */, kind, 0, 0x0002 /* ABORTIFHUNG */, 100, out var icon) != 0 && icon != 0) return icon;
        var cls = GetClassLongPtrW(h, -34 /* GCLP_HICONSM */);
        return cls != 0 ? cls : GetClassLongPtrW(h, -14 /* GCLP_HICON */);
    }

    private static bool Cloaked(IntPtr h) => DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static ScreenRect? Bounds(IntPtr h)
    {
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, sizeof(RECT)) != 0 && !GetWindowRect(h, out r)) return null;
        return new ScreenRect(r.Left, r.Top, r.Right, r.Bottom);
    }

    private static string ClassName(IntPtr h)
    {
        var buffer = stackalloc char[256];
        var n = GetClassNameW(h, buffer, 256);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    private static string Title(IntPtr h)
    {
        var buffer = stackalloc char[512];
        var n = InternalGetWindowText(h, buffer, 512); // doesn't send a message (no hang on a busy window)
        return n > 0 ? new string(buffer, 0, n).Trim() : "";
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern nint GetClassLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetClassNameW(IntPtr hwnd, char* name, int max);
    [DllImport("user32.dll")] private static extern int InternalGetWindowText(IntPtr hwnd, char* text, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeoutW(IntPtr hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char* name, ref int size);
    [DllImport("kernel32.dll")] private static extern int GetApplicationUserModelId(IntPtr process, ref int length, char* id);
}

/// <summary>
/// Where the hidden apps are in the picture, frame by frame (design §7.3–7.5). Fails closed: when in doubt, more is
/// hidden, or no frame is sent at all.
/// </summary>
internal sealed class AppMask(IAppWindows windows)
{
    /// <summary>Hidden apps' windows are covered a little beyond their edges (shadows, a pixel of rounding).</summary>
    internal const int Margin = 4;

    /// <summary>After a hidden app's window appeared, moved, changed size or went away: no frame for this long.</summary>
    internal const int HoldMs = 400;

    private readonly object _lock = new(); // Reset comes from the UI thread, Compute from the capture thread
    private ScreenRect[] _last = [];
    private List<(IntPtr, ScreenRect)> _lastWindows = [];
    private long _holdUntil;

    /// <summary>Forget what was seen (the hidden apps changed): the next frames are held until it settled.</summary>
    public void Reset(long now) { lock (_lock) ResetNow(now); }

    private void ResetNow(long now)
    {
        _last = [];
        _lastWindows = [];
        _holdUntil = now + HoldMs;
    }

    /// <summary>
    /// The rectangles to cover now (screen pixels) – this frame's and the last one's, since the picture may be a moment
    /// older than the windows' positions – and whether no frame may be sent at the moment (<paramref name="hold"/>).
    /// </summary>
    public ScreenRect[] Compute(IReadOnlySet<string> hidden, long now, out bool hold) { lock (_lock) return ComputeNow(hidden, now, out hold); }

    private ScreenRect[] ComputeNow(IReadOnlySet<string> hidden, long now, out bool hold)
    {
        hold = false;
        if (hidden.Count == 0)
        {
            (_last, _lastWindows) = ([], []);
            return [];
        }
        var masked = new List<ScreenRect>();
        var covers = new List<ScreenRect>();
        var seen = new List<(IntPtr, ScreenRect)>();
        foreach (var w in windows.Visible()) // topmost first
        {
            var hide = w.Shell || AppIds.IsHidden(hidden, w.App, w.Exe, w.Unresolved);
            if (hide)
            {
                if (!w.Shell) seen.Add((w.Handle, w.Bounds));
                masked.AddRange(ScreenRect.Subtract(w.Bounds.Inflate(Margin), covers));
            }
            else if (w.Opaque) covers.Add(w.Bounds);
        }
        // A hidden window opened, moved, was resized, minimized or restored: Windows animates it with its content,
        // outside any rectangle known here – nothing is sent until that is over.
        if (!seen.SequenceEqual(_lastWindows)) _holdUntil = now + HoldMs;
        _lastWindows = seen;
        var result = masked.Concat(_last).ToArray();
        _last = [.. masked];
        hold = now < _holdUntil;
        return result;
    }

    /// <summary>Covers the rectangles (screen pixels) in a picture of <paramref name="monitor"/>.</summary>
    internal static void Apply(FrameBuffer frame, DesktopMonitor monitor, IEnumerable<ScreenRect> rects, uint bgra = Fill)
    {
        var all = new ScreenRect(0, 0, frame.Width, frame.Height);
        var px = frame.Pixels;
        foreach (var r in rects)
        {
            var f = r.Offset(-monitor.X, -monitor.Y).Intersect(all);
            if (f.IsEmpty) continue;
            for (var y = f.Top; y < f.Bottom; y++)
                MemoryMarshal.Cast<byte, uint>(px.AsSpan((y * frame.Width + f.Left) * 4, f.Width * 4)).Fill(bgra);
        }
    }

    /// <summary>The colour of a hidden area: the console's dark window colour (BGRA in memory, opaque).</summary>
    internal const uint Fill = 0xFF1C2230;
}
