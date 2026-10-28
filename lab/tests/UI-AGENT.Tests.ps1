# UI-AG / UI-TL / R-AGENT / W-TRUST - RemoteHub's own window on the admin's PC (this client VM, the console account's
# session), driven by UI Automation (RhPerson.ps1 -Do uia), against the device (the Server VM): a connection made in
# the dialog, PowerShell remoting set up with RemoteHub's script and its certificate trusted, the agent installed,
# tested and updated from the menu, every tool tab with its filter, the agent uninstalled from the menu (and removed
# here only when it can't be reached), and the agent's refusals as RemoteHub shows them. The device side is
# checked over PowerShell Direct. Client VMs only (2VM). See docs/test-lab-cases.md section 10.5.
# LabRole: console
# LabNeeds: lane
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $isClient = Test-LabConsoleVm -Name $VmName
}

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $isClient = Test-LabConsoleVm -Name $VmName
    $password = (Get-LabConfig).Password
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "ui-agent-$name.json") -Encoding UTF8 }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    # the connection's context menu entry
    # (right-click and the entry in one step: the menu closes when another process takes the focus; "A>B" = submenu)
    function Menu([string]$Entry, [int]$TimeoutSec = 30) { $m = Ui 'Device' -Value "menu:$Entry" -TimeoutSec $TimeoutSec; if ($m.PSObject.Properties['error']) { throw "menu: $($m.error) - entries: $(@($m.entries) -join ', ')" } }
    # RemoteHub's message boxes are named after their text in UI Automation, not after their title (lab run
    # 20261019-193627: no window 'PowerShell remoting' while the box "PowerShell remoting is not available ..." was
    # open). The first box whose name is like one of -Like within -TimeoutSec, or $null; its .window names it for the
    # buttons ([WildcardPattern]::Escape).
    function Answer([string[]]$Like, [int]$TimeoutSec) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            foreach ($t in $Like) {
                $m = Ui '*' -Title $t -Value list -Optional -TimeoutSec 2
                if ($m.found) { return $m }
            }
        } while ((Get-Date) -lt $until)
        $null
    }
    # what the boxes of the connection test start with: PowerShell remoting ("... works (", "... is not available for",
    # "... answers on"; "<name> answers over HTTPS, but ..." for a certificate not trusted yet) and the agent
    $psTitles = @('PowerShell remoting*', '*answers over HTTPS*')
    $agentTitles = @('The RemoteHub Agent*', 'RemoteHub Agent')
    # the text of the last message box (named like -Title; '?*' = RemoteHub's own dialogs first, as UIA lists them inside
    # the main window), closed with -Button
    function Message([string[]]$Title = @('?*'), [string]$Button = 'OK', [int]$TimeoutSec = 120) {
        $m = Answer $Title $TimeoutSec
        if (-not $m) { throw "no message box like '$($Title -join "' / '")' within $TimeoutSec s" }
        $null = Ui $Button -Title ([WildcardPattern]::Escape($m.window)) -Optional
        ($m.items -join "`n")
    }
    function Shot([string]$name) { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "ui-agent-$name.png") }
    # Esc on the console keyboard (a menu left open after a missed entry: RhPerson closes only the submenu)
    function Esc { $null = Invoke-LabPerson -Name $VmName -Account Console -Do key -Value 1B; Start-Sleep -Milliseconds 500 }
    # the entries of the connection's RemoteHub Agent submenu (an entry that isn't there answers them; tried again when the
    # menu didn't open in time and answered none); the menu is closed again with Esc by the caller
    function AgentMenu {
        for ($i = 0; $i -lt 3; $i++) {
            $e = @((Ui 'Device' -Value 'menu:RemoteHub Agent>zz-no-such-entry' -Optional).entries)
            if ($e.Count -gt 0) { return , $e }
            Esc; Start-Sleep -Seconds 2
        }
        , @()
    }

    # RemoteHub's setup of PowerShell remoting, from its answer that WinRM isn't available (open when this is called):
    # Set up on the target, Copy script (Windows' Save As dialog has no usable UIA patterns here); the admin pastes it
    # into an elevated PowerShell on the device - here over PowerShell Direct, as an admin there; Test again, RemoteHub's
    # question about the certificate, then Windows' question about the root certificate. Answers what each step showed.
    function SetUpRemoting {
        $s = [ordered]@{}
        $box = Answer $psTitles 90
        if (-not $box) { throw "Test PowerShell remoting: no answer within 90 s (no box like '$($psTitles -join "' / '")')." }
        $s.test1 = @($box.items) -join "`n"
        $null = Ui 'Set up on the target*' -Title ([WildcardPattern]::Escape($box.window))
        $null = Ui 'Copy script' -Title 'Set up PowerShell remoting' -TimeoutSec 60
        $script = Invoke-LabInSession -Name $VmName -Account Console -ScriptBlock { Get-Clipboard -Raw }
        $null = New-Item -ItemType Directory -Path (Join-Path $EvidenceDir 'ui-agent-script') -Force
        $scriptFile = Join-Path $EvidenceDir 'ui-agent-script\remotehub-setup.ps1'
        [IO.File]::WriteAllText($scriptFile, "$script", (New-Object Text.UTF8Encoding $true))
        Copy-LabFile -Name $device.Name -Path $scriptFile -Destination 'C:\LabSetup'
        $s.run = Invoke-LabGuest -Name $device.Name -ScriptBlock { Set-ExecutionPolicy -Scope Process Bypass -Force; & 'C:\LabSetup\remotehub-setup.ps1' *>&1 | ForEach-Object { "$_" } | Select-Object -Last 15 }
        $null = Ui 'Test again' -Title 'Set up PowerShell remoting'
        $s.trust = Ui 'Same*trust it' -Title '*' -TimeoutSec 60
        $s.windows = Ui 'Yes' -Title 'Security Warning' -TimeoutSec 30
        $s.result = Message -Title $psTitles -TimeoutSec 90 # a bare "?*" would match the main window before the box opens
        $null = Ui 'Close' -Title 'Set up PowerShell remoting' -Optional
        $s.trusted = Invoke-LabInSession -Name $VmName -Account Console -ScriptBlock { @(Get-ChildItem Cert:\CurrentUser\Root | Where-Object FriendlyName -like 'RemoteHub*WinRM*' | ForEach-Object FriendlyName) -join ';' }
        $s
    }

    # Tools lists the tools (PowerShell, File transfer, Registry, Services, Processes) only once the connection's test
    # has passed the way it connects now (WinRM or the agent); until then it has one entry "Test PowerShell remoting" /
    # "Test RemoteHub Agent" (" again" after a failure; "Testing ..." switched off while it runs). This runs that test
    # from the menu, waits for its answer and reopens the menu until the tools are there. Answers the test's dialog text
    # ('' when the tools were offered already). -SetUp: when the answer is that WinRM isn't available, RemoteHub's setup
    # runs once (the device starts from its checkpoint without WinRM HTTPS; the setup context may not have run, as in a
    # rerun of single cases).
    function ToolsReady([switch]$SetUp, [int]$TimeoutSec = 300) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        $answer = ''; $last = ''; $setUpDone = $false
        do {
            $m = Ui 'Device' -Value 'menu:Tools>Test*' -Optional -TimeoutSec 15
            $err = if ($m.PSObject.Properties['error']) { "$($m.error)" } else { '' }
            if ($m.found -and -not $err) {
                # the test started: its answer in RemoteHub's own dialog, "... works (...)" with OK, else a question
                $titles = if ("$($m.value)" -like '*RemoteHub Agent*') { $agentTitles } else { $psTitles }
                $box = Answer $titles 180
                if (-not $box) { throw "Tools: '$($m.value)' gave no answer within 180 s (no box like '$($titles -join "' / '")')." }
                $title = [WildcardPattern]::Escape($box.window)
                $answer = @($box.items) -join "`n"
                if ($answer -match 'works \(') { $null = Ui 'OK' -Title '?*' -Optional -TimeoutSec 10 }
                elseif ($SetUp -and -not $setUpDone -and $answer -match 'Set up on the target') { $setUpDone = $true; $null = SetUpRemoting }
                else {
                    $null = Ui 'Cancel' -Title '?*' -Optional -TimeoutSec 10
                    throw "Tools: '$($m.value)' from the menu didn't pass$(if ($setUpDone) { ' (after the setup)' }) - $answer"
                }
                $last = "after '$($m.value)'"
            }
            elseif ($m.found -and $err -like 'no menu entry*' -and @(@($m.entries) | Where-Object { $_ -eq 'PowerShell' }).Count) {
                Esc
                return $answer
            }
            else {
                # "Testing ..." (switched off while the test runs), or the menu not there yet: looked at again
                $last = if ($m.found) { "$err - entries: $(@($m.entries) -join ', ')" } else { "'Device' not found" }
                if ($m.found) { Esc }
                Start-Sleep -Seconds 3
            }
        } while ((Get-Date) -lt $until)
        throw "Tools: the connection's tools weren't offered within $TimeoutSec s ($last)."
    }
    # a tool tab from the menu (Tools>Entry), after the connection's test when Tools doesn't offer the tools (yet / again)
    function Tool([string]$Entry) {
        $m = Ui 'Device' -Value "menu:Tools>$Entry" -Optional
        if ($m.found -and -not ($m.PSObject.Properties['error'] -and $m.error)) { return }
        if ($m.found) { Esc }
        $null = ToolsReady -SetUp
        Menu "Tools>$Entry"
    }

    # Install RemoteHub Agent from the menu, silently over PowerShell remoting (offered only while WinRM is known to work:
    # tested first); "Use the agent for this connection afterwards" on. Answers what the steps showed.
    function InstallFromMenu {
        $a = [ordered]@{}
        $a.remoting = ToolsReady -SetUp
        Menu 'RemoteHub Agent>Install RemoteHub Agent*'
        $a.dialog = Ui 'Install silently over PowerShell remoting*' -Title 'Install RemoteHub Agent' -Value check -TimeoutSec 60
        $a.useAfter = Ui 'Use the agent for this connection afterwards' -Title 'Install RemoteHub Agent' -Value check -Optional
        $null = Ui 'Install' -Title 'Install RemoteHub Agent'
        $a.done = Ui 'Done' -Title 'Install RemoteHub Agent' -TimeoutSec 600
        $a.state = Get-LabAgentState -Name $device.Name
        $a
    }
    # the agent running on the device; installed from the menu when it isn't (the install context may not have run)
    function AgentReady {
        if ("$((Get-LabAgentState -Name $device.Name).Service.State)" -ne 'Running') { $null = InstallFromMenu }
        "$((Get-LabAgentState -Name $device.Name).Service.State)"
    }

    $device = Get-LabDevice -For $VmName
    if ($isClient) {
        Reset-LabVm -Name $device.Name
        $deviceIp = Get-LabVmAddress -Name $device.Name
        # RemoteHub installed (kept from an earlier file when it left the VM so)
        $null = Initialize-LabVm -Name $VmName -Client
        $clientIp = Get-LabVmAddress -Name $VmName
        $task = Start-LabClient -Name $VmName
        New-LabClientConnection -Name $VmName -Display 'Device' -TargetHost $deviceIp -User $device.AdminAccount -Password $password
    }
}

AfterAll {
    if ($isClient) {
        # the device's WinRM certificate trusted in the console account's root store (needs Windows' question to
        # remove): the next file starts from the checkpoint; the device has WinRM HTTPS and the agent
        Set-LabVmDirty -Name $VmName
        if ($device) { Set-LabVmDirty -Name $device.Name }
        Shot 'end'
        Stop-LabClient -Name $VmName -Task $task
        try { Save-LabAgentEvidence -Name $device.Name -Destination (Join-Path $EvidenceDir 'ui-agent-device-logs') } catch { }
        Stop-LabVm -Name $device.Name
    }
}

Describe 'UI-AGENT <VmName>' -Tag Client, UI, 'UI-AG', 'UI-TL', 'R-AGENT', 'W-TRUST' -Skip:(-not $isClient) {

    Context 'PowerShell remoting set up from RemoteHub' {
        BeforeAll {
            # the test fails (no HTTPS yet) and offers the setup
            Menu 'Tools>Test PowerShell remoting*'
            $s = SetUpRemoting
            Shot 'remoting'
            Save 'remoting' $s
        }

        It 'UI-TL-06 Set up on the target: the script saved, run on the device, Test again: available over HTTPS' -Tag 'UI-TL-06' {
            $s.test1 | Should -Match 'not|unavailable|couldn''t|can''t|refused' -Because 'before the setup WinRM HTTPS isn''t there'
            ($s.run -join "`n") | Should -Match 'Done\. PowerShell remoting is enabled over HTTPS'
            $s.result | Should -Match 'available|PowerShell \d'
        }

        It 'W-TRUST-01 Test again puts "RemoteHub*WinRM [host]" into CurrentUser\Root after Windows'' question' -Tag 'W-TRUST-01' {
            $s.trust.found | Should -BeTrue
            $s.windows.found | Should -BeTrue
            "$($s.trusted)" | Should -Match 'RemoteHub.*WinRM'
        }
    }

    Context 'tools over WinRM' {
        BeforeAll {
            $t = [ordered]@{}
            # Tools offers PowerShell once the remoting test has passed: run from the menu first (the setup context
            # left it passed; on its own the setup runs here)
            $t.test = ToolsReady -SetUp
            Menu 'Tools>PowerShell'
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
            $a = InstallFromMenu
            Shot 'installed'
            # Test RemoteHub Agent: only in the menu while the connection isn't known to work - after the install from
            # the menu it already is, and the menu offers the tools instead (documented)
            $m = Ui 'Device' -Value 'menu:Tools>Test RemoteHub Agent*' -Optional
            if ($m.PSObject.Properties['error']) { $a.entries = @($m.entries); Esc } else { $a.test = Message -Title $agentTitles -TimeoutSec 60 }
            Save 'install' $a
        }

        It 'UI-AG-01 Install RemoteHub Agent from the menu (silent over remoting): success, the device runs the agent, audit "agent installed"' -Tag 'UI-AG-01' {
            $a.done.found | Should -BeTrue
            $a.state.Service.State | Should -Be 'Running'
        }

        It 'UI-AG-02 Test RemoteHub Agent: works (PowerShell version and computer), or the tools offered once it is known to work' -Tag 'UI-AG-02' {
            # the dialog names the device's PowerShell, not the agent's version ("The RemoteHub Agent works (PowerShell 5.1 on <computer>).")
            if ($a.Contains('test')) { $a.test | Should -Match 'The RemoteHub Agent works \(PowerShell \d+\.\d+ on ' }
            else { foreach ($e in 'PowerShell', 'File transfer', 'Registry', 'Services', 'Processes') { $a.entries | Should -Contain $e } }
        }
    }

    Context 'tools through the agent' {
        BeforeAll {
            $r = [ordered]@{}
            # the agent from the install context (installed here when that context didn't run)
            $r.agent = AgentReady
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
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock { $d = 'C:\Users\Public\UpFolder'; $null = New-Item -ItemType Directory -Path $d -Force; 1..5 | ForEach-Object { Set-Content -Encoding ASCII "$d\f$_.txt" "ui file $_" } }
            $local = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-ChildItem 'C:\Users\Public\UpFolder' -File | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash } | Sort-Object) }
            Step files {
            Tool 'File transfer'
            # the address boxes by name: "Path This PC" and "Path <connection>  (<host>)" (each side has a filter box too)
            $null = Ui 'Path This PC' -Value 'keys:^aC:\Users\Public{ENTER}' -TimeoutSec 60
            $null = Ui 'Path Device*' -Value 'keys:^aC:\{ENTER}'
            Start-Sleep -Seconds 3
            $null = Ui 'UpFolder' -Value realclick
            $null = Ui 'Upload selected files'
            Start-Sleep -Seconds 15
            $r.uploaded = Invoke-LabGuest -Name $device.Name -ScriptBlock { @(Get-ChildItem 'C:\Users\*\Documents\UpFolder', 'C:\Users\*\UpFolder', 'C:\UpFolder' -File -ErrorAction SilentlyContinue | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash } | Sort-Object) }
            Shot 'files'
            # this PC's filter: "UpFol" leaves UpFolder and selects it, Enter opens it
            $null = Ui 'Filter This PC' -Value 'set:UpFol'
            $r.filterRows = @((Ui '*' -Value list).items | Where-Object { $_ -match '\|(f\d\.txt|UpFolder|Public|Documents)\|' })
            $null = Ui 'Filter This PC' -Value 'keys:{ENTER}'
            Start-Sleep -Seconds 2
            $r.filterOpened = (Ui 'Path This PC' -Value read).value
            $r.filterCleared = (Ui 'Filter This PC' -Value read).value
            # Ctrl+F with the focus on the device's side goes to that side's filter, not to the navigation search;
            # typed on the console keyboard (W, I, N), which writes into whatever has the focus
            $null = Ui 'Path Device*' -Value 'keys:^f'
            foreach ($k in '57', '49', '4E') { $null = Invoke-LabPerson -Name $VmName -Account Console -Do key -Value $k; Start-Sleep -Milliseconds 150 }
            Start-Sleep -Milliseconds 500
            $r.ctrlFDevice = (Ui 'Filter Device*' -Value read).value
            $r.ctrlFThisPc = (Ui 'Filter This PC' -Value read).value
            $r.ctrlFSearch = (Ui 'SearchBox' -Value read -Optional).value
            $null = Ui 'Filter Device*' -Value 'keys:{ESC}'
            Shot 'files-filter'
            }
            # registry
            # the key made on the device before the tab opens (the tab loads HKLM\SOFTWARE once and doesn't look again
            # when a path is typed), then opened by its path in the address box: set directly, Enter opens it
            $null = Invoke-LabGuest -Name $device.Name -ScriptBlock { $null = New-Item HKLM:\SOFTWARE\RemoteHubLab -Force }
            Step registry {
            Tool 'Registry'
            # the tab has to be connected and showing its start key first: a key typed before that isn't opened
            $null = Ui '* keys, * values*' -Value exists -TimeoutSec 90
            $null = Ui 'Registry key' -Value 'set:HKLM\SOFTWARE\RemoteHubLab' -TimeoutSec 60
            $null = Ui 'Registry key' -Value 'keys:{ENTER}'
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
            # read on the device until the value is there (the tab writes it in the background), for up to 20 s
            $until = (Get-Date).AddSeconds(20)
            do { $r.reg = Invoke-LabGuest -Name $device.Name -ScriptBlock { (Get-ItemProperty HKLM:\SOFTWARE\RemoteHubLab -ErrorAction SilentlyContinue).FromUi }; if ($r.reg) { break }; Start-Sleep -Seconds 2 } while ((Get-Date) -lt $until)
            # the value filter: "FromUi" selects the value, Enter opens it for editing ("Edit REG_SZ"; value data = 2nd Edit)
            $null = Ui 'Filter values' -Value 'set:FromUi'
            $null = Ui 'Filter values' -Value 'keys:{ENTER}'
            $r.regFilterEdit = (Ui 'type:Edit#2' -Title 'Edit REG*' -Value read -Optional -TimeoutSec 15).value
            $null = Ui 'Cancel' -Title 'Edit REG*' -Optional
            $null = Ui 'Filter values' -Value 'keys:{ESC}'
            }
            # services
            Step services {
            Tool 'Services'
            # the filter box is named "Filter"; the tool buttons are named by their tooltips; Dialogs.Confirm asks first
            $null = Ui 'Filter' -Value 'set:Spooler' -TimeoutSec 60
            # the first match is selected at once (the services list allows one selection: this used to throw), so the
            # service's buttons are on before any row is clicked; RemoteHub is still running
            # (the list loads over the agent for a few seconds; rows that arrive after the text was typed get the match too)
            $until = (Get-Date).AddSeconds(15)
            do { $r.serviceAutoSelected = [bool](Ui 'Stop the service' -Value exists -Optional).enabled; if ($r.serviceAutoSelected) { break }; Start-Sleep -Seconds 1 } while ((Get-Date) -lt $until)
            $r.aliveAfterFilter = [bool](Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process RemoteHub -ErrorAction SilentlyContinue })
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
            Tool 'Processes'
            $null = Ui 'Filter' -Value 'set:notepad' -TimeoutSec 60
            $null = Ui 'notepad*' -Value realclick
            $null = Ui 'End process (Del)'
            $null = Ui 'End process' -Title '?*'
            Start-Sleep -Seconds 3
            $r.notepadGone = -not (Invoke-LabGuest -Name $device.Name -ArgumentList $notepad -ScriptBlock { param($p) [bool](Get-Process -Id $p -ErrorAction SilentlyContinue) })
            $null = Ui 'Filter' -Value 'set:lsass'
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
            # The tab's optional Connect (up to 15 s) and the person's start ran past the 30 s countdown on Server 2025
            # (runs 20261025-052539 and -132336: refused at 30 s, no agent window seen): a longer countdown, as in D-CLIP
            Set-LabAgentPolicy -Name $device.Name -Values @{ ConsentTimeoutSeconds = 90 }
            # with the agent in use, Connect opens the agent desktop ("Remote desktop via RDP" is the other way)
            Menu 'Connect'
            # the tab signs in first and then asks what to show ("Signed in as ... Choose what to show."): its Connect
            $null = Ui 'Connect' -Optional -TimeoutSec 15
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
            # (this context's own record: another context's variables aren't visible here)
            $r.agent | Should -Be 'Running'
            @($r.uploaded).Count | Should -Be 5
            $r.desktop.found | Should -BeTrue
        }

        It 'UI-TL-10 a file pane''s filter narrows the folder to the matches and selects the first, so Enter opens it' -Tag 'UI-TL-10' {
            ($r.filterRows -join "`n") | Should -Match '\|UpFolder\|'
            ($r.filterRows -join "`n") | Should -Not -Match '\|Documents\|'
            $r.filterOpened | Should -Be 'C:\Users\Public\UpFolder'
            $r.filterCleared | Should -BeNullOrEmpty -Because 'another folder starts unfiltered'
        }

        It 'UI-TL-11 Ctrl+F goes to the filter of the side that has the focus, not to the navigation search' -Tag 'UI-TL-11' {
            $r.ctrlFDevice | Should -Be 'win'
            $r.ctrlFThisPc | Should -BeNullOrEmpty
            $r.ctrlFSearch | Should -Not -Be 'win'
        }

        It 'UI-TL-12 the registry''s value filter selects the matching value; Enter edits it' -Tag 'UI-TL-12' {
            $r.regFilterEdit | Should -Be 'set in the registry tab'
        }

        It 'UI-TL-13 the services filter selects the first match without an error (the list allows one selection)' -Tag 'UI-TL-13' {
            $r.aliveAfterFilter | Should -BeTrue
            $r.serviceAutoSelected | Should -BeTrue
        }
    }

    Context 'the agent uninstalled from RemoteHub' {
        BeforeAll {
            $x = [ordered]@{}
            # installed from RemoteHub (its certificate pinned), so the uninstall goes through the agent's own PowerShell
            $x.agent = AgentReady
            $x.menuBefore = (AgentMenu)
            Esc
            Menu 'RemoteHub Agent>Uninstall RemoteHub Agent*'
            # the question names what goes; Uninstall starts it (the progress window has the same title)
            # the question is a message box: inside the main window, named by its text ('?*'); the progress is a window of
            # its own, titled Uninstall RemoteHub Agent
            $x.question = (@((Ui '*' -Title '?*' -Value list -TimeoutSec 30).items) -join "`n")
            $null = Ui 'Uninstall' -Title '?*'
            $x.done = (Ui 'Done' -Title 'Uninstall RemoteHub Agent' -Value exists -Optional -TimeoutSec 300).found
            $x.progress = (@((Ui '*' -Title 'Uninstall RemoteHub Agent' -Value list -Optional -TimeoutSec 5).items) -join "`n")
            Shot 'uninstalled'
            $null = Ui 'Done' -Title 'Uninstall RemoteHub Agent' -Optional
            # the agent goes silent early in the uninstall (its service is stopped first); msiexec goes on removing the rest:
            # the device is checked until msiexec has ended and nothing is left, for up to 3 minutes
            $until = (Get-Date).AddMinutes(3)
            do {
                $x.device = Invoke-LabGuest -Name $device.Name -ScriptBlock {
                    $log = "$env:SystemRoot\Temp\RemoteHub-Agent-uninstall.log"
                    [pscustomobject]@{
                        Msiexec  = @(Get-CimInstance Win32_Process -Filter "Name = 'msiexec.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -match '/x' }).Count
                        Service  = [bool](Get-Service RemoteHubAgent -ErrorAction SilentlyContinue)
                        Product  = [bool](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'RemoteHub Agent' })
                        Firewall = [bool](Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'RemoteHub Agent*' })
                        Provider = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{718FFFD2-6671-4FA4-9FA8-86D1CD4D84C3}'
                        Log      = Test-Path $log
                        LogTail  = @(Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_ -match 'Return value 3|Error \d{4}|MainEngineThread is returning|Removal completed' } | Select-Object -Last 6)
                    }
                }
                if ($x.device.Msiexec -eq 0 -and -not ($x.device.Service -or $x.device.Firewall -or $x.device.Provider)) { break }
                Start-Sleep -Seconds 10
            } while ((Get-Date) -lt $until)
            $x.menuAfter = (AgentMenu)
            Esc
            Save 'uninstall' $x
        }

        It 'UI-AG-17 Uninstall RemoteHub Agent from the menu: the device has no agent, firewall rule or credential provider left; the connection offers Install again' -Tag 'UI-AG-17' {
            $x.agent | Should -Be 'Running'
            ($x.menuBefore -join ', ') | Should -Match 'Uninstall RemoteHub Agent'
            $x.question | Should -Match 'credential provider, certificate and firewall rule'
            $x.done | Should -BeTrue
            $x.progress | Should -Match 'no longer runs the RemoteHub Agent'
            $x.device.Service | Should -BeFalse
            $x.device.Product | Should -BeFalse
            $x.device.Firewall | Should -BeFalse
            $x.device.Provider | Should -BeFalse
            $x.device.Log | Should -BeTrue
            ($x.menuAfter -join ', ') | Should -Match 'Install RemoteHub Agent'
            ($x.menuAfter -join ', ') | Should -Not -Match 'Uninstall RemoteHub Agent|Update RemoteHub Agent'
        }
    }

    Context 'refusals as RemoteHub shows them' {
        BeforeAll {
            $f = [ordered]@{}
            # the agent in use and known to work, so Tools offers PowerShell (installed here when the earlier contexts
            # didn't run)
            $f.agent = AgentReady
            $f.test = ToolsReady
            # the agent stopped: the tab says so, no hang; started again: works on retry
            $null = Invoke-LabGuest -Name $device.Name -ScriptBlock { Stop-Service RemoteHubAgent -Force }
            # the tab's own text (the output) and whatever dialog shows
            function TabText { "$((Ui 'type:Document' -Value read -Optional -TimeoutSec 20).value)`n$((Ui '*' -Title '?*' -Value list -Optional -TimeoutSec 5).items -join "`n")" }
            Menu 'Tools>PowerShell'
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
            # the tab while Tools offers PowerShell; when it doesn't (the reconnect above failed, so the connection's last
            # result is a failure) the test from the menu, whose answer names the certificate as well - closed with Cancel
            $m = Ui 'Device' -Value 'menu:Tools>PowerShell' -Optional
            if ($m.found -and -not ($m.PSObject.Properties['error'] -and $m.error)) { Start-Sleep -Seconds 15; $f.pinMessage = TabText }
            else {
                if ($m.found) { Esc }
                $null = Ui 'Device' -Value 'menu:Tools>Test RemoteHub Agent*' -TimeoutSec 15
                $f.pinMessage = Message -Title $agentTitles -Button 'Cancel' -TimeoutSec 90
            }
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

    Context 'the agent removed here only when the device can''t be reached that way' {
        BeforeAll {
            $h = [ordered]@{}
            # after the refusals the device runs an agent with a certificate RemoteHub didn't make: the uninstall through
            # it fails, and RemoteHub offers to remove the agent from the connection here only
            $h.deviceBefore = "$((Get-LabAgentState -Name $device.Name).Service.State)"
            Menu 'RemoteHub Agent>Uninstall RemoteHub Agent*'
            $null = Ui 'Uninstall' -Title '?*' -TimeoutSec 30
            $h.offer = (Ui 'Remove here' -Title '?*' -Value exists -Optional -TimeoutSec 300).found
            $h.offerText = (@((Ui '*' -Title '?*' -Value list -Optional -TimeoutSec 5).items) -join "`n")
            Shot 'remove-here'
            $null = Ui 'Remove here' -Title '?*' -Optional
            $null = Ui 'Close' -Title 'Uninstall RemoteHub Agent' -Optional -TimeoutSec 5
            $h.deviceAfter = "$((Get-LabAgentState -Name $device.Name).Service.State)"
            $h.menuAfter = (AgentMenu)
            Esc
            Save 'remove-here' $h
        }

        It 'UI-AG-18 an uninstall that can''t reach the agent offers Remove here: the connection forgets the agent, the device keeps it' -Tag 'UI-AG-18' {
            $h.deviceBefore | Should -Be 'Running'
            $h.offer | Should -BeTrue
            $h.offerText | Should -Match 'agent stays on the device'
            $h.deviceAfter | Should -Be 'Running' -Because 'Remove here changes nothing on the device'
            ($h.menuAfter -join ', ') | Should -Match 'Install RemoteHub Agent'
            ($h.menuAfter -join ', ') | Should -Not -Match 'Uninstall RemoteHub Agent'
        }
    }
}
