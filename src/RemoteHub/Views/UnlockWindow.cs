using System.Windows;
using System.Windows.Controls;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using Image = System.Windows.Controls.Image;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Views;

/// <summary>
/// Shown at start-up when the startup protection is on – and when RemoteHub locked itself after a while without input
/// (<paramref name="whileRunning"/>: the sessions keep running behind it; closing it exits RemoteHub): Windows Hello (asked right away
/// when set up) or the password.
/// </summary>
public sealed class UnlockWindow : FluentWindow
{
    private readonly PasswordBox _password = new() { PlaceholderText = "Startup password" };
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _unlock = new() { Content = "Unlock", Appearance = ControlAppearance.Primary, IsDefault = true, MinWidth = 110 };
    private readonly Button _hello = new() { Content = "Windows Hello", Icon = new SymbolIcon(SymbolRegular.Fingerprint24), Margin = new Thickness(0, 0, 8, 0) };
    private int _failures;

    /// <summary>Windows Hello is set up and this PC's policy allows it (AllowWindowsHello, docs/admx-design.md §6.3).</summary>
    private static bool HelloAllowed => AppLock.HasHello && AppPolicy.AllowWindowsHello;

    public UnlockWindow(bool whileRunning = false)
    {
        Title = "RemoteHub – locked";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        Topmost = false;

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var logo = new Image { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        AppIcon.Apply(logo, 48);
        DockPanel.SetDock(logo, Dock.Left);
        head.Children.Add(logo);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = "RemoteHub is locked", FontSize = 20, FontWeight = FontWeights.SemiBold });
        var sub = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = (whileRunning ? "Locked after a while without input – your sessions keep running. " : "") +
            (HelloAllowed
                ? "Unlock with Windows Hello (PIN, fingerprint or face) or the startup password."
                : "Enter the startup password to open your connections.") };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        names.Children.Add(sub);
        head.Children.Add(names);

        _unlock.Click += (_, _) => _ = UnlockWithPassword();
        _hello.Click += (_, _) => _ = UnlockWithHello();
        var exit = new Button { Content = whileRunning ? "Exit RemoteHub" : "Exit", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0),
            ToolTip = whileRunning ? "Disconnects all sessions and closes RemoteHub" : null };
        exit.Click += (_, _) => DialogResult = false;
        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        if (HelloAllowed) { DockPanel.SetDock(_hello, Dock.Left); buttons.Children.Add(_hello); }
        DockPanel.SetDock(exit, Dock.Right);
        DockPanel.SetDock(_unlock, Dock.Right);
        buttons.Children.Add(exit);
        buttons.Children.Add(_unlock);

        var body = new StackPanel { Margin = new Thickness(24, 4, 24, 22) };
        body.Children.Add(head);
        body.Children.Add(_password);
        body.Children.Add(_error);
        body.Children.Add(buttons);

        var root = new StackPanel();
        root.Children.Add(new TitleBar { Title = "RemoteHub", ShowMaximize = false, ShowMinimize = true });
        root.Children.Add(body);
        Content = root;

        Loaded += (_, _) => _password.Focus();
        ContentRendered += (_, _) => { if (HelloAllowed) _ = UnlockWithHello(); };
    }

    private void Busy(bool on)
    {
        _unlock.IsEnabled = _hello.IsEnabled = _password.IsEnabled = !on;
        Cursor = on ? System.Windows.Input.Cursors.Wait : null;
    }

    private async Task UnlockWithPassword()
    {
        var pw = _password.Password ?? "";
        if (pw.Length == 0) { _error.Text = "Enter the startup password."; return; }
        Busy(true);
        var ok = await Task.Run(() => AppLock.TryUnlock(pw)); // PBKDF2 takes a moment
        Busy(false);
        if (ok)
        {
            DiagLog.Write("Startup protection: unlocked with the password");
            Data.AuditLog.Write(Data.AuditEvents.AppUnlocked, null, null, "With the startup password");
            DialogResult = true;
            return;
        }
        _failures++;
        DiagLog.Write($"Startup protection: wrong password (attempt {_failures})");
        // Written to the database once it's unlocked (a failed attempt before a successful one is kept).
        Data.AuditLog.Write(Data.AuditEvents.AppUnlockFailed, null, null, $"Wrong startup password (attempt {_failures})");
        _error.Text = "Wrong password.";
        _password.Password = "";
        _password.Focus();
    }

    private async Task UnlockWithHello()
    {
        Busy(true);
        _error.Text = "";
        Activate();
        var error = await AppLock.TryUnlockWithHello();
        Busy(false);
        if (error is null)
        {
            DiagLog.Write("Startup protection: unlocked with Windows Hello");
            Data.AuditLog.Write(Data.AuditEvents.AppUnlocked, null, null, "With Windows Hello");
            DialogResult = true;
            return;
        }
        DiagLog.Write("Startup protection: Windows Hello not used – " + error);
        _error.Text = error;
        Activate();
        _password.Focus();
    }
}
