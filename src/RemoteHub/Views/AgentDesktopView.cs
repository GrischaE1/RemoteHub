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
using RemoteHub.Services;
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
    private readonly Canvas _regionLayer = new() { IsHitTestVisible = false }; // "Hidden by the user" over the areas of hidden apps
    private readonly Viewbox _viewbox = new() { Stretch = Stretch.Uniform };
    private readonly Grid _content = new() { Focusable = true, FocusVisualStyle = null, Background = Brushes.Black };
    private readonly ComboBox _monitors = new() { MinWidth = 150, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Monitor of the device" };
    private readonly ComboBox _quality = new() { MinWidth = 210, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Frames per second and picture quality" };
    private readonly TextBlock _viewOnlyBadge = new() { Text = "View only", FontSize = 12, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _privacyBadge = new() { FontSize = 12, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _cad;
    private readonly Button _privacy;
    private readonly SymbolIcon _pausedIcon = new(SymbolRegular.PlugDisconnected24) { FontSize = 36, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _pausedHeading = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _pausedText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, TextAlignment = TextAlignment.Center, Foreground = Brushes.White };
    private readonly Button _winKey;
    private readonly Button _fit;
    private readonly Button _infoButton;
    private readonly Button _keysButton;
    private readonly System.Windows.Threading.DispatcherTimer _resizeDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly Border _choosePanel = new(), _waitPanel = new(), _pinPanel = new(), _endPanel = new(), _securePanel = new(), _noticePanel = new(),
        _infoPanel = new(), _reconnectPanel = new(), _pausedPanel = new(), _transferPanel = new(), _dropPanel = new() { Visibility = Visibility.Collapsed },
        _appClosedPanel = new(), _appClosingPanel = new();
    private readonly TextBlock _dropText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Foreground = Brushes.White };
    private readonly TextBlock _selfViewWarning = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private bool _showInfo, _keyCombinations = true;
    private Action<bool, bool> _savePreferences = (_, _) => { };
    private readonly StackPanel _sessionList = new();
    private readonly ComboBox _consent = new() { MinWidth = 300 };
    private readonly CheckBox _viewOnly = new() { Content = "View only – no mouse and keyboard" };
    /// <summary>Automatic sign-in on the device's sign-in screen (only offered where the device allows it for this account).</summary>
    private readonly CheckBox _autoSignIn = new();
    private readonly Func<bool> _autoSignInPreferred;
    private readonly Func<string?> _autoSignInEndAction;
    private readonly ComboBox _chooseQuality = new() { MinWidth = 300 };
    private readonly TextBox _pin = new() { PlaceholderText = "6-digit code", MaxLength = 7, Width = 180, FontSize = 18 };
    private readonly TextBlock _pinInfo = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Margin = new Thickness(0, 6, 0, 0) };
    private bool _updatingMonitors;
    private Point _lastMouse = new(-1, -1);

    private readonly Action<DesktopResolution> _rememberResolution;

    public AgentDesktopView(AgentDesktopSession session, Action reconnect, Action close, Action useRdp,
        Func<DesktopQualityPreset> defaultQuality, Action<DesktopQualityPreset, string> remember, Func<DesktopResolution> resolution, Action<DesktopResolution> rememberResolution,
        Func<bool>? autoSignInPreferred = null, Func<string?>? autoSignInEndAction = null)
    {
        _rememberResolution = rememberResolution;
        _autoSignInPreferred = autoSignInPreferred ?? (() => false);
        _autoSignInEndAction = autoSignInEndAction ?? (() => null);
        _session = session;
        _defaultQuality = defaultQuality;
        _remember = remember;
        DataContext = session;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(2);
        SetResourceReference(BackgroundProperty, "SessionAreaBrush");
        SetResourceReference(BorderBrushProperty, "ChromeStrokeBrush");

        _cad = RemoteToolChrome.Tool(SymbolRegular.Keyboard24, "Send Ctrl+Alt+Del", session.SendCtrlAltDel);
        _privacy = RemoteToolChrome.Tool(SymbolRegular.EyeOff24, "", () => { });
        _privacy.Click += (_, _) => { var m = PrivacyMenu(); m.PlacementTarget = _privacy; m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; m.IsOpen = true; };
        _privacyBadge.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _winKey = RemoteToolChrome.Tool(SymbolRegular.Grid24, "Press the Windows key (Start menu)", PressWindowsKey);
        _fit = RemoteToolChrome.Tool(SymbolRegular.ScaleFit24, "", () => { });
        _fit.Click += (_, _) => { var m = ResolutionMenu(); m.PlacementTarget = _fit; m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; m.IsOpen = true; };
        _fit.Margin = new Thickness(0, 0, 4, 0);
        System.Windows.Automation.AutomationProperties.SetName(_fit, "Device resolution");
        _infoButton = RemoteToolChrome.Tool(SymbolRegular.DataUsage24, "", () => { _showInfo = !_showInfo; _savePreferences(_showInfo, _keyCombinations); Refresh(); });
        _infoButton.Margin = new Thickness(0, 0, 4, 0);
        _keysButton = RemoteToolChrome.Tool(SymbolRegular.KeyCommand24, "", () => { _keyCombinations = !_keyCombinations; _savePreferences(_showInfo, _keyCombinations); UpdateKeyHook(); Refresh(); });
        _keysButton.Margin = new Thickness(0, 0, 4, 0);
        // Disabled while the user at the device hides their screen – the tooltip says why.
        foreach (var b in new[] { _cad, _winKey }) ToolTipService.SetShowOnDisabled(b, true);
        session.Resolution = resolution();
        _resizeDebounce.Tick += (_, _) => { _resizeDebounce.Stop(); ReportSize(); };
        _content.SizeChanged += (_, _) => { UpdatePictureScale(); _resizeDebounce.Stop(); _resizeDebounce.Start(); };

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
        _picture.Children.Add(_regionLayer);
        _cursorLayer.Children.Add(_cursorOverlay);
        _picture.Children.Add(_cursorLayer);
        _viewbox.Child = _picture;
        _viewbox.SizeChanged += (_, _) => UpdateRegions(); // the labels keep their size on the screen
        _content.Children.Add(_viewbox);
        _screen.SetBinding(System.Windows.Controls.Image.SourceProperty, nameof(AgentDesktopSession.Screen));
        _cursorOverlay.SetBinding(System.Windows.Controls.Image.SourceProperty, nameof(AgentDesktopSession.CursorImage));

        BuildChoosePanel(close, useRdp);
        BuildWaitPanel();
        BuildPinPanel();
        BuildEndPanel(reconnect, close, useRdp);
        BuildSecurePanel();
        BuildNoticePanel();
        BuildTransferPanel();
        BuildInfoPanel();
        BuildReconnectPanel();
        BuildPausedPanel();
        BuildDropPanel();
        BuildAppClosedPanel(close);
        BuildAppClosingPanel();
        foreach (var p in new[] { _securePanel, _pausedPanel, _noticePanel, _transferPanel, _infoPanel, _reconnectPanel, _choosePanel, _waitPanel, _pinPanel, _endPanel, _dropPanel,
                     _appClosedPanel, _appClosingPanel })
            _content.Children.Add(p);

        // Files dragged onto the picture: saved on the device's Desktop (AgentDesktopSession.DropFiles). While dragging,
        // the panel says where they go – or why they can't.
        _content.AllowDrop = true;
        _content.DragEnter += (_, e) => OnDrag(e);
        _content.DragOver += (_, e) => OnDrag(e);
        _content.DragLeave += (_, _) => _dropPanel.Visibility = Visibility.Collapsed;
        _content.Drop += (_, e) =>
        {
            _dropPanel.Visibility = Visibility.Collapsed;
            if (DroppedFiles(e) is { Length: > 0 } files) _session.DropFiles(files);
            e.Handled = true;
        };

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
        var sub = new TextBlock { Text = (_session.App is null ? "  ·  Desktop  ·  " : "  ·  App  ·  ") + _session.Connection.HostWithRoute, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        left.Children.Add(sub);
        left.Children.Add(RemoteToolChrome.TransportBadge());
        left.Children.Add(RemoteToolChrome.RecordingBadge());

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _viewOnlyBadge.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        right.Children.Add(_viewOnlyBadge);
        right.Children.Add(_privacyBadge);
        right.Children.Add(_monitors);
        right.Children.Add(_quality);
        right.Children.Add(_infoButton);
        right.Children.Add(_keysButton);
        right.Children.Add(_fit);
        right.Children.Add(_winKey);
        _cad.Margin = new Thickness(4, 0, 0, 0);
        right.Children.Add(_cad);
        _privacy.Margin = new Thickness(4, 0, 0, 0);
        right.Children.Add(_privacy);
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
        _autoSignIn.Margin = new Thickness(0, 14, 0, 0);
        p.Children.Add(_autoSignIn);
        _viewOnly.Checked += (_, _) => UpdateAutoSignIn();
        _viewOnly.Unchecked += (_, _) => UpdateAutoSignIn();
        foreach (var q in DesktopQualityPreset.All) _chooseQuality.Items.Add(new ComboBoxItem { Content = q.Label, Tag = q });

        _connectChosen.Click += (_, _) => StartChosen();
        var rdp = new Button { Content = "Use RDP instead", Icon = new SymbolIcon(SymbolRegular.Desktop24), Margin = new Thickness(0, 0, 8, 0) };
        rdp.Click += (_, _) => useRdp();
        var closeBtn = new Button { Content = "Close tab", Icon = new SymbolIcon(SymbolRegular.Dismiss24) };
        closeBtn.Click += (_, _) => close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(_connectChosen);
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
        // An app tab neither falls back to RDP nor shows the desktop's icon (§8.4).
        if (_session.App is not null) icon.Symbol = SymbolRegular.Apps24;
        var rdp = new Button { Content = "Use RDP instead", Icon = new SymbolIcon(SymbolRegular.Desktop24), Margin = new Thickness(0, 0, 8, 0),
            Visibility = _session.App is null ? Visibility.Visible : Visibility.Collapsed };
        rdp.Click += (_, _) => useRdp();
        var closeBtn = new Button { Content = "Close tab", Icon = new SymbolIcon(SymbolRegular.Dismiss24) };
        closeBtn.Click += (_, _) => close();
        buttons.Children.Add(connect);
        buttons.Children.Add(rdp);
        buttons.Children.Add(closeBtn);
        p.Children.Add(buttons);
        _endPanel.Child = Card(p, 560);
    }

    /// <summary>App tab: its app ended by itself (app-tabs-design §4.3) – start it again on the same monitor, or close the tab.</summary>
    private void BuildAppClosedPanel(Action close)
    {
        var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var icon = new SymbolIcon(SymbolRegular.Apps24) { FontSize = 44, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Center };
        icon.SetBinding(Control.ForegroundProperty, nameof(SessionBase.AccentBrush));
        p.Children.Add(icon);
        var t = Status();
        t.TextAlignment = TextAlignment.Center;
        p.Children.Add(t);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var again = new Button { Content = "Start again", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.ArrowRepeatAll24), Margin = new Thickness(0, 0, 8, 0) };
        again.Click += (_, _) => _session.RestartApp();
        var closeBtn = new Button { Content = "Close tab", Icon = new SymbolIcon(SymbolRegular.Dismiss24) };
        closeBtn.Click += (_, _) => close();
        buttons.Children.Add(again);
        buttons.Children.Add(closeBtn);
        p.Children.Add(buttons);
        _appClosedPanel.Child = Card(p, 560);
    }

    /// <summary>
    /// App tab closed while its app asks something first ("Save changes?"): a card at the bottom, like the transfer panel,
    /// so the app's question above stays visible and can be answered in the tab – or the app is left running.
    /// </summary>
    private void BuildAppClosingPanel()
    {
        var p = new StackPanel { Width = 360 };
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        title.SetBinding(TextBlock.TextProperty, nameof(SessionBase.StatusText));
        p.Children.Add(title);
        p.Children.Add(new TextBlock
        {
            Text = "Answer it above and the tab closes with the app – or leave the app running on the device. Cancelled it? Keep the tab.", FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xCC, 0xD4)), Margin = new Thickness(0, 0, 0, 10),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var leave = new Button { Content = "Leave it running", Icon = new SymbolIcon(SymbolRegular.Open24), Margin = new Thickness(0, 0, 8, 0) };
        leave.Click += (_, _) => _session.CloseApp(leaveRunning: true);
        var keep = new Button { Content = "Keep the tab" };
        keep.Click += (_, _) => _session.KeepTab();
        buttons.Children.Add(leave);
        buttons.Children.Add(keep);
        p.Children.Add(buttons);
        _appClosingPanel.Child = new Border { Child = p, Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x18, 0x1C, 0x24)), Padding = new Thickness(16, 12, 12, 12),
            CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 16) };
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
        var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        p.Children.Add(new ProgressRing { IsIndeterminate = true, Width = 40, Height = 40, Margin = new Thickness(0, 0, 0, 16), HorizontalAlignment = HorizontalAlignment.Center });
        p.Children.Add(new TextBlock { Text = "Connection lost – reconnecting…", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) });
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 440, TextAlignment = TextAlignment.Center, FontSize = 13, LineHeight = 20,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xCC, 0xD4)), Margin = new Thickness(0, 0, 0, 18) };
        t.SetBinding(TextBlock.TextProperty, nameof(AgentDesktopSession.ReconnectDetail));
        p.Children.Add(t);
        var cancel = new Button { Content = "Stop reconnecting", Icon = new SymbolIcon(SymbolRegular.Dismiss24), HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 160 };
        cancel.Click += (_, _) => _session.Disconnect();
        p.Children.Add(cancel);
        // Darker behind, and an opaque card: readable over any picture (the last frame stays underneath).
        _reconnectPanel.Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x08, 0x0A, 0x0E));
        _reconnectPanel.Child = new Border { Child = p, Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x2A)), Padding = new Thickness(32, 26, 32, 24),
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4C)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520 };
    }

    /// <summary>The Windows session is disconnected: the picture stands still under this message until it has a screen again.</summary>
    private void BuildPausedPanel()
    {
        // Why the picture stands still: the session is disconnected, or the user at the device hid their screen (UpdatePausedPanel).
        var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        p.Children.Add(_pausedIcon);
        p.Children.Add(_pausedHeading);
        p.Children.Add(_pausedText);
        _pausedPanel.Child = new Border { Child = p, Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x18, 0x1C, 0x24)), Padding = new Thickness(28, 22, 28, 22),
            CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    private DesktopPrivacy? _privacyShown;

    /// <summary>"Hidden by the user" over each area a hidden app covers in the picture (the device sends where they are).</summary>
    private void UpdateRegions()
    {
        _privacyShown = _session.Privacy with { Regions = null };
        _regionLayer.Children.Clear();
        if (_session.Phase != DesktopPhase.Live || _session.HiddenByUser || _session.Privacy.Regions is not { } regions) return;
        // The layer lies in the picture's own pixels, scaled with it: frame and label are scaled back, so they keep their
        // size on the screen whatever the device's resolution.
        var scale = _picture.ActualWidth > 0 && _picture.ActualHeight > 0 ? Math.Min(_viewbox.ActualWidth / _picture.ActualWidth, _viewbox.ActualHeight / _picture.ActualHeight) : 1; // Uniform
        if (scale <= 0 || double.IsNaN(scale)) scale = 1;
        foreach (var r in regions)
        {
            var area = new Border { Width = r.Width, Height = r.Height, BorderThickness = new Thickness(1 / scale), ClipToBounds = true };
            area.SetResourceReference(Border.BorderBrushProperty, "ChromeStrokeBrush");
            // The label only where it fits on the screen; small pieces (a covered strip beside another window) stay plain.
            if (r.Width * scale >= 200 && r.Height * scale >= 56)
            {
                var label = new StackPanel { Orientation = Orientation.Horizontal };
                label.Children.Add(new SymbolIcon(SymbolRegular.EyeOff24) { FontSize = 16, Foreground = Brushes.White, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                label.Children.Add(new TextBlock { Text = RegionLabel(_session.Privacy), Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
                // The same dark card as the tab's other notices over the picture.
                area.Child = new Border
                {
                    Child = label, Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x18, 0x1C, 0x24)), Padding = new Thickness(12, 6, 12, 6),
                    CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    LayoutTransform = new ScaleTransform(1 / scale, 1 / scale),
                };
            }
            Canvas.SetLeft(area, r.X);
            Canvas.SetTop(area, r.Y);
            _regionLayer.Children.Add(area);
        }
    }

    /// <summary>
    /// The label on a hidden area. The device doesn't say whose app an area belongs to, so it names the user or the
    /// policy only when all hidden apps are theirs – an app the policy hides must not read as one the user hid.
    /// </summary>
    internal static string RegionLabel(DesktopPrivacy privacy) => (privacy.HiddenApps, privacy.PolicyHiddenApps) switch
    {
        (> 0, > 0) => "Hidden",
        (_, > 0) => "Hidden by IT policy",
        _ => "Hidden by the user", // also from an agent that doesn't count the policy's apps apart
    };

    /// <summary>The header's note on hidden apps: the user's and the policy's counted separately, never which.</summary>
    internal static string HiddenAppsText(DesktopPrivacy privacy)
    {
        static string Count(int n, string by) => n switch { <= 0 => "", 1 => $"1 app hidden by {by}", _ => $"{n} apps hidden by {by}" };
        return string.Join(" · ", new[] { Count(privacy.HiddenApps, "the user"), Count(privacy.PolicyHiddenApps, "IT policy") }.Where(t => t.Length > 0));
    }

    private void UpdatePausedPanel()
    {
        var hidden = _session.HiddenByUser;
        var blurred = _session.Privacy.User == DesktopScreenHide.Blur;
        _pausedIcon.Symbol = hidden ? SymbolRegular.EyeOff24 : SymbolRegular.PlugDisconnected24;
        _pausedHeading.Text = hidden ? (blurred ? "The user has blurred their screen" : "The user has hidden their screen") : "The session is disconnected";
        _pausedText.Text = hidden ? "You'll see it again when they click \"Show screen\". Mouse and keyboard are paused." : _session.Paused;
        // Blurred: the card moves to the bottom edge, so the blurred picture stays in view.
        if (_pausedPanel.Child is Border card)
        {
            card.VerticalAlignment = hidden && blurred ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            card.Margin = hidden && blurred ? new Thickness(0, 0, 0, 24) : new Thickness(0);
        }
    }

    /// <summary>The privacy screen's menu: what the person at the device sees while you work.</summary>
    private System.Windows.Controls.ContextMenu PrivacyMenu()
    {
        var on = _session.Privacy.Screen != DesktopPrivacyStyle.Off;
        var block = new System.Windows.Controls.MenuItem
        {
            Header = "Block mouse and keyboard at the device", IsCheckable = true,
            IsChecked = _session.CanBlockLocalInput && (on ? _session.Privacy.LocalInputBlocked : _session.BlockLocalInput),
            IsEnabled = _session.CanBlockLocalInput, StaysOpenOnClick = true,
            ToolTip = _session.CanBlockLocalInput ? "On by default. Clicks on \"Disconnect\" at the device and Ctrl+Alt+Del still work there." : "Not allowed on this device (policy).",
        };
        ToolTipService.SetShowOnDisabled(block, true);
        // Remembered for this tab; while the screen is on it applies at once, in the style that is on.
        block.Click += (_, _) =>
        {
            _session.BlockLocalInput = block.IsChecked;
            if (on) _session.SetPrivacyScreen(_session.Privacy.Screen);
        };
        var m = new System.Windows.Controls.ContextMenu();
        // The style that is on now shows a tick in place of its icon (a menu item with an icon has no separate tick column).
        var black = RemoteToolChrome.Item("Black screen", _session.Privacy.Screen == DesktopPrivacyStyle.Black ? SymbolRegular.Checkmark24 : SymbolRegular.EyeOff24, () => _session.SetPrivacyScreen(DesktopPrivacyStyle.Black));
        var message = RemoteToolChrome.Item("Black screen with a message", _session.Privacy.Screen == DesktopPrivacyStyle.Message ? SymbolRegular.Checkmark24 : SymbolRegular.Chat24, () => _session.SetPrivacyScreen(DesktopPrivacyStyle.Message));
        m.Items.Add(black);
        m.Items.Add(message);
        m.Items.Add(new Separator());
        m.Items.Add(block);
        if (on)
        {
            m.Items.Add(new Separator());
            m.Items.Add(RemoteToolChrome.Item("Turn off the privacy screen", SymbolRegular.Eye24, () => _session.SetPrivacyScreen(DesktopPrivacyStyle.Off)));
        }
        return m;
    }

    /// <summary>A copy of files or an image (either way) in progress: bar, "X of Y · speed · time left", cancel (to the device only).</summary>
    private void BuildTransferPanel()
    {
        var p = new StackPanel { Width = 360 };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var cancel = new Button { Padding = new Thickness(3), MinWidth = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Content = new SymbolIcon(SymbolRegular.Dismiss24) { FontSize = 12, Foreground = Brushes.White }, ToolTip = "Cancel", VerticalAlignment = VerticalAlignment.Top,
            Cursor = System.Windows.Input.Cursors.Arrow };
        cancel.Click += (_, _) => _session.CancelTransfer();
        cancel.SetBinding(VisibilityProperty, new Binding(nameof(AgentDesktopSession.TransferCancellable)) { Converter = new BooleanToVisibilityConverter() });
        DockPanel.SetDock(cancel, Dock.Right);
        head.Children.Add(cancel);
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        title.SetBinding(TextBlock.TextProperty, nameof(AgentDesktopSession.TransferTitle));
        head.Children.Add(title);
        p.Children.Add(head);
        var bar = new System.Windows.Controls.ProgressBar { Height = 4, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 0, 0, 6) };
        bar.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, nameof(AgentDesktopSession.TransferProgress));
        p.Children.Add(bar);
        var detail = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xCC, 0xD4)) };
        detail.SetBinding(TextBlock.TextProperty, nameof(AgentDesktopSession.TransferDetail));
        p.Children.Add(detail);
        _transferPanel.Child = new Border { Child = p, Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x18, 0x1C, 0x24)), Padding = new Thickness(16, 12, 12, 12),
            CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 16) };
    }

    /// <summary>Remembered per PC: the connection info and sending key combinations.</summary>
    public void UsePreferences(bool showInfo, bool keyCombinations, Action<bool, bool> save)
    {
        _showInfo = AppPolicy.AgentDesktopShowInfo ?? showInfo;
        _keyCombinations = AppPolicy.AgentDesktopKeyCombinations ?? keyCombinations;
        _savePreferences = save;
        Refresh();
    }

    /// <summary>Toolbar options this PC's policy fixes (§6.8): greyed out; called after Refresh set their own tooltips.</summary>
    private void LockByPolicy()
    {
        if (AppPolicy.AgentDesktopShowInfo is not null) PolicyLock.Apply(_infoButton);
        if (AppPolicy.AgentDesktopKeyCombinations is not null) PolicyLock.Apply(_keysButton);
        if (AppPolicy.AgentDesktopFitToWindow is not null) PolicyLock.Apply(_fit);
        if (AppPolicy.AgentDesktopQuality is not null) PolicyLock.Apply(_quality);
    }

    /// <summary>Shown while files are dragged over the picture: where they will be saved, or why they can't be.</summary>
    private void BuildDropPanel()
    {
        _dropPanel.Child = new Border { Child = _dropText, Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x30, 0x30, 0x30)), Padding = new Thickness(14, 8, 14, 8),
            CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _dropPanel.IsHitTestVisible = false;
    }

    internal static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] f ? f : [];

    /// <summary>A drag over the picture: copy when the files may go, with the panel saying where to; else why not.</summary>
    private void OnDrag(DragEventArgs e)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop);
        (e.Effects, var text, var show) = DragState(files, files ? _session.DropRefusal : null);
        _dropText.Text = text;
        _dropPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    /// <summary>What a drag over the picture does: files that may go are copied, with the hint; else none, with why.</summary>
    internal static (DragDropEffects Effects, string Text, bool Show) DragState(bool files, string? refusal) =>
        !files ? (DragDropEffects.None, "", false)
        : refusal is null ? (DragDropEffects.Copy, DropHint, true)
        : (DragDropEffects.None, refusal, true);

    internal static readonly string DropHint = $"Drop to save on the device's Desktop, in “{DesktopProtocol.DropFolder}”.";

    private void BuildNoticePanel()
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Foreground = Brushes.White };
        t.SetBinding(TextBlock.TextProperty, nameof(AgentDesktopSession.Notice));
        _noticePanel.Child = new Border { Child = t, Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x30, 0x30, 0x30)), Padding = new Thickness(14, 8, 14, 8),
            CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 0, 0) };
        _noticePanel.IsHitTestVisible = false;
    }

    // ---- choosing

    private readonly Button _connectChosen = new()
        { Content = "Connect", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.PlugConnected24), Margin = new Thickness(0, 0, 8, 0) };

    private void FillChoices()
    {
        var hello = _session.Hello;
        _sessionList.Children.Clear();
        if (hello is null) return;
        RadioButton? first = null;
        // The sign-in and lock screen need the SecureDesktop right – the agent would refuse them anyway: say so here.
        bool Usable(DesktopSessionInfo s) => s.State != DesktopSessionState.Disconnected &&
                                             (hello.Rights.SecureDesktop || s.State == DesktopSessionState.Active);
        // Pre-select where someone works: a signed-in session (console or RDP) before a locked one, before the sign-in screen.
        var preferred = hello.Sessions.Where(Usable)
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
            if (s.State is DesktopSessionState.SignIn or DesktopSessionState.Locked && !hello.Rights.SecureDesktop)
                text += " – your account may not see it";
            var rb = new RadioButton { Content = text, Tag = s, GroupName = "session" + GetHashCode(), Margin = new Thickness(0, 2, 0, 2),
                IsEnabled = Usable(s) };
            rb.Checked += (_, _) => { UpdateConsentChoices(); UpdateSelfViewWarning(); UpdateAutoSignIn(); };
            _sessionList.Children.Add(rb);
            if (ReferenceEquals(s, preferred)) first = rb;
        }
        first ??= _sessionList.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsEnabled);
        if (first is not null) first.IsChecked = true;
        UpdateSelfViewWarning();
        if (hello.Sessions.Length == 0)
            _sessionList.Children.Add(new TextBlock { Text = "No Windows session with a screen was found on the device.", TextWrapping = TextWrapping.Wrap });
        else if (!hello.Sessions.Any(Usable))
            _sessionList.Children.Add(new TextBlock
            {
                Text = hello.Rights.SecureDesktop
                    ? "No session has a screen right now (disconnected). Connect to it by RDP, or wait until someone signs in."
                    : "Nobody is using a session right now, and your account may not see the sign-in or lock screen on this device " +
                      "(right \"SecureDesktop\" in the device's policy). Wait until someone signs in, or use RDP.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });
        _connectChosen.IsEnabled = hello.Sessions.Any(Usable);

        _viewOnly.IsChecked = !hello.Rights.Control || AppPolicy.AgentDesktopViewOnly;
        _viewOnly.IsEnabled = hello.Rights.Control;
        // Fixed by this PC's policy (docs/admx-design.md §6.8): shown, greyed out.
        if (AppPolicy.AgentDesktopViewOnly) PolicyLock.Apply(_viewOnly);
        var preset = _defaultQuality();
        _chooseQuality.SelectedItem = _chooseQuality.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, preset)) ?? _chooseQuality.Items[1];
        if (AppPolicy.AgentDesktopQuality is not null) PolicyLock.Apply(_chooseQuality);
        UpdateConsentChoices();
        _autoSignIn.Content = $"Sign in automatically as {hello.Account}";
        _autoSignIn.IsChecked = _autoSignInPreferred();
        UpdateAutoSignIn();
    }

    /// <summary>Offered for the console's sign-in screen when the device allows it for this account; not in view-only.</summary>
    private void UpdateAutoSignIn()
    {
        var offered = ChosenSession is { } s && _session.CanAutoSignIn(s);
        _autoSignIn.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
        _autoSignIn.IsEnabled = _viewOnly.IsChecked != true;
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
        _consent.ToolTip = null; // a lock for another session's choice doesn't carry over
        // Your own signed-in session: connecting without asking is always offered (the agent allows it there).
        string[] modes = !later && SameAccount(s.User, hello.Account) && !hello.ConsentModes.Contains(DesktopConsent.None)
            ? [DesktopConsent.None, .. hello.ConsentModes] : hello.ConsentModes;
        foreach (var m in modes)
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
        // This PC's policy AgentDesktopConsent fixes the mode – when the device accepts it; the device's AllowedConsentModes
        // still decide, so a mode it doesn't offer leaves the choice as it is (§6.8).
        var byPolicy = AppPolicy.AgentDesktopConsent is { } p && modes.Contains(p) ? p : null;
        var remembered = byPolicy ?? previous ?? _rememberedConsent;
        _consent.SelectedItem = _consent.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == remembered)
            ?? _consent.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == DesktopConsent.Ask)
            ?? _consent.Items.OfType<ComboBoxItem>().FirstOrDefault();
        if (byPolicy is not null) PolicyLock.Apply(_consent);
    }

    /// <summary>The same account: "DEVICE\user" and ".\user" name the same local account.</summary>
    private static bool SameAccount(string a, string b)
    {
        static string Local(string x) => x.StartsWith(@".\", StringComparison.Ordinal) ? x[2..] : x;
        if (a.Length == 0 || b.Length == 0) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (!a.StartsWith(@".\", StringComparison.Ordinal) && !b.StartsWith(@".\", StringComparison.Ordinal)) return false;
        string Name(string x) => Local(x)[(Local(x).LastIndexOf('\\') + 1)..];
        return string.Equals(Name(a), Name(b), StringComparison.OrdinalIgnoreCase);
    }

    private string? _rememberedConsent;

    /// <summary>The consent mode used last time (pre-selected when allowed).</summary>
    public void RememberedConsent(string? mode) => _rememberedConsent = mode;

    private void StartChosen()
    {
        if (ChosenSession is not { } s || _consent.SelectedItem is not ComboBoxItem { Tag: string consent }) return;
        var quality = (_chooseQuality.SelectedItem as ComboBoxItem)?.Tag as DesktopQualityPreset ?? DesktopQualityPreset.Default;
        _remember(quality, consent);
        _session.Choose(s, consent, _viewOnly.IsChecked == true, quality,
            autoSignIn: _autoSignIn.Visibility == Visibility.Visible && _autoSignIn.IsEnabled && _autoSignIn.IsChecked == true,
            endAction: _autoSignInEndAction());
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
            case nameof(AgentDesktopSession.Privacy) when (_session.Privacy with { Regions = null }) == _privacyShown:
                UpdateRegions(); // only the hidden areas moved (several times a second while a window is dragged)
                break;
            case nameof(AgentDesktopSession.Phase):
            case nameof(AgentDesktopSession.Paused):
            case nameof(AgentDesktopSession.Privacy):
            case nameof(AgentDesktopSession.SecureDesktopHidden):
            case nameof(AgentDesktopSession.Notice):
            case nameof(AgentDesktopSession.TransferActive):
            case nameof(AgentDesktopSession.ClosingApp):
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
            case nameof(AgentDesktopSession.Resolution):
                UpdateFitButton();
                _rememberResolution(_session.Resolution);
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
        _transferPanel.Visibility = V(phase is DesktopPhase.Live or DesktopPhase.Reconnecting && _session.TransferActive);
        _reconnectPanel.Visibility = V(phase == DesktopPhase.Reconnecting);
        _pausedPanel.Visibility = V(phase == DesktopPhase.Live && (_session.Paused.Length > 0 || _session.HiddenByUser));
        UpdatePausedPanel();
        var live = phase == DesktopPhase.Live;
        _appClosedPanel.Visibility = V(phase == DesktopPhase.AppClosed);
        _appClosingPanel.Visibility = V(live && _session.ClosingApp);
        _infoPanel.Visibility = V(live && _showInfo);
        // An app tab shows only its app: no monitor to switch to, no resolution to choose (the tab sets it), no Windows key
        // or Ctrl+Alt+Del (dropped there anyway) and no privacy screen (the device covers its own screens) – §8.4.
        var desktop = _session.App is null;
        _monitors.Visibility = V(live && desktop);
        _quality.Visibility = _infoButton.Visibility = V(live);
        _cad.Visibility = _winKey.Visibility = _fit.Visibility = V(live && !_session.ViewOnly && desktop);
        _keysButton.Visibility = V(live && !_session.ViewOnly);
        // The user at the device hid their screen: nothing typed or sent from here gets through until they show it again.
        var hidden = _session.HiddenByUser;
        _cad.IsEnabled = _winKey.IsEnabled = !hidden; // the keys toggle is this PC's setting: it stays usable
        _cad.ToolTip = hidden ? "Not while the user at the device hides their screen" : "Send Ctrl+Alt+Del";
        _winKey.ToolTip = hidden ? "Not while the user at the device hides their screen" : "Press the Windows key (Start menu)";
        _privacy.Visibility = V(live && !_session.ViewOnly && _session.CanUsePrivacyScreen && desktop);
        var screenOn = _session.Privacy.Screen != DesktopPrivacyStyle.Off;
        _privacy.Appearance = screenOn ? ControlAppearance.Primary : ControlAppearance.Secondary;
        var withMessage = _session.Privacy.Screen == DesktopPrivacyStyle.Message;
        _privacy.ToolTip = screenOn
            ? $"The device's screen is blacked out{(withMessage ? " with a message" : "")} – the person at the device can't see what you do. Click to change or turn it off."
            : "Black out the device's screen – the person at the device can't see what you do";
        System.Windows.Automation.AutomationProperties.SetName(_privacy, "Privacy screen");
        var apps = HiddenAppsText(_session.Privacy);
        var screenText = screenOn ? $"Privacy screen on ({(withMessage ? "message" : "black")}){(_session.Privacy.LocalInputBlocked ? " · mouse and keyboard blocked" : "")}" : "";
        _privacyBadge.Text = hidden ? (_session.Privacy.User == DesktopScreenHide.Blur ? "Blurred by the user" : "Hidden by the user")
            : string.Join(" · ", new[] { screenText, apps }.Where(t => t.Length > 0));
        _privacyBadge.Visibility = V(live && _privacyBadge.Text.Length > 0);
        UpdateRegions();
        _infoButton.Appearance = _showInfo ? ControlAppearance.Primary : ControlAppearance.Secondary;
        _infoButton.ToolTip = _showInfo ? "Hide the connection info" : "Show connection info (frames, data rate, round trip, quality)";
        _keysButton.Appearance = _keyCombinations ? ControlAppearance.Primary : ControlAppearance.Secondary;
        _keysButton.ToolTip = _keyCombinations
            ? "Key combinations (Alt+Tab, Windows key + …) go to the device while this tab has the focus. Click to keep them on this PC."
            : "Key combinations (Alt+Tab, Windows key + …) stay on this PC. Click to send them to the device.";
        UpdateKeyHook();
        _quality.SelectedItem = _quality.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, _session.Quality));
        UpdateFitButton();
        LockByPolicy();
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
        UpdatePictureScale();
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

    /// <summary>The picture area in device pixels, so a fitted resolution is shown 1:1 (sharp), and this monitor's scale.</summary>
    private void ReportSize()
    {
        if (_content.ActualWidth < 1 || PresentationSource.FromVisual(_content) is not { CompositionTarget: { } target }) return;
        var m = target.TransformToDevice;
        _session.WindowSizeChanged((int)Math.Round(_content.ActualWidth * m.M11), (int)Math.Round(_content.ActualHeight * m.M22), (int)Math.Round(m.M11 * 100));
    }

    /// <summary>Moved to a monitor with another scale: the size in pixels and the scale both change.</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdatePictureScale();
        _resizeDebounce.Stop();
        _resizeDebounce.Start();
    }

    /// <summary>
    /// A picture that fits the area with at most <see cref="PictureFit.Slack"/> device pixels to spare is shown 1:1,
    /// centred on whole pixels. A fitted resolution is rounded down to even sizes, so an area an odd number of pixels
    /// wide (e.g. once the transfers pane opens) got a picture one pixel smaller, which the Viewbox stretched by 0.1 % –
    /// blurring all of it.
    /// </summary>
    private void UpdatePictureScale()
    {
        if (_session.Layout is not { } layout || PresentationSource.FromVisual(_content) is not { CompositionTarget: { } target }) return;
        var m = target.TransformToDevice;
        var native = PictureFit.ShowsNative(layout.Width, layout.Height, _content.ActualWidth * m.M11, _content.ActualHeight * m.M22);
        _viewbox.StretchDirection = native ? StretchDirection.DownOnly : StretchDirection.Both;
        _viewbox.UseLayoutRounding = native;
        _picture.LayoutTransform = native ? new ScaleTransform(1 / m.M11, 1 / m.M22) : Transform.Identity;
    }

    private void UpdateFitButton()
    {
        var r = _session.Resolution;
        _fit.Appearance = r.Kind == DesktopResolutionKind.Device ? ControlAppearance.Secondary : ControlAppearance.Primary;
        _fit.ToolTip = r.Kind switch
        {
            DesktopResolutionKind.Fit => "The device's resolution follows this window (back to its own when you disconnect). Click to choose another.",
            DesktopResolutionKind.Fixed => $"The device runs at {r.Label} – the picture is scaled to this window (back to its own resolution when you disconnect). Click to choose another.",
            _ => "Change the device's resolution – to fit this window, or larger to see more of the screen",
        };
        if (AppPolicy.AgentDesktopFitToWindow is not null) PolicyLock.Apply(_fit); // fixed by policy: its own tooltip, not the one above
    }

    /// <summary>The device's resolution: its own, this window's size or a fixed one (larger than the window too – the picture is scaled down).</summary>
    private System.Windows.Controls.ContextMenu ResolutionMenu()
    {
        var current = _session.Resolution;
        // The choice that is on shows a tick in place of its icon, as in the privacy screen's menu.
        System.Windows.Controls.MenuItem Choice(DesktopResolution r, SymbolRegular icon, string? label = null) =>
            RemoteToolChrome.Item(label ?? r.Label, r == current ? SymbolRegular.Checkmark24 : icon, () => _session.Resolution = r);
        var m = new System.Windows.Controls.ContextMenu();
        m.Items.Add(Choice(DesktopResolution.Device, SymbolRegular.Desktop24));
        m.Items.Add(Choice(DesktopResolution.Fit, SymbolRegular.ScaleFit24));
        m.Items.Add(new Separator());
        foreach (var r in DesktopResolution.Presets) m.Items.Add(Choice(r, SymbolRegular.Resize24));
        if (current.Kind == DesktopResolutionKind.Fixed && !DesktopResolution.Presets.Contains(current)) m.Items.Add(Choice(current, SymbolRegular.Resize24));
        m.Items.Add(new Separator());
        m.Items.Add(RemoteToolChrome.Item("Custom resolution…", SymbolRegular.Edit24, ChooseCustomResolution));
        return m;
    }

    private void ChooseCustomResolution()
    {
        var initial = _session.Resolution.Kind == DesktopResolutionKind.Fixed ? _session.Resolution.Label : "2560 × 1600";
        var dlg = new InputDialog("Custom resolution", "Width × height in pixels for the device – larger than this window too, the picture is scaled down. The display may only offer a size close to it.",
            initial) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        if (DesktopResolution.Parse(dlg.Value) is { Kind: DesktopResolutionKind.Fixed } r) _session.Resolution = r;
        else _session.ShowNotice($"That is not a valid resolution – enter width × height, from {DesktopProtocol.MinFitWidth} × {DesktopProtocol.MinFitHeight} up to {DesktopProtocol.MaxFitWidth} × {DesktopProtocol.MaxFitHeight}.");
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

/// <summary>When the agent desktop's picture is shown 1:1 instead of scaled to the tab.</summary>
internal static class PictureFit
{
    /// <summary>Device pixels the area may be larger than the picture and it is still shown 1:1 (a fitted size is rounded down to even).</summary>
    public const double Slack = 2;

    /// <summary>The picture (device's pixels) fits the area (this PC's device pixels) with at most <see cref="Slack"/> to spare each way.</summary>
    public static bool ShowsNative(int pictureWidth, int pictureHeight, double areaWidth, double areaHeight)
    {
        if (pictureWidth < 1 || pictureHeight < 1) return false;
        var spareX = areaWidth - pictureWidth;
        var spareY = areaHeight - pictureHeight;
        // Half a pixel short is rounding of the area's size, not a picture larger than the tab.
        return spareX >= -0.5 && spareY >= -0.5 && spareX <= Slack && spareY <= Slack;
    }
}
