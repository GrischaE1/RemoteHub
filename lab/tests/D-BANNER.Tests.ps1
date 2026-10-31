# D-BAN / D-VIEW / D-RIGHT - what the user at the device sees while connected (the banner: wording, collapsing,
# Disconnect, can't be closed otherwise), view-only by choice and by right, and the SecureDesktop right. normaluser is
# signed in on the console and allows PSAdmin through the agent's dialog (RhPerson.ps1: OCR + real clicks); the
# picture RemoteHub gets is read with OCR on the lab host. See docs/test-lab-cases.md sections 9.3 and 9.4.
# LabRole: device
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
        $allow = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
        if (-not $allow.clicked) {
            # not connected: the steps after the park are let run to their end, and the context fails here rather than
            # at a missing picture or a wrong state later on
            Set-Content -Encoding ASCII (Join-Path $o 'go.txt') 'go'
            throw "${Name}: Allow on the agent's consent dialog wasn't clicked within 60 s (the dialog asks for 30 s); OCR read: $(@($allow.lines) -join ' | ')$(if ($allow.PSObject.Properties['seen']) { '; windows seen: ' + (@($allow.seen) -join ' || ') })"
        }
        Wait-LabHostFile (Join-Path $o 'ready.txt') -TimeoutSec 120
        $job
    }
    function Go([string]$Name) { Set-Content -Encoding ASCII (Join-Path (Out $Name) 'go.txt') 'go' }

    $init = Initialize-LabVm -Name $VmName -Agent -AutoLogon User
    $install = $init.Install
    $pin = $init.Pin
    $is1809 = $vm.Version -eq '1809'
    # the person's first call after the sign-in starts its server, compiles its helper and starts the OCR (slow): done
    # here, not against the consent dialog's 30 s countdown (as in D-MORE)
    $null = Invoke-LabPerson -Name $VmName -Do windows
    $null = Invoke-LabPerson -Name $VmName -Do screen-read
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-banner-agent-logs')
    # left reusable: the policies and rights cleared, the account 'viewer' (and a profile, if one was made) and the
    # groups 'RH Viewers' / 'RH Others' removed
    Set-LabAgentPolicy -Name $VmName -Clear
    $null = Invoke-LabGuest -Name $VmName -ScriptBlock {
        $u = Get-LocalUser -Name 'viewer' -ErrorAction SilentlyContinue
        if ($u) { Get-CimInstance Win32_UserProfile -Filter "SID='$($u.SID.Value)'" -ErrorAction SilentlyContinue | Remove-CimInstance -ErrorAction SilentlyContinue }
        Remove-LocalUser -Name 'viewer' -ErrorAction SilentlyContinue
        Remove-LocalGroup -Name 'RH Viewers' -ErrorAction SilentlyContinue
        Remove-LocalGroup -Name 'RH Others' -ErrorAction SilentlyContinue
    }
}

Describe 'D-BANNER <VmName>' -Tag Desktop, 'D-BAN', 'D-VIEW', 'D-RIGHT' {

    Context 'the banner while controlled' {
        BeforeAll {
            $b = [ordered]@{}
            $job = Connect-Allowed 'control' -Before @(@{ do = 'frame'; out = 'frame.png'; timeout = 30 }) -After @(
                @{ do = 'wait'; anyOf = @('Ended'); timeout = 120 }, @{ do = 'reconnect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; resume = 'last' }
                @{ do = 'wait'; anyOf = @('WaitingForUser', 'Connected', 'Failed'); timeout = 60 }
                # asked again: the connection stays until the question is answered (the test clicks Deny), else it would
                # end at once and take the question with it before it is read
                @{ do = 'wait'; anyOf = @('Denied', 'Connected', 'Ended', 'Failed'); timeout = 90 })
            # read until the banner has its text (run 20261021-062735 caught it half drawn), at most 20 s
            $readUntil = (Get-Date).AddSeconds(20)
            do { $b.read = Invoke-LabPerson -Name $VmName -Do read -Title 'RemoteHub'; if ("$($b.read.text)" -match '(being viewed and controlled|Controlled) by') { break }; Start-Sleep -Seconds 1 } while ((Get-Date) -lt $readUntil)
            $b.wide = @(Invoke-LabPerson -Name $VmName -Do windows)
            $null = Get-LabScreenshot -Name $VmName -Path (Join-Path (Out 'control') 'console.png')
            $b.consoleOcr = Get-LabOcr -Path (Join-Path (Out 'control') 'console.png')
            $b.frameOcr = Get-LabOcr -Path (Join-Path (Out 'control') 'frame.png')
            # the banner collapses by its own 8 s timer; nothing on the guest says "collapsed" without the pill's width,
            # which is what D-BAN-04 measures
            Start-Sleep -Seconds 12
            $b.collapsed = @(Invoke-LabPerson -Name $VmName -Do windows)
            $b.hover = Invoke-LabPerson -Name $VmName -Do hover -Title 'RemoteHub'
            # the user tries to get rid of it without Disconnect: a click on its text, then Alt+F4. The banner never takes
            # the focus (WS_EX_NOACTIVATE), so Alt+F4 goes to the foreground window. Without a window of the user's own
            # that is the shell: on Server 2025 the taskbar had the focus and normaluser was signed out there (runs
            # 20261016-212514 and 20261017-054704: helper ended, session 2 at the sign-in screen, every later context
            # without a session). The marker is made the foreground window first (a console click on it), so the
            # key lands there.
            $closeMarker = Start-LabMarker -Name $VmName
            if (-not $closeMarker.Shown) { throw "the marker window didn't show, so Alt+F4 would reach the shell ($($closeMarker.Why))" }
            Send-LabMouse -Name $VmName -X 200 -Y 400
            # the click makes the marker the foreground window before the keys
            Start-Sleep -Seconds 1
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Controlled' -Title 'RemoteHub'
            Send-LabKeyCombo -Name $VmName -Codes 0x12, 0x73
            # the banner must not close in this time (and the marker has the key by then)
            Start-Sleep -Seconds 2
            $b.close = [pscustomobject]@{ left = @(Invoke-LabPerson -Name $VmName -Do windows | Where-Object Title -eq 'RemoteHub').Count
                altF4 = @(Get-LabMarker -Name $VmName -Marker $closeMarker | Where-Object { $_.e -eq 'down' -and $_.vk -eq 115 }).Count }
            Stop-LabInSession -Name $VmName -Task $closeMarker.Task
            $helper = Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-CimInstance Win32_Process -Filter "Name='RemoteHubAgent.exe'" | Where-Object CommandLine -match '\sdesktop\s*$' | Select-Object -First 1).ProcessId }
            $b.kill = Invoke-LabInSession -Name $VmName -ScriptBlock ([scriptblock]::Create("taskkill.exe /PID $helper /F 2>&1"))
            $b.afterTries = @(Invoke-LabPerson -Name $VmName -Do windows)
            # then Disconnect: the session ends, the resume token is void (asked again)
            Go 'control'
            # Disconnect is on the open banner only: by now it has collapsed to the pill (the pointer left it for the
            # marker), and lab run 20261019-193627 (W11) found no "Disconnect" at all - neither the session's OCR (the
            # banner is kept out of captures) nor the console's (no words read in its area). Invoke-LabBannerClick opens
            # it with a hover first, finds the whole label in the console picture and keeps that picture.
            $b.disconnect = Invoke-LabBannerClick -Name $VmName -Label 'Disconnect' -TimeoutSec 30 -EvidencePath (Join-Path (Out 'control') 'disconnect-console.png')
            $b.again = Invoke-LabPerson -Name $VmName -Do read -TimeoutSec 60
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Deny'
            $b.r = Receive-LabDesktop $job
            Save 'control' $b
        }

        It 'D-BAN-01 a topmost banner "Your screen is being viewed and controlled by PSAdmin" with Disconnect, on the console' -Tag 'D-BAN-01' {
            $b.read.found | Should -BeTrue
            # full text or the pill; OCR splits the account over lines and reads the title in between ("DESKTOP-\nRemoteHub\nOUNPN53\PSAdmin", run 20261019-193627)
            $b.read.text | Should -Match "(?s)(being viewed and controlled|Controlled) by .*$([regex]::Escape($vm.AdminAccount))"
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
            # the full width from the hover, not from $b.wide: that is read after the connect and the OCR, which can take
            # longer than the 8 s, so it often caught the pill already (412 = 412, most runs since 16 Oct)
            $pill = ($b.collapsed | Where-Object Title -eq 'RemoteHub').Width
            $b.hover.before.Width | Should -Be $pill -Because 'collapsed before the mouse came'
            $b.hover.after.Width | Should -BeGreaterThan $pill
        }

        It 'D-BAN-06 the user can''t close it otherwise: WM_CLOSE (Alt+F4) and taskkill leave it' -Tag 'D-BAN-06' {
            $b.close.left | Should -BeGreaterThan 0
            "$($b.kill)" | Should -Match 'denied|ERROR'
            @($b.afterTries | Where-Object Title -eq 'RemoteHub').Count | Should -Be 1
        }

        It 'D-BAN-05 Disconnect by the user: Ended "The user at the device ended the session.", the resume token is void' -Tag 'D-BAN-05' {
            $b.disconnect.clicked | Should -BeTrue -Because "Disconnect on the banner ($($b.disconnect.why); read there: $(@($b.disconnect.lines) -join ' | '); d-banner-control\disconnect-console.png)"
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
            $res = Get-LabResolution -Name $VmName
            # a fit to a size the console isn't at: else an ignored fit and an obeyed one look the same
            $size = if ($res -eq '1024x768') { @(1280, 720) } else { @(1024, 768) }
            # the marker records the test's own clip-set too: counted once that is in (run 20261019-193627 counted
            # before it and took it for the clipboard reaching the device in view-only)
            $until = (Get-Date).AddSeconds(15)
            do {
                $before = @(Get-LabMarker -Name $VmName -Marker $marker)
                if (@($before | Where-Object { $_.e -eq 'clipboard' -and "$($_.start)" -eq 'device clipboard before' }).Count) { break }
                Start-Sleep -Milliseconds 500
            } while ((Get-Date) -lt $until)
            $count = $before.Count
            $job = Connect-Allowed 'view' -ViewOnly $true -Before @(
                @{ do = 'keys'; text = 'viewonly' }, @{ do = 'mouse'; action = 'Down'; button = 'Left'; x = 200; y = 200 }, @{ do = 'mouse'; action = 'Up'; button = 'Left'; x = 200; y = 200 }
                @{ do = 'cad' }, @{ do = 'clipboardText'; text = 'from RemoteHub in view-only' }, @{ do = 'settings'; fitWidth = $size[0]; fitHeight = $size[1] }, @{ do = 'sleep'; seconds = 5 })
            # read until the banner has its text (run 20261021-062735 caught it half drawn), at most 20 s
            $readUntil = (Get-Date).AddSeconds(20)
            do { $v.read = Invoke-LabPerson -Name $VmName -Do read -Title 'RemoteHub'; if ("$($v.read.text)" -match '(being viewed|Viewed) by') { break }; Start-Sleep -Seconds 1 } while ((Get-Date) -lt $readUntil)
            $v.seen = @(Get-LabMarker -Name $VmName -Marker $marker | Select-Object -Skip $count | Where-Object e -in 'down', 'char', 'mdown', 'mup', 'clipboard')
            $v.clip = Invoke-LabPerson -Name $VmName -Do clip-get
            $v.res = Get-LabResolution -Name $VmName
            $v.logon = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-Process LogonUI -ErrorAction SilentlyContinue).Count }
            Go 'view'
            $v.r = Receive-LabDesktop $job
            Stop-LabInSession -Name $VmName -Task $marker.Task
            Save 'view' @{ V = $v; Before = $res }
        }

        It 'D-BAN-07 view-only: the banner says "viewed by", not "controlled"' -Tag 'D-BAN-07' {
            $v.read.text | Should -Match '(being viewed|Viewed) by'   # the full text, or the pill after 8 s (read from the console picture)
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
            # read until the banner has its text (run 20261021-062735 caught it half drawn), at most 20 s
            $readUntil = (Get-Date).AddSeconds(20)
            do { $read = Invoke-LabPerson -Name $VmName -Do read -Title 'RemoteHub'; if ("$($read.text)" -match '(being viewed|Viewed) by') { break }; Start-Sleep -Seconds 1 } while ((Get-Date) -lt $readUntil)
            Go 'viewer'
            $r = Receive-LabDesktop $job
            Set-LabAgentPolicy -Name $VmName -Clear
            Save 'viewer' @{ R = $r; Read = $read }
        }

        It 'D-VIEW-02 an account only in Rights\DesktopViewOnly: Hello control=false, forced view-only though Start asked for control' -Tag 'D-VIEW-02' {
            $r.hello.rights.control | Should -BeFalse
            (Of $r 'Connected')[0].message | Should -Match 'view only'
            @($r.layouts | Where-Object { $_.layout.viewOnly }).Count | Should -BeGreaterThan 0
            $read.text | Should -Match '(being viewed|Viewed) by'
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
            # the marker must have the keyboard focus for the key-down to be recorded: the consent dialog took the focus
            # (SetForegroundWindow) and when it closes Windows needn't give it back to the marker, another process - in
            # the run of 16.10.2026 the key reached Windows but no window. A click on the marker gives it the focus first.
            $job = Connect-Allowed 'held' -Before @(
                @{ do = 'sleep'; seconds = 2 }, @{ do = 'mouse'; action = 'Down'; button = 'Left'; x = 200; y = 200 }, @{ do = 'mouse'; action = 'Up'; button = 'Left'; x = 200; y = 200 }
                @{ do = 'sleep'; seconds = 1 }, @{ do = 'key'; scan = '0x1E' }, @{ do = 'sleep'; seconds = 2 }) -After @(
                @{ do = 'markFile'; path = (Join-Path $o 'held.txt') }, @{ do = 'waitFile'; path = (Join-Path $o 'uac.txt'); timeout = 120 }, @{ do = 'key'; scan = '0x1E'; flags = @('Up') }, @{ do = 'sleep'; seconds = 3 })
            Go 'held'
            Wait-LabHostFile (Join-Path $o 'held.txt')
            $task = Start-LabInSession -Name $VmName -ScriptBlock { Start-Process cmd.exe -Verb RunAs }
            $until = (Get-Date).AddSeconds(60)
            while (-not (Invoke-LabGuest -Name $VmName -ScriptBlock { [bool](Get-Process consent -ErrorAction SilentlyContinue) }) -and (Get-Date) -lt $until) { Start-Sleep 2 }
            Set-Content -Encoding ASCII (Join-Path $o 'uac.txt') 'up'
            $r = Receive-LabDesktop $job
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process consent -ErrorAction SilentlyContinue | Stop-Process -Force }
            Stop-LabInSession -Name $VmName -Task $task
            # the secure desktop goes with consent.exe: the key state is read on the user's desktop again (no condition
            # from here says which desktop has the input)
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
            @($events | Where-Object { $_.e -eq 'mdown' }).Count | Should -BeGreaterThan 0 -Because 'the click on the marker (for the focus) reached it'
            @($events | Where-Object { $_.e -eq 'down' -and $_.vk -eq 65 }).Count | Should -BeGreaterThan 0 -Because 'the key was held down in the session (precondition)'
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
            Set-Content -Encoding ASCII (Join-Path $o 'uac-up.txt') 'up'
            $uac = Receive-LabDesktop $job
            $stillOpen = Invoke-LabGuest -Name $VmName -ScriptBlock { $c = @(Get-Process consent -ErrorAction SilentlyContinue); $c | Stop-Process -Force -ErrorAction SilentlyContinue; $c.Count -gt 0 }
            Stop-LabInSession -Name $VmName -Task $task
            # locked: Start is refused
            $since = Get-LabGuestTime -Name $VmName
            Lock-LabSession -Name $VmName
            # locked: the lock screen (LogonUI) shows in the console session
            $null = Wait-LabCondition -NoThrow -TimeoutSec 20 -Because 'the console session did not show the lock screen' -Until {
                @(Get-LabSessions -Name $VmName | Where-Object { $_.Console -and $_.LogonUI }).Count -gt 0
            }
            $locked = Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out 'locked') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Failed', 'Connected', 'WaitingForUser'); timeout = 60 })
            # (Where-Object: a timeout hands back $null, which @() would count as one event)
            $events = @(Wait-LabCondition -NoThrow -TimeoutSec 15 -Because 'event 2100 was not written' -Until { @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2100) } | Where-Object { $_ })
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
