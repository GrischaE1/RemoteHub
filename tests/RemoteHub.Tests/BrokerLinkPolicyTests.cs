extern alias agent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// The device policy BrokerEnabled and <c>broker leave</c> against a connection that is already open (lab cases B-BRK-03
/// and B-BRK-04): neither restarts the agent, so the link reads the policy and agent.json again while connected and
/// closes the connection when the broker is turned off or gone. A minimal local broker answers the challenge and
/// welcomes the device.
/// </summary>
[Collection("Broker")]
public class BrokerLinkPolicyTests
{
    private volatile bool _brokerEnabled = true;
    private volatile string? _closedBy;

    [Theory]
    [InlineData("policy", "turned off by policy")]
    [InlineData("leave", "left the broker")]
    public async Task Turning_the_broker_off_or_leaving_it_closes_a_connection_that_is_already_open(string how, string reason)
    {
        var connected = new SemaphoreSlim(0);
        var ended = new SemaphoreSlim(0);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var broker = builder.Build();
        broker.UseWebSockets();
        broker.Map("/v1/o/{org}/agent/{id}", async (HttpContext ctx) =>
        {
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await SendAsync(ws, new JsonObject { ["t"] = "challenge", ["nonce"] = "n0nce", ["host"] = ctx.Request.Host.Value });
            await ReceiveAsync(ws); // the proof: not checked here
            await SendAsync(ws, new JsonObject { ["t"] = "welcome", ["orgName"] = "Test" });
            connected.Release();
            try
            {
                while (await ReceiveAsync(ws)) { }
                _closedBy = ws.CloseStatusDescription;
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch (WebSocketException) { /* the agent dropped the connection */ }
            ended.Release();
        });
        await broker.StartAsync();

        var config = new A.AgentConfig
        {
            DeviceId = Guid.NewGuid().ToString(),
            Broker = new A.BrokerSettings
            {
                Url = broker.Urls.First(),
                Org = "o_abcdefghijklmnop",
                SecretId = "s_abcdefgh",
                EnrollKey = A.BrokerProtocol.B64u(RandomNumberGenerator.GetBytes(32)),
                RecordKey = A.BrokerProtocol.B64u(RandomNumberGenerator.GetBytes(32)),
            },
        };
        using var key = RSA.Create(2048);
        using var cert = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var link = new A.BrokerLink(() => config, cert, () => A.AgentPolicy.Defaults(config) with { BrokerEnabled = _brokerEnabled },
            new A.AgentLog("", 1), new A.PeerTransport());

        var check = A.BrokerLink.SettingsCheck;
        A.BrokerLink.SettingsCheck = TimeSpan.FromMilliseconds(100);
        using var stop = new CancellationTokenSource();
        var run = Task.Run(() => link.RunAsync(stop.Token));
        try
        {
            Assert.True(await connected.WaitAsync(TimeSpan.FromSeconds(20)), "The link didn't connect to the local broker.");
            Assert.True(SpinWait.SpinUntil(() => link.State == "connected", TimeSpan.FromSeconds(10)), $"The link is {link.State}, not connected.");

            if (how == "policy") _brokerEnabled = false;
            else config = new A.AgentConfig { DeviceId = config.DeviceId }; // what 'broker leave' leaves in agent.json
            Assert.True(await ended.WaitAsync(TimeSpan.FromSeconds(10)), $"The open connection wasn't closed ({how}).");
            // a close the broker sees at once, not a dropped socket it notices only when its keep-alive gives up
            Assert.Equal(reason, _closedBy);
            Assert.True(SpinWait.SpinUntil(() => link.State == "idle", TimeSpan.FromSeconds(10)), $"The link is {link.State}, not idle.");
            // nothing may connect again while the broker is off or gone
            Assert.False(await connected.WaitAsync(TimeSpan.FromSeconds(1)), $"The link connected again ({how}).");
        }
        finally
        {
            A.BrokerLink.SettingsCheck = check;
            stop.Cancel();
            try { await run; } catch (OperationCanceledException) { }
            await broker.StopAsync();
        }
    }

    private static Task SendAsync(WebSocket ws, JsonObject m) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(m.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Reads one message; false when the other end closed.</summary>
    private static async Task<bool> ReceiveAsync(WebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) return false;
        } while (!r.EndOfMessage);
        return true;
    }
}
