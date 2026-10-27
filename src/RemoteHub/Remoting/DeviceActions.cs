using System.Management.Automation.Runspaces;

namespace RemoteHub.Remoting;

/// <summary>What a connection's Device actions do on the device (README → Remote tools).</summary>
public enum DeviceAction { Lock, SendMessage, SignOut, Restart, ShutDown, GroupPolicyUpdate, IntuneSync }

/// <summary>A user with a session on the device (console or RDP): the session's id and the account as DOMAIN\user.</summary>
public sealed record SignedInUser(int SessionId, string Account);

/// <summary>What happened in one user's session (lock, sign out, message): the tool's exit code and what it printed.</summary>
public sealed record SessionOutcome(int SessionId, string Account, int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>An action's result as RemoteHub reports it: whether it worked and the sentence(s) to show.</summary>
public sealed record DeviceActionResult(bool Ok, string Text);

/// <summary>
/// Lock, send a message, sign out, restart, shut down, update Group Policy and sync with Intune – fixed PowerShell run over
/// the connection's PowerShell (the agent's or WinRM), as the account that signs in there. Nothing new listens on the
/// device: the agent's PowerShellEnabled and its PowerShell right decide who may do this, as for the other tools. The
/// only text that comes from the user, a message, goes in as a parameter, never into the script.
/// </summary>
public static class DeviceActions
{
    /// <summary>Longest message: msg.exe shows it in a message box, and longer text is cut there.</summary>
    public const int MaxMessageLength = 255;

    /// <summary>How long a message stays on the user's screen unless they close it.</summary>
    public const int MessageSeconds = 3600;

    // The users with a session: the owner of a process in each session other than 0, but only a real account (a local or
    // domain user S-1-5-21-…, or an Entra ID user S-1-12-1-…). Windows' own accounts in those sessions – SYSTEM, DWM-n,
    // UMFD-n – are left out by their SID, so it doesn't depend on the device's language. The remoting session itself runs
    // in session 0.
    private const string SignedInFunction = """
        function Get-RhSignedIn {
            $sids = @{}; $users = [ordered]@{}
            # Without an administrator's rights the owners can't be read: say so rather than report nobody signed in.
            try { $all = @(Get-Process -IncludeUserName -ErrorAction Stop) }
            catch { throw "RemoteHub can't see who is signed in: the account isn't an administrator on the device ($($_.Exception.Message))" }
            foreach ($p in @($all | Sort-Object SessionId)) {
                $key = [string]$p.SessionId
                if ($p.SessionId -le 0 -or -not $p.UserName -or $users.Contains($key)) { continue }
                if (-not $sids.ContainsKey($p.UserName)) {
                    try { $sids[$p.UserName] = ([Security.Principal.NTAccount]$p.UserName).Translate([Security.Principal.SecurityIdentifier]).Value }
                    catch { $sids[$p.UserName] = '' }
                }
                if ($sids[$p.UserName] -match '^S-1-(5-21|12-1)-') { $users[$key] = $p.UserName }
            }
            foreach ($k in $users.Keys) { [pscustomobject]@{ SessionId = [int]$k; Account = [string]$users[$k] } }
        }

        """;

    private const string SignedInScript = SignedInFunction + """
        foreach ($u in @(Get-RhSignedIn)) { "$($u.SessionId)`t$($u.Account)" }
        """;

    // tsdiscon disconnects the session: the console shows its lock screen, an RDP user's window closes and the session
    // waits for them. logoff signs out. msg shows a message box. Each answers per session, so one refused session doesn't
    // hide the others.
    private const string SessionScript = """
        param([string]$Action, [string]$Text = '', [int]$Seconds = 3600)

        """ + SignedInFunction + """
        $ErrorActionPreference = 'Stop'
        $exe = @{ Lock = 'tsdiscon.exe'; SignOut = 'logoff.exe'; SendMessage = 'msg.exe' }[$Action]
        if (-not $exe) { throw "Unknown action: $Action" }
        $path = Join-Path $env:windir "System32\$exe"
        if (-not (Test-Path -LiteralPath $path)) { throw "$exe isn't on this device (Windows Home doesn't have it)." }
        foreach ($u in @(Get-RhSignedIn)) {
            if ($Action -eq 'SendMessage') { $out = & $path $u.SessionId "/TIME:$Seconds" $Text 2>&1 }
            else { $out = & $path $u.SessionId 2>&1 }
            "$($u.SessionId)`t$($u.Account)`t$LASTEXITCODE`t" + (($out | Out-String).Trim() -replace '[\t\r\n]+', ' ')
        }
        """;

    // shutdown.exe returns at once and Windows goes down after the answer is sent. The comment and reason are in the
    // device's System log (event 1074) next to the account that asked.
    private const string PowerScript = """
        param([string]$Kind)
        $ErrorActionPreference = 'Stop'
        $out = & (Join-Path $env:windir 'System32\shutdown.exe') "/$Kind" /t 0 /f /d p:0:0 /c 'Requested from RemoteHub' 2>&1
        if ($LASTEXITCODE -ne 0) { throw (($out | Out-String).Trim()) }
        """;

    // gpupdate may ask whether it may restart or sign out (policies that apply only then): the answer is no.
    private const string GroupPolicyScript = """
        $ErrorActionPreference = 'Stop'
        $out = 'N', 'N' | & (Join-Path $env:windir 'System32\gpupdate.exe') /force 2>&1
        "$LASTEXITCODE"
        ($out | Out-String).Trim()
        """;

    // The sync the Settings app's "Sync" button starts: PushLaunch of each MDM enrollment (older builds only have the
    // enrollment's schedule #3).
    private const string IntuneScript = """
        $ErrorActionPreference = 'Stop'
        $tasks = @(Get-ScheduledTask -TaskPath '\Microsoft\Windows\EnterpriseMgmt\*' -ErrorAction SilentlyContinue)
        $sync = @($tasks | Where-Object TaskName -eq 'PushLaunch')
        if ($sync.Count -eq 0) { $sync = @($tasks | Where-Object TaskName -eq 'Schedule #3 created by enrollment client') }
        foreach ($t in $sync) { Start-ScheduledTask -TaskPath $t.TaskPath -TaskName $t.TaskName }
        $sync.Count
        """;

    /// <summary>
    /// Whether a connection's menu offers them: where its PowerShell (agent or WinRM) is available, as for the Tools, and
    /// this PC's policy allows them over that transport (AllowAgentDeviceActions / AllowRemotingDeviceActions).
    /// </summary>
    public static bool Offered(bool powerShellAvailable, bool viaAgent, Agent.IPolicySource? source = null) =>
        powerShellAvailable && Services.Features.IsAllowed(Services.RemoteTool.DeviceActions, viaAgent, source);

    /// <summary>Every script, for the tests that parse them (and run the one that only reads).</summary>
    internal static IReadOnlyList<(string Name, string Script)> Scripts =>
    [
        (nameof(SignedInScript), SignedInScript), (nameof(SessionScript), SessionScript), (nameof(PowerScript), PowerScript),
        (nameof(GroupPolicyScript), GroupPolicyScript), (nameof(IntuneScript), IntuneScript),
    ];

    /// <summary>The users the action reaches or interrupts are looked up first, to name them in the confirmation.</summary>
    public static bool NamesUsers(DeviceAction a) => a is DeviceAction.SignOut or DeviceAction.Restart or DeviceAction.ShutDown;

    public static List<SignedInUser> SignedIn(Runspace rs) => ParseUsers(RemoteFs.Invoke(rs, SignedInScript).Select(o => o?.ToString() ?? ""));

    /// <summary>Runs the action. A refusal of the whole action (no such tool, access denied) throws <see cref="RemoteCommandException"/>.</summary>
    public static DeviceActionResult Run(Runspace rs, DeviceAction a, string device, string? message = null)
    {
        switch (a)
        {
            case DeviceAction.Lock or DeviceAction.SignOut or DeviceAction.SendMessage:
                var text = a == DeviceAction.SendMessage ? CleanMessage(message ?? "") : "";
                var lines = RemoteFs.Invoke(rs, SessionScript, ("Action", a.ToString()), ("Text", text), ("Seconds", MessageSeconds));
                return DescribeSessions(a, ParseOutcomes(lines.Select(o => o?.ToString() ?? "")), device);
            case DeviceAction.Restart or DeviceAction.ShutDown:
                RemoteFs.Invoke(rs, PowerScript, ("Kind", a == DeviceAction.Restart ? "r" : "s"));
                return a == DeviceAction.Restart
                    ? new(true, $"{device} is restarting. Its connections come back once Windows has started again.")
                    : new(true, $"{device} is shutting down.");
            case DeviceAction.GroupPolicyUpdate:
                var gp = RemoteFs.Invoke(rs, GroupPolicyScript).Select(o => o?.ToString() ?? "").ToList();
                return DescribeGroupPolicy(int.TryParse(gp.FirstOrDefault(), out var exit) ? exit : -1, string.Join("\n", gp.Skip(1)), device);
            default:
                var started = RemoteFs.Invoke(rs, IntuneScript).Select(o => int.TryParse(o?.ToString(), out var n) ? n : 0).FirstOrDefault();
                return DescribeIntune(started, device);
        }
    }

    /// <summary>
    /// A message as msg.exe gets it: one line, at most <see cref="MaxMessageLength"/> characters. Windows PowerShell passes
    /// it on the command line, where a straight double quote or a backslash before the closing quote would end the text
    /// early, so quotes become typographic ones and trailing backslashes go.
    /// </summary>
    public static string CleanMessage(string text)
    {
        var t = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Replace('"', '”').Trim().TrimEnd('\\').Trim();
        return t.Length > MaxMessageLength ? t[..MaxMessageLength] : t;
    }

    public static List<SignedInUser> ParseUsers(IEnumerable<string> lines) =>
        lines.Select(l => l.Split('\t'))
             .Where(f => f.Length == 2 && int.TryParse(f[0], out _) && f[1].Length > 0)
             .Select(f => new SignedInUser(int.Parse(f[0]), f[1]))
             .ToList();

    public static List<SessionOutcome> ParseOutcomes(IEnumerable<string> lines) =>
        lines.Select(l => l.Split('\t', 4))
             .Where(f => f.Length == 4 && int.TryParse(f[0], out _) && int.TryParse(f[2], out _))
             .Select(f => new SessionOutcome(int.Parse(f[0]), f[1], int.Parse(f[2]), f[3]))
             .ToList();

    /// <summary>The menu entry, as the connection's Device actions show it.</summary>
    public static string Header(DeviceAction a) => a switch
    {
        DeviceAction.Lock => "Lock",
        DeviceAction.SendMessage => "Send message…",
        DeviceAction.SignOut => "Sign out users…",
        DeviceAction.Restart => "Restart…",
        DeviceAction.ShutDown => "Shut down…",
        DeviceAction.GroupPolicyUpdate => "Update Group Policy",
        _ => "Sync with Intune",
    };

    /// <summary>Its dialogs' title, a verb and what it acts on, as the other tools' dialogs ("Restart service").</summary>
    public static string Title(DeviceAction a) => a switch
    {
        DeviceAction.Lock => "Lock device",
        DeviceAction.SendMessage => "Send message",
        DeviceAction.SignOut => "Sign out users",
        DeviceAction.Restart => "Restart device",
        DeviceAction.ShutDown => "Shut down device",
        _ => Header(a),
    };

    public static string Tip(DeviceAction a) => a switch
    {
        DeviceAction.Lock => "Locks the screen of everyone signed in; an RDP user's window closes and their session waits for them",
        DeviceAction.SendMessage => "Shows a message on the screen of everyone signed in",
        DeviceAction.SignOut => "Signs out the users signed in – their unsaved work is lost",
        DeviceAction.Restart => "Restarts the device at once – programs are closed without asking to save",
        DeviceAction.ShutDown => "Shuts the device down – RemoteHub can't switch it on again",
        DeviceAction.GroupPolicyUpdate => "Applies the device's Group Policy again (gpupdate /force)",
        _ => "Asks Intune (or another MDM) for the device's policies and apps now, like Sync in the Settings app",
    };

    /// <summary>The confirmation for an action that interrupts users (null: none – it runs at once).</summary>
    public static (string Title, string Text, string Button)? Confirmation(DeviceAction a, string device, IReadOnlyList<SignedInUser> users)
    {
        var who = users.Count == 0 ? "Nobody is signed in."
            : $"Signed in: {string.Join(", ", users.Select(u => u.Account).Distinct(StringComparer.OrdinalIgnoreCase))} – unsaved work in their programs is lost.";
        return a switch
        {
            DeviceAction.SignOut => (Title(a), $"Sign out the users on {device}?\n\n{who}", "Sign out"),
            DeviceAction.Restart => (Title(a), $"Restart {device} now?\n\n{who}", "Restart"),
            DeviceAction.ShutDown => (Title(a), $"Shut down {device} now?\n\n{who}\n\nRemoteHub can't switch it on again: someone has to, " +
                                                   "or its management board (iDRAC, iLO) or hypervisor.", "Shut down"),
            _ => null,
        };
    }

    /// <summary>Signing out, locking or writing to nobody: said instead of asking or connecting again.</summary>
    public static string NobodySignedIn(DeviceAction a, string device) => a switch
    {
        DeviceAction.Lock => $"Nobody is signed in on {device} – there's nothing to lock.",
        DeviceAction.SendMessage => $"Nobody is signed in on {device} – there's nobody to show the message to.",
        _ => $"Nobody is signed in on {device}.",
    };

    public static DeviceActionResult DescribeSessions(DeviceAction a, IReadOnlyList<SessionOutcome> outcomes, string device)
    {
        if (outcomes.Count == 0) return new(true, NobodySignedIn(a, device));
        var ok = outcomes.Where(o => o.Ok).Select(o => o.Account).ToList();
        var who = string.Join(", ", ok);
        var (done, failed) = a switch
        {
            DeviceAction.Lock => ($"Locked on {device}: {who}.", "couldn't be locked"),
            DeviceAction.SignOut => ($"Signed out on {device}: {who}.", "couldn't be signed out"),
            _ => ($"Message shown on {device} to {who}.", "couldn't be shown the message"),
        };
        var lines = new List<string>();
        if (ok.Count > 0) lines.Add(done);
        foreach (var o in outcomes.Where(o => !o.Ok))
            lines.Add($"{o.Account} (session {o.SessionId}) {failed}" + (o.Output.Length > 0 ? $": {o.Output}" : $" (exit code {o.ExitCode})."));
        return new(ok.Count == outcomes.Count, string.Join("\n\n", lines));
    }

    public static DeviceActionResult DescribeGroupPolicy(int exitCode, string output, string device)
    {
        var text = output.Trim();
        return exitCode == 0
            ? new(true, $"Group Policy was updated on {device}." + (text.Length > 0 ? $"\n\n{text}" : ""))
            : new(false, $"Group Policy couldn't be updated on {device} (gpupdate exit code {exitCode})." + (text.Length > 0 ? $"\n\n{text}" : ""));
    }

    public static DeviceActionResult DescribeIntune(int started, string device) => started > 0
        ? new(true, $"{device} is syncing with Intune. The policies and apps arrive over the next minutes – the device's Settings → Accounts → " +
                    "Access work or school shows the last sync.")
        : new(false, $"{device} isn't enrolled in Intune or another MDM – no enrollment's sync task was found.");

    /// <summary>For the audit log: the result first, as the other entries ("Installed – …"), then the action – never a message's text.</summary>
    public static string AuditDetail(DeviceAction a, DeviceActionResult? result, string? failure) =>
        (failure is not null ? "Failed" : result!.Ok ? "Done" : "Partly done or nothing to do") + $" – {Title(a)}";
}
