# C-POL / C-UPD / C-TEL / C-CRASH / C-MSI (part 2) - RemoteHub's policies, update check and installer, telemetry and
# crash reports, on a client VM against the lab's update.remotehub.dev inside the VM (RhMockServer.ps1), the cases
# C-POLUPD.Tests.ps1 and C-MSI.Tests.ps1 don't cover:
#   C-POL-09  UpdateChannel values and types (ga/release -> stable, prerelease/pre-release -> beta, any case; an unknown
#             value ignored; a DWORD policy stored as REG_SZ ignored)
#   C-UPD-06  no valid SHA-256 checksum - nothing requested from the download URL
#   C-UPD-07  size mismatch (larger than announced / incomplete)
#   C-UPD-08  untrusted redirect (a 302 to dl.untrusted.test - the mock under a second name, TLS-valid, not allowed)
#   C-UPD-09  update-server errors (404 stable / 404 beta / 500 / bad JSON / lower version / no answer within 15 s)
#   C-UPD-10  install declined at the open-sessions question (needs an active session: the device VM, 2VM)
#   SEC-UPD-01 an installer with the right checksum but a broken signature: refused before the approval prompt
#   SEC-UPD-02 the download swapped while Windows asks for approval: the elevated step checks its own copy and refuses
#             it; without a swap it installs that copy (docs/security-test-lab-design.md 4.11, test-lab-cases.md 13)
#   C-UPD-11  the approval is for "RemoteHub Update" (RemoteHubUpdate.exe, signed like RemoteHub), which starts the step
#             from RemoteHub's folder
#   SEC-UPD-04 a .NET profiler set in the user's environment (HKCU\Environment) doesn't reach the elevated step
#   SEC-UPD-05 RemoteHubUpdate.exe run directly: malformed values refused, a package checked like an update's
#   C-TEL-03  telemetry off discards local records (install-id deleted under the policy too)
#   C-CRASH-03 native crash -> app_crash NativeCrash (a synthetic Application Error 1000 event; there is no crash switch)
#   C-CRASH-04 crash-queue limits (newest 5 sent, older than 30 days deleted; refusal-persistence and the 3-per-session
#             handled errors need hooks the lab doesn't have - see the It)
#   C-MSI-09  same-version rebuild (New-LabRebuiltMsi: a copy with a new ProductCode / PackageCode, same version)
# Needs: the client VM, the lab mock server; C-UPD-10 also the Server VM (device). Release build (telemetry isn't sent
# from a DEBUG build). Based on lab/tests/C-POLUPD.Tests.ps1 and C-MSI.Tests.ps1. Client VMs only. See section 10.3 / 10.1.
# TimeLimitMin: 120   (a download, an install and many update checks with client restarts: > 60 min)
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
    $localAppData = "C:\Users\$($vm.ConsoleAccount)\AppData\Local\RemoteHub"
    function Save($name, $data) { $data | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $EvidenceDir "c-polupd2-$name.json") -Encoding UTF8 }
    function Shot([string]$name) { try { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "c-polupd2-$name.png") } catch { } }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    function WinText([string]$Title) { (@((Ui '*' -Title $Title -Value list -Optional -TimeoutSec 15).items) -join "`n") }
    # Values are DWORDs for [int], REG_SZ for strings - so a DWORD policy can be written as the wrong type on purpose.
    function Policy([hashtable]$Values, [string]$Hive = 'HKLM') {
        Invoke-LabGuest -Name $VmName -ArgumentList $Values, $Hive -ScriptBlock {
            param($values, $hive)
            $k = if ($hive -eq 'HKLM') { 'HKLM:\SOFTWARE\Policies\RemoteHub' } else {
                $sid = (New-Object Security.Principal.NTAccount 'Admin').Translate([Security.Principal.SecurityIdentifier]).Value
                "Registry::HKEY_USERS\$sid\SOFTWARE\Policies\RemoteHub" }
            $null = New-Item $k -Force
            foreach ($n in $values.Keys) {
                if ($null -eq $values[$n]) { Remove-ItemProperty $k -Name $n -ErrorAction SilentlyContinue }
                elseif ($values[$n] -is [int]) { Set-ItemProperty $k -Name $n -Value $values[$n] -Type DWord }
                else { Set-ItemProperty $k -Name $n -Value $values[$n] -Type String }
            }
        }
    }
    function Restart-Client { if ($script:task) { Stop-LabClient -Name $VmName -Task $script:task }; $script:task = Start-LabClient -Name $VmName }
    # started without waiting for the main window (a message box may come first); Close-Rh ends it
    function Start-Rh { $script:task = Start-LabInSession -Name $VmName -Account Console -ScriptBlock { & 'C:\Program Files\RemoteHub\RemoteHub.exe' } }
    function Wait-Main([int]$TimeoutSec = 90) {
        $r = Ui 'SearchBox' -Value exists -Optional -TimeoutSec $TimeoutSec
        # the welcome tour of a new profile (or What's new after an upgrade) closed as a user would - it is modal
        if ($r.found) { $null = Close-LabFirstStart -Name $VmName; $null = Ui 'NewButton' -Value fit -Optional -TimeoutSec 10 }
        [bool]$r.found
    }
    function Close-Rh {
        try { $null = Invoke-LabInSession -Name $VmName -Account Console -TimeoutSec 60 -ScriptBlock { Get-Process RemoteHub -ErrorAction SilentlyContinue | ForEach-Object { $null = $_.CloseMainWindow() } } } catch { }
        try { $null = Ui 'Exit' -Title '?*' -Optional -TimeoutSec 5 } catch { }
        Invoke-LabGuest -Name $VmName -ScriptBlock {
            Get-Process RemoteHub -ErrorAction SilentlyContinue | Wait-Process -Timeout 20 -ErrorAction SilentlyContinue
            Get-Process RemoteHub -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        }
        if ($script:task) { try { Stop-LabInSession -Name $VmName -Task $script:task } catch { }; $script:task = $null }
    }
    function Requests([string]$Path) { , @(Get-LabMockRequests -Name $VmName | Where-Object path -eq $Path) }
    function Clear-Requests { Invoke-LabGuest -Name $VmName -ScriptBlock { Remove-Item 'C:\ProgramData\RemoteHubLab\mock\requests.jsonl' -ErrorAction SilentlyContinue } }
    function Settings-Page([string]$Page) { $null = Ui 'Settings'; $null = Ui $Page -Title 'Settings' }
    # Settings -> Updates & privacy -> Check for updates now; reads the 'RemoteHub updates' window, then closes everything
    function CheckNow([switch]$Beta) {
        Settings-Page 'Updates & privacy'
        if ($Beta) { $null = Ui 'Beta *pre-releases*' -Title 'Settings' -Value check }
        else { $null = Ui 'GA *stable releases*' -Title 'Settings' -Value check }
        $null = Ui 'Check for updates now' -Title 'Settings'
        # the window opens with "Checking for updates..." and shows the result when the check ends (at most 15 s for
        # the server, then a little for the window): read it once that line is gone
        $deadline = (Get-Date).AddSeconds(40)
        do {
            $text = WinText 'RemoteHub updates'
            if ("$text" -notmatch 'Checking for updates') { break }
            Start-Sleep -Seconds 2
        } while ((Get-Date) -lt $deadline)
        $null = Ui 'Close' -Title 'RemoteHub updates' -Optional -TimeoutSec 10
        $null = Ui 'Later' -Title 'RemoteHub updates' -Optional -TimeoutSec 5
        $null = Ui 'Cancel' -Title 'Settings' -Optional -TimeoutSec 10
        $text
    }
    # the installed RemoteHub.exe version
    function InstalledVersion { Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-Item 'C:\Program Files\RemoteHub\RemoteHub.exe' -ErrorAction SilentlyContinue).VersionInfo.ProductVersion } }
    # sets download.size in the mock's latest.json (Set-LabMockLatest computes it from the file; this overrides it)
    function Set-MockSize([long]$Size) {
        Invoke-LabGuest -Name $VmName -ArgumentList $Size -ScriptBlock {
            param($size)
            $p = 'C:\ProgramData\RemoteHubLab\mock\latest.json'
            $j = Get-Content $p -Raw | ConvertFrom-Json
            $j.download.size = $size
            $j | ConvertTo-Json -Depth 6 | Set-Content $p -Encoding UTF8
        }
    }
    # writes the mock's latest.json byte for byte (for a body that isn't valid JSON)
    function Set-MockRaw([string]$Text) {
        Invoke-LabGuest -Name $VmName -ArgumentList $Text -ScriptBlock { param($t) Set-Content 'C:\ProgramData\RemoteHubLab\mock\latest.json' $t -Encoding UTF8 -NoNewline }
    }
    # the update server answers /latest with 500 (the mock's control.json; a missing latest.json is only a 404)
    function Set-MockServerError { Set-LabMockControl -Name $VmName -LatestStatus 500 }
    function Clear-MockServerError { Set-LabMockControl -Name $VmName -Clear }
    $script:task = $null
    $device = Get-LabDevice -For $VmName
}

AfterAll {
    if ($isClient) {
        Shot 'end'
        try { Close-Rh } catch { }
        try { Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process RemoteHub -ErrorAction SilentlyContinue | Stop-Process -Force } } catch { }
        try { Stop-LabVm -Name $device.Name } catch { }
        # policies, the mock server, an updated RemoteHub, telemetry ids here; Remote Desktop turned on at the device
        Set-LabVmDirty -Name $VmName
        if ($device) { Set-LabVmDirty -Name $device.Name }
    }
}

Describe 'C-POLUPD2 <VmName>' -Tag Client, 'C-POL', 'C-UPD', 'C-TEL', 'C-CRASH', 'C-MSI' -Skip:(-not $isClient) {

    Context 'update channel policy and server errors' {
        BeforeAll {
            $p = [ordered]@{}
            Reset-LabVm -Name $VmName
            $current = Get-LabClientMsi
            $null = Install-LabClient -Name $VmName -Network   # no disable policies: update check and telemetry allowed
            Start-LabMockServer -Name $VmName -Files $current
            Set-LabMockLatest -Name $VmName -Version '99.0.0' -File (Split-Path $current -Leaf)

            # C-POL-09: UpdateChannel read at start-up decides the /latest query; each value needs a restart
            $p.channel = [ordered]@{}
            foreach ($value in 'ga', 'release', 'PreRelease', 'pre-release', 'nightly') {
                Policy @{ UpdateChannel = $value }
                Clear-Requests
                Restart-Client
                Start-Sleep -Seconds 18
                $r = Requests '/latest'
                $p.channel[$value] = [pscustomobject]@{ count = @($r).Count; beta = @($r | Where-Object { $_.query -match 'channel=beta' }).Count }
            }
            # nightly (ignored): the channel buttons in Settings stay on
            Settings-Page 'Updates & privacy'
            $p.nightlyStable = Ui 'GA *stable releases*' -Title 'Settings' -Value exists -Optional -TimeoutSec 10
            $p.nightlyBeta = Ui 'Beta *pre-releases*' -Title 'Settings' -Value exists -Optional -TimeoutSec 5
            $null = Ui 'Cancel' -Title 'Settings' -Optional
            # ga (mapped): the buttons are managed (off)
            Policy @{ UpdateChannel = 'ga' }
            Restart-Client
            Settings-Page 'Updates & privacy'
            $p.gaStable = Ui 'GA *stable releases*' -Title 'Settings' -Value exists -Optional -TimeoutSec 10
            $null = Ui 'Cancel' -Title 'Settings' -Optional
            # a DWORD policy as REG_SZ: DisableTelemetry = "1" (string) is ignored -> telemetry is still sent
            Policy @{ UpdateChannel = $null; DisableTelemetry = '1' }
            Clear-Requests
            Restart-Client
            Start-Sleep -Seconds 18
            $p.regSzTelemetry = Requests '/telemetry'
            Policy @{ DisableTelemetry = $null }
            Save 'channel' $p

            # C-UPD-06 / C-UPD-07 / C-UPD-09: all against version 99.0.0 (always "available")
            $u = [ordered]@{}
            Set-LabMockLatest -Name $VmName -Version '99.0.0' -File (Split-Path $current -Leaf)
            Restart-Client
            Start-Sleep -Seconds 5
            # C-UPD-06: a checksum that is 64 chars but not hex -> refused before any download
            Set-LabMockLatest -Name $VmName -Version '99.0.0' -File (Split-Path $current -Leaf) -Sha256 ('Z' * 64)
            Clear-Requests
            Settings-Page 'Updates & privacy'
            $null = Ui 'Check for updates now' -Title 'Settings'
            $null = Ui 'Download and install' -Title 'RemoteHub updates' -TimeoutSec 40
            Start-Sleep -Seconds 10
            $u.noChecksum = WinText 'RemoteHub updates'
            $null = Ui 'Close' -Title 'RemoteHub updates' -Optional -TimeoutSec 5
            $null = Ui 'Later' -Title 'RemoteHub updates' -Optional -TimeoutSec 5
            $null = Ui 'Cancel' -Title 'Settings' -Optional
            $u.noChecksumDownloads = @(Get-LabMockRequests -Name $VmName | Where-Object path -like '/download/*').Count

            # C-UPD-07: size announced smaller than the file (larger than announced), then larger (incomplete)
            Set-LabMockLatest -Name $VmName -Version '99.0.0' -File (Split-Path $current -Leaf)
            Set-MockSize 1024
            Settings-Page 'Updates & privacy'
            $null = Ui 'Check for updates now' -Title 'Settings'
            $null = Ui 'Download and install' -Title 'RemoteHub updates' -TimeoutSec 40
            Start-Sleep -Seconds 12
            $u.tooLarge = WinText 'RemoteHub updates'
            $null = Ui 'Close' -Title 'RemoteHub updates' -Optional -TimeoutSec 5
            $null = Ui 'Later' -Title 'RemoteHub updates' -Optional -TimeoutSec 5
            $null = Ui 'Cancel' -Title 'Settings' -Optional
            Set-LabMockLatest -Name $VmName -Version '99.0.0' -File (Split-Path $current -Leaf)
            Set-MockSize 9999999999
            Settings-Page 'Updates & privacy'
            $null = Ui 'Check for updates now' -Title 'Settings'
            $null = Ui 'Download and install' -Title 'RemoteHub updates' -TimeoutSec 40
            Start-Sleep -Seconds 15
            $u.incomplete = WinText 'RemoteHub updates'
            $null = Ui 'Close' -Title 'RemoteHub updates' -Optional -TimeoutSec 5
            $null = Ui 'Later' -Title 'RemoteHub updates' -Optional -TimeoutSec 5
            $null = Ui 'Cancel' -Title 'Settings' -Optional
            $u.sizeFiles = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-ChildItem 'C:\Users\Admin\AppData\Local\Temp\RemoteHub\Updates' -Include *.msi, *.partial -Recurse -ErrorAction SilentlyContinue).Count }

            # C-UPD-08: the allowed URL answers 302 to dl.untrusted.test - the mock under a second name, reachable with a
            # valid certificate, but not an allowed download host
            $leaf = Split-Path $current -Leaf
            Set-LabMockLatest -Name $VmName -Version '99.0.0' -File $leaf
            Set-LabMockControl -Name $VmName -Redirects @{ "/download/$leaf" = "https://dl.untrusted.test/download/$leaf" }
            Clear-Requests
            Settings-Page 'Updates & privacy'
            $null = Ui 'Check for updates now' -Title 'Settings'
            $null = Ui 'Download and install' -Title 'RemoteHub updates' -TimeoutSec 40
            Start-Sleep -Seconds 15
            $u.redirect = WinText 'RemoteHub updates'
            $null = Ui 'Close' -Title 'RemoteHub updates' -Optional -TimeoutSec 5
            $null = Ui 'Later' -Title 'RemoteHub updates' -Optional -TimeoutSec 5
            $null = Ui 'Cancel' -Title 'Settings' -Optional
            Set-LabMockControl -Name $VmName -Clear
            $u.redirectHosts = @(Get-LabMockRequests -Name $VmName | Where-Object path -like '/download/*' | ForEach-Object host)
            $u.redirectFiles = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-ChildItem 'C:\Users\Admin\AppData\Local\Temp\RemoteHub\Updates' -Include *.msi, *.partial -Recurse -ErrorAction SilentlyContinue).Count }

            # C-UPD-09: the server errors and a lower version (Check for updates now re-fetches live)
            Set-LabMockLatest -Name $VmName -Version '99.0.0' -File (Split-Path $current -Leaf) -None
            $u.err404stable = CheckNow
            $u.err404beta = CheckNow -Beta
            Set-MockServerError
            $u.err500 = CheckNow
            Clear-MockServerError
            Set-MockRaw '{ this is : not json'
            $u.errBadJson = CheckNow
            Set-LabMockLatest -Name $VmName -Version '0.0.1' -File (Split-Path $current -Leaf)
            $u.lower = CheckNow
            # no answer within 15 s: the mock holds /latest for 25 s
            Set-LabMockLatest -Name $VmName -Version '99.0.0' -File (Split-Path $current -Leaf)
            Set-LabMockControl -Name $VmName -LatestDelayMs 25000
            $u.slow = CheckNow
            Set-LabMockControl -Name $VmName -Clear
            Start-Sleep -Seconds 15   # the held request ends before the next one
            Save 'updates' $u
        }

        It 'C-POL-09 UpdateChannel: ga/release -> stable, prerelease/pre-release -> beta (any case), an unknown value ignored; a DWORD policy stored as REG_SZ ignored' -Tag 'C-POL-09' {
            foreach ($v in 'ga', 'release') {
                $p.channel[$v].count | Should -BeGreaterThan 0 -Because "$v still checks for updates"
                $p.channel[$v].beta | Should -Be 0 -Because "$v maps to stable (no ?channel=beta)"
            }
            foreach ($v in 'PreRelease', 'pre-release') {
                $p.channel[$v].beta | Should -BeGreaterThan 0 -Because "$v maps to beta (?channel=beta)"
            }
            $p.channel['nightly'].count | Should -BeGreaterThan 0 -Because 'an unknown value still checks (the user''s channel applies)'
            $p.nightlyStable.found | Should -BeTrue
            $p.nightlyStable.enabled | Should -BeTrue -Because 'an unknown policy value leaves the channel buttons on'
            $p.nightlyBeta.enabled | Should -BeTrue
            $p.gaStable.enabled | Should -BeFalse -Because 'a valid policy value manages the channel'
            @($p.regSzTelemetry).Count | Should -BeGreaterThan 0 -Because 'DisableTelemetry as REG_SZ is the wrong type and ignored, so telemetry is still sent'
        }

        It 'C-UPD-06 no valid SHA-256 checksum: refused, nothing requested from the download URL' -Tag 'C-UPD-06' {
            $u.noChecksum | Should -Match "The update has no valid SHA-256 checksum, so it can't be verified\."
            $u.noChecksumDownloads | Should -Be 0 -Because 'the download URL is never requested'
        }

        It 'C-UPD-07 size mismatch: "larger than announced" / "incomplete (x of y bytes)"; nothing kept' -Tag 'C-UPD-07' {
            $u.tooLarge | Should -Match 'The download is larger than announced\.'
            $u.incomplete | Should -Match 'The download is incomplete \([\d,\.]+ of [\d,\.]+ bytes\)\.'
            $u.sizeFiles | Should -Be 0 -Because 'neither .partial nor .msi is kept'
        }

        It 'C-UPD-08 untrusted redirect: an allowed URL redirecting to an untrusted host is refused' -Tag 'C-UPD-08' {
            # the redirect was followed (the request reached the untrusted name) and then refused
            $u.redirectHosts | Should -Contain 'dl.untrusted.test' -Because 'the client followed the 302 (precondition)'
            $u.redirect | Should -Match "The download was redirected to an address that isn't trusted\."
            $u.redirectFiles | Should -Be 0 -Because 'nothing from the untrusted host is kept'
        }

        It 'C-UPD-09 update-server errors: 404 stable/beta, 500, bad JSON, a lower version' -Tag 'C-UPD-09' {
            # "no answer in 15 s" ("The update server didn't answer in time.") is not checked - the mock can't stall a request.
            $u.err404stable | Should -Match 'No release in this channel yet'
            $u.err404stable | Should -Match 'There is no GA \(stable\) release yet\. Switch to the Beta channel'
            $u.err404beta | Should -Match 'The update server has no release for this channel\.'
            $u.err500 | Should -Match "Couldn't check for updates"
            $u.errBadJson | Should -Match "an answer RemoteHub doesn't understand\."
            $u.lower | Should -Match 'RemoteHub is up to date'
            $u.slow | Should -Match "The update server didn't answer in time\."
        }
    }

    Context 'install declined at the open-sessions question' {
        BeforeAll {
            $i = [ordered]@{}
            Reset-LabVm -Name $device.Name
            $deviceIp = Get-LabVmAddress -Name $device.Name
            $null = Invoke-LabGuest -Name $device.Name -ScriptBlock {
                Set-ItemProperty 'HKLM:\System\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
                Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -ErrorAction SilentlyContinue
            }
            Reset-LabVm -Name $VmName
            $current = Get-LabClientMsi
            $null = Install-LabClient -Name $VmName -Network
            Start-LabMockServer -Name $VmName -Files $current
            Set-LabMockLatest -Name $VmName -Version '99.0.0' -File (Split-Path $current -Leaf)
            $i.versionBefore = InstalledVersion
            Restart-Client
            New-LabClientConnection -Name $VmName -Display 'Device' -TargetHost $deviceIp -User $device.AdminAccount -Password $password
            # connect, so a session is active when the open-sessions question comes
            $null = Ui 'Device' -Value doubleclick
            $null = Ui 'Yes' -Title '*Remote Desktop*' -Optional -TimeoutSec 20
            $i.connected = Ui 'Disconnect' -Value exists -Optional -TimeoutSec 120
            Clear-Requests
            Settings-Page 'Updates & privacy'
            $null = Ui 'Check for updates now' -Title 'Settings'
            $null = Ui 'Download and install' -Title 'RemoteHub updates' -TimeoutSec 40
            # the download runs and verifies, then the open-sessions question comes; decline it
            $i.confirm = $null
            $deadline = (Get-Date).AddSeconds(120)
            do {
                Start-Sleep -Seconds 3
                # the question's window is named by its text in UI Automation (the shown title is "Install update")
                $m = Ui '*' -Title '*still connected*' -Value list -Optional -TimeoutSec 3
                if ($m.found) { $i.confirm = ($m.items -join "`n"); break }
            } while ((Get-Date) -lt $deadline)
            $null = Ui 'Cancel' -Title '*still connected*' -Optional -TimeoutSec 10
            Start-Sleep -Seconds 3
            $i.status = WinText 'RemoteHub updates'
            $i.installEnabled = Ui 'Download and install' -Title 'RemoteHub updates' -Value exists -Optional -TimeoutSec 5
            Shot 'upd10'
            $null = Ui 'Close' -Title 'RemoteHub updates' -Optional -TimeoutSec 5
            $null = Ui 'Later' -Title 'RemoteHub updates' -Optional -TimeoutSec 5
            $null = Ui 'Cancel' -Title 'Settings' -Optional
            # "msiexec.exe /V" is the Windows Installer service: Install-LabClient above leaves it running for about
            # 10 minutes. Only an install the update started counts - a client msiexec, or the helper script RemoteHub
            # writes next to the download before it exits (UpdateService.StartInstall).
            $i.msiexec = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $procs = @(Get-CimInstance Win32_Process -Filter "Name='msiexec.exe'" | Where-Object { $_.CommandLine -notmatch '\s/V\s*$' })
                [pscustomobject]@{
                    installs     = $procs.Count
                    commandLines = @($procs | ForEach-Object CommandLine)
                    helper       = Test-Path 'C:\Users\Admin\AppData\Local\Temp\RemoteHub\Updates\install-update.cmd'
                }
            }
            Start-Sleep -Seconds 5
            $i.versionAfter = InstalledVersion
            Close-Rh
            Save 'upd10' $i
        }

        It 'C-UPD-10 install declined at the open-sessions question: "Install cancelled. The verified download is kept until the next update."; no install; Download and install enabled again' -Tag 'C-UPD-10' {
            $i.connected.found | Should -BeTrue -Because 'the session must be active for the question to come'
            $i.confirm | Should -Match 'Install'
            $i.status | Should -Match 'Install cancelled\. The verified download is kept until the next update\.'
            $i.installEnabled.found | Should -BeTrue
            $i.installEnabled.enabled | Should -BeTrue
            $i.msiexec.installs | Should -Be 0 -Because "the install was declined (msiexec: $($i.msiexec.commandLines -join '; '))"
            $i.msiexec.helper | Should -BeFalse -Because 'the install helper is written only when the install goes ahead'
            $i.versionAfter | Should -Be $i.versionBefore
        }
    }

    Context 'update signature and the elevated install step (SEC-UPD)' {
        BeforeAll {
            $u = [ordered]@{}
            Reset-LabVm -Name $VmName
            $current = Get-LabClientMsi
            $updates = 'C:\Users\Admin\AppData\Local\Temp\RemoteHub\Updates'
            # Install-LabClient makes the VM trust the lab certificate, which signed the build under test. Installed is a
            # rebuild of the build under test (the same files, another ProductCode), so the signed package offered below
            # is a real (same-version) upgrade, as every release is (WiX gives each build its own ProductCode). The
            # package over itself was a maintenance run of the installed product, which needs the package it was installed
            # from and failed with 1603 "SecureRepair Failed" (run 20261022-112607, msiexec log).
            $installed = New-LabRebuiltMsi -Source $current -Path (Join-Path $EvidenceDir 'installed\RemoteHub-installed-x64.msi')
            $null = Install-LabClient -Name $VmName -Network -Msi $installed.FullName
            $u.versionBefore = InstalledVersion
            # the same package rebuilt (New-LabRebuiltMsi changes the ProductCode): its signature no longer matches it
            $rebuilt = New-LabRebuiltMsi -Source $current -Path (Join-Path $EvidenceDir 'RemoteHub-99.0.0-x64.msi')
            Copy-LabFile -Name $VmName -Path $rebuilt.FullName -Destination 'C:\LabClient\swap'
            Start-LabMockServer -Name $VmName -Files $current, $rebuilt.FullName
            function Consent { Invoke-LabGuest -Name $VmName -ScriptBlock { [bool](Get-Process consent -ErrorAction SilentlyContinue) } }
            # Windows' approval prompt answered (Alt+Y yes, Alt+N no) until it is gone - a key can be lost on the secure
            # desktop (run 20261021-222745: after the swap RemoteHub still waited for approval 90 s later); true when it went
            function Answer-Consent([int[]]$Keys) {
                for ($i = 0; $i -lt 3; $i++) {
                    Send-LabKeyCombo -Name $VmName -Codes $Keys
                    $until = (Get-Date).AddSeconds(10)
                    do { Start-Sleep -Seconds 1 } while ((Consent) -and (Get-Date) -lt $until)
                    if (-not (Consent)) { return $true }
                }
                $false
            }
            function Install-Now {
                Settings-Page 'Updates & privacy'
                $null = Ui 'Check for updates now' -Title 'Settings'
                $null = Ui 'Download and install' -Title 'RemoteHub updates' -TimeoutSec 40
            }
            # the elevated step's folder and outcomes, read as the admin account
            function Staging {
                Invoke-LabGuest -Name $VmName -ScriptBlock {
                    $stage = 'C:\Program Files\RemoteHub Updates'
                    if (-not (Test-Path $stage)) { return [pscustomobject]@{ exists = $false } }
                    $acl = Get-Acl $stage
                    [pscustomobject]@{
                        exists    = $true
                        protected = $acl.AreAccessRulesProtected
                        who       = @($acl.Access | ForEach-Object { try { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { "$($_.IdentityReference)" } } | Sort-Object -Unique)
                        msi       = @(Get-ChildItem $stage -Filter *.msi -Force -ErrorAction SilentlyContinue).Count
                        results   = @(Get-ChildItem (Join-Path $stage 'results') -Filter *.txt -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | ForEach-Object { (Get-Content $_.FullName -Raw).Trim() })
                        # msiexec's log of the last install: its errors and the action that returned 3 (failed)
                        logErrors = @(Get-ChildItem (Join-Path $stage 'results') -Filter *.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1 |
                                ForEach-Object { Get-Content $_.FullName | Where-Object { $_ -match 'Error \d{4}|Return value 3|error code 16' } | Select-Object -First 12 })
                    }
                }
            }
            function Wait-Restarted([int]$TimeoutSec = 300) {
                $until = (Get-Date).AddSeconds($TimeoutSec)
                do { Start-Sleep -Seconds 5; $n = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-Process RemoteHub -ErrorAction SilentlyContinue).Count } } while (-not $n -and (Get-Date) -lt $until)
                [bool]$n
            }

            # C-UPD-11: what Windows asks approval for - the installed RemoteHubUpdate.exe next to the step's script
            $u.updater = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $exe = 'C:\Program Files\RemoteHub\RemoteHubUpdate.exe'
                if (-not (Test-Path $exe)) { return [pscustomobject]@{ exists = $false } }
                $sig = Get-AuthenticodeSignature $exe
                $rh = Get-AuthenticodeSignature 'C:\Program Files\RemoteHub\RemoteHub.exe'
                [pscustomobject]@{
                    exists      = $true
                    description = (Get-Item $exe).VersionInfo.FileDescription
                    status      = "$($sig.Status)"
                    sameSigner  = [bool]($sig.SignerCertificate -and $sig.SignerCertificate.Thumbprint -eq $rh.SignerCertificate.Thumbprint)
                    script      = Test-Path 'C:\Program Files\RemoteHub\InstallUpdate.ps1'
                }
            }

            # SEC-UPD-01: right checksum and size, broken signature -> refused before Windows asks, nothing kept
            Set-LabMockLatest -Name $VmName -Version '99.0.0' -File (Split-Path $rebuilt.FullName -Leaf)
            Restart-Client
            Install-Now
            $u.unsignedConsent = $false
            $until = (Get-Date).AddSeconds(120)
            do {
                Start-Sleep -Seconds 2
                if (Consent) { $u.unsignedConsent = $true }
                $u.unsigned = WinText 'RemoteHub updates'
            } while ("$($u.unsigned)" -notmatch 'won''t be installed' -and (Get-Date) -lt $until)
            if (Consent) { $u.unsignedConsent = $true; $null = Answer-Consent 0x12, 0x4E }   # Alt+N: never install it
            $u.unsignedFiles = Invoke-LabGuest -Name $VmName -ArgumentList $updates -ScriptBlock { param($d) @(Get-ChildItem $d -Filter *.msi -ErrorAction SilentlyContinue).Count }
            Shot 'sec-upd-01'
            $null = Ui 'Later' -Title 'RemoteHub updates' -Optional -TimeoutSec 5
            $null = Ui 'Cancel' -Title 'Settings' -Optional

            # SEC-UPD-02: the signed package; while Windows asks for approval, the user's download is swapped for the rebuild
            Set-LabMockLatest -Name $VmName -Version '99.0.0' -File (Split-Path $current -Leaf)
            Restart-Client
            Install-Now
            $until = (Get-Date).AddSeconds(180)
            while (-not (Consent) -and (Get-Date) -lt $until) { Start-Sleep -Seconds 2 }
            $u.swapConsent = Consent
            Shot 'c-upd-11-prompt'   # the secure desktop as the VM's console shows it: the program and its publisher
            $u.swap = Invoke-LabGuest -Name $VmName -ArgumentList $updates -ScriptBlock {
                param($d)
                $msi = @(Get-ChildItem $d -Filter *.msi)[0]
                Copy-Item 'C:\LabClient\swap\RemoteHub-99.0.0-x64.msi' $msi.FullName -Force
                (Get-FileHash $msi.FullName).Hash
            }
            if ($u.swapConsent) { $u.swapApproved = Answer-Consent 0x12, 0x59 }   # Alt+Y: approve
            $u.swapRestarted = Wait-Restarted
            $until = (Get-Date).AddSeconds(90)
            # the outcome comes as a message inside RemoteHub's main window after the restart (run 20261022-061254: on
            # screen, but not a window of its own for the title search), so the main window is read too
            do { Start-Sleep -Seconds 3; $u.swapMessage = "$(WinText 'RemoteHub updates')`n$(WinText 'RemoteHub')" } while ("$($u.swapMessage)" -notmatch 'wasn''t installed' -and (Get-Date) -lt $until)
            Shot 'sec-upd-02'
            $null = Ui 'OK' -Title 'RemoteHub updates' -Optional -TimeoutSec 5
            $null = Ui 'OK' -Title 'RemoteHub' -Optional -TimeoutSec 5
            $u.swapStaging = Staging
            $u.swapVersion = InstalledVersion

            # the same without the swap: the elevated step installs its own checked copy and RemoteHub starts again.
            # Nothing of the swap left: no prompt still open, no elevated step still waiting for RemoteHub
            if (Consent) { $null = Answer-Consent 0x12, 0x4E }
            # (RemoteHubUpdate.exe runs InstallUpdate.ps1; the Windows PowerShell fallback names the staging folder)
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock {
                Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'InstallUpdate\.ps1|RemoteHub Updates' } |
                    ForEach-Object { Wait-Process -Id $_.ProcessId -Timeout 120 -ErrorAction SilentlyContinue }
            }
            # SEC-UPD-04: a .NET profiler in the user's own environment, which Windows gives their elevated processes too.
            # Windows PowerShell would try to load it and write a .NET Runtime event naming its process id; the profiler's
            # file doesn't exist, so nothing runs either way. Set before RemoteHub starts again, so it has it as well.
            $profiler = '{' + [guid]::NewGuid().ToString().ToUpperInvariant() + '}'
            $profilerSince = Invoke-LabGuest -Name $VmName -ArgumentList $profiler -ScriptBlock {
                param($clsid)
                foreach ($v in @(@('COR_ENABLE_PROFILING', '1'), @('COR_PROFILER', $clsid), @('COR_PROFILER_PATH', 'C:\Users\Public\rh-lab-profiler.dll'))) {
                    Set-ItemProperty -Path 'HKCU:\Environment' -Name $v[0] -Value $v[1]
                }
                Get-Date
            }
            Close-Rh
            Restart-Client
            Install-Now
            $until = (Get-Date).AddSeconds(180)
            while (-not (Consent) -and (Get-Date) -lt $until) { Start-Sleep -Seconds 2 }
            $u.okConsent = Consent
            if ($u.okConsent) { $u.okApproved = Answer-Consent 0x12, 0x59 }
            # the step while it installs (msiexec takes a while): its command line and process id
            $until = (Get-Date).AddSeconds(60)
            do {
                $u.step = Invoke-LabGuest -Name $VmName -ScriptBlock {
                    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'InstallUpdate\.ps1|RemoteHub Updates' } |
                        Select-Object -First 1 ProcessId, CommandLine
                }
                if (-not $u.step) { Start-Sleep -Seconds 1 }
            } while (-not $u.step -and (Get-Date) -lt $until)
            $u.okRestarted = Wait-Restarted
            $until = (Get-Date).AddSeconds(120)
            do { Start-Sleep -Seconds 5; $u.okStaging = Staging } while (@($u.okStaging.results).Count -lt 2 -and (Get-Date) -lt $until)
            $u.okMessage = "$(WinText 'RemoteHub updates')`n$(WinText 'RemoteHub')"
            Close-Rh
            try { Get-LabFile -Name $VmName -Path 'C:\Program Files\RemoteHub Updates\results\*.log' -Destination (Join-Path $EvidenceDir 'c-polupd2-sec-upd-msi-logs') } catch { }

            # SEC-UPD-04: which processes tried to load the profiler. A Windows PowerShell started here with the same
            # values shows that the event is written at all (otherwise "not in the step" would prove nothing).
            $u.profiler = Invoke-LabGuest -Name $VmName -ArgumentList $profiler, $profilerSince -ScriptBlock {
                param($clsid, $since)
                $env:COR_ENABLE_PROFILING = '1'; $env:COR_PROFILER = $clsid; $env:COR_PROFILER_PATH = 'C:\Users\Public\rh-lab-profiler.dll'
                $control = (Start-Process "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList '-NoProfile', '-Command', 'exit' -WindowStyle Hidden -PassThru -Wait).Id
                foreach ($n in 'COR_ENABLE_PROFILING', 'COR_PROFILER', 'COR_PROFILER_PATH') { Remove-ItemProperty -Path 'HKCU:\Environment' -Name $n -ErrorAction SilentlyContinue }
                Start-Sleep -Seconds 2
                $pids = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = '.NET Runtime'; StartTime = $since } -ErrorAction SilentlyContinue |
                    Where-Object { $_.Message -match [regex]::Escape($clsid) -and $_.Message -match 'Process ID \(decimal\): (\d+)' } | ForEach-Object { [int]$Matches[1] })
                [pscustomobject]@{ control = $control; pids = $pids }
            }

            # SEC-UPD-05: RemoteHubUpdate.exe run directly (this session is elevated, so Windows doesn't ask): values it
            # doesn't take start nothing; a package it takes is checked like an update's - the rebuild isn't installed.
            $u.direct = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $exe = 'C:\Program Files\RemoteHub\RemoteHubUpdate.exe'
                $msi = 'C:\LabClient\swap\RemoteHub-99.0.0-x64.msi'
                $sha = (Get-FileHash $msi -Algorithm SHA256).Hash
                $results = 'C:\Program Files\RemoteHub Updates\results'
                # a process id that has already ended: the step doesn't wait
                $gone = "$((Start-Process "$env:SystemRoot\System32\cmd.exe" -ArgumentList '/c', 'exit' -WindowStyle Hidden -PassThru -Wait).Id)"
                function Run([string[]]$Arguments) { (Start-Process $exe -ArgumentList $Arguments -PassThru -Wait).ExitCode }
                function NewId { [guid]::NewGuid().ToString('N') }
                $before = @(Get-ChildItem $results -Filter *.txt -ErrorAction SilentlyContinue).Count
                $refused = [ordered]@{
                    shortSha  = Run '--msi', $msi, '--sha256', $sha.Substring(1), '--pid', $gone, '--id', (NewId)
                    publisher = Run '--msi', $msi, '--sha256', $sha, '--pid', $gone, '--id', (NewId), '--publisher', 'CN=Someone'
                    stream    = Run '--msi', 'C:\LabClient\swap\a:b.msi', '--sha256', $sha, '--pid', $gone, '--id', (NewId)
                    unc       = Run '--msi', '\\localhost\C$\LabClient\swap\RemoteHub-99.0.0-x64.msi', '--sha256', $sha, '--pid', $gone, '--id', (NewId)
                    traversal = Run '--msi', 'C:\LabClient\swap\..\swap\RemoteHub-99.0.0-x64.msi', '--sha256', $sha, '--pid', $gone, '--id', (NewId)
                }
                $afterRefused = @(Get-ChildItem $results -Filter *.txt -ErrorAction SilentlyContinue).Count
                $id = NewId
                $step = Run '--msi', $msi, '--sha256', $sha, '--pid', $gone, '--id', $id
                if ($step -gt 0) { Wait-Process -Id $step -Timeout 120 -ErrorAction SilentlyContinue }
                [pscustomobject]@{
                    refused          = $refused
                    refusedResults   = $afterRefused - $before
                    step             = $step
                    result           = "$(Get-Content (Join-Path $results "$id.txt") -ErrorAction SilentlyContinue)".Trim()
                    msiLeft          = @(Get-ChildItem 'C:\Program Files\RemoteHub Updates' -Filter *.msi -Force -ErrorAction SilentlyContinue).Count
                }
            }
            Save 'sec-upd' $u
        }

        It 'SEC-UPD-01 an installer whose signature doesn''t match is refused before Windows asks; nothing is kept' -Tag 'SEC-UPD-01' {
            $u.unsigned | Should -Match 'signature that doesn''t match the file or that this PC doesn''t trust\. It was deleted and won''t be installed\.'
            $u.unsignedConsent | Should -BeFalse -Because 'the refusal comes before the administrator approval'
            $u.unsignedFiles | Should -Be 0
        }

        It 'SEC-UPD-02 a download swapped while Windows asks for approval isn''t installed; the step''s folder is for administrators only' -Tag 'SEC-UPD-02' {
            $u.swapConsent | Should -BeTrue -Because 'Windows asks for approval before RemoteHub exits'
            $u.swapApproved | Should -BeTrue -Because 'the approval prompt closes once answered'
            $u.swapRestarted | Should -BeTrue -Because 'the restart helper starts RemoteHub again after the elevated step'
            $u.swapMessage | Should -Match 'the downloaded file changed after RemoteHub had checked it'
            @($u.swapStaging.results)[0] | Should -Be 'refused changed'
            $u.swapStaging.msi | Should -Be 0 -Because 'the step removes its copy'
            $u.swapStaging.protected | Should -BeTrue
            @($u.swapStaging.who | Where-Object { $_ -notin 'S-1-5-18', 'S-1-5-32-544' }) | Should -BeNullOrEmpty -Because "only SYSTEM and Administrators (users may read only its results folder) - found: $($u.swapStaging.who -join ', ')"
            $u.swapVersion | Should -Be $u.versionBefore
        }

        It 'SEC-UPD-02 without a swap the elevated step installs its own checked copy and RemoteHub starts again' -Tag 'SEC-UPD-02' {
            $u.okConsent | Should -BeTrue
            $u.okRestarted | Should -BeTrue
            $u.okApproved | Should -BeTrue -Because 'the approval prompt closes once answered'
            @($u.okStaging.results)[-1] | Should -Match '^installed (0|3010)$' -Because "the step's outcomes: $(@($u.okStaging.results) -join ' / '); msiexec: $(@($u.okStaging.logErrors) -join ' | ')"
            $u.okStaging.msi | Should -Be 0
            "$($u.okMessage)" | Should -Not -Match 'wasn''t installed|couldn''t be installed'
        }

        It 'C-UPD-11 the approval is for RemoteHub Update, signed like RemoteHub, which starts the step from RemoteHub''s folder' -Tag 'C-UPD-11' {
            $u.updater.exists | Should -BeTrue -Because 'the MSI installs RemoteHubUpdate.exe next to RemoteHub.exe'
            $u.updater.description | Should -Be 'RemoteHub Update' -Because 'Windows shows the file description as the program''s name'
            $u.updater.status | Should -Be 'Valid'
            $u.updater.sameSigner | Should -BeTrue -Because 'the prompt''s publisher is RemoteHub''s'
            $u.updater.script | Should -BeTrue
            $u.step | Should -Not -BeNullOrEmpty -Because 'the step runs while the package installs'
            $u.step.CommandLine | Should -Match '-File "C:\\Program Files\\RemoteHub\\InstallUpdate\.ps1"'
            $u.step.CommandLine | Should -Match '-Signer "C:\\Program Files\\RemoteHub\\RemoteHubUpdate\.exe"'
            $u.step.CommandLine | Should -Not -Match '-Publisher|-EncodedCommand' -Because 'not the Windows PowerShell fallback'
        }

        It 'SEC-UPD-04 a .NET profiler set in the user''s environment doesn''t reach the elevated step' -Tag 'SEC-UPD-04' {
            $u.profiler.pids | Should -Contain $u.profiler.control -Because 'Windows PowerShell with the profiler set writes a .NET Runtime event (the check can see it)'
            $u.step.ProcessId | Should -BeGreaterThan 0
            $u.profiler.pids | Should -Not -Contain $u.step.ProcessId -Because "RemoteHubUpdate.exe gives the step a fresh environment - events from: $(@($u.profiler.pids) -join ', ')"
        }

        It 'SEC-UPD-05 RemoteHubUpdate.exe run directly: values it doesn''t take start nothing, a package it takes is checked like an update''s' -Tag 'SEC-UPD-05' {
            foreach ($case in $u.direct.refused.Keys) { $u.direct.refused[$case] | Should -Be 0 -Because "$case is refused before anything starts" }
            $u.direct.refusedResults | Should -Be 0
            $u.direct.step | Should -BeGreaterThan 0 -Because 'well-formed values start the step (its process id is the exit code)'
            $u.direct.result | Should -Be 'refused signature' -Because 'the rebuilt package''s signature doesn''t match it'
            $u.direct.msiLeft | Should -Be 0
        }
    }

    Context 'telemetry and crash reports' {
        BeforeAll {
            $t = [ordered]@{}
            Reset-LabVm -Name $VmName
            $current = Get-LabClientMsi
            $currentVersion = (Split-Path $current -Leaf) -replace '^RemoteHub-(.+)-x64\.msi$', '$1'
            $null = Install-LabClient -Name $VmName -Network
            Policy @{ DisableUpdateCheck = 1 }   # no update traffic; telemetry stays on (default)
            Start-LabMockServer -Name $VmName

            # ---- C-TEL-03: telemetry on first, so install-id / run-time.json / a pending crash exist
            Restart-Client
            Start-Sleep -Seconds 75   # run-time.json is written and ticks once a minute
            Close-Rh
            $t.idBefore = Invoke-LabGuest -Name $VmName -ScriptBlock { Test-Path 'C:\Users\Admin\AppData\Local\RemoteHub\install-id' }
            $t.runtimeBefore = Invoke-LabGuest -Name $VmName -ScriptBlock { Test-Path 'C:\Users\Admin\AppData\Local\RemoteHub\run-time.json' }
            # a pending crash file waiting to be sent
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $d = 'C:\Users\Admin\AppData\Local\RemoteHub\crash'; $null = New-Item -ItemType Directory -Path $d -Force
                $r = [ordered]@{ Version = '9.9.9'; ErrorType = 'InvalidOperationException'; Message = 'lab pending crash'; Stack = 'at Lab.Pending()'; TimeUtc = (Get-Date).ToUniversalTime().ToString('o') }
                Set-Content (Join-Path $d "crash-$((Get-Date).Ticks)-labtel.json") ($r | ConvertTo-Json) -Encoding UTF8
            }
            # telemetry OFF by policy, then the next start must discard the local records
            Policy @{ DisableTelemetry = 1 }
            Clear-Requests
            Restart-Client
            Start-Sleep -Seconds 20
            $t.off = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $l = 'C:\Users\Admin\AppData\Local\RemoteHub'
                [pscustomobject]@{
                    installId  = Test-Path "$l\install-id"
                    runtime    = Test-Path "$l\run-time.json"
                    crashFiles = @(Get-ChildItem "$l\crash" -Filter 'crash-*.json' -ErrorAction SilentlyContinue | ForEach-Object Name).Count
                }
            }
            $t.offRequests = Requests '/telemetry'   # (already an array; @() would nest it)
            Close-Rh
            Policy @{ DisableTelemetry = $null }
            Save 'tel03' $t

            # ---- C-CRASH-03: a synthetic Application Error 1000 for RemoteHub.exe after an unclean exit
            $c = [ordered]@{}
            Reset-LabVm -Name $VmName
            $null = Install-LabClient -Name $VmName -Network
            Policy @{ DisableUpdateCheck = 1 }
            Start-LabMockServer -Name $VmName
            Restart-Client
            Start-Sleep -Seconds 10
            # end it unclean (the session marker stays), then write the 1000 event, then start again
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process RemoteHub -ErrorAction SilentlyContinue | Stop-Process -Force }
            if ($script:task) { try { Stop-LabInSession -Name $VmName -Task $script:task } catch { }; $script:task = $null }
            $c.injected = Invoke-LabGuest -Name $VmName -ScriptBlock {
                try {
                    $src = 'Application Error'
                    if (-not [System.Diagnostics.EventLog]::SourceExists($src)) { [System.Diagnostics.EventLog]::CreateEventSource($src, 'Application') }
                    $log = New-Object System.Diagnostics.EventLog 'Application'
                    $log.Source = $src
                    # the eight insertion strings Windows writes for a native fault (CrashReporter reads [0],[3],[4],[6],[7])
                    $data = @('RemoteHub.exe', '0.0.0.0', '00000000', 'clrhost.dll', '1.2.3.4', '00000000',
                        '0xc0000005', '0x000000000001a2b3')
                    $inst = New-Object System.Diagnostics.EventInstance 1000, 0, ([System.Diagnostics.EventLogEntryType]::Error)
                    $log.WriteEvent($inst, $data)
                    'ok'
                }
                catch { "ERROR: $_" }
            }
            Clear-Requests
            Restart-Client
            Start-Sleep -Seconds 25
            # every telemetry event that arrived (run 20261016-024318: no app_crash - was anything sent at all?)
            $c.all = @((Requests '/telemetry') | ForEach-Object json | ForEach-Object event)
            $c.pending = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-ChildItem 'C:\Users\Admin\AppData\Local\RemoteHub\crash' -ErrorAction SilentlyContinue | ForEach-Object Name) }
            $c.events = @((Requests '/telemetry') | ForEach-Object json | Where-Object { $_.event -eq 'app_crash' })
            Close-Rh
            # why nothing may have arrived: is the mock running, what did it get, what does RemoteHub's log say
            $c.why = Invoke-LabGuest -Name $VmName -ScriptBlock { [ordered]@{ mock = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object CommandLine -match 'RhMockServer').Count; requests = @(Get-Content 'C:\ProgramData\RemoteHubLab\mock\requests.jsonl' -ErrorAction SilentlyContinue | ForEach-Object { ($_ | ConvertFrom-Json).path }); diag = @(Get-Content 'C:\Users\Admin\AppData\Roaming\RemoteHub\logs\remotehub.log' -Tail 25 -ErrorAction SilentlyContinue) } }
            Save 'crash03' @{ why = $c.why;  injected = $c.injected; events = $c.events; all = $c.all; pending = $c.pending; version = $currentVersion }

            # ---- C-CRASH-04: seven recent crash files + one older than 30 days, telemetry on; the newest 5 are sent
            Reset-LabVm -Name $VmName
            $null = Install-LabClient -Name $VmName -Network
            Policy @{ DisableUpdateCheck = 1 }
            Start-LabMockServer -Name $VmName
            $q = [ordered]@{}
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $d = 'C:\Users\Admin\AppData\Local\RemoteHub\crash'; $null = New-Item -ItemType Directory -Path $d -Force
                Get-ChildItem $d -Filter 'crash-*.json' -ErrorAction SilentlyContinue | Remove-Item -Force
                $base = (Get-Date).Ticks
                foreach ($n in 1..7) {
                    $r = [ordered]@{ Version = '9.9.9'; ErrorType = 'InvalidOperationException'; Message = "labcrash$n"; Stack = 'at Lab.N()'; TimeUtc = (Get-Date).ToUniversalTime().ToString('o') }
                    Set-Content (Join-Path $d ("crash-{0}-lab{1}.json" -f ($base + $n), $n)) ($r | ConvertTo-Json) -Encoding UTF8
                }
                # older than 30 days: deleted, never sent (a small Ticks sorts it first)
                $old = [ordered]@{ Version = '9.9.9'; ErrorType = 'InvalidOperationException'; Message = 'labcrashOld'; Stack = 'at Lab.Old()'; TimeUtc = (Get-Date).AddDays(-31).ToUniversalTime().ToString('o') }
                Set-Content (Join-Path $d ('crash-{0}-labold.json' -f ($base - 1000))) ($old | ConvertTo-Json) -Encoding UTF8
            }
            Clear-Requests
            Restart-Client
            Start-Sleep -Seconds 25
            $q.all = @((Requests '/telemetry') | ForEach-Object json | ForEach-Object event)
            $q.sent = @((Requests '/telemetry') | ForEach-Object json | Where-Object { $_.event -eq 'app_crash' } | ForEach-Object message)
            $q.left = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-ChildItem 'C:\Users\Admin\AppData\Local\RemoteHub\crash' -Filter 'crash-*.json' -ErrorAction SilentlyContinue | ForEach-Object Name).Count }
            Close-Rh
            $q.why = Invoke-LabGuest -Name $VmName -ScriptBlock { [ordered]@{ mock = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object CommandLine -match 'RhMockServer').Count; requests = @(Get-Content 'C:\ProgramData\RemoteHubLab\mock\requests.jsonl' -ErrorAction SilentlyContinue | ForEach-Object { ($_ | ConvertFrom-Json).path }); diag = @(Get-Content 'C:\Users\Admin\AppData\Roaming\RemoteHub\logs\remotehub.log' -Tail 25 -ErrorAction SilentlyContinue) } }
            Save 'crash04' $q
        }

        It 'C-TEL-03 telemetry off (policy) discards local records: crash files and run-time.json gone, nothing sent, install-id deleted too' -Tag 'C-TEL-03' {
            $t.idBefore | Should -BeTrue
            $t.runtimeBefore | Should -BeTrue
            @($t.offRequests) | Should -BeNullOrEmpty -Because 'nothing is sent while telemetry is off'
            $t.off.crashFiles | Should -Be 0 -Because 'pending crash files are deleted and never sent'
            $t.off.runtime | Should -BeFalse -Because 'run-time.json is removed'
            $t.off.installId | Should -BeFalse -Because 'telemetry fully off must delete the install ID under the policy too'
        }

        It 'C-CRASH-03 a native crash (Application Error 1000): app_crash with errorType NativeCrash, exception message and module+offset stack, the crashed session''s version' -Tag 'C-CRASH-03' {
            if ($c.injected -ne 'ok') { Set-ItResult -Skipped -Because "couldn't write a synthetic Application Error 1000 event: $($c.injected)"; return }
            $native = @($c.events | Where-Object { $_.errorType -eq 'NativeCrash' })
            $native.Count | Should -BeGreaterThan 0 -Because 'the unclean exit plus the 1000 event is reported as a native crash'
            $native[0].message | Should -Match '^Exception 0x[0-9a-fA-F]+ in .+'
            $native[0].stack | Should -Match '\+0x[0-9a-fA-F]+$'
            $native[0].version | Should -BeLike "$currentVersion*" -Because 'the version of the crashed session'
        }

        It 'C-CRASH-04 crash-queue limits: at most the newest 5 sent, older than 30 days deleted' -Tag 'C-CRASH-04' {
            # The other two parts of the case need hooks the lab doesn't have and are not checked here:
            #   - "a report the server refuses stays and goes at the next start" needs a mock that rejects /telemetry;
            #   - "a handled error recorded at most 3 times per session" needs a way to make RemoteHub throw on the UI thread.
            $sent = @($q.sent)
            $sent.Count | Should -Be 5 -Because 'at most the newest 5 reports are sent'
            foreach ($n in 3..7) { $sent | Should -Contain "labcrash$n" -Because 'the five newest' }
            $sent | Should -Not -Contain 'labcrash1'
            $sent | Should -Not -Contain 'labcrash2'
            $sent | Should -Not -Contain 'labcrashOld' -Because 'older than 30 days, deleted before sending'
            $q.left | Should -Be 0 -Because 'the sent reports are deleted and the old ones too'
        }
    }

    Context 'installer' {
        BeforeAll {
            $m = [ordered]@{}
            Reset-LabVm -Name $VmName
            $current = Get-LabClientMsi
            $version = (Split-Path $current -Leaf) -replace '^RemoteHub-(.+)-x64\.msi$', '$1'
            # a rebuild of the same version: a new ProductCode and PackageCode, everything else the same
            $rebuilt = $null
            try { $rebuilt = New-LabRebuiltMsi -Source $current -Path (Join-Path $EvidenceDir 'RemoteHub-rebuilt.msi') } catch { $m.rebuildError = "$_" }
            function Arp { Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -match '^RemoteHub( |$)' -and $_.DisplayName -notmatch 'Agent' } | ForEach-Object { "$($_.DisplayName) $($_.DisplayVersion)" }) } }
            function Install([string]$Msi) {
                Copy-LabFile -Name $VmName -Path $Msi -Destination 'C:\LabClient'
                Invoke-LabGuest -Name $VmName -ArgumentList (Split-Path $Msi -Leaf) -ScriptBlock {
                    param($leaf)
                    $log = "C:\LabClient\$leaf.log"
                    $p = Start-Process msiexec.exe -ArgumentList "/i `"C:\LabClient\$leaf`" /qn /norestart /l*v `"$log`"" -Wait -PassThru
                    [pscustomobject]@{ Exit = $p.ExitCode; Log = @(Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_ -match 'newer version|Return value 3|Error \d{4}' } | Select-Object -Last 6) }
                }
            }
            if ($rebuilt) {
                $m.first = Install $current
                $m.arpFirst = Arp
                $m.second = Install $rebuilt.FullName
                $m.arpSecond = Arp
                $m.version = Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-Item 'C:\Program Files\RemoteHub\RemoteHub.exe').VersionInfo.ProductVersion }
            }
            $m.expectVersion = $version
            Save 'msi09' $m
        }

        It 'C-MSI-09 a rebuilt MSI with the same version replaces the installed one (AllowSameVersionUpgrades): exit 0, one Apps entry' -Tag 'C-MSI-09' {
            # ($m is an ordered dictionary: its keys are not PSObject properties)
            if ($m.Contains('rebuildError') -or -not $m.Contains('second')) { Set-ItResult -Skipped -Because "no second same-version MSI: New-LabRebuiltMsi failed ($($m.rebuildError))"; return }
            $m.first.Exit | Should -BeIn 0, 3010
            $m.second.Exit | Should -BeIn 0, 3010 -Because 'AllowSameVersionUpgrades lets the rebuild replace the installed product'
            @($m.arpSecond).Count | Should -Be 1 -Because 'one entry in Apps (a major upgrade, not a second product)'
            @($m.arpSecond)[0] | Should -Be "RemoteHub $($m.expectVersion)"
            $m.version | Should -Match "^$([regex]::Escape($m.expectVersion))"
        }
    }
}
