using System.Diagnostics;
using System.Net;
using System.Threading.Channels;

namespace RemoteHub.Agent;

/// <summary>What a <see cref="StreamRelay"/> needs from the connection that carries it: sending frames, and hearing that it ended.</summary>
internal interface IRelayOwner
{
    Task Send(byte[] frame);
    void Ended(StreamRelay relay);
}

/// <summary>
/// One stream of <see cref="TunnelProtocol"/>'s framing, copied both ways with its flow control: at most
/// <see cref="TunnelProtocol.Window"/> bytes ahead of the credit the other side returned, half-close by
/// <see cref="TunnelMessage.Close"/>, <see cref="TunnelMessage.Reset"/> on failure. The jump host runs it over a TCP
/// connection to a target (<see cref="TunnelEndpoint"/>), the broker relay over a connection into the agent itself
/// (<see cref="BrokerRelay"/>).
/// </summary>
/// <param name="shutdownSend">The other side closed its direction: end ours towards the target (a TCP half-close).</param>
/// <param name="abortHard">End the target connection at once (e.g. a TCP reset rather than a graceful close).</param>
internal sealed class StreamRelay(IRelayOwner owner, uint id, Stream stream, Action shutdownSend, Action? abortHard = null)
{
    /// <summary>After the other side closed its direction: how long the target may still send before the stream is cut.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(60);

    private readonly Channel<byte[]> _toTarget = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _creditChanged = new(0);
    private readonly CancellationTokenSource _abort = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _credit = TunnelProtocol.Window;
    private int _unacknowledged;
    private int _done; // bit 1: target → other side finished, bit 2: other side → target finished
    private int _ended;

    public uint Id => id;
    /// <summary>For the audit: the name it was asked for and where it connected.</summary>
    public string Name { get; init; } = "";
    public IPEndPoint? Remote { get; init; }
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
        try { abortHard?.Invoke(); } catch { /* closed */ }
        stream.Dispose();
        owner.Ended(this);
    }

    private async Task ToTarget()
    {
        try
        {
            await foreach (var data in _toTarget.Reader.ReadAllAsync(_abort.Token))
            {
                await stream.WriteAsync(data, _abort.Token);
                BytesIn += data.Length;
                Interlocked.Add(ref _unacknowledged, -data.Length);
                await owner.Send(TunnelProtocol.Credit(id, data.Length));
            }
            shutdownSend();
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
                var n = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, Volatile.Read(ref _credit))), _abort.Token);
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
        stream.Dispose();
        owner.Ended(this);
    }

    /// <summary>The target connection failed: tell the other side, end the stream.</summary>
    private void Failed()
    {
        if (Volatile.Read(ref _ended) == 1) return;
        _ = owner.Send(TunnelProtocol.Frame(TunnelMessage.Reset, id)).ContinueWith(_ => { }, TaskScheduler.Default);
        Abort();
    }
}
