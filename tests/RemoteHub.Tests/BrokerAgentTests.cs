extern alias agent;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using A = agent::RemoteHub.Agent;
using static RemoteHub.Agent.BrokerProtocol;

namespace RemoteHub.Tests;

/// <summary>
/// The agent's side of the RemoteHub Broker against the real broker (the TypeScript Worker, run locally with
/// <c>wrangler dev</c>): registration with the device secret, the permanent connection, a console's sealed offer and a
/// relayed stream into the agent's own HTTPS endpoint. Skipped unless REMOTEHUB_BROKER_URL and REMOTEHUB_BROKER_TOKEN
/// point at a broker (the private broker repository isn't on the CI runner).
/// </summary>
[Collection("Broker")]
public class BrokerAgentTests
{
    [SkippableFact]
    public async Task An_agent_registers_itself_and_a_sealed_offer_reaches_its_HTTPS_endpoint_through_the_relay()
    {
        var broker = TestBroker.FromEnvironment();
        Skip.If(broker is null, "No local RemoteHub Broker (REMOTEHUB_BROKER_URL, REMOTEHUB_BROKER_TOKEN).");
        var org = await broker.CreateOrgAsync();

        await using var agentHost = await TestBrokerAgent.StartAsync(broker, org);
        await broker.WaitForOnlineAsync(org, agentHost.DeviceId);

        await using var console = await TestConsole.JoinAsync(broker, org);
        var accepted = await console.ConnectAsync(agentHost.DeviceId);
        Assert.Equal("accepted", accepted["t"]?.GetValue<string>());

        await using var relay = await TestRelay.OpenAsync(broker, org, accepted);
        await using var stream = await relay.OpenStreamAsync(1);
        // TLS to the agent's pinned certificate inside the relayed stream, then an ordinary HTTP request.
        await using var tls = new SslStream(stream, false, (_, cert, _, _) => cert is not null && A.AgentProtocol.Fingerprint(cert.GetRawCertData()) == agentHost.Fingerprint);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "agent" });
        await tls.WriteAsync("GET /v1/health HTTP/1.1\r\nHost: agent\r\nConnection: close\r\n\r\n"u8.ToArray());
        var response = await new StreamReader(tls, Encoding.UTF8).ReadToEndAsync();
        Assert.StartsWith("HTTP/1.1 200", response);
        Assert.Contains(agentHost.DeviceId, response);
    }

    [SkippableFact]
    public async Task An_offer_the_broker_made_up_is_refused_by_the_agent()
    {
        var broker = TestBroker.FromEnvironment();
        Skip.If(broker is null, "No local RemoteHub Broker (REMOTEHUB_BROKER_URL, REMOTEHUB_BROKER_TOKEN).");
        var org = await broker.CreateOrgAsync();
        await using var agentHost = await TestBrokerAgent.StartAsync(broker, org);
        await broker.WaitForOnlineAsync(org, agentHost.DeviceId);
        await using var console = await TestConsole.JoinAsync(broker, org);

        // Sealed with a key that isn't the organisation's record key – what a broker without the device secret could do.
        var refused = await console.ConnectAsync(agentHost.DeviceId, recordKey: RandomNumberGenerator.GetBytes(32));
        Assert.Equal("refused", refused["t"]?.GetValue<string>());
        Assert.Equal("offer", refused["reason"]?.GetValue<string>());
    }
}

/// <summary>The local broker for tests, and its admin API (what <c>rhbroker</c> does).</summary>
internal sealed class TestBroker(Uri url, string token)
{
    public Uri Url => url;
    private readonly HttpClient _http = new() { BaseAddress = url };

    public static TestBroker? FromEnvironment() =>
        Environment.GetEnvironmentVariable("REMOTEHUB_BROKER_URL") is { Length: > 0 } u && Environment.GetEnvironmentVariable("REMOTEHUB_BROKER_TOKEN") is { Length: > 0 } t
            ? new TestBroker(new Uri(u), t) : null;

    public sealed record Org(string Id, byte[] DeviceSecret, string DeviceSecretId, byte[] ConsoleSecret, string ConsoleSecretId);

    public async Task<Org> CreateOrgAsync()
    {
        var id = "o_" + B32(RandomNumberGenerator.GetBytes(10));
        var org = new Org(id, RandomNumberGenerator.GetBytes(32), "s_" + B32(RandomNumberGenerator.GetBytes(5)), RandomNumberGenerator.GetBytes(32), "s_" + B32(RandomNumberGenerator.GetBytes(5)));
        var body = new JsonObject
        {
            ["id"] = id,
            ["shortName"] = "t" + id[2..12],
            ["displayName"] = "Test " + id,
            ["secrets"] = new JsonArray(
                new JsonObject { ["id"] = org.DeviceSecretId, ["kind"] = "device", ["key"] = B64u(DeriveKey(org.DeviceSecret, id, KeyLabel.Enroll)) },
                new JsonObject { ["id"] = org.ConsoleSecretId, ["kind"] = "console", ["key"] = B64u(DeriveKey(org.ConsoleSecret, id, KeyLabel.Join)) }),
        };
        var r = await Admin(HttpMethod.Post, "orgs", body);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return org;
    }

    public async Task WaitForOnlineAsync(Org org, string deviceId)
    {
        for (var i = 0; i < 100; i++)
        {
            var r = await Admin(HttpMethod.Get, $"orgs/{org.Id}/devices");
            var list = JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsArray();
            if (list.Any(d => d?["id"]?.GetValue<string>() == deviceId && d["online"]?.GetValue<bool>() == true)) return;
            await Task.Delay(100);
        }
        Assert.Fail("The agent didn't come online at the broker.");
    }

    private Task<HttpResponseMessage> Admin(HttpMethod method, string path, JsonObject? body = null)
    {
        var req = new HttpRequestMessage(method, "/v1/admin/" + path);
        req.Headers.Authorization = new("Bearer", token);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }
}

/// <summary>An in-process agent with the peer transport and its broker link, as the service starts them.</summary>
internal sealed class TestBrokerAgent : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _stop = new();
    public string DeviceId { get; }
    public string Fingerprint { get; }

    private TestBrokerAgent(WebApplication app, string deviceId, string fingerprint) => (_app, DeviceId, Fingerprint) = (app, deviceId, fingerprint);

    public static async Task<TestBrokerAgent> StartAsync(TestBroker broker, TestBroker.Org org, Func<A.AgentPolicy>? policy = null)
    {
        var deviceId = NewId();
        using var key = RSA.Create(3072);
        var req = new CertificateRequest($"CN={deviceId}, O=RemoteHub Agent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var made = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddDays(30));
        var cert = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null); // SChannel needs a persisted key
        var config = new A.AgentConfig
        {
            DeviceId = deviceId,
            ListenPort = 0,
            Broker = new A.BrokerSettings
            {
                Url = broker.Url.GetLeftPart(UriPartial.Authority),
                Org = org.Id,
                SecretId = org.DeviceSecretId,
                EnrollKey = B64u(DeriveKey(org.DeviceSecret, org.Id, KeyLabel.Enroll)),
                RecordKey = B64u(DeriveKey(org.DeviceSecret, org.Id, KeyLabel.Record)),
            },
        };
        var peers = new A.PeerTransport();
        var log = new A.AgentLog("", 1);
        var currentPolicy = policy ?? (() => A.AgentPolicy.Defaults(config));
        var app = A.AgentHost.Build(new A.AgentHostOptions(config, cert, new NoSignIn(), new NoShell(), log, IPAddress.Loopback, Policy: currentPolicy) { Peers = peers });
        await app.StartAsync();
        var agent = new TestBrokerAgent(app, deviceId, A.AgentProtocol.Fingerprint(cert.RawData));
        var link = new A.BrokerLink(() => config, cert, currentPolicy, log, peers);
        _ = Task.Run(() => link.RunAsync(agent._stop.Token));
        return agent;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class NoSignIn : A.IShellAuthenticator
    {
        public A.AuthResult Authenticate(string userName, string password) => A.AuthResult.Fail(A.AuthOutcome.BadCredentials, "no sign-in in this test");
    }

    private sealed class NoShell : A.IShellLauncher
    {
        public A.IShellProcess Start(A.ShellIdentity identity) => throw new InvalidOperationException("no shell in this test");
    }
}

/// <summary>A console as far as the broker protocol goes: registration with the console secret, and sealed offers.</summary>
internal sealed class TestConsole : IAsyncDisposable
{
    private readonly ClientWebSocket _ws;
    private readonly TestBroker.Org _org;
    private readonly ECDsa _key;
    private readonly string _id;

    private TestConsole(ClientWebSocket ws, TestBroker.Org org, ECDsa key, string id) => (_ws, _org, _key, _id) = (ws, org, key, id);

    public static async Task<TestConsole> JoinAsync(TestBroker broker, TestBroker.Org org)
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new UriBuilder(broker.Url) { Scheme = broker.Url.Scheme == "http" ? "ws" : "wss", Path = $"/v1/o/{org.Id}/console" }.Uri, CancellationToken.None);
        var challenge = await Json.ReceiveAsync(ws);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var id = NewId();
        var nonce = challenge["nonce"]!.GetValue<string>();
        var host = challenge["host"]!.GetValue<string>();
        await Json.SendAsync(ws, new JsonObject
        {
            ["t"] = "register",
            ["id"] = id,
            ["publicKey"] = B64u(spki),
            ["alg"] = "ES256",
            ["name"] = "Test console",
            ["secretId"] = org.ConsoleSecretId,
            ["join"] = Mac(DeriveKey(org.ConsoleSecret, org.Id, KeyLabel.Join), org.Id, "join", id, KeyHash(spki), nonce),
            ["sig"] = SignEs256(key, ProofText(org.Id, "console", id, nonce, host)),
        });
        Assert.Equal("welcome", (await Json.ReceiveAsync(ws))["t"]?.GetValue<string>());
        return new TestConsole(ws, org, key, id);
    }

    /// <summary>A connect request with an offer sealed by <paramref name="recordKey"/> (default: the organisation's); the answer.</summary>
    public async Task<JsonObject> ConnectAsync(string deviceId, byte[]? recordKey = null)
    {
        var offer = new JsonObject
        {
            ["consoleId"] = _id,
            ["consoleKey"] = KeyHash(_key.ExportSubjectPublicKeyInfo()),
            ["deviceId"] = deviceId,
            ["expires"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120,
            ["kind"] = "device",
            ["nonce"] = B64u(RandomNumberGenerator.GetBytes(16)),
        };
        var seal = Seal(recordKey ?? DeriveKey(_org.DeviceSecret, _org.Id, KeyLabel.Record), _org.Id, "offer", offer);
        await Json.SendAsync(_ws, new JsonObject { ["t"] = "connect", ["ref"] = "c1", ["deviceId"] = deviceId, ["offer"] = offer.DeepClone(), ["offerSeal"] = seal, ["cands"] = new JsonArray() });
        while (true)
        {
            var m = await Json.ReceiveAsync(_ws);
            if (m["ref"]?.GetValue<string>() == "c1") return m;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { /* gone */ }
        _ws.Dispose();
        _key.Dispose();
    }
}

/// <summary>The console's side of a relayed session: the jump host's framing, one stream at a time.</summary>
internal sealed class TestRelay : IAsyncDisposable
{
    private readonly ClientWebSocket _ws;
    private TestRelay(ClientWebSocket ws) => _ws = ws;

    public static async Task<TestRelay> OpenAsync(TestBroker broker, TestBroker.Org org, JsonObject accepted)
    {
        var session = accepted["session"]!.GetValue<string>();
        var key = FromB64u(accepted["key"]!.GetValue<string>())!;
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "RemoteHub-Relay " + Mac(key, org.Id, "relay", session, "console"));
        var path = accepted["relay"]!.GetValue<string>() + "&side=console";
        await ws.ConnectAsync(new Uri(new UriBuilder(broker.Url) { Scheme = broker.Url.Scheme == "http" ? "ws" : "wss" }.Uri, path), CancellationToken.None);
        return new TestRelay(ws);
    }

    public async Task<Stream> OpenStreamAsync(uint id)
    {
        await _ws.SendAsync(Frame(1, id, []), WebSocketMessageType.Binary, true, CancellationToken.None); // Open, no payload
        var opened = await ReceiveFrameAsync();
        Assert.Equal(2, opened.Type); // Opened
        return new RelayStream(this, id);
    }

    internal static byte[] Frame(byte type, uint id, ReadOnlySpan<byte> payload)
    {
        var f = new byte[5 + payload.Length];
        f[0] = type;
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(1), id);
        payload.CopyTo(f.AsSpan(5));
        return f;
    }

    internal async Task<(byte Type, byte[] Payload)> ReceiveFrameAsync()
    {
        var buffer = new byte[70_000];
        var length = 0;
        ValueWebSocketReceiveResult r;
        do
        {
            r = await _ws.ReceiveAsync(buffer.AsMemory(length), CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) return (0, []);
            length += r.Count;
        } while (!r.EndOfMessage);
        return (buffer[0], buffer[5..length]);
    }

    internal Task SendAsync(byte[] frame) => _ws.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { /* gone */ }
        _ws.Dispose();
    }

    private sealed class RelayStream(TestRelay relay, uint id) : Stream
    {
        private byte[] _pending = [];
        private int _offset;
        private bool _closed;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_offset >= _pending.Length)
            {
                if (_closed) return 0;
                var (type, payload) = await relay.ReceiveFrameAsync();
                switch (type)
                {
                    case 4: // Data: take it, and give the agent its credit back
                        _pending = payload;
                        _offset = 0;
                        var credit = new byte[4];
                        BinaryPrimitives.WriteInt32BigEndian(credit, payload.Length);
                        await relay.SendAsync(Frame(5, id, credit));
                        break;
                    case 6 or 7 or 0: // Close, Reset, the relay itself closed
                        _closed = true;
                        break;
                }
            }
            var n = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < buffer.Length; i += 60_000)
                await relay.SendAsync(Frame(4, id, buffer.Span.Slice(i, Math.Min(60_000, buffer.Length - i))));
        }
    }
}

internal static class Json
{
    public static Task SendAsync(ClientWebSocket ws, JsonObject m) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(m.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    public static async Task<JsonObject> ReceiveAsync(ClientWebSocket ws)
    {
        var buffer = new byte[70_000];
        var length = 0;
        ValueWebSocketReceiveResult r;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        do
        {
            r = await ws.ReceiveAsync(buffer.AsMemory(length), timeout.Token);
            if (r.MessageType == WebSocketMessageType.Close) throw new IOException($"closed: {ws.CloseStatus} {ws.CloseStatusDescription}");
            length += r.Count;
        } while (!r.EndOfMessage);
        return JsonNode.Parse(buffer.AsSpan(0, length))!.AsObject();
    }
}
