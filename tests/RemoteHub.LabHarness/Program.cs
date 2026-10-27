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
using RemoteHub.Models;
using RemoteHub.Remoting;

// Lab harness: runs RemoteHub's own client code against a lab VM so the lab tests the real thing.
//   install --vm <Hyper-V name> --user <guest admin> --password <pw> --msi <path> --label <label> [--scope <addresses>] [--port <n>]
//       AgentEnrollment.Create (the lab's enrollment secret, LabEnrollment.cs) + AgentInstaller.Prepare + AgentInstaller.InstallOverRemoting; the pin is the certificate the device made, read back. The runspace is a
//       PowerShell Direct session instead of WinRM - everything the client does over it is the same.
//       --keep-identity 1: no new enrollment (an update over remoting); --virtual-display 0|1 (default 1);
//       --auto-sign-in 0|1: MSI property AUTOSIGNIN (not passed without it); --groups <text>: the dialog's "Allowed accounts"
//       (AgentInstaller.ParseGroups); --enrollment-file <path>: send that file as the enrollment instead (to break an
//       install on purpose). --via winrm --host <address> --cert <thumbprint>: over WinRM HTTPS
//       instead of PowerShell Direct (RemoteHub's way on a device without the agent; through the jump host with --jump).
//   enrollment --label .. --out <file> [--port n] [--scope ..] [--groups ..]
//       AgentEnrollment.Create as RemoteHub makes it for the clipboard / by-hand install / software distribution: writes
//       the .rhagent (the lab's enrollment secret, no key) and answers its keyId. --label may be left out (the device's name).
//   claim --host <address> [--port n] [--name <host name>] [--revoke-current 1] [--keys <file>]
//       AgentInstaller.ClaimAsync as RemoteHub claims an agent: outcome, pin, deviceId, computerName (LabEnrollment.cs).
//   health --host <address> --port <n> --pin <sha256>
//       AgentInstaller.HealthAsync (TLS, certificate pinned to the fingerprint).
//   update --host <address> --port <n> --pin <sha256> --user <guest admin> --password <pw> --msi <path>
//          [--new-identity <label> [--scope <addresses>]] [--enrollment-file <path>] [--virtual-display 0|1] [--hold-session 1] [--auto-sign-in 0|1]
//       Update through the running agent, as MainWindow.RunAgentInstall (AgentInstallMethod.ThroughAgent) does it:
//       PowerShell over the agent, AgentInstaller.StartUpdateThroughAgent, PackageVersion, WaitForRestartAsync (with
//       --new-identity: WaitForStopAsync and the claim of the new certificate), and the
//       same verdict (updated / not installed / no answer). --enrollment-file sends any file as the enrollment (to
//       break an update on purpose); --hold-session keeps another agent session open during the update.
//   run --host <address> --port <n> --pin <sha256> --user <account> --password <pw> --script <PowerShell>
//       A PowerShell session through the agent (AgentConnectionInfo, as every RemoteHub tool opens it); answers the
//       script's output as strings.
//   credential-provider --host .. --port .. --pin .. --user .. --password .. --msi <path> --install 1|0
//       AgentInstaller.ChangeCredentialProvider over the agent's PowerShell (RemoteHub's "Install / Remove credential provider").
//   auto-sign-in --host .. --port .. --pin .. --user .. --password .. [--view-only 1] [--no-request 1] [--password-for-sign-in ..] [--stay s] [--close 1]
//       Automatic sign-in on the console's sign-in screen through the agent desktop, as RemoteHub's desktop tab does it.
//   share ... [--windows-user <COMPUTER\user>] [--safety 1] / share-conflict ... / share-lifetime ... / share-session ...
//       network shares: copy scenario, transfer safety, Windows SMB connection lifetime, the share tab's connection (see ShareHarness.cs)
//   ssh --host .. --user .. [--password ..] [--key <file> [--passphrase ..]] [--command .. --expect ..] [--resize WxH ..]
//       an SSH connection as the SSH tab runs it: sign-in, a command, a resize, exit (see SshHarness.cs)
//   seed --connections n [--folders n] [--shares n] [--standalone n]   test data for the start-time test (see SeedHarness.cs)
//   tool --op <op> --via agent|winrm ...   RemoteHub's PowerShell connection and remote tools (see LabTools.cs)
//   probe --kind <kind> ...                raw HTTP/TLS/WebSocket checks of the agent (see LabProbes.cs)
//   jump --kind <kind> ...                 the jump host tunnel itself (see LabJump.cs)
// Every command that reaches a device takes --jump <address> --jump-pin .. --jump-user .. --jump-password ..: then it goes
// through that RemoteHub Agent as a jump host, as RemoteHub does for a connection placed under one (LabJump.cs).
//   desktop --host .. --port .. --pin .. --user .. --password .. --steps <json> [--out <folder>] [--timeout s]
//       a scripted remote desktop viewer (see DesktopHarness.cs)
//   broker --kind org|devices|remove-org|health ...   the RemoteHub Broker: a throwaway organisation, and the agent's
//       health through the broker as RemoteHub reaches it (see LabBroker.cs)
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
        "claim" => await LabEnrollment.Claim(opt),
        "health" => await Health(opt),
        "cad" => await CtrlAltDel(opt),
        "copy" => await CopyFiles(opt),
        "update" => await Update(opt),
        "run" => RunOverAgent(opt),
        "credential-provider" => CredentialProvider(opt),
        "auto-sign-in" => await AutoSignIn(opt),
        "share" => ShareHarness.Run(opt),
        "share-conflict" => ShareHarness.Conflict(opt),
        "share-lifetime" => ShareHarness.Lifetime(opt),
        "share-session" => ShareHarness.Session(opt),
        "ssh" => SshHarness.Run(opt),
        "seed" => SeedHarness.Run(opt),
        "old-backup" => OldBackup.Run(opt),
        "tool" => await LabTools.Run(opt),
        "probe" => await LabProbes.Run(opt),
        "jump" => await LabJump.Run(opt),
        "desktop" => await DesktopHarness.Run(opt),
        "broker" => await LabBroker.Run(opt),
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
    // the device makes its own key: the pin is the certificate read back over the signed-in session (as RemoteHub pins it)
    var enrollment = keep ? null : LabEnrollment.Create(o, o["label"], port, groups, scope);
    var virtualDisplay = o.GetValueOrDefault("virtual-display") != "0";
    bool? autoSignIn = o.GetValueOrDefault("auto-sign-in") switch { "1" => true, "0" => false, _ => null };
    var (folder, msi, rhagent) = AgentInstaller.Prepare(o["msi"], enrollment);
    var legacyEnrollment = false;
    // An agent older than enrollment format 2 (the previous release the update cases start from) refuses the new file:
    // it gets the format-1 file it reads, as RemoteHub of its own version made it (LabEnrollment.FormatOne).
    AgentInstaller.Installed WithLegacyFallback(Func<string?, AgentInstaller.Installed> install)
    {
        try { return install(rhagent); }
        catch (RemoteCommandException ex) when (enrollment is not null && !o.ContainsKey("enrollment-file") && ex.Message.Contains("Enrollment format 2 is not supported"))
        {
            progress.Add("The agent reads only enrollment format 1 - installing again with a format-1 file");
            File.WriteAllBytes(rhagent!, LabEnrollment.FormatOne(enrollment));
            legacyEnrollment = true;
            return install(rhagent);
        }
    }
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
            var viaWinRm = WithLegacyFallback(r => AgentInstaller.InstallOverRemoting(rs, msi, r, progress.Add, virtualDisplay, autoSignIn));
            return new { deviceId = viaWinRm.DeviceId, label = enrollment?.DeviceLabel, pin = viaWinRm.Fingerprint, port, scope, installed = viaWinRm, legacyEnrollment, progress };
        }
        using var local = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        local.Open();
        var session = OpenDirectSession(local, o["vm"], o["user"], o["password"]);
        try
        {
            var installed = WithLegacyFallback(r => AgentInstaller.InstallOverRemoting(session.Runspace, msi, r, progress.Add, virtualDisplay, autoSignIn));
            return new
            {
                deviceId = installed.DeviceId,
                label = enrollment?.DeviceLabel,
                pin = installed.Fingerprint,
                port,
                scope,
                enrollmentFile = rhagent is null ? null : Path.GetFileName(rhagent),
                legacyEnrollment,
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
    // no pin and no device ID yet: the device makes both, and the claim (harness "claim") tells them
    var enrollment = LabEnrollment.Create(o, o.GetValueOrDefault("label") ?? "", port, groups, scope);
    File.WriteAllBytes(o["out"], enrollment.Serialize());
    return new { file = o["out"], keyId = enrollment.KeyId, port, groups = enrollment.AllowedGroups };
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
    // --close 1: ended on purpose, as RemoteHub's tab does when closed (the end action runs); without it the connection
    // just drops when the harness exits (the session stays covered for a resume, then is signed out)
    var closed = o.GetValueOrDefault("close") == "1" ? await client.CloseAsync(TimeSpan.FromSeconds(10)) : (bool?)null;
    return new
    {
        rights = client.Hello.Rights,
        features = client.Hello.Features,
        session = console,
        statuses = statuses.Select(s => $"{s.State}: {s.Message}").ToList(),
        frames,
        closed,
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

// Opens a PowerShell session through the agent. A refused open fails with the text RemoteHub shows (PsRemoting.Explain):
// PowerShell's own exception only says "An error has occurred which PowerShell cannot handle", the agent's reason is
// inside it.
static Runspace OpenAgentSession(string host, AgentEndpoint endpoint, PSCredential credential)
{
    var rs = RunspaceFactory.CreateRunspace(new AgentConnectionInfo(host, endpoint, credential));
    try { rs.Open(); }
    catch (Exception ex)
    {
        rs.Dispose();
        throw new InvalidOperationException(PsRemoting.Explain(ex, new PsTarget(host, true, new CredentialSet(), endpoint)), ex);
    }
    return rs;
}

static object RunOverAgent(Dictionary<string, string> o)
{
    var secure = new SecureString();
    foreach (var c in o["password"]) secure.AppendChar(c);
    using var rs = OpenAgentSession(o["host"], LabJump.Endpoint(o, int.Parse(o["port"]), o["pin"]), new PSCredential(o["user"], secure));
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
    using var rs = OpenAgentSession(o["host"], LabJump.Endpoint(o, int.Parse(o["port"]), o["pin"]), new PSCredential(o["user"], secure));
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

    // "Enroll again": a new identity the device makes itself - RemoteHub claims it after the restart (expectedPin then)
    AgentEnrollment? enrollment = null;
    var expectedPin = pin;
    string? deviceId = null;
    if (o.TryGetValue("new-identity", out var label))
        enrollment = LabEnrollment.Create(o, label, port, null, o.GetValueOrDefault("scope"), virtualDisplay);
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
            held = OpenAgentSession(host, LabJump.Endpoint(o, port, pin), credential);
            using var ps = PowerShell.Create();
            ps.Runspace = held;
            heldPid = Convert.ToInt32(ps.AddScript("$PID").Invoke().Single().BaseObject);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? startError = null;
        try
        {
            using var rs = OpenAgentSession(host, LabJump.Endpoint(o, port, pin), credential);
            AgentInstaller.StartUpdateThroughAgent(rs, msi, rhagent, progress.Add, virtualDisplay, autoSignIn);
        }
        catch (Exception ex) { startError = ex.Message; } // a refused open: already RemoteHub's text (OpenAgentSession)

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
            if (enrollment is null)
                health = await AgentInstaller.WaitForRestartAsync(host, LabJump.Endpoint(o, port, expectedPin), expected, progress.Add, CancellationToken.None);
            else
            {
                // as RunAgentInstall: the old certificate goes away, then the claim of the new one, then its health
                await AgentInstaller.WaitForStopAsync(host, LabJump.Endpoint(o, port, pin), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(3), progress.Add, CancellationToken.None);
                progress.Add("Waiting for the agent to answer with its new certificate…");
                if (await LabEnrollment.ClaimWithin(host, port, o, TimeSpan.FromMinutes(3)) is { Outcome: AgentInstaller.ClaimOutcome.Claimed } claimed)
                {
                    expectedPin = claimed.Claim!.Fingerprint;
                    deviceId = claimed.Claim.DeviceId;
                    try { health = await AgentInstaller.HealthAsync(host, LabJump.Endpoint(o, port, expectedPin), CancellationToken.None); } catch { /* no answer */ }
                }
            }
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
            deviceId = deviceId ?? before.DeviceId,
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
