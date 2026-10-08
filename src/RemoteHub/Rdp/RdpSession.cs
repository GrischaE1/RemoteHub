using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteHub.Models;

namespace RemoteHub.Rdp;

public enum SessionState { Idle, Connecting, Connected, Disconnected, Failed }

/// <summary>One live RDP session (one ActiveX control instance).</summary>
public sealed partial class RdpSession : SessionBase
{
    private readonly RdpClientHost _host = new() { Dock = System.Windows.Forms.DockStyle.Fill };
    private readonly DispatcherTimer _resizeTimer;
    private readonly DispatcherTimer _focusTimer;
    /// <summary>Re-sends the window size a few times after login (the server ignores it until its display channel is ready).</summary>
    private readonly DispatcherTimer _fitTimer;
    private int _fitAttempt;
    private static readonly int[] FitDelaysMs = [300, 1000, 2500, 5000, 9000];
    /// <summary>Desktop size last reported by the server (0 = unknown).</summary>
    private int _remoteW, _remoteH;
    /// <summary>Connect attempts deferred because the session area had no real size yet.</summary>
    private int _sizeWaits;
    private bool _loggedIn, _closeRequested, _connectPending, _disposed;
    private RdpOptions _opt = new();
    private CredentialSet _cred = new();

    public RdpSession(Connection connection) : base(connection)
    {
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); ApplyDynamicResolution(); };
        _focusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _focusTimer.Tick += (_, _) => HasKeyboardFocus = ControlHasFocus();
        _focusTimer.Start();
        _fitTimer = new DispatcherTimer();
        _fitTimer.Tick += (_, _) =>
        {
            _fitTimer.Stop();
            if (Fits()) return; // the server already uses the window size
            ApplyDynamicResolution();
            if (++_fitAttempt < FitDelaysMs.Length) { _fitTimer.Interval = TimeSpan.FromMilliseconds(FitDelaysMs[_fitAttempt]); _fitTimer.Start(); }
        };

        // Defer until layout has run so the initial desktop size matches the visible area.
        _host.ControlCreated += () => Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            () => { if (_connectPending && !_disposed) ConnectNow(); });
        _host.Resize += (_, _) => { if (_opt.Resize == DisplayResize.DynamicResolution) { _resizeTimer.Stop(); _resizeTimer.Start(); } };
        _host.Connecting += () => SetState(SessionState.Connecting, "Connecting…");
        _host.Connected += () => SetState(SessionState.Connecting, "Authenticating…");
        _host.LoginComplete += () => { _loggedIn = true; SetState(SessionState.Connected, "Connected"); ApplyDynamicResolution(); StartFitRetries(); LoggedIn?.Invoke(); };
        _host.Disconnected += OnDisconnected;
        _host.FatalError += code => SetState(SessionState.Failed, $"Fatal RDP client error ({code}).");
        _host.LogonError += code => Debug.WriteLine($"RDP logon error {code}");
        _host.AutoReconnecting += (n, max) => SetState(SessionState.Connecting, $"Connection lost – reconnecting ({n}/{max})…");
        _host.AutoReconnected += () => { SetState(SessionState.Connected, "Connected"); StartFitRetries(); };
        _host.EnterFullScreen += () => IsFullScreen = true;
        _host.LeaveFullScreen += () => IsFullScreen = false;
        _host.RequestMinimize += () => MinimizeRequested?.Invoke();
        _host.FocusReleased += dir => FocusReleased?.Invoke(dir);
        _host.RemoteDesktopSizeChanged += (w, h) =>
        {
            _remoteW = w;
            _remoteH = h;
            Services.DiagLog.Write($"Session {GetHashCode():x8}: remote desktop {w}x{h}, window {_host.ClientSize.Width}x{_host.ClientSize.Height}");
            // The server settled on a size that is not the window size (e.g. the first request was too early): ask again.
            if (_loggedIn && !Fits() && !_fitTimer.IsEnabled) StartFitRetries();
        };
    }

    internal System.Windows.Forms.Control Control => _host;

    [ObservableProperty] private bool _isFullScreen;

    public override string KindSymbol => "Desktop24";
    public override string KindLabel => "Remote desktop";

    public event Action? MinimizeRequested;
    public event Action? LoggedIn;

    public DateTime ConnectStartedUtc { get; private set; }
    /// <summary>Effective "when Windows asks for the password" setting for the current/last connect.</summary>
    public PasswordPromptAction PasswordPrompt => _opt.PasswordPrompt == PasswordPromptAction.Off ? PasswordPromptAction.Off : PasswordPromptAction.Clipboard;
    /// <summary>Credentials used for the current/last connect (kept in memory only).</summary>
    internal CredentialSet UsedCredentials => _cred;
    /// <summary>User pressed the session-switch hotkey inside the session (argument = direction reported by the control).</summary>
    public event Action<int>? FocusReleased;

    /// <summary>Starts (or restarts) the connection using freshly resolved settings.</summary>
    public void Connect(RdpOptions options, CredentialSet credentials, string credentialSource = "")
    {
        _opt = options;
        _cred = credentials;
        CredentialInfo = Describe(credentials, credentialSource);
        AccentBrush = Views.ColorTags.Brush(options.ColorTag) ?? Views.ColorTags.Frozen(Views.AppChrome.Accent);
        ConnectStartedUtc = DateTime.UtcNow;
        _remoteW = _remoteH = 0;
        Title = Connection.Name;
        _connectPending = true;
        SetState(SessionState.Connecting, "Connecting…");
        if (_host.IsCreated && _host.IsHandleCreated) ConnectNow();
    }

    private void ConnectNow()
    {
        // The first connect of a new tab can come before WPF has given the control its real size. Wait for it
        // (up to ~1.2 s) so the session starts with the window size instead of a small default desktop.
        var cs = _host.ClientSize;
        if ((cs.Width < 400 || cs.Height < 300) && _sizeWaits < 12 && !_disposed)
        {
            _sizeWaits++;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            t.Tick += (_, _) => { t.Stop(); if (_connectPending && !_disposed) ConnectNow(); };
            _connectPending = true;
            t.Start();
            return;
        }
        _sizeWaits = 0;
        _connectPending = false;
        _loggedIn = false;
        try
        {
            var c = _host.Client;
            if ((int)c.Connected != 0) { _connectPending = true; c.Disconnect(); return; }
            Configure(c);
            c.Connect();
        }
        catch (Exception ex)
        {
            SetState(SessionState.Failed, "Could not start the connection: " + ex.Message);
        }
    }

    private void Configure(dynamic c)
    {
        var o = _opt;
        c.Server = Connection.Host.Trim();
        c.FullScreenTitle = Connection.Name;
        c.ConnectingText = "Connecting…";
        c.UserName = _cred.Username ?? "";
        c.Domain = _cred.Domain ?? "";
        if (!string.IsNullOrEmpty(_cred.Password)) _host.NonScriptable.SetClearTextPassword(_cred.Password);
        else Try(() => _host.NonScriptable.ResetPassword());

        var (w, h) = InitialSize();
        c.DesktopWidth = w;
        c.DesktopHeight = h;
        c.ColorDepth = o.ColorDepth ?? 32;

        var a = c.AdvancedSettings9;
        a.RDPPort = Connection.Port;
        Try(() => a.EnableCredSspSupport = o.EnableNla ?? true);
        Try(() => a.AuthenticationLevel = (uint)(o.ServerAuthentication ?? ServerAuth.Warn));
        Try(() => a.RedirectClipboard = o.RedirectClipboard ?? true);
        Try(() => a.RedirectDrives = o.RedirectDrives ?? false);
        Try(() => a.RedirectPrinters = o.RedirectPrinters ?? false);
        Try(() => a.RedirectSmartCards = o.RedirectSmartCards ?? false);
        Try(() => a.SmartSizing = o.Resize == DisplayResize.SmartSizing);
        Try(() => a.EnableAutoReconnect = o.AutoReconnect ?? true);
        Try(() => a.MaxReconnectAttempts = 20);
        Try(() => a.ConnectToAdministerServer = o.AdminSession ?? false);
        Try(() => a.EnableWindowsKey = (o.ForwardWindowsKeys ?? true) ? 1 : 0);
        Try(() => a.allowBackgroundInput = 1);
        Try(() => a.ContainerHandledFullScreen = 0);
        ApplySwitchHotkeys(a);
        Try(() => a.DisplayConnectionBar = true);
        Try(() => a.PinConnectionBar = false);
        Try(() => a.ConnectionBarShowMinimizeButton = true);
        Try(() => a.ConnectionBarShowRestoreButton = true);
        Try(() => a.BitmapPersistence = 1);
        Try(() => a.Compress = 1);
        Try(() => a.GrabFocusOnConnect = true);
        Try(() => a.BandwidthDetection = true);
        Try(() => a.NetworkConnectionType = 7); // auto-detect
        Try(() => a.PerformanceFlags = PerformanceFlags(o.Quality ?? VisualQuality.Balanced));

        var s = c.SecuredSettings2;
        // 1 = Windows key combinations are sent to the remote session while the session has focus.
        Try(() => s.KeyboardHookMode = (o.ForwardWindowsKeys ?? true) ? 1 : 0);
        Try(() => s.AudioRedirectionMode = (int)(o.Audio ?? AudioMode.PlayLocal));

        Try(() => VTable.PutBool((object)c, VTable.IID_NonScriptable5, VTable.UseMultimonPut, o.UseMultimon ?? false));

        if (_host.ExtendedSettings is { } ext)
        {
            var scale = DesktopScale();
            Try(() => { object v = (uint)scale; ext.SetProperty("DesktopScaleFactor", ref v); });
            Try(() => { object v = (uint)100; ext.SetProperty("DeviceScaleFactor", ref v); });
        }
    }

    private static string Describe(CredentialSet c, string source)
    {
        if (c.IsEmpty) return $"Credentials: {source}";
        var user = c.Username!;
        var format = c.IsLocalAccount ? ".\\user (local account on the target)"
                   : user.Contains('@') ? "user@domain"
                   : !string.IsNullOrEmpty(c.Domain) ? "DOMAIN\\user"
                   : user.Contains('\\') ? "DOMAIN\\user (in username)"
                   : "user only (no domain)";
        var pw = string.IsNullOrEmpty(c.Password) ? "no password stored – Windows will ask"
               : c.Password != c.Password.Trim() ? "password sent – WARNING: it starts or ends with a space"
               : "password sent";
        return $"Credentials from {source} · {format} · {pw}";
    }

    private static int PerformanceFlags(VisualQuality q) => q switch
    {
        // TS_PERF_* flags
        VisualQuality.Best => 0x80 | 0x100,                         // font smoothing + desktop composition
        VisualQuality.Balanced => 0x01 | 0x04 | 0x80 | 0x100,       // no wallpaper / menu animations
        _ => 0x01 | 0x02 | 0x04 | 0x08 | 0x20 | 0x40,               // everything off
    };

    private (int W, int H) InitialSize()
    {
        if (_opt.Resize == DisplayResize.Fixed && TryParseResolution(_opt.FixedResolution, out var fw, out var fh))
            return (fw, fh);
        var size = _host.ClientSize;
        int w = Math.Clamp(size.Width, 640, 8192) & ~1;
        int h = Math.Clamp(size.Height, 480, 8192) & ~1;
        // Not laid out yet (e.g. still the WinForms default of 300x300): start with the screen size instead of a tiny
        // desktop – with dynamic resolution it is fitted to the window right after login.
        if (size.Width < 400 || size.Height < 300)
        {
            var screen = System.Windows.Forms.Screen.FromControl(_host).WorkingArea;
            (w, h) = (screen.Width & ~1, screen.Height & ~1);
        }
        return (w, h);
    }

    public static bool TryParseResolution(string? text, out int w, out int h)
    {
        w = h = 0;
        var parts = (text ?? "").ToLowerInvariant().Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h) && w >= 200 && h >= 200;
    }

    private int DesktopScale()
    {
        var dpi = _host.IsHandleCreated ? _host.DeviceDpi : 96;
        return Math.Clamp((int)Math.Round(dpi * 100.0 / 96 / 25) * 25, 100, 500);
    }

    /// <summary>True when the server's desktop matches the window (within a few pixels) or fitting does not apply.</summary>
    private bool Fits()
    {
        if (_opt.Resize != DisplayResize.DynamicResolution || IsFullScreen) return true;
        if (_remoteW == 0 || _remoteH == 0) return false;
        var s = _host.ClientSize;
        return Math.Abs(_remoteW - s.Width) <= 4 && Math.Abs(_remoteH - s.Height) <= 4;
    }

    private void StartFitRetries()
    {
        _fitAttempt = 0;
        _fitTimer.Stop();
        _fitTimer.Interval = TimeSpan.FromMilliseconds(FitDelaysMs[0]);
        _fitTimer.Start();
    }

    private void ApplyDynamicResolution()
    {
        if (!_loggedIn || State != SessionState.Connected || _opt.Resize != DisplayResize.DynamicResolution || IsFullScreen) return;
        var size = _host.ClientSize;
        if (size.Width < 200 || size.Height < 200) return;
        uint w = (uint)(Math.Min(size.Width, 8192) & ~1), h = (uint)Math.Min(size.Height, 8192);
        var scale = (uint)DesktopScale();
        Try(() => _host.Client.UpdateSessionDisplaySettings(w, h, w, h, 0u, scale, 100u));
    }

    private void OnDisconnected(int reason)
    {
        Services.DiagLog.Write($"Session {GetHashCode():x8}: disconnected, reason {reason}");
        _loggedIn = false;
        IsFullScreen = false;
        if (_closeRequested) { FinishClose(); return; }
        if (_connectPending) { ConnectNow(); return; } // reconnect requested while connected

        string text;
        if (reason is 1 or 2 or 3) text = "Disconnected.";
        else
        {
            text = $"Disconnected (code {reason}).";
            Try(() =>
            {
                var c = _host.Client;
                string d = c.GetErrorDescription((uint)reason, (uint)c.ExtendedDisconnectReason);
                if (!string.IsNullOrWhiteSpace(d)) text = d;
            });
        }
        SetState(reason is 1 or 2 or 3 ? SessionState.Disconnected : SessionState.Failed, text);
    }

    public override void Disconnect()
    {
        _connectPending = false;
        Try(() => { if ((int)_host.Client.Connected != 0) _host.Client.Disconnect(); });
    }

    public void SendCtrlAltDel()
    {
        if (!IsConnected) return;
        _host.Focus();
        // Scan codes: LCtrl 0x1D, LAlt 0x38, Del 0x53 – press, then release in reverse order.
        int[] keys = [0x1D, 0x38, 0x53, 0x53, 0x38, 0x1D];
        short[] up = [0, 0, 0, -1, -1, -1];
        Try(() => _host.NonScriptable.SendKeys(keys.Length, up, keys));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    /// <summary>
    /// Re-sends Ctrl+Alt+&lt;key&gt; to the server. Used when the user pressed AltGr+key (AltGr = Ctrl+Alt) to type a
    /// character such as "|" but the RDP control treated it as the focus-release hotkey.
    /// </summary>
    public void SendCtrlAltKey(int virtualKey)
    {
        if (!IsConnected) return;
        int sc = (int)MapVirtualKey((uint)virtualKey, 0 /* MAPVK_VK_TO_VSC */);
        if (sc == 0) return;
        _host.Focus();
        int[] keys = [0x1D, 0x38, sc, sc, 0x38, 0x1D];
        short[] up = [0, 0, 0, -1, -1, -1];
        Try(() => _host.NonScriptable.SendKeys(keys.Length, up, keys));
    }

    public void EnterFullScreen()
    {
        if (!IsConnected) return;
        Try(() => _host.Client.FullScreen = true);
    }

    /// <summary>
    /// Ctrl+Alt+&lt;key&gt; makes the RDP control release keyboard focus back to the app (raising OnFocusReleased).
    /// </summary>
    private static void ApplySwitchHotkeys(dynamic advancedSettings)
    {
        var keys = Services.SwitchHotkeys.Current;
        Try(() => advancedSettings.HotKeyFocusReleaseLeft = keys.PrevVk);
        Try(() => advancedSettings.HotKeyFocusReleaseRight = keys.NextVk);
    }

    /// <summary>Re-applies the switch hotkeys to an existing control (takes effect immediately or on reconnect).</summary>
    public void RefreshSwitchHotkeys()
    {
        if (_host.IsCreated) Try(() => ApplySwitchHotkeys(_host.Client.AdvancedSettings9));
    }

    public void LeaveFullScreen()
    {
        if (IsFullScreen) Try(() => _host.Client.FullScreen = false);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    private bool ControlHasFocus()
    {
        if (_disposed || !_host.IsHandleCreated || IsFullScreen) return IsFullScreen && IsConnected;
        if (State is not (SessionState.Connected or SessionState.Connecting)) return false; // overlay shown, nothing to type into
        var focus = GetFocus(); // focus window of this (UI) thread
        if (focus == IntPtr.Zero || (focus != _host.Handle && !IsChild(_host.Handle, focus))) return false;
        return GetAncestor(_host.Handle, 2 /* GA_ROOT */) == GetForegroundWindow();
    }

    public override void FocusSession()
    {
        if (_host.IsHandleCreated) _host.Focus();
    }

    /// <summary>Disconnects (if needed) and releases the control. Raises <see cref="Closed"/>.</summary>
    public override void Close()
    {
        if (_closeRequested) return;
        _closeRequested = true;
        bool connected = false;
        Try(() => connected = _host.IsCreated && (int)_host.Client.Connected != 0);
        if (connected)
        {
            Try(() => _host.Client.Disconnect());
            // Safety net in case the control never raises OnDisconnected.
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            t.Tick += (_, _) => { t.Stop(); FinishClose(); };
            t.Start();
        }
        else FinishClose();
    }

    private void FinishClose()
    {
        if (_disposed) return;
        Dispose();
        RaiseClosed();
    }

    private void SetState(SessionState state, string text)
    {
        if (state != State) Services.DiagLog.Write($"Session {GetHashCode():x8}: {State} -> {state}");
        State = state;
        StatusText = text;
    }

    private static void Try(Action a)
    {
        try { a(); } catch (Exception ex) { Debug.WriteLine("RDP: " + ex.Message); }
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _resizeTimer.Stop();
        _focusTimer.Stop();
        _fitTimer.Stop();
        Try(() => _host.Dispose());
    }
}
