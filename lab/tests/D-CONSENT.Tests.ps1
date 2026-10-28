# D-ASK / D-PIN / D-NONE / D-MODES - the question to the user at the device. normaluser is signed in on the console;
# PSAdmin connects through RemoteHub's desktop client (harness "desktop") and the person at the device (RhPerson.ps1 in
# normaluser's session: OCR of the agent's dialog, real mouse clicks) answers. See docs/test-lab-cases.md section 9.2.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "d-consent-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "d-consent-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Of($r, [string]$state) { , @($r.statuses | Where-Object state -eq $state) }
    # a session in the background that waits for the answer; -Extra steps run after the start (e.g. a PIN)
    function Ask([string]$Name, [string]$Consent = 'Ask', [bool]$ViewOnly = $false, [object[]]$Extra = @(), [int]$Wait = 60) {
        Start-LabDesktop -Name $VmName -Pin $pin -Out (Out $Name) -Steps (@(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = $Consent; viewOnly = $ViewOnly }
                @{ do = 'wait'; anyOf = @('WaitingForUser', 'EnterPin', 'Failed'); timeout = 60 }) + $Extra + @(
                @{ do = 'wait'; anyOf = @('Connected', 'Denied', 'Failed', 'Ended'); timeout = $Wait }, @{ do = 'close' }))
    }

    $init = Initialize-LabVm -Name $VmName -Agent -AutoLogon User
    $install = $init.Install
    $pin = $init.Pin
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-consent-agent-logs')
    # nothing to undo: the context 'own session' reverts the VM (Reset-LabVm), which drops the reuse state
}

Describe 'D-CONSENT <VmName>' -Tag Desktop, 'D-ASK', 'D-PIN', 'D-NONE', 'D-MODES' {

    Context 'Ask' {
        BeforeAll {
            $a = [ordered]@{}
            $since = Get-LabGuestTime -Name $VmName
            # allowed
            $job = Ask 'allow'
            # one step reads and clicks (each person action costs seconds of the dialog's 30)
            $a.click = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
            $a.read = [pscustomobject]@{ found = [bool]$a.click.clicked; text = (@($a.click.lines) -join ' ') }
            $a.allow = Receive-LabDesktop $job
            Start-Sleep -Seconds 3
            $a.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1100, 2100)
            # denied
            $since = Get-LabGuestTime -Name $VmName
            $job = Ask 'deny'
            $a.denyClick = Invoke-LabPerson -Name $VmName -Do click -Text 'Deny' -TimeoutSec 60
            $a.deny = Receive-LabDesktop $job
            Start-Sleep -Seconds 2
            $a.denyEvents = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2100)
            # no answer: the dialog counts down from 30 s
            $job = Ask 'timeout' -Wait 260
            $a.timeout = Receive-LabDesktop $job
            # Enter at the device doesn't allow, Esc denies
            $ready = Join-Path (Out 'keys') 'asking.txt'; $back = Join-Path (Out 'keys') 'keys-done.txt'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Out (Out 'keys') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'Ask' }, @{ do = 'wait'; state = 'WaitingForUser'; timeout = 60 }
                @{ do = 'markFile'; path = $ready }, @{ do = 'waitFile'; path = $back; timeout = 120 }
                @{ do = 'wait'; anyOf = @('Connected', 'Denied', 'Failed'); timeout = 30 }, @{ do = 'close' })
            Wait-LabHostFile $ready
            $null = Invoke-LabPerson -Name $VmName -Do read -TimeoutSec 30   # the dialog is up
            $null = Invoke-LabPerson -Name $VmName -Do key -Value '0D'
            Start-Sleep -Seconds 3
            $a.afterEnter = @(Invoke-LabPerson -Name $VmName -Do windows) | Where-Object { $_.Title -ne 'RemoteHub' }
            $null = Invoke-LabPerson -Name $VmName -Do key -Value '1B'
            Set-Content -Encoding ASCII $back 'done'
            $a.keys = Receive-LabDesktop $job
            # view-only wording
            $job = Ask 'view-only' -ViewOnly $true
            $a.viewRead = Invoke-LabPerson -Name $VmName -Do read -TimeoutSec 60
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Deny'
            $a.view = Receive-LabDesktop $job
            Save 'ask' $a
        }

        It 'D-ASK-01 Ask -> the dialog "PSAdmin wants to see and control your screen", Allow -> Connected, event 1100 with consent Ask' -Tag 'D-ASK-01' {
            $a.read.found | Should -BeTrue
            $a.read.text | Should -Match "$([regex]::Escape($vm.AdminAccount)) wants to see and control your screen"
            $a.click.clicked | Should -BeTrue
            (Of $a.allow 'WaitingForUser').Count | Should -BeGreaterThan 0
            (Of $a.allow 'Connected').Count | Should -BeGreaterThan 0
            @($a.events | Where-Object { $_.Id -eq 1100 -and $_.Message -match 'consent Ask' }).Count | Should -BeGreaterThan 0
        }

        It 'D-ASK-02 Ask -> Deny: Denied "didn''t allow it", event 2100' -Tag 'D-ASK-02' {
            $a.denyClick.clicked | Should -BeTrue
            $d = Of $a.deny 'Denied'
            $d.Count | Should -Be 1
            $d[0].message | Should -Match "didn't allow it"
            $a.denyEvents.Count | Should -BeGreaterThan 0
        }

        It 'D-ASK-03 no answer: Denied within 30 s + 10 s' -Tag 'D-ASK-03' {
            $asked = (Of $a.timeout 'WaitingForUser')[0].ms
            $d = Of $a.timeout 'Denied'
            $d.Count | Should -Be 1
            ($d[0].ms - $asked) / 1000 | Should -BeLessOrEqual 40
            ($d[0].ms - $asked) / 1000 | Should -BeGreaterOrEqual 25
        }

        It 'D-ASK-04 Enter at the device never allows; Esc denies' -Tag 'D-ASK-04' {
            @($a.afterEnter).Count | Should -BeGreaterThan 0 -Because 'the dialog is still open after Enter'
            (Of $a.keys 'Connected').Count | Should -Be 0
            (Of $a.keys 'Denied').Count | Should -Be 1
        }

        It 'D-ASK-07 view-only: the dialog says "see", not "control"' -Tag 'D-ASK-07' {
            $a.viewRead.text | Should -Match 'wants to see your screen'
            $a.viewRead.text | Should -Not -Match 'control'
        }
    }

    Context 'no input before the answer' {
        BeforeAll {
            $marker = Start-LabMarker -Name $VmName
            $before = @(Get-LabMarker -Name $VmName -Marker $marker).Count
            $ready = Join-Path (Out 'input') 'asking.txt'; $back = Join-Path (Out 'input') 'answer.txt'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Out (Out 'input') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'Ask' }, @{ do = 'wait'; state = 'WaitingForUser'; timeout = 60 }
                @{ do = 'keys'; text = 'blocked' }, @{ do = 'mouse'; action = 'Move'; x = 300; y = 300 }, @{ do = 'mouse'; action = 'Down'; button = 'Left'; x = 300; y = 300 }
                @{ do = 'mouse'; action = 'Up'; button = 'Left'; x = 300; y = 300 }, @{ do = 'markFile'; path = $ready }
                @{ do = 'waitFile'; path = $back; timeout = 120 }, @{ do = 'wait'; anyOf = @('Denied', 'Connected'); timeout = 30 }, @{ do = 'close' })
            Wait-LabHostFile $ready
            Start-Sleep -Seconds 2
            $seen = @(Get-LabMarker -Name $VmName -Marker $marker | Select-Object -Skip $before | Where-Object e -in 'down', 'char', 'mdown', 'mup')
            $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Deny'
            Set-Content -Encoding ASCII $back 'done'
            $r = Receive-LabDesktop $job
            Stop-LabInSession -Name $VmName -Task $marker.Task
            Save 'input' @{ Seen = $seen; R = $r }
        }

        It 'D-ASK-06 keys and clicks sent while waiting for the answer don''t reach the session' -Tag 'D-ASK-06' {
            @($seen) | Should -BeNullOrEmpty
        }
    }

    Context 'PIN' {
        BeforeAll {
            $p = [ordered]@{}
            $since = Get-LabGuestTime -Name $VmName
            foreach ($case in 'right', 'spaced') {
                $file = Join-Path (Out "pin-$case") 'pin.txt'
                $job = Ask "pin-$case" -Consent Pin -Extra @(@{ do = 'pin'; fromFile = $file; timeout = 90 })
                $p["$case-read"] = Invoke-LabPerson -Name $VmName -Do pin -TimeoutSec 60
                $code = $p["$case-read"].pin
                Set-Content -Encoding ASCII $file $(if ($case -eq 'spaced' -and $code) { $code.Substring(0, 3) + ' ' + $code.Substring(3) } else { $code })
                $p[$case] = Receive-LabDesktop $job
            }
            $job = Ask 'pin-wrong' -Consent Pin -Extra @(@{ do = 'pin'; pin = '000000' }, @{ do = 'sleep'; seconds = 1 }, @{ do = 'pin'; pin = '000001' }, @{ do = 'sleep'; seconds = 1 }, @{ do = 'pin'; pin = '000002' })
            $p.wrong = Receive-LabDesktop $job
            $p.wrongEvents = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2100)
            $job = Ask 'pin-deny' -Consent Pin
            $p.denyClick = Invoke-LabPerson -Name $VmName -Do click -Text 'Deny' -TimeoutSec 60
            $p.deny = Receive-LabDesktop $job
            $job = Ask 'pin-timeout' -Consent Pin -Wait 240
            $p.timeout = Receive-LabDesktop $job
            Save 'pin' $p
        }

        It 'D-PIN-01 the 6-digit code shown at the device, sent -> Connected' -Tag 'D-PIN-01' {
            $p['right-read'].pin | Should -Match '^\d{6}$'
            (Of $p.right 'EnterPin').Count | Should -BeGreaterThan 0
            (Of $p.right 'Connected').Count | Should -BeGreaterThan 0
        }

        It 'D-PIN-02 three wrong codes: attempts left 2, 1, then Denied "entered wrong too often", event 2100' -Tag 'D-PIN-02' {
            # Of returns its list as one object (the comma): member enumeration, not ForEach-Object on that one array
            $left = @((Of $p.wrong 'EnterPin').pinAttemptsLeft)
            $left | Should -Contain 2
            $left | Should -Contain 1
            (Of $p.wrong 'Denied')[0].message | Should -Match 'entered wrong too often'
            @($p.wrongEvents | Where-Object { $_.Message -match 'entered wrong too often' }).Count | Should -BeGreaterThan 0
        }

        It 'D-PIN-03 "123 456" (with a space) is accepted' -Tag 'D-PIN-03' {
            $p['spaced-read'].pin | Should -Match '^\d{6}$' -Because 'the code is read at the device before it is sent with a space'
            (Of $p.spaced 'Connected').Count | Should -BeGreaterThan 0
        }

        It 'D-PIN-04 Deny in the PIN dialog: Denied "refused"' -Tag 'D-PIN-04' {
            $p.denyClick.clicked | Should -BeTrue
            (Of $p.deny 'Denied')[0].message | Should -Match 'refused'
        }

        It 'D-PIN-05 no code for 3 minutes: Denied' -Tag 'D-PIN-05' {
            $asked = (Of $p.timeout 'EnterPin')[0].ms
            $d = Of $p.timeout 'Denied'
            $d.Count | Should -Be 1
            ($d[0].ms - $asked) / 1000 | Should -BeGreaterOrEqual 170
        }
    }

    Context 'timeout policy' {
        BeforeAll {
            $t = [ordered]@{}
            foreach ($v in 5, 4, 601) {
                Set-LabAgentPolicy -Name $VmName -Clear -Values @{ ConsentTimeoutSeconds = $v }
                $t["hello-$v"] = (Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out "timeout-$v") -Steps @(@{ do = 'connect' }, @{ do = 'close' })).hello.consentTimeoutSeconds
            }
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ ConsentTimeoutSeconds = 5 }
            $t.run = Receive-LabDesktop (Ask 'timeout-5s')
            Set-LabAgentPolicy -Name $VmName -Clear
            Save 'timeout-policy' $t
        }

        It 'D-ASK-05 ConsentTimeoutSeconds=5 counts down 5 s; 4 and 601 are ignored (30)' -Tag 'D-ASK-05' {
            $t['hello-5'] | Should -Be 5
            $t['hello-4'] | Should -Be 30
            $t['hello-601'] | Should -Be 30
            $asked = (Of $t.run 'WaitingForUser')[0].ms
            ((Of $t.run 'Denied')[0].ms - $asked) / 1000 | Should -BeLessOrEqual 15
        }
    }

    Context 'None and the allowed modes' {
        BeforeAll {
            $n = [ordered]@{}
            $since = Get-LabGuestTime -Name $VmName
            $direct = { param($name, $consent) Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out $name) -Steps @(
                    @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = $consent }
                    @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'EnterPin', 'Failed', 'Denied'); timeout = 60 }, @{ do = 'close' }) }
            $n.default = & $direct 'none-default' 'None'
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ AllowedConsentModes = @('Ask', 'Pin', 'None') }
            $n.policyOnly = & $direct 'none-policy' 'None'
            Set-LabAgentPolicy -Name $VmName -Values @{ AllowedConsentModes = @('Ask', 'Pin', 'None') } -Rights @{ ConsentBypass = @('S-1-5-32-544') }
            $ready = Join-Path (Out 'none-both') 'connected.txt'; $back = Join-Path (Out 'none-both') 'checked.txt'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Out (Out 'none-both') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'None' }, @{ do = 'wait'; anyOf = @('Connected', 'Failed'); timeout = 60 }
                @{ do = 'markFile'; path = $ready }, @{ do = 'waitFile'; path = $back; timeout = 120 }, @{ do = 'close' })
            Wait-LabHostFile $ready
            Start-Sleep -Seconds 3
            $n.banner = @(Invoke-LabPerson -Name $VmName -Do windows)
            Set-Content -Encoding ASCII $back 'done'
            $n.both = Receive-LabDesktop $job
            # REG_SZ list / invalid list
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ AllowedConsentModes = 'Pin;Ask' }
            $n.regSz = (Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out 'modes-sz') -Steps @(@{ do = 'connect' }, @{ do = 'close' })).hello.consentModes
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ AllowedConsentModes = 'Foo' }
            $n.invalid = (Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out 'modes-foo') -Steps @(@{ do = 'connect' }, @{ do = 'close' })).hello.consentModes
            # only Pin
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ AllowedConsentModes = @('Pin') }
            $n.pinOnlyAsk = & $direct 'pin-only-ask' 'Ask'
            $n.pinOnlyPin = & $direct 'pin-only-pin' 'Pin'
            Set-LabAgentPolicy -Name $VmName -Clear
            $n.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2100)
            Save 'none' $n
        }

        It 'D-NONE-01 None isn''t offered by default; a Start with None is refused, event 2100' -Tag 'D-NONE-01' {
            (Of $n.default 'Failed').Count | Should -Be 1
            (Of $n.default 'Failed')[0].message | Should -Match "isn't allowed"
            @($n.events | Where-Object { $_.Message -match 'None' }).Count | Should -BeGreaterThan 0
        }

        It 'D-NONE-02 None in the policy but no right ConsentBypass: still refused' -Tag 'D-NONE-02' {
            (Of $n.policyOnly 'Failed').Count | Should -Be 1
        }

        It 'D-NONE-03 policy and right: connected without a question, the banner shown' -Tag 'D-NONE-03' {
            (Of $n.both 'WaitingForUser').Count | Should -Be 0
            (Of $n.both 'Connected').Count | Should -BeGreaterThan 0
            @($n.banner | Where-Object Title -eq 'RemoteHub').Count | Should -Be 1
        }

        It 'D-MODES-01 "Pin;Ask" as REG_SZ works; "Foo" falls back to Ask + Pin' -Tag 'D-MODES-01' {
            @($n.regSz | Sort-Object) | Should -Be @('Ask', 'Pin')
            @($n.invalid | Sort-Object) | Should -Be @('Ask', 'Pin')
        }

        It 'D-MODES-02 only Pin allowed: Ask refused, Pin works' -Tag 'D-MODES-02' {
            (Of $n.pinOnlyAsk 'Failed').Count | Should -Be 1
            (Of $n.pinOnlyPin 'EnterPin').Count | Should -BeGreaterThan 0
        }
    }

    Context 'own session' {
        BeforeAll {
            # the console account on its own console session
            Clear-LabAutoLogon -Name $VmName
            Reset-LabVm -Name $VmName
            $own = Install-LabAgent -Name $VmName
            $since = Get-LabGuestTime -Name $VmName
            $r = Invoke-LabDesktop -Name $VmName -Pin $own.pin -Account Console -Out (Out 'own') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'None' }
                @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'Failed'); timeout = 60 }, @{ do = 'close' })
            Start-Sleep -Seconds 2
            $events = @(Get-LabAgentEvent -Name $VmName -Since $since)
            Save 'own' @{ R = $r; Events = $events }
        }

        It 'D-NONE-04 connecting to the own signed-in session needs no question' -Tag 'D-NONE-04' {
            (Of $r 'WaitingForUser').Count | Should -Be 0
            (Of $r 'Connected').Count | Should -BeGreaterThan 0
            @($events | Where-Object Id -eq 1100).Count | Should -BeGreaterThan 0
        }
    }
}
