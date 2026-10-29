# POL (RemoteHub) - the Group Policy template in the editor, both products on one VM, Settings -> Policies and settings
# greyed out by policy (docs/admx-design.md sections 4 and 16), console features hidden and refused (section 5, POL-14),
# settings and connection settings by policy (sections 6 and 7, POL-15). RemoteHub and the agent are installed on the
# same client VM; RemoteHub runs in the console account's session, driven by UI Automation. Console values are written
# the way the template writes them (HKLM, or the console account's HKCU). POL-14 and POL-15 pair RemoteHub with the agent
# on this VM itself and use WinRM to the lane's device (WinRM from a standard session into the same machine is refused,
# so 127.0.0.1 can't stand in for it); the RDP session's own drives can't be looked at here (no RDP target), the .rdp
# file RemoteHub writes for the connection is checked instead. Client VMs only. See docs/test-lab-cases.md section 10.7.
# LabRole: console
# LabNeeds: lane
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $isClient = Test-LabConsoleVm -Name $VmName
    # gpedit's window title and the template's fallback language are checked on English Windows only
    $isEnglish = (Get-LabVm $VmName).Id -notlike '*de-DE*'
}

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $cfg = Get-LabConfig
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "pol-client-$name.json") -Encoding UTF8 }
    function Shot([string]$name) { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "pol-client-$name.png") }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    # a console policy value as the template writes it: HKLM, or HKCU of the console account; -Key a subkey
    # (Connections, AgentInstall). $null removes it, a number is a DWORD, a list a MULTI_SZ.
    function Policy([hashtable]$Values, [string]$Hive = 'HKLM', [string]$Key = '') {
        Invoke-LabGuest -Name $VmName -ArgumentList $Values, $Hive, $vm.ConsoleAccount, $Key -ScriptBlock {
            param($values, $hive, $account, $sub)
            $k = if ($hive -eq 'HKLM') { 'HKLM:\SOFTWARE\Policies\RemoteHub' } else {
                $sid = (New-Object Security.Principal.NTAccount $account).Translate([Security.Principal.SecurityIdentifier]).Value
                "Registry::HKEY_USERS\$sid\SOFTWARE\Policies\RemoteHub" }
            if ($sub) { $k = "$k\$sub" }
            if (-not (Test-Path $k)) { $null = New-Item $k -Force }
            foreach ($n in $values.Keys) {
                if ($null -eq $values[$n]) { Remove-ItemProperty $k -Name $n -ErrorAction SilentlyContinue }
                elseif ($values[$n] -is [int]) { Set-ItemProperty $k -Name $n -Value $values[$n] -Type DWord }
                # (an array arrives over PowerShell Direct as a list)
                elseif ($values[$n] -is [System.Collections.IList]) { Set-ItemProperty $k -Name $n -Value ([string[]]@($values[$n])) -Type MultiString }
                else { Set-ItemProperty $k -Name $n -Value $values[$n] -Type String }
            }
        }
    }
    function Restart-Client { if ($script:task) { Stop-LabClient -Name $VmName -Task $script:task }; $script:task = Start-LabClient -Name $VmName }
    function Settings-Page([string]$Page) { $null = Ui 'Settings'; $null = Ui $Page -Title 'Settings' }
    function Settings-Text { (Ui '*' -Title 'Settings' -Value list).items -join "`n" }
    function Policies-Text { Settings-Page 'Policies'; $t = Settings-Text; $null = Ui 'Cancel' -Title 'Settings'; $t }
    $script:task = $null

    # ---- POL-14 / POL-15: connections in RemoteHub, the agent paired with it, WinRM on this VM
    $password = $cfg.Password
    $script:setup = $null
    # a key at the console keyboard (hex virtual-key code: 1B Esc)
    # (the pause lets the key take effect - a menu close - before the next step looks at the window)
    function Key([string]$Hex) { $null = Invoke-LabPerson -Name $VmName -Account Console -Do key -Value $Hex; Start-Sleep -Milliseconds 400 }
    # the condition's first answer that counts as true, asked every -Every seconds; $null after -TimeoutSec
    function WaitFor([int]$TimeoutSec, [scriptblock]$Condition, [int]$Every = 3) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            $r = & $Condition
            if ($r) { return $r }
            Start-Sleep -Seconds $Every
        } while ((Get-Date) -lt $until)
        $null
    }
    # each section on its own: a failing step is written down (error, screenshot, open dialogs) and the next one still
    # runs; dot-sourced, so the result dictionary of the context is filled
    function Step([System.Collections.IDictionary]$R, [string]$Name, [scriptblock]$Do) {
        try { . $Do }
        catch {
            $R["$Name-error"] = "$_"
            try { Shot "fail-$Name" } catch { }
            try { $R["$Name-dialogs"] = @((Invoke-LabUi -Name $VmName -Text '*' -Title '?*' -Value list -Optional -TimeoutSec 3).items | Select-Object -First 60) } catch { }
            foreach ($i in 1..2) { try { Key 1B } catch { } }
        }
    }
    # the steps that failed, with the setup's: what an It reports first
    function Errors([System.Collections.IDictionary]$R) {
        @(@($R, $script:setup) | Where-Object { $_ } | ForEach-Object { $d = $_; @($d.Keys) | Where-Object { "$_" -like '*-error' } | ForEach-Object { "${_}: $($d[$_])" } })
    }
    function Has([object[]]$List, [string]$Like) { [bool]@($List | Where-Object { "$_" -like $Like }).Count }
    # the text of one of RemoteHub's message boxes (its window title -Title), closed with -Button; '' when none showed
    function Message([string]$Title, [string]$Button = 'OK', [int]$TimeoutSec = 60) {
        $m = Ui '*' -Title $Title -Value list -TimeoutSec $TimeoutSec -Optional
        if (-not $m.found) { return '' }
        $null = Ui $Button -Title $Title -Optional -TimeoutSec 5
        @($m.items) -join "`n"
    }
    # The entries a context menu offers: an entry that isn't there makes RhPerson list every menu entry on the screen.
    # -Node: a connection in the tree (right-click), or with -Pick a button (left-click: "New", the tab bar's "+").
    # -Parent opens that submenu first (Tools, RemoteHub Agent, More); a submenu that isn't there is skipped, so the
    # answer is the menu itself then. The menu is closed again (RhPerson closes only the innermost level).
    function MenuEntries([string]$Node, [string]$Parent = '', [switch]$Pick) {
        $path = if ($Parent) { "$Parent>zz-no-such-entry" } else { 'zz-no-such-entry' }
        $m = Ui $Node -Value "$(if ($Pick) { 'pick' } else { 'menu' }):$path" -Optional -TimeoutSec 20
        Key 1B
        if (-not $m.found) { throw "MenuEntries: '$Node' not found (windows: $(@($m.windows) -join ', '))" }
        @(@($m.entries) | Where-Object { $_ })
    }
    # Tools lists the tools only once the connection's test passed the way it connects now (agent or WinRM); until then it
    # has "Test RemoteHub Agent" / "Test PowerShell remoting". This runs the test from the menu and reads its answer until
    # the tools are offered (as UI-AGENT's ToolsReady, without the WinRM setup).
    function ToolsReady([string]$Node, [int]$TimeoutSec = 240) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        $last = ''
        do {
            $m = Ui $Node -Value 'menu:Tools>Test*' -Optional -TimeoutSec 15
            $err = if ($m.PSObject.Properties['error']) { "$($m.error)" } else { '' }
            if ($m.found -and -not $err) {
                # the answer's dialog under either title: the menu click doesn't always name the entry it took (lab run
                # 20261019-190129 looked for "PowerShell remoting", missed the agent's "works" and left that dialog open)
                # UIA names the box by its text ("The RemoteHub Agent works (...)", run 20261021-062735), not only by its
                # title: the patterns UI-AGENT uses, and the box closed under the name it was found by
                $answer = ''; $title = ''
                $wait = (Get-Date).AddSeconds(180)
                do {
                    foreach ($t in 'The RemoteHub Agent*', 'RemoteHub Agent', 'PowerShell remoting*', '*answers over HTTPS*') {
                        $box = Ui '*' -Title $t -Value list -Optional -TimeoutSec 2
                        if ($box.found) { $title = [WildcardPattern]::Escape("$($box.window)"); $answer = @($box.items) -join "`n"; break }
                    }
                } while (-not $title -and (Get-Date) -lt $wait)
                if ($answer -match 'works \(') { $null = Ui 'OK' -Title $title -Optional -TimeoutSec 10 }
                else {
                    if ($title) { $null = Ui 'Cancel' -Title $title -Optional -TimeoutSec 5 }
                    throw "${Node}: '$($m.value)' didn't pass - $(if ($title) { $answer } else { 'no answer within 180 s' })"
                }
                $last = "after '$($m.value)'"
            }
            elseif ($m.found -and $err -like 'no menu entry*' -and @($m.entries) -contains 'PowerShell') { Key 1B; return }
            else {
                # "Testing ..." (switched off while the test runs), or the menu not there yet: looked at again
                $last = if ($m.found) { "$err - entries: $(@($m.entries) -join ', ')" } else { "'$Node' not found" }
                if ($m.found) { Key 1B }
                Start-Sleep -Seconds 3
            }
        } while ((Get-Date) -lt $until)
        throw "${Node}: the tools weren't offered within $TimeoutSec s ($last)."
    }
    # every tab closed: the group's x ("Close this tab" shows only with more than one tab of a connection)
    function CloseTabs {
        foreach ($i in 1..8) {
            $c = Ui 'Close all tabs of this connection' -Optional -TimeoutSec 3
            if (-not $c.found) { $c = Ui 'Close this tab' -Optional -TimeoutSec 2 }
            if (-not $c.found) { break }
            $null = WaitFor 10 { -not (Ui 'Reconnect' -Value exists -Optional -TimeoutSec 1).found } -Every 1
        }
    }
    # the connection's (or folder's) editor: -Do on it, then Save or Cancel
    function Editor([string]$Node, [string]$Title, [scriptblock]$Do, [string]$Button = 'Cancel') {
        $null = Ui $Node -Value 'menu:Edit?'
        $null = Ui 'Save' -Title $Title -Value exists -TimeoutSec 30
        try { & $Do } finally { $null = Ui $Button -Title $Title -Optional -TimeoutSec 10 }
    }
    # a row of the editor's option pages: the value of the ComboBox after its label, and whether it can be changed
    function Row([string]$Title, [string]$Label) {
        $r = Ui $Label -Title $Title -Value readnext
        [ordered]@{ value = "$($r.value)"; enabled = $r.enabled }
    }
    # "Save as .rdp file..." of a connection to -Path in the guest; answers the file's text ('' when none was written)
    function Export-Rdp([string]$Node, [string]$Path) {
        Invoke-LabGuest -Name $VmName -ArgumentList $Path -ScriptBlock { param($p) $null = New-Item (Split-Path $p) -ItemType Directory -Force; Remove-Item $p -ErrorAction SilentlyContinue }
        # opened right after an editor closed, the menu came up with no entries at all (run 20261022-132024,
        # drivesAfter): opened again, for up to 10 s
        $until = (Get-Date).AddSeconds(10)
        while ($true) {
            try { $null = Ui $Node -Value 'menu:More>Save as .rdp file*'; break }
            catch {
                if ("$_" -notmatch 'no menu entry' -or (Get-Date) -gt $until) { throw }
                try { Send-LabScancodes -Name $VmName -Codes 0x01 } catch { }   # Esc: the empty menu closed
                Start-Sleep -Seconds 2
            }
        }
        # Windows' Save As dialog has no usable UIA patterns here (as in UI-RDP): once it is listed, its file name box has
        # the focus with the proposed name selected - typed over at the console keyboard
        $null = WaitFor 30 { @(@((Ui '*' -Value list -Optional -TimeoutSec 5).items) | Where-Object { "$_" -match '^Window\|(Save As|Speichern unter)\|' }).Count }
        Send-LabText -Name $VmName -Text $Path
        Send-LabKey -Name $VmName -Key Enter
        # a file that goes between Test-Path and the read (saved through a temporary one and moved into place - run
        # 20261022-154825: "Could not find file") counts as not there yet: read again at the next turn
        "$(WaitFor 30 { Invoke-LabGuest -Name $VmName -ArgumentList $Path -ScriptBlock { param($p) try { if (Test-Path $p) { [IO.File]::ReadAllText($p) } } catch { $null } } })"
    }
    # the agent's request at this VM's own console (the agent desktop of the console session): Allow, by OCR and the
    # console mouse; tried again until -TimeoutSec
    function AllowAtConsole([int]$TimeoutSec = 60) {
        WaitFor $TimeoutSec {
            $r = try { Invoke-LabPerson -Name $VmName -Account Console -Do click -Text 'Allow' -TimeoutSec 20 } catch { $null }
            if ($r -and $r.PSObject.Properties['clicked'] -and $r.clicked) { $r }
        } -Every 2
    }
    # the mean brightness (0..1) of a part of a screenshot (x, y, width, height), every 4th pixel each way
    function Brightness([string]$Path, [int[]]$Rect) {
        Add-Type -AssemblyName System.Drawing
        $bmp = [Drawing.Bitmap]::FromFile($Path)
        try {
            $x0 = [math]::Max(0, $Rect[0]); $y0 = [math]::Max(0, $Rect[1])
            $x1 = [math]::Min($bmp.Width, $Rect[0] + $Rect[2]); $y1 = [math]::Min($bmp.Height, $Rect[1] + $Rect[3])
            $sum = 0.0; $n = 0
            for ($y = $y0; $y -lt $y1; $y += 4) { for ($x = $x0; $x -lt $x1; $x += 4) { $c = $bmp.GetPixel($x, $y); $sum += (0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B) / 255; $n++ } }
            [math]::Round($sum / [math]::Max(1, $n), 3)
        }
        finally { $bmp.Dispose() }
    }
    # RemoteHub's main window moved to the top left inside the screen ("fit"), its rectangle; a screenshot of it and the
    # brightness of that part
    function MainWindowBrightness([string]$Name) {
        $v = "$((Ui 'NewButton' -Value fit).value)"
        $n = @([regex]::Matches($v, '-?\d+') | ForEach-Object { [int]$_.Value })
        $rect = if ($n.Count -ge 4) { [int[]]$n[0..3] } else { [int[]]@(0, 0, 800, 600) }
        Shot $Name
        Brightness (Join-Path $EvidenceDir "pol-client-$Name.png") $rect
    }

    # What POL-14 and POL-15 work with, made once (the first of them that runs): WinRM on this VM for 127.0.0.1, and in
    # RemoteHub
    # - PolLocal: this VM by its name, the console account; the agent installed through RemoteHub's clipboard install
    #   (the lab's own agent from the start of the file removed first), so RemoteHub has paired with it and uses it;
    # - PolWinRm: the lane's device by its address, its administrator (PSAdmin), WinRM ("Check automatically after RDP
    #   login" on). Not this VM: WinRM from a standard (non-elevated) session back into the same machine is refused
    #   ("Access is denied") for every account and every address of it - 127.0.0.1, its own IP, by name - while the same
    #   sign-in from an elevated process works (checked on the VM after runs 20261022-092309 and -124318);
    # - PolRdp: an address that never answers (TEST-NET-1), with an account - an RDP tab that tries without a prompt;
    # - PolOffline: another such address, no account - for redirection, .rdp export and Connect;
    # - PolFolder: a folder, for its editor.
    # The test answers of PolLocal (agent) and PolWinRm (WinRM) have passed, so Tools offers their tools.
    function Ensure-Setup {
        if ($null -ne $script:setup) { return }
        $u = [ordered]@{}
        $script:setup = $u
        Step $u winrm {
            # WinRM on the device, for its administrator over the network (a local administrator's full token:
            # LocalAccountTokenFilterPolicy, which Enable-PSRemoting sets only on client Windows)
            $u.winrm = Invoke-LabGuest -Name $device.Name -ScriptBlock {
                Enable-PSRemoting -Force -SkipNetworkProfileCheck | Out-Null
                Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name LocalAccountTokenFilterPolicy -Value 1 -Type DWord
                "$((Get-Service WinRM).Status)"
            }
            # an IP address and explicit credentials over HTTP need the address in TrustedHosts on this PC
            $null = Invoke-LabGuest -Name $VmName -ArgumentList $deviceIp -ScriptBlock {
                param($ip)
                Set-Service WinRM -StartupType Manual; Start-Service WinRM
                Set-Item WSMan:\localhost\Client\TrustedHosts -Value $ip -Force
            }
        }
        $u.computer = Invoke-LabGuest -Name $VmName -ScriptBlock { $env:COMPUTERNAME }
        Restart-Client
        Step $u connections {
            New-LabClientConnection -Name $VmName -Display 'PolLocal' -TargetHost $u.computer -User $vm.ConsoleAccount -Password $password
            New-LabClientConnection -Name $VmName -Display 'PolWinRm' -TargetHost $deviceIp -User $device.AdminAccount -Password $password
            New-LabClientConnection -Name $VmName -Display 'PolRdp' -TargetHost '192.0.2.10' -User $vm.ConsoleAccount -Password $password
            New-LabClientConnection -Name $VmName -Display 'PolOffline' -TargetHost '192.0.2.11'
            # selected first: the folder lands at the top level, next to it
            $null = Ui 'PolOffline' -Value realclick
            $null = Ui 'SearchBox' -Value 'keys:^+n'
            $null = Ui 'Display name' -Title 'New folder' -Value 'setnext:PolFolder'
            $null = Ui 'Save' -Title 'New folder'
            $null = Ui 'PolFolder' -Value exists
            Editor 'PolWinRm' 'Edit connection' -Button 'Save' {
                $null = Ui 'PowerShell remoting' -Title 'Edit connection'
                $null = Ui 'Check automatically after RDP login' -Title 'Edit connection' -Value 'choose:On'
            }
        }
        Step $u pair {
            $u.uninstall = Uninstall-LabAgent -Name $VmName
            $null = Ui 'PolLocal' -Value 'menu:RemoteHub Agent>Install RemoteHub Agent*'
            $null = Ui 'Copy the installer and the enrollment file to the clipboard*' -Title 'Install RemoteHub Agent' -Value check -TimeoutSec 60
            $null = Ui 'Install' -Title 'Install RemoteHub Agent'
            $null = Ui 'Done' -Title '*RemoteHub Agent' -Value exists -TimeoutSec 60
            $null = Ui 'Done' -Title '*RemoteHub Agent' -Optional
            # the two files RemoteHub put on the clipboard (its staging folder in the console account's %TEMP%)
            $raw = "$(Invoke-LabInSession -Name $VmName -Account Console -ScriptBlock { Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Clipboard]::GetFileDropList() | ForEach-Object { "FILE:$_" } })"
            $files = @([regex]::Matches($raw, '(?m)^FILE:(.+?)\s*$') | ForEach-Object { $_.Groups[1].Value })
            $u.files = @($files | ForEach-Object { Split-Path $_ -Leaf })
            if (-not @($files | Where-Object { $_ -like '*.msi' }).Count) { throw "RemoteHub's clipboard install put no MSI on the clipboard: $raw" }
            Install-LabSigningCertificate -Name $VmName
            # installed by hand, as the admin would run the MSI next to the enrollment file; the .rhagent holds
            # RemoteHub's enrollment secret and is removed afterwards
            $u.exit = Invoke-LabGuest -Name $VmName -ArgumentList (, [string[]]$files) -ScriptBlock {
                param([string[]]$f)
                $d = 'C:\LabAgentByHand'
                if (Test-Path $d) { Remove-Item $d -Recurse -Force }
                $null = New-Item -ItemType Directory -Path $d -Force
                Copy-Item -LiteralPath $f -Destination $d
                $msi = Get-ChildItem $d -Filter 'RemoteHub-Agent*.msi' | Select-Object -First 1
                $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$($msi.FullName)`"", '/qn', '/norestart', '/l*v', "`"$d\install.log`"" -Wait -PassThru
                Get-ChildItem $d -Filter *.rhagent | Remove-Item -Force
                $p.ExitCode
            }
            # RemoteHub asks the agent every 10 s; when it answers with the device certificate it writes this line
            $log = "C:\Users\$($vm.ConsoleAccount)\AppData\Roaming\RemoteHub\logs\remotehub.log"
            $u.paired = [bool](WaitFor 180 { Invoke-LabGuest -Name $VmName -ArgumentList $log -ScriptBlock { param($l) [bool](Select-String -Path $l -Pattern 'installed by hand' -SimpleMatch -Quiet -ErrorAction SilentlyContinue) } } -Every 5)
            if (-not $u.paired) { throw "RemoteHub didn't mark PolLocal's agent installed within 180 s (no 'installed by hand' in its diagnostic log)" }
        }
        Step $u tools {
            ToolsReady 'PolLocal'
            ToolsReady 'PolWinRm'
        }
        # RemoteHub's own record of a refused test (run 20261021-222745: WinRM refused PolWinRm's account although the
        # same sign-in worked from the VM itself): its diagnostic log next to the evidence
        if ($u['tools-error']) {
            try { Get-LabFile -Name $VmName -Path "C:\Users\$($vm.ConsoleAccount)\AppData\Roaming\RemoteHub\logs\remotehub.log" -Destination (Join-Path $EvidenceDir 'pol-client-remotehub-log') } catch { }
        }
        Shot 'setup'
        Save 'setup' $u
    }

    if ((Test-LabConsoleVm -Name $VmName)) {
        # the lane's device: PolWinRm's WinRM target
        $device = Get-LabDevice -For $VmName
        Reset-LabVm -Name $device.Name
        $deviceIp = Get-LabVmAddress -Name $device.Name
        Reset-LabVm -Name $VmName
        $install = Install-LabAgent -Name $VmName
        $pin = $install.pin
        # Install-LabClient sets DisableUpdateCheck and DisableTelemetry: removed again, the cases set their own
        $null = Install-LabClient -Name $VmName
        Policy @{ DisableUpdateCheck = $null; DisableTelemetry = $null }
    }
}

AfterAll {
    if ((Test-LabConsoleVm -Name $VmName)) {
        Shot 'end'
        Stop-LabClient -Name $VmName -Task $script:task
        Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'pol-client-agent-logs')
        # WinRM, TrustedHosts and the agent RemoteHub paired with (POL-14 / POL-15): not for the next file
        Set-LabVmDirty -Name $VmName
        if ($device) { Set-LabVmDirty -Name $device.Name }
    }
}

Describe 'POL-CLIENT <VmName>' -Tag Client, UI, 'POL' -Skip:(-not $isClient) {

    Context 'the template in the Group Policy editor' -Skip:(-not $isEnglish) {
        BeforeAll {
            $t = [ordered]@{}
            # the build's template, published by Start-LabRun.ps1 next to the MSIs
            $policy = Join-Path $cfg.LabRoot 'policy'
            Copy-LabFile -Name $VmName -Path (Join-Path $policy 'RemoteHub.admx') -Destination 'C:\Windows\PolicyDefinitions'
            Copy-LabFile -Name $VmName -Path (Join-Path $policy 'en-US\RemoteHub.adml') -Destination 'C:\Windows\PolicyDefinitions\en-US'
            $editor = Start-LabInSession -Name $VmName -Account Console -ScriptBlock { & "$env:SystemRoot\System32\mmc.exe" "$env:SystemRoot\System32\gpedit.msc" }
            # mmc.exe asks for the highest available rights, so the editor runs elevated for the console account (an
            # administrator). UI Automation from the person server (a limited token) sees only its window frames then - no
            # tree items, no menu (UIPI). So the editor is read by RhPerson run elevated in the same session.
            & (Get-Module RemoteHub.TestLab) { param($n) Copy-LabPerson -Name $n } $VmName
            function GpEdit([string]$Text, [string]$Value, [string]$Title = 'Local Group Policy Editor', [int]$TimeoutSec = 30) {
                $out = "C:\ProgramData\RemoteHubLab\session\pol-gpedit-$([guid]::NewGuid().ToString('N').Substring(0, 8)).json"
                $q = { param($s) "'" + ($s -replace "'", "''") + "'" }
                $cmd = "& 'C:\ProgramData\RemoteHubLab\RhPerson.ps1' -Do uia -Title $(& $q $Title) -Text $(& $q $Text) -Value $(& $q $Value) -TimeoutSec $TimeoutSec -Out '$out'"
                $raw = Invoke-LabInSession -Name $VmName -Account Console -Elevated -TimeoutSec ($TimeoutSec + 60) -ScriptBlock ([scriptblock]::Create($cmd))
                # RhPerson writes UTF-8 without a BOM
                $json = Invoke-LabGuest -Name $VmName -ArgumentList $out -ScriptBlock { param($f) if (Test-Path $f) { [IO.File]::ReadAllText($f, [Text.Encoding]::UTF8); Remove-Item $f } }
                if (-not $json) { throw "RhPerson -Do uia '$Text' in the elevated session gave no answer: $raw" }
                $json | ConvertFrom-Json
            }
            $t.window = GpEdit '*' exists -TimeoutSec 90
            # Computer Configuration -> Administrative Templates -> RemoteHub, opened in the results list: the scope tree
            # shows no items to UI Automation and the editor opens with only the two configurations listed (run
            # 20261017-054704: 'Computer Configuration' / 'User Configuration' ListItems, an empty 'Console Embedded Scope')
            $t.computer = GpEdit 'Computer Configuration' doubleclick
            $null = GpEdit 'Administrative Templates' doubleclick
            # the editor reads the templates when Administrative Templates is opened: one it can't read shows a dialog
            # titled "Administrative Templates" with the file and line
            $t.error = GpEdit '*' list -Title 'Administrative Templates' -TimeoutSec 5
            $null = GpEdit 'RemoteHub' doubleclick
            # both branches listed under RemoteHub
            $t.agentBranch = GpEdit 'RemoteHub Agent' exists
            $t.tree = (GpEdit '*' list).items -join "`n"
            Shot 'gpedit'
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process mmc -ErrorAction SilentlyContinue | Stop-Process -Force }
            Stop-LabInSession -Name $VmName -Task $editor
            Save 'gpedit' $t
        }

        It 'POL-01 gpedit.msc reads the template without an error and shows both branches under RemoteHub' -Tag 'POL-01' {
            $t.window.found | Should -BeTrue
            $t.error.found | Should -Not -BeTrue -Because "a template the editor can't read shows an 'Administrative Templates' error dialog"
            # whole items ("<type>|<name>|<id>" lines of the list): "RemoteHub Agent features" (under the console) must not
            # count as the agent's branch
            $t.tree | Should -Match '(?m)^ListItem\|RemoteHub Console\|'
            $t.tree | Should -Match '(?m)^ListItem\|RemoteHub Agent\|'
        }
    }

    Context 'both products on one VM' {
        BeforeAll {
            $b = [ordered]@{}
            # the agent's policy: the remote desktop to this VM off
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ DesktopEnabled = 0 }
            $b.desktop = try { Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Join-Path $EvidenceDir 'pol-client-desktop') -Steps @(@{ do = 'connect' }, @{ do = 'close' }) } catch { [pscustomobject]@{ error = "$_" } }
            # RemoteHub's policy at the root key: not read by the agent
            Policy @{ DisableRecording = 1 }
            $b.agentReport = Invoke-LabGuest -Name $VmName -ScriptBlock { (& 'C:\Program Files\RemoteHub Agent\RemoteHubAgent.exe' policy) -join "`n" }
            $b.ps = try { Invoke-LabAgentCommand -Name $VmName -Pin $pin -Script '$PID' } catch { [pscustomobject]@{ error = "$_" } }
            Restart-Client
            $b.page = Policies-Text
            Shot 'both-products'
            Policy @{ DisableRecording = $null }
            Set-LabAgentPolicy -Name $VmName -Clear
            Save 'both-products' $b
        }

        It 'POL-02 each product reads only its own key; Settings -> Policies shows the agent''s values in their own section' -Tag 'POL-02' {
            # the harness reports a refused connection in connectError (RemoteHub's text: the agent answers 404 and its
            # health has no "desktop"); error only when the harness itself failed
            $b.desktop.error | Should -BeNullOrEmpty
            $b.desktop.connectError.status | Should -Be 404
            $b.desktop.connectError.connectError | Should -Match 'The remote desktop is turned off by a policy on .+ \(DesktopEnabled in HKLM\\SOFTWARE\\Policies\\RemoteHub\\Agent\)'
            $b.agentReport | Should -Not -Match 'DisableRecording'
            $b.agentReport | Should -Match 'DesktopEnabled\s+Off\s+\[policy\]'
            $b.ps.error | Should -BeNullOrEmpty -Because 'the agent still allows PowerShell'
            # the console's section lists recording, the agent's section the desktop
            $console, $agent = $b.page -split 'RemoteHub Agent on this PC', 2
            $console | Should -Match 'Turn off recording'
            $console | Should -Not -Match 'Allow remote desktop'
            $agent | Should -Match 'Allow remote desktop'
            $agent | Should -Match 'Off'
        }
    }

    Context 'Settings -> Policies' {
        BeforeAll {
            $s = [ordered]@{}
            Policy @{ DisableTelemetry = $null; AuditRetentionDays = $null }
            Restart-Client
            $s.none = Policies-Text
            Policy @{ DisableTelemetry = 1 }
            Policy @{ AuditRetentionDays = 0 } -Hive HKCU
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ DesktopClipboard = 1 }
            Restart-Client
            Settings-Page 'Policies'
            $s.page = Settings-Text
            $null = Ui 'Copy as text' -Title 'Settings'
            $s.clipboard = Invoke-LabInSession -Name $VmName -Account Console -ScriptBlock { Get-Clipboard -Raw }
            # changed while the page is open: shown after Refresh
            Policy @{ UpdateChannel = 'beta' }
            $null = Ui 'Refresh' -Title 'Settings'
            $s.refreshed = Wait-LabCondition -NoThrow -TimeoutSec 15 -Because 'the Policies page did not show the update channel after Refresh' -Until {
                $t = Settings-Text; if ($t -match 'Update channel') { $t } }
            if (-not $s.refreshed) { $s.refreshed = Settings-Text }
            Shot 'policies-page'
            $null = Ui 'Cancel' -Title 'Settings'
            # Settings overview: the section and its button
            Settings-Page 'Help & about'
            $null = Ui 'Settings overview*' -Title 'Settings'
            $s.overview = (Ui '*' -Title 'Settings overview' -Value list -TimeoutSec 30).items -join "`n"
            $null = Ui 'Show policies*' -Title 'Settings overview'
            $s.fromOverview = (Ui '*' -Title 'Settings' -Value list -TimeoutSec 30).items -join "`n"
            # two windows open over RemoteHub: closed by ending it
            Stop-LabClient -Name $VmName -Task $script:task
            $script:task = $null
            Policy @{ DisableTelemetry = $null; UpdateChannel = $null }
            Policy @{ AuditRetentionDays = $null } -Hive HKCU
            Set-LabAgentPolicy -Name $VmName -Clear
            Save 'policies-page' $s
        }

        It 'POL-11 lists every value set, its source and invalid values; Copy as text, Refresh and the overview work' -Tag 'POL-11' {
            $s.none | Should -Match 'No policies are set for RemoteHub on this PC'
            $s.page | Should -Match '2 settings of RemoteHub on this PC are set by your organisation \(1 for the computer, 1 for your user account\)'
            $s.page | Should -Match 'Turn off usage data and crash reports'
            $s.page | Should -Match 'Turned off'
            # "Computer (middle dot) HKLM\..." - any character for the dot: the file stays ASCII for Windows PowerShell 5.1
            $s.page | Should -Match 'Computer . HKLM\\SOFTWARE\\Policies\\RemoteHub\\DisableTelemetry'
            $s.page | Should -Match 'AuditRetentionDays 0 is outside 1.36500 . ignored'
            $s.page | Should -Match 'RemoteHub Agent on this PC'
            $s.page | Should -Match 'Clipboard'
            $s.clipboard | Should -Match 'Turn off usage data and crash reports: Turned off'
            $s.clipboard | Should -Match 'INVALID: AuditRetentionDays 0 is outside 1.36500 . ignored'
            $s.clipboard | Should -Match 'Clipboard: Text'
            $s.refreshed | Should -Match 'Update channel'
            $s.overview | Should -Match 'set by your organisation'
            $s.fromOverview | Should -Match 'Copy as text'
        }
    }

    Context 'greyed out by policy' {
        BeforeAll {
            $g = [ordered]@{}
            function Controls {
                Settings-Page 'Updates & privacy'
                $c = [ordered]@{
                    telemetry = Ui 'Send anonymous usage data and crash reports' -Title 'Settings' -Value exists
                    stable    = Ui 'GA*' -Title 'Settings' -Value exists
                }
                $null = Ui 'Logs' -Title 'Settings'
                $c.retention = Ui 'Keep entries for' -Title 'Settings' -Value readnext
                $null = Ui 'Recordings' -Title 'Settings'
                $c.folder = Ui 'Change folder*' -Title 'Settings' -Value exists
                $null = Ui 'Cancel' -Title 'Settings'
                $c
            }
            Restart-Client
            $g.without = Controls
            Policy @{ DisableTelemetry = 1; UpdateChannel = 'stable'; AuditRetentionDays = 90; RecordingFolder = '%USERPROFILE%\PolicyRecordings' }
            Restart-Client
            $g.with = Controls
            Shot 'greyed'
            Policy @{ DisableTelemetry = $null; UpdateChannel = $null; AuditRetentionDays = $null; RecordingFolder = $null }
            Save 'greyed' $g
        }

        It 'POL-12 settings set by policy stay visible and are disabled; without the policies they can be changed' -Tag 'POL-12' {
            foreach ($name in 'telemetry', 'stable', 'retention', 'folder') {
                $g.without[$name].found | Should -BeTrue -Because "$name is there without the policy"
                $g.without[$name].enabled | Should -BeTrue -Because "$name can be changed without the policy"
                $g.with[$name].found | Should -BeTrue -Because "$name is greyed out, never hidden"
                $g.with[$name].enabled | Should -BeFalse -Because "$name is set by policy"
            }
            $g.with.retention.value | Should -Match '90 days|3 months'
        }
    }

    # docs/admx-design.md section 5 and 18: Features.IsAllowed is read when a menu opens or an action starts, so the values
    # are set while RemoteHub runs (no restart) - that is part of what is checked
    Context 'console features hidden and refused' {
        BeforeAll {
            $f = [ordered]@{}
            Ensure-Setup
            CloseTabs
            Step $f baseline {
                $f.localBase = MenuEntries 'PolLocal'
                $f.localAgentBase = MenuEntries 'PolLocal' 'RemoteHub Agent'
                $f.localToolsBase = MenuEntries 'PolLocal' 'Tools'
                $f.winrmToolsBase = MenuEntries 'PolWinRm' 'Tools'
                $f.offlineBase = MenuEntries 'PolOffline'
                $f.offlineToolsBase = MenuEntries 'PolOffline' 'Tools'
                $f.newBase = MenuEntries 'NewButton' 'New connection' -Pick
                Settings-Page 'Security & storage'
                $f.exportBase = (Ui 'Export backup*' -Title 'Settings' -Value exists -Optional -TimeoutSec 5).found
                $null = Ui 'Cancel' -Title 'Settings'
            }
            # a PowerShell tab of PolLocal through the agent, opened before the policy: the tab bar's "+" offers its tools
            Step $f tab {
                $null = Ui 'PolLocal' -Value 'menu:Tools>PowerShell'
                $f.psTab = (Ui 'Stop the running command (Ctrl+C)' -Value exists -TimeoutSec 90).found
                $f.plusBase = MenuEntries 'Open another tab for this connection' -Pick
            }
            # the agent's main switch off; its own value AllowAgentDesktop = 1 shows as off because of it
            Step $f agentOff {
                Policy @{ AllowAgent = 0; AllowAgentDesktop = 1 }
                $f.agentOffMenu = MenuEntries 'PolLocal'
                $f.agentOffAgent = MenuEntries 'PolLocal' 'RemoteHub Agent'
                $f.agentOffTools = MenuEntries 'PolLocal' 'Tools'
                $f.agentOffPlus = MenuEntries 'Open another tab for this connection' -Pick
                $f.agentOffPlusTools = MenuEntries 'Open another tab for this connection' 'Tools' -Pick
                Editor 'PolLocal' 'Edit connection' {
                    $null = Ui 'PowerShell remoting' -Title 'Edit connection'
                    $f.connectThrough = Row 'Edit connection' 'Connect through'
                    $f.desktopThrough = Row 'Edit connection' 'Remote desktop through'
                    $f.turnedOffNote = (Ui 'Turned off by your organisation*' -Title 'Edit connection' -Value exists -Optional -TimeoutSec 5).found
                    Shot 'features-editor'
                }
                $f.agentOffPolicies = Policies-Text
            }
            Policy @{ AllowAgent = $null; AllowAgentDesktop = $null }
            CloseTabs
            # WinRM's main switch off, the agent allowed
            Step $f remotingOff {
                Policy @{ AllowPowerShellRemoting = 0 }
                $f.remotingOffWinRm = MenuEntries 'PolWinRm'
                $f.remotingOffWinRmTools = MenuEntries 'PolWinRm' 'Tools'
                $f.remotingOffWinRmAgent = MenuEntries 'PolWinRm' 'RemoteHub Agent'
                $f.remotingOffLocalTools = MenuEntries 'PolLocal' 'Tools'
            }
            Policy @{ AllowPowerShellRemoting = $null }
            Step $f fileTransferOff {
                Policy @{ AllowAgentFileTransfer = 0 }
                $f.ftLocalTools = MenuEntries 'PolLocal' 'Tools'
                $f.ftWinRmTools = MenuEntries 'PolWinRm' 'Tools'
            }
            Policy @{ AllowAgentFileTransfer = $null }
            # an RDP tab opened before AllowRdp = 0 (it tries an address that never answers): Reconnect fails in the tab
            Step $f rdpOff {
                $null = Ui 'PolRdp' -Value 'menu:Connect'
                $null = Ui 'Reconnect' -Value exists -TimeoutSec 30
                # its own attempt over first (the tab's card with "Close tab"), so that attempt's error can't replace the
                # refusal's text afterwards
                $f.rdpFailed = (Ui 'Close tab' -Value exists -Optional -TimeoutSec 120).found
                Policy @{ AllowRdp = 0; AllowNetworkShares = 0; AllowRdpFileImport = 0; AllowBackupExport = 0 }
                $null = Ui 'Reconnect'
                $f.reconnect = Ui 'Remote desktop over RDP is turned off by your organisation*' -Value exists -Optional -TimeoutSec 30
                Shot 'features-reconnect'
                CloseTabs
                $f.rdpOffMenu = MenuEntries 'PolOffline'
                $f.sharesOffTools = MenuEntries 'PolOffline' 'Tools'
                $f.newOff = MenuEntries 'NewButton' 'New connection' -Pick
                Settings-Page 'Security & storage'
                $f.exportOff = (Ui 'Export backup*' -Title 'Settings' -Value exists -Optional -TimeoutSec 5).found
                $f.importOff = (Ui 'Import backup*' -Title 'Settings' -Value exists -Optional -TimeoutSec 5).found
                $null = Ui 'Cancel' -Title 'Settings'
                # Settings stayed open over the tree in run 20261021-062735 and took POL-15's clicks: closed for sure
                if ((Ui '*' -Title 'Settings' -Value exists -Optional -TimeoutSec 2).found) { $null = Ui 'TitleBarCloseButton' -Title 'Settings' -Optional -TimeoutSec 5 }
            }
            Policy @{ AllowRdp = $null; AllowNetworkShares = $null; AllowRdpFileImport = $null; AllowBackupExport = $null }
            CloseTabs
            # a typed address that is no saved connection, Enter
            Step $f quick {
                Policy @{ AllowQuickConnect = 0 }
                $null = Ui 'SearchBox' -Value 'keys:pol14-quick.invalid{ENTER}'
                # the box may be named by its text ("Quick connect is turned off ..."), not its title
                $f.quick = Message 'Quick connect*' -TimeoutSec 30
                $null = Ui 'OK' -Title 'Quick connect*' -Optional -TimeoutSec 3
                $f.quickTab = (Ui 'Reconnect' -Value exists -Optional -TimeoutSec 3).found
                $null = Ui 'SearchBox' -Value 'keys:{ESC}' -Optional
            }
            Policy @{ AllowQuickConnect = $null }
            CloseTabs
            Shot 'features'
            Save 'features' $f
        }

        It 'POL-14 blocked features are hidden in the tree, + and New menus and Settings, refused where they start; the editor greys out the agent''s rows; Settings -> Policies marks what a main switch turns off' -Tag 'POL-14' {
            Errors $f | Should -BeNullOrEmpty
            # without the policies: what the blocks take away is there
            Has $f.localAgentBase 'Update RemoteHub Agent*' | Should -BeTrue -Because 'RemoteHub paired with the agent on PolLocal'
            Has $f.localToolsBase 'File transfer' | Should -BeTrue -Because "the agent's test passed (Tools: $($f.localToolsBase -join ', '))"
            Has $f.winrmToolsBase 'File transfer' | Should -BeTrue -Because "WinRM's test passed (Tools: $($f.winrmToolsBase -join ', '))"
            Has $f.offlineBase 'Connect' | Should -BeTrue
            Has $f.offlineToolsBase 'Add network share*' | Should -BeTrue
            # New > New connection lists the kinds (the entries of both menu levels are read)
            foreach ($kind in 'Remote Control', 'SSH', 'SFTP', 'FTP / FTPS', 'SMB', 'WebDAV') { Has $f.newBase $kind | Should -BeTrue -Because "New offers: $($f.newBase -join ', ')" }
            Has $f.newBase 'Import .rdp files*' | Should -BeTrue
            $f.exportBase | Should -BeTrue
            $f.psTab | Should -BeTrue

            # AllowAgent = 0: no agent entries in the tree menu or the "+" menu, the tools over WinRM
            foreach ($menu in 'agentOffMenu', 'agentOffAgent', 'agentOffPlus') {
                foreach ($entry in 'Remote desktop via RemoteHub Agent', 'Install RemoteHub Agent*', 'Update RemoteHub Agent*', '*credential provider*') {
                    Has $f[$menu] $entry | Should -BeFalse -Because "$menu offers: $($f[$menu] -join ', ')"
                }
            }
            Has $f.agentOffTools 'Test PowerShell remoting*' | Should -BeTrue -Because "the connection's tools go over WinRM (Tools: $($f.agentOffTools -join ', '))"
            Has $f.agentOffTools 'Test RemoteHub Agent*' | Should -BeFalse
            Has $f.agentOffPlusTools 'Test PowerShell remoting*' | Should -BeTrue -Because "the + menu's tools go over WinRM too ($($f.agentOffPlusTools -join ', '))"
            $f.connectThrough.enabled | Should -BeFalse
            $f.desktopThrough.enabled | Should -BeFalse
            $f.turnedOffNote | Should -BeTrue -Because 'the rows say "Turned off by your organisation"'
            $f.agentOffPolicies | Should -Match 'Off because .Allow the RemoteHub Agent. is off'

            # AllowPowerShellRemoting = 0: nothing over WinRM for PolWinRm, the agent's tools for PolLocal stay
            foreach ($entry in 'PowerShell', 'File transfer', 'Registry', 'Test PowerShell remoting*') {
                Has $f.remotingOffWinRmTools $entry | Should -BeFalse -Because "PolWinRm's Tools: $($f.remotingOffWinRmTools -join ', ')"
            }
            Has $f.remotingOffWinRmAgent 'Set up PowerShell remoting*' | Should -BeFalse
            Has $f.remotingOffLocalTools 'PowerShell' | Should -BeTrue -Because "the agent's tools are still offered ($($f.remotingOffLocalTools -join ', '))"
            Has $f.remotingOffLocalTools 'File transfer' | Should -BeTrue

            # AllowAgentFileTransfer = 0: gone for the agent connection only
            Has $f.ftLocalTools 'File transfer' | Should -BeFalse -Because "PolLocal's Tools: $($f.ftLocalTools -join ', ')"
            Has $f.ftLocalTools 'PowerShell' | Should -BeTrue
            Has $f.ftWinRmTools 'File transfer' | Should -BeTrue -Because "over WinRM it is still there ($($f.ftWinRmTools -join ', '))"

            # AllowRdp, AllowNetworkShares, AllowRdpFileImport, AllowBackupExport = 0
            $f.reconnect.found | Should -BeTrue -Because 'the RDP tab opened before the policy fails with the reason on Reconnect'
            Has $f.rdpOffMenu 'Connect' | Should -BeFalse -Because "PolOffline's menu: $($f.rdpOffMenu -join ', ')"
            Has $f.rdpOffMenu 'Open additional session' | Should -BeFalse
            Has $f.sharesOffTools 'Add network share*' | Should -BeFalse
            foreach ($kind in 'SFTP', 'FTP / FTPS', 'SMB', 'WebDAV') { Has $f.newOff $kind | Should -BeFalse -Because "New offers: $($f.newOff -join ', ')" }
            # SSH has a switch of its own: network shares off leaves it
            Has $f.newOff 'SSH' | Should -BeTrue -Because "New offers: $($f.newOff -join ', ')"
            Has $f.newOff 'Import .rdp files*' | Should -BeFalse
            $f.exportOff | Should -BeFalse
            $f.importOff | Should -BeTrue -Because 'only the export is blocked'

            # AllowQuickConnect = 0
            $f.quick | Should -Match 'Quick connect is turned off by your organisation'
            $f.quickTab | Should -BeFalse
        }
    }

    # docs/admx-design.md sections 6, 7 and 19. Connection values (subkey Connections) are cached for 2 s and the theme is
    # applied at start: RemoteHub is restarted after those are set.
    Context 'settings and connection settings by policy' {
        BeforeAll {
            $s = [ordered]@{}
            Ensure-Setup
            # a fresh start: nothing POL-14 left open (a dialog, Settings) covers the tree
            Restart-Client
            CloseTabs
            # PolOffline's own "Local drives" On, and its .rdp file without the policy
            Step $s drivesOwn {
                Editor 'PolOffline' 'Edit connection' -Button 'Save' {
                    $null = Ui 'Local resources & keyboard' -Title 'Edit connection'
                    $null = Ui 'Local drives' -Title 'Edit connection' -Value 'choose:On'
                }
                $s.rdpBefore = Export-Rdp 'PolOffline' 'C:\lab\pol15-before.rdp'
            }
            Step $s drives {
                Policy @{ RedirectDrives = 0 } -Key 'Connections'
                Restart-Client
                # the connection's editor, saved unchanged (its own value must stay)
                Editor 'PolOffline' 'Edit connection' -Button 'Save' {
                    $null = Ui 'Local resources & keyboard' -Title 'Edit connection'
                    $s.connection = Row 'Edit connection' 'Local drives'
                    $s.connectionNote = (Ui 'Set by your organisation*' -Title 'Edit connection' -Value exists -Optional -TimeoutSec 5).found
                    Shot 'settings-drives-connection'
                }
                Editor 'PolFolder' 'Edit folder' {
                    $null = Ui 'Local resources & keyboard' -Title 'Edit folder'
                    $s.folder = Row 'Edit folder' 'Local drives'
                    $s.folderNote = (Ui 'Set by your organisation*' -Title 'Edit folder' -Value exists -Optional -TimeoutSec 5).found
                }
                Settings-Page 'Local resources & keyboard'
                $s.defaults = Row 'Settings' 'Local drives'
                $s.defaultsNote = (Ui 'Set by your organisation*' -Title 'Settings' -Value exists -Optional -TimeoutSec 5).found
                $null = Ui 'Cancel' -Title 'Settings'
                # the .rdp file RemoteHub writes uses the same resolved settings as its RDP session (Inheritance.ResolveOptions)
                $s.rdpWith = Export-Rdp 'PolOffline' 'C:\lab\pol15-with.rdp'
            }
            Policy @{ RedirectDrives = $null } -Key 'Connections'
            Step $s drivesAfter {
                Restart-Client
                Editor 'PolOffline' 'Edit connection' {
                    $null = Ui 'Local resources & keyboard' -Title 'Edit connection'
                    $s.connectionAfter = Row 'Edit connection' 'Local drives'
                }
                $s.rdpAfter = Export-Rdp 'PolOffline' 'C:\lab\pol15-after.rdp'
            }
            # usage data switched off by the user, then DisableTelemetry = 0 (on and locked)
            Step $s telemetry {
                Settings-Page 'Updates & privacy'
                $null = Ui 'Send anonymous usage data and crash reports' -Title 'Settings' -Value toggle-off
                $null = Ui 'Save' -Title 'Settings'
                Policy @{ DisableTelemetry = 0 }
                Settings-Page 'Updates & privacy'
                $s.telemetry = Ui 'Send anonymous usage data and crash reports' -Title 'Settings' -Value read
                $s.telemetryNote = (Ui 'Turned on by your organisation*' -Title 'Settings' -Value exists -Optional -TimeoutSec 5).found
                Shot 'settings-telemetry'
                $null = Ui 'Cancel' -Title 'Settings'
            }
            Policy @{ DisableTelemetry = $null }
            # the silent install over WinRM: offered for PolWinRm (its remoting check is on), greyed out with Methods
            Step $s methods {
                $null = Ui 'PolWinRm' -Value 'menu:RemoteHub Agent>Install RemoteHub Agent*'
                $s.methodsBase = Ui 'Install silently over PowerShell remoting*' -Title 'Install RemoteHub Agent' -Value exists -TimeoutSec 60
                $null = Ui 'Close' -Title 'Install RemoteHub Agent'
                Policy @{ Methods = @('Clipboard', 'SaveFiles') } -Key 'AgentInstall'
                $null = Ui 'PolWinRm' -Value 'menu:RemoteHub Agent>Install RemoteHub Agent*'
                $s.methodsWith = Ui 'Install silently over PowerShell remoting*' -Title 'Install RemoteHub Agent' -Value exists -TimeoutSec 60
                $s.methodsClipboard = Ui 'Copy the installer and the enrollment file to the clipboard*' -Title 'Install RemoteHub Agent' -Value exists -Optional -TimeoutSec 5
                Shot 'settings-methods'
                $null = Ui 'Close' -Title 'Install RemoteHub Agent'
            }
            Policy @{ Methods = $null } -Key 'AgentInstall'
            # AgentDesktopViewOnly = 1: the start panel's View only ticked and greyed out; the session it starts is view only
            # (the agent desktop of this VM's own console session, allowed at its console)
            Step $s viewOnly {
                Policy @{ AgentDesktopViewOnly = 1 }
                $null = Ui 'PolLocal' -Value 'menu:Connect'
                $null = Ui 'Signed in as *' -Value exists -TimeoutSec 90
                $s.viewOnlyBox = Ui 'View only*no mouse and keyboard' -Value read
                Shot 'settings-viewonly-start'
                $null = Ui 'Console*' -Value check -Optional -TimeoutSec 5
                $null = Ui 'Connect'
                $s.viewOnlyAllow = [bool](AllowAtConsole 60)
                $s.viewOnlyBadge = (Ui 'View only' -Value exists -Optional -TimeoutSec 60).found
                # a session that can be controlled offers Ctrl+Alt+Del in its toolbar (UI-DESKTOP's sign that it is live)
                $s.viewOnlyCad = (Ui 'Send Ctrl+Alt+Del' -Value exists -Optional -TimeoutSec 3).found
                Shot 'settings-viewonly-live'
            }
            CloseTabs
            Policy @{ AgentDesktopViewOnly = $null }
            # RemoteDesktopVia = Agent by policy while the agent desktop is blocked
            Step $s conflict {
                Policy @{ AllowAgentDesktop = 0 }
                Policy @{ RemoteDesktopVia = 'Agent' } -Key 'Connections'
                Restart-Client
                $s.conflictPage = Policies-Text
                $null = Ui 'PolOffline' -Value 'menu:Connect'
                # the refusal is a dialog owned by the main window, which UI Automation lists inside that window - not
                # as a window titled "RemoteHub Agent" (run 20261022-152400: on screen, the title search found nothing)
                $s.conflictConnect = Message 'RemoteHub Agent' -TimeoutSec 10
                if (-not $s.conflictConnect) {
                    $s.conflictConnect = "$((Ui 'Remote desktop through the RemoteHub Agent*' -Value exists -Optional -TimeoutSec 20).name)"
                    $null = Ui 'OK' -Optional -TimeoutSec 5
                }
                $s.conflictTab = (Ui 'Reconnect' -Value exists -Optional -TimeoutSec 3).found
            }
            Policy @{ AllowAgentDesktop = $null }
            Policy @{ RemoteDesktopVia = $null } -Key 'Connections'
            CloseTabs
            # the user's own theme Dark, then Theme = Light by policy
            Step $s theme {
                Settings-Page 'General'
                $null = Ui 'Theme' -Title 'Settings' -Value 'choose:Dark'
                $null = Ui 'Save' -Title 'Settings'
                Restart-Client
                $s.darkBrightness = MainWindowBrightness 'settings-theme-own'
                Policy @{ Theme = 'Light' }
                Restart-Client
                $s.lightBrightness = MainWindowBrightness 'settings-theme-policy'
                Settings-Page 'General'
                $s.theme = Row 'Settings' 'Theme'
                $null = Ui 'Cancel' -Title 'Settings'
            }
            Policy @{ Theme = $null }
            Save 'settings' $s
        }

        It 'POL-15 a connection value set by policy shows greyed out at every level and goes into the .rdp file, the stored value stays; Theme, DisableTelemetry = 0, AgentDesktopViewOnly and AgentInstall\Methods lock their controls; RemoteDesktopVia = Agent with the agent desktop blocked conflicts and refuses Connect' -Tag 'POL-15' {
            Errors $s | Should -BeNullOrEmpty
            # Local drives: the policy's Off at the connection, its folder and the global defaults, greyed out with the note
            foreach ($level in 'connection', 'folder', 'defaults') {
                $s[$level].value | Should -Be 'Off' -Because "the $level shows the policy's value"
                $s[$level].enabled | Should -BeFalse -Because "the $level's row is set by policy"
                $s["${level}Note"] | Should -BeTrue -Because "the $level's row says ""Set by your organisation"""
            }
            # the drives in the RDP settings RemoteHub writes: shared before, not with the policy, again after it - the
            # connection's own On was kept through a save with the policy set
            $s.rdpBefore | Should -Match '(?m)^drivestoredirect:s:\*\s*$'
            $s.rdpWith | Should -Match '(?m)^drivestoredirect:s:\s*$'
            $s.rdpAfter | Should -Match '(?m)^drivestoredirect:s:\*\s*$'
            $s.connectionAfter.value | Should -Be 'On'
            $s.connectionAfter.enabled | Should -BeTrue

            # Theme = Light: shown and greyed out; the window is light, brighter than with the user's own Dark
            $s.theme.value | Should -Be 'Light'
            $s.theme.enabled | Should -BeFalse
            $s.lightBrightness | Should -BeGreaterThan 0.5
            $s.lightBrightness | Should -BeGreaterThan ($s.darkBrightness + 0.2)

            # DisableTelemetry = 0 with usage data off: on and greyed out
            $s.telemetry.value | Should -Be 'On'
            $s.telemetry.enabled | Should -BeFalse
            $s.telemetryNote | Should -BeTrue

            # AgentDesktopViewOnly = 1
            $s.viewOnlyBox.value | Should -Be 'On'
            $s.viewOnlyBox.enabled | Should -BeFalse
            $s.viewOnlyBadge | Should -BeTrue -Because "the session started is view only (Allow at the console clicked: $($s.viewOnlyAllow))"
            $s.viewOnlyCad | Should -BeFalse -Because 'a view-only session offers no Ctrl+Alt+Del'

            # AgentInstall\Methods = Clipboard, SaveFiles: the WinRM method there but greyed out
            $s.methodsBase.enabled | Should -BeTrue -Because 'without the policy the silent install is offered for PolWinRm'
            $s.methodsWith.found | Should -BeTrue -Because 'a method IT doesn''t offer is greyed out, not removed'
            $s.methodsWith.enabled | Should -BeFalse
            $s.methodsClipboard.enabled | Should -BeTrue

            # RemoteDesktopVia = Agent with AllowAgentDesktop = 0 (the colour, amber, isn't visible to UI Automation)
            $s.conflictPage | Should -Match 'Remote desktop through the RemoteHub Agent is turned off by your organisation, so there is no remote desktop'
            $s.conflictConnect | Should -Match 'Remote desktop through the RemoteHub Agent is turned off by your organisation'
            $s.conflictTab | Should -BeFalse -Because 'Connect doesn''t fall back to RDP'
        }
    }
}
