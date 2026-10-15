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
//       --keep-identity 1: no new enrollment (an update over remoting); --virtual-display 0|1 (default 1);
//       --auto-sign-in 0|1: MSI property AUTOSIGNIN (not passed without it); --groups <text>: the dialog's "Allowed accounts"
//       (AgentInstaller.ParseGroups); --enrollment-file <path>: send that file as the enrollment instead (to break an
//       install on purpose). --via winrm --host <address> --cert <thumbprint>: over WinRM HTTPS
//       instead of PowerShell Direct (RemoteHub's way on a device without the agent; through the jump host with --jump).
//   enrollment --label .. --out <file> [--port n] [--scope ..] [--groups ..]
//       AgentEnrollment.Create as RemoteHub makes it for the clipboard / by-hand install: writes the .rhagent, answers
//       pin and deviceId.
//   health --host <address> --port <n> --pin <sha256>
//       AgentInstaller.HealthAsync (TLS, certificate pinned to the fingerprint).
//   update --host <address> --port <n> --pin <sha256> --user <guest admin> --password <pw> --msi <path>
//          [--new-identity <label> [--scope <addresses>]] [--enrollment-file <path>] [--virtual-display 0|1] [--hold-session 1] [--auto-sign-in 0|1]
//       Update through the running agent, as MainWindow.RunAgentInstall (AgentInstallMethod.ThroughAgent) does it:
//       PowerShell over the agent, AgentInstaller.StartUpdateThroughAgent, PackageVersion, WaitForRestartAsync, and the
//       same verdict (updated / not installed / no answer). --enrollment-file sends any file as the enrollment (to
//       break an update on purpose); --hold-session keeps another agent session open during the update.
//   run --host <address> --port <n> --pin <sha256> --user <account> --password <pw> --script <PowerShell>
//       A PowerShell session through the agent (AgentConnectionInfo, as every RemoteHub tool opens it); answers the
//       script's output as strings.
//   credential-provider --host .. --port .. --pin .. --user .. --password .. --msi <path> --install 1|0
//       AgentInstaller.ChangeCredentialProvider over the agent's PowerShell (RemoteHub's "Install / Remove credential provider").
//   auto-sign-in --host .. --port .. --pin .. --user .. --password .. [--view-only 1] [--no-request 1] [--password-for-sign-in ..] [--stay s]
//       Automatic sign-in on the console's sign-in screen through the agent desktop, as RemoteHub's desktop tab does it.
//   share ... / share-conflict ...   network shares (see ShareHarness.cs)
//   seed --connections n [--folders n] [--shares n] [--standalone n]   test data for the start-time test (see SeedHarness.cs)
//   tool --op <op> --via agent|winrm ...   RemoteHub's PowerShell connection and remote tools (see LabTools.cs)
//   probe --kind <kind> ...                raw HTTP/TLS/WebSocket checks of the agent (see LabProbes.cs)
//   jump --kind <kind> ...                 the jump host tunnel itself (see LabJump.cs)
// Every command that reaches a device takes --jump <address> --jump-pin .. --jump-user .. --jump-password ..: then it goes
// through that RemoteHub Agent as a jump host, as RemoteHub does for a connection placed under one (LabJump.cs).
//   desktop --host .. --port .. --pin .. --user .. --password .. --steps <json> [--out <folder>] [--timeout s]
//       a scripted remote desktop viewer (see DesktopHarness.cs)
// Answers one JSON object on stdout; exit code 0 = ok, 1 = failed (the object then has "error").

var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
try
{
    var cmd = args.Length > 0 ? args[0] : "";
    var opt = Options(args.Skip(1).ToArray());
    object result = cmd switch
    {
        "install" => Install(opt),
        "enrollment" => NewEnrollment(opt),
        "health" => await Health(opt),
        "cad" => await CtrlAltDel(opt),
        "copy" => await CopyFiles(opt),
        "update" => await Update(opt),
        "run" => RunOverAgent(opt),
        "credential-provider" => CredentialProvider(opt),
        "auto-sign-in" => await AutoSignIn(opt),
        "share" => ShareHarness.Run(opt),
        "share-conflict" => ShareHarness.Conflict(opt),
        "seed" => SeedHarness.Run(opt),
        "tool" => await LabTools.Run(opt),
        "probe" => await LabProbes.Run(opt),
        "jump" => await LabJump.Run(opt),
        "desktop" => await DesktopHarness.Run(opt),
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

    // exactly what MainWindow.RunAgentInstall does before installing: create the device identity, keep the pin -
    // or, for an update with the device's identity kept, no enrollment at all
    var keep = o.GetValueOrDefault("keep-identity") == "1";
    var groups = o.TryGetValue("groups", out var g) ? AgentInstaller.ParseGroups(g) : null;
    var (enrollment, pin) = keep ? (null, null) : AgentEnrollment.Create(o["label"], port, groups, string.IsNullOrWhiteSpace(scope) ? null : scope);
    var virtualDisplay = o.GetValueOrDefault("virtual-display") != "0";
    bool? autoSignIn = o.GetValueOrDefault("auto-sign-in") switch { "1" => true, "0" => false, _ => null };
    var (folder, msi, rhagent) = AgentInstaller.Prepare(o["msi"], enrollment);
    if (o.TryGetValue("enrollment-file", out var forced))
    {
        rhagent = Path.Combine(folder, Path.GetFileName(forced));
        File.Copy(forced, rhagent, true);
    }
    try
    {
        // --via winrm --host .. --cert ..: over WinRM HTTPS as RemoteHub installs on a device without the agent (through
        // the jump host when --jump is given)
        if (o.GetValueOrDefault("via") == "winrm")
        {
            using var rs = LabTools.OpenWinRm(o).GetAwaiter().GetResult();
            var viaWinRm = AgentInstaller.InstallOverRemoting(rs, msi, rhagent, progress.Add, virtualDisplay, autoSignIn);
            return new { deviceId = enrollment?.DeviceId ?? viaWinRm.DeviceId, label = enrollment?.DeviceLabel, pin = pin ?? viaWinRm.Fingerprint, port, scope, installed = viaWinRm, progress };
        }
        using var local = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        local.Open();
        var session = OpenDirectSession(local, o["vm"], o["user"], o["password"]);
        try
        {
            var installed = AgentInstaller.InstallOverRemoting(session.Runspace, msi, rhagent, progress.Add, virtualDisplay, autoSignIn);
            return new
            {
                deviceId = enrollment?.DeviceId ?? installed.DeviceId,
                label = enrollment?.DeviceLabel,
                pin = pin ?? installed.Fingerprint,
                port,
                scope,
                enrollmentFile = rhagent is null ? null : Path.GetFileName(rhagent),
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

static object NewEnrollment(Dictionary<string, string> o)
{
    var port = o.TryGetValue("port", out var p) ? int.Parse(p) : AgentProtocol.DefaultPort;
    var scope = o.GetValueOrDefault("scope");
    var groups = o.TryGetValue("groups", out var g) ? AgentInstaller.ParseGroups(g) : null;
    var (enrollment, pin) = AgentEnrollment.Create(o["label"], port, groups, string.IsNullOrWhiteSpace(scope) ? null : scope);
    File.WriteAllBytes(o["out"], enrollment.Serialize());
    return new { file = o["out"], pin, deviceId = enrollment.DeviceId, port, groups = enrollment.AllowedGroups };
}

static async Task<object> Health(Dictionary<string, string> o)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var health = await AgentInstaller.HealthAsync(o["host"], LabJump.Endpoint(o, int.Parse(o["port"]), o["pin"]), cts.Token);
    return health;
}

// The remote desktop of the console session (as RemoteHub opens it), then the Ctrl+Alt+Del button. Answers the
// statuses the agent sent.
static async Task<object> CtrlAltDel(Dictionary<string, string> o)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var secure = new SecureString();
    foreach (var c in o["password"]) secure.AppendChar(c);
    using var client = await AgentDesktopClient.ConnectAsync(o["host"], LabJump.Endpoint(o, int.Parse(o["port"]), o["pin"]),
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

// Automatic sign-in as RemoteHub's desktop tab does it: the console's sign-in screen with DesktopStart.AutoSignIn, the
// password (the one the agent sign-in used) sent once on SignInReady, then the statuses until SignedIn or SignInFailed.
// --view-only 1 / --no-request 1 play the refusal and timeout cases; --password-for-sign-in sends another password.
static async Task<object> AutoSignIn(Dictionary<string, string> o)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
    var secure = new SecureString();
    foreach (var c in o["password"]) secure.AppendChar(c);
    using var client = await AgentDesktopClient.ConnectAsync(o["host"], LabJump.Endpoint(o, int.Parse(o["port"]), o["pin"]),
        new PSCredential(o["user"], secure), cts.Token);
    var statuses = new System.Collections.Concurrent.ConcurrentQueue<DesktopStatus>();
    var frames = 0;
    client.Received += m =>
    {
        switch (DesktopWire.TypeOf(m))
        {
            case DesktopMessage.Status:
                var s = JsonSerializer.Deserialize<DesktopStatus>(m.AsSpan(1), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
                statuses.Enqueue(s);
                if (s.State == DesktopStatusState.SignInReady && o.GetValueOrDefault("no-request") != "1")
                    client.Send(DesktopWire.SignInRequest(o.GetValueOrDefault("password-for-sign-in") ?? o["password"]));
                break;
            case DesktopMessage.Video:
                Interlocked.Increment(ref frames);
                client.Send(DesktopWire.Ack(DesktopWire.ReadVideo(m).FrameId));
                break;
        }
    };
    client.Start();
    var console = client.Hello.Sessions.FirstOrDefault(s => s.Console) ?? throw new InvalidOperationException("no console session");
    var start = JsonSerializer.SerializeToUtf8Bytes(new DesktopStart(console.Id, DesktopConsent.Ask, o.GetValueOrDefault("view-only") == "1", 30, 60, 0, null,
        null, AutoSignIn: true), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    client.Send([(byte)DesktopMessage.Start, .. start]);
    while (!statuses.Any(s => s.State is DesktopStatusState.SignedIn or DesktopStatusState.SignInFailed or DesktopStatusState.Failed or DesktopStatusState.Ended))
        await Task.Delay(200, cts.Token);
    await Task.Delay(TimeSpan.FromSeconds(int.TryParse(o.GetValueOrDefault("stay"), out var stay) ? stay : 8), cts.Token); // the picture follows into the new session
    return new
    {
        rights = client.Hello.Rights,
        features = client.Hello.Features,
        session = console,
        statuses = statuses.Select(s => $"{s.State}: {s.Message}").ToList(),
        frames,
    };
}

// The remote desktop of the console session, then files "copied" to it exactly like the desktop tab does
// (ClipboardTransfer.Files + Chunks). Answers what the agent sent back (statuses, transfer results).
static async Task<object> CopyFiles(Dictionary<string, string> o)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var secure = new SecureString();
    foreach (var c in o["password"]) secure.AppendChar(c);
    using var client = await AgentDesktopClient.ConnectAsync(o["host"], LabJump.Endpoint(o, int.Parse(o["port"]), o["pin"]),
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

static object RunOverAgent(Dictionary<string, string> o)
{
    var secure = new SecureString();
    foreach (var c in o["password"]) secure.AppendChar(c);
    using var rs = RunspaceFactory.CreateRunspace(new AgentConnectionInfo(o["host"], LabJump.Endpoint(o, int.Parse(o["port"]), o["pin"]),
        new PSCredential(o["user"], secure)));
    rs.Open();
    using var ps = PowerShell.Create();
    ps.Runspace = rs;
    var output = ps.AddScript(o["script"]).Invoke().Select(x => x?.ToString()).ToList();
    return new { output, errors = ps.Streams.Error.Select(e => e.ToString()).ToList() };
}

// Install / remove the credential provider over the agent's PowerShell, as MainWindow.ChangeCredentialProvider does.
// Answers installed (true/false) and RemoteHub's message, or the reason RemoteHub would show ("failed").
static object CredentialProvider(Dictionary<string, string> o)
{
    var secure = new SecureString();
    foreach (var c in o["password"]) secure.AppendChar(c);
    using var rs = RunspaceFactory.CreateRunspace(new AgentConnectionInfo(o["host"], LabJump.Endpoint(o, int.Parse(o["port"]), o["pin"]),
        new PSCredential(o["user"], secure)));
    rs.Open();
    var progress = new List<string>();
    try
    {
        var result = AgentInstaller.ChangeCredentialProvider(rs, o["msi"], o["install"] == "1", progress.Add);
        return new { ok = true, installed = result.Installed, message = result.Message, progress };
    }
    catch (RemoteCommandException ex) { return new { ok = false, installed = (bool?)null, message = ex.Message, progress }; }
}

// Update through the running agent - the ThroughAgent branch of MainWindow.RunAgentInstall, step by step, with the
// same verdict and texts. Keep the two in step when that branch changes.
static async Task<object> Update(Dictionary<string, string> o)
{
    var host = o["host"];
    var port = int.Parse(o["port"]);
    var pin = o["pin"];
    var secure = new SecureString();
    foreach (var c in o["password"]) secure.AppendChar(c);
    var credential = new PSCredential(o["user"], secure);
    var progress = new List<string>();
    var virtualDisplay = o.GetValueOrDefault("virtual-display") != "0";
    // AUTOSIGNIN as MainWindow.RunAgentInstall passes it (o.AutoSignIn): not given = the device keeps its choice
    bool? autoSignIn = o.GetValueOrDefault("auto-sign-in") switch { "1" => true, "0" => false, _ => null };

    // "Create a new device certificate": a new identity, and RemoteHub then waits for the new fingerprint
    AgentEnrollment? enrollment = null;
    var expectedPin = pin;
    if (o.TryGetValue("new-identity", out var label))
    {
        var scope = o.GetValueOrDefault("scope");
        (enrollment, expectedPin) = AgentEnrollment.Create(label, port, null, string.IsNullOrWhiteSpace(scope) ? null : scope, virtualDisplay: virtualDisplay);
    }
    var (folder, msi, rhagent) = AgentInstaller.Prepare(o["msi"], enrollment);
    if (o.TryGetValue("enrollment-file", out var forced))
    {
        rhagent = Path.Combine(folder, Path.GetFileName(forced));
        File.Copy(forced, rhagent, true);
    }

    Runspace? held = null;
    int? heldPid = null;
    try
    {
        var before = await AgentInstaller.HealthAsync(host, LabJump.Endpoint(o, port, pin), CancellationToken.None);
        if (o.GetValueOrDefault("hold-session") == "1")
        {
            held = RunspaceFactory.CreateRunspace(new AgentConnectionInfo(host, LabJump.Endpoint(o, port, pin), credential));
            held.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = held;
            heldPid = Convert.ToInt32(ps.AddScript("$PID").Invoke().Single().BaseObject);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? startError = null;
        try
        {
            using var rs = RunspaceFactory.CreateRunspace(new AgentConnectionInfo(host, LabJump.Endpoint(o, port, pin), credential));
            rs.Open();
            AgentInstaller.StartUpdateThroughAgent(rs, msi, rhagent, progress.Add, virtualDisplay, autoSignIn);
        }
        catch (Exception ex) { startError = ex.Message; }

        string verdict, message;
        AgentHealth? health = null;
        var expected = AgentInstaller.PackageVersion(msi);
        if (startError is not null)
        {
            verdict = "failed";
            message = "Failed: " + startError;
        }
        else
        {
            health = await AgentInstaller.WaitForRestartAsync(host, LabJump.Endpoint(o, port, expectedPin), expected, progress.Add, CancellationToken.None);
            if (health is null) { verdict = "noAnswer"; message = "The agent didn't answer again within a few minutes."; }
            else if (expected is not null && health.Version != expected) { verdict = "notInstalled"; message = $"The update didn't install: the device still runs RemoteHub Agent {health.Version} (expected {expected})."; }
            else { verdict = "updated"; message = $"Updated – RemoteHub Agent {health.Version} is running."; }
        }
        var seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);

        // the session that was open during the update: does it still work?
        string? heldAfter = null;
        if (held is not null)
        {
            // bounded: a session whose agent went away must end, not hang - "hung" is a finding, not a lab timeout
            var call = Task.Run(() =>
            {
                using var ps = PowerShell.Create();
                ps.Runspace = held;
                return "still works (PID " + ps.AddScript("$PID").Invoke().Single() + ")";
            });
            try { heldAfter = await call.WaitAsync(TimeSpan.FromSeconds(60)); }
            catch (TimeoutException) { heldAfter = "hung: no answer and no error within 60 s"; }
            catch (Exception ex) { heldAfter = "ended: " + ex.GetBaseException().GetType().Name; }
        }

        // what the agent answers now with the old and with the expected pin
        string? oldPinNow;
        try { oldPinNow = (await AgentInstaller.HealthAsync(host, LabJump.Endpoint(o, port, pin), CancellationToken.None)).Version; }
        catch (Exception ex) { oldPinNow = "refused: " + ex.GetBaseException().GetType().Name; }

        return new
        {
            verdict,
            message,
            expectedVersion = expected,
            before = before.Version,
            health,
            expectedPin,
            deviceId = enrollment?.DeviceId ?? before.DeviceId,
            oldPinNow,
            heldPid,
            heldAfter,
            seconds,
            progress,
        };
    }
    finally
    {
        // a hung session can block Dispose too - don't let it hold the result back
        if (held is not null) _ = Task.Run(held.Dispose).WaitAsync(TimeSpan.FromSeconds(15)).ContinueWith(_ => { });
        try { Directory.Delete(folder, true); } catch { /* staging folder in %TEMP% */ }
    }
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
