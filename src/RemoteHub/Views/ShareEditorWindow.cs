using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RemoteHub.Models;
using RemoteHub.Remoting.Shares;
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
    private readonly PasswordBox _password = new();
    private readonly Button _removePassword = new() { Content = "Remove stored password", Margin = new Thickness(8, 0, 0, 0) };
    private Secret? _storedPassword; // never shown: the box starts empty and a typed password replaces it
    private readonly Grid _customFields = new();
    // SSH and SFTP: a private key, imported into the encrypted database, and its passphrase (docs/network-shares.md §6.2).
    private readonly StackPanel _keyFields = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _keyText = Caption();
    private readonly Button _importKey = new() { Content = "Import key…", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _removeKey = new() { Content = "Remove key" };
    private readonly PasswordBox _passphrase = new();
    private readonly Button _removePassphrase = new() { Content = "Remove stored passphrase", Margin = new Thickness(8, 0, 0, 0) };
    private Secret? _storedKey, _storedPassphrase; // never shown; an imported key or a typed passphrase replaces them
    private readonly TextBlock _accountHint = Caption();
    private readonly StackPanel _trustRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TitleBar _titleBar = new() { ShowMaximize = false, ShowMinimize = false };
    private readonly bool _isNew;
    private string? _trusted;

    public ShareEditorWindow(NetworkShare share, Connection? server, AppData data, bool isNew)
    {
        _share = share;
        _server = server;
        _data = data;
        _isNew = isNew;
        _trusted = share.TrustedFingerprint;
        Width = 560; SizeToContent = SizeToContent.Height; MinHeight = 300;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;

        // SSH is a terminal of its own, never one of a server's shares; a kind this PC's policy blocks isn't offered
        // (the item's own kind stays, so a blocked item can still be edited).
        foreach (var (p, text) in Kinds(server is null, isNew ? null : share.Protocol))
            _protocol.Items.Add(new ComboBoxItem { Content = text, Tag = p });
        foreach (var (s, text) in new[] { (FtpSecurity.Explicit, "TLS (explicit, port 21)"), (FtpSecurity.Implicit, "TLS (implicit, port 990)"), (FtpSecurity.None, "None – not encrypted") })
            _ftpSecurity.Items.Add(new ComboBoxItem { Content = text, Tag = s });

        _name.Text = share.Name;
        _protocol.SelectedItem = _protocol.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (ShareProtocol)i.Tag == share.Protocol) ?? _protocol.Items[0];
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
        _storedPassword = share.Credentials.Password;
        _storedKey = share.Credentials.PrivateKey;
        _storedPassphrase = share.Credentials.KeyPassphrase;
        ShowPasswordState();
        _removePassword.Click += (_, _) => { _storedPassword = null; _password.Password = ""; ShowPasswordState(); };
        _removePassphrase.Click += (_, _) => { _storedPassphrase = null; _passphrase.Password = ""; ShowPasswordState(); };
        ToolTipService.SetShowOnDisabled(_passphrase, true);
        _importKey.Click += (_, _) => ImportKey();
        _removeKey.Click += (_, _) => { _storedKey = _storedPassphrase = null; _passphrase.Password = ""; ShowPasswordState(); };
        // Policy AllowSavedPasswords = 0 (§5.6): no password and no passphrase is stored; the key itself isn't a password.
        if (!Features.IsAllowed(Feature.SavedPasswords)) { PolicyLock.TurnedOff(_password); PolicyLock.TurnedOff(_passphrase); }

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
        var pwRow = new Grid();
        pwRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pwRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pwRow.Children.Add(_password);
        Grid.SetColumn(_removePassword, 1);
        pwRow.Children.Add(_removePassword);
        pwCol.Children.Add(Label("Password")); pwCol.Children.Add(pwRow);
        Grid.SetRow(pwCol, 1);
        Grid.SetColumnSpan(pwCol, 2);
        _customFields.Children.Add(userCol);
        _customFields.Children.Add(domainCol);
        _customFields.Children.Add(pwCol);
        _customFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _keyFields.Children.Add(Label("Private key"));
        var keyButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        keyButtons.Children.Add(_importKey);
        keyButtons.Children.Add(_removeKey);
        _keyFields.Children.Add(keyButtons);
        _keyText.Margin = new Thickness(0, 0, 0, 10);
        _keyFields.Children.Add(_keyText);
        var passphraseRow = new Grid();
        passphraseRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        passphraseRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        passphraseRow.Children.Add(_passphrase);
        Grid.SetColumn(_removePassphrase, 1);
        passphraseRow.Children.Add(_removePassphrase);
        _keyFields.Children.Add(Label("Key passphrase"));
        _keyFields.Children.Add(passphraseRow);
        Grid.SetRow(_keyFields, 2);
        Grid.SetColumnSpan(_keyFields, 2);
        _customFields.Children.Add(_keyFields);
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
        DockPanel.SetDock(_titleBar, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(_titleBar);
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

    /// <summary>
    /// The kinds the type list offers, in its order: SSH only for an item on its own, and only the kinds this PC's policy
    /// allows – besides <paramref name="current"/>, the kind of an item being edited (a new item gets only allowed ones).
    /// </summary>
    internal static IEnumerable<(ShareProtocol Kind, string Text)> Kinds(bool standalone, ShareProtocol? current, Agent.IPolicySource? policy = null)
    {
        (ShareProtocol, string)[] all =
        [
            (ShareProtocol.Ssh, "SSH – terminal"),
            (ShareProtocol.Smb, "SMB – Windows file share, NAS"),
            (ShareProtocol.Sftp, "SFTP – over SSH"),
            (ShareProtocol.Ftp, "FTP / FTPS"),
            (ShareProtocol.WebDav, "WebDAV – e.g. Nextcloud"),
        ];
        return all.Where(k => (standalone || k.Item1 != ShareProtocol.Ssh) && (k.Item1 == current || Features.IsAllowed(k.Item1, policy)));
    }

    /// <summary>"New SSH connection" for SSH, the share titles for the rest – following the type while it is changed.</summary>
    private void UpdateTitle()
    {
        Title = Protocol == ShareProtocol.Ssh ? (_isNew ? "New SSH connection" : $"SSH connection {_share.Name}")
            : _server is null ? (_isNew ? "New network share" : $"Network share {_share.Name}")
            : _isNew ? $"Add network share to {_server.Name}" : $"Network share {_share.Name} on {_server.Name}";
        _titleBar.Title = Title;
    }

    /// <summary>What the dialog's messages are titled.</summary>
    private string Topic => Protocol == ShareProtocol.Ssh ? "SSH connection" : "Network share";

    private void Update()
    {
        var p = Protocol;
        UpdateTitle();
        _name.PlaceholderText = p == ShareProtocol.Ssh ? "e.g. web01" : "e.g. share";
        _custom.Content = p == ShareProtocol.Ssh ? "Enter an account for this connection" : "Enter an account for this share";
        _pathLabel.Visibility = _path.Visibility = p == ShareProtocol.Ssh ? Visibility.Collapsed : Visibility.Visible;
        _keyFields.Visibility = ShareConnector.TakesKey(p) ? Visibility.Visible : Visibility.Collapsed;
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
            ShareProtocol.Sftp or ShareProtocol.Ssh => "Signs in with a private key, a password, or both (the key first). The server's key is shown for you to confirm the first time.",
            ShareProtocol.Ftp => "Without an account, RemoteHub signs in anonymously.",
            _ => "Without an account, your Windows account is used (intranet servers).",
        };
        _trustRow.Children.Clear();
        if (_trusted is not null)
        {
            var text = Caption();
            text.Text = $"Accepted {(ShareConnector.TakesKey(Protocol) ? "host key" : "certificate")}: {Short(_trusted)}";
            text.VerticalAlignment = VerticalAlignment.Center;
            var forget = new Button { Content = "Forget", Margin = new Thickness(10, 0, 0, 0), ToolTip = "Ask again the next time it is opened" };
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
        var ssh = Protocol == ShareProtocol.Ssh;
        if (ssh) path = "";
        if (name.Length == 0) { await Dialogs.Info(this, Topic, ssh ? "Enter a name for the connection." : "Enter a name for the share."); return; }
        if (_server is null && _host.Text.Trim().Length == 0)
        {
            await Dialogs.Info(this, Topic, ssh ? "Enter the host to connect to (name or address)." : "Enter the host the share is on (name or address).");
            return;
        }
        if (Protocol == ShareProtocol.Smb && Remoting.Shares.ShareConnector.SplitSmbPath(path).Share.Length == 0)
        {
            await Dialogs.Info(this, "Network share", "Enter the name of the share, e.g. \"share\" for \\\\" + (_host.Text.Trim().Length > 0 ? _host.Text.Trim() : _server!.Host) + "\\share.");
            return;
        }
        int? port = null;
        if (_port.Text.Trim().Length > 0)
        {
            if (!int.TryParse(_port.Text.Trim(), out var n) || n is < 1 or > 65535) { await Dialogs.Info(this, Topic, "The port must be a number from 1 to 65535."); return; }
            port = n;
        }
        if (port == NetworkShare.DefaultPort(Protocol, Security, _https.IsChecked == true)) port = null; // the default: follows the type
        var host = HostToSave();
        if (ShareConnector.TakesKey(Protocol) && _inherit.IsChecked != true && _custom.IsChecked == true && _user.Text.Trim().Length == 0)
        {
            await Dialogs.Info(this, Topic, $"{NetworkShare.Label(Protocol)} needs a user name.");
            return;
        }
        var takesKey = ShareConnector.TakesKey(Protocol) && _custom.IsChecked == true;
        var typedPassphrase = Features.IsAllowed(Feature.SavedPasswords) ? Secret.From(_passphrase.Password) : null;
        if (takesKey && _storedKey is { } key && typedPassphrase is not null)
        {
            // A wrong passphrase is said now, not at the next connect.
            try { SshSignIn.Describe(key, typedPassphrase); }
            catch (SshKeyException ex) { await Dialogs.Info(this, Topic, ex.Message); return; }
        }

        // SFTP and SSH to the same host and port meet the same server key: switching between them keeps it.
        var sameServer = ShareConnector.TakesKey(_share.Protocol) && ShareConnector.TakesKey(Protocol);
        var changedTarget = (_share.Protocol != Protocol && !sameServer) || !string.Equals(_share.Host ?? "", host ?? "", StringComparison.OrdinalIgnoreCase) || _share.Port != port;
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
            // Policy AllowSavedPasswords = 0: the password box is greyed out and what was stored is kept (§14 Q2).
            ? new CredentialSet { Username = Null(_user.Text), Domain = Null(_domain.Text),
                                  Password = (Features.IsAllowed(Feature.SavedPasswords) ? Secret.From(_password.Password) : null) ?? _storedPassword,
                                  // A key is kept only for the kinds that sign in with one.
                                  PrivateKey = takesKey ? _storedKey : null,
                                  KeyPassphrase = takesKey && _storedKey is not null ? typedPassphrase ?? _storedPassphrase : null }
            : new CredentialSet();
        // A key or certificate belongs to one machine: another host or type asks again.
        _share.TrustedFingerprint = changedTarget ? null : _trusted;
        DialogResult = true;
    }

    private static string? Null(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void ShowPasswordState()
    {
        _password.PlaceholderText = _storedPassword is not null ? "A password is stored – type a new one to replace it"
            : _storedKey is not null ? "Optional with a key – tried after it" : "Leave empty to be asked when connecting";
        _removePassword.Visibility = _storedPassword is not null ? Visibility.Visible : Visibility.Collapsed;
        _removeKey.Visibility = _storedKey is not null ? Visibility.Visible : Visibility.Collapsed;
        _importKey.Content = _storedKey is not null ? "Replace key…" : "Import key…";
        _passphrase.IsEnabled = _storedKey is not null && Features.IsAllowed(Feature.SavedPasswords);
        // Greyed until there is a key, and says so (with saved passwords off, PolicyLock's reason stays).
        if (Features.IsAllowed(Feature.SavedPasswords)) _passphrase.ToolTip = _storedKey is null ? "Import a key first – the passphrase belongs to it" : null;
        _passphrase.PlaceholderText = _storedPassphrase is not null ? "A passphrase is stored – type a new one to replace it"
            : "Leave empty if the key has none, or to be asked when connecting";
        _removePassphrase.Visibility = _storedPassphrase is not null ? Visibility.Visible : Visibility.Collapsed;
        _keyText.Text = PrivateKeyPicker.Describe(_storedKey, _storedPassphrase);
    }

    /// <summary>Reads a private key file into the database (encrypted, like a password); the file itself isn't needed afterwards.</summary>
    private async void ImportKey()
    {
        if (await PrivateKeyPicker.Pick(this, Topic) is not { } key) return;
        _storedKey = key;
        _storedPassphrase = null;
        _passphrase.Password = "";
        ShowPasswordState();
    }

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
