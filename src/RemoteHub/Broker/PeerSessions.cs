using System.Net;
using System.Net.NetworkInformation;
using System.Net.Quic;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using RemoteHub.Agent;
using RemoteHub.Remoting;
using RemoteHub.Services;

namespace RemoteHub.Broker;

/// <summary>
/// How a device is reached through a RemoteHub Broker: the organisation's connection, the device, and the fingerprint its
/// sealed record pins (empty: no direct path, which pins it in the QUIC handshake).
/// </summary>
public sealed record BrokerRoute(BrokerClient Client, string DeviceId, string? SecretId, string DeviceName, string Fingerprint = "")
{
    internal string Key => $"{Client.Info.Id}|{DeviceId}";
}

/// <summary>Where a session's new streams go.</summary>
public enum PeerPath { Relayed, DirectLocal, DirectInternet }

/// <summary>
/// The sessions to devices through their broker: one per device, shared by every tab and tool, opened with a sealed
/// offer and closed two minutes after its last stream. A session starts relayed and punched at once; new streams take
/// the direct path when it is up within 3 s, and while relayed it tries again from time to time. Every stream ends at
/// the device's own agent; TLS to its pinned certificate and the Windows sign-in run inside it, whichever the path.
/// </summary>
public static class PeerSessions
{
    private static readonly Dictionary<string, Task<PeerSession>> Sessions = [];

    /// <summary>A session's path changed (route key, new path) – raised on a thread-pool thread.</summary>
    public static event Action<string, PeerPath>? PathChanged;

    /// <summary>A new stream to the device's agent (its HTTPS endpoint, as a TCP connection to its port would be).</summary>
    public static async Task<Stream> OpenAsync(BrokerRoute route, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var session = await Get(route, ct).ConfigureAwait(false);
            try { return await session.OpenAsync(ct).ConfigureAwait(false); }
            catch (ObjectDisposedException) when (attempt == 0) { /* idle-closed or dropped meanwhile: offer again */ }
        }
    }

    /// <summary>The path of the device's open session, or null when none is open.</summary>
    public static PeerPath? PathOf(BrokerRoute route)
    {
        lock (Sessions)
            return Sessions.TryGetValue(route.Key, out var t) && t is { IsCompletedSuccessfully: true, Result.IsClosed: false } ? t.Result.Path : null;
    }

    internal static void Raise(string key, PeerPath path) => PathChanged?.Invoke(key, path);

    private static async Task<PeerSession> Get(BrokerRoute route, CancellationToken ct)
    {
        Task<PeerSession> task;
        lock (Sessions)
        {
            if (!Sessions.TryGetValue(route.Key, out task!) || task.IsFaulted || task.IsCanceled || task is { IsCompletedSuccessfully: true, Result.IsClosed: true })
                Sessions[route.Key] = task = PeerSession.ConnectAsync(route);
        }
        try { return await task.WaitAsync(ct).ConfigureAwait(false); }
        catch when (task.IsFaulted)
        {
            lock (Sessions)
                if (Sessions.TryGetValue(route.Key, out var current) && current == task) Sessions.Remove(route.Key);
            throw;
        }
    }

    /// <summary>Ends every session (tests; RemoteHub closing; a broker connection removed).</summary>
    public static void CloseAll()
    {
        List<Task<PeerSession>> all;
        lock (Sessions)
        {
            all = [.. Sessions.Values];
            Sessions.Clear();
        }
        foreach (var t in all)
            if (t.IsCompletedSuccessfully) t.Result.Close();
    }
}

/// <summary>One session with a device: its relay, and the direct QUIC connection when there is one.</summary>
internal sealed class PeerSession
{
    /// <summary>From the broker's answer: direct when QUIC is up within this, else relayed for now.</summary>
    internal static TimeSpan DirectWait = TimeSpan.FromSeconds(3);
    /// <summary>While relayed: the first new try after this, then twice as long each time up to <see cref="RetryMax"/>.</summary>
    internal static TimeSpan RetryFirst = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryMax = TimeSpan.FromMinutes(5);

    private readonly BrokerRoute _route;
    private readonly byte[] _session, _key;
    private readonly bool _directAllowed;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private JumpTunnel? _relay;
    private QuicConnection? _direct;
    private PeerPath _directPath;
    private int _directStreams;
    private Timer? _directIdle;
    private int _closed;

    private PeerSession(BrokerRoute route, byte[] session, byte[] key, bool directAllowed)
    {
        _route = route;
        _session = session;
        _key = key;
        _directAllowed = directAllowed;
    }

    public PeerPath Path
    {
        get { lock (_gate) return _direct is not null ? _directPath : PeerPath.Relayed; }
    }

    public bool IsClosed
    {
        get
        {
            if (Volatile.Read(ref _closed) == 1) return true;
            lock (_gate) return _direct is null && _relay is not { IsClosed: false };
        }
    }

    /// <summary>The policy BrokerDirect and the Windows version allow a direct path from this PC, and the device has a pin for it.</summary>
    private static bool DirectAllowed(BrokerRoute route) => AppPolicy.BrokerDirect && PeerDirect.Supported && route.Fingerprint.Length > 0;

    internal static async Task<PeerSession> ConnectAsync(BrokerRoute route)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var allowed = DirectAllowed(route);
        var round = allowed ? await Round.PrepareAsync(route.Client.StunServer, timeout.Token).ConfigureAwait(false) : null;
        JsonObject accepted;
        try { accepted = await route.Client.OfferAsync(route.DeviceId, route.SecretId, round?.Cands ?? [], timeout.Token).ConfigureAwait(false); }
        catch
        {
            round?.Dispose();
            throw;
        }
        var session = BrokerProtocol.FromB64u(accepted["session"]?.GetValue<string>() ?? "") is { Length: 16 } s ? s : null;
        var key = BrokerProtocol.FromB64u(accepted["key"]?.GetValue<string>() ?? "") is { Length: 32 } k ? k : null;
        if (session is null || key is null)
        {
            round?.Dispose();
            throw new BrokerException("The broker's answer is damaged.");
        }
        var peer = new PeerSession(route, session, key, allowed);
        var remote = PeerDirect.Usable(Strings(accepted["cands"]), 2);

        // Relay and punching at the same time: a relayed session loses no time.
        var direct = round is not null && remote.Count > 0 ? peer.SafeDirectAsync(round, remote) : Task.FromResult(false);
        if (round is not null && remote.Count == 0) round.Dispose();
        var relay = OpenRelay(route, accepted, timeout.Token);
        // Direct when it is up within DirectWait; otherwise as soon as the relay is (direct may still follow).
        var up = await Task.WhenAny(direct, Task.Delay(DirectWait)).ConfigureAwait(false) == direct && direct.Result;
        if (up) _ = relay.ContinueWith(t => { if (t.IsCompletedSuccessfully) peer.SetRelay(t.Result); }, TaskScheduler.Default);
        else
        {
            try { peer.SetRelay(await relay.ConfigureAwait(false)); }
            catch
            {
                if (!await direct.ConfigureAwait(false))
                {
                    peer.Close();
                    throw;
                }
            }
        }
        _ = peer.RetryLoop();
        PeerSessions.Raise(route.Key, peer.Path); // the tabs show which path it started on
        return peer;
    }

    private static Task<JumpTunnel> OpenRelay(BrokerRoute route, JsonObject accepted, CancellationToken ct) => Task.Run(async () =>
    {
        var ws = await route.Client.OpenRelayAsync(accepted, ct).ConfigureAwait(false);
        return JumpTunnel.OverRelay(ws, route.DeviceName);
    }, CancellationToken.None);

    private void SetRelay(JumpTunnel relay)
    {
        lock (_gate)
            if (Volatile.Read(ref _closed) == 0) { _relay = relay; return; }
        relay.Close();
    }

    public async Task<Stream> OpenAsync(CancellationToken ct)
    {
        QuicConnection? direct;
        lock (_gate) direct = _direct;
        if (direct is not null)
        {
            try
            {
                var stream = await direct.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
                return new DirectStream(stream, this);
            }
            catch (Exception ex) when (ex is QuicException or ObjectDisposedException) { DirectLost(direct); }
        }
        JumpTunnel? relay;
        lock (_gate) relay = _relay;
        if (relay is null || Volatile.Read(ref _closed) == 1) throw new ObjectDisposedException(nameof(PeerSession));
        return await relay.OpenAsync("", 0, ct).ConfigureAwait(false);
    }

    // ---- The direct path ------------------------------------------------------------------------------------------

    /// <summary>The socket and candidates of one try; null when STUN and the adapters give no candidate.</summary>
    private sealed class Round(Socket socket, List<string> cands) : IDisposable
    {
        public Socket Socket => socket;
        public List<string> Cands => cands;
        public int Port => ((IPEndPoint)socket.LocalEndPoint!).Port;
        public void Dispose() => socket.Dispose();

        public static async Task<Round?> PrepareAsync(string stun, CancellationToken ct)
        {
            var socket = PeerDirect.NewSocket();
            var reflexive = await Stun.QueryAsync(socket, stun, TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
            var cands = PeerDirect.OwnCandidates(((IPEndPoint)socket.LocalEndPoint!).Port, reflexive, 2);
            if (cands.Count > 0) return new Round(socket, cands);
            socket.Dispose();
            return null;
        }
    }

    private async Task<bool> SafeDirectAsync(Round round, IReadOnlyList<PeerCandidate> remote)
    {
        try { return await DirectAsync(round, remote, _stop.Token).ConfigureAwait(false); }
        catch (Exception ex)
        {
            DiagLog.Write($"Broker session: the direct path failed ({ex.GetBaseException().Message})");
            return false;
        }
    }

    /// <summary>Punch, hand the port to msquic, connect pinned to the device; true when the direct path is up.</summary>
    private async Task<bool> DirectAsync(Round round, IReadOnlyList<PeerCandidate> remote, CancellationToken ct)
    {
        PeerDirect.PunchResult? won;
        int port;
        using (round)
        {
            port = round.Port;
            try { won = await PeerDirect.PunchAsync(round.Socket, remote, _session, _key, agent: false, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException) { won = null; }
        }
        if (won is null) return false;
        QuicConnection connection;
        try
        {
            var options = PeerDirect.ClientOptions(won.Remote, port, _route.Client.PeerCertificate(), _route.Fingerprint);
            connection = await PeerDirect.ConnectAsync(options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is QuicException or AuthenticationException or SocketException or OperationCanceledException or BrokerException or CryptographicException)
        {
            DiagLog.Write($"Broker session: the direct path didn't come up ({ex.GetBaseException().Message})");
            return false;
        }
        var path = won.Local ? PeerPath.DirectLocal : PeerPath.DirectInternet;
        lock (_gate)
        {
            if (Volatile.Read(ref _closed) == 1 || _direct is not null)
            {
                _ = connection.DisposeAsync();
                return _direct is not null;
            }
            _direct = connection;
            _directPath = path;
            StartDirectIdle();
        }
        DiagLog.Write($"Broker session: {Describe(path)}");
        PeerSessions.Raise(_route.Key, path);
        return true;
    }

    /// <summary>
    /// While relayed: another try now and then, asked of the agent through the relay (no new offer at the broker). Stops
    /// when the agent answers it won't try, the session ends, or nothing could change (no candidates of our own).
    /// </summary>
    private async Task RetryLoop()
    {
        if (!_directAllowed) return;
        var wait = RetryFirst;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NetworkAddressChangedEventHandler onChange = (_, _) => changed.TrySetResult();
        NetworkChange.NetworkAddressChanged += onChange;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                // A new network (Wi-Fi to tethering, a VPN up) is worth a try at once.
                try { await Task.WhenAny(Task.Delay(wait, _stop.Token), changed.Task).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (_stop.IsCancellationRequested || IsClosed) return;
                if (changed.Task.IsCompleted)
                {
                    changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = RetryFirst;
                }
                else wait = TimeSpan.FromTicks(Math.Min(RetryMax.Ticks, wait.Ticks * 2));
                JumpTunnel? relay;
                lock (_gate)
                {
                    if (_direct is not null) continue; // up: nothing to do until it goes
                    relay = _relay;
                }
                if (relay is null || relay.IsClosed) return;
                var round = await Round.PrepareAsync(_route.Client.StunServer, _stop.Token).ConfigureAwait(false);
                if (round is null) continue;
                string[] theirs;
                try { theirs = await relay.AskPathAsync([.. round.Cands], _stop.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or TimeoutException or IOException)
                {
                    round.Dispose();
                    continue;
                }
                var remote = PeerDirect.Usable(theirs, 2);
                if (remote.Count == 0)
                {
                    round.Dispose();
                    if (theirs.Length == 0) return; // the agent won't try (its policy, its Windows version)
                    continue;
                }
                await SafeDirectAsync(round, remote).ConfigureAwait(false);
            }
        }
        finally { NetworkChange.NetworkAddressChanged -= onChange; }
    }

    private void DirectLost(QuicConnection connection)
    {
        lock (_gate)
        {
            if (_direct != connection) return;
            _direct = null;
            _directIdle?.Dispose();
            _directIdle = null;
        }
        _ = connection.DisposeAsync();
        DiagLog.Write("Broker session: the direct path ended");
        PeerSessions.Raise(_route.Key, PeerPath.Relayed);
        if (IsClosed) Close();
    }

    internal void DirectStreamOpened()
    {
        lock (_gate)
        {
            _directStreams++;
            _directIdle?.Dispose();
            _directIdle = null;
        }
    }

    internal void DirectStreamEnded()
    {
        lock (_gate)
            if (--_directStreams == 0) StartDirectIdle();
    }

    /// <summary>Like the relay: the direct connection closes two minutes after its last stream.</summary>
    private void StartDirectIdle()
    {
        var direct = _direct;
        if (direct is null || _directStreams > 0) return;
        _directIdle?.Dispose();
        _directIdle = new Timer(_ =>
        {
            lock (_gate) if (_directStreams > 0 || _direct != direct) return;
            DirectLost(direct);
        }, null, JumpTunnel.IdleClose, Timeout.InfiniteTimeSpan);
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        _stop.Cancel();
        JumpTunnel? relay;
        QuicConnection? direct;
        lock (_gate)
        {
            relay = _relay;
            direct = _direct;
            _relay = null;
            _direct = null;
            _directIdle?.Dispose();
        }
        relay?.Close();
        if (direct is not null) _ = direct.DisposeAsync();
        CryptographicOperations.ZeroMemory(_key);
    }

    internal static string Describe(PeerPath path) => path switch
    {
        PeerPath.DirectLocal => "direct (local network)",
        PeerPath.DirectInternet => "direct (internet)",
        _ => "relayed",
    };

    private static IEnumerable<string?> Strings(JsonNode? node) =>
        node is JsonArray a ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null) : [];

    /// <summary>A QUIC stream that tells its session when it ends (for the idle close), and closes its output like TCP.</summary>
    private sealed class DirectStream : Stream
    {
        private readonly QuicStream _inner;
        private readonly PeerSession _owner;
        private int _disposed;

        public DirectStream(QuicStream inner, PeerSession owner)
        {
            _inner = inner;
            _owner = owner;
            owner.DirectStreamOpened();
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>This side sends nothing more (the other side still may).</summary>
        public void CloseOutput() => _inner.CompleteWrites();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _inner.Dispose();
                _owner.DirectStreamEnded();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
                _owner.DirectStreamEnded();
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
