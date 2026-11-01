using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Agent;
using RemoteHub.Broker;
using RemoteHub.Data;
using RemoteHub.Remoting;
using static RemoteHub.Agent.BrokerProtocol;

/// <summary>
/// The RemoteHub Broker in the lab (docs/test-lab-cases.md §9.14). The broker is the one lab.json names; its admin token
/// comes from lab.local.json. The organisations made here are throwaway ones with secrets made here – test values only.
///   broker --kind org --url <broker> --token <admin token>
///       a new organisation through the admin API, as <c>rhbroker org create</c> makes it (both secrets, the console
///       secret's keyring holding the device secret): its id and both secrets
///   broker --kind devices --url .. --token .. --org <id>      the admin API's device list (online, record, secretId)
///   broker --kind remove-org --url .. --token .. --org <id>   the organisation deleted again
///   broker --kind health --url .. --console-secret <rhc1…> --device <id> [--seconds s]
///       RemoteHub's way through the broker: Connect with the console secret (in a throwaway local database and key),
///       the device's record from the synced list with its seal checked here, then the agent's health through a relayed
///       session, pinned to the fingerprint in that sealed record
/// </summary>
internal static class LabBroker
{
    public static async Task<object> Run(Dictionary<string, string> o) => o.GetValueOrDefault("kind") switch
    {
        "org" => await CreateOrg(o),
        "devices" => JsonNode.Parse(await (await Admin(o, HttpMethod.Get, $"orgs/{o["org"]}/devices")).Content.ReadAsStringAsync())!,
        "remove-org" => new { status = (int)(await Admin(o, HttpMethod.Delete, $"orgs/{o["org"]}")).StatusCode },
        "health" => await Health(o),
        _ => throw new ArgumentException("broker --kind org|devices|remove-org|health ..."),
    };

    private static async Task<HttpResponseMessage> Admin(Dictionary<string, string> o, HttpMethod method, string path, JsonObject? body = null)
    {
        using var http = new HttpClient { BaseAddress = new Uri(o["url"]) };
        var req = new HttpRequestMessage(method, "/v1/admin/" + path);
        req.Headers.Authorization = new("Bearer", o["token"]);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await http.SendAsync(req);
    }

    private static async Task<object> CreateOrg(Dictionary<string, string> o)
    {
        var id = "o_" + B32(RandomNumberGenerator.GetBytes(10));
        var device = new Secret(SecretKind.Device, id, "s_" + B32(RandomNumberGenerator.GetBytes(5)), RandomNumberGenerator.GetBytes(32));
        var console = new Secret(SecretKind.Console, id, "s_" + B32(RandomNumberGenerator.GetBytes(5)), RandomNumberGenerator.GetBytes(32));
        var body = new JsonObject
        {
            ["id"] = id,
            ["shortName"] = "lab" + id[2..10],
            ["displayName"] = "Lab " + id,
            ["secrets"] = new JsonArray(
                new JsonObject { ["id"] = device.Id, ["kind"] = "device", ["key"] = B64u(DeriveKey(device.Key, id, KeyLabel.Enroll)) },
                new JsonObject { ["id"] = console.Id, ["kind"] = "console", ["key"] = B64u(DeriveKey(console.Key, id, KeyLabel.Join)) }),
            ["keyrings"] = new JsonArray(new JsonObject { ["secretId"] = console.Id, ["blob"] = Keyring(console, device.ToString()) }),
        };
        var r = await Admin(o, HttpMethod.Post, "orgs", body);
        if (r.StatusCode != HttpStatusCode.Created) throw new InvalidOperationException($"The broker didn't create the organisation: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return new { org = id, name = "Lab " + id, deviceSecret = device.ToString(), deviceSecretId = device.Id, consoleSecret = console.ToString() };
    }

    /// <summary>The console secret's keyring as <c>rhbroker</c> makes it: iv ‖ AES-GCM(ciphertext ‖ tag).</summary>
    private static string Keyring(Secret console, string deviceSecret)
    {
        var plain = Encoding.UTF8.GetBytes(new JsonObject { ["v"] = 1, ["deviceSecrets"] = new JsonArray(JsonValue.Create(deviceSecret)) }.ToJsonString());
        var iv = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(DeriveKey(console.Key, console.Org, KeyLabel.Keyring), 16);
        aes.Encrypt(iv, plain, cipher, tag, Encoding.UTF8.GetBytes($"remotehub-broker/1|{console.Org}|keyring|{console.Id}"));
        return B64u([.. iv, .. cipher, .. tag]);
    }

    /// <summary>
    /// What the session logged about its path – why a direct path failed, for one. The diagnostic log is in this run's
    /// temporary folder and goes with it, so the answer carries these lines.
    /// </summary>
    private static string[] SessionLog() =>
        File.Exists(RemoteHub.Services.DiagLog.FilePath) ? [.. File.ReadAllLines(RemoteHub.Services.DiagLog.FilePath).Where(l => l.Contains("Broker session", StringComparison.Ordinal))] : [];

    private static async Task<object> Health(Dictionary<string, string> o)
    {
        // A throwaway local database (and with it this run's broker connection and key), never the lab user's own.
        var data = Path.Combine(Path.GetTempPath(), "rhlab-broker-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("REMOTEHUB_DATA_DIR", data);
        LocalDb.Open();
        BrokerConnection? info = null;
        try
        {
            var seconds = int.Parse(o.GetValueOrDefault("seconds") ?? "60");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            info = await BrokerClient.JoinAsync(o["url"], o["console-secret"], "Lab harness", timeout.Token);
            await using var client = BrokerClient.Start(info);
            var deviceId = o["device"];
            var found = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Changed += (_, items) =>
            {
                foreach (var i in items)
                    if (i["kind"]?.GetValue<string>() == "device" && i["id"]?.GetValue<string>() == deviceId) found.TrySetResult(i);
            };
            var item = await found.Task.WaitAsync(timeout.Token);
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!client.IsConnected && DateTime.UtcNow < deadline) await Task.Delay(100);
            var record = item["record"]!.AsObject();
            var isSealed = client.RecordIsSealed(record, item["seal"]?.GetValue<string>());
            var pin = record["fingerprint"]?.GetValue<string>() ?? "";
            if (!isSealed) return new { joined = info.OrgName, connected = client.IsConnected, isSealed, online = item["online"]?.GetValue<bool>() };
            var route = new BrokerRoute(client, deviceId, item["secretId"]?.GetValue<string>(), "lab device", pin);
            RemoteCertificateValidationCallback pinned = (_, cert, _, _) => cert is not null && AgentProtocol.Fingerprint(cert.GetRawCertData()) == pin;
            // --direct-first 1: as RemoteHub with Reach Automatic – the record's name, then every address the agent
            // reported, before the broker path (DirectReach). The answer says which host answered, and what the broker
            // has of the device's addresses either way.
            DeviceAddresses? reported = null;
            if (o.GetValueOrDefault("direct-first") == "1")
            {
                var name = string.Join('.', new[] { record["computerName"]?.GetValue<string>(), record["domain"]?.GetValue<string>() }.Where(s => !string.IsNullOrEmpty(s)));
                reported = await client.DeviceAddressesAsync(deviceId, timeout.Token);
                var tried = DateTime.UtcNow;
                if (await DirectReach.FindAsync(route, name, AgentProtocol.DefaultPort, pin, timeout.Token) is { } reach)
                {
                    using var direct = new SocketsHttpHandler { SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = pinned } };
                    using var directHttp = new HttpClient(direct) { Timeout = TimeSpan.FromSeconds(seconds) };
                    var host = reach.Host.Contains(':') ? $"[{reach.Host}]" : reach.Host;
                    var answer = JsonNode.Parse(await directHttp.GetStringAsync($"https://{host}:{reach.Port}{AgentProtocol.HealthPath}"));
                    return new { joined = info.OrgName, connected = client.IsConnected, isSealed, online = item["online"]?.GetValue<bool>(), pin, health = answer, path = "Network",
                                 directHost = reach.Host, name, addresses = reported?.Addresses.Select(a => a.ToString()), inbound = reported?.Inbound,
                                 seconds = Math.Round((DateTime.UtcNow - tried).TotalSeconds, 1) };
                }
            }
            using var handler = AgentClientTransport.ThroughBroker(route, pinned);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(seconds) };
            var started = DateTime.UtcNow;
            var health = JsonNode.Parse(await http.GetStringAsync($"https://lab-device:{AgentProtocol.DefaultPort}{AgentProtocol.HealthPath}"));
            // How the session went: direct (local / internet) or relayed, and how long the first answer took.
            var path = PeerSessions.PathOf(route)?.ToString();
            return new { joined = info.OrgName, connected = client.IsConnected, isSealed, online = item["online"]?.GetValue<bool>(), pin, health, path,
                         addresses = reported?.Addresses.Select(a => a.ToString()), inbound = reported?.Inbound, seconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1),
                         sessionLog = SessionLog() };
        }
        finally
        {
            PeerSessions.CloseAll();
            if (info is not null) try { BrokerStore.Remove(info); } catch { /* the folder goes anyway */ }
            LocalDb.Close();
            try { Directory.Delete(data, recursive: true); } catch { /* temp */ }
        }
    }
}
