using System.Windows;
using System.Windows.Controls;
using RemoteHub.Agent;
using RemoteHub.Models;
using RemoteHub.Remoting;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>
/// Install or update the RemoteHub Agent on a connection's device: device options and how to deliver it (silent over
/// PowerShell remoting, through the running agent, or via the clipboard). "Install" closes it with <see cref="Options"/>;
/// the progress is shown in <see cref="AgentInstallProgressWindow"/>.
/// </summary>
public sealed class AgentInstallDialog : FluentWindow
{
    /// <summary>What was chosen (set when the dialog closes with true).</summary>
    public AgentInstallOptions? Options { get; private set; }
    private readonly TextBox _label, _port, _firewall, _groups;
    private readonly RadioButton _viaRemoting, _viaAgent, _viaClipboard, _viaFiles;
    private readonly CheckBox _newIdentity, _useAfter, _virtualDisplay, _autoSignIn;
    private readonly TextBlock _autoSignInHint;
    private readonly bool? _hadProvider;
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Foreground = System.Windows.Media.Brushes.IndianRed };
    private readonly Button _install, _close;
    private readonly StackPanel _options;

    /// <param name="localAddress">The address the device sees connections from (this PC, or the jump host in the device's network).</param>
    /// <param name="jumpHost">The jump host the device is reached through, or null.</param>
    /// <param name="credentialProvider">RemoteHub's agent package contains the credential provider (automatic sign-in).</param>
    public AgentInstallDialog(Connection c, bool remotingAvailable, bool agentReachable, string? localAddress, string? jumpHost = null, bool credentialProvider = false)
    {
        var existing = c.Agent;
        _hadProvider = existing?.CredentialProvider;
        var installed = existing is { Status: AgentStatus.Installed };
        Title = installed ? "Update RemoteHub Agent" : "Install RemoteHub Agent";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        ShowInTaskbar = false;

        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 20) };
        body.Children.Add(new TextBlock { Text = $"{c.Name}  ·  {c.HostWithRoute}", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        body.Children.Add(Muted("The agent gives RemoteHub PowerShell, files, registry, services and processes on this device without WinRM. " +
                                "Operators sign in with a Windows account of the device (administrators by default); RemoteHub only accepts the " +
                                "device certificate created here.", 0, 0, 0, 12));

        _options = new StackPanel();
        body.Children.Add(_options);

        if (existing is not null)
            _options.Children.Add(Muted($"{(installed ? "Installed" : "Prepared, not confirmed yet")}: device {AgentInstaller.Head(existing.DeviceId, 8)}…, port {existing.Port}, " +
                                        $"certificate …{AgentInstaller.Tail(existing.Fingerprint, 8)}{(existing.Version is { } v ? $", version {v}" : "")}", 0, 0, 0, 6));
        _newIdentity = new CheckBox
        {
            Content = "Create a new device certificate (the current one stops working)",
            IsChecked = !installed,
            Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible,
            Margin = new Thickness(0, 0, 0, 8),
        };
        _newIdentity.Checked += (_, _) => UpdateState();
        _newIdentity.Unchecked += (_, _) => UpdateState();
        _options.Children.Add(_newIdentity);

        _label = Field("Device label", existing?.Label is { Length: > 0 } l ? l : c.Name, "Shown in the agent's log and the certificate.");
        _port = Field("Port", (existing?.Port ?? AgentProtocol.DefaultPort).ToString(), "TCP port the agent listens on (default 58732).");
        _groups = Field("Allowed accounts", "Administrators",
            "Members of these groups on the device may connect (comma-separated group names or SIDs). \"Administrators\" works in any Windows language.");
        _firewall = Field("Allow connections from", "",
            "Firewall scope on the device: empty = any address. Example: 10.1.0.0/16, 192.168.1.20 (all PCs running RemoteHub must be included).");
        if (localAddress is not null)
        {
            var add = new Button { Content = jumpHost is null ? $"Add this PC ({localAddress})" : $"Add {jumpHost} ({localAddress})", Icon = new SymbolIcon(SymbolRegular.Add24), Margin = new Thickness(0, 4, 0, 0) };
            add.Click += (_, _) => _firewall.Text = string.Join(", ", _firewall.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Append(localAddress).Distinct());
            _options.Children.Add(add);
        }

        _virtualDisplay = new CheckBox
        {
            Content = "Virtual display for servers without a monitor",
            IsChecked = existing?.VirtualDisplay ?? true,
            Margin = new Thickness(0, 14, 0, 0),
        };
        _options.Children.Add(_virtualDisplay);
        _options.Children.Add(Muted("Without a monitor, Windows often offers only 640×480. The agent then adds a virtual monitor at the size of " +
                                    "the RemoteHub window while you're connected (open-source driver by VirtualDrivers, signed by SignPath " +
                                    "Foundation, which the device then trusts as a driver publisher). Unchecked: not installed, or removed.", 26, 2, 0, 0));
        _autoSignIn = new CheckBox
        {
            Content = "Allow automatic sign-in (installs a credential provider)",
            IsChecked = _hadProvider == true,
            Margin = new Thickness(0, 14, 0, 0),
            Visibility = credentialProvider ? Visibility.Visible : Visibility.Collapsed,
        };
        _options.Children.Add(_autoSignIn);
        _autoSignInHint = Muted("When nobody is signed in on the device, RemoteHub can sign in with the connection's credentials (remote desktop " +
                                "through the agent). Installs a sign-in component and allows it for the allowed accounts – a policy IT has set " +
                                "on the device stays as it is. Anyone at the device can see the session while you work. Silent installs only; " +
                                "unchecked after it was installed: removed.", 26, 2, 0, 0);
        _autoSignInHint.Visibility = _autoSignIn.Visibility;
        _options.Children.Add(_autoSignInHint);
        _options.Children.Add(new TextBlock { Text = "How", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) });
        _viaRemoting = Radio("Install silently over PowerShell remoting (WinRM) – copies both files to a temp folder, installs, deletes them",
            remotingAvailable, remotingAvailable && !(installed && agentReachable));
        _viaAgent = Radio("Update through the running agent", installed && agentReachable, installed && agentReachable);
        _viaAgent.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        _viaClipboard = Radio("Copy the installer and the enrollment file to the clipboard – paste both into the RDP session (same folder) and run the MSI as administrator",
            true, jumpHost is null && !remotingAvailable && !(installed && agentReachable));
        _viaFiles = Radio("Save the installer and the enrollment file to a folder – install them on the device by hand or with software distribution (both in the same folder)",
            true, jumpHost is not null && !remotingAvailable && !(installed && agentReachable));
        if (!remotingAvailable)
            _options.Children.Add(Muted("PowerShell remoting isn't available for this connection, so the silent install isn't offered.", 26, 0, 0, 4));
        if (jumpHost is not null)
            _options.Children.Add(Muted($"{c.Name} is reached through {jumpHost}, so its agent will only be reached from there: allow connections from " +
                                        $"{jumpHost}'s address in that network.", 26, 0, 0, 4));

        // Below a jump host an installed agent is used anyway, unless the connection itself says WinRM (Inheritance.UsesAgent).
        _useAfter = new CheckBox { Content = "Use the agent for this connection afterwards", IsChecked = true, Margin = new Thickness(0, 12, 0, 0),
                                   Visibility = jumpHost is null ? Visibility.Visible : Visibility.Collapsed };
        _options.Children.Add(_useAfter);
        foreach (var way in new[] { _viaRemoting, _viaAgent, _viaClipboard, _viaFiles })
        {
            way.Checked += (_, _) => UpdateState();
            way.Unchecked += (_, _) => UpdateState();
        }

        body.Children.Add(_error);

        _install = new Button { Content = installed ? "Update" : "Install", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.ArrowDownload24), MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
        _install.Click += (_, _) => Start();
        _close = new Button { Content = "Close", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_install);
        buttons.Children.Add(_close);
        body.Children.Add(buttons);

        var root = new DockPanel();
        var bar = new TitleBar { Title = Title, ShowMinimize = false, ShowMaximize = false };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 760 });
        Content = root;
        UpdateState();
    }

    private void UpdateState()
    {
        // A new certificate needs the enrollment fields; a plain update keeps the device's settings.
        var fresh = _newIdentity.IsChecked == true;
        _label.IsEnabled = _port.IsEnabled = _groups.IsEnabled = _firewall.IsEnabled = fresh;
        // AUTOSIGNIN goes on msiexec's command line: only the silent ways can pass it.
        if (_autoSignIn is not null) _autoSignIn.IsEnabled = _viaRemoting?.IsChecked == true || _viaAgent?.IsChecked == true;
    }

    private void Start()
    {
        if (!int.TryParse(_port.Text.Trim(), out var port) || port is < 1 or > 65535) { Fail("The port must be a number from 1 to 65535."); return; }
        if (!AgentEnrollment.IsValidAddressList(_firewall.Text)) { Fail("\"Allow connections from\" must be IP addresses or ranges, comma-separated (or empty for any)."); return; }
        if (string.IsNullOrWhiteSpace(_label.Text)) { Fail("Enter a device label."); return; }

        var method = _viaAgent.IsChecked == true ? AgentInstallMethod.ThroughAgent
                   : _viaRemoting.IsChecked == true ? AgentInstallMethod.Remoting
                   : _viaFiles.IsChecked == true ? AgentInstallMethod.SaveFiles
                   : AgentInstallMethod.Clipboard;
        var options = new AgentInstallOptions(method, _newIdentity.IsChecked == true, _label.Text.Trim(), port, _firewall.Text.Trim(),
            AgentInstaller.ParseGroups(_groups.Text), _useAfter.IsChecked == true, _virtualDisplay.IsChecked == true,
            // Ticked: install and allow. Unticked: removed only if RemoteHub had installed it; otherwise left as the device has it.
            AutoSignIn: _autoSignIn.Visibility != Visibility.Visible || !_autoSignIn.IsEnabled ? null
                : _autoSignIn.IsChecked == true ? true : _hadProvider == true ? false : null);

        Options = options;
        DialogResult = true;
    }

    private void Fail(string message) => _error.Text = message;

    private TextBox Field(string label, string value, string hint)
    {
        _options.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
        var box = new TextBox { Text = value };
        _options.Children.Add(box);
        _options.Children.Add(Muted(hint, 0, 3, 0, 0));
        return box;
    }

    private RadioButton Radio(string text, bool enabled, bool selected)
    {
        var r = new RadioButton { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsEnabled = enabled, IsChecked = selected, GroupName = "how", Margin = new Thickness(0, 0, 0, 6) };
        _options.Children.Add(r);
        return r;
    }

    private static TextBlock Muted(string text, double l, double t, double r, double b)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(l, t, r, b) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return tb;
    }
}
