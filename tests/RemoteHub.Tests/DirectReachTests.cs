extern alias agent;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Agent;
using RemoteHub.Broker;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// A broker device reached without the broker (DirectReach, Reach Automatic): its name, then every address its agent
/// reported, before the broker path – what the agent reports, how the answer is read, the order of the tries, what is
/// remembered, and a device that moved.
/// </summary>
[Collection("DirectReach")]
public sealed class DirectReachTests : IDisposable
{
    public DirectReachTests()
    {
        DirectReach.Reset();
        DirectReach.NameWait = TimeSpan.FromMilliseconds(300);
        DirectReach.AddressWait = TimeSpan.FromMilliseconds(500);
        DirectReach.MoveWait = TimeSpan.FromSeconds(3);
        DirectReach.MovePoll = TimeSpan.FromMilliseconds(50);
    }

    public void Dispose()
    {
        DirectReach.Reset();
        DirectReach.NameWait = TimeSpan.FromMilliseconds(1500);
        DirectReach.AddressWait = TimeSpan.FromSeconds(2);
        DirectReach.MoveWait = TimeSpan.FromSeconds(15);
        DirectReach.MovePoll = TimeSpan.FromMilliseconds(2500);
    }

    private static string NewPin() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static DeviceAddresses Reported(DateTimeOffset at, bool? inbound = true, int? port = 58732, params string[] addresses) =>
        new([.. addresses.Select(IPAddress.Parse)], port, inbound, at);

    /// <summary>A probe that answers for <paramref name="answering"/> only and records what it was asked, with when.</summary>
    private sealed class Probes(params string[] answering)
    {
        public readonly ConcurrentQueue<(string Host, int Port, DateTime At)> Asked = new();
        public HashSet<string> Answering { get; set; } = [.. answering];
        public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(50);

        public async Task<bool> Probe(string host, int port, string pin, TimeSpan within, CancellationToken ct)
        {
            Asked.Enqueue((host, port, DateTime.UtcNow));
            if (!Answering.Contains(host))
            {
                try { await Task.Delay(within, ct); } catch (OperationCanceledException) { }
                return false;
            }
            await Task.Delay(Delay, ct);
            return true;
        }

        public string[] Hosts => [.. Asked.Select(a => a.Host)];
    }

    [Fact]
    public async Task The_name_comes_first_and_the_broker_isnt_asked_when_it_answers()
    {
        var probes = new Probes("pc-0815.contoso.local");
        var asked = 0;
        var found = await DirectReach.FindAsync(NewPin(), "pc-0815.contoso.local", 58732, _ => { asked++; return Task.FromResult<DeviceAddresses?>(null); }, probes.Probe, CancellationToken.None);
        Assert.Equal(new DirectHost("pc-0815.contoso.local", 58732), found);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task Without_the_name_every_reported_address_is_tried_at_once_and_the_first_to_answer_wins()
    {
        var probes = new Probes("192.168.250.25") { Delay = TimeSpan.FromMilliseconds(150) };
        var now = DateTimeOffset.UtcNow;
        var found = await DirectReach.FindAsync(NewPin(), "hv01.contoso.local", 58732,
            _ => Task.FromResult<DeviceAddresses?>(Reported(now, true, 58740, "172.24.0.1", "192.168.250.25", "fd00::25")), probes.Probe, CancellationToken.None);
        Assert.Equal(new DirectHost("192.168.250.25", 58740), found); // the port the agent reported
        Assert.Equal(["hv01.contoso.local", "172.24.0.1", "192.168.250.25", "fd00::25"], probes.Hosts);
        // at once: every address was asked before the one that answered had answered
        var addressTries = probes.Asked.Skip(1).Select(a => a.At).ToList();
        Assert.True(addressTries.Max() - addressTries.Min() < TimeSpan.FromMilliseconds(100), "the addresses were tried one after the other");
    }

    [Fact]
    public async Task A_broker_only_device_an_older_broker_or_no_answer_goes_to_the_broker_path_without_address_tries()
    {
        foreach (var reported in new DeviceAddresses?[] { Reported(DateTimeOffset.UtcNow, false, 58732, "10.0.0.5"), null })
        {
            var probes = new Probes("10.0.0.5");
            Assert.Null(await DirectReach.FindAsync(NewPin(), "pc", 58732, _ => Task.FromResult(reported), probes.Probe, CancellationToken.None));
            Assert.Equal(["pc"], probes.Hosts);
        }
        // the broker can't be asked now (not connected): the broker path as well
        var none = new Probes();
        Assert.Null(await DirectReach.FindAsync(NewPin(), "pc", 58732, _ => throw new BrokerException("not connected"), none.Probe, CancellationToken.None));
    }

    [Fact]
    public async Task What_answered_is_tried_first_next_time_and_what_didnt_isnt_tried_again_until_the_device_reports_new_addresses()
    {
        var pin = NewPin();
        var at = DateTimeOffset.UtcNow;
        var reported = Reported(at, true, 58732, "10.0.0.5");
        var probes = new Probes("10.0.0.5");
        Assert.Equal("10.0.0.5", (await DirectReach.FindAsync(pin, "pc", 58732, _ => Task.FromResult<DeviceAddresses?>(reported), probes.Probe, CancellationToken.None))!.Host);
        Assert.True(DirectReach.HasHost(pin));

        // next time: the remembered host only
        var next = new Probes("10.0.0.5");
        Assert.Equal("10.0.0.5", (await DirectReach.FindAsync(pin, "pc", 58732, _ => throw new InvalidOperationException("not asked"), next.Probe, CancellationToken.None))!.Host);
        Assert.Equal(["10.0.0.5"], next.Hosts);

        // nothing answers any more (this PC left the network): the broker path, and next time at once
        DirectReach.OwnNetworkChanged();
        var gone = new Probes();
        Assert.Null(await DirectReach.FindAsync(pin, "pc", 58732, _ => Task.FromResult<DeviceAddresses?>(reported), gone.Probe, CancellationToken.None));
        Assert.False(DirectReach.HasHost(pin));
        var skipped = new Probes();
        Assert.Null(await DirectReach.FindAsync(pin, "pc", 58732, _ => Task.FromResult<DeviceAddresses?>(reported), skipped.Probe, CancellationToken.None));
        Assert.Empty(skipped.Hosts);

        // the device reported other addresses since: tried again
        var moved = new Probes("10.9.9.9");
        Assert.Equal("10.9.9.9", (await DirectReach.FindAsync(pin, "pc", 58732, _ => Task.FromResult<DeviceAddresses?>(Reported(at.AddSeconds(30), true, 58732, "10.9.9.9")), moved.Probe, CancellationToken.None))!.Host);

        // this PC's network changed: tried again as well
        var pin2 = NewPin();
        Assert.Null(await DirectReach.FindAsync(pin2, "pc", 58732, _ => Task.FromResult<DeviceAddresses?>(reported), new Probes().Probe, CancellationToken.None));
        DirectReach.OwnNetworkChanged();
        var again = new Probes("10.0.0.5");
        Assert.NotNull(await DirectReach.FindAsync(pin2, "pc", 58732, _ => Task.FromResult<DeviceAddresses?>(reported), again.Probe, CancellationToken.None));
    }

    [Fact]
    public async Task A_device_that_moved_is_waited_for_until_it_reports_its_new_network()
    {
        var pin = NewPin();
        var before = DateTimeOffset.UtcNow.AddMinutes(-5);
        var first = new Probes("10.0.0.5");
        Assert.NotNull(await DirectReach.FindAsync(pin, "", 58732, _ => Task.FromResult<DeviceAddresses?>(Reported(before, true, 58732, "10.0.0.5")), first.Probe, CancellationToken.None));

        // 10.0.0.5 stops answering; this PC's network stayed the same. The broker has the old addresses for a while,
        // then the device's hello from its new network.
        var asks = 0;
        var probes = new Probes("10.20.0.7");
        Task<DeviceAddresses?> Broker(CancellationToken _) => Task.FromResult<DeviceAddresses?>(++asks < 4
            ? Reported(before, true, 58732, "10.0.0.5")
            : Reported(DateTimeOffset.UtcNow, true, 58732, "10.20.0.7"));
        var found = await DirectReach.FindAsync(pin, "", 58732, Broker, probes.Probe, CancellationToken.None);
        Assert.Equal("10.20.0.7", found!.Host);
        Assert.True(asks >= 4, $"the broker was asked {asks} times");
        Assert.DoesNotContain("10.0.0.5", probes.Hosts.Skip(1)); // the old address isn't tried twice
    }

    [Fact]
    public async Task When_this_PC_moved_a_lost_host_isnt_waited_for()
    {
        var pin = NewPin();
        var before = DateTimeOffset.UtcNow.AddMinutes(-5);
        Assert.NotNull(await DirectReach.FindAsync(pin, "", 58732, _ => Task.FromResult<DeviceAddresses?>(Reported(before, true, 58732, "10.0.0.5")), new Probes("10.0.0.5").Probe, CancellationToken.None));
        DirectReach.OwnNetworkChanged(); // the laptop left the office
        var asks = 0;
        var started = DateTime.UtcNow;
        Assert.Null(await DirectReach.FindAsync(pin, "", 58732, _ => { asks++; return Task.FromResult<DeviceAddresses?>(Reported(before, true, 58732, "10.0.0.5")); }, new Probes().Probe, CancellationToken.None));
        Assert.Equal(1, asks);
        Assert.True(DateTime.UtcNow - started < DirectReach.MoveWait, "it waited for the device to move");
    }

    [Fact]
    public void The_brokers_answer_keeps_only_addresses_of_another_computer()
    {
        var answer = JsonNode.Parse("""
            {"t":"deviceAddresses","addresses":["192.168.250.25","fd00:1234::25","rh.contoso.com","10.1","127.0.0.1","169.254.1.2","fe80::1%12","224.0.0.1","::1","192.168.250.25"],
             "port":58740,"inbound":true,"reportedAt":1793000000,"online":true}
            """)!.AsObject();
        var r = BrokerClient.ReadDeviceAddresses(answer);
        Assert.Equal(["192.168.250.25", "fd00:1234::25"], r.Addresses.Select(a => a.ToString()));
        Assert.Equal(58740, r.Port);
        Assert.True(r.Inbound);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793000000), r.ReportedAt);

        var many = new JsonObject { ["addresses"] = new JsonArray([.. Enumerable.Range(1, 30).Select(i => JsonValue.Create($"10.0.0.{i}"))]), ["port"] = 70000, ["inbound"] = "yes" };
        var capped = BrokerClient.ReadDeviceAddresses(many);
        Assert.Equal(BrokerClient.MaxDeviceAddresses, capped.Addresses.Count);
        Assert.Null(capped.Port);
        Assert.Null(capped.Inbound);
        Assert.Null(capped.ReportedAt);
    }

    [Fact]
    public void The_agent_tells_the_broker_its_addresses_its_port_and_whether_it_takes_direct_connections()
    {
        var hello = A.BrokerLink.Hello(new A.AgentPolicy(), new A.AgentConfig { ListenPort = 58740 });
        Assert.Equal("hello", hello["t"]!.GetValue<string>());
        Assert.Equal(58740, hello["port"]!.GetValue<int>());
        Assert.NotNull(hello["inbound"]?.GetValue<bool>());
        var addresses = hello["addresses"]!.AsArray().Select(a => IPAddress.Parse(a!.GetValue<string>())).ToList();
        Assert.True(addresses.Count <= 16);
        Assert.All(addresses, a => Assert.False(A.PeerDirect.IsSpecial(a), $"{a} can't be another computer's way to the device"));
        Assert.All(addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6), a => Assert.Equal(0, a.ScopeId)); // no zone: useless on another computer
    }

    [Fact]
    public void An_agent_on_any_free_port_names_no_port_in_its_hello()
    {
        // The broker refuses a hello with port 0 and closes the connection, so the device looked online but wasn't.
        var hello = A.BrokerLink.Hello(new A.AgentPolicy(), new A.AgentConfig { ListenPort = 0 });
        Assert.False(hello.ContainsKey("port"));
        Assert.NotNull(hello["addresses"]);
    }

    [Fact]
    public async Task A_try_answers_only_for_the_pinned_certificate()
    {
        using var cert = SelfSigned();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(stop.Token); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    await using (var tls = new SslStream(c.GetStream()))
                        try { await tls.AuthenticateAsServerAsync(cert); } catch { /* the client refused it */ }
                });
            }
        });
        try
        {
            var pin = AgentProtocol.Fingerprint(cert.RawData);
            Assert.True(await DirectReach.TryTls("127.0.0.1", port, pin, TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.False(await DirectReach.TryTls("127.0.0.1", port, NewPin(), TimeSpan.FromSeconds(5), CancellationToken.None));
            listener.Stop();
            Assert.False(await DirectReach.TryTls("127.0.0.1", port, pin, TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.False(await DirectReach.TryTls("no-such-host.invalid", port, pin, TimeSpan.FromSeconds(5), CancellationToken.None));
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            await server;
        }
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=" + Guid.NewGuid(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var made = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
    }
}
