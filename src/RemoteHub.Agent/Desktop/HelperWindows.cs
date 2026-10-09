using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace RemoteHub.Agent;

/// <summary>What the person at the device sees: the question, the code, the "being viewed" banner – and the clipboard.</summary>
internal interface IHelperUi : IDisposable
{
    /// <summary>Asks the user; <paramref name="answered"/> gets true for Allow, false for Deny or no answer in time.</summary>
    void AskConsent(HelperInit init, Action<bool> answered);

    /// <summary>Shows the code the user reads out to the admin; <paramref name="denied"/> if the user refuses.</summary>
    void ShowPin(HelperInit init, Action denied);

    /// <summary>The banner for the whole session; it can't be hidden. <paramref name="disconnect"/> when the user ends it.</summary>
    void ShowBanner(HelperInit init, Action disconnect);

    void CloseDialogs();
    void SetClipboard(string text);
    event Action<string>? ClipboardChanged;

    /// <summary>A PNG from RemoteHub onto the device's clipboard.</summary>
    void SetClipboardImage(byte[] png);

    /// <summary>Files (already written on the device) onto the clipboard, to be pasted like files copied in Explorer.</summary>
    void SetClipboardFiles(IReadOnlyList<string> paths);

    /// <summary>The device's clipboard now holds an image or files.</summary>
    event Action<HelperClipboard>? ClipboardContent;
}

/// <summary>
/// The device-side windows in RemoteHub's look (the console's dark palette, accent, rounded corners). Drawn by hand
/// with GDI+ – the agent has no UI framework, it is published trimmed.
/// </summary>
internal sealed unsafe partial class Win32HelperUi : IHelperUi
{
    private const uint WM_APP = 0x8000, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_TIMER = 0x0113, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002,
        WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSELEAVE = 0x02A3, WM_NCHITTEST = 0x0084,
        WM_SETCURSOR = 0x0020, WM_KEYDOWN = 0x0100, WM_CLIPBOARDUPDATE = 0x031D;
    private const string ClassName = "RemoteHubAgentWindow";

    private static Win32HelperUi? _instance;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Dictionary<IntPtr, FluentWindow> _windows = [];
    private IntPtr _messageWindow;
    private string _lastClipboard = "";
    private nuint _gdiplus;

    public event Action<string>? ClipboardChanged;

    public Win32HelperUi()
    {
        _instance = this;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Desktop helper UI" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(10));
    }

    // ---- public API (any thread)

    public void AskConsent(HelperInit init, Action<bool> answered) => Post(() =>
    {
        CloseDialogsNow();
        var done = false;
        var seconds = Math.Max(5, init.TimeoutSeconds);
        FluentWindow? w = null;
        void Answer(bool allow)
        {
            if (done) return;
            done = true;
            CloseDialogsNow();
            answered(allow);
        }
        w = new FluentWindow(this, Theme.Current(), FluentKind.Dialog)
        {
            Subtitle = "Remote access request",
            Heading = $"{init.Account} wants to {What(init)} your screen",
            Body = "Only allow this if you expect it – for example because you asked IT for help. You can end it at any time with \"Disconnect\" at the top of the screen.",
            Footnote = $"Denied automatically in {seconds} s",
            Progress = 1,
            Buttons = [new FluentButton(1, "Allow", ButtonStyle.Primary), new FluentButton(2, "Deny", ButtonStyle.Secondary)],
            Clicked = id => Answer(id == 1),
            Cancelled = () => Answer(false),
        };
        w.Timer(1000, () =>
        {
            seconds--;
            if (seconds <= 0) { Answer(false); return; }
            w.Footnote = $"Denied automatically in {seconds} s";
            w.Progress = seconds / (double)Math.Max(5, init.TimeoutSeconds);
            w.Invalidate();
        });
        w.Show();
    });

    public void ShowPin(HelperInit init, Action denied) => Post(() =>
    {
        CloseDialogsNow();
        var w = new FluentWindow(this, Theme.Current(), FluentKind.Dialog)
        {
            Subtitle = "Remote access request",
            Heading = $"{init.Account} wants to {What(init)} your screen",
            Body = "If you agree, read this code out to them. Nobody can connect without it.",
            Code = init.Pin,
            Buttons = [new FluentButton(2, "Deny", ButtonStyle.Secondary)],
        };
        w.Clicked = _ => { CloseDialogsNow(); denied(); };
        w.Cancelled = () => { CloseDialogsNow(); denied(); };
        w.Show();
    });

    public void ShowBanner(HelperInit init, Action disconnect) => Post(() =>
    {
        var w = new FluentWindow(this, Theme.Current(), FluentKind.Banner)
        {
            Heading = $"Your screen is being {(init.ViewOnly ? "viewed" : "viewed and controlled")} by {init.Account}",
            ShortHeading = $"{(init.ViewOnly ? "Viewed" : "Controlled")} by {init.Account}",
            Subtitle = "RemoteHub",
            Buttons = [new FluentButton(3, "Disconnect", ButtonStyle.Danger)],
            Clicked = _ => disconnect(),
        };
        w.Show();
    });

    public void CloseDialogs() => Post(CloseDialogsNow);

    public void SetClipboard(string text) => Post(() =>
    {
        if (!OpenClipboardRetry()) return;
        try
        {
            _lastClipboard = text; // our own change comes back as WM_CLIPBOARDUPDATE: not sent back
            EmptyClipboard();
            var memory = GlobalAlloc(0x0002, (nuint)((text.Length + 1) * 2)); // GMEM_MOVEABLE
            if (memory == IntPtr.Zero) return;
            var p = (char*)GlobalLock(memory);
            text.AsSpan().CopyTo(new Span<char>(p, text.Length));
            p[text.Length] = '\0';
            GlobalUnlock(memory);
            if (SetClipboardData(13, memory) == IntPtr.Zero) GlobalFree(memory); // CF_UNICODETEXT
        }
        finally
        {
            CloseClipboard();
            _ownSequence = GetClipboardSequenceNumber();
        }
    });

    /// <summary>Another program may hold the clipboard for a moment.</summary>
    private bool OpenClipboardRetry()
    {
        for (var i = 0; i < 10; i++)
        {
            if (OpenClipboard(_messageWindow)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    public void Dispose()
    {
        Post(() =>
        {
            foreach (var h in _windows.Keys.ToList()) DestroyWindow(h);
            PostQuitMessage(0);
        });
        _thread.Join(TimeSpan.FromSeconds(3));
    }

    // ---- UI thread

    private void Loop()
    {
        var input = new GdiplusStartupInput { GdiplusVersion = 1 };
        GdiplusStartup(out _gdiplus, ref input, IntPtr.Zero);
        var wc = new WNDCLASSEXW
        {
            cbSize = sizeof(WNDCLASSEXW),
            style = 0x0003, // CS_HREDRAW | CS_VREDRAW
            lpfnWndProc = &WndProc,
            hInstance = GetModuleHandleW(null),
            hCursor = LoadCursorW(IntPtr.Zero, 32512),
            hIcon = AppIcon(32),
            hIconSm = AppIcon(16),
        };
        fixed (char* name = ClassName)
        {
            wc.lpszClassName = name;
            RegisterClassExW(ref wc);
        }
        _messageWindow = CreateWindowExW(0, ClassName, "RemoteHub", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        AddClipboardFormatListener(_messageWindow);
        _ready.Set();
        MSG msg;
        while (GetMessageW(&msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_APP && msg.hwnd == _messageWindow) { RunQueue(); continue; }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    private void RunQueue()
    {
        while (_queue.TryDequeue(out var a))
            try { a(); } catch { /* a broken window must not stop the session */ }
    }

    private void Post(Action a)
    {
        _queue.Enqueue(a);
        PostMessageW(_messageWindow, WM_APP, 0, 0);
    }

    private static string What(HelperInit init) => init.ViewOnly ? "see" : "see and control";

    private void CloseDialogsNow()
    {
        foreach (var (h, w) in _windows.ToList())
            if (w.Kind == FluentKind.Dialog)
            {
                _windows.Remove(h);
                w.Destroy();
            }
    }

    internal void Register(FluentWindow w) => _windows[w.Handle] = w;

    [UnmanagedCallersOnly]
    private static nint WndProc(IntPtr hwnd, uint msg, nint wParam, nint lParam)
    {
        var ui = _instance;
        try
        {
            if (ui is not null)
            {
                if (msg == WM_CLIPBOARDUPDATE && hwnd == ui._messageWindow) { ui.OnClipboard(); return 0; }
                if (ui._windows.TryGetValue(hwnd, out var w))
                {
                    if (msg == WM_DESTROY) ui._windows.Remove(hwnd);
                    else if (w.Process(msg, wParam, lParam, out var result)) return result;
                }
            }
        }
        catch { /* never let an exception cross into Windows */ }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void OnClipboard()
    {
        if (GetClipboardSequenceNumber() == _ownSequence) return; // what we put there ourselves
        if (!IsClipboardFormatAvailable(13))
        {
            HelperClipboard? other = null;
            if (OpenClipboardRetry())
            {
                try { other = ReadOther(); }
                finally { CloseClipboard(); }
            }
            if (other is not null)
            {
                _lastClipboard = "";
                ClipboardContent?.Invoke(other);
            }
            return;
        }
        if (!OpenClipboardRetry()) return;
        string? text = null;
        try
        {
            var data = GetClipboardData(13);
            if (data != IntPtr.Zero)
            {
                var p = (char*)GlobalLock(data);
                if (p != null)
                {
                    try { text = new string(p, 0, (int)Math.Min(GlobalSize(data) / 2, 1_000_000)).Split('\0')[0]; }
                    finally { GlobalUnlock(data); }
                }
            }
        }
        finally { CloseClipboard(); }
        if (text is null || text == _lastClipboard) return;
        _lastClipboard = text;
        ClipboardChanged?.Invoke(text);
    }

    // =====================================================================
    // Look

    /// <summary>RemoteHub's dark palette (see the console's AppChrome), on every device: the windows look the same as the console.</summary>
    internal sealed record Theme(bool Dark, uint Window, uint Header, uint Stroke, uint Text, uint Secondary, uint ButtonFill, uint ButtonHover, uint CodeBox)
    {
        public const uint Accent = 0xFF0078D4, AccentHover = 0xFF1A86D9, AccentPressed = 0xFF006CBE;
        public const uint Danger = 0xFFC42B1C, DangerHover = 0xFFD13A2B, DangerPressed = 0xFFA82418;

        public static readonly Theme DarkTheme = new(true, 0xFF1C2230, 0xFF232A3A, 0xFF2E3850, 0xFFF3F5F9, 0xFFAAB4C5, 0xFF2E3850, 0xFF39445E, 0xFF161B26);

        public static Theme Current() => DarkTheme;
    }

    internal enum FluentKind { Dialog, Banner }
    internal enum ButtonStyle { Primary, Secondary, Danger }
    internal sealed record FluentButton(int Id, string Text, ButtonStyle Style);

    /// <summary>One window: a dialog (header, text, optional code, footer with buttons) or the banner (a pill at the top).</summary>
    internal sealed class FluentWindow(Win32HelperUi ui, Theme theme, FluentKind kind)
    {
        public FluentKind Kind => kind;
        public IntPtr Handle { get; private set; }
        public string Subtitle = "", Heading = "", ShortHeading = "", Body = "", Footnote = "";
        public string? Code;
        public double? Progress;
        public FluentButton[] Buttons = [];
        public Action<int>? Clicked;
        public Action? Cancelled;

        private readonly Dictionary<int, RECT> _buttonRects = [];
        private int _hover = -1, _pressed = -1;
        private bool _tracking;
        private float _s = 1;
        private Action? _tick;
        private IntPtr _font, _bold, _small, _heading, _code;

        private int S(double v) => (int)Math.Round(v * _s);

        // The banner shrinks to a small pill after a few seconds (less of the screen covered) and opens again under the mouse.
        private bool _collapsed;
        private const int CollapseTimer = 2;

        private (int W, int H) BannerSize()
        {
            var h = S(_collapsed ? 36 : 46);
            var sx = GetSystemMetrics(0);
            var text = _collapsed ? TextWidth(_small, ShortHeading) : TextWidth(_bold, Heading) + S(14) + TextWidth(_small, Subtitle);
            return (Math.Min(sx - S(24), S(16 + 12 + 12) + text + S(20) + ButtonWidth(Buttons[0]) + S(10)), h);
        }

        private void SetCollapsed(bool collapsed)
        {
            if (_collapsed == collapsed || Handle == IntPtr.Zero) return;
            _collapsed = collapsed;
            var (w, h) = BannerSize();
            SetWindowPos(Handle, new IntPtr(-1) /* TOPMOST */, (GetSystemMetrics(0) - w) / 2, S(8), w, h, 0x0010 /* NOACTIVATE */);
            Invalidate();
        }

        public void Timer(uint ms, Action tick)
        {
            _tick = tick;
            _timerMs = ms;
        }

        private uint _timerMs;

        public void Show()
        {
            _s = Math.Max(1, GetDpiForSystem() / 96f);
            _font = Font(14, 400);
            _bold = Font(14, 600);
            _small = Font(12, 400);
            _heading = Font(18, 600);
            _code = Font(30, 600);
            int sx = GetSystemMetrics(0), sy = GetSystemMetrics(1);
            int w, h;
            if (kind == FluentKind.Banner) (w, h) = BannerSize();
            else
            {
                w = S(480);
                h = DialogHeight(w);
            }
            var x = (sx - w) / 2;
            var y = kind == FluentKind.Banner ? S(8) : (sy - h) / 2;
            // POPUP | VISIBLE; TOPMOST | TOOLWINDOW (+ NOACTIVATE for the banner)
            uint ex = 0x00000008 | 0x00000080 | (kind == FluentKind.Banner ? 0x08000000u : 0u);
            Handle = CreateWindowExW(ex, ClassName, kind == FluentKind.Banner ? "RemoteHub" : "RemoteHub – Remote access request", 0x80000000,
                x, y, w, h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Handle == IntPtr.Zero) return;
            ui.Register(this);
            var round = kind == FluentKind.Banner ? 2 : 2;  // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            var border = (int)(kind == FluentKind.Banner ? 0x001C2BC4 : 0x00D47800); // red banner, accent dialog (COLORREF)
            DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
            // Not in the captured picture, so the admin sees the screen without it. Only from Windows 10 2004 on: before, Windows
            // paints such a window black in captures – then the banner simply shows in the picture.
            if (kind == FluentKind.Banner && Environment.OSVersion.Version.Build >= 19041) SetWindowDisplayAffinity(Handle, 0x11);
            if (kind == FluentKind.Banner) SetTimer(Handle, CollapseTimer, 8000, IntPtr.Zero);
            ShowWindow(Handle, kind == FluentKind.Banner ? 4 : 5); // SW_SHOWNOACTIVATE / SW_SHOW
            if (kind == FluentKind.Dialog) SetForegroundWindow(Handle);
            if (_tick is not null) SetTimer(Handle, 1, _timerMs, IntPtr.Zero);
        }

        public void Invalidate() { if (Handle != IntPtr.Zero) InvalidateRect(Handle, IntPtr.Zero, false); }

        public void Destroy()
        {
            if (Handle == IntPtr.Zero) return;
            KillTimer(Handle, 1);
            KillTimer(Handle, CollapseTimer);
            DestroyWindow(Handle);
            Handle = IntPtr.Zero;
            foreach (var f in new[] { _font, _bold, _small, _heading, _code }) if (f != IntPtr.Zero) DeleteObject(f);
        }

        private IntPtr Font(int px, int weight) => CreateFontW(-S(px), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE */, 0, "Segoe UI");

        private static int TextWidth(IntPtr font, string text)
        {
            var dc = GetDC(IntPtr.Zero);
            try
            {
                var old = SelectObject(dc, font);
                GetTextExtentPoint32W(dc, text, text.Length, out var size);
                SelectObject(dc, old);
                return size.cx;
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }

        private int TextHeight(IntPtr font, string text, int width)
        {
            if (text.Length == 0) return 0;
            var dc = GetDC(IntPtr.Zero);
            try
            {
                var old = SelectObject(dc, font);
                var r = new RECT { Right = width };
                DrawTextW(dc, text, text.Length, ref r, 0x0010 | 0x0400); // WORDBREAK | CALCRECT
                SelectObject(dc, old);
                return r.Bottom;
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }

        private int ButtonWidth(FluentButton b) => Math.Max(S(96), TextWidth(_bold, b.Text) + S(32));

        // Layout of a dialog: header 56, padding 24, heading, 8, body, (code 84), 12, footnote, 24, footer 64.
        private const int HeaderH = 56, FooterH = 64, Pad = 24;

        private int DialogHeight(int w)
        {
            var inner = w - S(2 * Pad);
            var h = S(HeaderH) + S(Pad) + TextHeight(_heading, Heading, inner) + S(8) + TextHeight(_font, Body, inner);
            if (Code is not null) h += S(20) + S(64);
            if (Footnote.Length > 0) h += S(14) + TextHeight(_small, Footnote, inner);
            return h + S(Pad) + S(FooterH);
        }

        public bool Process(uint msg, nint wParam, nint lParam, out nint result)
        {
            result = 0;
            switch (msg)
            {
                case WM_ERASEBKGND:
                    result = 1;
                    return true;
                case WM_PAINT:
                    Paint();
                    return true;
                case WM_TIMER when wParam == CollapseTimer:
                    KillTimer(Handle, CollapseTimer);
                    if (!_tracking) SetCollapsed(true);
                    return true;
                case WM_TIMER:
                    _tick?.Invoke();
                    return true;
                case WM_NCHITTEST when kind == FluentKind.Dialog:
                    // The header moves the window.
                    var pt = new POINT { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
                    ScreenToClient(Handle, ref pt);
                    if (pt.Y < S(HeaderH) && HitButton(pt.X, pt.Y) < 0) { result = 2; return true; } // HTCAPTION
                    return false;
                case WM_MOUSEMOVE:
                    var hover = HitButton((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                    if (!_tracking)
                    {
                        var tme = new TRACKMOUSEEVENT { cbSize = sizeof(TRACKMOUSEEVENT), dwFlags = 0x2, hwndTrack = Handle }; // TME_LEAVE
                        _tracking = TrackMouseEvent(ref tme);
                    }
                    if (hover != _hover) { _hover = hover; Invalidate(); }
                    if (kind == FluentKind.Banner && _collapsed) SetCollapsed(false);
                    return true;
                case WM_MOUSELEAVE:
                    _tracking = false;
                    if (kind == FluentKind.Banner) SetTimer(Handle, CollapseTimer, 3000, IntPtr.Zero);
                    if (_hover >= 0 || _pressed >= 0) { _hover = _pressed = -1; Invalidate(); }
                    return true;
                case WM_SETCURSOR when _hover >= 0:
                    SetCursor(LoadCursorW(IntPtr.Zero, 32649)); // IDC_HAND
                    result = 1;
                    return true;
                case WM_LBUTTONDOWN:
                    _pressed = HitButton((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                    if (_pressed >= 0) { SetCapture(Handle); Invalidate(); }
                    return true;
                case WM_LBUTTONUP:
                    var up = HitButton((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                    var pressed = _pressed;
                    _pressed = -1;
                    ReleaseCapture();
                    Invalidate();
                    if (pressed >= 0 && up == pressed) Clicked?.Invoke(pressed);
                    return true;
                case WM_KEYDOWN when kind == FluentKind.Dialog && wParam == 0x1B: // Esc = deny (Enter never allows by accident)
                    Cancelled?.Invoke();
                    return true;
                case WM_CLOSE:
                    if (kind == FluentKind.Dialog) Cancelled?.Invoke(); // the banner can only be ended with "Disconnect"
                    return true;
            }
            return false;
        }

        private int HitButton(int x, int y)
        {
            foreach (var (id, r) in _buttonRects)
                if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom) return id;
            return -1;
        }

        private void Paint()
        {
            var ps = new PAINTSTRUCT();
            var hdc = BeginPaint(Handle, ref ps);
            try
            {
                GetClientRect(Handle, out var rc);
                int w = rc.Right, h = rc.Bottom;
                var mem = CreateCompatibleDC(hdc);
                var bmp = CreateCompatibleBitmap(hdc, w, h);
                var oldBmp = SelectObject(mem, bmp);
                try
                {
                    nint g;
                    GdipCreateFromHDC(mem, out g);
                    GdipSetSmoothingMode(g, 4); // anti-alias
                    try
                    {
                        if (kind == FluentKind.Banner) PaintBanner(mem, g, w, h);
                        else PaintDialog(mem, g, w, h);
                    }
                    finally { GdipDeleteGraphics(g); }
                    BitBlt(hdc, 0, 0, w, h, mem, 0, 0, 0x00CC0020);
                }
                finally
                {
                    SelectObject(mem, oldBmp);
                    DeleteObject(bmp);
                    DeleteDC(mem);
                }
            }
            finally { EndPaint(Handle, ref ps); }
        }

        private void PaintDialog(IntPtr dc, nint g, int w, int h)
        {
            FillRect(g, 0, 0, w, h, theme.Window);
            // Header: app mark + "RemoteHub" + what this is.
            FillRect(g, 0, 0, w, S(HeaderH), theme.Header);
            FillRect(g, 0, S(HeaderH) - 1, w, 1, theme.Stroke);
            DrawMark(dc, g, S(Pad), (S(HeaderH) - S(28)) / 2, S(28));
            Text(dc, _bold, "RemoteHub", S(Pad + 40), S(10), w, S(22), theme.Text);
            Text(dc, _small, Subtitle, S(Pad + 40), S(29), w, S(20), theme.Secondary);
            if (Progress is { } p) FillRect(g, 0, S(HeaderH) - S(3), (int)(w * Math.Clamp(p, 0, 1)), S(3), Theme.Accent);

            var inner = w - S(2 * Pad);
            var y = S(HeaderH) + S(Pad);
            y += TextBlock(dc, _heading, Heading, S(Pad), y, inner, theme.Text) + S(8);
            y += TextBlock(dc, _font, Body, S(Pad), y, inner, theme.Secondary);
            if (Code is { } code)
            {
                y += S(20);
                // One rounded box per digit, like a code field.
                int box = S(52), gap = S(8), group = S(18);
                var total = code.Length * box + (code.Length - 1) * gap + (code.Length == 6 ? group - gap : 0);
                var x = (w - total) / 2;
                for (var i = 0; i < code.Length; i++)
                {
                    RoundRect(g, x, y, box, S(64), S(6), theme.CodeBox, theme.Stroke);
                    Text(dc, _code, code[i].ToString(), x, y, box, S(64), theme.Text, center: true);
                    x += box + (i == 2 && code.Length == 6 ? group : gap);
                }
                y += S(64);
            }
            if (Footnote.Length > 0)
            {
                y += S(14);
                TextBlock(dc, _small, Footnote, S(Pad), y, inner, theme.Secondary);
            }

            // Footer band with the buttons on the right (primary first from the right edge inward, like the console's dialogs).
            var fy = h - S(FooterH);
            FillRect(g, 0, fy, w, S(FooterH), theme.Header);
            FillRect(g, 0, fy, w, 1, theme.Stroke);
            _buttonRects.Clear();
            var bx = w - S(Pad);
            for (var i = Buttons.Length - 1; i >= 0; i--)
            {
                var b = Buttons[i];
                var bw = ButtonWidth(b);
                bx -= bw;
                DrawButton(dc, g, b, bx, fy + (S(FooterH) - S(34)) / 2, bw, S(34));
                bx -= S(8);
            }
        }

        private void PaintBanner(IntPtr dc, nint g, int w, int h)
        {
            FillRect(g, 0, 0, w, h, theme.Header);
            // Red "live" dot, the message and the app name (collapsed: only who).
            var dot = S(12);
            Ellipse(g, S(16), (h - dot) / 2, dot, dot, Theme.Danger);
            var x = S(16) + dot + S(12);
            if (_collapsed)
                Text(dc, _small, ShortHeading, x, 0, TextWidth(_small, ShortHeading) + S(4), h, theme.Text, vcenter: true);
            else
            {
                var tw = TextWidth(_bold, Heading);
                Text(dc, _bold, Heading, x, 0, tw + S(4), h, theme.Text, vcenter: true);
                x += tw + S(14);
                Text(dc, _small, Subtitle, x, 0, TextWidth(_small, Subtitle) + S(4), h, theme.Secondary, vcenter: true);
            }
            _buttonRects.Clear();
            var b = Buttons[0];
            var bw = ButtonWidth(b);
            DrawButton(dc, g, b, w - bw - S(7), S(6), bw, h - S(12), _collapsed ? _small : _bold);
        }

        private void DrawButton(IntPtr dc, nint g, FluentButton b, int x, int y, int w, int h, IntPtr font = default)
        {
            _buttonRects[b.Id] = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
            var hot = _hover == b.Id;
            var down = _pressed == b.Id;
            var (fill, stroke, text) = b.Style switch
            {
                ButtonStyle.Primary => (down ? Theme.AccentPressed : hot ? Theme.AccentHover : Theme.Accent, 0u, 0xFFFFFFFFu),
                ButtonStyle.Danger => (down ? Theme.DangerPressed : hot ? Theme.DangerHover : Theme.Danger, 0u, 0xFFFFFFFFu),
                _ => (hot || down ? theme.ButtonHover : theme.ButtonFill, theme.Stroke, theme.Text),
            };
            RoundRect(g, x, y, w, h, S(4), fill, stroke);
            Text(dc, font == default ? _bold : font, b.Text, x, y, w, h, text, center: true);
        }

        /// <summary>The app mark: RemoteHub's icon (embedded in this exe); a monitor in an accent tile if it can't be loaded.</summary>
        private void DrawMark(IntPtr dc, nint g, int x, int y, int size)
        {
            if (AppIcon(size) is var icon && icon != IntPtr.Zero)
            {
                GdipFlush(g, 1); // GDI+ drawing first, then the icon on top (GDI)
                DrawIconEx(dc, x, y, icon, size, size, 0, IntPtr.Zero, 3 /* DI_NORMAL */);
                return;
            }
            RoundRect(g, x, y, size, size, S(6), Theme.Accent, 0);
            var m = size / 5f;
            GdipCreatePen1(0xFFFFFFFF, Math.Max(1.5f, _s * 1.6f), 2, out var pen);
            try
            {
                using var path = new GdiPath();
                path.RoundRect(x + m, y + m * 1.1f, size - 2 * m, size * 0.48f, _s * 2);
                GdipDrawPath(g, pen, path.Handle);
                var cx = x + size / 2f;
                GdipDrawLine(g, pen, cx, y + m * 1.1f + size * 0.48f, cx, y + size - m * 1.1f);
                GdipDrawLine(g, pen, cx - m, y + size - m * 1.1f, cx + m, y + size - m * 1.1f);
            }
            finally { GdipDeletePen(pen); }
        }

        private static void FillRect(nint g, int x, int y, int w, int h, uint argb)
        {
            if (w <= 0 || h <= 0) return;
            GdipCreateSolidFill(argb, out var brush);
            GdipFillRectangleI(g, brush, x, y, w, h);
            GdipDeleteBrush(brush);
        }

        private static void Ellipse(nint g, int x, int y, int w, int h, uint argb)
        {
            GdipCreateSolidFill(argb, out var brush);
            GdipFillEllipseI(g, brush, x, y, w, h);
            GdipDeleteBrush(brush);
        }

        private static void RoundRect(nint g, int x, int y, int w, int h, float radius, uint fill, uint stroke)
        {
            using var path = new GdiPath();
            path.RoundRect(x + 0.5f, y + 0.5f, w - 1, h - 1, radius);
            GdipCreateSolidFill(fill, out var brush);
            GdipFillPath(g, brush, path.Handle);
            GdipDeleteBrush(brush);
            if (stroke == 0) return;
            GdipCreatePen1(stroke, 1, 2, out var pen);
            GdipDrawPath(g, pen, path.Handle);
            GdipDeletePen(pen);
        }

        private static void Text(IntPtr dc, IntPtr font, string text, int x, int y, int w, int h, uint argb, bool center = false, bool vcenter = false)
        {
            var old = SelectObject(dc, font);
            SetBkMode(dc, 1); // transparent
            SetTextColor(dc, (argb & 0xFF) << 16 | (argb & 0xFF00) | ((argb >> 16) & 0xFF));
            var r = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
            DrawTextW(dc, text, text.Length, ref r, center ? 0x1u | 0x4 | 0x20 : vcenter ? 0x4u | 0x20 | 0x8000 /* END_ELLIPSIS */ : 0x20u); // CENTER|VCENTER|SINGLELINE
            SelectObject(dc, old);
        }

        /// <summary>Wrapped text; returns its height.</summary>
        private static int TextBlock(IntPtr dc, IntPtr font, string text, int x, int y, int w, uint argb)
        {
            if (text.Length == 0) return 0;
            var old = SelectObject(dc, font);
            SetBkMode(dc, 1);
            SetTextColor(dc, (argb & 0xFF) << 16 | (argb & 0xFF00) | ((argb >> 16) & 0xFF));
            var r = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + 2000 };
            var height = DrawTextW(dc, text, text.Length, ref r, 0x0010); // WORDBREAK
            SelectObject(dc, old);
            return height;
        }
    }

    /// <summary>A GDI+ path (rounded rectangles).</summary>
    private sealed class GdiPath : IDisposable
    {
        public nint Handle;
        public GdiPath() => GdipCreatePath(0, out Handle);

        public void RoundRect(float x, float y, float w, float h, float r)
        {
            r = Math.Min(r, Math.Min(w, h) / 2);
            var d = r * 2;
            if (d <= 0) { GdipAddPathRectangle(Handle, x, y, w, h); return; }
            GdipAddPathArc(Handle, x, y, d, d, 180, 90);
            GdipAddPathArc(Handle, x + w - d, y, d, d, 270, 90);
            GdipAddPathArc(Handle, x + w - d, y + h - d, d, d, 0, 90);
            GdipAddPathArc(Handle, x, y + h - d, d, d, 90, 90);
            GdipClosePathFigure(Handle);
        }

        public void Dispose() => GdipDeletePath(Handle);
    }

    // ---- Win32 / GDI / GDI+

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public int cbSize;
        public uint style;
        public delegate* unmanaged<IntPtr, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor;
        public nint hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [StructLayout(LayoutKind.Sequential)] private struct PAINTSTRUCT { public IntPtr hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate; public fixed byte rgbReserved[32]; }
    [StructLayout(LayoutKind.Sequential)] private struct GdiplusStartupInput { public uint GdiplusVersion; public IntPtr DebugEventCallback; public int SuppressBackgroundThread, SuppressExternalCodecs; }

    [DllImport("user32.dll")] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll")] private static extern IntPtr LoadImageW(IntPtr instance, nint name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int cx, int cy, int step, IntPtr brush, int flags);
    [DllImport("gdiplus.dll")] private static extern int GdipFlush(nint graphics, int intention);

    private static readonly Dictionary<int, IntPtr> Icons = [];

    /// <summary>RemoteHub's icon at this size (the exe's icon: resource 32512, from ApplicationIcon), kept for the process.</summary>
    private static IntPtr AppIcon(int size)
    {
        lock (Icons)
        {
            if (!Icons.TryGetValue(size, out var icon))
                Icons[size] = icon = LoadImageW(GetModuleHandleW(null), 32512, 1 /* IMAGE_ICON */, size, size, 0);
            return icon;
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, nint menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(IntPtr hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(MSG* msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(MSG* msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(MSG* msg);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);
    [DllImport("user32.dll")] private static extern nint SetTimer(IntPtr hwnd, nint id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, nint id);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, nint name);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern void* GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(IntPtr mem);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int length, out SIZE size);
    [DllImport("gdiplus.dll")] private static extern int GdiplusStartup(out nuint token, ref GdiplusStartupInput input, IntPtr output);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateFromHDC(IntPtr hdc, out nint graphics);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteGraphics(nint graphics);
    [DllImport("gdiplus.dll")] private static extern int GdipSetSmoothingMode(nint graphics, int mode);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateSolidFill(uint argb, out nint brush);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteBrush(nint brush);
    [DllImport("gdiplus.dll")] private static extern int GdipCreatePen1(uint argb, float width, int unit, out nint pen);
    [DllImport("gdiplus.dll")] private static extern int GdipDeletePen(nint pen);
    [DllImport("gdiplus.dll")] private static extern int GdipFillRectangleI(nint graphics, nint brush, int x, int y, int w, int h);
    [DllImport("gdiplus.dll")] private static extern int GdipFillEllipseI(nint graphics, nint brush, int x, int y, int w, int h);
    [DllImport("gdiplus.dll")] private static extern int GdipDrawLine(nint graphics, nint pen, float x1, float y1, float x2, float y2);
    [DllImport("gdiplus.dll")] private static extern int GdipCreatePath(int fillMode, out nint path);
    [DllImport("gdiplus.dll")] private static extern int GdipDeletePath(nint path);
    [DllImport("gdiplus.dll")] private static extern int GdipAddPathArc(nint path, float x, float y, float w, float h, float start, float sweep);
    [DllImport("gdiplus.dll")] private static extern int GdipAddPathRectangle(nint path, float x, float y, float w, float h);
    [DllImport("gdiplus.dll")] private static extern int GdipClosePathFigure(nint path);
    [DllImport("gdiplus.dll")] private static extern int GdipFillPath(nint graphics, nint brush, nint path);
    [DllImport("gdiplus.dll")] private static extern int GdipDrawPath(nint graphics, nint pen, nint path);
}
