extern alias agent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Agent;
using RemoteHub.Remoting;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

// RemoteHub ending a connection on purpose (Disconnect, closing the tab or the app): AgentDesktopSession closes its
// client with CloseQuietly, which must reach the agent as a close – not as the reset of a drop – even while frames flow,
// and must not wait long for an agent that doesn't answer.
public partial class AgentDesktopFlowTests
{
    [Fact]
    public async Task Closing_on_purpose_while_frames_flow_is_a_close_for_the_agent_and_gets_the_end_action()
    {
        A.DesktopEndpoint.AutoSignInAttempts = new A.AutoSignInGate();
        var broker = new FakeAutoSignIn();
        // The picture keeps changing: the agent is sending frames when RemoteHub closes, so unread data is on the way.
        await using var d = await StartDevice(policy: AutoSignInPolicy(), autoSignIn: broker, sessionList: [ConsoleSignIn],
            screens: _ => new Screen(secure: true, changing: int.MaxValue));
        broker.Answer = SignsIn(d.Sessions, SidOf("admin"));
        using var v = await Connect(d);
        v.Start(ConsoleSignIn.Id, DesktopConsent.Ask, autoSignIn: true);
        v.Next(DesktopStatusState.SignInReady);
        v.Client.Send(DesktopWire.SignInRequest("pw"));
        v.Next(DesktopStatusState.SignedIn);
        WaitFor(() => d.Helpers.SessionIds.Contains(3), "a helper in the new session");
        var frames = v.Frames.Count;
        WaitFor(() => v.Frames.Count > frames + 5, "frames flowing from the new session");

        v.Client.CloseQuietly(); // what AgentDesktopSession does on Disconnect or closing the tab
        WaitFor(() => d.Sessions.Disconnects.Contains(3), "the end action (lock)");
        Assert.Empty(d.Sessions.LogOffs); // not taken for a drop (that would cover, then sign out)
        WaitFor(() => d.Audits.Contains(A.AuditEvent.AutoSignInEnded), "audit 1107");
    }

    /// <summary>Forwards TCP to the agent until <see cref="Freeze"/>: then the agent is unreachable but the connection stays open.</summary>
    private sealed class FreezingProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private volatile bool _frozen;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FreezingProxy(int target)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient inbound;
                    try { inbound = await _listener.AcceptTcpClientAsync(_stop.Token); } catch { return; }
                    var outbound = new TcpClient();
                    await outbound.ConnectAsync(IPAddress.Loopback, target);
                    _ = Pump(inbound.GetStream(), outbound.GetStream());
                    _ = Pump(outbound.GetStream(), inbound.GetStream());
                }
            });
        }

        public void Freeze() => _frozen = true;

        private async Task Pump(Stream from, Stream to)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                int n;
                while ((n = await from.ReadAsync(buffer, _stop.Token)) > 0)
                    if (!_frozen) await to.WriteAsync(buffer.AsMemory(0, n), _stop.Token); // frozen: swallowed
            }
            catch { /* stopped */ }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    [Fact]
    public async Task An_agent_that_does_not_answer_the_close_delays_it_no_longer_than_the_limit()
    {
        await using var d = await StartDevice();
        using var proxy = new FreezingProxy(d.Port);
        var client = await AgentDesktopClient.ConnectAsync("127.0.0.1", new AgentEndpoint(proxy.Port, d.Fingerprint), Cred("admin"), CancellationToken.None);
        using var v = new Viewer(client);
        v.Start(ActiveUser.Id, DesktopConsent.None);
        v.Next(DesktopStatusState.Connected);

        proxy.Freeze(); // the agent hangs (or the network does): the close is never answered
        var limit = TimeSpan.FromSeconds(1);
        var watch = Stopwatch.StartNew();
        Assert.False(await client.CloseAsync(limit));
        Assert.True(watch.Elapsed < limit + TimeSpan.FromSeconds(1.5), $"the close took {watch.Elapsed.TotalSeconds:0.0} s");
    }
}
