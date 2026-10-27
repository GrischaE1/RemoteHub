using System.Net.WebSockets;
using System.Threading;

namespace RemoteHub.Agent;

/// <summary>
/// Whether a connection to the RemoteHub Broker still carries anything (this file is compiled into both RemoteHub and
/// the agent). A connection can die without a close – the PC locked into standby, the network changed, a router forgot
/// the connection – and the socket still looks open: a ping is buffered without an error and a read waits for ever.
/// RemoteHub then showed "Connected" while nothing reached a device. So every frame from the broker counts (the "pong"
/// its edge answers each ping with, too); when nothing came for two keep-alive intervals and <see cref="Grace"/>, or a
/// ping can't be sent, the connection is given up and the link connects again. After a wake, an unlock or a network
/// change <see cref="Check"/> pings at once and gives the broker <see cref="CheckWindow"/> to answer.
/// </summary>
public sealed class BrokerLiveness
{
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private DateTime _heard, _nextPing;
    private DateTime? _checkBy;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="every">The keep-alive interval: a ping this often.</param>
    /// <param name="clock">For tests: the time (UTC).</param>
    /// <param name="grace">For tests: <see cref="Grace"/> (15 s).</param>
    /// <param name="checkWindow">For tests: <see cref="CheckWindow"/> (10 s).</param>
    public BrokerLiveness(TimeSpan every, Func<DateTime>? clock = null, TimeSpan? grace = null, TimeSpan? checkWindow = null)
    {
        Every = every;
        Grace = grace ?? TimeSpan.FromSeconds(15);
        CheckWindow = checkWindow ?? TimeSpan.FromSeconds(10);
        _now = clock ?? (() => DateTime.UtcNow);
        _heard = _now();
        _nextPing = _heard + every;
    }

    public TimeSpan Every { get; }

    /// <summary>Beyond two intervals, how long an answer may take.</summary>
    public TimeSpan Grace { get; }

    /// <summary>How long the broker has to answer the ping after a <see cref="Check"/>, and a ping to be sent.</summary>
    public TimeSpan CheckWindow { get; }

    /// <summary>How long the broker may be silent: two pings and their answers, and <see cref="Grace"/>.</summary>
    public TimeSpan Limit => Every + Every + Grace;

    /// <summary>A frame came from the broker – any message, a pong included.</summary>
    public void Heard()
    {
        lock (_lock)
        {
            _heard = _now();
            _checkBy = null;
        }
    }

    /// <summary>A wake, an unlock or a network change: a ping now, and an answer expected within <see cref="CheckWindow"/>.</summary>
    public void Check()
    {
        TaskCompletionSource wake;
        lock (_lock)
        {
            var now = _now();
            _checkBy ??= now + CheckWindow;
            _nextPing = now;
            wake = _wake;
        }
        wake.TrySetResult();
    }

    /// <summary>Why the connection is to be given up now, or null while it is alive.</summary>
    public string? Dead()
    {
        lock (_lock)
        {
            var now = _now();
            if (_checkBy is { } by && now >= by) return $"no answer within {(int)CheckWindow.TotalSeconds} s after a wake or a network change";
            return now - _heard > Limit ? $"nothing from the broker for {(int)(now - _heard).TotalSeconds} s" : null;
        }
    }

    /// <summary>Whether a ping is due; if so, the next one is due an interval later.</summary>
    public bool PingDue()
    {
        lock (_lock)
        {
            var now = _now();
            if (now < _nextPing) return false;
            _nextPing = now + Every;
            return true;
        }
    }

    /// <summary>
    /// The keep-alive of one connection: <paramref name="ping"/> when one is due (the caller's send, so it never overlaps
    /// another send on the socket), and <paramref name="ws"/> aborted when <see cref="Dead"/> or a ping fails – the
    /// connection's reader then ends with an error and the link connects again. Returns why it gave up; null when
    /// <paramref name="ct"/> ended it.
    /// </summary>
    public async Task<string?> KeepAliveAsync(WebSocket ws, Func<CancellationToken, Task> ping, CancellationToken ct)
    {
        // Looked at often enough that a dead connection is noticed within a second (a tenth of the interval in tests).
        var tick = TimeSpan.FromTicks(Math.Clamp(Every.Ticks / 10, TimeSpan.TicksPerMillisecond * 10, TimeSpan.TicksPerSecond));
        while (!ct.IsCancellationRequested)
        {
            if (Dead() is { } why)
            {
                ws.Abort();
                return why;
            }
            if (PingDue())
            {
                // A send can hang on a dead connection (a full buffer, or the send lock held by a request stuck the same way).
                try { await ping(ct).WaitAsync(CheckWindow, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
                catch (Exception ex)
                {
                    ws.Abort();
                    return "the keep-alive couldn't be sent: " + ex.Message;
                }
            }
            Task wake;
            lock (_lock)
            {
                if (_wake.Task.IsCompleted) _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
                wake = _wake.Task;
            }
            await Task.WhenAny(wake, Task.Delay(tick, ct)).ConfigureAwait(false);
        }
        return null;
    }
}
