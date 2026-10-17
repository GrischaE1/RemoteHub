using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management.Automation;
using System.Net.Sockets;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Remoting;

/// <summary>
/// Jump hosts in the lab: every command that reaches a device takes
///   --jump <address> --jump-pin <sha256> --jump-user .. --jump-password .. [--jump-port n]
/// and then goes through that device's RemoteHub Agent exactly as RemoteHub does for a connection placed under a jump
/// host (AgentEndpoint.Via / PsTarget.Via = the JumpRoute). The "jump" command checks the tunnel itself:
///   jump --kind open --host <target> --port n [--seconds s]     JumpTunnels.OpenAsync: connected, or the refusal (reason,
///                                                              message), the jump host's own address towards the target
///   jump --kind local --host <target> --port n                 JumpTunnels.LocalAddressAsync (the firewall scope RemoteHub offers)
///   jump --kind rdp --host <target> [--port 3389]              the target's RDP certificate through a forwarder (ServerCertificates.RdpAsync)
///   jump --kind forward --host <target> --port n               JumpForwarders.Open: a local port; then another local process
///                                                              connects to it (must be refused) and this process connects (must work)
///   jump --kind streams --host <target> --port n --count n     n tunnel connections at once (MaxTunnelStreams)
///   jump --kind direct --host <target> --port n                a plain TCP connect from this PC (J0: must fail)
///   jump --kind raw --host <target> --port n --case ..          frames sent by hand on /v1/tunnel (J-29), see Raw
/// </summary>
internal static class LabJump
{
    /// <summary>The jump route the options name, or null.</summary>
    public static JumpRoute? Route(Dictionary<string, string> o)
    {
        if (!o.TryGetValue("jump", out var host) || host.Length == 0) return null;
        var secure = new SecureString();
        foreach (var c in o["jump-password"]) secure.AppendChar(c);
        var port = int.Parse(o.GetValueOrDefault("jump-port") ?? "58732");
        return new JumpRoute("jump " + host, host, new AgentEndpoint(port, o["jump-pin"]), new PSCredential(o["jump-user"], secure));
    }

    /// <summary>The agent endpoint for --port / --pin, through the jump host when one is given.</summary>
    public static AgentEndpoint Endpoint(Dictionary<string, string> o, int port, string pin) => new(port, pin, Route(o));

    public static async Task<object> Run(Dictionary<string, string> o)
    {
        var host = o["host"];
        var port = int.Parse(o.GetValueOrDefault("port") ?? "3389");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(int.Parse(o.GetValueOrDefault("timeout") ?? "60")));
        if (o["kind"] == "direct")
        {
            using var tcp = new TcpClient();
            try
            {
                await tcp.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(10));
                return new { connected = true };
            }
            catch (Exception ex) { return new { connected = false, reason = ex.GetBaseException().Message }; }
        }
        var route = Route(o) ?? throw new ArgumentException("--jump .. is needed");
        try
        {
            switch (o["kind"])
            {
                case "open":
                {
                    var sw = Stopwatch.StartNew();
                    await using var s = await JumpTunnels.OpenAsync(route, host, port, cts.Token);
                    var seconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
                    await Task.Delay(TimeSpan.FromSeconds(int.Parse(o.GetValueOrDefault("seconds") ?? "0")), cts.Token);
                    return new { connected = true, local = s.LocalAddress, seconds };
                }
                case "local":
                    return new { local = await JumpTunnels.LocalAddressAsync(route, host, port, cts.Token) };
                case "rdp":
                {
                    await using var s = await JumpTunnels.OpenAsync(route, host, port, cts.Token);
                    var cert = await ServerCertificates.RdpAsync(s, host, cts.Token);
                    return new { connected = true, certificate = cert is { } c ? new { subject = c.Certificate.Subject, pin = ServerCertificates.Pin(c.Certificate), valid = c.Valid } : null };
                }
                case "forward":
                {
                    using var forward = JumpForwarders.Open(route, host, port);
                    // another program on this PC (powershell's TcpClient) - the forwarder only serves RemoteHub's own process
                    var other = OtherProcessConnects(forward.Port);
                    string own;
                    using (var tcp = new TcpClient())
                    {
                        await tcp.ConnectAsync("127.0.0.1", forward.Port, cts.Token);
                        var buffer = new byte[1];
                        // a refused or failed connection is closed at once; a working one stays open
                        var read = tcp.GetStream().ReadAsync(buffer, 0, 1, cts.Token);
                        own = await Task.WhenAny(read, Task.Delay(3000, cts.Token)) == read ? $"closed ({read.Result} bytes)" : "open";
                    }
                    return new { localPort = forward.Port, other, own, forwardError = forward.TakeError() };
                }
                case "raw":
                    return await Raw(route, host, port, o, cts.Token);
                case "streams":
                {
                    var count = int.Parse(o["count"]);
                    var streams = new List<TunnelStream>();
                    var results = new List<object>();
                    try
                    {
                        for (var i = 0; i < count; i++)
                        {
                            try { streams.Add(await JumpTunnels.OpenAsync(route, host, port, cts.Token)); results.Add(new { i, connected = true }); }
                            catch (JumpHostException ex) { results.Add(new { i, connected = false, reason = ex.Reason, message = ex.Message }); }
                        }
                    }
                    finally { foreach (var s in streams) await s.DisposeAsync(); }
                    return new { results };
                }
            }
            throw new ArgumentException("unknown kind " + o["kind"]);
        }
        catch (JumpHostException ex) { return new { connected = false, reason = ex.Reason, message = ex.Message, local = ex.Local }; }
        finally { JumpForwarders.CloseAll(); JumpTunnels.CloseAll(); }
    }

    // Frames sent by hand on /v1/tunnel (J-29) - what RemoteHub never sends:
    //   window    Open, then 64 KB Data frames without waiting for credit, until Reset or --bytes (default 1 MB)
    //   text      a text message;  garbled  a 2-byte binary frame;  unknown  frame type 99;  duplicate  Open twice with id 1
    //   drain     Open, then Close, then wait up to --wait s (default 75) for the agent's Reset
    // Answers opened, reset, resetAfterSeconds, ended (the tunnel closed or aborted), closeStatus, the frames received.
    private static async Task<object> Raw(JumpRoute route, string host, int port, Dictionary<string, string> o, CancellationToken outer)
    {
        // its own time limit: "drain" waits longer than the command's default 60 s
        using var rawLimit = new CancellationTokenSource(TimeSpan.FromSeconds(int.Parse(o.GetValueOrDefault("wait") ?? "75") + 60));
        var ct = rawLimit.Token;
        var expected = RemoteHub.Agent.AgentProtocol.NormalizeFingerprint(route.Endpoint.Fingerprint);
        using var ws = new System.Net.WebSockets.ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && RemoteHub.Agent.AgentProtocol.Fingerprint(cert.GetRawCertData()) == expected;
        var cred = route.Credential.GetNetworkCredential();
        var user = string.IsNullOrEmpty(cred.Domain) ? cred.UserName : cred.Domain + "\\" + cred.UserName;
        ws.Options.SetRequestHeader("Authorization", "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(user + ":" + cred.Password)));
        await ws.ConnectAsync(new Uri($"wss://{route.Host}:{route.Endpoint.Port}{RemoteHub.Agent.TunnelProtocol.Path}"), ct);

        var frames = new List<object>();
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reset = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receive = Task.Run(async () =>
        {
            var buffer = new byte[RemoteHub.Agent.TunnelProtocol.MaxFrame + 16];
            try
            {
                while (ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buffer, ct);
                    if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                    if (!RemoteHub.Agent.TunnelProtocol.TryRead(buffer.AsSpan(0, r.Count), out var type, out var stream)) continue;
                    var payload = r.Count > 5 && type != RemoteHub.Agent.TunnelMessage.Data ? System.Text.Encoding.UTF8.GetString(buffer, 5, r.Count - 5) : null;
                    lock (frames) frames.Add(new { type = type.ToString(), stream, payload, bytes = r.Count - 5 });
                    if (type is RemoteHub.Agent.TunnelMessage.Opened) opened.TrySetResult(true);
                    if (type is RemoteHub.Agent.TunnelMessage.Refused) opened.TrySetResult(false);
                    if (type is RemoteHub.Agent.TunnelMessage.Reset) reset.TrySetResult(true);
                }
            }
            catch (Exception) { /* aborted by the agent or the timeout */ }
        }, ct);

        Task Send(byte[] frame) => ws.SendAsync(frame, System.Net.WebSockets.WebSocketMessageType.Binary, true, ct);
        byte[] Open(uint id) => RemoteHub.Agent.TunnelProtocol.Json(RemoteHub.Agent.TunnelMessage.Open, id, new RemoteHub.Agent.TunnelOpen(host, port), RemoteHub.Agent.TunnelJson.Default.TunnelOpen);
        bool? wasOpened = null;
        double? resetAfter = null;
        string? sendError = null;
        var sw = Stopwatch.StartNew();
        try
        {
            switch (o.GetValueOrDefault("case") ?? "")
            {
                case "window":
                {
                    await Send(Open(1));
                    wasOpened = await opened.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                    var chunk = new byte[RemoteHub.Agent.TunnelProtocol.MaxData];
                    var limit = long.Parse(o.GetValueOrDefault("bytes") ?? (1024 * 1024).ToString());
                    sw.Restart();
                    for (long sent = 0; sent < limit && !reset.Task.IsCompleted && ws.State == System.Net.WebSockets.WebSocketState.Open; sent += chunk.Length)
                        await Send(RemoteHub.Agent.TunnelProtocol.Frame(RemoteHub.Agent.TunnelMessage.Data, 1, chunk));
                    if (await Task.WhenAny(reset.Task, Task.Delay(TimeSpan.FromSeconds(10), ct)) == reset.Task) resetAfter = Math.Round(sw.Elapsed.TotalSeconds, 2);
                    break;
                }
                case "text":
                    await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes("hello"), System.Net.WebSockets.WebSocketMessageType.Text, true, ct);
                    break;
                case "garbled":
                    await Send([1, 0]);
                    break;
                case "unknown":
                    await Send(RemoteHub.Agent.TunnelProtocol.Frame((RemoteHub.Agent.TunnelMessage)99, 1));
                    break;
                case "duplicate":
                    await Send(Open(1));
                    await Send(Open(1));
                    break;
                case "drain":
                {
                    await Send(Open(1));
                    wasOpened = await opened.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                    sw.Restart();
                    await Send(RemoteHub.Agent.TunnelProtocol.Frame(RemoteHub.Agent.TunnelMessage.Close, 1));
                    var wait = TimeSpan.FromSeconds(int.Parse(o.GetValueOrDefault("wait") ?? "75"));
                    if (await Task.WhenAny(reset.Task, Task.Delay(wait, ct)) == reset.Task) resetAfter = Math.Round(sw.Elapsed.TotalSeconds, 2);
                    break;
                }
                default: throw new ArgumentException("raw needs --case window|text|garbled|unknown|duplicate|drain");
            }
        }
        catch (Exception ex) when (ex is not ArgumentException) { sendError = ex.GetBaseException().Message; }
        // the agent ends the tunnel on a protocol error: wait a moment for it
        await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(5), ct));
        var ended = ws.State != System.Net.WebSockets.WebSocketState.Open;
        if (!ended) try { await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "lab", CancellationToken.None); } catch { /* gone */ }
        lock (frames) return new { opened = wasOpened, reset = reset.Task.IsCompleted, resetAfterSeconds = resetAfter, ended, closeStatus = ws.CloseStatus?.ToString(), sendError, frames = frames.ToArray() };
    }

    // Answers what another local process gets from the forwarder: "refused" (closed without data), "open" or the error.
    private static string OtherProcessConnects(int port)
    {
        var script = $"$c = New-Object Net.Sockets.TcpClient; try {{ $c.Connect('127.0.0.1', {port}); $s = $c.GetStream(); $s.ReadTimeout = 3000; " +
                     "try { $n = $s.Read((New-Object byte[] 1), 0, 1); if ($n -eq 0) { 'closed' } else { 'data' } } catch { if ($_.Exception.InnerException -is [IO.IOException] -and $c.Connected) { 'open' } else { 'closed' } } } " +
                     "catch { 'refused' } finally { $c.Close() }";
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"" + script.Replace("\"", "\\\"") + "\"")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(20000);
        return output;
    }
}
