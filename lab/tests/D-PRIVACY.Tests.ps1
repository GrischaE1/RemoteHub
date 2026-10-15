# D-PRIV / D-HIDE - privacy while connected: the admin's privacy screen (the device's own screen dark, the picture
# still coming; optionally the device's own mouse and keyboard blocked), apps hidden by policy, and what the user at the
# device hides (screen from the banner, apps from the question). normaluser is signed in and allows PSAdmin
# (RhPerson.ps1). See docs/test-lab-cases.md section 9.11a.
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "d-priv-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "d-priv-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Mark([string]$o, [string]$n) { @{ do = 'markFile'; path = (Join-Path $o "$n.txt") } }
    function Park([string]$o, [string]$n, [int]$t = 300) { @{ do = 'waitFile'; path = (Join-Path $o "$n.txt"); timeout = $t } }
    function Reached([string]$o, [string]$n, [int]$t = 180) { Wait-LabHostFile (Join-Path $o "$n.txt") -TimeoutSec $t }
    function Release([string]$o, [string]$n) { Set-Content (Join-Path $o "$n.txt") 'go' }
    function Begin([string]$Name, [object[]]$Steps, [string]$Answer = 'Allow') {
        $job = Start-LabDesktop -Name $VmName -Pin $pin -Out (Out $Name) -Steps (@(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 120 }) + $Steps + @(@{ do = 'close' }))
        if ($Answer) { $null = Invoke-LabPerson -Name $VmName -Do click -Text $Answer -TimeoutSec 60 }
        $job
    }
    function Console-Luma([string]$Path) {
        $null = Get-LabScreenshot -Name $VmName -Path $Path
        Add-Type -AssemblyName System.Drawing
        $b = [Drawing.Bitmap]::FromFile($Path); $sum = 0; $n = 0
        try { for ($y = 0; $y -lt $b.Height; $y += 8) { for ($x = 0; $x -lt $b.Width; $x += 8) { $c = $b.GetPixel($x, $y); $sum += 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B; $n++ } } } finally { $b.Dispose() }
        $sum / $n
    }

    Reset-LabVm -Name $VmName
    $install = Install-LabAgent -Name $VmName
    $pin = $install.pin
    Set-LabAutoLogon -Name $VmName -Account User
    $null = Wait-LabAgent -Name $VmName -Pin $pin
    $old = $vm.Version -eq '1809'
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-priv-agent-logs')
}

Describe 'D-PRIV <VmName>' -Tag Desktop, 'D-PRIV', 'D-HIDE' {

    Context 'privacy screen' {
        BeforeAll {
            $p = [ordered]@{}
            $since = Get-LabGuestTime -Name $VmName
            foreach ($level in 0, 1, 2) {
                Set-LabAgentPolicy -Name $VmName -Clear -Values @{ PrivacyScreen = $level }
                $p["hello$level"] = (Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out "hello$level") -Steps @(@{ do = 'connect' }, @{ do = 'close' })).hello
            }
            # level 2: on with the device's input blocked
            $marker = Start-LabMarker -Name $VmName
            $o = Out 'on'
            $job = Begin 'on' @(
                @{ do = 'privacy'; style = 'Black'; blockLocalInput = $true }, @{ do = 'sleep'; seconds = 5 }, @{ do = 'frame'; out = 'picture.png' }
                (Mark $o 'on'), (Park $o 'typed'), @{ do = 'saveToken'; path = (Join-Path $o 'token.txt') }, @{ do = 'drop' }, @{ do = 'reconnect' }
                @{ do = 'start'; session = 'console'; consent = 'auto'; resume = 'last' }, @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser'); timeout = 60 }, @{ do = 'sleep'; seconds = 5 })
            Reached $o 'on'
            $p.consoleLuma = Console-Luma (Join-Path $o 'console.png')
            $count = @(Get-LabMarker -Name $VmName -Marker $marker).Count
            Send-LabText -Name $VmName -Text 'blocked'
            Start-Sleep -Seconds 2
            $p.blockedKeys = @(Get-LabMarker -Name $VmName -Marker $marker | Select-Object -Skip $count | Where-Object e -eq 'char')
            Release $o 'typed'
            $p.on = Receive-LabDesktop $job
            Start-Sleep -Seconds 3
            # level 1: blocking refused, the device's keyboard still works
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ PrivacyScreen = 1 }
            $o = Out 'level1'
            $job = Begin 'level1' @(@{ do = 'privacy'; style = 'Black'; blockLocalInput = $true }, @{ do = 'sleep'; seconds = 3 }, @{ do = 'privacy'; style = 'Black'; blockLocalInput = $false }, @{ do = 'sleep'; seconds = 3 }, (Mark $o 'on'), (Park $o 'typed'))
            Reached $o 'on'
            $count = @(Get-LabMarker -Name $VmName -Marker $marker).Count
            Send-LabText -Name $VmName -Text 'free'
            Start-Sleep -Seconds 2
            $p.freeKeys = @(Get-LabMarker -Name $VmName -Marker $marker | Select-Object -Skip $count | Where-Object e -eq 'char')
            Release $o 'typed'
            $p.level1 = Receive-LabDesktop $job
            # off by policy: refused
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ PrivacyScreen = 0 }
            $p.off = Receive-LabDesktop (Begin 'off' @(@{ do = 'privacy'; style = 'Black' }, @{ do = 'sleep'; seconds = 3 }))
            Set-LabAgentPolicy -Name $VmName -Clear
            Stop-LabInSession -Name $VmName -Task $marker.Task
            Start-Sleep -Seconds 2
            $p.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1104, 2103)
            Save 'screen' $p
        }

        It 'D-PRIV-01 levels 0 / 1 / 2 in Hello; on: the console black, the picture still coming, event 1104; refused: 2103' -Tag 'D-PRIV-01' {
            if ($old) { Set-ItResult -Skipped -Because 'Windows before 2004 has no privacy screen (D-PRIV-03)'; return }
            $p.hello0.rights.privacyScreen | Should -BeFalse
            $p.hello1.rights.privacyScreen | Should -BeTrue
            $p.hello1.rights.privacyInputBlock | Should -BeFalse
            $p.hello2.rights.privacyInputBlock | Should -BeTrue
            @($p.hello2.features) | Should -Contain 'privacyScreen'
            $p.consoleLuma | Should -BeLessThan 20
            @($p.on.images)[0].meanLuma | Should -BeGreaterThan 20 -Because 'RemoteHub still sees the desktop'
            @($p.events | Where-Object Id -eq 1104).Count | Should -BeGreaterThan 0
            @($p.events | Where-Object Id -eq 2103).Count | Should -BeGreaterThan 0
            @($p.off.privacy | Where-Object { $_.privacy.reason -match 'turned off on this device' }).Count | Should -BeGreaterThan 0
        }

        It 'D-PRIV-02 level 2 blocks the device''s own keyboard; level 1 doesn''t' -Tag 'D-PRIV-02' {
            if ($old) { Set-ItResult -Skipped -Because 'Windows before 2004 has no privacy screen'; return }
            @($p.blockedKeys) | Should -BeNullOrEmpty
            (-join @($p.freeKeys | ForEach-Object c)) | Should -Be 'free'
            @($p.level1.privacy | Where-Object { $_.privacy.reason -match "isn't allowed on this device" }).Count | Should -BeGreaterThan 0
        }

        It 'D-PRIV-03 before Windows 2004: no privacyScreen feature, a request refused with 2103' -Tag 'D-PRIV-03' {
            if (-not $old) { Set-ItResult -Skipped -Because 'needs the W10-1809 VM'; return }
            @($p.hello2.features) | Should -Not -Contain 'privacyScreen'
        }

        It 'D-PRIV-04 after a resume the privacy screen is still on' -Tag 'D-PRIV-04' {
            if ($old) { Set-ItResult -Skipped -Because 'Windows before 2004 has no privacy screen'; return }
            $last = @($p.on.privacy | Where-Object { $_.connection -eq 2 })[-1]
            $last.privacy.screen | Should -Not -Be 'Off'
        }
    }

    Context 'hidden apps' {
        BeforeAll {
            $h = [ordered]@{}
            $text = 'RHLABSECRETAPPTEXT'
            $task = Start-LabInSession -Name $VmName -ScriptBlock ([scriptblock]::Create("Set-Content `$env:TEMP\rhlab-secret.txt '$text'; notepad.exe `$env:TEMP\rhlab-secret.txt"))
            Start-Sleep -Seconds 5
            $h.control = Receive-LabDesktop (Begin 'apps-control' @(@{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'visible.png' }))
            $notepad = Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-Process notepad | Select-Object -First 1).Path }
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ HiddenApps = @($notepad) }
            $h.policy = Receive-LabDesktop (Begin 'apps-policy' @(@{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'hidden.png' }))
            Set-LabAgentPolicy -Name $VmName -Clear
            $h.visibleOcr = Get-LabOcr -Path (Join-Path (Out 'apps-control') 'visible.png')
            $h.hiddenOcr = Get-LabOcr -Path (Join-Path (Out 'apps-policy') 'hidden.png')
            # the user hides the screen from the banner; kept after a resume
            $since = Get-LabGuestTime -Name $VmName
            $o = Out 'user-hides'
            $job = Begin 'user-hides' @((Mark $o 'connected'), (Park $o 'hidden'), @{ do = 'sleep'; seconds = 3 }, @{ do = 'drop' }, @{ do = 'reconnect' }
                @{ do = 'start'; session = 'console'; consent = 'auto'; resume = 'last' }, @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser'); timeout = 60 }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'after-resume.png' })
            Reached $o 'connected'
            $null = Invoke-LabPerson -Name $VmName -Do hover -Title 'RemoteHub'
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Hide' -Title 'RemoteHub'
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Black' -TimeoutSec 20
            Start-Sleep -Seconds 3
            Release $o 'hidden'
            $h.user = Receive-LabDesktop $job
            Start-Sleep -Seconds 2
            $h.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1105)
            Stop-LabInSession -Name $VmName -Task $task
            Save 'apps' $h
        }

        It 'D-HIDE-01 HiddenApps (full path): the app''s window is never in the picture' -Tag 'D-HIDE-01' {
            $h.visibleOcr.text | Should -Match $text -Because 'without the policy the app is visible (control)'
            $h.hiddenOcr.text | Should -Not -Match $text
        }

        It 'D-HIDE-02 the user hides the screen: event 1105, a Privacy message; still hidden after a resume without a new question' -Tag 'D-HIDE-02' {
            $h.events.Count | Should -BeGreaterThan 0
            @($h.user.privacy | Where-Object { $_.privacy.user -ne 'None' }).Count | Should -BeGreaterThan 0
            @($h.user.statuses | Where-Object { $_.connection -eq 2 -and $_.state -eq 'WaitingForUser' }) | Should -BeNullOrEmpty
            @($h.user.privacy | Where-Object { $_.connection -eq 2 -and $_.privacy.user -ne 'None' }).Count | Should -BeGreaterThan 0
        }
    }
}
