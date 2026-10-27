using System.Threading;
using RemoteHub.Agent;
using RemoteHub.Services;

namespace RemoteHub.Broker;

/// <summary>What Settings → RemoteHub Broker → Test found: one line each, and whether a direct path can come up from here.</summary>
public sealed record BrokerTestResult(string Control, string Stun, bool Direct, string Verdict);

/// <summary>
/// The test behind Settings → RemoteHub Broker: the control channel, and whether a STUN request gets an answer from this
/// network – without one, UDP doesn't get out and every session runs through the relay.
/// </summary>
public static class BrokerTest
{
    public static Task<BrokerTestResult> RunAsync(BrokerClient client, CancellationToken ct) => RunAsync(client.IsConnected, client.State, client.StunServer, ct);

    internal static async Task<BrokerTestResult> RunAsync(bool connected, string state, string stun, CancellationToken ct)
    {
        var control = connected ? "Control channel: connected." : $"Control channel: not connected – {state}";
        if (!AppPolicy.BrokerDirect)
            return new(control, "STUN: not asked.", false, "Only relayed connections: this PC's policy turns direct connections off (BrokerDirect).");
        if (!PeerDirect.Supported)
            return new(control, "STUN: not asked.", false, "Only relayed connections: direct connections need Windows 11 or Windows Server 2022 or later.");
        using var socket = PeerDirect.NewSocket();
        var mapped = await Stun.QueryAsync(socket, stun, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        return mapped is null
            ? new(control, $"STUN ({stun}): no answer.", false, "Only relayed connections: UDP is blocked from this network.")
            : new(control, $"STUN ({stun}): answered – this network's public address is {mapped.Address}.", true, "Direct connections possible.");
    }
}
