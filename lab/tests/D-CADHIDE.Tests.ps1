# D-CADHIDE - while the privacy screen blocks the device's mouse and keyboard, the Ctrl+Alt+Del screen offers only
# Task Manager and Cancel: Lock, Switch user, Change a password, Sign out and the power button are hidden by their policy
# values (SasActions.cs), and put back as they were when the block ends, the connection ends, or - after a helper that
# was killed - when the service starts. The same on the privacy screen automatic sign-in puts up.
# The agent MSI must be built with the credential provider (build-installer.ps1 -LabSigning -CredentialProvider).
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "d-cadhide-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "d-cadhide-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Mark([string]$o, [string]$n) { @{ do = 'markFile'; path = (Join-Path $o "$n.txt") } }
    function Park([string]$o, [string]$n, [int]$t = 300) { @{ do = 'waitFile'; path = (Join-Path $o "$n.txt"); timeout = $t } }
    function Reached([string]$o, [string]$n, [int]$t = 180) { Wait-LabHostFile (Join-Path $o "$n.txt") -TimeoutSec $t }
    function Release([string]$o, [string]$n) { Set-Content -Encoding ASCII (Join-Path $o "$n.txt") 'go' }

    # The six values for an account: 1 = hidden, $null = not there; plus the agent's record files.
    function Get-Values([string]$Account) {
        Invoke-LabGuest -Name $VmName -ArgumentList $Account -ScriptBlock {
            param($account)
            $sid = (New-Object Security.Principal.NTAccount($account)).Translate([Security.Principal.SecurityIdentifier]).Value
            $u = "Registry::HKEY_USERS\$sid\Software\Microsoft\Windows\CurrentVersion\Policies"
            $m = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
            function V($path, $name) { (Get-ItemProperty $path -Name $name -ErrorAction SilentlyContinue).$name }
            [pscustomobject]@{
                loaded                 = Test-Path "Registry::HKEY_USERS\$sid"
                DisableTaskMgr         = V "$u\System" DisableTaskMgr
                DisableLockWorkstation = V "$u\System" DisableLockWorkstation
                DisableChangePassword  = V "$u\System" DisableChangePassword
                NoLogoff               = V "$u\Explorer" NoLogoff
                NoClose                = V "$u\Explorer" NoClose
                HideFastUserSwitching  = V $m HideFastUserSwitching
                records                = @(Get-ChildItem 'C:\ProgramData\RemoteHub\Agent' -Filter 'sas-actions-*.txt' -ErrorAction SilentlyContinue | ForEach-Object Name)
            }
        }
    }
    # the user at the device allows the connection (the click is tried again when it missed)
    function Allow { for ($i = 0; $i -lt 3; $i++) { if ((Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60).clicked) { return }; Start-Sleep -Seconds 3 } }
    $names = 'DisableLockWorkstation', 'DisableChangePassword', 'NoLogoff', 'NoClose', 'HideFastUserSwitching'
    $actions = 'Lock', 'Switch user', 'Sign out', 'Change a password', 'Task Manager'
    # Ctrl+Alt+Del at the console, its screen read, Esc back
    # The screen is read once it is there (it has Cancel) - Ctrl+Alt+Del is sent again when it didn't come within 10 s
    # (run 20261021-062735 read the privacy screen 4 s after it). Actions match as whole words with their case: "Lock"
    # is not the one in "blocked".
    function Read-CadScreen([string]$Path) {
        $text = ''
        foreach ($try in 1..2) {
            Send-LabCtrlAltDel -Name $VmName
            $until = (Get-Date).AddSeconds(10)
            do {
                Start-Sleep -Seconds 2
                $null = Get-LabScreenshot -Name $VmName -Path $Path
                $text = (Get-LabOcr -Path $Path).text
            } while ($text -cnotmatch '\bCancel\b' -and (Get-Date) -lt $until)
            if ($text -cmatch '\bCancel\b') { break }
        }
        $null = Invoke-LabPerson -Name $VmName -Do key -Value '1B'
        # the screen gone again (no Cancel any more), so the next step doesn't meet it
        $gone = [IO.Path]::ChangeExtension($Path, '.closed.png')
        $null = Wait-LabCondition -NoThrow -TimeoutSec 15 -Because 'the Ctrl+Alt+Del screen did not close after Esc' -Until {
            $null = Get-LabScreenshot -Name $VmName -Path $gone
            (Get-LabOcr -Path $gone).text -cnotmatch '\bCancel\b'
        }
        [pscustomobject]@{ text = $text; shown = @($actions | Where-Object { $text -cmatch "\b$([regex]::Escape($_))\b" }) }
    }
    # whether none of the five values is set and no record is left for an account (put back)
    function Test-PutBack([string]$Account) {
        $v = Get-Values $Account
        -not @($names | Where-Object { $v.$_ }).Count -and -not @($v.records).Count
    }

    $vm = Get-LabVm $VmName
    $userAccount = $vm.UserAccount
    $consoleAccount = $vm.ConsoleAccount
    $init = Initialize-LabVm -Name $VmName -Agent -AutoSignIn $true -AutoLogon User
    $install = $init.Install
    $pin = $init.Pin
    $r = [ordered]@{}

    # ---- 1. the admin's privacy screen, input blocked; then unblocked while still connected; then the end
    $r.before = Get-Values $userAccount
    $r.cadBefore = Read-CadScreen (Join-Path (Out 'before') 'cad.png')
    $o = Out 'privacy'
    $job = Start-LabDesktop -Name $VmName -Pin $pin -Out $o -Steps @(
        @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 120 }
        @{ do = 'privacy'; style = 'Black'; blockLocalInput = $true }, @{ do = 'sleep'; seconds = 4 }, (Mark $o 'blocked'), (Park $o 'blocked-done')
        @{ do = 'privacy'; style = 'Black'; blockLocalInput = $false }, @{ do = 'sleep'; seconds = 3 }, (Mark $o 'free'), (Park $o 'free-done')
        @{ do = 'privacy'; style = 'Black'; blockLocalInput = $true }, @{ do = 'sleep'; seconds = 4 }, (Mark $o 'again'), (Park $o 'again-done')
        @{ do = 'close' })
    Allow
    Reached $o 'blocked'
    $r.blocked = Get-Values $userAccount
    $r.cadBlocked = Read-CadScreen (Join-Path $o 'cad-blocked.png')
    Release $o 'blocked-done'
    Reached $o 'free'
    $r.free = Get-Values $userAccount
    Release $o 'free-done'
    Reached $o 'again'
    $r.again = Get-Values $userAccount
    Release $o 'again-done'
    $r.privacy = Receive-LabDesktop $job
    $null = Wait-LabCondition -NoThrow -TimeoutSec 20 -Because 'the values were not put back after the connection ended' -Until { Test-PutBack $userAccount }
    $r.ended = Get-Values $userAccount

    # ---- 2. the helper is killed while they are hidden: the service puts them back when it starts
    $o = Out 'killed'
    $job = Start-LabDesktop -Name $VmName -Pin $pin -Out $o -Steps @(
        @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 120 }
        @{ do = 'privacy'; style = 'Black'; blockLocalInput = $true }, @{ do = 'sleep'; seconds = 4 }, (Mark $o 'blocked'), (Park $o 'blocked-done'), @{ do = 'close' })
    Allow
    Reached $o 'blocked'
    $r.killed = Get-Values $userAccount
    # the service and its helper both ended abruptly (a helper alone is started again by the service, and that one puts
    # them back when the connection ends): only the service's start can put them back
    $null = Invoke-LabGuest -Name $VmName -ScriptBlock {
        $killed = @(Get-Process RemoteHubAgent -ErrorAction SilentlyContinue | ForEach-Object Id)
        if (-not $killed.Count) { return }
        Get-Process -Id $killed -ErrorAction SilentlyContinue | Stop-Process -Force
        # until the killed processes have ended (a restarted service is a new process)
        $until = (Get-Date).AddSeconds(15)
        do {
            if (-not (Get-Process -Id $killed -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 250
        } while ((Get-Date) -lt $until)
    }
    Release $o 'blocked-done'
    $null = try { Receive-LabDesktop $job } catch { $_ }
    $null = Restart-LabAgent -Name $VmName -Pin $pin
    $null = Wait-LabAgent -Name $VmName -Pin $pin
    $r.afterRestart = Get-Values $userAccount
    $r.restartLog = @(Get-LabAgentLog -Name $VmName | Where-Object { $_ -match 'Ctrl\+Alt\+Del screen' })

    # ---- 3. automatic sign-in: the admin's own new session starts covered, input blocked
    Disconnect-LabConsole -Name $VmName
    $harness = @('auto-sign-in', '--host', (Get-LabVmAddress -Name $VmName), '--port', '58732', '--pin', $pin,
        '--user', $consoleAccount, '--password', (Get-LabConfig).Password, '--stay', '90', '--close', '1')
    $mod = Join-Path $PSScriptRoot '..\RemoteHub.TestLab\RemoteHub.TestLab.psd1'
    $auto = Start-Job -ArgumentList $mod, $harness, $VmName -ScriptBlock { param($mod, $a, $vm) $m = Import-Module $mod -PassThru; & $m { param($a, $vm) Invoke-LabHarness $a -TimeoutSec 300 -VmName $vm } $a $vm }
    $until = (Get-Date).AddSeconds(150)
    do {
        Start-Sleep -Seconds 5
        $who = Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-CimInstance Win32_ComputerSystem).UserName }
    } while ($who -notmatch [regex]::Escape(($consoleAccount -split '\\')[-1]) -and (Get-Date) -lt $until)
    $r.autoUser = $who
    # the helper in the new session, its overlay and the input block: the five values set
    $null = Wait-LabCondition -NoThrow -TimeoutSec 30 -Because 'the five values were not set in the automatic sign-in session' -Until {
        $v = Get-Values $consoleAccount
        @($names | Where-Object { $v.$_ -eq 1 }).Count -eq $names.Count
    }
    $r.autoBlocked = Get-Values $consoleAccount
    $r.cadAuto = Read-CadScreen (Join-Path (Out 'auto') 'cad-auto.png')
    # ended on purpose (--close 1): the session is locked and the values go back. Run 20261019-193627 let the harness
    # exit without a close - a dropped connection keeps the screen covered for 120 s, so 5 s later all five were still set
    $r.auto = Receive-Job $auto -Wait -AutoRemoveJob
    $until = (Get-Date).AddSeconds(60)
    do {
        $r.autoEnded = Get-Values $consoleAccount
        if (-not @($r.autoEnded.records).Count) { break }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $until)
    $r.log = @(Get-LabAgentLog -Name $VmName | Where-Object { $_ -match 'Ctrl\+Alt\+Del screen' })
    Save 'result' $r
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-cadhide-agent-logs')
    # the agent killed, the console account signed in by automatic sign-in, its Ctrl+Alt+Del values: not for the next file
    Set-LabVmDirty -Name $VmName
}

Describe 'D-CADHIDE <VmName>' -Tag Desktop, 'D-CADHIDE' {
    It 'D-CADHIDE-01 before: the values are not set, the Ctrl+Alt+Del screen offers Lock and Task Manager (control)' -Tag 'D-CADHIDE-01' {
        foreach ($n in $names) { $r.before.$n | Should -BeNullOrEmpty -Because $n }
        $r.cadBefore.shown | Should -Contain 'Lock'
        $r.cadBefore.shown | Should -Contain 'Task Manager'
    }

    It 'D-CADHIDE-02 privacy screen with input blocked: the five set, Task Manager not; the Ctrl+Alt+Del screen offers only Task Manager' -Tag 'D-CADHIDE-02' {
        foreach ($n in $names) { $r.blocked.$n | Should -Be 1 -Because $n }
        $r.blocked.DisableTaskMgr | Should -BeNullOrEmpty
        @($r.cadBlocked.shown) | Should -Be @('Task Manager')
    }

    It 'D-CADHIDE-03 input unblocked while still connected: put back; blocked again: hidden again' -Tag 'D-CADHIDE-03' {
        foreach ($n in $names) { $r.free.$n | Should -BeNullOrEmpty -Because $n }
        foreach ($n in $names) { $r.again.$n | Should -Be 1 -Because $n }
    }

    It 'D-CADHIDE-04 the connection ends: put back as they were, no record left' -Tag 'D-CADHIDE-04' {
        foreach ($n in $names) { $r.ended.$n | Should -BeNullOrEmpty -Because $n }
        @($r.ended.records) | Should -BeNullOrEmpty
    }

    It 'D-CADHIDE-05 the helper is killed: still hidden; the service puts them back when it starts' -Tag 'D-CADHIDE-05' {
        foreach ($n in $names) { $r.killed.$n | Should -Be 1 -Because $n }
        foreach ($n in $names) { $r.afterRestart.$n | Should -BeNullOrEmpty -Because $n }
        @($r.afterRestart.records) | Should -BeNullOrEmpty
        @($r.restartLog | Where-Object { $_ -match 'earlier helper hid are back' }).Count | Should -BeGreaterThan 0
    }

    It 'D-CADHIDE-06 automatic sign-in: hidden in the admin''s new session, the Ctrl+Alt+Del screen offers only Task Manager; put back after' -Tag 'D-CADHIDE-06' {
        $r.autoUser | Should -Match ([regex]::Escape(($consoleAccount -split '\\')[-1]))
        foreach ($n in $names) { $r.autoBlocked.$n | Should -Be 1 -Because $n }
        $r.autoBlocked.DisableTaskMgr | Should -BeNullOrEmpty
        @($r.cadAuto.shown) | Should -Be @('Task Manager')
        foreach ($n in $names) { $r.autoEnded.$n | Should -BeNullOrEmpty -Because $n }
        @($r.autoEnded.records) | Should -BeNullOrEmpty -Because "an unloaded hive reads as no values - the records say whether they were put back (hive loaded: $($r.autoEnded.loaded))"
    }
}
