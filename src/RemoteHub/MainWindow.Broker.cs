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

namespace RemoteHub;

/// <summary>
/// The RemoteHub Broker in the main window (broker-design.md §11): one client per connected organisation, its synced
/// folder in the navigation, and how the devices in it are reached – directly, through the broker, or the first that
/// works (Reach). RDP to a device reached through the broker goes through its own agent's tunnel ("self", §9.1).
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

    /// <summary>Disconnect: removes this PC from the organisation (best effort), its key, its cache and the synced folder.</summary>
    internal async Task DisconnectBrokerAsync(BrokerClient client)
    {
        try { if (client.IsConnected) await client.RemoveConsoleAsync(client.Info.ConsoleId); }
        catch (BrokerException ex) { DiagLog.Write("Broker: removing this console failed: " + ex.Message); }
        _brokers.Remove(client.Info.Id);
        await client.DisposeAsync();
        PeerSessions.CloseAll();
        BrokerStore.Remove(client.Info);
        Sync.Remove(client.Info);
    }

    private async Task StopBrokersAsync()
    {
        PeerSessions.CloseAll();
        foreach (var c in _brokers.Values.ToList()) await c.DisposeAsync();
        _brokers.Clear();
    }

    // ---- How a synced device is reached (Reach, design §11.1) --------------------------------------------------------

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
