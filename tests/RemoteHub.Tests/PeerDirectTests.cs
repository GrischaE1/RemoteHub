extern alias agent;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Agent;
using RemoteHub.Services;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// The direct path of RemoteHub Broker sessions: candidates and the addresses nothing is sent to, STUN, hole punching,
/// QUIC on the punched ports with both ends pinned, and the policies that turn it off.
/// </summary>
[Collection("Policies")]
public class PeerDirectTests
{
    private static readonly byte[] Session = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)(i + 100)).ToArray();

    // ---- candidates ------------------------------------------------------------------------------------------------

    [Fact]
    public void Candidates_have_one_wire_form_and_refuse_the_rest()
    {
        Assert.Equal("host:10.1.2.3:51820", PeerCandidate.Parse("host:10.1.2.3:51820")!.ToString());
        Assert.Equal("srflx:[2001:db8::7]:40000", PeerCandidate.Parse("srflx:[2001:db8::7]:40000")!.ToString());
        Assert.Equal(CandidateKind.Srflx, PeerCandidate.Parse("srflx:203.0.113.7:1")!.Kind);
        foreach (var bad in new[]
                 {
                     "relay:203.0.113.7:3478", "prflx:10.1.2.3:5", "host:10.1.2.3", "host:10.1.2.3:0", "host:10.1.2.3:65536", "host:pc-0815:51820",
                     "host:2001:db8::7:40000", "host:[10.1.2.3]:5", "host:[fe80::1%3]:5", "host:10.1.2.3:+5", "host: 10.1.2.3:5", "",
                     "host:[2001:db8:1111:2222:3333:4444:5555:6666]:65535" + new string('0', 30),
                 })
            Assert.Null(PeerCandidate.Parse(bad));
    }

    [Fact]
    public void Special_addresses_are_never_punched_and_at_most_eight_are_taken()
    {
        string[] special =
        [
            "host:0.0.0.0:5", "host:127.0.0.1:5", "host:169.254.1.1:5", "host:224.0.0.251:5353", "host:239.255.255.250:1900", "host:255.255.255.255:5",
            "host:240.0.0.1:5", "host:[::]:5", "host:[::1]:5", "host:[fe80::1]:5", "host:[ff02::1]:5", "host:[::ffff:127.0.0.1]:5", "host:[::7f00:1]:5",
        ];
        Assert.Empty(PeerDirect.Usable(special, 2));
        Assert.All(special, s => Assert.True(PeerDirect.IsSpecial(PeerCandidate.Parse(s)!.EndPoint.Address), s));

        var many = Enumerable.Range(1, 20).Select(i => $"host:10.0.0.{i}:5").Prepend("host:10.0.0.1:5").ToList();
        var usable = PeerDirect.Usable(many, 2);
        Assert.Equal(7, usable.Count); // the first eight, one of them twice
        Assert.Equal("host:10.0.0.7:5", usable[^1].ToString());
        Assert.Empty(PeerDirect.Usable(["garbage", null, "host:10.0.0.1:5"], 0));
    }

    [Fact]
    public void BrokerDirect_1_punches_public_addresses_only()
    {
        var both = new[] { "host:192.168.1.5:5000", "srflx:203.0.113.7:40000" };
        Assert.Equal(["srflx:203.0.113.7:40000"], PeerDirect.Usable(both, 1).Select(c => c.ToString()));
        Assert.Equal(2, PeerDirect.Usable(both, 2).Count);

        var reflexive = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40000);
        Assert.Equal(["srflx:203.0.113.7:40000"], PeerDirect.OwnCandidates(5000, reflexive, 1));
        Assert.Empty(PeerDirect.OwnCandidates(5000, reflexive, 0));
        Assert.Empty(PeerDirect.OwnCandidates(5000, null, 1));
        var own = PeerDirect.OwnCandidates(5000, reflexive, 2);
        Assert.Equal("srflx:203.0.113.7:40000", own[0]);
        Assert.True(own.Count <= BrokerProtocol.MaxCandidates);
        Assert.All(own.Skip(1), c => Assert.True(PeerCandidate.Parse(c) is { Kind: CandidateKind.Host, EndPoint.Port: 5000 } h && !PeerDirect.IsSpecial(h.EndPoint.Address), c));
    }

    // ---- STUN ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_STUN_answer_is_read_from_XOR_MAPPED_ADDRESS_for_both_families()
    {
        var transaction = RandomNumberGenerator.GetBytes(12);
        var request = Stun.BindingRequest(transaction);
        Assert.Equal(20, request.Length);
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(request));
        Assert.Equal(0x2112A442u, BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(4)));

        var v4 = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40000);
        Assert.Equal(v4, Stun.ParseBindingResponse(StunResponder.Answer(transaction, v4, xor: true), transaction));
        var v6 = new IPEndPoint(IPAddress.Parse("2001:db8::7"), 51820);
        Assert.Equal(v6, Stun.ParseBindingResponse(StunResponder.Answer(transaction, v6, xor: true), transaction));
        // An old server's MAPPED-ADDRESS is taken when there is no XOR-MAPPED-ADDRESS.
        Assert.Equal(v4, Stun.ParseBindingResponse(StunResponder.Answer(transaction, v4, xor: false), transaction));
    }

    [Fact]
    public void A_STUN_answer_for_another_request_or_malformed_is_ignored()
    {
        var transaction = RandomNumberGenerator.GetBytes(12);
        var answer = StunResponder.Answer(transaction, new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40000), xor: true);
        Assert.Null(Stun.ParseBindingResponse(answer, RandomNumberGenerator.GetBytes(12)));
        Assert.Null(Stun.ParseBindingResponse(answer.AsSpan(0, answer.Length - 1), transaction)); // shorter than its length says
        var error = (byte[])answer.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(error, 0x0111); // a binding error
        Assert.Null(Stun.ParseBindingResponse(error, transaction));
        var oversized = (byte[])answer.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(oversized.AsSpan(22), 200); // an attribute longer than the message
        Assert.Null(Stun.ParseBindingResponse(oversized, transaction));
        Assert.Null(Stun.ParseBindingResponse(new byte[19], transaction));
    }

    [Fact]
    public void A_STUN_service_is_a_name_and_port()
    {
        Assert.Equal(("stun.cloudflare.com", 3478), Stun.ParseServer("stun.cloudflare.com:3478"));
        Assert.Equal(("stun.contoso.com", 3478), Stun.ParseServer(" stun.contoso.com "));
        Assert.Equal(("10.0.0.1", 53), Stun.ParseServer("10.0.0.1:53"));
        Assert.Equal(("2001:db8::1", 3478), Stun.ParseServer("[2001:db8::1]:3478"));
        foreach (var bad in new[] { "", "host:0", "host:70000", "2001:db8::1", "[2001:db8::1", "a b:3478", "host:34x" })
            Assert.True(Stun.ParseServer(bad) is null, bad);
    }

    [Fact]
    public async Task The_session_socket_learns_its_public_address_from_a_STUN_service()
    {
        await using var responder = StunResponder.Start(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40000));
        using var socket = PeerDirect.NewSocket();
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40000), await Stun.QueryAsync(socket, $"127.0.0.1:{responder.Port}", TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.Equal(1, responder.Requests);

        // Nothing answering (UDP blocked): null within the time, not an error.
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var started = DateTime.UtcNow;
        Assert.Null(await Stun.QueryAsync(socket, $"127.0.0.1:{((IPEndPoint)silent.Client.LocalEndPoint!).Port}", TimeSpan.FromMilliseconds(600), CancellationToken.None));
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    // ---- punching --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_punch_checks_with_its_session_and_key_only()
    {
        var p = PeerDirect.PunchPacket(Session, RandomNumberGenerator.GetBytes(8), Key);
        Assert.Equal(PeerDirect.PunchPacketSize, p.Length);
        Assert.True(PeerDirect.IsPunch(p, Session, Key));
        Assert.False(PeerDirect.IsPunch(p, Session, RandomNumberGenerator.GetBytes(32)));
        Assert.False(PeerDirect.IsPunch(p, RandomNumberGenerator.GetBytes(16), Key));
        var flipped = (byte[])p.Clone();
        flipped[22] ^= 1; // the nonce
        Assert.False(PeerDirect.IsPunch(flipped, Session, Key));
        Assert.False(PeerDirect.IsPunch(p.AsSpan(0, 43), Session, Key));
    }

    [Fact]
    public async Task Both_sides_punch_through_and_the_console_takes_the_address_that_answered()
    {
        using var console = PeerDirect.NewSocket();
        using var agent = PeerDirect.NewSocket();
        var consoleCand = Candidate(console, CandidateKind.Host);
        var agentCand = Candidate(agent, CandidateKind.Host);
        var a = PeerDirect.PunchAsync(agent, [consoleCand], Session, Key, agent: true, CancellationToken.None);
        var c = PeerDirect.PunchAsync(console, [agentCand], Session, Key, agent: false, CancellationToken.None);
        var (cr, ar) = (await c, await a);
        Assert.NotNull(cr);
        Assert.Equal(agentCand.EndPoint, cr.Remote);
        Assert.True(cr.Local);
        Assert.NotNull(ar); // the console's punch got through to the agent
        Assert.InRange(cr.Packets + ar.Packets, 2, PeerDirect.MaxPackets * 2);
    }

    [Fact]
    public async Task Punching_without_an_answer_ends_after_three_seconds_within_the_packet_limit()
    {
        using var console = PeerDirect.NewSocket();
        using var sink = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); // takes the packets, answers nothing
        var silent = new PeerCandidate(CandidateKind.Host, new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)sink.Client.LocalEndPoint!).Port));
        var many = Enumerable.Range(0, 8).Select(_ => silent).ToList();
        var started = DateTime.UtcNow;
        Assert.Null(await PeerDirect.PunchAsync(console, many, Session, Key, agent: false, CancellationToken.None));
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(5));
        var received = 0;
        while (sink.Available > 0)
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            sink.Receive(ref from);
            received++;
        }
        Assert.InRange(received, 1, PeerDirect.MaxPackets);
    }

    [Fact]
    public async Task A_punch_with_another_key_gets_no_echo()
    {
        using var console = PeerDirect.NewSocket();
        using var agent = PeerDirect.NewSocket();
        var a = PeerDirect.PunchAsync(agent, [Candidate(console, CandidateKind.Host)], Session, RandomNumberGenerator.GetBytes(32), agent: true, CancellationToken.None);
        var c = PeerDirect.PunchAsync(console, [Candidate(agent, CandidateKind.Host)], Session, Key, agent: false, CancellationToken.None);
        Assert.Null(await c);
        Assert.Null(await a);
    }

    // ---- QUIC on the punched ports ---------------------------------------------------------------------------------

    [SkippableFact]
    public async Task QUIC_on_the_punched_ports_pins_the_device_and_takes_only_the_announced_console_key()
    {
        Skip.IfNot(PeerDirect.Supported, "msquic needs Windows 11 or Server 2022 or later.");
        using var device = DeviceCertificate();
        using var consoleKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var consoleCert = ConsoleCertificate(consoleKey);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherCert = ConsoleCertificate(otherKey);
        var consoleHash = BrokerProtocol.KeyHash(consoleKey.ExportSubjectPublicKeyInfo());
        var fingerprint = AgentProtocol.Fingerprint(device.RawData);

        // The announced key and the pinned device: up, and a stream carries bytes both ways.
        await using (var pair = await Pair(device, consoleHash, consoleCert, fingerprint))
        {
            Assert.NotNull(pair.Client);
            await using var stream = await pair.Client!.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
            await stream.WriteAsync("ping"u8.ToArray());
            await using var inbound = await pair.Server!.AcceptInboundStreamAsync();
            var buffer = new byte[4];
            await inbound.ReadExactlyAsync(buffer);
            Assert.Equal("ping"u8.ToArray(), buffer);
        }
        // Another console's key: no connection at the agent.
        await using (var other = await Pair(device, consoleHash, otherCert, fingerprint)) Assert.Null(other.Server);
        // A device that isn't the pinned one: the console gives up in the handshake.
        await using (var wrong = await Pair(device, consoleHash, consoleCert, new string('A', 64))) Assert.Null(wrong.Client);
    }

    [SkippableFact]
    public async Task The_console_tries_again_from_its_port_until_a_late_listener_is_up()
    {
        Skip.IfNot(PeerDirect.Supported, "msquic needs Windows 11 or Server 2022 or later.");
        using var device = DeviceCertificate();
        using var consoleKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var consoleCert = ConsoleCertificate(consoleKey);
        await using var pair = await Pair(device, BrokerProtocol.KeyHash(consoleKey.ExportSubjectPublicKeyInfo()), consoleCert, AgentProtocol.Fingerprint(device.RawData),
            listenerDelay: TimeSpan.FromMilliseconds(700));
        Assert.NotNull(pair.Client);
        Assert.NotNull(pair.Server);
    }

    // ---- policy ----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_device_policy_BrokerDirect_and_BrokerStun_are_read_and_a_bad_value_restricts()
    {
        var config = new A.AgentConfig();
        A.AgentPolicy Apply(Dictionary<string, object> v) => A.AgentPolicy.From(config, new PolicyTemplateTests.Values(v));
        Assert.Equal(2, Apply([]).BrokerDirect);
        Assert.Null(Apply([]).BrokerStun);
        Assert.Equal(0, Apply(new() { ["BrokerDirect"] = 0 }).BrokerDirect);
        Assert.Equal(1, Apply(new() { ["BrokerDirect"] = 1 }).BrokerDirect);
        Assert.Equal(0, Apply(new() { ["BrokerDirect"] = 7 }).BrokerDirect); // out of range: relay only, not the default
        Assert.Equal("stun.contoso.com:3478", Apply(new() { ["BrokerStun"] = "stun.contoso.com:3478" }).BrokerStun);
        Assert.Null(Apply(new() { ["BrokerStun"] = "not a server:x" }).BrokerStun);
    }

    [Fact]
    public void This_PCs_policy_BrokerDirect_0_keeps_every_session_relayed()
    {
        try
        {
            Assert.True(AppPolicy.BrokerDirect);
            AppPolicy.Source = new OneValue("BrokerDirect", 0);
            Assert.False(AppPolicy.BrokerDirect);
            AppPolicy.Source = new OneValue("BrokerDirect", 1);
            Assert.True(AppPolicy.BrokerDirect);
        }
        finally { AppPolicy.Source = RegistryPolicySource.Instance; }
    }

    [Fact]
    public async Task Test_in_settings_says_direct_connections_are_possible_only_when_STUN_answers()
    {
        await using var responder = StunResponder.Start(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40000));
        if (PeerDirect.Supported)
        {
            var open = await RemoteHub.Broker.BrokerTest.RunAsync(true, "Connected", $"127.0.0.1:{responder.Port}", CancellationToken.None);
            Assert.True(open.Direct);
            Assert.Equal("Direct connections possible.", open.Verdict);
            Assert.Equal("Control channel: connected.", open.Control);
            Assert.Contains("203.0.113.7", open.Stun);

            using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var blocked = await RemoteHub.Broker.BrokerTest.RunAsync(false, "The connection to the RemoteHub Broker rh.contoso.com was lost.",
                $"127.0.0.1:{((IPEndPoint)silent.Client.LocalEndPoint!).Port}", CancellationToken.None);
            Assert.False(blocked.Direct);
            Assert.Equal("Only relayed connections: UDP is blocked from this network.", blocked.Verdict);
            Assert.StartsWith("Control channel: not connected – The connection", blocked.Control);
        }
        try
        {
            AppPolicy.Source = new OneValue("BrokerDirect", 0);
            var off = await RemoteHub.Broker.BrokerTest.RunAsync(true, "Connected", $"127.0.0.1:{responder.Port}", CancellationToken.None);
            Assert.False(off.Direct);
            Assert.Contains("this PC's policy", off.Verdict);
        }
        finally { AppPolicy.Source = RegistryPolicySource.Instance; }
    }

    [Fact]
    public void A_tab_through_the_broker_names_its_path_beside_the_transport()
    {
        Assert.Equal("RemoteHub Agent · through RemoteHub Broker (direct)", RemoteHub.Rdp.SessionBase.TransportWithPath("RemoteHub Agent", RemoteHub.Broker.PeerPath.DirectLocal));
        Assert.Equal("RemoteHub Agent · through RemoteHub Broker (direct)", RemoteHub.Rdp.SessionBase.TransportWithPath("RemoteHub Agent", RemoteHub.Broker.PeerPath.DirectInternet));
        Assert.Equal("RemoteHub Agent · through RemoteHub Broker (relayed)", RemoteHub.Rdp.SessionBase.TransportWithPath("RemoteHub Agent", RemoteHub.Broker.PeerPath.Relayed));
        Assert.Equal("RemoteHub Agent · through RemoteHub Broker", RemoteHub.Rdp.SessionBase.TransportWithPath("RemoteHub Agent", null)); // before the session is open
    }

    private sealed class OneValue(string name, object value) : IPolicySource
    {
        public object? Read(PolicyScope scope, string key, string n) =>
            scope == PolicyScope.Computer && key.Equals(PolicyCatalog.Root, StringComparison.OrdinalIgnoreCase) && n == name ? value : null;
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static PeerCandidate Candidate(Socket s, CandidateKind kind) =>
        new(kind, new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)s.LocalEndPoint!).Port));

    private sealed class QuicPair : IAsyncDisposable
    {
        public QuicConnection? Client, Server;

        public async ValueTask DisposeAsync()
        {
            if (Client is not null) await Client.DisposeAsync();
            if (Server is not null) await Server.DisposeAsync();
        }
    }

    /// <summary>Punch on loopback, hand both ports to msquic as the agent and the console do, and connect.</summary>
    private static async Task<QuicPair> Pair(X509Certificate2 device, string consoleHash, X509Certificate2 consoleCert, string fingerprint, TimeSpan listenerDelay = default)
    {
        var console = PeerDirect.NewSocket();
        var agent = PeerDirect.NewSocket();
        var consolePort = ((IPEndPoint)console.LocalEndPoint!).Port;
        var agentPort = ((IPEndPoint)agent.LocalEndPoint!).Port;
        var a = PeerDirect.PunchAsync(agent, [Candidate(console, CandidateKind.Host)], Session, Key, agent: true, CancellationToken.None);
        var won = await PeerDirect.PunchAsync(console, [Candidate(agent, CandidateKind.Host)], Session, Key, agent: false, CancellationToken.None);
        Assert.NotNull(won);
        console.Dispose();
        Assert.NotNull(await a);
        agent.Dispose();

        var pair = new QuicPair();
        var options = PeerDirect.ServerOptions(device, consoleHash, 10);
        var listening = Task.Run(async () =>
        {
            await Task.Delay(listenerDelay);
            return await QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.IPv6Any, agentPort),
                ApplicationProtocols = [new System.Net.Security.SslApplicationProtocol(PeerDirect.Alpn)],
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(options),
            });
        });
        var connecting = PeerDirect.ConnectAsync(PeerDirect.ClientOptions(won.Remote, consolePort, consoleCert, fingerprint), new CancellationTokenSource(10_000).Token);
        await using var listener = await listening;
        var accepting = Task.Run(async () =>
        {
            using var wait = new CancellationTokenSource(PeerDirect.ConnectTime + TimeSpan.FromSeconds(2));
            while (true)
            {
                try { return await listener.AcceptConnectionAsync(wait.Token); }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex) when (ex is AuthenticationException or QuicException) { }
            }
        });
        try { pair.Client = await connecting; }
        catch (Exception ex) when (ex is AuthenticationException or QuicException) { }
        pair.Server = await accepting;
        return pair;
    }

    private static X509Certificate2 DeviceCertificate()
    {
        using var key = RSA.Create(3072);
        var req = new CertificateRequest("CN=device, O=RemoteHub Agent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var made = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null); // Schannel needs a persisted key
    }

    private static X509Certificate2 ConsoleCertificate(ECDsa key)
    {
        var req = new CertificateRequest("CN=console, O=RemoteHub", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        using var made = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
    }
}

/// <summary>A STUN service for tests and the lab: answers every binding request with a fixed address.</summary>
internal sealed class StunResponder : IAsyncDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _requests;

    private StunResponder(IPEndPoint mapped) => _loop = Task.Run(() => Loop(mapped));

    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public int Requests => _requests;

    public static StunResponder Start(IPEndPoint mapped) => new(mapped);

    private async Task Loop(IPEndPoint mapped)
    {
        try
        {
            while (true)
            {
                var r = await _udp.ReceiveAsync(_stop.Token);
                if (r.Buffer.Length < 20 || BinaryPrimitives.ReadUInt16BigEndian(r.Buffer) != 0x0001) continue;
                Interlocked.Increment(ref _requests);
                await _udp.SendAsync(Answer(r.Buffer.AsSpan(8, 12), mapped, xor: true), r.RemoteEndPoint, _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>A binding success with (XOR-)MAPPED-ADDRESS for <paramref name="mapped"/>.</summary>
    public static byte[] Answer(ReadOnlySpan<byte> transaction, IPEndPoint mapped, bool xor)
    {
        var address = mapped.Address.GetAddressBytes();
        var value = new byte[4 + address.Length];
        value[1] = (byte)(address.Length == 4 ? 1 : 2);
        var port = (ushort)mapped.Port;
        Span<byte> cookie = [0x21, 0x12, 0xA4, 0x42, .. transaction];
        if (xor)
        {
            port ^= 0x2112;
            for (var i = 0; i < address.Length; i++) address[i] ^= cookie[i];
        }
        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(2), port);
        address.CopyTo(value, 4);
        var p = new byte[20 + 4 + value.Length];
        BinaryPrimitives.WriteUInt16BigEndian(p, 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)(4 + value.Length));
        cookie.CopyTo(p.AsSpan(4));
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(20), (ushort)(xor ? 0x0020 : 0x0001));
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(22), (ushort)value.Length);
        value.CopyTo(p, 24);
        return p;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _udp.Dispose();
        await _loop;
    }
}
