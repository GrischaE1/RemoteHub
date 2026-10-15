# D-VID / D-FIT / D-MON / D-VD - the picture: frame rates and quality (asked, limited by policy, on a slow link), only
# changes sent, key frames, how it captures, a dark display, the resolution fitted to RemoteHub's window and restored,
# monitors, and the virtual monitor (auto / always / off, reference-counted, resized). The console account connects to
# its own session (no question). Stats arrive every 2 s; the device side is read over PowerShell Direct.
# See docs/test-lab-cases.md section 9.8.
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
    function Resolution { Invoke-LabGuest -Name $VmName -ScriptBlock { Add-Type -AssemblyName System.Windows.Forms; $b = [Windows.Forms.Screen]::PrimaryScreen.Bounds; "$($b.Width)x$($b.Height)" } }
    function Get-Vdd { Invoke-LabGuest -Name $VmName -ScriptBlock { $d = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { @($_.HardwareID) -contains 'Root\MttVDD' } | Select-Object -First 1; if ($d) { [string]$d.Status } } }
    function Set-AgentJsonValue([string]$Key, $Value) {
        Invoke-LabGuest -Name $VmName -ArgumentList $Key, $Value -ScriptBlock {
            param($k, $v)
            $f = 'C:\ProgramData\RemoteHub\Agent\agent.json'
            $c = Get-Content $f -Raw | ConvertFrom-Json
            $c | Add-Member -NotePropertyName $k -NotePropertyValue $v -Force
            [IO.File]::WriteAllText($f, ($c | ConvertTo-Json -Depth 5))
        }
        $null = Restart-LabAgent -Name $VmName -Pin $pin
    }

    Reset-LabVm -Name $VmName
    $install = Install-LabAgent -Name $VmName
    $pin = $install.pin
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-vid-agent-logs')
}

Describe 'D-VID <VmName>' -Tag Desktop, 'D-VID', 'D-FIT', 'D-MON', 'D-VD' {

    Context 'frame rate, quality, changes, key frames, capture' {
        BeforeAll {
            $v = [ordered]@{}
            foreach ($fps in 15, 30, 60) { $v["fps$fps"] = Session "fps$fps" @(@{ do = 'moves'; count = 400; x0 = 100; y0 = 100; dx = 2; dy = 1; delayMs = 25 }, @{ do = 'sleep'; seconds = 3 }) @{ fps = $fps } }
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ MaxFramesPerSecond = 10; MaxQuality = 40 }
            $v.limited = Session 'limited' @(@{ do = 'settings'; fps = 60; quality = 100 }, @{ do = 'moves'; count = 300; x0 = 100; y0 = 100; dx = 2; dy = 1; delayMs = 25 }, @{ do = 'sleep'; seconds = 3 }) @{ fps = 60; quality = 100 }
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
            @($v.fps30.stats | ForEach-Object { $_.stats.framesPerSecond } | Measure-Object -Maximum).Maximum | Should -BeGreaterThan 5
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
            $v.fps60.frames.count | Should -BeGreaterThan 20
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
            Set-VMNetworkAdapter -VMName $vm.Name -MaximumBandwidth 2000000
            try { $slow = Session 'slow' @(@{ do = 'moves'; count = 1200; x0 = 50; y0 = 50; dx = 1; dy = 1; delayMs = 25 }, @{ do = 'sleep'; seconds = 4 }) @{ fps = 30; quality = 80 } }
            finally { Set-VMNetworkAdapter -VMName $vm.Name -MaximumBandwidth 0 }
            Save 'slow' $slow
        }

        It 'D-VID-05 on a 2 Mbit/s link the quality steps down and pictures still come' -Tag 'D-VID-05' {
            $q = @($slow.stats | ForEach-Object { $_.stats.quality })
            ($q | Measure-Object -Minimum).Minimum | Should -BeLessThan 80
            $slow.frames.count | Should -BeGreaterThan 20
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
            $f.before = Resolution
            $o = Out 'fit'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; state = 'Connected'; timeout = 60 }
                @{ do = 'settings'; fitWidth = 1600; fitHeight = 900 }, @{ do = 'sleep'; seconds = 8 }, @{ do = 'markFile'; path = (Join-Path $o 'fitted.txt') }
                @{ do = 'waitFile'; path = (Join-Path $o 'go.txt'); timeout = 60 }, @{ do = 'settings'; fitWidth = 1234; fitHeight = 777 }, @{ do = 'sleep'; seconds = 8 }, @{ do = 'close' })
            Wait-LabHostFile (Join-Path $o 'fitted.txt')
            $f.during = Resolution
            Set-Content (Join-Path $o 'go.txt') 'go'
            $f.r = Receive-LabDesktop $job
            Start-Sleep -Seconds 5
            $f.after = Resolution
            # the helper killed while fitted: restored all the same
            $o = Out 'fit-kill'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; state = 'Connected'; timeout = 60 }
                @{ do = 'settings'; fitWidth = 1280; fitHeight = 720 }, @{ do = 'sleep'; seconds = 8 }, @{ do = 'markFile'; path = (Join-Path $o 'fitted.txt') }
                @{ do = 'waitFile'; path = (Join-Path $o 'go.txt'); timeout = 60 }, @{ do = 'close' })
            Wait-LabHostFile (Join-Path $o 'fitted.txt')
            $f.killFitted = Resolution
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-CimInstance Win32_Process -Filter "Name='RemoteHubAgent.exe'" | Where-Object CommandLine -match '\sdesktop\s*$' | ForEach-Object { Stop-Process -Id $_.ProcessId -Force } }
            # the service starts the helper again and sends it the Fit again: fitted again while connected
            Start-Sleep -Seconds 15
            $f.afterKill = Resolution
            Set-Content (Join-Path $o 'go.txt') 'go'
            $null = Receive-LabDesktop $job
            Start-Sleep -Seconds 5
            $f.afterKillClose = Resolution
            Save 'fit' $f
        }

        It 'D-FIT-01 fit 1600x900: the guest runs 1600x900 (or the nearest mode); restored after the disconnect' -Tag 'D-FIT-01' {
            $f.during | Should -Not -Be $f.before
            $f.during | Should -Match '^1[56]\d\dx\d{3}$'
            $f.after | Should -Be $f.before
        }

        It 'D-FIT-02 1234x777 (no such mode): a mode of the same shape or the nearest, the stats say which' -Tag 'D-FIT-02' {
            @($f.r.stats | Where-Object { $_.stats.fit -match '1234' }).Count | Should -BeGreaterThan 0
        }

        It 'D-FIT-03 the helper killed while fitted: fitted again while connected, restored when the session ends' -Tag 'D-FIT-03' {
            $f.killFitted | Should -Not -Be $f.before
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
            Set-Content (Join-Path $o 'go.txt') 'go'
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
            # a guest whose display offers less than the window (Hyper-V video set before the start)
            Stop-VM -Name $vm.Name -TurnOff -Force
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
            $a = & $two 'vd-a'
            Wait-LabHostFile (Join-Path (Out 'vd-a') 'on.txt')
            $d.onA = Get-Vdd
            $d.resA = Resolution
            Set-Content (Join-Path (Out 'vd-a') 'resize.txt') 'go'
            Wait-LabHostFile (Join-Path (Out 'vd-a') 'resized.txt')
            $d.resized = Resolution
            $b = & $two 'vd-b'
            Wait-LabHostFile (Join-Path (Out 'vd-b') 'on.txt')
            Set-Content (Join-Path (Out 'vd-a') 'go.txt') 'go'
            $d.a = Receive-LabDesktop $a
            Start-Sleep -Seconds 5
            $d.afterFirst = Get-Vdd
            Set-Content (Join-Path (Out 'vd-b') 'resize.txt') 'go'; Set-Content (Join-Path (Out 'vd-b') 'go.txt') 'go'
            $d.b = Receive-LabDesktop $b
            Start-Sleep -Seconds 5
            $d.afterLast = Get-Vdd
            # off: never switched on
            Set-AgentJsonValue 'virtualDisplay' 'off'
            $null = Session 'vd-off' @(@{ do = 'settings'; fitWidth = 1600; fitHeight = 900 }, @{ do = 'sleep'; seconds = 8 })
            $d.off = Get-Vdd
            Save 'vd' $d
            # back to the checkpoint's video settings (the next file reverts anyway)
            Stop-VM -Name $vm.Name -TurnOff -Force
            Set-VMVideo -VMName $vm.Name -ResolutionType Maximum -HorizontalResolution 1920 -VerticalResolution 1080
            Reset-LabVm -Name $VmName
        }

        It 'D-VD-01 "auto" on a guest whose display offers less than the window: the virtual monitor switched on; off after the disconnect' -Tag 'D-VD-01' {
            $d.onA | Should -Be 'OK'
            $d.resA | Should -Not -Be '1024x768'
            $d.afterLast | Should -Not -Be 'OK'
        }

        It 'D-VD-02 "off": never switched on' -Tag 'D-VD-02' {
            $d.off | Should -Not -Be 'OK'
        }

        It 'D-VD-03 two connections: the virtual monitor stays until the last one ends' -Tag 'D-VD-03' {
            $d.afterFirst | Should -Be 'OK'
            $d.afterLast | Should -Not -Be 'OK'
        }

        It 'D-VD-04 another window size: the driver takes the new size within 5 s' -Tag 'D-VD-04' {
            $d.resized | Should -Be '1280x720'
        }
    }
}
