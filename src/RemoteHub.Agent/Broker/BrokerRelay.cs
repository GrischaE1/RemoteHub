using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;

namespace RemoteHub.Agent;

/// <summary>
/// The agent's side of a relayed broker session: one WebSocket through the broker, carrying the
/// jump host's stream framing end to end with the console. Unlike a jump connection a stream has <b>no target</b> – an
/// <see cref="TunnelMessage.Open"/> with any payload is a protocol error – and always ends at this agent's own HTTPS
/// endpoint through <see cref="PeerTransport"/>. Reaching other hosts is <c>/v1/tunnel</c> inside such a stream,
/// signed in and checked as always.
/// </summary>
internal sealed class BrokerRelay(PeerTransport peers, IPEndPoint source, int maxStreams) : IRelayOwner
{
    private readonly ConcurrentDictionary<uint, StreamRelay> _streams = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private WebSocket _ws = null!;
    private CancellationToken _ct;
    private int _opened;

    public int StreamsOpened => _opened;
    public long BytesIn, BytesOut;

    /// <summary>Runs until the relay closes; the reason, for the audit.</summary>
    public async Task<string> RunAsync(WebSocket ws, CancellationToken aborted)
    {
        _ws = ws;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        _ct = cts.Token;
        try
        {
            await ReceiveLoop();
            return "closed by the console";
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException) { return "connection lost"; }
        catch (InvalidDataException ex) { return "protocol error: " + ex.Message; }
        finally
        {
            cts.Cancel();
            foreach (var r in _streams.Values) r.Abort();
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, new CancellationTokenSource(2000).Token);
            }
            catch { /* already gone */ }
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
                throw new InvalidDataException("not a relay frame");
            var payload = buffer.AsSpan(TunnelProtocol.HeaderSize, length - TunnelProtocol.HeaderSize);
            switch (type)
            {
                case TunnelMessage.Open:
                    if (payload.Length != 0) throw new InvalidDataException("a broker stream has no target");
                    await Open(id);
                    break;
                case TunnelMessage.Data:
                    if (_streams.TryGetValue(id, out var target)) target.Received(payload.ToArray());
                    break; // a stream that just ended: its data is dropped
                case TunnelMessage.Credit:
                    if (_streams.TryGetValue(id, out var credited)) credited.AddCredit(TunnelProtocol.ReadCredit(payload));
                    break;
                case TunnelMessage.Close:
                    if (_streams.TryGetValue(id, out var closing)) closing.RemoteClosed();
                    break;
                case TunnelMessage.Reset:
                    if (_streams.TryGetValue(id, out var reset)) reset.Abort();
                    break;
                default:
                    throw new InvalidDataException($"unexpected frame {type}");
            }
        }
    }

    private async Task Open(uint id)
    {
        if (_streams.ContainsKey(id)) throw new InvalidDataException($"stream {id} is already open");
        if (_streams.Count >= maxStreams)
        {
            await Send(TunnelProtocol.Json(TunnelMessage.Refused, id, new TunnelRefused(TunnelRefusal.Limit,
                $"Too many connections through the RemoteHub Broker at once (MaxTunnelStreams = {maxStreams}).", null), TunnelJson.Default.TunnelRefused));
            return;
        }
        if (peers.Connect(source) is not PeerTransport.DuplexStream stream)
        {
            await Send(TunnelProtocol.Json(TunnelMessage.Refused, id, new TunnelRefused(TunnelRefusal.Unreachable, "The agent is stopping.", null), TunnelJson.Default.TunnelRefused));
            return;
        }
        var relay = new StreamRelay(this, id, stream, stream.CompleteOutput) { Name = "agent", Remote = source };
        _streams[id] = relay;
        Interlocked.Increment(ref _opened);
        await Send(TunnelProtocol.Json(TunnelMessage.Opened, id, new TunnelOpened("agent", null), TunnelJson.Default.TunnelOpened));
        relay.Start();
    }

    public async Task Send(byte[] frame)
    {
        await _sendLock.WaitAsync(_ct);
        try { await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, _ct); }
        finally { _sendLock.Release(); }
    }

    public void Ended(StreamRelay r)
    {
        if (!_streams.TryRemove(new KeyValuePair<uint, StreamRelay>(r.Id, r))) return;
        Interlocked.Add(ref BytesIn, r.BytesIn);
        Interlocked.Add(ref BytesOut, r.BytesOut);
    }
}
