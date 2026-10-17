# D-PRIVACY2 - more privacy cases of the agent's remote desktop: SnipGuard (snip keys and window switchers dropped,
# snipping tools closed while the privacy screen is up), view-only, monitors changing, apps hidden by policy.
# Covers: D-PRIV-05, D-PRIV-06, D-PRIV-07, D-PRIV-08, D-HIDE-03 (docs/test-lab-cases.md section 9.11a).
# Needs: one VM as the device, Windows 10 2004 or later (no privacy screen before: the cases skip on 1809); the desktop
#        harness on the host; the person at the device (RhPerson.ps1), the marker app and the console keyboard
#        (Send-LabKeyCombo). normaluser is signed in on the console and allows PSAdmin.
# Known product bug: D-HIDE-03 - apps hidden by the policy HiddenApps are counted as hidden by the user (first Privacy
#        message hiddenApps=1, banner "1 app hidden", 1105 "the user ... hides 1 app(s)"; DesktopHelper.cs:177-179,
#        DesktopEndpoint.cs:762-763). Tested for the correct behaviour: fails until fixed.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "d-priv2-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "d-priv2-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Mark([string]$o, [string]$n) { @{ do = 'markFile'; path = (Join-Path $o "$n.txt") } }
    function Park([string]$o, [string]$n, [int]$t = 300) { @{ do = 'waitFile'; path = (Join-Path $o "$n.txt"); timeout = $t } }
    function Reached([string]$o, [string]$n, [int]$t = 180) { Wait-LabHostFile (Join-Path $o "$n.txt") -TimeoutSec $t }
    function Release([string]$o, [string]$n) { Set-Content (Join-Path $o "$n.txt") 'go' }
    # a session normaluser allows (the person clicks Allow); $Steps run once connected
    function Begin([string]$Name, [object[]]$Steps, [bool]$ViewOnly = $false) {
        $job = Start-LabDesktop -Name $VmName -Pin $pin -Out (Out $Name) -Steps (@(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; viewOnly = $ViewOnly }, @{ do = 'wait'; anyOf = @('Connected', 'Denied', 'Failed'); timeout = 90 }) + $Steps + @(@{ do = 'close' }))
        $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
        $job
    }
    function Console-Luma([string]$Path) {
        $null = Get-LabScreenshot -Name $VmName -Path $Path
        Add-Type -AssemblyName System.Drawing
        $b = [Drawing.Bitmap]::FromFile($Path); $sum = 0; $n = 0
        try { for ($y = 0; $y -lt $b.Height; $y += 8) { for ($x = 0; $x -lt $b.Width; $x += 8) { $c = $b.GetPixel($x, $y); $sum += 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B; $n++ } } } finally { $b.Dispose() }
        $sum / $n
    }
    # the console's resolution as Hyper-V's video head has it (what the device's monitor shows, whatever session asks)
    function Resolution {
        $heads = @(Get-CimAssociatedInstance -InputObject (Get-LabCimVm $VmName) -ResultClassName Msvm_VideoHead)
        $head = @($heads | Where-Object EnabledState -eq 2) + $heads | Select-Object -First 1
        "$(@($head.CurrentHorizontalResolution)[0])x$(@($head.CurrentVerticalResolution)[0])"
    }
    # the foreground window in normaluser's session: title, process and window class
    function Foreground {
        $code = @'
Add-Type -Namespace RhLab -Name Fg -MemberDefinition @"
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
"@
$h = [RhLab.Fg]::GetForegroundWindow()
$t = New-Object System.Text.StringBuilder 256; $null = [RhLab.Fg]::GetWindowText($h, $t, 256)
$c = New-Object System.Text.StringBuilder 256; $null = [RhLab.Fg]::GetClassName($h, $c, 256)
$p = [uint32]0; $null = [RhLab.Fg]::GetWindowThreadProcessId($h, [ref]$p)
$name = (Get-Process -Id $p -ErrorAction SilentlyContinue).ProcessName
"$($t.ToString())|$name|$($c.ToString())"
'@
        $line = "$(Invoke-LabInSession -Name $VmName -ScriptBlock ([scriptblock]::Create($code)))".Trim()
        $title, $process, $class = $line -split '\|', 3
        [pscustomobject]@{ Title = $title; Process = $process; Class = $class }
    }
    # the window classes of Alt+Tab and Task View (AppWindows.ShellClasses)
    $switchers = 'XamlExplorerHostIslandWindow', 'MultitaskingViewFrame', 'TaskSwitcherWnd', 'TaskSwitcherOverlayWnd', 'ForegroundStaging'
    function Snippers { Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-Process SnippingTool, ScreenClippingHost, ScreenSketch -ErrorAction SilentlyContinue | ForEach-Object ProcessName) } }
    function SnipLog([datetime]$Since) { @(Get-LabAgentLog -Name $VmName -Since $Since | Where-Object { $_ -match 'Privacy screen: .* (was closed|couldn''t be closed)' }) }

    $init = Initialize-LabVm -Name $VmName -Agent -AutoLogon User
    $install = $init.Install
    $pin = $init.Pin
    # the console's resolution at the start: the context 'monitors' changes it through Fit, the agent puts it back when
    # the connection ends - AfterAll checks that
    $startResolution = Resolution
    $old = $vm.Version -eq '1809'
    $markerTitle = 'RemoteHub lab marker'
}

AfterAll {
    Get-Job | Where-Object State -eq 'Running' | Stop-Job
    Get-Job | Remove-Job -Force
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-priv2-agent-logs')
    # left reusable: the snipping tools and the notepad closed, Search / a window switcher still open closed with Esc,
    # the policies cleared. The resolution Fit changed is put back by the agent at the end of the connection; if it
    # isn't (a bug, or a connection still open), the VM isn't reused.
    $null = Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process SnippingTool, ScreenClippingHost, ScreenSketch, notepad -ErrorAction SilentlyContinue | Stop-Process -Force }
    $null = Invoke-LabPerson -Name $VmName -Do key -Value '1B'
    Set-LabAgentPolicy -Name $VmName -Clear
    if ((Resolution) -ne $startResolution) { Set-LabVmDirty -Name $VmName }
}

Describe 'D-PRIVACY2 <VmName>' -Tag Desktop, 'D-PRIV', 'D-HIDE' {

    Context 'snips and window switchers while the privacy screen is up' {
        BeforeAll {
            $p = [ordered]@{}
            if (-not $old) {
                # two windows in normaluser's session: a notepad, and the marker (topmost, in front) that records keys
                $notepad = Start-LabInSession -Name $VmName -ScriptBlock { notepad.exe }
                Start-Sleep -Seconds 3
                $marker = Start-LabMarker -Name $VmName
                # the notepad minimized (still in Alt+Tab's list): else it stays in front of the marker and gets the keys
                $null = Invoke-LabInSession -Name $VmName -ScriptBlock {
                    Add-Type -Namespace RhLab -Name Win -MemberDefinition '[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int c);'
                    Get-Process notepad -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | ForEach-Object { [void][RhLab.Win]::ShowWindow($_.MainWindowHandle, 6) }
                }
                Start-Sleep -Seconds 1
                $null = Invoke-LabPerson -Name $VmName -Do clip-set -Value 'before the snips'
                $o = Out 'snip'
                # the device's mouse and keyboard stay usable (blockLocalInput false): the device's own keys are tested too
                $job = Begin 'snip' @(
                    @{ do = 'privacy'; style = 'Black'; blockLocalInput = $false }, @{ do = 'sleep'; seconds = 4 }, (Mark $o 'on'), (Park $o 'admin')
                    # from the admin: Shift+Tab (works), Alt+Tab, Print Screen, Win+Tab, Win+Shift+S (dropped)
                    @{ do = 'combo'; scans = @('0x2A', '0x0F') }, @{ do = 'sleep'; seconds = 2 }
                    @{ do = 'combo'; scans = @('0x38', '0x0F') }, @{ do = 'sleep'; seconds = 2 }, (Mark $o 'a-alttab'), (Park $o 'a-alttab-read')
                    @{ do = 'combo'; scans = @('0xE037') }, @{ do = 'sleep'; seconds = 2 }
                    @{ do = 'combo'; scans = @('0xE05B', '0x0F') }, @{ do = 'sleep'; seconds = 3 }, (Mark $o 'a-wintab'), (Park $o 'a-wintab-read')
                    @{ do = 'key'; scan = '0x01' }, @{ do = 'key'; scan = '0x01'; flags = @('Up') }, @{ do = 'sleep'; seconds = 1 }
                    @{ do = 'combo'; scans = @('0xE05B', '0x2A', '0x1F') }, @{ do = 'sleep'; seconds = 4 }, (Mark $o 'a-snip'), (Park $o 'a-snip-read')
                    @{ do = 'key'; scan = '0x01' }, @{ do = 'key'; scan = '0x01'; flags = @('Up') }, @{ do = 'sleep'; seconds = 1 }
                    (Mark $o 'admin-done'), (Park $o 'off'), @{ do = 'privacy'; style = 'Off' }, @{ do = 'sleep'; seconds = 3 }, (Mark $o 'is-off'), (Park $o 'end'))
                Reached $o 'on'
                Start-Sleep -Seconds 2
                $p.consoleLuma = Console-Luma (Join-Path $o 'console-on.png')

                # at the device (console keyboard)
                $since = Get-LabGuestTime -Name $VmName
                # the marker in front with the keyboard (a click at the device; the overlay lets clicks through)
                Send-LabMouse -Name $VmName -X 200 -Y 400
                Start-Sleep -Seconds 1
                $count = @(Get-LabMarker -Name $VmName -Marker $marker).Count
                $p.dStart = Foreground
                Send-LabKeyCombo -Name $VmName -Codes 0x10, 0x09            # Shift+Tab: works
                Start-Sleep -Seconds 2
                Send-LabKeyCombo -Name $VmName -Codes 0x12, 0x09            # Alt+Tab
                Start-Sleep -Seconds 2
                $p.dAltTab = Foreground
                Send-LabKeyCombo -Name $VmName -Codes 0x2C                  # Print Screen
                Start-Sleep -Seconds 2
                $p.dPrint = Invoke-LabPerson -Name $VmName -Do clip-get
                Send-LabKeyCombo -Name $VmName -Codes 0x5B, 0x09            # Win+Tab
                Start-Sleep -Seconds 3
                $p.dWinTab = Foreground
                $null = Invoke-LabPerson -Name $VmName -Do key -Value '1B'
                Start-Sleep -Seconds 1
                Send-LabKeyCombo -Name $VmName -Codes 0x5B, 0x10, 0x53      # Win+Shift+S
                Start-Sleep -Seconds 4
                $p.dSnippers = Snippers
                $null = Invoke-LabPerson -Name $VmName -Do key -Value '1B'
                Start-Sleep -Seconds 1
                $p.dSeen = @(Get-LabMarker -Name $VmName -Marker $marker | Select-Object -Skip $count)
                $p.dSnipLog = SnipLog $since

                # from the admin (the harness)
                $since = Get-LabGuestTime -Name $VmName
                Send-LabMouse -Name $VmName -X 200 -Y 400
                Start-Sleep -Seconds 1
                $count = @(Get-LabMarker -Name $VmName -Marker $marker).Count
                $p.aStart = Foreground
                Release $o 'admin'
                Reached $o 'a-alttab'
                $p.aAltTab = Foreground
                Release $o 'a-alttab-read'
                Reached $o 'a-wintab'
                $p.aWinTab = Foreground
                Release $o 'a-wintab-read'
                Reached $o 'a-snip'
                $p.aSnippers = Snippers
                Release $o 'a-snip-read'
                Reached $o 'admin-done'
                $p.aPrint = Invoke-LabPerson -Name $VmName -Do clip-get
                $p.aSeen = @(Get-LabMarker -Name $VmName -Marker $marker | Select-Object -Skip $count)
                $p.aSnipLog = SnipLog $since

                # Win+S at the device: works (Search comes up)
                $p.sBefore = Foreground
                Send-LabKeyCombo -Name $VmName -Codes 0x5B, 0x53
                Start-Sleep -Seconds 4
                $p.sAfter = Foreground
                $null = Invoke-LabPerson -Name $VmName -Do key -Value '1B'
                Start-Sleep -Seconds 2

                # privacy screen off: Alt+Tab switches again
                Release $o 'off'
                Reached $o 'is-off'
                Send-LabMouse -Name $VmName -X 200 -Y 400
                Start-Sleep -Seconds 1
                $p.offBefore = Foreground
                Send-LabKeyCombo -Name $VmName -Codes 0x12, 0x09
                Start-Sleep -Seconds 2
                $p.offAfter = Foreground
                Release $o 'end'
                $p.r = Receive-LabDesktop $job
                Stop-LabInSession -Name $VmName -Task $marker.Task
                Stop-LabInSession -Name $VmName -Task $notepad
                Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force }
                Save 'snip' $p
            }
        }

        It 'D-PRIV-05 privacy screen up: Print Screen, Win+Shift+S, Alt+Tab, Win+Tab dropped at the device and from the admin; Win+S and Shift+Tab work; switching works again when off' -Tag 'D-PRIV-05' {
            if ($old) { Set-ItResult -Skipped -Because 'Windows before 2004 has no privacy screen'; return }
            $p.consoleLuma | Should -BeLessThan 20 -Because 'the privacy screen is up'
            $p.dStart.Title | Should -Be $markerTitle -Because 'the marker is in front before the keys (precondition)'
            # at the device
            "$($p.dAltTab.Title)|$($p.dAltTab.Process)" | Should -Be "$($p.dStart.Title)|$($p.dStart.Process)" -Because 'Alt+Tab is dropped'
            $p.dPrint.start | Should -Be 'before the snips' -Because 'Print Screen is dropped'
            $p.dWinTab.Class | Should -Not -BeIn $switchers -Because 'Win+Tab is dropped'
            @($p.dSnippers) | Should -BeNullOrEmpty -Because 'Win+Shift+S is dropped'
            @($p.dSnipLog) | Should -BeNullOrEmpty -Because 'no snipping tool even started'
            @($p.dSeen | Where-Object { $_.e -eq 'down' -and $_.vk -eq 9 -and $_.mods -match 'Shift' }).Count | Should -BeGreaterThan 0 -Because 'Shift+Tab works'
            @($p.dSeen | Where-Object { $_.e -eq 'down' -and $_.vk -eq 9 -and $_.mods -notmatch 'Shift' }) | Should -BeNullOrEmpty
            # from the admin
            $p.aStart.Title | Should -Be $markerTitle -Because 'the marker is in front before the admin''s keys (precondition)'
            "$($p.aAltTab.Title)|$($p.aAltTab.Process)" | Should -Be "$($p.aStart.Title)|$($p.aStart.Process)" -Because 'Alt+Tab from the admin is dropped'
            $p.aPrint.start | Should -Be 'before the snips' -Because 'Print Screen from the admin is dropped'
            $p.aWinTab.Class | Should -Not -BeIn $switchers -Because 'Win+Tab from the admin is dropped'
            @($p.aSnippers) | Should -BeNullOrEmpty
            @($p.aSnipLog) | Should -BeNullOrEmpty
            @($p.aSeen | Where-Object { $_.e -eq 'down' -and $_.vk -eq 9 -and $_.mods -match 'Shift' }).Count | Should -BeGreaterThan 0 -Because 'Shift+Tab from the admin works'
            @($p.aSeen | Where-Object { $_.e -eq 'down' -and $_.vk -eq 9 -and $_.mods -notmatch 'Shift' }) | Should -BeNullOrEmpty
            # Win+S works
            "$($p.sAfter.Title)|$($p.sAfter.Process)" | Should -Not -Be "$($p.sBefore.Title)|$($p.sBefore.Process)" -Because 'Win+S opens Search'
            # off: switching works again
            "$($p.offAfter.Title)|$($p.offAfter.Process)" | Should -Not -Be "$($p.offBefore.Title)|$($p.offBefore.Process)" -Because 'Alt+Tab switches once the privacy screen is off'
        }
    }

    Context 'snipping tools started anyway' {
        BeforeAll {
            $t = [ordered]@{}
            if (-not $old) {
                # SnippingTool.exe (classic) where Windows has it, else the Store tool / the snip bar by protocol
                $launch = {
                    $null = Invoke-LabInSession -Name $VmName -ScriptBlock {
                        $exe = Join-Path $env:WINDIR 'System32\SnippingTool.exe'
                        if (Test-Path $exe) { Start-Process $exe } else { try { Start-Process 'ms-screensketch:' } catch { Start-Process 'ms-screenclip:' } }
                    }
                    Start-Sleep -Seconds 6
                }
                # control, no connection: the tool stays open
                & $launch
                $t.control = Snippers
                Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process SnippingTool, ScreenClippingHost, ScreenSketch -ErrorAction SilentlyContinue | Stop-Process -Force }
                $since = Get-LabGuestTime -Name $VmName
                $o = Out 'snippers'
                $job = Begin 'snippers' @(@{ do = 'privacy'; style = 'Black'; blockLocalInput = $false }, @{ do = 'sleep'; seconds = 4 }, (Mark $o 'on'), (Park $o 'end'))
                Reached $o 'on'
                & $launch
                $t.during = Snippers
                Release $o 'end'
                $t.r = Receive-LabDesktop $job
                Start-Sleep -Seconds 2
                $t.log = SnipLog $since
                $t.events = @(Get-LabAgentEvent -Name $VmName -Since $since | Where-Object { $_.Message -match 'SnippingTool|ScreenClippingHost|ScreenSketch|snip' })
                Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process SnippingTool, ScreenClippingHost, ScreenSketch -ErrorAction SilentlyContinue | Stop-Process -Force }
                Save 'snippers' $t
            }
        }

        It 'D-PRIV-06 a snipping tool showing a window is closed; agent log "... was closed - a snip would show the screen at the device", no event' -Tag 'D-PRIV-06' {
            if ($old) { Set-ItResult -Skipped -Because 'Windows before 2004 has no privacy screen'; return }
            if (@($t.control).Count -eq 0) { Set-ItResult -Skipped -Because 'no snipping tool could be started on this VM (SnippingTool.exe, ms-screensketch:, ms-screenclip:)'; return }
            @($t.during) | Should -BeNullOrEmpty -Because 'closed as soon as it showed a window'
            @($t.log | Where-Object { $_ -match '(SnippingTool|ScreenClippingHost|ScreenSketch)\.exe was closed . a snip would show the screen at the device' }).Count | Should -BeGreaterThan 0
            @($t.events) | Should -BeNullOrEmpty
        }
    }

    Context 'view-only' {
        BeforeAll {
            $v = [ordered]@{}
            if (-not $old) {
                $since = Get-LabGuestTime -Name $VmName
                $o = Out 'view-only'
                $job = Begin 'view-only' @(@{ do = 'sleep'; seconds = 2 }, @{ do = 'privacy'; style = 'Black'; blockLocalInput = $true }, @{ do = 'sleep'; seconds = 5 }, (Mark $o 'asked'), (Park $o 'end')) -ViewOnly $true
                Reached $o 'asked'
                $v.consoleLuma = Console-Luma (Join-Path $o 'console.png')
                Release $o 'end'
                $v.r = Receive-LabDesktop $job
                Start-Sleep -Seconds 2
                $v.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1104, 2103)
                Save 'view-only' $v
            }
        }

        It 'D-PRIV-07 a privacy screen request on a view-only connection is ignored' -Tag 'D-PRIV-07' {
            if ($old) { Set-ItResult -Skipped -Because 'Windows before 2004 has no privacy screen'; return }
            (@($v.r.statuses | Where-Object state -eq 'Connected'))[0].message | Should -Match 'view only'
            @($v.r.privacy | Where-Object { $_.privacy.screen -ne 'Off' -or $_.privacy.reason }) | Should -BeNullOrEmpty
            @($v.events) | Should -BeNullOrEmpty -Because 'neither turned on (1104) nor refused (2103): ignored'
            $v.consoleLuma | Should -BeGreaterThan 20 -Because 'the device''s screen isn''t covered'
        }
    }

    Context 'monitors change while it is on' {
        BeforeAll {
            $m = [ordered]@{}
            if (-not $old) {
                $m.before = Resolution
                $size = if ($m.before -eq '1280x720') { @(1024, 768) } else { @(1280, 720) }
                $since = Get-LabGuestTime -Name $VmName
                $o = Out 'monitors'
                # the resolution changed through Fit while the privacy screen is up: WM_DISPLAYCHANGE for the overlays
                $job = Begin 'monitors' @(
                    @{ do = 'privacy'; style = 'Black'; blockLocalInput = $false }, @{ do = 'sleep'; seconds = 4 }, (Mark $o 'on'), (Park $o 'shot')
                    @{ do = 'settings'; fitWidth = $size[0]; fitHeight = $size[1] }, @{ do = 'sleep'; seconds = 10 }, (Mark $o 'changed'), (Park $o 'end'))
                Reached $o 'on'
                $m.lumaBefore = Console-Luma (Join-Path $o 'console-before.png')
                Release $o 'shot'
                Reached $o 'changed'
                $m.during = Resolution
                $m.lumaAfter = Console-Luma (Join-Path $o 'console-after.png')
                Release $o 'end'
                $m.r = Receive-LabDesktop $job
                Start-Sleep -Seconds 2
                $m.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2103)
                Save 'monitors' $m
            }
        }

        It 'D-PRIV-08 monitors change while it is on: the overlays are rebuilt, or 2103 "... turned off because the device''s monitors changed"' -Tag 'D-PRIV-08' {
            if ($old) { Set-ItResult -Skipped -Because 'Windows before 2004 has no privacy screen'; return }
            $m.lumaBefore | Should -BeLessThan 20
            $m.during | Should -Not -Be $m.before -Because 'the display changed (precondition)'
            $turnedOff = @($m.r.privacy | Where-Object { $_.privacy.reason -match "turned off because the device's monitors changed" })
            if ($turnedOff.Count -gt 0) {
                @($m.events | Where-Object { $_.Message -match "turned off because the device's monitors changed" }).Count | Should -BeGreaterThan 0
            }
            else {
                $m.lumaAfter | Should -BeLessThan 20 -Because 'the overlays cover the new layout'
                @($m.r.privacy)[-1].privacy.screen | Should -Not -Be 'Off'
            }
        }
    }

    Context 'apps hidden by the policy' {
        BeforeAll {
            $h = [ordered]@{}
            $task = Start-LabInSession -Name $VmName -ScriptBlock { notepad.exe }
            Start-Sleep -Seconds 4
            $notepad = Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-Process notepad | Select-Object -First 1).Path }
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ HiddenApps = @($notepad) }
            $since = Get-LabGuestTime -Name $VmName
            $o = Out 'policy-apps'
            $job = Begin 'policy-apps' @(@{ do = 'sleep'; seconds = 4 }, (Mark $o 'connected'), (Park $o 'end'))
            Reached $o 'connected'
            $null = Invoke-LabPerson -Name $VmName -Do hover -Title 'RemoteHub'
            $h.banner = Invoke-LabPerson -Name $VmName -Do read -Title 'RemoteHub' -TimeoutSec 30
            Release $o 'end'
            $h.r = Receive-LabDesktop $job
            Set-LabAgentPolicy -Name $VmName -Clear
            Start-Sleep -Seconds 2
            $h.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1105)
            Stop-LabInSession -Name $VmName -Task $task
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force }
            Save 'policy-apps' $h
        }

        # Known product bug (DesktopHelper.cs:177-179, DesktopEndpoint.cs:762-763): today the policy's apps count as hidden
        # by the user. Tested for the correct behaviour - fails until fixed.
        It 'D-HIDE-03 apps hidden by the policy aren''t the user''s: no hiddenApps in the first Privacy message, no "1 app hidden", no 1105' -Tag 'D-HIDE-03' {
            @($h.r.statuses | Where-Object state -eq 'Connected').Count | Should -BeGreaterThan 0
            @($h.r.privacy).Count | Should -BeGreaterThan 0
            @($h.r.privacy)[0].privacy.hiddenApps | Should -Be 0
            $h.banner.found | Should -BeTrue
            $h.banner.text | Should -Not -Match 'app hidden|apps hidden'
            @($h.events | Where-Object { $_.Message -match 'hides \d+ app' }) | Should -BeNullOrEmpty
        }
    }
}
