using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RemoteHub.Rdp;
using RemoteHub.Remoting;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Views;

/// <summary>Header and "not connected" overlay shared by the remote tool tabs (file transfer, registry).</summary>
internal static class RemoteToolChrome
{
    /// <summary>Header bar: colour stripe, icon, connection name and what the tab is.</summary>
    public static Border Header(RemoteToolSession session, SymbolRegular icon, string kind, Border stripe)
    {
        var header = new Border { CornerRadius = new CornerRadius(6, 6, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        header.SetResourceReference(Border.BackgroundProperty, "SessionHeaderBrush");
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(stripe);
        left.Children.Add(new SymbolIcon(icon) { FontSize = 15, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetBinding(TextBlock.TextProperty, nameof(SessionBase.Title));
        left.Children.Add(title);
        var sub = new TextBlock { Text = $"  ·  {kind}  ·  " + session.Connection.Host, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        left.Children.Add(sub);
        header.Child = left;
        return header;
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
