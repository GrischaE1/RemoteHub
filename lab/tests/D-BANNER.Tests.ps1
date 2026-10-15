# D-BAN / D-VIEW / D-RIGHT - what the user at the device sees while connected (the banner: wording, collapsing,
# Disconnect, can't be closed otherwise), view-only by choice and by right, and the SecureDesktop right. normaluser is
# signed in on the console and allows PSAdmin through the agent's dialog (RhPerson.ps1: OCR + real clicks); the
# picture RemoteHub gets is read with OCR on the lab host. See docs/test-lab-cases.md sections 9.3 and 9.4.
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "d-banner-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "d-banner-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Of($r, [string]$state) { , @($r.statuses | Where-Object state -eq $state) }
    # starts a session normaluser allows; returns the job once it is connected and parked at waitFile <out>\go.txt.
    # -Before: steps between Connected and the park; -After: steps after it (then close).
    function Connect-Allowed([string]$Name, [bool]$ViewOnly = $false, [object[]]$Before = @(), [object[]]$After = @(), [string]$Account = 'Admin', [string]$P = $pin) {
        $o = Out $Name
        $job = Start-LabDesktop -Name $VmName -Pin $P -Account $Account -Out $o -Steps (@(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; viewOnly = $ViewOnly }
                @{ do = 'wait'; anyOf = @('WaitingForUser', 'Failed'); timeout = 60 }, @{ do = 'wait'; anyOf = @('Connected', 'Denied', 'Failed'); timeout = 60 }) +
            $Before + @(@{ do = 'markFile'; path = (Join-Path $o 'ready.txt') }, @{ do = 'waitFile'; path = (Join-Path $o 'go.txt'); timeout = 300 }) + $After + @(@{ do = 'close' }))
        $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
        Wait-LabHostFile (Join-Path $o 'ready.txt') -TimeoutSec 120
        $job
    }
    function Go([string]$Name) { Set-Content (Join-Path (Out $Name) 'go.txt') 'go' }

    Reset-LabVm -Name $VmName
    $install = Install-LabAgent -Name $VmName
    $pin = $install.pin
    Set-LabAutoLogon -Name $VmName -Account User
    $null = Wait-LabAgent -Name $VmName -Pin $pin
    $is1809 = $vm.Version -eq '1809'
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-banner-agent-logs')
}

Describe 'D-BANNER <VmName>' -Tag Desktop, 'D-BAN', 'D-VIEW', 'D-RIGHT' {

    Context 'the banner while controlled' {
        BeforeAll {
            $b = [ordered]@{}
            $job = Connect-Allowed 'control' -Before @(@{ do = 'frame'; out = 'frame.png'; timeout = 30 }) -After @(
                @{ do = 'wait'; anyOf = @('Ended'); timeout = 120 }, @{ do = 'reconnect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; resume = 'last' }
                @{ do = 'wait'; anyOf = @('WaitingForUser', 'Connected', 'Failed'); timeout = 60 })
            $b.read = Invoke-LabPerson -Name $VmName -Do read -Title 'RemoteHub'
            $b.wide = @(Invoke-LabPerson -Name $VmName -Do windows)
            $null = Get-LabScreenshot -Name $VmName -Path (Join-Path (Out 'control') 'console.png')
            $b.consoleOcr = Get-LabOcr -Path (Join-Path (Out 'control') 'console.png')
            $b.frameOcr = Get-LabOcr -Path (Join-Path (Out 'control') 'frame.png')
            Start-Sleep -Seconds 12
            $b.collapsed = @(Invoke-LabPerson -Name $VmName -Do windows)
            $b.hover = Invoke-LabPerson -Name $VmName -Do hover -Title 'RemoteHub'
            # the user tries to get rid of it without Disconnect
            # Alt+F4 on the banner (a console click on its text gives it the focus)
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Controlled' -Title 'RemoteHub'
            Send-LabKeyCombo -Name $VmName -Codes 0x12, 0x73
            Start-Sleep -Seconds 2
            $b.close = [pscustomobject]@{ left = @(Invoke-LabPerson -Name $VmName -Do windows | Where-Object Title -eq 'RemoteHub').Count }
            $helper = Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-CimInstance Win32_Process -Filter "Name='RemoteHubAgent.exe'" | Where-Object CommandLine -match '\sdesktop\s*$' | Select-Object -First 1).ProcessId }
            $b.kill = Invoke-LabInSession -Name $VmName -ScriptBlock ([scriptblock]::Create("taskkill.exe /PID $helper /F 2>&1"))
            $b.afterTries = @(Invoke-LabPerson -Name $VmName -Do windows)
            # then Disconnect: the session ends, the resume token is void (asked again)
            Go 'control'
            Start-Sleep -Seconds 4
            $b.disconnect = Invoke-LabPerson -Name $VmName -Do click -Text 'Disconnect'
            $b.again = Invoke-LabPerson -Name $VmName -Do read -TimeoutSec 60
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Deny'
            $b.r = Receive-LabDesktop $job
            Save 'control' $b
        }

        It 'D-BAN-01 a topmost banner "Your screen is being viewed and controlled by PSAdmin" with Disconnect, on the console' -Tag 'D-BAN-01' {
            $b.read.found | Should -BeTrue
            $b.read.text | Should -Match "being viewed and controlled by .*$([regex]::Escape($vm.AdminAccount))"
            $b.read.text | Should -Match 'Disconnect'
            $b.consoleOcr.text | Should -Match 'viewed and controlled|Controlled by'
        }

        It 'D-BAN-02 the banner is not in the picture RemoteHub gets (Windows 2004 and later)' -Tag 'D-BAN-02' {
            if ($is1809) { Set-ItResult -Skipped -Because 'before Windows 2004 the banner can''t be kept out of the picture (D-BAN-03)'; return }
            $b.frameOcr.text | Should -Not -Match 'viewed and controlled|Controlled by'
        }

        It 'D-BAN-03 on 1809 the banner is in the picture, not black' -Tag 'D-BAN-03' {
            if (-not $is1809) { Set-ItResult -Skipped -Because 'needs the W10-1809 VM'; return }
            $b.frameOcr.text | Should -Match 'viewed and controlled|Controlled by'
        }

        It 'D-BAN-04 after 8 s the banner is the small pill; hovering expands it' -Tag 'D-BAN-04' {
            $wide = ($b.wide | Where-Object Title -eq 'RemoteHub').Width
            $pill = ($b.collapsed | Where-Object Title -eq 'RemoteHub').Width
            $pill | Should -BeLessThan $wide
            $b.hover.after.Width | Should -BeGreaterThan $pill
        }

        It 'D-BAN-06 the user can''t close it otherwise: WM_CLOSE (Alt+F4) and taskkill leave it' -Tag 'D-BAN-06' {
            $b.close.left | Should -BeGreaterThan 0
            "$($b.kill)" | Should -Match 'denied|ERROR'
            @($b.afterTries | Where-Object Title -eq 'RemoteHub').Count | Should -Be 1
        }

        It 'D-BAN-05 Disconnect by the user: Ended "The user at the device ended the session.", the resume token is void' -Tag 'D-BAN-05' {
            $b.disconnect.clicked | Should -BeTrue
            (Of $b.r 'Ended')[0].message | Should -Match 'The user at the device ended the session'
            # resumed with the token: asked again
            $b.again.found | Should -BeTrue
            (Of $b.r 'WaitingForUser').Count | Should -BeGreaterOrEqual 2
        }
    }

    Context 'view-only by choice' {
        BeforeAll {
            $v = [ordered]@{}
            $marker = Start-LabMarker -Name $VmName
            $null = Invoke-LabPerson -Name $VmName -Do clip-set -Value 'device clipboard before'
            $res = Invoke-LabGuest -Name $VmName -ScriptBlock { Add-Type -AssemblyName System.Windows.Forms; $s = [Windows.Forms.Screen]::PrimaryScreen.Bounds; "$($s.Width)x$($s.Height)" }
            $count = @(Get-LabMarker -Name $VmName -Marker $marker).Count
            $job = Connect-Allowed 'view' -ViewOnly $true -Before @(
                @{ do = 'keys'; text = 'viewonly' }, @{ do = 'mouse'; action = 'Down'; button = 'Left'; x = 200; y = 200 }, @{ do = 'mouse'; action = 'Up'; button = 'Left'; x = 200; y = 200 }
                @{ do = 'cad' }, @{ do = 'clipboardText'; text = 'from RemoteHub in view-only' }, @{ do = 'settings'; fitWidth = 1024; fitHeight = 768 }, @{ do = 'sleep'; seconds = 5 })
            $v.read = Invoke-LabPerson -Name $VmName -Do read -Title 'RemoteHub'
            $v.seen = @(Get-LabMarker -Name $VmName -Marker $marker | Select-Object -Skip $count | Where-Object e -in 'down', 'char', 'mdown', 'mup', 'clipboard')
            $v.clip = Invoke-LabPerson -Name $VmName -Do clip-get
            $v.res = Invoke-LabGuest -Name $VmName -ScriptBlock { Add-Type -AssemblyName System.Windows.Forms; $s = [Windows.Forms.Screen]::PrimaryScreen.Bounds; "$($s.Width)x$($s.Height)" }
            $v.logon = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-Process LogonUI -ErrorAction SilentlyContinue).Count }
            Go 'view'
            $v.r = Receive-LabDesktop $job
            Stop-LabInSession -Name $VmName -Task $marker.Task
            Save 'view' @{ V = $v; Before = $res }
        }

        It 'D-BAN-07 view-only: the banner says "viewed by", not "controlled"' -Tag 'D-BAN-07' {
            $v.read.text | Should -Match 'being viewed by'
            $v.read.text | Should -Not -Match 'controlled'
        }

        It 'D-VIEW-01 view-only drops mouse, keys, Ctrl+Alt+Del and the clipboard; fit doesn''t change the resolution' -Tag 'D-VIEW-01' {
            (Of $v.r 'Connected')[0].message | Should -Match 'view only'
            @($v.seen) | Should -BeNullOrEmpty
            $v.clip.start | Should -Be 'device clipboard before'
            $v.res | Should -Be $res
            $v.logon | Should -Be 0
        }
    }

    Context 'view-only by right' {
        BeforeAll {
            $null = New-LabGuestUser -Name $VmName -User 'viewer' -Group 'RH Viewers'
            Set-LabAgentPolicy -Name $VmName -Clear -Rights @{ DesktopViewOnly = @('RH Viewers') }
            $job = Connect-Allowed 'viewer' -Account 'viewer'
            $read = Invoke-LabPerson -Name $VmName -Do read -Title 'RemoteHub'
            Go 'viewer'
            $r = Receive-LabDesktop $job
            Set-LabAgentPolicy -Name $VmName -Clear
            Save 'viewer' @{ R = $r; Read = $read }
        }

        It 'D-VIEW-02 an account only in Rights\DesktopViewOnly: Hello control=false, forced view-only though Start asked for control' -Tag 'D-VIEW-02' {
            $r.hello.rights.control | Should -BeFalse
            (Of $r 'Connected')[0].message | Should -Match 'view only'
            @($r.layouts | Where-Object { $_.layout.viewOnly }).Count | Should -BeGreaterThan 0
            $read.text | Should -Match 'being viewed by'
        }
    }

    Context 'a key held when input stops being allowed' {
        BeforeAll {
            # no SecureDesktop right: when UAC's secure desktop comes up, input isn't allowed any more - the release of a
            # key still held down must get through, so nothing stays stuck in the session
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock { if (-not (Get-LocalGroup 'RH Others' -ErrorAction SilentlyContinue)) { New-LocalGroup 'RH Others' } }
            Set-LabAgentPolicy -Name $VmName -Clear -Rights @{ SecureDesktop = @('RH Others') }
            $marker = Start-LabMarker -Name $VmName
            $o = Out 'held'
            $job = Connect-Allowed 'held' -Before @(@{ do = 'key'; scan = '0x1E' }, @{ do = 'sleep'; seconds = 2 }) -After @(
                @{ do = 'markFile'; path = (Join-Path $o 'held.txt') }, @{ do = 'waitFile'; path = (Join-Path $o 'uac.txt'); timeout = 120 }, @{ do = 'key'; scan = '0x1E'; flags = @('Up') }, @{ do = 'sleep'; seconds = 3 })
            Go 'held'
            Wait-LabHostFile (Join-Path $o 'held.txt')
            $task = Start-LabInSession -Name $VmName -ScriptBlock { Start-Process cmd.exe -Verb RunAs }
            $until = (Get-Date).AddSeconds(60)
            while (-not (Invoke-LabGuest -Name $VmName -ScriptBlock { [bool](Get-Process consent -ErrorAction SilentlyContinue) }) -and (Get-Date) -lt $until) { Start-Sleep 2 }
            Set-Content (Join-Path $o 'uac.txt') 'up'
            $r = Receive-LabDesktop $job
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process consent -ErrorAction SilentlyContinue | Stop-Process -Force }
            Stop-LabInSession -Name $VmName -Task $task
            Start-Sleep -Seconds 3
            $stuck = Invoke-LabInSession -Name $VmName -ScriptBlock {
                Add-Type -Namespace RhLab -Name Keys -MemberDefinition '[DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);'
                [bool]([RhLab.Keys]::GetAsyncKeyState(0x41) -band 0x8000)
            }
            $events = @(Get-LabMarker -Name $VmName -Marker $marker)
            Stop-LabInSession -Name $VmName -Task $marker.Task
            Set-LabAgentPolicy -Name $VmName -Clear
            Save 'held' @{ R = $r; Events = $events; Stuck = "$stuck" }
        }

        It 'D-VIEW-03 a key held down when input stops being allowed (secure desktop without the right) is released - no stuck key' -Tag 'D-VIEW-03' {
            @($events | Where-Object { $_.e -eq 'down' -and $_.vk -eq 65 }).Count | Should -BeGreaterThan 0
            "$stuck".Trim() | Should -Be 'False'
        }
    }

    Context 'without the right SecureDesktop' {
        BeforeAll {
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock { if (-not (Get-LocalGroup 'RH Others' -ErrorAction SilentlyContinue)) { New-LocalGroup 'RH Others' } }
            Set-LabAgentPolicy -Name $VmName -Clear -Rights @{ SecureDesktop = @('RH Others') }
            # UAC opens while connected: no picture of it, input doesn't reach it
            $o = Out 'uac'
            $job = Connect-Allowed 'uac' -After @(
                @{ do = 'markFile'; path = (Join-Path $o 'uac-now.txt') }, @{ do = 'waitFile'; path = (Join-Path $o 'uac-up.txt'); timeout = 120 }
                @{ do = 'frame'; out = 'during-uac.png'; timeout = 10 }, @{ do = 'keys'; text = "`n" }, @{ do = 'sleep'; seconds = 3 })
            Go 'uac'
            Wait-LabHostFile (Join-Path $o 'uac-now.txt')
            $task = Start-LabInSession -Name $VmName -ScriptBlock { Start-Process cmd.exe -Verb RunAs }
            $until = (Get-Date).AddSeconds(60)
            while (-not (Invoke-LabGuest -Name $VmName -ScriptBlock { [bool](Get-Process consent -ErrorAction SilentlyContinue) }) -and (Get-Date) -lt $until) { Start-Sleep 2 }
            Set-Content (Join-Path $o 'uac-up.txt') 'up'
            $uac = Receive-LabDesktop $job
            $stillOpen = Invoke-LabGuest -Name $VmName -ScriptBlock { $c = @(Get-Process consent -ErrorAction SilentlyContinue); $c | Stop-Process -Force -ErrorAction SilentlyContinue; $c.Count -gt 0 }
            Stop-LabInSession -Name $VmName -Task $task
            # locked: Start is refused
            $since = Get-LabGuestTime -Name $VmName
            Lock-LabSession -Name $VmName
            Start-Sleep -Seconds 5
            $locked = Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out 'locked') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Failed', 'Connected', 'WaitingForUser'); timeout = 60 })
            Start-Sleep -Seconds 2
            $events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2100)
            Unlock-LabSession -Name $VmName
            Set-LabAgentPolicy -Name $VmName -Clear
            Save 'secure' @{ Uac = $uac; StillOpen = $stillOpen; Locked = $locked; Events = $events }
        }

        It 'D-RIGHT-01 Start on a locked session without SecureDesktop: Failed, event 2100' -Tag 'D-RIGHT-01' {
            (Of $locked 'Failed').Count | Should -Be 1
            (Of $locked 'Failed')[0].message | Should -Match 'may not see the sign-in screen'
            $events.Count | Should -BeGreaterThan 0
        }

        It 'D-RIGHT-02 UAC while connected without SecureDesktop: no picture of the prompt, Enter doesn''t reach it' -Tag 'D-RIGHT-02' {
            $frameStep = @($uac.steps | Where-Object do -eq 'frame')[0]
            $frameStep.ok | Should -BeFalse -Because 'no picture of the secure desktop may be sent'
            $stillOpen | Should -BeTrue -Because 'the prompt is still open after Enter was sent'
        }

        # the sheet has no own steps for it: "covered by D-RIGHT-02" - the same connection without the SecureDesktop right
        It 'D-UAC-02 the UAC prompt isn''t leaked to a connection without the SecureDesktop right (as D-RIGHT-02)' -Tag 'D-UAC-02' {
            @($uac.steps | Where-Object do -eq 'frame')[0].ok | Should -BeFalse
            $stillOpen | Should -BeTrue
        }
    }
}
