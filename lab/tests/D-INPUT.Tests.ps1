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
    # what the marker saw from the moment the session was connected.
    # -Focus: keys reach only the window with the keyboard focus. The consent dialog takes the foreground
    # (SetForegroundWindow) and when it closes Windows needn't give it back to the marker - in the run of 16.10.2026
    # the keys of three of four sessions reached Windows but no window. So a click on the marker comes first; Focused
    # says whether it arrived, and Seen then starts after it (its button events aren't the test's).
    function Send-Input([string]$Name, [object[]]$Steps, [switch]$NoClose, [switch]$Focus) {
        $marker = Start-LabMarker -Name $VmName
        $o = Out $Name
        $tail = if ($NoClose) { @() } else { @(@{ do = 'sleep'; seconds = 3 }, @{ do = 'close' }) }
        $click = if ($Focus) {
            @(@{ do = 'mouse'; action = 'Down'; button = 'Left'; x = 200; y = 200 }, @{ do = 'mouse'; action = 'Up'; button = 'Left'; x = 200; y = 200 }, @{ do = 'sleep'; seconds = 1 })
        }
        else { @() }
        $job = Start-LabDesktop -Name $VmName -Pin $pin -Out $o -Steps (@(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 90 }
                @{ do = 'markFile'; path = (Join-Path $o 'connected.txt') }, @{ do = 'waitFile'; path = (Join-Path $o 'go.txt'); timeout = 120 }) + $click + $Steps + $tail)
        $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
        Wait-LabHostFile (Join-Path $o 'connected.txt')
        Start-Sleep -Seconds 2
        $skip = @(Get-LabMarker -Name $VmName -Marker $marker).Count
        Set-Content (Join-Path $o 'go.txt') 'go'
        $r = Receive-LabDesktop $job
        Start-Sleep -Seconds 4
        $seen = @(Get-LabMarker -Name $VmName -Marker $marker | Select-Object -Skip $skip)
        Stop-LabInSession -Name $VmName -Task $marker.Task
        $focused = $false
        if ($Focus) {
            for ($i = 0; $i -lt $seen.Count; $i++) {
                if ($seen[$i].e -eq 'mup' -and $seen[$i].b -eq 'Left') { $focused = $true; $seen = @($seen | Select-Object -Skip ($i + 1)); break }
            }
        }
        [pscustomobject]@{ R = $r; Seen = $seen; Focused = $focused }
    }
    # The layout the session's programs start with (the marker's): the high word of the keyboard layout handle,
    # e.g. 0409 US, 0407 German - a VM in en-US can still have a German keyboard first.
    function Get-SessionLayout {
        $h = Invoke-LabInSession -Name $VmName -ScriptBlock { Add-Type -AssemblyName System.Windows.Forms; '{0:X8}' -f ([Windows.Forms.InputLanguage]::CurrentInputLanguage.Handle.ToInt64() -band 0xFFFFFFFF) }
        if ("$h" -match '([0-9A-F]{4})[0-9A-F]{4}') { $Matches[1] } else { "unknown ($("$h".Trim()))" }
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
            # the harness sends the scan codes of these characters on a US keyboard; the device's layout decides what
            # they are. No y or z in it: the expectations differ only in the symbols.
            $text = 'The quick brown fox 0123456789 !@#$%^&*()_+-=[]{};:,./<>?'
            $layout = Get-SessionLayout
            # German (0407): the same keys give !"S$%&/()=?`ss'u+U*oO,.-;:_ - S section sign, ss sharp s, ' acute,
            # u/U/o/O umlauts; ` and acute are dead keys, so each comes with the next key's character
            $expected = @{
                '0409' = $text
                '0407' = 'The quick brown fox 0123456789 !"' + [char]0xA7 + '$%&/()=?`' + [char]0xDF + [char]0xB4 + [char]0xFC + '+' + [char]0xDC + '*' + [char]0xF6 + [char]0xD6 + ',.-;:_'
            }
            $typed = Send-Input 'keys' @(@{ do = 'keys'; text = $text; delayMs = 20 }) -Focus
            # extended keys: Up, Down, Left, Right, Home, End, Delete, right Ctrl, numpad Enter
            $ext = foreach ($s in '0xE048', '0xE050', '0xE04B', '0xE04D', '0xE047', '0xE04F', '0xE053', '0xE01D', '0xE01C') {
                @{ do = 'key'; scan = $s }, @{ do = 'key'; scan = $s; flags = @('Up') }
            }
            $extended = Send-Input 'extended' @($ext | ForEach-Object { $_ }) -Focus
            Save 'keyboard' @{ Layout = $layout; Typed = $typed; Extended = $extended }
        }

        It 'D-IN-01 text typed by scan code arrives as on the device''s layout' -Tag 'D-IN-01' {
            if (-not $expected.ContainsKey($layout)) { Set-ItResult -Skipped -Because "the expectation is written for the US (0409) and German (0407) layouts; the session's is $layout"; return }
            $typed.Focused | Should -BeTrue -Because 'the click on the marker (for the keyboard focus) reached it'
            Chars $typed.Seen | Should -BeExactly $expected[$layout] -Because "layout $layout"
        }

        It 'D-IN-02 extended keys arrive: arrows, Home, End, Delete, right Ctrl, numpad Enter' -Tag 'D-IN-02' {
            $extended.Focused | Should -BeTrue -Because 'the click on the marker (for the keyboard focus) reached it'
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
                # a second above between the wheel turns: two arriving before the marker took the first are merged by
                # Windows into one message with the sum (both runs before 16.10.2026: one wheel of -120)
                @{ do = 'mouse'; action = 'Wheel'; x = 406; y = 306; delta = 120 }, @{ do = 'sleep'; seconds = 1 }, @{ do = 'mouse'; action = 'Wheel'; x = 406; y = 306; delta = -240 })
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
            # OCR may miss the label (runs 20261022-172139, 20261025-052539 read 'en:'); the buttons OK, Cancel, Browse... are Run's own
            $runShown.text | Should -Match 'Type the name of a program|Open:|Cancel\s+Browse'
        }
    }

    Context 'no stuck keys' {
        BeforeAll {
            $held = Send-Input 'held' @(@{ do = 'key'; scan = '0x2A' }, @{ do = 'mouse'; action = 'Down'; button = 'Left'; x = 500; y = 500 }, @{ do = 'sleep'; seconds = 1 }, @{ do = 'drop' }) -NoClose -Focus
            Save 'held' $held
        }

        It 'D-IN-05 a key and a button held when the connection drops are released' -Tag 'D-IN-05' {
            $held.Focused | Should -BeTrue -Because 'the click on the marker (for the keyboard focus) reached it'
            @($held.Seen | Where-Object { $_.e -eq 'down' -and $_.vk -eq 16 }).Count | Should -BeGreaterThan 0
            @($held.Seen | Where-Object { $_.e -eq 'up' -and $_.vk -eq 16 }).Count | Should -BeGreaterThan 0
            @($held.Seen | Where-Object { $_.e -eq 'mup' -and $_.b -eq 'Left' }).Count | Should -BeGreaterThan 0
        }
    }

    Context 'flood' {
        BeforeAll {
            $flood = Send-Input 'flood' @(@{ do = 'moves'; count = 10000; x0 = 10; y0 = 400; dx = 0; dy = 0 }, @{ do = 'keys'; text = 'after the flood' }, @{ do = 'sleep'; seconds = 3 }) -Focus
            $health = Get-LabAgentHealth -Name $VmName -Pin $pin
            Save 'flood' @{ Flood = $flood; Health = $health }
        }

        It 'D-IN-06 10,000 mouse moves: no crash, keys after it all arrive' -Tag 'D-IN-06' {
            $health.deviceId | Should -Be $install.deviceId
            $flood.Focused | Should -BeTrue -Because 'the click on the marker (for the keyboard focus) reached it'
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
