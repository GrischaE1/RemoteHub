using System.Windows;
using System.Windows.Controls;
using RemoteHub.Remoting;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>
/// After a failed PowerShell remoting test: builds a ready-to-run setup script for the target (with this PC's
/// address and, for HTTPS, a certificate made for that target) and copies it for the admin to paste and run there.
/// </summary>
public sealed class RemotingSetupDialog : FluentWindow
{
    private const int ClipboardSeconds = 120;
    private readonly string _connectionName, _host;
    private readonly RadioButton _https = new() { Content = "HTTPS with a new certificate for this computer (recommended – works with IP addresses and computers outside the domain)", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) };
    private readonly RadioButton _http = new() { Content = "HTTP (domain computers reached by name – Kerberos)", Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBox _allow;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Button _test;
    private ListenerCertificate? _cert;
    private bool _trusted;

    /// <summary>True when the HTTPS script was handed out and this PC trusts its certificate: the connection should use HTTPS.</summary>
    public bool SwitchToHttps { get; private set; }
    /// <summary>Thumbprint of the certificate this PC now trusts (for the connection's status).</summary>
    public string? TrustedThumbprint => _trusted ? _cert?.Thumbprint : null;

    public RemotingSetupDialog(string connectionName, string host, string? lastError, string? detectedAddress)
    {
        _connectionName = connectionName;
        _host = host;
        Title = "Set up PowerShell remoting";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        ShowInTaskbar = false;

        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 20) };
        body.Children.Add(new TextBlock { Text = $"{connectionName}  ·  {host}", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        if (!string.IsNullOrWhiteSpace(lastError))
            body.Children.Add(Muted("Last test: " + lastError, 0, 0, 0, 12));
        body.Children.Add(Muted("RemoteHub creates a setup script for this computer. Paste it into an elevated PowerShell on the target " +
                                "(e.g. inside its RDP session) and run it. RemoteHub does not run anything on the target itself.", 0, 0, 0, 14));

        body.Children.Add(new TextBlock { Text = "Connection type", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        _https.Content = new TextBlock { Text = (string)_https.Content, TextWrapping = TextWrapping.Wrap };
        _http.Content = new TextBlock { Text = (string)_http.Content, TextWrapping = TextWrapping.Wrap };
        body.Children.Add(_https);
        body.Children.Add(_http);

        body.Children.Add(new TextBlock { Text = "Allow connections from", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        _allow = new TextBox { Text = string.Join(", ", RemotingSetup.AllLocalAddresses(detectedAddress)), PlaceholderText = "e.g. 192.168.1.20 or 10.0.0.0/24", TextWrapping = TextWrapping.Wrap };
        body.Children.Add(_allow);
        body.Children.Add(Muted(detectedAddress is null
            ? "All addresses of this PC are filled in. With NAT or VPN in between, add the address the target sees, or a subnet (comma-separated)."
            : $"All addresses of this PC (it uses {detectedAddress} to reach {host}). With NAT or VPN in between, add the address the target sees, or a subnet. Only these addresses get a firewall rule.",
            0, 4, 0, 0));

        body.Children.Add(Muted("HTTPS: the certificate is made for this target only (not a CA, server authentication only, 2 years). " +
                                "Its private key is only in the script – RemoteHub does not keep it. This PC trusts the certificate " +
                                "for your Windows account (Windows asks you to confirm), and the connection switches to HTTPS. " +
                                $"The script is copied without clipboard history and cleared after {ClipboardSeconds} s.", 0, 12, 0, 0));

        body.Children.Add(_status);

        var copy = new Button { Content = "Copy script", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.Copy24), MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => Produce(toClipboard: true);
        var save = new Button { Content = "Save as .ps1…", Icon = new SymbolIcon(SymbolRegular.Save24), Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) => Produce(toClipboard: false);
        _test = new Button { Content = "Test again", Icon = new SymbolIcon(SymbolRegular.ArrowSync24), IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
        _test.Click += (_, _) => DialogResult = true;
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(copy);
        buttons.Children.Add(save);
        buttons.Children.Add(_test);
        buttons.Children.Add(close);
        body.Children.Add(buttons);

        var root = new StackPanel();
        root.Children.Add(new TitleBar { Title = Title, ShowMaximize = false, ShowMinimize = false });
        root.Children.Add(body);
        Content = root;
        _https.Checked += (_, _) => _status.Text = "";
        _http.Checked += (_, _) => _status.Text = "";
    }

    private static TextBlock Muted(string text, double l, double t, double r, double b)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(l, t, r, b) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return tb;
    }

    private void Produce(bool toClipboard)
    {
        if (!RemotingSetup.TryParseSources(_allow.Text, out var sources, out var error)) { Status(error!, true); return; }
        var https = _https.IsChecked == true;
        try
        {
            if (https)
            {
                _cert ??= RemotingSetup.CreateCertificate(_host, DateTimeOffset.Now);
                if (!_trusted)
                {
                    try { RemotingSetup.TrustOnThisPc(_cert, _host); }
                    catch (System.Security.Cryptography.CryptographicException)
                    {
                        Status("This PC does not trust the certificate (the Windows prompt was declined), so the HTTPS connection would fail. Nothing was copied.", true);
                        return;
                    }
                    _trusted = true;
                    DiagLog.Write("Remoting setup: certificate trusted for the current user");
                }
                SwitchToHttps = true;
            }
            var script = RemotingSetup.BuildScript(_connectionName, _host, sources, https ? _cert : null, DateTime.Now);

            if (toClipboard)
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (!SecureClipboard.CopySecret(hwnd, script, TimeSpan.FromSeconds(ClipboardSeconds))) { Status("The clipboard is in use by another program – try again.", true); return; }
                Status($"Copied. Open an elevated PowerShell on {_host}, paste (right-click or Ctrl+V) and press Enter. Then click \"Test again\".", false);
            }
            else
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PowerShell script (*.ps1)|*.ps1", FileName = $"Enable-Remoting-{RdpFileWriter.SafeFileName(_connectionName)}.ps1" };
                if (dlg.ShowDialog(this) != true) return;
                File.WriteAllText(dlg.FileName, script, new System.Text.UTF8Encoding(true));
                Status(https
                    ? $"Saved. The file contains the certificate's private key – delete it after running it on {_host}. Then click \"Test again\"."
                    : $"Saved. Run it in an elevated PowerShell on {_host}, then click \"Test again\".", false);
            }
            DiagLog.Write($"Remoting setup script produced ({(https ? "HTTPS" : "HTTP")}, {(toClipboard ? "clipboard" : "file")})");
            _test.IsEnabled = true;
        }
        catch (Exception ex) { Status("Could not create the script: " + ex.Message, true); }
    }

    private void Status(string text, bool error)
    {
        _status.Text = text;
        if (error) _status.Foreground = System.Windows.Media.Brushes.IndianRed;
        else _status.SetResourceReference(TextBlock.ForegroundProperty, "SystemAccentColorPrimaryBrush");
    }
}
