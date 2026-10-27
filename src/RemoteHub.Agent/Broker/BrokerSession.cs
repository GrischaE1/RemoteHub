using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteHub.Agent;

/// <summary>Opens the punched port of a direct try in the device's firewall, for the console's address only (service only; tests pass none).</summary>
internal interface IDirectFirewall
{
    bool Open(int localPort, IPEndPoint remote);
    void Close(int localPort);
}

/// <summary>
/// One accepted broker session on the agent: the relay through the broker and, where both sides allow it, a direct
/// QUIC connection on a punched port. It lives while either does. Every stream of either ends at the agent's own HTTPS
/// endpoint through <see cref="PeerTransport"/> – the direct path changes how bytes travel, not what they reach.
/// </summary>
internal sealed class BrokerSession(
    string id,
    byte[] sessionKey,
    string consoleKeyHash,
    string who,
    IPAddress source,
    AgentPolicy policy,
    Func<AgentPolicy> currentPolicy,
    string stun,
    PeerTransport peers,
    X509Certificate2 certificate,
    AgentLog log,
    IDirectFirewall? firewall = null)
{
    /// <summary>A console asking again sooner than this gets no new round (it asks every 30 s while relayed).</summary>
    private static readonly TimeSpan MinRoundGap = TimeSpan.FromSeconds(10);

    private readonly byte[] _session = BrokerProtocol.FromB64u(id) is { Length: 16 } s ? s : throw new InvalidDataException("session");
    private readonly List<string> _paths = [];
    private readonly object _gate = new();
    private CancellationToken _end;
    private Task _round = Task.CompletedTask;
    private Task _direct = Task.CompletedTask;
    private DateTime _lastRound;
    private volatile bool _directUp;
    private volatile bool _relayDone;
    private int _streams;
    private long _bytesIn, _bytesOut;

    /// <summary>For tests: the path the session is on now.</summary>
    internal string Path => _directUp ? _paths[^1] : "relayed";

    /// <summary>The socket and candidates of one try at the direct path; null when this device won't try (policy, Windows version, no address).</summary>
    internal sealed class Round(Socket socket, List<string> cands) : IDisposable
    {
        public Socket Socket => socket;
        public List<string> Cands => cands;
        public int Port => ((IPEndPoint)socket.LocalEndPoint!).Port;
        public void Dispose() => socket.Dispose();
    }

    internal static async Task<Round?> PrepareAsync(int level, string stun, CancellationToken ct)
    {
        if (level <= 0 || !PeerDirect.Supported) return null;
        var socket = PeerDirect.NewSocket();
        try
        {
            var reflexive = await Stun.QueryAsync(socket, stun, TimeSpan.FromMilliseconds(750), ct);
            var cands = PeerDirect.OwnCandidates(((IPEndPoint)socket.LocalEndPoint!).Port, reflexive, level);
            if (cands.Count > 0) return new Round(socket, cands);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { }
        socket.Dispose();
        return null;
    }

    /// <summary>Runs the session until both paths are gone; the end goes to the audit.</summary>
    public async Task<string> RunAsync(Uri relayUri, string auth, Round? first, List<PeerCandidate> remote, CancellationToken stop)
    {
        var started = DateTime.UtcNow;
        using var end = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _end = end.Token;
        var relay = new BrokerRelay(peers, new IPEndPoint(source, 0), policy.MaxTunnelStreams) { PathRequested = PathRequestedAsync };
        var relayTask = RunRelayAsync(relay, relayUri, auth, end.Token);

        var path = "relayed";
        var directFirst = false;
        if (first is not null)
        {
            _lastRound = DateTime.UtcNow;
            if (directFirst = await RoundAsync(first, remote, end.Token)) path = _paths[^1];
        }
        log.Audit(AuditEvent.BrokerSessionStarted, $"Session through the RemoteHub Broker started: {who}, {path}");

        var reason = await relayTask;
        _relayDone = true;
        // The relay is gone (idle, or lost): a direct connection carries the session on until it ends too.
        Task round, direct;
        lock (_gate) round = _round;
        try { await round; } catch { /* ended */ }
        lock (_gate) direct = _direct;
        try { await direct; } catch { /* ended */ }
        end.Cancel();
        CryptographicOperations.ZeroMemory(sessionKey);

        var paths = string.Join(", then ", directFirst ? _paths : ["relayed", .. _paths]);
        log.Audit(AuditEvent.BrokerSessionEnded, $"Session through the RemoteHub Broker ended: {who} after {DateTime.UtcNow - started:hh\\:mm\\:ss}, " +
                                                  $"{relay.StreamsOpened + _streams} stream(s), {relay.BytesIn + _bytesIn:N0} bytes in, {relay.BytesOut + _bytesOut:N0} bytes out, " +
                                                  $"path {paths} ({reason})");
        return reason;
    }

    private async Task<string> RunRelayAsync(BrokerRelay relay, Uri relayUri, string auth, CancellationToken ct)
    {
        try
        {
            using var ws = BrokerLink.NewSocket(policy);
            ws.Options.SetRequestHeader("Authorization", "RemoteHub-Relay " + auth);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await ws.ConnectAsync(relayUri, timeout.Token);
            }
            return await relay.RunAsync(ws, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return "relay failed"; }
        catch (OperationCanceledException) { return "agent stopping"; }
    }

    /// <summary>
    /// RemoteHub asks for another try while relayed: this device's candidates for it, or none (direct already up, a
    /// try running or just made, the relay ending, or this device won't).
    /// </summary>
    internal async Task<string[]> PathRequestedAsync(string[] consoleCands)
    {
        lock (_gate)
        {
            if (_directUp || _relayDone || !_round.IsCompleted || DateTime.UtcNow - _lastRound < MinRoundGap) return [];
            _lastRound = DateTime.UtcNow;
        }
        // The policy as it is now: IT may have turned the direct path on or off since the session began.
        var level = currentPolicy().BrokerDirect;
        var round = await PrepareAsync(level, stun, _end);
        if (round is null) return [];
        var remote = PeerDirect.Usable(consoleCands, level);
        lock (_gate)
            _round = Task.Run(async () =>
            {
                if (await RoundAsync(round, remote, _end))
                    log.Write($"Session {Short} through the RemoteHub Broker: now also {_paths[^1]} (new streams take it).");
            }, CancellationToken.None);
        return [.. round.Cands];
    }

    private string Short => id[..8];

    /// <summary>Punch, hand the port to msquic, take the one connection that brings the announced console key.</summary>
    private async Task<bool> RoundAsync(Round round, IReadOnlyList<PeerCandidate> remote, CancellationToken ct)
    {
        PeerDirect.PunchResult? punched;
        int port;
        using (round)
        {
            port = round.Port;
            try { punched = await PeerDirect.PunchAsync(round.Socket, remote, _session, sessionKey, agent: true, ct); }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException) { punched = null; }
        }
        if (punched is null) return false;

        var opened = firewall?.Open(port, punched.Remote) == true;
        QuicConnection? connection = null;
        try
        {
            var options = PeerDirect.ServerOptions(certificate, consoleKeyHash, policy.MaxTunnelStreams);
            await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.IPv6Any, port),
                ApplicationProtocols = [new System.Net.Security.SslApplicationProtocol(PeerDirect.Alpn)],
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(options),
                ListenBacklog = 1,
            }, ct);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(PeerDirect.ConnectTime + TimeSpan.FromSeconds(2));
            while (connection is null)
            {
                // A handshake without the announced console key fails here and is never a session.
                try { connection = await listener.AcceptConnectionAsync(wait.Token); }
                catch (Exception ex) when (ex is AuthenticationException or QuicException) { }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or QuicException or SocketException)
        {
            if (opened) firewall!.Close(port);
            return false;
        }

        var label = punched.Local ? "direct local" : "direct internet";
        lock (_gate)
        {
            _paths.Add(label);
            _directUp = true;
            _direct = ServeAsync(connection, ct, opened ? port : null);
        }
        return true;
    }

    /// <summary>Each QUIC stream becomes one connection into Kestrel, from the console's address on this path.</summary>
    private async Task ServeAsync(QuicConnection connection, CancellationToken ct, int? firewallPort)
    {
        var remote = PeerDirect.Normal(connection.RemoteEndPoint);
        try
        {
            while (true)
            {
                var stream = await connection.AcceptInboundStreamAsync(ct);
                _ = Task.Run(() => PumpAsync(stream, remote), CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is QuicException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            _directUp = false;
            try { await connection.CloseAsync(0); } catch { /* gone */ }
            await connection.DisposeAsync();
            if (firewallPort is { } p) firewall!.Close(p);
        }
    }

    private async Task PumpAsync(QuicStream quic, IPEndPoint remote)
    {
        await using var _ = quic;
        if (peers.Connect(new IPEndPoint(remote.Address, 0)) is not PeerTransport.DuplexStream kestrel)
        {
            quic.Abort(QuicAbortDirection.Both, 1); // the agent is stopping
            return;
        }
        Interlocked.Increment(ref _streams);
        using (kestrel)
        {
            var up = Copy(quic, kestrel, kestrel.CompleteOutput, n => Interlocked.Add(ref _bytesIn, n));
            var down = Copy(kestrel, quic, quic.CompleteWrites, n => Interlocked.Add(ref _bytesOut, n));
            // Each direction ends on its own (half-close); a failure on either ends both.
            if (await Task.WhenAny(up, down) is { IsFaulted: true })
            {
                quic.Abort(QuicAbortDirection.Both, 1);
                kestrel.Dispose();
            }
            try { await Task.WhenAll(up, down); } catch { /* ended */ }
        }
    }

    private static async Task Copy(Stream from, Stream to, Action ended, Action<int> counted)
    {
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = await from.ReadAsync(buffer)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, n));
            await to.FlushAsync();
            counted(n);
        }
        ended();
    }
}
