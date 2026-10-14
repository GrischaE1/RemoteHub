using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RemoteHub.Models;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>Adds or edits a network share – of a server, or on its own (server null): type, where it is, and the account it uses.</summary>
public sealed class ShareEditorWindow : FluentWindow
{
    private readonly NetworkShare _share;
    private readonly Connection? _server;
    private readonly AppData _data;
    private readonly TextBox _name = new() { PlaceholderText = "e.g. share" };
    private readonly ComboBox _protocol = new();
    private readonly TextBox _host = new();
    private readonly TextBox _port = new() { Width = 110 };
    /// <summary>Below the host for a server's share: whose address it is, and how a jump host's own share is reached.</summary>
    private readonly TextBlock _hostHint = Caption();
    /// <summary>The protocol's default port as last filled in (replaced when the protocol changes, unless the user typed another).</summary>
    private string _defaultPort = "";
    private readonly TextBlock _pathLabel = Label("");
    private readonly TextBox _path = new();
    private readonly ComboBox _ftpSecurity = new();
    private readonly System.Windows.Controls.CheckBox _https = new() { Content = "HTTPS (encrypted)", Margin = new Thickness(0, 0, 0, 10) };
    private readonly StackPanel _ftpRow = new();
    private readonly TextBlock _warning = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private readonly RadioButton _inherit = new() { GroupName = "ShareCred", Margin = new Thickness(0, 0, 0, 2) };
    private readonly TextBlock _inheritText = Caption();
    private readonly RadioButton _saved = new() { GroupName = "ShareCred", Content = "Use a saved credential", Margin = new Thickness(0, 0, 0, 4) };
    private readonly ComboBox _savedList = new() { Margin = new Thickness(28, 0, 0, 8), DisplayMemberPath = nameof(SavedCredential.Name) };
    private readonly RadioButton _custom = new() { GroupName = "ShareCred", Content = "Enter an account for this share", Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox _user = new() { PlaceholderText = "user or DOMAIN\\user" };
    private readonly TextBox _domain = new() { PlaceholderText = "Optional" };
    private readonly PasswordBox _password = new() { PlaceholderText = "Leave empty to be asked when connecting" };
    private readonly Grid _customFields = new();
    private readonly TextBlock _accountHint = Caption();
    private readonly StackPanel _trustRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
    private string? _trusted;

    public ShareEditorWindow(NetworkShare share, Connection? server, AppData data, bool isNew)
    {
        _share = share;
        _server = server;
        _data = data;
        _trusted = share.TrustedFingerprint;
        Title = server is null ? (isNew ? "New network share" : $"Network share {share.Name}")
            : isNew ? $"Add network share to {server.Name}" : $"Network share {share.Name} on {server.Name}";
        Width = 560; SizeToContent = SizeToContent.Height; MinHeight = 300;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;

        foreach (var (p, text) in new[]
                 {
                     (ShareProtocol.Smb, "SMB – Windows file share, NAS"),
                     (ShareProtocol.Sftp, "SFTP – over SSH"),
                     (ShareProtocol.Ftp, "FTP / FTPS"),
                     (ShareProtocol.WebDav, "WebDAV – e.g. Nextcloud"),
                 })
            _protocol.Items.Add(new ComboBoxItem { Content = text, Tag = p });
        foreach (var (s, text) in new[] { (FtpSecurity.Explicit, "TLS (explicit, port 21)"), (FtpSecurity.Implicit, "TLS (implicit, port 990)"), (FtpSecurity.None, "None – not encrypted") })
            _ftpSecurity.Items.Add(new ComboBoxItem { Content = text, Tag = s });

        _name.Text = share.Name;
        _protocol.SelectedIndex = (int)share.Protocol;
        // Filled in, not only a placeholder: a server's share uses its address unless another is entered (saved as "the server's").
        _host.Text = share.Host ?? server?.Host.Trim() ?? "";
        _host.PlaceholderText = server is null ? "e.g. nas or 192.168.1.10" : $"{server.Host} (the server's)";
        _port.Text = share.Port?.ToString() ?? ""; // the default is filled in by Update()
        _path.Text = share.Path;
        _ftpSecurity.SelectedIndex = (int)share.FtpSecurity;
        _https.IsChecked = share.WebDavHttps;

        _savedList.ItemsSource = data.SavedCredentials;
        _savedList.SelectedItem = share.CredentialId is { } id ? Inheritance.FindSaved(data, id) : data.SavedCredentials.FirstOrDefault();
        _saved.IsEnabled = data.SavedCredentials.Count > 0;
        _inherit.IsChecked = share.InheritCredentials;
        _saved.IsChecked = !share.InheritCredentials && share.CredentialId is not null;
        _custom.IsChecked = !share.InheritCredentials && share.CredentialId is null;
        _user.Text = share.Credentials.Username ?? "";
        _domain.Text = share.Credentials.Domain ?? "";
        _password.Password = share.Credentials.Password ?? "";

        var (inherited, source) = server is null ? Inheritance.ResolveCredentials(share, data, includeSelf: false) : Inheritance.ResolveCredentials(server, data);
        _inherit.Content = server is null ? "Inherit from parent folder" : $"Same as the server ({server.Name})";
        _inheritText.Text = inherited.IsEmpty ? "No account set" : $"{inherited.Domain}{(string.IsNullOrEmpty(inherited.Domain) ? "" : "\\")}{inherited.Username} – from {source}";
        _inheritText.Margin = new Thickness(28, 0, 0, 8);

        // ---- layout
        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 10) };
        body.Children.Add(Label("Name")); body.Children.Add(Spaced(_name));
        body.Children.Add(Label("Type")); body.Children.Add(Spaced(_protocol));

        var where = new Grid();
        where.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        where.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hostCol = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        hostCol.Children.Add(Label("Host")); hostCol.Children.Add(Spaced(_host));
        var portCol = new StackPanel();
        portCol.Children.Add(Label("Port")); portCol.Children.Add(Spaced(_port));
        Grid.SetColumn(portCol, 1);
        where.Children.Add(hostCol);
        where.Children.Add(portCol);
        body.Children.Add(where);
        _hostHint.Margin = new Thickness(0, 0, 0, 10);
        body.Children.Add(_hostHint);

        body.Children.Add(_pathLabel); body.Children.Add(Spaced(_path));
        _ftpRow.Children.Add(Label("Encryption")); _ftpRow.Children.Add(Spaced(_ftpSecurity));
        body.Children.Add(_ftpRow);
        body.Children.Add(_https);
        _warning.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        body.Children.Add(_warning);

        body.Children.Add(new TextBlock { Text = "Account", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6) });
        body.Children.Add(_inherit);
        body.Children.Add(_inheritText);
        body.Children.Add(_saved);
        body.Children.Add(_savedList);
        body.Children.Add(_custom);
        _customFields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _customFields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _customFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _customFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var userCol = new StackPanel { Margin = new Thickness(0, 0, 10, 10) };
        userCol.Children.Add(Label("Username")); userCol.Children.Add(_user);
        var domainCol = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        domainCol.Children.Add(Label("Domain")); domainCol.Children.Add(_domain);
        Grid.SetColumn(domainCol, 1);
        var pwCol = new StackPanel();
        pwCol.Children.Add(Label("Password")); pwCol.Children.Add(_password);
        Grid.SetRow(pwCol, 1);
        Grid.SetColumnSpan(pwCol, 2);
        _customFields.Children.Add(userCol);
        _customFields.Children.Add(domainCol);
        _customFields.Children.Add(pwCol);
        body.Children.Add(_customFields);
        _accountHint.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(_accountHint);
        body.Children.Add(_trustRow);

        var save = new Button { Content = isNew ? "Add" : "Save", Appearance = ControlAppearance.Primary, IsDefault = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 100 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 6, 20, 16) };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        var root = new DockPanel();
        var tb = new TitleBar { Title = Title, ShowMaximize = false, ShowMinimize = false };
        DockPanel.SetDock(tb, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(tb);
        root.Children.Add(buttons);
        root.Children.Add(body);
        Content = root;

        _protocol.SelectionChanged += (_, _) => Update();
        _ftpSecurity.SelectionChanged += (_, _) => Update();
        _https.Checked += (_, _) => Update();
        _https.Unchecked += (_, _) => Update();
        _inherit.Checked += (_, _) => Update();
        _saved.Checked += (_, _) => Update();
        _custom.Checked += (_, _) => Update();
        _host.TextChanged += (_, _) => UpdateHostHint();
        Update();
        UpdateHostHint();
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    private ShareProtocol Protocol => (ShareProtocol)((ComboBoxItem)_protocol.SelectedItem).Tag;
    private FtpSecurity Security => (FtpSecurity)((ComboBoxItem)_ftpSecurity.SelectedItem).Tag;

    private void Update()
    {
        var p = Protocol;
        _pathLabel.Text = p switch
        {
            ShareProtocol.Smb => "Share name",
            ShareProtocol.WebDav => "Path",
            _ => "Start folder",
        };
        _path.PlaceholderText = p switch
        {
            ShareProtocol.Smb => @"share   or   share\folder   or   \\host\share",
            ShareProtocol.WebDav => "e.g. /remote.php/dav/files/me  (empty = /)",
            _ => "Empty = the account's home folder",
        };
        _ftpRow.Visibility = p == ShareProtocol.Ftp ? Visibility.Visible : Visibility.Collapsed;
        _https.Visibility = p == ShareProtocol.WebDav ? Visibility.Visible : Visibility.Collapsed;
        var defaultPort = NetworkShare.DefaultPort(p, Security, _https.IsChecked == true).ToString();
        _port.PlaceholderText = defaultPort;
        // The default shown as a value; a port the user typed stays (SMB is always 445).
        if (p == ShareProtocol.Smb || _port.Text.Trim().Length == 0 || _port.Text.Trim() == _defaultPort) _port.Text = defaultPort;
        _defaultPort = defaultPort;
        _port.IsEnabled = p != ShareProtocol.Smb;
        _warning.Text = (p == ShareProtocol.Ftp && Security == FtpSecurity.None) || (p == ShareProtocol.WebDav && _https.IsChecked != true)
            ? "Not encrypted: the password and every file go over the network in plain text. Use it only in a trusted network."
            : "";
        _warning.Visibility = _warning.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _savedList.Visibility = _saved.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _customFields.Visibility = _custom.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _accountHint.Text = p switch
        {
            ShareProtocol.Smb => "Without an account, your Windows account is used. Another account for a server Windows already uses with a different account works too – RemoteHub then connects with its own SMB client.",
            ShareProtocol.Sftp => "SFTP signs in with user name and password. The server's key is shown for you to confirm the first time.",
            ShareProtocol.Ftp => "Without an account, RemoteHub signs in anonymously.",
            _ => "Without an account, your Windows account is used (intranet servers).",
        };
        _trustRow.Children.Clear();
        if (_trusted is not null)
        {
            var text = Caption();
            text.Text = $"Accepted {(Protocol == ShareProtocol.Sftp ? "host key" : "certificate")}: {Short(_trusted)}";
            text.VerticalAlignment = VerticalAlignment.Center;
            var forget = new Button { Content = "Forget", Margin = new Thickness(10, 0, 0, 0), ToolTip = "Ask again next time the share is opened" };
            forget.Click += (_, _) => { _trusted = null; Update(); };
            _trustRow.Children.Add(text);
            _trustRow.Children.Add(forget);
        }
    }

    private static string Short(string fp) => fp.Length > 40 ? fp[..40] + "…" : fp;

    /// <summary>The host as saved: null when it is the server's (so it follows the server's address).</summary>
    private string? HostToSave()
    {
        var host = _host.Text.Trim();
        return host.Length == 0 || (_server is not null && string.Equals(host, _server.Host.Trim(), StringComparison.OrdinalIgnoreCase)) ? null : host;
    }

    private void UpdateHostHint()
    {
        _hostHint.FontSize = 12; // a caption; the caution below is written like the dialog's other warning
        _hostHint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        if (_server is null) _hostHint.Text = "";
        else if (HostToSave() is null) _hostHint.Text = $"{_server.Name}'s address. Enter another only for a share on another device.";
        else if (_server.Children.Count > 0)
        {
            // A jump host's own shares are reached like it – directly – not through it.
            _hostHint.Text = $"Reached directly, like {_server.Name} itself. For a device reached through {_server.Name}, add the share to that " +
                             $"device's connection, or place a share on its own below {_server.Name}.";
            _hostHint.ClearValue(TextBlock.FontSizeProperty);
            _hostHint.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        }
        else _hostHint.Text = _server.JumpHost is { } jump ? $"Another device, reached through {jump.Name} like {_server.Name}." : "";
        _hostHint.Visibility = _hostHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Save()
    {
        var name = _name.Text.Trim();
        var path = _path.Text.Trim();
        // SMB: a pasted \\host\share\folder fills in the host as well
        if (Protocol == ShareProtocol.Smb && path.StartsWith(@"\\") && path.TrimStart('\\').Split('\\', 2) is [{ Length: > 0 } uncHost, var rest])
        {
            if (HostToSave() is null) _host.Text = uncHost; // empty, or only the server's filled in
            path = rest;
            _path.Text = path;
        }
        if (name.Length == 0) name = Protocol == ShareProtocol.Smb && path.Length > 0 ? Remoting.Shares.ShareConnector.SplitSmbPath(path).Share : "";
        if (name.Length == 0) { await Dialogs.Info(this, "Network share", "Enter a name for the share."); return; }
        if (_server is null && _host.Text.Trim().Length == 0) { await Dialogs.Info(this, "Network share", "Enter the host the share is on (name or address)."); return; }
        if (Protocol == ShareProtocol.Smb && Remoting.Shares.ShareConnector.SplitSmbPath(path).Share.Length == 0)
        {
            await Dialogs.Info(this, "Network share", "Enter the name of the share, e.g. \"share\" for \\\\" + (_host.Text.Trim().Length > 0 ? _host.Text.Trim() : _server!.Host) + "\\share.");
            return;
        }
        int? port = null;
        if (_port.Text.Trim().Length > 0)
        {
            if (!int.TryParse(_port.Text.Trim(), out var n) || n is < 1 or > 65535) { await Dialogs.Info(this, "Network share", "The port must be a number from 1 to 65535."); return; }
            port = n;
        }
        if (port == NetworkShare.DefaultPort(Protocol, Security, _https.IsChecked == true)) port = null; // the default: follows the type
        var host = HostToSave();
        if (Protocol == ShareProtocol.Sftp && _inherit.IsChecked != true && _custom.IsChecked == true && _user.Text.Trim().Length == 0)
        {
            await Dialogs.Info(this, "Network share", "SFTP needs a user name.");
            return;
        }

        var changedTarget = _share.Protocol != Protocol || !string.Equals(_share.Host ?? "", host ?? "", StringComparison.OrdinalIgnoreCase) || _share.Port != port;
        _share.Name = name;
        _share.Protocol = Protocol;
        _share.Host = host;
        _share.Port = port;
        _share.Path = path;
        _share.FtpSecurity = Security;
        _share.WebDavHttps = _https.IsChecked == true;
        _share.InheritCredentials = _inherit.IsChecked == true;
        _share.CredentialId = _saved.IsChecked == true ? (_savedList.SelectedItem as SavedCredential)?.Id : null;
        if (_saved.IsChecked == true && _share.CredentialId is null) _share.InheritCredentials = true;
        _share.Credentials = _custom.IsChecked == true
            ? new CredentialSet { Username = Null(_user.Text), Domain = Null(_domain.Text), Password = Null(_password.Password) }
            : new CredentialSet();
        // A key or certificate belongs to one machine: another host or type asks again.
        _share.TrustedFingerprint = changedTarget ? null : _trusted;
        DialogResult = true;
    }

    private static string? Null(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 0, 0, 4) };

    private static TextBlock Caption()
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    private static FrameworkElement Spaced(FrameworkElement e)
    {
        e.Margin = new Thickness(0, 0, 0, 10);
        return e;
    }
}
