namespace RemoteHub.Remoting.Decoder;

/// <summary>
/// The decoder of one agent desktop session (docs/decoder-isolation-design.md §3.1, §3.4): started when first needed,
/// and started again after it misbehaves – but more than <see cref="MaxRestarts"/> failures in a minute and the session
/// gives up, so a device can't get endless tries at the decoder. Thread-safe.
/// </summary>
public sealed class SessionDecoder : IDisposable
{
    public const int MaxRestarts = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Func<DecoderHost> _start;
    private readonly Action<string, bool> _stopped;
    private readonly Func<DateTime> _now;
    private readonly Queue<DateTime> _failures = new();
    private readonly object _gate = new();
    private DecoderHost? _host;
    private bool _disposed;

    /// <param name="start">Starts a decoder (<see cref="DecoderHost.Start"/>).</param>
    /// <param name="stopped">A decoder misbehaved and was ended: the reason, and whether the session gives up now.</param>
    public SessionDecoder(Func<DecoderHost> start, Action<string, bool> stopped, Func<DateTime>? now = null)
    {
        _start = start;
        _stopped = stopped;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Too many failures: nothing more is decoded in this session.</summary>
    public bool GaveUp { get; private set; }

    /// <summary>Gave up, or the session closed it: no more decoding.</summary>
    public bool Stopped => GaveUp || _disposed;

    /// <summary>Runs <paramref name="call"/> on the session's decoder, starting one if there is none.</summary>
    /// <exception cref="DecoderFailedException">The decoder misbehaved (it has been ended), or the session gave up.</exception>
    /// <exception cref="DecoderUnavailableException">No sandbox can be made on this PC.</exception>
    public T Use<T>(Func<DecoderHost, T> call)
    {
        DecoderHost host;
        lock (_gate)
        {
            if (GaveUp || _disposed) throw new DecoderFailedException("the decoder was stopped too often");
            try { host = _host ??= _start(); }
            catch (DecoderFailedException ex)
            {
                Failed(null, ex.Message);
                throw;
            }
        }
        try { return call(host); }
        catch (DecoderFailedException ex)
        {
            lock (_gate) Failed(host, ex.Message);
            throw;
        }
    }

    private void Failed(DecoderHost? host, string reason)
    {
        if (_disposed) return; // the session closed it while a call was waiting: not a failure
        if (host is not null)
        {
            if (!ReferenceEquals(_host, host)) return; // the video and an image saw the same failure: counted once
            _host = null;
            host.Dispose();
        }
        var now = _now();
        _failures.Enqueue(now);
        while (_failures.Count > 0 && now - _failures.Peek() > Window) _failures.Dequeue();
        if (_failures.Count > MaxRestarts) GaveUp = true;
        _stopped(reason, GaveUp);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _host?.Dispose();
            _host = null;
        }
    }
}
