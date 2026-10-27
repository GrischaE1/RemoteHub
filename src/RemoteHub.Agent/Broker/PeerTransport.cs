using System.IO.Pipelines;
using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;

namespace RemoteHub.Agent;

/// <summary>Where Kestrel listens for streams that come through the RemoteHub Broker rather than over TCP.</summary>
internal sealed class PeerEndPoint : EndPoint
{
    public static readonly PeerEndPoint Instance = new();
    public override string ToString() => "remotehub-broker";
}

/// <summary>
/// A second Kestrel transport next to the TCP sockets: every stream of a broker session becomes
/// an ordinary connection to the agent's HTTPS endpoint – same certificate, same TLS, same sign-in, rights and limits as
/// a connection over the network. Its remote address is the console's public address as the broker saw it, so the
/// sign-in gate and the audit see where it came from.
/// </summary>
internal sealed class PeerTransport : IConnectionListenerFactory, IConnectionListenerFactorySelector
{
    private readonly Listener _listener = new();

    public bool CanBind(EndPoint endpoint) => endpoint is PeerEndPoint;

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default) =>
        endpoint is PeerEndPoint ? new(_listener) : throw new NotSupportedException($"{endpoint} isn't a broker endpoint.");

    /// <summary>
    /// A new connection into Kestrel; the returned stream is its other end (what the broker relay writes and reads).
    /// Null while Kestrel isn't listening yet or any more.
    /// </summary>
    public Stream? Connect(IPEndPoint remote) => _listener.Connect(remote);

    private sealed class Listener : IConnectionListener
    {
        private readonly Channel<ConnectionContext> _accepted = Channel.CreateUnbounded<ConnectionContext>();
        private volatile bool _bound = true;

        public EndPoint EndPoint => PeerEndPoint.Instance;

        public Stream? Connect(IPEndPoint remote)
        {
            if (!_bound) return null;
            var toKestrel = new Pipe();
            var fromKestrel = new Pipe();
            var connection = new PeerConnection(new Duplex(toKestrel.Reader, fromKestrel.Writer), remote);
            if (!_accepted.Writer.TryWrite(connection)) return null;
            return new DuplexStream(fromKestrel.Reader, toKestrel.Writer);
        }

        public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            try { return await _accepted.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return null; }
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
        {
            _bound = false;
            _accepted.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => UnbindAsync();
    }

    private sealed class Duplex(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input => input;
        public PipeWriter Output => output;
    }

    private sealed class PeerConnection : ConnectionContext, IConnectionEndPointFeature
    {
        private readonly CancellationTokenSource _closed = new();

        public PeerConnection(IDuplexPipe transport, IPEndPoint remote)
        {
            Transport = transport;
            RemoteEndPoint = remote;
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            Features.Set<IConnectionEndPointFeature>(this);
            ConnectionClosed = _closed.Token;
        }

        public override string ConnectionId { get; set; } = "broker-" + Guid.NewGuid().ToString("N")[..12];
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();
        public override IDuplexPipe Transport { get; set; }
        public override EndPoint? RemoteEndPoint { get; set; }
        public override EndPoint? LocalEndPoint { get; set; }

        public override void Abort(ConnectionAbortedException abortReason)
        {
            Transport.Input.CancelPendingRead();
            Transport.Output.Complete(abortReason);
            if (!_closed.IsCancellationRequested) _closed.Cancel();
        }

        public override async ValueTask DisposeAsync()
        {
            await Transport.Input.CompleteAsync();
            await Transport.Output.CompleteAsync();
            if (!_closed.IsCancellationRequested) _closed.Cancel();
            _closed.Dispose();
        }
    }

    /// <summary>The relay's end of a peer connection: reads what Kestrel writes, writes what Kestrel reads.</summary>
    internal sealed class DuplexStream(PipeReader input, PipeWriter output) : Stream
    {
        private readonly Stream _read = input.AsStream();
        private readonly Stream _write = output.AsStream();

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _read.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _write.WriteAsync(buffer, cancellationToken);
        public override void Flush() => _write.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>The other side closed its direction: Kestrel reads the end of the stream.</summary>
        public void CompleteOutput() => output.Complete();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                input.Complete();
                output.Complete();
            }
            base.Dispose(disposing);
        }
    }
}
