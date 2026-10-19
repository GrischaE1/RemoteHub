using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Views;

/// <summary>
/// The welcome tour: a few pages on what RemoteHub does, once on the first start after a new install, or from
/// Help &amp; about. Pages for features IT policy turns off are left out.
/// </summary>
public sealed class WelcomeWindow : FluentWindow
{
    private sealed record Page(SymbolRegular Icon, string Title, string Text);

    private readonly List<Page> _pages;
    private readonly StackPanel _body = new() { Margin = new Thickness(24, 4, 24, 22) };
    private readonly ContentControl _page = new() { MinHeight = 170 };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _back = new() { Content = "Back", MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _next = new() { Content = "Next", Appearance = ControlAppearance.Primary, IsDefault = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
    private int _index;

    public WelcomeWindow()
    {
        _pages = Pages();
        Title = "Welcome to RemoteHub";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        ShowInTaskbar = false;

        var root = new StackPanel();
        root.Children.Add(new TitleBar { Title = "Welcome to RemoteHub", ShowMaximize = false, ShowMinimize = false });
        root.Children.Add(_body);
        Content = root;

        _body.Children.Add(_page);
        _body.Children.Add(_dots);
        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        var skip = new Button { Content = "Skip", IsCancel = true, MinWidth = 96 };
        skip.Click += (_, _) => Close();
        DockPanel.SetDock(skip, Dock.Left);
        DockPanel.SetDock(_next, Dock.Right);
        DockPanel.SetDock(_back, Dock.Right);
        buttons.Children.Add(skip);
        buttons.Children.Add(_next);
        buttons.Children.Add(_back);
        _body.Children.Add(buttons);
        _back.Click += (_, _) => Show(_index - 1);
        _next.Click += (_, _) => { if (_index == _pages.Count - 1) Close(); else Show(_index + 1); };
        Show(0);
    }

    private static List<Page> Pages()
    {
        var pages = new List<Page>
        {
            new(SymbolRegular.Desktop24, "Welcome to RemoteHub",
                "Remote desktop sessions, a PowerShell console, file transfer, registry, services and processes – organised in " +
                "folders with inherited settings and credentials. This short tour shows where to start."),
            Features.IsAllowed(Feature.ConnectionEditing)
                ? new(SymbolRegular.Add24, "Add your connections",
                    "Use New in the navigation to add a connection or a folder" +
                    (Features.IsAllowed(Feature.RdpFileImport) ? ", or drop .rdp files anywhere in the window to import them" : "") +
                    ". Double-click a connection to open it in a tab; a tab can also run in its own window.")
                : new(SymbolRegular.Desktop24, "Your connections",
                    "Your organisation provides the connections in the navigation. Double-click one to open it in a tab; a tab can " +
                    "also run in its own window."),
        };
        if (Features.IsAllowed(Feature.Agent))
            pages.Add(new(SymbolRegular.PlugConnected24, "The RemoteHub Agent",
                "With the agent on a device, RemoteHub reaches it without RDP or WinRM: its desktop, PowerShell, files, registry, " +
                "services and processes." +
                (Features.IsAllowed(Feature.AgentInstall) ? " Right-click a connection → Install RemoteHub Agent… to set it up." : "")));
        if (Features.IsAllowed(Feature.Notes))
            pages.Add(new(SymbolRegular.Notebook24, "Notes",
                "Keep scripts, tables, images and how-tos in notebooks on the Notes view of the rail, with search across all pages."));
        pages.Add(new(SymbolRegular.ShieldLock24, "Keep it safe",
            "Settings (the gear, or Ctrl+,) has startup protection – a password or Windows Hello every time RemoteHub starts – " +
            "saved credentials, updates and backups. Settings → Help & about shows this tour again."));
        return pages;
    }

    private void Show(int index)
    {
        _index = Math.Clamp(index, 0, _pages.Count - 1);
        var p = _pages[_index];

        var page = new StackPanel();
        var icon = new SymbolIcon(p.Icon) { FontSize = 40, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 12) };
        icon.SetResourceReference(IconElement.ForegroundProperty, "SystemAccentColorPrimaryBrush");
        page.Children.Add(icon);
        page.Children.Add(new TextBlock { Text = p.Title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        var text = new TextBlock { Text = p.Text, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        page.Children.Add(text);
        _page.Content = page;

        _dots.Children.Clear();
        for (var i = 0; i < _pages.Count; i++)
        {
            var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(3, 0, 3, 0) };
            dot.SetResourceReference(Shape.FillProperty, i == _index ? "SystemAccentColorPrimaryBrush" : "ControlStrongFillColorDisabledBrush");
            _dots.Children.Add(dot);
        }
        // The dots have no automation peer of their own, so the buttons tell a screen reader where the tour is.
        var where = $"Page {_index + 1} of {_pages.Count}: {p.Title}";
        System.Windows.Automation.AutomationProperties.SetHelpText(_next, where);
        System.Windows.Automation.AutomationProperties.SetHelpText(_back, where);

        _back.Visibility = _index == 0 ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = _index == _pages.Count - 1 ? "Get started" : "Next";
    }
}
