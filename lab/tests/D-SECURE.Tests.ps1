# D-LOCK / D-SIGNIN / D-UAC / D-FUS / D-DISC / D-RDPSES / D-SAS - the secure desktop (lock, sign-in and UAC screens),
# users changing at the console and Ctrl+Alt+Del, through RemoteHub's desktop client (harness "desktop"). The person
# at the console types with the Hyper-V keyboard; the user's answers come from RhPerson.ps1 in their session; things in
# the picture are found with OCR on the decoded frame and clicked through the session (harness "click"). The layout's
# secureDesktop flag and the agent's log say which desktop is shown. See docs/test-lab-cases.md sections 9.5 and 9.6.
# LabRole: device
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
    # The person allows the connection (the agent's dialog counts down 30 s). Not clicked: the harness' parked steps are
    # released so it ends, and the context fails here with what the person read - not later at a missing picture.
    function Allow([string]$o, [string[]]$Release = @(), [string]$Account = 'User') {
        $a = Invoke-LabPerson -Name $VmName -Account $Account -Do click -Text 'Allow' -TimeoutSec 60
        if (-not ($a -and $a.PSObject.Properties['clicked'] -and $a.clicked)) {
            foreach ($n in $Release) { Release $o $n }
            $read = if ($a -and $a.PSObject.Properties['lines']) { @($a.lines) -join ' | ' } else { '' }
            throw "$(Split-Path $o -Leaf): Allow on the agent's consent dialog wasn't clicked within 60 s (the dialog asks for 30 s); OCR read: $read"
        }
        $a
    }
    # A name as OCR may read it: any 5 letters of it in a row ("normaluse(" and "maluser" for normaluser).
    function NameLike([string]$n) {
        $k = [Math]::Min(5, $n.Length)
        (@(0..($n.Length - $k) | ForEach-Object { [regex]::Escape($n.Substring($_, $k)) }) -join '|')
    }

    $init = Initialize-LabVm -Name $VmName -Agent -AutoLogon User
    $install = $init.Install
    $pin = $init.Pin
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-secure-agent-logs')
    # SoftwareSASGeneration, users switched and signed out at the console, the VM reverted by the last context: not for the next file
    Set-LabVmDirty -Name $VmName
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
            $null = Allow $o @('go')
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
                # Windows 11's "Press Ctrl+Alt+Delete" screen and the held picture send no new frame (D-LOCK-01, D-LOCK-05):
                # short frame timeouts, so the wait doesn't eat the consent dialog's 30 s (run 20261016-212514: the 30 s
                # default for held.png ran out with the countdown and normaluser's answer came too late - Denied)
                @{ do = 'sleep'; seconds = 3 }, @{ do = 'frame'; out = 'locked.png'; timeout = 5 }, (Mark $o 'connected'), (Park $o 'unlocked')
                @{ do = 'wait'; state = 'WaitingForUser'; timeout = 60 }, @{ do = 'frame'; out = 'held.png'; timeout = 5 }, (Mark $o 'asking')
                @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 90 }, @{ do = 'sleep'; seconds = 2 }, @{ do = 'frame'; out = 'allowed.png' }
                # Win+L by the user while connected: the connection stays
                (Mark $o 'allowed'), (Park $o 'relocked'), @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'relocked.png' }, @{ do = 'close' })
            Reached $o 'connected'
            Unlock-LabSession -Name $VmName
            Release $o 'unlocked'
            Reached $o 'asking'
            try { $null = Allow $o @('relocked') }
            catch { Stop-LabInSession -Name $VmName -Task $marker.Task; throw }
            Reached $o 'allowed'
            Lock-LabSession -Name $VmName
            Release $o 'relocked'
            $l.allow = Receive-LabDesktop $job
            Unlock-LabSession -Name $VmName
            # no held.png: no new picture came while the user was asked - nothing of their desktop either
            $held = Join-Path $o 'held.png'
            $l.heldPicture = Test-Path $held
            $l.heldMagenta = if ($l.heldPicture) { Get-LabColorShare -Path $held } else { 0 }
            $l.allowedMagenta = Get-LabColorShare -Path (Join-Path $o 'allowed.png')
            $l.relockedMagenta = Get-LabColorShare -Path (Join-Path $o 'relocked.png')
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
            # Win+L passes through the secure desktop, then Windows 11 / Server 2025 show the lock screen on the Default
            # desktop (as in D-LOCK-01): the last layout is not secure (run 20261019-193627), so the case checks the
            # switch after Allow and that the picture is the lock screen, no longer the user's desktop (the marker)
            $allowedAt = (Of $l.allow 'Connected')[-1].ms   # (Of answers the list itself: @(...) would wrap it once more)
            @($l.allow.layouts | Where-Object { $_.ms -gt $allowedAt -and $_.layout.secureDesktop }).Count | Should -BeGreaterThan 0
            $l.relockedMagenta | Should -BeLessThan 0.05 -Because 'd-secure-lock\relocked.png shows the lock screen, not the marker'
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
            $null = Allow $o
            $r = Receive-LabDesktop $job
            $unlocked = -not (Lock-State)
            $pwOcr = Get-LabOcr -Path (Join-Path $o 'password.png')
            Save 'sas-lock' @{ R = $r; Unlocked = $unlocked; Ocr = $pwOcr }
        }

        It 'D-SAS-06 on the lock screen: Ctrl+Alt+Del shows the password field, the typed password unlocks, then normaluser is asked' -Tag 'D-SAS-06' {
            # the name is small on the wallpaper: OCR read "maluser ... normaluse(" in run 20261016-212514
            # the caret in the empty field hides the P: OCR read "assword" in run 20261019-193627
            $pwOcr.text | Should -Match 'assword|PIN'
            $pwOcr.text | Should -Match (NameLike $vm.UserAccount)
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
                # normaluser isn't an admin: the prompt offers the device's administrators with the first one (Admin, the
                # console account) chosen and its password field focused - so only the password is typed (run
                # 20261016-212514 typed "PSAdmin<Tab>" into that field and nothing was elevated)
                @{ do = 'keys'; text = "$password`n"; delayMs = 30 }, @{ do = 'sleep'; seconds = 8 }, @{ do = 'close' })
            $null = Allow $o @('uac-up')
            Reached $o 'connected'
            $task = Start-LabInSession -Name $VmName -ScriptBlock { Start-Process cmd.exe -ArgumentList '/k title RHLAB-ELEVATED' -Verb RunAs }
            $until = (Get-Date).AddSeconds(60)
            while (-not (Invoke-LabGuest -Name $VmName -ScriptBlock { [bool](Get-Process consent -ErrorAction SilentlyContinue) }) -and (Get-Date) -lt $until) { Start-Sleep 2 }
            Release $o 'uac-up'
            $r = Receive-LabDesktop $job
            $elevated = Invoke-LabGuest -Name $VmName -ScriptBlock {
                @(Get-CimInstance Win32_Process -Filter "Name='cmd.exe'" | Where-Object CommandLine -match 'RHLAB-ELEVATED' | ForEach-Object { (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User })
            }
            # a prompt left open keeps the user's session on the secure desktop: the person's screen capture there fails
            # ("The handle is invalid") and the next contexts can't answer the agent - closed here whatever happened
            $uacLeft = Invoke-LabGuest -Name $VmName -ScriptBlock { $p = @(Get-Process consent -ErrorAction SilentlyContinue); $p | Stop-Process -Force -ErrorAction SilentlyContinue; $p.Count }
            Stop-LabInSession -Name $VmName -Task $task
            $uacOcr = Get-LabOcr -Path (Join-Path $o 'uac.png')
            Save 'uac' @{ R = $r; Elevated = $elevated; Ocr = $uacOcr; PromptLeftOpen = $uacLeft }
        }

        It 'D-UAC-01 UAC in normaluser''s session is in the picture and usable: an admin''s password typed through the session elevates' -Tag 'D-UAC-01' {
            $uacOcr.text | Should -Match 'User Account Control|Do you want to allow'
            $uacOcr.text | Should -Match (NameLike $vm.ConsoleAccount) -Because 'the prompt offers the console account first and the test types only its password'
            @($elevated) | Should -Contain $vm.ConsoleAccount
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
            $null = Allow $o @('back')
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
            # Admin is asked in a session that has just started: the person's first call there is slow (20-45 s), so the
            # dialog counts down longer than the default 30 s
            Set-LabAgentPolicy -Name $VmName -Values @{ ConsentTimeoutSeconds = 90 }
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 90 }
                # Switch user through the picture, then Admin's tile and password
                @{ do = 'cad' }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'options.png' }, (Mark $o 'options'), @{ do = 'click'; fromFile = (Join-Path $o 'switch-xy.txt') }
                # Server 2025 shows "Press Ctrl+Alt+Delete to unlock" first (run 20261019-193627): Ctrl+Alt+Del then
                @{ do = 'sleep'; seconds = 8 }, @{ do = 'frame'; out = 'curtain.png' }, (Mark $o 'curtain'), @{ do = 'cad'; ifFile = (Join-Path $o 'cad.txt') }
                # the tiles read in up to four pictures: on a slow switch the first still shows the spinner (run
                # 20261022-061254, Server 2025: tile list read empty)
                @(1..4 | ForEach-Object { @{ do = 'sleep'; seconds = 5 }, @{ do = 'frame'; out = "tiles-$_.png" }, (Mark $o "tiles-$_"), (Park $o "tiles-$_-go" 120) })
                @{ do = 'click'; fromFile = (Join-Path $o 'admin-xy.txt') }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'selected.png' }, (Mark $o 'selected'), (Park $o 'type' 120), @{ do = 'keys'; text = "$password`n" }
                @{ do = 'wait'; state = 'WaitingForUser'; timeout = 120 }, (Mark $o 'asking'), @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 120 }, @{ do = 'close' })
            $null = Allow $o @('type')
            Reached $o 'options'
            $xy = Find-LabPictureText -Path (Join-Path $o 'options.png') -Text 'Switch'; Set-Content (Join-Path $o 'switch-xy.txt') $(if ($xy) { $xy } else { '0,0' })
            Reached $o 'curtain'
            Set-Content (Join-Path $o 'cad.txt') (Get-LabCadAnswer -Picture (Join-Path $o 'curtain.png'))
            # the tile names are small on the wallpaper and OCR reads them in parts ("min" for Admin): matched by distance
            # to the device's accounts, not as an exact word (an exact "Admin" clicked 0,0 in D-ROBUST)
            $tile = $null
            foreach ($n in 1..4) {
                Reached $o "tiles-$n"
                if (-not ($tile -and $tile.XY)) {
                    $tile = Find-LabSignInTile -Name $VmName -Picture (Join-Path $o "tiles-$n.png") -Account $vm.ConsoleAccount
                    Copy-Item (Join-Path $o "tiles-$n.png") (Join-Path $o 'tiles.png') -Force
                }
                # where to click, before the last picture lets the harness go on to the click
                if ($n -eq 4) { Set-Content (Join-Path $o 'admin-xy.txt') $(if ($tile.XY) { $tile.XY } else { '0,0' }) }
                Release $o "tiles-$n-go"
            }
            Reached $o 'selected'
            # the chosen tile's name stands large in the middle; when it isn't Admin the password signs normaluser in
            # again and nobody new is asked - the context stops here with the pictures as evidence
            $chosen = Find-LabSignInTile -Name $VmName -Picture (Join-Path $o 'selected.png') -Account $vm.ConsoleAccount -Selected
            Release $o 'type'
            if (-not $chosen.Selected) {
                Set-LabAgentPolicy -Name $VmName -Clear
                throw "fus: $($vm.ConsoleAccount)'s tile wasn't chosen on the sign-in screen (d-secure-fus\tiles.png, selected.png); tile list read: $(@($tile.Lines) -join ', '); chosen: $(@($chosen.Lines) -join ', ')"
            }
            Reached $o 'asking' 240
            try { $asked = Allow $o -Account Console }
            finally { Set-LabAgentPolicy -Name $VmName -Clear }
            $fus = Receive-LabDesktop $job
            Save 'fus' @{ R = $fus; Asked = $asked; Tile = $tile; Chosen = $chosen }
        }

        It 'D-FUS-01 Switch user to Admin while connected: the picture follows the console, Admin is asked' -Tag 'D-FUS-01' {
            $asked.clicked | Should -BeTrue
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
            # the agent's log of the contexts above, before the revert below takes it (run 20261016-212514 kept only this context's)
            Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-secure-agent-logs-before-signin')
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
                (Mark $o 'connected'), (Park $o 'signedout' 240), @{ do = 'sleep'; seconds = 5 }, @{ do = 'frame'; out = 'after.png' }, @{ do = 'close' })
            Reached $o 'connected'
            # started, not waited for: the sign-out ends the session task before it can report (run 20261016-212514:
            # "no result from the session task after 120 s"); the sign-out itself is waited for on the device
            $out = Start-LabInSession -Name $VmName -Account Console -ScriptBlock { shutdown.exe /l }
            $until = (Get-Date).AddSeconds(180)
            $s.signedOut = $false
            while ((Get-Date) -lt $until) {
                $s.signedOut = -not (Invoke-LabGuest -Name $VmName -ArgumentList $vm.ConsoleAccount -ScriptBlock {
                        param($a) @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Where-Object { (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User -eq $a }).Count -gt 0 })
                if ($s.signedOut) { break }
                Start-Sleep -Seconds 2
            }
            Release $o 'signedout'
            Stop-LabInSession -Name $VmName -Task $out
            $s.signout = Receive-LabDesktop $job
            # someone else signs in at the console while PSAdmin is connected to the sign-in screen: they are asked - in a
            # session that has just started, where the person's first call is slow (20-45 s): a longer countdown
            Set-LabAgentPolicy -Name $VmName -Values @{ ConsentTimeoutSeconds = 90 }
            $o = Out 'other'
            $job = Start-LabDesktop -Name $VmName -Pin $own.pin -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Failed'); timeout = 60 }
                (Mark $o 'connected'), @{ do = 'wait'; state = 'WaitingForUser'; timeout = 180 }, (Mark $o 'asking'), @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 120 }
                @{ do = 'sleep'; seconds = 2 }, @{ do = 'close' })
            Reached $o 'connected'
            try {
                Unlock-LabSession -Name $VmName -Account Console
                Reached $o 'asking'
                # not Allow: a miss here fails D-SIGNIN-03 only, not the cases above
                $s.allowOther = Invoke-LabPerson -Name $VmName -Account Console -Do click -Text 'Allow' -TimeoutSec 60
            }
            finally { Set-LabAgentPolicy -Name $VmName -Clear }
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
            $s.signedOut | Should -BeTrue -Because "shutdown /l in $($vm.ConsoleAccount)'s session signs them out (d-secure-signout\after.png)"
            (Of $s.signout 'Ended').Count | Should -Be 0
            @(Secure $s.signout)[-1] | Should -BeTrue
        }

        It 'D-SIGNIN-03 someone signs in at the console while connected: the helper follows, they are asked, Allow continues' -Tag 'D-SIGNIN-03' {
            (Of $s.other 'WaitingForUser').Count | Should -BeGreaterThan 0
            $s.allowOther.clicked | Should -BeTrue -Because "Allow in $($vm.ConsoleAccount)'s session; OCR read: $(@($s.allowOther.lines) -join ' | ')"
            @($s.other.statuses | Where-Object state -eq 'Connected').Count | Should -BeGreaterOrEqual 2
        }
    }
}
