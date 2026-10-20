using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RemoteHub.Rdp;
using RemoteHub.Remoting;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Views;

/// <summary>A tab view whose content area (everything below the header) is what a recording shows.</summary>
public interface IRecordableView
{
    FrameworkElement RecordingArea { get; }
}

/// <summary>A view with its own filter box: Ctrl+F with the focus inside it goes there instead of to the navigation search.</summary>
public interface IFilterableView
{
    void FocusFilter();
}

/// <summary>Header and "not connected" overlay shared by the remote tool tabs (file transfer, registry).</summary>
internal static class RemoteToolChrome
{
    /// <summary>Header bar: colour stripe, icon, connection name and what the tab is.</summary>
    public static Border Header(SessionBase session, SymbolRegular icon, string kind, Border stripe, string? where = null)
    {
        var header = new Border { CornerRadius = new CornerRadius(6, 6, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        header.SetResourceReference(Border.BackgroundProperty, "SessionHeaderBrush");
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(stripe);
        left.Children.Add(new SymbolIcon(icon) { FontSize = 15, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetBinding(TextBlock.TextProperty, nameof(SessionBase.Title));
        left.Children.Add(title);
        var sub = new TextBlock { Text = $"  ·  {kind}  ·  " + (where ?? session.Connection.HostWithRoute), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        left.Children.Add(sub);
        left.Children.Add(TransportBadge());
        left.Children.Add(RecordingBadge());
        header.Child = left;
        return header;
    }

    /// <summary>
    /// Small badge after the host: how the tab reaches the device – "RemoteHub Agent" or "PowerShell remoting (HTTPS)".
    /// Bound to the session's <see cref="SessionBase.Transport"/> (DataContext), so it follows a reconnect.
    /// </summary>
    public static Border TransportBadge()
    {
        var icon = new SymbolIcon(SymbolRegular.PlugConnected24) { FontSize = 12, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        text.SetBinding(TextBlock.TextProperty, nameof(SessionBase.Transport));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(text);
        var badge = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 7, 2),
            Margin = new Thickness(10, 0, 0, 0),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "How RemoteHub reaches this device",
        };
        badge.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        badge.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
        badge.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(SessionBase.Transport)) { Converter = new BoolToVisibility() });
        return badge;
    }

    /// <summary>Red "REC 01:23" badge while the tab is recorded (bound to the session; it is part of the recording, too).</summary>
    public static Border RecordingBadge()
    {
        var text = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        text.SetBinding(TextBlock.TextProperty, nameof(SessionBase.RecordingText));
        var badge = new Border
        {
            Child = text,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 1, 7, 2),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = ColorTags.Frozen(System.Windows.Media.Color.FromRgb(0xE5, 0x48, 0x4D)),
            ToolTip = "This tab is being recorded to a video file",
        };
        badge.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(SessionBase.IsRecording)) { Converter = new BoolToVisibility() });
        return badge;
    }

    /// <summary>Covers the remote side while not connected: status text, Connect and Close tab.</summary>
    public static Border Overlay(Action reconnect, Action close)
    {
        var status = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 460, Margin = new Thickness(0, 12, 0, 18) };
        status.SetBinding(TextBlock.TextProperty, nameof(SessionBase.StatusText));
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var connect = new Button { Content = "Connect", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.PlugConnected24), Margin = new Thickness(0, 0, 8, 0) };
        connect.Click += (_, _) => reconnect();
        var closeBtn = new Button { Content = "Close tab", Icon = new SymbolIcon(SymbolRegular.Dismiss24) };
        closeBtn.Click += (_, _) => close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(connect);
        buttons.Children.Add(closeBtn);
        var card = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var icon = new SymbolIcon(SymbolRegular.Server24) { FontSize = 40, Margin = new Thickness(0, 0, 0, 8) };
        icon.SetBinding(Control.ForegroundProperty, nameof(SessionBase.AccentBrush));
        card.Children.Add(icon);
        card.Children.Add(status);
        card.Children.Add(buttons);
        var overlay = new Border { Child = card, CornerRadius = new CornerRadius(6) };
        overlay.SetResourceReference(Border.BackgroundProperty, "SessionAreaBrush");
        overlay.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(SessionBase.IsConnected)) { Converter = new BoolToVisibility { Invert = true } });
        return overlay;
    }

    public static System.Windows.Controls.MenuItem Item(string header, SymbolRegular icon, Action action, bool enabled = true, string? gesture = null)
    {
        var mi = new System.Windows.Controls.MenuItem { Header = header, Icon = new SymbolIcon(icon), IsEnabled = enabled, InputGestureText = gesture ?? "" };
        mi.Click += (_, _) => action();
        return mi;
    }

    /// <summary>A refresh interval for people: "15 s", "2 min".</summary>
    public static string Every(int seconds) => seconds % 60 == 0 && seconds >= 60 ? $"{seconds / 60} min" : $"{seconds} s";

    public static Button Tool(SymbolRegular icon, string tip, Action a)
    {
        var b = new Button { Icon = new SymbolIcon(icon), ToolTip = tip, Padding = new Thickness(8, 5, 8, 5), MinWidth = 0 };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => a();
        return b;
    }
}
