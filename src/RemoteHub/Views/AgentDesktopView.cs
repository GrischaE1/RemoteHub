using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using RemoteHub.Agent;
using RemoteHub.Rdp;
using RemoteHub.Remoting;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>
/// Tab with the screen of a device through the RemoteHub Agent: header with monitor, picture and Ctrl+Alt+Del; the
/// picture scaled to the tab; panels for choosing the session, waiting for the user and entering the code.
/// Mouse positions are taken in the picture's own pixels (the Viewbox scales), so they match the remote screen.
/// </summary>
public sealed class AgentDesktopView : Border, IRecordableView
{
    public FrameworkElement RecordingArea { get; }

    private readonly AgentDesktopSession _session;
    private readonly Func<DesktopQualityPreset> _defaultQuality;
    private readonly Action<DesktopQualityPreset, string> _remember;
    private readonly Border _stripe = new() { Width = 4, Height = 18, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0) };
    private readonly Grid _picture = new() { ClipToBounds = true };
    private readonly System.Windows.Controls.Image _screen = new() { Stretch = Stretch.Fill };
    private readonly System.Windows.Controls.Image _cursorOverlay = new() { IsHitTestVisible = false };
    private readonly Canvas _cursorLayer = new() { IsHitTestVisible = false };
    private readonly Viewbox _viewbox = new() { Stretch = Stretch.Uniform };
    private readonly Grid _content = new() { Focusable = true, FocusVisualStyle = null, Background = Brushes.Black };
    private readonly ComboBox _monitors = new() { MinWidth = 150, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Monitor of the device" };
    private readonly ComboBox _quality = new() { MinWidth = 210, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Frames per second and picture quality" };
    private readonly TextBlock _viewOnlyBadge = new() { Text = "View only", FontSize = 12, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _cad;
    private readonly Button _winKey;
    private readonly Button _fit;
    private readonly Button _infoButton;
    private readonly Button _keysButton;
    private readonly System.Windows.Threading.DispatcherTimer _resizeDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly Border _choosePanel = new(), _waitPanel = new(), _pinPanel = new(), _endPanel = new(), _securePanel = new(), _noticePanel = new(),
        _infoPanel = new(), _reconnectPanel = new(), _pausedPanel = new();
    private readonly TextBlock _selfViewWarning = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private bool _showInfo, _keyCombinations = true;
    private Action<bool, bool> _savePreferences = (_, _) => { };
    private readonly StackPanel _sessionList = new();
    private readonly ComboBox _consent = new() { MinWidth = 300 };
    private readonly CheckBox _viewOnly = new() { Content = "View only – no mouse and keyboard" };
    private readonly ComboBox _chooseQuality = new() { MinWidth = 300 };
    private readonly TextBox _pin = new() { PlaceholderText = "6-digit code", MaxLength = 7, Width = 180, FontSize = 18 };
    private readonly TextBlock _pinInfo = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Margin = new Thickness(0, 6, 0, 0) };
    private bool _updatingMonitors;
    private Point _lastMouse = new(-1, -1);

    private readonly Action<bool> _rememberFit;

    public AgentDesktopView(AgentDesktopSession session, Action reconnect, Action close, Action useRdp,
        Func<DesktopQualityPreset> defaultQuality, Action<DesktopQualityPreset, string> remember, Func<bool> fitToWindow, Action<bool> rememberFit)
    {
        _rememberFit = rememberFit;
        _session = session;
        _defaultQuality = defaultQuality;
        _remember = remember;
        DataContext = session;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(2);
        SetResourceReference(BackgroundProperty, "SessionAreaBrush");
        SetResourceReference(BorderBrushProperty, "ChromeStrokeBrush");

        _cad = RemoteToolChrome.Tool(SymbolRegular.Keyboard24, "Send Ctrl+Alt+Del", session.SendCtrlAltDel);
        _winKey = RemoteToolChrome.Tool(SymbolRegular.Grid24, "Press the Windows key (Start menu)", PressWindowsKey);
        _fit = RemoteToolChrome.Tool(SymbolRegular.ScaleFit24, "", () => session.FitToWindow = !session.FitToWindow);
        _fit.Margin = new Thickness(0, 0, 4, 0);
        _infoButton = RemoteToolChrome.Tool(SymbolRegular.DataUsage24, "", () => { _showInfo = !_showInfo; _savePreferences(_showInfo, _keyCombinations); Refresh(); });
        _infoButton.Margin = new Thickness(0, 0, 4, 0);
        _keysButton = RemoteToolChrome.Tool(SymbolRegular.KeyCommand24, "", () => { _keyCombinations = !_keyCombinations; _savePreferences(_showInfo, _keyCombinations); UpdateKeyHook(); Refresh(); });
        _keysButton.Margin = new Thickness(0, 0, 4, 0);
        session.FitToWindow = fitToWindow();
        _resizeDebounce.Tick += (_, _) => { _resizeDebounce.Stop(); ReportSize(); };
        _content.SizeChanged += (_, _) => { _resizeDebounce.Stop(); _resizeDebounce.Start(); };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Child = root;
        root.Children.Add(Header());

        Grid.SetRow(_content, 1);
        root.Children.Add(_content);
        RecordingArea = _content;

        // Picture + pointer, in the remote screen's pixels, scaled to fit.
        _picture.Children.Add(_screen);
        _cursorLayer.Children.Add(_cursorOverlay);
        _picture.Children.Add(_cursorLayer);
        _viewbox.Child = _picture;
        _content.Children.Add(_viewbox);
        _screen.SetBinding(System.Windows.Controls.Image.SourceProperty, nameof(AgentDesktopSession.Screen));
        _cursorOverlay.SetBinding(System.Windows.Controls.Image.SourceProperty, nameof(AgentDesktopSession.CursorImage));

        BuildChoosePanel(close, useRdp);
        BuildWaitPanel();
        BuildPinPanel();
        BuildEndPanel(reconnect, close, useRdp);
        BuildSecurePanel();
        BuildNoticePanel();
        BuildInfoPanel();
        BuildReconnectPanel();
        BuildPausedPanel();
        foreach (var p in new[] { _securePanel, _pausedPanel, _noticePanel, _infoPanel, _reconnectPanel, _choosePanel, _waitPanel, _pinPanel, _endPanel }) _content.Children.Add(p);

        _picture.MouseMove += OnMouseMove;
        _picture.MouseDown += OnMouseDown;
        _picture.MouseUp += OnMouseUp;
        _picture.MouseWheel += OnMouseWheel;
        _picture.MouseLeave += (_, _) => UpdateCursor();
        _picture.MouseEnter += (_, _) => UpdateCursor();
        _content.PreviewKeyDown += (_, e) => OnKey(e, up: false);
        _content.PreviewKeyUp += (_, e) => OnKey(e, up: true);
        _content.GotKeyboardFocus += (_, _) => { session.HasKeyboardFocus = true; session.SyncClipboardToRemote(); UpdateFrame(); UpdateKeyHook(); };
        _content.LostKeyboardFocus += (_, _) => { session.HasKeyboardFocus = false; ReleaseKeys(); UpdateFrame(); UpdateKeyHook(); };
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is not { } w) return;
            w.Activated += (_, _) => UpdateKeyHook();
            w.Deactivated += (_, _) => { ReleaseKeys(); UpdateKeyHook(); };
        };
        Unloaded += (_, _) => { ReleaseKeys(); RemoveKeyHook(); };
        session.FocusRequested += () => { if (session.Phase == DesktopPhase.Live) _content.Focus(); };
        session.PropertyChanged += OnSessionChanged;
        foreach (var q in DesktopQualityPreset.All) _quality.Items.Add(new ComboBoxItem { Content = q.Label, Tag = q });
        _quality.SelectionChanged += (_, _) =>
        {
            if (_quality.SelectedItem is ComboBoxItem { Tag: DesktopQualityPreset q } && q != session.Quality) session.SetQuality(q);
        };
        _monitors.SelectionChanged += (_, _) =>
        {
            if (!_updatingMonitors && _monitors.SelectedItem is ComboBoxItem { Tag: int index }) session.ShowMonitor(index);
        };
        Refresh();
    }

    public AgentDesktopSession Session => _session;

    private Border Header()
    {
        var header = new Border { CornerRadius = new CornerRadius(6, 6, 0, 0), Padding = new Thickness(12, 5, 8, 5) };
        header.SetResourceReference(Border.BackgroundProperty, "SessionHeaderBrush");
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_stripe);
        left.Children.Add(new SymbolIcon(SymbolRegular.DesktopCursor24) { FontSize = 15, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetBinding(TextBlock.TextProperty, nameof(SessionBase.Title));
        left.Children.Add(title);
        var sub = new TextBlock { Text = "  ·  Desktop  ·  " + _session.Connection.Host, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        left.Children.Add(sub);
        left.Children.Add(RemoteToolChrome.TransportBadge());
        left.Children.Add(RemoteToolChrome.RecordingBadge());

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _viewOnlyBadge.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        right.Children.Add(_viewOnlyBadge);
        right.Children.Add(_monitors);
        right.Children.Add(_quality);
        right.Children.Add(_infoButton);
        right.Children.Add(_keysButton);
        right.Children.Add(_fit);
        right.Children.Add(_winKey);
        _cad.Margin = new Thickness(4, 0, 0, 0);
        right.Children.Add(_cad);
        var row = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(left);
        header.Child = row;
        return header;
    }

    // ---- panels

    private static Border Card(UIElement content, double maxWidth = 560)
    {
        var card = new Border
        {
            Child = content, CornerRadius = new CornerRadius(8), Padding = new Thickness(28, 24, 28, 24), MaxWidth = maxWidth,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        card.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        var host = new Border { Child = card };
        host.SetResourceReference(Border.BackgroundProperty, "SessionAreaBrush");
        return host;
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };

    private static TextBlock Status()
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 480, Margin = new Thickness(0, 0, 0, 16) };
        t.SetBinding(TextBlock.TextProperty, nameof(SessionBase.StatusText));
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    private void BuildChoosePanel(Action close, Action useRdp)
    {
        var p = new StackPanel();
        p.Children.Add(Heading("Remote desktop through the RemoteHub Agent"));
        p.Children.Add(Status());
        p.Children.Add(Label("Windows session"));
        p.Children.Add(_sessionList);
        _selfViewWarning.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        p.Children.Add(_selfViewWarning);
        p.Children.Add(Label("The signed-in user"));
        p.Children.Add(_consent);
        p.Children.Add(Label("Picture"));
        p.Children.Add(_chooseQuality);
        _viewOnly.Margin = new Thickness(0, 14, 0, 0);
        p.Children.Add(_viewOnly);
        foreach (var q in DesktopQualityPreset.All) _chooseQuality.Items.Add(new ComboBoxItem { Content = q.Label, Tag = q });

        var connect = new Button { Content = "Connect", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.PlugConnected24), Margin = new Thickness(0, 0, 8, 0) };
        connect.Click += (_, _) => StartChosen();
        var rdp = new Button { Content = "Use RDP instead", Icon = new SymbolIcon(SymbolRegular.Desktop24), Margin = new Thickness(0, 0, 8, 0) };
        rdp.Click += (_, _) => useRdp();
        var closeBtn = new Button { Content = "Close tab", Icon = new SymbolIcon(SymbolRegular.Dismiss24) };
        closeBtn.Click += (_, _) => close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(connect);
        buttons.Children.Add(rdp);
        buttons.Children.Add(closeBtn);
        p.Children.Add(buttons);
        _choosePanel.Child = Card(new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    private void BuildWaitPanel()
    {
        var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        p.Children.Add(new ProgressRing { IsIndeterminate = true, Width = 40, Height = 40, Margin = new Thickness(0, 0, 0, 16) });
        var t = Status();
        t.TextAlignment = TextAlignment.Center;
        p.Children.Add(t);
        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => _session.Disconnect();
        p.Children.Add(cancel);
        _waitPanel.Child = Card(p, 480);
    }

    private void BuildPinPanel()
    {
        var p = new StackPanel();
        p.Children.Add(Heading("Enter the code"));
        p.Children.Add(Status());
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_pin);
        var ok = new Button { Content = "Connect", Appearance = ControlAppearance.Primary, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => SubmitPin();
        _pin.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SubmitPin(); e.Handled = true; } };
        row.Children.Add(ok);
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => _session.Disconnect();
        row.Children.Add(cancel);
        p.Children.Add(row);
        _pinInfo.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        p.Children.Add(_pinInfo);
        _pinPanel.Child = Card(p, 520);
    }

    private void BuildEndPanel(Action reconnect, Action close, Action useRdp)
    {
        var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var icon = new SymbolIcon(SymbolRegular.DesktopCursor24) { FontSize = 44, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Center };
        icon.SetBinding(Control.ForegroundProperty, nameof(SessionBase.AccentBrush));
        p.Children.Add(icon);
        var t = Status();
        t.TextAlignment = TextAlignment.Center;
        p.Children.Add(t);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var connect = new Button { Content = "Connect", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.PlugConnected24), Margin = new Thickness(0, 0, 8, 0) };
        connect.Click += (_, _) => reconnect();
        var rdp = new Button { Content = "Use RDP instead", Icon = new SymbolIcon(SymbolRegular.Desktop24), Margin = new Thickness(0, 0, 8, 0) };
        rdp.Click += (_, _) => useRdp();
        var closeBtn = new Button { Content = "Close tab", Icon = new SymbolIcon(SymbolRegular.Dismiss24) };
        closeBtn.Click += (_, _) => close();
        buttons.Children.Add(connect);
        buttons.Children.Add(rdp);
        buttons.Children.Add(closeBtn);
        p.Children.Add(buttons);
        _endPanel.Child = Card(p, 560);
    }

    private void BuildSecurePanel()
    {
        var t = new TextBlock
        {
            Text = "The device shows the Windows sign-in screen or a UAC prompt. Your account may not see it on this device – the picture continues when it is gone.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 460, TextAlignment = TextAlignment.Center, Foreground = Brushes.White, FontSize = 14,
        };
        _securePanel.Child = new Border { Child = t, Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x20, 0x20, 0x20)), Padding = new Thickness(24),
            CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    private void BuildInfoPanel()
    {
        var t = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
        t.SetBinding(TextBlock.TextProperty, nameof(AgentDesktopSession.ConnectionInfo));
        _infoPanel.Child = new Border { Child = t, Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x18, 0x1C, 0x24)), Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 10, 0) };
        _infoPanel.IsHitTestVisible = false;
    }

    /// <summary>The connection dropped: the last picture stays, dimmed, while RemoteHub reconnects.</summary>
    private void BuildReconnectPanel()
    {
        var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(new ProgressRing { IsIndeterminate = true, Width = 36, Height = 36, Margin = new Thickness(0, 0, 0, 14) });
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, TextAlignment = TextAlignment.Center, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 14) };
        t.SetBinding(TextBlock.TextProperty, nameof(SessionBase.StatusText));
        p.Children.Add(t);
        var cancel = new Button { Content = "Stop reconnecting", HorizontalAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => _session.Disconnect();
        p.Children.Add(cancel);
        _reconnectPanel.Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x12, 0x18));
        _reconnectPanel.Child = p;
    }

    /// <summary>The Windows session is disconnected: the picture stands still under this message until it has a screen again.</summary>
    private void BuildPausedPanel()
    {
        var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        p.Children.Add(new SymbolIcon(SymbolRegular.PlugDisconnected24) { FontSize = 36, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Center });
        p.Children.Add(new TextBlock { Text = "The session is disconnected", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) });
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, TextAlignment = TextAlignment.Center, Foreground = Brushes.White };
        t.SetBinding(TextBlock.TextProperty, nameof(AgentDesktopSession.Paused));
        p.Children.Add(t);
        _pausedPanel.Child = new Border { Child = p, Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x18, 0x1C, 0x24)), Padding = new Thickness(28, 22, 28, 22),
            CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>Remembered per PC: the connection info and sending key combinations.</summary>
    public void UsePreferences(bool showInfo, bool keyCombinations, Action<bool, bool> save)
    {
        _showInfo = showInfo;
        _keyCombinations = keyCombinations;
        _savePreferences = save;
        Refresh();
    }

    private void BuildNoticePanel()
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Foreground = Brushes.White };
        t.SetBinding(TextBlock.TextProperty, nameof(AgentDesktopSession.Notice));
        _noticePanel.Child = new Border { Child = t, Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x30, 0x30, 0x30)), Padding = new Thickness(14, 8, 14, 8),
            CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 0, 0) };
        _noticePanel.IsHitTestVisible = false;
    }

    // ---- choosing

    private void FillChoices()
    {
        var hello = _session.Hello;
        _sessionList.Children.Clear();
        if (hello is null) return;
        RadioButton? first = null;
        // Pre-select where someone works: a signed-in session (console or RDP) before a locked one, before the sign-in screen.
        var preferred = hello.Sessions.Where(s => s.State != DesktopSessionState.Disconnected)
            .OrderBy(s => s.State switch { DesktopSessionState.Active => 0, DesktopSessionState.Locked => 1, _ => 2 })
            .ThenByDescending(s => s.Console)
            .FirstOrDefault();
        foreach (var s in hello.Sessions)
        {
            var text = (s.Console ? "Console" : $"Remote session ({s.Station})") + " – " + s.State switch
            {
                DesktopSessionState.Active => $"{s.User} (signed in)",
                DesktopSessionState.Locked => $"{s.User} (locked)",
                DesktopSessionState.SignIn => "sign-in screen (nobody signed in)",
                _ => $"{s.User} (disconnected – no screen)",
            };
            var rb = new RadioButton { Content = text, Tag = s, GroupName = "session" + GetHashCode(), Margin = new Thickness(0, 2, 0, 2),
                IsEnabled = s.State != DesktopSessionState.Disconnected };
            rb.Checked += (_, _) => { UpdateConsentChoices(); UpdateSelfViewWarning(); };
            _sessionList.Children.Add(rb);
            if (ReferenceEquals(s, preferred)) first = rb;
        }
        first ??= _sessionList.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsEnabled);
        if (first is not null) first.IsChecked = true;
        UpdateSelfViewWarning();
        if (hello.Sessions.Length == 0)
            _sessionList.Children.Add(new TextBlock { Text = "No Windows session with a screen was found on the device.", TextWrapping = TextWrapping.Wrap });

        _viewOnly.IsChecked = !hello.Rights.Control;
        _viewOnly.IsEnabled = hello.Rights.Control;
        var preset = _defaultQuality();
        _chooseQuality.SelectedItem = _chooseQuality.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, preset)) ?? _chooseQuality.Items[1];
        UpdateConsentChoices();
    }

    /// <summary>
    /// The chosen session is this PC's own RDP session: the RDP window follows the remote pointer and moves this PC's mouse
    /// into it – a loop RemoteHub can't stop.
    /// </summary>
    private void UpdateSelfViewWarning()
    {
        var s = ChosenSession;
        var self = s is { Console: false, Client.Length: > 0 } && s.Client.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        _selfViewWarning.Text = self
            ? $"This session is connected by RDP from this PC ({s!.Client}). Its RDP window moves your mouse to follow the remote pointer – minimize the RDP window while you control it here, or connect from another PC."
            : "";
        _selfViewWarning.Visibility = self ? Visibility.Visible : Visibility.Collapsed;
    }

    private DesktopSessionInfo? ChosenSession =>
        _sessionList.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as DesktopSessionInfo;

    private void UpdateConsentChoices()
    {
        var hello = _session.Hello;
        var s = ChosenSession;
        var previous = (_consent.SelectedItem as ComboBoxItem)?.Tag as string;
        _consent.Items.Clear();
        if (hello is null || s is null) return;
        // Locked or sign-in screen: shown right away (nothing private); the mode applies when someone unlocks or signs in –
        // until they allow it, the device shows nothing more.
        var later = s.State != DesktopSessionState.Active;
        _consent.IsEnabled = true;
        foreach (var m in hello.ConsentModes)
            _consent.Items.Add(new ComboBoxItem
            {
                Tag = m,
                Content = (later ? "When someone unlocks or signs in: " : "") + m switch
                {
                    DesktopConsent.Ask => later ? "ask them" : "Ask them to allow it",
                    DesktopConsent.Pin => later ? "show them a code" : "Show them a code – they read it out to you",
                    _ => later ? "don't ask (they see a banner)" : "Connect without asking (they see a banner)",
                },
            });
        var remembered = previous ?? _rememberedConsent;
        _consent.SelectedItem = _consent.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == remembered) ?? _consent.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private string? _rememberedConsent;

    /// <summary>The consent mode used last time (pre-selected when allowed).</summary>
    public void RememberedConsent(string? mode) => _rememberedConsent = mode;

    private void StartChosen()
    {
        if (ChosenSession is not { } s || _consent.SelectedItem is not ComboBoxItem { Tag: string consent }) return;
        var quality = (_chooseQuality.SelectedItem as ComboBoxItem)?.Tag as DesktopQualityPreset ?? DesktopQualityPreset.Default;
        _remember(quality, consent);
        _session.Choose(s, consent, _viewOnly.IsChecked == true, quality);
    }

    private void SubmitPin()
    {
        var digits = new string(_pin.Text.Where(char.IsDigit).ToArray());
        if (digits.Length != 6) return;
        _session.SendPin(digits);
        _pin.Text = "";
    }

    // ---- state

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AgentDesktopSession.Phase):
            case nameof(AgentDesktopSession.Paused):
            case nameof(AgentDesktopSession.SecureDesktopHidden):
            case nameof(AgentDesktopSession.Notice):
            case nameof(AgentDesktopSession.ViewOnly):
            case nameof(AgentDesktopSession.PinAttemptsLeft):
                Refresh();
                break;
            case nameof(AgentDesktopSession.Layout):
                ApplyLayout();
                break;
            case nameof(AgentDesktopSession.CursorPosition):
            case nameof(AgentDesktopSession.CursorVisible):
            case nameof(AgentDesktopSession.CursorHotspot):
            case nameof(AgentDesktopSession.Cursor):
                UpdateCursor();
                break;
            case nameof(SessionBase.AccentBrush):
                UpdateFrame();
                break;
            case nameof(AgentDesktopSession.FitToWindow):
                UpdateFitButton();
                _rememberFit(_session.FitToWindow);
                break;
            case nameof(AgentDesktopSession.Quality):
                _quality.SelectedItem = _quality.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, _session.Quality));
                break;
        }
    }

    private void Refresh()
    {
        var phase = _session.Phase;
        static Visibility V(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
        _choosePanel.Visibility = V(phase == DesktopPhase.Choose);
        _waitPanel.Visibility = V(phase is DesktopPhase.Waiting or DesktopPhase.Connecting);
        _pinPanel.Visibility = V(phase == DesktopPhase.Pin);
        _endPanel.Visibility = V(phase is DesktopPhase.Ended or DesktopPhase.Idle);
        _securePanel.Visibility = V(phase == DesktopPhase.Live && _session.SecureDesktopHidden);
        _noticePanel.Visibility = V(phase is DesktopPhase.Live or DesktopPhase.Reconnecting && _session.Notice.Length > 0);
        _reconnectPanel.Visibility = V(phase == DesktopPhase.Reconnecting);
        _pausedPanel.Visibility = V(phase == DesktopPhase.Live && _session.Paused.Length > 0);
        var live = phase == DesktopPhase.Live;
        _infoPanel.Visibility = V(live && _showInfo);
        _monitors.Visibility = _quality.Visibility = _infoButton.Visibility = V(live);
        _cad.Visibility = _winKey.Visibility = _fit.Visibility = _keysButton.Visibility = V(live && !_session.ViewOnly);
        _infoButton.Appearance = _showInfo ? ControlAppearance.Primary : ControlAppearance.Secondary;
        _infoButton.ToolTip = _showInfo ? "Hide the connection info" : "Show connection info (frames, data rate, round trip, quality)";
        _keysButton.Appearance = _keyCombinations ? ControlAppearance.Primary : ControlAppearance.Secondary;
        _keysButton.ToolTip = _keyCombinations
            ? "Key combinations (Alt+Tab, Windows key + …) go to the device while this tab has the focus. Click to keep them on this PC."
            : "Key combinations (Alt+Tab, Windows key + …) stay on this PC. Click to send them to the device.";
        UpdateKeyHook();
        _quality.SelectedItem = _quality.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, _session.Quality));
        UpdateFitButton();
        if (live) ReportSize();
        _viewOnlyBadge.Visibility = V(live && _session.ViewOnly);
        if (phase == DesktopPhase.Choose) FillChoices();
        if (phase == DesktopPhase.Pin)
        {
            _pinInfo.Text = _session.PinAttemptsLeft > 0 ? $"{_session.PinAttemptsLeft} attempt(s) left." : "";
            Dispatcher.BeginInvoke(() => _pin.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
        if (live) Dispatcher.BeginInvoke(() => _content.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        UpdateCursor();
        UpdateFrame();
    }

    private void ApplyLayout()
    {
        var layout = _session.Layout;
        if (layout is null) return;
        _picture.Width = Math.Max(1, layout.Width);
        _picture.Height = Math.Max(1, layout.Height);
        _updatingMonitors = true;
        try
        {
            _monitors.Items.Clear();
            foreach (var m in layout.Monitors)
                _monitors.Items.Add(new ComboBoxItem { Content = $"Monitor {m.Index + 1}{(m.Primary ? " (main)" : "")} · {m.Width}×{m.Height}", Tag = m.Index });
            if (layout.Monitors.Length > 1 && _session.CanShowAllMonitors)
                _monitors.Items.Add(new ComboBoxItem { Content = $"All monitors · {layout.Width}×{layout.Height}", Tag = DesktopProtocol.AllMonitors });
            _monitors.SelectedItem = _monitors.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, layout.Monitor)) ?? _monitors.Items.OfType<ComboBoxItem>().FirstOrDefault();
            _monitors.IsEnabled = layout.Monitors.Length > 1;
        }
        finally { _updatingMonitors = false; }
        Refresh();
    }

    /// <summary>
    /// Controlling: this PC's pointer takes the remote shape (no delay). Viewing, or when the mouse is elsewhere: the
    /// remote pointer is drawn where it is on the device.
    /// </summary>
    private void UpdateCursor()
    {
        var control = _session.Phase == DesktopPhase.Live && !_session.ViewOnly;
        _picture.Cursor = control ? _session.Cursor ?? Cursors.Arrow : null;
        var overlay = _session.Phase == DesktopPhase.Live && _session.CursorVisible && (!control || !_picture.IsMouseOver);
        _cursorOverlay.Visibility = overlay ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_cursorOverlay, _session.CursorPosition.X - _session.CursorHotspot.X);
        Canvas.SetTop(_cursorOverlay, _session.CursorPosition.Y - _session.CursorHotspot.Y);
    }

    /// <summary>The picture area in device pixels, so a fitted resolution is shown 1:1 (sharp).</summary>
    private void ReportSize()
    {
        if (_content.ActualWidth < 1 || PresentationSource.FromVisual(_content) is not { CompositionTarget: { } target }) return;
        var m = target.TransformToDevice;
        _session.WindowSizeChanged((int)Math.Round(_content.ActualWidth * m.M11), (int)Math.Round(_content.ActualHeight * m.M22));
    }

    private void UpdateFitButton()
    {
        _fit.Appearance = _session.FitToWindow ? ControlAppearance.Primary : ControlAppearance.Secondary;
        _fit.ToolTip = _session.FitToWindow
            ? "The device's resolution follows this window (back to its own when you disconnect). Click to keep the device's resolution."
            : "Change the device's resolution to fit this window";
    }

    private void UpdateFrame()
    {
        var accent = _session.AccentBrush;
        _stripe.Background = accent;
        if (_session.HasKeyboardFocus && _session.Phase == DesktopPhase.Live) BorderBrush = accent;
        else SetResourceReference(BorderBrushProperty, "ChromeStrokeBrush");
    }

    // ---- input

    private bool Controlling => _session.Phase == DesktopPhase.Live && !_session.ViewOnly;

    private (int X, int Y) Pixel(MouseEventArgs e)
    {
        var p = e.GetPosition(_picture);
        return ((int)Math.Floor(p.X), (int)Math.Floor(p.Y));
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!Controlling) return;
        var (x, y) = Pixel(e);
        if (x == (int)_lastMouse.X && y == (int)_lastMouse.Y) return;
        _lastMouse = new Point(x, y);
        _session.SendMouse(new RemoteMouse(RemoteMouseAction.Move, RemoteMouseButton.None, x, y, 0));
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _content.Focus();
        if (!Controlling) return;
        var (x, y) = Pixel(e);
        _picture.CaptureMouse();
        _session.SendMouse(new RemoteMouse(RemoteMouseAction.Down, Button(e.ChangedButton), x, y, 0));
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!Controlling) return;
        var (x, y) = Pixel(e);
        _session.SendMouse(new RemoteMouse(RemoteMouseAction.Up, Button(e.ChangedButton), x, y, 0));
        if (Mouse.LeftButton == MouseButtonState.Released && Mouse.RightButton == MouseButtonState.Released && Mouse.MiddleButton == MouseButtonState.Released)
            _picture.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Controlling) return;
        var (x, y) = Pixel(e);
        _session.SendMouse(new RemoteMouse(RemoteMouseAction.Wheel, RemoteMouseButton.None, x, y, (short)Math.Clamp(e.Delta, short.MinValue, short.MaxValue)));
        e.Handled = true;
    }

    private static RemoteMouseButton Button(System.Windows.Input.MouseButton b) => b switch
    {
        System.Windows.Input.MouseButton.Left => RemoteMouseButton.Left,
        System.Windows.Input.MouseButton.Right => RemoteMouseButton.Right,
        System.Windows.Input.MouseButton.Middle => RemoteMouseButton.Middle,
        System.Windows.Input.MouseButton.XButton1 => RemoteMouseButton.X1,
        System.Windows.Input.MouseButton.XButton2 => RemoteMouseButton.X2,
        _ => RemoteMouseButton.None,
    };

    private void OnKey(KeyEventArgs e, bool up)
    {
        if (!Controlling || !_content.IsKeyboardFocusWithin) return;
        var (scan, extended) = ScanCode(e);
        if (scan == 0) return;
        SendKey(scan, extended, up);
        e.Handled = true; // Tab, Alt, F10 … go to the device, not to RemoteHub
    }

    // Keys held down on the device: released when the tab loses the focus, so nothing stays pressed there.
    private readonly HashSet<(int Scan, bool Extended)> _down = [];

    private void SendKey(int scan, bool extended, bool up)
    {
        if (up) _down.Remove((scan, extended));
        else _down.Add((scan, extended));
        _session.SendKey(new RemoteKey((ushort)scan, (extended ? RemoteKeyFlags.Extended : 0) | (up ? RemoteKeyFlags.Up : 0)));
    }

    private void ReleaseKeys()
    {
        foreach (var (scan, extended) in _down.ToList()) _session.SendKey(new RemoteKey((ushort)scan, (extended ? RemoteKeyFlags.Extended : 0) | RemoteKeyFlags.Up));
        _down.Clear();
    }

    // ---- key combinations: Windows handles Alt+Tab, the Windows key and its combinations before any window sees them.
    // A low-level keyboard hook takes every key while this tab controls the device and has the focus in the foreground
    // window, and sends it there instead (like the RDP client). Ctrl+Alt+Del always stays with Windows: use the button.

    private IntPtr _hook;
    private static AgentDesktopView? _hooked;
    private static readonly LowLevelKeyboardProc HookProc = OnHookKey;

    private void UpdateKeyHook()
    {
        var want = _keyCombinations && Controlling && _content.IsKeyboardFocusWithin && Window.GetWindow(this) is { IsActive: true };
        if (want && _hook == IntPtr.Zero)
        {
            _hooked?.RemoveKeyHook();
            _hook = SetWindowsHookExW(13 /* WH_KEYBOARD_LL */, HookProc, GetModuleHandleW(null), 0);
            if (_hook != IntPtr.Zero) _hooked = this;
        }
        else if (!want) RemoveKeyHook();
    }

    private void RemoveKeyHook()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        if (_hooked == this) _hooked = null;
    }

    private static unsafe IntPtr OnHookKey(int code, IntPtr wParam, IntPtr lParam)
    {
        var view = _hooked;
        if (code >= 0 && view is not null && view.Controlling && view._content.IsKeyboardFocusWithin)
        {
            var k = (KBDLLHOOKSTRUCT*)lParam;
            const uint injected = 0x10, extendedFlag = 0x01, upFlag = 0x80;
            if ((k->flags & injected) == 0 && k->scanCode != 0)
            {
                view.SendKey((int)(k->scanCode & 0xFF), (k->flags & extendedFlag) != 0, (k->flags & upFlag) != 0);
                return 1; // swallowed: the device gets it, this PC doesn't
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int id, LowLevelKeyboardProc proc, IntPtr module, uint thread);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

    private void PressWindowsKey()
    {
        SendKey(0x5B, true, up: false);
        SendKey(0x5B, true, up: true);
        _content.Focus();
    }

    private static readonly PropertyInfo? ScanCodeProperty = typeof(KeyEventArgs).GetProperty("ScanCode", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? ExtendedProperty = typeof(KeyEventArgs).GetProperty("IsExtendedKey", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>The hardware scan code of the key (the device's layout then decides the character, like RDP).</summary>
    internal static (int Scan, bool Extended) ScanCode(KeyEventArgs e)
    {
        if (ScanCodeProperty?.GetValue(e) is int scan && scan != 0)
            return (scan & 0xFF, ExtendedProperty?.GetValue(e) is true);
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        var mapped = MapVirtualKey((uint)vk, 4 /* MAPVK_VK_TO_VSC_EX */);
        return ((int)(mapped & 0xFF), (mapped & 0xFF00) == 0xE000);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
