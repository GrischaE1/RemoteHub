# D-IDLE / D-RES / D-ROB - the agent's remote desktop over time and when things break: the idle timeout, continuing
# after a dropped connection with the resume token (single use, one account, one minute, void after a deliberate end,
# asked again when someone else signed in), the helper killed or failing, a service restart, a long session (D-ROB-04:
# lab.json SoakMinutes, default 50 - a release run sets 480 and -FileTimeoutMin above it), fuzzed messages, ping.
# normaluser is signed in and allows PSAdmin (RhPerson.ps1). See docs/test-lab-cases.md section 9.10.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $cfg = Get-LabConfig
    $password = $cfg.Password
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "d-rob-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "d-rob-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Of($r, [string]$state) { , @($r.statuses | Where-Object state -eq $state) }
    function Mark([string]$o, [string]$n) { @{ do = 'markFile'; path = (Join-Path $o "$n.txt") } }
    function Park([string]$o, [string]$n, [int]$t = 300) { @{ do = 'waitFile'; path = (Join-Path $o "$n.txt"); timeout = $t } }
    function Reached([string]$o, [string]$n, [int]$t = 180) { Wait-LabHostFile (Join-Path $o "$n.txt") -TimeoutSec $t }
    function Release([string]$o, [string]$n) { Set-Content -Encoding ASCII (Join-Path $o "$n.txt") 'go' }
    $connect = @(@{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Denied', 'Failed'); timeout = 90 })
    function Begin([string]$Name, [object[]]$Steps, [string]$Account = 'Admin', [switch]$NoAllow) {
        if (-not $pin) { throw "no agent PIN for '$Name' - the context's Initialize-LabVm didn't give one" }
        $job = Start-LabDesktop -Name $VmName -Pin $pin -Account $Account -Out (Out $Name) -Steps ($connect + $Steps)
        if (-not $NoAllow) {
            # the dialog can come up only after the person's first look (a slow first call in the session): one more look
            $a = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
            if (-not ($a -and $a.PSObject.Properties['x'])) { $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 20 }
        }
        $job
    }
    function Helper { Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-CimInstance Win32_Process -Filter "Name='RemoteHubAgent.exe'" | Where-Object CommandLine -match '\sdesktop\s*$' | ForEach-Object ProcessId) } }

    $init = Initialize-LabVm -Name $VmName -Agent -AutoLogon User
    $install = $init.Install
    $pin = $init.Pin
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-rob-agent-logs')
    # reverted and reinstalled midway, the helper killed, the service restarted, fuzzed: not for the next file
    Set-LabVmDirty -Name $VmName
}

Describe 'D-ROB <VmName>' -Tag Desktop, 'D-IDLE', 'D-RES', 'D-ROB' {

    Context 'idle timeout' {
        BeforeAll {
            $i = [ordered]@{}
            $since = Get-LabGuestTime -Name $VmName
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ DesktopIdleTimeoutMinutes = 1 }
            $i.idle = Receive-LabDesktop (Begin 'idle' @(@{ do = 'wait'; state = 'Ended'; timeout = 120 }))
            $i.keys = Receive-LabDesktop (Begin 'idle-keys' @(
                    @{ do = 'sleep'; seconds = 20 }, @{ do = 'key'; scan = '0x2A' }, @{ do = 'key'; scan = '0x2A'; flags = @('Up') }
                    @{ do = 'sleep'; seconds = 20 }, @{ do = 'key'; scan = '0x2A' }, @{ do = 'key'; scan = '0x2A'; flags = @('Up') }
                    @{ do = 'sleep'; seconds = 20 }, @{ do = 'key'; scan = '0x2A' }, @{ do = 'key'; scan = '0x2A'; flags = @('Up') }
                    @{ do = 'sleep'; seconds = 20 }, @{ do = 'key'; scan = '0x2A' }, @{ do = 'key'; scan = '0x2A'; flags = @('Up') }, @{ do = 'sleep'; seconds = 15 }, @{ do = 'close' }))
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Out (Out 'idle-view') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; viewOnly = $true }, @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 90 }
                @{ do = 'wait'; state = 'Ended'; timeout = 120 })
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
            $i.view = Receive-LabDesktop $job
            Set-LabAgentPolicy -Name $VmName -Clear
            $null = Wait-LabCondition -NoThrow -TimeoutSec 15 -Because 'event 2102 was not written' -Until { @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2102).Count -gt 0 }
            $i.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2102)
            Save 'idle' $i
        }

        It 'D-IDLE-01 DesktopIdleTimeoutMinutes=1: a warning at ~30 s, Ended at 60 s, event 2102' -Tag 'D-IDLE-01' {
            $c = (Of $i.idle 'Connected')[0].ms
            $warn = @($i.idle.statuses | Where-Object message -match 'No mouse or keyboard input for a while')[0]
            $end = (Of $i.idle 'Ended')[0]
            # without a connection there is nothing to time (all of these would be $null and the times 0)
            $c | Should -Not -BeNullOrEmpty -Because "the session must connect first (Allow clicked in time); states: $(@($i.idle.statuses | ForEach-Object state) -join ', ')"
            $warn | Should -Not -BeNullOrEmpty -Because 'the agent warns before it ends an idle session'
            $end | Should -Not -BeNullOrEmpty -Because 'the agent ends the idle session'
            ($warn.ms - $c) / 1000 | Should -BeGreaterOrEqual 20
            ($warn.ms - $c) / 1000 | Should -BeLessOrEqual 45
            ($end.ms - $c) / 1000 | Should -BeGreaterOrEqual 55
            ($end.ms - $c) / 1000 | Should -BeLessOrEqual 75
            $end.message | Should -Match 'without mouse or keyboard input'
            $i.events.Count | Should -BeGreaterThan 0
        }

        It 'D-IDLE-02 a key every 20 s: the session never ends' -Tag 'D-IDLE-02' {
            (Of $i.keys 'Ended').Count | Should -Be 0
        }

        It 'D-IDLE-03 a view-only session ends as well' -Tag 'D-IDLE-03' {
            (Of $i.view 'Ended').Count | Should -Be 1
        }
    }

    Context 'resume' {
        BeforeAll {
            $r = [ordered]@{}
            $since = Get-LabGuestTime -Name $VmName
            # a network cut of 10 s, then the token: no new question; the same token a second time: asked
            $o = Out 'res'
            $job = Begin 'res' @(
                @{ do = 'saveToken'; path = (Join-Path $o 'token1.txt') }, (Mark $o 'connected'), (Park $o 'back'), @{ do = 'drop' }, @{ do = 'reconnect' }
                @{ do = 'start'; session = 'console'; consent = 'auto'; resume = 'last' }, @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'Failed'); timeout = 60 }
                @{ do = 'drop' }, @{ do = 'reconnect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; resume = "file:$(Join-Path $o 'token1.txt')" }
                @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'Failed'); timeout = 60 }, (Mark $o 'second'), @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 60 }, @{ do = 'close' })
            Reached $o 'connected'
            $cut = Disconnect-LabNetwork -Name $VmName
            # the cut lasts 10 s (what the resume covers)
            Start-Sleep -Seconds 10
            Connect-LabNetwork -Name $VmName -Switches $cut
            # the harness reconnects only once the agent answers over the network again
            $null = Wait-LabAgent -Name $VmName -Pin $pin -TimeoutSec 60
            Release $o 'back'
            Reached $o 'second'
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Deny' -TimeoutSec 30
            $r.first = Receive-LabDesktop $job
            $r.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1103)
            # another account presents the token: ignored (asked); then PSAdmin with it: still good
            $null = New-LabGuestUser -Name $VmName -User 'lapsadmin' -Admin
            $o = Out 'res-other'
            $job = Begin 'res-other' @(@{ do = 'saveToken'; path = (Join-Path $o 'token.txt') }, @{ do = 'drop' })
            $null = Receive-LabDesktop $job
            $other = Start-LabDesktop -Name $VmName -Pin $pin -Account 'lapsadmin' -Out (Out 'res-other-laps') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; resume = "file:$(Join-Path $o 'token.txt')" }
                @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'Failed'); timeout = 60 }, @{ do = 'close' })
            $r.laps = Receive-LabDesktop $other
            $r.own = Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out 'res-other-own') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; resume = "file:$(Join-Path $o 'token.txt')" }
                @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'Failed'); timeout = 60 }, @{ do = 'close' })
            try { $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Deny' -TimeoutSec 5 } catch { }
            # after a minute: expired
            $o = Out 'res-late'
            $job = Begin 'res-late' @(@{ do = 'drop' }, @{ do = 'sleep'; seconds = 70 }, @{ do = 'reconnect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; resume = 'last' }
                @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'Failed'); timeout = 60 }, (Mark $o 'asked'), @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 60 }, @{ do = 'close' })
            Reached $o 'asked' 240
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Deny' -TimeoutSec 30
            $r.late = Receive-LabDesktop $job
            # ended on purpose by RemoteHub: void
            $o = Out 'res-closed'
            $job = Begin 'res-closed' @(@{ do = 'close' }, @{ do = 'reconnect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; resume = 'last' }
                @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'Failed'); timeout = 60 }, (Mark $o 'asked'), @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 60 }, @{ do = 'close' })
            Reached $o 'asked'
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Deny' -TimeoutSec 30
            $r.closed = Receive-LabDesktop $job
            # the old connection still alive: the new one takes over, the old one ends "continued in a new connection"
            $o = Out 'res-takeover'
            $old = Begin 'res-takeover' @(@{ do = 'saveToken'; path = (Join-Path $o 'token.txt') }, (Mark $o 'saved'), @{ do = 'wait'; anyOf = @('Ended'); timeout = 120 })
            Reached $o 'saved'
            $r.newer = Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out 'res-takeover-new') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; resume = "file:$(Join-Path $o 'token.txt')" }
                @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'Failed'); timeout = 60 }, @{ do = 'sleep'; seconds = 3 }, @{ do = 'close' })
            $r.older = Receive-LabDesktop $old
            $r.log = @(Get-LabAgentLog -Name $VmName -Since $since)
            Save 'resume' $r
        }

        It 'D-RES-01 after a 10 s network cut the token continues without a new question, event 1103' -Tag 'D-RES-01' {
            $after = @($r.first.statuses | Where-Object { $_.connection -eq 2 })
            @($after | Where-Object state -eq 'Connected').Count | Should -BeGreaterThan 0
            @($after | Where-Object state -eq 'WaitingForUser') | Should -BeNullOrEmpty
            $r.events.Count | Should -BeGreaterThan 0
        }

        It 'D-RES-02 the token works once: the same token again means a new question' -Tag 'D-RES-02' {
            @($r.first.statuses | Where-Object { $_.connection -eq 3 -and $_.state -eq 'WaitingForUser' }).Count | Should -BeGreaterThan 0
        }

        It 'D-RES-03 a token presented by another account is ignored and stays good for PSAdmin' -Tag 'D-RES-03' {
            (Of $r.laps 'WaitingForUser').Count | Should -BeGreaterThan 0
            (Of $r.own 'Connected').Count | Should -BeGreaterThan 0
            (Of $r.own 'WaitingForUser').Count | Should -Be 0
        }

        It 'D-RES-04 a token after more than a minute has expired: asked again' -Tag 'D-RES-04' {
            (Of $r.late 'WaitingForUser').Count | Should -BeGreaterOrEqual 2
        }

        It 'D-RES-05 after a deliberate end the token is void: asked again' -Tag 'D-RES-05' {
            (Of $r.closed 'WaitingForUser').Count | Should -BeGreaterOrEqual 2
        }

        It 'D-RES-07 resumed while the old connection still looks alive: the agent closes the old one (no Status, no 1101 for it)' -Tag 'D-RES-07' {
            (Of $r.newer 'Connected').Count | Should -BeGreaterThan 0
            # the old connection is closed by the agent - not by the harness - and gets no Status of its own
            @($r.older.closes | Where-Object { $_.by -notin 'close', 'drop' }).Count | Should -BeGreaterThan 0
        }
    }

    Context 'resume after another user signed in' {
        BeforeAll {
            # Switch user through the picture, then the connection drops; resumed on the sign-in screen, Admin signs in
            # through the picture: Admin is asked although normaluser allowed the first connection
            $o = Out 'res-user'
            $job = Begin 'res-user' @(
                @{ do = 'cad' }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'options.png' }, (Mark $o 'options'), @{ do = 'click'; fromFile = (Join-Path $o 'switch-xy.txt') }
                @{ do = 'sleep'; seconds = 8 }, @{ do = 'drop' }, @{ do = 'reconnect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; resume = 'last' }
                @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'Failed'); timeout = 60 }, @{ do = 'sleep'; seconds = 4 }
                # Server 2025 shows "Press Ctrl+Alt+Delete to unlock" there (run 20261019-193627): Ctrl+Alt+Del then
                @{ do = 'frame'; out = 'curtain.png' }, (Mark $o 'curtain'), @{ do = 'cad'; ifFile = (Join-Path $o 'cad.txt') }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'tiles.png' }
                (Mark $o 'tiles'), @{ do = 'click'; fromFile = (Join-Path $o 'admin-xy.txt') }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'selected.png' }
                (Mark $o 'selected'), (Park $o 'type' 120), @{ do = 'keys'; text = "$password`n" }
                @{ do = 'wait'; state = 'WaitingForUser'; timeout = 120 }, (Mark $o 'asked'), @{ do = 'wait'; anyOf = @('Connected', 'Denied', 'Ended'); timeout = 60 }, @{ do = 'close' })
            Reached $o 'options'
            $xy = Find-LabPictureText -Path (Join-Path $o 'options.png') -Text 'Switch'; Set-Content -Encoding ASCII (Join-Path $o 'switch-xy.txt') $(if ($xy) { $xy } else { '0,0' })
            Reached $o 'curtain'
            Set-Content -Encoding ASCII (Join-Path $o 'cad.txt') (Get-LabCadAnswer -Picture (Join-Path $o 'curtain.png'))
            Reached $o 'tiles'
            # the tile names are small on the wallpaper and OCR reads them in parts ("min" for Admin): matched by distance
            # to the device's accounts, not as an exact word
            $xy = (Find-LabSignInTile -Name $VmName -Picture (Join-Path $o 'tiles.png') -Account $vm.ConsoleAccount).XY
            Set-Content -Encoding ASCII (Join-Path $o 'admin-xy.txt') $(if ($xy) { $xy } else { '0,0' })
            Reached $o 'selected'
            # the chosen tile's name stands large in the middle; when it isn't Admin the password signs normaluser in again
            # and the case fails on $adminSelected (with the pictures as evidence), not on a session that isn't there
            $chosen = Find-LabSignInTile -Name $VmName -Picture (Join-Path $o 'selected.png') -Account $vm.ConsoleAccount -Selected
            $selected = @($chosen.Lines)
            $adminSelected = $chosen.Selected
            Release $o 'type'
            Reached $o 'asked' 240
            # Admin answers in Admin's own session - there only when the sign-in through the picture worked (otherwise a
            # session task for Admin never runs); unanswered, the dialog's countdown denies it, which the case doesn't mind
            $adminIn = Invoke-LabGuest -Name $VmName -ArgumentList $vm.ConsoleAccount -ScriptBlock {
                param($a)
                @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Where-Object { (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User -eq $a }).Count -gt 0
            }
            if ($adminIn) {
                try { $null = Invoke-LabPerson -Name $VmName -Account Console -Do click -Text 'Deny' -TimeoutSec 60 }
                catch { Write-Warning "${VmName}: Deny in $($vm.ConsoleAccount)'s session: $($_.Exception.Message)" }
            }
            $u = Receive-LabDesktop $job
            Save 'res-user' ([ordered]@{ AdminSelected = $adminSelected; Selected = $selected; AdminTile = $xy; AdminSignedIn = $adminIn; Result = $u })
        }

        # Admin is signed in now (or normaluser again after a failed step): the next context starts from the file's state
        AfterAll { Set-LabVmDirty -Name $VmName }

        It 'D-RES-06 another user signed in meanwhile: resumed, they are asked' -Tag 'D-RES-06' {
            $adminSelected | Should -BeTrue -Because "$($vm.ConsoleAccount) signs in through the picture: its tile must be chosen on the sign-in screen (d-rob-res-user\tiles.png, selected.png; read there: $($selected -join ', '))"
            @($u.statuses | Where-Object { $_.connection -ge 2 -and $_.state -eq 'WaitingForUser' }).Count | Should -BeGreaterThan 0
        }
    }

    Context 'helper and service failures' {
        BeforeAll {
            # the context before signed Admin in: back to the file's state (normaluser signed in, the agent) - from the run
            # checkpoint or installed again - with that state's PIN
            $pin = (Initialize-LabVm -Name $VmName -Agent -AutoLogon User).Pin
            $f = [ordered]@{}
            $o = Out 'helper'
            $job = Begin 'helper' @((Mark $o 'connected'), (Park $o 'killed'), @{ do = 'sleep'; seconds = 8 }, (Mark $o 'back'), (Park $o 'flood'), @{ do = 'wait'; state = 'Ended'; timeout = 120 })
            Reached $o 'connected'
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-CimInstance Win32_Process -Filter "Name='RemoteHubAgent.exe'" | Where-Object CommandLine -match '\sdesktop\s*$' | ForEach-Object { Stop-Process -Id $_.ProcessId -Force } }
            Release $o 'killed'
            Reached $o 'back'
            $f.asked = @(Invoke-LabPerson -Name $VmName -Do windows | Where-Object { $_.Title -ne 'RemoteHub' })
            # six kills within a minute
            Release $o 'flood'
            for ($k = 0; $k -lt 6; $k++) {
                # the rate the test needs: six kills within a minute
                Start-Sleep -Seconds 5
                Invoke-LabGuest -Name $VmName -ScriptBlock { Get-CimInstance Win32_Process -Filter "Name='RemoteHubAgent.exe'" | Where-Object CommandLine -match '\sdesktop\s*$' | ForEach-Object { Stop-Process -Id $_.ProcessId -Force } }
            }
            $f.r = Receive-LabDesktop $job
            # the service restarted while connected
            $before = Get-LabResolution -Name $VmName
            # fitted to a size the console isn't at, so "restored" can tell
            $size = if ($before -eq '1280x720') { @(1024, 768) } else { @(1280, 720) }
            $o = Out 'service'
            $job = Begin 'service' @(@{ do = 'settings'; fitWidth = $size[0]; fitHeight = $size[1] }, @{ do = 'sleep'; seconds = 6 }, (Mark $o 'fitted'), @{ do = 'wait'; anyOf = @('Ended', 'Failed'); timeout = 90 }, @{ do = 'sleep'; seconds = 5 })
            Reached $o 'fitted'
            $f.fitted = Wait-LabResolution -Name $VmName -Not $before -TimeoutSec 15 -NoThrow
            Invoke-LabGuest -Name $VmName -ScriptBlock { Restart-Service RemoteHubAgent -Force }
            $f.service = Receive-LabDesktop $job
            $null = Wait-LabCondition -NoThrow -TimeoutSec 20 -Because 'a desktop helper was still running after the service restart' -Until { -not @(Helper).Count }
            $f.helpersAfter = Helper
            $f.resAfter = Wait-LabResolution -Name $VmName -Is $before -TimeoutSec 20 -NoThrow
            $f.before = $before
            Save 'failures' $f
        }

        # the helper killed, the service restarted: the next context checks (and if need be rebuilds) the state
        AfterAll { Set-LabVmDirty -Name $VmName }

        It 'D-ROB-01 the helper killed: started again ("screen changed ... reconnecting"), no new question for the same user' -Tag 'D-ROB-01' {
            @($f.r.statuses | Where-Object message -match 'reconnecting').Count | Should -BeGreaterThan 0
            @($f.asked) | Should -BeNullOrEmpty
        }

        It 'D-ROB-02 the helper failing again and again (6 kills in a minute): Ended "kept failing"' -Tag 'D-ROB-02' {
            (Of $f.r 'Ended')[0].message | Should -Match 'kept failing'
        }

        It 'D-ROB-03 a service restart while connected: the session ends cleanly, no helper left, the resolution restored' -Tag 'D-ROB-03' {
            @($f.service.closes).Count | Should -BeGreaterThan 0
            @($f.helpersAfter) | Should -BeNullOrEmpty
            $f.fitted | Should -Not -Be $f.before -Because 'the fit changed the console''s resolution (precondition: else "restored" says nothing)'
            $f.resAfter | Should -Be $f.before
        }
    }

    Context 'fuzzing and ping' {
        BeforeAll {
            $pin = (Initialize-LabVm -Name $VmName -Agent -AutoLogon User).Pin
            $z = [ordered]@{}
            $since = Get-LabGuestTime -Name $VmName
            $z.ping = Receive-LabDesktop (Begin 'ping' @(@{ do = 'ping'; bytes = '0102030405060708' }, @{ do = 'close' }))
            $rng = New-Object Random 4711
            $junk = foreach ($k in 1..25) {
                $len = $rng.Next(1, 64); $b = New-Object byte[] $len; $rng.NextBytes($b)
                @{ do = 'raw'; hex = (($b | ForEach-Object { $_.ToString('X2') }) -join '') }
            }
            $z.fuzz = Receive-LabDesktop (Begin 'fuzz' (@($junk) + @(@{ do = 'rawJson'; type = 6; json = '{"monitor": "x", ' }, @{ do = 'rawJson'; type = 7; json = 'not json' }, @{ do = 'sleep'; seconds = 5 })))
            # a crash the junk caused has this time to show (event 3100, no health answer)
            Start-Sleep -Seconds 3
            $z.health = Get-LabAgentHealth -Name $VmName -Pin $pin
            $z.crashes = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 3100)
            $z.after = Receive-LabDesktop (Begin 'after-fuzz' @(@{ do = 'close' }))
            Save 'fuzz' $z
        }

        It 'D-ROB-06 Ping is answered with the same 8 bytes' -Tag 'D-ROB-06' {
            @($z.ping.pongs)[0].bytes | Should -Be '0102030405060708'
        }

        It 'D-ROB-05 fuzzed messages end the session; the agent stays healthy, no crash loop' -Tag 'D-ROB-05' {
            @($z.fuzz.closes).Count | Should -BeGreaterThan 0
            $z.health.deviceId | Should -Be (Get-LabAgentState -Name $VmName).Config.deviceId
            @($z.crashes).Count | Should -BeLessOrEqual 1
            (Of $z.after 'Connected').Count | Should -BeGreaterThan 0
        }
    }

    Context 'soak' {
        BeforeAll {
            $pin = (Initialize-LabVm -Name $VmName -Agent -AutoLogon User).Pin
            $minutes = if ($cfg.PSObject.Properties['SoakMinutes']) { [int]$cfg.SoakMinutes } else { 50 }
            $ws0 = Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-Process -Id (Get-CimInstance Win32_Service -Filter "Name='RemoteHubAgent'").ProcessId).WorkingSet64 }
            $steps = foreach ($k in 1..$minutes) { @{ do = 'mouse'; action = 'Move'; x = 100 + $k % 300; y = 200 }, @{ do = 'sleep'; seconds = 60 } }
            $soak = Receive-LabDesktop (Begin 'soak' (@($steps | ForEach-Object { $_ }) + @(@{ do = 'close' })))
            $ws1 = Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-Process -Id (Get-CimInstance Win32_Service -Filter "Name='RemoteHubAgent'").ProcessId).WorkingSet64 }
            Save 'soak' @{ Minutes = $minutes; Ws0 = $ws0; Ws1 = $ws1; Statuses = $soak.statuses; Frames = $soak.frames }
        }

        It 'D-ROB-04 one session for hours with input every minute: still connected, the agent''s working set grows < 50 MB' -Tag 'D-ROB-04' {
            @($soak.statuses | Where-Object state -in 'Ended', 'Failed') | Should -BeNullOrEmpty
            ($ws1 - $ws0) / 1MB | Should -BeLessThan 50
        }
    }
}
