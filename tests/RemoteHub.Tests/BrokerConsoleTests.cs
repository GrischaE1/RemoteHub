extern alias agent;
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
/// relayed session. Skipped unless REMOTEHUB_BROKER_URL and REMOTEHUB_BROKER_TOKEN point at a broker.
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
        var item = await device.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(SpinWait.SpinUntil(() => client.IsConnected, TimeSpan.FromSeconds(10)), client.State); // connected once the first sync is in
        // The record's seal checks with the organisation's record key from the keyring; a changed name doesn't.
        var record = item["record"]!.AsObject();
        Assert.True(client.RecordIsSealed(record, item["seal"]!.GetValue<string>()));
        Assert.Equal(agentHost.Fingerprint, record["fingerprint"]!.GetValue<string>());
        var renamed = (JsonObject)record.DeepClone();
        renamed["computerName"] = "DC01";
        Assert.False(client.RecordIsSealed(renamed, item["seal"]!.GetValue<string>()));

        // The agent's HTTPS endpoint through a relayed session, TLS to the pin from the sealed record.
        var route = new BrokerRoute(client, agentHost.DeviceId, item["secretId"]?.GetValue<string>(), "PC-0815");
        using var handler = AgentClientTransport.ThroughBroker(route, (_, cert, _, _) => cert is not null && AgentProtocol.Fingerprint(cert.GetRawCertData()) == agentHost.Fingerprint);
        using var http = new HttpClient(handler);
        var health = await http.GetStringAsync($"https://pc-0815:{AgentProtocol.DefaultPort}{AgentProtocol.HealthPath}");
        Assert.Contains(agentHost.DeviceId, health);
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
