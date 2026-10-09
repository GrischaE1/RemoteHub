using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Server.Kestrel.Core;

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
    /// <summary>The policy for this connection (read again each time, so changes apply without a restart).</summary>
    public AgentPolicy CurrentPolicy() => Policy?.Invoke() ?? AgentPolicy.Load(Config);
}

public static class AgentHost
{
    public static string AgentVersion =>
        typeof(AgentHost).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public static WebApplication Build(AgentHostOptions o, string[]? args = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args ?? []);
        builder.Logging.ClearProviders();
        builder.Services.AddWindowsService(w => w.ServiceName = "RemoteHubAgent"); // no effect when run from a console
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
        });
        var app = builder.Build();
        var gate = new SignInGate();
        var sessions = 0;

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        // RequestDelegates only (no reflection-based parameter binding): the agent is published trimmed.
        app.MapGet(AgentProtocol.HealthPath, (RequestDelegate)(ctx =>
        {
            string[] features = o.Desktop is not null && o.CurrentPolicy().DesktopEnabled ? [AgentFeatures.PowerShell, AgentFeatures.Desktop] : [AgentFeatures.PowerShell];
            var health = new AgentHealth("RemoteHub Agent", AgentVersion, AgentProtocol.Version, o.Config.DeviceId, features);
            return ctx.Response.WriteAsJsonAsync(health, AgentJson.Default.AgentHealth);
        }));

        // Checks the request, the sign-in and the right (TLS to the pinned certificate is done). Null = refused, status code set.
        async Task<ShellIdentity?> SignIn(HttpContext ctx, IReadOnlyList<string> groups, string source)
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return null; }
            if (gate.IsBlocked(source))
            {
                o.Log.Audit(AuditEvent.SignInRefused, $"Refused {source}: too many failed sign-ins");
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return null;
            }
            if (!TryBasic(ctx.Request.Headers.Authorization, out var user, out var password))
            {
                ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"RemoteHub Agent\"";
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return null;
            }

            var auth = await Task.Run(() => o.Authenticator.Authenticate(user, password, groups));
            password = "";
            if (auth.Outcome != AuthOutcome.Ok)
            {
                if (auth.Outcome != AuthOutcome.Error) gate.Failed(source);
                o.Log.Audit(AuditEvent.SignInRefused, $"Refused {user} from {source}: {auth.Reason}");
                ctx.Response.StatusCode = auth.Outcome switch
                {
                    AuthOutcome.BadCredentials => StatusCodes.Status401Unauthorized,
                    AuthOutcome.NotAllowed => StatusCodes.Status403Forbidden,
                    _ => StatusCodes.Status500InternalServerError,
                };
                return null;
            }
            gate.Succeeded(source);
            return auth.Identity!;
        }

        app.Map(AgentProtocol.PowerShellPath, (RequestDelegate)(async ctx =>
        {
            var source = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            using var identity = await SignIn(ctx, o.CurrentPolicy().Groups(AgentRight.PowerShell), source);
            if (identity is null) return;
            if (Interlocked.Increment(ref sessions) > o.Config.MaxSessions)
            {
                Interlocked.Decrement(ref sessions);
                o.Log.Write($"Refused {identity.Account} from {source}: session limit ({o.Config.MaxSessions}) reached");
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
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
            var policy = o.CurrentPolicy();
            if (o.Desktop is null || !policy.DesktopEnabled)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound; // RemoteHub explains: turned off on this device
                return;
            }
            using var identity = await SignIn(ctx, [.. policy.Groups(AgentRight.Desktop), .. policy.Groups(AgentRight.DesktopViewOnly)], source);
            if (identity is null) return;
            if (Interlocked.Increment(ref sessions) > o.Config.MaxSessions)
            {
                Interlocked.Decrement(ref sessions);
                o.Log.Write($"Refused {identity.Account} from {source}: session limit ({o.Config.MaxSessions}) reached");
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            try
            {
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                await new DesktopEndpoint(o, identity, policy, source).RunAsync(ws, ctx.RequestAborted);
            }
            finally { Interlocked.Decrement(ref sessions); }
        }));

        return app;
    }

    /// <summary>"Authorization: Basic base64(user:password)" – sent only inside TLS to a pinned certificate.</summary>
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
/// 5 minutes. (Windows' own account lockout policy applies on top, since every attempt is a real sign-in.)
/// </summary>
internal sealed class SignInGate(int maxFailures = 5, TimeSpan? window = null, Func<DateTime>? clock = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(5);
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly ConcurrentDictionary<string, (int Count, DateTime First, DateTime? BlockedUntil)> _failures = new();

    public bool IsBlocked(string source) =>
        _failures.TryGetValue(source, out var f) && f.BlockedUntil is { } until && until > _now();

    public void Failed(string source) => _failures.AddOrUpdate(source,
        _ => (1, _now(), maxFailures <= 1 ? _now() + _window : null),
        (_, f) =>
        {
            var now = _now();
            if (now - f.First > _window || f.BlockedUntil < now) f = (0, now, null);
            var count = f.Count + 1;
            return (count, f.First, count >= maxFailures ? now + _window : null);
        });

    public void Succeeded(string source) => _failures.TryRemove(source, out _);
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
        var reason = first == toShell ? "closed by RemoteHub" : "PowerShell ended";
        if (first.IsFaulted) reason = "connection lost: " + first.Exception!.GetBaseException().Message;
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
