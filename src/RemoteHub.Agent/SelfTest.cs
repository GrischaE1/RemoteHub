using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RemoteHub.Agent;

/// <summary>
/// <c>RemoteHubAgent.exe selftest</c>: starts the agent's listener on 127.0.0.1 with a throw-away certificate and checks
/// TLS, the health endpoint, the WebSocket endpoint and that a wrong password is refused. Changes nothing on the
/// device; useful after an update or when the service won't start.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=RemoteHub Agent self-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var temp = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        using var cert = X509CertificateLoader.LoadPkcs12(temp.Export(X509ContentType.Pfx), null);
        var fingerprint = AgentProtocol.Fingerprint(cert.RawData);

        var config = new AgentConfig { DeviceId = "self-test", ListenPort = 0 };
        await using var app = AgentHost.Build(new AgentHostOptions(config, cert, new WindowsAuthenticator(config.AllowedGroups),
            new WindowsShellLauncher(), AgentLog.Null, IPAddress.Loopback, DesktopServices.Windows()));
        await app.StartAsync();
        var port = new Uri(app.Urls.First()).Port;
        var ok = true;
        void Report(string step, bool passed, string detail = "")
        {
            ok &= passed;
            Console.WriteLine($"{(passed ? "OK  " : "FAIL")} {step}{(detail.Length > 0 ? " – " + detail : "")}");
        }

        try
        {
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => c is not null && AgentProtocol.Fingerprint(c.RawData) == fingerprint };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var json = await http.GetStringAsync($"https://127.0.0.1:{port}{AgentProtocol.HealthPath}");
            Report("TLS and health endpoint", json.Contains($"\"version\":\"{AgentHost.AgentVersion}\""), json);
        }
        catch (Exception ex) { Report("TLS and health endpoint", false, ex.GetBaseException().Message); }

        foreach (var (path, what) in new[] { (AgentProtocol.PowerShellPath, "PowerShell"), (DesktopProtocol.Path, "remote desktop") })
        {
            var step = $"Sign-in check refuses a wrong password ({what})";
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.CollectHttpResponseDetails = true;
                ws.Options.RemoteCertificateValidationCallback = (_, c, _, _) => c is not null && AgentProtocol.Fingerprint(c.GetRawCertData()) == fingerprint;
                ws.Options.SetRequestHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(@".\remotehub-selftest-nouser:" + Guid.NewGuid())));
                try { await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{port}{path}"), new CancellationTokenSource(15000).Token); }
                catch (WebSocketException) { /* expected */ }
                // The remote desktop may be turned off by policy on this device (404) – that's fine too.
                var off = path == DesktopProtocol.Path && ws.HttpStatusCode == HttpStatusCode.NotFound;
                Report(step, ws.HttpStatusCode == HttpStatusCode.Unauthorized || off, off ? "turned off by policy" : $"HTTP {(int)ws.HttpStatusCode}");
            }
            catch (Exception ex) { Report(step, false, ex.GetBaseException().Message); }
        }

        Report("Windows PowerShell 5.1 present", File.Exists(ShellCommand.PowerShellExe), ShellCommand.PowerShellExe);
        await app.StopAsync();
        Console.WriteLine(ok ? "Self-test passed." : "Self-test FAILED.");
        return ok ? 0 : 1;
    }
}
