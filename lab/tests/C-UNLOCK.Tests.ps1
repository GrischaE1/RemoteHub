# C-UNLOCK - the admin's own session on the console, locked or disconnected, continued by automatic sign-in instead of
# being refused (docs/credential-provider-design.md section 12.1, docs/app-tabs-design.md section 4.1; the cases are in
# docs/test-lab-cases.md section 9.12). The console account is signed in at the console (AutoLogon), its session locked
# in the session itself (LockWorkStation); the harness signs in to the agent as that account, and the provider unlocks
# the session on its lock screen (CPUS_UNLOCK_WORKSTATION). The agent MSI under test must be built with the provider and
# signed (build-installer.ps1 -LabSigning -CredentialProvider).
#   C-UNLOCK-01  an app tab with the account's session locked: unlocked - the same session, its explorer.exe kept - and
#                the app runs in it
#   C-UNLOCK-02  the last app tab ending locks the session again (event 1107 "locked"); it is never signed out
#   C-UNLOCK-03  a desktop tab: "Ready to unlock", "Unlocked as", the same session behind the privacy screen, end action
#                Lock although RemoteHub asked for a sign-out
#   C-UNLOCK-04  closing that desktop tab locks the session again; it is never signed out
#   C-UNLOCK-05  the session the lock left disconnected: the next automatic sign-in reconnects it ("Signed in to your
#                session") - the same session and explorer.exe - and locks it again at the end
# Each context starts from the checkpoint with its own install (the lock leaves the session disconnected).
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $account = $vm.ConsoleAccount
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "c-unlock-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "c-unlock-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Mark([string]$o, [string]$n) { @{ do = 'markFile'; path = (Join-Path $o "$n.txt") } }
    function Park([string]$o, [string]$n, [int]$t = 300) { @{ do = 'waitFile'; path = (Join-Path $o "$n.txt"); timeout = $t } }
    function Reached([string]$o, [string]$n, [int]$t = 300) { Wait-LabHostFile (Join-Path $o "$n.txt") -TimeoutSec $t }
    function Release([string]$o, [string]$n) { Set-Content -Encoding ASCII (Join-Path $o "$n.txt") 'go' }
    function Text($r) { (@($r.statuses | ForEach-Object { "$($_.state): $($_.message)" }) + @($r.errors)) -join "`n" }
    function Signed($r) { @($r.statuses | Where-Object state -eq 'SignedIn') | Select-Object -First 1 }
    function Shot([string]$name) { $p = Join-Path $EvidenceDir "c-unlock-$name.png"; $null = Get-LabScreenshot -Name $VmName -Path $p; [string](Get-LabOcr -Path $p).text }
    function Describe-Sessions { (Get-LabSessions -Name $VmName | ForEach-Object { "$($_.Id) $($_.State) '$($_.User)' console=$($_.Console) logonui=$($_.LogonUI) explorer=$($_.Explorer -join ',')" }) -join '; ' }
    # the console account's sessions (",": the array as it is, also for none or one)
    function Mine { , @(Get-LabSessions -Name $VmName | Where-Object { $_.User -match "\\$([regex]::Escape($account))$" }) }
    # signed in at the console, its desktop up (explorer.exe running)
    function Wait-Desktop([int]$TimeoutSec = 120) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do { $m = Mine; $s = @($m | Where-Object { $_.Console -and $_.State -eq 'Active' -and $_.Explorer.Count -gt 0 -and -not $_.LogonUI }); if ($s.Count -gt 0) { return $s[0] }; Start-Sleep -Seconds 2 } while ((Get-Date) -lt $until)
        throw "the console account's desktop wasn't up within $TimeoutSec s ($(Describe-Sessions))"
    }
    # its session locked: still on the console, the lock screen (LogonUI) in it
    function Wait-Locked([int]$TimeoutSec = 60) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do { $m = Mine; $s = @($m | Where-Object { $_.Console -and $_.LogonUI }); if ($s.Count -gt 0) { return $s[0] }; Start-Sleep -Seconds 2 } while ((Get-Date) -lt $until)
        throw "the console account's session wasn't locked within $TimeoutSec s ($(Describe-Sessions))"
    }
    # after the end action: its session off the console's desktop - disconnected, or locked - but still there
    function Wait-Secured([int]$Id, [int]$TimeoutSec = 60) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do { $m = Mine; $s = @($m | Where-Object { $_.Id -eq $Id -and ($_.State -eq 'Disconnected' -or $_.LogonUI) }); if ($s.Count -gt 0) { return $s[0] }; Start-Sleep -Seconds 3 } while ((Get-Date) -lt $until)
        $null
    }
    # event 1107 "Automatic sign-in session <n> of <account> locked / signed out (<why>)" since a guest time
    function Wait-Ended([datetime]$Since, [int]$TimeoutSec = 90) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do { $e = @(Get-LabAgentEvent -Name $VmName -Since $Since -Id 1107); if ($e.Count -gt 0) { return $e }; Start-Sleep -Seconds 3 } while ((Get-Date) -lt $until)
        @()
    }
    # automatic sign-in allowed for Administrators by policy (as IT would set it), a fresh agent (rate limit, cached checks)
    function Prepare {
        Set-LabAgentPolicy -Name $VmName -Clear -Values @{ NoUserBehavior = 'AutoSignIn' } -Rights @{ AutoSignIn = @('S-1-5-32-544') }
        $null = Restart-LabAgent -Name $VmName -Pin $pin
    }
    # the console account signed in at the console, then its session locked; answers the session before the lock
    function Lock-Mine {
        $before = Wait-Desktop
        Lock-LabSession -Name $VmName -Account Console
        $null = Wait-Locked
        $before
    }
}

AfterAll {
    Get-Job | Where-Object State -eq 'Running' | Stop-Job
    Get-Job | Remove-Job -Force
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'c-unlock-agent-logs')
    # sessions locked and disconnected, policy set: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'C-UNLOCK <VmName>' -Tag Agent, 'C-UNLOCK' {

    Context 'an app tab in the locked session' {
        BeforeAll {
            $init = Initialize-LabVm -Name $VmName -Agent -AutoSignIn $true -AutoLogon Console -Clean
            $pin = $init.Pin
            $exe = Install-LabTestApp -Name $VmName
            $appLog = 'C:\ProgramData\RemoteHubLab\session\c-unlock-app.jsonl'
            Prepare
            $a = [ordered]@{}
            $a.Before = Lock-Mine
            $o = Out 'app'
            $app = @{ program = $exe; arguments = "/colour:0,255,0 /title:RH-unlock /log:$appLog"; workingFolder = ''; elevated = $false; name = 'RH unlock' }
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; app = $app; autoSignIn = $true }
                @{ do = 'wait'; state = 'StartingApp'; anyOf = @('Failed', 'Denied', 'Ended'); timeout = 300 }
                @{ do = 'wait'; state = 'Connected'; anyOf = @('Failed', 'Ended'); timeout = 150 }
                @{ do = 'sleep'; seconds = 5 }, (Mark $o 'running'), (Park $o 'close'), @{ do = 'close' })
            Reached $o 'running' 480
            $a.Running = Mine
            $a.Started = @(Get-LabTestAppLog -Name $VmName -Path $appLog | Where-Object e -eq 'started')
            $since = Get-LabGuestTime -Name $VmName
            Release $o 'close'
            $a.R = Receive-LabDesktop $job
            $a.Ended = @(Wait-Ended $since)
            $a.After = Wait-Secured $a.Before.Id
            $a.Sessions = Describe-Sessions
            $a.SignedInEvent = @(Get-LabAgentEvent -Name $VmName -Since $since.AddMinutes(-10) -Id 1106 | Select-Object -Last 1)
            Save 'app' $a
        }

        It 'C-UNLOCK-01 an app tab unlocks the account''s own locked session - the same session, its desktop kept - and runs the app in it' -Tag 'C-UNLOCK-01' {
            Text $a.R | Should -Match 'SignedIn: Unlocked as'
            (Signed $a.R).existing | Should -BeTrue
            $a.Started.Count | Should -BeGreaterThan 0 -Because "the app runs in the unlocked session ($(Text $a.R))"
            $same = @($a.Running | Where-Object Id -eq $a.Before.Id)
            $same.Count | Should -Be 1 -Because "no new session - the one that was locked ($($a.Sessions))"
            @($a.Running).Count | Should -Be 1
            @($same[0].Explorer | Where-Object { $a.Before.Explorer -contains $_ }).Count | Should -BeGreaterThan 0 -Because 'the desktop that was there before (its explorer.exe) is kept'
            ($a.SignedInEvent.Message -join "`n") | Should -Match 'unlocked console session'
        }

        It 'C-UNLOCK-02 the last app tab ending locks the session again - never signed out' -Tag 'C-UNLOCK-02' {
            ($a.Ended.Message -join "`n") | Should -Match ' locked \('
            ($a.Ended.Message -join "`n") | Should -Not -Match 'signed out'
            $a.After | Should -Not -BeNullOrEmpty -Because "the session is still there, off the console's desktop ($($a.Sessions))"
            @($a.After.Explorer | Where-Object { $a.Before.Explorer -contains $_ }).Count | Should -BeGreaterThan 0 -Because 'its programs keep running'
        }
    }

    Context 'a desktop tab in the locked session, then the disconnected one' {
        BeforeAll {
            $init = Initialize-LabVm -Name $VmName -Agent -AutoSignIn $true -AutoLogon Console -Clean
            $pin = $init.Pin
            Prepare
            $d = [ordered]@{}
            $d.Before = Lock-Mine
            # RemoteHub asks for a sign-out at the end: a session that was there before is locked all the same
            $o = Out 'desktop'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'None'; autoSignIn = $true; endAction = 'SignOut' }
                @{ do = 'wait'; anyOf = @('SignedIn', 'SignInFailed', 'Failed', 'Denied'); timeout = 150 }, @{ do = 'sleep'; seconds = 10 }
                (Mark $o 'unlocked'), (Park $o 'close'), @{ do = 'close' })
            Reached $o 'unlocked'
            $d.Unlocked = Mine
            $d.ConsoleText = Shot 'desktop-console'
            $since = Get-LabGuestTime -Name $VmName
            Release $o 'close'
            $d.R = Receive-LabDesktop $job
            $d.Ended = @(Wait-Ended $since)
            $d.After = Wait-Secured $d.Before.Id
            $d.Sessions = Describe-Sessions
            $d.SignedInEvent = @(Get-LabAgentEvent -Name $VmName -Since $since.AddMinutes(-5) -Id 1106 | Select-Object -Last 1)

            # C-UNLOCK-05: the lock left the session disconnected - the console shows the sign-in screen
            $o = Out 'reconnect'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'None'; autoSignIn = $true }
                @{ do = 'wait'; anyOf = @('SignedIn', 'SignInFailed', 'Failed', 'Denied'); timeout = 150 }, @{ do = 'sleep'; seconds = 10 }
                (Mark $o 'reconnected'), (Park $o 'close'), @{ do = 'close' })
            Reached $o 'reconnected'
            $d.Reconnected = Mine
            $since = Get-LabGuestTime -Name $VmName
            Release $o 'close'
            $d.R2 = Receive-LabDesktop $job
            $d.Ended2 = @(Wait-Ended $since)
            $d.After2 = Wait-Secured $d.Before.Id
            $d.Sessions2 = Describe-Sessions
            $d.SignedInEvent2 = @(Get-LabAgentEvent -Name $VmName -Since $since.AddMinutes(-5) -Id 1106 | Select-Object -Last 1)
            Save 'desktop' $d
        }

        It 'C-UNLOCK-03 a desktop tab unlocks the account''s own locked session behind the privacy screen; the end action is Lock although RemoteHub asked for a sign-out' -Tag 'C-UNLOCK-03' {
            Text $d.R | Should -Match 'SignInReady: Ready to unlock as'
            Text $d.R | Should -Match 'SignedIn: Unlocked as'
            $signed = Signed $d.R
            $signed.existing | Should -BeTrue
            $signed.endAction | Should -Be 'Lock'
            $same = @($d.Unlocked | Where-Object { $_.Id -eq $d.Before.Id -and $_.Console -and -not $_.LogonUI })
            $same.Count | Should -Be 1 -Because "the session that was locked is on the console's desktop again ($($d.Sessions))"
            @($same[0].Explorer | Where-Object { $d.Before.Explorer -contains $_ }).Count | Should -BeGreaterThan 0
            ($d.SignedInEvent.Message -join "`n") | Should -Match 'unlocked console session.*privacy screen on'
            $d.ConsoleText | Should -Match 'signed in remotely'
        }

        It 'C-UNLOCK-04 closing that desktop tab locks the session again - never signed out' -Tag 'C-UNLOCK-04' {
            ($d.Ended.Message -join "`n") | Should -Match ' locked \('
            ($d.Ended.Message -join "`n") | Should -Not -Match 'signed out'
            $d.After | Should -Not -BeNullOrEmpty -Because "the session is still there ($($d.Sessions))"
            @($d.After.Explorer | Where-Object { $d.Before.Explorer -contains $_ }).Count | Should -BeGreaterThan 0
        }

        It 'C-UNLOCK-05 the next automatic sign-in reconnects the account''s disconnected session and locks it again at the end' -Tag 'C-UNLOCK-05' {
            Text $d.R2 | Should -Match 'SignedIn: Signed in to your session as'
            (Signed $d.R2).existing | Should -BeTrue
            $same = @($d.Reconnected | Where-Object { $_.Id -eq $d.Before.Id -and $_.Console })
            $same.Count | Should -Be 1 -Because "the disconnected session came back to the console, no new one ($($d.Sessions2))"
            @($d.Reconnected).Count | Should -Be 1
            @($same[0].Explorer | Where-Object { $d.Before.Explorer -contains $_ }).Count | Should -BeGreaterThan 0
            ($d.SignedInEvent2.Message -join "`n") | Should -Match 'disconnected session, reconnected'
            ($d.Ended2.Message -join "`n") | Should -Match ' locked \('
            $d.After2 | Should -Not -BeNullOrEmpty
        }
    }
}
