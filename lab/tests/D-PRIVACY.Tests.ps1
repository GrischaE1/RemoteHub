# D-PRIV / D-HIDE - privacy while connected: the admin's privacy screen (the device's own screen dark, the picture
# still coming; optionally the device's own mouse and keyboard blocked), apps hidden by policy, and what the user at the
# device hides (screen from the banner, apps from the question). normaluser is signed in and allows PSAdmin
# (RhPerson.ps1). See docs/test-lab-cases.md section 9.11a.
# LabRole: device
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
    function Release([string]$o, [string]$n) { Set-Content -Encoding ASCII (Join-Path $o "$n.txt") 'go' }
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

    $init = Initialize-LabVm -Name $VmName -Agent -AutoLogon User
    $install = $init.Install
    $pin = $init.Pin
    $old = $vm.Version -eq '1809'
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-priv-agent-logs')
    # left reusable: the policies cleared, the notepad closed
    Set-LabAgentPolicy -Name $VmName -Clear
    $null = Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force }
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
            # The secret as OCR reads it: an exact match or a word at most 3 characters off - Server 2025's frame read
            # "BHLABSECRETAPPTEXT" (run 20261016-212514) though the window was plainly in the picture.
            function Find-Secret($ocr) {
                if ("$($ocr.text)" -match $text) { return $text }
                @($ocr.words | ForEach-Object { "$($_.text)" } | Where-Object {
                        $t = ($_ -replace '[^A-Za-z0-9]', '').ToUpperInvariant()
                        $t.Length -ge $text.Length - 3 -and (Get-LabTextDistance $t $text) -le 3 }) | Select-Object -First 1
            }
            # The app's window in the session: waited for (60 s), restored when minimised, brought to the front (an Alt tap
            # lets the task's process set the foreground window); answers its title, program, rectangle (physical pixels)
            # and whether it has the foreground.
            $frontCode = @'
Add-Type -Namespace RhLab -Name Win -MemberDefinition '
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, IntPtr e);
public struct RECT { public int Left, Top, Right, Bottom; }'
$null = [RhLab.Win]::SetProcessDPIAware()
$p = $null
$until = (Get-Date).AddSeconds(60)
while ($true) {
    $p = @(Get-Process | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero -and $_.MainWindowTitle -like 'rhlab-secret*' }) | Select-Object -First 1
    if ($p -or (Get-Date) -gt $until) { break }
    Start-Sleep -Milliseconds 500
}
if (-not $p) { 'WIN=' + (@{ found = $false } | ConvertTo-Json -Compress); return }
$hw = $p.MainWindowHandle
$iconic = [RhLab.Win]::IsIconic($hw)
if ($iconic) { $null = [RhLab.Win]::ShowWindow($hw, 9) }
for ($i = 0; $i -lt 5 -and [RhLab.Win]::GetForegroundWindow() -ne $hw; $i++) {
    [RhLab.Win]::keybd_event(0x12, 0, 0, [IntPtr]::Zero); [RhLab.Win]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)
    $null = [RhLab.Win]::SetForegroundWindow($hw)
    Start-Sleep -Milliseconds 400
}
$r = New-Object RhLab.Win+RECT
$null = [RhLab.Win]::GetWindowRect($hw, [ref]$r)
'WIN=' + ([ordered]@{ found = $true; title = $p.MainWindowTitle; path = $p.Path; wasMinimised = $iconic; foreground = ([RhLab.Win]::GetForegroundWindow() -eq $hw)
    x = $r.Left; y = $r.Top; width = $r.Right - $r.Left; height = $r.Bottom - $r.Top } | ConvertTo-Json -Compress)
'@
            function Front-App {
                $raw = Invoke-LabInSession -Name $VmName -TimeoutSec 120 -ScriptBlock ([scriptblock]::Create($frontCode))
                if ("$raw" -match 'WIN=(\{.*\})') { $Matches[1] | ConvertFrom-Json } else { [pscustomobject]@{ found = $false; error = "$raw".Trim() } }
            }
            $task = Start-LabInSession -Name $VmName -ScriptBlock ([scriptblock]::Create("Set-Content -Encoding ASCII `$env:TEMP\rhlab-secret.txt '$text'; notepad.exe `$env:TEMP\rhlab-secret.txt"))
            # Precondition of D-HIDE-01: the app's window on screen and in front before the visible baseline. Still not in
            # front after the session's attempt: the person clicks into its text (console mouse), then it is asked again.
            $h.window = Front-App
            if ($h.window.found -and -not $h.window.foreground) {
                Send-LabMouse -Name $VmName -X ($h.window.x + [int]($h.window.width / 2)) -Y ($h.window.y + [int]($h.window.height / 2))
                Start-Sleep -Seconds 1
                $h.window = Front-App
                $h.window | Add-Member -NotePropertyName clicked -NotePropertyValue $true -Force
            }
            $console = Join-Path $EvidenceDir 'd-priv-apps-console.png'
            $null = Get-LabScreenshot -Name $VmName -Path $console
            $h.consoleSeen = Find-Secret (Get-LabOcr -Path $console)
            $h.control = Receive-LabDesktop (Begin 'apps-control' @(@{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'visible.png' }))
            # the program of the process that owns the window - on Windows 11 the Store Notepad under WindowsApps (a
            # packaged app; System32\notepad.exe only starts it), kept in the evidence with its package
            $notepad = if ($h.window.found -and $h.window.path) { $h.window.path } else { Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-Process notepad | Select-Object -First 1).Path } }
            $h.hiddenPath = $notepad
            $h.package = Invoke-LabGuest -Name $VmName -ScriptBlock { try { $a = Get-AppxPackage -AllUsers Microsoft.WindowsNotepad -ErrorAction Stop | Select-Object -First 1; if ($a) { $a.PackageFamilyName } } catch { '' } }
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ HiddenApps = @($notepad) }
            $h.policy = Receive-LabDesktop (Begin 'apps-policy' @(@{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'hidden.png' }))
            Set-LabAgentPolicy -Name $VmName -Clear
            $h.visibleOcr = Get-LabOcr -Path (Join-Path (Out 'apps-control') 'visible.png')
            $h.hiddenOcr = Get-LabOcr -Path (Join-Path (Out 'apps-policy') 'hidden.png')
            $h.visibleSeen = Find-Secret $h.visibleOcr
            $h.hiddenSeen = Find-Secret $h.hiddenOcr
            # the user hides the screen from the banner; kept after a resume
            $since = Get-LabGuestTime -Name $VmName
            $o = Out 'user-hides'
            $job = Begin 'user-hides' @((Mark $o 'connected'), (Park $o 'hidden'), @{ do = 'sleep'; seconds = 3 }, @{ do = 'drop' }, @{ do = 'reconnect' }
                @{ do = 'start'; session = 'console'; consent = 'auto'; resume = 'last' }, @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser'); timeout = 60 }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'frame'; out = 'after-resume.png' })
            # (finally: the harness released and the notepad's session task stopped whatever happens here - a session left
            # parked kept its banner up for the next context)
            try {
                Reached $o 'connected'
                # the whole label: "Hide" alone is also on "Hide apps..." (see Invoke-LabBannerClick)
                $h.hideClick = Invoke-LabBannerClick -Name $VmName -Label 'Hide screen' -EvidencePath (Join-Path $EvidenceDir 'd-priv-user-hides-banner.png')
                $h.blackClick = if ($h.hideClick.clicked) { Invoke-LabPerson -Name $VmName -Do click -Text 'Black' -TimeoutSec 20 } else { $null }
                # hidden when event 1105 is there: waited for (up to 20 s) before the session drops and resumes
                $until = (Get-Date).AddSeconds(20)
                do {
                    Start-Sleep -Seconds 1
                    $h.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1105)
                } while ($h.events.Count -eq 0 -and (Get-Date) -lt $until)
            }
            finally {
                Release $o 'hidden'
                $h.user = Receive-LabDesktop $job
                Start-Sleep -Seconds 2
                $h.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1105)
                Stop-LabInSession -Name $VmName -Task $task
                Save 'apps' $h
            }
        }

        It 'D-HIDE-01 HiddenApps (full path): the app''s window is never in the picture' -Tag 'D-HIDE-01' {
            # precondition: without the policy the app's window is in the picture - otherwise the hiding isn't tested at all
            $w = $h.window
            $where = if ($w.found) { "window '$($w.title)' at $($w.x),$($w.y) $($w.width)x$($w.height), foreground $($w.foreground), was minimised $($w.wasMinimised)" } else { "no window titled rhlab-secret* in the session ($($w.error))" }
            [bool]$h.visibleSeen | Should -BeTrue -Because "precondition: the app's window wasn't in the visible picture ($where; console screenshot showed the text: $([bool]$h.consoleSeen); frame OCR: $($h.visibleOcr.text))"
            $h.hiddenSeen | Should -BeNullOrEmpty -Because "HiddenApps = $($h.hiddenPath) (package: $($h.package)) covers the app's window (hidden frame OCR: $($h.hiddenOcr.text))"
        }

        It 'D-HIDE-02 the user hides the screen: event 1105, a Privacy message; still hidden after a resume without a new question' -Tag 'D-HIDE-02' {
            $h.hideClick.clicked | Should -BeTrue -Because "the person clicked 'Hide screen' on the banner ($($h.hideClick.why); OCR: $(@($h.hideClick.lines) -join ' | '))"
            $h.blackClick.clicked | Should -BeTrue -Because 'the person chose "Black out" in "Hide your screen"'
            $h.events.Count | Should -BeGreaterThan 0 -Because 'the screen was hidden (event 1105)'
            @($h.user.privacy | Where-Object { $_.privacy.user -ne 'None' }).Count | Should -BeGreaterThan 0
            @($h.user.statuses | Where-Object { $_.connection -eq 2 -and $_.state -eq 'WaitingForUser' }) | Should -BeNullOrEmpty
            @($h.user.privacy | Where-Object { $_.connection -eq 2 -and $_.privacy.user -ne 'None' }).Count | Should -BeGreaterThan 0
        }
    }
}
