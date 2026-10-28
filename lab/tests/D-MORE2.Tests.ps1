# D-MORE2 - more cases of the agent's remote desktop: picture / fit / virtual display / clipboard / idle / resume /
# robustness.
# Covers: D-VID-11, D-VID-12, D-VID-13, D-FIT-04, D-VD-05, D-CLIP-19, D-CLIP-20, D-CLIP-21, D-CLIP-22, D-IDLE-04,
#         D-RES-08, D-RES-09, D-RES-10, D-ROB-07, D-ROB-08 (docs/test-lab-cases.md section 9).
# Needs: one VM as the device (like the other D-* files), the desktop harness on the host, the person at the device
#        (RhPerson.ps1) and network control (Disconnect-LabNetwork / Connect-LabNetwork) for D-RES-10; the virtual
#        display driver (Install-LabAgent -VirtualDisplay, the default) for D-VD-05. normaluser is signed in on the
#        console and allows PSAdmin.
# D-RES-10 keeps the network cut until the agent has ended the connection itself, then resumes at once: the token is
#        valid for a minute from that end, however long the agent took to notice.
# D-VID-12 can only be checked where Desktop Duplication stands still by itself (no hook to force it): skipped otherwise.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "d-more2-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "d-more2-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Of($r, [string]$state) { , @($r.statuses | Where-Object state -eq $state) }
    function Mark([string]$o, [string]$n) { @{ do = 'markFile'; path = (Join-Path $o "$n.txt") } }
    function Park([string]$o, [string]$n, [int]$t = 300) { @{ do = 'waitFile'; path = (Join-Path $o "$n.txt"); timeout = $t } }
    function Reached([string]$o, [string]$n, [int]$t = 180) { Wait-LabHostFile (Join-Path $o "$n.txt") -TimeoutSec $t }
    function Release([string]$o, [string]$n) { Set-Content -Encoding ASCII (Join-Path $o "$n.txt") 'go' }
    # the harness' clock (ms since its start) when it wrote a markFile
    function MarkMs([string]$o, [string]$n) { [long](Get-Content (Join-Path $o "$n.txt") -Raw).Trim() }
    # a session normaluser allows (the person clicks Allow); $Steps run once connected
    function Begin([string]$Name, [object[]]$Steps, [string]$Answer = 'Allow') {
        $job = Start-LabDesktop -Name $VmName -Pin $pin -Out (Out $Name) -Steps (@(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Denied', 'Failed'); timeout = 90 }) + $Steps)
        # the click is tried again when it missed or the person server failed (as D-CADHIDE's Allow; run
        # 20261021-222745: one lost click let the question run out - Denied)
        if ($Answer) {
            for ($i = 0; $i -lt 3; $i++) {
                $c = try { Invoke-LabPerson -Name $VmName -Do click -Text $Answer -TimeoutSec 40 } catch { $null }
                if ($c -and $c.clicked) { break }
                Start-Sleep -Seconds 2
            }
        }
        $job
    }
    function Get-Vdd { Invoke-LabGuest -Name $VmName -ScriptBlock { $d = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { @($_.HardwareID) -contains 'Root\MttVDD' } | Select-Object -First 1; if ($d) { [string]$d.Status } } }
    function Set-AgentJsonValue([string]$Key, $Value) { Set-LabAgentJsonValue -Name $VmName -Pin $pin -Key $Key -Value $Value }
    # pastes the clipboard into a folder of normaluser's, as Explorer's Paste does, until $Count files are there
    # (Invoke-LabPaste: it pumps messages while the shell fetches the offered files); answers the paste's result - files
    # (Path, Name, Length, Hash), the clipboard's formats before the paste and the thread's apartment
    function Paste-AtDevice([string]$Folder, [int]$Count = 1) {
        Invoke-LabPaste -Name $VmName -Account User -Folder $Folder -Count $Count -TimeoutSec 120
    }
    function Ends($r) { , @($r.transfers | Where-Object kind -eq 'end') }

    # files on the host
    $data = Out 'data'
    1..3 | ForEach-Object { [IO.File]::WriteAllText("$data\file$_.txt", "clipboard file $_ $([guid]::NewGuid())") }
    # "broken" BMPs: no "BM" file header, and a header with nothing after it
    [IO.File]::WriteAllBytes("$data\broken.bmp", [byte[]](@(0x58, 0x58) + @(0) * 62))
    [IO.File]::WriteAllBytes("$data\short.bmp", [byte[]](0x42, 0x4D, 1, 2, 3, 4, 5, 6, 7, 8))

    $init = Initialize-LabVm -Name $VmName -Agent -AutoLogon User
    $install = $init.Install
    $pin = $init.Pin
    $userSid = Invoke-LabGuest -Name $VmName -ArgumentList $vm.UserAccount -ScriptBlock { param($u) (Get-LocalUser -Name $u).SID.Value }
}

AfterAll {
    Get-Job | Where-Object State -eq 'Running' | Stop-Job
    Get-Job | Remove-Job -Force
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-more2-agent-logs')
    # the power plan (display off after 1 minute), agent.json, normaluser's RDP client value, the network cut: not for the next file
    Set-LabVmDirty -Name $VmName
}

Describe 'D-MORE2 <VmName>' -Tag Desktop, 'D-VID', 'D-FIT', 'D-VD', 'D-CLIP', 'D-IDLE', 'D-RES', 'D-ROB' {

    Context 'the picture' {
        BeforeAll {
            $v = [ordered]@{}
            # RDP windows keep drawing: normaluser's RemoteDesktop_SuppressWhenMinimized is 0 before, 2 while connected, 0 after
            $rdpRead = {
                Invoke-LabGuest -Name $VmName -ArgumentList $userSid -ScriptBlock {
                    param($sid)
                    $k = "Registry::HKEY_USERS\$sid\Software\Microsoft\Terminal Server Client"
                    $value = (Get-ItemProperty $k -ErrorAction SilentlyContinue).RemoteDesktop_SuppressWhenMinimized
                    $file = "C:\ProgramData\RemoteHub\Agent\rdp-minimized-$sid.txt"
                    [pscustomobject]@{ Value = $value; Record = $(if (Test-Path $file) { (Get-Content $file -Raw).Trim() } else { $null }) }
                }
            }
            Invoke-LabGuest -Name $VmName -ArgumentList $userSid -ScriptBlock {
                param($sid)
                $k = "Registry::HKEY_USERS\$sid\Software\Microsoft\Terminal Server Client"
                if (-not (Test-Path $k)) { $null = New-Item $k -Force }
                Set-ItemProperty $k -Name RemoteDesktop_SuppressWhenMinimized -Value 0 -Type DWord
            }
            $v.rdpBefore = & $rdpRead
            # a notepad that is typed into while connected (the screen changes - D-VID-12 watches the capture)
            $task = Start-LabInSession -Name $VmName -ScriptBlock { notepad.exe }
            Start-Sleep -Seconds 4
            $since = Get-LabGuestTime -Name $VmName
            $o = Out 'drawing'
            $job = Begin 'drawing' @(
                @{ do = 'sleep'; seconds = 12 }, (Mark $o 'on'), (Park $o 'read')
                @{ do = 'keys'; text = ('the screen changes while typing ' * 4); delayMs = 150 }, @{ do = 'sleep'; seconds = 5 }, @{ do = 'close' })
            Reached $o 'on'
            $v.rdpDuring = & $rdpRead
            Release $o 'read'
            $v.drawing = Receive-LabDesktop $job
            Start-Sleep -Seconds 5
            $v.rdpAfter = & $rdpRead
            $v.log = @(Get-LabAgentLog -Name $VmName -Since $since)
            Stop-LabInSession -Name $VmName -Task $task
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force }

            # the display kept on: power plan 1 minute, 100 s without input while connected
            $displayRequests = {
                Invoke-LabGuest -Name $VmName -ScriptBlock {
                    $section = ''
                    foreach ($l in @(powercfg.exe /requests)) {
                        if ($l -match '^([A-Z]+):\s*$') { $section = $Matches[1]; continue }
                        if ($section -eq 'DISPLAY' -and $l.Trim() -and $l.Trim() -ne 'None.') { $l.Trim() }
                    }
                }
            }
            Invoke-LabGuest -Name $VmName -ScriptBlock { powercfg.exe /change monitor-timeout-ac 1; powercfg.exe /change monitor-timeout-dc 1 }
            $o = Out 'display'
            $job = Begin 'display' @(@{ do = 'sleep'; seconds = 100 }, (Mark $o 'idle'), (Park $o 'checked'), @{ do = 'frame'; out = 'after-idle.png'; timeout = 5 }, @{ do = 'close' })
            Reached $o 'idle' 240
            $v.displayDuring = @(& $displayRequests)
            $v.shot = Get-LabScreenshot -Name $VmName -Path (Join-Path $o 'console.png')
            Release $o 'checked'
            $v.display = Receive-LabDesktop $job
            Start-Sleep -Seconds 5
            $v.displayAfter = @(& $displayRequests)
            Invoke-LabGuest -Name $VmName -ScriptBlock { powercfg.exe /change monitor-timeout-ac 0; powercfg.exe /change monitor-timeout-dc 0 }

            # fit bounds: 300x150 ignored, 10000x6000 capped at 7680x4320
            # (the console's resolution from Hyper-V's video head: a PowerShell Direct session sees its own 1024x768)
            $v.resBefore = Get-LabResolution -Name $VmName
            $o = Out 'fit-bounds'
            $job = Begin 'fit-bounds' @(
                @{ do = 'settings'; fitWidth = 300; fitHeight = 150 }, @{ do = 'sleep'; seconds = 8 }, (Mark $o 'small'), (Park $o 'small-read')
                @{ do = 'settings'; fitWidth = 10000; fitHeight = 6000 }, @{ do = 'sleep'; seconds = 10 }, (Mark $o 'large'), (Park $o 'large-read'), @{ do = 'close' })
            Reached $o 'small'
            $v.resSmall = Get-LabResolution -Name $VmName
            Release $o 'small-read'
            # recorded only: the largest mode the display offers may be the one it was at
            Reached $o 'large'
            $v.resLarge = Get-LabResolution -Name $VmName
            Release $o 'large-read'
            $v.fit = Receive-LabDesktop $job
            $v.smallMs = MarkMs $o 'small'
            $v.resAfter = Wait-LabResolution -Name $VmName -Is $v.resBefore -TimeoutSec 20 -NoThrow
            Save 'picture' $v
        }

        It 'D-VID-11 while connected RemoteDesktop_SuppressWhenMinimized = 2 (rdp-minimized-[sid].txt keeps the old value), put back afterwards; a log line' -Tag 'D-VID-11' {
            # the value is set by the helper and put back when it ends: a refused connection (consent not clicked in time)
            # has it back at 0 before it is read
            (Of $v.drawing 'Connected').Count | Should -BeGreaterThan 0 -Because "the person allowed the connection (statuses: $((@($v.drawing.statuses) | ForEach-Object state) -join ', '))"
            $v.rdpBefore.Value | Should -Be 0
            $v.rdpDuring.Value | Should -Be 2
            $v.rdpDuring.Record | Should -Be '0'
            $v.rdpAfter.Value | Should -Be 0
            $v.rdpAfter.Record | Should -BeNullOrEmpty
            @($v.log | Where-Object { $_ -match 'RDP windows in this session keep drawing when minimized' }).Count | Should -BeGreaterThan 0
        }

        It 'D-VID-12 Desktop Duplication standing still while the screen changes: GDI for the rest of the connection, log "... using GDI for this connection"' -Tag 'D-VID-12' {
            $line = @($v.log | Where-Object { $_ -match 'stood still while the screen changed . using GDI for this connection' })
            if ($line.Count -eq 0) {
                $methods = (@($v.drawing.stats | ForEach-Object { $_.stats.capture }) | Sort-Object -Unique) -join ', '
                Set-ItResult -Skipped -Because "Desktop Duplication didn't stand still on this VM - forcing it needs a test hook in ScreenCapture.StoodStill (capture seen: $methods)"
                return
            }
            # from the first GDI picture on, GDI only
            $caps = @($v.drawing.stats | ForEach-Object { $_.stats.capture })
            $first = [array]::IndexOf($caps, ($caps | Where-Object { $_ -match 'GDI' } | Select-Object -First 1))
            $first | Should -BeGreaterOrEqual 0
            @($caps[$first..($caps.Count - 1)] | Where-Object { $_ -notmatch 'GDI' }) | Should -BeNullOrEmpty
        }

        It 'D-VID-13 the display doesn''t turn off while connected (power plan 1 min, 100 s without input)' -Tag 'D-VID-13' {
            ($v.displayDuring -join ' ') | Should -Match 'RemoteHubAgent' -Because 'the helper asks for the display (ES_DISPLAY_REQUIRED)'
            $v.shot.IsBlank | Should -BeFalse
            $f = @($v.display.steps | Where-Object do -eq 'frame')[0]
            $f.ok | Should -BeTrue
            @($v.display.images)[0].meanLuma | Should -BeGreaterThan 5
            ($v.displayAfter -join ' ') | Should -Not -Match 'RemoteHubAgent' -Because 'the request ends with the connection'
        }

        It 'D-FIT-04 fit below 320x200 is ignored, above 7680x4320 capped' -Tag 'D-FIT-04' {
            $v.resSmall | Should -Be $v.resBefore
            @($v.fit.stats | Where-Object { $_.ms -le $v.smallMs -and $_.stats.fit }) | Should -BeNullOrEmpty -Because '300x150 never reached the display'
            @($v.fit.stats | Where-Object { "$($_.stats.fit)" -match '^300.150' }) | Should -BeNullOrEmpty
            @($v.fit.stats | Where-Object { "$($_.stats.fit)" -match '^7680.4320 ' }).Count | Should -BeGreaterThan 0
            @($v.fit.stats | Where-Object { "$($_.stats.fit)" -match '10000' }) | Should -BeNullOrEmpty
            $v.resAfter | Should -Be $v.resBefore
        }
    }

    Context 'the virtual display after a service restart' {
        BeforeAll {
            $d = [ordered]@{}
            Set-AgentJsonValue 'virtualDisplay' 'always'
            # diagnosis: the device node as the agent finds it (pnputil 1167 "not connected" seen when switching it on)
            $d.device = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $dev = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { @($_.HardwareID) -contains 'Root\MttVDD' } | Select-Object -First 1
                [ordered]@{
                    status  = if ($dev) { [string]$dev.Status } else { $null }
                    present = if ($dev) { $dev.Present } else { $null }
                    problem = if ($dev) { (Get-PnpDeviceProperty -InstanceId $dev.InstanceId -KeyName DEVPKEY_Device_ProblemCode -ErrorAction SilentlyContinue).Data } else { $null }
                    pnputil = (pnputil.exe /enum-devices /class Display 2>&1 | Out-String)
                }
            }
            $o = Out 'vd-restart'
            $since = Get-LabGuestTime -Name $VmName
            # "always" switches the virtual monitor on once a Fit size is sent
            $job = Begin 'vd-restart' @(@{ do = 'settings'; fitWidth = 1280; fitHeight = 720 }, @{ do = 'sleep'; seconds = 10 }, (Mark $o 'on'), (Park $o 'end'), @{ do = 'sleep'; seconds = 1 })
            Reached $o 'on'
            $d.on = Get-Vdd
            # the agent's own word on switching it on ("virtual monitor on at ..." or "... not switched on: <why>")
            $d.onLog = @(Get-LabAgentLog -Name $VmName -Since $since | ForEach-Object { "$_" } | Where-Object { $_ -match 'virtual monitor' })
            # the service killed (nothing switched off on the way out), then started again
            Invoke-LabGuest -Name $VmName -ScriptBlock { Stop-Process -Id (Get-CimInstance Win32_Service -Filter "Name='RemoteHubAgent'").ProcessId -Force }
            Invoke-LabGuest -Name $VmName -ScriptBlock {
                Start-Sleep -Seconds 5
                if ((Get-Service RemoteHubAgent).Status -ne 'Running') { try { Start-Service RemoteHubAgent -ErrorAction Stop } catch { } }
            }
            $null = Wait-LabAgent -Name $VmName -Pin $pin
            Start-Sleep -Seconds 10
            $d.afterRestart = Get-Vdd
            Release $o 'end'
            $d.r = Receive-LabDesktop $job
            Set-AgentJsonValue 'virtualDisplay' 'auto'
            Save 'vd-restart' $d
        }

        It 'D-VD-05 the virtual display is switched off when the service starts again' -Tag 'D-VD-05' {
            $d.on | Should -Be 'OK' -Because "the virtual monitor was on while connected (agent log: $(@($d.onLog) -join ' / '))"
            $d.afterRestart | Should -Not -Be 'OK'
        }
    }

    Context 'clipboard' {
        BeforeAll {
            $c = [ordered]@{}
            $null = Invoke-LabPerson -Name $VmName -Do clip-set -Value 'before the broken image'
            $o = Out 'clip'
            $job = Begin 'clip' @(
                # two copies offered one after the other: the second replaces the first
                @{ do = 'files'; paths = @("$data\file1.txt", "$data\file2.txt"); offer = $true; waitRequest = 0 }, @{ do = 'sleep'; seconds = 3 }
                @{ do = 'files'; paths = @("$data\file3.txt"); offer = $true; waitRequest = 0 }, @{ do = 'sleep'; seconds = 5 }, (Mark $o 'offered'), (Park $o 'pasted')
                @{ do = 'sleep'; seconds = 3 }, (Mark $o 'long'), (Park $o 'longcopied'), @{ do = 'sleep'; seconds = 10 }
                (Mark $o 'many'), (Park $o 'manycopied'), @{ do = 'sleep'; seconds = 15 }
                # broken images to the device, sent at once (not offered)
                (Mark $o 'images'), (Park $o 'clipset'), @{ do = 'clipboardImage'; path = "$data\broken.bmp"; offer = $false; waitReceipt = 20 }
                @{ do = 'clipboardImage'; path = "$data\short.bmp"; offer = $false; waitReceipt = 20 }, @{ do = 'sleep'; seconds = 3 }, (Mark $o 'imagessent'), (Park $o 'end'), @{ do = 'close' })
            Reached $o 'offered'
            $c.paste = Paste-AtDevice "C:\Users\$($vm.UserAccount)\Desktop\Newer"
            $c.pasted = @($c.paste.files)
            Release $o 'pasted'
            Reached $o 'long'
            # 1,200,000 characters copied at the device
            $null = Invoke-LabInSession -Name $VmName -ScriptBlock { Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.Clipboard]::SetText(('0123456789' * 120000)) }
            Release $o 'longcopied'
            Reached $o 'many'
            # 1001 single files copied at the device
            $null = Invoke-LabInSession -Name $VmName -TimeoutSec 240 -ScriptBlock {
                $d = 'C:\ProgramData\RemoteHubLab\many1001'; $null = New-Item -ItemType Directory -Path $d -Force
                1..1001 | ForEach-Object { [IO.File]::WriteAllText("$d\f$_.txt", "$_") }
                Set-Clipboard -Path @(Get-ChildItem $d -File | ForEach-Object FullName)
            }
            Release $o 'manycopied'
            Reached $o 'images'
            $null = Invoke-LabPerson -Name $VmName -Do clip-set -Value 'before the broken image'
            Release $o 'clipset'
            Reached $o 'imagessent'
            $c.afterImages = Invoke-LabPerson -Name $VmName -Do clip-get
            Release $o 'end'
            $c.r = Receive-LabDesktop $job
            $c.ms = [ordered]@{ long = (MarkMs $o 'long'); many = (MarkMs $o 'many'); images = (MarkMs $o 'images') }
            Save 'clip' $c
        }

        It 'D-CLIP-19 a second copy offered while the first is still offered replaces it silently: the paste gets the second' -Tag 'D-CLIP-19' {
            $ids = @($c.r.steps | Where-Object do -eq 'files' | ForEach-Object { $_.detail.id })
            $first = $ids[0]; $second = $ids[1]
            @($c.pasted | ForEach-Object { Split-Path $_.Path -Leaf }) | Should -Be @('file3.txt') -Because "normaluser's clipboard offered: $(@($c.paste.formats) -join ', '); $($c.paste.apartment) thread"
            @($c.r.transfers | Where-Object { $_.kind -eq 'request' -and $_.fromDevice } | ForEach-Object { [int]$_.id }) | Should -Not -Contain $first
            @($c.r.transfers | Where-Object { $_.kind -eq 'request' -and $_.fromDevice } | ForEach-Object { [int]$_.id }) | Should -Contain $second
            # silently: no end of the first, no message
            # ((Ends ...)): Ends hands its list back as one object - the parentheses unroll it for Where-Object
            @((Ends $c.r) | Where-Object { $_.id -eq $first }) | Should -BeNullOrEmpty
            @($c.r.statuses | Where-Object { $_.ms -lt $c.ms.long -and $_.message -match 'replaced|copy' }) | Should -BeNullOrEmpty
        }

        It 'D-CLIP-20 text over 1,000,000 characters copied at the device: truncated to 1,000,000, no message' -Tag 'D-CLIP-20' {
            $long = @($c.r.clipboard | Where-Object { $_.ms -gt $c.ms.long -and $_.ms -lt $c.ms.many })
            $long.Count | Should -Be 1
            $long[0].length | Should -Be 1000000
            $long[0].head | Should -BeExactly ('0123456789' * 10)
            @($c.r.statuses | Where-Object { $_.ms -gt $c.ms.long -and $_.ms -lt $c.ms.many }) | Should -BeNullOrEmpty
        }

        It 'D-CLIP-21 1001 single files copied at the device: 1000 offered, no message' -Tag 'D-CLIP-21' {
            $offer = @($c.r.transfers | Where-Object { $_.kind -eq 'offer' -and $_.fromDevice -and $_.ms -gt $c.ms.many -and $_.ms -lt $c.ms.images })
            $offer.Count | Should -Be 1
            @($offer[0].transfer.items).Count | Should -Be 1000
            @((Ends $c.r) | Where-Object { $_.ms -gt $c.ms.many -and $_.ms -lt $c.ms.images -and -not $_.ok }) | Should -BeNullOrEmpty
            @($c.r.statuses | Where-Object { $_.ms -gt $c.ms.many -and $_.ms -lt $c.ms.images }) | Should -BeNullOrEmpty
        }

        It 'D-CLIP-22 a broken BMP to the device: the receipt is ok, the device''s clipboard unchanged' -Tag 'D-CLIP-22' {
            $img = @($c.r.steps | Where-Object do -eq 'clipboardImage')
            $img.Count | Should -Be 2
            foreach ($s in $img) {
                $s.ok | Should -BeTrue
                $s.detail.receipt.ok | Should -BeTrue
            }
            $c.afterImages.start | Should -Be 'before the broken image'
            @($c.afterImages.formats) | Should -Not -Contain 'DeviceIndependentBitmap'
        }
    }

    Context 'idle timeout and the clipboard' {
        BeforeAll {
            $i = [ordered]@{}
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ DesktopIdleTimeoutMinutes = 1 }
            # control: nothing sent - ends after a minute
            $i.control = Receive-LabDesktop (Begin 'idle-control' @(@{ do = 'wait'; state = 'Ended'; timeout = 120 }))
            # text to the device every 20 s, for 115 s
            $clip = foreach ($k in 1..5) { @{ do = 'sleep'; seconds = 20 }, @{ do = 'clipboardText'; text = "idle clip $k" } }
            $i.clip = Receive-LabDesktop (Begin 'idle-clip' (@($clip | ForEach-Object { $_ }) + @(@{ do = 'sleep'; seconds = 15 }, @{ do = 'close' })))
            # a file to the device every 20 s (sent at once: TransferStart)
            $files = foreach ($k in 1..5) { @{ do = 'sleep'; seconds = 20 }, @{ do = 'files'; paths = @("$data\file1.txt"); offer = $false; waitReceipt = 10 } }
            $i.files = Receive-LabDesktop (Begin 'idle-files' (@($files | ForEach-Object { $_ }) + @(@{ do = 'sleep'; seconds = 15 }, @{ do = 'close' })))
            Set-LabAgentPolicy -Name $VmName -Clear
            Save 'idle' $i
        }

        It 'D-IDLE-04 clipboard text and file copies reset the idle time' -Tag 'D-IDLE-04' {
            (Of $i.control 'Ended').Count | Should -Be 1 -Because 'without anything sent the session ends after a minute (control)'
            (Of $i.clip 'Ended').Count | Should -Be 0
            (Of $i.files 'Ended').Count | Should -Be 0
            @($i.clip.steps | Where-Object do -eq 'clipboardText').Count | Should -Be 5
            @($i.files.steps | Where-Object { $_.do -eq 'files' -and $_.ok }).Count | Should -Be 5
        }
    }

    Context 'resume tokens' {
        BeforeAll {
            $r = [ordered]@{}
            # a protocol error (Settings that aren't JSON) ends the session and its token
            $since = Get-LabGuestTime -Name $VmName
            $o = Out 'res-invalid'
            $job = Begin 'res-invalid' @(
                @{ do = 'sleep'; seconds = 2 }, @{ do = 'rawJson'; type = 6; json = '{"monitor": "x", ' }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'reconnect' }
                @{ do = 'start'; session = 'console'; consent = 'auto'; resume = 'last' }, @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'Failed'); timeout = 60 }
                (Mark $o 'asked'), @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 60 }, @{ do = 'close' })
            Reached $o 'asked'
            try { $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Deny' -TimeoutSec 30 } catch { }
            $r.invalid = Receive-LabDesktop $job
            Start-Sleep -Seconds 2
            $r.invalidEvents = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1101)

            # a service restart: the tokens are gone with it
            $o = Out 'res-restart'
            $job = Begin 'res-restart' @(
                (Mark $o 'connected'), (Park $o 'restarted'), @{ do = 'reconnect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; resume = 'last' }
                @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'Failed'); timeout = 60 }, (Mark $o 'asked'), @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 60 }, @{ do = 'close' })
            Reached $o 'connected'
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            Release $o 'restarted'
            Reached $o 'asked'
            try { $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Deny' -TimeoutSec 30 } catch { }
            $r.restart = Receive-LabDesktop $job
            Save 'resume' $r
        }

        It 'D-RES-08 an invalid message ends the session and voids its resume token' -Tag 'D-RES-08' {
            @($r.invalid.closes | Where-Object { $_.connection -eq 1 -and $_.by -eq 'agent' }).Count | Should -Be 1
            @($r.invalidEvents | Where-Object { $_.Message -match '\(invalid message from RemoteHub: ' }).Count | Should -BeGreaterThan 0
            @($r.invalid.statuses | Where-Object { $_.connection -eq 2 -and $_.state -eq 'WaitingForUser' }).Count | Should -BeGreaterThan 0
        }

        It 'D-RES-09 a resume after a service restart: asked again' -Tag 'D-RES-09' {
            @($r.restart.statuses | Where-Object { $_.connection -eq 1 -and $_.state -eq 'Connected' }).Count | Should -BeGreaterThan 0
            @($r.restart.statuses | Where-Object { $_.connection -eq 2 -and $_.state -eq 'WaitingForUser' }).Count | Should -BeGreaterThan 0
        }
    }

    Context 'resume after a drop the agent noticed first' {
        BeforeAll {
            $r = [ordered]@{}
            # the agent notices the drop first: the network stays cut until the agent has ended the connection itself (its
            # keep-alive gives up after 30-60 s, TCP sometimes sooner), then RemoteHub resumes at once. The token is valid
            # for a minute from that end - a fixed cut (75 s, lab run 20261017-160133) resumed 61.5 s after an end the
            # agent noticed 20 s into the cut, so the token had expired and the test failed on its own timing.
            $since = Get-LabGuestTime -Name $VmName
            $o = Out 'res-long-cut'
            $job = Begin 'res-long-cut' @(
                (Mark $o 'connected'), (Park $o 'restored'), @{ do = 'drop' }, @{ do = 'reconnect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; resume = 'last' }
                @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'Failed'); timeout = 60 }, (Mark $o 'second'), @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 60 }
                @{ do = 'sleep'; seconds = 3 }, @{ do = 'close' })
            Reached $o 'connected'
            Start-Sleep -Seconds 3
            $cutAt = Get-LabGuestTime -Name $VmName
            $lost = @()
            $cut = Disconnect-LabNetwork -Name $VmName
            try {
                # PowerShell Direct still reaches the guest while its network is cut
                $until = (Get-Date).AddSeconds(120)
                do {
                    $lost = @(Get-LabAgentEvent -Name $VmName -Since $cutAt -Id 1101 | Where-Object { $_.Message -match '\(connection lost' })
                    if ($lost.Count -gt 0) { break }
                    Start-Sleep -Seconds 2
                } while ((Get-Date) -lt $until)
            }
            finally { Connect-LabNetwork -Name $VmName -Switches $cut }
            if ($lost.Count -eq 0) { throw 'D-RES-10: the agent did not end the connection as "connection lost" within 120 s of the network cut' }
            $r.noticedAfterSec = [math]::Round(([datetime]$lost[0].Time - $cutAt).TotalSeconds)
            Release $o 'restored'
            Reached $o 'second'
            $r.resumedAfterEndSec = [math]::Round(((Get-LabGuestTime -Name $VmName) - [datetime]$lost[0].Time).TotalSeconds)
            Start-Sleep -Seconds 3
            # asked again (the token was revoked or had expired): deny so the run goes on
            try { $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Deny' -TimeoutSec 10 } catch { }
            $r.cut = Receive-LabDesktop $job
            Start-Sleep -Seconds 2
            $r.cutEvents = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1101, 1103)
            Save 'resume-long-cut' $r
        }

        # Before 0.18.16 a drop the agent noticed first ended "cancelled", was taken as "closed by RemoteHub" and the token
        # was revoked (DesktopEndpoint.cs, the main loop's fromClient check).
        It 'D-RES-10 the agent notices the drop first, resumed within the minute: 1101 "(connection lost ...)", 1103, no new question' -Tag 'D-RES-10' {
            $r.resumedAfterEndSec | Should -BeLessThan 50 -Because 'the resume must come within the token''s minute for this test to say anything'
            @($r.cut.statuses | Where-Object { $_.connection -eq 1 -and $_.state -eq 'Connected' }).Count | Should -BeGreaterThan 0
            @($r.cutEvents | Where-Object { $_.Id -eq 1101 -and $_.Message -match '\(connection lost' }).Count | Should -BeGreaterThan 0
            # the dropped connection's end only: the resumed one ("continued after a dropped connection") is closed by
            # RemoteHub at the end of the test, as it should be
            @($r.cutEvents | Where-Object { $_.Id -eq 1101 -and $_.Message -match '\(closed by RemoteHub\)' -and $_.Message -notmatch 'continued after a dropped connection' }) | Should -BeNullOrEmpty
            @($r.cut.statuses | Where-Object { $_.connection -eq 2 -and $_.state -eq 'WaitingForUser' }) | Should -BeNullOrEmpty
            @($r.cut.statuses | Where-Object { $_.connection -eq 2 -and $_.state -eq 'Connected' }).Count | Should -BeGreaterThan 0
            @($r.cutEvents | Where-Object Id -eq 1103).Count | Should -BeGreaterThan 0
        }
    }

    Context 'invalid messages' {
        BeforeAll {
            $m = [ordered]@{}
            $since = Get-LabGuestTime -Name $VmName
            # a sign-in request nobody asked for (no automatic sign-in in this connection)
            $m.unasked = Receive-LabDesktop (Begin 'rob-signin-unasked' @(@{ do = 'sleep'; seconds = 2 }, @{ do = 'signIn' }, @{ do = 'sleep'; seconds = 4 }))
            # a sign-in request larger than any password (3-byte header + 256 UTF-16 characters)
            $m.large = Receive-LabDesktop (Begin 'rob-signin-large' @(@{ do = 'sleep'; seconds = 2 }, @{ do = 'raw'; hex = ('1F' + ('AA' * 700)) }, @{ do = 'sleep'; seconds = 4 }))
            # a message over 16 MiB (clipboard text of 17,000,000 ASCII characters)
            $m.huge = Receive-LabDesktop (Begin 'rob-huge' @(@{ do = 'sleep'; seconds = 2 }, @{ do = 'clipboardText'; lengthChars = 17000000 }, @{ do = 'sleep'; seconds = 8 }))
            Start-Sleep -Seconds 2
            $m.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1101)
            $m.health = Get-LabAgentHealth -Name $VmName -Pin $pin
            Save 'invalid' $m
        }

        It 'D-ROB-07 an unrequested sign-in request: "invalid message from RemoteHub: A sign-in request the agent didn''t ask for." / "Sign-in request too large."' -Tag 'D-ROB-07' {
            @($m.unasked.closes | Where-Object by -eq 'agent').Count | Should -BeGreaterThan 0
            @($m.large.closes | Where-Object by -eq 'agent').Count | Should -BeGreaterThan 0
            @($m.events | Where-Object { $_.Message -match "\(invalid message from RemoteHub: A sign-in request the agent didn't ask for\.\)" }).Count | Should -BeGreaterThan 0
            @($m.events | Where-Object { $_.Message -match '\(invalid message from RemoteHub: Sign-in request too large\.\)' }).Count | Should -BeGreaterThan 0
        }

        It 'D-ROB-08 a message over 16 MiB: 1101 "(invalid message from RemoteHub: Message too large.)"' -Tag 'D-ROB-08' {
            @($m.huge.closes | Where-Object by -eq 'agent').Count | Should -BeGreaterThan 0
            @($m.events | Where-Object { $_.Message -match '\(invalid message from RemoteHub: Message too large\.\)' }).Count | Should -BeGreaterThan 0
            $m.health | Should -Not -BeNullOrEmpty -Because 'the agent is still healthy'
        }
    }
}
