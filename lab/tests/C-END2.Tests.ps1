# C-END (2) - automatic sign-in, phase 4 continued: the service's privacy screen over the session it created, and the
# 2 minutes after a dropped connection (docs/credential-provider-design.md sections 9.1 / 9.2; DesktopEndpoint.cs,
# AutoSignInSessions.cs). Harness only, no RemoteHub UI, signed in to the agent as the console account with nobody at
# the console (consent None), as lab/tests/C-END.Tests.ps1 does.
#
# Covers: C-END-09, C-END-10, C-END-11, C-END-12, C-END-13, C-END-14, C-END-15.
# Needs:  one VM (runs on the client and the Server VM alike), the agent MSI built with the provider and signed
#         (build-installer.ps1 -LabSigning -CredentialProvider), Hyper-V network cut (C-END-15).
#
# What the code does (and this file checks):
#  - the service's cover after SignedIn is DesktopPrivacyScreen("Message", BlockLocalInput: true) at policy
#    PrivacyScreen 1 and 2 alike (C-END-09); at 0 there is none and 1106 says so (C-END-10);
#  - after a drop the old helper holds the cover for 2 minutes; Ctrl+Alt+Del at the device offers only Task Manager and
#    Cancel then (privacy-mode-design 5.9 - no Lock) and the 2 minutes run out as usual (C-END-11); a resume restores the service's cover without the PrivacyScreen right
#    (ScreenByService, C-END-12); a failed sign-in drops it (C-END-13);
#  - the 1104 for the service's cover names the service, not the admin (C-END-14);
#  - KNOWN PRODUCT FINDING, tested for the correct behaviour (fails until fixed): C-END-15 (a drop the agent notices first counts as
#    "closed by RemoteHub", DesktopEndpoint.cs:220-230 - the session is locked at once, no resume). The same finding
#    hits the harness' "drop" (C-END-11, C-END-12) whenever the agent sees the aborted socket as a cancelled read.
# Every sign-in starts from the sign-in screen with a restarted agent (the rate limit is 3 per account in 15 minutes).
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $cfg = Get-LabConfig
    $account = $vm.ConsoleAccount
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "c-end2-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "c-end2-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    # bright spots (icons on the sign-in screen) in a band of a picture: their middles, left to right
    function Find-LabIcons([string]$Path, [int]$Top, [int]$Bottom) {
        Add-Type -AssemblyName System.Drawing
        $bmp = [Drawing.Bitmap]::FromFile($Path)
        try {
            $cols = for ($x = 0; $x -lt $bmp.Width; $x += 2) {
                $ys = @(for ($y = [math]::Max(0, $Top); $y -lt [math]::Min($bmp.Height, $Bottom); $y += 2) { $c = $bmp.GetPixel($x, $y); if (($c.R + $c.G + $c.B) / 3 -gt 200) { $y } })
                if ($ys) { [pscustomobject]@{ X = $x; Y = [int](($ys | Measure-Object -Average).Average) } }
            }
            $groups = @(); $cur = @()
            foreach ($c in $cols) { if ($cur -and $c.X - $cur[-1].X -gt 12) { $groups += , $cur; $cur = @() }; $cur += $c }
            if ($cur) { $groups += , $cur }
            foreach ($g in $groups) { if ($g.Count -ge 3) { [pscustomobject]@{ X = [int](($g | Measure-Object X -Average).Average); Y = [int](($g | Measure-Object Y -Average).Average) } } }
        }
        finally { $bmp.Dispose() }
    }
    function Mark([string]$o, [string]$n) { @{ do = 'markFile'; path = (Join-Path $o "$n.txt") } }
    function Park([string]$o, [string]$n, [int]$t = 300) { @{ do = 'waitFile'; path = (Join-Path $o "$n.txt"); timeout = $t } }
    function Reached([string]$o, [string]$n, [int]$t = 300) { Wait-LabHostFile (Join-Path $o "$n.txt") -TimeoutSec $t }
    function Release([string]$o, [string]$n) { Set-Content (Join-Path $o "$n.txt") 'go' }
    function Text($r) { (@($r.statuses | ForEach-Object { "$($_.state): $($_.message)" }) + @($r.errors)) -join "`n" }
    function Shot([string]$name) { $p = Join-Path $EvidenceDir "c-end2-$name.png"; $null = Get-LabScreenshot -Name $VmName -Path $p; [string](Get-LabOcr -Path $p).text }
    # the privacy reports the harness got: [connection, screen, localInputBlocked]
    function Covers($r) { @($r.privacy | ForEach-Object { [pscustomobject]@{ Connection = $_.connection; Screen = $_.privacy.screen; Blocked = [bool]$_.privacy.localInputBlocked } }) }

    # the Windows sessions as WTS lists them (as in C-END.Tests.ps1): id, state, user, console, explorer.exe, LogonUI
    $wts = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class RhLabWts {
    [StructLayout(LayoutKind.Sequential)]
    private struct SessionInfo { public int Id; public IntPtr Station; public int State; }
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr info, out int count);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQuerySessionInformationW(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("kernel32.dll")] public static extern int WTSGetActiveConsoleSessionId();
    private static string Query(int session, int infoClass) {
        IntPtr b; int n;
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out b, out n)) return "";
        try { return Marshal.PtrToStringUni(b) ?? ""; } finally { WTSFreeMemory(b); }
    }
    public static string[] List() {
        var list = new List<string>();
        IntPtr info; int count;
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out info, out count)) return list.ToArray();
        try {
            var size = Marshal.SizeOf(typeof(SessionInfo));
            for (var i = 0; i < count; i++) {
                var s = (SessionInfo)Marshal.PtrToStructure(new IntPtr(info.ToInt64() + i * size), typeof(SessionInfo));
                var user = Query(s.Id, 5);
                list.Add(s.Id + "|" + s.State + "|" + (user.Length > 0 ? Query(s.Id, 7) + "\\" + user : ""));
            }
        } finally { WTSFreeMemory(info); }
        return list.ToArray();
    }
}
'@
    function Sessions {
        Invoke-LabGuest -Name $VmName -ArgumentList $wts -ScriptBlock {
            param($code)
            if (-not ('RhLabWts' -as [type])) { Add-Type -TypeDefinition $code }
            $names = 'Active', 'Connected', 'ConnectQuery', 'Shadow', 'Disconnected', 'Idle', 'Listen', 'Reset', 'Down', 'Init'
            $console = [RhLabWts]::WTSGetActiveConsoleSessionId()
            $explorer = @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'")
            $logonUi = @(Get-Process LogonUI -ErrorAction SilentlyContinue | ForEach-Object SessionId)
            foreach ($line in [RhLabWts]::List()) {
                $id, $st, $user = $line -split '\|', 3
                $id = [int]$id
                [pscustomobject]@{
                    Id = $id; State = $names[[int]$st]; User = $user; Console = ($id -eq $console)
                    Explorer = @($explorer | Where-Object SessionId -eq $id | ForEach-Object { [int]$_.ProcessId }); LogonUI = ($logonUi -contains $id)
                }
            }
        }
    }
    # the console account's sessions (",": the array as it is, also for none or one)
    function Mine { , @(Sessions | Where-Object { $_.User -match "\\$([regex]::Escape($account))$" }) }
    # (Mine, not @(Mine): @() would wrap the array Mine hands back, and an empty list would count as 1)
    function Wait-Gone([int]$TimeoutSec = 90) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do { $left = Mine; if ($left.Count -eq 0) { return , @() }; Start-Sleep -Seconds 3 } while ((Get-Date) -lt $until)
        , $left
    }
    # event 1107 "Automatic sign-in session <n> of <account> locked / signed out (<why>)" since a guest time
    function Wait-Ended([datetime]$Since, [int]$TimeoutSec = 90) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do { $e = @(Get-LabAgentEvent -Name $VmName -Since $Since -Id 1107); if ($e.Count -gt 0) { return , $e }; Start-Sleep -Seconds 3 } while ((Get-Date) -lt $until)
        , @()
    }
    function Wait-DropLogged([datetime]$Since, [int]$TimeoutSec = 60) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            $l = @(Get-LabAgentLog -Name $VmName -Since $Since | Where-Object { $_ -match 'Automatic sign-in: the connection to session \d+ dropped' })
            if ($l.Count -gt 0) { return $l[0] }
            Start-Sleep -Seconds 3
        } while ((Get-Date) -lt $until)
        $null
    }
    # the guest time a log line starts with
    function LineTime([string]$Line) { if ($Line -match '^(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2})') { [datetime]$Matches[1] } else { $null } }
    # every sign-in: automatic sign-in allowed for Administrators by policy (as IT would set it - the install keeps its own
    # choice in agent.json) (+ extra values / rights), a fresh agent, nobody at the console
    function Prepare([hashtable]$Policy = @{}, [hashtable]$Rights = @{}) {
        Get-Job | Where-Object State -eq 'Running' | Stop-Job
        Get-Job | Remove-Job -Force
        $values = @{ NoUserBehavior = 'AutoSignIn' }
        foreach ($k in $Policy.Keys) { $values[$k] = $Policy[$k] }
        $r = @{ AutoSignIn = @('S-1-5-32-544') }
        foreach ($k in $Rights.Keys) { $r[$k] = $Rights[$k] }
        Set-LabAgentPolicy -Name $VmName -Clear -Values $values -Rights $r
        $null = Restart-LabAgent -Name $VmName -Pin $pin
        Disconnect-LabConsole -Name $VmName
    }
    # signed in automatically, as RemoteHub does it (end action Lock unless the policy says otherwise)
    $signIn = @(
        @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'None'; autoSignIn = $true }
        @{ do = 'wait'; anyOf = @('SignedIn', 'SignInFailed', 'Failed', 'Denied'); timeout = 120 }, @{ do = 'sleep'; seconds = 10 })
    # the device's own keyboard after SignedIn: a marker in the signed-in session records what arrives
    # whether the last LocalKeys had its marker window up (none counted is no proof otherwise): Shown, Why
    $markerState = @{ Shown = $null; Why = $null }
    function LocalKeys {
        $marker = Start-LabMarker -Name $VmName -Account Console
        $markerState.Shown = $marker.Shown
        $markerState.Why = $marker.Why
        try {
            Start-Sleep -Seconds 2
            $count = @(Get-LabMarker -Name $VmName -Marker $marker).Count
            Send-LabText -Name $VmName -Text 'local'
            Start-Sleep -Seconds 3
            , @(Get-LabMarker -Name $VmName -Marker $marker | Select-Object -Skip $count | Where-Object e -eq 'char')
        }
        finally { Stop-LabInSession -Name $VmName -Task $marker.Task }
    }
    # signed in, looked at, closed: the record plus what the console showed and what its keyboard reached
    function Covered-Run([string]$Name) {
        $o = Out $Name
        $x = [ordered]@{}
        $since = Get-LabGuestTime -Name $VmName
        $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps ($signIn + @((Mark $o 'signed'), (Park $o 'close'), @{ do = 'close' }))
        Reached $o 'signed'
        $x.Signed = Mine
        $p = Join-Path $EvidenceDir "c-end2-$Name-console.png"
        $x.Blank = (Get-LabScreenshot -Name $VmName -Path $p).IsBlank
        $x.ConsoleText = [string](Get-LabOcr -Path $p).text
        $x.LocalKeys = LocalKeys
        $x.MarkerShown = $markerState.Shown
        $x.MarkerWhy = $markerState.Why
        Release $o 'close'
        $x.R = Receive-LabDesktop $job
        $x.SignedInEvent = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1106 | Select-Object -Last 1)
        $x.PrivacyEvents = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1104)
        $x
    }

    # -Clean: the sessions and autosignin.json must be only this file's (no earlier automatic sign-ins)
    $init = Initialize-LabVm -Name $VmName -Agent -AutoSignIn $true -Clean
    $install = $init.Install
    $pin = $init.Pin
}

AfterAll {
    Get-Job | Where-Object State -eq 'Running' | Stop-Job
    Get-Job | Remove-Job -Force
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'c-end2-agent-logs')
    # the console account signed out / locked, the network cut: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'C-END2 <VmName>' -Tag Agent, 'C-END' {

    Context 'covered at policy level 1' {
        BeforeAll {
            # PrivacyScreen = 1: the admin may hide the screen but not block the device's input - the service's cover blocks it anyway
            Prepare @{ PrivacyScreen = 1 }
            $l1 = Covered-Run 'level1'
            Save 'level1' $l1
        }

        It 'C-END-09 PrivacyScreen=1: after SignedIn the console''s own keys still don''t reach the session' -Tag 'C-END-09' {
            Text $l1.R | Should -Match 'SignedIn: Signed in as'
            $l1.R.hello.rights.privacyInputBlock | Should -BeFalse -Because 'level 1: the admin may not block input themselves'
            ($l1.SignedInEvent.Message -join "`n") | Should -Match 'privacy screen on'
            @(Covers $l1.R | Where-Object { $_.Screen -eq 'Message' -and $_.Blocked }).Count | Should -BeGreaterThan 0
            $l1.ConsoleText | Should -Match 'signed in remotely'
            $l1.MarkerShown | Should -BeTrue -Because "lab: the marker window must be up to count keys (c-end2-level1.json MarkerWhy: $($l1.MarkerWhy))"
            @($l1.LocalKeys).Count | Should -Be 0 -Because 'the device''s keyboard is blocked in a session automatic sign-in created, at level 1 too'
        }

        # Was a product finding (credential-provider-design 9.1): the helper's report of the service's cover was audited as
        # "<admin> from <source> turned the privacy screen on". It now reads "the service (automatic sign-in of ...)".
        It 'C-END-14 the 1104 for the service''s cover doesn''t say the admin turned it on' -Tag 'C-END-14' {
            Text $l1.R | Should -Match 'SignedIn: Signed in as'
            @($l1.R.steps | Where-Object do -eq 'privacy') | Should -BeNullOrEmpty -Because 'the admin never asked for a privacy screen'
            @($l1.PrivacyEvents | Where-Object { $_.Message -match 'privacy screen on' }).Count | Should -BeGreaterThan 0 -Because 'the cover is audited'
            $byAdmin = @($l1.PrivacyEvents | Where-Object { $_.Message -match "$([regex]::Escape($account)) from .* turned the privacy screen on" -and $_.Message -notmatch 'automatic sign-in' })
            ($byAdmin.Message -join "`n") | Should -BeNullOrEmpty -Because 'the service turned it on, not the admin'
        }
    }

    Context 'no privacy screen available' {
        BeforeAll {
            Prepare @{ PrivacyScreen = 0 }
            $l0 = Covered-Run 'level0'
            Save 'level0' $l0
        }

        It 'C-END-10 PrivacyScreen=0: signed in, 1106 ", no privacy screen on this device", the console shows the desktop, no banner' -Tag 'C-END-10' {
            # run 20261016-212514 (Server 2025): SignInFailed "didn't take the sign-in" after 4 s while Windows did sign
            # the account in - the provider's result never reached the agent (see the test document's findings)
            $inAnyway = @($l0.Signed | Where-Object { $_.State -eq 'Active' -and @($_.Explorer).Count -gt 0 }).Count -gt 0
            Text $l0.R | Should -Match 'SignedIn: Signed in as' -Because "the sign-in went through at the device: $inAnyway"
            ($l0.SignedInEvent.Message -join "`n") | Should -Match ', no privacy screen on this device\)'
            @(Covers $l0.R | Where-Object { $_.Screen -and $_.Screen -ne 'Off' }) | Should -BeNullOrEmpty
            @($l0.Signed | Where-Object State -eq 'Active').Count | Should -Be 1
            @($l0.Signed[0].Explorer).Count | Should -BeGreaterThan 0
            $l0.Signed[0].LogonUI | Should -BeFalse
            $l0.Blank | Should -BeFalse
            $l0.ConsoleText | Should -Not -Match 'signed in remotely'
            $l0.ConsoleText | Should -Not -Match 'IT administrator is working'
            $l0.ConsoleText | Should -Not -Match '\bDisconnect\b' -Because 'no banner for a session automatic sign-in created'
        }
    }

    Context 'locked at the device during the hold' {
        BeforeAll {
            Prepare
            $k = [ordered]@{}
            $o = Out 'lock-hold'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps ($signIn + @(@{ do = 'drop' }, (Mark $o 'dropped')))
            Reached $o 'dropped'
            $k.DropLine = Wait-DropLogged (Get-LabGuestTime -Name $VmName).AddMinutes(-1)
            $k.R = Receive-LabDesktop $job
            $k.Before = Mine
            # the person at the device: Ctrl+Alt+Del - the one thing the cover can't block. While it blocks input the screen
            # offers only Task Manager and Cancel (privacy-mode-design 5.9: Lock and Win+L are hidden too), so nobody can
            # lock or sign out there; the screen just stays up.
            Send-LabCtrlAltDel -Name $VmName
            $k.CadText = ''
            $until = (Get-Date).AddSeconds(30)
            do {
                Start-Sleep -Seconds 2
                $k.CadText = Shot 'lock-hold-cad'
            } while ($k.CadText -notmatch 'Task Manager' -and (Get-Date) -lt $until)
            $lockedAt = Get-LabGuestTime -Name $VmName
            $k.Locked = Mine
            $k.LockedText = Shot 'lock-hold-locked'
            # not signed out at once: nothing in the next 30 s ...
            $k.Early = @(Wait-Ended $lockedAt 30)
            # ... but when the 2 minutes after the drop run out
            $dropAt = LineTime $k.DropLine
            $k.Ended = @(Wait-Ended $(if ($dropAt) { $dropAt } else { $lockedAt.AddMinutes(-1) }) 180)
            # both times from the agent's log, in the device's own clock: the event's time arrives converted to this host's
            # time zone (run 20261017-160133: 9 h apart on Server 2025)
            if ($dropAt -and $k.Ended.Count -gt 0) {
                $k.SignOutLine = @(Get-LabAgentLog -Name $VmName -Since $dropAt | ForEach-Object { "$_" } |
                        Where-Object { $_ -match 'signed out \(connection lost and not continued in time\)' })[0]
                $signedOutAt = LineTime $k.SignOutLine
                if ($signedOutAt) { $k.HeldSeconds = [math]::Round(($signedOutAt - $dropAt).TotalSeconds) }
            }
            $k.Gone = Wait-Gone
            Save 'lock-hold' $k
        }

        It 'C-END-11 Ctrl+Alt+Del at the console during the 2 minutes: only Task Manager and Cancel, not signed out at once, signed out when they run out' -Tag 'C-END-11' {
            Text $k.R | Should -Match 'SignedIn: Signed in as'
            $k.DropLine | Should -Not -BeNullOrEmpty -Because 'the agent kept the cover after the drop'
            $k.CadText | Should -Match 'Task Manager' -Because 'the Ctrl+Alt+Del screen came up'
            $k.CadText | Should -Not -Match '\b(Lock|Sign out|Switch user|Change a password)\b' -Because 'its actions are hidden while the cover blocks input'
            @($k.Locked).Count | Should -Be 1 -Because 'the session is still there with the Ctrl+Alt+Del screen up'
            $k.Locked[0].LogonUI | Should -BeTrue -Because 'the Ctrl+Alt+Del screen runs as LogonUI in the session'
            @($k.Early) | Should -BeNullOrEmpty -Because 'the cover''s helper keeps running: not signed out at once'
            ($k.Ended.Message -join "`n") | Should -Match "Automatic sign-in session \d+ of .*$([regex]::Escape($account)) signed out"
            $k.HeldSeconds | Should -BeGreaterOrEqual 115
            $k.HeldSeconds | Should -BeLessOrEqual 150
            @($k.Gone).Count | Should -Be 0
        }
    }

    Context 'resume brings the cover back' {
        BeforeAll {
            # the operator may not use the privacy screen (Rights\PrivacyScreen = a group they aren't in)
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock { if (-not (Get-LocalGroup 'RH Others' -ErrorAction SilentlyContinue)) { New-LocalGroup 'RH Others' } }
            Prepare -Rights @{ PrivacyScreen = @('RH Others') }
            $b = [ordered]@{}
            $o = Out 'resume-cover'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps ($signIn + @(
                    (Mark $o 'signed'), @{ do = 'drop' }, @{ do = 'sleep'; seconds = 5 }, @{ do = 'reconnect' }
                    @{ do = 'start'; session = 'console'; consent = 'None'; resume = 'last' }, @{ do = 'wait'; anyOf = @('Connected', 'Failed', 'Denied'); timeout = 90 }
                    @{ do = 'sleep'; seconds = 10 }, @{ do = 'frame'; out = 'resumed.png' }, (Mark $o 'resumed'), (Park $o 'close'), @{ do = 'close' }))
            Reached $o 'signed'
            $b.Before = Mine
            $since = Get-LabGuestTime -Name $VmName
            Reached $o 'resumed' 240
            $b.After = Mine
            $b.ConsoleText = Shot 'resume-cover-console'
            $b.LocalKeys = LocalKeys
            $b.MarkerShown = $markerState.Shown
            $b.MarkerWhy = $markerState.Why
            $b.Resumed = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1103)
            Release $o 'close'
            $b.R = Receive-LabDesktop $job
            Save 'resume-cover' $b
        }

        It 'C-END-12 an operator without Rights\PrivacyScreen resumes: the service''s privacy screen is back' -Tag 'C-END-12' {
            Text $b.R | Should -Match 'SignedIn: Signed in as'
            $b.R.hello.rights.privacyScreen | Should -BeFalse -Because 'lab: the operator has no PrivacyScreen right'
            @($b.R.statuses | Where-Object { $_.connection -eq 2 -and $_.state -eq 'Connected' }).Count | Should -BeGreaterThan 0
            @($b.Resumed).Count | Should -BeGreaterThan 0 -Because 'event 1103: continued'
            $b.After[0].Id | Should -Be $b.Before[0].Id
            @(Covers $b.R | Where-Object { $_.Connection -eq 2 -and $_.Screen -eq 'Message' -and $_.Blocked }).Count | Should -BeGreaterThan 0 -Because 'the resumed connection''s helper covers the session again'
            $b.ConsoleText | Should -Match 'signed in remotely'
            $b.MarkerShown | Should -BeTrue -Because "lab: the marker window must be up to count keys (c-end2-resume-cover.json MarkerWhy: $($b.MarkerWhy))"
            @($b.LocalKeys).Count | Should -Be 0
        }
    }

    Context 'failed sign-in removes the cover' {
        BeforeAll {
            Prepare
            $f = [ordered]@{}
            $o = Out 'failed-cover'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'None'; autoSignIn = $true; signInPassword = 'not-the-password-' + [guid]::NewGuid().ToString('N').Substring(0, 6) }
                @{ do = 'wait'; anyOf = @('SignedIn', 'SignInFailed', 'Failed', 'Denied'); timeout = 120 }, (Mark $o 'failed'), (Park $o 'byhand' 400)
                @{ do = 'sleep'; seconds = 10 }, @{ do = 'frame'; out = 'byhand.png' }, (Mark $o 'pictured'), (Park $o 'close'), @{ do = 'close' })
            Reached $o 'failed' 180
            # Windows' message about the wrong password: OK; then the console account signs in by hand (the password
            # box has the focus again)
            Start-Sleep -Seconds 3
            Send-LabKey -Name $VmName -Key Enter
            Start-Sleep -Seconds 3
            # RemoteHub's tile stays selected after the failure (run 20261015-225719: "Signing in ...\Admin...", no password
            # box): "Sign-in options", then the password's icon - the first of the row below it (Windows lists the
            # password provider first)
            $s1 = Join-Path $EvidenceDir 'c-end2-byhand-1-after-ok.png'
            $null = Get-LabScreenshot -Name $VmName -Path $s1
            # OCR may read trailing dots ("options..", run 20261019-193627 W11): the word only has to start with it
            $words1 = @((Get-LabOcr -Path $s1).words)
            $f.OptionsAt = @($words1 | Where-Object { $_.text -match '^options' } |
                    ForEach-Object { "$([int]($_.x + $_.w / 2)),$([int]($_.y + $_.h / 2))" }) | Select-Object -First 1
            # Since the provider drops its tile after a failure (C-END-13), Windows' own password box is there: its
            # "Password" placeholder is clicked and typed into (run 20261021-062735 missed "Sign-in options" and typed
            # into RemoteHub's tile, which has no box)
            $f.PasswordAt = @($words1 | Where-Object { $_.text -ceq 'Password' } |
                    ForEach-Object { "$([int]($_.x + $_.w / 2)),$([int]($_.y + $_.h / 2))" }) | Select-Object -First 1
            if ($f.PasswordAt) {
                $px, $py = [int[]]("$($f.PasswordAt)" -split ',')
                Send-LabMouse -Name $VmName -X $px -Y $py
                Start-Sleep -Seconds 1
            }
            elseif ($f.OptionsAt) {
                $ox, $oy = [int[]]("$($f.OptionsAt)" -split ',')
                Send-LabMouse -Name $VmName -X $ox -Y $oy
                Start-Sleep -Seconds 2
                $s2 = Join-Path $EvidenceDir 'c-end2-byhand-2-options.png'
                $null = Get-LabScreenshot -Name $VmName -Path $s2
                # the icons: bright spots in the band below the text, left to right
                # (only those centred under the text: the account tiles at the left edge are in the same band)
                $f.Icons = @(Find-LabIcons -Path $s2 -Top ($oy + 15) -Bottom ($oy + 90) | Where-Object { [math]::Abs($_.X - $ox) -lt 150 })
                if ($f.Icons) { Send-LabMouse -Name $VmName -X $f.Icons[0].X -Y $f.Icons[0].Y; Start-Sleep -Seconds 2 }
                $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir 'c-end2-byhand-2b-password.png')
            }
            else { Send-LabCtrlAltDel -Name $VmName; Start-Sleep -Seconds 3 }
            Send-LabText -Name $VmName -Text $cfg.Password
            $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir 'c-end2-byhand-3-typed.png')
            Send-LabKey -Name $VmName -Key Enter
            Start-Sleep -Seconds 5
            $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir 'c-end2-byhand-4-entered.png')
            $f.ByHandError = $null
            try { Wait-LabDesktop -Name $VmName -Account Console -TimeoutSec 120 } catch { $f.ByHandError = "$_" }
            Start-Sleep -Seconds 10   # the connection follows the console into the new session
            $f.Signed = Mine
            $f.ConsoleText = Shot 'failed-cover-console'
            $f.LocalKeys = LocalKeys
            $f.MarkerShown = $markerState.Shown
            $f.MarkerWhy = $markerState.Why
            Release $o 'byhand'
            Reached $o 'pictured' 120
            Release $o 'close'
            $f.R = Receive-LabDesktop $job
            Save 'failed-cover' $f
        }

        It 'C-END-13 wrong password: the service''s privacy screen is dropped, a later sign-in by hand isn''t covered' -Tag 'C-END-13' {
            Text $f.R | Should -Match 'SignInFailed: The password was wrong'
            $f.ByHandError | Should -BeNullOrEmpty -Because 'lab: the console account signed in by hand'
            @($f.Signed | Where-Object State -eq 'Active').Count | Should -Be 1
            @(Covers $f.R | Where-Object { $_.Screen -and $_.Screen -ne 'Off' }) | Should -BeNullOrEmpty -Because 'no helper got the service''s cover'
            $f.ConsoleText | Should -Not -Match 'signed in remotely'
            $f.MarkerShown | Should -BeTrue -Because "lab: the marker window must be up to count keys (c-end2-failed-cover.json MarkerWhy: $($f.MarkerWhy))"
            @($f.LocalKeys).Count | Should -BeGreaterThan 0 -Because 'the device''s keyboard isn''t blocked'
        }
    }

    Context 'the agent notices the drop first' {
        BeforeAll {
            Prepare
            $d = [ordered]@{}
            $o = Out 'late-resume'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -TimeoutSec 900 -Steps ($signIn + @(
                    (Mark $o 'signed'), (Park $o 'back' 600), @{ do = 'drop' }, @{ do = 'reconnect' }
                    @{ do = 'start'; session = 'console'; consent = 'None'; resume = 'last' }, @{ do = 'wait'; anyOf = @('Connected', 'Failed', 'Denied'); timeout = 90 }
                    @{ do = 'sleep'; seconds = 5 }, @{ do = 'frame'; out = 'resumed.png' }, (Mark $o 'resumed'), (Park $o 'close'), @{ do = 'close' }))
            Reached $o 'signed'
            $d.Before = Mine
            $cutAt = Get-LabGuestTime -Name $VmName
            $cut = Disconnect-LabNetwork -Name $VmName
            try {
                # 80 s: longer than the agent needs to notice the lost connection (keep-alive / TCP), well within the 2 minutes
                Start-Sleep -Seconds 40
                $d.CutText = Shot 'late-resume-cut'
                Start-Sleep -Seconds 40
            }
            finally { Connect-LabNetwork -Name $VmName -Switches $cut }
            $d.During = Mine
            Start-Sleep -Seconds 5
            Release $o 'back'
            Reached $o 'resumed' 240
            $d.After = Mine
            $d.AfterText = Shot 'late-resume-after'
            $d.DropLine = @(Get-LabAgentLog -Name $VmName -Since $cutAt | Where-Object { $_ -match 'Automatic sign-in: the connection to session \d+ dropped' })[0]
            $d.DesktopEnded = @(Get-LabAgentEvent -Name $VmName -Since $cutAt -Id 1101)
            $d.EndedBeforeClose = @(Get-LabAgentEvent -Name $VmName -Since $cutAt -Id 1107)
            $d.Resumed = @(Get-LabAgentEvent -Name $VmName -Since $cutAt -Id 1103)
            Release $o 'close'
            $d.R = Receive-LabDesktop $job
            Save 'late-resume' $d
        }

        # Known product bug (D-RES-10, DesktopEndpoint.cs:220-230): when the agent notices the drop first, the client
        # loop ends cancelled, not faulted - "closed by RemoteHub", the session is locked at once and the resume token
        # revoked. Tested for the correct behaviour: fails until fixed.
        It 'C-END-15 cut for 80 s, resumed within 2 minutes: same session, no lock' -Tag 'C-END-15' {
            Text $d.R | Should -Match 'SignedIn: Signed in as'
            ($d.DesktopEnded.Message -join "`n") | Should -Not -Match 'closed by RemoteHub' -Because 'the connection was lost, not ended on purpose'
            $d.DropLine | Should -Not -BeNullOrEmpty -Because 'the agent kept the cover for 2 minutes'
            @($d.EndedBeforeClose) | Should -BeNullOrEmpty -Because 'no lock and no sign-out while resumed in time'
            @($d.During).Count | Should -Be 1
            $d.During[0].Id | Should -Be $d.Before[0].Id
            $d.During[0].State | Should -Be 'Active' -Because 'not locked (disconnected) during the cut'
            $resumed = @($d.R.statuses | Where-Object { $_.connection -eq 2 })
            ($resumed | ForEach-Object { "$($_.state): $($_.message)" }) -join "`n" | Should -Match 'Continuing on the device'
            @($resumed | Where-Object state -eq 'Connected').Count | Should -BeGreaterThan 0
            @($d.Resumed).Count | Should -BeGreaterThan 0 -Because 'event 1103: continued'
            @($d.After).Count | Should -Be 1
            $d.After[0].Id | Should -Be $d.Before[0].Id
            $d.After[0].State | Should -Be 'Active'
            $d.After[0].LogonUI | Should -BeFalse -Because 'not locked'
            (@($d.After[0].Explorer) -join ',') | Should -Be (@($d.Before[0].Explorer) -join ',') -Because 'the same desktop'
            $d.AfterText | Should -Match 'signed in remotely'
        }
    }
}
