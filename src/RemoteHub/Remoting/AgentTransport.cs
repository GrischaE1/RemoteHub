using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Management.Automation.Remoting;
using System.Management.Automation.Remoting.Client;
using System.Management.Automation.Runspaces;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using RemoteHub.Agent;

namespace RemoteHub.Remoting;

/// <summary>
/// Where the RemoteHub Agent of a device listens, and the device certificate it must present. <paramref name="Via"/>: the
/// jump host it is reached through, if any.
/// </summary>
public sealed record AgentEndpoint(int Port, string Fingerprint, JumpRoute? Via = null);

/// <summary>
/// A PowerShell session through the RemoteHub Agent instead of WinRM. Everything above the transport – runspaces,
/// file transfer, registry, processes, services, the console – works unchanged.
/// <para>
/// Security: TLS to the agent is accepted only if the device certificate matches the pinned fingerprint (no CA, no
/// trust on first use); only then are the credentials sent. The agent checks them as a Windows sign-in on the device.
/// </para>
/// </summary>
public sealed class AgentConnectionInfo : RunspaceConnectionInfo
{
    public AgentConnectionInfo(string host, AgentEndpoint endpoint, PSCredential? credential)
    {
        ComputerName = host;
        Endpoint = endpoint;
        Credential = credential!;
        OpenTimeout = 20_000;
    }

    public AgentEndpoint Endpoint { get; }
    public override string ComputerName { get; set; }
    public override PSCredential Credential { get; set; }
    public override AuthenticationMechanism AuthenticationMechanism { get; set; } = AuthenticationMechanism.Basic;
    public override string CertificateThumbprint { get; set; } = "";

    public Uri Uri => UriFor(AgentProtocol.PowerShellPath);

    public Uri UriFor(string path)
    {
        var host = ComputerName.Trim();
        if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]";
        return new Uri($"wss://{host}:{Endpoint.Port}{path}");
    }

    public override RunspaceConnectionInfo Clone() => new AgentConnectionInfo(ComputerName, Endpoint, Credential) { OpenTimeout = OpenTimeout };

    public override BaseClientSessionTransportManager CreateClientSessionTransportManager(Guid instanceId, string sessionName, PSRemotingCryptoHelper cryptoHelper) =>
        new AgentClientTransport(instanceId, this, cryptoHelper);
}

/// <summary>Carries PowerShell's out-of-process protocol lines over one WebSocket.</summary>
internal sealed class AgentClientTransport(Guid instanceId, AgentConnectionInfo info, PSRemotingCryptoHelper crypto)
    : ClientSessionTransportManagerBase(instanceId, crypto)
{
    private ClientWebSocket? _ws;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _closing;

    /// <summary>
    /// The out-of-process protocol waits for an acknowledgement after every fragment, so with the default 32 KB
    /// fragments throughput is capped at 32 KB per round trip (about 1 MB/s at 30 ms). Larger fragments lift that cap.
    /// </summary>
    internal const int FragmentSize = 1024 * 1024;

    public override void CreateAsync()
    {
        SetFragmentSize(this, FragmentSize);
        _ws = Connect(info, _cts.Token);
        SetMessageWriter(new WebSocketLineWriter(_ws, _cts.Token));
        _ = Task.Run(ReadLoop);
        SendOneItem();
    }

    /// <summary>Opens the pinned TLS connection and signs in (blocking). Throws with a readable message.</summary>
    internal static ClientWebSocket Connect(AgentConnectionInfo info, CancellationToken ct, string path = AgentProtocol.PowerShellPath)
    {
        var expected = AgentProtocol.NormalizeFingerprint(info.Endpoint.Fingerprint);
        if (expected.Length == 0) throw new PSRemotingTransportException("No device certificate fingerprint is stored for this connection's RemoteHub Agent.");
        string? seen = null;
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30); // no answer to a ping: the connection is gone (VPN drop)
        ws.Options.CollectHttpResponseDetails = true;
        RemoteCertificateValidationCallback pinned = (_, cert, _, _) =>
        {
            // The pin is the trust anchor: name and chain don't matter (devices are reached by IP or alias, certs are self-issued).
            seen = cert is null ? "" : AgentProtocol.Fingerprint(cert.GetRawCertData());
            return seen == expected;
        };
        // Through a jump host: the socket is a stream of its tunnel; TLS to the pinned certificate runs inside it, end to end.
        var via = info.Endpoint.Via;
        if (via is null) ws.Options.RemoteCertificateValidationCallback = pinned;
        if (info.Credential is { } c)
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.UserName}:{c.GetNetworkCredential().Password}"));
            ws.Options.SetRequestHeader("Authorization", "Basic " + basic);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(info.OpenTimeout > 0 ? info.OpenTimeout : 20_000);
        if (via is not null) timeout.CancelAfter(Math.Max(info.OpenTimeout, 0) + 30_000); // signing in at the jump host and its connect come first
        try
        {
            if (via is null) ws.ConnectAsync(info.UriFor(path), timeout.Token).GetAwaiter().GetResult();
            else
            {
                // Disposed afterwards: an upgraded connection has left its pool; a refused one (401, 404) must not stay open in it.
                using var invoker = new HttpMessageInvoker(ThroughJumpHost(via, pinned));
                ws.ConnectAsync(info.UriFor(path), invoker, timeout.Token).GetAwaiter().GetResult();
            }
            return ws;
        }
        catch (Exception ex)
        {
            var status = ws.HttpStatusCode;
            ws.Dispose();
            if (JumpError(ex) is { } jump) throw new AgentTransportException(jump.Message, jump);
            throw new AgentTransportException(AgentErrors.Explain(ex, status, seen, expected, info, path), ex);
        }
    }

    /// <summary>An HTTP handler whose connections are streams through <paramref name="via"/> (for the agent's WebSocket and health check).</summary>
    internal static SocketsHttpHandler ThroughJumpHost(JumpRoute via, RemoteCertificateValidationCallback validate) => new()
    {
        ConnectCallback = async (context, token) => await JumpTunnels.OpenAsync(via, context.DnsEndPoint.Host, context.DnsEndPoint.Port, token).ConfigureAwait(false),
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = validate },
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
    };

    /// <summary>The jump host's own error somewhere inside <paramref name="ex"/> (HttpClient and ClientWebSocket wrap it).</summary>
    internal static JumpHostException? JumpError(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is JumpHostException jump) return jump;
        return null;
    }

    private async Task ReadLoop()
    {
        var ws = _ws!;
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, r.Count);
                if (message.Length > AgentProtocol.MaxMessageBytes) throw new InvalidDataException("Message from the agent too large.");
                if (!r.EndOfMessage) continue;
                if (message.Length > 0) HandleDataReceived(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                message.SetLength(0);
            }
            if (!_closing) Lost("the RemoteHub Agent closed the session");
        }
        catch (Exception ex) when (!_closing)
        {
            Lost(ex.GetBaseException().Message);
        }
        catch { /* closing */ }
    }

    /// <summary>The SDK keeps the fragment size internal; RemoteHub pins the SDK version and a test checks this still works.</summary>
    internal static bool SetFragmentSize(BaseTransportManager transport, int size)
    {
        try
        {
            var fragmentor = typeof(BaseTransportManager).GetProperty("Fragmentor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(transport);
            var field = fragmentor?.GetType().GetField("_fragmentSize", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field is null) return false;
            field.SetValue(fragmentor, size);
            return true;
        }
        catch { return false; } // keep the default: slower, still correct
    }

    private void Lost(string why) =>
        RaiseErrorHandler(new TransportErrorOccuredEventArgs(
            new PSRemotingTransportException("Connection to the RemoteHub Agent lost: " + why), TransportMethodEnum.ReceiveShellOutputEx));

    protected override void CleanupConnection()
    {
        _closing = true;
        var ws = _ws;
        _ws = null;
        if (ws is null) return;
        try
        {
            if (ws.State == WebSocketState.Open)
                ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, new CancellationTokenSource(2000).Token).Wait(2000);
        }
        catch { /* already gone */ }
        _cts.Cancel();
        ws.Dispose();
    }
}

/// <summary>Each protocol line becomes one WebSocket text message.</summary>
internal sealed class WebSocketLineWriter(WebSocket ws, CancellationToken ct) : TextWriter
{
    private readonly StringBuilder _line = new();
    private readonly object _lock = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_lock)
        {
            if (value == '\n') Send();
            else if (value != '\r') _line.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        lock (_lock)
            foreach (var ch in value)
            {
                if (ch == '\n') Send();
                else if (ch != '\r') _line.Append(ch);
            }
    }

    public override void WriteLine(string? value)
    {
        lock (_lock)
        {
            _line.Append(value);
            Send();
        }
    }

    private void Send()
    {
        if (_line.Length == 0) return; // PowerShell's server treats an empty line as a protocol error
        var bytes = Encoding.UTF8.GetBytes(_line.ToString());
        _line.Clear();
        ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).GetAwaiter().GetResult();
    }
}

/// <summary>
/// A refused or failed connection whose message is already RemoteHub's readable text (AgentErrors), so it is shown as
/// it is – also when it doesn't name the agent, like PowerShellEnabled = 0.
/// </summary>
internal sealed class AgentTransportException(string message, Exception inner) : PSRemotingTransportException(message, inner);

internal static class AgentErrors
{
    public static string Explain(Exception ex, HttpStatusCode status, string? seenFingerprint, string expected, AgentConnectionInfo info, string path = AgentProtocol.PowerShellPath)
    {
        var where = $"{info.ComputerName}:{info.Endpoint.Port}";
        var desktop = path == DesktopProtocol.Path;
        if (path == TunnelProtocol.Path && status is HttpStatusCode.NotFound)
            return $"The RemoteHub Agent on {where} can't be used as a jump host: jump connections are turned off by a policy on the device " +
                   "(TunnelEnabled), or the agent there is older than this RemoteHub – update it (right-click the connection → Update RemoteHub Agent).";
        if (path == TunnelProtocol.Path && status is HttpStatusCode.Forbidden)
            return $"The account may not use the RemoteHub Agent on {where} as a jump host: it must be in a group that the device's policy allows " +
                   "for jump connections (right Tunnel – by default the administrators).";
        if (seenFingerprint is { } seen && seen != expected)
            return $"The RemoteHub Agent on {where} presented a different certificate than the one stored for this device " +
                   $"(expected …{expected[^8..]}, got …{(seen.Length >= 8 ? seen[^8..] : "none")}). The connection was stopped before " +
                   "any credentials were sent. If the agent was reinstalled, update the device's fingerprint; otherwise someone may be " +
                   "intercepting the connection.";
        if (desktop && status is HttpStatusCode.NotFound)
            return $"The remote desktop isn't available through the RemoteHub Agent on {where}: it is turned off by a policy on the device, " +
                   "or the agent there is older than this RemoteHub – update the agent (right-click the connection → Update RemoteHub Agent).";
        if (desktop && status is HttpStatusCode.Forbidden)
            return $"The account may not use the remote desktop of the RemoteHub Agent on {where}: it must be in a group that the device's policy " +
                   "allows for the remote desktop (by default the administrators), and network sign-in must not be denied by policy.";
        return status switch
        {
            // Agents since 0.18.12: the device's policy PowerShellEnabled = 0 (older agents never answer 404 here).
            HttpStatusCode.NotFound => $"PowerShell, files, registry, services and processes are turned off on {where} by a policy on the device " +
                                       "(PowerShellEnabled). The remote desktop may still be available.",
            // Older agents answered 500 instead for an expired account and a password that must be changed (Windows errors 1793, 1907).
            HttpStatusCode.Unauthorized => $"The RemoteHub Agent on {where} rejected the sign-in: wrong user name or password, or the account is " +
                                           "disabled, locked or expired, or its password has expired or must be changed. Use an account of that " +
                                           "device or its domain. The reason is in the agent's log on the device.",
            HttpStatusCode.Forbidden => $"The account is not allowed to use the RemoteHub Agent on {where}: it must be an administrator there " +
                                        "(or a member of a group the agent was configured to allow), and network sign-in must not be denied by policy.",
            HttpStatusCode.TooManyRequests => $"The RemoteHub Agent on {where} refuses sign-ins from this PC for a few minutes after too many failed attempts.",
            HttpStatusCode.ServiceUnavailable => $"The RemoteHub Agent on {where} has reached its limit of simultaneous sessions.",
            HttpStatusCode.InternalServerError => $"The RemoteHub Agent on {where} could not check the sign-in with Windows (for example, no domain " +
                                                  "controller answered) or could not start PowerShell. Details are in the agent's log on the device.",
            _ when ex is OperationCanceledException => $"No answer from a RemoteHub Agent on {where} in time. Check that the agent is installed and running, " +
                                                       "and that a firewall allows the port.",
            _ => $"Could not connect to the RemoteHub Agent on {where}: {ex.GetBaseException().Message}",
        };
    }
}
