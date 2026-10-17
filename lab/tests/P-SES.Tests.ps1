# P-SES - RemoteHub's PowerShell connection through the agent (PsRemoting.Open with an AgentEndpoint, the same code the
# PowerShell tab uses): the session itself, what ending it does on the device, the session limit, tab completion.
# The device side is checked over PowerShell Direct. See docs/test-lab-cases.md section 7.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "p-ses-$name.json") -Encoding UTF8 }
    function Get-AgentEvents([int]$Id, [datetime]$Since) {
        Invoke-LabGuest -Name $VmName -ArgumentList $Id, $Since -ScriptBlock {
            param($Id, $Since)
            @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'RemoteHub Agent'; Id = $Id; StartTime = $Since } -ErrorAction SilentlyContinue |
                    ForEach-Object { [string]$_.Message })
        }
    }
}

Describe 'P-SES <VmName>' -Tag Agent, 'P-SES' {
    BeforeAll {
        $init = Initialize-LabVm -Name $VmName -Agent
        $install = $init.Install
        $pin = $init.Pin
        $guestNow = Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-Date).AddSeconds(-5) }
    }

    AfterAll {
        # the probe value P-SES-02 wrote; the processes it started are ended by the agent - the VM stays reusable
        Invoke-LabGuest -Name $VmName -ScriptBlock { Remove-Item HKCU:\Software\RemoteHubLab -Recurse -Force -ErrorAction SilentlyContinue; Get-Process notepad, ping -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
    }

    Context 'one session' {
        BeforeAll {
            $s = Invoke-LabTool -Name $VmName -Op ps -Pin $pin -Params @{ script = '$PSVersionTable.PSVersion.ToString(); whoami; $PID; $env:USERPROFILE; New-Item HKCU:\Software\RemoteHubLab -Force | Out-Null; Set-ItemProperty HKCU:\Software\RemoteHubLab -Name Probe -Value 7; (Get-ItemProperty HKCU:\Software\RemoteHubLab).Probe' }
            $out = @($s.result.output)
            Start-Sleep -Seconds 2
            $started = Get-AgentEvents 1000 $guestNow
            Save 'one-session' @{ Session = $s; Started = $started }
        }

        It 'P-SES-01 opens a session: Windows PowerShell 5.1 as PSAdmin, event 1000 with the PID' -Tag 'P-SES-01' {
            $s.refused | Should -BeNullOrEmpty
            @($s.result.errors).Count | Should -Be 0
            $out[0] | Should -Match '^5\.1\.'
            $out[1] | Should -Match "\\$([regex]::Escape($vm.AdminAccount))$"
            $sessionPid = $out[2]
            @($started | Where-Object { $_ -match "PowerShell PID $sessionPid\)" }).Count | Should -Be 1
        }

        It 'P-SES-02 runs in PSAdmin''s profile with a writable HKCU' -Tag 'P-SES-02' {
            $out[3] | Should -Match "\\Users\\$([regex]::Escape($vm.AdminAccount))"
            $out[4] | Should -Be '7'
        }
    }

    Context 'ending the session' {
        BeforeAll {
            $since = Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Date }
            $s = Invoke-LabTool -Name $VmName -Op ps -Pin $pin -Params @{ script = '$PID; (Start-Process ping.exe -ArgumentList ''-t'', ''127.0.0.1'' -PassThru -WindowStyle Hidden).Id; (Start-Process notepad.exe -PassThru).Id' }
            $ids = @($s.result.output | ForEach-Object { [int]$_ })
            # the harness closed the session when the call returned; the agent must end the whole tree
            Start-Sleep -Seconds 5
            $left = Invoke-LabGuest -Name $VmName -ArgumentList (, $ids) -ScriptBlock { param($ids) @(Get-Process -Id $ids -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name)[$($_.Id)]" }) }
            $ended = Get-AgentEvents 1001 $since
            Save 'end' @{ Session = $s; Left = $left; Ended = $ended }
        }

        It 'P-SES-03 ends powershell.exe and its children within 5 s, event 1001' -Tag 'P-SES-03' {
            $ids.Count | Should -Be 3
            @($left) | Should -BeNullOrEmpty -Because 'the session process and everything it started must be gone'
            @($ended | Where-Object { $_ -match 'Session ended' }).Count | Should -BeGreaterThan 0
        }
    }

    Context 'session limit' {
        BeforeAll {
            $hold = Invoke-LabProbe -Name $VmName -Kind hold -Params @{ pin = $pin; user = $vm.AdminAccount; password = (Get-LabConfig).Password; count = 21; seconds = 5 }
            $open = @($hold.results | Where-Object open)
            $refusedOne = @($hold.results | Where-Object { -not $_.open })
            # all closed again: a normal session must work
            Start-Sleep -Seconds 3
            $after = Invoke-LabTool -Name $VmName -Op ps -Pin $pin -Params @{ script = '$PID' }
            Save 'limit' @{ Hold = $hold; After = $after }
        }

        It 'P-SES-07 20 sessions at once work, the 21st gets 503' -Tag 'P-SES-07' {
            $open.Count | Should -Be 20
            $refusedOne.Count | Should -Be 1
            $refusedOne[0].status | Should -Be 503
            $after.refused | Should -BeNullOrEmpty
            @($after.result.output).Count | Should -Be 1
        }
    }

    Context 'tab completion' {
        BeforeAll {
            $cmd = Invoke-LabTool -Name $VmName -Op complete -Pin $pin -Params @{ line = 'Get-Ser' }
            $par = Invoke-LabTool -Name $VmName -Op complete -Pin $pin -Params @{ line = 'Get-Service -Na' }
            $path = Invoke-LabTool -Name $VmName -Op complete -Pin $pin -Params @{ line = 'Get-ChildItem C:\Win' }
            Save 'complete' @{ Command = $cmd; Parameter = $par; Path = $path }
        }

        It 'P-SES-09 completes commands, parameters and paths on the device' -Tag 'P-SES-09' {
            @($cmd.result.matches) | Should -Contain 'Get-Service'
            @($par.result.matches) | Should -Contain '-Name'
            @($path.result.matches) | Should -Contain 'C:\Windows'
        }
    }
}
