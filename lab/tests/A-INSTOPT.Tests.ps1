# A-INST (options) / A-ROLLBACK / A-UNINST-11,14 - the first install beyond the defaults A-INST.Tests.ps1 checks:
# service recovery and reboot, no extracted runtime, the virtual display (on, off, publisher already trusted), the
# Ctrl+Alt+Del setting, selftest, firewall scope (any / list), another port, groups as typed in the dialog, a custom
# group, a label with umlauts, a port already in use (the install rolls back) and a broken enrollment (RemoteHub
# shows the msiexec log lines). Every context reverts its VM. See docs/test-lab-cases.md section 5.1.
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $password = (Get-LabConfig).Password
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "a-instopt-$name.json") -Encoding UTF8 }
    function Ws([string]$User, [string]$Pin, [int]$Port = 58732) {
        (Invoke-LabProbe -Name $VmName -Kind ws -Port $Port -Params @{ pin = $Pin; user = $User; password = $password }).results[0]
    }
    # VirtualDisplay.cs: the signer of the driver package and the shipped files
    $signPath = '3CF8CF26D8BA266C3A483AB7D26D4A818E317D76'
    $vddFiles = @{
        'MttVDD.inf'             = '550D211FE481E74DFE3F9D724ED78BE48B3A9113405965D683D9373E8D672F5D'
        'MttVDD.dll'             = 'C9CA837F57A98FBD43BC416A7F535A95843626E7759EAF85CF0CD7CE334DBB05'
        'mttvdd.cat'             = '08A0093FC9B2E32B287A6F8A77CA4DE0A31830D29FC33D2B13A918DC859468F6'
        'SignPathFoundation.cer' = '69DFD3E1CBD49F039463EDFE4C0D876C1DC7FDCD3AEF08700A41355DC6C15FDB'
    }
    # what the device has of the virtual display: device, driver package, trust, state file, settings folder, files
    function Get-Vdd {
        Invoke-LabGuest -Name $VmName -ArgumentList $signPath -ScriptBlock {
            param($signPath)
            $dev = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { @($_.HardwareID) -contains 'Root\MttVDD' })
            $drivers = @(Get-WindowsDriver -Online -ErrorAction SilentlyContinue | Where-Object { $_.OriginalFileName -like '*\mttvdd.inf' })
            $state = 'C:\ProgramData\RemoteHub\Agent\virtual-display.txt'
            $folder = 'C:\Program Files\RemoteHub Agent\VirtualDisplay'
            [pscustomobject]@{
                Devices   = @($dev | ForEach-Object { [pscustomobject]@{ Id = $_.InstanceId; Status = [string]$_.Status; Problem = [string]$_.Problem } })
                Drivers   = @($drivers | ForEach-Object Driver)
                Trusted   = [bool](Get-ChildItem Cert:\LocalMachine\TrustedPublisher | Where-Object Thumbprint -eq $signPath)
                State     = $(if (Test-Path $state) { Get-Content $state -Raw } else { $null })
                Settings  = Test-Path 'C:\VirtualDisplayDriver'
                FileHashes = @(if (Test-Path $folder) { Get-ChildItem $folder -File | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Sha = (Get-FileHash $_.FullName -Algorithm SHA256).Hash } } })
            }
        }
    }
    function Wait-Health([string]$Pin, [int]$Seconds, [int]$Port = 58732) {
        $until = (Get-Date).AddSeconds($Seconds)
        do {
            try { return Get-LabAgentHealth -Name $VmName -Pin $Pin -Port $Port } catch { Start-Sleep -Seconds 5 }
        } while ((Get-Date) -lt $until)
        $null
    }
}

Describe 'A-INSTOPT <VmName>' -Tag Agent, 'A-INST', 'A-ROLLBACK', 'A-UNINST' {

    Context 'default install: service, runtime, virtual display, Ctrl+Alt+Del, selftest, reboot, uninstall' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $clean = Get-LabAgentState -Name $VmName
            $install = Install-LabAgent -Name $VmName
            $pin = $install.pin
            $state = Get-LabAgentState -Name $VmName
            $vdd = Get-Vdd
            $guest = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $exe = 'C:\Program Files\RemoteHub Agent\RemoteHubAgent.exe'
                $certsBefore = @(Get-ChildItem Cert:\LocalMachine\My).Count
                $logsBefore = @(Get-ChildItem 'C:\ProgramData\RemoteHub\Agent\logs' -Filter 'agent-*.log' | Get-Content).Count
                $selftest = @(& $exe selftest 2>&1 | ForEach-Object { [string]$_ })
                $selftestExit = $LASTEXITCODE
                [pscustomobject]@{
                    QFailure     = (sc.exe qfailure RemoteHubAgent) -join "`n"
                    Selftest     = $selftest; SelftestExit = $selftestExit
                    CertsChanged = (@(Get-ChildItem Cert:\LocalMachine\My).Count -ne $certsBefore)
                    LogsAdded    = @(Get-ChildItem 'C:\ProgramData\RemoteHub\Agent\logs' -Filter 'agent-*.log' | Get-Content).Count - $logsBefore
                    DotNet       = @('C:\Windows\Temp\.net', 'C:\Windows\SystemTemp\.net', 'C:\Windows\System32\config\systemprofile\AppData\Local\Temp\.net' | Where-Object { Test-Path $_ })
                    SasMarker    = Test-Path 'C:\ProgramData\RemoteHub\Agent\sas.agent'
                    SignPathCer  = [Convert]::ToBase64String([IO.File]::ReadAllBytes('C:\Program Files\RemoteHub Agent\VirtualDisplay\SignPathFoundation.cer'))
                }
            }
            # the publisher certificate as shipped, for "trust already present" below
            Set-Content (Join-Path $EvidenceDir 'signpath.b64') -Value $guest.SignPathCer -Encoding ASCII

            # kill the process: the service recovery restarts it
            $killed = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $old = (Get-CimInstance Win32_Service -Filter "Name='RemoteHubAgent'").ProcessId
                Stop-Process -Id $old -Force
                $sw = [Diagnostics.Stopwatch]::StartNew()
                do {
                    Start-Sleep -Seconds 1
                    $s = Get-CimInstance Win32_Service -Filter "Name='RemoteHubAgent'"
                } while (($s.State -ne 'Running' -or $s.ProcessId -eq $old) -and $sw.Elapsed.TotalSeconds -lt 45)
                [pscustomobject]@{ Old = $old; New = $s.ProcessId; State = $s.State; Seconds = [math]::Round($sw.Elapsed.TotalSeconds) }
            }

            # reboot: delayed start, the same certificate answers within 3 minutes
            Restart-LabVm -Name $VmName
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $afterReboot = Wait-Health $pin 180
            $rebootSeconds = [math]::Round($sw.Elapsed.TotalSeconds)

            # uninstall: everything of the virtual display goes, the trust too (the agent added it)
            $uninstall = Uninstall-LabAgent -Name $VmName
            $vddAfter = Get-Vdd
            # reinstall: a new enrollment works, nothing doubled
            $reinstall = Install-LabAgent -Name $VmName
            $reinstalled = Get-LabAgentState -Name $VmName
            Save 'default' @{ Clean = $clean; Install = $install; State = $state; Vdd = $vdd; Guest = $guest; Killed = $killed; AfterReboot = $afterReboot
                RebootSeconds = $rebootSeconds; Uninstall = $uninstall; VddAfter = $vddAfter; Reinstall = $reinstall; Reinstalled = $reinstalled }
        }
        AfterAll { Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'a-instopt-default-logs') }

        It 'A-INST-10 sets restart x3 after 10 s, reset after a day; a killed agent is back within 30 s' -Tag 'A-INST-10' {
            [regex]::Matches($guest.QFailure, 'RESTART -- Delay = 10000').Count | Should -Be 3
            $guest.QFailure | Should -Match 'RESET_PERIOD \(in seconds\)\s*:\s*86400'
            $killed.State | Should -Be 'Running'
            $killed.New | Should -Not -Be $killed.Old
            $killed.Seconds | Should -BeLessOrEqual 30
        }

        It 'A-INST-11 answers again within 3 minutes after a reboot, with the same certificate' -Tag 'A-INST-11' {
            $afterReboot | Should -Not -BeNullOrEmpty
            $afterReboot.deviceId | Should -Be $install.deviceId
            $rebootSeconds | Should -BeLessOrEqual 180
        }

        It 'A-INST-12 extracts no runtime: no .net folder in the system temp folders' -Tag 'A-INST-12' {
            @($guest.DotNet) | Should -BeNullOrEmpty
        }

        It 'A-INST-20 installs the virtual display disabled, files as shipped, SignPath trusted and recorded as added' -Tag 'A-INST-20' {
            $vdd.Devices.Count | Should -Be 1
            $vdd.Devices[0].Status | Should -Not -Be 'OK' -Because 'switched off until a connection needs it'
            $vdd.Drivers.Count | Should -BeGreaterOrEqual 1
            foreach ($f in $vddFiles.Keys) { ($vdd.FileHashes | Where-Object Name -eq $f).Sha | Should -Be $vddFiles[$f] -Because $f }
            $vdd.Trusted | Should -BeTrue
            $vdd.State | Should -Match 'trustAdded=1'
        }

        It 'A-INST-22 sets SoftwareSASGeneration = 1 and the marker when no policy set it before' -Tag 'A-INST-22' {
            if ($null -ne $clean.SoftwareSas) { Set-ItResult -Skipped -Because "the checkpoint already has SoftwareSASGeneration = $($clean.SoftwareSas)"; return }
            $state.SoftwareSas | Should -Be 1
            $guest.SasMarker | Should -BeTrue
        }

        It 'A-INST-24 selftest passes on the device and changes nothing (no certificate, no log line)' -Tag 'A-INST-24' {
            $guest.SelftestExit | Should -Be 0
            ($guest.Selftest -join "`n") | Should -Match 'Self-test passed\.'
            $guest.CertsChanged | Should -BeFalse
            $guest.LogsAdded | Should -Be 0
        }

        It 'A-UNINST-11 removes the virtual display: device, driver package, settings, state file and the trust it added' -Tag 'A-UNINST-11' {
            $uninstall.ExitCode | Should -BeIn 0, 3010
            @($vddAfter.Devices) | Should -BeNullOrEmpty
            @($vddAfter.Drivers) | Should -BeNullOrEmpty
            $vddAfter.Settings | Should -BeFalse
            $vddAfter.State | Should -BeNullOrEmpty
            $vddAfter.Trusted | Should -BeFalse
        }

        It 'A-UNINST-14 a reinstall after the uninstall enrolls anew: one certificate, one rule' -Tag 'A-UNINST-14' {
            $reinstall.installed.fingerprint | Should -Be $reinstall.pin
            $reinstall.deviceId | Should -Not -Be $install.deviceId
            @($reinstalled.OurCertificates).Count | Should -Be 1
            $reinstalled.Firewall.Count | Should -Be 1
            (Get-LabAgentHealth -Name $VmName -Pin $reinstall.pin).deviceId | Should -Be $reinstall.deviceId
        }
    }

    Context 'no scope, port 58800, label with umlauts, groups as typed' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $label = "B$([char]0xFC)ro PC 7"
            $install = Install-LabAgent -Name $VmName -Scope '' -Port 58800 -Label $label -Groups 'Administrators'
            $state = Get-LabAgentState -Name $VmName
            $listen = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $agent = (Get-CimInstance Win32_Service -Filter "Name='RemoteHubAgent'").ProcessId
                [pscustomobject]@{
                    Agent = @(Get-NetTCPConnection -State Listen -OwningProcess $agent -ErrorAction SilentlyContinue | ForEach-Object LocalPort | Sort-Object -Unique)
                    Old   = @(Get-NetTCPConnection -State Listen -LocalPort 58732 -ErrorAction SilentlyContinue).Count
                }
            }
            $health = Get-LabAgentHealth -Name $VmName -Pin $install.pin -Port 58800
            Save 'options' @{ Install = $install; State = $state; Listen = $listen; Health = $health }
        }

        It 'A-INST-13 an empty scope makes the rule allow any address' -Tag 'A-INST-13' {
            @($state.Firewall.RemoteAddress) | Should -Be @('Any')
        }

        It 'A-INST-15 port 58800: listens there, rule and health on 58800, nothing on 58732' -Tag 'A-INST-15' {
            $install.installed.port | Should -Be 58800
            @($listen.Agent) | Should -Contain 58800
            @($listen.Agent) | Should -Not -Contain 58732
            $listen.Old | Should -Be 0
            @($state.Firewall.LocalPort) | Should -Be @('58800')
            $health.deviceId | Should -Be $install.deviceId
        }

        It 'A-INST-16 "Administrators" as typed is stored as S-1-5-32-544' -Tag 'A-INST-16' {
            @($state.Config.allowedGroups) | Should -Be @('S-1-5-32-544')
        }

        It 'A-INST-18 a label with spaces and umlauts enrolls and is kept' -Tag 'A-INST-18' {
            $state.Config.deviceLabel | Should -Be $label
        }
    }

    Context 'firewall scope list' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $hostIp = Get-LabHostAddress -Name $VmName
            $install = Install-LabAgent -Name $VmName -Scope "10.0.0.0/8, 192.168.5.10-192.168.5.20, $hostIp"
            $state = Get-LabAgentState -Name $VmName
            $health = Get-LabAgentHealth -Name $VmName -Pin $install.pin
            Save 'scope-list' @{ Install = $install; State = $state }
        }

        It 'A-INST-14 the rule has exactly the listed addresses and the host still connects' -Tag 'A-INST-14' {
            $ra = @($state.Firewall.RemoteAddress | Sort-Object)
            $ra.Count | Should -Be 3
            $ra | Should -Contain $hostIp
            $ra | Should -Contain '10.0.0.0/255.0.0.0'
            $ra | Should -Contain '192.168.5.10-192.168.5.20'
            $health.deviceId | Should -Be $install.deviceId
        }
    }

    Context 'custom allowed group' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $null = New-LabGuestUser -Name $VmName -User $vm.UserAccount -Group 'RH Operators'
            $install = Install-LabAgent -Name $VmName -Groups 'RH Operators'
            $state = Get-LabAgentState -Name $VmName
            $user = Ws $vm.UserAccount $install.pin
            $admin = Ws $vm.AdminAccount $install.pin
            Save 'custom-group' @{ Install = $install; State = $state; User = $user; Admin = $admin }
        }

        It 'A-INST-17 a member of "RH Operators" signs in, PSAdmin (not a member) gets 403' -Tag 'A-INST-17' {
            @($state.Config.allowedGroups) | Should -Be @('RH Operators')
            $user.status | Should -Be 101
            $admin.status | Should -Be 403
        }
    }

    Context 'virtual display off' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName -VirtualDisplay $false
            $vdd = Get-Vdd
            Save 'vdd-off' @{ Install = $install; Vdd = $vdd }
        }

        It 'A-INST-19 VIRTUALDISPLAY=0: no device, no driver package, no SignPath trust' -Tag 'A-INST-19' {
            @($vdd.Devices) | Should -BeNullOrEmpty
            @($vdd.Drivers) | Should -BeNullOrEmpty
            $vdd.Trusted | Should -BeFalse
            $vdd.State | Should -BeNullOrEmpty
        }
    }

    Context 'publisher already trusted' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $cerFile = Join-Path $EvidenceDir 'signpath.b64'
            if (-not (Test-Path $cerFile)) { throw 'The SignPath certificate was not read in the default context.' }
            Invoke-LabGuest -Name $VmName -ArgumentList (Get-Content $cerFile -Raw).Trim() -ScriptBlock {
                param($b64)
                $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String($b64))
                $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPublisher', 'LocalMachine')
                $store.Open('ReadWrite'); try { $store.Add($cert) } finally { $store.Close() }
            }
            $install = Install-LabAgent -Name $VmName
            $vdd = Get-Vdd
            $uninstall = Uninstall-LabAgent -Name $VmName
            $vddAfter = Get-Vdd
            Save 'trust-present' @{ Install = $install; Vdd = $vdd; Uninstall = $uninstall; VddAfter = $vddAfter }
        }

        It 'A-INST-21 trust already present: recorded as not added, and the uninstall leaves it' -Tag 'A-INST-21' {
            $vdd.State | Should -Match 'trustAdded=0'
            $uninstall.ExitCode | Should -BeIn 0, 3010
            $vddAfter.Trusted | Should -BeTrue
        }
    }

    Context 'port already in use' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            # a listener outside the PowerShell Direct session (it would end with it)
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $cmd = 'powershell.exe -NoProfile -Command "$l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Any, 58732); $l.Start(); Start-Sleep 3600"'
                $null = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd }
                $until = (Get-Date).AddSeconds(30)
                while (-not (Get-NetTCPConnection -State Listen -LocalPort 58732 -ErrorAction SilentlyContinue) -and (Get-Date) -lt $until) { Start-Sleep 1 }
            }
            $failure = $null
            try { $install = Install-LabAgent -Name $VmName } catch { $failure = $_.Exception.Message }
            $state = Get-LabAgentState -Name $VmName
            Save 'port-in-use' @{ Failure = $failure; State = $state }
        }

        It 'A-INST-25 a port in use fails the install cleanly, the log says the agent could not start, no other port taken' -Tag 'A-INST-25' {
            # an ordinary listener (IPv4, shared use allowed) - what another program on the device typically has
            $failure | Should -Not -BeNullOrEmpty -Because 'the agent must not run on a port another program listens on'
            $failure | Should -Match 'installer failed'
            ($state.LogLines -join "`n") | Should -Match 'could not start'
            @($state.Processes) | Should -BeNullOrEmpty
        }
    }

    Context 'install fails after the enrollment' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            # a copy of the package with a step right after AgentEnroll that fails: the first install rolls back
            $broken = New-LabBrokenMsi -Source (Get-LabAgentMsi) -Path (Join-Path $EvidenceDir 'after-enroll\RemoteHub-Agent-broken-x64.msi') -Kind AfterEnroll
            $failure = $null
            try { $null = Install-LabAgent -Name $VmName -Msi $broken.FullName } catch { $failure = $_.Exception.Message }
            $state = Get-LabAgentState -Name $VmName
            Save 'rollback' @{ Failure = $failure; State = $state }
        }

        It 'A-ROLLBACK-01 a first install failing after the enrollment leaves no rule, certificate, agent.json or service' -Tag 'A-ROLLBACK-01' {
            $failure | Should -Match 'installer failed'
            ($state.LogLines -join "`n") | Should -Match 'Enrolled as device' -Because 'the failure has to come after the enrollment for this case'
            $state.Product | Should -BeNullOrEmpty
            $state.Service | Should -BeNullOrEmpty
            $state.Firewall | Should -BeNullOrEmpty
            @($state.OurCertificates) | Should -BeNullOrEmpty
            $state.Config | Should -BeNullOrEmpty
        }
    }

    Context 'broken enrollment' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $bad = Join-Path $EvidenceDir 'broken.rhagent'
            Set-Content $bad -Value '{ "format": 1, "deviceId": "not json' -Encoding ASCII
            $failure = $null
            try { $null = Install-LabAgent -Name $VmName -EnrollmentFile $bad } catch { $failure = $_.Exception.Message }
            $state = Get-LabAgentState -Name $VmName
            Save 'broken-enrollment' @{ Failure = $failure; State = $state }
        }

        It 'A-INST-26 RemoteHub''s error carries the WixQuietExec / Error lines of the msiexec log' -Tag 'A-INST-26' {
            $failure | Should -Match 'installer failed \(exit code 1603\)'
            $failure | Should -Match 'WixQuietExec|Error \d{4}'
            $state.Product | Should -BeNullOrEmpty
        }
    }
}
