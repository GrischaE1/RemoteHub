# C-END - automatic sign-in, phase 4: what happens at the device after the agent signed the console account in and the
# connection ends (docs/credential-provider-design.md section 9, AutoSignInSessions.cs, DesktopEndpoint.cs). Harness only,
# no RemoteHub UI: the desktop harness (DesktopHarness.cs) signs in to the agent as the console account - the account it
# signs in at the console - with nobody signed in there, so nobody is asked (consent None). The agent MSI under test must
# be built with the provider and signed (build-installer.ps1 -LabSigning -CredentialProvider). Runs on both VMs.
#
# What the code does (and this file checks):
#  - after SignedIn the new session is covered by the service's privacy screen (style Message, device input blocked);
#  - ended on purpose: the end action - "Lock" = WTSDisconnectSession on client AND Server (the session is disconnected,
#    the console shows the sign-in / lock screen; the audit says "locked" on both), "SignOut" = WTSLogoffSession;
#    policy AutoSignInEndAction wins over what RemoteHub sends; event 1107;
#  - dropped: the helper keeps the cover for 2 minutes (AutoSignInSessions.HoldAfterDrop), a resume takes it over,
#    otherwise signed out; the cover ending sooner signs out at once (no banner since 0.18.10 - SignedInRemotely);
#  - the service killed: autosignin.json is dealt with at the next start (dropped -> signed out, else the end action).
# KNOWN PRODUCT FINDING (D-RES-10 / C-END-15): when the agent sees a drop as a cancelled read (keep-alive timeout, or the
# request aborted before the read fails), it counts as "closed by RemoteHub" and the session is locked at once - then
# C-END-06, -07 and -08 fail at their DropLine (DesktopEndpoint.cs main loop, fromClient cancelled, not faulted).
# Every sign-in starts from the sign-in screen with a restarted agent (the rate limit is 3 per account in 15 minutes).
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $account = $vm.ConsoleAccount
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "c-end-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "c-end-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Mark([string]$o, [string]$n) { @{ do = 'markFile'; path = (Join-Path $o "$n.txt") } }
    function Park([string]$o, [string]$n, [int]$t = 300) { @{ do = 'waitFile'; path = (Join-Path $o "$n.txt"); timeout = $t } }
    function Reached([string]$o, [string]$n, [int]$t = 300) { Wait-LabHostFile (Join-Path $o "$n.txt") -TimeoutSec $t }
    function Release([string]$o, [string]$n) { Set-Content -Encoding ASCII (Join-Path $o "$n.txt") 'go' }
    function Text($r) { (@($r.statuses | ForEach-Object { "$($_.state): $($_.message)" }) + @($r.errors)) -join "`n" }
    function Shot([string]$name) { $p = Join-Path $EvidenceDir "c-end-$name.png"; $null = Get-LabScreenshot -Name $VmName -Path $p; [string](Get-LabOcr -Path $p).text }

    # the Windows sessions as WTS lists them: id, state (Active / Disconnected / ...), user, console, explorer.exe, LogonUI
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
    # the console account's sessions (the one automatic sign-in created)
    # (",": the array as it is - PowerShell would hand back $null for none and the object itself for one)
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
        do { $e = @(Get-LabAgentEvent -Name $VmName -Since $Since -Id 1107); if ($e.Count -gt 0) { return $e }; Start-Sleep -Seconds 3 } while ((Get-Date) -lt $until)
        @()
    }
    function AutoSignInState { Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Content 'C:\ProgramData\RemoteHub\Agent\autosignin.json' -Raw -ErrorAction SilentlyContinue } }
    function Wait-DropLogged([datetime]$Since, [int]$TimeoutSec = 60) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            $l = @(Get-LabAgentLog -Name $VmName -Since $Since | Where-Object { $_ -match 'Automatic sign-in: the connection to session \d+ dropped' })
            if ($l.Count -gt 0) { return $l[0] }
            Start-Sleep -Seconds 3
        } while ((Get-Date) -lt $until)
        $null
    }
    # the service killed (no clean stop: what is left in autosignin.json is dealt with at the next start), then started
    function Kill-Agent {
        Invoke-LabGuest -Name $VmName -ScriptBlock { Stop-Process -Id (Get-CimInstance Win32_Service -Filter "Name='RemoteHubAgent'").ProcessId -Force }
        Invoke-LabGuest -Name $VmName -ScriptBlock {
            Start-Sleep -Seconds 5
            if ((Get-Service RemoteHubAgent).Status -ne 'Running') { try { Start-Service RemoteHubAgent -ErrorAction Stop } catch { } }
        }
        $null = Wait-LabAgent -Name $VmName -Pin $pin
    }
    # every sign-in: automatic sign-in allowed for Administrators by policy (as IT would set it - the install keeps its own
    # choice in agent.json) (+ extra values), a fresh agent (rate limit, cached checks), nobody at the console
    function Prepare([hashtable]$Policy = @{}) {
        Get-Job | Where-Object State -eq 'Running' | Stop-Job
        Get-Job | Remove-Job -Force
        $values = @{ NoUserBehavior = 'AutoSignIn' }
        foreach ($k in $Policy.Keys) { $values[$k] = $Policy[$k] }
        Set-LabAgentPolicy -Name $VmName -Clear -Values $values -Rights @{ AutoSignIn = @('S-1-5-32-544') }
        $null = Restart-LabAgent -Name $VmName -Pin $pin
        Disconnect-LabConsole -Name $VmName
    }
    # signed in automatically, as RemoteHub does it (no end action sent: Lock unless the policy says otherwise)
    $signIn = @(
        @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'None'; autoSignIn = $true }
        @{ do = 'wait'; anyOf = @('SignedIn', 'SignInFailed', 'Failed', 'Denied'); timeout = 120 }, @{ do = 'sleep'; seconds = 10 })
    # the same with RemoteHub's end action in DesktopStart - the harness' start step has no field for it, so the Start
    # message goes as raw JSON and the password by hand at SignInReady
    function SignInWith([string]$EndAction) {
        $console = @(Sessions | Where-Object Console)[0].Id
        $start = [ordered]@{ sessionId = $console; consent = 'None'; viewOnly = $false; framesPerSecond = 30; quality = 60; monitor = 0; resume = $null
            features = @('transferOnPaste'); autoSignIn = $true; autoSignInEndAction = $EndAction } | ConvertTo-Json -Compress
        @(
            @{ do = 'connect' }, @{ do = 'rawJson'; type = 2; json = $start }
            @{ do = 'wait'; anyOf = @('SignInReady', 'SignInFailed', 'Failed', 'Denied'); timeout = 90 }, @{ do = 'signIn' }
            @{ do = 'wait'; anyOf = @('SignedIn', 'SignInFailed', 'Failed'); timeout = 120 }, @{ do = 'sleep'; seconds = 10 })
    }
    # signed in, then closed on purpose: the end action
    function Close-Run([string]$Name, [object[]]$SignInSteps) {
        $o = Out $Name
        $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps ($SignInSteps + @((Mark $o 'signed'), (Park $o 'close'), @{ do = 'close' }))
        Reached $o 'signed'
        $x = [ordered]@{ Signed = Mine }
        $since = Get-LabGuestTime -Name $VmName
        Release $o 'close'
        $x.R = Receive-LabDesktop $job
        $x.Ended = @(Wait-Ended $since)
        Start-Sleep -Seconds 5
        $x.After = Mine
        $x.SignedInEvent = @(Get-LabAgentEvent -Name $VmName -Since $since.AddMinutes(-5) -Id 1106 | Select-Object -Last 1)
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
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'c-end-agent-logs')
    # the console account signed out / locked, the agent killed: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'C-END <VmName>' -Tag Agent, 'C-END' {

    Context 'covered, then closed' {
        BeforeAll {
            Prepare
            $c = [ordered]@{}
            $o = Out 'covered'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps ($signIn + @(
                    (Mark $o 'signed'), (Park $o 'typed'), @{ do = 'keys'; text = 'remote' }, @{ do = 'sleep'; seconds = 3 }, @{ do = 'frame'; out = 'picture.png' }
                    (Mark $o 'pictured'), (Park $o 'close'), @{ do = 'close' }))
            Reached $o 'signed'
            $c.Signed = Mine
            $c.ConsoleText = Shot 'covered-console'
            # the device's own keyboard: blocked; RemoteHub's keys: arrive (the marker in the signed-in session records both)
            $marker = Start-LabMarker -Name $VmName -Account Console
            $c.MarkerShown = $marker.Shown
            $c.MarkerWhy = $marker.Why
            Start-Sleep -Seconds 2
            $count = @(Get-LabMarker -Name $VmName -Marker $marker).Count
            Send-LabText -Name $VmName -Text 'local'
            Start-Sleep -Seconds 3
            $c.LocalKeys = @(Get-LabMarker -Name $VmName -Marker $marker | Select-Object -Skip $count | Where-Object e -eq 'char')
            $count = @(Get-LabMarker -Name $VmName -Marker $marker).Count
            Release $o 'typed'
            Reached $o 'pictured' 120
            $c.RemoteKeys = @(Get-LabMarker -Name $VmName -Marker $marker | Select-Object -Skip $count | Where-Object e -eq 'char')
            Stop-LabInSession -Name $VmName -Task $marker.Task
            $picture = Join-Path $o 'picture.png'
            $c.PictureText = if (Test-Path $picture) { [string](Get-LabOcr -Path $picture).text } else { $null }
            $since = Get-LabGuestTime -Name $VmName
            Release $o 'close'
            $c.R = Receive-LabDesktop $job
            $c.Ended = @(Wait-Ended $since)
            Start-Sleep -Seconds 5
            $c.After = Mine
            $c.SignedInEvent = @(Get-LabAgentEvent -Name $VmName -Since $since.AddMinutes(-5) -Id 1106 | Select-Object -Last 1)
            $null = Shot 'closed-console'
            Save 'covered' $c
        }

        It 'C-END-01 after SignedIn the console shows the privacy screen message, its own keys are blocked, RemoteHub sees the desktop' -Tag 'C-END-01' {
            Text $c.R | Should -Match 'SignedIn: Signed in as'
            ($c.SignedInEvent.Message -join "`n") | Should -Match 'privacy screen on'
            $c.ConsoleText | Should -Match 'signed in remotely'
            @($c.R.privacy | Where-Object { $_.privacy.screen -eq 'Message' -and $_.privacy.localInputBlocked }).Count | Should -BeGreaterThan 0
            $c.MarkerShown | Should -BeTrue -Because "lab: the marker window must be up to count keys (c-end-covered.json MarkerWhy: $($c.MarkerWhy))"
            @($c.LocalKeys).Count | Should -Be 0 -Because 'the device''s keyboard is blocked in a session automatic sign-in created'
            @($c.RemoteKeys).Count | Should -BeGreaterThan 0 -Because 'RemoteHub''s keys reach the session'
            @($c.R.images)[0].meanLuma | Should -BeGreaterThan 20 -Because 'RemoteHub''s picture is the desktop, not the cover'
            $c.PictureText | Should -Not -Match 'signed in remotely'
        }

        It 'C-END-02 closed: the session is locked - disconnected, work kept, the console back at the sign-in screen; event 1107 "locked"' -Tag 'C-END-02' {
            @($c.Signed | Where-Object State -eq 'Active').Count | Should -Be 1
            ($c.Ended.Message -join "`n") | Should -Match "Automatic sign-in session \d+ of .*$([regex]::Escape($account)) locked"
            # WTSDisconnectSession on client and Server alike: client Windows shows the lock screen, Server the sign-in screen
            @($c.After).Count | Should -Be 1
            $c.After[0].Id | Should -Be $c.Signed[0].Id
            $c.After[0].State | Should -Be 'Disconnected'
            $c.After[0].Console | Should -BeFalse
            @($c.After[0].Explorer).Count | Should -BeGreaterThan 0 -Because 'a lock keeps the open work'
        }
    }

    Context 'sign out' {
        BeforeAll {
            Prepare
            $byConnection = Close-Run 'signout-connection' (SignInWith 'SignOut')
            $byConnection.Gone = Wait-Gone
            Prepare @{ AutoSignInEndAction = 'SignOut' }
            $byPolicy = Close-Run 'signout-policy' $signIn
            $byPolicy.Gone = Wait-Gone
            Save 'signout' @{ Connection = $byConnection; Policy = $byPolicy }
        }

        It 'C-END-03 end action SignOut from the connection or from the policy: the session is gone, event 1107 "signed out"' -Tag 'C-END-03' {
            foreach ($k in @{ connection = $byConnection; policy = $byPolicy }.GetEnumerator()) {
                Text $k.Value.R | Should -Match 'SignedIn: Signed in as' -Because $k.Key
                ($k.Value.SignedInEvent.Message -join "`n") | Should -Match 'when the connection ends: sign out' -Because $k.Key
                ($k.Value.Ended.Message -join "`n") | Should -Match 'Automatic sign-in session \d+ of .* signed out' -Because $k.Key
                @($k.Value.Gone).Count | Should -Be 0 -Because "$($k.Key): signed out, no session of the account left"
            }
        }
    }

    Context 'policy wins' {
        BeforeAll {
            Prepare @{ AutoSignInEndAction = 'Lock' }
            $w = Close-Run 'policy-lock' (SignInWith 'SignOut')
            Save 'policy-wins' $w
        }

        It 'C-END-04 policy AutoSignInEndAction=Lock, RemoteHub sends SignOut: locked' -Tag 'C-END-04' {
            Text $w.R | Should -Match 'SignedIn: Signed in as'
            ($w.SignedInEvent.Message -join "`n") | Should -Match 'when the connection ends: lock'
            ($w.Ended.Message -join "`n") | Should -Match 'Automatic sign-in session \d+ of .* locked'
            ($w.Ended.Message -join "`n") | Should -Not -Match 'signed out'
            @($w.After).Count | Should -Be 1
            $w.After[0].State | Should -Be 'Disconnected'
        }
    }

    Context 'network drop and resume' {
        BeforeAll {
            Prepare
            $d = [ordered]@{}
            $o = Out 'resume'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -TimeoutSec 900 -Steps ($signIn + @(
                    (Mark $o 'signed'), (Park $o 'back' 600), @{ do = 'drop' }, @{ do = 'reconnect' }
                    @{ do = 'start'; session = 'console'; consent = 'None'; resume = 'last' }, @{ do = 'wait'; anyOf = @('Connected', 'Failed', 'Denied'); timeout = 90 }
                    @{ do = 'sleep'; seconds = 5 }, @{ do = 'frame'; out = 'resumed.png' }, (Mark $o 'resumed'), (Park $o 'close'), @{ do = 'close' }))
            Reached $o 'signed'
            $d.Before = Mine
            $since = Get-LabGuestTime -Name $VmName
            $cut = Disconnect-LabNetwork -Name $VmName
            try {
                Start-Sleep -Seconds 20
                $d.CutText = Shot 'resume-cut'
                $d.During = Mine
                Start-Sleep -Seconds 10
            }
            finally { Connect-LabNetwork -Name $VmName -Switches $cut }
            Start-Sleep -Seconds 5
            Release $o 'back'
            Reached $o 'resumed' 240
            $d.After = Mine
            $d.AfterText = Shot 'resume-after'
            $d.EndedBeforeClose = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1107)
            $d.Resumed = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1103)
            Release $o 'close'
            $d.R = Receive-LabDesktop $job
            Save 'resume' $d
        }

        It 'C-END-05 the adapter cut for 30 s: the cover stays, the desktop untouched; resumed - same session, no sign-out' -Tag 'C-END-05' {
            Text $d.R | Should -Match 'SignedIn: Signed in as'
            $d.CutText | Should -Match 'signed in remotely'
            @($d.During).Count | Should -Be 1
            $d.During[0].Id | Should -Be $d.Before[0].Id
            $d.During[0].State | Should -Be 'Active'
            $resumed = @($d.R.statuses | Where-Object { $_.connection -eq 2 })
            ($resumed | ForEach-Object { "$($_.state): $($_.message)" }) -join "`n" | Should -Match 'Continuing on the device'
            @($resumed | Where-Object state -eq 'Connected').Count | Should -BeGreaterThan 0
            @($resumed | Where-Object state -in 'SignInReady', 'SigningIn', 'WaitingForUser') | Should -BeNullOrEmpty
            @($d.Resumed).Count | Should -BeGreaterThan 0 -Because 'event 1103: continued'
            @($d.EndedBeforeClose) | Should -BeNullOrEmpty -Because 'no end action and no sign-out while resumed in time'
            @($d.After).Count | Should -Be 1
            $d.After[0].Id | Should -Be $d.Before[0].Id
            $d.After[0].State | Should -Be 'Active'
            (@($d.After[0].Explorer) -join ',') | Should -Be (@($d.Before[0].Explorer) -join ',') -Because 'the same explorer.exe: the desktop untouched'
            $d.AfterText | Should -Match 'signed in remotely'
        }
    }

    Context 'network drop without resume' {
        BeforeAll {
            Prepare
            $n = [ordered]@{}
            $o = Out 'noresume'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -TimeoutSec 900 -Steps ($signIn + @((Mark $o 'signed'), (Park $o 'back' 700), @{ do = 'close' }))
            Reached $o 'signed'
            $n.Before = Mine
            $cutAt = Get-LabGuestTime -Name $VmName
            $cut = Disconnect-LabNetwork -Name $VmName
            try {
                # 3 minutes and more: until the session is signed out (the agent may notice the cut late), at most 6 minutes
                Start-Sleep -Seconds 60
                $n.CutText = Shot 'noresume-cut'
                # always an array: one entry would come back as a single object, whose .Count is $null in 5.1
                $n.Ended = @(Wait-Ended $cutAt 300)
            }
            finally { Connect-LabNetwork -Name $VmName -Switches $cut }
            $n.Gone = Wait-Gone
            $agentLog = @(Get-LabAgentLog -Name $VmName -Since $cutAt | ForEach-Object { "$_" })
            $n.DropLine = @($agentLog | Where-Object { $_ -match 'Automatic sign-in: the connection to session \d+ dropped' })[0]
            $n.SignOutLine = @($agentLog | Where-Object { $_ -match 'signed out \(connection lost and not continued in time\)' })[0]
            # both times from the agent's log, in the device's own clock: the event's time arrives converted to this host's
            # time zone (run 20261017-160133: 9 h apart on Server 2025)
            $stamp = '^(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?)'
            if ($n.DropLine -match $stamp) {
                $dropped = [datetime]$Matches[1]
                if ($n.SignOutLine -match $stamp) { $n.HeldSeconds = [math]::Round(([datetime]$Matches[1] - $dropped).TotalSeconds) }
            }
            $n.CutToEndSeconds = if ($n.Ended.Count -gt 0) { [math]::Round(([datetime]$n.Ended[0].Time - $cutAt).TotalSeconds) } else { $null }
            Release $o 'back'
            $n.R = Receive-LabDesktop $job
            Save 'noresume' $n
        }

        It 'C-END-06 the adapter cut for 3 minutes: covered while held, signed out 2 minutes after the drop, event 1107' -Tag 'C-END-06' {
            Text $n.R | Should -Match 'SignedIn: Signed in as'
            # first: without the drop the session got its end action (locked), and the console shows the lock screen
            $n.DropLine | Should -Not -BeNullOrEmpty -Because 'the agent noticed the dropped connection'
            $n.CutText | Should -Match 'signed in remotely'
            ($n.Ended.Message -join "`n") | Should -Match 'signed out \(connection lost and not continued in time\)'
            $n.HeldSeconds | Should -BeGreaterOrEqual 115
            $n.HeldSeconds | Should -BeLessOrEqual 150
            @($n.Gone).Count | Should -Be 0
        }
    }

    Context 'agent restart' {
        BeforeAll {
            $k = [ordered]@{}
            # killed while connected: the session was not dropped - the end action (Lock) at the next start
            Prepare
            $o = Out 'restart-connected'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps ($signIn + @((Mark $o 'signed'), @{ do = 'wait'; anyOf = @('Ended', 'Failed'); timeout = 180 }))
            Reached $o 'signed'
            $k.ConnectedSigned = Mine
            $k.ConnectedState = AutoSignInState
            $since = Get-LabGuestTime -Name $VmName
            Kill-Agent
            $k.ConnectedEnded = @(Wait-Ended $since)
            $k.ConnectedR = Receive-LabDesktop $job
            Start-Sleep -Seconds 5
            $k.ConnectedAfter = Mine
            # dropped, then killed during the 2 minutes: signed out at the next start
            Prepare
            $o = Out 'restart-dropped'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps ($signIn + @(@{ do = 'drop' }, (Mark $o 'dropped')))
            Reached $o 'dropped'
            $since = Get-LabGuestTime -Name $VmName
            $k.DropLine = Wait-DropLogged $since.AddMinutes(-1)
            $k.DroppedState = AutoSignInState
            $since = Get-LabGuestTime -Name $VmName
            Kill-Agent
            $k.DroppedEnded = @(Wait-Ended $since)
            $k.DroppedR = Receive-LabDesktop $job
            $k.DroppedGone = Wait-Gone
            Save 'restart' $k
        }

        It 'C-END-07 the service killed: at the next start the session gets its end action (locked), signed out when it was dropped' -Tag 'C-END-07' {
            Text $k.ConnectedR | Should -Match 'SignedIn: Signed in as'
            $k.ConnectedState | Should -Match '"Dropped"\s*:\s*false'
            ($k.ConnectedEnded.Message -join "`n") | Should -Match 'locked \(the agent restarted while it was open\)'
            @($k.ConnectedAfter).Count | Should -Be 1
            $k.ConnectedAfter[0].State | Should -Be 'Disconnected'
            Text $k.DroppedR | Should -Match 'SignedIn: Signed in as'
            $k.DropLine | Should -Not -BeNullOrEmpty
            $k.DroppedState | Should -Match '"Dropped"\s*:\s*true'
            ($k.DroppedEnded.Message -join "`n") | Should -Match 'signed out \(the agent restarted while it was open\)'
            @($k.DroppedGone).Count | Should -Be 0
        }
    }

    Context 'the cover ends after a drop' {
        BeforeAll {
            Prepare
            $b = [ordered]@{}
            $o = Out 'cover-ends'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps ($signIn + @(@{ do = 'drop' }, (Mark $o 'dropped')))
            Reached $o 'dropped'
            $b.DropLine = Wait-DropLogged (Get-LabGuestTime -Name $VmName).AddMinutes(-1)
            $b.R = Receive-LabDesktop $job
            $b.Before = Mine
            $b.CoverText = Shot 'cover-ends-console'
            # the cover's helper ends (as a crash, a local sign-out, the console moving would end it)
            $killAt = Get-LabGuestTime -Name $VmName
            $b.Killed = Invoke-LabGuest -Name $VmName -ArgumentList $b.Before[0].Id -ScriptBlock {
                param($sid)
                @(Get-CimInstance Win32_Process -Filter "Name='RemoteHubAgent.exe'" | Where-Object { $_.CommandLine -match '\sdesktop\s*$' -and $_.SessionId -eq $sid } |
                    ForEach-Object { Stop-Process -Id $_.ProcessId -Force; $_.ProcessId })
            }
            $b.Ended = @(Wait-Ended $killAt 90)
            $b.KillToEndSeconds = if ($b.Ended.Count -gt 0) { [math]::Round(([datetime]$b.Ended[0].Time - $killAt).TotalSeconds) } else { $null }
            $b.Gone = Wait-Gone
            Save 'cover-ends' $b
        }

        # The sheet says "Disconnect on the banner"; since 0.18.10 a session automatic sign-in created has no banner
        # (HelperInit.SignedInRemotely, design 9.2) - the cover ending during the 2 minutes is what signs out at once.
        It 'C-END-08 after a drop: no banner to disconnect, only the cover; the cover ending during the 2 minutes signs out at once' -Tag 'C-END-08' {
            Text $b.R | Should -Match 'SignedIn: Signed in as'
            $b.DropLine | Should -Not -BeNullOrEmpty
            $b.CoverText | Should -Match 'signed in remotely'
            $b.CoverText | Should -Not -Match '\bDisconnect\b'
            @($b.Killed).Count | Should -BeGreaterThan 0 -Because 'the cover''s helper runs in the session during the 2 minutes'
            ($b.Ended.Message -join "`n") | Should -Match 'signed out \(the screen cover ended after the connection dropped\)'
            $b.KillToEndSeconds | Should -BeLessThan 30
            @($b.Gone).Count | Should -Be 0
        }
    }
}
