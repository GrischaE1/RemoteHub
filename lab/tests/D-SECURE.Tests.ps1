# D-LOCK / D-SIGNIN / D-UAC / D-FUS / D-DISC / D-RDPSES / D-SAS - the secure desktop (lock, sign-in and UAC screens),
# users changing at the console and Ctrl+Alt+Del, through RemoteHub's desktop client (harness "desktop"). The person
# at the console types with the Hyper-V keyboard; the user's answers come from RhPerson.ps1 in their session; things in
# the picture are found with OCR on the decoded frame and clicked through the session (harness "click"). The layout's
# secureDesktop flag and the agent's log say which desktop is shown. See docs/test-lab-cases.md sections 9.5 and 9.6.
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $password = (Get-LabConfig).Password
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "d-secure-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "d-secure-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Of($r, [string]$state) { , @($r.statuses | Where-Object state -eq $state) }
    function Mark([string]$o, [string]$n) { @{ do = 'markFile'; path = (Join-Path $o "$n.txt") } }
    function Park([string]$o, [string]$n, [int]$t = 300) { @{ do = 'waitFile'; path = (Join-Path $o "$n.txt"); timeout = $t } }
    function Reached([string]$o, [string]$n, [int]$t = 180) { Wait-LabHostFile (Join-Path $o "$n.txt") -TimeoutSec $t }
    function Release([string]$o, [string]$n) { Set-Content (Join-Path $o "$n.txt") 'go' }
    function Secure($r) { @($r.layouts | ForEach-Object { $_.layout.secureDesktop }) }
    function Get-Sas([datetime]$Since) { @(Get-LabAgentLog -Name $VmName -Since $Since | Where-Object { $_ -match 'Ctrl\+Alt\+Del for' }) }
    function Lock-State { Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-Process LogonUI -ErrorAction SilentlyContinue).Count -gt 0 } }

    Reset-LabVm -Name $VmName
    $install = Install-LabAgent -Name $VmName
    $pin = $install.pin
    Set-LabAutoLogon -Name $VmName -Account User
    $null = Wait-LabAgent -Name $VmName -Pin $pin
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-secure-agent-logs')
}

Describe 'D-SECURE <VmName>' -Tag Desktop, 'D-LOCK', 'D-SIGNIN', 'D-UAC', 'D-FUS', 'D-DISC', 'D-RDPSES', 'D-SAS' {

    Context 'Ctrl+Alt+Del on the desktop' {
        BeforeAll {
            $o = Out 'sas-desktop'
            $since = Get-LabGuestTime -Name $VmName
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 90 }
                # the security options screen, then Esc
                @{ do = 'cad' }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'options.png' }, @{ do = 'key'; scan = '0x01' }, @{ do = 'key'; scan = '0x01'; flags = @('Up') }
                @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'back.png' }
                # ten times in a row
                @{ do = 'cad' }, @{ do = 'sleep'; seconds = 3 }, @{ do = 'key'; scan = '0x01' }, @{ do = 'sleep'; seconds = 3 }
                @{ do = 'cad' }, @{ do = 'sleep'; seconds = 3 }, @{ do = 'key'; scan = '0x01' }, @{ do = 'sleep'; seconds = 3 }
                @{ do = 'cad' }, @{ do = 'sleep'; seconds = 3 }, @{ do = 'key'; scan = '0x01' }, @{ do = 'sleep'; seconds = 3 }
                @{ do = 'cad' }, @{ do = 'sleep'; seconds = 3 }, @{ do = 'key'; scan = '0x01' }, @{ do = 'sleep'; seconds = 3 }
                @{ do = 'cad' }, @{ do = 'sleep'; seconds = 3 }, @{ do = 'key'; scan = '0x01' }, @{ do = 'sleep'; seconds = 3 }
                @{ do = 'cad' }, @{ do = 'sleep'; seconds = 3 }, @{ do = 'key'; scan = '0x01' }, @{ do = 'sleep'; seconds = 3 }
                @{ do = 'cad' }, @{ do = 'sleep'; seconds = 3 }, @{ do = 'key'; scan = '0x01' }, @{ do = 'sleep'; seconds = 3 }
                @{ do = 'cad' }, @{ do = 'sleep'; seconds = 3 }, @{ do = 'key'; scan = '0x01' }, @{ do = 'sleep'; seconds = 3 }
                # Task Manager from the options screen (found by OCR, clicked through the session)
                @{ do = 'cad' }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'options2.png' }, (Mark $o 'options2'), @{ do = 'click'; fromFile = (Join-Path $o 'taskmgr-xy.txt') }
                @{ do = 'sleep'; seconds = 6 }, (Mark $o 'taskmgr')
                # Lock from the options screen
                @{ do = 'cad' }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'options3.png' }, (Mark $o 'options3'), @{ do = 'click'; fromFile = (Join-Path $o 'lock-xy.txt') }
                @{ do = 'sleep'; seconds = 6 }, @{ do = 'frame'; out = 'locked.png' }, (Mark $o 'locked'), (Park $o 'go'), @{ do = 'close' })
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
            Reached $o 'options2'
            $xy = Find-LabPictureText -Path (Join-Path $o 'options2.png') -Text 'Task' ; if (-not $xy) { $xy = '0,0' }
            Set-Content (Join-Path $o 'taskmgr-xy.txt') $xy
            Reached $o 'taskmgr'
            $taskmgr = Invoke-LabGuest -Name $VmName -ScriptBlock { $p = @(Get-Process Taskmgr -ErrorAction SilentlyContinue); $p | Stop-Process -Force -ErrorAction SilentlyContinue; $p.Count }
            Reached $o 'options3'
            $xy = Find-LabPictureText -Path (Join-Path $o 'options3.png') -Text 'Lock'; if (-not $xy) { $xy = '0,0' }
            Set-Content (Join-Path $o 'lock-xy.txt') $xy
            Reached $o 'locked'
            $lockedNow = Lock-State
            Release $o 'go'
            $r = Receive-LabDesktop $job
            $optionsOcr = Get-LabOcr -Path (Join-Path $o 'options.png')
            $sas = Get-Sas $since
            Unlock-LabSession -Name $VmName
            Save 'sas-desktop' @{ R = $r; Options = $optionsOcr; Sas = $sas; TaskMgr = $taskmgr; Locked = $lockedNow }
        }

        It 'D-SAS-01 the console: Ctrl+Alt+Del reaches the secure desktop, the log line has SoftwareSASGeneration 1' -Tag 'D-SAS-01' {
            Secure $r | Should -Contain $true
            ($sas -join "`n") | Should -Match 'by the service: sent \(SoftwareSASGeneration = 1'
        }

        It 'D-SAS-08 on the desktop: the security options (Lock, Sign out, Task Manager) are in the picture; Esc returns' -Tag 'D-SAS-08' {
            $optionsOcr.text | Should -Match 'Lock'
            $optionsOcr.text | Should -Match 'Sign out'
            $optionsOcr.text | Should -Match 'Task Manager'
            @(Secure $r)[-1] | Should -BeFalse -Because 'the session ended back on the user''s desktop or the lock screen'
        }

        It 'D-SAS-10 Task Manager chosen there opens in the session' -Tag 'D-SAS-10' {
            $taskmgr | Should -BeGreaterThan 0
        }

        It 'D-SAS-09 Lock chosen by mouse: the session is locked, the connection stays, the lock screen is shown' -Tag 'D-SAS-09' {
            $lockedNow | Should -BeTrue
            @($r.closes | Where-Object by -eq 'agent') | Should -BeNullOrEmpty
            (Of $r 'Ended').Count | Should -Be 0
        }

        It 'D-SAS-11 ten Ctrl+Alt+Del in a row: every one works and is logged' -Tag 'D-SAS-11' {
            @($sas | Where-Object { $_ -match ': sent' }).Count | Should -BeGreaterOrEqual 11
        }
    }

    Context 'Ctrl+Alt+Del refused' {
        BeforeAll {
            $q = [ordered]@{}
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock { if (-not (Get-LocalGroup 'RH Others' -ErrorAction SilentlyContinue)) { New-LocalGroup 'RH Others' } }
            $run = {
                param($name)
                $j = Start-LabDesktop -Name $VmName -Pin $pin -Out (Out $name) -Steps @(
                    @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Denied', 'Failed'); timeout = 90 }
                    @{ do = 'cad' }, @{ do = 'sleep'; seconds = 5 }, @{ do = 'key'; scan = '0x01' }, @{ do = 'sleep'; seconds = 2 }, @{ do = 'close' })
                $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
                Receive-LabDesktop $j
            }
            Set-LabAgentPolicy -Name $VmName -Clear -Rights @{ SecureDesktop = @('RH Others') }
            $q.noRight = & $run 'sas-no-right'
            Set-LabAgentPolicy -Name $VmName -Clear
            # IT's policy: the agent's marker gone, the value set by "Group Policy"
            $setSas = { param($v) Invoke-LabGuest -Name $VmName -ArgumentList $v -ScriptBlock {
                    param($v) Remove-Item 'C:\ProgramData\RemoteHub\Agent\sas.agent' -ErrorAction SilentlyContinue
                    Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name SoftwareSASGeneration -Value $v -Type DWord } }
            & $setSas 0
            $q.policy0 = & $run 'sas-policy-0'
            & $setSas 3
            $q.policy3 = & $run 'sas-policy-3'
            & $setSas 1
            Save 'sas-refused' $q
        }

        It 'D-SAS-03 without the right SecureDesktop: "Your account may not use the sign-in screen on this device."' -Tag 'D-SAS-03' {
            @($q.noRight.statuses | Where-Object message -match 'may not use the sign-in screen').Count | Should -BeGreaterThan 0
            Secure $q.noRight | Should -Not -Contain $true
        }

        It 'D-SAS-04 policy 0: "turned off by a policy on this device", nothing happens' -Tag 'D-SAS-04' {
            @($q.policy0.statuses | Where-Object message -match 'turned off by a policy').Count | Should -BeGreaterThan 0
            Secure $q.policy0 | Should -Not -Contain $true
        }

        It 'D-SAS-05 policy 3 (services and ease of access): works' -Tag 'D-SAS-05' {
            Secure $q.policy3 | Should -Contain $true
        }

        It 'D-SAS-02 in an RDP session Ctrl+Alt+Del goes to that session' -Tag 'D-SAS-02' {
            Set-ItResult -Skipped -Because 'needs an RDP session on the device (2VM: RemoteHub in a client VM connected over RDP)'
        }
    }

    Context 'lock screen' {
        BeforeAll {
            $l = [ordered]@{}
            $since = Get-LabGuestTime -Name $VmName
            $marker = Start-LabMarker -Name $VmName   # the user's desktop is magenta: it must not show before they allow it
            # connected to the locked session; normaluser unlocks at the console and is asked
            Lock-LabSession -Name $VmName
            Start-Sleep -Seconds 5
            $o = Out 'lock'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'Failed'); timeout = 60 }
                @{ do = 'sleep'; seconds = 3 }, @{ do = 'frame'; out = 'locked.png' }, (Mark $o 'connected'), (Park $o 'unlocked')
                @{ do = 'wait'; state = 'WaitingForUser'; timeout = 60 }, @{ do = 'frame'; out = 'held.png' }, (Mark $o 'asking')
                @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 90 }, @{ do = 'sleep'; seconds = 2 }, @{ do = 'frame'; out = 'allowed.png' }
                # Win+L by the user while connected: the connection stays
                (Mark $o 'allowed'), (Park $o 'relocked'), @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'relocked.png' }, @{ do = 'close' })
            Reached $o 'connected'
            Unlock-LabSession -Name $VmName
            Release $o 'unlocked'
            Reached $o 'asking'
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
            Reached $o 'allowed'
            Lock-LabSession -Name $VmName
            Release $o 'relocked'
            $l.allow = Receive-LabDesktop $job
            Unlock-LabSession -Name $VmName
            $l.heldMagenta = Get-LabColorShare -Path (Join-Path $o 'held.png')
            $l.allowedMagenta = Get-LabColorShare -Path (Join-Path $o 'allowed.png')
            # the same, and the user refuses
            Lock-LabSession -Name $VmName
            Start-Sleep -Seconds 5
            $o = Out 'lock-deny'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Failed'); timeout = 60 }
                (Mark $o 'connected'), (Park $o 'unlocked'), @{ do = 'wait'; state = 'WaitingForUser'; timeout = 60 }, @{ do = 'wait'; anyOf = @('Denied', 'Ended', 'Connected'); timeout = 90 })
            Reached $o 'connected'
            Unlock-LabSession -Name $VmName
            Release $o 'unlocked'
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Deny' -TimeoutSec 60
            $l.deny = Receive-LabDesktop $job
            Stop-LabInSession -Name $VmName -Task $marker.Task
            Start-Sleep -Seconds 2
            $l.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1102, 2101)
            Save 'lock' $l
        }

        It 'D-LOCK-01 connecting to a locked session asks nobody; the lock screen is in the picture' -Tag 'D-LOCK-01' {
            # Windows 11 / Server 2025 show "Press Ctrl+Alt+Delete" on the Default desktop: no picture of it until
            # Ctrl+Alt+Del brings up the sign-in screen (the console unlock below does that) - then the secure desktop is shown
            $first = @($l.allow.statuses | Where-Object state -in 'Connected', 'WaitingForUser')[0]
            $first.state | Should -Be 'Connected'
            Secure $l.allow | Should -Contain $true
        }

        It 'D-LOCK-02 the user unlocks while connected: the picture holds, the user is asked, Allow continues, event 1102' -Tag 'D-LOCK-02' {
            (Of $l.allow 'WaitingForUser').Count | Should -BeGreaterThan 0
            @($l.events | Where-Object Id -eq 1102).Count | Should -BeGreaterThan 0
            $l.allowedMagenta | Should -BeGreaterThan 0.3 -Because 'after Allow the user''s desktop (the marker) is shown'
        }

        It 'D-LOCK-03 the user unlocks and refuses: Denied, event 2101, the session ends' -Tag 'D-LOCK-03' {
            @($l.deny.statuses | Where-Object state -in 'Denied', 'Ended').Count | Should -BeGreaterThan 0
            @($l.events | Where-Object Id -eq 2101).Count | Should -BeGreaterThan 0
        }

        It 'D-LOCK-04 Win+L by the user while connected: the connection stays, the lock screen is shown' -Tag 'D-LOCK-04' {
            (Of $l.allow 'Ended').Count | Should -Be 0
            @(Secure $l.allow)[-1] | Should -BeTrue
        }

        It 'D-LOCK-05 while waiting for the answer the picture is held: no user desktop before consent' -Tag 'D-LOCK-05' {
            $l.heldMagenta | Should -BeLessThan 0.05
        }
    }

    Context 'Ctrl+Alt+Del on the lock screen' {
        BeforeAll {
            Lock-LabSession -Name $VmName
            Start-Sleep -Seconds 5
            $o = Out 'sas-lock'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Failed'); timeout = 60 }
                @{ do = 'cad' }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'password.png' }, @{ do = 'keys'; text = "$password`n" }
                @{ do = 'wait'; state = 'WaitingForUser'; timeout = 90 }, (Mark $o 'asking'), @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 90 }, @{ do = 'close' })
            Reached $o 'asking'
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
            $r = Receive-LabDesktop $job
            $unlocked = -not (Lock-State)
            $pwOcr = Get-LabOcr -Path (Join-Path $o 'password.png')
            Save 'sas-lock' @{ R = $r; Unlocked = $unlocked; Ocr = $pwOcr }
        }

        It 'D-SAS-06 on the lock screen: Ctrl+Alt+Del shows the password field, the typed password unlocks, then normaluser is asked' -Tag 'D-SAS-06' {
            $pwOcr.text | Should -Match ([regex]::Escape($vm.UserAccount))
            $unlocked | Should -BeTrue
            (Of $r 'WaitingForUser').Count | Should -BeGreaterThan 0
            @($r.statuses | Where-Object state -eq 'Connected')[-1] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'UAC' {
        BeforeAll {
            $o = Out 'uac'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 90 }
                (Mark $o 'connected'), (Park $o 'uac-up'), @{ do = 'sleep'; seconds = 3 }, @{ do = 'frame'; out = 'uac.png' }
                # normaluser isn't an admin: the prompt asks for an admin's credentials
                @{ do = 'keys'; text = "$($vm.AdminAccount)`t$password`n"; delayMs = 30 }, @{ do = 'sleep'; seconds = 8 }, @{ do = 'close' })
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
            Reached $o 'connected'
            $task = Start-LabInSession -Name $VmName -ScriptBlock { Start-Process cmd.exe -ArgumentList '/k title RHLAB-ELEVATED' -Verb RunAs }
            $until = (Get-Date).AddSeconds(60)
            while (-not (Invoke-LabGuest -Name $VmName -ScriptBlock { [bool](Get-Process consent -ErrorAction SilentlyContinue) }) -and (Get-Date) -lt $until) { Start-Sleep 2 }
            Release $o 'uac-up'
            $r = Receive-LabDesktop $job
            $elevated = Invoke-LabGuest -Name $VmName -ScriptBlock {
                @(Get-CimInstance Win32_Process -Filter "Name='cmd.exe'" | Where-Object CommandLine -match 'RHLAB-ELEVATED' | ForEach-Object { (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User })
            }
            Stop-LabInSession -Name $VmName -Task $task
            $uacOcr = Get-LabOcr -Path (Join-Path $o 'uac.png')
            Save 'uac' @{ R = $r; Elevated = $elevated; Ocr = $uacOcr }
        }

        It 'D-UAC-01 UAC in normaluser''s session is in the picture and usable: an admin''s credentials typed through the session elevate' -Tag 'D-UAC-01' {
            $uacOcr.text | Should -Match 'User Account Control|Do you want to allow'
            @($elevated) | Should -Contain $vm.AdminAccount
        }
    }

    Context 'session disconnected, another user' {
        BeforeAll {
            $d = [ordered]@{}
            $o = Out 'disc'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 90 }
                (Mark $o 'connected'), @{ do = 'wait'; state = 'Paused'; timeout = 60 }, (Mark $o 'paused'), (Park $o 'back')
                @{ do = 'wait'; state = 'Connected'; timeout = 90 }, @{ do = 'sleep'; seconds = 3 }, @{ do = 'close' })
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
            Reached $o 'connected'
            $null = Invoke-LabInSession -Name $VmName -ScriptBlock { tsdiscon.exe }
            Reached $o 'paused' 90
            # normaluser comes back at the console (the sign-in screen offers the last user)
            Unlock-LabSession -Name $VmName
            Release $o 'back'
            $d.disc = Receive-LabDesktop $job
            Save 'disc' $d
        }

        It 'D-DISC-01 the session disconnected while connected: Paused, then the picture follows the console (sign-in screen) and the user back' -Tag 'D-DISC-01' {
            # tsdiscon on the console moves it to a new session: the service follows the console right after Paused
            # ("has a screen again" is the RDP-session case, 2VM)
            (Of $d.disc 'Paused').Count | Should -BeGreaterThan 0
            $afterPause = @($d.disc.statuses | Where-Object { $_.ms -gt (Of $d.disc 'Paused')[0].ms })
            @($afterPause | Where-Object state -eq 'Connected').Count | Should -BeGreaterThan 0
            @($afterPause | Where-Object state -in 'Ended', 'Failed') | Should -BeNullOrEmpty
        }

        It 'D-RDPSES-01 a user''s RDP session is listed with the client''s name and can be shown' -Tag 'D-RDPSES-01' {
            Set-ItResult -Skipped -Because 'needs an RDP session on the device (2VM: RemoteHub in a client VM connected over RDP)'
        }
    }

    Context 'fast user switching' {
        BeforeAll {
            $o = Out 'fus'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 90 }
                # Switch user through the picture, then Admin's tile and password
                @{ do = 'cad' }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'options.png' }, (Mark $o 'options'), @{ do = 'click'; fromFile = (Join-Path $o 'switch-xy.txt') }
                @{ do = 'sleep'; seconds = 8 }, @{ do = 'frame'; out = 'tiles.png' }, (Mark $o 'tiles'), @{ do = 'click'; fromFile = (Join-Path $o 'admin-xy.txt') }
                @{ do = 'sleep'; seconds = 4 }, @{ do = 'keys'; text = "$password`n" }
                @{ do = 'wait'; state = 'WaitingForUser'; timeout = 120 }, (Mark $o 'asking'), @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 90 }, @{ do = 'close' })
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
            Reached $o 'options'
            $xy = Find-LabPictureText -Path (Join-Path $o 'options.png') -Text 'Switch'; Set-Content (Join-Path $o 'switch-xy.txt') $(if ($xy) { $xy } else { '0,0' })
            Reached $o 'tiles'
            $xy = Find-LabPictureText -Path (Join-Path $o 'tiles.png') -Text $vm.ConsoleAccount -Last; Set-Content (Join-Path $o 'admin-xy.txt') $(if ($xy) { $xy } else { '0,0' })
            Reached $o 'asking'
            $asked = Invoke-LabPerson -Name $VmName -Account Console -Do read -TimeoutSec 60
            $null = Invoke-LabPerson -Name $VmName -Account Console -Do click -Text 'Allow' -TimeoutSec 60
            $fus = Receive-LabDesktop $job
            Save 'fus' @{ R = $fus; Asked = $asked }
        }

        It 'D-FUS-01 Switch user to Admin while connected: the picture follows the console, Admin is asked' -Tag 'D-FUS-01' {
            $asked.found | Should -BeTrue
            @($fus.statuses | Where-Object state -eq 'Connected').Count | Should -BeGreaterOrEqual 2
        }

        It 'D-FUS-02 normaluser''s answer doesn''t count for Admin: a new question' -Tag 'D-FUS-02' {
            (Of $fus 'WaitingForUser').Count | Should -BeGreaterOrEqual 2
        }
    }

    Context 'sign-in screen' {
        BeforeAll {
            $s = [ordered]@{}
            # nobody signed in: the console account (Admin) was last; the admin signs in through the picture
            Clear-LabAutoLogon -Name $VmName
            Reset-LabVm -Name $VmName
            $own = Install-LabAgent -Name $VmName
            Disconnect-LabConsole -Name $VmName
            $since = Get-LabGuestTime -Name $VmName
            $o = Out 'signin'
            $s.own = Invoke-LabDesktop -Name $VmName -Pin $own.pin -Account Console -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Failed'); timeout = 60 }
                @{ do = 'sleep'; seconds = 3 }, @{ do = 'frame'; out = 'signin.png' }, @{ do = 'cad' }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'credential.png' }
                @{ do = 'keys'; text = "$password`n" }, @{ do = 'sleep'; seconds = 25 }, @{ do = 'frame'; out = 'desktop.png' }, @{ do = 'close' })
            $s.desktopAfter = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | ForEach-Object { (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User }) }
            $s.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1102)
            $s.credOcr = Get-LabOcr -Path (Join-Path $o 'credential.png')
            # signing out while connected: the picture follows to the sign-in screen
            $o = Out 'signout'
            $job = Start-LabDesktop -Name $VmName -Pin $own.pin -Account Console -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Failed'); timeout = 60 }
                (Mark $o 'connected'), @{ do = 'sleep'; seconds = 25 }, @{ do = 'frame'; out = 'after.png' }, @{ do = 'close' })
            Reached $o 'connected'
            $null = Invoke-LabInSession -Name $VmName -Account Console -ScriptBlock { shutdown.exe /l }
            $s.signout = Receive-LabDesktop $job
            # someone else signs in at the console while PSAdmin is connected to the sign-in screen: they are asked
            $o = Out 'other'
            $job = Start-LabDesktop -Name $VmName -Pin $own.pin -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Failed'); timeout = 60 }
                (Mark $o 'connected'), @{ do = 'wait'; state = 'WaitingForUser'; timeout = 180 }, (Mark $o 'asking'), @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 90 }
                @{ do = 'sleep'; seconds = 2 }, @{ do = 'close' })
            Reached $o 'connected'
            Unlock-LabSession -Name $VmName -Account Console
            Reached $o 'asking'
            $null = Invoke-LabPerson -Name $VmName -Account Console -Do click -Text 'Allow' -TimeoutSec 60
            $s.other = Receive-LabDesktop $job
            Save 'signin' $s
        }

        It 'D-SIGNIN-01 nobody signed in: the sign-in screen is shown' -Tag 'D-SIGNIN-01' {
            (Of $s.own 'Connected').Count | Should -BeGreaterThan 0
            Secure $s.own | Should -Contain $true
        }

        It 'D-SIGNIN-02 the admin signs in through the picture: signed in, no question for the own account' -Tag 'D-SIGNIN-02' {
            @($s.desktopAfter) | Should -Contain $vm.ConsoleAccount
            (Of $s.own 'WaitingForUser').Count | Should -Be 0
            @($s.events | Where-Object { $_.Message -match 'own account' }).Count | Should -BeGreaterThan 0
        }

        It 'D-SAS-07 on the sign-in screen (nobody signed in): Ctrl+Alt+Del gives the credential field' -Tag 'D-SAS-07' {
            $s.credOcr.text | Should -Match 'Password|PIN|Sign-in options'
            @($s.desktopAfter) | Should -Contain $vm.ConsoleAccount
        }

        It 'D-SIGNIN-04 signing out while connected: the picture follows to the sign-in screen' -Tag 'D-SIGNIN-04' {
            (Of $s.signout 'Ended').Count | Should -Be 0
            @(Secure $s.signout)[-1] | Should -BeTrue
        }

        It 'D-SIGNIN-03 someone signs in at the console while connected: the helper follows, they are asked, Allow continues' -Tag 'D-SIGNIN-03' {
            (Of $s.other 'WaitingForUser').Count | Should -BeGreaterThan 0
            @($s.other.statuses | Where-Object state -eq 'Connected').Count | Should -BeGreaterOrEqual 2
        }
    }
}
