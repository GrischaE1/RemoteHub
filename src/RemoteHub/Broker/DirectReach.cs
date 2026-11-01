using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using RemoteHub.Agent;

namespace RemoteHub.Broker;

/// <summary>A device's agent reached without the broker: the host that answered and its port.</summary>
public sealed record DirectHost(string Host, int Port);

/// <summary>
/// How a device of a synced broker folder is reached without the broker (Reach Automatic): its name from the sealed
/// record first, then every address its agent reported, all at once – each tried as a TLS handshake pinned to the
/// record's certificate. Only when none answers does a connection take the broker path (punching, then the relay), so in
/// the device's own network the broker carries nothing, also where its name doesn't resolve.
/// <para>
/// Remembered per device (by its pin): the host that answered, tried first next time; and that nothing answered, so for
/// a while connections take the broker path at once – until the device reports other addresses or this PC's network
/// changes. A remembered host that stops answering while this PC's network stayed the same means the device moved: its
/// new addresses are waited for (<see cref="MoveWait"/>) before the broker path.
/// </para>
/// </summary>
internal static class DirectReach
{
    internal static TimeSpan NameWait { get; set; } = TimeSpan.FromMilliseconds(1500);
    /// <summary>How long the name is tried alone before the broker is asked what the agent reported.</summary>
    internal static TimeSpan NameHeadStart { get; set; } = TimeSpan.FromMilliseconds(250);
    internal static TimeSpan AddressWait { get; set; } = TimeSpan.FromSeconds(2);
    internal static TimeSpan MoveWait { get; set; } = TimeSpan.FromSeconds(15);
    internal static TimeSpan MovePoll { get; set; } = TimeSpan.FromMilliseconds(2500);
    internal static TimeSpan AskWait { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>A lost host within this of this PC's own network change: this PC moved, not the device – nothing to wait for.</summary>
    internal static TimeSpan OwnMoveWindow { get; set; } = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NotDirectFor = TimeSpan.FromMinutes(10);

    private sealed class Memory
    {
        public DirectHost? Host;
        public DateTime NotDirectUntil;
        public DateTimeOffset? NotDirectReported;
    }

    private static readonly ConcurrentDictionary<string, Memory> Known = new();
    private static DateTime _ownNetworkChanged = DateTime.MinValue;

    static DirectReach()
    {
        NetworkChange.NetworkAddressChanged += (_, _) => OwnNetworkChanged();
    }

    /// <summary>This PC's network changed: what didn't answer before may now, and a lost host is this PC's move.</summary>
    internal static void OwnNetworkChanged()
    {
        _ownNetworkChanged = DateTime.UtcNow;
        foreach (var m in Known.Values)
            lock (m) m.NotDirectUntil = DateTime.MinValue;
    }

    /// <summary>A pinned TLS try of one host (replaced by the tests).</summary>
    internal delegate Task<bool> Probe(string host, int port, string pin, TimeSpan within, CancellationToken ct);

    /// <summary>The host to connect to directly, or null: take the broker path.</summary>
    public static Task<DirectHost?> FindAsync(BrokerRoute route, string name, int port, string pin, CancellationToken ct) =>
        FindAsync(pin, name, port, c => route.Client.DeviceAddressesAsync(route.DeviceId, c), TryTls, ct);

    public const string NoDirectMessage = "This device takes no direct connections (its agent doesn't listen on the network). " +
                                          "Set Reach to Automatic or Through the RemoteHub Broker.";

    /// <summary>
    /// Whether the device's agent said it takes no direct connections (policy ListenEnabled = 0, or no firewall rule).
    /// False when the broker can't be asked now: the direct try then shows what happens.
    /// </summary>
    public static Task<bool> TakesNoDirectAsync(BrokerRoute route, CancellationToken ct) =>
        TakesNoDirectAsync(c => route.Client.DeviceAddressesAsync(route.DeviceId, c), ct);

    internal static async Task<bool> TakesNoDirectAsync(Func<CancellationToken, Task<DeviceAddresses?>> addresses, CancellationToken ct) =>
        await Ask(addresses, ct).ConfigureAwait(false) is { Inbound: false };

    /// <summary>A direct connection through <paramref name="host"/> failed after all: it isn't tried first next time.</summary>
    public static void Failed(string pin, DirectHost host)
    {
        if (Known.TryGetValue(AgentProtocol.NormalizeFingerprint(pin), out var m))
            lock (m) if (m.Host == host) m.Host = null;
    }

    /// <summary>Whether the device's agent answered at a host of its own lately – its connections then go there, not through the broker.</summary>
    public static bool HasHost(string pin) =>
        Known.TryGetValue(AgentProtocol.NormalizeFingerprint(pin), out var m) && Volatile.Read(ref m.Host) is not null;

    /// <summary>Forgets every device (tests).</summary>
    internal static void Reset()
    {
        Known.Clear();
        _ownNetworkChanged = DateTime.MinValue;
    }

    internal static async Task<DirectHost?> FindAsync(string pin, string name, int port, Func<CancellationToken, Task<DeviceAddresses?>> addresses, Probe probe, CancellationToken ct)
    {
        pin = AgentProtocol.NormalizeFingerprint(pin);
        if (pin.Length == 0) return null;
        var memory = Known.GetOrAdd(pin, _ => new Memory());
        DirectHost? remembered;
        DateTime notUntil;
        DateTimeOffset? notReported;
        lock (memory) (remembered, notUntil, notReported) = (memory.Host, memory.NotDirectUntil, memory.NotDirectReported);

        // 1. The host that answered last time.
        DateTime? lost = null;
        if (remembered is not null)
        {
            if (await probe(remembered.Host, remembered.Port, pin, NameWait, ct).ConfigureAwait(false)) return remembered;
            lost = DateTime.UtcNow;
            lock (memory) if (memory.Host == remembered) memory.Host = null;
        }

        // Nothing answered lately: the broker path at once – unless the device has reported addresses since.
        DeviceAddresses? reported = null;
        if (lost is null && notUntil > DateTime.UtcNow)
        {
            reported = await Ask(addresses, ct).ConfigureAwait(false);
            if (reported?.ReportedAt is not { } at || (notReported is { } before && at <= before)) return null;
        }

        // 2. Its name (the sealed record's computer name and domain). The broker is asked for what the agent reported
        // once the name has had NameHeadStart: a name that answers by then costs no request, and an agent that takes no
        // direct connections (no firewall rule, or policy ListenEnabled = 0) ends the try then instead of after NameWait.
        name = name.Trim();
        var tryName = name.Length > 0 && !string.Equals(name, remembered?.Host, StringComparison.OrdinalIgnoreCase);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var asked = reported is not null ? Task.FromResult<DeviceAddresses?>(reported)
            : AskAfter(tryName ? NameHeadStart : TimeSpan.Zero, addresses, stop.Token, ct);
        if (tryName)
        {
            var named = probe(name, port, pin, NameWait, stop.Token);
            if (await Task.WhenAny(named, asked).ConfigureAwait(false) == asked && await asked.ConfigureAwait(false) is { Inbound: false } none)
            {
                stop.Cancel();
                try { await named.ConfigureAwait(false); } catch (OperationCanceledException) { /* stopped above */ }
                return NotDirect(memory, none);
            }
            if (await named.ConfigureAwait(false))
            {
                stop.Cancel(); // the broker isn't asked if it hasn't been yet
                return Remember(memory, new DirectHost(name, port));
            }
        }

        // 3. Every address its agent reported, at once – after a move of the device, its new ones.
        reported = await asked.ConfigureAwait(false);
        if (lost is { } since && reported is not null && DateTime.UtcNow - _ownNetworkChanged > OwnMoveWindow)
            reported = await WaitForMove(addresses, reported, since, ct).ConfigureAwait(false);
        if (reported is null || reported.Inbound == false) return NotDirect(memory, reported);
        var to = reported.Port ?? port;
        var hosts = reported.Addresses.Select(a => a.ToString())
            .Where(h => !string.Equals(h, name, StringComparison.OrdinalIgnoreCase) && !(remembered is { } r && r.Host == h && r.Port == to)).ToList();
        if (hosts.Count > 0 && await FirstAsync(hosts, to, pin, probe, ct).ConfigureAwait(false) is { } host)
            return Remember(memory, new DirectHost(host, to));
        return NotDirect(memory, reported);
    }

    private static DirectHost Remember(Memory memory, DirectHost host)
    {
        lock (memory)
        {
            memory.Host = host;
            memory.NotDirectUntil = DateTime.MinValue;
        }
        return host;
    }

    private static DirectHost? NotDirect(Memory memory, DeviceAddresses? reported)
    {
        lock (memory)
        {
            memory.NotDirectUntil = DateTime.UtcNow + NotDirectFor;
            memory.NotDirectReported = reported?.ReportedAt;
        }
        return null;
    }

    /// <summary><see cref="Ask"/> after <paramref name="wait"/>; null without asking when <paramref name="stop"/> ends the wait.</summary>
    private static async Task<DeviceAddresses?> AskAfter(TimeSpan wait, Func<CancellationToken, Task<DeviceAddresses?>> addresses, CancellationToken stop, CancellationToken ct)
    {
        if (wait > TimeSpan.Zero)
        {
            try { await Task.Delay(wait, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        }
        return await Ask(addresses, ct).ConfigureAwait(false);
    }

    /// <summary>The broker's answer, or null when it can't be asked now (not connected, too slow, an older broker).</summary>
    private static async Task<DeviceAddresses?> Ask(Func<CancellationToken, Task<DeviceAddresses?>> addresses, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AskWait);
        try { return await addresses(timeout.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is BrokerException or OperationCanceledException && !ct.IsCancellationRequested) { return null; }
    }

    /// <summary>The device's addresses once it has reported them after <paramref name="since"/>, waited for at most <see cref="MoveWait"/>.</summary>
    private static async Task<DeviceAddresses> WaitForMove(Func<CancellationToken, Task<DeviceAddresses?>> addresses, DeviceAddresses current, DateTime since, CancellationToken ct)
    {
        // A little slack for the broker's clock against this PC's.
        var newer = new DateTimeOffset(since).AddSeconds(-2);
        var deadline = DateTime.UtcNow + MoveWait;
        while ((current.ReportedAt is not { } at || at < newer) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(MovePoll, ct).ConfigureAwait(false);
            current = await Ask(addresses, ct).ConfigureAwait(false) ?? current;
        }
        return current;
    }

    /// <summary>The first of <paramref name="hosts"/> whose agent answers with the pinned certificate; the other tries are cancelled.</summary>
    private static async Task<string?> FirstAsync(IReadOnlyList<string> hosts, int port, string pin, Probe probe, CancellationToken ct)
    {
        using var others = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tries = hosts.ToDictionary(h => probe(h, port, pin, AddressWait, others.Token), h => h);
        var pending = tries.Keys.ToList();
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            if (done.IsCompletedSuccessfully && done.Result)
            {
                others.Cancel();
                return tries[done];
            }
        }
        ct.ThrowIfCancellationRequested();
        return null;
    }

    /// <summary>A TCP connection and a TLS handshake within <paramref name="within"/>, accepted only with the pinned certificate; closed again.</summary>
    internal static async Task<bool> TryTls(string host, int port, string pin, TimeSpan within, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(within);
        try
        {
            using var tcp = new TcpClient(AddressFamily.InterNetworkV6) { Client = { DualMode = true } };
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            await using var tls = new SslStream(tcp.GetStream(), false,
                (_, cert, _, _) => cert is not null && AgentProtocol.Fingerprint(cert.GetRawCertData()) == pin);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "agent" }, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException or ArgumentException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested)) { return false; }
    }
}
