# D-APPSPIKE (spike for app tabs, docs/app-tabs-design.md section 11; docs/test-lab-cases.md section 9.15) - can the
# pieces app tabs are built on do what the design needs, before any of it is built? The agent as it is, the Virtual
# Display Driver it ships, and Windows:
#   D-APPSPIKE-01  the driver adds and removes monitors one at a time (count in its settings + its reload command, or a
#                  device restart when the reload isn't enough)
#   D-APPSPIKE-02  windows on the monitors that stay keep their place when another monitor goes
#   D-APPSPIKE-03  each virtual monitor runs its own mode; a size added to the list later can be set after a reload
#   D-APPSPIKE-04  the driver takes 8 monitors (MaxAppTabs' default)
#   D-APPSPIKE-05  "show my taskbar on all displays" (MMTaskbarEnabled) takes effect without restarting Explorer
#   D-APPSPIKE-06  a process that sent the last input can give the foreground to another app's window, and typing then
#                  reaches that window. The stand-in runs as the session's user, not as the agent's LocalSystem helper.
#   D-APPSPIKE-07  two desktop connections to the same session at once: both get pictures; one ending doesn't end the other
# The console account is signed in on the console (app tabs run in the admin's own session). One desktop connection
# holds the virtual display on for the whole file; the test changes the driver's settings itself, as the agent will.
# Findings go to the evidence (d-appspike-*.json) and into the design document once the spike has run.
# LabRole: device
# LabRuns: on request
# TimeLimitMin: 60
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    function Save($name, $data) { $data | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $EvidenceDir "d-appspike-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "d-appspike-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Release([string]$o, [string]$n) { Set-Content -Encoding ASCII (Join-Path $o "$n.txt") 'go' }

    # The driver's settings as the agent writes them (VirtualDisplay.Settings), with a monitor count and a size list,
    # then its reload command through the control pipe (VirtualDisplay.Reload: "RELOAD_DRIVER", UTF-16).
    function Set-Vdd([int]$Count, [string[]]$Sizes) {
        [string](Invoke-LabGuest -Name $VmName -ArgumentList $Count, (@($Sizes) -join ';') -ScriptBlock {
            param($count, $sizes)
            $res = ($sizes -split ';' | ForEach-Object { $w, $h = $_ -split 'x'; "<resolution><width>$w</width><height>$h</height><refresh_rate>60</refresh_rate></resolution>" }) -join ''
            $xml = "<?xml version=`"1.0`" encoding=`"utf-8`"?><vdd_settings><monitors><count>$count</count></monitors><gpu><friendlyname>default</friendlyname></gpu>" +
                "<global><g_refresh_rate>60</g_refresh_rate></global><resolutions>$res</resolutions><options><CustomEdid>false</CustomEdid><PreventSpoof>false</PreventSpoof>" +
                "<EdidCeaOverride>false</EdidCeaOverride><HardwareCursor>true</HardwareCursor><SDR10bit>false</SDR10bit><HDRPlus>false</HDRPlus><logging>false</logging>" +
                "<debuglogging>false</debuglogging></options></vdd_settings>"
            $f = 'C:\VirtualDisplayDriver\vdd_settings.xml'
            Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
            [IO.File]::WriteAllText($f, $xml, (New-Object Text.UTF8Encoding $false))
            $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', 'MTTVirtualDisplayPipe', [IO.Pipes.PipeDirection]::InOut)
            try {
                $pipe.Connect(3000)
                $b = [Text.Encoding]::Unicode.GetBytes('RELOAD_DRIVER')
                $pipe.Write($b, 0, $b.Length); $pipe.Flush()
                'reloaded'
            }
            catch { "reload failed: $($_.Exception.Message)" }
            finally { $pipe.Dispose() }
        })
    }
    # Restarts the driver's device, also one that failed (problem 43): restart first, disable + enable when that leaves
    # it failed. Answers what each step said and the state after it.
    function Restart-Vdd {
        [string](Invoke-LabGuest -Name $VmName -ScriptBlock {
            $said = @()
            foreach ($x in @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { @($_.HardwareID) -contains 'Root\MttVDD' })) {
                $out = & pnputil.exe /restart-device $x.InstanceId 2>&1
                $said += "restart $($x.InstanceId): exit $LASTEXITCODE"
                Start-Sleep -Seconds 3
                if ((Get-PnpDevice -InstanceId $x.InstanceId).Status -ne 'OK') {
                    $null = & pnputil.exe /disable-device $x.InstanceId 2>&1; $e1 = $LASTEXITCODE
                    $null = & pnputil.exe /enable-device $x.InstanceId 2>&1; $e2 = $LASTEXITCODE
                    Start-Sleep -Seconds 3
                    $said += "disable exit $e1, enable exit $e2"
                }
                $now = Get-PnpDevice -InstanceId $x.InstanceId
                $said += "now $($now.Status) problem $([int]$now.Problem)"
            }
            $said -join ', '
        })
    }
    # Before a context: a device that failed in the one before is restarted, so one failure doesn't fail every case
    # after it. Answers 'ok' or what the restart did (kept in the context's evidence).
    function Repair-Vdd {
        $s = Get-VddState
        if ($s -match ' OK problem 0$') { return 'ok' }
        "was $s; $(Restart-Vdd)"
    }
    # The driver's device as Plug and Play sees it: status and problem code (43 = the driver reported a failure)
    function Get-VddState {
        [string](Invoke-LabGuest -Name $VmName -ScriptBlock {
            @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { @($_.HardwareID) -contains 'Root\MttVDD' } |
                    ForEach-Object { "$($_.InstanceId) $($_.Status) problem $([int]$_.Problem)" }) -join '; '
        })
    }
    # Waits until the session shows $Total monitors (or the time is up); answers the last layout.
    function Wait-Monitors([int]$Total, [int]$TimeoutSec = 40) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            $l = Get-LabScreenLayout -Name $VmName
            if (@($l.monitors).Count -eq $Total) { return $l }
            Start-Sleep -Seconds 2
        } while ((Get-Date) -lt $until)
        $l
    }
    # One step of the count: the reload first, a device restart only when the reload didn't do it. Answers the way that
    # worked ('reload', 'restart' or 'none'), the seconds and the layout.
    function Set-Count([int]$Virtual, [string[]]$Sizes = @('1280x720', '1600x900', '1024x768')) {
        $want = $script:physical + $Virtual
        $t = [Diagnostics.Stopwatch]::StartNew()
        $said = Set-Vdd $Virtual $Sizes
        $l = Wait-Monitors $want 40
        $how = 'reload'
        if (@($l.monitors).Count -ne $want) {
            $said = "$said; $(Restart-Vdd)"
            $l = Wait-Monitors $want 60
            $how = $(if (@($l.monitors).Count -eq $want) { 'restart' } else { 'none' })
        }
        [pscustomobject]@{ virtual = $Virtual; want = $want; got = @($l.monitors).Count; how = $how; said = $said; seconds = [math]::Round($t.Elapsed.TotalSeconds, 1); device = (Get-VddState); layout = $l }
    }
    # RhLabApp windows in the console account's session; answers their pids (in the order of -Colours)
    function Start-Apps([string[]]$Colours, [string]$Tag) {
        $pids = @()
        foreach ($c in $Colours) {
            $log = "C:\ProgramData\RemoteHubLab\session\appspike-$Tag-$($c -replace ',', '-').jsonl"
            $r = Invoke-LabInSessionJson -Name $VmName -Data @{ exe = $script:exe; c = $c; log = $log } -Code @'
$p = Start-Process -FilePath $A.exe -ArgumentList "/colour:$($A.c)", "/log:$($A.log)", "/title:RH-Spike-$($A.c)" -PassThru
$p.Id
'@
            $pids += [int]$r
        }
        $until = (Get-Date).AddSeconds(30)
        do { $l = Get-LabScreenLayout -Name $VmName -Pids $pids; if (@($l.windows).Count -ge $pids.Count) { break }; Start-Sleep -Seconds 1 } while ((Get-Date) -lt $until)
        $pids
    }
    # Moves each pid's main window to the top left of a monitor (by device name), 640x480
    function Move-Apps([int[]]$Pids, [string[]]$Devices) {
        Invoke-LabInSessionJson -Name $VmName -Data @{ pids = @($Pids); devices = @($Devices) } -Code @'
if (-not ('RhSpikeMove' -as [type])) {
    Add-Type @"
using System; using System.Runtime.InteropServices;
public static class RhSpikeMove { [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int c, uint f); }
"@
}
Add-Type -AssemblyName System.Windows.Forms
$screens = [Windows.Forms.Screen]::AllScreens
$done = @()
for ($i = 0; $i -lt @($A.pids).Count; $i++) {
    $h = (Get-Process -Id $A.pids[$i]).MainWindowHandle
    # a monitor that isn't there (the driver failed) puts the window on the primary one, apart from the others
    $s = $screens | Where-Object DeviceName -eq $A.devices[$i] | Select-Object -First 1
    $dx = 0
    if (-not $s) { $s = [Windows.Forms.Screen]::PrimaryScreen; $dx = 330 * $i }
    $ok = [RhSpikeMove]::SetWindowPos($h, [IntPtr]::Zero, $s.Bounds.X + 20 + $dx, $s.Bounds.Y + 20, 300, 300, 0x0044)
    $done += [pscustomobject]@{ pid = $A.pids[$i]; device = $A.devices[$i]; ok = $ok }
}
$done
'@
    }
    function Stop-Apps([int[]]$Pids) {
        Invoke-LabGuest -Name $VmName -ArgumentList (, $Pids) -ScriptBlock { param($p) foreach ($x in $p) { Stop-Process -Id $x -Force -ErrorAction SilentlyContinue } }
    }

    $init = Initialize-LabVm -Name $VmName -Agent -AutoLogon Console
    $pin = $init.Pin
    Set-LabAgentJsonValue -Name $VmName -Pin $pin -Key 'virtualDisplay' -Value 'always'
    $script:exe = Install-LabTestApp -Name $VmName
    Wait-LabDesktop -Name $VmName -Account Console | Out-Null

    # the holder: a connection to the account's own session that switches the virtual display on (Fit) and keeps it on
    # until the end of the file; D-APPSPIKE-07 takes a picture through it while a second connection is up
    $holdOut = Out 'holder'
    $holder = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $holdOut -TimeoutSec 3000 -Steps @(
        @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; state = 'Connected'; timeout = 60 }
        @{ do = 'settings'; fitWidth = 1280; fitHeight = 720 }, @{ do = 'sleep'; seconds = 8 }
        @{ do = 'markFile'; path = (Join-Path $holdOut 'on.txt') }
        @{ do = 'waitFile'; path = (Join-Path $holdOut 'second-up.txt'); timeout = 2700 }
        @{ do = 'frame'; out = 'holder-while-second.png' }
        @{ do = 'markFile'; path = (Join-Path $holdOut 'holder-pictured.txt') }
        @{ do = 'waitFile'; path = (Join-Path $holdOut 'go.txt'); timeout = 600 }, @{ do = 'close' })
    Wait-LabHostFile (Join-Path $holdOut 'on.txt') -TimeoutSec 180
    # the virtual monitor the holder switched on is counted as "physical" here: the steps below set the count themselves
    $script:physical = [math]::Max(1, @((Get-LabScreenLayout -Name $VmName).monitors).Count - 1)
    $script:taskbar = Invoke-LabInSessionJson -Name $VmName -Code @'
$v = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name MMTaskbarEnabled -ErrorAction SilentlyContinue
if ($v) { [int]$v.MMTaskbarEnabled } else { -1 }
'@
}

AfterAll {
    try { Release (Join-Path $EvidenceDir 'd-appspike-holder') 'second-up'; Release (Join-Path $EvidenceDir 'd-appspike-holder') 'go'; $h = Receive-LabDesktop $holder; Save 'holder' $h } catch { }
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-appspike-agent-logs')
    # the driver's count, the account's taskbar setting and the test apps: not for the next file
    Set-LabVmDirty -Name $VmName
}

Describe 'D-APPSPIKE <VmName>' -Tag AppSpike {

    Context 'monitors one at a time' {
        BeforeAll {
            $m = [ordered]@{ physical = $script:physical }
            $m.up2 = Set-Count 2
            $m.up3 = Set-Count 3
            # a window on each virtual monitor, then the last monitor goes
            $virtual = @($m.up3.layout.monitors | Where-Object { -not $_.primary } | Select-Object -Last 3 | ForEach-Object device)
            $script:spikePids = Start-Apps @('255,0,0', '0,255,0', '0,0,255') 'move'
            $m.moved = Move-Apps $script:spikePids $virtual
            $m.before = Get-LabScreenLayout -Name $VmName -Pids $script:spikePids
            $m.down2 = Set-Count 2
            $m.after = Get-LabScreenLayout -Name $VmName -Pids $script:spikePids
            $m.down1 = Set-Count 1
            Stop-Apps $script:spikePids
            $m.gone = @($m.down2.layout.monitors | ForEach-Object device)
            Save 'monitors' $m
        }

        It 'D-APPSPIKE-01 the driver adds and removes monitors one at a time' -Tag 'D-APPSPIKE-01' {
            foreach ($s in $m.up2, $m.up3, $m.down2, $m.down1) {
                $s.got | Should -Be $s.want -Because "count $($s.virtual): $($s.got) monitors, not $($s.want) (way: $($s.how); $($s.said))"
            }
        }

        # by place, not by name: the driver's reload names every monitor anew (DISPLAY8 comes back as DISPLAY11)
        It 'D-APPSPIKE-02 windows on the monitors that stay keep their place when another monitor goes' -Tag 'D-APPSPIKE-02' {
            function Place($mon) { "$($mon.left),$($mon.top),$($mon.right),$($mon.bottom)" }
            $stayed = @($m.after.monitors | ForEach-Object { Place $_ })
            $checked = 0
            foreach ($w in @($m.before.windows)) {
                $was = @($m.before.monitors | Where-Object device -eq $w.monitor)[0]
                if (-not $was -or (Place $was) -notin $stayed) { continue }
                $a = @($m.after.windows | Where-Object pid -eq $w.pid) | Select-Object -First 1
                $a | Should -Not -BeNullOrEmpty
                "$($a.left),$($a.top)" | Should -Be "$($w.left),$($w.top)" -Because "pid $($w.pid) was on the monitor at $(Place $was), which stayed"
                $checked++
            }
            $checked | Should -BeGreaterThan 0 -Because 'at least one window was on a monitor that stayed'
        }
    }

    Context 'a mode per monitor' {
        BeforeAll {
            $d = [ordered]@{}
            $d.three = Set-Count 3 @('1280x720', '1600x900', '1024x768', '1366x768')
            $virtual = @($d.three.layout.monitors | Where-Object { -not $_.primary } | Select-Object -Last 2 | ForEach-Object device)
            $setMode = @'
if (-not ('RhSpikeMode' -as [type])) {
    Add-Type @"
using System; using System.Runtime.InteropServices;
public static class RhSpikeMode {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public short SpecVersion, DriverVersion, Size, DriverExtra; public int Fields;
        public int X, Y, Orientation, FixedOutput; public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName; public short LogPixels; public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
    // CDS_UPDATEREGISTRY: a CDS_FULLSCREEN mode is undone when the process that set it ends (seen in the first run);
    // the agent's helper lives on, but this stand-in doesn't. Answers the result and the size read back.
    public static string Set(string device, int w, int h) {
        var dm = new DEVMODE(); dm.Size = (short)Marshal.SizeOf(typeof(DEVMODE));
        if (!EnumDisplaySettings(device, -1, ref dm)) return "no mode";
        dm.PelsWidth = w; dm.PelsHeight = h; dm.Fields = 0x80000 | 0x100000;
        var r = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, 0x1, IntPtr.Zero);
        var now = new DEVMODE(); now.Size = dm.Size;
        EnumDisplaySettings(device, -1, ref now);
        return r + " " + now.PelsWidth + "x" + now.PelsHeight;
    }
}
"@
}
@($A.sets | ForEach-Object { [pscustomobject]@{ device = $_.device; size = "$($_.w)x$($_.h)"; result = [RhSpikeMode]::Set($_.device, [int]$_.w, [int]$_.h) } })
'@
            $d.set = Invoke-LabInSessionJson -Name $VmName -Code $setMode -Data @{ sets = @(@{ device = $virtual[0]; w = 1600; h = 900 }, @{ device = $virtual[1]; w = 1024; h = 768 }) }
            Start-Sleep -Seconds 3
            $d.afterSet = Get-LabScreenLayout -Name $VmName
            # a size that isn't in the list yet: refused before the reload, taken after it
            $d.unlisted = Invoke-LabInSessionJson -Name $VmName -Code $setMode -Data @{ sets = @(@{ device = $virtual[0]; w = 1500; h = 800 }) }
            $d.reload = Set-Vdd 3 @('1280x720', '1600x900', '1024x768', '1366x768', '1500x800')
            $d.afterReload = Wait-Monitors $d.three.want 40
            # in the third run the device failed (problem 43) on this reload, with the two monitors in their own modes
            $d.deviceAfterReload = Get-VddState
            $d.repair = Repair-Vdd
            if ($d.repair -ne 'ok') { $d.afterReload = Wait-Monitors $d.three.want 60 }
            # the reload names the monitors anew: the same place in the row, under its new name
            $d.virtualAfterReload = @($d.afterReload.monitors | Where-Object { -not $_.primary } | Select-Object -Last 2 | ForEach-Object device)
            $d.listed = Invoke-LabInSessionJson -Name $VmName -Code $setMode -Data @{ sets = @(@{ device = $d.virtualAfterReload[0]; w = 1500; h = 800 }) }
            Start-Sleep -Seconds 3
            $d.afterListed = Get-LabScreenLayout -Name $VmName
            $d.virtual = $virtual
            $d.device = Get-VddState
            Save 'modes' $d
        }

        It 'D-APPSPIKE-03 each virtual monitor runs its own mode; a size added later can be set after a reload' -Tag 'D-APPSPIKE-03' {
            $d.three.got | Should -Be $d.three.want
            function Size($layout, $dev) { $x = @($layout.monitors | Where-Object device -eq $dev)[0]; if ($x) { "$($x.right - $x.left)x$($x.bottom - $x.top)" } }
            Size $d.afterSet $d.virtual[0] | Should -Be '1600x900' -Because "ChangeDisplaySettingsEx answered $(@($d.set)[0].result)"
            Size $d.afterSet $d.virtual[1] | Should -Be '1024x768' -Because "ChangeDisplaySettingsEx answered $(@($d.set)[1].result)"
            $d.deviceAfterReload | Should -Match ' OK problem 0$' -Because "a reload while the monitors run their own modes keeps the driver up (restart: $($d.repair))"
            Size $d.afterListed $d.virtualAfterReload[0] | Should -Be '1500x800' -Because "before the reload: $(@($d.unlisted)[0].result), after: $(@($d.listed)[0].result) ($($d.reload); device $($d.device))"
        }
    }

    Context 'the most monitors' {
        BeforeAll {
            $repair = Repair-Vdd
            $n = Set-Count 8 @('1280x720', '1024x768')
            # back down in one step: in the second run the device was at problem 43 by the next context
            $back = Set-Count 3
            Save 'eight' ([ordered]@{ repair = $repair; up = $n; back = $back })
        }

        It 'D-APPSPIKE-04 the driver takes 8 monitors' -Tag 'D-APPSPIKE-04' {
            $n.got | Should -Be $n.want -Because "$($n.got - $script:physical) virtual monitors came up (way: $($n.how); $($n.said))"
            $back.got | Should -Be $back.want -Because "from 8 back to 3: $($back.got - $script:physical) virtual monitors (way: $($back.how); $($back.said); device $($back.device))"
        }
    }

    Context 'the taskbar on other monitors' {
        BeforeAll {
            $setTaskbar = @'
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name MMTaskbarEnabled -Value ([int]$A.v) -Type DWord
if (-not ('RhSpikeTray' -as [type])) {
    Add-Type @"
using System; using System.Runtime.InteropServices;
public static class RhSpikeTray {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
    public static void Changed() { IntPtr r; SendMessageTimeout((IntPtr)0xFFFF, 0x1A /* WM_SETTINGCHANGE */, IntPtr.Zero, "TraySettings", 0x2 /* ABORTIFHUNG */, 3000, out r); }
}
"@
}
[RhSpikeTray]::Changed()
'ok'
'@
            function Wait-Trays([scriptblock]$Until, [int]$TimeoutSec = 20) {
                $end = (Get-Date).AddSeconds($TimeoutSec)
                do { $l = Get-LabScreenLayout -Name $VmName; if (& $Until $l.trays) { return $l.trays }; Start-Sleep -Seconds 2 } while ((Get-Date) -lt $end)
                $l.trays
            }
            # other monitors to put a taskbar on, whatever the cases before left
            $t = [ordered]@{ original = $script:taskbar; repair = (Repair-Vdd) }
            $t.count = Set-Count 2
            $t.monitors = $t.count.got
            $null = Invoke-LabInSessionJson -Name $VmName -Code $setTaskbar -Data @{ v = 1 }
            $t.on = Wait-Trays { param($n) $n -gt 0 }
            $null = Invoke-LabInSessionJson -Name $VmName -Code $setTaskbar -Data @{ v = 0 }
            $t.off = Wait-Trays { param($n) $n -eq 0 }
            $null = Invoke-LabInSessionJson -Name $VmName -Code $setTaskbar -Data @{ v = 1 }
            $t.onAgain = Wait-Trays { param($n) $n -gt 0 }
            if ($t.original -ge 0) { $null = Invoke-LabInSessionJson -Name $VmName -Code $setTaskbar -Data @{ v = $t.original } }
            Save 'taskbar' $t
        }

        It 'D-APPSPIKE-05 MMTaskbarEnabled takes effect without restarting Explorer' -Tag 'D-APPSPIKE-05' {
            $t.on | Should -BeGreaterThan 0 -Because "with 1, the other $($t.monitors - 1) monitors get a taskbar"
            $t.off | Should -Be 0 -Because 'with 0 and WM_SETTINGCHANGE "TraySettings", the other taskbars go without restarting Explorer'
            $t.onAgain | Should -BeGreaterThan 0 -Because 'and come back with 1'
        }
    }

    Context 'the keyboard between two apps' {
        BeforeAll {
            $f = [ordered]@{ repair = (Repair-Vdd) }
            $f.count = (Set-Count 2).got
            $pids = Start-Apps @('255,0,0', '0,255,0') 'fg'
            $layout = Get-LabScreenLayout -Name $VmName -Pids $pids
            $virtual = @($layout.monitors | Where-Object { -not $_.primary } | Select-Object -Last 2 | ForEach-Object device)
            $null = Move-Apps $pids $virtual
            $layout = Get-LabScreenLayout -Name $VmName -Pids $pids
            $a = @($layout.windows | Where-Object pid -eq $pids[0])[0]
            $b = @($layout.windows | Where-Object pid -eq $pids[1])[0]
            # the stand-in for the helper: it clicks into A (SendInput), then gives the foreground to B and types
            $f.standIn = Invoke-LabInSessionJson -Name $VmName -Data @{ ax = [int](($a.left + $a.right) / 2); ay = [int](($a.top + $a.bottom) / 2); a = $a.hwnd; b = $b.hwnd } -Code @'
if (-not ('RhSpikeInput' -as [type])) {
    Add-Type @"
using System; using System.Runtime.InteropServices;
public static class RhSpikeInput {
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int X, Y, Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public short Vk, Scan; public int Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] struct INPUT { [FieldOffset(0)] public int Type; [FieldOffset(8)] public MOUSEINPUT M; [FieldOffset(8)] public KEYBDINPUT K; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    public static void Click(int x, int y) {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        var i = new INPUT[3];
        for (var k = 0; k < 3; k++) { i[k].Type = 0; i[k].M.X = (x - vx) * 65535 / (vw - 1); i[k].M.Y = (y - vy) * 65535 / (vh - 1); }
        i[0].M.Flags = 0x8000 | 0x4000 | 0x1; i[1].M.Flags = 0x8000 | 0x4000 | 0x2; i[2].M.Flags = 0x8000 | 0x4000 | 0x4;
        SendInput(3, i, Marshal.SizeOf(typeof(INPUT)));
    }
    public static void Type(string s) {
        foreach (var c in s) {
            var i = new INPUT[2];
            i[0].Type = 1; i[0].K.Scan = (short)c; i[0].K.Flags = 0x4;
            i[1].Type = 1; i[1].K.Scan = (short)c; i[1].K.Flags = 0x4 | 0x2;
            SendInput(2, i, Marshal.SizeOf(typeof(INPUT)));
        }
    }
}
"@
}
# the click lands in A's window, not on its text box (that is at the top left)
[RhSpikeInput]::Click([int]$A.ax, [int]$A.ay)
Start-Sleep -Milliseconds 500
$fgA = [RhSpikeInput]::GetForegroundWindow().ToInt64() -eq [long]$A.a
$set = [RhSpikeInput]::SetForegroundWindow([IntPtr][long]$A.b)
Start-Sleep -Milliseconds 500
$fgB = [RhSpikeInput]::GetForegroundWindow().ToInt64() -eq [long]$A.b
[RhSpikeInput]::Type('spike')
Start-Sleep -Milliseconds 800
[pscustomobject]@{ foregroundA = $fgA; setB = $set; foregroundB = $fgB }
'@
            # control, recorded only: a new process that sent no input asks for A - Windows may refuse (the foreground lock)
            $f.control = try {
                Invoke-LabInSessionJson -Name $VmName -Data @{ a = $a.hwnd } -Code @'
Add-Type -Namespace RhSpike -Name Fg -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();'
$set = [RhSpike.Fg]::SetForegroundWindow([IntPtr][long]$A.a)
Start-Sleep -Milliseconds 500
[pscustomobject]@{ setA = $set; foregroundA = [RhSpike.Fg]::GetForegroundWindow().ToInt64() -eq [long]$A.a }
'@
            }
            catch { "$_" }
            $f.logA = @(Get-LabTestAppLog -Name $VmName -Path 'C:\ProgramData\RemoteHubLab\session\appspike-fg-255-0-0.jsonl')
            $f.logB = @(Get-LabTestAppLog -Name $VmName -Path 'C:\ProgramData\RemoteHubLab\session\appspike-fg-0-255-0.jsonl')
            Stop-Apps $pids
            Save 'foreground' $f
        }

        It 'D-APPSPIKE-06 the process that sent the last input gives the foreground to another app, and typing reaches it' -Tag 'D-APPSPIKE-06' {
            $f.standIn.foregroundA | Should -BeTrue -Because 'the click brought A to the front'
            $f.standIn.setB | Should -BeTrue -Because 'SetForegroundWindow(B) from the process that sent the last input'
            $f.standIn.foregroundB | Should -BeTrue
            (@($f.logB | Where-Object e -eq 'text') | Select-Object -Last 1).text | Should -Be 'spike' -Because 'the typing went to B'
            @($f.logA | Where-Object e -eq 'text') | Should -BeNullOrEmpty -Because 'nothing was typed into A'
        }
    }

    Context 'two connections to one session' {
        BeforeAll {
            $c = [ordered]@{}
            $o = Out 'second'
            $holdOut = Join-Path $EvidenceDir 'd-appspike-holder'
            $c.second = Invoke-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -TimeoutSec 600 -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; monitor = 1 }, @{ do = 'wait'; state = 'Connected'; timeout = 60 }
                @{ do = 'frame'; out = 'second-while-holder.png' }
                @{ do = 'markFile'; path = (Join-Path $holdOut 'second-up.txt') }
                @{ do = 'waitFile'; path = (Join-Path $holdOut 'holder-pictured.txt'); timeout = 120 }
                @{ do = 'markFile'; path = (Join-Path $holdOut 'go.txt') }
                @{ do = 'sleep'; seconds = 10 }
                @{ do = 'frame'; out = 'second-after-holder.png' }, @{ do = 'close' })
            $c.holder = Receive-LabDesktop $holder
            Save 'two' $c
        }

        It 'D-APPSPIKE-07 two connections to one session both get pictures; one ending leaves the other' -Tag 'D-APPSPIKE-07' {
            ($c.second.statuses | Where-Object State -eq 'Connected') | Should -Not -BeNullOrEmpty -Because "the second connection: $(@($c.second.statuses | ForEach-Object { "$($_.state): $($_.message)" }) -join ' / ')"
            $steps = @($c.second.steps)
            ($steps | Where-Object { $_.do -eq 'frame' })[0].ok | Should -BeTrue -Because 'the second connection gets a picture while the first is up'
            ($steps | Where-Object { $_.do -eq 'frame' })[1].ok | Should -BeTrue -Because 'and still after the first ended'
            (@($c.holder.steps) | Where-Object { $_.do -eq 'frame' })[0].ok | Should -BeTrue -Because 'the first connection gets a picture while the second is up'
            $c.holder.closed.by | Should -Be 'close' -Because "the first connection ended only when it was closed, not by the second one ($($c.holder.closed.why))"
        }
    }
}
