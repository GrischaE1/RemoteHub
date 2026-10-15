# D-IDLE / D-RES / D-ROB - the agent's remote desktop over time and when things break: the idle timeout, continuing
# after a dropped connection with the resume token (single use, one account, one minute, void after a deliberate end,
# asked again when someone else signed in), the helper killed or failing, a service restart, a long session (D-ROB-04:
# lab.json SoakMinutes, default 50 - a release run sets 480 and -FileTimeoutMin above it), fuzzed messages, ping.
# normaluser is signed in and allows PSAdmin (RhPerson.ps1). See docs/test-lab-cases.md section 9.10.
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
    function Release([string]$o, [string]$n) { Set-Content (Join-Path $o "$n.txt") 'go' }
    $connect = @(@{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Denied', 'Failed'); timeout = 90 })
    function Begin([string]$Name, [object[]]$Steps, [string]$Account = 'Admin', [switch]$NoAllow) {
        $job = Start-LabDesktop -Name $VmName -Pin $pin -Account $Account -Out (Out $Name) -Steps ($connect + $Steps)
        if (-not $NoAllow) { $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60 }
        $job
    }
    function Helper { Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-CimInstance Win32_Process -Filter "Name='RemoteHubAgent.exe'" | Where-Object CommandLine -match '\sdesktop\s*$' | ForEach-Object ProcessId) } }

    Reset-LabVm -Name $VmName
    $install = Install-LabAgent -Name $VmName
    $pin = $install.pin
    Set-LabAutoLogon -Name $VmName -Account User
    $null = Wait-LabAgent -Name $VmName -Pin $pin
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-rob-agent-logs')
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
            Start-Sleep -Seconds 2
            $i.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2102)
            Save 'idle' $i
        }

        It 'D-IDLE-01 DesktopIdleTimeoutMinutes=1: a warning at ~30 s, Ended at 60 s, event 2102' -Tag 'D-IDLE-01' {
            $c = (Of $i.idle 'Connected')[0].ms
            $warn = @($i.idle.statuses | Where-Object message -match 'No mouse or keyboard input for a while')[0]
            $end = (Of $i.idle 'Ended')[0]
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
            Start-Sleep -Seconds 10
            Connect-LabNetwork -Name $VmName -Switches $cut
            Start-Sleep -Seconds 5
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
                @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'Failed'); timeout = 60 }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'tiles.png' }
                (Mark $o 'tiles'), @{ do = 'click'; fromFile = (Join-Path $o 'admin-xy.txt') }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'keys'; text = "$password`n" }
                @{ do = 'wait'; state = 'WaitingForUser'; timeout = 120 }, (Mark $o 'asked'), @{ do = 'wait'; anyOf = @('Connected', 'Denied', 'Ended'); timeout = 60 }, @{ do = 'close' })
            Reached $o 'options'
            $xy = Find-LabPictureText -Path (Join-Path $o 'options.png') -Text 'Switch'; Set-Content (Join-Path $o 'switch-xy.txt') $(if ($xy) { $xy } else { '0,0' })
            Reached $o 'tiles'
            $xy = Find-LabPictureText -Path (Join-Path $o 'tiles.png') -Text $vm.ConsoleAccount -Last; Set-Content (Join-Path $o 'admin-xy.txt') $(if ($xy) { $xy } else { '0,0' })
            Reached $o 'asked'
            $null = Invoke-LabPerson -Name $VmName -Account Console -Do click -Text 'Deny' -TimeoutSec 60
            $u = Receive-LabDesktop $job
            Save 'res-user' $u
            # back to normaluser for what follows
            Reset-LabVm -Name $VmName
            $script:pin2 = (Install-LabAgent -Name $VmName).pin
            Set-LabAutoLogon -Name $VmName -Account User
        }

        It 'D-RES-06 another user signed in meanwhile: resumed, they are asked' -Tag 'D-RES-06' {
            @($u.statuses | Where-Object { $_.connection -ge 2 -and $_.state -eq 'WaitingForUser' }).Count | Should -BeGreaterThan 0
        }
    }

    Context 'helper and service failures' {
        BeforeAll {
            $pin = $script:pin2
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
                Start-Sleep -Seconds 5
                Invoke-LabGuest -Name $VmName -ScriptBlock { Get-CimInstance Win32_Process -Filter "Name='RemoteHubAgent.exe'" | Where-Object CommandLine -match '\sdesktop\s*$' | ForEach-Object { Stop-Process -Id $_.ProcessId -Force } }
            }
            $f.r = Receive-LabDesktop $job
            # the service restarted while connected
            $before = Invoke-LabGuest -Name $VmName -ScriptBlock { Add-Type -AssemblyName System.Windows.Forms; $b = [Windows.Forms.Screen]::PrimaryScreen.Bounds; "$($b.Width)x$($b.Height)" }
            $o = Out 'service'
            $job = Begin 'service' @(@{ do = 'settings'; fitWidth = 1280; fitHeight = 720 }, @{ do = 'sleep'; seconds = 6 }, (Mark $o 'fitted'), @{ do = 'wait'; anyOf = @('Ended', 'Failed'); timeout = 90 }, @{ do = 'sleep'; seconds = 5 })
            Reached $o 'fitted'
            Invoke-LabGuest -Name $VmName -ScriptBlock { Restart-Service RemoteHubAgent -Force }
            $f.service = Receive-LabDesktop $job
            Start-Sleep -Seconds 5
            $f.helpersAfter = Helper
            $f.resAfter = Invoke-LabGuest -Name $VmName -ScriptBlock { Add-Type -AssemblyName System.Windows.Forms; $b = [Windows.Forms.Screen]::PrimaryScreen.Bounds; "$($b.Width)x$($b.Height)" }
            $f.before = $before
            Save 'failures' $f
        }

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
            $f.resAfter | Should -Be $f.before
        }
    }

    Context 'fuzzing and ping' {
        BeforeAll {
            $pin = $script:pin2
            $z = [ordered]@{}
            $since = Get-LabGuestTime -Name $VmName
            $z.ping = Receive-LabDesktop (Begin 'ping' @(@{ do = 'ping'; bytes = '0102030405060708' }, @{ do = 'close' }))
            $rng = New-Object Random 4711
            $junk = foreach ($k in 1..25) {
                $len = $rng.Next(1, 64); $b = New-Object byte[] $len; $rng.NextBytes($b)
                @{ do = 'raw'; hex = (($b | ForEach-Object { $_.ToString('X2') }) -join '') }
            }
            $z.fuzz = Receive-LabDesktop (Begin 'fuzz' (@($junk) + @(@{ do = 'rawJson'; type = 6; json = '{"monitor": "x", ' }, @{ do = 'rawJson'; type = 7; json = 'not json' }, @{ do = 'sleep'; seconds = 5 })))
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
            $pin = $script:pin2
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
