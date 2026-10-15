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
