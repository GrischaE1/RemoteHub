# C-POL / C-UPD / C-TEL / C-CRASH - RemoteHub's policies, its update check and installer, telemetry and crash reports,
# on a client VM against the lab's update.remotehub.dev inside the VM (RhMockServer.ps1: hosts entry, trusted
# certificate, /latest from a file, /telemetry and every request logged). RemoteHub runs in the console account's
# session, driven by UI Automation. Client VMs only. See docs/test-lab-cases.md section 10.3.
# LabRole: console
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
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "c-polupd-$name.json") -Encoding UTF8 }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    function Policy([hashtable]$Values, [string]$Hive = 'HKLM', [string]$Key = 'RemoteHub') {
        Invoke-LabGuest -Name $VmName -ArgumentList $Values, $Hive, $Key -ScriptBlock {
            param($values, $hive, $key)
            $k = if ($hive -eq 'HKLM') { "HKLM:\SOFTWARE\Policies\$key" } else {
                $sid = (New-Object Security.Principal.NTAccount 'Admin').Translate([Security.Principal.SecurityIdentifier]).Value
                "Registry::HKEY_USERS\$sid\SOFTWARE\Policies\$key" }
            $null = New-Item $k -Force
            foreach ($n in $values.Keys) {
                if ($null -eq $values[$n]) { Remove-ItemProperty $k -Name $n -ErrorAction SilentlyContinue }
                elseif ($values[$n] -is [int]) { Set-ItemProperty $k -Name $n -Value $values[$n] -Type DWord }
                else { Set-ItemProperty $k -Name $n -Value $values[$n] -Type String }
            }
        }
    }
    function Restart-Client { if ($script:task) { Stop-LabClient -Name $VmName -Task $script:task }; $script:task = Start-LabClient -Name $VmName }
    function Requests([string]$Path) { , @(Get-LabMockRequests -Name $VmName | Where-Object path -eq $Path) }
    function Clear-Requests { Invoke-LabGuest -Name $VmName -ScriptBlock { Remove-Item 'C:\ProgramData\RemoteHubLab\mock\requests.jsonl' -ErrorAction SilentlyContinue } }
    function Settings-Page([string]$Page) { $null = Ui 'Settings'; $null = Ui $Page -Title 'Settings' }
    function Settings-Text { (Ui '*' -Title 'Settings' -Value list).items -join "`n" }
    # The text of the 'RemoteHub updates' window once it matches $Pattern (a download error is shown there, not in a
    # message box), or its last text after $TimeoutSec.
    function Wait-UpdateText([string]$Pattern, [int]$TimeoutSec = 90) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            $text = @((Ui '*' -Title 'RemoteHub updates' -Value list -Optional -TimeoutSec 10).items) -join "`n"
            if ($text -match $Pattern) { break }
            Start-Sleep -Seconds 2
        } while ((Get-Date) -lt $until)
        $text
    }
    $script:task = $null
}

AfterAll {
    if ($isClient) {
        # policies, the mock server, an updated RemoteHub, telemetry ids: the next file starts from the checkpoint
        Set-LabVmDirty -Name $VmName
        Stop-LabClient -Name $VmName -Task $script:task
    }
}

Describe 'C-POLUPD <VmName>' -Tag Client, 'C-POL', 'C-UPD', 'C-TEL', 'C-CRASH' -Skip:(-not $isClient) {

    Context 'policies' {
        BeforeAll {
            $p = [ordered]@{}
            Reset-LabVm -Name $VmName
            $null = Install-LabClient -Name $VmName   # DisableUpdateCheck and DisableTelemetry set
            Start-LabMockServer -Name $VmName
            Set-LabMockLatest -Name $VmName -Version '99.0.0' -Url 'https://update.remotehub.dev/download/none.msi'
            Restart-Client
            Start-Sleep -Seconds 15
            $p.offLatest = Requests '/latest'
            $p.offTelemetry = Requests '/telemetry'
            Settings-Page 'Updates & privacy'
            $p.managed = Settings-Text
            $null = Ui 'Cancel' -Title 'Settings'
            # UpdateChannel stable: no beta channel asked
            Policy @{ DisableUpdateCheck = $null; UpdateChannel = 'stable' }
            Clear-Requests
            Restart-Client
            Start-Sleep -Seconds 15
            $p.stable = Requests '/latest'
            # recording off, and the HKCU / HKLM order. The update check is off again: its bar appears about 3 s after the
            # start and moves the list down, so a double-click on 'Local' could land beside it and open no tab.
            Policy @{ DisableRecording = 1; DisableUpdateCheck = 1 }
            New-LabClientConnection -Name $VmName -Display 'Local' -TargetHost '127.0.0.1'
            Restart-Client
            $null = Ui 'Local' -Value doubleclick
            # the tab is open once the main window is named after it ('Local - Remote desktop - RemoteHub')
            $p.tabHidden = Ui '*' -Title 'Local*' -Value list -Optional -TimeoutSec 20
            $p.recordHidden = Ui 'Record this tab to a video file' -Optional -TimeoutSec 5
            Policy @{ DisableRecording = 0 }
            Policy @{ DisableRecording = 1 } -Hive HKCU
            Restart-Client
            $null = Ui 'Local' -Value doubleclick
            $p.tabHklmWins = Ui '*' -Title 'Local*' -Value list -Optional -TimeoutSec 20
            $p.recordHklmWins = Ui 'Record this tab to a video file' -Optional -TimeoutSec 10
            Policy @{ DisableRecording = $null } -Hive HKCU
            # recording folder with %USERPROFILE%
            Policy @{ DisableRecording = $null; RecordingFolder = '%USERPROFILE%\LabRecordings' }
            Restart-Client
            $null = Ui 'Local' -Value doubleclick
            Start-Sleep -Seconds 5
            $null = Ui 'Record this tab to a video file' -TimeoutSec 20
            $null = Ui 'Start recording' -Title '*' -Optional
            Start-Sleep -Seconds 8
            $null = Ui 'Stop recording and save the video'
            $null = Ui 'Close' -Title '*' -Optional -TimeoutSec 20
            $p.recordings = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-ChildItem 'C:\Users\Admin\LabRecordings' -Filter *.mp4 -ErrorAction SilentlyContinue | ForEach-Object Name) }
            Settings-Page 'Recordings'
            $p.recordingPage = Settings-Text
            $null = Ui 'Cancel' -Title 'Settings'
            # settings folder fixed by policy (the legacy RdpManager key is read for this one)
            Policy @{ SettingsFolder = 'C:\Users\Public\LabSettings' } -Key 'RdpManager'
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock { $null = New-Item -ItemType Directory -Path 'C:\Users\Public\LabSettings' -Force; icacls 'C:\Users\Public\LabSettings' /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null }
            # the folder holds no RemoteHub.settings: RemoteHub warns before its window and runs on this PC's settings
            # (SettingsStore: the file is never created in a policy folder) - started by hand, Start-LabClient waits for
            # the main window
            if ($script:task) { Stop-LabClient -Name $VmName -Task $script:task }
            $script:task = Start-LabInSession -Name $VmName -Account Console -ScriptBlock { & 'C:\Program Files\RemoteHub\RemoteHub.exe' }
            $p.folderWarning = Ui '*could not be opened*' -Title '*' -Value read -Optional -TimeoutSec 90
            $null = Ui 'OK' -Title '*' -Optional -TimeoutSec 10
            $null = Ui 'NewButton' -Value fit -TimeoutSec 90
            Settings-Page 'Security & storage'
            $p.changeLocation = Ui 'Change location*' -Title 'Settings' -Value exists -Optional
            $p.storageText = Settings-Text
            $null = Ui 'Cancel' -Title 'Settings' -Optional
            # automatic sign-in: both editor rows (PowerShell remoting page) - first without the policy (control), then greyed out by it
            function AutoSignInRows {
                $null = Ui 'Local' -Value rightclick
                $null = Ui 'Edit?' -Title '*'
                $null = Ui 'PowerShell remoting' -Title 'Edit connection'
                $rows = [ordered]@{
                    # readnext: found, and whether the ComboBox after the label can be changed
                    signIn    = Ui 'Sign in automatically when nobody is signed in' -Title 'Edit connection' -Value readnext -Optional -TimeoutSec 5
                    endAction = Ui 'When an automatic sign-in*s connection ends' -Title 'Edit connection' -Value readnext -Optional -TimeoutSec 5
                }
                $null = Ui 'Cancel' -Title 'Edit connection'
                $rows
            }
            $p.autoSignInRowsBefore = AutoSignInRows
            # SettingsFolder off again: while it points at the empty folder every start shows the warning before the main
            # window, and Restart-Client (which waits for that window) would time out on it
            Policy @{ SettingsFolder = $null } -Key 'RdpManager'
            Policy @{ DisableAgentAutoSignIn = 1 }
            Restart-Client
            $p.autoSignInRows = AutoSignInRows
            Save 'policies' $p
        }

        It 'C-POL-01 DisableTelemetry: no request to /telemetry, shown as managed' -Tag 'C-POL-01' {
            @($p.offTelemetry) | Should -BeNullOrEmpty
            $p.managed | Should -Match 'managed by your organisation|Turned off by your organisation'
        }

        It 'C-POL-02 DisableUpdateCheck: no request to /latest' -Tag 'C-POL-02' {
            @($p.offLatest) | Should -BeNullOrEmpty
        }

        It 'C-POL-03 UpdateChannel stable: no ?channel=beta' -Tag 'C-POL-03' {
            @($p.stable).Count | Should -BeGreaterThan 0
            @($p.stable | Where-Object query -match 'channel=beta') | Should -BeNullOrEmpty
        }

        It 'C-POL-04 SettingsFolder without a settings file there: warned, this PC''s settings used; the location can''t be changed' -Tag 'C-POL-04' {
            $p.folderWarning.found | Should -BeTrue
            "$($p.folderWarning.value)$($p.folderWarning.name)" | Should -Match 'LabSettings'
            $p.changeLocation.found | Should -BeTrue
            $p.changeLocation.enabled | Should -BeFalse -Because 'the location is managed'
            $p.storageText | Should -Match 'set by your organi[sz]ation'
        }

        It 'C-POL-05 DisableRecording: the Record button is gone' -Tag 'C-POL-05' {
            $p.tabHidden.found | Should -BeTrue -Because 'the Local tab must be open for a missing Record button to mean anything'
            $p.recordHidden.found | Should -BeFalse
        }

        It 'C-POL-06 RecordingFolder with %USERPROFILE%: recordings land there' -Tag 'C-POL-06' {
            @($p.recordings).Count | Should -BeGreaterThan 0
            $p.recordingPage | Should -Match 'LabRecordings|organisation'
        }

        It 'C-POL-07 HKLM before HKCU; SettingsFolder also from the legacy RdpManager key' -Tag 'C-POL-07' {
            $p.tabHklmWins.found | Should -BeTrue -Because 'the Local tab must be open for the Record button to be checked'
            $p.recordHklmWins.found | Should -BeTrue -Because 'HKLM DisableRecording=0 wins over HKCU 1'
            "$($p.folderWarning.value)$($p.folderWarning.name)" | Should -Match 'LabSettings' -Because 'SettingsFolder was read from the legacy RdpManager key'
        }

        It 'C-POL-08 DisableAgentAutoSignIn: both automatic sign-in rows of the editor stay visible but greyed out (editable without it)' -Tag 'C-POL-08' {
            $p.autoSignInRowsBefore.signIn.enabled | Should -BeTrue -Because 'without the policy the row can be changed (control)'
            $p.autoSignInRowsBefore.endAction.enabled | Should -BeTrue
            $p.autoSignInRows.signIn.found | Should -BeTrue -Because 'a setting set by policy is greyed out, never hidden (docs/admx-design.md P2)'
            $p.autoSignInRows.signIn.enabled | Should -BeFalse
            $p.autoSignInRows.endAction.found | Should -BeTrue
            $p.autoSignInRows.endAction.enabled | Should -BeFalse
        }
    }

    Context 'update check and install' {
        BeforeAll {
            $u = [ordered]@{}
            Reset-LabVm -Name $VmName
            $previous = Get-LabClientMsi -Previous
            $current = Get-LabClientMsi
            $currentVersion = (Split-Path $current -Leaf) -replace '^RemoteHub-(.+)-x64\.msi$', '$1'
            $null = Install-LabClient -Name $VmName -Msi $previous -Network
            Policy @{ DisableTelemetry = 1 }
            Start-LabMockServer -Name $VmName -Files $current
            # lower / equal: nothing
            Set-LabMockLatest -Name $VmName -Version '0.0.1' -File (Split-Path $current -Leaf)
            Restart-Client
            Start-Sleep -Seconds 12
            $u.lower = Ui 'Details?' -Optional -TimeoutSec 5
            # higher: the bar
            Set-LabMockLatest -Name $VmName -Version $currentVersion -File (Split-Path $current -Leaf)
            Restart-Client
            $u.banner = Ui "RemoteHub $currentVersion is available*" -Value exists -TimeoutSec 40 -Optional
            # wrong checksum: deleted, not installed
            Set-LabMockLatest -Name $VmName -Version $currentVersion -File (Split-Path $current -Leaf) -Sha256 ('AB' * 32)
            Restart-Client
            $null = Ui 'Details?' -TimeoutSec 40
            $null = Ui 'Download and install' -Title 'RemoteHub updates'
            # the error is shown in the update window itself ('*' as the title would read the taskbar)
            $u.badSum = Wait-UpdateText 'checksum'
            $null = Ui 'Later' -Title 'RemoteHub updates' -Optional -TimeoutSec 5
            $u.badSumFiles = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-ChildItem 'C:\Users\Admin\AppData\Local\Temp\RemoteHub\Updates' -Filter *.msi -ErrorAction SilentlyContinue).Count }
            $u.badSumVersion = Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-Item 'C:\Program Files\RemoteHub\RemoteHub.exe').VersionInfo.ProductVersion }
            # a download host that isn't allowed
            Set-LabMockLatest -Name $VmName -Version $currentVersion -File (Split-Path $current -Leaf) -Url 'https://evil.example.com/RemoteHub.msi'
            # (the checksum step above downloaded from /download/: only this step's requests count)
            Clear-Requests
            Restart-Client
            $null = Ui 'Details?' -TimeoutSec 40
            $null = Ui 'Download and install' -Title 'RemoteHub updates'
            $u.badHost = Wait-UpdateText 'trusted'
            $null = Ui 'Later' -Title 'RemoteHub updates' -Optional -TimeoutSec 5
            $u.badHostRequests = @(Get-LabMockRequests -Name $VmName | Where-Object path -like '/download/*').Count
            # skip / mandatory
            Set-LabMockLatest -Name $VmName -Version '98.0.0' -File (Split-Path $current -Leaf)
            Restart-Client
            $null = Ui 'Details?' -TimeoutSec 40
            $null = Ui 'Skip this version' -Title 'RemoteHub updates'
            # RemoteHub saves a change 600 ms later (MainViewModel's save timer) and on exit; Restart-Client kills it at
            # once, which could lose the skip. Closed the way a user closes it, so the skip is saved on exit.
            $null = Invoke-LabInSession -Name $VmName -Account Console -TimeoutSec 60 -ScriptBlock { Get-Process RemoteHub -ErrorAction SilentlyContinue | ForEach-Object { $null = $_.CloseMainWindow() } }
            $null = Ui 'Exit' -Title '?*' -Optional -TimeoutSec 5
            $left = Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process RemoteHub -ErrorAction SilentlyContinue | Wait-Process -Timeout 20 -ErrorAction SilentlyContinue; @(Get-Process RemoteHub -ErrorAction SilentlyContinue).Count }
            if ($left) { throw "RemoteHub didn't exit within 20 s of closing its window after 'Skip this version'" }
            Restart-Client
            Start-Sleep -Seconds 12
            $u.skipped = Ui 'Details?' -Optional -TimeoutSec 5
            Set-LabMockLatest -Name $VmName -Version '98.0.1' -File (Split-Path $current -Leaf) -Mandatory
            Restart-Client
            $u.mandatory = Ui '*' -Title 'RemoteHub updates' -Value list -TimeoutSec 40 -Optional
            $null = Ui 'Later' -Title 'RemoteHub updates' -Optional -TimeoutSec 5
            # install: the real package of the build under test; UAC answered at the console (Alt+Y)
            Set-LabMockLatest -Name $VmName -Version $currentVersion -File (Split-Path $current -Leaf)
            Restart-Client
            $null = Ui 'Details?' -TimeoutSec 40
            $null = Ui 'Download and install' -Title 'RemoteHub updates'
            $until = (Get-Date).AddSeconds(120)
            while (-not (Invoke-LabGuest -Name $VmName -ScriptBlock { [bool](Get-Process consent -ErrorAction SilentlyContinue) }) -and (Get-Date) -lt $until) { Start-Sleep -Seconds 2 }
            $u.uacShown = (Get-Date) -lt $until
            Start-Sleep -Seconds 2
            try { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir 'c-polupd-uac.png') } catch { }
            # Yes is Alt+Y
            $u.uacKeys = @()
            if (Invoke-LabGuest -Name $VmName -ScriptBlock { [bool](Get-Process consent -ErrorAction SilentlyContinue) }) {
                Send-LabKeyCombo -Name $VmName -Codes 0x12, 0x59
                $u.uacKeys += 'Alt+Y'
            }
            $until = (Get-Date).AddMinutes(5)
            do { Start-Sleep -Seconds 5; $v = Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-Item 'C:\Program Files\RemoteHub\RemoteHub.exe' -ErrorAction SilentlyContinue).VersionInfo.ProductVersion } } while ($v -notlike "$currentVersion*" -and (Get-Date) -lt $until)
            $u.installed = $v
            Start-Sleep -Seconds 20
            $u.restarted = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-Process RemoteHub -ErrorAction SilentlyContinue | ForEach-Object { $_.MainModule.FileVersionInfo.ProductVersion }) }
            Save 'update' $u
        }

        It 'C-UPD-01 a higher version announced: the bar; lower or equal: nothing' -Tag 'C-UPD-01' {
            $u.lower.found | Should -BeFalse
            $u.banner.found | Should -BeTrue
        }

        It 'C-UPD-02 a checksum mismatch: the download deleted, not installed' -Tag 'C-UPD-02' {
            $u.badSum | Should -Match 'doesn''t match the published SHA-256 checksum'
            $u.badSumFiles | Should -Be 0
            $u.badSumVersion | Should -Not -BeLike "$currentVersion*"
        }

        It 'C-UPD-03 a download URL outside GitHub / remotehub.dev is refused' -Tag 'C-UPD-03' {
            $u.badHost | Should -Match 'download address isn''t trusted'
            $u.badHostRequests | Should -Be 0
        }

        It 'C-UPD-04 Download and install: UAC approved, the package installs, RemoteHub restarts with the new version' -Tag 'C-UPD-04' {
            $u.installed | Should -BeLike "$currentVersion*" -Because "UAC prompt seen: $($u.uacShown), keys sent: $($u.uacKeys -join ', ')"
            @($u.restarted | Where-Object { $_ -like "$currentVersion*" }).Count | Should -BeGreaterThan 0
        }

        It 'C-UPD-05 a skipped version stays hidden; a mandatory one can''t be skipped' -Tag 'C-UPD-05' {
            $u.skipped.found | Should -BeFalse
            $u.mandatory.found | Should -BeTrue
            ($u.mandatory.items -join "`n") | Should -Not -Match 'Skip this version'
        }
    }

    Context 'telemetry and crash reports' {
        BeforeAll {
            $t = [ordered]@{}
            Reset-LabVm -Name $VmName
            $null = Install-LabClient -Name $VmName -Network
            Policy @{ DisableUpdateCheck = 1 }
            Start-LabMockServer -Name $VmName
            Restart-Client
            New-LabClientConnection -Name $VmName -Display 'Local' -TargetHost '127.0.0.1'
            $null = Ui 'New'; $null = Ui 'New folder' -Title '*'
            $null = Ui 'Display name' -Title 'New folder' -Value 'setnext:Lab folder' -Optional
            $null = Ui 'Save' -Title 'New folder' -Optional
            # app_session only follows a session with run time on record (written every minute; Restart-Client kills)
            Start-Sleep -Seconds 75
            Restart-Client
            Start-Sleep -Seconds 15
            $t.events = @((Requests '/telemetry') | ForEach-Object json)
            # killed as Task Manager would: UnexpectedExit at the next start (the earlier kill's report cleared first)
            Clear-Requests
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process RemoteHub | Stop-Process -Force }
            $script:task = Start-LabClient -Name $VmName
            Start-Sleep -Seconds 15
            $t.afterKill = @((Requests '/telemetry') | ForEach-Object json)
            # off: the install ID goes
            $t.idBefore = Invoke-LabGuest -Name $VmName -ScriptBlock { Test-Path 'C:\Users\Admin\AppData\Local\RemoteHub\install-id' }
            Settings-Page 'Updates & privacy'
            $null = Ui 'Send anonymous usage data and crash reports' -Title 'Settings' -Value uncheck
            $null = Ui 'Save' -Title 'Settings'
            Start-Sleep -Seconds 3
            $t.idAfter = Invoke-LabGuest -Name $VmName -ScriptBlock { Test-Path 'C:\Users\Admin\AppData\Local\RemoteHub\install-id' }
            $t.os = Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-CimInstance Win32_OperatingSystem).Caption }
            Save 'telemetry' $t
        }

        It 'C-TEL-01 app_start has exactly event, version, channel, os, arch, installId and the counts; app_session after a restart' -Tag 'C-TEL-01' {
            $start = @($t.events | Where-Object event -eq 'app_start')[0]
            $start | Should -Not -BeNullOrEmpty
            @($start.PSObject.Properties.Name | Sort-Object) | Should -Be @('agents', 'arch', 'channel', 'connections', 'event', 'folders', 'installId', 'os', 'shares', 'version')
            $start.os | Should -Be $(if ($t.os -match 'Server') { 'Windows Server' } elseif ($t.os -match 'Windows 11') { 'Windows 11' } else { 'Windows 10' })
            @($t.events | Where-Object { $_.event -eq 'app_start' -and $_.connections -ge 1 -and $_.folders -ge 1 }).Count | Should -BeGreaterThan 0
            @($t.events | Where-Object event -eq 'app_session').Count | Should -BeGreaterThan 0
        }

        It 'C-TEL-02 telemetry turned off deletes the install ID' -Tag 'C-TEL-02' {
            $t.idBefore | Should -BeTrue
            $t.idAfter | Should -BeFalse
        }

        It 'C-CRASH-02 killed: "UnexpectedExit" sent at the next start' -Tag 'C-CRASH-02' {
            @($t.afterKill | Where-Object { $_.event -eq 'app_crash' -and $_.errorType -eq 'UnexpectedExit' }).Count | Should -BeGreaterThan 0
        }

        It 'C-CRASH-01 a .NET crash: a file in crash\, sent at the next start as app_crash, redacted' -Tag 'C-CRASH-01' {
            Set-ItResult -Skipped -Because 'RemoteHub has no switch to force a crash (test-lab-test-document: needs a test build hook)'
        }
    }
}
