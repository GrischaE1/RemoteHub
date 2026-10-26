using System.Management.Automation.Runspaces;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using RemoteHub.Agent;
using RemoteHub.Broker;
using RemoteHub.Models;
using RemoteHub.Rdp;
using RemoteHub.Remoting;
using RemoteHub.Services;
using RemoteHub.Views;
using Wpf.Ui.Controls;

namespace RemoteHub;

/// <summary>
/// The RemoteHub Broker in the main window: one client per connected organisation, its synced
/// folder in the navigation, and how the devices in it are reached – directly, through the broker, or the first that
/// works (Reach). RDP to a device reached through the broker goes through its own agent's tunnel ("self").
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, BrokerClient> _brokers = new();
    private BrokerSync? _brokerSync;

    private BrokerSync Sync => _brokerSync ??= new BrokerSync(_vm.Items, _vm.RequestSave);

    /// <summary>At start: a client for every broker this PC is connected to (if this PC's policy allows the broker).</summary>
    private void StartBrokers()
    {
        if (Features.WhyNot(Feature.Broker) is not null) return;
        List<BrokerConnection> all;
        try { all = BrokerStore.All(); }
        catch (Exception ex) { DiagLog.Write("Broker: connections not read: " + ex.Message); return; }
        foreach (var b in all) StartBroker(b);
    }

    internal BrokerClient StartBroker(BrokerConnection b)
    {
        Sync.Root(b);
        var client = BrokerClient.Start(b);
        client.Changed += (c, items) => Dispatcher.BeginInvoke(() => Sync.Apply(c, items));
        client.Presence += (c, id, online, _) => Dispatcher.BeginInvoke(() => Sync.Presence(c.Info, id, online));
        client.StateChanged += c => Dispatcher.BeginInvoke(() =>
        {
            Sync.SetConnected(c.Info, c.IsConnected);
            BrokerStateChanged?.Invoke(c);
        });
        _brokers[b.Id] = client;
        return client;
    }

    /// <summary>For the settings page: a connection's state changed.</summary>
    internal event Action<BrokerClient>? BrokerStateChanged;

    internal IReadOnlyCollection<BrokerClient> Brokers => _brokers.Values;

    /// <summary>Settings → RemoteHub Broker: tells <paramref name="changed"/> about state changes until disposed.</summary>
    private IDisposable WatchBrokers(Action<BrokerClient> changed)
    {
        BrokerStateChanged += changed;
        return new Unsubscribe(() => BrokerStateChanged -= changed);
    }

    private sealed class Unsubscribe(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>Settings → RemoteHub Broker → Connect: registers this PC and starts its client (throws <see cref="BrokerException"/>).</summary>
    private async Task<BrokerClient> ConnectBrokerAsync(string url, string consoleSecret, string name)
    {
        if (Features.WhyNot(Feature.Broker) is { } off) throw new BrokerException(off);
        if (BrokerProtocol.ParseSecret(consoleSecret) is { } s)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(s.Key);
            if (_brokers.Values.FirstOrDefault(c => c.Info.Org == s.Org) is { } known)
                throw new BrokerException($"This PC is already connected to {(known.Info.OrgName.Length > 0 ? known.Info.OrgName : s.Org)}.");
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var joined = await BrokerClient.JoinAsync(url, consoleSecret, name, timeout.Token);
        Data.AuditLog.Write(Data.AuditEvents.BrokerConnected, null, $"{joined.Org} on {new Uri(joined.Url).Authority}");
        return StartBroker(joined);
    }

    /// <summary>Disconnect: removes this PC from the organisation (best effort), its key, its cache and the synced folder.</summary>
    internal async Task DisconnectBrokerAsync(BrokerClient client)
    {
        try { if (client.IsConnected) await client.RemoveConsoleAsync(client.Info.ConsoleId); }
        catch (BrokerException ex) { DiagLog.Write($"Broker: removing this console failed ({ex.Code ?? "no answer"})"); } // its message names the broker
        _brokers.Remove(client.Info.Id);
        await client.DisposeAsync();
        PeerSessions.CloseAll();
        BrokerStore.Remove(client.Info);
        Sync.Remove(client.Info);
        Data.AuditLog.Write(Data.AuditEvents.BrokerDisconnected, null, $"{client.Info.Org} on {new Uri(client.Info.Url).Authority}");
    }

    private async Task StopBrokersAsync()
    {
        PeerSessions.CloseAll();
        foreach (var c in _brokers.Values.ToList()) await c.DisposeAsync();
        _brokers.Clear();
    }

    // ---- The shared layout: what changes here goes to the broker, sealed -----------------------------------------------

    /// <summary>Why an item of a synced folder can't be changed now – only while connected to its broker. Null: it can, or it isn't one.</summary>
    private string? WhyNotBrokerChange(NodeBase node)
    {
        if (node.BrokerRoot is not { Broker: { } root } folder) return null;
        if (Features.WhyNot(Feature.Broker) is { } off) return off;
        if (!_brokers.TryGetValue(root.Connection, out var client))
            return $"This PC isn't connected to the RemoteHub Broker of {folder.Name}. Connect in Settings → RemoteHub Broker.";
        return client.IsConnected ? null : $"The synced folder changes only while RemoteHub is connected to its broker. {client.State}";
    }

    /// <summary>
    /// After a folder was made, renamed or moved, or a device was moved or renamed in a synced folder: its entry at the
    /// broker, with a higher version, for every console of the organisation. A device placed out of New devices by a
    /// rename lands at the top of the synced folder, as the other consoles will show it.
    /// </summary>
    private async Task PublishLayout(NodeBase node)
    {
        if (node.Broker is not { Kind: BrokerItemKind.Folder or BrokerItemKind.Device } r || !_brokers.TryGetValue(r.Connection, out var client)) return;
        if (node is Connection && node.Parent is Folder { Broker.Kind: BrokerItemKind.NewDevices } && node.BrokerRoot is { } root) _vm.Move(node, root);
        var v = Math.Max(r.Version + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var parent = node.Parent is Folder { Broker: { Kind: BrokerItemKind.Folder } p } ? p.Item : null;
        var (kind, body) = node is Folder
            ? ("folder", new JsonObject { ["id"] = r.Item, ["name"] = node.Name, ["parent"] = parent, ["deleted"] = false, ["v"] = v })
            : ("place", new JsonObject { ["id"] = r.Item, ["folder"] = parent, ["label"] = node.Name, ["v"] = v });
        try
        {
            await client.WriteAsync([(kind, body)]);
            r.Version = v;
            _vm.RequestSave();
        }
        catch (Exception ex)
        {
            await Dialogs.Info(this, "RemoteHub Broker", $"The change to {node.Name} wasn't saved at the broker, so the other consoles don't see it: {ex.Message}");
        }
    }

    /// <summary>Delete in a synced folder: a folder for every console (its contents move up), a device removed from the broker.</summary>
    private async Task DeleteBrokerItem(NodeBase n)
    {
        switch (n.Broker?.Kind)
        {
            case BrokerItemKind.Root:
                await Dialogs.Info(this, "Delete", $"{n.Name} is the synced folder of a RemoteHub Broker. To remove it, disconnect in Settings → RemoteHub Broker.");
                return;
            case BrokerItemKind.NewDevices:
                await Dialogs.Info(this, "Delete", $"{n.Name} is where devices land when they register – it can't be deleted.");
                return;
        }
        var why = WhyNotBrokerChange(n);
        if (why is not null || n.Broker is not { } r || !_brokers.TryGetValue(r.Connection, out var client))
        {
            await Dialogs.Info(this, "Delete", why ?? "This item isn't known to the broker.");
            return;
        }
        if (n is Folder f)
        {
            var into = f.Parent;
            if (!await Dialogs.Confirm(this, "Delete", $"Delete folder \"{f.Name}\" for every console of the organisation? The devices and folders in it move up to \"{into?.Name}\".", "Delete"))
                return;
            var v = Math.Max(r.Version + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var parent = into is Folder { Broker: { Kind: BrokerItemKind.Folder } p } ? p.Item : null;
            try { await client.WriteAsync([("folder", new JsonObject { ["id"] = r.Item, ["name"] = f.Name, ["parent"] = parent, ["deleted"] = true, ["v"] = v })]); }
            catch (Exception ex) { await Dialogs.Info(this, "Delete", $"\"{f.Name}\" wasn't deleted at the broker: {ex.Message}"); return; }
            foreach (var child in f.Children.ToList()) _vm.Move(child, into);
            _vm.Remove(f);
            return;
        }
        if (!await Dialogs.Confirm(this, "Remove from the RemoteHub Broker",
                $"Remove {n.Name} from the RemoteHub Broker? It disappears for every console of the organisation and can no longer be reached " +
                "through the broker. To bring it back, join its agent to the broker again.", "Remove"))
            return;
        try { await client.RemoveDeviceAsync(r.Item); }
        catch (Exception ex) { await Dialogs.Info(this, "Remove from the RemoteHub Broker", $"{n.Name} wasn't removed: {ex.Message}"); return; }
        _vm.Remove(n);
    }

    // ---- Assign to RemoteHub Broker ------------------------------------------------------------------------------------

    /// <summary>
    /// The connection menu's entry: for a connection of the user's own tree whose agent is installed, while this PC is
    /// connected to a broker – one entry per organisation when there are several.
    /// </summary>
    private void FillAssignToBroker(Connection? c)
    {
        CmAgentBroker.Items.Clear();
        var connected = _brokers.Values.Where(b => b.IsConnected).OrderBy(b => b.Info.OrgName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var offered = c is { Agent: { Status: AgentStatus.Installed, Fingerprint.Length: > 0 }, BrokerRoot: null } && connected.Count > 0
                      && Features.WhyNot(Feature.Broker) is null;
        CmAgentBroker.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
        // A group opening on hover (like Move under) has no "…"; a single entry opens the question.
        CmAgentBroker.Header = connected.Count > 1 ? "Assign to RemoteHub Broker" : "Assign to RemoteHub Broker…";
        if (!offered || connected.Count == 1) return;
        foreach (var b in connected)
            CmAgentBroker.Items.Add(MenuEntry(b.Info.OrgName.Length > 0 ? b.Info.OrgName : b.Info.Org, SymbolRegular.Building24, () => _ = AssignToBroker(c!, b),
                tip: new Uri(b.Info.Url).Authority));
    }

    private void CmAgentBroker_Click(object sender, RoutedEventArgs e)
    {
        // With several organisations the entries below it handle the click; this one only when it stands alone.
        if (e.OriginalSource != CmAgentBroker || _vm.SelectedNode is not Connection c) return;
        if (_brokers.Values.FirstOrDefault(b => b.IsConnected) is { } only) _ = AssignToBroker(c, only);
    }

    /// <summary>Joins <paramref name="c"/>'s agent to <paramref name="client"/>'s organisation: <c>broker join</c> on the device, through its agent.</summary>
    private async Task AssignToBroker(Connection c, BrokerClient client)
    {
        const string title = "Assign to RemoteHub Broker";
        var org = client.Info.OrgName.Length > 0 ? client.Info.OrgName : client.Info.Org;
        if (client.DeviceSecret is not { } secret)
        {
            await Dialogs.Info(this, title, $"This console has no device secret of {org} – ask your administrator for a current console secret.");
            return;
        }
        if (!await Dialogs.Confirm(this, title,
                $"{c.Name}'s RemoteHub Agent joins {org} at the RemoteHub Broker {new Uri(client.Info.Url).Authority}. Every console of the organisation " +
                "then finds it in the synced folder (New devices) and can reach it from anywhere. This connection stays as it is.", "Assign"))
            return;
        if (ToolTarget(c, KnownTarget(c)) is not { } t) return;
        string? said = null, failure = null;
        Runspace? rs = null;
        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            rs = await Task.Run(() => PsRemoting.Open(t.Target));
            var open = rs;
            said = await Task.Run(() => AgentInstaller.JoinBroker(open, client.Info.Url, secret));
        }
        catch (RemoteCommandException ex) { failure = ex.Message; }
        catch (Exception ex) { failure = PsRemoting.Explain(ex, t.Target); }
        finally
        {
            Cursor = null;
            if (rs is { } done) _ = Task.Run(() => PsSession.CloseQuietly(done));
        }
        DiagLog.Write($"Assign to RemoteHub Broker: {(failure is null ? "joined" : "failed")}");
        Data.AuditLog.Write(Data.AuditEvents.DeviceAction, c, failure is null ? $"Assigned to RemoteHub Broker {client.Info.Org}" : $"Assigning to RemoteHub Broker {client.Info.Org} failed");
        await Dialogs.Info(this, title, failure is null ? $"{c.Name} joined {org}. {said}" : $"{c.Name} didn't join {org}.\n\n{failure}");
    }

    // ---- How a synced device is reached (Reach) ------------------------------------------------------------------------

    /// <summary>The broker route for a device of a synced folder, or null (not a broker device, or not usable – said unless quiet).</summary>
    private BrokerRoute? BrokerRouteFor(Connection c, bool quiet)
    {
        if (c.Broker is not { Kind: BrokerItemKind.Device } r) return null;
        if (!r.Sealed || c.Agent is not { Fingerprint.Length: > 0 })
        {
            if (!quiet) _ = Dialogs.Info(this, "RemoteHub Broker", $"{c.Name}'s record isn't sealed by your organisation, so RemoteHub doesn't connect to it. " +
                                                                   "The broker may have been tampered with – tell your administrator.");
            return null;
        }
        if (Features.WhyNot(Feature.Broker) is { } off)
        {
            if (!quiet) _ = Dialogs.Info(this, "RemoteHub Broker", off);
            return null;
        }
        if (!_brokers.TryGetValue(r.Connection, out var client))
        {
            if (!quiet) _ = Dialogs.Info(this, "RemoteHub Broker", $"This PC isn't connected to the RemoteHub Broker of {c.BrokerRoot?.Name}. Connect in Settings → RemoteHub Broker.");
            return null;
        }
        return new BrokerRoute(client, r.Item, r.SecretId, c.Name);
    }

    private BrokerReach ReachOf(Connection c) => Inheritance.ResolveOptions(c, _vm.Data).Reach ?? BrokerReach.Automatic;

    /// <summary>The agent endpoint of a device in a synced folder: Reach decides (null when it can't be reached now).</summary>
    private AgentEndpoint? BrokerEndpointOf(Connection c, AgentDevice device, bool quiet)
    {
        var reach = ReachOf(c);
        if (reach == BrokerReach.Direct) return new AgentEndpoint(device.Port, device.Fingerprint);
        if (BrokerRouteFor(c, quiet) is not { } route)
            return reach == BrokerReach.Automatic && c.Agent is { Fingerprint.Length: > 0 } && c.Broker is { Sealed: true } ? new AgentEndpoint(device.Port, device.Fingerprint) : null;
        return new AgentEndpoint(device.Port, device.Fingerprint, Broker: route, DirectFirst: reach == BrokerReach.Automatic);
    }

    /// <summary>
    /// RDP to a device of a synced folder. Directly when Reach says so or its RDP port answers (Automatic, 1.5 s); otherwise
    /// through the broker into its own agent, whose tunnel opens the device's own RDP port ("self", policy TunnelOwnPorts).
    /// The RDP certificate is then checked here, as through a jump host. Null: not now.
    /// </summary>
    private async Task<RdpRoute?> BrokerRdpRouteAsync(Connection c)
    {
        var reach = ReachOf(c);
        if (reach == BrokerReach.Direct) return RdpRoute.Direct;
        if (reach == BrokerReach.Automatic && await AnswersAsync(c.Host.Trim(), c.Port, TimeSpan.FromMilliseconds(1500))) return RdpRoute.Direct;
        if (BrokerRouteFor(c, quiet: false) is not { } broker) return null;
        if (DeviceAsJumpHost(c, broker) is not { } self) return null;

        (X509Certificate2 Certificate, bool Valid)? presented;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var stream = await JumpTunnels.OpenAsync(self, TunnelOpen.Self, 0, timeout.Token);
            presented = await ServerCertificates.RdpAsync(stream, c.Host.Trim(), timeout.Token);
        }
        catch (JumpHostException) { throw; }
        catch (Broker.BrokerException) { throw; }
        catch (Exception ex) { throw new IOException($"{c.Name} through the RemoteHub Broker: {ex.GetBaseException().Message}", ex); }

        if (presented is not ({ } cert, var valid)) return new RdpRoute(JumpForwarders.Open(self, TunnelOpen.Self, 0), false);
        using (cert)
        {
            var pin = ServerCertificates.Pin(cert);
            if (!valid && pin != c.RdpCertificate)
            {
                var changed = c.RdpCertificate is not null;
                var what = $"Issued to: {cert.GetNameInfo(X509NameType.SimpleName, false)}\nIssued by: {cert.GetNameInfo(X509NameType.SimpleName, true)}\n" +
                           $"Valid until: {cert.NotAfter:d}\nThumbprint: {RemotingSetup.Grouped(cert.Thumbprint)}";
                if (!await Dialogs.Confirm(this, changed ? "RDP certificate changed" : "Trust this RDP certificate?",
                        changed
                            ? $"{c.Name} presents a different RDP certificate than the one you trusted before:\n\n{what}\n\nContinue only if its certificate was renewed or replaced."
                            : $"{c.Name} is reached through the RemoteHub Broker, so the RDP client can't check its certificate by name. It presents:\n\n{what}\n\n" +
                              "RemoteHub remembers it and connects without asking while it stays the same.",
                        changed ? "Trust the new one" : "Trust and connect"))
                    return null;
                c.RdpCertificate = pin;
                _vm.RequestSave();
            }
        }
        return new RdpRoute(JumpForwarders.Open(self, TunnelOpen.Self, 0), CertificateChecked: true);
    }

    /// <summary>The device's own agent as the "jump host" to itself, signed in with the device's own credentials.</summary>
    private JumpRoute? DeviceAsJumpHost(Connection c, BrokerRoute broker)
    {
        var (resolved, _) = Inheritance.ResolveCredentials(c, _vm.Data);
        var cred = resolved.Clone();
        if (cred.IsEmpty || PsRemoting.NeedsPassword(cred))
        {
            _ = Dialogs.Info(this, "RemoteHub Broker", $"RDP to {c.Name} through the RemoteHub Broker signs in to its RemoteHub Agent with the connection's credentials – " +
                                                       "set a user name and password on it or its folder.");
            return null;
        }
        return new JumpRoute(c.Name, c.Host.Trim(), new AgentEndpoint(c.Agent!.Port, c.Agent.Fingerprint, Broker: broker), PsRemoting.AgentCredential(cred)!);
    }

    private static async Task<bool> AnswersAsync(string host, int port, TimeSpan within)
    {
        if (host.Length == 0) return false;
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(within);
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch { return false; }
    }
}
