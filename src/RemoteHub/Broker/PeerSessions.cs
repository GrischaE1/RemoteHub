using System.Threading;
using RemoteHub.Remoting;

namespace RemoteHub.Broker;

/// <summary>How a device is reached through a RemoteHub Broker: the organisation's connection and the device.</summary>
public sealed record BrokerRoute(BrokerClient Client, string DeviceId, string? SecretId, string DeviceName)
{
    internal string Key => $"{Client.Info.Id}|{DeviceId}";
}

/// <summary>
/// The relayed sessions to devices through their broker: one per device, shared by every tab
/// and tool, opened with a sealed offer and closed two minutes after its last stream (the tunnel's idle close). Every
/// stream ends at the device's own agent; TLS to its pinned certificate and the Windows sign-in run inside it.
/// </summary>
public static class PeerSessions
{
    private static readonly Dictionary<string, Task<JumpTunnel>> Sessions = [];

    /// <summary>A new stream to the device's agent (its HTTPS endpoint, as a TCP connection to its port would be).</summary>
    public static async Task<TunnelStream> OpenAsync(BrokerRoute route, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var session = await Get(route, ct).ConfigureAwait(false);
            try { return await session.OpenAsync("", 0, ct).ConfigureAwait(false); }
            catch (ObjectDisposedException) when (attempt == 0) { /* idle-closed or dropped meanwhile: offer again */ }
        }
    }

    private static async Task<JumpTunnel> Get(BrokerRoute route, CancellationToken ct)
    {
        Task<JumpTunnel> task;
        lock (Sessions)
        {
            if (!Sessions.TryGetValue(route.Key, out task!) || task.IsFaulted || task.IsCanceled || task is { IsCompletedSuccessfully: true, Result.IsClosed: true })
                Sessions[route.Key] = task = ConnectAsync(route);
        }
        try { return await task.WaitAsync(ct).ConfigureAwait(false); }
        catch when (task.IsFaulted)
        {
            lock (Sessions)
                if (Sessions.TryGetValue(route.Key, out var current) && current == task) Sessions.Remove(route.Key);
            throw;
        }
    }

    private static async Task<JumpTunnel> ConnectAsync(BrokerRoute route)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var accepted = await route.Client.OfferAsync(route.DeviceId, route.SecretId, timeout.Token).ConfigureAwait(false);
        var ws = await route.Client.OpenRelayAsync(accepted, timeout.Token).ConfigureAwait(false);
        return JumpTunnel.OverRelay(ws, route.DeviceName);
    }

    /// <summary>Ends every session (tests; RemoteHub closing; a broker connection removed).</summary>
    public static void CloseAll()
    {
        List<Task<JumpTunnel>> all;
        lock (Sessions)
        {
            all = [.. Sessions.Values];
            Sessions.Clear();
        }
        foreach (var t in all)
            if (t.IsCompletedSuccessfully) t.Result.Close();
    }
}
