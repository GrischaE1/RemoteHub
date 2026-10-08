using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Shapes;
using RemoteHub.Rdp;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Views;

/// <summary>
/// Visual container of one session: a coloured frame with a header strip (name, address, keyboard-focus indicator),
/// the hosted ActiveX control, and a WPF status overlay (shown instead of the control while idle/disconnected —
/// WPF cannot draw on top of an HWND). The same instance is moved between the main window and detached windows.
/// </summary>
public sealed class SessionView : Border
{
    private readonly RdpSession _session;
    private readonly Border _headerStripe = new() { Width = 4, Height = 18, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _focusText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly SymbolIcon _focusIcon = new(SymbolRegular.Keyboard24) { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };

    public SessionView(RdpSession session, Action reconnect, Action close)
    {
        _session = session;
        Session = session;
        DataContext = session;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(2);
        Padding = new Thickness(0);
        SetResourceReference(BackgroundProperty, "SessionAreaBrush");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Child = root;

        // ---- header strip
        var header = new Border { CornerRadius = new CornerRadius(6, 6, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        header.SetResourceReference(Border.BackgroundProperty, "SessionHeaderBrush");
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetBinding(TextBlock.TextProperty, nameof(RdpSession.Title));
        var addr = new TextBlock { Text = "  ·  " + session.Connection.Address, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        addr.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        var focus = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        focus.Children.Add(_focusIcon);
        focus.Children.Add(_focusText);
        var headerRow = new DockPanel();
        DockPanel.SetDock(focus, Dock.Right);
        headerRow.Children.Add(focus);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_headerStripe);
        left.Children.Add(title);
        left.Children.Add(addr);
        headerRow.Children.Add(left);
        header.Child = headerRow;
        root.Children.Add(header);

        // ---- content: RDP control + overlay
        var content = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        Host = new WindowsFormsHost { Child = session.Control, Background = Brushes.Black };
        Host.SetBinding(VisibilityProperty, new Binding(nameof(RdpSession.ShowsOverlay)) { Converter = new BoolToVisibility { Invert = true, UseHidden = true } });
        content.Children.Add(Host);
        content.Children.Add(BuildOverlay(session, reconnect, close));

        session.PropertyChanged += OnSessionChanged;
        UpdateFrame();
    }

    public RdpSession Session { get; }
    public WindowsFormsHost Host { get; }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RdpSession.HasKeyboardFocus) or nameof(RdpSession.AccentBrush) or nameof(RdpSession.State))
            UpdateFrame();
    }

    /// <summary>Bright frame while keystrokes go into the session; muted otherwise.</summary>
    private void UpdateFrame()
    {
        var accent = _session.AccentBrush;
        _headerStripe.Background = accent;
        if (_session.HasKeyboardFocus)
        {
            BorderBrush = accent;
            _focusIcon.Foreground = accent;
            _focusText.Foreground = accent;
            _focusText.Text = "Keyboard → remote session";
        }
        else
        {
            SetResourceReference(BorderBrushProperty, "ChromeStrokeBrush");
            _focusIcon.SetResourceReference(Control.ForegroundProperty, "TextFillColorTertiaryBrush");
            _focusText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            _focusText.Text = _session.IsConnected ? "Click into the session to type" : "";
        }
        _focusIcon.Visibility = _focusText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Border BuildOverlay(RdpSession session, Action reconnect, Action close)
    {
        var status = new TextBlock
        {
            FontSize = 15, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            MaxWidth = 520, Margin = new Thickness(0, 12, 0, 18),
        };
        status.SetBinding(TextBlock.TextProperty, nameof(RdpSession.StatusText));
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var title = new TextBlock { FontSize = 22, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
        title.SetBinding(TextBlock.TextProperty, nameof(RdpSession.Title));

        var address = new TextBlock { FontSize = 13, TextAlignment = TextAlignment.Center, Text = session.Connection.Address, Margin = new Thickness(0, 4, 0, 0) };
        address.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

        var credInfo = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 560, Margin = new Thickness(0, -6, 0, 18) };
        credInfo.SetBinding(TextBlock.TextProperty, nameof(RdpSession.CredentialInfo));
        credInfo.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

        var reconnectBtn = new Button
        {
            Content = "Connect", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.PlugConnected24),
            Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 8, 0),
        };
        reconnectBtn.Click += (_, _) => reconnect();
        var closeBtn = new Button { Content = "Close tab", Icon = new SymbolIcon(SymbolRegular.Dismiss24), Padding = new Thickness(18, 8, 18, 8) };
        closeBtn.Click += (_, _) => close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(reconnectBtn);
        buttons.Children.Add(closeBtn);

        var icon = new SymbolIcon(SymbolRegular.Desktop24) { FontSize = 48, Margin = new Thickness(0, 0, 0, 12) };
        icon.SetBinding(Control.ForegroundProperty, nameof(RdpSession.AccentBrush));

        var card = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        card.Children.Add(icon);
        card.Children.Add(title);
        card.Children.Add(address);
        card.Children.Add(status);
        card.Children.Add(credInfo);
        card.Children.Add(buttons);

        var overlay = new Border { Child = card };
        overlay.SetBinding(VisibilityProperty, new Binding(nameof(RdpSession.ShowsOverlay)) { Converter = new BoolToVisibility() });
        return overlay;
    }
}
