using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace RemoteHub.Agent;

/// <summary>
/// One jump connection (<see cref="TunnelProtocol"/>): RemoteHub opens TCP streams from this device to others. The agent
/// only checks where they go (policy, every time) and copies bytes – what runs inside (RDP, WinRM, another agent's TLS)
/// stays encrypted end to end.
/// </summary>
internal sealed class TunnelEndpoint(AgentLog log, string account, string source, Func<AgentPolicy> policy, TunnelNetwork? network = null)
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    /// <summary>After RemoteHub closed its side: how long the target may still send before the stream is cut.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(60);

    private readonly TunnelNetwork _net = network ?? TunnelNetwork.System;
    private readonly ConcurrentDictionary<uint, Relay?> _streams = new(); // null while opening
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private WebSocket _ws = null!;
    private CancellationToken _ct;
    private int _opened;

    public async Task RunAsync(WebSocket ws, CancellationToken aborted)
    {
        _ws = ws;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        _ct = cts.Token;
        var started = DateTime.UtcNow;
        log.Audit(AuditEvent.TunnelStarted, $"Jump connection started: {account} from {source}");
        var reason = "closed by RemoteHub";
        try { await ReceiveLoop(); }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException) { reason = "connection lost"; }
        catch (Exception ex) when (ex is InvalidDataException or JsonException) { reason = "protocol error: " + ex.Message; }
        finally
        {
            cts.Cancel();
            foreach (var r in _streams.Values) r?.Abort();
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, new CancellationTokenSource(2000).Token);
            }
            catch { /* already gone */ }
            log.Audit(AuditEvent.TunnelEnded, $"Jump connection ended: {account} from {source} after {DateTime.UtcNow - started:hh\\:mm\\:ss}, " +
                                              $"{_opened} connection(s) ({reason})");
        }
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[TunnelProtocol.MaxFrame];
        while (true)
        {
            var length = 0;
            ValueWebSocketReceiveResult r;
            do
            {
                if (length == buffer.Length) throw new InvalidDataException("frame too large");
                r = await _ws.ReceiveAsync(buffer.AsMemory(length), _ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                length += r.Count;
            } while (!r.EndOfMessage);
            if (r.MessageType != WebSocketMessageType.Binary || !TunnelProtocol.TryRead(buffer.AsSpan(0, length), out var type, out var id))
                throw new InvalidDataException("not a tunnel frame");
            var payload = buffer.AsSpan(TunnelProtocol.HeaderSize, length - TunnelProtocol.HeaderSize);
            switch (type)
            {
                case TunnelMessage.Open:
                    Open(id, JsonSerializer.Deserialize(payload, TunnelJson.Default.TunnelOpen) ?? throw new InvalidDataException("empty Open"));
                    break;
                case TunnelMessage.Data:
                    if (_streams.TryGetValue(id, out var target) && target is not null) target.Received(payload.ToArray());
                    break; // a stream that just ended: its data is dropped
                case TunnelMessage.Credit:
                    if (_streams.TryGetValue(id, out var credited) && credited is not null) credited.AddCredit(TunnelProtocol.ReadCredit(payload));
                    break;
                case TunnelMessage.Close:
                    if (_streams.TryGetValue(id, out var closing) && closing is not null) closing.RemoteClosed();
                    break;
                case TunnelMessage.Reset:
                    if (_streams.TryGetValue(id, out var reset) && reset is not null) reset.Abort();
                    break;
                default:
                    throw new InvalidDataException($"unexpected frame {type}");
            }
        }
    }

    private void Open(uint id, TunnelOpen open)
    {
        var p = policy();
        if (_streams.ContainsKey(id)) throw new InvalidDataException($"stream {id} is already open");
        if (_streams.Count >= p.MaxTunnelStreams)
        {
            Unobserved(Refuse(id, TunnelRefusal.Limit, $"Too many connections through this jump host at once (MaxTunnelStreams = {p.MaxTunnelStreams}).", null));
            return;
        }
        if (!_streams.TryAdd(id, null)) throw new InvalidDataException($"stream {id} is already open");
        _ = Task.Run(() => OpenAsync(id, open, p));
    }

    private async Task OpenAsync(uint id, TunnelOpen open, AgentPolicy p)
    {
        var host = open.Host.Trim().Trim('[', ']');
        var where = $"{host}:{open.Port}";
        Socket? socket = null;
        Relay? relay = null;
        try
        {
            if (host.Length == 0 || open.Port is < 1 or > 65535) { await Refuse(id, TunnelRefusal.Unreachable, $"{where} is not a valid target.", null); return; }
            if (!p.TunnelEnabled) { await Refused(id, where, "TunnelEnabled = 0", "Jump connections are turned off on this device by policy (TunnelEnabled)."); return; }
            if (!p.Tunnel.PortAllowed(open.Port))
            {
                await Refused(id, where, "TunnelPorts", $"This jump host's policy doesn't allow connections to port {open.Port} (TunnelPorts).");
                return;
            }

            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
            else
            {
                using var resolveTimeout = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                resolveTimeout.CancelAfter(ResolveTimeout);
                try { addresses = await _net.Resolve(host, resolveTimeout.Token); }
                catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !_ct.IsCancellationRequested))
                {
                    await Refuse(id, TunnelRefusal.Dns, $"The jump host can't resolve {host}.", null);
                    return;
                }
                if (addresses.Length == 0) { await Refuse(id, TunnelRefusal.Dns, $"The jump host can't resolve {host}.", null); return; }
            }

            var allowed = p.Tunnel.Allowed(host, addresses);
            if (allowed.Count == 0)
            {
                var rule = addresses.All(TunnelRules.IsSpecial) ? "own or special address" : "TunnelTargets";
                await Refused(id, where, rule, addresses.All(TunnelRules.IsSpecial)
                    ? $"{host} is this jump host itself or a special address ({string.Join(", ", addresses.Select(a => a.ToString()))}); its policy doesn't allow it (TunnelTargets)."
                    : $"This jump host's policy doesn't allow connections to {host} (TunnelTargets).");
                return;
            }

            var local = _net.LocalAddressFor(allowed[0]);
            if (open.Probe) // only asked from where it would connect (e.g. for the device's firewall scope)
            {
                await Refuse(id, TunnelRefusal.Probe, $"{where} is allowed; not connected (probe).", local?.ToString());
                return;
            }
            Exception? last = null;
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_ct))
            {
                connectTimeout.CancelAfter(ConnectTimeout);
                foreach (var address in allowed)
                {
                    try { socket = await _net.Connect(new IPEndPoint(address, open.Port), connectTimeout.Token); break; }
                    catch (Exception ex) when (!_ct.IsCancellationRequested) { last = ex; if (connectTimeout.IsCancellationRequested) break; }
                }
            }
            if (socket is null)
            {
                var timeout = last is OperationCanceledException || last is SocketException { SocketErrorCode: SocketError.TimedOut };
                log.Write($"Jump connection {account} from {source}: {where} unreachable ({last?.Message})");
                await Refuse(id, timeout ? TunnelRefusal.Timeout : TunnelRefusal.Unreachable,
                    timeout ? $"No answer from {where} in time." : $"{where} can't be reached from the jump host ({(last as SocketException)?.SocketErrorCode.ToString() ?? last?.Message}).",
                    local?.ToString());
                return;
            }

            var remote = (IPEndPoint)socket.RemoteEndPoint!;
            relay = new Relay(this, id, socket, host, remote);
            _streams[id] = relay;
            Interlocked.Increment(ref _opened);
            await Send(TunnelProtocol.Json(TunnelMessage.Opened, id, new TunnelOpened(remote.Address.ToString(), (socket.LocalEndPoint as IPEndPoint)?.Address.ToString()), TunnelJson.Default.TunnelOpened));
            relay.Start();
        }
        catch (Exception ex)
        {
            // The target connection, if there is one, must not outlive this failure (e.g. the WebSocket dropped while answering).
            if (relay is not null) relay.Abort();
            else socket?.Dispose();
            _streams.TryRemove(new KeyValuePair<uint, Relay?>(id, null));
            if (ex is OperationCanceledException && _ct.IsCancellationRequested) return;
            log.Write($"Jump connection {account} from {source}: opening {where} failed: {ex.Message}");
            try { await Refuse(id, TunnelRefusal.Unreachable, $"Opening {where} failed on the jump host: {ex.Message}", null); } catch { /* tunnel gone */ }
        }
    }

    /// <summary>A send nobody waits for: a failure (the tunnel closing) is expected and ignored.</summary>
    private static void Unobserved(Task t) => t.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

    /// <summary>Refused by policy: audited.</summary>
    private Task Refused(uint id, string where, string rule, string message)
    {
        log.Audit(AuditEvent.TunnelRefused, $"Jump connection refused: {account} from {source} to {where} ({rule})");
        return Refuse(id, TunnelRefusal.Policy, message, null);
    }

    private Task Refuse(uint id, string reason, string message, string? local)
    {
        _streams.TryRemove(new KeyValuePair<uint, Relay?>(id, null)); // only a stream still opening – never a live one
        return Send(TunnelProtocol.Json(TunnelMessage.Refused, id, new TunnelRefused(reason, message, local), TunnelJson.Default.TunnelRefused));
    }

    private async Task Send(byte[] frame)
    {
        await _sendLock.WaitAsync(_ct);
        try { await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, _ct); }
        finally { _sendLock.Release(); }
    }

    private void Ended(Relay r)
    {
        if (!_streams.TryRemove(new KeyValuePair<uint, Relay?>(r.Id, r))) return;
        log.Audit(AuditEvent.TunnelStream, $"Jump connection: {account} from {source} to {r.Name}:{r.Remote.Port} ({r.Remote.Address}) for " +
                                           $"{r.Elapsed:hh\\:mm\\:ss}, {r.BytesIn:N0} bytes sent, {r.BytesOut:N0} bytes received");
    }

    /// <summary>One TCP connection to a target, both directions with flow control.</summary>
    private sealed class Relay(TunnelEndpoint owner, uint id, Socket socket, string name, IPEndPoint remote)
    {
        private readonly NetworkStream _stream = new(socket, ownsSocket: true);
        private readonly Channel<byte[]> _toTarget = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private readonly SemaphoreSlim _creditChanged = new(0);
        private readonly CancellationTokenSource _abort = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _credit = TunnelProtocol.Window;
        private int _unacknowledged;
        private int _done; // bit 1: target → RemoteHub finished, bit 2: RemoteHub → target finished
        private int _ended;

        public uint Id => id;
        public string Name => name;
        public IPEndPoint Remote => remote;
        public TimeSpan Elapsed => _clock.Elapsed;
        public long BytesIn, BytesOut;

        public void Start()
        {
            _ = Task.Run(ToTarget);
            _ = Task.Run(FromTarget);
        }

        public void Received(byte[] data)
        {
            if (Interlocked.Add(ref _unacknowledged, data.Length) > TunnelProtocol.Window)
            {
                Failed(); // more than the window allows
                return;
            }
            _toTarget.Writer.TryWrite(data);
        }

        public void AddCredit(int bytes)
        {
            if (bytes <= 0) return;
            Interlocked.Add(ref _credit, bytes);
            _creditChanged.Release();
        }

        public void RemoteClosed()
        {
            _toTarget.Writer.TryComplete();
            // The target may still answer; if it doesn't close as well, don't keep the connection forever.
            _ = Task.Delay(DrainTimeout, _abort.Token).ContinueWith(t => { if (!t.IsCanceled) Failed(); }, TaskScheduler.Default);
        }

        public void Abort()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 1) return;
            _abort.Cancel();
            _toTarget.Writer.TryComplete();
            try { socket.LingerState = new LingerOption(true, 0); } catch { /* closed */ }
            _stream.Dispose();
            owner.Ended(this);
        }

        private async Task ToTarget()
        {
            try
            {
                await foreach (var data in _toTarget.Reader.ReadAllAsync(_abort.Token))
                {
                    await _stream.WriteAsync(data, _abort.Token);
                    BytesIn += data.Length;
                    Interlocked.Add(ref _unacknowledged, -data.Length);
                    await owner.Send(TunnelProtocol.Credit(id, data.Length));
                }
                socket.Shutdown(SocketShutdown.Send);
                Finished(2);
            }
            catch { Failed(); }
        }

        private async Task FromTarget()
        {
            var buffer = new byte[TunnelProtocol.MaxData];
            try
            {
                while (true)
                {
                    while (Volatile.Read(ref _credit) <= 0) await _creditChanged.WaitAsync(_abort.Token);
                    var n = await _stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, Volatile.Read(ref _credit))), _abort.Token);
                    if (n == 0)
                    {
                        await owner.Send(TunnelProtocol.Frame(TunnelMessage.Close, id));
                        Finished(1);
                        return;
                    }
                    Interlocked.Add(ref _credit, -n);
                    BytesOut += n;
                    await owner.Send(TunnelProtocol.Frame(TunnelMessage.Data, id, buffer.AsSpan(0, n)));
                }
            }
            catch { Failed(); }
        }

        private void Finished(int bit)
        {
            if ((Interlocked.Or(ref _done, bit) | bit) != 3) return;
            if (Interlocked.Exchange(ref _ended, 1) == 1) return;
            _abort.Cancel();
            _stream.Dispose();
            owner.Ended(this);
        }

        /// <summary>The target connection failed: tell RemoteHub, end the stream.</summary>
        private void Failed()
        {
            if (Volatile.Read(ref _ended) == 1) return;
            _ = owner.Send(TunnelProtocol.Frame(TunnelMessage.Reset, id)).ContinueWith(_ => { }, TaskScheduler.Default);
            Abort();
        }
    }
}

/// <summary>Name resolution and connections of the jump host (tests pass their own resolver).</summary>
internal sealed class TunnelNetwork(Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
{
    public static readonly TunnelNetwork System = new();

    public Task<IPAddress[]> Resolve(string host, CancellationToken ct) => resolve?.Invoke(host, ct) ?? Dns.GetHostAddressesAsync(host, ct);

    public async Task<Socket> Connect(IPEndPoint target, CancellationToken ct)
    {
        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(target, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>This device's own address towards <paramref name="target"/> (no packet is sent); null if unknown.</summary>
    public IPAddress? LocalAddressFor(IPAddress target)
    {
        try
        {
            using var s = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(target, 9);
            return ((IPEndPoint)s.LocalEndPoint!).Address;
        }
        catch { return null; }
    }
}
