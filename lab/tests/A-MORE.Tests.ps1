# A (more) - install, update and uninstall cases next to a running session or the person at the device: the
# Ctrl+Alt+Del policy kept, repair, the interactive install (Windows Installer's own progress window - the package has
# no dialogs), an update during a remote desktop, an update that never comes back, uninstalling from Settings and while
# a remote desktop is open, and RemoteHub's credential provider action without an agent. See docs/test-lab-cases.md
# sections 5 and 9.12.
# Every context starts from the checkpoint (Reset-LabVm): each changes the installation itself.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "a-more-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "a-more-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Helper { Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-CimInstance Win32_Process -Filter "Name='RemoteHubAgent.exe'" | Where-Object CommandLine -match '\sdesktop\s*$' | ForEach-Object ProcessId) } }
    # the console account's own session, parked until <out>\go.txt
    function Own-Session([string]$Name, [string]$P) {
        $o = Out $Name
        $job = Start-LabDesktop -Name $VmName -Pin $P -Account Console -Out $o -Steps @(
            @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Failed'); timeout = 60 }
            @{ do = 'markFile'; path = (Join-Path $o 'connected.txt') }, @{ do = 'waitFile'; path = (Join-Path $o 'go.txt'); timeout = 600 }, @{ do = 'close' })
        Wait-LabHostFile (Join-Path $o 'connected.txt')
        $job
    }
}

AfterAll {
    try { Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'a-more-agent-logs') } catch { }
    # repaired, updated and uninstalled agents, the Ctrl+Alt+Del policy: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'A-MORE <VmName>' -Tag Agent, 'A-INST', 'A-REPAIR', 'A-HAND', 'A-UPD', 'A-UPDFAIL', 'A-UNINST', 'C-PROV' {

    Context 'Ctrl+Alt+Del policy kept, repair' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            Invoke-LabGuest -Name $VmName -ScriptBlock { Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name SoftwareSASGeneration -Value 0 -Type DWord }
            $install = Install-LabAgent -Name $VmName
            $state = Get-LabAgentState -Name $VmName
            $cad = Invoke-LabDesktop -Name $VmName -Pin $install.pin -Account Console -Out (Out 'sas') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Failed'); timeout = 60 }
                @{ do = 'cad' }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'close' })
            $sasMarker = Invoke-LabGuest -Name $VmName -ScriptBlock { Test-Path 'C:\ProgramData\RemoteHub\Agent\sas.agent' }
            # repair: msiexec /fa
            $repair = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $code = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'RemoteHub Agent' }).PSChildName
                $p = Start-Process msiexec.exe -ArgumentList '/fa', $code, '/qn', '/norestart', '/l*v', 'C:\Windows\Temp\rh-repair.log' -Wait -PassThru
                $p.ExitCode
            }
            $afterRepair = Get-LabAgentState -Name $VmName
            $health = Wait-LabAgent -Name $VmName -Pin $install.pin
            Save 'sas-repair' @{ State = $state; Cad = $cad; Repair = $repair; After = $afterRepair }
        }

        It 'A-INST-23 SoftwareSASGeneration=0 set by a policy before the install stays 0; Ctrl+Alt+Del in the desktop tab explains it' -Tag 'A-INST-23' {
            $state.SoftwareSas | Should -Be 0
            $sasMarker | Should -BeFalse
            @($cad.statuses | Where-Object message -match 'turned off by a policy').Count | Should -BeGreaterThan 0
        }

        It 'A-REPAIR-01 msiexec /fa repairs: identity kept, the service running, no new enrollment' -Tag 'A-REPAIR-01' {
            $repair | Should -BeIn 0, 3010
            $afterRepair.Config.deviceId | Should -Be $install.deviceId
            $afterRepair.Service.State | Should -Be 'Running'
            $health.deviceId | Should -Be $install.deviceId
            @($afterRepair.LogLines | Where-Object { $_ -match 'Enrolled as device' }).Count | Should -Be 1
        }
    }

    Context 'interactive install' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            Install-LabSigningCertificate -Name $VmName
            $e = New-LabEnrollment -Name $VmName -Path (Join-Path (Out 'interactive') 'device.rhagent')
            Copy-LabFile -Name $VmName -Path (Get-LabAgentMsi), $e.file -Destination 'C:\LabInteractive'
            $msiName = Split-Path (Get-LabAgentMsi) -Leaf
            # Windows Installer's progress window in the console session; nothing to click
            $task = Start-LabInSession -Name $VmName -Account Console -Elevated -ScriptBlock ([scriptblock]::Create("msiexec.exe /i 'C:\LabInteractive\$msiName' /qb /l*v C:\LabInteractive\install.log"))
            $seen = Invoke-LabPerson -Name $VmName -Account Console -Do uia -Title '*RemoteHub Agent*' -Text '*' -Value exists -TimeoutSec 60
            $until = (Get-Date).AddMinutes(5)
            do { Start-Sleep -Seconds 5; $state = Get-LabAgentState -Name $VmName } while (-not ($state.Service -and $state.Service.State -eq 'Running') -and (Get-Date) -lt $until)
            Stop-LabInSession -Name $VmName -Task $task
            Save 'interactive' @{ Seen = $seen; State = $state }
        }

        It 'A-HAND-06 the interactive install (Windows Installer''s progress window, no own dialogs) installs and enrolls' -Tag 'A-HAND-06' {
            $seen.found | Should -BeTrue
            $state.Config.deviceId | Should -Be $e.deviceId
            $state.Service.State | Should -Be 'Running'
        }
    }

    Context 'update and uninstall during a remote desktop' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName
            $job = Own-Session 'upd' $install.pin
            $before = Helper
            $rebuilt = New-LabRebuiltMsi -Source (Get-LabAgentMsi) -Path (Join-Path (Out 'rebuilt') 'RemoteHub-Agent-rebuilt-x64.msi')
            $update = Update-LabAgent -Name $VmName -Pin $install.pin -Msi $rebuilt.FullName
            Set-Content (Join-Path (Out 'upd') 'go.txt') 'go'
            $upd = Receive-LabDesktop $job
            $afterUpdate = Helper
            $banner = @(Invoke-LabPerson -Name $VmName -Account Console -Do windows)
            $again = Invoke-LabDesktop -Name $VmName -Pin $install.pin -Account Console -Out (Out 'upd-again') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Failed'); timeout = 60 }, @{ do = 'close' })
            # uninstall with a session open
            $resBefore = Invoke-LabGuest -Name $VmName -ScriptBlock { Add-Type -AssemblyName System.Windows.Forms; $b = [Windows.Forms.Screen]::PrimaryScreen.Bounds; "$($b.Width)x$($b.Height)" }
            $o = Out 'uninst'
            $job = Start-LabDesktop -Name $VmName -Pin $install.pin -Account Console -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Failed'); timeout = 60 }
                @{ do = 'settings'; fitWidth = 1280; fitHeight = 720 }, @{ do = 'sleep'; seconds = 6 }, @{ do = 'markFile'; path = (Join-Path $o 'fitted.txt') }, @{ do = 'wait'; anyOf = @('Ended', 'Failed'); timeout = 180 })
            Wait-LabHostFile (Join-Path $o 'fitted.txt')
            $uninstall = Uninstall-LabAgent -Name $VmName
            $un = Receive-LabDesktop $job
            Start-Sleep -Seconds 5
            $afterUninstall = [ordered]@{ Helpers = Helper; Banner = @(Invoke-LabPerson -Name $VmName -Account Console -Do windows)
                Resolution = Invoke-LabGuest -Name $VmName -ScriptBlock { Add-Type -AssemblyName System.Windows.Forms; $b = [Windows.Forms.Screen]::PrimaryScreen.Bounds; "$($b.Width)x$($b.Height)" } }
            Save 'during-session' @{ Update = $update; Upd = $upd; Before = $before; AfterUpdate = $afterUpdate; Banner = $banner; Again = $again; Uninstall = $uninstall; Un = $un; After = $afterUninstall; ResBefore = $resBefore }
        }

        It 'A-UPD-09 an update during a remote desktop: that session ends, its helper and banner go, a new session works' -Tag 'A-UPD-09' {
            $update.verdict | Should -Be 'updated' -Because $update.message
            @($upd.closes).Count | Should -BeGreaterThan 0
            @($before).Count | Should -Be 1
            @($afterUpdate | Where-Object { $_ -in $before }) | Should -BeNullOrEmpty
            @($banner) | Should -BeNullOrEmpty
            @($again.statuses | Where-Object state -eq 'Connected').Count | Should -BeGreaterThan 0
        }

        It 'A-UNINST-13 uninstalling during a remote desktop: the session ends, no helper, the display restored, no banner' -Tag 'A-UNINST-13' {
            $uninstall.ExitCode | Should -BeIn 0, 3010
            @($un.closes).Count | Should -BeGreaterThan 0
            @($afterUninstall.Helpers) | Should -BeNullOrEmpty
            @($afterUninstall.Banner) | Should -BeNullOrEmpty
            $afterUninstall.Resolution | Should -Be $resBefore
        }
    }

    Context 'an update that never comes back' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName
            # takes the port the moment the old agent lets go of it - the new one (and a rollback) can't listen
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $code = 'while ($true) { try { $s = [Net.Sockets.Socket]::new([Net.Sockets.AddressFamily]::InterNetworkV6, [Net.Sockets.SocketType]::Stream, [Net.Sockets.ProtocolType]::Tcp); ' +
                    '$s.DualMode = $true; $s.ExclusiveAddressUse = $true; $s.Bind([Net.IPEndPoint]::new([Net.IPAddress]::IPv6Any, 58732)); $s.Listen(5); Start-Sleep 900; break } catch { Start-Sleep -Milliseconds 50 } }'
                $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))
                $null = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "powershell.exe -NoProfile -EncodedCommand $enc" }
            }
            $rebuilt = New-LabRebuiltMsi -Source (Get-LabAgentMsi) -Path (Join-Path (Out 'rebuilt2') 'RemoteHub-Agent-rebuilt-x64.msi')
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $never = Update-LabAgent -Name $VmName -Pin $install.pin -Msi $rebuilt.FullName
            $seconds = $sw.Elapsed.TotalSeconds
            Save 'never' @{ Update = $never; Seconds = $seconds }
        }

        It 'A-UPDFAIL-03 an update whose agent never answers again: RemoteHub gives up after ~3 minutes and says so' -Tag 'A-UPDFAIL-03' {
            $never.verdict | Should -Be 'noAnswer'
            $never.message | Should -Match "didn't answer again"
            $seconds | Should -BeGreaterOrEqual 150
        }
    }

    Context 'uninstall from Settings' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName
            # the console account is an admin: no prompt for its own elevation in this test (the checkpoint restores it)
            Invoke-LabGuest -Name $VmName -ScriptBlock { Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name ConsentPromptBehaviorAdmin -Value 0 }
            $null = Invoke-LabInSession -Name $VmName -Account Console -ScriptBlock { Start-Process 'ms-settings:appsfeatures' }
            $s = [ordered]@{}
            $s.search = Invoke-LabPerson -Name $VmName -Account Console -Do uia -Title 'Settings' -Text '*Search apps*' -Value 'set:RemoteHub Agent' -TimeoutSec 60
            Start-Sleep -Seconds 3
            $s.more = Invoke-LabPerson -Name $VmName -Account Console -Do uia -Title 'Settings' -Text 'More options*' -TimeoutSec 30
            $s.menu = Invoke-LabPerson -Name $VmName -Account Console -Do uia -Title 'Settings' -Text 'Uninstall' -TimeoutSec 30
            $s.confirm = Invoke-LabPerson -Name $VmName -Account Console -Do uia -Title '*' -Text 'Uninstall' -TimeoutSec 30
            $until = (Get-Date).AddMinutes(5)
            do { Start-Sleep -Seconds 5; $after = Get-LabAgentState -Name $VmName } while ($after.Product -and (Get-Date) -lt $until)
            Save 'settings' @{ Steps = $s; After = $after }
        }

        It 'A-UNINST-12 uninstalled from Settings > Apps: the same result as msiexec /x (product, service, rule, certificate, agent.json gone)' -Tag 'A-UNINST-12' {
            $after.Product | Should -BeNullOrEmpty
            $after.Service | Should -BeNullOrEmpty
            $after.Firewall | Should -BeNullOrEmpty
            @($after.OurCertificates) | Should -BeNullOrEmpty
            $after.Config | Should -BeNullOrEmpty
        }
    }

    Context 'credential provider without an agent' {
        It 'C-PROV-09 without an installed agent: "isn''t installed"; a 3010 exit: "after the device restarts"' -Tag 'C-PROV-09' {
            # MainWindow.ChangeCredentialProvider: offered only for Agent.Status Installed and run over the agent itself
            Set-ItResult -Skipped -Because 'RemoteHub offers the action only on connections with an installed agent and runs it over that agent, so "isn''t installed" can''t be reached; the 3010 path needs a package that asks for a restart'
        }
    }
}
