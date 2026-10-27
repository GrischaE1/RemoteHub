using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using RemoteHub.Agent;
using RemoteHub.Services;
using static RemoteHub.Agent.BrokerProtocol;

namespace RemoteHub.Broker;

/// <summary>The broker refused, or couldn't be reached; <see cref="Exception.Message"/> is for the user.</summary>
public sealed class BrokerException(string message, string? code = null, Exception? inner = null) : IOException(message, inner)
{
    /// <summary>The broker's error code or refusal reason (unknown, offline, policy, …), null when it wasn't the broker's answer.</summary>
    public string? Code { get; } = code;
}

/// <summary>
/// This console's connection to one RemoteHub Broker organisation: registration or proof with
/// this PC's key, the keyring, the shared list from the last cursor on, and what the broker pushes. Requests (writes,
/// session offers) go over the same connection. Everything that decides where credentials go arrives sealed and is
/// checked here (<see cref="BrokerSync"/>), never trusted because the broker sent it.
/// </summary>
public sealed class BrokerClient : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(KeepAliveDefault);

    private readonly BrokerConnection _info;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private Task? _run;
    private int _nextRef;

    /// <summary>Keys from the console secret: join (registering again), layout (sealing folders and placements).</summary>
    private readonly byte[] _join, _layout, _keyring;
    private readonly string _consoleSecretId;
    /// <summary>Record keys by device secret id, from the keyring: they check device records and seal offers.</summary>
    private Dictionary<string, byte[]> _recordKeys = [];

    public BrokerConnection Info => _info;
    public string State { get; private set; } = "Connecting…";
    public bool IsConnected { get; private set; }
    public string KeyHash { get; private set; } = "";

    /// <summary>The STUN service the broker names for the direct path (its welcome); Cloudflare's until then.</summary>
    public string StunServer { get; private set; } = PeerDirect.DefaultStun;

    private readonly object _certificateLock = new();
    private ECDsa? _peerKey;
    private X509Certificate2? _peerCertificate;

    /// <summary>
    /// The organisation's newest device secret, from the keyring (null until it is read, or when it holds none): what new
    /// devices join with – the MSI command line and Assign to RemoteHub Broker. Memory only, never logged.
    /// </summary>
    public string? DeviceSecret { get; private set; }

    /// <summary>Raised on a thread-pool thread – the receiver marshals to the dispatcher.</summary>
    public event Action<BrokerClient, IReadOnlyList<JsonObject>>? Changed;
    public event Action<BrokerClient, string, bool, long>? Presence;
    /// <summary>Every device's online state (a <c>presenceList</c> snapshot): after connecting and every <see cref="PresenceInterval"/>.</summary>
    public event Action<BrokerClient, IReadOnlyList<(string DeviceId, bool Online)>>? PresenceSnapshot;

    /// <summary>
    /// How often the online state of every device is read again while connected. The shared list doesn't carry it (it
    /// changes without moving the cursor) and a push can be missed – a connection that drops, a debounce – so each dot is
    /// at most this old.
    /// </summary>
    internal static TimeSpan PresenceInterval { get; set; } = TimeSpan.FromSeconds(45);
    /// <summary>A snapshot reads at most this many pages (500 devices each); a broker that keeps saying "more" is stopped there.</summary>
    internal const int MaxPresencePages = 200;
    private bool _presenceList;
    public event Action<BrokerClient>? StateChanged;

    private BrokerClient(BrokerConnection info, string consoleSecret)
    {
        _info = info;
        var s = ParseSecret(consoleSecret) ?? throw new BrokerException("The stored console secret is damaged – connect to the broker again.");
        _consoleSecretId = s.Id;
        _join = DeriveKey(s.Key, s.Org, KeyLabel.Join);
        _layout = DeriveKey(s.Key, s.Org, KeyLabel.Layout);
        _keyring = DeriveKey(s.Key, s.Org, KeyLabel.Keyring);
        CryptographicOperations.ZeroMemory(s.Key);
    }

    /// <summary>Starts the connection of a stored broker connection (RemoteHub's start, or after joining).</summary>
    public static BrokerClient Start(BrokerConnection info)
    {
        var c = new BrokerClient(info, BrokerStore.Secret(info.Id));
        c._run = Task.Run(c.RunAsync);
        return c;
    }

    /// <summary>
    /// Settings → RemoteHub Broker → Connect: registers this PC with the console secret (a new key for this PC) and
    /// stores the connection. Throws <see cref="BrokerException"/> with what went wrong.
    /// </summary>
    public static async Task<BrokerConnection> JoinAsync(string url, string consoleSecret, string consoleName, CancellationToken ct)
    {
        var broker = ParseBrokerUrl(url) ?? throw new BrokerException("That isn't a broker address – use https://name.");
        var secret = ParseSecret(consoleSecret) ?? throw new BrokerException("That isn't a console secret (rhc1.…).");
        if (secret.Kind != SecretKind.Console) throw new BrokerException("That is a device secret – RemoteHub needs the console secret (rhc1.…).");
        var consoleId = NewId();
        using var key = BrokerKey.Create(consoleId);
        string orgName;
        try
        {
            using var ws = await OpenAsync(broker, secret.Org, ct).ConfigureAwait(false);
            var (nonce, host) = await ChallengeAsync(ws, broker, ct).ConfigureAwait(false);
            var spki = key.ExportSubjectPublicKeyInfo();
            await SendAsync(ws, new JsonObject
            {
                ["t"] = "register",
                ["id"] = consoleId,
                ["publicKey"] = B64u(spki),
                ["alg"] = "ES256",
                ["name"] = consoleName,
                ["secretId"] = secret.Id,
                ["join"] = Mac(DeriveKey(secret.Key, secret.Org, KeyLabel.Join), secret.Org, "join", consoleId, BrokerProtocol.KeyHash(spki), nonce),
                ["sig"] = SignEs256(key, ProofText(secret.Org, "console", consoleId, nonce, host)),
            }, ct).ConfigureAwait(false);
            var welcome = await ReceiveAsync(ws, ct).ConfigureAwait(false) ?? throw Refused(ws);
            orgName = Text(welcome, "orgName", 200);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct).ConfigureAwait(false); } catch { /* done */ }
        }
        catch
        {
            BrokerKey.Delete(consoleId);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(secret.Key); }
        return BrokerStore.Add(broker.GetLeftPart(UriPartial.Authority), secret.Org, orgName, consoleId, consoleName, consoleSecret.Trim());
    }

    // ---- The connection ------------------------------------------------------------------------------------------

    private async Task RunAsync()
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!_stop.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try { await SessionAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (BrokerException ex) when (ex.Code is "removed" or "nokey")
            {
                SetState(false, ex.Message);
                return; // nothing a retry changes: the user connects again
            }
            catch (Exception ex) { SetState(false, $"Can't reach the RemoteHub Broker {Host}: {Describe(ex)}"); }
            foreach (var p in _pending.Values) p.TrySetException(new BrokerException($"The connection to the RemoteHub Broker {Host} was lost."));
            _pending.Clear();
            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(1)) backoff = TimeSpan.FromSeconds(1);
            try { await Task.Delay(backoff + TimeSpan.FromMilliseconds(Random.Shared.Next((int)backoff.TotalMilliseconds)), _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(60).Ticks, backoff.Ticks * 2));
        }
    }

    private string Host => new Uri(_info.Url).Authority;

    private async Task SessionAsync()
    {
        var broker = new Uri(_info.Url);
        var ct = _stop.Token;
        using var key = BrokerKey.Open(_info.ConsoleId)
            ?? throw new BrokerException("This PC's key for the RemoteHub Broker is missing (another Windows user, or a new PC). Connect to the broker again.", "nokey");
        var spki = key.ExportSubjectPublicKeyInfo();
        KeyHash = BrokerProtocol.KeyHash(spki);
        using var ws = await OpenAsync(broker, _info.Org, ct).ConfigureAwait(false);
        var (nonce, host) = await ChallengeAsync(ws, broker, ct).ConfigureAwait(false);
        var sig = SignEs256(key, ProofText(_info.Org, "console", _info.ConsoleId, nonce, host));
        await SendAsync(ws, new JsonObject { ["t"] = "proof", ["id"] = _info.ConsoleId, ["sig"] = sig }, ct).ConfigureAwait(false);
        var welcome = await ReceiveAsync(ws, ct).ConfigureAwait(false);
        if (welcome is null)
        {
            if ((int?)ws.CloseStatus == Close.Unknown)
            {
                // The broker doesn't know this console (it lost its data): register again with the kept key.
                await RegisterAgainAsync(broker, key, spki, ct).ConfigureAwait(false);
                return;
            }
            if ((int?)ws.CloseStatus == Close.Refused) throw new BrokerException("This PC was removed from the RemoteHub Broker. Connect again with the console secret.", "removed");
            throw Refused(ws);
        }
        var orgName = Text(welcome, "orgName", 200);
        if (welcome["stun"] is JsonValue stun && stun.TryGetValue<string>(out var server) && Stun.ParseServer(server) is not null) StunServer = server.Trim();
        // An older broker closes the connection on a request it doesn't know: ask only for what it names.
        _presenceList = HasFeature(welcome, "presenceList");
        if (orgName != _info.OrgName && orgName.Length > 0) BrokerStore.SetOrgName(_info.Id, orgName);

        _ws = ws;
        var reader = Task.Run(() => ReadLoop(ws, ct), ct);
        using var alive = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var keepAlive = PingLoop(ws, alive.Token);
        var presence = Task.CompletedTask;
        try
        {
            await LoadKeyringAsync().ConfigureAwait(false);
            await SyncAsync().ConfigureAwait(false);
            SetState(true, $"Connected to {orgName} on {Host}");
            if (_presenceList) presence = PresenceLoop(alive.Token);
            await reader.ConfigureAwait(false);
        }
        finally
        {
            alive.Cancel();
            _ws = null;
            SetState(false, $"The connection to the RemoteHub Broker {Host} was lost.");
            try { await keepAlive.ConfigureAwait(false); } catch { /* ended with it */ }
            try { await presence.ConfigureAwait(false); } catch { /* ended with it */ }
        }
    }

    private async Task RegisterAgainAsync(Uri broker, ECDsa key, byte[] spki, CancellationToken ct)
    {
        using var ws = await OpenAsync(broker, _info.Org, ct).ConfigureAwait(false);
        var (nonce, host) = await ChallengeAsync(ws, broker, ct).ConfigureAwait(false);
        await SendAsync(ws, new JsonObject
        {
            ["t"] = "register",
            ["id"] = _info.ConsoleId,
            ["publicKey"] = B64u(spki),
            ["alg"] = "ES256",
            ["name"] = _info.ConsoleName,
            ["secretId"] = _consoleSecretId,
            ["join"] = Mac(_join, _info.Org, "join", _info.ConsoleId, BrokerProtocol.KeyHash(spki), nonce),
            ["sig"] = SignEs256(key, ProofText(_info.Org, "console", _info.ConsoleId, nonce, host)),
        }, ct).ConfigureAwait(false);
        _ = await ReceiveAsync(ws, ct).ConfigureAwait(false) ?? throw Refused(ws);
        DiagLog.Write("Broker: this console registered again (the broker didn't know it any more)");
    }

    private async Task LoadKeyringAsync()
    {
        var answer = await RequestAsync(new JsonObject { ["t"] = "keyring" }).ConfigureAwait(false);
        var secrets = OpenKeyring(_keyring, _info.Org, _consoleSecretId, answer["blob"]?.GetValue<string>()) ?? [];
        var keys = new Dictionary<string, byte[]>();
        string? newest = null;
        foreach (var text in secrets)
        {
            if (ParseSecret(text) is not { Kind: SecretKind.Device } d || d.Org != _info.Org) continue;
            keys[d.Id] = DeriveKey(d.Key, d.Org, KeyLabel.Record);
            CryptographicOperations.ZeroMemory(d.Key);
            newest = text; // the keyring lists them oldest first
        }
        _recordKeys = keys;
        DeviceSecret = newest;
        if (keys.Count == 0) DiagLog.Write("Broker: no device secret in this console's keyring – devices can't be checked or reached");
    }

    /// <summary>The shared list from the stored cursor on, page by page, into the local cache and to <see cref="Changed"/>.</summary>
    private async Task SyncAsync()
    {
        var cursor = BrokerStore.All().FirstOrDefault(b => b.Id == _info.Id)?.Cursor ?? 0;
        while (true)
        {
            var page = await RequestAsync(new JsonObject { ["t"] = "sync", ["since"] = cursor }).ConfigureAwait(false);
            var items = page["items"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
            cursor = page["cursor"]?.GetValue<long>() ?? cursor;
            Cache(items, cursor);
            if (items.Count > 0) Changed?.Invoke(this, items);
            if (page["more"]?.GetValue<bool>() != true) return;
        }
    }

    /// <summary>Every device's online state now, then again every <see cref="PresenceInterval"/> until the connection ends.</summary>
    private async Task PresenceLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { PresenceSnapshot?.Invoke(this, await PresenceListAsync(ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is BrokerException or InvalidDataException) { DiagLog.Write("Broker: the online state wasn't read: " + ex.Message); }
            await Task.Delay(PresenceInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The <c>presenceList</c> pages, in device id order.</summary>
    private async Task<IReadOnlyList<(string DeviceId, bool Online)>> PresenceListAsync(CancellationToken ct)
    {
        var all = new List<(string, bool)>();
        string? after = null;
        for (var page = 0; page < MaxPresencePages; page++)
        {
            var request = new JsonObject { ["t"] = "presenceList" };
            if (after is not null) request["after"] = after;
            var (items, more) = ReadPresencePage(await RequestAsync(request, ct).ConfigureAwait(false), after);
            all.AddRange(items);
            if (!more || items.Count == 0) return all;
            after = items[^1].DeviceId;
        }
        DiagLog.Write($"Broker: the online state stopped after {MaxPresencePages} pages");
        return all;
    }

    /// <summary>
    /// One <c>presenceList</c> answer, checked: device ids as GUIDs, each one after <paramref name="after"/> and the one
    /// before it – a page out of order or repeating itself is refused rather than read without end.
    /// </summary>
    /// <exception cref="InvalidDataException">The page isn't one.</exception>
    internal static (List<(string DeviceId, bool Online)> Items, bool More) ReadPresencePage(JsonObject answer, string? after)
    {
        if (answer["items"] is not JsonArray array) throw new InvalidDataException("presenceList from the broker has no items");
        var items = new List<(string, bool)>(array.Count);
        var last = after;
        foreach (var node in array)
        {
            if (node is not JsonObject i) throw new InvalidDataException("presenceList from the broker has an item that isn't an object");
            var id = Text(i, "deviceId", 36);
            if (!Guid.TryParseExact(id, "D", out _)) throw new InvalidDataException("presenceList from the broker has an item without a device id");
            if (last is not null && string.CompareOrdinal(id, last) <= 0) throw new InvalidDataException("presenceList from the broker isn't in device id order");
            items.Add((id, i["online"] is JsonValue v && v.TryGetValue<bool>(out var on) && on));
            last = id;
        }
        return (items, answer["more"] is JsonValue m && m.TryGetValue<bool>(out var more) && more);
    }

    /// <summary>Whether a broker's <c>welcome</c> names <paramref name="feature"/> among the requests beyond the first version.</summary>
    internal static bool HasFeature(JsonObject welcome, string feature) =>
        welcome["features"] is JsonArray list && list.Any(f => f is JsonValue v && v.TryGetValue<string>(out var s) && s == feature);

    private void Cache(IReadOnlyList<JsonObject> items, long cursor) =>
        BrokerStore.PutItems(_info.Id, items.Select(i => (i["kind"]?.GetValue<string>() ?? "", i["id"]?.GetValue<string>() ?? "", i.ToJsonString())), cursor);

    private async Task ReadLoop(ClientWebSocket ws, CancellationToken ct)
    {
        while (true)
        {
            var m = await ReceiveAsync(ws, ct).ConfigureAwait(false);
            if (m is null) return;
            if (m["ref"]?.GetValue<string>() is { } r && _pending.TryRemove(r, out var waiting)) { waiting.TrySetResult(m); continue; }
            switch (m["t"]?.GetValue<string>())
            {
                case "changed":
                    var items = m["items"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
                    Cache(items, m["cursor"]?.GetValue<long>() ?? 0);
                    Changed?.Invoke(this, items);
                    break;
                case "presence":
                    Presence?.Invoke(this, Text(m, "deviceId", 36), m["online"]?.GetValue<bool>() == true, m["lastSeen"]?.GetValue<long>() ?? 0);
                    break;
            }
        }
    }

    private static async Task PingLoop(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(KeepAlive, ct).ConfigureAwait(false);
            await ws.SendAsync("ping"u8.ToArray(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
    }

    private void SetState(bool connected, string state)
    {
        if (IsConnected == connected && State == state) return;
        IsConnected = connected;
        State = state;
        StateChanged?.Invoke(this);
    }

    // ---- Requests ------------------------------------------------------------------------------------------------

    /// <summary>Sends a request and waits for the answer with the same ref. An "error" answer is thrown as <see cref="BrokerException"/>.</summary>
    internal async Task<JsonObject> RequestAsync(JsonObject request, CancellationToken ct = default)
    {
        var ws = _ws ?? throw new BrokerException($"RemoteHub isn't connected to the RemoteHub Broker {Host} right now.");
        var r = "r" + Interlocked.Increment(ref _nextRef).ToString(System.Globalization.CultureInfo.InvariantCulture);
        request["ref"] = r;
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[r] = tcs;
        try
        {
            await SendLocked(ws, request, ct).ConfigureAwait(false);
            var answer = await tcs.Task.WaitAsync(RequestTimeout, ct).ConfigureAwait(false);
            if (answer["t"]?.GetValue<string>() == "error")
                throw new BrokerException(answer["message"]?.GetValue<string>() ?? "The broker refused the request.", answer["code"]?.GetValue<string>());
            return answer;
        }
        catch (TimeoutException) { throw new BrokerException($"The RemoteHub Broker {Host} didn't answer in time."); }
        finally { _pending.TryRemove(r, out _); }
    }

    private async Task SendLocked(ClientWebSocket ws, JsonObject m, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await SendAsync(ws, m, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    // ---- Sealed data ---------------------------------------------------------------------------------------------

    /// <summary>Whether a device record was sealed with one of the organisation's record keys.</summary>
    public bool RecordIsSealed(JsonObject? record, string? seal) =>
        record is not null && _recordKeys.Values.Any(k => VerifySeal(k, seal, _info.Org, "device", record));

    /// <summary>Whether a folder, placement or target was sealed with this organisation's layout key – only consoles have it.</summary>
    public bool LayoutIsSealed(string kind, JsonObject? body, string? seal) => VerifySeal(_layout, seal, _info.Org, kind, body);

    public string SealLayout(string kind, JsonObject body) => Seal(_layout, _info.Org, kind, body);

    /// <summary>Writes folders, placements or targets (sealed here); the broker keeps the highest version of each.</summary>
    public Task<JsonObject> WriteAsync(IEnumerable<(string Kind, JsonObject Body)> entries)
    {
        var items = new JsonArray([.. entries.Select(e => (JsonNode)new JsonObject
        {
            ["kind"] = e.Kind, ["id"] = e.Body["id"]!.GetValue<string>(), ["body"] = e.Body.DeepClone(), ["seal"] = SealLayout(e.Kind, e.Body),
        })]);
        return RequestAsync(new JsonObject { ["t"] = "write", ["items"] = items });
    }

    public Task<JsonObject> RemoveDeviceAsync(string deviceId) => RequestAsync(new JsonObject { ["t"] = "removeDevice", ["id"] = deviceId });

    public async Task<IReadOnlyList<JsonObject>> ConsolesAsync() =>
        (await RequestAsync(new JsonObject { ["t"] = "consoles" }).ConfigureAwait(false))["items"]?.AsArray().OfType<JsonObject>().ToList() ?? [];

    public Task<JsonObject> RemoveConsoleAsync(string consoleId) => RequestAsync(new JsonObject { ["t"] = "removeConsole", ["id"] = consoleId });

    /// <summary>
    /// Asks for a session with a device: an offer sealed with the record key of the device secret the device
    /// registered with, so a broker alone can't make one. Returns the broker's <c>accepted</c> answer.
    /// </summary>
    public async Task<JsonObject> OfferAsync(string deviceId, string? secretId, IReadOnlyList<string> cands, CancellationToken ct)
    {
        var recordKey = (secretId is not null && _recordKeys.TryGetValue(secretId, out var k) ? k : _recordKeys.Values.FirstOrDefault())
            ?? throw new BrokerException("This console has no device secret of the organisation – ask the administrator for a current console secret.");
        var offer = new JsonObject
        {
            ["consoleId"] = _info.ConsoleId,
            ["consoleKey"] = KeyHash,
            ["deviceId"] = deviceId,
            ["expires"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120,
            ["kind"] = "device",
            ["nonce"] = B64u(RandomNumberGenerator.GetBytes(16)),
        };
        var answer = await RequestAsync(new JsonObject
        {
            ["t"] = "connect", ["deviceId"] = deviceId, ["offer"] = offer.DeepClone(), ["offerSeal"] = Seal(recordKey, _info.Org, "offer", offer),
            ["cands"] = new JsonArray([.. cands.Take(MaxCandidates).Select(c => JsonValue.Create(c))]),
        }, ct).ConfigureAwait(false);
        if (answer["t"]?.GetValue<string>() == "refused")
            throw new BrokerException(answer["message"]?.GetValue<string>() ?? "The device refused the connection.", answer["reason"]?.GetValue<string>());
        return answer;
    }

    /// <summary>
    /// This PC's certificate for the direct path: on its console key (CNG, never exportable), made once per run. The
    /// agent takes a QUIC connection only with the key the sealed offer names.
    /// </summary>
    internal X509Certificate2 PeerCertificate()
    {
        lock (_certificateLock)
        {
            if (_peerCertificate is { } made && made.NotAfter > DateTime.Now.AddHours(1)) return made;
            _peerKey ??= BrokerKey.Open(_info.ConsoleId)
                ?? throw new BrokerException("This PC's key for the RemoteHub Broker is missing (another Windows user, or a new PC). Connect to the broker again.", "nokey");
            var request = new CertificateRequest($"CN={_info.ConsoleId}, O=RemoteHub", _peerKey, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
            _peerCertificate?.Dispose();
            return _peerCertificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7));
        }
    }

    /// <summary>The relay WebSocket of an accepted session, console side.</summary>
    internal async Task<ClientWebSocket> OpenRelayAsync(JsonObject accepted, CancellationToken ct)
    {
        var session = Text(accepted, "session", 22);
        var sessionKey = FromB64u(Text(accepted, "key", 64)) ?? throw new BrokerException("The broker's answer is damaged.");
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        ws.Options.SetRequestHeader("Authorization", "RemoteHub-Relay " + Mac(sessionKey, _info.Org, "relay", session, "console"));
        CryptographicOperations.ZeroMemory(sessionKey);
        var path = Text(accepted, "relay", 200) + "&side=console";
        try { await ws.ConnectAsync(new Uri(WsBase(new Uri(_info.Url)), path), ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            ws.Dispose();
            throw new BrokerException($"The RemoteHub Broker's relay didn't open: {Describe(ex)}", inner: ex);
        }
        return ws;
    }

    // ---- Plumbing -------------------------------------------------------------------------------------------------

    private static Uri WsBase(Uri broker) => new UriBuilder(broker) { Scheme = broker.Scheme == "http" ? "ws" : "wss", Path = "/" }.Uri;

    private static async Task<ClientWebSocket> OpenAsync(Uri broker, string org, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.Zero; // the application keep-alive, answered at the broker's edge
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await ws.ConnectAsync(new Uri(WsBase(broker), $"/v1/o/{org}/console"), timeout.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            ws.Dispose();
            var detail = Describe(ex);
            var message = ex is OperationCanceledException ? $"The RemoteHub Broker {broker.Authority} didn't answer in time."
                : detail.Contains("404", StringComparison.Ordinal) ? $"The RemoteHub Broker {broker.Authority} doesn't know this organisation."
                : $"Can't reach the RemoteHub Broker {broker.Authority}: {detail}";
            throw new BrokerException(message, inner: ex);
        }
        return ws;
    }

    private static async Task<(string Nonce, string Host)> ChallengeAsync(ClientWebSocket ws, Uri broker, CancellationToken ct)
    {
        var challenge = await ReceiveAsync(ws, ct).ConfigureAwait(false) ?? throw Refused(ws);
        var host = Text(challenge, "host", 255);
        // Sign only for the broker this PC connected to.
        if (!string.Equals(host, broker.Authority, StringComparison.OrdinalIgnoreCase)) throw new BrokerException($"The broker answers as {host}, not {broker.Authority}.");
        return (Text(challenge, "nonce", 64), host);
    }

    private static BrokerException Refused(ClientWebSocket ws) => (int?)ws.CloseStatus switch
    {
        Close.AuthFailed => new BrokerException(ws.CloseStatusDescription is { Length: > 0 } d && d.Contains("no longer", StringComparison.Ordinal)
            ? "The console secret is no longer accepted (retired). Ask your administrator for the current one."
            : "The RemoteHub Broker refused the console secret – check it with your administrator.", "auth"),
        Close.Refused => new BrokerException("This PC was removed from the RemoteHub Broker. Connect again with the console secret.", "removed"),
        Close.Unknown => new BrokerException("The RemoteHub Broker doesn't know this console.", "unknown"),
        _ => new BrokerException($"The RemoteHub Broker closed the connection ({ws.CloseStatusDescription}).", "closed"),
    };

    private static Task SendAsync(ClientWebSocket ws, JsonObject m, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(m.ToJsonString()), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonObject?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[MaxMessageBytes];
        while (true)
        {
            var length = 0;
            ValueWebSocketReceiveResult r;
            do
            {
                if (length == buffer.Length) throw new InvalidDataException("message from the broker too large");
                r = await ws.ReceiveAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                length += r.Count;
            } while (!r.EndOfMessage);
            if (length == 4 && buffer.AsSpan(0, 4).SequenceEqual("pong"u8)) continue;
            try { return JsonNode.Parse(buffer.AsSpan(0, length)) as JsonObject ?? throw new InvalidDataException("message isn't an object"); }
            catch (JsonException) { throw new InvalidDataException("message from the broker isn't JSON"); }
        }
    }

    private static string Text(JsonObject m, string name, int max)
    {
        var v = m[name] is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : "";
        if (v.Length > max) throw new InvalidDataException($"{name} from the broker is too long");
        return v;
    }

    private static string Describe(Exception ex) => ex is WebSocketException { InnerException: { } inner } ? inner.Message : ex.Message;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { _ws?.Abort(); } catch { /* gone */ }
        if (_run is not null) try { await _run.ConfigureAwait(false); } catch { /* stopping */ }
        CryptographicOperations.ZeroMemory(_join);
        CryptographicOperations.ZeroMemory(_layout);
        CryptographicOperations.ZeroMemory(_keyring);
        foreach (var k in _recordKeys.Values) CryptographicOperations.ZeroMemory(k);
        DeviceSecret = null;
        lock (_certificateLock)
        {
            _peerCertificate?.Dispose();
            _peerKey?.Dispose();
        }
    }
}
