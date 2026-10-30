# D-APP - apps in their own tabs (docs/app-tabs-design.md; docs/test-lab-cases.md section 9.15): apps from the device's
# Start menu list, started on demand in the admin's own session, each on a virtual monitor of its own, one tab each.
#   D-APP-01  the Start menu list with nobody signed in: programs and consoles listed, documents / links / missing
#             targets / what is behind a junction skipped, a duplicate once, groups and "Run as administrator" kept
#   D-APP-02  the list follows the policy: DesktopAppsAllowed narrows it, DesktopAppsElevated = 0 clears elevation,
#             DesktopApps = 0 refuses it
#   D-APP-03  three apps in three tabs with nobody signed in: one automatic sign-in, each tab shows only its own app
#             (never the others, never the primary monitor's marker window)
#   D-APP-04  typing in a tab reaches that tab's app, also after a click into another tab and with another window in front
#   D-APP-05  resizing a tab resizes its monitor and the app on it
#   D-APP-06  shell keys (Win+R) are dropped in an app tab
#   D-APP-07  closing a tab whose app asks "Save changes?" shows the question in the tab; "leave it running" ends the
#             tab and the app keeps running
#   D-APP-08  the last app tab of a session automatic sign-in created signs it out
#   D-APP-09  the agent audits each start with the program's path, never its arguments
#   D-APP-10  Run as administrator: elevated without a UAC prompt; refused with DesktopAppsElevated = 0
#   D-APP-11  someone else signed in on the console: refused, naming them
#   D-APP-12  a dropped connection continues with the same app (resume)
#   D-APP-13  the account's taskbar setting (MMTaskbarEnabled) comes back after the service was killed
#   D-APP-14  MaxAppTabs: one tab more than allowed is refused
#   D-APP-15  with nobody signed in the list says Store apps come once signed in, and has none
#   D-APP-16  signed in, the list adds the account's Store apps (Settings) in a part of its own, group "Store apps"
#   D-APP-17  a Store app (Settings, a UWP app drawn by ApplicationFrameHost) in a tab: its frame window is maximised on
#             the tab's monitor, not the primary one; the start is audited as shell:AppsFolder\<AppUserModelID>
#   D-APP-18  closing the Store app's tab closes the app
# Every case is skipped while the agent doesn't offer app tabs (Hello without the "apps" feature). The harness steps
# for app tabs come with the feature (DesktopHarness.cs): start {app: {program, arguments, workingFolder, elevated,
# name}}, appList (answers appLists), appClose {force}, appRestart.
# LabRole: device
# TimeLimitMin: 90
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    function Save($name, $data) { $data | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $EvidenceDir "d-app-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "d-app-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Mark([string]$o, [string]$n) { @{ do = 'markFile'; path = (Join-Path $o "$n.txt") } }
    function Park([string]$o, [string]$n, [int]$t = 600) { @{ do = 'waitFile'; path = (Join-Path $o "$n.txt"); timeout = $t } }
    function Reached([string]$o, [string]$n, [int]$t = 300) { Wait-LabHostFile (Join-Path $o "$n.txt") -TimeoutSec $t }
    function Release([string]$o, [string]$n) { Set-Content -Encoding ASCII (Join-Path $o "$n.txt") 'go' }
    function Skip { if ($script:noApps) { Set-ItResult -Skipped -Because $script:noApps } }
    function Log([string]$n) { "C:\ProgramData\RemoteHubLab\session\d-app-$n.jsonl" }
    # an app of the test app (RhLabApp) as a connection would list it
    function App([string]$n, [string]$Colour, [switch]$Ask, [switch]$Elevated) {
        @{ program = $script:exe; arguments = "/colour:$Colour /title:RH-$n /log:$(Log $n)$(if ($Ask) { ' /ask' })"; workingFolder = ''; elevated = $Elevated.IsPresent; name = "RH $n" }
    }
    # the first steps of an app tab: connect, start the app (signing in when nobody is), the tab's size
    function Tab($App, [switch]$SignIn, [int]$Width = 1280, [int]$Height = 720) {
        # the app's own start, then its outcome - a "Connected" before it is the sign-in screen's
        @(@{ do = 'connect' }, @{ do = 'start'; app = $App; autoSignIn = $SignIn.IsPresent }, @{ do = 'wait'; state = 'StartingApp'; anyOf = @('Failed', 'Denied', 'Ended'); timeout = 300 }
            @{ do = 'wait'; state = 'Connected'; anyOf = @('Failed', 'Ended'); timeout = 150 }
            @{ do = 'settings'; fitWidth = $Width; fitHeight = $Height }, @{ do = 'sleep'; seconds = 5 })
    }
    function States($r) { @($r.statuses | ForEach-Object { "$($_.state): $($_.message)" }) -join ' / ' }
    # Settings: a UWP app on every lab device (W11 and Server), whose window ApplicationFrameHost draws
    $script:settingsId = 'windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel'
    # the session's ApplicationFrameWindows: title|maximised|on the primary monitor|that monitor's size|visible|cloaked
    # (hidden and cloaked ones too, for the evidence; the cases look at the visible, uncloaked ones)
    $script:framesCode = @'
Add-Type -TypeDefinition @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class RhFrames {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int size);
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public int L, T, R, B, WL, WT, WR, WB; public uint Flags; }
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MonitorInfo i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string c, string n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rect r);
    // the shell's windows a Store app needs, and the top-level windows of one process: class|visible|cloaked|rect
    public static List<string> Shell(uint pid) {
        var list = new List<string>();
        list.Add("immersiveShell=" + (FindWindowW("ApplicationManager_ImmersiveShellWindow", null) != IntPtr.Zero));
        list.Add("tray=" + (FindWindowW("Shell_TrayWnd", null) != IntPtr.Zero));
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            int cloaked; if (DwmGetWindowAttribute(h, 14, out cloaked, 4) != 0) cloaked = -1;
            Rect r; GetWindowRect(h, out r);
            list.Add(c + "|" + IsWindowVisible(h) + "|" + cloaked + "|" + r.L + "," + r.T + "," + r.R + "," + r.B);
            return true;
        }, IntPtr.Zero);
        return list;
    }
    public static List<string> Frames() {
        var list = new List<string>();
        EnumWindows((h, l) => {
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            if (c.ToString() != "ApplicationFrameWindow") return true;
            var t = new StringBuilder(256); GetWindowText(h, t, 256);
            var mi = new MonitorInfo(); mi.Size = Marshal.SizeOf(typeof(MonitorInfo));
            GetMonitorInfo(MonitorFromWindow(h, 2), ref mi);
            int cloaked;
            if (DwmGetWindowAttribute(h, 14, out cloaked, 4) != 0) cloaked = -1; // DWMWA_CLOAKED
            list.Add(t + "|" + IsZoomed(h) + "|" + ((mi.Flags & 1) != 0) + "|" + (mi.R - mi.L) + "x" + (mi.B - mi.T) + "|" + IsWindowVisible(h) + "|" + cloaked);
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
"@
@([RhFrames]::Frames())
'@
    function Started([string]$n) { @(Get-LabTestAppLog -Name $VmName -Path (Log $n) | Where-Object e -eq 'started') }
    function ConsoleUser { @(Get-LabSessions -Name $VmName | Where-Object { $_.Console -and $_.User }) }
    # signed out and back at the sign-in screen: the console session is there again, with nobody in it (right after a
    # sign-out Windows has no console session for a moment, and a start then finds none)
    function Wait-SignedOut([int]$TimeoutSec = 120) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            $s = @(Get-LabSessions -Name $VmName | Where-Object Console)
            if ($s.Count -gt 0 -and -not ($s | Where-Object User)) { return $true }
            Start-Sleep -Seconds 3
        } while ((Get-Date) -lt $until)
        $false
    }

    # automatic sign-in available (the credential provider), nobody on the console
    $init = Initialize-LabVm -Name $VmName -Agent -AutoSignIn $true
    $pin = $init.Pin
    Disconnect-LabConsole -Name $VmName
    $script:exe = Install-LabTestApp -Name $VmName -StartMenu
    $hello = Invoke-LabDesktop -Name $VmName -Pin $pin -Account Console -Out (Out 'hello') -Steps @(@{ do = 'connect' }, @{ do = 'close' })
    $script:noApps = if (@($hello.hello.features) -notcontains 'apps') { "the agent doesn't offer app tabs (features: $(@($hello.hello.features) -join ', '))" }
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-app-agent-logs')
    # policy values, Start menu shortcuts, sessions signed in and out: not for the next file
    Set-LabVmDirty -Name $VmName
}

Describe 'D-APP <VmName>' -Tag Device, 'D-APP' {

    Context 'the Start menu list' {
        BeforeAll {
            if ($script:noApps) { return }
            $l = [ordered]@{}
            function List([string]$n) { Invoke-LabDesktop -Name $VmName -Pin $pin -Account Console -Out (Out "list-$n") -Steps @(@{ do = 'connect' }, @{ do = 'appList'; timeout = 60 }, @{ do = 'close' }) }
            $l.plain = List 'plain'
            $l.console = @(ConsoleUser)
            Set-LabAgentPolicy -Name $VmName -Values @{ DesktopAppsAllowed = @($script:exe); DesktopAppsElevated = 0 }
            $l.narrowed = List 'narrowed'
            Set-LabAgentPolicy -Name $VmName -Values @{ DesktopApps = 0 }
            $l.off = List 'off'
            Set-LabAgentPolicy -Name $VmName -Clear
            Save 'list' $l
        }

        It 'D-APP-01 the Start menu list with nobody signed in: programs and consoles, nothing that isn''t a program' -Tag 'D-APP-01' {
            Skip; if ($script:noApps) { return }
            $apps = @(@($l.plain.appLists)[0].apps)
            $apps.Count | Should -BeGreaterThan 0 -Because "the agent answered: $(@($l.plain.steps | Where-Object do -eq 'appList')[0].detail | ConvertTo-Json -Compress)"
            @($l.console) | Should -BeNullOrEmpty -Because 'listing needs no session - nobody was signed in for it'
            $ev = @($apps | Where-Object { $_.program -match '\\mmc\.exe$' -and $_.arguments -match 'eventvwr\.msc' })
            $ev | Should -Not -BeNullOrEmpty -Because 'Event Viewer (a .msc shortcut) is listed as mmc.exe with the console'
            $mine = @($apps | Where-Object { $_.program -eq $script:exe })
            @($mine | Where-Object name -eq 'RH Lab App').Count | Should -Be 1 -Because 'the duplicate shortcut (same program and arguments) is listed once'
            @($mine | Where-Object name -eq 'RH Lab App Copy') | Should -BeNullOrEmpty
            (@($mine | Where-Object name -eq 'RH Lab Tool')[0]).group | Should -Be 'RH Lab' -Because 'the Start menu folder is the group'
            (@($mine | Where-Object name -eq 'RH Lab Admin')[0]).elevated | Should -BeTrue -Because 'the shortcut says Run as administrator'
            (@($mine | Where-Object name -eq 'RH Lab App')[0]).arguments | Should -Match '/colour:255,0,0'
            foreach ($skipped in 'RH Lab Doc', 'RH Lab Missing', 'RH Lab Web', 'RH Lab Behind Junction') {
                @($apps | Where-Object name -eq $skipped) | Should -BeNullOrEmpty -Because "$skipped isn't a program that is there (or is behind a junction)"
            }
        }

        It 'D-APP-15 with nobody signed in the list says Store apps come once signed in, and has none' -Tag 'D-APP-15' {
            Skip; if ($script:noApps) { return }
            $parts = @($l.plain.appLists)
            $parts.Count | Should -Be 1 -Because 'without a session there is no second part'
            "$($parts[0].note)" | Should -Match 'Store apps are listed once you''re signed in on '
            @($parts[0].apps | Where-Object group -eq 'Store apps') | Should -BeNullOrEmpty
        }

        It 'D-APP-02 the list follows the policy: allowed programs only, no elevation, none when app tabs are off' -Tag 'D-APP-02' {
            Skip; if ($script:noApps) { return }
            $apps = @(@($l.narrowed.appLists)[0].apps)
            @($apps | Where-Object { $_.program -ne $script:exe }) | Should -BeNullOrEmpty -Because 'DesktopAppsAllowed lists only the test app'
            @($apps | Where-Object elevated) | Should -BeNullOrEmpty -Because 'DesktopAppsElevated = 0 clears Run as administrator'
            @($l.off.appLists) | Should -BeNullOrEmpty -Because 'DesktopApps = 0 refuses the list'
            (States $l.off) | Should -Match 'Failed' -Because 'the refusal says why'
        }
    }

    Context 'three apps in three tabs' {
        BeforeAll {
            if ($script:noApps) { return }
            $t = [ordered]@{}
            $o = Out 'tabs'
            $since = Get-LabGuestTime -Name $VmName
            # red: signs in; its app asks before closing. green and blue start right after it and wait for that sign-in.
            $red = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out (Out 'red') -TimeoutSec 1800 -Steps ((Tab (App 'red' '255,0,0' -Ask) -SignIn) + @(
                (Mark $o 'red-up'), (Park $o 'marker-on'), @{ do = 'frame'; out = 'red-1.png' }, (Mark $o 'red-pictured')
                (Park $o 'click'), @{ do = 'click'; x = 640; y = 400 }, @{ do = 'sleep'; seconds = 1 }, (Mark $o 'red-clicked')
                (Park $o 'close-red'), @{ do = 'appClose'; force = $false }, @{ do = 'wait'; state = 'AppClosing'; timeout = 30 }, @{ do = 'sleep'; seconds = 2 }
                @{ do = 'frame'; out = 'red-closing.png' }, (Mark $o 'red-closing')
                (Park $o 'leave-red'), @{ do = 'appClose'; force = $true }, @{ do = 'wait'; anyOf = @('Ended'); timeout = 30 }))
            Start-Sleep -Seconds 3
            $green = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out (Out 'green') -TimeoutSec 1800 -Steps ((Tab (App 'green' '0,255,0')) + @(
                (Mark $o 'green-up'), (Park $o 'marker-on'), @{ do = 'frame'; out = 'green-1.png' }, (Mark $o 'green-pictured')
                (Park $o 'type'), @{ do = 'keys'; text = 'green' }, @{ do = 'sleep'; seconds = 2 }, (Mark $o 'green-typed')
                (Park $o 'close-green'), @{ do = 'appClose'; force = $false }, @{ do = 'wait'; anyOf = @('Ended', 'AppClosed'); timeout = 60 }))
            $blue = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out (Out 'blue') -TimeoutSec 1800 -Steps ((Tab (App 'blue' '0,0,255')) + @(
                (Mark $o 'blue-up'), (Park $o 'marker-on'), @{ do = 'frame'; out = 'blue-1.png' }, (Mark $o 'blue-pictured')
                (Park $o 'resize'), @{ do = 'settings'; fitWidth = 1600; fitHeight = 900 }, @{ do = 'sleep'; seconds = 5 }, @{ do = 'frame'; out = 'blue-1600.png' }, (Mark $o 'blue-resized')
                (Park $o 'shell'), @{ do = 'combo'; scans = @('0xE05B', '0x13') }, @{ do = 'sleep'; seconds = 3 }, (Mark $o 'blue-shell')
                (Park $o 'close-blue'), @{ do = 'appClose'; force = $false }, @{ do = 'wait'; anyOf = @('Ended', 'AppClosed'); timeout = 60 }))
            foreach ($c in 'red', 'green', 'blue') { Reached $o "$c-up" 420 }

            # a magenta window on the primary monitor of the same session: no tab may show it
            $marker = Start-LabMarker -Name $VmName -Account Console
            Release $o 'marker-on'
            foreach ($c in 'red', 'green', 'blue') { Reached $o "$c-pictured" }
            $t.pids = @{ red = @(Started 'red')[0].pid; green = @(Started 'green')[0].pid; blue = @(Started 'blue')[0].pid }
            $t.layout = Get-LabScreenLayout -Name $VmName

            # D-APP-04: a click into red's tab, then typing in green's tab (the marker took the keyboard before)
            Release $o 'click'; Reached $o 'red-clicked'
            Release $o 'type'; Reached $o 'green-typed'
            $t.redLog = @(Get-LabTestAppLog -Name $VmName -Path (Log 'red'))
            $t.greenLog = @(Get-LabTestAppLog -Name $VmName -Path (Log 'green'))
            $t.markerKeys = @(Get-LabMarker -Name $VmName -Marker $marker | Where-Object e -eq 'char')
            # D-APP-05 and D-APP-06 in blue's tab
            Release $o 'resize'; Reached $o 'blue-resized'
            $t.resized = Get-LabScreenLayout -Name $VmName -Pids @($t.pids.blue)
            Release $o 'shell'; Reached $o 'blue-shell'
            $t.afterShell = Get-LabScreenLayout -Name $VmName
            Stop-LabInSession -Name $VmName -Task $marker.Task

            # D-APP-07: red asks before closing; the question shows in red's tab, then the tab ends and red runs on
            Release $o 'close-red'; Reached $o 'red-closing' 120
            $t.closing = Get-LabScreenLayout -Name $VmName -Pids @($t.pids.red)
            Release $o 'leave-red'
            $t.red = Receive-LabDesktop $red
            $t.redRunning = Invoke-LabGuest -Name $VmName -ArgumentList $t.pids.red -ScriptBlock { param($p) [bool](Get-Process -Id $p -ErrorAction SilentlyContinue) }
            Invoke-LabGuest -Name $VmName -ArgumentList $t.pids.red -ScriptBlock { param($p) Stop-Process -Id $p -Force -ErrorAction SilentlyContinue }
            # D-APP-08: green and blue close; the last one signs the session out
            Release $o 'close-green'; $t.green = Receive-LabDesktop $green
            Release $o 'close-blue'; $t.blue = Receive-LabDesktop $blue
            $t.signedOut = Wait-SignedOut 120
            $t.events = @(Get-LabAgentEvent -Name $VmName -Since $since)
            $t.signIns = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1106)
            $t.starts = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1109)
            Save 'tabs' $t
        }

        It 'D-APP-03 three apps in three tabs: one automatic sign-in, each tab shows only its own app' -Tag 'D-APP-03' {
            Skip; if ($script:noApps) { return }
            foreach ($r in $t.red, $t.green, $t.blue) { (States $r) | Should -Match 'Connected' -Because (States $r) }
            @($t.signIns).Count | Should -Be 1 -Because 'the first tab signed in; the other two waited for it and joined its session'
            $want = @{ red = @(255, 0, 0); green = @(0, 255, 0); blue = @(0, 0, 255) }
            foreach ($c in 'red', 'green', 'blue') {
                $png = Join-Path (Join-Path $EvidenceDir "d-app-$c") "$c-1.png"
                $own = Get-LabColorShare -Path $png -R $want[$c][0] -G $want[$c][1] -B $want[$c][2]
                $own | Should -BeGreaterThan 0.9 -Because "$c's tab is filled by its app (maximised on a monitor of the tab's size)"
                foreach ($other in @($want.Keys | Where-Object { $_ -ne $c })) {
                    Get-LabColorShare -Path $png -R $want[$other][0] -G $want[$other][1] -B $want[$other][2] | Should -BeLessThan 0.001 -Because "$other's app never shows in $c's tab"
                }
                Get-LabColorShare -Path $png | Should -BeLessThan 0.001 -Because "the marker window on the primary monitor never shows in $c's tab"
            }
            $monitors = @($t.layout.windows | Where-Object { $_.pid -in @($t.pids.red, $t.pids.green, $t.pids.blue) } | ForEach-Object monitor | Select-Object -Unique)
            $monitors.Count | Should -Be 3 -Because 'each app is on a monitor of its own'
        }

        It 'D-APP-04 typing in a tab reaches that tab''s app, after a click in another tab and with another window in front' -Tag 'D-APP-04' {
            Skip; if ($script:noApps) { return }
            (@($t.greenLog | Where-Object e -eq 'text') | Select-Object -Last 1).text | Should -Be 'green'
            @($t.redLog | Where-Object e -eq 'text') | Should -BeNullOrEmpty -Because 'red''s app had the keyboard last, but the typing was in green''s tab'
            @($t.markerKeys) | Should -BeNullOrEmpty -Because 'the marker window had the keyboard before the tabs were used'
        }

        It 'D-APP-05 resizing a tab resizes its monitor and the app on it' -Tag 'D-APP-05' {
            Skip; if ($script:noApps) { return }
            $img = @($t.blue.images | Where-Object { $_.path -match 'blue-1600' })[0]
            "$($img.width)x$($img.height)" | Should -Be '1600x900' -Because 'the picture is the tab''s new size'
            $w = @($t.resized.windows | Where-Object { $_.pid -eq $t.pids.blue -and $_.title -eq 'RH-blue' })[0]
            $m = @($t.resized.monitors | Where-Object device -eq $w.monitor)[0]
            "$($m.right - $m.left)x$($m.bottom - $m.top)" | Should -Be '1600x900' -Because 'blue''s monitor follows the tab'
            $w.maximized | Should -BeTrue -Because 'the app fills its monitor'
        }

        It 'D-APP-06 shell keys are dropped in an app tab' -Tag 'D-APP-06' {
            Skip; if ($script:noApps) { return }
            @($t.afterShell.windows | Where-Object { $_.class -eq '#32770' -and $_.title -eq 'Run' }) | Should -BeNullOrEmpty -Because 'Win+R in blue''s tab opened no Run dialog'
        }

        It 'D-APP-07 a "Save changes?" question shows in the tab; leaving the app running ends the tab' -Tag 'D-APP-07' {
            Skip; if ($script:noApps) { return }
            (States $t.red) | Should -Match 'AppClosing'
            $redMonitor = @($t.layout.windows | Where-Object { $_.pid -eq $t.pids.red -and $_.title -eq 'RH-red' })[0].monitor
            $ask = @($t.closing.windows | Where-Object { $_.class -eq '#32770' })[0]
            $ask | Should -Not -BeNullOrEmpty -Because 'red''s app asks before closing'
            $ask.monitor | Should -Be $redMonitor -Because 'the question opens on the tab''s monitor'
            Get-LabColorShare -Path (Join-Path (Join-Path $EvidenceDir 'd-app-red') 'red-closing.png') -R 255 -G 0 -B 0 | Should -BeLessThan 0.98 -Because 'the question is in the tab''s picture'
            $t.redRunning | Should -BeTrue -Because '"leave it running" ended the tab, not the app'
        }

        It 'D-APP-08 the last app tab signs out the session automatic sign-in created' -Tag 'D-APP-08' {
            Skip; if ($script:noApps) { return }
            $t.signedOut | Should -BeTrue -Because "nobody is on the console 2 minutes after the last tab (sessions: $(@(ConsoleUser | ForEach-Object User) -join ', '))"
        }

        It 'D-APP-09 each start is audited with the program''s path, never its arguments' -Tag 'D-APP-09' {
            Skip; if ($script:noApps) { return }
            @($t.starts).Count | Should -Be 3
            foreach ($e in $t.starts) { "$e" | Should -Match ([regex]::Escape(($script:exe).ToLowerInvariant())) -Because 'the program''s path' }
            ($t.events -join "`n") | Should -Not -Match '/colour:|/title:RH-|d-app-red\.jsonl' -Because 'no audit entry carries the arguments'
        }
    }

    Context 'run as administrator' {
        BeforeAll {
            if ($script:noApps) { return }
            # Each context signs in again: a fresh agent process starts its automatic sign-in limit (a few per 15 minutes) anew.
            Restart-LabAgent -Name $VmName -Pin $pin | Out-Null
            $e = [ordered]@{}
            $e.on = Invoke-LabDesktop -Name $VmName -Pin $pin -Account Console -Out (Out 'elevated') -TimeoutSec 900 -Steps ((Tab (App 'elevated' '0,255,0' -Elevated) -SignIn) + @(
                @{ do = 'sleep'; seconds = 3 }, @{ do = 'appClose'; force = $false }, @{ do = 'wait'; anyOf = @('Ended', 'AppClosed'); timeout = 60 }))
            $e.started = @(Started 'elevated')
            $null = Wait-SignedOut 120
            $e.consent = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 4688 } -MaxEvents 400 -ErrorAction SilentlyContinue | Where-Object { $_.Message -match '\\consent\.exe' }).Count }
            Set-LabAgentPolicy -Name $VmName -Values @{ DesktopAppsElevated = 0 }
            $e.off = Invoke-LabDesktop -Name $VmName -Pin $pin -Account Console -Out (Out 'elevated-off') -TimeoutSec 900 -Steps (Tab (App 'elevated-off' '0,255,0' -Elevated) -SignIn)
            $e.offStarted = @(Started 'elevated-off')
            Set-LabAgentPolicy -Name $VmName -Clear
            $null = Wait-SignedOut 120
            Save 'elevated' $e
        }

        It 'D-APP-10 Run as administrator starts elevated without a UAC prompt, and is refused with DesktopAppsElevated = 0' -Tag 'D-APP-10' {
            Skip; if ($script:noApps) { return }
            $e.started[0].elevated | Should -BeTrue -Because "the app reports an elevated token ($(States $e.on))"
            $e.consent | Should -Be 0 -Because 'no consent.exe (UAC prompt) ran - where process auditing is on'
            (States $e.off) | Should -Match 'Failed' -Because 'elevation is refused by policy'
            @($e.offStarted) | Should -BeNullOrEmpty -Because 'nothing was started'
        }
    }

    Context 'drops, the service and limits' {
        BeforeAll {
            if ($script:noApps) { return }
            Restart-LabAgent -Name $VmName -Pin $pin | Out-Null
            $d = [ordered]@{}
            $o = Out 'drop'
            $d.job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out (Out 'resume') -TimeoutSec 1200 -Steps ((Tab (App 'resume' '255,0,0') -SignIn) + @(
                @{ do = 'drop' }, @{ do = 'sleep'; seconds = 5 }, @{ do = 'reconnect' }, @{ do = 'start'; app = (App 'resume' '255,0,0'); resume = 'last' }
                @{ do = 'wait'; state = 'Connected'; anyOf = @('Failed', 'Ended'); timeout = 120 }, @{ do = 'frame'; out = 'resumed.png' }
                (Mark $o 'resumed'), (Park $o 'limit')
                (Mark $o 'limited'), (Park $o 'kill'), @{ do = 'sleep'; seconds = 2 }, @{ do = 'close' }))
            Reached $o 'resumed' 420
            $d.resumeStarts = @(Started 'resume')
            # D-APP-14: one tab more than MaxAppTabs = 1
            Set-LabAgentPolicy -Name $VmName -Values @{ MaxAppTabs = 1 }
            $d.second = Invoke-LabDesktop -Name $VmName -Pin $pin -Account Console -Out (Out 'second') -TimeoutSec 600 -Steps (Tab (App 'second' '0,0,255'))
            Set-LabAgentPolicy -Name $VmName -Clear
            Release $o 'limit'; Reached $o 'limited'
            # D-APP-13: while the tab is open the taskbar setting is off; the service is killed and starts again
            # the account's hive, read in its own session (signed in by the tab)
            $sid = Invoke-LabInSessionJson -Name $VmName -Account Console -Code '[Security.Principal.WindowsIdentity]::GetCurrent().User.Value'
            $d.sid = $sid
            $d.during = Invoke-LabGuest -Name $VmName -ArgumentList $sid -ScriptBlock { param($s) (Get-ItemProperty "Registry::HKEY_USERS\$s\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" -Name MMTaskbarEnabled -ErrorAction SilentlyContinue).MMTaskbarEnabled }
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process RemoteHubAgent -ErrorAction SilentlyContinue | Stop-Process -Force }
            Release $o 'kill'
            $d.resume = Receive-LabDesktop $d.job
            $d.job = $null
            Invoke-LabGuest -Name $VmName -ScriptBlock { Start-Service RemoteHubAgent -ErrorAction SilentlyContinue }
            Restart-LabAgent -Name $VmName -Pin $pin | Out-Null
            # the value comes back first and the record goes after it: wait for both
            $until = (Get-Date).AddSeconds(90)
            do {
                $d.after = Invoke-LabGuest -Name $VmName -ArgumentList $sid -ScriptBlock { param($s) (Get-ItemProperty "Registry::HKEY_USERS\$s\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" -Name MMTaskbarEnabled -ErrorAction SilentlyContinue).MMTaskbarEnabled }
                $d.records = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-ChildItem 'C:\ProgramData\RemoteHub\Agent' -Filter '*taskbar*' -ErrorAction SilentlyContinue | ForEach-Object Name) }
                if ($d.after -ne 0 -and @($d.records).Count -eq 0) { break }; Start-Sleep -Seconds 3
            } while ((Get-Date) -lt $until)
            Save 'drop' $d
        }

        It 'D-APP-12 a dropped connection continues with the same app' -Tag 'D-APP-12' {
            Skip; if ($script:noApps) { return }
            @($d.resume.statuses | Where-Object state -eq 'Connected').Count | Should -BeGreaterOrEqual 2 -Because (States $d.resume)
            @($d.resumeStarts).Count | Should -Be 1 -Because 'the app was started once and kept running through the drop'
            (@($d.resume.steps | Where-Object do -eq 'frame')[0]).ok | Should -BeTrue
        }

        It 'D-APP-13 the taskbar setting comes back after the service was killed' -Tag 'D-APP-13' {
            Skip; if ($script:noApps) { return }
            $d.during | Should -Be 0 -Because 'while an app tab is open the other monitors have no taskbar'
            $d.after | Should -Not -Be 0 -Because 'the restarted service (or the next helper) puts the account''s value back'
            @($d.records) | Should -BeNullOrEmpty -Because 'no record is left once it is back'
        }

        It 'D-APP-14 one tab more than MaxAppTabs is refused' -Tag 'D-APP-14' {
            Skip; if ($script:noApps) { return }
            (States $d.second) | Should -Match 'Failed' -Because 'MaxAppTabs = 1 and one tab was open'
            @(Started 'second') | Should -BeNullOrEmpty
        }
    }

    Context 'a Store app' {
        BeforeAll {
            if ($script:noApps) { return }
            Restart-LabAgent -Name $VmName -Pin $pin | Out-Null
            $null = Wait-SignedOut 180
            $st = [ordered]@{}
            $since = Get-LabGuestTime -Name $VmName
            $o = Out 'store'
            $settings = @{ program = "shell:AppsFolder\$($script:settingsId)"; arguments = ''; workingFolder = ''; elevated = $false; name = 'Settings' }
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -TimeoutSec 1200 -Steps ((Tab $settings -SignIn) + @(
                @{ do = 'frame'; out = 'store.png' }, (Mark $o 'up'), (Park $o 'close')
                @{ do = 'appClose'; force = $false }, @{ do = 'wait'; anyOf = @('AppClosed', 'Ended'); timeout = 60 }))
            Reached $o 'up' 600
            # the helper has placed the frame window (outside the tab's job) on the tab's monitor - up to 90 s: a UWP app's
            # frame may come late right after the sign-in (Server)
            $until = (Get-Date).AddSeconds(90)
            do {
                $st.frames = @(Invoke-LabInSessionJson -Name $VmName -Code $script:framesCode)
                if (@($st.frames | Where-Object { $_ -match '\|True\|False\|\d+x\d+\|True\|0$' }).Count) { break }
                Start-Sleep -Seconds 2
            } while ((Get-Date) -lt $until)
            # the shell's processes in the account's session and how long they run, should the frame not be there
            $st.shell = @(Invoke-LabInSessionJson -Name $VmName -Code ($script:framesCode.Replace('@([RhFrames]::Frames())',
                '$s = @(Get-Process SystemSettings -ErrorAction SilentlyContinue | Where-Object SessionId -eq (Get-Process -Id $PID).SessionId)[0]; @([RhFrames]::Shell($(if ($s) { [uint32]$s.Id } else { [uint32]0 })))')))
            $st.procs = @(Invoke-LabInSessionJson -Name $VmName -Code @'
$me = (Get-Process -Id $PID).SessionId
@(Get-Process -Name ApplicationFrameHost, SystemSettings, explorer, ShellExperienceHost, StartMenuExperienceHost, RuntimeBroker -ErrorAction SilentlyContinue |
    Where-Object SessionId -eq $me | ForEach-Object { "$($_.Name) $([int]((Get-Date) - $_.StartTime).TotalSeconds)s windows:$($_.MainWindowHandle -ne 0)" })
'@)
            # signed in now: the list has a second part with the account's Store apps
            $st.list = Invoke-LabDesktop -Name $VmName -Pin $pin -Account Console -Out (Out 'store-list') -Steps @(@{ do = 'connect' }, @{ do = 'appList'; timeout = 200 }, @{ do = 'close' })
            Release $o 'close'
            $st.tab = Receive-LabDesktop $job
            $until = (Get-Date).AddSeconds(30)
            do {
                $st.running = [int](Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-Process SystemSettings -ErrorAction SilentlyContinue).Count })
                if ($st.running -eq 0) { break }
                Start-Sleep -Seconds 2
            } while ((Get-Date) -lt $until)
            $st.starts = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1109)
            $null = Wait-SignedOut 180
            Save 'store' $st
        }

        It 'D-APP-16 signed in, the list adds the account''s Store apps in a part of its own' -Tag 'D-APP-16' {
            Skip; if ($script:noApps) { return }
            $parts = @($st.list.appLists)
            $parts.Count | Should -Be 2 -Because "the Start menu, then the Store apps ($(@($st.list.steps | Where-Object do -eq 'appList')[0].detail | ConvertTo-Json -Compress))"
            $parts[0].more | Should -BeTrue
            $parts[1].more | Should -BeFalse
            $settings = @($parts[1].apps | Where-Object { $_.program -eq "shell:AppsFolder\$($script:settingsId)" })
            $settings.Count | Should -Be 1 -Because "Settings is among the Store apps ($(@($parts[1].apps | ForEach-Object name) -join ', '))"
            $settings[0].group | Should -Be 'Store apps'
            $settings[0].elevated | Should -BeFalse
        }

        It 'D-APP-17 a Store app in a tab: its frame window is maximised on the tab''s monitor; audited by its ID' -Tag 'D-APP-17' {
            Skip; if ($script:noApps) { return }
            (States $st.tab) | Should -Match 'Connected: Settings started' -Because (States $st.tab)
            @($st.frames | Where-Object { $_ -match '\|True\|False\|\d+x\d+\|True\|0$' }).Count | Should -BeGreaterThan 0 -Because "a maximised, shown ApplicationFrameWindow on a monitor that isn't the primary one (frames as title|maximised|primary|size|visible|cloaked: $(@($st.frames) -join '; '); processes: $(@($st.procs | ForEach-Object { $_ }) -join ', '); shell: $(@($st.shell | ForEach-Object { $_ }) -join '; '))"
            @($st.starts | Where-Object { "$_" -match [regex]::Escape("shell:AppsFolder\$($script:settingsId)") }).Count | Should -Be 1 -Because 'the start is audited by the app''s ID'
        }

        It 'D-APP-18 closing the Store app''s tab closes the app' -Tag 'D-APP-18' {
            Skip; if ($script:noApps) { return }
            (States $st.tab) | Should -Match 'AppClosed|Ended'
            $st.running | Should -Be 0 -Because 'Settings ended within 30 s of the close'
        }
    }

    Context 'someone else on the console' {
        BeforeAll {
            if ($script:noApps) { return }
            Set-LabAutoLogon -Name $VmName -Account User
            Wait-LabAgent -Name $VmName -Pin $pin | Out-Null # the VM restarted for the other account's sign-in
            $s = [ordered]@{}
            $s.r = Invoke-LabDesktop -Name $VmName -Pin $pin -Account Console -Out (Out 'other') -TimeoutSec 600 -Steps (Tab (App 'other' '255,0,0') -SignIn)
            $s.started = @(Started 'other')
            Save 'other' $s
        }

        It 'D-APP-11 someone else signed in on the console: refused, naming them' -Tag 'D-APP-11' {
            Skip; if ($script:noApps) { return }
            (States $s.r) | Should -Match 'Failed: .*normaluser' -Because 'app tabs need the admin''s own session'
            @($s.started) | Should -BeNullOrEmpty
        }
    }
}
