using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using RemoteHub.Models;
using RemoteHub.Rdp;
using RemoteHub.Remoting;
using RemoteHub.Services;
using RemoteHub.Views;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace RemoteHub;

/// <summary>
/// Jump hosts: a connection placed under another connection is reached through that connection's RemoteHub Agent – the
/// agent's own connections and WebDAV shares in-process, RDP, PowerShell remoting (WinRM), SMB and SFTP shares through a
/// local port (<see cref="JumpForwarders"/>).
/// </summary>
public partial class MainWindow
{
    /// <summary>Passwords entered for jump hosts (memory only), so each is asked once.</summary>
    private readonly Dictionary<Guid, CredentialSet> _jumpCredentials = new();

    /// <summary>
    /// The jump host route of <paramref name="c"/>: true with null when it is reached directly, true with the route, or false
    /// when it can't be used now (said in a dialog unless <paramref name="quiet"/>; quiet never asks for a password).
    /// </summary>
    private bool TryRoute(Connection c, bool quiet, out JumpRoute? route)
    {
        route = null;
        if (c.JumpHost is not { } j) return true;
        if (Features.WhyNot(Feature.JumpHosts) is { } off)
        {
            if (!quiet) _ = Dialogs.Info(this, "Jump host", $"{c.Name} is reached through {j.Name}. {off}");
            return false;
        }
        if (j.Agent is not { Fingerprint.Length: > 0 } agent)
        {
            if (!quiet)
                _ = Dialogs.Info(this, "Jump host", $"{c.Name} is reached through {j.Name}, but {j.Name} has no RemoteHub Agent. Install it there " +
                                                    $"(right-click {j.Name} → Install RemoteHub Agent…), or move {c.Name} out of {j.Name} to connect directly.");
            return false;
        }
        var (resolved, _) = Inheritance.ResolveCredentials(j, _vm.Data);
        var cred = resolved.Clone();
        if (cred.IsEmpty)
        {
            if (!quiet)
                _ = Dialogs.Info(this, "Jump host", $"No credentials are set for the jump host {j.Name}. Its RemoteHub Agent signs in with a Windows " +
                                                    $"account of {j.Name} (an administrator there) – set credentials on {j.Name} or its folder.");
            return false;
        }
        if (PsRemoting.NeedsPassword(cred))
        {
            if (_jumpCredentials.TryGetValue(j.Id, out var known) && known.Username == cred.Username && known.Domain == cred.Domain)
                cred.Password = known.Password;
            else if (KnownTarget(j) is { Agent: not null, Credentials: { Password.Length: > 0 } open } && open.Username == cred.Username && open.Domain == cred.Domain)
                cred.Password = open.Password;
            else if (quiet) return false;
            else
            {
                var dlg = new PasswordDialog(confirm: false, title: "Jump host",
                    message: $"No password is stored for {PsRemoting.UserName(cred, j.Host)} on the jump host {j.Name}. Enter it to connect to {c.Name} " +
                             "through it – it is kept in memory only.",
                    okText: "Connect") { Owner = this };
                if (dlg.ShowDialog() != true) return false;
                cred.Password = Secret.From(dlg.Password);
            }
            _jumpCredentials[j.Id] = cred;
        }
        route = new JumpRoute(j.Name, j.Host.Trim(), new AgentEndpoint(agent.Port, agent.Fingerprint), PsRemoting.AgentCredential(cred)!);
        return true;
    }

    /// <summary>Where <paramref name="device"/>'s agent is reached for <paramref name="c"/> – through its jump host, if any (null: not now).</summary>
    private AgentEndpoint? EndpointOf(Connection c, AgentDevice device, bool quiet = true) =>
        c is { Broker.Kind: BrokerItemKind.Device, JumpHost: null } ? BrokerEndpointOf(c, device, quiet)
        : TryRoute(c, quiet, out var via) ? new AgentEndpoint(device.Port, device.Fingerprint, via) : null;

    /// <summary>
    /// How an RDP session reaches <paramref name="c"/>: directly, or through a local port that leads through its jump host. The
    /// RDP control then only sees 127.0.0.1 and can't check the server's name, so the certificate the server presents through
    /// the jump host is checked here first: one this PC trusts for the name is fine; otherwise the one the user trusted for this
    /// connection (asked the first time, and when it changes). Null: not now (cancelled, or said in a dialog).
    /// </summary>
    private async Task<RdpRoute?> RdpRouteAsync(Connection c)
    {
        if (c is { Broker.Kind: BrokerItemKind.Device, JumpHost: null }) return await BrokerRdpRouteAsync(c);
        if (c.JumpHost is not { } jump) return RdpRoute.Direct;
        if (!TryRoute(c, quiet: false, out var via) || via is null) return null;
        var host = c.Host.Trim();
        (X509Certificate2 Certificate, bool Valid)? presented;
        try
        {
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var stream = await JumpTunnels.OpenAsync(via, host, c.Port, timeout.Token);
            presented = await ServerCertificates.RdpAsync(stream, host, timeout.Token);
        }
        catch (JumpHostException) { throw; }
        catch (Exception ex) { throw new IOException($"{c.Address} through {jump.Name}: {ex.GetBaseException().Message}", ex); }

        if (presented is not ({ } cert, var valid)) return new RdpRoute(JumpForwarders.Open(via, host, c.Port), false); // old RDP security: the control warns
        using (cert)
        {
            var pin = ServerCertificates.Pin(cert);
            if (!valid && pin != c.RdpCertificate)
            {
                var changed = c.RdpCertificate is not null;
                var what = $"Issued to: {cert.GetNameInfo(X509NameType.SimpleName, false)}\nIssued by: {cert.GetNameInfo(X509NameType.SimpleName, true)}\n" +
                           $"Valid until: {cert.NotAfter:d}\nThumbprint: {RemotingSetup.Grouped(cert.Thumbprint)}";
                var check = $"To compare, run this in PowerShell on {c.Name}:\n" +
                            "(Get-CimInstance -Namespace root\\cimv2\\TerminalServices -ClassName Win32_TSGeneralSetting).SSLCertificateSHA1Hash";
                if (!await Dialogs.Confirm(this, changed ? "RDP certificate changed" : "Trust this RDP certificate?",
                        changed
                            ? $"{c.Name} presents a different RDP certificate than the one you trusted before:\n\n{what}\n\n" +
                              $"Continue only if its certificate was renewed or replaced. {check}"
                            : $"{c.Name} is reached through {jump.Name}, so the RDP client can't check its certificate by name. It presents:\n\n{what}\n\n" +
                              $"RemoteHub remembers it and connects without asking while it stays the same. {check}",
                        changed ? "Trust the new one" : "Trust and connect"))
                    return null;
                c.RdpCertificate = pin;
                _vm.RequestSave();
            }
        }
        return new RdpRoute(JumpForwarders.Open(via, host, c.Port), CertificateChecked: true);
    }

    /// <summary>
    /// For the agent's firewall scope: the address a device behind a jump host sees connections from – the jump host's own
    /// address in that network, which its agent reports for a connection attempt (nothing needs to listen there).
    /// </summary>
    private async Task<string?> JumpLocalAddressAsync(Connection c, int port)
    {
        if (!TryRoute(c, quiet: false, out var via) || via is null) return null;
        try
        {
            // Only asked, not connected: no agent listens there yet, and a firewall that drops the attempt would take long.
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await JumpTunnels.LocalAddressAsync(via, c.Host.Trim(), port, timeout.Token);
        }
        catch { return null; }
    }

    /// <summary>Context menu "Move under": the jump hosts (and connections with an agent) an item can be reached through.</summary>
    private void FillMoveUnder(MenuItem menu, NodeBase node)
    {
        menu.Items.Clear();
        if (node.Parent is Connection current)
            menu.Items.Add(MoveEntry($"Out of {current.Name} – reached directly", SymbolRegular.ArrowExit20, node, current.Parent,
                $"Moves it next to {current.Name}."));
        var candidates = _vm.AllNodes().OfType<Connection>()
            .Where(c => c != node.Parent && (c.Children.Count > 0 || c.Agent is { Fingerprint.Length: > 0 }) && WhyNotMove(node, c) is null)
            .OrderByDescending(c => c.Children.Count > 0).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(40).ToList();
        if (candidates.Count > 0 && menu.Items.Count > 0) menu.Items.Add(new Separator());
        foreach (var c in candidates)
            menu.Items.Add(MoveEntry(c.Name, c.Children.Count > 0 ? SymbolRegular.Router24 : SymbolRegular.Desktop24, node, c,
                $"{Inheritance.Location(c)}{(c.Children.Count > 0 ? $" · jump host for {c.Children.Count} item(s)" : "")}"));
        if (menu.Items.Count == 0)
            menu.Items.Add(MenuEntry("No connection with a RemoteHub Agent to go through", SymbolRegular.Info24, () => { }, enabled: false,
                tip: "Install the RemoteHub Agent on the connection that should be the jump host, or drag this item onto any connection."));
        // Off for a jump host (or a folder holding one) – it can't be below another one; the tooltip says why.
        var why = node.Parent is Connection ? null : TreeLayout.WhyNot(node, new Connection());
        menu.IsEnabled = why is null;
        menu.ToolTip = why ?? "Reach it through another connection's RemoteHub Agent (a jump host) – or directly again";
        ToolTipService.SetShowOnDisabled(menu, true);
    }

    private MenuItem MoveEntry(string header, SymbolRegular icon, NodeBase node, NodeBase? into, string tip) =>
        MenuEntry(header, icon, () => _ = MoveUnder(node, into), tip: tip);

    /// <summary>
    /// Why <paramref name="node"/> can't go into <paramref name="into"/>: a read-only tree, the tree's rules, and – with jump hosts blocked by this
    /// PC's policy – not below a connection. Moving out of a jump host stays possible.
    /// </summary>
    private static string? WhyNotMove(NodeBase node, NodeBase? into) =>
        Features.WhyNot(Feature.ConnectionEditing) ?? TreeLayout.WhyNot(node, into) ?? (into is Connection || into?.JumpHost is not null ? Features.WhyNot(Feature.JumpHosts) : null);

    private async Task MoveUnder(NodeBase node, NodeBase? into)
    {
        if (Refused(Feature.ConnectionEditing)) return; // read-only tree by policy (docs/admx-design.md §5.4)
        if (WhyNotMove(node, into) is { } why) { await Dialogs.Info(this, "Move", why); return; }
        if (WhyNotBrokerChange(node) is { } broker) { await Dialogs.Info(this, "Move", broker); return; }
        if (!await ConfirmMove(node, into)) return;
        _vm.Move(node, into);
        node.IsSelected = true;
        await PublishLayout(node);
    }
}
