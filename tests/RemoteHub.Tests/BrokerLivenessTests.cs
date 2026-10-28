using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// The broker connections' liveness (BrokerLiveness, used by the console's BrokerClient and the agent's BrokerLink): a
/// connection that stops carrying anything is given up, whether the socket says so or not.
/// </summary>
public class BrokerLivenessTests
{
    private sealed class Clock
    {
        public DateTime Now = new(2026, 10, 27, 15, 38, 0, DateTimeKind.Utc);
        public void Add(double seconds) => Now = Now.AddSeconds(seconds);
    }

    [Fact]
    public void Silence_for_two_intervals_and_the_grace_gives_the_connection_up_and_every_frame_counts()
    {
        var clock = new Clock();
        var live = new BrokerLiveness(TimeSpan.FromSeconds(60), () => clock.Now);
        Assert.Equal(TimeSpan.FromSeconds(135), live.Limit);
        clock.Add(135);
        Assert.Null(live.Dead());
        // a pong (or any message) just in time: alive again for a whole limit
        live.Heard();
        clock.Add(135);
        Assert.Null(live.Dead());
        clock.Add(1);
        Assert.Equal("nothing from the broker for 136 s", live.Dead());
    }

    [Fact]
    public void A_ping_is_due_once_an_interval()
    {
        var clock = new Clock();
        var live = new BrokerLiveness(TimeSpan.FromSeconds(60), () => clock.Now);
        Assert.False(live.PingDue());
        clock.Add(59);
        Assert.False(live.PingDue());
        clock.Add(1);
        Assert.True(live.PingDue());
        Assert.False(live.PingDue()); // once, then the next interval
        clock.Add(60);
        Assert.True(live.PingDue());
    }

    [Fact]
    public void A_wake_or_a_network_change_pings_at_once_and_gives_the_broker_ten_seconds()
    {
        var clock = new Clock();
        var live = new BrokerLiveness(TimeSpan.FromSeconds(60), () => clock.Now);
        clock.Add(5);
        live.Check();
        Assert.True(live.PingDue());
        clock.Add(9);
        Assert.Null(live.Dead());
        clock.Add(1);
        Assert.Equal("no answer within 10 s after a wake or a network change", live.Dead());

        // answered in time: the check is over, the connection stays
        var answered = new BrokerLiveness(TimeSpan.FromSeconds(60), () => clock.Now);
        answered.Check();
        clock.Add(2);
        answered.Heard();
        clock.Add(30);
        Assert.Null(answered.Dead());
        // a second check while one runs doesn't move its deadline
        answered.Check();
        clock.Add(6);
        answered.Check();
        clock.Add(4);
        Assert.NotNull(answered.Dead());
    }

    [Fact]
    public async Task A_broker_that_stops_answering_is_given_up_and_the_reader_ends()
    {
        await using var pair = await SocketPair.OpenAsync();
        // a short interval, so the test is quick; a grace of 2 s, so a busy test machine that delays a pong doesn't end it early
        var live = new BrokerLiveness(TimeSpan.FromMilliseconds(150), grace: TimeSpan.FromSeconds(2));
        var reader = pair.ReadAsync(live);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var keepAlive = live.KeepAliveAsync(pair.Client, ct => pair.Client.SendAsync("ping"u8.ToArray(), WebSocketMessageType.Text, true, ct), stop.Token);

        // answered: alive for several limits
        Assert.True(SpinWait.SpinUntil(() => pair.Pongs >= 6, TimeSpan.FromSeconds(10)), $"only {pair.Pongs} pongs");
        Assert.False(keepAlive.IsCompleted);

        // the connection stays open but nothing comes back (a sleeping PC, a forgotten connection)
        pair.Freeze();
        var why = await keepAlive.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("nothing from the broker for", why);
        Assert.Equal(WebSocketState.Aborted, pair.Client.State);
        // the reader, waiting on the socket, ends with it – the link then connects again
        await Assert.ThrowsAnyAsync<Exception>(() => reader.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_ping_that_fails_or_hangs_gives_the_connection_up()
    {
        await using (var pair = await SocketPair.OpenAsync())
        {
            var live = new BrokerLiveness(TimeSpan.FromMilliseconds(100));
            var why = await live.KeepAliveAsync(pair.Client, _ => throw new WebSocketException("the network went away"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("the keep-alive couldn't be sent: the network went away", why);
            Assert.Equal(WebSocketState.Aborted, pair.Client.State);
        }
        await using (var pair = await SocketPair.OpenAsync())
        {
            // a send stuck on a dead connection (or behind the send lock): given up after the check window
            var live = new BrokerLiveness(TimeSpan.FromMilliseconds(100), checkWindow: TimeSpan.FromMilliseconds(300));
            var why = await live.KeepAliveAsync(pair.Client, _ => Task.Delay(Timeout.Infinite), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.StartsWith("the keep-alive couldn't be sent", why);
            Assert.Equal(WebSocketState.Aborted, pair.Client.State);
        }
    }

    [Fact]
    public async Task Stopping_the_keep_alive_leaves_the_connection_alone()
    {
        await using var pair = await SocketPair.OpenAsync();
        var live = new BrokerLiveness(TimeSpan.FromMilliseconds(100));
        using var stop = new CancellationTokenSource();
        var keepAlive = live.KeepAliveAsync(pair.Client, ct => pair.Client.SendAsync("ping"u8.ToArray(), WebSocketMessageType.Text, true, ct), stop.Token);
        stop.Cancel();
        Assert.Null(await keepAlive.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(WebSocketState.Open, pair.Client.State);
    }

    /// <summary>
    /// Two WebSockets over loopback TCP: the client (as a link's), and a server that answers "ping" with "pong" as the
    /// broker's edge does – until <see cref="Freeze"/>, after which it reads and answers nothing, the TCP connection open.
    /// </summary>
    private sealed class SocketPair : IAsyncDisposable
    {
        private readonly TcpClient _clientTcp, _serverTcp;
        private readonly CancellationTokenSource _stop = new();
        private volatile bool _frozen;
        private int _pongs;
        public WebSocket Client { get; }
        private WebSocket Server { get; }
        public int Pongs => Volatile.Read(ref _pongs);

        private SocketPair(TcpClient client, TcpClient server)
        {
            _clientTcp = client;
            _serverTcp = server;
            Client = WebSocket.CreateFromStream(client.GetStream(), isServer: false, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
            Server = WebSocket.CreateFromStream(server.GetStream(), isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
            _ = Task.Run(AnswerAsync);
        }

        public static async Task<SocketPair> OpenAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var client = new TcpClient();
                var accept = listener.AcceptTcpClientAsync();
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                return new SocketPair(client, await accept);
            }
            finally { listener.Stop(); }
        }

        public void Freeze() => _frozen = true;

        private async Task AnswerAsync()
        {
            var buffer = new byte[64];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var r = await Server.ReceiveAsync(buffer, _stop.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    if (_frozen) continue;
                    await Server.SendAsync("pong"u8.ToArray(), WebSocketMessageType.Text, true, _stop.Token);
                }
            }
            catch { /* closed */ }
        }

        /// <summary>The link's reader: every frame tells the liveness the connection carries.</summary>
        public async Task ReadAsync(BrokerLiveness live)
        {
            var buffer = new byte[64];
            while (true)
            {
                var r = await Client.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) return;
                live.Heard();
                Interlocked.Increment(ref _pongs);
            }
        }

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            Client.Dispose();
            Server.Dispose();
            _clientTcp.Dispose();
            _serverTcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
