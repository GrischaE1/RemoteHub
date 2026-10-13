using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Agent;

/// <summary>
/// Raw checks of the agent's listener, below RemoteHub's client code - what an attacker or a broken client could send:
///   probe --kind health --host .. --port n --pin ..          status, headers and JSON fields of /v1/health
///   probe --kind http --host .. --port n                     plain HTTP to the TLS port (must fail)
///   probe --kind tls --host .. --port n --protocol tls12|tls13|tls11|tls10   handshake with only that protocol
///   probe --kind ws --host .. --port n --pin .. [--path /v1/powershell] [--auth basic|none|malformed]
///         [--user .. --password ..] [--count n] [--parallel 1] [--upgrade 0]
///       WebSocket upgrades (or a plain GET with --upgrade 0); answers the HTTP status of each attempt and the
///       WWW-Authenticate header. --pin wrong: the TLS pin is checked like RemoteHub does - nothing is sent on a mismatch.
///   probe --kind hold --host .. --port n --pin .. --user .. --password .. --count n [--seconds s]
///       opens n PowerShell WebSockets and keeps them open for s seconds; answers the status of each.
/// </summary>
internal static class LabProbes
{
    public static async Task<object> Run(Dictionary<string, string> o)
    {
        var host = o["host"];
        var port = int.Parse(o.GetValueOrDefault("port") ?? AgentProtocol.DefaultPort.ToString());
        switch (o["kind"])
        {
            case "health":
            {
                using var http = Pinned(o["pin"], out var pinOk);
                var r = await http.GetAsync($"https://{host}:{port}{AgentProtocol.HealthPath}");
                var body = await r.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                return new
                {
                    status = (int)r.StatusCode,
                    fields = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList(),
                    headers = r.Headers.Concat(r.Content.Headers).Select(h => h.Key).ToList(),
                    server = r.Headers.Server.ToString(),
                    body,
                };
            }
            case "http":
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                try
                {
                    var r = await http.GetAsync($"http://{host}:{port}{AgentProtocol.HealthPath}");
                    return new { answered = true, status = (int)r.StatusCode };
                }
                catch (Exception ex) { return new { answered = false, error = ex.GetBaseException().Message }; }
            }
            case "tls":
            {
                var protocol = o["protocol"] switch
                {
#pragma warning disable SYSLIB0039 // old protocols on purpose: the agent must refuse them
                    "tls10" => SslProtocols.Tls,
                    "tls11" => SslProtocols.Tls11,
#pragma warning restore SYSLIB0039
                    "tls12" => SslProtocols.Tls12,
                    "tls13" => SslProtocols.Tls13,
                    var p => throw new ArgumentException("protocol " + p),
                };
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port);
                using var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                try
                {
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host, EnabledSslProtocols = protocol })
                        .WaitAsync(TimeSpan.FromSeconds(15));
                    return new { handshake = true, negotiated = tls.SslProtocol.ToString() };
                }
                catch (Exception ex) { return new { handshake = false, error = ex.GetBaseException().Message }; }
            }
            case "ws":
            {
                var count = int.Parse(o.GetValueOrDefault("count") ?? "1");
                var attempts = Enumerable.Range(0, count).Select(_ => (Func<Task<object>>)(() => Ws(host, port, o)));
                var results = new List<object>();
                if (o.GetValueOrDefault("parallel") == "1") results.AddRange(await Task.WhenAll(attempts.Select(a => a())));
                else foreach (var a in attempts) results.Add(await a());
                return new { results };
            }
            case "hold":
            {
                var count = int.Parse(o["count"]);
                var sockets = new List<ClientWebSocket>();
                var results = new List<object>();
                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        var ws = NewSocket(o, out var pinOk);
                        try
                        {
                            await ws.ConnectAsync(new Uri($"wss://{host}:{port}{AgentProtocol.PowerShellPath}"), CancellationToken.None);
                            sockets.Add(ws);
                            results.Add(new { i, status = (int)ws.HttpStatusCode, open = true });
                        }
                        catch (Exception ex) { results.Add(new { i, status = (int)ws.HttpStatusCode, open = false, error = ex.GetBaseException().Message }); ws.Dispose(); }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(int.Parse(o.GetValueOrDefault("seconds") ?? "3")));
                }
                finally { foreach (var s in sockets) s.Dispose(); }
                return new { results };
            }
        }
        throw new ArgumentException("unknown kind " + o["kind"]);
    }

    private static async Task<object> Ws(string host, int port, Dictionary<string, string> o)
    {
        var path = o.GetValueOrDefault("path") ?? AgentProtocol.PowerShellPath;
        if (o.GetValueOrDefault("upgrade") == "0")
        {
            using var http = Pinned(o["pin"], out _);
            var r = await http.GetAsync($"https://{host}:{port}{path}");
            return new { status = (int)r.StatusCode, upgraded = false };
        }
        using var ws = NewSocket(o, out var pinOk);
        try
        {
            await ws.ConnectAsync(new Uri($"wss://{host}:{port}{path}"), CancellationToken.None);
            var status = (int)ws.HttpStatusCode;
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "lab", CancellationToken.None); } catch { /* the agent may close first */ }
            return new { status, upgraded = true, pinMatched = pinOk() };
        }
        catch (Exception ex)
        {
            var auth = ws.HttpResponseHeaders?.TryGetValue("WWW-Authenticate", out var v) == true ? string.Join(",", v) : null;
            return new { status = (int)ws.HttpStatusCode, upgraded = false, pinMatched = pinOk(), wwwAuthenticate = auth, error = ex.GetBaseException().Message };
        }
    }

    // TLS pinned to the fingerprint exactly like AgentTransport: a mismatch ends the handshake before any request.
    private static ClientWebSocket NewSocket(Dictionary<string, string> o, out Func<bool?> pinOk)
    {
        var expected = AgentProtocol.NormalizeFingerprint(o["pin"]);
        bool? matched = null;
        var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;
        ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => (matched = cert is not null && AgentProtocol.Fingerprint(cert.GetRawCertData()) == expected).Value;
        switch (o.GetValueOrDefault("auth") ?? "basic")
        {
            case "basic":
                ws.Options.SetRequestHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o["user"]}:{o["password"]}")));
                break;
            case "malformed":
                ws.Options.SetRequestHeader("Authorization", "Basic not-base64!");
                break;
        }
        pinOk = () => matched;
        return ws;
    }

    private static HttpClient Pinned(string pin, out Func<bool?> pinOk)
    {
        var expected = AgentProtocol.NormalizeFingerprint(pin);
        bool? matched = null;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => (matched = cert is not null && AgentProtocol.Fingerprint(cert.RawData) == expected).Value,
        };
        pinOk = () => matched;
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }
}
