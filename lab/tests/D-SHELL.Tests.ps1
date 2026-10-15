# D-SHELL (spike/uiaccess-overlay) - do the Windows shell surfaces (Start, Search, Quick Settings / Action Center,
# Notification Center, Task View, Alt+Tab, a toast, a taskbar thumbnail) draw over the privacy screen at the device?
# The same run twice: the helper without UIAccess (HelperUIAccess = 0) and with it (the default on this branch).
# Per surface: the console's bright share outside the banner strip (covered: < 0.5 %), the admin's picture against the
# one before (the surface is in it: > 0.5 % of the pixels changed), the overlays' display affinity and z-band.
# See docs/privacy-mode-design.md, "Spike: UIAccess overlay".
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "d-shell-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "d-shell-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Mark([string]$o, [string]$n) { @{ do = 'markFile'; path = (Join-Path $o "$n.txt") } }
    function Park([string]$o, [string]$n, [int]$t = 300) { @{ do = 'waitFile'; path = (Join-Path $o "$n.txt"); timeout = $t } }
    function Reached([string]$o, [string]$n, [int]$t = 180) { Wait-LabHostFile (Join-Path $o "$n.txt") -TimeoutSec $t }
    function Release([string]$o, [string]$n) { Set-Content (Join-Path $o "$n.txt") 'go' }
    Add-Type -AssemblyName System.Drawing
    function Luma($c) { 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B }
    # share of bright pixels below the banner strip (top 12 %)
    function Bright([string]$Path) {
        $b = [Drawing.Bitmap]::FromFile($Path); $hit = 0; $n = 0
        try { for ($y = [int]($b.Height * 0.12); $y -lt $b.Height; $y += 4) { for ($x = 0; $x -lt $b.Width; $x += 4) { if ((Luma $b.GetPixel($x, $y)) -gt 40) { $hit++ }; $n++ } } }
        finally { $b.Dispose() }
        [math]::Round($hit / $n, 4)
    }
    # share of pixels whose luma changed by more than 30 between two pictures of the same size
    function Changed([string]$PathA, [string]$PathB) {
        $a = [Drawing.Bitmap]::FromFile($PathA); $b = [Drawing.Bitmap]::FromFile($PathB); $hit = 0; $n = 0
        try {
            if ($a.Width -ne $b.Width -or $a.Height -ne $b.Height) { return -1 }
            for ($y = 0; $y -lt $a.Height; $y += 4) { for ($x = 0; $x -lt $a.Width; $x += 4) { if ([math]::Abs((Luma $a.GetPixel($x, $y)) - (Luma $b.GetPixel($x, $y))) -gt 30) { $hit++ }; $n++ } }
        }
        finally { $a.Dispose(); $b.Dispose() }
        [math]::Round($hit / $n, 4)
    }
    # In normaluser's session: the top-level windows in z-order (title, class, band, display affinity) - the overlays
    # ("RemoteHub - Privacy screen"), the banner and what is above them.
    $zorder = {
        Add-Type @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class RhZ {
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(IntPtr h, out uint a);
    [DllImport("user32.dll")] static extern bool GetWindowBand(IntPtr h, out uint band);
    [DllImport("user32.dll")] static extern int GetWindowLongW(IntPtr h, int i);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    public static List<string> Read(int max) {
        var list = new List<string>();
        for (var h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero && list.Count < max; h = GetWindow(h, 2)) {
            if (!IsWindowVisible(h)) continue;
            var t = new StringBuilder(256); GetWindowTextW(h, t, 256);
            var c = new StringBuilder(256); GetClassNameW(h, c, 256);
            uint a, band, pid; GetWindowDisplayAffinity(h, out a); GetWindowBand(h, out band); GetWindowThreadProcessId(h, out pid);
            var ex = GetWindowLongW(h, -20);
            RECT rc; GetWindowRect(h, out rc);
            list.Add(string.Format("{0}|{1}|band={2}|wda=0x{3:X}|topmost={4}|pid={5}|rect={6},{7},{8},{9}", t, c, band, a, (ex & 8) != 0, pid, rc.L, rc.T, rc.R, rc.B));
        }
        return list;
    }
}
'@
        [RhZ]::Read(25) | ConvertTo-Json | Set-Content 'C:\Users\Public\rhlab-zorder.json' -Encoding UTF8
    }
    function Get-ZOrder {
        $null = Invoke-LabInSession -Name $VmName -ScriptBlock $zorder
        $json = Invoke-LabGuest -Name $VmName -ScriptBlock { $f = 'C:\Users\Public\rhlab-zorder.json'; if (Test-Path $f) { Get-Content $f -Raw; Remove-Item $f } }
        @(([string]$json | ConvertFrom-Json)) # (..): 5.1 writes an array as one object - the parentheses unroll it
    }
    # The helper's token: TokenUIAccess (26) - as PSAdmin, elevated, in the guest
    function Get-HelperUiAccess {
        Invoke-LabGuest -Name $VmName -ScriptBlock {
            Add-Type @'
using System; using System.Runtime.InteropServices;
public static class RhTok {
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint a, bool i, int pid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint a, out IntPtr t);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr t, int c, out int v, int l, out int r);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    public static string UiAccess(int pid) {
        var p = OpenProcess(0x1000, false, pid); if (p == IntPtr.Zero) return "OpenProcess " + Marshal.GetLastWin32Error();
        try {
            IntPtr t; if (!OpenProcessToken(p, 8, out t)) return "OpenProcessToken " + Marshal.GetLastWin32Error();
            try { int v, r; return GetTokenInformation(t, 26, out v, 4, out r) ? v.ToString() : "GetTokenInformation " + Marshal.GetLastWin32Error(); }
            finally { CloseHandle(t); }
        } finally { CloseHandle(p); }
    }
}
'@
            $helper = Get-CimInstance Win32_Process -Filter "Name='RemoteHubAgent.exe'" | Where-Object CommandLine -match '\sdesktop\s*$' | Select-Object -First 1
            if ($helper) { [RhTok]::UiAccess([int]$helper.ProcessId) } else { 'no helper' }
        }
    }
    $toast = {
        $null = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        $null = [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
        $x = New-Object Windows.Data.Xml.Dom.XmlDocument
        $x.LoadXml('<toast scenario="reminder"><visual><binding template="ToastGeneric"><text>RHLABTOAST</text><text>A toast over the privacy screen?</text></binding></visual><actions><action content="OK" arguments="ok"/></actions></toast>')
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe').Show([Windows.UI.Notifications.ToastNotification]::new($x))
    }
    # Notepad's place among the taskbar's app buttons (for the thumbnail: Win+T, then Right that many times). By
    # keyboard, not by position: the centred taskbar moves whenever the lab's own console window comes and goes.
    $taskbarButton = {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $root = [Windows.Automation.AutomationElement]::RootElement
        $tray = $root.FindFirst('Children', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ClassNameProperty, 'Shell_TrayWnd')))
        $names = @($tray.FindAll('Descendants', [Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ClassName -match 'TaskListButton' } | ForEach-Object { $_.Current.Name })
        $index = -1; for ($i = 0; $i -lt $names.Count; $i++) { if ($index -lt 0 -and $names[$i] -match '^Notepad') { $index = $i } }
        @{ names = $names; index = $index } | ConvertTo-Json | Set-Content 'C:\Users\Public\rhlab-taskbar.json' -Encoding UTF8
    }

    $vm = Get-LabVm $VmName
    Reset-LabVm -Name $VmName
    $install = Install-LabAgent -Name $VmName
    $pin = $install.pin
    Set-LabAutoLogon -Name $VmName -Account User
    $null = Wait-LabAgent -Name $VmName -Pin $pin
    $notepad = Start-LabInSession -Name $VmName -ScriptBlock { notepad.exe }
    Start-Sleep -Seconds 6
    $null = Invoke-LabInSession -Name $VmName -ScriptBlock $taskbarButton
    $taskbar = [string](Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Content 'C:\Users\Public\rhlab-taskbar.json' -Raw }) | ConvertFrom-Json
    $right = @(1..([math]::Max(0, [int]$taskbar.index)) | Where-Object { $taskbar.index -gt 0 } | ForEach-Object { @{ do = 'key'; scan = '0xE04D' }, @{ do = 'key'; scan = '0xE04D'; flags = @('Up') }, @{ do = 'sleep'; seconds = 1 } })

    $esc = @(@{ do = 'combo'; scans = @('0x01') })
    $win = '0xE05B'
    # name, harness steps that open it, steps that close it, something the host does first
    $surfaces = @(
        @{ n = 'start'; open = @(@{ do = 'combo'; scans = @($win) }); close = $esc }
        @{ n = 'search'; open = @(@{ do = 'combo'; scans = @($win, '0x1F') }); close = $esc }
        @{ n = 'quicksettings'; open = @(@{ do = 'combo'; scans = @($win, '0x1E') }); close = $esc }
        @{ n = 'notifications'; open = @(@{ do = 'combo'; scans = @($win, '0x31') }); close = $esc }
        @{ n = 'taskview'; open = @(@{ do = 'combo'; scans = @($win, '0x0F') }); close = $esc }
        @{ n = 'alttab'; open = @(@{ do = 'key'; scan = '0x38' }, @{ do = 'key'; scan = '0x0F' }, @{ do = 'key'; scan = '0x0F'; flags = @('Up') })
            close = @(@{ do = 'key'; scan = '0x38'; flags = @('Up') }) + $esc }
        @{ n = 'thumbnail'; open = @(@{ do = 'combo'; scans = @($win, '0x14') }, @{ do = 'sleep'; seconds = 1 }) + $right; close = $esc }
        @{ n = 'toast'; open = @(); close = @(); host = $toast }
    )
    # the surfaces close before the next one is opened; Alt+Tab is still held while its pictures are taken
    $r = [ordered]@{ taskbar = $taskbar }
    foreach ($mode in 'off', 'on') {
        Set-LabAgentPolicy -Name $VmName -Clear -Values @{ PrivacyScreen = 2; HelperUIAccess = $(if ($mode -eq 'on') { 1 } else { 0 }) }
        $o = Out $mode
        $steps = @(@{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 120 }
            @{ do = 'privacy'; style = 'Black'; blockLocalInput = $true }, @{ do = 'sleep'; seconds = 5 }, @{ do = 'frame'; out = 'base.png' }, (Mark $o 'base'), (Park $o 'base-go'))
        foreach ($s in $surfaces) {
            $steps += @((Mark $o "$($s.n)-pre"), (Park $o "$($s.n)-go")) + $s.open + @(@{ do = 'sleep'; seconds = 3 }, @{ do = 'frame'; out = "$($s.n).png" }, (Mark $o $s.n), (Park $o "$($s.n)-done")) + $s.close + @(@{ do = 'sleep'; seconds = 2 })
        }
        # the device's user clicks Disconnect on the banner (over the overlays, through the input block)
        $steps += @((Mark $o 'banner'), @{ do = 'wait'; anyOf = @('Ended'); timeout = 90 }, @{ do = 'close' })
        $job = Start-LabDesktop -Name $VmName -Pin $pin -Out $o -Steps $steps
        $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
        $m = [ordered]@{}
        Reached $o 'base'
        $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $o 'console-base.png')
        $m.uiAccess = Get-HelperUiAccess
        $m.zBase = Get-ZOrder
        $m.base = Bright (Join-Path $o 'console-base.png')
        Release $o 'base-go'
        foreach ($s in $surfaces) {
            Reached $o "$($s.n)-pre"
            if ($s.host) { $null = Invoke-LabInSession -Name $VmName -ScriptBlock $s.host; Start-Sleep -Seconds 2 }
            Release $o "$($s.n)-go"
            Reached $o $s.n
            $shot = Join-Path $o "console-$($s.n).png"
            $null = Get-LabScreenshot -Name $VmName -Path $shot
            $m[$s.n] = [ordered]@{
                console = Bright $shot
                picture = Changed (Join-Path $o 'base.png') (Join-Path $o "$($s.n).png")
                z       = Get-ZOrder
            }
            Release $o "$($s.n)-done"
        }
        Reached $o 'banner'
        Start-Sleep -Seconds 3
        # by the banner's rectangle, not by OCR: the banner (and the overlay under it) is excluded from capture, a
        # screen read in the session sees neither. "Disconnect" is the banner's last button, at its right end.
        $banner = @(Get-ZOrder | Where-Object { $_ -like 'RemoteHub|*' })[0]
        $m.disconnect = [ordered]@{ banner = $banner; clicked = [bool]$banner }
        if ($banner -match 'rect=(-?\d+),(-?\d+),(-?\d+),(-?\d+)') {
            $m.disconnect.x = [int]$Matches[3] - 55; $m.disconnect.y = [int](([int]$Matches[2] + [int]$Matches[4]) / 2)
            Send-LabMouse -Name $VmName -X $m.disconnect.x -Y $m.disconnect.y
            Start-Sleep -Seconds 1
            $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $o 'console-after-disconnect.png')
        }
        $m.r = Receive-LabDesktop $job
        $m.ended = @($m.r.statuses | Where-Object state -eq 'Ended' | ForEach-Object message)
        $r[$mode] = $m
        Start-Sleep -Seconds 3
    }
    Set-LabAgentPolicy -Name $VmName -Clear
    Stop-LabInSession -Name $VmName -Task $notepad
    $r.log = @(Get-LabAgentLog -Name $VmName | Where-Object { $_ -match 'UIPI|denied|0x5\b|error|failed|exception' })
    Save 'result' $r
    $names = $surfaces | ForEach-Object n
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-shell-agent-logs')
}

Describe 'D-SHELL <VmName>' -Tag Desktop, 'D-SHELL' {
    It 'D-SHELL-01 the helper has UIAccess only when switched on' {
        $r.on.uiAccess | Should -Be '1'
        $r.off.uiAccess | Should -Be '0'
    }

    It 'D-SHELL-02 <_> with UIAccess: the console stays covered' -ForEach @('start', 'search', 'quicksettings', 'notifications', 'taskview', 'alttab', 'thumbnail', 'toast') {
        $r.on[$_].console | Should -BeLessThan 0.005
    }

    # Task View and Alt+Tab don't open while the privacy screen is up - SnipGuard drops their keys - so not here
    It 'D-SHELL-06 <_> with UIAccess: the admin''s picture still shows it' -ForEach @('start', 'search', 'quicksettings', 'notifications', 'thumbnail', 'toast') {
        $r.on[$_].picture | Should -BeGreaterThan 0.005
    }

    It 'D-SHELL-03 the overlays keep WDA_EXCLUDEFROMCAPTURE (0x11) with UIAccess' {
        $overlays = @($r.on.zBase | Where-Object { $_ -match 'Privacy screen' })
        $overlays.Count | Should -BeGreaterThan 0
        @($overlays | Where-Object { $_ -notmatch 'wda=0x11' }) | Should -BeNullOrEmpty
    }

    It 'D-SHELL-04 the banner is above the overlays' {
        $z = @($r.on.zBase | ForEach-Object { ($_ -split '\|')[0] })
        $banner = [array]::IndexOf($z, 'RemoteHub')
        $overlay = @(0..($z.Count - 1) | Where-Object { $z[$_] -match 'Privacy screen' })[0]
        $banner | Should -BeGreaterOrEqual 0
        $banner | Should -BeLessThan $overlay
    }

    It 'D-SHELL-05 the device''s user ends the session with Disconnect, with UIAccess' {
        $r.on.disconnect.clicked | Should -BeTrue
        $r.on.ended -join ' ' | Should -Match 'The user at the device ended the session'
    }
}
