using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Agent;
using RemoteHub.Remoting;

// Lab harness: runs RemoteHub's own client code against a lab VM so the lab tests the real thing.
//   install --vm <Hyper-V name> --user <guest admin> --password <pw> --msi <path> --label <label> [--scope <addresses>] [--port <n>]
//       AgentEnrollment.Create + AgentInstaller.Prepare + AgentInstaller.InstallOverRemoting. The runspace is a
//       PowerShell Direct session instead of WinRM - everything the client does over it is the same.
//   health --host <address> --port <n> --pin <sha256>
//       AgentInstaller.HealthAsync (TLS, certificate pinned to the fingerprint).
// Answers one JSON object on stdout; exit code 0 = ok, 1 = failed (the object then has "error").

var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
try
{
    var cmd = args.Length > 0 ? args[0] : "";
    var opt = Options(args.Skip(1).ToArray());
    object result = cmd switch
    {
        "install" => Install(opt),
        "health" => await Health(opt),
        "cad" => await CtrlAltDel(opt),
        "copy" => await CopyFiles(opt),
        _ => throw new ArgumentException("usage: install --vm .. --user .. --password .. --msi .. --label .. [--scope ..] [--port ..] | health --host .. --port .. --pin .. | " +
                                         "cad --host .. --port .. --pin .. --user .. --password .."),
    };
    Console.WriteLine(JsonSerializer.Serialize(result, json));
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine(JsonSerializer.Serialize(new { error = ex.GetType().Name + ": " + ex.Message, stack = ex.StackTrace }, json));
    return 1;
}

static object Install(Dictionary<string, string> o)
{
    var port = o.TryGetValue("port", out var p) ? int.Parse(p) : AgentProtocol.DefaultPort;
    var scope = o.GetValueOrDefault("scope");
    var progress = new List<string>();

    // exactly what MainWindow.RunAgentInstall does before installing: create the device identity, keep the pin
    var (enrollment, pin) = AgentEnrollment.Create(o["label"], port, null, string.IsNullOrWhiteSpace(scope) ? null : scope);
    var (folder, msi, rhagent) = AgentInstaller.Prepare(o["msi"], enrollment);
    try
    {
        using var local = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        local.Open();
        var session = OpenDirectSession(local, o["vm"], o["user"], o["password"]);
        try
        {
            var installed = AgentInstaller.InstallOverRemoting(session.Runspace, msi, rhagent, progress.Add);
            return new
            {
                deviceId = enrollment.DeviceId,
                label = enrollment.DeviceLabel,
                pin,
                port,
                scope,
                enrollmentFile = Path.GetFileName(rhagent),
                installed,
                progress,
            };
        }
        finally
        {
            using var ps = PowerShell.Create(local);
            ps.AddCommand("Remove-PSSession").AddParameter("Session", session).Invoke();
        }
    }
    finally
    {
        try { Directory.Delete(folder, true); } catch { /* staging folder in %TEMP% */ }
    }
}

static async Task<object> Health(Dictionary<string, string> o)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var health = await AgentInstaller.HealthAsync(o["host"], int.Parse(o["port"]), o["pin"], cts.Token);
    return health;
}

// The remote desktop of the console session (as RemoteHub opens it), then the Ctrl+Alt+Del button. Answers the
// statuses the agent sent.
static async Task<object> CtrlAltDel(Dictionary<string, string> o)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var secure = new SecureString();
    foreach (var c in o["password"]) secure.AppendChar(c);
    using var client = await AgentDesktopClient.ConnectAsync(o["host"], new AgentEndpoint(int.Parse(o["port"]), o["pin"]),
        new PSCredential(o["user"], secure), cts.Token);
    var statuses = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var frames = 0;
    var layouts = new System.Collections.Concurrent.ConcurrentQueue<string>();
    client.Received += m =>
    {
        switch (DesktopWire.TypeOf(m))
        {
            case DesktopMessage.Status:
                statuses.Enqueue(DesktopWire.ReadText(m)); // the status JSON
                break;
            case DesktopMessage.Layout:
                layouts.Enqueue($"{frames} frames: " + DesktopWire.ReadText(m));
                break;
            case DesktopMessage.Video:
                Interlocked.Increment(ref frames);
                client.Send(DesktopWire.Ack(DesktopWire.ReadVideo(m).FrameId));
                break;
        }
    };
    client.Start();
    var console = client.Hello.Sessions.FirstOrDefault(s => s.Console) ?? throw new InvalidOperationException("no console session");
    var consent = o.GetValueOrDefault("consent") ?? DesktopConsent.Ask;
    var startJson = JsonSerializer.SerializeToUtf8Bytes(new DesktopStart(console.Id, consent, false, 30, 60, int.TryParse(o.GetValueOrDefault("monitor"), out var m0) ? m0 : 0, null), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    client.Send([(byte)DesktopMessage.Start, .. startJson]);
    var wait = int.TryParse(o.GetValueOrDefault("wait"), out var w) ? w : 5;
    for (var i = 0; i < 100 && !statuses.Any(x => x.Contains($"\"{DesktopStatusState.Connected}\"")); i++) await Task.Delay(100, cts.Token);
    // Fit to the viewer's window, like RemoteHub with "fit to window" on.
    var monitor = int.TryParse(o.GetValueOrDefault("monitor"), out var mi) ? mi : 0;
    var (fitW, fitH) = o.GetValueOrDefault("fit") is { } fit && fit.Split('x') is [var fw, var fh] ? (int.Parse(fw), int.Parse(fh)) : (1600, 900);
    var settings = JsonSerializer.SerializeToUtf8Bytes(new DesktopSettings(monitor, 30, 60, fitW, fitH), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    client.Send([(byte)DesktopMessage.Settings, .. settings]);
    await Task.Delay(TimeSpan.FromSeconds(wait), cts.Token); // the helper is up and capturing, like a viewer looking at it
    if (o.ContainsKey("input"))
    {
        // Like someone using the viewer: the mouse moves, Shift is pressed and released.
        for (var i = 0; i < 10; i++) { client.Send(DesktopWire.Mouse(new RemoteMouse(RemoteMouseAction.Move, RemoteMouseButton.None, 100 + i * 20, 100 + i * 10, 0))); await Task.Delay(50, cts.Token); }
        client.Send(DesktopWire.Key(new RemoteKey(0x2A, RemoteKeyFlags.None)));
        client.Send(DesktopWire.Key(new RemoteKey(0x2A, RemoteKeyFlags.Up)));
        await Task.Delay(3000, cts.Token);
    }
    var framesAtPress = frames;
    if (!o.ContainsKey("nocad")) client.Send(DesktopWire.Empty(DesktopMessage.SecureAttention));
    await Task.Delay(TimeSpan.FromSeconds(6), cts.Token);
    return new { session = console, statuses, layouts, framesAtPress, frames };
}

// The remote desktop of the console session, then files "copied" to it exactly like the desktop tab does
// (ClipboardTransfer.Files + Chunks). Answers what the agent sent back (statuses, transfer results).
static async Task<object> CopyFiles(Dictionary<string, string> o)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var secure = new SecureString();
    foreach (var c in o["password"]) secure.AppendChar(c);
    using var client = await AgentDesktopClient.ConnectAsync(o["host"], new AgentEndpoint(int.Parse(o["port"]), o["pin"]),
        new PSCredential(o["user"], secure), cts.Token);
    var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
    client.Received += m =>
    {
        switch (DesktopWire.TypeOf(m))
        {
            case DesktopMessage.Status: messages.Enqueue("status " + DesktopWire.ReadText(m)); break;
            case DesktopMessage.TransferEnd: messages.Enqueue("transferEnd " + DesktopWire.ReadText(m)); break;
            case DesktopMessage.Video: client.Send(DesktopWire.Ack(DesktopWire.ReadVideo(m).FrameId)); break;
        }
    };
    client.Start();
    var console = client.Hello.Sessions.FirstOrDefault(s => s.Console) ?? throw new InvalidOperationException("no console session");
    var startJson = JsonSerializer.SerializeToUtf8Bytes(new DesktopStart(console.Id, o.GetValueOrDefault("consent") ?? DesktopConsent.None, false, 30, 60, 0, null),
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    client.Send([(byte)DesktopMessage.Start, .. startJson]);
    for (var i = 0; i < 100 && !messages.Any(x => x.Contains($"\"{DesktopStatusState.Connected}\"")); i++) await Task.Delay(100, cts.Token);
    await Task.Delay(2000, cts.Token);
    var transfer = ClipboardTransfer.Files(1, o["file"].Split('|'), out var error) ?? throw new InvalidOperationException(error);
    var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    double? requestedAfter = null;
    if (o.ContainsKey("offer"))
    {
        // Like RemoteHub with a newer agent: offer, then send only when the device asks (someone pastes there).
        var requested = new TaskCompletionSource();
        client.Received += m => { if (DesktopWire.TypeOf(m) == DesktopMessage.TransferRequest) { messages.Enqueue("request " + DesktopWire.ReadText(m)); requested.TrySetResult(); } };
        var offeredAt = System.Diagnostics.Stopwatch.StartNew();
        client.Send([(byte)DesktopMessage.TransferOffer, .. JsonSerializer.SerializeToUtf8Bytes(transfer.Transfer, json)]);
        messages.Enqueue("offered");
        if (int.TryParse(o.GetValueOrDefault("ctrlv"), out var pasteAfter))
            _ = Task.Run(async () =>
            {
                // The admin pastes in the viewer: Ctrl+V through the agent into the focused window (Explorer).
                await Task.Delay(TimeSpan.FromSeconds(pasteAfter));
                messages.Enqueue("ctrl+v");
                client.Send(DesktopWire.Key(new RemoteKey(0x1D, RemoteKeyFlags.None)));
                client.Send(DesktopWire.Key(new RemoteKey(0x2F, RemoteKeyFlags.None)));
                client.Send(DesktopWire.Key(new RemoteKey(0x2F, RemoteKeyFlags.Up)));
                client.Send(DesktopWire.Key(new RemoteKey(0x1D, RemoteKeyFlags.Up)));
            });
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(int.Parse(o["offer"])), cts.Token);
        requestedAfter = Math.Round(offeredAt.Elapsed.TotalSeconds, 1);
    }
    var sw = System.Diagnostics.Stopwatch.StartNew();
    client.Send([(byte)DesktopMessage.TransferStart, .. JsonSerializer.SerializeToUtf8Bytes(transfer.Transfer, json)]);
    foreach (var chunk in ClipboardTransfer.Chunks(transfer))
    {
        while (client.Queued > 8) await Task.Delay(10, cts.Token);
        client.Send(chunk);
    }
    while (client.Queued > 0) await Task.Delay(10, cts.Token);
    var sent = sw.Elapsed.TotalSeconds;
    await Task.Delay(TimeSpan.FromSeconds(int.TryParse(o.GetValueOrDefault("wait"), out var w) ? w : 5), cts.Token);
    return new { bytes = transfer.Transfer.Total, requestedAfter, sentSeconds = Math.Round(sent, 2), messages };
}

// Retries the connection while a busy guest (right after a revert) drops it - nothing has run in the guest yet.
static PSSession OpenDirectSession(Runspace local, string vm, string user, string password, int attempts = 5)
{
    var secure = new SecureString();
    foreach (var c in password) secure.AppendChar(c);
    var credential = new PSCredential(user, secure);
    for (var i = 1; ; i++)
    {
        using var ps = PowerShell.Create(local);
        ps.AddCommand("New-PSSession").AddParameter("VMName", vm).AddParameter("Credential", credential);
        string error;
        try
        {
            var result = ps.Invoke();
            if (!ps.HadErrors && result.Count == 1) return (PSSession)result[0].BaseObject;
            error = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
        }
        catch (Exception ex) { error = ex.Message; }
        if (i >= attempts) throw new InvalidOperationException($"PowerShell Direct to {vm} failed {attempts} times: {error}");
        Thread.Sleep(TimeSpan.FromSeconds(5));
    }
}

static Dictionary<string, string> Options(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i + 1 < a.Length; i += 2)
    {
        if (!a[i].StartsWith("--")) throw new ArgumentException("expected --name value, got " + a[i]);
        d[a[i][2..]] = a[i + 1];
    }
    return d;
}
