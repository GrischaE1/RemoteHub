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
            # repair: RemoteHub deletes the folder it installed from, and the cached package in C:\Windows\Installer has
            # no files (the cabinet is embedded), so msiexec /fa - by product code or with a copy of the MSI - only looks
            # in the registered source list and ends with 1706 / 1603 (runs 20261017-054704 to 20261019-193627). The
            # package offers no Repair (ARPNOREPAIR); the repair an admin with the MSI can run is a maintenance install
            # from it, REINSTALL=ALL with /fa's mode - the launched package is a valid source then (as in A-HAND-07)
            $msiName = Split-Path (Get-LabAgentMsi) -Leaf
            Copy-LabFile -Name $VmName -Path (Get-LabAgentMsi) -Destination 'C:\LabRepair'
            $noRepair = Invoke-LabGuest -Name $VmName -ArgumentList $state.Product.Code -ScriptBlock {
                param($c)
                (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$c" -ErrorAction SilentlyContinue).NoRepair
            }
            # Windows Installer only takes a source package with the name it was registered under (RemoteHub uploads it
            # as RemoteHub-Agent-x64.msi): run 20261017-054704 got error 1706 with the copy under its build name
            $repair = Invoke-LabGuest -Name $VmName -ArgumentList "C:\LabRepair\$msiName" -ScriptBlock {
                param($msi)
                $registered = Get-ChildItem 'HKLM:\SOFTWARE\Classes\Installer\Products' -ErrorAction SilentlyContinue |
                    Where-Object { (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).ProductName -eq 'RemoteHub Agent' } |
                    ForEach-Object { (Get-ItemProperty (Join-Path $_.PSPath 'SourceList') -ErrorAction SilentlyContinue).PackageName } |
                    Select-Object -First 1
                if ($registered -and $registered -ne (Split-Path $msi -Leaf)) {
                    $target = Join-Path (Split-Path $msi) $registered
                    Copy-Item $msi $target -Force
                    $msi = $target
                }
                $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$msi`"", 'REINSTALL=ALL', 'REINSTALLMODE=amus', '/qn', '/norestart', '/l*v', 'C:\Windows\Temp\rh-repair.log' -Wait -PassThru
                $p.ExitCode
            }
            # the verbose log, to trace a failed repair
            try { Get-LabFile -Name $VmName -Path 'C:\Windows\Temp\rh-repair.log' -Destination (Out 'repair') } catch { }
            $afterRepair = Get-LabAgentState -Name $VmName
            $health = Wait-LabAgent -Name $VmName -Pin $install.pin
            Save 'sas-repair' @{ State = $state; Cad = $cad; NoRepair = $noRepair; Repair = $repair; After = $afterRepair }
        }

        It 'A-INST-23 SoftwareSASGeneration=0 set by a policy before the install stays 0; Ctrl+Alt+Del in the desktop tab explains it' -Tag 'A-INST-23' {
            $state.SoftwareSas | Should -Be 0
            $sasMarker | Should -BeFalse
            @($cad.statuses | Where-Object message -match 'turned off by a policy').Count | Should -BeGreaterThan 0
        }

        It 'A-REPAIR-01 no Repair offered that can''t work; a repair from the MSI (REINSTALL=ALL): identity kept, the service running, no new enrollment' -Tag 'A-REPAIR-01' {
            $noRepair | Should -Be 1 -Because 'ARPNOREPAIR: a repair by product code finds no source'
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
            # Windows Installer's progress window in the console session; nothing to click. A first install is over in a
            # few seconds - a look from outside afterwards can miss the window (run 20261016-212514 saw only the desktop) -
            # so the session that starts msiexec notes the titles of its visible main window until msiexec ends
            $code = @'
$p = Start-Process msiexec.exe -ArgumentList '/i', '"C:\LabInteractive\__MSI__"', '/qb', '/l*v', 'C:\LabInteractive\install.log' -WindowStyle Normal -PassThru
$null = $p.Handle
$titles = @()
while (-not $p.HasExited) {
    $p.Refresh()
    $t = $p.MainWindowTitle
    if ($t -and $titles -notcontains $t) { $titles += $t }
    Start-Sleep -Milliseconds 100
}
@{ exitCode = $p.ExitCode; titles = $titles } | ConvertTo-Json -Compress
'@
            $raw = Invoke-LabInSession -Name $VmName -Account Console -Elevated -TimeoutSec 600 -ScriptBlock ([scriptblock]::Create($code.Replace('__MSI__', $msiName)))
            $seen = [ordered]@{ found = $false; titles = @(); exitCode = $null; raw = "$raw".Trim() }
            try {
                $r = "$raw".Trim() | ConvertFrom-Json
                $seen.titles = @($r.titles); $seen.exitCode = $r.exitCode
                $seen.found = @($seen.titles | Where-Object { $_ -like '*RemoteHub Agent*' }).Count -gt 0
            } catch { }
            $until = (Get-Date).AddMinutes(5)
            do { $state = Get-LabAgentState -Name $VmName; if ($state.Service -and $state.Service.State -eq 'Running') { break }; Start-Sleep -Seconds 5 } while ((Get-Date) -lt $until)
            Save 'interactive' @{ Seen = $seen; State = $state }
        }

        It 'A-HAND-06 the interactive install (Windows Installer''s progress window, no own dialogs) installs and enrolls' -Tag 'A-HAND-06' {
            $seen.found | Should -BeTrue -Because "msiexec's window titles: $(@($seen.titles) -join ', '); output: $($seen.raw)"
            $seen.exitCode | Should -BeIn 0, 3010
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
            $resBefore = Get-LabResolution -Name $VmName
            # fitted to a size the console isn't at, so "restored" can tell
            $size = if ($resBefore -eq '1280x720') { @(1024, 768) } else { @(1280, 720) }
            $o = Out 'uninst'
            $job = Start-LabDesktop -Name $VmName -Pin $install.pin -Account Console -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Failed'); timeout = 60 }
                @{ do = 'settings'; fitWidth = $size[0]; fitHeight = $size[1] }, @{ do = 'sleep'; seconds = 6 }, @{ do = 'markFile'; path = (Join-Path $o 'fitted.txt') }, @{ do = 'wait'; anyOf = @('Ended', 'Failed'); timeout = 180 })
            Wait-LabHostFile (Join-Path $o 'fitted.txt')
            $fitted = Wait-LabResolution -Name $VmName -Not $resBefore -TimeoutSec 15 -NoThrow
            $uninstall = Uninstall-LabAgent -Name $VmName
            $un = Receive-LabDesktop $job
            Start-Sleep -Seconds 5
            $afterUninstall = [ordered]@{ Helpers = Helper; Banner = @(Invoke-LabPerson -Name $VmName -Account Console -Do windows)
                Resolution = Wait-LabResolution -Name $VmName -Is $resBefore -TimeoutSec 20 -NoThrow }
            Save 'during-session' @{ Update = $update; Upd = $upd; Before = $before; AfterUpdate = $afterUpdate; Banner = $banner; Again = $again; Uninstall = $uninstall; Un = $un; After = $afterUninstall; ResBefore = $resBefore; Fitted = $fitted }
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
            $fitted | Should -Not -Be $resBefore -Because 'the fit changed the console''s resolution (precondition: else "restored" says nothing)'
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
            # the list is up when the apps' "More options" buttons are
            $s.page = Invoke-LabPerson -Name $VmName -Account Console -Do uia -Title 'Settings' -Text 'More options' -Value exists -TimeoutSec 60
            # the page's search box: its name differs between Windows builds ('*Search apps*' found nothing on 26H2), so
            # it is picked from the page's elements - an Edit or ComboBox that isn't Settings' own "Find a setting"
            $s.elements = @((Invoke-LabPerson -Name $VmName -Account Console -Do uia -Title 'Settings' -Text '*' -Value list -TimeoutSec 30).items)
            $boxes = @($s.elements | ForEach-Object { $f = "$_" -split '\|', 3; if ($f.Count -eq 3 -and $f[0] -in 'Edit', 'ComboBox' -and "$($f[1]) $($f[2])" -notlike '*setting*') { [pscustomobject]@{ Name = $f[1]; Id = $f[2] } } })
            $box = @($boxes | Where-Object { "$($_.Name) $($_.Id)" -like '*app*' }) + @($boxes | Select-Object -Last 1) | Select-Object -First 1
            if ($box) {
                $s.searchBox = $box
                # typed like a person, not set through UI Automation: the list filters on typed input
                $s.search = Invoke-LabPerson -Name $VmName -Account Console -Do uia -Title 'Settings' -Text $(if ($box.Name) { $box.Name } else { $box.Id }) -Value 'keys:RemoteHub Agent' -TimeoutSec 30
            }
            # "More options" of RemoteHub Agent's own row - never the first app's in the list (without a working search the
            # old step uninstalled whichever app came first)
            $s.more = Invoke-LabPerson -Name $VmName -Account Console -Do uia -Title 'Settings' -Text 'RemoteHub Agent' -Value 'row:More options*' -TimeoutSec 30
            if ($s.more.found -and -not $s.more.PSObject.Properties['error']) {
                $s.menu = Invoke-LabPerson -Name $VmName -Account Console -Do uia -Title 'Settings' -Text 'Uninstall' -TimeoutSec 30
                # the flyout "This app and its related info will be uninstalled." and its own Uninstall button
                $s.confirm = Invoke-LabPerson -Name $VmName -Account Console -Do uia -Title '*' -Text 'Uninstall' -TimeoutSec 30
                # Windows Installer's own question, where this Windows build asks it for an MSI
                $s.prompt = Invoke-LabPerson -Name $VmName -Account Console -Do uia -Title 'Windows Installer' -Text 'Yes' -TimeoutSec 10
            }
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
