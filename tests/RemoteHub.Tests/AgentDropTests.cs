extern alias agent;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// The agent's side of a WebSocket, driven message by message: a close frame from RemoteHub, or a drop the agent notices
/// first – .NET's keep-alive timeout ends the pending receive with an <see cref="OperationCanceledException"/>, not a
/// <see cref="WebSocketException"/>, which a real socket can't be made to do on demand.
/// </summary>
internal sealed class ScriptedSocket : WebSocket
{
    private static readonly object CloseFrame = new();
    private readonly Channel<object> _in = Channel.CreateUnbounded<object>();
    private volatile WebSocketState _state = WebSocketState.Open;

    /// <summary>Everything the agent sent, in order.</summary>
    public readonly BlockingCollection<byte[]> Sent = new();

    private readonly ConcurrentDictionary<byte, int> _kinds = new();

    /// <summary>How many messages of this kind (first byte) the agent sent so far, taken by a test or not.</summary>
    public int SentOf(byte kind) => _kinds.TryGetValue(kind, out var n) ? n : 0;

    public void Receive(byte[] message) => _in.Writer.TryWrite(message);
    public void Receive(string line) => Receive(Encoding.UTF8.GetBytes(line));
    public void Close() => _in.Writer.TryWrite(CloseFrame);
    public void Drop() => _in.Writer.TryWrite(new OperationCanceledException("Aborted"));

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        var item = await _in.Reader.ReadAsync(ct);
        if (item is Exception e)
        {
            _state = WebSocketState.Aborted;
            throw e;
        }
        if (item == CloseFrame)
        {
            _state = _state == WebSocketState.CloseSent ? WebSocketState.Closed : WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, null);
        }
        var m = (byte[])item;
        m.CopyTo(buffer.Array!, buffer.Offset);
        return new WebSocketReceiveResult(m.Length, WebSocketMessageType.Binary, true);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_state is not (WebSocketState.Open or WebSocketState.CloseReceived)) throw new WebSocketException(WebSocketError.InvalidState);
        if (buffer.Count > 0) _kinds.AddOrUpdate(buffer[0], 1, (_, n) => n + 1);
        Sent.Add(buffer.ToArray());
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct)
    {
        _state = _state == WebSocketState.CloseReceived ? WebSocketState.Closed : WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct)
    {
        await CloseOutputAsync(closeStatus, statusDescription, ct);
        _state = WebSocketState.Closed;
    }

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;
    public override void Abort() => _state = WebSocketState.Aborted;
    public override void Dispose() { }
}

/// <summary>
/// How the agent tells a dropped connection from one closed on purpose (lab P-SES-06): the PowerShell relay's end reason
/// for event 1001, whichever side notices the end first.
/// </summary>
public class AgentDropTests
{
    /// <summary>PowerShell's standard output: nothing until <see cref="End"/>, like a shell waiting for input.</summary>
    private sealed class QuietOutput : Stream
    {
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void End() => _ended.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await _ended.Task.WaitAsync(ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Shell : A.IShellProcess
    {
        public readonly MemoryStream In = new();
        public readonly QuietOutput Out = new();
        public Stream Input => In;
        public Stream Output => Out;
        public int ProcessId => 0;
        public void Dispose() { }
    }

    private static async Task<string> Run(Action<ScriptedSocket, Shell, CancellationTokenSource> end)
    {
        var ws = new ScriptedSocket();
        var shell = new Shell();
        using var aborted = new CancellationTokenSource();
        var run = A.Tunnel.RunAsync(ws, shell, aborted.Token);
        ws.Receive("<Command/>"); // a line for PowerShell first: the relay is running
        SpinWait.SpinUntil(() => shell.In.Length > 0, TimeSpan.FromSeconds(10));
        Assert.True(shell.In.Length > 0, "the line reached PowerShell");
        end(ws, shell, aborted);
        return await run.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_drop_the_agent_notices_first_is_a_lost_connection()
    {
        Assert.Equal("connection lost", await Run((ws, _, _) => ws.Drop()));
    }

    [Fact]
    public async Task An_aborted_request_is_a_lost_connection_whichever_side_ends_first()
    {
        Assert.Equal("connection lost", await Run((_, _, aborted) => aborted.Cancel()));
    }

    [Fact]
    public async Task A_close_from_RemoteHub_and_PowerShell_ending_keep_their_reasons()
    {
        Assert.Equal("closed by RemoteHub", await Run((ws, _, _) => ws.Close()));
        Assert.Equal("PowerShell ended", await Run((_, shell, _) => shell.Out.End()));
    }
}
