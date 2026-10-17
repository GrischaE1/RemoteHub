# D-MORE - more cases of the agent's remote desktop: connect / consent / banner / sign-in / Ctrl+Alt+Del / input.
# Covers: D-CON-11, D-ASK-08, D-ASK-09, D-PIN-06, D-NONE-05, D-MODES-03, D-MODES-04, D-BAN-08, D-BAN-09, D-BAN-10,
#         D-SIGNIN-05, D-SAS-12, D-IN-08, D-IN-09 (docs/test-lab-cases.md section 9).
# Needs: one VM as the device (any OS, like the other D-* files); the desktop harness (DesktopHarness.cs) on the host;
#        the person at the device (RhPerson.ps1: OCR + console mouse) and the marker app. D-BAN-10 needs the agent MSI
#        built with the credential provider and signed (build-installer.ps1 -LabSigning -CredentialProvider), as
#        C-AUTO2 / C-END do. normaluser is signed in on the console and allows PSAdmin unless a context says otherwise.
# Known product bugs: none of these rows is one.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $password = (Get-LabConfig).Password
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "d-more-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "d-more-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Shot([string]$name) { $p = Join-Path $EvidenceDir "d-more-$name.png"; $null = Get-LabScreenshot -Name $VmName -Path $p; $p }
    function Of($r, [string]$state) { , @($r.statuses | Where-Object state -eq $state) }
    function Mark([string]$o, [string]$n) { @{ do = 'markFile'; path = (Join-Path $o "$n.txt") } }
    function Park([string]$o, [string]$n, [int]$t = 300) { @{ do = 'waitFile'; path = (Join-Path $o "$n.txt"); timeout = $t } }
    function Reached([string]$o, [string]$n, [int]$t = 180) { Wait-LabHostFile (Join-Path $o "$n.txt") -TimeoutSec $t }
    function Release([string]$o, [string]$n) { Set-Content (Join-Path $o "$n.txt") 'go' }
    # a session normaluser allows (the person clicks Allow); $Steps run once connected
    function Begin([string]$Name, [object[]]$Steps, [string]$Account = 'Admin', [bool]$ViewOnly = $false) {
        $first = @(@{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; viewOnly = $ViewOnly }, @{ do = 'wait'; anyOf = @('Connected', 'Denied', 'Failed'); timeout = 90 })
        $job = Start-LabDesktop -Name $VmName -Pin $pin -Account $Account -Out (Out $Name) -Steps ($first + $Steps)
        $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
        $job
    }
    # the agent's banner as the person at the device sees it (window bounds; Title "RemoteHub")
    # (asked again for up to 10 s: right after a change the banner may be missing from the list for a moment)
    function Banner([string]$Account = 'User') {
        foreach ($try in 1..5) {
            $w = @(Invoke-LabPerson -Name $VmName -Account $Account -Do windows | Where-Object Title -eq 'RemoteHub')[0]
            if ($w) { return $w }
            Start-Sleep -Seconds 2
        }
        $null
    }
    # a context starts on normaluser's desktop: a security screen left by an earlier context (Ctrl+Alt+Del that got
    # through) is closed with Esc - the person's steps capture the screen, which fails on the secure desktop
    function Ready([string]$name) {
        if (Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-Process LogonUI -ErrorAction SilentlyContinue).Count -gt 0 }) {
            $null = Invoke-LabPerson -Name $VmName -Do key -Value '1B'
            Start-Sleep -Seconds 3
        }
        $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "d-more-start-$name.png")
        Save "start-state-$name" (Invoke-LabGuest -Name $VmName -ScriptBlock { quser 2>&1 | Out-String; @(Get-Process LogonUI -ErrorAction SilentlyContinue).Count })
    }
    # "Denied automatically in N s" read from the OCR lines of an agent dialog: N, or $null
    function Countdown($lines) {
        foreach ($l in @($lines)) { if ("$l" -match 'automatically in (\d+)') { return [int]$Matches[1] } }
        $null
    }
    # whether a key is held down in normaluser's session (GetAsyncKeyState)
    function KeyDown([int]$Vk) {
        $r = Invoke-LabInSession -Name $VmName -ScriptBlock ([scriptblock]::Create(@"
Add-Type -Namespace RhLab -Name Keys -MemberDefinition '[DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);'
[bool]([RhLab.Keys]::GetAsyncKeyState($Vk) -band 0x8000)
"@))
        "$r".Trim() -eq 'True'
    }

    $init = Initialize-LabVm -Name $VmName -Agent -AutoLogon User
    $install = $init.Install
    $pin = $init.Pin
    # the person's first call compiles its helper and starts the OCR (slow): done here, not against a 30 s consent countdown
    $null = Invoke-LabPerson -Name $VmName -Do windows
    $null = Invoke-LabPerson -Name $VmName -Do screen-read
}

AfterAll {
    Get-Job | Where-Object State -eq 'Running' | Stop-Job
    Get-Job | Remove-Job -Force
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-more-agent-logs')
    # the last contexts revert the VM and leave an account 'rhsid2', DontDisplayLastUserName and automatic sign-in: not for the next file
    Set-LabVmDirty -Name $VmName
}

Describe 'D-MORE <VmName>' -Tag Desktop, 'D-CON', 'D-ASK', 'D-PIN', 'D-NONE', 'D-MODES', 'D-BAN', 'D-SIGNIN', 'D-SAS', 'D-IN' {

    Context 'a sign-in request RemoteHub was not asked for' {
        BeforeAll {
            $c = [ordered]@{}
            $since = Get-LabGuestTime -Name $VmName
            # before Start: the first message is a SignInRequest (type 31) - the agent closes the connection
            $c.early = Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out 'signin-early') -Steps @(
                @{ do = 'connect' }, @{ do = 'signIn' }, @{ do = 'sleep'; seconds = 5 })
            # during a session: a protocol error - the session ends, its resume token is void (asked again)
            $o = Out 'signin-session'
            $job = Begin 'signin-session' @(
                @{ do = 'sleep'; seconds = 2 }, @{ do = 'signIn' }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'reconnect' }
                @{ do = 'start'; session = 'console'; consent = 'auto'; resume = 'last' }, @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'Failed'); timeout = 60 }
                (Mark $o 'asked'), @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 60 }, @{ do = 'close' })
            Reached $o 'asked'
            try { $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Deny' -TimeoutSec 30 } catch { }
            $c.session = Receive-LabDesktop $job
            Start-Sleep -Seconds 2
            $c.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1101)
            Save 'signin-unasked' $c
        }

        It 'D-CON-11 an unrequested SignInRequest closes the connection; during a session it is a protocol error and the resume token is void' -Tag 'D-CON-11' {
            # before Start: closed by the agent, nothing started
            @($c.early.closes | Where-Object by -eq 'agent').Count | Should -BeGreaterThan 0
            @($c.early.statuses) | Should -BeNullOrEmpty
            # during the session: the first connection closed by the agent ...
            @($c.session.closes | Where-Object { $_.connection -eq 1 -and $_.by -eq 'agent' }).Count | Should -Be 1
            (@($c.session.statuses | Where-Object { $_.connection -eq 1 -and $_.state -eq 'Connected' })).Count | Should -BeGreaterThan 0
            # ... as an invalid message, not a lost connection
            @($c.events | Where-Object { $_.Message -match "invalid message from RemoteHub: A sign-in request the agent didn't ask for" }).Count | Should -BeGreaterThan 0
            # the token is void: resumed, the user is asked again
            @($c.session.statuses | Where-Object { $_.connection -eq 2 -and $_.state -eq 'WaitingForUser' }).Count | Should -BeGreaterThan 0
        }
    }

    Context 'consent' {
        BeforeAll {
            Ready 'consent'
            $a = [ordered]@{}
            # RemoteHub closes while the user is asked
            $since = Get-LabGuestTime -Name $VmName
            $o = Out 'ask-closed'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'Ask' }, @{ do = 'wait'; state = 'WaitingForUser'; timeout = 60 }
                @{ do = 'sleep'; seconds = 3 }, @{ do = 'close' })
            $a.closed = Receive-LabDesktop $job
            Start-Sleep -Seconds 3
            $a.closedEvents = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2100)
            $a.dialogLeft = @(Invoke-LabPerson -Name $VmName -Do windows | Where-Object { $_.Title -ne 'RemoteHub' })

            # "Allow and hide apps...": a notepad with a secret text, 60 s to answer (time for the person's steps)
            $secret = 'RHLABHIDEFROMSTART'
            $task = Start-LabInSession -Name $VmName -ScriptBlock ([scriptblock]::Create("Set-Content `$env:TEMP\rhlab-hide.txt '$secret'; notepad.exe `$env:TEMP\rhlab-hide.txt"))
            Start-Sleep -Seconds 5
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ ConsentTimeoutSeconds = 60 }
            $o = Out 'ask-hide'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'Ask' }, @{ do = 'wait'; state = 'WaitingForUser'; timeout = 60 }
                @{ do = 'wait'; anyOf = @('Connected', 'Denied', 'Failed'); timeout = 180 }, @{ do = 'sleep'; seconds = 3 }, @{ do = 'frame'; out = 'first.png' }
                @{ do = 'sleep'; seconds = 2 }, @{ do = 'close' })
            $a.question = Invoke-LabPerson -Name $VmName -Do read -TimeoutSec 60
            Start-Sleep -Seconds 10
            # 'hide' is only in the button "Allow and hide apps..." (the click answers the OCR lines read just before it)
            $a.hideClick = Invoke-LabPerson -Name $VmName -Do click -Text 'hide' -TimeoutSec 30
            Start-Sleep -Seconds 2
            $a.picker = Invoke-LabPerson -Name $VmName -Do read -TimeoutSec 30
            $a.tick = Invoke-LabPerson -Name $VmName -Do click -Text 'Notepad' -TimeoutSec 30
            $a.allow = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 30
            $a.hide = Receive-LabDesktop $job
            Set-LabAgentPolicy -Name $VmName -Clear
            $first = Join-Path $o 'first.png'
            $a.firstOcr = if (Test-Path $first) { [string](Get-LabOcr -Path $first).text } else { $null }
            Stop-LabInSession -Name $VmName -Task $task
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force }
            $a.secret = $secret

            # the PIN dialog: Hide apps... and Deny; Deny refuses
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Out (Out 'pin-choices') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'Pin' }, @{ do = 'wait'; state = 'EnterPin'; timeout = 60 }
                @{ do = 'wait'; anyOf = @('Connected', 'Denied', 'Failed'); timeout = 120 }, @{ do = 'close' })
            $a.pinRead = Invoke-LabPerson -Name $VmName -Do read -TimeoutSec 60
            $a.pinDeny = Invoke-LabPerson -Name $VmName -Do click -Text 'Deny' -TimeoutSec 30
            $a.pin = Receive-LabDesktop $job

            # no mode left: only None allowed, but no ConsentBypass right - nothing usable
            $since = Get-LabGuestTime -Name $VmName
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ AllowedConsentModes = @('None') }
            $a.none = Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out 'no-mode') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'Ask' }
                @{ do = 'wait'; anyOf = @('Failed', 'WaitingForUser', 'Connected'); timeout = 60 }, @{ do = 'close' })
            Start-Sleep -Seconds 2
            $a.noneEvents = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2100)

            # a mode the policy doesn't allow, on a locked session: runs as None; the follow-up mode (Pin) at the unlock
            $since = Get-LabGuestTime -Name $VmName
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ AllowedConsentModes = @('Pin') }
            Lock-LabSession -Name $VmName
            Start-Sleep -Seconds 5
            $o = Out 'locked-mode'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'Ask' }, @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'EnterPin', 'Failed'); timeout = 60 }
                (Mark $o 'connected'), @{ do = 'wait'; anyOf = @('EnterPin', 'WaitingForUser', 'Denied', 'Ended', 'Failed'); timeout = 240 }, (Mark $o 'asked')
                @{ do = 'wait'; anyOf = @('Connected', 'Denied', 'Ended'); timeout = 120 }, @{ do = 'close' })
            Reached $o 'connected'
            Start-Sleep -Seconds 3
            Unlock-LabSession -Name $VmName -TimeoutSec 120
            Reached $o 'asked' 240
            $a.lockedRead = Invoke-LabPerson -Name $VmName -Do read -TimeoutSec 30
            try { $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Deny' -TimeoutSec 30 } catch { }
            $a.locked = Receive-LabDesktop $job
            Set-LabAgentPolicy -Name $VmName -Clear
            Start-Sleep -Seconds 2
            $a.lockedEvents = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1100)
            Save 'consent' $a
        }

        It 'D-ASK-08 RemoteHub closes while the user is asked: event 2100 "...: The connection ended before access was granted."' -Tag 'D-ASK-08' {
            (Of $a.closed 'WaitingForUser').Count | Should -BeGreaterThan 0
            (Of $a.closed 'Connected').Count | Should -Be 0
            @($a.closedEvents | Where-Object { $_.Message -match 'The connection ended before access was granted\.' }).Count | Should -BeGreaterThan 0
        }

        It 'D-ASK-09 "Allow and hide apps..." opens the app picker, its countdown starts again, the chosen app is hidden from the first picture' -Tag 'D-ASK-09' {
            $a.hideClick.clicked | Should -BeTrue
            ($a.picker.text) | Should -Match 'Hide apps from'
            # the countdown starts again in the picker (60 s policy): more seconds left there than in the question
            $inQuestion = Countdown $a.hideClick.lines
            $inPicker = Countdown $a.picker.lines
            $inQuestion | Should -Not -BeNullOrEmpty
            $inPicker | Should -Not -BeNullOrEmpty
            $inPicker | Should -BeGreaterThan $inQuestion
            $a.tick.clicked | Should -BeTrue
            $a.allow.clicked | Should -BeTrue
            (Of $a.hide 'Connected').Count | Should -BeGreaterThan 0
            # hidden from the start: the first Privacy message already counts it, the first picture doesn't show it
            @($a.hide.privacy)[0].privacy.hiddenApps | Should -BeGreaterOrEqual 1
            $a.firstOcr | Should -Not -BeNullOrEmpty
            $a.firstOcr | Should -Not -Match $a.secret
        }

        It 'D-PIN-06 the code window offers "Hide apps..." and "Deny"; Deny -> Denied' -Tag 'D-PIN-06' {
            $a.pinRead.found | Should -BeTrue
            ($a.pinRead.lines -join ' ') | Should -Match 'Hide apps'
            ($a.pinRead.lines -join ' ') | Should -Match 'Deny'
            $a.pinDeny.clicked | Should -BeTrue
            (Of $a.pin 'Denied').Count | Should -Be 1
            (Of $a.pin 'Denied')[0].message | Should -Match 'refused'
        }

        It 'D-MODES-03 AllowedConsentModes leaves nothing usable: Hello consentModes [], Failed "No way to connect is allowed on this device (policy).", event 2100' -Tag 'D-MODES-03' {
            @($a.none.hello.consentModes) | Should -BeNullOrEmpty
            (Of $a.none 'Failed').Count | Should -Be 1
            (Of $a.none 'Failed')[0].message | Should -Be 'No way to connect is allowed on this device (policy).'
            @($a.noneEvents | Where-Object { $_.Message -match 'No way to connect is allowed on this device' }).Count | Should -BeGreaterThan 0
        }

        It 'D-MODES-04 a mode the policy refuses, on a locked session: runs as None; at the unlock the follow-up mode (Pin) asks' -Tag 'D-MODES-04' {
            (Of $a.locked 'Failed').Count | Should -Be 0
            $firstState = @($a.locked.statuses | Where-Object state -in 'Connected', 'WaitingForUser', 'EnterPin')[0].state
            $firstState | Should -Be 'Connected'
            @($a.lockedEvents | Where-Object { $_.Message -match 'consent None \(on unlock or sign-in: Pin\)' }).Count | Should -BeGreaterThan 0
            (Of $a.locked 'EnterPin').Count | Should -BeGreaterThan 0
            (Of $a.locked 'WaitingForUser').Count | Should -Be 0
        }
    }

    Context 'the banner: clicks pass through, kept open while something is hidden' {
        BeforeAll {
            Ready 'the-banner-clicks-pa'
            $b = [ordered]@{}
            # the admin clicks where the banner is: the click reaches the marker window below it
            $marker = Start-LabMarker -Name $VmName
            $o = Out 'banner-click'
            $job = Begin 'banner-click' @(
                @{ do = 'sleep'; seconds = 12 }, (Mark $o 'pill'), @{ do = 'click'; fromFile = (Join-Path $o 'xy.txt'); timeout = 120 }
                @{ do = 'sleep'; seconds = 3 }, (Mark $o 'clicked'), (Park $o 'end'), @{ do = 'close' })
            Reached $o 'pill'
            $b.banner = Banner
            $count = @(Get-LabMarker -Name $VmName -Marker $marker).Count
            $b.x = $b.banner.X + [int]($b.banner.Width / 2); $b.y = $b.banner.Y + [int]($b.banner.Height / 2)
            Set-Content (Join-Path $o 'xy.txt') "$($b.x),$($b.y)"
            Reached $o 'clicked'
            Start-Sleep -Seconds 1
            $b.seen = @(Get-LabMarker -Name $VmName -Marker $marker | Select-Object -Skip $count)
            $b.after = Banner
            Release $o 'end'
            $b.click = Receive-LabDesktop $job
            Stop-LabInSession -Name $VmName -Task $marker.Task

            # the admin's privacy screen with the device's input blocked: the banner stays open and says so
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ PrivacyScreen = 2 }
            $o = Out 'banner-blocked'
            $job = Begin 'banner-blocked' @(
                @{ do = 'sleep'; seconds = 12 }, (Mark $o 'pill'), (Park $o 'measured'), @{ do = 'privacy'; style = 'Black'; blockLocalInput = $true }
                @{ do = 'sleep'; seconds = 3 }, (Mark $o 'on'), (Park $o 'end'), @{ do = 'privacy'; style = 'Off' }, @{ do = 'sleep'; seconds = 2 }, @{ do = 'close' })
            Reached $o 'pill'
            $b.blockedPill = Banner
            Release $o 'measured'
            Reached $o 'on'
            $b.blockedOpen = Banner
            Start-Sleep -Seconds 12
            $b.blockedLater = Banner
            $b.blockedRead = Invoke-LabPerson -Name $VmName -Do read -Title 'RemoteHub' -TimeoutSec 30
            Release $o 'end'
            $b.blocked = Receive-LabDesktop $job
            Set-LabAgentPolicy -Name $VmName -Clear
            Save 'banner' $b
        }

        It 'D-BAN-09 the admin''s click on the banner''s area reaches the window below it (WS_EX_TRANSPARENT)' -Tag 'D-BAN-09' {
            $b.banner | Should -Not -BeNullOrEmpty
            $down = @($b.seen | Where-Object { $_.e -eq 'mdown' -and [math]::Abs($_.x - $b.x) -le 4 -and [math]::Abs($_.y - $b.y) -le 4 })
            $down.Count | Should -BeGreaterThan 0 -Because 'the marker below the banner got the click'
            $b.after | Should -Not -BeNullOrEmpty -Because 'the banner is still there'
            (Of $b.click 'Ended').Count | Should -Be 0
        }

        It 'D-BAN-08 something hidden: the banner doesn''t collapse, with " . mouse and keyboard blocked" and ''Click "Disconnect" to end''' -Tag 'D-BAN-08' {
            # the admin's privacy screen with the input block (the user hiding their screen: see the next context)
            $b.blockedOpen.Width | Should -BeGreaterThan $b.blockedPill.Width -Because 'opened again when the privacy screen came up'
            $b.blockedLater.Width | Should -BeGreaterOrEqual $b.blockedOpen.Width -Because 'not collapsed to the pill after 8 s'
            $b.blockedRead.text | Should -Match 'mouse and keyboard blocked'
            $b.blockedRead.text | Should -Match 'Click .?Disconnect.? to end'
        }
    }

    Context 'the user hides their screen' {
        BeforeAll {
            Ready 'the-user-hides-their'
            $h = [ordered]@{}
            $marker = Start-LabMarker -Name $VmName
            $o = Out 'user-hides'
            # A held down by the admin; after the user hid the screen: keys, a click, a wheel and Ctrl+Alt+Del
            $job = Begin 'user-hides' @(
                @{ do = 'sleep'; seconds = 2 }, @{ do = 'key'; scan = '0x1E' }, @{ do = 'sleep'; seconds = 2 }, (Mark $o 'held'), (Park $o 'hidden')
                @{ do = 'keys'; text = 'zz' }, @{ do = 'mouse'; action = 'Move'; x = 300; y = 300 }, @{ do = 'mouse'; action = 'Down'; button = 'Left'; x = 300; y = 300 }
                @{ do = 'mouse'; action = 'Up'; button = 'Left'; x = 300; y = 300 }, @{ do = 'mouse'; action = 'Wheel'; x = 300; y = 300; delta = 120 }
                @{ do = 'cad' }, @{ do = 'sleep'; seconds = 4 }, (Mark $o 'sent'), (Park $o 'end'), @{ do = 'close' })
            Reached $o 'held'
            $h.heldBefore = KeyDown 0x41
            Start-Sleep -Seconds 10
            $h.pill = Banner
            $since = Get-LabGuestTime -Name $VmName
            $count = @(Get-LabMarker -Name $VmName -Marker $marker).Count
            $null = Invoke-LabPerson -Name $VmName -Do hover -Title 'RemoteHub'
            $h.hideClick = Invoke-LabPerson -Name $VmName -Do click -Text 'Hide' -Title 'RemoteHub'
            $h.blackClick = Invoke-LabPerson -Name $VmName -Do click -Text 'Black' -TimeoutSec 20
            Start-Sleep -Seconds 3
            $h.heldAfter = KeyDown 0x41
            $h.atHide = @(Get-LabMarker -Name $VmName -Marker $marker | Select-Object -Skip $count)
            $h.open = Banner
            $count = @(Get-LabMarker -Name $VmName -Marker $marker).Count
            Release $o 'hidden'
            Reached $o 'sent'
            Start-Sleep -Seconds 2
            $h.seen = @(Get-LabMarker -Name $VmName -Marker $marker | Select-Object -Skip $count)
            $h.logonUi = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-Process LogonUI -ErrorAction SilentlyContinue).Count }
            Start-Sleep -Seconds 8
            $h.later = Banner
            $h.read = Invoke-LabPerson -Name $VmName -Do read -Title 'RemoteHub' -TimeoutSec 30
            Release $o 'end'
            $h.r = Receive-LabDesktop $job
            Stop-LabInSession -Name $VmName -Task $marker.Task
            $h.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1105)
            Save 'user-hides' $h
        }

        It 'D-IN-09 the user hides the screen: the key the admin held is released, no input passes afterwards' -Tag 'D-IN-09' {
            $h.heldBefore | Should -BeTrue -Because 'the admin''s A is down before the user hides the screen'
            @($h.events).Count | Should -BeGreaterThan 0 -Because 'the screen was hidden (event 1105)'
            $h.heldAfter | Should -BeFalse -Because 'released when the user hid the screen'
            @($h.seen | Where-Object e -in 'down', 'char', 'mdown', 'wheel') | Should -BeNullOrEmpty
        }

        It 'D-SAS-12 Ctrl+Alt+Del while the user hides the screen: "The user at the device has hidden their screen - Ctrl+Alt+Del isn''t sent until they show it again."' -Tag 'D-SAS-12' {
            @($h.r.statuses | Where-Object { $_.message -match "The user at the device has hidden their screen . Ctrl\+Alt\+Del isn't sent until they show it again\." }).Count | Should -BeGreaterThan 0
            $h.logonUi | Should -Be 0 -Because 'no security screen came up'
        }

        It 'D-BAN-08 the user hid the screen: the banner opens and stays open (no pill)' -Tag 'D-BAN-08' {
            $h.open.Width | Should -BeGreaterThan $h.pill.Width
            $h.later.Width | Should -BeGreaterOrEqual $h.open.Width
            $h.read.text | Should -Match 'hidden for'
        }
    }

    Context 'no input into hidden apps' {
        BeforeAll {
            Ready 'no-input-into-hidden'
            $i = [ordered]@{}
            # the same input with and without the marker's program (powershell.exe) hidden by the policy HiddenApps
            $inputSteps = @(
                @{ do = 'sleep'; seconds = 3 }, @{ do = 'mouse'; action = 'Move'; x = 300; y = 300 }, @{ do = 'mouse'; action = 'Move'; x = 310; y = 305 }
                @{ do = 'mouse'; action = 'Down'; button = 'Left'; x = 310; y = 305 }, @{ do = 'mouse'; action = 'Up'; button = 'Left'; x = 310; y = 305 }
                @{ do = 'mouse'; action = 'Wheel'; x = 310; y = 305; delta = 120 }, @{ do = 'keys'; text = 'hidden'; delayMs = 30 }, @{ do = 'sleep'; seconds = 3 })
            foreach ($run in 'visible', 'hidden') {
                if ($run -eq 'hidden') {
                    $ps = Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-Command powershell.exe).Source }
                    Set-LabAgentPolicy -Name $VmName -Clear -Values @{ HiddenApps = @($ps) }
                }
                $marker = Start-LabMarker -Name $VmName
                $o = Out "hidden-input-$run"
                $job = Begin "hidden-input-$run" (@((Mark $o 'connected'), (Park $o 'go')) + $inputSteps + @(@{ do = 'close' }))
                Reached $o 'connected'
                Start-Sleep -Seconds 2
                $count = @(Get-LabMarker -Name $VmName -Marker $marker).Count
                Release $o 'go'
                $i[$run] = Receive-LabDesktop $job
                Start-Sleep -Seconds 2
                $i["$run-seen"] = @(Get-LabMarker -Name $VmName -Marker $marker | Select-Object -Skip $count)
                Stop-LabInSession -Name $VmName -Task $marker.Task
            }
            Set-LabAgentPolicy -Name $VmName -Clear
            Save 'hidden-input' $i
        }

        It 'D-IN-08 over a hidden app: mouse down and wheel dropped, keys dropped while it has the focus; moves still pass' -Tag 'D-IN-08' {
            # control: without the policy the same input reaches the marker
            @($i['visible-seen'] | Where-Object e -eq 'mdown').Count | Should -BeGreaterThan 0
            @($i['visible-seen'] | Where-Object e -eq 'char').Count | Should -BeGreaterThan 0
            # hidden: only the moves (and the release of the button) arrive
            @($i['hidden-seen'] | Where-Object e -eq 'move').Count | Should -BeGreaterThan 0
            @($i['hidden-seen'] | Where-Object e -in 'mdown', 'wheel', 'down', 'char') | Should -BeNullOrEmpty
            @($i.hidden.privacy | Where-Object { $_.privacy.hiddenApps -ge 1 }).Count | Should -BeGreaterThan 0
        }
    }

    Context 'the own session with Ask and Pin' {
        BeforeAll {
            # the console account (Admin) signed in at the checkpoint, connecting to its own session
            Clear-LabAutoLogon -Name $VmName
            Reset-LabVm -Name $VmName
            $script:ownPin = (Install-LabAgent -Name $VmName).pin
            $n = [ordered]@{}
            foreach ($mode in 'Ask', 'Pin') {
                $job = Start-LabDesktop -Name $VmName -Pin $script:ownPin -Account Console -Out (Out "own-$mode") -Steps @(
                    @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = $mode }
                    @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'EnterPin', 'Failed'); timeout = 60 }
                    @{ do = 'wait'; anyOf = @('Connected', 'Denied', 'Failed'); timeout = 120 }, @{ do = 'close' })
                $n["$mode-read"] = Invoke-LabPerson -Name $VmName -Account Console -Do read -TimeoutSec 60
                $n["$mode-deny"] = Invoke-LabPerson -Name $VmName -Account Console -Do click -Text 'Deny' -TimeoutSec 30
                $n[$mode] = Receive-LabDesktop $job
            }
            Save 'own-session' $n
        }

        It 'D-NONE-05 the own session started with Ask or Pin: the dialog is shown all the same' -Tag 'D-NONE-05' {
            (Of $n.Ask 'WaitingForUser').Count | Should -BeGreaterThan 0
            $n['Ask-read'].found | Should -BeTrue
            $n['Ask-read'].text | Should -Match 'wants to see and control your screen'
            (Of $n.Pin 'EnterPin').Count | Should -BeGreaterThan 0
            $n['Pin-read'].found | Should -BeTrue
            $n['Pin-read'].text | Should -Match 'read this code out'
            (Of $n.Ask 'Connected').Count | Should -Be 0
            (Of $n.Pin 'Connected').Count | Should -Be 0
        }
    }

    Context 'the own account recognised by its SID' {
        BeforeAll {
            # an admin account of its own, signed in to the agent as "rhsid"; renamed to "rhsid2" while connected to the sign-in
            # screen, then signed in through the picture as "rhsid2": another name, the same SID
            $s = [ordered]@{}
            $s.sid = New-LabGuestUser -Name $VmName -User 'rhsid' -Admin
            # the sign-in screen asks for user name and password (no last-user tile)
            Invoke-LabGuest -Name $VmName -ScriptBlock { Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name DontDisplayLastUserName -Value 1 -Type DWord }
            Disconnect-LabConsole -Name $VmName
            $since = Get-LabGuestTime -Name $VmName
            $o = Out 'own-sid'
            $job = Start-LabDesktop -Name $VmName -Pin $script:ownPin -Account 'rhsid' -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Failed'); timeout = 60 }
                (Mark $o 'connected'), (Park $o 'renamed'), @{ do = 'cad' }, @{ do = 'sleep'; seconds = 5 }, @{ do = 'frame'; out = 'credential.png' }
                @{ do = 'keys'; text = "rhsid2`t$password`n"; delayMs = 40 }, @{ do = 'sleep'; seconds = 75 }, @{ do = 'frame'; out = 'desktop.png' }, @{ do = 'close' })
            Reached $o 'connected'
            Invoke-LabGuest -Name $VmName -ScriptBlock { Rename-LocalUser -Name 'rhsid' -NewName 'rhsid2' }
            Release $o 'renamed'
            $s.r = Receive-LabDesktop $job
            $s.owners = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | ForEach-Object { (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User }) }
            $s.renamedSid = Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-LocalUser -Name 'rhsid2').SID.Value }
            $s.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1102)
            Save 'own-sid' $s
        }

        It 'D-SIGNIN-05 the account signing in is recognised as the admin''s own by its SID (renamed meanwhile): continued without asking' -Tag 'D-SIGNIN-05' {
            $s.renamedSid | Should -Be $s.sid
            @($s.owners) | Should -Contain 'rhsid2' -Because 'signed in through the picture under the new name'
            (Of $s.r 'WaitingForUser').Count | Should -Be 0
            (Of $s.r 'EnterPin').Count | Should -Be 0
            @($s.events | Where-Object { $_.Message -match 'with their own account . continued without asking' }).Count | Should -BeGreaterThan 0
        }
    }

    Context 'a session automatic sign-in created' {
        BeforeAll {
            Get-Job | Where-Object State -eq 'Running' | Stop-Job
            Get-Job | Remove-Job -Force
            Reset-LabVm -Name $VmName
            $auto = Install-LabAgent -Name $VmName -AutoSignIn $true
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ NoUserBehavior = 'AutoSignIn' } -Rights @{ AutoSignIn = @('S-1-5-32-544') }
            $null = Restart-LabAgent -Name $VmName -Pin $auto.pin
            Disconnect-LabConsole -Name $VmName
            $t = [ordered]@{}
            $o = Out 'auto-signed'
            $job = Start-LabDesktop -Name $VmName -Pin $auto.pin -Account Console -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'None'; autoSignIn = $true }
                @{ do = 'wait'; anyOf = @('SignedIn', 'SignInFailed', 'Failed', 'Denied'); timeout = 120 }, @{ do = 'sleep'; seconds = 10 }
                (Mark $o 'signed'), (Park $o 'checked'), @{ do = 'close' })
            Reached $o 'signed' 300
            $t.windows = @(Invoke-LabPerson -Name $VmName -Account Console -Do windows)
            $t.console = [string](Get-LabOcr -Path (Shot 'auto-signed-console')).text
            Start-Sleep -Seconds 15
            $t.windowsLater = @(Invoke-LabPerson -Name $VmName -Account Console -Do windows)
            Release $o 'checked'
            $t.r = Receive-LabDesktop $job
            Save 'auto-signed' $t
        }

        It 'D-BAN-10 a session automatic sign-in created shows only the cover, no banner' -Tag 'D-BAN-10' {
            (Of $t.r 'SignedIn').Count | Should -BeGreaterThan 0
            @($t.windows | Where-Object { $_.Title -like 'RemoteHub*Privacy screen' }).Count | Should -BeGreaterThan 0 -Because 'the cover is up'
            @($t.windows + $t.windowsLater | Where-Object Title -eq 'RemoteHub') | Should -BeNullOrEmpty
            $t.console | Should -Match 'signed in remotely'
            $t.console | Should -Not -Match 'viewed and controlled|Controlled by'
        }
    }
}
