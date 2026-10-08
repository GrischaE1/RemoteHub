using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Views;

/// <summary>Asks for a password (backups, shared settings file); with confirmation when creating one.</summary>
public sealed class PasswordDialog : FluentWindow
{
    private readonly PasswordBox _pw1 = new() { PlaceholderText = "Password", Margin = new Thickness(0, 0, 0, 8) };
    private readonly PasswordBox _pw2 = new() { PlaceholderText = "Repeat password", Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private readonly bool _confirm;

    public string Password { get; private set; } = "";

    public PasswordDialog(bool confirm, string? title = null, string? message = null, string? okText = null)
    {
        _confirm = confirm;
        Title = title ?? (confirm ? "Protect backup" : "Unlock backup");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        ShowInTaskbar = false;

        var info = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Text = message ?? (confirm
                ? "The backup contains all connections and passwords. It is encrypted with this password (AES-256), so it can be restored on any PC. Use at least 8 characters."
                : "Enter the password that was used when the backup was created."),
        };
        info.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var ok = new Button { Content = okText ?? (confirm ? "Export" : "Import"), Appearance = ControlAppearance.Primary, IsDefault = true, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 96 };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 20) };
        body.Children.Add(info);
        body.Children.Add(_pw1);
        if (confirm) body.Children.Add(_pw2);
        body.Children.Add(_error);
        body.Children.Add(buttons);

        var root = new StackPanel();
        root.Children.Add(new TitleBar { Title = Title, ShowMaximize = false, ShowMinimize = false });
        root.Children.Add(body);
        Content = root;
        Loaded += (_, _) => _pw1.Focus();
    }

    private void Accept()
    {
        var p = _pw1.Password ?? "";
        if (_confirm && p.Length < 8) { _error.Text = "Use at least 8 characters."; return; }
        if (_confirm && p != _pw2.Password) { _error.Text = "The passwords do not match."; return; }
        if (p.Length == 0) { _error.Text = "Enter the password."; return; }
        Password = p;
        DialogResult = true;
    }
}
