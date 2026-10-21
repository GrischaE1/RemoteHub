# A-UPD / A-SAME / A-DOWN - updating an installed agent the ways RemoteHub does it: through the running agent
# (AgentInstaller.StartUpdateThroughAgent + WaitForRestartAsync, the verdict of MainWindow.RunAgentInstall), with a
# new device certificate, over remoting; the same version again; and a downgrade. The previous release comes from
# LabRoot\msi\previous, the build under test from LabRoot\msi. See docs/test-lab-cases.md section 5.3.
# Every context starts from the checkpoint (Reset-LabVm) with the previous release.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $newMsi = Get-LabAgentMsi
    $oldMsi = Get-LabAgentMsi -Previous
    $newVersion = Get-LabMsiVersion $newMsi
    $oldVersion = Get-LabMsiVersion $oldMsi
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "a-upd-$name.json") -Encoding UTF8 }
    # The staging folder is removed by the detached "cmd /c start /wait msiexec ... & rd" after msiexec ended.
    function Wait-NoStaging {
        $until = (Get-Date).AddMinutes(3)
        do {
            $left = @((Get-LabAgentState -Name $VmName).Leftovers)
            if ($left.Count -eq 0) { return $left }
            Start-Sleep -Seconds 5
        } while ((Get-Date) -lt $until)
        $left
    }
}

AfterAll {
    # updated / downgraded agents: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'A-UPD <VmName>' -Tag Agent, 'A-UPD' {

    Context 'update through the agent' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName -Msi $oldMsi
            # a policy value and the log written so far must survive the update
            Set-LabAgentPolicy -Name $VmName -Values @{ ConsentTimeoutSeconds = 45 }
            $before = Get-LabAgentState -Name $VmName
            $update = Update-LabAgent -Name $VmName -Pin $install.pin -Msi $newMsi -HoldSession
            $left = Wait-NoStaging
            $after = Get-LabAgentState -Name $VmName
            $policy = Get-LabAgentPolicy -Name $VmName
            $updateLog = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $log = Join-Path $env:TEMP 'RemoteHub-Agent-update.log'
                if (Test-Path $log) { Get-Content $log | Where-Object { $_ -match 'Installation (success or error status|operation completed)|completed successfully|Return value 3' } | ForEach-Object { [string]$_ } }
            }
            Save 'through-agent' @{ Install = $install; Before = $before; Update = $update; After = $after; Left = $left; Policy = $policy; UpdateLog = $updateLog }
        }
        AfterAll {
            Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'a-upd-through-agent-logs')
        }

        It 'A-UPD-01 updates through the agent: down, back with the new version, same certificate' -Tag 'A-UPD-01' {
            $update.before | Should -Be $oldVersion
            $update.expectedVersion | Should -Be $newVersion
            $update.verdict | Should -Be 'updated' -Because $update.message
            $update.health.version | Should -Be $newVersion
            $update.health.deviceId | Should -Be $install.deviceId
            $update.oldPinNow | Should -Be $newVersion   # the old pin still answers: the certificate was kept
            $after.Product.Version | Should -Match "^$([regex]::Escape($newVersion))"
            $after.Service.State | Should -Be 'Running'
        }

        It 'A-UPD-02 removes the staging folder after msiexec ended and keeps the update log' -Tag 'A-UPD-02' {
            @($left) | Should -BeNullOrEmpty
            @($updateLog) -join "`n" | Should -Match 'completed successfully|success or error status: 0'
        }

        It 'A-UPD-03 keeps the device identity: device ID, certificate, port, firewall scope, allowed groups' -Tag 'A-UPD-03' {
            $after.Config.deviceId | Should -Be $before.Config.deviceId
            $after.Config.certificateThumbprint | Should -Be $before.Config.certificateThumbprint
            $after.Config.listenPort | Should -Be $before.Config.listenPort
            @($after.Config.allowedGroups) | Should -Be @($before.Config.allowedGroups)
            $after.Certificate.Sha256 | Should -Be $install.pin
            $after.Certificate.Exportable | Should -Be $false
            @($after.OurCertificates).Count | Should -Be 1
            $after.Firewall.Count | Should -Be 1
            @($after.Firewall.RemoteAddress) | Should -Be @($before.Firewall.RemoteAddress)
            $after.Service.Delayed | Should -BeTrue
        }

        It 'A-UPD-06 keeps the policy and the earlier log' -Tag 'A-UPD-06' {
            $policy.ConsentTimeoutSeconds | Should -Be 45
            $firstLine = @($before.LogLines)[0]
            @($after.LogLines) | Should -Contain $firstLine
            ($after.LogLines -join "`n") | Should -Match "RemoteHub Agent $([regex]::Escape($newVersion)) starting"
        }

        It 'A-UPD-08 ends a PowerShell session that was open during the update; a new one works' -Tag 'A-UPD-08' {
            $update.heldPid | Should -BeGreaterThan 0
            $update.heldAfter | Should -Match '^ended'
            # a fresh session through the updated agent - a new PowerShell process
            $again = Invoke-LabAgentCommand -Name $VmName -Pin $install.pin -Script '$PID'
            [int]@($again.output)[0] | Should -BeGreaterThan 0
            [int]@($again.output)[0] | Should -Not -Be $update.heldPid
        }
    }

    Context 'update with a new device certificate' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName -Msi $oldMsi
            $update = Update-LabAgent -Name $VmName -Pin $install.pin -Msi $newMsi -NewIdentity
            $after = Get-LabAgentState -Name $VmName
            Save 'new-identity' @{ Install = $install; Update = $update; After = $after }
        }

        It 'A-UPD-04 answers with the new certificate only; the old one is gone' -Tag 'A-UPD-04' {
            $update.verdict | Should -Be 'updated' -Because $update.message
            $update.expectedPin | Should -Not -Be $install.pin
            $update.health.deviceId | Should -Be $update.deviceId
            $update.oldPinNow | Should -Match '^refused'
            $after.Certificate.Sha256 | Should -Be $update.expectedPin
            $after.Config.deviceId | Should -Be $update.deviceId
            @($after.OurCertificates).Count | Should -Be 1
            $after.Firewall.Count | Should -Be 1
            @($after.Leftovers) | Should -BeNullOrEmpty
        }
    }

    Context 'update over remoting' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName -Msi $oldMsi
            $before = Get-LabAgentState -Name $VmName
            $update = Install-LabAgent -Name $VmName -Msi $newMsi -KeepIdentity -VirtualDisplay $false
            $after = Get-LabAgentState -Name $VmName
            $health = Get-LabAgentHealth -Name $VmName -Pin $install.pin
            Save 'over-remoting' @{ Install = $install; Before = $before; Update = $update; After = $after; Health = $health }
        }

        It 'A-UPD-05 updates over remoting and keeps the identity' -Tag 'A-UPD-05' {
            $update.installed.fingerprint | Should -Be $install.pin
            $update.installed.deviceId | Should -Be $install.deviceId
            $health.version | Should -Be $newVersion
            $after.Config.certificateThumbprint | Should -Be $before.Config.certificateThumbprint
            @($after.OurCertificates).Count | Should -Be 1
        }

        It 'A-UPD-07 removes the virtual display driver when the update turns it off' -Tag 'A-UPD-07' {
            if (-not $before.VirtualDisplay) {
                Set-ItResult -Skipped -Because "agent $oldVersion didn't install the virtual display driver"
                return
            }
            $after.VirtualDisplay | Should -BeNullOrEmpty
        }
    }

    Context 'same version again' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName -Msi $newMsi
            $same = Update-LabAgent -Name $VmName -Pin $install.pin -Msi $newMsi
            $sameNew = Update-LabAgent -Name $VmName -Pin $install.pin -Msi $newMsi -NewIdentity
            $after = Get-LabAgentState -Name $VmName
            Save 'same-version' @{ Install = $install; Same = $same; SameNewIdentity = $sameNew; After = $after }
        }

        It 'A-SAME-01 installs the same version again through the agent and keeps the identity' -Tag 'A-SAME-01' {
            $same.verdict | Should -Be 'updated' -Because $same.message
            $same.health.deviceId | Should -Be $install.deviceId
            $same.oldPinNow | Should -Be $newVersion
        }

        It 'A-SAME-02 takes a new device certificate with the same version' -Tag 'A-SAME-02' {
            # RemoteHub suggests "Install again with Enroll again" when a pin doesn't match
            $sameNew.verdict | Should -Be 'updated' -Because $sameNew.message
            $sameNew.oldPinNow | Should -Match '^refused'
            $after.Certificate.Sha256 | Should -Be $sameNew.expectedPin
            @($after.OurCertificates).Count | Should -Be 1
        }
    }

    Context 'downgrade' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName -Msi $newMsi
            Copy-LabFile -Name $VmName -Path $oldMsi -Destination 'C:\Windows\Temp\RemoteHubLab'
            $down = Invoke-LabGuest -Name $VmName -ArgumentList (Split-Path $oldMsi -Leaf) -ScriptBlock {
                param($file)
                $log = 'C:\Windows\Temp\RemoteHubLab\downgrade.log'
                $p = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -Wait -PassThru -WindowStyle Hidden `
                    -ArgumentList @('/i', "`"C:\Windows\Temp\RemoteHubLab\$file`"", '/qn', '/norestart', '/l*v', "`"$log`"")
                [pscustomobject]@{ ExitCode = $p.ExitCode; Lines = @(Get-Content $log | Where-Object { $_ -match 'newer version|Return value 3|success or error status' }) }
            }
            $after = Get-LabAgentState -Name $VmName
            $health = Get-LabAgentHealth -Name $VmName -Pin $install.pin
            Save 'downgrade' @{ Install = $install; Downgrade = $down; After = $after; Health = $health }
        }

        It 'A-DOWN-01 refuses the older version and keeps the newer one running' -Tag 'A-DOWN-01' {
            $down.ExitCode | Should -Not -Be 0
            ($down.Lines -join "`n") | Should -Match 'A newer version of the RemoteHub Agent is already installed'
            $after.Product.Version | Should -Match "^$([regex]::Escape($newVersion))"
            $health.version | Should -Be $newVersion
            $after.Config.deviceId | Should -Be $install.deviceId
        }
    }
}
