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

    /// <summary>
    /// The banner for the whole session; it can't be hidden. <paramref name="disconnect"/> when the user ends it,
    /// <paramref name="hide"/> when they hide or show their screen (<see cref="DesktopScreenHide"/>).
    /// </summary>
    void ShowBanner(HelperInit init, Action disconnect, Action<string> hide);

    /// <summary>The banner shows what is hidden now: the user's own choice, and the admin's privacy screen.</summary>
    void ShowPrivacy(DesktopPrivacy privacy);

    /// <summary>
    /// From now on the consent question, the code and the banner offer "Hide apps…": the user picks running apps, those
    /// in <paramref name="hidden"/> already ticked, those in <paramref name="always"/> (policy) ticked for good.
    /// <paramref name="chosen"/> gets the new choice – apps chosen before that aren't running now stay in it.
    /// </summary>
    void OfferHideApps(IReadOnlySet<string> hidden, IReadOnlySet<string> always, Action<IReadOnlySet<string>> chosen);

    /// <summary>
    /// The admin's privacy screen over every monitor, kept out of the captured picture (style Off removes it), and the
    /// block of the device's mouse and keyboard. <paramref name="done"/> gets null when it is as asked, else why not
    /// (then nothing is shown and nothing blocked).
    /// </summary>
    void SetPrivacyScreen(DesktopPrivacyScreen screen, Action<string?> done);

    /// <summary>
    /// The admin's pointer is about to go to this screen point (before each mouse event the agent injects). Over the banner,
    /// the banner lets the admin's clicks through – it is the user's, and the admin usually can't even see it – until the
    /// user's own mouse moves again.
    /// </summary>
    void AdminPointer(int x, int y) { }

    void CloseDialogs();
    void SetClipboard(string text);
    event Action<string>? ClipboardChanged;

    /// <summary>A PNG from RemoteHub onto the device's clipboard.</summary>
    /// <summary>An image from RemoteHub: PNG bytes, or BMP bytes when <paramref name="png"/> is false.</summary>
    void SetClipboardImage(byte[] image, bool png);

    /// <summary>Files (already written on the device) onto the clipboard, to be pasted like files copied in Explorer.</summary>
    void SetClipboardFiles(IReadOnlyList<string> paths);

    /// <summary>The device's clipboard now holds an image or files.</summary>
    event Action<HelperClipboard>? ClipboardContent;

    /// <summary>
    /// Files or an image copied in RemoteHub, offered without their data: when someone pastes, <paramref name="fetch"/>
    /// gets it (the paste waits; its argument names the program that pastes); null if it didn't arrive.
    /// </summary>
    void OfferClipboard(DesktopTransfer offer, Func<string, HelperClipboard?> fetch);
}

/// <summary>
/// The device-side windows in RemoteHub's look (the console's dark palette, accent, rounded corners). Drawn by hand
/// with GDI+ – the agent has no UI framework, it is published trimmed.
/// </summary>
internal sealed unsafe partial class Win32HelperUi : IHelperUi
{
    private const uint WM_APP = 0x8000, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_TIMER = 0x0113, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002,
        WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSELEAVE = 0x02A3, WM_NCHITTEST = 0x0084,
        WM_SETCURSOR = 0x0020, WM_KEYDOWN = 0x0100, WM_CLIPBOARDUPDATE = 0x031D, WM_DISPLAYCHANGE = 0x007E, WM_MOUSEWHEEL = 0x020A,
        WM_RENDERFORMAT = 0x0305, WM_RENDERALLFORMATS = 0x0306, WM_DESTROYCLIPBOARD = 0x0307;
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

    public void AskConsent(HelperInit init, Action<bool> answered) => Post(() => AskConsentNow(init, answered));

    private void AskConsentNow(HelperInit init, Action<bool> answered)
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
            Buttons = _appsOffer is null
                ? [new FluentButton(1, "Allow", ButtonStyle.Primary), new FluentButton(2, "Deny", ButtonStyle.Secondary)]
                : [new FluentButton(1, "Allow", ButtonStyle.Primary), new FluentButton(PickAppsButton, "Allow and hide apps…", ButtonStyle.Secondary), new FluentButton(2, "Deny", ButtonStyle.Secondary)],
            Cancelled = () => Answer(false),
        };
        // The countdown goes on in the picker (from the start again): "Allow" there allows with the choice, "Cancel" asks again,
        // no answer in time denies – as in the question itself.
        w.Clicked = id =>
        {
            if (id != PickAppsButton) { Answer(id == 1); return; }
            done = true;
            PickApps(init, "Allow", () => answered(true), () => AskConsentNow(init, answered), timedOut: () => answered(false));
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
    }

    public void ShowPin(HelperInit init, Action denied) => Post(() => ShowPinNow(init, denied));

    private void ShowPinNow(HelperInit init, Action denied)
    {
        CloseDialogsNow();
        var w = new FluentWindow(this, Theme.Current(), FluentKind.Dialog)
        {
            Subtitle = "Remote access request",
            Heading = $"{init.Account} wants to {What(init)} your screen",
            Body = "If you agree, read this code out to them. Nobody can connect without it.",
            Code = init.Pin,
            Buttons = _appsOffer is null
                ? [new FluentButton(2, "Deny", ButtonStyle.Secondary)]
                : [new FluentButton(PickAppsButton, "Hide apps…", ButtonStyle.Secondary), new FluentButton(2, "Deny", ButtonStyle.Secondary)],
        };
        // After picking (or cancelling) the code is shown again – it stays the same.
        w.Clicked = id =>
        {
            if (id == PickAppsButton) PickApps(init, "Hide selected", () => ShowPinNow(init, denied), () => ShowPinNow(init, denied));
            else { CloseDialogsNow(); denied(); }
        };
        w.Cancelled = () => { CloseDialogsNow(); denied(); };
        w.Show();
    }

    // ---- hiding apps

    private const int PickAppsButton = 5, PickDone = 20, PickCancel = 21;
    private (IReadOnlySet<string> Hidden, IReadOnlySet<string> Always, Action<IReadOnlySet<string>> Chosen)? _appsOffer;
    private WindowsAppWindows? _apps;

    public void OfferHideApps(IReadOnlySet<string> hidden, IReadOnlySet<string> always, Action<IReadOnlySet<string>> chosen) => Post(() =>
    {
        _appsOffer = (hidden, always, chosen);
        LayoutBanner();
    });

    /// <summary>
    /// "Hide apps from …": the running apps with a box each. <paramref name="done"/> after the choice went to the
    /// session, <paramref name="cancelled"/> when nothing changed.
    /// </summary>
    private void PickApps(HelperInit init, string doneText, Action done, Action cancelled, Action? timedOut = null)
    {
        if (_appsOffer is not { } offer) return;
        CloseDialogsNow();
        IReadOnlyList<RunningApp> running;
        try { running = (_apps ??= new WindowsAppWindows()).Running(); }
        catch { running = []; }
        var items = running.Select(a =>
        {
            var locked = offer.Always.Contains(a.Id);
            var detail = locked ? "Always hidden (IT policy)" : a.Windows == 1 ? "1 window" : $"{a.Windows} windows";
            return new PickItem(a.Id, a.Name, detail, a.Icon, locked || offer.Hidden.Contains(a.Id), locked);
        }).ToList();
        var w = new FluentWindow(this, Theme.Current(), FluentKind.Dialog)
        {
            Subtitle = "Privacy",
            Heading = $"Hide apps from {init.Account}",
            Body = "Windows of the apps you select are covered for them – also when you open, move or restore them later. They can't click or type into them.",
            Items = items,
            Buttons = [new FluentButton(PickDone, doneText, ButtonStyle.Primary), new FluentButton(PickCancel, "Cancel", ButtonStyle.Secondary)],
        };
        w.Clicked = id =>
        {
            CloseDialogsNow();
            if (id != PickDone) { cancelled(); return; }
            // Apps picked before that aren't running now stay hidden: they are covered again when they start.
            var shown = items.Select(i => i.Id).ToHashSet();
            var choice = offer.Hidden.Where(h => !shown.Contains(h))
                .Concat(items.Where(i => i.Checked && !i.Locked).Select(i => i.Id)).ToHashSet();
            _appsOffer = offer with { Hidden = choice };
            offer.Chosen(choice);
            done();
        };
        w.Cancelled = () => { CloseDialogsNow(); cancelled(); };
        if (timedOut is not null)
        {
            // In the consent question: denied when nobody answers in time, like the question itself.
            var limit = Math.Max(5, init.TimeoutSeconds);
            var seconds = limit;
            w.Footnote = $"Denied automatically in {seconds} s";
            w.Progress = 1;
            w.Timer(1000, () =>
            {
                seconds--;
                if (seconds <= 0) { CloseDialogsNow(); timedOut(); return; }
                w.Footnote = $"Denied automatically in {seconds} s";
                w.Progress = seconds / (double)limit;
                w.Invalidate();
            });
        }
        w.Show();
        KeepOnTop(); // above the privacy screen, and clickable while input is blocked
    }

    public void ShowBanner(HelperInit init, Action disconnect, Action<string> hide) => Post(() =>
    {
        _banner = new FluentWindow(this, Theme.Current(), FluentKind.Banner)
        {
            Subtitle = "RemoteHub",
            Clicked = id =>
            {
                if (id == DisconnectButton) disconnect();
                else if (id == HideButton && _privacy.User != DesktopScreenHide.None) hide(DesktopScreenHide.None);
                else if (id == HideButton) AskHide(init, hide);
                else if (id == PickAppsButton) PickApps(init, "Hide selected", () => { }, () => { });
            },
        };
        _bannerInit = init;
        LayoutBanner();
        _banner.Show();
        KeepOnTop();
    });

    private const int DisconnectButton = 3, HideButton = 4;
    private FluentWindow? _banner;

    // 1 while the banner lets clicks through (the admin's pointer is over it, and the user's mouse hasn't moved since).
    private int _bannerPassThrough;

    /// <summary>
    /// Input thread, before an injected mouse event. The banner is made click-through before the event goes out, so even
    /// the first click passes; the user's own mouse makes it clickable again at once (<see cref="LocalPointerWatch"/>), so
    /// "Disconnect" can't be kept out of the user's reach. Never waits for anything but the UI thread, which never waits
    /// for this thread.
    /// </summary>
    public void AdminPointer(int x, int y)
    {
        if (_banner is not { Handle: var h } || h == IntPtr.Zero || !GetWindowRect(h, out var r)) return;
        var over = x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
        if (over && Volatile.Read(ref _bannerPassThrough) == 0)
        {
            SetClickThrough(h, true);
            Volatile.Write(ref _bannerPassThrough, 1);
            LocalPointerWatch.Start(() => Post(BannerClickableAgain));
        }
        else if (!over && Interlocked.Exchange(ref _bannerPassThrough, 0) == 1)
        {
            SetClickThrough(h, false);
            LocalPointerWatch.Stop();
        }
    }

    /// <summary>UI thread: the user's own mouse moved – the banner is theirs to click again.</summary>
    private void BannerClickableAgain()
    {
        if (Interlocked.Exchange(ref _bannerPassThrough, 0) == 0) return;
        if (_banner is { Handle: var h } && h != IntPtr.Zero) SetClickThrough(h, false);
        LocalPointerWatch.Stop();
    }

    /// <summary>Clicks go through the (layered) window to what is below it, or not.</summary>
    private static void SetClickThrough(IntPtr h, bool on)
    {
        var ex = GetWindowLongPtrW(h, -20); // GWL_EXSTYLE
        var next = on ? ex | 0x20 : ex & ~0x20; // WS_EX_TRANSPARENT
        if (next != ex) SetWindowLongPtrW(h, -20, next);
    }
    private HelperInit? _bannerInit;
    private DesktopPrivacy _privacy = DesktopPrivacy.Nothing;

    /// <summary>The banner's text and buttons for what is hidden now.</summary>
    private void LayoutBanner()
    {
        if (_banner is not { } b || _bannerInit is not { } init) return;
        var hidden = _privacy.User != DesktopScreenHide.None;
        var blurred = _privacy.User == DesktopScreenHide.Blur;
        b.Heading = hidden
            ? $"Your screen is {(blurred ? "blurred" : "hidden")} for {init.Account}"
            : $"Your screen is being {(init.ViewOnly ? "viewed" : "viewed and controlled")} by {init.Account}";
        b.ShortHeading = hidden ? $"{(blurred ? "Blurred" : "Hidden")} for {init.Account}" : $"{(init.ViewOnly ? "Viewed" : "Controlled")} by {init.Account}";
        if (_privacy.Screen != DesktopPrivacyStyle.Off)
        {
            b.Heading += _privacy.LocalInputBlocked ? " · mouse and keyboard blocked" : "";
            b.Subtitle = _privacy.LocalInputBlocked ? "Click \"Disconnect\" to end" : "RemoteHub";
        }
        else b.Subtitle = "RemoteHub";
        if (_privacy.HiddenApps > 0 && !hidden)
        {
            var apps = _privacy.HiddenApps == 1 ? "1 app hidden" : $"{_privacy.HiddenApps} apps hidden";
            b.Heading += " · " + apps;
            b.ShortHeading += " · " + apps;
        }
        b.Buttons =
        [
            hidden ? new FluentButton(HideButton, "Show screen", ButtonStyle.Primary) : new FluentButton(HideButton, "Hide screen", ButtonStyle.Secondary),
            .. _appsOffer is not null && !hidden ? new[] { new FluentButton(PickAppsButton, "Hide apps…", ButtonStyle.Secondary) } : [],
            new FluentButton(DisconnectButton, "Disconnect", ButtonStyle.Danger),
        ];
        // While something is hidden the way back stays in sight: no collapsing to the pill.
        b.KeepOpen = hidden || _privacy.Screen != DesktopPrivacyStyle.Off;
        b.Accent = hidden;
        b.Relayout();
        KeepOnTop();
    }

    /// <summary>"Hide your screen": black it out or blur it.</summary>
    private void AskHide(HelperInit init, Action<string> hide)
    {
        CloseDialogsNow();
        var w = new FluentWindow(this, Theme.Current(), FluentKind.Dialog)
        {
            Subtitle = "Privacy",
            Heading = $"Hide your screen from {init.Account}",
            Body = "Black out: they see nothing until you show it again.\nBlur: they see roughly what's on your screen but can't read it.\n\n" +
                   "They can't use your mouse or keyboard while your screen is hidden.",
            Buttons = [new FluentButton(10, "Black out", ButtonStyle.Primary), new FluentButton(11, "Blur", ButtonStyle.Secondary), new FluentButton(12, "Cancel", ButtonStyle.Secondary)],
        };
        w.Clicked = id =>
        {
            CloseDialogsNow();
            if (id == 10) hide(DesktopScreenHide.Black);
            else if (id == 11) hide(DesktopScreenHide.Blur);
        };
        w.Cancelled = CloseDialogsNow;
        w.Show();
        KeepOnTop(); // above the privacy screen, and clickable while input is blocked
    }

    public void ShowPrivacy(DesktopPrivacy privacy) => Post(() =>
    {
        _privacy = privacy;
        LayoutBanner();
    });

    // ---- the admin's privacy screen

    private readonly List<FluentWindow> _overlays = [];
    private DesktopPrivacyScreen _screen = new(DesktopPrivacyStyle.Off);

    private Action<string?>? _screenChanged;

    public void SetPrivacyScreen(DesktopPrivacyScreen screen, Action<string?> done) => Post(() =>
    {
        _screenChanged = done; // called again if the overlays have to come down later (monitors changed)
        var style = DesktopPrivacyStyle.Valid(screen.Style);
        if (style == DesktopPrivacyStyle.Off)
        {
            RemoveOverlays();
            done(null);
            return;
        }
        _screen = new DesktopPrivacyScreen(style, screen.BlockLocalInput);
        var error = ShowOverlays();
        if (error is null)
        {
            if (screen.BlockLocalInput) error = InputBlocker.Start();
            else InputBlocker.Stop();
        }
        if (error is not null) RemoveOverlays();
        done(error);
    });

    /// <summary>One overlay per monitor, in the current style; null when all of them are up and out of the captured picture.</summary>
    private string? ShowOverlays()
    {
        // Without WDA_EXCLUDEFROMCAPTURE (before Windows 10 2004) the overlay would black out the admin's picture as well.
        if (Environment.OSVersion.Version.Build < ExcludeFromCaptureBuild) return "Windows on this device is too old for the privacy screen (it needs Windows 10 version 2004 or later).";
        // The new ones go up before the old ones come down: the screen isn't uncovered for a moment.
        var old = _overlays.ToList();
        _overlays.Clear();
        try { return AddOverlays(); }
        finally { foreach (var o in old) o.Destroy(); }
    }

    private string? AddOverlays()
    {
        foreach (var m in DisplayMonitors.List())
        {
            var w = new FluentWindow(this, Theme.Current(), FluentKind.Overlay)
            {
                Heading = "Your IT administrator is working on this device",
                Body = "Your screen is hidden while they work. You can end the session with \"Disconnect\" at the top of the screen.",
                Footnote = _screen.BlockLocalInput ? "Mouse and keyboard are blocked." : "",
                Black = _screen.Style == DesktopPrivacyStyle.Black,
                Bounds = (m.X, m.Y, m.Width, m.Height),
            };
            if (w.Show() is { } error)
            {
                w.Destroy();
                return error;
            }
            _overlays.Add(w);
        }
        if (_overlays.Count == 0) return "The device has no monitor to cover.";
        KeepOnTop();
        SetTimer(_messageWindow, TopTimer, 500, IntPtr.Zero); // other topmost windows (Task Manager) must not stay above it
        return null;
    }

    private void RemoveOverlays()
    {
        InputBlocker.Stop();
        KillTimer(_messageWindow, TopTimer);
        foreach (var o in _overlays) o.Destroy();
        _overlays.Clear();
        _screen = new DesktopPrivacyScreen(DesktopPrivacyStyle.Off);
    }

    private const int TopTimer = 7;

    /// <summary>The overlays on top of everything, and the banner on top of them (so "Disconnect" stays reachable).</summary>
    private void KeepOnTop()
    {
        foreach (var o in _overlays)
            if (o.Handle != IntPtr.Zero) SetWindowPos(o.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // TOPMOST; NOSIZE | NOMOVE | NOACTIVATE
        // Above them: an open dialog ("Hide your screen") and the banner – both stay usable, also while input is blocked.
        var allowed = new List<(int, int, int, int)>();
        foreach (var w in _windows.Values.Where(w => w.Kind == FluentKind.Dialog).Append(_banner))
            if (w is { Handle: var h } && h != IntPtr.Zero)
            {
                SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
                if (GetWindowRect(h, out var r)) allowed.Add((r.Left, r.Top, r.Right, r.Bottom));
            }
        InputBlocker.AllowClicks([.. allowed]);
    }

    /// <summary>Monitors were added, removed or changed: the overlays follow.</summary>
    private void DisplayChanged()
    {
        if (_overlays.Count == 0) return;
        if (ShowOverlays() is not { } error) return;
        RemoveOverlays(); // nothing half-covered stays
        _screenChanged?.Invoke("The privacy screen was turned off because the device's monitors changed: " + error);
    }

    private const int ExcludeFromCaptureBuild = 19041;
    private bool _displayChangePending;

    /// <summary>Every overlay gets WM_DISPLAYCHANGE: they are rebuilt once, after Windows settled the new layout.</summary>
    private void OverlaysNeedUpdate()
    {
        if (_displayChangePending) return;
        _displayChangePending = true;
        Post(() =>
        {
            _displayChangePending = false;
            DisplayChanged();
        });
    }

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
            InputBlocker.Stop();
            LocalPointerWatch.Stop();
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
        VirtualFiles.SecurityResult = VirtualFiles.AllowInteractiveCallers(); // pastes from the user's programs reach our clipboard object
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
                if (hwnd == ui._messageWindow)
                    switch (msg)
                    {
                        case WM_CLIPBOARDUPDATE: ui.OnClipboard(); return 0;
                        case WM_RENDERFORMAT: ui.Render((uint)wParam); return 0;
                        case WM_RENDERALLFORMATS: return 0; // the helper ends: an offer not pasted yet is gone, nothing is fetched
                        case WM_DESTROYCLIPBOARD: ui.OfferEnded(); return 0;
                        case WM_TIMER when wParam == TopTimer: ui.KeepOnTop(); return 0;
                    }
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
        if (GetClipboardOwner() == _messageWindow) return;        // still ours (e.g. an offer that was just pasted): never sent back
        if (VirtualFiles.IsCurrent(_virtual)) return;             // our virtual files (OLE owns the clipboard for us)
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

    internal enum FluentKind { Dialog, Banner, Overlay }

    /// <summary>A row of the app picker.</summary>
    internal sealed class PickItem(string id, string name, string detail, IntPtr icon, bool isChecked, bool locked)
    {
        public string Id => id;
        public string Name => name;
        public string Detail => detail;
        public IntPtr Icon => icon;
        public bool Locked => locked;
        public bool Checked { get; set; } = isChecked;
    }
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
        /// <summary>Banner: never collapses to the pill (something is hidden – the way back stays in sight).</summary>
        public bool KeepOpen;
        /// <summary>Banner: the accent colour instead of red (the screen is hidden from the admin).</summary>
        public bool Accent;
        /// <summary>Overlay: plain black instead of the message.</summary>
        public bool Black;
        /// <summary>Overlay: the monitor it covers, in screen pixels.</summary>
        public (int X, int Y, int Width, int Height) Bounds;
        /// <summary>Dialog: a list of rows with a box each, between the text and the buttons (the app picker).</summary>
        public List<PickItem>? Items;

        // The list: rows of RowH, at most MaxRows visible (the wheel and the arrow keys scroll).
        private const int RowH = 40, MaxRows = 8;
        private int _scroll, _focusRow = -1, _hoverRow = -1;
        private RECT _listRect;

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
            var buttons = BannerButtons().Sum(b => ButtonWidth(b) + S(8)) - S(8);
            return (Math.Min(sx - S(24), S(16 + 12 + 12) + text + S(20) + buttons + S(10)), h);
        }

        /// <summary>The banner's buttons as shown: all of them, or only the last one ("Disconnect") on the pill.</summary>
        private FluentButton[] BannerButtons() => _collapsed && Buttons.Length > 1 ? [Buttons[^1]] : Buttons;

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

        /// <summary>Null when the window is up; for an overlay, why it couldn't be shown (then nothing is shown).</summary>
        public string? Show()
        {
            _s = Math.Max(1, GetDpiForSystem() / 96f);
            _font = Font(14, 400);
            _bold = Font(14, 600);
            _small = Font(12, 400);
            _heading = Font(18, 600);
            _code = Font(30, 600);
            if (kind == FluentKind.Overlay) return ShowOverlay();
            if (Items is { Count: > 0 }) _focusRow = 0; // the keyboard starts in the list
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
            // POPUP | VISIBLE; TOPMOST | TOOLWINDOW (+ NOACTIVATE and LAYERED for the banner: layered, it can let the admin's
            // clicks through – see AdminPointer)
            uint ex = 0x00000008 | 0x00000080 | (kind == FluentKind.Banner ? 0x08000000u | 0x00080000u : 0u);
            Handle = CreateWindowExW(ex, ClassName, kind == FluentKind.Banner ? "RemoteHub" : "RemoteHub – " + (Subtitle.Length > 0 ? Subtitle : "Remote access request"), 0x80000000,
                x, y, w, h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Handle == IntPtr.Zero) return null;
            ui.Register(this);
            if (kind == FluentKind.Banner) SetLayeredWindowAttributes(Handle, 0, 255, 0x2); // LWA_ALPHA: fully opaque
            var round = kind == FluentKind.Banner ? 2 : 2;  // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            SetBorder();
            // Not in the captured picture, so the admin sees the screen without it. Only from Windows 10 2004 on: before, Windows
            // paints such a window black in captures – then the banner simply shows in the picture.
            if (kind == FluentKind.Banner && Environment.OSVersion.Version.Build >= 19041) SetWindowDisplayAffinity(Handle, 0x11);
            if (kind == FluentKind.Banner) SetTimer(Handle, CollapseTimer, 8000, IntPtr.Zero);
            ShowWindow(Handle, kind == FluentKind.Banner ? 4 : 5); // SW_SHOWNOACTIVATE / SW_SHOW
            if (kind == FluentKind.Dialog) SetForegroundWindow(Handle);
            if (_tick is not null) SetTimer(Handle, 1, _timerMs, IntPtr.Zero);
            return null;
        }

        /// <summary>
        /// The privacy screen over one monitor. Clicks go through it (layered + transparent: the admin's input reaches the
        /// windows below), and it is kept out of the captured picture – checked before it is shown: a window that is
        /// captured would black out the admin's picture too.
        /// </summary>
        private string? ShowOverlay()
        {
            var (x, y, w, h) = Bounds;
            // TOPMOST | TOOLWINDOW | NOACTIVATE | LAYERED | TRANSPARENT; POPUP (not visible yet)
            Handle = CreateWindowExW(0x00000008 | 0x00000080 | 0x08000000 | 0x00080000 | 0x00000020, ClassName, "RemoteHub – Privacy screen", 0x80000000,
                x, y, w, h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Handle == IntPtr.Zero) return "The privacy screen couldn't be created.";
            ui.Register(this);
            SetLayeredWindowAttributes(Handle, 0, 255, 0x2); // LWA_ALPHA: fully opaque
            var square = 1; // DWMWCP_DONOTROUND
            DwmSetWindowAttribute(Handle, 33, ref square, sizeof(int));
            if (!SetWindowDisplayAffinity(Handle, 0x11) || !GetWindowDisplayAffinity(Handle, out var affinity) || affinity != 0x11)
                return "Windows on this device can't keep the privacy screen out of the picture, so it isn't shown.";
            ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
            return null;
        }

        /// <summary>The banner after its text, buttons or state changed: new size, border colour, open or collapsed.</summary>
        public void Relayout()
        {
            if (Handle == IntPtr.Zero || kind != FluentKind.Banner) return;
            if (KeepOpen) _collapsed = false;
            var (w, h) = BannerSize();
            SetWindowPos(Handle, new IntPtr(-1) /* TOPMOST */, (GetSystemMetrics(0) - w) / 2, S(8), w, h, 0x0010 /* NOACTIVATE */);
            SetBorder();
            if (!KeepOpen && !_tracking) SetTimer(Handle, CollapseTimer, 8000, IntPtr.Zero);
            Invalidate();
        }

        private void SetBorder()
        {
            // Red while the screen goes to the admin, accent while it's hidden from them and for dialogs (COLORREF).
            var border = (int)(kind == FluentKind.Banner && !Accent ? 0x001C2BC4 : 0x00D47800);
            DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
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
            if (Items is not null) h += S(16) + S(RowH) * Math.Clamp(Items.Count, 1, MaxRows);
            if (Footnote.Length > 0) h += S(14) + TextHeight(_small, Footnote, inner);
            return h + S(Pad) + S(FooterH);
        }

        public bool Process(uint msg, nint wParam, nint lParam, out nint result)
        {
            result = 0;
            if (Items is not null && ProcessList(msg, wParam, lParam, ref result)) return true;
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
                    if (!_tracking && !KeepOpen) SetCollapsed(true);
                    return true;
                case WM_DISPLAYCHANGE when kind == FluentKind.Overlay:
                    ui.OverlaysNeedUpdate();
                    return false;
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
                    if (_hover >= 0 || _pressed >= 0 || _hoverRow >= 0) { _hover = _pressed = _hoverRow = -1; Invalidate(); }
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
                    if (kind == FluentKind.Dialog) Cancelled?.Invoke(); // the banner can only be ended with "Disconnect", an overlay by the admin
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
                        else if (kind == FluentKind.Overlay) PaintOverlay(mem, g, w, h);
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
            if (Items is not null)
            {
                y += S(16);
                y += PaintList(dc, g, S(Pad), y, inner);
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
            // Red "live" dot (accent while the screen is hidden from the admin), the message and the app name (collapsed: only who).
            var dot = S(12);
            Ellipse(g, S(16), (h - dot) / 2, dot, dot, Accent ? Theme.Accent : Theme.Danger);
            var x = S(16) + dot + S(12);
            var shown = BannerButtons();
            // The text ends before the buttons: on a narrow screen it is cut with "…", the subtitle goes first.
            var room = w - S(7) - shown.Sum(b => ButtonWidth(b) + S(8)) - S(12) - x;
            if (_collapsed)
                Text(dc, _small, ShortHeading, x, 0, Math.Min(room, TextWidth(_small, ShortHeading) + S(4)), h, theme.Text, vcenter: true);
            else
            {
                var tw = Math.Min(room, TextWidth(_bold, Heading) + S(4));
                Text(dc, _bold, Heading, x, 0, tw, h, theme.Text, vcenter: true);
                var sw = TextWidth(_small, Subtitle) + S(4);
                if (room - tw - S(10) >= sw) Text(dc, _small, Subtitle, x + tw + S(10), 0, sw, h, theme.Secondary, vcenter: true);
            }
            _buttonRects.Clear();
            var bx = w - S(7);
            for (var i = shown.Length - 1; i >= 0; i--)
            {
                var bw = ButtonWidth(shown[i]);
                bx -= bw;
                DrawButton(dc, g, shown[i], bx, S(6), bw, h - S(12), _collapsed ? _small : _bold);
                bx -= S(8);
            }
        }

        /// <summary>The picker's rows (box, icon, name, how many windows) in a framed list; returns its height.</summary>
        private int PaintList(IntPtr dc, nint g, int x, int y, int w)
        {
            var items = Items!;
            var visible = Math.Clamp(items.Count, 1, MaxRows);
            var h = S(RowH) * visible;
            _listRect = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
            RoundRect(g, x, y, w, h, S(6), theme.CodeBox, theme.Stroke);
            if (items.Count == 0)
            {
                Text(dc, _font, "No apps with open windows", x, y, w, h, theme.Secondary, center: true);
                return h;
            }
            var scrollbar = items.Count > MaxRows;
            for (var row = 0; row < visible && _scroll + row < items.Count; row++)
            {
                var i = _scroll + row;
                var item = items[i];
                var ry = y + row * S(RowH);
                var rw = w - (scrollbar ? ScrollbarW : 0);
                // Hover and focus inset and rounded, inside the list's own rounded frame.
                if (i == _hoverRow && !item.Locked) RoundRect(g, x + S(3), ry + S(2), rw - S(6), S(RowH) - S(4), S(4), theme.ButtonFill, 0);
                if (i == _focusRow && _focusButton < 0) RoundRect(g, x + S(3), ry + S(2), rw - S(6), S(RowH) - S(4), S(4), 0, Theme.Accent);
                // The box: accent and a tick when chosen, dimmer when the policy decides it.
                int box = S(18), bx = x + S(12), by = ry + (S(RowH) - box) / 2;
                var on = item.Locked ? theme.Secondary : Theme.Accent;
                RoundRect(g, bx, by, box, box, S(4), item.Checked ? on : theme.Window, item.Checked ? on : theme.Secondary);
                if (item.Checked)
                {
                    GdipCreatePen1(0xFFFFFFFF, Math.Max(1.5f, _s * 2), 2, out var tick);
                    GdipDrawLine(g, tick, bx + box * 0.24f, by + box * 0.52f, bx + box * 0.42f, by + box * 0.70f);
                    GdipDrawLine(g, tick, bx + box * 0.42f, by + box * 0.70f, bx + box * 0.78f, by + box * 0.30f);
                    GdipDeletePen(tick);
                }
                var tx = bx + box + S(12);
                if (item.Icon != IntPtr.Zero)
                {
                    GdipFlush(g, 1);
                    DrawIconEx(dc, tx, ry + (S(RowH) - S(20)) / 2, item.Icon, S(20), S(20), 0, IntPtr.Zero, 3 /* DI_NORMAL */);
                }
                tx += S(20) + S(10);
                var dw = TextWidth(_small, item.Detail) + S(4);
                Text(dc, _font, item.Name, tx, ry, Math.Max(0, x + rw - S(12) - dw - S(12) - tx), S(RowH), item.Locked ? theme.Secondary : theme.Text, vcenter: true);
                Text(dc, _small, item.Detail, x + rw - S(12) - dw, ry, dw, S(RowH), theme.Secondary, vcenter: true);
            }
            if (scrollbar)
            {
                var (top, thumb) = Thumb();
                RoundRect(g, x + w - S(7), top, S(4), thumb, S(2), theme.Secondary, 0);
            }
            return h;
        }

        private const int ScrollbarW = 14;
        private int _focusButton = -1, _pressedRow = -1, _dragFrom = -1, _dragScroll;

        /// <summary>The scrollbar thumb: top and height in client pixels.</summary>
        private (int Top, int Height) Thumb()
        {
            var count = Items!.Count;
            var track = (_listRect.Bottom - _listRect.Top) - S(8);
            var thumb = Math.Max(S(24), track * MaxRows / count);
            return (_listRect.Top + S(4) + (track - thumb) * _scroll / Math.Max(1, count - MaxRows), thumb);
        }

        private bool OnScrollbar(int x, int y) => Items!.Count > MaxRows && x >= _listRect.Right - S(ScrollbarW) && x < _listRect.Right && y >= _listRect.Top && y < _listRect.Bottom;

        private void ScrollBy(int rows)
        {
            _scroll = Math.Clamp(_scroll + rows, 0, Math.Max(0, Items!.Count - MaxRows));
            Invalidate();
        }

        /// <summary>
        /// The list's mouse and keys. A row is ticked when the button is let go on it (like the buttons); the wheel, Up/Down
        /// and the scrollbar scroll; Tab moves between the list and the buttons, Space or Enter use the one with the focus.
        /// True when handled.
        /// </summary>
        private bool ProcessList(uint msg, nint wParam, nint lParam, ref nint result)
        {
            int x = (short)(lParam & 0xFFFF), y = (short)((lParam >> 16) & 0xFFFF);
            var items = Items!;
            switch (msg)
            {
                case WM_MOUSEMOVE when _dragFrom >= 0:
                    var (_, thumb) = Thumb();
                    var track = (_listRect.Bottom - _listRect.Top) - S(8) - thumb;
                    _scroll = Math.Clamp(_dragScroll + (int)Math.Round((y - _dragFrom) * (double)(items.Count - MaxRows) / Math.Max(1, track)), 0, items.Count - MaxRows);
                    Invalidate();
                    return true;
                case WM_MOUSEMOVE:
                    var hover = OnScrollbar(x, y) ? -1 : HitRow(x, y);
                    if (hover != _hoverRow) { _hoverRow = hover; Invalidate(); }
                    return false; // the buttons still get their hover
                case WM_SETCURSOR when _hoverRow >= 0 && !items[_hoverRow].Locked:
                    SetCursor(LoadCursorW(IntPtr.Zero, 32649)); // IDC_HAND
                    result = 1;
                    return true;
                case WM_LBUTTONDOWN when OnScrollbar(x, y):
                    var (top, height) = Thumb();
                    if (y >= top && y < top + height) { _dragFrom = y; _dragScroll = _scroll; SetCapture(Handle); }
                    else ScrollBy(y < top ? -MaxRows : MaxRows); // the track: a page
                    return true;
                case WM_LBUTTONDOWN when HitRow(x, y) is var row and >= 0:
                    _pressedRow = row;
                    SetCapture(Handle);
                    return true;
                case WM_LBUTTONUP when _dragFrom >= 0:
                    _dragFrom = -1;
                    ReleaseCapture();
                    return true;
                case WM_LBUTTONUP when _pressedRow >= 0:
                    var pressed = _pressedRow;
                    _pressedRow = -1;
                    ReleaseCapture();
                    if (HitRow(x, y) == pressed && !OnScrollbar(x, y))
                    {
                        (_focusRow, _focusButton) = (pressed, -1);
                        Toggle(pressed);
                    }
                    return true;
                case WM_MOUSEWHEEL:
                    ScrollBy(-Math.Sign((short)((wParam >> 16) & 0xFFFF)));
                    _hoverRow = -1;
                    return true;
                case WM_KEYDOWN when wParam == 0x09: // Tab: list → buttons (left to right) → list
                    var forward = (GetKeyState(0x10) & 0x8000) == 0;
                    var order = new List<int> { -1 };
                    order.AddRange(Buttons.Select(b => b.Id));
                    var at = order.IndexOf(_focusButton);
                    _focusButton = order[((at < 0 ? 0 : at) + (forward ? 1 : order.Count - 1)) % order.Count];
                    if (_focusButton < 0 && _focusRow < 0 && items.Count > 0) _focusRow = 0;
                    Invalidate();
                    return true;
                case WM_KEYDOWN when wParam is 0x20 or 0x0D && _focusButton >= 0: // Space, Enter on a button with the focus (chosen on purpose)
                    Clicked?.Invoke(_focusButton);
                    return true;
                case WM_KEYDOWN when wParam is 0x26 or 0x28 && items.Count > 0 && _focusButton < 0: // Up, Down
                    _focusRow = Math.Clamp(_focusRow + (wParam == 0x28 ? 1 : -1), 0, items.Count - 1);
                    ScrollTo(_focusRow);
                    Invalidate();
                    return true;
                case WM_KEYDOWN when wParam == 0x20 && _focusRow >= 0 && _focusButton < 0: // Space
                    Toggle(_focusRow);
                    return true;
            }
            return false;
        }

        /// <summary>The list row under this point (-1: none; the scrollbar is not a row).</summary>
        private int HitRow(int x, int y)
        {
            if (Items is not { Count: > 0 } items || x < _listRect.Left || x >= _listRect.Right || y < _listRect.Top || y >= _listRect.Bottom || OnScrollbar(x, y)) return -1;
            var i = _scroll + (y - _listRect.Top) / S(RowH);
            return i < items.Count ? i : -1;
        }

        private void Toggle(int i)
        {
            if (Items is not { } items || i < 0 || i >= items.Count || items[i].Locked) return;
            items[i].Checked = !items[i].Checked;
            Invalidate();
        }

        private void ScrollTo(int i)
        {
            if (Items is not { } items) return;
            if (i < _scroll) _scroll = i;
            else if (i >= _scroll + MaxRows) _scroll = i - MaxRows + 1;
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, items.Count - MaxRows));
        }

        /// <summary>The privacy screen: black, or the console's dark background with what is going on, centred.</summary>
        private void PaintOverlay(IntPtr dc, nint g, int w, int h)
        {
            if (Black)
            {
                FillRect(g, 0, 0, w, h, OverlayBlack);
                return;
            }
            FillRect(g, 0, 0, w, h, theme.Window);
            var inner = Math.Min(w - S(2 * Pad), S(560));
            var x = (w - inner) / 2;
            var height = S(48) + S(20) + TextHeight(_heading, Heading, inner) + S(8) + TextHeight(_font, Body, inner)
                + (Footnote.Length > 0 ? S(14) + TextHeight(_small, Footnote, inner) : 0);
            var y = (h - height) / 2;
            DrawMark(dc, g, (w - S(48)) / 2, y, S(48));
            y += S(48) + S(20);
            y += TextBlock(dc, _heading, Heading, x, y, inner, theme.Text, center: true) + S(8);
            y += TextBlock(dc, _font, Body, x, y, inner, theme.Secondary, center: true);
            if (Footnote.Length > 0) TextBlock(dc, _small, Footnote, x, y + S(14), inner, theme.Secondary, center: true);
        }

        private const uint OverlayBlack = 0xFF000000;

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
            if (_focusButton == b.Id) RoundRect(g, x - S(3), y - S(3), w + S(6), h + S(6), S(6), 0, theme.Text); // keyboard focus (Tab)
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
        private static int TextBlock(IntPtr dc, IntPtr font, string text, int x, int y, int w, uint argb, bool center = false)
        {
            if (text.Length == 0) return 0;
            var old = SelectObject(dc, font);
            SetBkMode(dc, 1);
            SetTextColor(dc, (argb & 0xFF) << 16 | (argb & 0xFF00) | ((argb >> 16) & 0xFF));
            var r = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + 2000 };
            var height = DrawTextW(dc, text, text.Length, ref r, 0x0010 | (center ? 0x1u : 0u)); // WORDBREAK (| CENTER)
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
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(IntPtr hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, nint name);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
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
    [DllImport("gdiplus.dll")] private static extern int GdipDrawRectangleI(nint graphics, nint pen, int x, int y, int w, int h);
    [DllImport("gdiplus.dll")] private static extern int GdipCreatePath(int fillMode, out nint path);
    [DllImport("gdiplus.dll")] private static extern int GdipDeletePath(nint path);
    [DllImport("gdiplus.dll")] private static extern int GdipAddPathArc(nint path, float x, float y, float w, float h, float start, float sweep);
    [DllImport("gdiplus.dll")] private static extern int GdipAddPathRectangle(nint path, float x, float y, float w, float h);
    [DllImport("gdiplus.dll")] private static extern int GdipClosePathFigure(nint path);
    [DllImport("gdiplus.dll")] private static extern int GdipFillPath(nint graphics, nint brush, nint path);
    [DllImport("gdiplus.dll")] private static extern int GdipDrawPath(nint graphics, nint pen, nint path);
}
