using System.IO;
using System.Windows;
using System.Windows.Controls;
using RemoteHub.Models;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Views;

/// <summary>Menu → Startup protection: turn the startup password (and Windows Hello) on, change it, or turn it off.</summary>
public sealed class StartupProtectionWindow : FluentWindow
{
    private readonly Func<AppData> _data;
    private readonly StackPanel _body = new() { Margin = new Thickness(24, 4, 24, 22) };
    private bool? _helloAvailable;

    private static string LegacyFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpManager");

    public StartupProtectionWindow(Func<AppData> data)
    {
        _data = data;
        Title = "Startup protection";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        ShowInTaskbar = false;

        var root = new StackPanel();
        root.Children.Add(new TitleBar { Title = "Startup protection", ShowMaximize = false, ShowMinimize = false });
        root.Children.Add(_body);
        Content = root;
        Build();
        Loaded += async (_, _) => { _helloAvailable = await WindowsHello.IsAvailableAsync(); Build(); };
    }

    // ------------------------------------------------------------------ layout helpers

    private static TextBlock Text(string t, bool muted = false, double bottom = 10, double size = 14)
    {
        var tb = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, bottom), FontSize = size };
        if (muted) tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return tb;
    }

    private static TextBlock Header(string t) => new() { Text = t, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 8) };

    private static Border Note(string t)
    {
        var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 4, 0, 10), BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        b.Child = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        return b;
    }

    private static PasswordBox Pw(string placeholder) => new() { PlaceholderText = placeholder, Margin = new Thickness(0, 0, 0, 8) };

    private TextBlock Error() => new() { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };

    private static string? CheckNew(string a, string b) =>
        a.Length < 8 ? "Use at least 8 characters." : a != b ? "The passwords do not match." : null;

    private async Task Run(Button b, TextBlock error, Func<Task<string?>> action)
    {
        b.IsEnabled = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        try { error.Text = await action() ?? ""; if (error.Text.Length == 0) Build(); }
        catch (Exception ex) { error.Text = ex.Message; }
        finally { b.IsEnabled = true; Cursor = null; }
    }

    // ------------------------------------------------------------------ content

    private void Build()
    {
        _body.Children.Clear();
        _body.Children.Add(Text("Ask for a password – or Windows Hello – every time RemoteHub starts. Until then the connections, " +
            "credentials and the remembered password of a shared settings file stay encrypted on this PC, also for other programs " +
            "running under your Windows account.", muted: true));
        if (AppLock.IsEnabled) BuildOn(); else BuildOff();
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        close.Click += (_, _) => Close();
        _body.Children.Add(close);
    }

    private void BuildOff()
    {
        if (AppPolicy.StartupProtection == "Required")
            _body.Children.Add(Note("Your organisation requires startup protection on this PC. Set a startup password to open RemoteHub."));
        _body.Children.Add(Header("Turn on"));
        var p1 = Pw("New startup password (at least 8 characters)");
        var p2 = Pw("Repeat password");
        _body.Children.Add(p1);
        _body.Children.Add(p2);
        var hello = new CheckBox
        {
            Content = "Also unlock with Windows Hello (PIN, fingerprint or face)",
            IsChecked = _helloAvailable == true && AppPolicy.AllowWindowsHello, IsEnabled = _helloAvailable == true, Margin = new Thickness(0, 2, 0, 4),
        };
        if (!AppPolicy.AllowWindowsHello) PolicyLock.TurnedOff(hello); // policy AllowWindowsHello = 0 (docs/admx-design.md §6.3)
        _body.Children.Add(hello);
        if (_helloAvailable == false) _body.Children.Add(Text("Windows Hello isn't set up on this PC (Settings → Accounts → Sign-in options).", muted: true, size: 12));
        CheckBox? legacy = null;
        if (Directory.Exists(LegacyFolder))
        {
            legacy = new CheckBox
            {
                Content = new TextBlock { Text = $"Delete the old, unprotected copy of the RDP Manager settings ({LegacyFolder})", TextWrapping = TextWrapping.Wrap },
                Margin = new Thickness(0, 4, 0, 4),
            };
            _body.Children.Add(legacy);
        }
        _body.Children.Add(Note("Keep the password safe. Without it – and without Windows Hello on this PC – the settings stored on this PC " +
            "can't be opened again, not even by RemoteHub. Export a backup first (Menu → Export backup) to be safe."));
        var error = Error();
        _body.Children.Add(error);
        var on = new Button { Content = "Turn on", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.LockClosed24), MinWidth = 120 };
        on.Click += (_, _) => _ = Run(on, error, async () =>
        {
            if (CheckNew(p1.Password ?? "", p2.Password ?? "") is { } bad) return bad;
            var pw = p1.Password!;
            await SettingsStore.Reprotect(_data(), () => Task.Run(() => AppLock.Enable(pw)));
            DiagLog.Write("Startup protection: turned on");
            string? warn = null;
            if (hello.IsChecked == true && await AppLock.AddHello() is { } helloError) warn = helloError;
            else if (hello.IsChecked == true) DiagLog.Write("Startup protection: Windows Hello set up");
            if (legacy?.IsChecked == true)
                try { Directory.Delete(LegacyFolder, true); } catch (Exception ex) { warn = (warn is null ? "" : warn + "\n") + "The old folder couldn't be deleted: " + ex.Message; }
            if (warn is not null) await Dialogs.Info(this, "Startup protection", "The startup password is on.\n\n" + warn);
            return null;
        });
        _body.Children.Add(on);
        if (AppPolicy.StartupProtection == "Off")
        {
            // Off by policy (kiosk and jump-server PCs): it can't be turned on here.
            PolicyLock.TurnedOff(on, p1, p2, hello);
            _body.Children.Add(Text("Startup protection is turned off by your organisation (Settings → Policies).", muted: true, size: 12));
        }
    }

    private void BuildOn()
    {
        _body.Children.Add(Note($"On – {AppLock.Describe().ToLowerInvariant()}."));
        _body.Children.Add(Text("Enter the current password to change anything:", bottom: 6));
        var current = Pw("Current startup password");
        _body.Children.Add(current);
        var error = Error();
        _body.Children.Add(error);
        // PBKDF2 with 600,000 rounds takes a moment: not on the UI thread.
        async Task<string?> Verify()
        {
            var pw = current.Password ?? "";
            return await Task.Run(() => AppLock.VerifyPassword(pw)) ? null : "The current password is wrong.";
        }

        // Windows Hello
        _body.Children.Add(Header("Windows Hello"));
        if (AppLock.HasHello)
        {
            var remove = new Button { Content = "Stop using Windows Hello", Icon = new SymbolIcon(SymbolRegular.Fingerprint24) };
            remove.Click += (_, _) => _ = Run(remove, error, async () =>
            {
                if (await Verify() is { } bad) return bad;
                await AppLock.RemoveHello();
                DiagLog.Write("Startup protection: Windows Hello removed");
                return null;
            });
            _body.Children.Add(remove);
        }
        else
        {
            var add = new Button { Content = "Set up Windows Hello", Icon = new SymbolIcon(SymbolRegular.Fingerprint24), IsEnabled = _helloAvailable == true };
            if (!AppPolicy.AllowWindowsHello) PolicyLock.TurnedOff(add);
            add.Click += (_, _) => _ = Run(add, error, async () =>
            {
                if (await Verify() is { } bad) return bad;
                var e = await AppLock.AddHello();
                if (e is null) DiagLog.Write("Startup protection: Windows Hello set up");
                return e;
            });
            _body.Children.Add(add);
            if (_helloAvailable == false) _body.Children.Add(Text("Windows Hello isn't set up on this PC.", muted: true, size: 12));
        }

        // Auto-lock
        _body.Children.Add(Header("Lock when you're away"));
        _body.Children.Add(Text("Locks RemoteHub after a while without mouse or keyboard input on this PC. Sessions keep running; " +
                                "unlocking asks for the password or Windows Hello.", muted: true, bottom: 6));
        var lockAfter = new System.Windows.Controls.ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (minutes, label) in new[] { (0, "Never"), (1, "After 1 minute"), (5, "After 5 minutes"), (15, "After 15 minutes"), (30, "After 30 minutes"), (60, "After 1 hour") })
            lockAfter.Items.Add(new ComboBoxItem { Content = label, Tag = minutes });
        // Fixed by policy AutoLockMinutes (§6.3): shown, greyed out, not stored.
        var currentMinutes = AppPolicy.AutoLockMinutes ?? _data().Ui.AutoLockMinutes;
        if (!lockAfter.Items.OfType<ComboBoxItem>().Any(i => (int)i.Tag == currentMinutes))
            lockAfter.Items.Add(new ComboBoxItem { Content = currentMinutes == 1 ? "After 1 minute" : $"After {currentMinutes} minutes", Tag = currentMinutes });
        lockAfter.SelectedItem = lockAfter.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == currentMinutes) ?? lockAfter.Items[0];
        if (AppPolicy.AutoLockMinutes is not null) PolicyLock.Apply(lockAfter);
        lockAfter.SelectionChanged += (_, _) =>
        {
            if (lockAfter.SelectedItem is not ComboBoxItem { Tag: int minutes }) return;
            var data = _data();
            data.Ui.AutoLockMinutes = minutes;
            try { SettingsStore.Save(data); } catch (Exception ex) { error.Text = ex.Message; }
            DiagLog.Write($"Startup protection: auto-lock {(minutes == 0 ? "off" : $"after {minutes} min")}");
        };
        _body.Children.Add(lockAfter);

        // Change password
        _body.Children.Add(Header("Change password"));
        var n1 = Pw("New startup password");
        var n2 = Pw("Repeat new password");
        _body.Children.Add(n1);
        _body.Children.Add(n2);
        var change = new Button { Content = "Change password", Icon = new SymbolIcon(SymbolRegular.Key24) };
        change.Click += (_, _) => _ = Run(change, error, async () =>
        {
            if (await Verify() is { } bad) return bad;
            if (CheckNew(n1.Password ?? "", n2.Password ?? "") is { } badNew) return badNew;
            var pw = n1.Password!;
            var hadHello = AppLock.HasHello;
            await SettingsStore.Reprotect(_data(), () => AppLock.ChangePassword(pw));
            DiagLog.Write("Startup protection: password changed");
            // Windows Hello unlocked the old key: set it up again for the new one.
            var helloError = hadHello && AppPolicy.AllowWindowsHello ? await AppLock.AddHello() : null; // not again where the policy blocks it
            await Dialogs.Info(this, "Startup protection", "The startup password was changed." +
                (helloError is null ? "" : $"\n\nWindows Hello has to be set up again: {helloError}"));
            return null;
        });
        _body.Children.Add(change);

        // Turn off
        _body.Children.Add(Header("Turn off"));
        _body.Children.Add(Text("The settings stay encrypted for your Windows account, but RemoteHub opens without asking.", muted: true));
        var off = new Button { Content = "Turn off startup protection", Icon = new SymbolIcon(SymbolRegular.LockOpen24) };
        off.Click += (_, _) => _ = Run(off, error, async () =>
        {
            if (await Verify() is { } bad) return bad;
            if (!await Dialogs.Confirm(this, "Turn off startup protection", "RemoteHub will open without a password or Windows Hello on this PC. Turn it off?", "Turn off")) return "";
            await SettingsStore.Reprotect(_data(), AppLock.Disable);
            DiagLog.Write("Startup protection: turned off");
            return null;
        });
        _body.Children.Add(off);
        if (AppPolicy.StartupProtection == "Required") PolicyLock.Apply(off); // required by policy: it can't be turned off
    }
}
