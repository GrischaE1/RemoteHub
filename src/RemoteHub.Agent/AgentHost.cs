using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;

namespace RemoteHub.Agent;

/// <summary>What the host needs; the service passes the real Windows pieces, tests pass their own.</summary>
public sealed record AgentHostOptions(
    AgentConfig Config,
    X509Certificate2 Certificate,
    IShellAuthenticator Authenticator,
    IShellLauncher Launcher,
    AgentLog Log,
    IPAddress? BindAddress = null,
    DesktopServices? Desktop = null,
    Func<AgentPolicy>? Policy = null)
{
    /// <summary>Name resolution for jump connections (tests set their own); null = the system's.</summary>
    internal TunnelNetwork? TunnelNetwork { get; init; }

    /// <summary>Streams that come through the RemoteHub Broker (<see cref="PeerTransport"/>); null = TCP only.</summary>
    internal PeerTransport? Peers { get; init; }

    /// <summary>The policy for this connection (read again each time, so changes apply without a restart).</summary>
    public AgentPolicy CurrentPolicy() => Policy?.Invoke() ?? AgentPolicy.Load(Config);
}

public static class AgentHost
{
    public static string AgentVersion =>
        typeof(AgentHost).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    /// <summary>A new random id for each start of the agent (<see cref="AgentHealth.Instance"/>).</summary>
    public static readonly string Instance = Guid.NewGuid().ToString("N");

    /// <summary>Whether any program listens on TCP <paramref name="port"/>, on any address and either IP version.</summary>
    internal static bool PortInUse(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(l => l.Port == port);

    public static WebApplication Build(AgentHostOptions o, string[]? args = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args ?? []);
        builder.Logging.ClearProviders();
        builder.Services.AddWindowsService(w => w.ServiceName = "RemoteHubAgent"); // no effect when run from a console
        // Windows lets the agent's dual-mode socket bind next to another program's ordinary IPv4 listener on the same port,
        // and connections then go to either of the two (A-INST-25). A port someone listens on fails the start instead
        // (Kestrel reports it as "address already in use"; Program logs "could not start").
        builder.WebHost.UseSockets(s => s.CreateBoundListenSocket = endpoint =>
        {
            if (endpoint is IPEndPoint { Port: > 0 } ip && PortInUse(ip.Port))
                throw new SocketException((int)SocketError.AddressAlreadyInUse);
            return SocketTransportOptions.CreateDefaultBoundListenSocket(endpoint);
        });
        // Kestrel picks the transport whose selector can bind the endpoint: sockets for the port, this one for the broker.
        if (o.Peers is { } peers) builder.Services.AddSingleton<Microsoft.AspNetCore.Connections.IConnectionListenerFactory>(peers);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            void Https(ListenOptions l)
            {
                l.Protocols = HttpProtocols.Http1;
                l.UseHttps(new Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions
                {
                    ServerCertificate = o.Certificate,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                });
            }
            if (o.BindAddress is null) k.ListenAnyIP(o.Config.ListenPort, Https);
            else k.Listen(o.BindAddress, o.Config.ListenPort, Https);
            if (o.Peers is not null) k.Listen(PeerEndPoint.Instance, Https);
        });
        var app = builder.Build();
        var gate = new SignInGate();
        var sessions = 0;

        // A ping every 30 s; no answer within 30 s ends the connection, so a silently dropped client doesn't keep its
        // session (and its PowerShell) until TCP gives up.
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30), KeepAliveTimeout = TimeSpan.FromSeconds(30) });

        // RequestDelegates only (no reflection-based parameter binding): the agent is published trimmed.
        app.MapGet(AgentProtocol.HealthPath, (RequestDelegate)(ctx =>
        {
            var policy = o.CurrentPolicy();
            List<string> features = [];
            if (policy.PowerShellEnabled) features.Add(AgentFeatures.PowerShell);
            if (o.Desktop is not null && policy.DesktopEnabled) features.Add(AgentFeatures.Desktop);
            if (policy.TunnelEnabled) features.Add(AgentFeatures.Tunnel);
            var health = new AgentHealth("RemoteHub Agent", AgentVersion, AgentProtocol.Version, o.Config.DeviceId, [.. features],
                policy.Managed.Count > 0 ? [.. policy.Managed] : null, Instance);
            return ctx.Response.WriteAsJsonAsync(health, AgentJson.Default.AgentHealth);
        }));

        // The claim (docs/agent-spec.md §5.5): which device this is and the proof that it was enrolled with RemoteHub's
        // secret, bound to this certificate and the caller's nonce. No credentials – it tells nothing a caller without the
        // secret can use. 404 on a device enrolled with a format-1 file (no secret: RemoteHub pinned it at creation).
        var fingerprint = AgentProtocol.Fingerprint(o.Certificate.RawData);
        app.MapGet(EnrollmentProof.Path, (RequestDelegate)(ctx =>
        {
            var nonce = ctx.Request.Query["nonce"].ToString();
            if (o.Config.EnrollmentSecret is not { } secret || !AgentEnrollment.IsValidSecret(secret) || o.Config.EnrollmentKeyId is not { Length: > 0 } keyId)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }
            if (!EnrollmentProof.IsValidNonce(nonce))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Task.CompletedTask;
            }
            var (computer, dns) = Names();
            var claim = EnrollmentProof.Answer(secret, keyId, o.Config.DeviceId, fingerprint, computer, dns, nonce);
            return ctx.Response.WriteAsJsonAsync(claim, AgentEnrollmentJson.Default.AgentClaim);
        }));

        // The policy for this connection; one that can't be read allows nothing (and says so in the log). An invalid value
        // keeps its default and is logged once per service run.
        var reported = new ConcurrentDictionary<string, byte>();
        AgentPolicy Policy()
        {
            var policy = o.CurrentPolicy();
            if (policy.LoadError is { } error) o.Log.Write($"The policy ({AgentPolicy.RegistryPath}) can't be read – access refused: {error}");
            foreach (var problem in policy.Problems)
                if (reported.TryAdd(problem, 0)) o.Log.Write($"Policy {problem} – ignored, the default applies");
            return policy;
        }

        // The session limit: policy MaxSessions, else agent.json (read for each new session).
        bool TryStartSession(AgentPolicy policy, ShellIdentity identity, string source, HttpContext ctx)
        {
            if (Interlocked.Increment(ref sessions) <= policy.MaxSessions) return true;
            Interlocked.Decrement(ref sessions);
            o.Log.Write($"Refused {identity.Account} from {source}: session limit ({policy.MaxSessions}) reached");
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return false;
        }

        // Checks the request, the sign-in and the right (TLS to the pinned certificate is done). Null = refused, status code set.
        async Task<ShellIdentity?> SignIn(HttpContext ctx, IReadOnlyList<string> groups, string source)
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return null; }
            // Attempts still running count against the limit, so parallel requests can't try more passwords than one after another.
            if (!gate.TryBegin(source))
            {
                o.Log.Audit(AuditEvent.SignInRefused, $"Refused {source}: too many failed sign-ins");
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return null;
            }
            try
            {
                if (!TryBasic(ctx.Request.Headers.Authorization, out var user, out var password))
                {
                    ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"RemoteHub Agent\"";
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return null;
                }
                // Only a few Windows sign-ins at a time, from all addresses together (they cost the device and its domain controller).
                if (!await gate.WaitForTurnAsync(ctx.RequestAborted))
                {
                    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return null;
                }
                AuthResult auth;
                try { auth = await Task.Run(() => o.Authenticator.Authenticate(user, password, groups)); }
                finally { gate.EndTurn(); }
                password = "";
                if (auth.Outcome != AuthOutcome.Ok)
                {
                    if (auth.Outcome != AuthOutcome.Error) gate.Failed(source);
                    o.Log.Audit(AuditEvent.SignInRefused, $"Refused {user} from {source}: {auth.Reason}");
                    ctx.Response.StatusCode = SignInRefusal.HttpStatus(auth.Outcome);
                    return null;
                }
                gate.Succeeded(source);
                return auth.Identity!;
            }
            finally { gate.End(source); }
        }

        app.Map(AgentProtocol.PowerShellPath, (RequestDelegate)(async ctx =>
        {
            var source = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var policy = Policy();
            if (!policy.PowerShellEnabled)
            {
                // Before the sign-in, like the desktop and the tunnel: nothing is tried. RemoteHub explains it from /health.
                o.Log.Write($"Refused PowerShell from {source}: turned off on this device (policy PowerShellEnabled)");
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            using var identity = await SignIn(ctx, policy.Groups(AgentRight.PowerShell), source);
            if (identity is null) return;
            if (!TryStartSession(policy, identity, source, ctx)) return;
            try
            {
                IShellProcess shell;
                try { shell = o.Launcher.Start(identity); }
                catch (Exception ex)
                {
                    o.Log.Write($"Could not start PowerShell for {identity.Account}: {ex.Message}");
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    return;
                }
                using (shell)
                {
                    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                    var started = DateTime.UtcNow;
                    o.Log.Audit(AuditEvent.PowerShellStarted, $"Session started: {identity.Account} from {source} (PowerShell PID {shell.ProcessId})");
                    var reason = await Tunnel.RunAsync(ws, shell, ctx.RequestAborted);
                    o.Log.Audit(AuditEvent.PowerShellEnded, $"Session ended: {identity.Account} from {source} after {(DateTime.UtcNow - started):hh\\:mm\\:ss} ({reason})");
                }
            }
            finally { Interlocked.Decrement(ref sessions); }
        }));

        app.Map(DesktopProtocol.Path, (RequestDelegate)(async ctx =>
        {
            var source = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var policy = Policy();
            if (o.Desktop is null || !policy.DesktopEnabled)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound; // RemoteHub explains: turned off on this device
                return;
            }
            using var identity = await SignIn(ctx, [.. policy.Groups(AgentRight.Desktop), .. policy.Groups(AgentRight.DesktopViewOnly)], source);
            if (identity is null) return;
            if (!TryStartSession(policy, identity, source, ctx)) return;
            try
            {
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                await new DesktopEndpoint(o, identity, policy, source).RunAsync(ws, ctx.RequestAborted);
            }
            finally { Interlocked.Decrement(ref sessions); }
        }));

        // The device as a jump host: RemoteHub reaches other devices through it (policy TunnelEnabled/Targets/Ports, right Tunnel).
        app.Map(TunnelProtocol.Path, (RequestDelegate)(async ctx =>
        {
            var source = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var policy = Policy();
            if (!policy.TunnelEnabled)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound; // RemoteHub explains: turned off on this device
                return;
            }
            using var identity = await SignIn(ctx, policy.Groups(AgentRight.Tunnel), source);
            if (identity is null) return;
            if (!TryStartSession(policy, identity, source, ctx)) return;
            try
            {
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                await new TunnelEndpoint(o.Log, identity.Account, source, Policy, o.TunnelNetwork).RunAsync(ws, ctx.RequestAborted);
            }
            finally { Interlocked.Decrement(ref sessions); }
        }));

        return app;
    }

    /// <summary>"Authorization: Basic base64(user:password)" – sent only inside TLS to a pinned certificate.</summary>
    /// <summary>The device's computer name and DNS name (with its domain, if it has one) – for RemoteHub's check that the claim comes from the host it asked.</summary>
    internal static (string Computer, string Dns) Names()
    {
        var computer = Environment.MachineName;
        try
        {
            var p = IPGlobalProperties.GetIPGlobalProperties();
            return (computer, string.IsNullOrEmpty(p.DomainName) ? p.HostName : $"{p.HostName}.{p.DomainName}");
        }
        catch { return (computer, computer); }
    }

    internal static bool TryBasic(string? header, out string user, out string password)
    {
        user = password = "";
        if (header is null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var colon = text.IndexOf(':');
            if (colon <= 0) return false;
            user = text[..colon];
            password = text[(colon + 1)..];
            return true;
        }
        catch (FormatException) { return false; }
    }
}

/// <summary>
/// Slows down password guessing per source address: after 5 failures within 5 minutes the address is refused for
/// 5 minutes. Attempts still running count as failures until they end, so parallel requests get no more tries than
/// sequential ones; and only a few Windows sign-ins run at once overall. (Windows' own account lockout policy applies on
/// top, since every attempt is a real sign-in.)
/// </summary>
internal sealed class SignInGate(int maxFailures = 5, TimeSpan? window = null, Func<DateTime>? clock = null, int maxParallel = 8)
{
    private const int PruneAbove = 1000;
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(5);
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly ConcurrentDictionary<string, (int Count, DateTime First, DateTime? BlockedUntil)> _failures = new();
    private readonly Dictionary<string, int> _running = [];
    private readonly SemaphoreSlim _turns = new(maxParallel, maxParallel);

    public bool IsBlocked(string source) =>
        _failures.TryGetValue(source, out var f) && f.BlockedUntil is { } until && until > _now();

    /// <summary>Starts an attempt from <paramref name="source"/>; false = refuse it (blocked, or the limit would be reached). End with <see cref="End"/>.</summary>
    public bool TryBegin(string source)
    {
        lock (_running)
        {
            if (IsBlocked(source)) return false;
            var running = _running.GetValueOrDefault(source);
            if (CurrentFailures(source) + running >= maxFailures) return false;
            _running[source] = running + 1;
            return true;
        }
    }

    public void End(string source)
    {
        lock (_running)
        {
            if (!_running.TryGetValue(source, out var n)) return;
            if (n <= 1) _running.Remove(source); else _running[source] = n - 1;
        }
    }

    /// <summary>Waits for one of the sign-in slots (false: the request was aborted or none became free in time).</summary>
    public async Task<bool> WaitForTurnAsync(CancellationToken ct)
    {
        try { return await _turns.WaitAsync(TimeSpan.FromSeconds(30), ct); }
        catch (OperationCanceledException) { return false; }
    }

    public void EndTurn() => _turns.Release();

    private int CurrentFailures(string source)
    {
        if (!_failures.TryGetValue(source, out var f)) return 0;
        var now = _now();
        return now - f.First > _window || f.BlockedUntil < now ? 0 : f.Count;
    }

    public void Failed(string source)
    {
        _failures.AddOrUpdate(source,
            _ => (1, _now(), maxFailures <= 1 ? _now() + _window : null),
            (_, f) =>
            {
                var now = _now();
                if (now - f.First > _window || f.BlockedUntil < now) f = (0, now, null);
                var count = f.Count + 1;
                return (count, f.First, count >= maxFailures ? now + _window : null);
            });
        if (_failures.Count > PruneAbove) Prune();
    }

    public void Succeeded(string source) => _failures.TryRemove(source, out _);

    /// <summary>Forgets addresses whose window and block are over (many source addresses, e.g. IPv6, must not pile up).</summary>
    private void Prune()
    {
        var now = _now();
        foreach (var (source, f) in _failures)
            if (now - f.First > _window && !(f.BlockedUntil > now)) _failures.TryRemove(source, out _);
    }

    internal int Tracked => _failures.Count;
}

/// <summary>Moves protocol lines between the WebSocket and PowerShell's standard input / output.</summary>
internal static class Tunnel
{
    public static async Task<string> RunAsync(WebSocket ws, IShellProcess shell, CancellationToken aborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        var toShell = Task.Run(() => ToShellAsync(ws, shell.Input, cts.Token));
        var fromShell = Task.Run(() => FromShellAsync(ws, shell.Output, cts.Token));
        var first = await Task.WhenAny(toShell, fromShell);
        // Nothing cancels either side on purpose before this point: a cancelled side means the connection dropped – the
        // keep-alive timeout ends the WebSocket's receive with an OperationCanceledException, and an aborted request
        // cancels both sides – whichever side noticed it first.
        var reason = first.IsFaulted ? "connection lost: " + first.Exception!.GetBaseException().Message
            : first.IsCanceled ? "connection lost"
            : first == toShell ? "closed by RemoteHub" : "PowerShell ended";
        cts.Cancel();
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, new CancellationTokenSource(2000).Token);
        }
        catch { /* already gone */ }
        try { await Task.WhenAll(toShell, fromShell).WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
        return reason;
    }

    private static async Task ToShellAsync(WebSocket ws, Stream input, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, r.Count);
            if (message.Length > AgentProtocol.MaxMessageBytes) throw new InvalidDataException("Message too large.");
            if (!r.EndOfMessage) continue;
            if (message.Length == 0) continue; // an empty line is a protocol error for PowerShell's server
            message.WriteByte((byte)'\n');
            await input.WriteAsync(message.GetBuffer().AsMemory(0, (int)message.Length), ct);
            await input.FlushAsync(ct);
            message.SetLength(0);
        }
    }

    private static async Task FromShellAsync(WebSocket ws, Stream output, CancellationToken ct)
    {
        using var reader = new StreamReader(output, new UTF8Encoding(false));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            // Protocol lines are XML elements; anything else is PowerShell's error output (same pipe) – not for the client.
            if (!line.StartsWith('<')) continue;
            await ws.SendAsync(Encoding.UTF8.GetBytes(line), WebSocketMessageType.Text, true, ct);
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(AgentHealth))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
internal partial class AgentJson : System.Text.Json.Serialization.JsonSerializerContext;
