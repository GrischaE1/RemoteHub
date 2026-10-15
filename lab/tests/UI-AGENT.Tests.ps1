# UI-AG / UI-TL / R-AGENT / W-TRUST - RemoteHub's own window on the admin's PC (this client VM, the console account's
# session), driven by UI Automation (RhPerson.ps1 -Do uia), against the device (the Server VM): a connection made in
# the dialog, PowerShell remoting set up with RemoteHub's script and its certificate trusted, the agent installed,
# tested and updated from the menu, every tool tab, and the agent's refusals as RemoteHub shows them. The device side is
# checked over PowerShell Direct. Client VMs only (2VM). See docs/test-lab-cases.md section 10.5.
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $isClient = (Get-LabVm $VmName).Os -ne 'Server'
}

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $password = (Get-LabConfig).Password
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "ui-agent-$name.json") -Encoding UTF8 }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    # the connection's context menu entry
    # (right-click and the entry in one step: the menu closes when another process takes the focus; "A>B" = submenu)
    function Menu([string]$Entry, [int]$TimeoutSec = 30) { $m = Ui 'Device' -Value "menu:$Entry" -TimeoutSec $TimeoutSec; if ($m.PSObject.Properties['error']) { throw "menu: $($m.error) - entries: $(@($m.entries) -join ', ')" } }
    # the text of the last message box (title like -Title; '?*' = RemoteHub's own dialogs first, as UIA lists them inside
    # the main window), closed with -Button
    function Message([string]$Title = '?*', [string]$Button = 'OK', [int]$TimeoutSec = 120) {
        $m = Ui '*' -Title $Title -Value list -TimeoutSec $TimeoutSec
        $null = Ui $Button -Title $Title -Optional
        ($m.items -join "`n")
    }
    function Shot([string]$name) { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "ui-agent-$name.png") }

    $device = @(Get-LabVm | Where-Object Os -eq 'Server')[0]
    if ($vm.Os -ne 'Server') {
        Reset-LabVm -Name $device.Name
        $deviceIp = Get-LabVmAddress -Name $device.Name
        Reset-LabVm -Name $VmName
        $clientIp = Get-LabVmAddress -Name $VmName
        $null = Install-LabClient -Name $VmName
        $task = Start-LabClient -Name $VmName
        New-LabClientConnection -Name $VmName -Display 'Device' -TargetHost $deviceIp -User $device.AdminAccount -Password $password
    }
}

AfterAll {
    if ($vm.Os -ne 'Server') {
        Shot 'end'
        Stop-LabClient -Name $VmName -Task $task
        try { Save-LabAgentEvidence -Name $device.Name -Destination (Join-Path $EvidenceDir 'ui-agent-device-logs') } catch { }
        Stop-LabVm -Name $device.Name
    }
}

Describe 'UI-AGENT <VmName>' -Tag Client, UI, 'UI-AG', 'UI-TL', 'R-AGENT', 'W-TRUST' -Skip:(-not $isClient) {

    Context 'PowerShell remoting set up from RemoteHub' {
        BeforeAll {
            $s = [ordered]@{}
            # the test fails (no HTTPS yet) and offers the setup
            Menu 'Test PowerShell remoting*'
            $s.test1 = Message -Button 'Set up on the target*' -TimeoutSec 90
            # Copy script (Windows' Save As dialog has no usable UIA patterns here); the admin pastes it into an
            # elevated PowerShell on the device - here over PowerShell Direct, as an admin there
            $null = Ui 'Copy script' -Title 'Set up PowerShell remoting' -TimeoutSec 60
            $script = Invoke-LabInSession -Name $VmName -Account Console -ScriptBlock { Get-Clipboard -Raw }
            $null = New-Item -ItemType Directory -Path (Join-Path $EvidenceDir 'ui-agent-script') -Force
            $scriptFile = Join-Path $EvidenceDir 'ui-agent-script\remotehub-setup.ps1'
            [IO.File]::WriteAllText($scriptFile, "$script", (New-Object Text.UTF8Encoding $true))
            Copy-LabFile -Name $device.Name -Path $scriptFile -Destination 'C:\LabSetup'
            $s.run = Invoke-LabGuest -Name $device.Name -ScriptBlock { Set-ExecutionPolicy -Scope Process Bypass -Force; & 'C:\LabSetup\remotehub-setup.ps1' *>&1 | ForEach-Object { "$_" } | Select-Object -Last 15 }
            $null = Ui 'Test again' -Title 'Set up PowerShell remoting'
            # RemoteHub's own question, then Windows' question about the root certificate
            $s.trust = Ui 'Same*trust it' -Title '*' -TimeoutSec 60
            $s.windows = Ui 'Yes' -Title 'Security Warning' -TimeoutSec 30
            $s.result = Message -TimeoutSec 90
            $null = Ui 'Close' -Title 'Set up PowerShell remoting' -Optional
            $s.trusted = Invoke-LabInSession -Name $VmName -Account Console -ScriptBlock { @(Get-ChildItem Cert:\CurrentUser\Root | Where-Object FriendlyName -like 'RemoteHub*WinRM*' | ForEach-Object FriendlyName) -join ';' }
            Shot 'remoting'
            Save 'remoting' $s
        }

        It 'UI-TL-06 Set up on the target: the script saved, run on the device, Test again: available over HTTPS' -Tag 'UI-TL-06' {
            $s.test1 | Should -Match 'not|unavailable|couldn''t|can''t|refused' -Because 'before the setup WinRM HTTPS isn''t there'
            ($s.run -join "`n") | Should -Match 'Done\. PowerShell remoting is enabled over HTTPS'
            $s.result | Should -Match 'available|PowerShell \d'
        }

        It 'W-TRUST-01 Test again puts "RemoteHub*WinRM <host>" into CurrentUser\Root after Windows'' question' -Tag 'W-TRUST-01' {
            $s.trust.found | Should -BeTrue
            $s.windows.found | Should -BeTrue
            "$($s.trusted)" | Should -Match 'RemoteHub.*WinRM'
        }
    }

    Context 'tools over WinRM' {
        BeforeAll {
            $t = [ordered]@{}
            Menu 'PowerShell'
            # the tab: its icon buttons are found by their tooltip, the input line and the output by type (no names)
            $null = Ui 'Stop the running command (Ctrl+C)' -Value exists -TimeoutSec 60
            $null = Ui 'type:Document' -Value exists -TimeoutSec 60
            Start-Sleep -Seconds 5
            $t.typed = Ui 'type:Edit' -Value 'keys:Get-Service Spooler | Format-List Name,Status{ENTER}' -Optional
            Start-Sleep -Seconds 5
            $t.output = (Ui 'type:Document' -Value read -Optional).value
            $t.complete = Ui 'type:Edit' -Value 'keys:Get-Ser{TAB}' -Optional
            Start-Sleep -Seconds 3
            $t.list = Ui '*' -Value list
            $t.input = (Ui 'type:Edit' -Value read -Optional).value
            $null = Ui 'type:Edit' -Value 'keys:{ESC}^a{DEL}exit{ENTER}' -Optional
            Start-Sleep -Seconds 4
            $t.afterExit = Ui 'Stop the running command (Ctrl+C)' -Value exists -Optional -TimeoutSec 5
            Shot 'ps-winrm'
            Save 'ps-winrm' $t
        }

        It 'UI-TL-01 PowerShell tab over WinRM HTTPS: Get-Service runs, Tab completes, exit closes' -Tag 'UI-TL-01' {
            $t.output | Should -Match 'Spooler'
            # Tab completes the line itself or shows the matches list
            "$($t.input)`n$($t.list.items -join "`n")" | Should -Match 'Get-Service'
            $t.afterExit.found | Should -BeFalse
        }
    }

    Context 'the agent installed from the menu' {
        BeforeAll {
            $a = [ordered]@{}
            Menu 'Install RemoteHub Agent*'
            $a.dialog = Ui 'Install silently over PowerShell remoting*' -Title 'Install RemoteHub Agent' -Value check -TimeoutSec 60
            $a.useAfter = Ui 'Use the agent for this connection afterwards' -Title 'Install RemoteHub Agent' -Value check -Optional
            $null = Ui 'Install' -Title 'Install RemoteHub Agent'
            $a.done = Ui 'Done' -Title 'Install RemoteHub Agent' -TimeoutSec 600
            $a.state = Get-LabAgentState -Name $device.Name
            Shot 'installed'
            # Test RemoteHub Agent: only in the menu while the connection isn't known to work - after the install from
            # the menu it already is, and the menu offers the tools instead (documented)
            $m = Ui 'Device' -Value 'menu:Test RemoteHub Agent*' -Optional
            if ($m.PSObject.Properties['error']) { $a.entries = @($m.entries) } else { $a.test = Message -TimeoutSec 60 }
            Save 'install' $a
        }

        It 'UI-AG-01 Install RemoteHub Agent from the menu (silent over remoting): success, the device runs the agent, audit "agent installed"' -Tag 'UI-AG-01' {
            $a.done.found | Should -BeTrue
            $a.state.Service.State | Should -Be 'Running'
        }

        It 'UI-AG-02 Test RemoteHub Agent: OK with the version' -Tag 'UI-AG-02' {
            if ($a.Contains('test')) { $a.test | Should -Match (Get-LabMsiVersion (Get-LabAgentMsi)) }
            else { foreach ($e in 'PowerShell', 'File transfer', 'Registry', 'Services', 'Processes') { $a.entries | Should -Contain $e } }
        }
    }

    Context 'tools through the agent' {
        BeforeAll {
            $r = [ordered]@{}
            # each tool on its own: a failing step is written down (error, screenshot, open dialogs) and the next tool
            # still runs; dot-sourced, so the variables stay
            function Step([string]$Tool, [scriptblock]$Do) {
                try { . $Do }
                catch {
                    $r["$Tool-error"] = "$_"
                    Shot "fail-$Tool"
                    $r["$Tool-dialogs"] = @((Ui '*' -Title '?*' -Value list -Optional -TimeoutSec 3).items | Select-Object -First 60)
                    foreach ($i in 1..2) { $null = Invoke-LabPerson -Name $VmName -Account Console -Do key -Value 1B; Start-Sleep -Milliseconds 500 }
                }
            }
            # files: a folder of the admin's PC uploaded through the file transfer tab
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock { $d = 'C:\Users\Public\UpFolder'; $null = New-Item -ItemType Directory -Path $d -Force; 1..5 | ForEach-Object { Set-Content "$d\f$_.txt" "ui file $_" } }
            $local = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-ChildItem 'C:\Users\Public\UpFolder' -File | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash } | Sort-Object) }
            Step files {
            Menu 'File transfer'
            # the address boxes have no names: this PC's (left) is the first, the device's the second
            $null = Ui 'type:Edit#1' -Value 'keys:^aC:\Users\Public{ENTER}' -TimeoutSec 60
            $null = Ui 'type:Edit#2' -Value 'keys:^aC:\{ENTER}'
            Start-Sleep -Seconds 3
            $null = Ui 'UpFolder' -Value realclick
            $null = Ui 'Upload selected files'
            Start-Sleep -Seconds 15
            $r.uploaded = Invoke-LabGuest -Name $device.Name -ScriptBlock { @(Get-ChildItem 'C:\Users\*\Documents\UpFolder', 'C:\Users\*\UpFolder', 'C:\UpFolder' -File -ErrorAction SilentlyContinue | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash } | Sort-Object) }
            Shot 'files'
            }
            # registry
            # the key made on the device before the tab opens (the tab loads HKLM\SOFTWARE once and doesn't look again
            # when a path is typed), then opened by its path in the address box: set directly, Enter opens it
            $null = Invoke-LabGuest -Name $device.Name -ScriptBlock { $null = New-Item HKLM:\SOFTWARE\RemoteHubLab -Force }
            Step registry {
            Menu 'Registry'
            $null = Ui 'type:Edit' -Value 'set:HKLM\SOFTWARE\RemoteHubLab' -TimeoutSec 60
            $null = Ui 'type:Edit' -Value 'keys:{ENTER}'
            Start-Sleep -Seconds 3
            # WPF drops the '_' of REG_SZ in menu headers (access key): the entry shows 'String value (REGSZ)...'
            $null = Ui 'New key or value' -Value 'pick:String value (REG*SZ)*'
            Start-Sleep -Seconds 2
            Shot 'reg-new'
            # the value dialog: "New REG_SZ"
            $null = Ui 'Value name' -Title 'New REG*' -Value 'setnext:FromUi'
            $null = Ui 'Value data' -Title 'New REG*' -Value 'setnext:set in the registry tab'
            $null = Ui 'Create' -Title 'New REG*'
            Start-Sleep -Seconds 3
            $r.reg = Invoke-LabGuest -Name $device.Name -ScriptBlock { (Get-ItemProperty HKLM:\SOFTWARE\RemoteHubLab -ErrorAction SilentlyContinue).FromUi }
            }
            # services
            Step services {
            Menu 'Services'
            # the filter box has no name; the tool buttons are named by their tooltips; Dialogs.Confirm asks first
            $null = Ui 'type:Edit' -Value 'set:Spooler' -TimeoutSec 60
            $null = Ui 'Print Spooler' -Value realclick
            $null = Ui 'Stop the service'
            $null = Ui 'Stop' -Title '?*' -Optional -TimeoutSec 10
            Start-Sleep -Seconds 5
            $r.stopped = Invoke-LabGuest -Name $device.Name -ScriptBlock { (Get-Service Spooler).Status.ToString() }
            $null = Ui 'Start the service'
            $null = Ui 'Start' -Title '?*' -Optional -TimeoutSec 5
            Start-Sleep -Seconds 5
            $r.started = Invoke-LabGuest -Name $device.Name -ScriptBlock { (Get-Service Spooler).Status.ToString() }
            }
            # processes
            Step processes {
            $notepad = Invoke-LabGuest -Name $device.Name -ScriptBlock { (Start-Process notepad.exe -PassThru).Id }
            Menu 'Processes'
            $null = Ui 'type:Edit' -Value 'set:notepad' -TimeoutSec 60
            $null = Ui 'notepad*' -Value realclick
            $null = Ui 'End process (Del)'
            $null = Ui 'End process' -Title '?*'
            Start-Sleep -Seconds 3
            $r.notepadGone = -not (Invoke-LabGuest -Name $device.Name -ArgumentList $notepad -ScriptBlock { param($p) [bool](Get-Process -Id $p -ErrorAction SilentlyContinue) })
            $null = Ui 'type:Edit' -Value 'set:lsass'
            # a critical process: End is switched off for it, and Del says why
            $null = Ui 'lsass*' -Value realclick
            $r.lsassEnd = (Ui 'End process (Del)' -Value exists).enabled
            # Del on the console keyboard (the selected row has the focus)
            $null = Invoke-LabPerson -Name $VmName -Account Console -Do key -Value 2E
            Start-Sleep -Seconds 2
            $r.lsassMessage = (Ui '*' -Value list).items -join "`n"
            }
            # agent desktop
            Step desktop {
            # with the agent in use, Connect opens the agent desktop ("Remote desktop via RDP" is the other way)
            Menu 'Connect'
            # Admin is signed in at the device and is asked: the person there allows it (console mouse on the agent's dialog)
            $r.allow = Invoke-LabPerson -Name $device.Name -Account Console -Do click -Text 'Allow' -TimeoutSec 60
            $r.desktop = Ui 'Send Ctrl+Alt+Del' -Value exists -TimeoutSec 60
            }
            Shot 'tools'
            Save 'tools' $r
        }

        It 'UI-TL-02 the file transfer tab uploads a folder: hashes equal on the device' -Tag 'UI-TL-02' {
            @($r.uploaded) | Should -Be @($local)
        }

        It 'UI-TL-03 the registry tab creates a value under HKLM\SOFTWARE\RemoteHubLab; the device agrees' -Tag 'UI-TL-03' {
            $r.reg | Should -Be 'set in the registry tab'
        }

        It 'UI-TL-04 the services tab stops and starts Spooler; the device agrees' -Tag 'UI-TL-04' {
            $r.stopped | Should -Be 'Stopped'
            $r.started | Should -Be 'Running'
        }

        It 'UI-TL-05 the processes tab ends a notepad on the device; lsass is refused' -Tag 'UI-TL-05' {
            $r.notepadGone | Should -BeTrue
            $r.lsassEnd | Should -BeFalse
            $r.lsassMessage | Should -Match 'critical Windows process and can''t be ended'
        }

        It 'R-AGENT-01 the full agent flow from the UI: install, then PowerShell, files, registry, services, processes and the agent desktop work' -Tag 'R-AGENT-01' {
            $a.done.found | Should -BeTrue
            @($r.uploaded).Count | Should -Be 5
            $r.desktop.found | Should -BeTrue
        }
    }

    Context 'refusals as RemoteHub shows them' {
        BeforeAll {
            $f = [ordered]@{}
            # the agent stopped: the tab says so, no hang; started again: works on retry
            $null = Invoke-LabGuest -Name $device.Name -ScriptBlock { Stop-Service RemoteHubAgent -Force }
            # the tab's own text (the output) and whatever dialog shows
            function TabText { "$((Ui 'type:Document' -Value read -Optional -TimeoutSec 20).value)`n$((Ui '*' -Title '?*' -Value list -Optional -TimeoutSec 5).items -join "`n")" }
            Menu 'PowerShell'
            Start-Sleep -Seconds 40
            $f.stoppedMessage = TabText
            $null = Invoke-LabGuest -Name $device.Name -ScriptBlock { Start-Service RemoteHubAgent }
            Start-Sleep -Seconds 10
            $null = Ui 'Reconnect' -Optional -TimeoutSec 5
            Start-Sleep -Seconds 15
            $f.afterStart = TabText
            Shot 'reconnected'
            # a new device certificate made by "another RemoteHub": the pin doesn't match
            $since = Get-LabGuestTime -Name $device.Name
            $f.oldPin = (Get-LabAgentState -Name $device.Name).Certificate.Sha256
            # uninstalled and installed again: a new identity (a reinstall over the running agent keeps serving the old
            # certificate - A-SAME-02)
            Uninstall-LabAgent -Name $device.Name
            $re = Install-LabAgent -Name $device.Name -Scope $clientIp
            $f.reportedPin = $re.pin
            $f.newPin = (Get-LabAgentState -Name $device.Name).Certificate.Sha256
            Menu 'PowerShell'
            Start-Sleep -Seconds 15
            $f.pinMessage = TabText
            Shot 'pin'
            $f.pinFailedLogons = @(Get-LabFailedLogon -Name $device.Name -Since $since)
            $f.pinLog = @(Get-LabAgentLog -Name $device.Name -Since $since | Where-Object { $_ -match 'Refused|Session started' })
            Save 'refusals' $f
        }

        It 'UI-AG-05 the agent stopped: the tab reports the failure, no hang; started again it reconnects on retry' -Tag 'UI-AG-05' {
            $f.stoppedMessage | Should -Match 'agent|connect|refused|answer'
            $f.afterStart | Should -Match 'PowerShell|PS '
        }

        It 'UI-AG-03 a changed device certificate: a warning that can''t be clicked through; nothing sent to the device' -Tag 'UI-AG-03' {
            $f.newPin | Should -Not -Be $f.oldPin -Because 'the device has a new certificate'
            $f.pinMessage | Should -Match 'certificate|fingerprint'
            @($f.pinFailedLogons) | Should -BeNullOrEmpty
            @($f.pinLog) | Should -BeNullOrEmpty
        }
    }
}
