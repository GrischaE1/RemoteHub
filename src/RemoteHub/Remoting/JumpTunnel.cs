using System.Collections.Concurrent;
using System.Management.Automation;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using RemoteHub.Agent;

namespace RemoteHub.Remoting;

/// <summary>The jump host a device is reached through: its RemoteHub Agent and the account to sign in there with.</summary>
public sealed record JumpRoute(string Name, string Host, AgentEndpoint Endpoint, PSCredential Credential)
{
    /// <summary>Same jump host and account = the same tunnel.</summary>
    internal string Key => $"{Host.Trim().ToLowerInvariant()}|{Endpoint.Port}|{Endpoint.Fingerprint}|{Credential.UserName.ToLowerInvariant()}";
}

/// <summary>A jump host refused or couldn't make a connection; <see cref="Exception.Message"/> is for the user.</summary>
public sealed class JumpHostException(string message, string? reason = null, string? local = null, Exception? inner = null) : IOException(message, inner)
{
    /// <summary><see cref="TunnelRefusal"/>, or null when the jump host itself couldn't be used.</summary>
    public string? Reason { get; } = reason;
    /// <summary>The jump host's own address towards the target, when it said so.</summary>
    public string? Local { get; } = local;
}

/// <summary>
/// The tunnels to jump hosts: one per jump host and account, shared by every connection through it, closed after a while
/// without connections.
/// </summary>
public static class JumpTunnels
{
    private static readonly Dictionary<string, Task<JumpTunnel>> Tunnels = [];

    /// <summary>A TCP connection from the jump host to <paramref name="host"/>:<paramref name="port"/>.</summary>
    public static Task<TunnelStream> OpenAsync(JumpRoute route, string host, int port, CancellationToken ct) => OpenAsync(route, host, port, probe: false, ct);

    /// <summary>
    /// The jump host's own address towards <paramref name="host"/> – what the device sees connections from (for its firewall
    /// scope) – without connecting to it; null if the jump host can't tell.
    /// </summary>
    public static async Task<string?> LocalAddressAsync(JumpRoute route, string host, int port, CancellationToken ct)
    {
        try { await using var s = await OpenAsync(route, host, port, probe: true, ct).ConfigureAwait(false); return s.LocalAddress; } // an older agent connects
        catch (JumpHostException ex) { return ex.Local; }
    }

    private static async Task<TunnelStream> OpenAsync(JumpRoute route, string host, int port, bool probe, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var tunnel = await Get(route, ct).ConfigureAwait(false);
            try { return await tunnel.OpenAsync(host, port, ct, probe).ConfigureAwait(false); }
            catch (ObjectDisposedException) when (attempt == 0) { /* closed meanwhile (idle or dropped): open a new one */ }
        }
    }

    private static async Task<JumpTunnel> Get(JumpRoute route, CancellationToken ct)
    {
        Task<JumpTunnel> task;
        lock (Tunnels)
        {
            if (!Tunnels.TryGetValue(route.Key, out task!) || task.IsFaulted || task.IsCanceled || task is { IsCompletedSuccessfully: true, Result.IsClosed: true })
                Tunnels[route.Key] = task = JumpTunnel.ConnectAsync(route);
        }
        try { return await task.WaitAsync(ct).ConfigureAwait(false); }
        catch when (task.IsFaulted)
        {
            lock (Tunnels)
                if (Tunnels.TryGetValue(route.Key, out var current) && current == task) Tunnels.Remove(route.Key);
            throw;
        }
    }

    /// <summary>Closes every tunnel (tests; RemoteHub closing).</summary>
    public static void CloseAll()
    {
        List<Task<JumpTunnel>> all;
        lock (Tunnels) { all = [.. Tunnels.Values]; Tunnels.Clear(); }
        foreach (var t in all)
            if (t.IsCompletedSuccessfully) t.Result.Close();
    }

    internal static int OpenTunnels
    {
        get { lock (Tunnels) return Tunnels.Values.Count(t => t is { IsCompletedSuccessfully: true, Result.IsClosed: false }); }
    }
}

/// <summary>
/// One signed-in connection to a jump host's agent, carrying any number of <see cref="TunnelStream"/>s – or a relayed
/// RemoteHub Broker session, where every stream goes to the device's own agent and an Open names no target
/// (broker-design.md §8.5): the same framing and flow control.
/// </summary>
public sealed class JumpTunnel
{
    internal static TimeSpan IdleClose = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(40);

    private readonly ClientWebSocket _ws;
    /// <summary>"the jump host JUMP01", or "PC-0815 through the RemoteHub Broker" – for messages.</summary>
    private readonly string _what;
    private readonly bool _targetless;
    private readonly ConcurrentDictionary<uint, TunnelStream> _streams = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<TunnelStream>> _opening = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _nextId;
    private int _closed;
    private System.Threading.Timer? _idle;

    private JumpTunnel(ClientWebSocket ws, string what, bool targetless = false)
    {
        _ws = ws;
        _what = what;
        _targetless = targetless;
        _ = Task.Run(ReceiveLoop);
        StartIdle();
    }

    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    internal static async Task<JumpTunnel> ConnectAsync(JumpRoute route)
    {
        var info = new AgentConnectionInfo(route.Host, route.Endpoint, route.Credential);
        try
        {
            var ws = await Task.Run(() => AgentClientTransport.Connect(info, CancellationToken.None, TunnelProtocol.Path)).ConfigureAwait(false);
            return new JumpTunnel(ws, $"the jump host {route.Name}");
        }
        catch (Exception ex)
        {
            throw new JumpHostException($"Can't use the jump host {route.Name}: {ex.Message}", inner: ex);
        }
    }

    /// <summary>A relayed broker session (<see cref="Broker.PeerSessions"/>): streams to the device's own agent only.</summary>
    internal static JumpTunnel OverRelay(ClientWebSocket ws, string deviceName) => new(ws, $"{deviceName} through the RemoteHub Broker", targetless: true);

    public async Task<TunnelStream> OpenAsync(string host, int port, CancellationToken ct, bool probe = false)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        var id = (uint)Interlocked.Increment(ref _nextId);
        var opened = new TaskCompletionSource<TunnelStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        _opening[id] = opened;
        StopIdle();
        TunnelStream? result = null;
        try
        {
            await Send(_targetless ? TunnelProtocol.Frame(TunnelMessage.Open, id)
                : TunnelProtocol.Json(TunnelMessage.Open, id, new TunnelOpen(host.Trim().Trim('[', ']'), port, probe), TunnelJson.Default.TunnelOpen)).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(OpenTimeout);
            return result = await opened.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !IsClosed)
        {
            throw new JumpHostException(_targetless ? $"{Cap(_what)} didn't answer in time." : $"{Cap(_what)} didn't answer in time when connecting to {host}:{port}.", TunnelRefusal.Timeout);
        }
        catch (Exception ex) when (ex is WebSocketException || (ex is OperationCanceledException && IsClosed))
        {
            Close(); // the tunnel is broken: the caller opens a new one
            throw new ObjectDisposedException(nameof(JumpTunnel));
        }
        finally
        {
            // Nobody takes a stream that opened while we gave up: close it (a later Opened is reset in ReceiveLoop).
            _opening.TryRemove(id, out _);
            if (result is null && !opened.TrySetCanceled() && opened.Task.IsCompletedSuccessfully) opened.Task.Result.Dispose();
            if (_streams.IsEmpty && _opening.IsEmpty) StartIdle();
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        _idle?.Dispose();
        foreach (var s in _streams.Values) s.Reset("The connection to the jump host was closed.");
        foreach (var o in _opening.Values) o.TrySetException(new ObjectDisposedException(nameof(JumpTunnel)));
        // Still open (idle, or RemoteHub closing): a proper close, so the jump host's audit says so – not "connection lost".
        try
        {
            if (_ws.State == WebSocketState.Open)
                _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* gone */ }
        _cts.Cancel();
        try { _ws.Abort(); } catch { /* gone */ }
        _ws.Dispose();
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[TunnelProtocol.MaxFrame];
        var why = "The connection to the jump host was closed.";
        try
        {
            while (true)
            {
                var length = 0;
                ValueWebSocketReceiveResult r;
                do
                {
                    if (length == buffer.Length) throw new InvalidDataException("frame too large");
                    r = await _ws.ReceiveAsync(buffer.AsMemory(length), _cts.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    length += r.Count;
                } while (!r.EndOfMessage);
                if (!TunnelProtocol.TryRead(buffer.AsSpan(0, length), out var type, out var id)) throw new InvalidDataException("not a tunnel frame");
                var payload = buffer.AsSpan(TunnelProtocol.HeaderSize, length - TunnelProtocol.HeaderSize);
                switch (type)
                {
                    case TunnelMessage.Opened:
                    {
                        var opened = JsonSerializer.Deserialize(payload, TunnelJson.Default.TunnelOpened)!;
                        var stream = new TunnelStream(this, id, opened.Local);
                        if (_opening.TryRemove(id, out var waiting))
                        {
                            _streams[id] = stream;
                            if (!waiting.TrySetResult(stream)) { _streams.TryRemove(id, out _); SendQuietly(TunnelProtocol.Frame(TunnelMessage.Reset, id)); }
                        }
                        else SendQuietly(TunnelProtocol.Frame(TunnelMessage.Reset, id)); // gave up waiting
                        break;
                    }
                    case TunnelMessage.Refused:
                    {
                        var refused = JsonSerializer.Deserialize(payload, TunnelJson.Default.TunnelRefused)!;
                        if (_opening.TryRemove(id, out var waiting))
                            waiting.TrySetException(new JumpHostException(Explain(refused), refused.Reason, refused.Local));
                        break;
                    }
                    case TunnelMessage.Data:
                        if (_streams.TryGetValue(id, out var data)) data.Received(payload.ToArray());
                        break;
                    case TunnelMessage.Credit:
                        if (_streams.TryGetValue(id, out var credited)) credited.AddCredit(TunnelProtocol.ReadCredit(payload));
                        break;
                    case TunnelMessage.Close:
                        if (_streams.TryGetValue(id, out var closed)) closed.RemoteClosed();
                        break;
                    case TunnelMessage.Reset:
                        if (_streams.TryGetValue(id, out var reset)) reset.Reset($"{Cap(_what)} ended the connection.");
                        break;
                    default:
                        throw new InvalidDataException($"unexpected frame {type}");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            why = $"The connection to {_what} was lost ({ex.GetBaseException().Message}).";
        }
        catch (OperationCanceledException) { /* closed */ }
        finally
        {
            foreach (var s in _streams.Values) s.Reset(why);
            Close();
        }
    }

    private string Explain(TunnelRefused r) => r.Reason == TunnelRefusal.Policy
        ? $"{Cap(_what)} doesn't allow this connection. {r.Message}"
        : $"Through {_what}: {r.Message}";

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>A send nobody waits for: its failure (the tunnel closing) is expected and ignored.</summary>
    internal void SendQuietly(byte[] frame) => Send(frame).ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

    internal async Task Send(byte[] frame)
    {
        await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try { await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, _cts.Token).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    internal void Ended(TunnelStream s)
    {
        if (_streams.TryRemove(new KeyValuePair<uint, TunnelStream>(s.Id, s)) && _streams.IsEmpty && _opening.IsEmpty) StartIdle();
    }

    private void StartIdle()
    {
        if (IsClosed) return;
        var timer = new System.Threading.Timer(_ => { if (_streams.IsEmpty && _opening.IsEmpty) Close(); }, null, IdleClose, Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _idle, timer)?.Dispose();
    }

    private void StopIdle() => Interlocked.Exchange(ref _idle, null)?.Dispose();
}

/// <summary>A TCP connection made by a jump host, as a stream (what runs inside – TLS, RDP – is the caller's).</summary>
public sealed class TunnelStream : Stream
{
    private readonly JumpTunnel _tunnel;
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly SemaphoreSlim _creditChanged = new(0);
    private readonly CancellationTokenSource _reset = new();
    private readonly CancellationTokenSource _disposing = new();
    private byte[]? _current;
    private int _offset;
    private int _credit = TunnelProtocol.Window;
    private string? _resetReason;
    private int _state; // bit 1: we closed, bit 2: they closed
    private int _disposed;
    private int _outputClosed;

    internal TunnelStream(JumpTunnel tunnel, uint id, string? local)
    {
        _tunnel = tunnel;
        Id = id;
        LocalAddress = local;
    }

    internal uint Id { get; }
    /// <summary>The jump host's own address towards the target (what the target sees as the source).</summary>
    public string? LocalAddress { get; }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return 0;
        while (_current is null)
        {
            try
            {
                // Also released by Dispose (a pending read-ahead of a pooled HTTP connection must not hang).
                using var all = CancellationTokenSource.CreateLinkedTokenSource(ct, _reset.Token, _disposing.Token);
                if (!await _incoming.Reader.WaitToReadAsync(all.Token).ConfigureAwait(false))
                {
                    if (_reset.IsCancellationRequested) throw ResetError(); // cut off, not a normal end
                    return 0; // they closed
                }
                if (_incoming.Reader.TryRead(out var next)) (_current, _offset) = (next, 0); // else taken by Dispose meanwhile: wait again
            }
            catch (OperationCanceledException) when (_reset.IsCancellationRequested) { throw ResetError(); }
            catch (OperationCanceledException) when (_disposing.IsCancellationRequested) { throw new ObjectDisposedException(nameof(TunnelStream)); }
        }
        var current = _current;
        if (current is null) throw new ObjectDisposedException(nameof(TunnelStream)); // disposed while reading
        var n = Math.Min(buffer.Length, current.Length - _offset);
        current.AsSpan(_offset, n).CopyTo(buffer.Span);
        _offset += n;
        if (_offset == current.Length)
        {
            _current = null;
            await ReturnCredit(current.Length).ConfigureAwait(false);
        }
        return n;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (_reset.IsCancellationRequested) throw ResetError();
        while (buffer.Length > 0)
        {
            try
            {
                using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, _reset.Token);
                while (Volatile.Read(ref _credit) <= 0) await _creditChanged.WaitAsync(both.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_reset.IsCancellationRequested) { throw ResetError(); }
            var n = Math.Min(buffer.Length, Math.Min(TunnelProtocol.MaxData, Volatile.Read(ref _credit)));
            Interlocked.Add(ref _credit, -n);
            try { await _tunnel.Send(TunnelProtocol.Frame(TunnelMessage.Data, Id, buffer.Span[..n])).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { throw ResetError(); }
            buffer = buffer[n..];
        }
    }

    /// <summary>Half-close: nothing more is sent (the target sees the end); reading goes on until it closes its side too.</summary>
    public void CloseOutput()
    {
        if (Volatile.Read(ref _disposed) == 1 || _reset.IsCancellationRequested || Interlocked.Exchange(ref _outputClosed, 1) == 1) return;
        _tunnel.SendQuietly(TunnelProtocol.Frame(TunnelMessage.Close, Id));
    }

    internal void Received(byte[] data)
    {
        _incoming.Writer.TryWrite(data);
        if (Volatile.Read(ref _disposed) == 1) Drop(); // nobody reads any more: keep the other side moving
    }

    /// <summary>Credits and drops everything received but not read.</summary>
    private void Drop()
    {
        while (_incoming.Reader.TryRead(out var pending)) _ = ReturnCredit(pending.Length);
    }

    internal void AddCredit(int bytes)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _credit, bytes);
        _creditChanged.Release();
    }

    internal void RemoteClosed()
    {
        _incoming.Writer.TryComplete();
        Finished(2);
    }

    internal void Reset(string reason)
    {
        _resetReason ??= reason;
        _reset.Cancel(); // first, so a reader woken by the completion sees a reset, not a normal end
        _incoming.Writer.TryComplete();
        Finished(3);
    }

    private async Task ReturnCredit(int bytes)
    {
        try { await _tunnel.Send(TunnelProtocol.Credit(Id, bytes)).ConfigureAwait(false); }
        catch { /* the tunnel is gone; the stream ends with it */ }
    }

    private void Finished(int bits)
    {
        if ((Interlocked.Or(ref _state, bits) | bits) == 3) _tunnel.Ended(this);
    }

    private IOException ResetError() => new(_resetReason ?? "The connection through the jump host was reset.");

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // Our side is done; what still arrives is credited and dropped until the jump host closes its side too.
            _disposing.Cancel();
            if (_current is { } partlyRead) _ = ReturnCredit(partlyRead.Length);
            _current = null;
            Drop();
            if (!_reset.IsCancellationRequested && Interlocked.Exchange(ref _outputClosed, 1) == 0) _tunnel.SendQuietly(TunnelProtocol.Frame(TunnelMessage.Close, Id));
            Finished(1);
        }
        base.Dispose(disposing);
    }
}
