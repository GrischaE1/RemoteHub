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
/// The agent's permanent connection to its RemoteHub Broker: outbound
/// only, kept open with a keep-alive, proven with the device's own key. What may come down it is a closed
/// list; anything else closes the connection. The only thing it ever leads to is a session the device checked and
/// accepted – a console's offer sealed with the record key – which then runs through <see cref="BrokerRelay"/> into the
/// agent's own HTTPS endpoint, where the pin, the Windows sign-in, rights and policy apply as on a direct connection.
/// </summary>
internal sealed class BrokerLink(
    Func<AgentConfig> currentConfig,
    X509Certificate2 certificate,
    Func<AgentPolicy> policy,
    AgentLog log,
    PeerTransport peers,
    Func<DateTimeOffset>? clock = null,
    IDirectFirewall? firewall = null)
{
    /// <summary>The message types the broker may send an agent – a closed list.</summary>
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
    /// <summary>One send at a time on the control socket: an answer to an offer and the keep-alive can come together.</summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    /// <summary>The current connection's liveness (null between connections).</summary>
    private volatile BrokerLiveness? _live;
    /// <summary>Completed by a network change while not connected: the next attempt starts at once.</summary>
    private volatile TaskCompletionSource _retryNow = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Completed by every network change: the connected link tells the broker its addresses again once they settle.</summary>
    private volatile TaskCompletionSource _networkChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>How long the addresses must stay the same after a change before they are sent (Windows reports one change in bursts).</summary>
    internal static TimeSpan AddressSettle { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>How many addresses the hello carries at most (the broker refuses more).</summary>
    internal const int MaxAddresses = 16;

    /// <summary>For tests: the state the link is in, and each session's end.</summary>
    internal string State { get; private set; } = "idle";
    internal event Action<string>? SessionEnded;

    public async Task RunAsync(CancellationToken stop)
    {
        // A device that sleeps comes back with a new network: a connection is checked at once (it may have died without
        // a close), and without one the next attempt doesn't wait for the back-off.
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        try { await RunLoopAsync(stop); }
        finally { NetworkChange.NetworkAddressChanged -= OnNetworkChanged; }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        if (_live is { } live) live.Check();
        else _retryNow.TrySetResult();
        _networkChanged.TrySetResult();
    }

    private async Task RunLoopAsync(CancellationToken stop)
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
            var retryNow = _retryNow.Task;
            await Task.WhenAny(Delay(backoff + TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(0, (int)backoff.TotalMilliseconds + 1)), stop), retryNow);
            if (retryNow.IsCompleted)
            {
                _retryNow = new(TaskCreationOptions.RunContinuationsAsynchronously);
                backoff = TimeSpan.FromSeconds(1);
            }
            else backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
        }
    }

    internal enum Outcome { Ended, Register, Stop }

    /// <summary>
    /// The broker says the organisation was deleted. The message isn't sealed and the broker isn't trusted, so the keys stay
    /// in agent.json: the link only stops and tries again in an hour (<see cref="Outcome.Stop"/>). If the organisation is
    /// really gone, that try is refused again; if it was restored from an export, the device comes back without a new
    /// join. Only <c>broker leave</c> or uninstalling removes the keys.
    /// </summary>
    internal Outcome OrgDeleted(BrokerSettings broker, Uri baseUri)
    {
        log.Audit(AuditEvent.BrokerError, $"The RemoteHub Broker {baseUri.Authority} says the organisation {broker.Org} was deleted – the device " +
                                          "keeps its broker keys and tries again in an hour ('broker leave' removes them).");
        return Outcome.Stop;
    }

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
        else if (_lastProblem is not null) log.Write($"RemoteHub Broker {host}: connected again");
        _lastProblem = null;
        State = "connected";

        var sent = Hello(p, config);
        await Send(ws, sent, stop);
        using var alive = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var addresses = HelloOnChangesAsync(ws, p, config, sent, alive.Token);
        var live = new BrokerLiveness(TimeSpan.FromSeconds(Math.Clamp(p.BrokerKeepAliveSeconds, BrokerProtocol.KeepAliveMin, BrokerProtocol.KeepAliveMax)));
        _live = live;
        // "ping" is answered at the broker's edge without waking anything.
        var keepAlive = live.KeepAliveAsync(ws, c => SendLocked(ws, "ping"u8.ToArray(), c), alive.Token);
        try { return await ReceiveLoop(ws, live, config, broker, baseUri, stop); }
        finally
        {
            alive.Cancel();
            _live = null;
            string? gaveUp = null;
            try { gaveUp = await keepAlive; } catch { /* ended with the connection */ }
            if (gaveUp is not null) log.Write($"RemoteHub Broker {host}: the connection was given up – {gaveUp}");
            try { await addresses; } catch { /* ended with the connection */ }
        }
    }

    /// <summary>
    /// After a network change (a new Wi-Fi, a VPN, another DHCP lease) the broker gets the device's addresses again – once
    /// they have stayed the same for <see cref="AddressSettle"/>, and only when they differ from what it has – so a
    /// console finds the device at its new address. A move that ends this connection sends them with the next one.
    /// </summary>
    private async Task HelloOnChangesAsync(ClientWebSocket ws, AgentPolicy p, AgentConfig config, JsonObject sent, CancellationToken ct)
    {
        var last = sent["addresses"]?.ToJsonString();
        while (!ct.IsCancellationRequested)
        {
            if (!await WaitChange(Timeout.InfiniteTimeSpan, ct)) return;
            // Settle: every further change within AddressSettle starts the wait again.
            while (await WaitChange(AddressSettle, ct)) { }
            if (ct.IsCancellationRequested) return;
            var hello = Hello(p, config);
            var now = hello["addresses"]?.ToJsonString();
            if (now == last) continue;
            await Send(ws, hello, ct);
            last = now;
        }
    }

    /// <summary>True when the network changed within <paramref name="within"/> (the change is taken); false after the wait, or when <paramref name="ct"/> ends.</summary>
    private async Task<bool> WaitChange(TimeSpan within, CancellationToken ct)
    {
        var changed = _networkChanged.Task;
        try { await changed.WaitAsync(within, ct); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { return false; }
        _networkChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return true;
    }

    private async Task<Outcome> ReceiveLoop(ClientWebSocket ws, BrokerLiveness live, AgentConfig config, BrokerSettings broker, Uri baseUri, CancellationToken stop)
    {
        var window = new Queue<DateTime>();
        while (true)
        {
            var m = await Receive(ws, stop, live);
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
                    return OrgDeleted(broker, baseUri);
                case "error":
                    log.Write($"RemoteHub Broker: {Text(m, "message", 300, required: false)}");
                    break;
                // wake and checks come with later versions of the broker: known, not acted on yet.
            }
        }
    }

    // ---- Offers ------------------------------------------------------------------------------------------------

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
        if (why is null && !BrokerProtocol.IsRelayPath(relay, broker.Org, session)) why = ("offer", "The broker named a relay that isn't its own.");
        var p = policy();
        var consoleId = offer["consoleId"]?.GetValue<string>() ?? "";
        if (why is null && (!p.BrokerEnabled || p.LoadError is not null)) why = ("policy", "The device's policy turns the RemoteHub Broker off (BrokerEnabled).");
        if (why is null && p.BrokerConsoles.Count > 0 && !p.BrokerConsoles.Contains(consoleId))
            why = ("policy", "The device's policy doesn't allow this PC through the RemoteHub Broker (BrokerConsoles).");
        if (why is null && _sessions.Count >= p.MaxSessions) why = ("limit", $"Too many sessions on the device (MaxSessions = {p.MaxSessions}).");
        if (why is { } refused)
        {
            log.Audit(AuditEvent.BrokerSessionRefused, $"Session through the RemoteHub Broker refused: console {consoleId} ({consoleName}) from {source} – {refused.Message}");
            CryptographicOperations.ZeroMemory(sessionKey);
            await Send(ws, new JsonObject { ["t"] = "refuse", ["session"] = session, ["reason"] = refused.Reason, ["message"] = refused.Message }, stop);
            return;
        }

        // The direct path's socket and candidates come first: they go in the answer.
        var stun = p.BrokerStun ?? (Stun.ParseServer(Text(m, "stun", 200, required: false)) is not null ? Text(m, "stun", 200) : PeerDirect.DefaultStun);
        var first = await BrokerSession.PrepareAsync(p.BrokerDirect, stun, stop);
        var remote = PeerDirect.Usable((m["cands"] as JsonArray)?.Select(c => c is JsonValue v && v.TryGetValue<string>(out var t) ? t : null), p.BrokerDirect);
        try { await Send(ws, new JsonObject { ["t"] = "accept", ["session"] = session, ["cands"] = new JsonArray([.. (first?.Cands ?? []).Select(c => JsonValue.Create(c))]) }, stop); }
        catch
        {
            first?.Dispose();
            throw;
        }
        var relayUri = new Uri(WsBase(baseUri), relay + "&side=agent");
        var auth = BrokerProtocol.Mac(sessionKey, broker.Org, "relay", session, "agent");
        var who = $"console {consoleId} ({consoleName}) from {source}";
        var consoleKey = offer["consoleKey"]?.GetValue<string>() ?? "";
        var s = new BrokerSession(session, sessionKey, consoleKey, who, source, p, policy, stun, peers, certificate, log, firewall);
        _sessions[session] = Task.Run(async () =>
        {
            string reason;
            try { reason = await s.RunAsync(relayUri, auth, first, remote, stop); }
            catch (Exception ex) { reason = "failed: " + Describe(ex); }
            finally { _sessions.TryRemove(session, out _); }
            SessionEnded?.Invoke(reason);
        }, CancellationToken.None);
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
        // The broker can't make this seal (it has no record key): a broker alone can't start a session.
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

    // ---- Messages to the broker --------------------------------------------------------------------------------

    /// <summary>Self-registration: the enroll MAC shows the organisation, the proof the device's own key.</summary>
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

    /// <summary>
    /// What the broker keeps about the device besides its sealed record: version, adapters (Wake-on-LAN), and the
    /// addresses and port a console tries before it uses the broker – <c>inbound</c> false when the agent's firewall rule
    /// isn't there (MSI BROKER_ONLY=1), so a console doesn't wait for an answer that can't come.
    /// </summary>
    internal static JsonObject Hello(AgentPolicy p, AgentConfig config) => new()
    {
        ["t"] = "hello",
        ["agentVersion"] = AgentHost.AgentVersion,
        ["os"] = Environment.OSVersion.VersionString,
        ["keepAlive"] = Math.Clamp(p.BrokerKeepAliveSeconds, BrokerProtocol.KeepAliveMin, BrokerProtocol.KeepAliveMax),
        ["macs"] = new JsonArray([.. Adapters().Take(16)]),
        // The addresses the direct path offers too – adapters with a gateway first, Hyper-V switches last.
        ["addresses"] = new JsonArray([.. PeerDirect.HostAddresses().Take(MaxAddresses).Select(a => JsonValue.Create(a.ToString()))]),
        ["port"] = config.ListenPort,
        ["inbound"] = Enrollment.HasFirewallRule(),
    };

    /// <summary>MAC address, IPv4 address and prefix of each adapter that is up – for Wake-on-LAN later.</summary>
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

    // ---- WebSocket plumbing ------------------------------------------------------------------------------------

    internal static ClientWebSocket NewSocket(AgentPolicy p)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.Zero; // the application keep-alive (BrokerLiveness)
        if (p.BrokerHttpProxy is { } proxy) ws.Options.Proxy = new WebProxy(proxy);
        return ws;
    }

    private static Uri WsBase(Uri broker) => new UriBuilder(broker) { Scheme = broker.Scheme == "http" ? "ws" : "wss", Path = "/" }.Uri;

    private Task Send(ClientWebSocket ws, JsonObject m, CancellationToken ct) => SendLocked(ws, Encoding.UTF8.GetBytes(m.ToJsonString()), ct);

    private async Task SendLocked(ClientWebSocket ws, byte[] text, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await ws.SendAsync(text, WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    /// <summary>
    /// The next message as an object; null when the broker closed. Larger than the limit, not UTF-8 or not an object:
    /// refused. Every frame, a pong included, tells <paramref name="live"/> the connection carries.
    /// </summary>
    private static async Task<JsonObject?> Receive(ClientWebSocket ws, CancellationToken ct, BrokerLiveness? live = null)
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
            live?.Heard();
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
