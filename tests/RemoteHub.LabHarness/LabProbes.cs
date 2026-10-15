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
///   probe --kind tls --host .. --port n --protocol tls12|tls13|tls11|tls10|tls12raw   handshake with only that protocol (1.0 / 1.1 and
///       tls12raw: a hand-written ClientHello, independent of what this PC's TLS stack allows)
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
                catch (Exception ex) { return new { answered = false, reason = ex.GetBaseException().Message }; }
            }
            case "tls":
            {
                // TLS 1.0 / 1.1: a ClientHello written by hand - this PC's own TLS stack may refuse to offer them at all,
                // and then a refusal would say nothing about the device.
                // "tls12raw" is the control: the same hand-written hello as 1.2 must be answered, or the refusals mean nothing.
                if (o["protocol"] is "tls10" or "tls11" or "tls12raw") return await LegacyHello(host, port, o["protocol"] switch { "tls10" => (byte)1, "tls11" => (byte)2, _ => (byte)3 });
                var protocol = o["protocol"] switch
                {
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
                // "reason", not "error": the lab reads "error" as a failed harness call
                catch (Exception ex) { return new { handshake = false, reason = ex.GetBaseException().Message }; }
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
            case "big":
            {
                // one text message of --mb MiB on the PowerShell socket (in 1 MiB frames, the end flag only on the last):
                // RemoteHub never sends one that large, the agent's 16 MiB limit has to end the session
                var mb = int.Parse(o.GetValueOrDefault("mb") ?? "17");
                using var ws = NewSocket(o, out _);
                await ws.ConnectAsync(new Uri($"wss://{host}:{port}{AgentProtocol.PowerShellPath}"), CancellationToken.None);
                var chunk = Encoding.ASCII.GetBytes(new string('A', 1024 * 1024));
                var sent = 0;
                string? sendError = null;
                try
                {
                    for (; sent < mb; sent++)
                        await ws.SendAsync(chunk, WebSocketMessageType.Text, sent == mb - 1, CancellationToken.None);
                }
                catch (Exception ex) { sendError = ex.GetBaseException().Message; }
                // the agent's answer: a close (or the connection dropped) within 30 s
                string? receiveError = null;
                var buffer = new byte[64 * 1024];
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    while (ws.State == WebSocketState.Open)
                        if ((await ws.ReceiveAsync(buffer, cts.Token)).MessageType == WebSocketMessageType.Close) break;
                }
                catch (Exception ex) { receiveError = ex.GetBaseException().Message; }
                return new
                {
                    sentMiB = sent, sendError, receiveError, state = ws.State.ToString(), closeStatus = ws.CloseStatus?.ToString(),
                    closeDescription = ws.CloseStatusDescription, ended = ws.State != WebSocketState.Open,
                };
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

    // A TLS 1.0 (minor 1) or 1.1 (minor 2) ClientHello without the supported_versions extension, so the server can only
    // answer with that version or refuse. Answers handshake = the server sent a ServerHello for exactly that version.
    private static async Task<object> LegacyHello(string host, int port, byte minor)
    {
        var body = new List<byte> { 3, minor };
        body.AddRange(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        body.Add(0); // no session id
        byte[] suites = [0xC0, 0x13, 0xC0, 0x14, 0x00, 0x2F, 0x00, 0x35, 0x00, 0x0A, 0x00, 0xFF];
        body.AddRange([0, (byte)suites.Length]); body.AddRange(suites);
        body.AddRange([1, 0]); // compression: null
        byte[] ext = [0x00, 0x0A, 0x00, 0x06, 0x00, 0x04, 0x00, 0x17, 0x00, 0x18,   // supported_groups: P-256, P-384
                      0x00, 0x0B, 0x00, 0x02, 0x01, 0x00];                            // ec_point_formats: uncompressed
        body.AddRange([0, (byte)ext.Length]); body.AddRange(ext);
        var hello = new List<byte> { 1, 0, (byte)(body.Count >> 8), (byte)body.Count };
        hello.AddRange(body);
        var record = new List<byte> { 0x16, 3, 1, (byte)(hello.Count >> 8), (byte)hello.Count };
        record.AddRange(hello);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port);
        var s = tcp.GetStream();
        await s.WriteAsync(record.ToArray());
        var head = new byte[11];
        var read = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (read < head.Length)
            {
                var n = await s.ReadAsync(head.AsMemory(read), cts.Token);
                if (n == 0) break;
                read += n;
            }
        }
        catch (Exception ex) { return new { handshake = false, reason = "connection ended: " + ex.GetBaseException().Message }; }
        if (read < 6) return new { handshake = false, reason = $"connection closed after {read} bytes" };
        if (head[0] == 0x15) return new { handshake = false, reason = $"alert {head[5]}{(read >= 7 ? "/" + head[6] : "")}" };
        // handshake record, ServerHello (type 2): server_version follows the 4-byte handshake header
        if (head[0] == 0x16 && head[5] == 2 && read >= 11)
            return new { handshake = head[9] == 3 && head[10] == minor, negotiated = $"{head[9]}.{head[10]}", reason = (string?)null };
        return new { handshake = false, reason = $"unexpected record type {head[0]}" };
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
