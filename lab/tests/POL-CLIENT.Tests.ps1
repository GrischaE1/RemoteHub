# POL (RemoteHub) - the Group Policy template in the editor, both products on one VM, Settings -> Policies and settings
# greyed out by policy (docs/admx-design.md sections 4 and 16). RemoteHub and the agent are installed on the same client VM;
# RemoteHub runs in the console account's session, driven by UI Automation. Console values are written the way the
# template writes them (HKLM, or the console account's HKCU). Client VMs only. See docs/test-lab-cases.md section 10.7.
# LabRole: console
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
    # a console policy value as the template writes it: HKLM, or HKCU of the console account; $null removes it
    function Policy([hashtable]$Values, [string]$Hive = 'HKLM') {
        Invoke-LabGuest -Name $VmName -ArgumentList $Values, $Hive, $vm.ConsoleAccount -ScriptBlock {
            param($values, $hive, $account)
            $k = if ($hive -eq 'HKLM') { 'HKLM:\SOFTWARE\Policies\RemoteHub' } else {
                $sid = (New-Object Security.Principal.NTAccount $account).Translate([Security.Principal.SecurityIdentifier]).Value
                "Registry::HKEY_USERS\$sid\SOFTWARE\Policies\RemoteHub" }
            if (-not (Test-Path $k)) { $null = New-Item $k -Force }
            foreach ($n in $values.Keys) {
                if ($null -eq $values[$n]) { Remove-ItemProperty $k -Name $n -ErrorAction SilentlyContinue }
                elseif ($values[$n] -is [int]) { Set-ItemProperty $k -Name $n -Value $values[$n] -Type DWord }
                else { Set-ItemProperty $k -Name $n -Value $values[$n] -Type String }
            }
        }
    }
    function Restart-Client { if ($script:task) { Stop-LabClient -Name $VmName -Task $script:task }; $script:task = Start-LabClient -Name $VmName }
    function Settings-Page([string]$Page) { $null = Ui 'Settings'; $null = Ui $Page -Title 'Settings' }
    function Settings-Text { (Ui '*' -Title 'Settings' -Value list).items -join "`n" }
    function Policies-Text { Settings-Page 'Policies'; $t = Settings-Text; $null = Ui 'Cancel' -Title 'Settings'; $t }
    $script:task = $null

    if ((Test-LabConsoleVm -Name $VmName)) {
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
            Start-Sleep -Seconds 2
            $s.refreshed = Settings-Text
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
}
