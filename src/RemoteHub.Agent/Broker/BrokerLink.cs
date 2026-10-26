using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteHub.Agent;

/// <summary>
/// The agent's permanent connection to its RemoteHub Broker (broker-design.md §7.1, broker-protocol.md §5–§6): outbound
/// only, kept open with a keep-alive, proven with the device's own key. What may come down it is the closed list of
/// §7.1a; anything else closes the connection. The only thing it ever leads to is a session the device checked and
/// accepted – a console's offer sealed with the record key – which then runs through <see cref="BrokerRelay"/> into the
/// agent's own HTTPS endpoint, where the pin, the Windows sign-in, rights and policy apply as on a direct connection.
/// </summary>
internal sealed class BrokerLink(
    Func<AgentConfig> currentConfig,
    X509Certificate2 certificate,
    Func<AgentPolicy> policy,
    AgentLog log,
    PeerTransport peers,
    Func<DateTimeOffset>? clock = null)
{
    /// <summary>The message types the broker may send an agent – a closed list (design §7.1a).</summary>
    private static readonly string[] Allowed = ["welcome", "incoming", "wake", "checks", "orgDeleted", "error"];
    private const int MaxMessagesPerSecond = 10;
    private static readonly TimeSpan IdleRetry = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<string, long> _nonces = new();
    private readonly ConcurrentDictionary<string, Task> _sessions = new();
    private bool _register;
    private string? _lastProblem;
    private string? _connectedTo;

    /// <summary>For tests: the state the link is in, and each session's end.</summary>
    internal string State { get; private set; } = "idle";
    internal event Action<string>? SessionEnded;

    public async Task RunAsync(CancellationToken stop)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!stop.IsCancellationRequested)
        {
            AgentConfig config;
            try { config = currentConfig(); }
            catch (Exception ex) { Problem($"agent.json can't be read: {ex.Message}"); await Delay(IdleRetry, stop); continue; }
            var p = policy();
            if (config.Broker is not { } broker || broker.Problem() is not null || !p.BrokerEnabled || p.LoadError is not null)
            {
                State = "idle";
                if (config.Broker?.Problem() is { } bad) Problem("broker: " + bad);
                else if (config.Broker is not null && !p.BrokerEnabled) Problem("The RemoteHub Broker is turned off by policy (BrokerEnabled).");
                await Delay(IdleRetry, stop);
                continue;
            }

            var started = DateTime.UtcNow;
            try
            {
                var outcome = await SessionAsync(config, broker, p, stop);
                if (outcome == Outcome.Register) { backoff = TimeSpan.FromSeconds(1); continue; } // at once, now with the enroll key
                if (outcome == Outcome.Stop) { await Delay(TimeSpan.FromHours(1), stop); continue; }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Problem($"RemoteHub Broker {Host(BrokerUri(broker, p))}: {Describe(ex)}"); }

            State = "waiting";
            // A connection that lasted resets the back-off; jitter, so many agents don't return in the same second.
            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(1)) backoff = TimeSpan.FromSeconds(1);
            await Delay(backoff + TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(0, (int)backoff.TotalMilliseconds + 1)), stop);
            backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
        }
    }

    private enum Outcome { Ended, Register, Stop }

    private static Uri BrokerUri(BrokerSettings broker, AgentPolicy p) => BrokerProtocol.ParseBrokerUrl(p.BrokerUrl ?? broker.Url)!;
    private static string Host(Uri u) => u.Authority;

    private async Task<Outcome> SessionAsync(AgentConfig config, BrokerSettings broker, AgentPolicy p, CancellationToken stop)
    {
        var baseUri = BrokerUri(broker, p);
        using var ws = NewSocket(p);
        var uri = new Uri(WsBase(baseUri), $"/v1/o/{broker.Org}/agent/{config.DeviceId}");
        State = "connecting";
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            timeout.CancelAfter(ConnectTimeout);
            await ws.ConnectAsync(uri, timeout.Token);
        }

        var challenge = await Receive(ws, stop) ?? throw new IOException("the broker closed the connection");
        var nonce = Text(challenge, "nonce", 64);
        var host = Text(challenge, "host", 255);
        if (Text(challenge, "t", 20) != "challenge") throw new InvalidDataException("no challenge");
        // Sign only for the broker this agent connected to, never a name the other end chose.
        if (!string.Equals(host, baseUri.Authority, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"the broker answers as {host}");

        using var key = certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("The device certificate has no RSA key.");
        var proof = BrokerProtocol.SignPs256(key, BrokerProtocol.ProofText(broker.Org, "agent", config.DeviceId, nonce, host));
        await Send(ws, _register ? Registration(config, broker, nonce, proof) : new JsonObject { ["t"] = "proof", ["id"] = config.DeviceId, ["sig"] = proof }, stop);

        var welcome = await Receive(ws, stop);
        if (welcome is null)
        {
            switch ((int?)ws.CloseStatus)
            {
                case BrokerProtocol.Close.Unknown when !_register:
                    _register = true; // not known (yet, or any more after a broker restore): register with the kept enroll key
                    return Outcome.Register;
                case BrokerProtocol.Close.Refused or BrokerProtocol.Close.AuthFailed:
                    log.Audit(AuditEvent.BrokerError, $"The RemoteHub Broker {host} refused this device: {ws.CloseStatusDescription} – it tries again in an hour.");
                    return Outcome.Stop;
                default:
                    throw new IOException($"closed ({(int?)ws.CloseStatus} {ws.CloseStatusDescription})");
            }
        }
        if (Text(welcome, "t", 20) != "welcome") throw new InvalidDataException("no welcome");
        if (_register) log.Audit(AuditEvent.BrokerRegistered, $"Registered with the RemoteHub Broker {host} (organisation {broker.Org}, device {config.DeviceId}, secret {broker.SecretId})");
        _register = false;
        if (_connectedTo != host)
        {
            log.Audit(AuditEvent.BrokerConnected, $"Connected to the RemoteHub Broker {host} (organisation {broker.Org}, {Text(welcome, "orgName", 200, required: false)})");
            _connectedTo = host;
        }
        _lastProblem = null;
        State = "connected";

        await Send(ws, Hello(p), stop);
        using var alive = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var keepAlive = KeepAlive(ws, TimeSpan.FromSeconds(Math.Clamp(p.BrokerKeepAliveSeconds, BrokerProtocol.KeepAliveMin, BrokerProtocol.KeepAliveMax)), alive.Token);
        try { return await ReceiveLoop(ws, config, broker, baseUri, stop); }
        finally
        {
            alive.Cancel();
            try { await keepAlive; } catch { /* ended with the connection */ }
        }
    }

    private async Task<Outcome> ReceiveLoop(ClientWebSocket ws, AgentConfig config, BrokerSettings broker, Uri baseUri, CancellationToken stop)
    {
        var window = new Queue<DateTime>();
        while (true)
        {
            var m = await Receive(ws, stop);
            if (m is null) return Outcome.Ended;
            var now = DateTime.UtcNow;
            window.Enqueue(now);
            while (window.Count > 0 && now - window.Peek() > TimeSpan.FromSeconds(1)) window.Dequeue();
            if (window.Count > MaxMessagesPerSecond) throw new InvalidDataException("too many messages");

            var type = Text(m, "t", 20);
            if (!Allowed.Contains(type)) throw new InvalidDataException($"message type \"{type}\" isn't allowed");
            switch (type)
            {
                case "incoming":
                    await Incoming(ws, m, config, broker, baseUri, stop);
                    break;
                case "orgDeleted":
                    log.Audit(AuditEvent.BrokerError, $"The RemoteHub Broker {baseUri.Authority} says the organisation {broker.Org} was deleted – the device leaves it.");
                    try { BrokerSetup.Leave(log, config.SourcePath ?? ""); } catch (Exception ex) { log.Write("Leaving the broker failed: " + ex.Message); }
                    return Outcome.Ended;
                case "error":
                    log.Write($"RemoteHub Broker: {Text(m, "message", 300, required: false)}");
                    break;
                // wake and checks come with later versions of the broker (design §10.5, §10.7): known, not acted on yet.
            }
        }
    }

    // ---- Offers (design §8.1) -----------------------------------------------------------------------------------

    private async Task Incoming(ClientWebSocket ws, JsonObject m, AgentConfig config, BrokerSettings broker, Uri baseUri, CancellationToken stop)
    {
        var session = Text(m, "session", 22);
        var sessionKey = BrokerProtocol.FromB64u(Text(m, "key", 64)) is { Length: 32 } k ? k : throw new InvalidDataException("session key");
        var offer = m["offer"] as JsonObject ?? throw new InvalidDataException("offer");
        var seal = Text(m, "offerSeal", 64);
        var source = IPAddress.TryParse(Text(m, "source", 45, required: false), out var ip) ? ip : IPAddress.None;
        var consoleName = Text(m, "consoleName", 200, required: false);
        var relay = Text(m, "relay", 200);
        if (m["cands"] is JsonArray { Count: > BrokerProtocol.MaxCandidates }) throw new InvalidDataException("candidates");

        var why = CheckOffer(offer, seal, config, broker);
        var p = policy();
        var consoleId = offer["consoleId"]?.GetValue<string>() ?? "";
        if (why is null && (!p.BrokerEnabled || p.LoadError is not null)) why = ("policy", "The device's policy turns the RemoteHub Broker off (BrokerEnabled).");
        if (why is null && p.BrokerConsoles.Count > 0 && !p.BrokerConsoles.Contains(consoleId))
            why = ("policy", "The device's policy doesn't allow this PC through the RemoteHub Broker (BrokerConsoles).");
        if (why is null && _sessions.Count >= p.MaxSessions) why = ("limit", $"Too many sessions on the device (MaxSessions = {p.MaxSessions}).");
        if (why is { } refused)
        {
            log.Audit(AuditEvent.BrokerSessionRefused, $"Session through the RemoteHub Broker refused: console {consoleId} ({consoleName}) from {source} – {refused.Message}");
            await Send(ws, new JsonObject { ["t"] = "refuse", ["session"] = session, ["reason"] = refused.Reason, ["message"] = refused.Message }, stop);
            return;
        }

        await Send(ws, new JsonObject { ["t"] = "accept", ["session"] = session, ["cands"] = new JsonArray() }, stop);
        var relayUri = new Uri(WsBase(baseUri), relay + "&side=agent");
        var auth = BrokerProtocol.Mac(sessionKey, broker.Org, "relay", session, "agent");
        CryptographicOperations.ZeroMemory(sessionKey);
        var who = $"console {consoleId} ({consoleName}) from {source}";
        _sessions[session] = Task.Run(() => RunRelay(session, relayUri, auth, new IPEndPoint(source, 0), who, p, stop), CancellationToken.None);
    }

    /// <summary>Null when the offer checks out, else why not (reason, message).</summary>
    internal (string Reason, string Message)? CheckOffer(JsonObject offer, string seal, AgentConfig config, BrokerSettings broker)
    {
        try { return CheckOfferFields(offer, seal, config, broker); }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return ("offer", "The offer isn't in its form."); }
    }

    private (string Reason, string Message)? CheckOfferFields(JsonObject offer, string seal, AgentConfig config, BrokerSettings broker)
    {
        var keys = offer.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        if (!keys.SequenceEqual(["consoleId", "consoleKey", "deviceId", "expires", "kind", "nonce"])) return ("offer", "The offer isn't in its form.");
        var recordKey = BrokerProtocol.FromB64u(broker.RecordKey)!;
        // The broker can't make this seal (it has no record key): a broker alone can't start a session (design §7.1a).
        if (!BrokerProtocol.VerifySeal(recordKey, seal, broker.Org, "offer", offer)) return ("offer", "The offer isn't sealed by this organisation.");
        if (offer["deviceId"]?.GetValue<string>() != config.DeviceId) return ("offer", "The offer is for another device.");
        if (offer["kind"]?.GetValue<string>() != "device") return ("unsupported", "Support sessions come in a later version of the agent.");
        var expires = offer["expires"]?.GetValue<long>() ?? 0;
        var now = _now().ToUnixTimeSeconds();
        if (expires < now - BrokerProtocol.ClockSkewSeconds || expires > now + BrokerProtocol.OfferLifetimeSeconds + BrokerProtocol.ClockSkewSeconds)
            return ("expired", "The offer has expired – check the clocks of this device and the console.");
        var nonce = offer["nonce"]?.GetValue<string>() ?? "";
        foreach (var old in _nonces.Where(n => n.Value < now - BrokerProtocol.ClockSkewSeconds).Select(n => n.Key).ToList()) _nonces.TryRemove(old, out _);
        if (_nonces.Count > 10_000 || !_nonces.TryAdd(nonce, expires)) return ("offer", "The offer was used before.");
        return null;
    }

    private async Task RunRelay(string session, Uri relayUri, string auth, IPEndPoint source, string who, AgentPolicy p, CancellationToken stop)
    {
        var started = DateTime.UtcNow;
        var relay = new BrokerRelay(peers, source, p.MaxTunnelStreams);
        string reason;
        log.Audit(AuditEvent.BrokerSessionStarted, $"Session through the RemoteHub Broker started: {who}, relayed");
        try
        {
            using var ws = NewSocket(p);
            ws.Options.SetRequestHeader("Authorization", "RemoteHub-Relay " + auth);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                timeout.CancelAfter(ConnectTimeout);
                await ws.ConnectAsync(relayUri, timeout.Token);
            }
            reason = await relay.RunAsync(ws, stop);
        }
        catch (Exception ex) { reason = "relay failed: " + Describe(ex); }
        finally { _sessions.TryRemove(session, out _); }
        log.Audit(AuditEvent.BrokerSessionEnded, $"Session through the RemoteHub Broker ended: {who} after {DateTime.UtcNow - started:hh\\:mm\\:ss}, " +
                                                  $"{relay.StreamsOpened} stream(s), {relay.BytesIn:N0} bytes in, {relay.BytesOut:N0} bytes out ({reason})");
        SessionEnded?.Invoke(reason);
    }

    // ---- Messages to the broker --------------------------------------------------------------------------------

    /// <summary>Self-registration (design §6.4): the enroll MAC shows the organisation, the proof the device's own key.</summary>
    private JsonObject Registration(AgentConfig config, BrokerSettings broker, string nonce, string proof)
    {
        using var rsa = certificate.GetRSAPublicKey()!;
        var spki = rsa.ExportSubjectPublicKeyInfo();
        var record = Record(config);
        return new JsonObject
        {
            ["t"] = "register",
            ["id"] = config.DeviceId,
            ["publicKey"] = BrokerProtocol.B64u(spki),
            ["alg"] = "PS256",
            ["certificate"] = BrokerProtocol.B64u(certificate.RawData),
            ["record"] = record,
            ["seal"] = BrokerProtocol.Seal(BrokerProtocol.FromB64u(broker.RecordKey)!, broker.Org, "device", record),
            ["secretId"] = broker.SecretId,
            ["enroll"] = BrokerProtocol.Mac(BrokerProtocol.FromB64u(broker.EnrollKey)!, broker.Org, "enroll", config.DeviceId, BrokerProtocol.KeyHash(spki), nonce),
            ["sig"] = proof,
        };
    }

    /// <summary>The sealed device record. <c>registered</c> is the certificate's start, so registering again seals the same record.</summary>
    internal JsonObject Record(AgentConfig config) => new()
    {
        ["deviceId"] = config.DeviceId,
        ["fingerprint"] = AgentProtocol.Fingerprint(certificate.RawData),
        ["computerName"] = Environment.MachineName,
        ["domain"] = DomainName(),
        ["registered"] = new DateTimeOffset(certificate.NotBefore.ToUniversalTime()).ToUnixTimeSeconds(),
    };

    private static JsonObject Hello(AgentPolicy p) => new()
    {
        ["t"] = "hello",
        ["agentVersion"] = AgentHost.AgentVersion,
        ["os"] = Environment.OSVersion.VersionString,
        ["keepAlive"] = Math.Clamp(p.BrokerKeepAliveSeconds, BrokerProtocol.KeepAliveMin, BrokerProtocol.KeepAliveMax),
        ["macs"] = new JsonArray([.. Adapters().Take(16)]),
    };

    /// <summary>MAC address, IPv4 address and prefix of each adapter that is up – for Wake-on-LAN later (design §10.7).</summary>
    private static IEnumerable<JsonNode> Adapters()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var mac = nic.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length != 6) continue;
            foreach (var a in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                yield return new JsonObject
                {
                    ["mac"] = string.Join(':', mac.Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture))),
                    ["address"] = a.Address.ToString(),
                    ["prefix"] = a.PrefixLength,
                };
        }
    }

    private static string DomainName()
    {
        try { return IPGlobalProperties.GetIPGlobalProperties().DomainName ?? ""; }
        catch { return ""; }
    }

    private static async Task KeepAlive(ClientWebSocket ws, TimeSpan every, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(every, ct);
            // "ping" is answered at the broker's edge without waking anything (design §7.1).
            await ws.SendAsync("ping"u8.ToArray(), WebSocketMessageType.Text, true, ct);
        }
    }

    // ---- WebSocket plumbing ------------------------------------------------------------------------------------

    private static ClientWebSocket NewSocket(AgentPolicy p)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.Zero; // the application keep-alive above
        if (p.BrokerProxy is { } proxy) ws.Options.Proxy = new WebProxy(proxy);
        return ws;
    }

    private static Uri WsBase(Uri broker) => new UriBuilder(broker) { Scheme = broker.Scheme == "http" ? "ws" : "wss", Path = "/" }.Uri;

    private static async Task Send(ClientWebSocket ws, JsonObject m, CancellationToken ct) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(m.ToJsonString()), WebSocketMessageType.Text, true, ct);

    /// <summary>The next message as an object; null when the broker closed. Larger than the limit, not UTF-8 or not an object: refused.</summary>
    private static async Task<JsonObject?> Receive(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[BrokerProtocol.MaxMessageBytes];
        while (true)
        {
            var length = 0;
            ValueWebSocketReceiveResult r;
            do
            {
                if (length == buffer.Length) throw new InvalidDataException("message too large");
                r = await ws.ReceiveAsync(buffer.AsMemory(length), ct);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                length += r.Count;
            } while (!r.EndOfMessage);
            if (r.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("binary message on the control channel");
            if (length == 4 && buffer.AsSpan(0, 4).SequenceEqual("pong"u8)) continue; // the answer to our keep-alive
            string text;
            try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, 0, length); }
            catch (DecoderFallbackException) { throw new InvalidDataException("message isn't UTF-8"); }
            try { return JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("message isn't an object"); }
            catch (JsonException) { throw new InvalidDataException("message isn't JSON"); }
        }
    }

    private static string Text(JsonObject m, string name, int max, bool required = true)
    {
        var node = m[name];
        if (node is null) return required ? throw new InvalidDataException($"{name} missing") : "";
        string value;
        try { value = node.GetValue<string>(); }
        catch (InvalidOperationException) { throw new InvalidDataException($"{name} isn't text"); }
        if (value.Length > max || (required && value.Length == 0)) throw new InvalidDataException($"{name} is out of range");
        return value;
    }

    private void Problem(string message)
    {
        if (message == _lastProblem) return; // once per change, not every retry
        _lastProblem = message;
        log.Write(message);
    }

    private static string Describe(Exception ex) => ex is WebSocketException { InnerException: { } inner } ? inner.Message : ex.Message;

    private static async Task Delay(TimeSpan t, CancellationToken ct)
    {
        try { await Task.Delay(t, ct); } catch (OperationCanceledException) { }
    }
}
