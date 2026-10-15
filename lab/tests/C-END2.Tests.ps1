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
#  - after a drop the old helper holds the cover for 2 minutes; a lock at the device (Ctrl+Alt+Del -> Lock) is left for
#    the 2 minutes to run out (C-END-11); a resume restores the service's cover without the PrivacyScreen right
#    (ScreenByService, C-END-12); a failed sign-in drops it (C-END-13);
#  - KNOWN PRODUCT FINDINGS, tested for the correct behaviour (they fail until fixed): C-END-14 (1104 names the admin
#    for the service's cover, DesktopEndpoint.cs:768-771) and C-END-15 (a drop the agent notices first counts as
#    "closed by RemoteHub", DesktopEndpoint.cs:220-230 - the session is locked at once, no resume).
# Every sign-in starts from the sign-in screen with a restarted agent (the rate limit is 3 per account in 15 minutes).
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
    function Wait-Gone([int]$TimeoutSec = 90) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do { $left = @(Mine); if ($left.Count -eq 0) { return , @() }; Start-Sleep -Seconds 3 } while ((Get-Date) -lt $until)
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
    # every sign-in: the policy the install wrote (+ extra values / rights), a fresh agent, nobody at the console
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
    function LocalKeys {
        $marker = Start-LabMarker -Name $VmName -Account Console
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
        Release $o 'close'
        $x.R = Receive-LabDesktop $job
        $x.SignedInEvent = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1106 | Select-Object -Last 1)
        $x.PrivacyEvents = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1104)
        $x
    }

    Reset-LabVm -Name $VmName
    $install = Install-LabAgent -Name $VmName -AutoSignIn $true
    $pin = $install.pin
}

AfterAll {
    Get-Job | Where-Object State -eq 'Running' | Stop-Job
    Get-Job | Remove-Job -Force
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'c-end2-agent-logs')
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
            @($l1.LocalKeys).Count | Should -Be 0 -Because 'the device''s keyboard is blocked in a session automatic sign-in created, at level 1 too'
        }

        # Product finding (credential-provider-design 9.1, DesktopEndpoint.cs:768-771): today the helper's report of the
        # service's cover is audited as "<admin> from <source> turned the privacy screen on" - fails until fixed.
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
            Text $l0.R | Should -Match 'SignedIn: Signed in as'
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
            # the person at the device: Ctrl+Alt+Del, then "Lock" on Windows' own screen (not covered, clicks reach it)
            Send-LabCtrlAltDel -Name $VmName
            Start-Sleep -Seconds 4
            $cad = Join-Path $EvidenceDir 'c-end2-lock-hold-cad.png'
            $null = Get-LabScreenshot -Name $VmName -Path $cad
            $k.LockAt = Find-LabPictureText -Path $cad -Text 'Lock'
            if ($k.LockAt) { $xy = $k.LockAt -split ','; Send-LabMouse -Name $VmName -X ([int]$xy[0]) -Y ([int]$xy[1]) }
            Start-Sleep -Seconds 5
            $lockedAt = Get-LabGuestTime -Name $VmName
            $k.Locked = Mine
            $k.LockedText = Shot 'lock-hold-locked'
            # not signed out at once: nothing in the next 30 s ...
            $k.Early = Wait-Ended $lockedAt 30
            # ... but when the 2 minutes after the drop run out
            $dropAt = LineTime $k.DropLine
            $k.Ended = Wait-Ended $(if ($dropAt) { $dropAt } else { $lockedAt.AddMinutes(-1) }) 180
            if ($dropAt -and $k.Ended.Count -gt 0) { $k.HeldSeconds = [math]::Round(([datetime]$k.Ended[0].Time - $dropAt).TotalSeconds) }
            $k.Gone = Wait-Gone
            Save 'lock-hold' $k
        }

        It 'C-END-11 Ctrl+Alt+Del -> Lock at the console during the 2 minutes: not signed out at once, signed out when they run out' -Tag 'C-END-11' {
            Text $k.R | Should -Match 'SignedIn: Signed in as'
            $k.DropLine | Should -Not -BeNullOrEmpty -Because 'the agent kept the cover after the drop'
            $k.LockAt | Should -Not -BeNullOrEmpty -Because 'lab: "Lock" on the Ctrl+Alt+Del screen'
            @($k.Locked).Count | Should -Be 1 -Because 'still there right after the lock'
            $k.Locked[0].LogonUI | Should -BeTrue -Because 'the session is locked'
            @($k.Early) | Should -BeNullOrEmpty -Because 'a lock at the device is safe: not signed out at once'
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
            Send-LabText -Name $VmName -Text $cfg.Password
            Send-LabKey -Name $VmName -Key Enter
            $f.ByHandError = $null
            try { Wait-LabDesktop -Name $VmName -Account Console -TimeoutSec 120 } catch { $f.ByHandError = "$_" }
            Start-Sleep -Seconds 10   # the connection follows the console into the new session
            $f.Signed = Mine
            $f.ConsoleText = Shot 'failed-cover-console'
            $f.LocalKeys = LocalKeys
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
