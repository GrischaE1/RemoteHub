# D-VID / D-FIT / D-MON / D-VD - the picture: frame rates and quality (asked, limited by policy, on a slow link), only
# changes sent, key frames, how it captures, a dark display, the resolution fitted to RemoteHub's window and restored,
# monitors, and the virtual monitor (auto / always / off, reference-counted, resized). The console account connects to
# its own session (no question). Stats arrive every 2 s; the device side is read over PowerShell Direct.
# See docs/test-lab-cases.md section 9.8.
# Frames come only when the picture changes: the pointer goes as its own message, so mouse moves alone change nothing.
# The rate cases put a window in the console session that changes on every timer tick (Start-Change; -Noise for the
# slow link: random pixels the encoder can't shrink, so 2 Mbit/s is really too little) and record how often it changed.
# The console's resolution is read from Hyper-V's video head (Get-LabResolution): a PowerShell Direct session sees its
# own (1024x768) screen, not the console's. With the virtual monitor on, the primary monitor comes from the agent's
# Layout (the head shows only the Hyper-V display).
# Known product bugs:
# - D-VD-05 - after a restart of the device the switched-off virtual display can't be switched on again: pnputil
#   /enable-device answers 1167 "not connected" (VirtualDisplay.cs:170, 205-209). The virtual display context powers
#   the VM off and on (Set-VMVideo needs it off), so D-VD-01, D-VD-03 and D-VD-04 fail until it's fixed; the
#   -Because shows the agent's "virtual monitor ..." log line.
# D-VID-05 failed in run 20261019-193627 (quality 80 in every stat): the step down counted blocked loops (equal to the
# captures on a saturated link) and the next frame put the viewer's quality back. Fixed: the agent steps down when a
# quarter of the time goes to waiting for acks and keeps the lowered quality (DesktopHelperSession.AdaptedQuality).
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "d-vid-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "d-vid-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Session([string]$Name, [object[]]$Steps, [hashtable]$Start = @{}) {
        $s = @{ do = 'start'; session = 'console'; consent = 'auto' }; foreach ($k in $Start.Keys) { $s[$k] = $Start[$k] }
        Invoke-LabDesktop -Name $VmName -Pin $pin -Account Console -Out (Out $Name) -Steps (@(@{ do = 'connect' }, $s, @{ do = 'wait'; anyOf = @('Connected', 'Failed'); timeout = 60 }) + $Steps + @(@{ do = 'close' }))
    }
    function LastStats($r) { @($r.stats)[-1].stats }
    # the primary monitor in the agent's last Layout before the harness wrote the mark file $Mark
    function PrimaryAt($r, [string]$Mark) {
        $at = @($r.steps | Where-Object { $_.do -eq 'markFile' -and "$($_.detail.path)" -like "*\$Mark" })[0].atMs
        $l = @($r.layouts | Where-Object { $_.ms -le $at })
        if ($l.Count -eq 0) { return $null }
        $p = @($l[-1].layout.monitors | Where-Object { $_.primary })[0]
        if ($p) { "$($p.width)x$($p.height)" }
    }
    # a window in the console session that keeps changing (the module's Start-LabChangingWindow; -Noise random pixels)
    function Start-Change([switch]$Noise) { Start-LabChangingWindow -Name $VmName -Noise:$Noise }
    # ends the window; returns how often it changed: ticks, ms and changes per second
    function Stop-Change($c) { Stop-LabChangingWindow -Change $c }
    function Fps($r) { @($r.stats | ForEach-Object { $_.stats.framesPerSecond }) }
    function Get-Vdd { Invoke-LabGuest -Name $VmName -ScriptBlock { $d = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { @($_.HardwareID) -contains 'Root\MttVDD' } | Select-Object -First 1; if ($d) { [string]$d.Status } } }
    function Set-AgentJsonValue([string]$Key, $Value) { Set-LabAgentJsonValue -Name $VmName -Pin $pin -Key $Key -Value $Value }

    $init = Initialize-LabVm -Name $VmName -Agent
    $install = $init.Install
    $pin = $init.Pin
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-vid-agent-logs')
    # agent.json, the virtual display driver disabled, the Hyper-V video stopped / set / reverted: not for the next file
    Set-LabVmDirty -Name $VmName
}

Describe 'D-VID <VmName>' -Tag Desktop, 'D-VID', 'D-FIT', 'D-MON', 'D-VD' {

    Context 'frame rate, quality, changes, key frames, capture' {
        BeforeAll {
            $v = [ordered]@{}
            # 12 s with the picture changing all the time (6 stats): what the agent sends is limited by the asked rate
            # and the encoder, not by how often something happened to change
            foreach ($fps in 15, 30, 60) {
                $c = Start-Change
                try { $v["fps$fps"] = Session "fps$fps" @(@{ do = 'sleep'; seconds = 12 }) @{ fps = $fps } }
                finally { $v["change$fps"] = Stop-Change $c }
            }
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ MaxFramesPerSecond = 10; MaxQuality = 40 }
            $c = Start-Change
            try { $v.limited = Session 'limited' @(@{ do = 'settings'; fps = 60; quality = 100 }, @{ do = 'sleep'; seconds = 10 }) @{ fps = 60; quality = 100 } }
            finally { $v.changeLimited = Stop-Change $c }
            Set-LabAgentPolicy -Name $VmName -Clear
            # still: only the first frames and the refinement; then a key frame on request; then typing changes the picture
            $v.still = Session 'still' @(@{ do = 'sleep'; seconds = 15 }, @{ do = 'keyframe' }, @{ do = 'sleep'; seconds = 2 })
            $v.secure = Session 'secure' @(@{ do = 'cad' }, @{ do = 'sleep'; seconds = 6 }, @{ do = 'key'; scan = '0x01' }, @{ do = 'key'; scan = '0x01'; flags = @('Up') }, @{ do = 'sleep'; seconds = 3 })
            Save 'rates' $v
        }

        It 'D-VID-01 15 / 30 / 60 fps asked: the stats stay at or below; quality defaults to 60' -Tag 'D-VID-01' {
            foreach ($fps in 15, 30, 60) {
                $s = @($v["fps$fps"].stats | ForEach-Object { $_.stats.framesPerSecond })
                $s.Count | Should -BeGreaterThan 0 -Because "$fps fps"
                ($s | Measure-Object -Maximum).Maximum | Should -BeLessOrEqual ($fps + 1) -Because "$fps fps"
            }
            (LastStats $v.fps30).quality | Should -BeLessOrEqual 60
        }

        It 'D-VID-02 MaxFramesPerSecond=10, MaxQuality=40: Hello limits, Start and Settings clamped' -Tag 'D-VID-02' {
            $v.limited.hello.maxFramesPerSecond | Should -Be 10
            $v.limited.hello.maxQuality | Should -Be 40
            @($v.limited.stats | ForEach-Object { $_.stats.framesPerSecond } | Measure-Object -Maximum).Maximum | Should -BeLessOrEqual 11
            @($v.limited.stats | ForEach-Object { $_.stats.quality } | Measure-Object -Maximum).Maximum | Should -BeLessOrEqual 40
        }

        It 'D-VID-03 a still screen sends (almost) no frames after the first ones; moving sends frames' -Tag 'D-VID-03' {
            $still = @($v.still.stats | Select-Object -Skip 3 | ForEach-Object { $_.stats.framesPerSecond })
            ($still | Measure-Object -Average).Average | Should -BeLessThan 3
            # the stimulus first: a guest too starved to change its window 10 times a second says nothing about the agent
            $v.change30.perSecond | Should -BeGreaterThan 10 -Because "the window in the console session must change faster than the frames asked for (it changed $($v.change30.perSecond) times a second)"
            $s = Fps $v.fps30
            ($s | Measure-Object -Maximum).Maximum | Should -BeGreaterThan 5 -Because "the picture changed $($v.change30.perSecond) times a second at 30 fps asked (stats fps: $($s -join ', '); $($v.fps30.frames.count) frames; capture: $((LastStats $v.fps30).capture))"
        }

        It 'D-VID-04 a key frame request is answered with a key frame' -Tag 'D-VID-04' {
            $v.still.frames.keyFrames | Should -BeGreaterOrEqual 2
        }

        It 'D-VID-06 the stats name the capture: Desktop Duplication or GDI; the secure desktop is always GDI' -Tag 'D-VID-06' {
            (LastStats $v.fps30).capture | Should -Match 'Desktop Duplication|GDI'
            @($v.secure.stats | Where-Object { $_.stats.capture -match 'GDI' }).Count | Should -BeGreaterThan 0
        }

        It 'D-VID-07 60 fps on a VM without a GPU encoder: software encoding, no error' -Tag 'D-VID-07' {
            @($v.fps60.statuses | Where-Object state -in 'Failed', 'Ended') | Should -BeNullOrEmpty
            (LastStats $v.fps60).capture | Should -Not -Match 'GPU encoder'
            $v.change60.perSecond | Should -BeGreaterThan 10 -Because "the window in the console session must keep changing (it changed $($v.change60.perSecond) times a second)"
            # 12 s of change: more than 20 frames is under 2 a second - only a stalled software encoder misses it
            $v.fps60.frames.count | Should -BeGreaterThan 20 -Because "the picture changed $($v.change60.perSecond) times a second for 12 s at 60 fps asked (stats fps: $((Fps $v.fps60) -join ', '))"
        }

        It 'D-VID-08 "Smooth" on a GPU-P VM uses the GPU encoder' -Tag 'D-VID-08' {
            Set-ItResult -Skipped -Because 'needs a GPU-P VM'
        }

        It 'D-VID-09 an N edition without H.264: a clear "Media Feature Pack" message, event 3100, no crash' -Tag 'D-VID-09' {
            Set-ItResult -Skipped -Because 'needs the N edition VM'
        }
    }

    Context 'slow link' {
        BeforeAll {
            # random pixels changing all the time: at quality 80 far more than 2 Mbit/s (a frame of noise is ~100 KB or
            # more), so the acks really lag; 30 s = 15 adaptation steps of 2 s
            $c = Start-Change -Noise
            try {
                Set-VMNetworkAdapter -VMName $vm.Name -MaximumBandwidth 2000000
                try { $slow = Session 'slow' @(@{ do = 'sleep'; seconds = 30 }) @{ fps = 30; quality = 80 } }
                finally { Set-VMNetworkAdapter -VMName $vm.Name -MaximumBandwidth 0 }
            }
            finally { $slowChange = Stop-Change $c }
            Save 'slow' $slow
            Save 'slow-change' $slowChange
        }

        It 'D-VID-05 on a 2 Mbit/s link the quality steps down and pictures still come' -Tag 'D-VID-05' {
            $q = @($slow.stats | ForEach-Object { $_.stats.quality })
            $kbits = [math]::Round($slow.frames.bytes * 8 / 1000 / 30)
            $slowChange.perSecond | Should -BeGreaterThan 10 -Because "the noise window must keep changing (it changed $($slowChange.perSecond) times a second)"
            ($q | Measure-Object -Minimum).Minimum | Should -BeLessThan 80 -Because "the link was loaded (~$kbits kbit/s of video arrived, $($slow.frames.count) frames; stats fps: $((Fps $slow) -join ', '); quality: $($q -join ', ')) - see the D-VID-05 note in the header"
            $slow.frames.count | Should -BeGreaterThan 20
        }

        It 'D-VID-14 on a 2 Mbit/s link the frame rate goes down only once the quality is at the readable level (50)' -Tag 'D-VID-14' {
            # quality 80 -> 70 -> 60 -> 50 (three steps of 2 s), then the frame rate: within the 30 s it is lowered
            $s = @($slow.stats | ForEach-Object { $_.stats })
            $limited = @($s | Where-Object { $_.frameRateLimit -gt 0 })
            $limited.Count | Should -BeGreaterThan 0 -Because "the stats say when the device lowered the frame rate (quality: $(($s | ForEach-Object quality) -join ', '); limits: $(($s | ForEach-Object frameRateLimit) -join ', '))"
            @($limited | Where-Object { $_.quality -gt 50 }) | Should -BeNullOrEmpty -Because 'the picture stays readable (quality 50) before it moves less often'
            @($limited | Where-Object { $_.frameRateLimit -ge 30 -or $_.frameRateLimit -lt 5 }) | Should -BeNullOrEmpty -Because 'between 5 fps and less than the 30 asked for'
        }
    }

    Context 'dark display' {
        BeforeAll {
            Invoke-LabGuest -Name $VmName -ScriptBlock { powercfg.exe /change monitor-timeout-ac 1; powercfg.exe /change monitor-timeout-dc 1 }
            Start-Sleep -Seconds 90
            $dark = Session 'dark' @(@{ do = 'frame'; out = 'woken.png'; timeout = 8 })
            Invoke-LabGuest -Name $VmName -ScriptBlock { powercfg.exe /change monitor-timeout-ac 0 }
            Save 'dark' $dark
        }

        It 'D-VID-10 the display off by the power plan: the agent wakes it, a picture within ~6 s' -Tag 'D-VID-10' {
            $f = @($dark.steps | Where-Object do -eq 'frame')[0]
            $f.ok | Should -BeTrue
            $f.seconds | Should -BeLessOrEqual 8
            @($dark.images)[0].meanLuma | Should -BeGreaterThan 5
        }
    }

    Context 'fit to the window' {
        BeforeAll {
            $f = [ordered]@{}
            $f.before = Get-LabResolution -Name $VmName
            $o = Out 'fit'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; state = 'Connected'; timeout = 60 }
                @{ do = 'settings'; fitWidth = 1600; fitHeight = 900 }, @{ do = 'sleep'; seconds = 8 }, @{ do = 'markFile'; path = (Join-Path $o 'fitted.txt') }
                @{ do = 'waitFile'; path = (Join-Path $o 'go.txt'); timeout = 60 }, @{ do = 'settings'; fitWidth = 1234; fitHeight = 777 }, @{ do = 'sleep'; seconds = 8 }, @{ do = 'close' })
            Wait-LabHostFile (Join-Path $o 'fitted.txt')
            $f.during = Get-LabResolution -Name $VmName
            Set-Content -Encoding ASCII (Join-Path $o 'go.txt') 'go'
            $f.r = Receive-LabDesktop $job
            $f.after = Wait-LabResolution -Name $VmName -Is $f.before -TimeoutSec 15 -NoThrow
            # the helper killed while fitted: restored all the same
            $o = Out 'fit-kill'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; state = 'Connected'; timeout = 60 }
                @{ do = 'settings'; fitWidth = 1280; fitHeight = 720 }, @{ do = 'sleep'; seconds = 8 }, @{ do = 'markFile'; path = (Join-Path $o 'fitted.txt') }
                @{ do = 'waitFile'; path = (Join-Path $o 'go.txt'); timeout = 60 }, @{ do = 'close' })
            Wait-LabHostFile (Join-Path $o 'fitted.txt')
            $f.killFitted = Get-LabResolution -Name $VmName
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-CimInstance Win32_Process -Filter "Name='RemoteHubAgent.exe'" | Where-Object CommandLine -match '\sdesktop\s*$' | ForEach-Object { Stop-Process -Id $_.ProcessId -Force } }
            # Windows undoes the temporary mode with the helper; the service starts the helper again and sends it the
            # Fit again: fitted again while connected (run 20261016-212514: back to the old size and fitted again within
            # half a second, ~3 s after the kill - the poll may miss the moment in between, so it's only recorded)
            $f.reverted = Wait-LabResolution -Name $VmName -Not $f.killFitted -TimeoutSec 10 -NoThrow
            $f.afterKill = Wait-LabResolution -Name $VmName -Is $f.killFitted -TimeoutSec 30 -NoThrow
            Set-Content -Encoding ASCII (Join-Path $o 'go.txt') 'go'
            $f.rKill = Receive-LabDesktop $job
            $f.afterKillClose = Wait-LabResolution -Name $VmName -Is $f.before -TimeoutSec 15 -NoThrow
            Save 'fit' $f
        }

        It 'D-FIT-01 fit 1600x900: the guest runs 1600x900 (or the nearest mode); restored after the disconnect' -Tag 'D-FIT-01' {
            $f.during | Should -Not -Be $f.before -Because "the agent said: $(@($f.r.stats | ForEach-Object { $_.stats.fit } | Where-Object { $_ } | Select-Object -First 1))"
            $f.during | Should -Match '^1[56]\d\dx\d{3}$'
            $f.after | Should -Be $f.before
        }

        It 'D-FIT-02 1234x777 (no such mode): a mode of the same shape or the nearest, the stats say which' -Tag 'D-FIT-02' {
            @($f.r.stats | Where-Object { $_.stats.fit -match '1234' }).Count | Should -BeGreaterThan 0
        }

        It 'D-FIT-03 the helper killed while fitted: fitted again while connected, restored when the session ends' -Tag 'D-FIT-03' {
            $f.killFitted | Should -Not -Be $f.before -Because "the agent said: $(@($f.rKill.stats | ForEach-Object { $_.stats.fit } | Where-Object { $_ } | Select-Object -First 1))"
            $f.afterKill | Should -Be $f.killFitted
            $f.afterKillClose | Should -Be $f.before
        }
    }

    Context 'monitors' {
        BeforeAll {
            $m = [ordered]@{}
            Set-AgentJsonValue 'virtualDisplay' 'always'
            $o = Out 'monitors'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; state = 'Connected'; timeout = 60 }
                # "always" switches the virtual monitor on once a Fit size is sent
                @{ do = 'settings'; fitWidth = 1280; fitHeight = 720 }, @{ do = 'sleep'; seconds = 8 }, @{ do = 'frame'; out = 'mon0.png' }, @{ do = 'settings'; monitor = 1 }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'mon1.png' }
                @{ do = 'settings'; monitor = -1 }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'all.png' }
                @{ do = 'markFile'; path = (Join-Path $o 'all.txt') }, @{ do = 'waitFile'; path = (Join-Path $o 'go.txt'); timeout = 120 }, @{ do = 'sleep'; seconds = 6 }, @{ do = 'close' })
            Wait-LabHostFile (Join-Path $o 'all.txt') -TimeoutSec 180
            # the virtual monitor taken away while connected
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-PnpDevice | Where-Object { @($_.HardwareID) -contains 'Root\MttVDD' } | Disable-PnpDevice -Confirm:$false }
            Set-Content -Encoding ASCII (Join-Path $o 'go.txt') 'go'
            $m.r = Receive-LabDesktop $job
            $m.similar = Compare-LabImage -A (Join-Path $o 'mon0.png') -B (Join-Path $o 'mon1.png')
            Set-AgentJsonValue 'virtualDisplay' 'auto'
            Save 'monitors' $m
        }

        It 'D-MON-01 two monitors (virtual display "always"): Layout lists 2, primary first; another monitor gives another picture' -Tag 'D-MON-01' {
            # the first Layout comes before the virtual monitor exists: the first one with two monitors
            $l = @($m.r.layouts | Where-Object { @($_.layout.monitors).Count -ge 2 })[0].layout
            @($l.monitors).Count | Should -Be 2
            $l.monitors[0].primary | Should -BeTrue
            $m.similar | Should -BeLessThan 0.95
        }

        It 'D-MON-02 all monitors (-1): the picture is the bounding box of both' -Tag 'D-MON-02' {
            $all = @($m.r.layouts | Where-Object { $_.layout.monitor -eq -1 })[0].layout
            $all | Should -Not -BeNullOrEmpty
            $all.width | Should -BeGreaterThan ((@($m.r.layouts | Where-Object { @($_.layout.monitors).Count -ge 2 })[0].layout.monitors | Measure-Object -Property width -Maximum).Maximum)
        }

        It 'D-MON-03 a monitor removed while connected: Layout sent again, the index clamped, no crash' -Tag 'D-MON-03' {
            $last = @($m.r.layouts)[-1].layout
            @($last.monitors).Count | Should -Be 1
            $last.monitor | Should -BeIn 0, -1
            @($m.r.statuses | Where-Object state -in 'Failed', 'Ended') | Should -BeNullOrEmpty
        }
    }

    Context 'virtual display' {
        BeforeAll {
            $d = [ordered]@{}
            # a guest whose display offers less than the window (Hyper-V video set before the start). Shut down, not
            # turned off: agent.json was written just before, and a power cut right after left it zero-filled - the agent
            # then didn't start at all (run 20261017-160133, product finding A-CFG-JSON)
            Stop-VM -Name $vm.Name -Force
            # "auto" asks for the virtual monitor when the display offers nothing as large as the window (capped at
            # 1280x720): a Hyper-V display limited to 1024x768
            Set-VMVideo -VMName $vm.Name -ResolutionType Maximum -HorizontalResolution 1024 -VerticalResolution 768
            Start-VM -Name $vm.Name
            Wait-LabVm -Name $vm.Name
            $null = Wait-LabAgent -Name $VmName -Pin $pin
            $two = {
                param($name)
                $o = Out $name
                Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps @(
                    @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; state = 'Connected'; timeout = 60 }
                    @{ do = 'settings'; fitWidth = 1600; fitHeight = 900 }, @{ do = 'sleep'; seconds = 8 }, @{ do = 'markFile'; path = (Join-Path $o 'on.txt') }
                    @{ do = 'waitFile'; path = (Join-Path $o 'resize.txt'); timeout = 180 }, @{ do = 'settings'; fitWidth = 1280; fitHeight = 720 }, @{ do = 'sleep'; seconds = 6 }
                    @{ do = 'markFile'; path = (Join-Path $o 'resized.txt') }, @{ do = 'waitFile'; path = (Join-Path $o 'go.txt'); timeout = 180 }, @{ do = 'close' })
            }
            # (the power cycle above is a restart after the install: see D-VD-05 in the header)
            $since = Get-LabGuestTime -Name $VmName
            $a = & $two 'vd-a'
            Wait-LabHostFile (Join-Path (Out 'vd-a') 'on.txt')
            $d.onA = Get-Vdd
            # the agent's own word on switching it on ("virtual monitor on at ..." or "... not switched on: <why>")
            $d.vdLog = @(Get-LabAgentLog -Name $VmName -Since $since | ForEach-Object { "$_" } | Where-Object { $_ -match 'virtual monitor' })
            $d.device = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $dev = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { @($_.HardwareID) -contains 'Root\MttVDD' } | Select-Object -First 1
                [ordered]@{
                    status  = if ($dev) { [string]$dev.Status } else { $null }
                    problem = if ($dev) { (Get-PnpDeviceProperty -InstanceId $dev.InstanceId -KeyName DEVPKEY_Device_ProblemCode -ErrorAction SilentlyContinue).Data } else { $null }
                }
            }
            Set-Content -Encoding ASCII (Join-Path (Out 'vd-a') 'resize.txt') 'go'
            Wait-LabHostFile (Join-Path (Out 'vd-a') 'resized.txt')
            $b = & $two 'vd-b'
            Wait-LabHostFile (Join-Path (Out 'vd-b') 'on.txt')
            # A connection's end is done with the virtual display once the agent logs what it did ("stays on for N other
            # connection(s)", "Virtual display off ...", "... not switched off ...", "... left on ..."); switching the
            # device off can take a while (pnputil, tries again), so the state is read then, not after a fixed wait.
            # Returns the lines and the seconds it took; none within the limit means the agent never said.
            $vdEnd = {
                param([datetime]$From, [string]$Pattern = 'Virtual display (stays on|off|not switched off|left on)', [int]$TimeoutSec = 120)
                $until = (Get-Date).AddSeconds($TimeoutSec)
                $watch = [Diagnostics.Stopwatch]::StartNew()
                $seen = @()
                do {
                    $seen = @(Get-LabAgentLog -Name $VmName -Since $From | ForEach-Object { "$_" } |
                            Where-Object { $_ -match $Pattern })
                    if ($seen) { break }
                    Start-Sleep -Seconds 2
                } while ((Get-Date) -lt $until)
                [pscustomobject]@{ Lines = $seen; Seconds = [int]$watch.Elapsed.TotalSeconds }
            }
            $endA = Get-LabGuestTime -Name $VmName
            Set-Content -Encoding ASCII (Join-Path (Out 'vd-a') 'go.txt') 'go'
            $d.a = Receive-LabDesktop $a
            # the primary monitor as the agent's Layout had it when the harness marked "on" and "resized" (with the
            # virtual monitor on, the Hyper-V display isn't the primary one any more)
            $d.resA = PrimaryAt $d.a 'on.txt'
            $d.resized = PrimaryAt $d.a 'resized.txt'
            $d.endFirst = & $vdEnd $endA
            $d.afterFirst = Get-Vdd
            Set-Content -Encoding ASCII (Join-Path (Out 'vd-b') 'resize.txt') 'go'
            $endB = Get-LabGuestTime -Name $VmName
            Set-Content -Encoding ASCII (Join-Path (Out 'vd-b') 'go.txt') 'go'
            $d.b = Receive-LabDesktop $b
            # only a switch-off result: the first connection's "stays on" line can fall inside the window (-Since allows 1 s)
            $d.endLast = & $vdEnd $endB 'Virtual display (off|not switched off|left on)'
            $d.afterLast = Get-Vdd
            # off: never switched on
            Set-AgentJsonValue 'virtualDisplay' 'off'
            $null = Session 'vd-off' @(@{ do = 'settings'; fitWidth = 1600; fitHeight = 900 }, @{ do = 'sleep'; seconds = 8 })
            $d.off = Get-Vdd
            Save 'vd' $d
            # the logs before Reset-LabVm takes them away (AfterAll only finds the checkpoint's)
            Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-vid-vd-agent-logs')
            # back to the checkpoint's video settings (the next file reverts anyway)
            Stop-VM -Name $vm.Name -TurnOff -Force
            Set-VMVideo -VMName $vm.Name -ResolutionType Maximum -HorizontalResolution 1920 -VerticalResolution 1080
            Reset-LabVm -Name $VmName
        }

        It 'D-VD-01 "auto" on a guest whose display offers less than the window: the virtual monitor switched on; off after the disconnect' -Tag 'D-VD-01' {
            $d.onA | Should -Be 'OK' -Because "the virtual monitor should be on while connected (agent log: $(@($d.vdLog) -join ' / '); known bug D-VD-05 after the power cycle)"
            $d.resA | Should -Not -BeNullOrEmpty
            $d.resA | Should -Not -Be '1024x768'
            @($d.endLast.Lines) | Should -Not -BeNullOrEmpty -Because "the agent should say what it did with the virtual display within 120 s of the last connection's end"
            $d.afterLast | Should -Not -Be 'OK' -Because "the virtual display should be off after the last connection (agent, after $($d.endLast.Seconds) s: $(@($d.endLast.Lines) -join ' / '))"
        }

        It 'D-VD-02 "off": never switched on' -Tag 'D-VD-02' {
            $d.off | Should -Not -Be 'OK'
        }

        It 'D-VD-03 two connections: the virtual monitor stays until the last one ends' -Tag 'D-VD-03' {
            $d.onA | Should -Be 'OK' -Because "the virtual monitor must be on to begin with (agent log: $(@($d.vdLog) -join ' / '); known bug D-VD-05 after the power cycle)"
            $d.afterFirst | Should -Be 'OK' -Because "the second connection still needs it (agent: $(@($d.endFirst.Lines) -join ' / '))"
            $d.afterLast | Should -Not -Be 'OK' -Because "the virtual display should be off after the last connection (agent, after $($d.endLast.Seconds) s: $(@($d.endLast.Lines) -join ' / '))"
        }

        It 'D-VD-04 another window size: the driver takes the new size within 5 s' -Tag 'D-VD-04' {
            $d.onA | Should -Be 'OK' -Because "the new size goes to the virtual monitor, which must be on (agent log: $(@($d.vdLog) -join ' / '); known bug D-VD-05 after the power cycle)"
            $d.resized | Should -Be '1280x720'
        }
    }
}
