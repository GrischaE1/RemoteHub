extern alias agent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Agent;
using RemoteHub.Broker;
using RemoteHub.Data;
using RemoteHub.Remoting;

namespace RemoteHub.Tests;

/// <summary>
/// RemoteHub's side of the RemoteHub Broker against the real broker and an in-process agent: connecting with the console
/// secret, the keyring, the synced list with its seals checked here, and the agent's HTTPS endpoint reached through a
/// relayed or direct session. Skipped unless REMOTEHUB_BROKER_URL and REMOTEHUB_BROKER_TOKEN point at a broker.
/// </summary>
[Collection("SettingsStore")]
public class BrokerConsoleTests : IDisposable
{
    public BrokerConsoleTests()
    {
        Assert.StartsWith(Path.GetTempPath(), LocalDb.Directory); // isolation guard
        StoreReset.Clean();
        LocalDb.Open();
    }

    public void Dispose()
    {
        PeerSessions.CloseAll();
        StoreReset.Clean();
    }

    [SkippableFact]
    public async Task A_console_connects_with_the_console_secret_sees_the_sealed_device_and_reaches_its_agent()
    {
        var broker = TestBroker.FromEnvironment();
        Skip.If(broker is null, "No local RemoteHub Broker (REMOTEHUB_BROKER_URL, REMOTEHUB_BROKER_TOKEN).");
        var org = await broker.CreateOrgAsync();
        await using var agentHost = await TestBrokerAgent.StartAsync(broker, org);
        await broker.WaitForOnlineAsync(org, agentHost.DeviceId);

        var info = await BrokerClient.JoinAsync(broker.Url.ToString(), broker.ConsoleSecretText(org), "Test console", CancellationToken.None);
        Assert.StartsWith("Test ", info.OrgName);
        Assert.Single(BrokerStore.All());

        await using var client = BrokerClient.Start(info);
        var device = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Changed += (_, items) =>
        {
            foreach (var i in items)
                if (i["kind"]?.GetValue<string>() == "device" && i["id"]?.GetValue<string>() == agentHost.DeviceId) device.TrySetResult(i);
        };
        // The online state of every device arrives right after connecting (presenceList), not only with a change.
        var snapshot = new TaskCompletionSource<IReadOnlyList<(string DeviceId, bool Online)>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PresenceSnapshot += (_, states) => snapshot.TrySetResult(states);
        var item = await device.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(SpinWait.SpinUntil(() => client.IsConnected, TimeSpan.FromSeconds(10)), client.State); // connected once the first sync is in
        Assert.Contains((agentHost.DeviceId, true), await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        // The record's seal checks with the organisation's record key from the keyring; a changed name doesn't.
        var record = item["record"]!.AsObject();
        Assert.True(client.RecordIsSealed(record, item["seal"]!.GetValue<string>()));
        Assert.Equal(agentHost.Fingerprint, record["fingerprint"]!.GetValue<string>());
        var renamed = (JsonObject)record.DeepClone();
        renamed["computerName"] = "DC01";
        Assert.False(client.RecordIsSealed(renamed, item["seal"]!.GetValue<string>()));
        // The device secret for the install command and Assign to RemoteHub Broker comes from the same keyring.
        Assert.Equal(new BrokerProtocol.Secret(BrokerProtocol.SecretKind.Device, org.Id, org.DeviceSecretId, org.DeviceSecret).ToString(), client.DeviceSecret);

        // The agent's HTTPS endpoint through a relayed session, TLS to the pin from the sealed record.
        var route = new BrokerRoute(client, agentHost.DeviceId, item["secretId"]?.GetValue<string>(), "PC-0815", agentHost.Fingerprint);
        using var handler = AgentClientTransport.ThroughBroker(route, (_, cert, _, _) => cert is not null && AgentProtocol.Fingerprint(cert.GetRawCertData()) == agentHost.Fingerprint);
        using var http = new HttpClient(handler);
        var health = await http.GetStringAsync($"https://pc-0815:{AgentProtocol.DefaultPort}{AgentProtocol.HealthPath}");
        Assert.Contains(agentHost.DeviceId, health);
    }

    [SkippableFact]
    public async Task A_session_goes_direct_where_both_sides_can_and_stays_relayed_where_the_device_says_no()
    {
        var broker = TestBroker.FromEnvironment();
        Skip.If(broker is null, "No local RemoteHub Broker (REMOTEHUB_BROKER_URL, REMOTEHUB_BROKER_TOKEN).");
        Skip.IfNot(PeerDirect.Supported, "msquic needs Windows 11 or Server 2022 or later.");
        var org = await broker.CreateOrgAsync();
        var firewall = new TestFirewall();
        await using var direct = await TestBrokerAgent.StartAsync(broker, org, firewall: firewall);
        await using var relayOnly = await TestBrokerAgent.StartAsync(broker, org, () => agent::RemoteHub.Agent.AgentPolicy.Defaults(new agent::RemoteHub.Agent.AgentConfig()) with { BrokerDirect = 0 });
        await broker.WaitForOnlineAsync(org, direct.DeviceId);
        await broker.WaitForOnlineAsync(org, relayOnly.DeviceId);

        var info = await BrokerClient.JoinAsync(broker.Url.ToString(), broker.ConsoleSecretText(org), "Test console", CancellationToken.None);
        await using var client = BrokerClient.Start(info);
        var items = new System.Collections.Concurrent.ConcurrentDictionary<string, JsonObject>();
        client.Changed += (_, changed) =>
        {
            foreach (var i in changed)
                if (i["kind"]?.GetValue<string>() == "device") items[i["id"]!.GetValue<string>()] = i;
        };
        Assert.True(SpinWait.SpinUntil(() => client.IsConnected && items.ContainsKey(direct.DeviceId) && items.ContainsKey(relayOnly.DeviceId), TimeSpan.FromSeconds(20)), client.State);

        async Task<PeerPath?> Health(TestBrokerAgent host)
        {
            var route = new BrokerRoute(client, host.DeviceId, items[host.DeviceId]["secretId"]?.GetValue<string>(), "PC-0815", host.Fingerprint);
            using var handler = AgentClientTransport.ThroughBroker(route, (_, cert, _, _) => cert is not null && AgentProtocol.Fingerprint(cert.GetRawCertData()) == host.Fingerprint);
            using var http = new HttpClient(handler);
            Assert.Contains(host.DeviceId, await http.GetStringAsync($"https://pc-0815:{AgentProtocol.DefaultPort}{AgentProtocol.HealthPath}"));
            return PeerSessions.PathOf(route);
        }

        // Both on this PC: the host candidates answer, so the session is direct over the local network.
        Assert.Equal(PeerPath.DirectLocal, await Health(direct));
        // The device's firewall opened the punched port for that console's address only, and closes it with the session.
        var (port, from) = Assert.Single(firewall.Opened);
        Assert.True(port > 0 && from.Port > 0 && !from.Address.Equals(IPAddress.Any), $"{port} {from}");
        PeerSessions.CloseAll();
        Assert.True(SpinWait.SpinUntil(() => firewall.Closed.Contains(port), TimeSpan.FromSeconds(10)));
        // The device's policy BrokerDirect = 0: no candidates from it, the same request relayed.
        Assert.Equal(PeerPath.Relayed, await Health(relayOnly));
    }

    [SkippableFact]
    public async Task A_relayed_session_tries_again_and_new_streams_take_the_direct_path_once_it_is_up()
    {
        var broker = TestBroker.FromEnvironment();
        Skip.If(broker is null, "No local RemoteHub Broker (REMOTEHUB_BROKER_URL, REMOTEHUB_BROKER_TOKEN).");
        Skip.IfNot(PeerDirect.Supported, "msquic needs Windows 11 or Server 2022 or later.");
        var org = await broker.CreateOrgAsync();
        var level = 0; // relayed at first, as behind a network that blocks UDP
        await using var host = await TestBrokerAgent.StartAsync(broker, org, () => agent::RemoteHub.Agent.AgentPolicy.Defaults(new agent::RemoteHub.Agent.AgentConfig()) with { BrokerDirect = Volatile.Read(ref level) });
        await broker.WaitForOnlineAsync(org, host.DeviceId);
        var info = await BrokerClient.JoinAsync(broker.Url.ToString(), broker.ConsoleSecretText(org), "Test console", CancellationToken.None);
        await using var client = BrokerClient.Start(info);
        JsonObject? item = null;
        client.Changed += (_, changed) => { foreach (var i in changed) if (i["id"]?.GetValue<string>() == host.DeviceId) item = i; };
        Assert.True(SpinWait.SpinUntil(() => client.IsConnected && item is not null, TimeSpan.FromSeconds(20)), client.State);

        var route = new BrokerRoute(client, host.DeviceId, item!["secretId"]?.GetValue<string>(), "PC-0815", host.Fingerprint);
        using var handler = AgentClientTransport.ThroughBroker(route, (_, cert, _, _) => cert is not null && AgentProtocol.Fingerprint(cert.GetRawCertData()) == host.Fingerprint);
        handler.PooledConnectionLifetime = TimeSpan.Zero; // every request a new stream, so the second one can take the new path
        using var http = new HttpClient(handler);
        var health = $"https://pc-0815:{AgentProtocol.DefaultPort}{AgentProtocol.HealthPath}";
        var moved = new TaskCompletionSource<PeerPath>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPath(string key, PeerPath path) { if (key == route.Key && path != PeerPath.Relayed) moved.TrySetResult(path); }
        var retry = PeerSession.RetryFirst;
        PeerSession.RetryFirst = TimeSpan.FromSeconds(2);
        PeerSessions.PathChanged += OnPath;
        try
        {
            Assert.Contains(host.DeviceId, await http.GetStringAsync(health));
            Assert.Equal(PeerPath.Relayed, PeerSessions.PathOf(route));

            Volatile.Write(ref level, 2); // the way is open now: the next try, asked through the relay, comes up
            Assert.Equal(PeerPath.DirectLocal, await moved.Task.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Contains(host.DeviceId, await http.GetStringAsync(health));
            Assert.Equal(PeerPath.DirectLocal, PeerSessions.PathOf(route));
        }
        finally
        {
            PeerSessions.PathChanged -= OnPath;
            PeerSession.RetryFirst = retry;
        }
    }

    [SkippableFact]
    public async Task A_wrong_or_retired_console_secret_is_refused_with_a_reason()
    {
        var broker = TestBroker.FromEnvironment();
        Skip.If(broker is null, "No local RemoteHub Broker (REMOTEHUB_BROKER_URL, REMOTEHUB_BROKER_TOKEN).");
        var org = await broker.CreateOrgAsync();
        var wrong = new BrokerProtocol.Secret(BrokerProtocol.SecretKind.Console, org.Id, org.ConsoleSecretId, new byte[32]).ToString();
        var ex = await Assert.ThrowsAsync<BrokerException>(() => BrokerClient.JoinAsync(broker.Url.ToString(), wrong, "Test", CancellationToken.None));
        Assert.Equal("auth", ex.Code);
        Assert.Empty(BrokerStore.All()); // nothing kept, and no key left behind

        var device = new BrokerProtocol.Secret(BrokerProtocol.SecretKind.Device, org.Id, org.DeviceSecretId, org.DeviceSecret).ToString();
        var asDevice = await Assert.ThrowsAsync<BrokerException>(() => BrokerClient.JoinAsync(broker.Url.ToString(), device, "Test", CancellationToken.None));
        Assert.Contains("device secret", asDevice.Message);
    }
}
