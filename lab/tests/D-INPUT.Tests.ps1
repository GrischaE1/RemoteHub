# D-IN - keyboard and mouse through the agent's remote desktop: what RemoteHub sends (scan codes, mouse at picture
# coordinates) and what arrives in normaluser's session, recorded by the marker app (RhPerson.ps1 -Do marker: a
# full-screen, DPI-aware window logging keys, characters, mouse and wheel). See docs/test-lab-cases.md section 9.7.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "d-in-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "d-in-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    # a session normaluser allows, running -Steps once connected, with the marker recording; answers the session and
    # what the marker saw from the moment the session was connected
    function Send-Input([string]$Name, [object[]]$Steps, [switch]$NoClose) {
        $marker = Start-LabMarker -Name $VmName
        $o = Out $Name
        $tail = if ($NoClose) { @() } else { @(@{ do = 'sleep'; seconds = 3 }, @{ do = 'close' }) }
        $job = Start-LabDesktop -Name $VmName -Pin $pin -Out $o -Steps (@(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 90 }
                @{ do = 'markFile'; path = (Join-Path $o 'connected.txt') }, @{ do = 'waitFile'; path = (Join-Path $o 'go.txt'); timeout = 120 }) + $Steps + $tail)
        $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
        Wait-LabHostFile (Join-Path $o 'connected.txt')
        Start-Sleep -Seconds 2
        $skip = @(Get-LabMarker -Name $VmName -Marker $marker).Count
        Set-Content (Join-Path $o 'go.txt') 'go'
        $r = Receive-LabDesktop $job
        Start-Sleep -Seconds 4
        $seen = @(Get-LabMarker -Name $VmName -Marker $marker | Select-Object -Skip $skip)
        Stop-LabInSession -Name $VmName -Task $marker.Task
        [pscustomobject]@{ R = $r; Seen = $seen }
    }
    function Chars($seen) { -join @($seen | Where-Object e -eq 'char' | ForEach-Object c) }
    function Near($a, $b) { [math]::Abs($a - $b) -le 2 }

    $init = Initialize-LabVm -Name $VmName -Agent -AutoLogon User
    $install = $init.Install
    $pin = $init.Pin
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-in-agent-logs')
    # normaluser's desktop at 150 % (DPI context, restarted): not for the next file
    Set-LabVmDirty -Name $VmName
}

Describe 'D-IN <VmName>' -Tag Desktop, 'D-IN' {

    Context 'keyboard' {
        BeforeAll {
            $text = 'The quick brown fox 0123456789 !@#$%^&*()_+-=[]{};:,./<>?'
            $typed = Send-Input 'keys' @(@{ do = 'keys'; text = $text; delayMs = 20 })
            # extended keys: Up, Down, Left, Right, Home, End, Delete, right Ctrl, numpad Enter
            $ext = foreach ($s in '0xE048', '0xE050', '0xE04B', '0xE04D', '0xE047', '0xE04F', '0xE053', '0xE01D', '0xE01C') {
                @{ do = 'key'; scan = $s }, @{ do = 'key'; scan = $s; flags = @('Up') }
            }
            $extended = Send-Input 'extended' @($ext | ForEach-Object { $_ })
            Save 'keyboard' @{ Typed = $typed; Extended = $extended }
        }

        It 'D-IN-01 text typed by scan code arrives as on the device''s layout' -Tag 'D-IN-01' {
            if ($vm.Language -ne 'en-US') { Set-ItResult -Skipped -Because "the expectation is written for en-US; $($vm.Language) needs its own (z/y, umlauts, AltGr)"; return }
            Chars $typed.Seen | Should -BeExactly $text
        }

        It 'D-IN-02 extended keys arrive: arrows, Home, End, Delete, right Ctrl, numpad Enter' -Tag 'D-IN-02' {
            $vks = @($extended.Seen | Where-Object e -eq 'down' | ForEach-Object vk)
            foreach ($vk in 38, 40, 37, 39, 36, 35, 46, 17, 13) { $vks | Should -Contain $vk -Because "virtual key $vk" }
        }
    }

    Context 'mouse' {
        BeforeAll {
            $steps = @(
                @{ do = 'mouse'; action = 'Move'; x = 400; y = 300 }, @{ do = 'sleep'; seconds = 1 }
                @{ do = 'mouse'; action = 'Down'; button = 'Left'; x = 401; y = 301 }, @{ do = 'mouse'; action = 'Up'; button = 'Left'; x = 401; y = 301 }
                @{ do = 'mouse'; action = 'Down'; button = 'Right'; x = 402; y = 302 }, @{ do = 'mouse'; action = 'Up'; button = 'Right'; x = 402; y = 302 }
                @{ do = 'key'; scan = '0x01' }, @{ do = 'key'; scan = '0x01'; flags = @('Up') }
                @{ do = 'mouse'; action = 'Down'; button = 'Middle'; x = 403; y = 303 }, @{ do = 'mouse'; action = 'Up'; button = 'Middle'; x = 403; y = 303 }
                @{ do = 'mouse'; action = 'Down'; button = 'X1'; x = 404; y = 304 }, @{ do = 'mouse'; action = 'Up'; button = 'X1'; x = 404; y = 304 }
                @{ do = 'mouse'; action = 'Down'; button = 'X2'; x = 405; y = 305 }, @{ do = 'mouse'; action = 'Up'; button = 'X2'; x = 405; y = 305 }
                @{ do = 'mouse'; action = 'Wheel'; x = 406; y = 306; delta = 120 }, @{ do = 'mouse'; action = 'Wheel'; x = 406; y = 306; delta = -240 })
            $m = Send-Input 'mouse' $steps
            Save 'mouse' $m
        }

        It 'D-IN-03 move, left / right / middle / X1 / X2, wheel arrive at the right place (+-2 px)' -Tag 'D-IN-03' {
            $downs = @($m.Seen | Where-Object e -eq 'mdown')
            $expect = @{ Left = 401; Right = 402; Middle = 403; XButton1 = 404; XButton2 = 405 }
            foreach ($b in $expect.Keys) {
                $d = @($downs | Where-Object b -eq $b)
                $d.Count | Should -BeGreaterThan 0 -Because $b
                Near $d[0].x $expect[$b] | Should -BeTrue -Because "$b x $($d[0].x)"
                Near $d[0].y ($expect[$b] - 100) | Should -BeTrue -Because "$b y $($d[0].y)"
            }
            @($m.Seen | Where-Object { $_.e -eq 'wheel' } | ForEach-Object d) | Should -Be @(120, -240)
        }
    }

    Context 'shortcuts' {
        BeforeAll {
            $win = Send-Input 'win-r' @(@{ do = 'combo'; scans = @('0xE05B', '0x13') }, @{ do = 'sleep'; seconds = 3 }) -NoClose
            $runShown = Invoke-LabPerson -Name $VmName -Do screen-read
            Save 'shortcuts' @{ Win = $win; Screen = $runShown }
        }

        It 'D-IN-04 Win+R sent through the protocol opens Run on the device' -Tag 'D-IN-04' {
            $runShown.text | Should -Match 'Type the name of a program|Open:'
        }
    }

    Context 'no stuck keys' {
        BeforeAll {
            $held = Send-Input 'held' @(@{ do = 'key'; scan = '0x2A' }, @{ do = 'mouse'; action = 'Down'; button = 'Left'; x = 500; y = 500 }, @{ do = 'sleep'; seconds = 1 }, @{ do = 'drop' }) -NoClose
            Save 'held' $held
        }

        It 'D-IN-05 a key and a button held when the connection drops are released' -Tag 'D-IN-05' {
            @($held.Seen | Where-Object { $_.e -eq 'down' -and $_.vk -eq 16 }).Count | Should -BeGreaterThan 0
            @($held.Seen | Where-Object { $_.e -eq 'up' -and $_.vk -eq 16 }).Count | Should -BeGreaterThan 0
            @($held.Seen | Where-Object { $_.e -eq 'mup' -and $_.b -eq 'Left' }).Count | Should -BeGreaterThan 0
        }
    }

    Context 'flood' {
        BeforeAll {
            $flood = Send-Input 'flood' @(@{ do = 'moves'; count = 10000; x0 = 10; y0 = 400; dx = 0; dy = 0 }, @{ do = 'keys'; text = 'after the flood' }, @{ do = 'sleep'; seconds = 3 })
            $health = Get-LabAgentHealth -Name $VmName -Pin $pin
            Save 'flood' @{ Flood = $flood; Health = $health }
        }

        It 'D-IN-06 10,000 mouse moves: no crash, keys after it all arrive' -Tag 'D-IN-06' {
            $health.deviceId | Should -Be $install.deviceId
            Chars $flood.Seen | Should -BeExactly 'after the flood'
        }
    }

    Context 'DPI 150 %' {
        BeforeAll {
            Invoke-LabGuest -Name $VmName -ArgumentList $vm.UserAccount -ScriptBlock {
                param($u)
                $sid = (New-Object Security.Principal.NTAccount $u).Translate([Security.Principal.SecurityIdentifier]).Value
                $k = "Registry::HKEY_USERS\$sid\Control Panel\Desktop"
                Set-ItemProperty $k -Name LogPixels -Value 144 -Type DWord
                Set-ItemProperty $k -Name Win8DpiScaling -Value 1 -Type DWord
            }
            Restart-LabVm -Name $VmName
            Wait-LabDesktop -Name $VmName -Account User
            $null = Wait-LabAgent -Name $VmName -Pin $pin
            $dpi = Send-Input 'dpi' @(@{ do = 'mouse'; action = 'Move'; x = 600; y = 450 }, @{ do = 'mouse'; action = 'Down'; button = 'Left'; x = 600; y = 450 }, @{ do = 'mouse'; action = 'Up'; button = 'Left'; x = 600; y = 450 })
            $scale = Invoke-LabInSession -Name $VmName -ScriptBlock { Add-Type -AssemblyName System.Drawing; $g = [Drawing.Graphics]::FromHwnd([IntPtr]::Zero); $g.DpiX }
            Save 'dpi' @{ Dpi = $dpi; Scale = $scale }
        }

        It 'D-IN-07 on a 150 % desktop the coordinates are still exact' -Tag 'D-IN-07' {
            $d = @($dpi.Seen | Where-Object { $_.e -eq 'mdown' -and $_.b -eq 'Left' })
            $d.Count | Should -BeGreaterThan 0
            Near $d[0].x 600 | Should -BeTrue -Because "x $($d[0].x)"
            Near $d[0].y 450 | Should -BeTrue -Because "y $($d[0].y)"
        }
    }
}
