using System.Windows;
using System.Windows.Controls;
using RemoteHub.Models;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>Choose where the settings file is stored: this PC (DPAPI) or a shared folder such as OneDrive (password).</summary>
public sealed class SettingsLocationWindow : FluentWindow
{
    private readonly AppData _current;
    private readonly RadioButton _local = new() { GroupName = "loc", Content = "This PC only", FontWeight = FontWeights.SemiBold };
    private readonly RadioButton _shared = new() { GroupName = "loc", Content = "Shared folder – e.g. OneDrive or a network share", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBox _folder = new() { PlaceholderText = @"e.g. C:\Users\me\OneDrive - Company\RemoteHub", Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

    /// <summary>Set when an existing shared file was opened – its data replaces the current data.</summary>
    public AppData? OpenedData { get; private set; }

    public SettingsLocationWindow(AppData current)
    {
        _current = current;
        Title = "Settings location";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;

        var body = new StackPanel { Margin = new Thickness(24, 4, 24, 20) };
        body.Children.Add(Caption(SettingsStore.Describe(), bold: true));

        if (SettingsStore.IsPolicyManaged)
        {
            body.Children.Add(Caption($"The settings location is managed by your organization:\n{SettingsStore.PolicyFolder}", margin: 12));
            body.Children.Add(Buttons(null));
            Build(body);
            return;
        }

        body.Children.Add(_local);
        body.Children.Add(Caption("Encrypted for your Windows account (DPAPI). No password, but only you on this PC can read it.", indent: true));

        body.Children.Add(_shared);
        body.Children.Add(Caption(
            "Encrypted with a password (AES-256). Use it to sync between your PCs or to share connections with other admins. " +
            "The password is asked once per PC and then remembered there. Window layout stays per PC.", indent: true));

        var browse = new Button { Content = "Browse…", Margin = new Thickness(0, 0, 8, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for the shared settings file" };
            if (dlg.ShowDialog(this) == true) { _folder.Text = dlg.FolderName; _shared.IsChecked = true; }
        };
        var row = new DockPanel { Margin = new Thickness(28, 8, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        if (SettingsStore.SuggestedOneDriveFolder() is { } od)
        {
            var oneDrive = new Button { Content = "Use OneDrive" };
            oneDrive.Click += (_, _) => { _folder.Text = od; _shared.IsChecked = true; };
            DockPanel.SetDock(oneDrive, Dock.Right);
            row.Children.Add(oneDrive);
        }
        row.Children.Add(browse);
        row.Children.Add(_folder);
        body.Children.Add(row);
        body.Children.Add(Caption("If two PCs change the file at the same time, RemoteHub notices it and asks which version to keep.", indent: true, margin: 6));

        var forget = new Button { Content = "Forget remembered passwords on this PC", Margin = new Thickness(0, 16, 0, 0), Appearance = ControlAppearance.Secondary };
        forget.Click += (_, _) => { SettingsStore.ForgetRememberedPasswords(); _error.Text = "Done – the password will be asked again at the next start."; };
        body.Children.Add(forget);
        body.Children.Add(_error);
        body.Children.Add(Buttons(Apply));

        _folder.Text = SettingsStore.SharedFolder ?? "";
        _shared.IsChecked = SettingsStore.IsShared;
        _local.IsChecked = !SettingsStore.IsShared;
        _folder.TextChanged += (_, _) => { if (_folder.Text.Length > 0) _shared.IsChecked = true; };
        Build(body);
    }

    private void Build(StackPanel body)
    {
        var root = new StackPanel();
        root.Children.Add(new TitleBar { Title = Title, ShowMaximize = false, ShowMinimize = false });
        root.Children.Add(body);
        Content = root;
    }

    private static TextBlock Caption(string text, bool indent = false, bool bold = false, double margin = 2)
    {
        var t = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(indent ? 28 : 0, margin, 0, bold ? 14 : 0),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        };
        if (!bold) t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    private StackPanel Buttons(Action? apply)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        if (apply is not null)
        {
            var ok = new Button { Content = "Apply", Appearance = ControlAppearance.Primary, IsDefault = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (_, _) => apply();
            panel.Children.Add(ok);
        }
        panel.Children.Add(new Button { Content = apply is null ? "Close" : "Cancel", IsCancel = true, MinWidth = 100 });
        return panel;
    }

    private async void Apply()
    {
        _error.Text = "";
        try
        {
            if (_shared.IsChecked == true)
            {
                var folder = _folder.Text.Trim();
                if (folder.Length == 0) { _error.Text = "Choose a folder."; return; }
                if (SettingsStore.IsShared && string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder)),
                        Path.GetFullPath(SettingsStore.SharedFolder!), StringComparison.OrdinalIgnoreCase))
                {
                    DialogResult = false;
                    return;
                }

                if (File.Exists(SettingsStore.SharedFilePathFor(folder)))
                {
                    var pw = new PasswordDialog(false, "Open shared settings",
                        "This folder already contains a shared settings file. Enter its password to use it.\n\n" +
                        "The connections currently on this PC are replaced by the shared ones – export a backup first if you still need them.",
                        "Open") { Owner = this };
                    if (pw.ShowDialog() != true) return;
                    OpenedData = SettingsStore.OpenShared(folder, pw.Password, _current.Ui);
                }
                else
                {
                    var pw = new PasswordDialog(true, "Protect shared settings",
                        "Your connections, folders and credentials are copied into the shared file and encrypted with this password (AES-256). " +
                        "Everyone who should use the file needs this password; it is asked once per PC. Use at least 8 characters.",
                        "Create") { Owner = this };
                    if (pw.ShowDialog() != true) return;
                    SettingsStore.CreateShared(folder, pw.Password, _current);
                }
                DialogResult = true;
            }
            else if (SettingsStore.IsShared)
            {
                if (!await Dialogs.Confirm(this, "Use this PC only",
                        "The current settings are copied to this PC (encrypted for your Windows account) and the shared file is no longer used. " +
                        "The shared file itself is not deleted.", "Switch")) return;
                SettingsStore.SwitchToLocal(_current);
                DialogResult = true;
            }
            else
            {
                DialogResult = false;
            }
        }
        catch (Exception ex)
        {
            _error.Text = ex.Message;
        }
    }
}
