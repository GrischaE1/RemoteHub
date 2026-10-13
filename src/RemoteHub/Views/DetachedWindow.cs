using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RemoteHub.Rdp;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;

namespace RemoteHub.Views;

/// <summary>Stand-alone window hosting one session. Closing it docks the session back into the main window.</summary>
public sealed class DetachedWindow : FluentWindow
{
    private readonly FrameworkElement _view;
    private readonly Grid _root;
    private bool _forceClose;

    public event Action? DockRequested;

    public DetachedWindow(SessionBase session, FrameworkElement view, IEnumerable<(SymbolRegular Icon, string? Text, string Tip, Action Action)> tools)
    {
        _view = view;
        DataContext = session;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.None;
        AppChrome.UseAccentBorder(this);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetBinding(TitleProperty, nameof(SessionBase.Title));

        var titleBar = new TitleBar
        {
            Icon = new ImageIcon(),
        };
        AppIcon.Apply((ImageIcon)titleBar.Icon, 18);
        AppIcon.ApplyTo(this);
        titleBar.SetBinding(TitleBar.TitleProperty, nameof(SessionBase.Title));

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 4) };
        foreach (var t in tools) toolbar.Children.Add(Tool(t.Icon, t.Text, t.Tip, t.Action));
        toolbar.Children.Add(Tool(SymbolRegular.WindowArrowUp24, "Dock", "Dock back into the main window", () => DockRequested?.Invoke()));

        _root = new Grid();
        _root.SetResourceReference(Panel.BackgroundProperty, "ChromeBackgroundBrush");
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.Children.Add(titleBar);
        Grid.SetRow(toolbar, 1);
        _root.Children.Add(toolbar);
        Grid.SetRow(view, 2);
        _root.Children.Add(view);
        Content = _root;

        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionBase.Title)) Title = session.Title;
        };
        Activated += (_, _) => Dispatcher.BeginInvoke(session.FocusSession, System.Windows.Threading.DispatcherPriority.Input);
    }

    private static Button Tool(SymbolRegular icon, string? text, string tip, Action action)
    {
        var b = new Button { Icon = new SymbolIcon(icon), ToolTip = tip, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(8, 5, 8, 5) };
        if (text is not null) b.Content = text;
        b.Click += (_, _) => action();
        return b;
    }

    /// <summary>Removes the session view so it can be re-parented elsewhere.</summary>
    public void ReleaseContent()
    {
        _root.Children.Remove(_view);
        Grid.SetRow(_view, 0);
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            e.Cancel = true;
            // Docking closes this window, which is not allowed from inside OnClosing.
            Dispatcher.BeginInvoke(() => DockRequested?.Invoke());
        }
        base.OnClosing(e);
    }
}
