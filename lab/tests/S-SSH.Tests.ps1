# S-SSH - SSH connections and private keys (docs/test-lab-cases.md section 7.1a, docs/network-shares.md section 6).
# Covers: S-SSH-01, S-SSH-02, S-SSH-03, S-SSH-04, S-SSH-05, S-SSH-06, S-SSH-07.
# Two parts, each for its own VM of the run:
#   - on the Server VM: RemoteHub's own SSH code from this PC through the lab harness ('ssh': the SSH tab's SshSession,
#     SshShell and terminal emulator; 'share' for SFTP with a key) against the guest's OpenSSH on port 2222
#     (Enable-LabShareServers): password and key sign-in, the host key question, a command and its output, the
#     terminal's size, exit, and nothing identifying in this PC's diagnostic log;
#   - on a client VM: RemoteHub's own window (UI Automation in the console account's session) with the Server VM as
#     the device: New > New connection > SSH, the editor, the trust dialog, and a command typed into the terminal tab,
#     its output read through the terminal's UI Automation value.
# Keys are made for the run on this PC (New-LabSshKey, a temporary folder removed at the end - never the evidence) and
# authorized for the device's administrators (Set-LabSshAuthorizedKeys).
# Needs: the Server VM (both parts), a client VM (UI part), Windows' OpenSSH client on the lab host (ssh-keygen).
# Runs on every kind: the harness part on a Server VM, the UI part on the admin PC (Test-LabConsoleVm) with its lane's
# Server VM (Get-LabDevice -For); the part that isn't for this VM reports each of its cases as skipped with the reason.
# LabRole: any
# LabQuick: every kind
# LabNeeds: lane
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $password = (Get-LabConfig).Password
    $isServer = $vm.Os -eq 'Server'
    $isClient = Test-LabConsoleVm -Name $VmName
    $device = if ($isClient) { Get-LabDevice -For $VmName } else { $null }
    $script:task = $null
    $notServer = "the harness part runs on the run's Server VM (against its own OpenSSH); $VmName is not a Server VM"
    $notClient = "the UI part runs on the admin PC (a Console VM, or a client Windows VM in a lab without one) with its lane's Server VM as the device; $VmName is not that PC"
    # a throwaway passphrase for a throwaway key, made for the run - not the lab password
    $phrase = "rh-$([guid]::NewGuid().ToString('N'))"
    $keyDir = Join-Path ([IO.Path]::GetTempPath()) "rh-lab-ssh-$([guid]::NewGuid().ToString('N').Substring(0, 8))"

    function Save($name, $data) { $data | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $EvidenceDir "s-ssh-$name.json") -Encoding UTF8 }
    # Where the file is, line by line as it goes: on Server it ran into its 60 min limit after S-SSH-05 in every run
    # since 20261030-004321, and the console log (written in blocks) stops at the context's name, so it doesn't say where
    function Mark([string]$What) { Add-Content -LiteralPath (Join-Path $EvidenceDir 's-ssh-progress.txt') -Value "$((Get-Date).ToString('HH:mm:ss')) $What" -Encoding UTF8 }
    function Shot([string]$name) { try { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "s-ssh-$name.png") } catch { } }
    # harness commands without a module wrapper (Invoke-LabHarness is internal to the module, so it is called in its scope)
    function Harness([string[]]$Arguments, [int]$TimeoutSec = 300) {
        & (Get-Module RemoteHub.TestLab) { param($a, $t) Invoke-LabHarness $a -TimeoutSec $t } $Arguments $TimeoutSec
    }
    # the harness' ssh scenario against the device's OpenSSH; -Arguments add to the address and port
    function Ssh([string[]]$Arguments) { Harness (@('ssh', '--host', $ip, '--port', '2222') + $Arguments) }
    # a line of the screen that is exactly the guest's computer name: what "hostname" printed (the typed command is on
    # the prompt's line, so only the output matches)
    function NameLine([string]$Computer) { "(?im)^$([regex]::Escape($Computer))\s*$" }

    # ------------------------------------------------------------------ RemoteHub's window (UI part)

    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    function Menu([string]$Entry, [string]$Node, [int]$TimeoutSec = 30) {
        $m = Ui $Node -Value "menu:$Entry" -TimeoutSec $TimeoutSec
        if ($m.PSObject.Properties['error'] -and $m.error) { throw "menu ${Node}: $($m.error) - entries: $(@($m.entries) -join ', ')" }
        $m
    }
    # the terminal's screen as UI Automation reads it, waited for until it matches -Pattern; throws with what it showed
    function Wait-Terminal([string]$Pattern, [string]$What, [int]$TimeoutSec = 60) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        $text = ''
        do {
            $r = Ui 'Terminal' -Value read -Optional -TimeoutSec 5
            if ($r.found) { $text = "$($r.value)" }
            if ($text -match $Pattern) { return $text }
            Start-Sleep -Milliseconds 500
        } while ((Get-Date) -lt $until)
        throw "the terminal didn't show $What within $TimeoutSec s; it showed: $text"
    }

    if ($isServer) {
        Reset-LabVm -Name $VmName
        $servers = Enable-LabShareServers -Name $VmName
        $ip = Get-LabVmAddress -Name $VmName
        $admin = $vm.AdminAccount
        $computer = "$(Invoke-LabGuest -Name $VmName -ScriptBlock { $env:COMPUTERNAME })".Trim()
        $key = New-LabSshKey -Folder $keyDir -Passphrase $phrase
        $unknownKey = New-LabSshKey -Folder $keyDir
        $authorized = Set-LabSshAuthorizedKeys -Name $VmName -PublicKey $key.PublicKey
        Save 'servers' @{ Servers = $servers; Authorized = $authorized; KeyFingerprint = $key.Fingerprint }
        $logSince = Get-Date
    }
    elseif ($isClient) {
        Reset-LabVm -Name $device.Name
        $null = Enable-LabShareServers -Name $device.Name
        $deviceIp = Get-LabVmAddress -Name $device.Name
        $devAdmin = $device.AdminAccount
        $deviceComputer = "$(Invoke-LabGuest -Name $device.Name -ScriptBlock { $env:COMPUTERNAME })".Trim()
        $init = Initialize-LabVm -Name $VmName -Client
        $script:task = Start-LabClient -Name $VmName
    }
}

AfterAll {
    Mark 'AfterAll'
    if (Test-Path $keyDir) { Remove-Item -LiteralPath $keyDir -Recurse -Force -ErrorAction SilentlyContinue }
    if ($isClient) {
        Shot 'end'
        try { Stop-LabClient -Name $VmName -Task $script:task } catch { }
        Stop-LabVm -Name $device.Name
        Set-LabVmDirty -Name $device.Name
    }
    # OpenSSH, its authorized keys and the share servers set up in the guest
    if ($isServer -or $isClient) { Set-LabVmDirty -Name $VmName }
    Mark 'AfterAll done'
}

Describe 'S-SSH harness <VmName>' -Tag Share, Ssh, 'S-SSH' {

    Context 'password sign-in and the terminal' {
        BeforeAll {
            if (-not $isServer) { return }
            $script:first = Ssh @('--user', $admin, '--password', $password, '--accept', '1', '--columns', '90', '--rows', '25',
                '--command', 'hostname', '--expect', (NameLine $computer),
                '--resize', '100x30', '--resize-command', 'mode con', '--resize-expect', 'Columns:\s+100\b')
            Save 'first' $script:first
        }

        It 'S-SSH-01 signs in with the password, asks about the host key once, runs a command and ends with "The session ended."' -Tag 'S-SSH-01' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            $r = $script:first
            $r.connected | Should -Match '^Connected . SSH . ' -Because "the tab's status: $($r.connected)"
            $r.transport | Should -Match '^SSH . OpenSSH'
            @($r.asked).Count | Should -Be 1 -Because 'the host key is asked about the first time'
            @($r.asked)[0].what | Should -Be 'SSH host key'
            $r.accepted | Should -Match '^SHA256:'
            $r.prompt | Should -BeTrue -Because 'the shell showed its prompt'
            $r.found | Should -BeTrue -Because "'hostname' printed the device's name; the screen: $($r.screen)"
            $r.ended | Should -BeTrue -Because "'exit' ends the session; the screen: $($r.screen)"
            $r.endText | Should -Be 'The session ended.'
        }

        It 'S-SSH-03 the shell gets the tab''s size, and the new one after a resize ("mode con" shows 100 columns)' -Tag 'S-SSH-03' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            $script:first.resized | Should -BeTrue -Because "after the resize to 100x30; the screen: $($script:first.screen)"
            $script:first.columns | Should -Be 100
            $script:first.rows | Should -Be 30
        }

        It 'S-SSH-02 an accepted host key isn''t asked about again; one the user refuses stops before anything is sent' -Tag 'S-SSH-02' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            $script:first.accepted | Should -Not -BeNullOrEmpty -Because 'S-SSH-01 accepted the key'
            $r = [ordered]@{}
            $r.known = Ssh @('--user', $admin, '--password', $password, '--trusted', $script:first.accepted, '--command', 'hostname', '--expect', (NameLine $computer))
            $r.refused = Ssh @('--user', $admin, '--password', $password, '--accept', '0')
            $r.changed = Ssh @('--user', $admin, '--password', $password, '--trusted', 'SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA', '--accept', '0')
            Save 'host-key' $r
            $r.known.ok | Should -BeTrue -Because "the screen: $($r.known.screen)"
            @($r.known.asked).Count | Should -Be 0
            $r.refused.ok | Should -BeFalse
            $r.refused.connected | Should -Match 'The server.s key wasn.t accepted . nothing was sent\.'
            @($r.changed.asked)[0].what | Should -Be 'SSH host key (changed!)'
            $r.changed.connected | Should -Match 'The server.s key wasn.t accepted . nothing was sent\.'
        }
    }

    Context 'private keys' {
        It 'S-SSH-04 a private key signs in without a password; a wrong passphrase or a key the server doesn''t know says so' -Tag 'S-SSH-04' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            $script:first.accepted | Should -Not -BeNullOrEmpty -Because 'S-SSH-01 accepted the host key'
            $r = [ordered]@{}
            $r.keyOnly = Ssh @('--user', $admin, '--key', $key.Path, '--passphrase', $phrase, '--trusted', $script:first.accepted,
                '--command', 'hostname', '--expect', (NameLine $computer))
            $r.wrongPassphrase = Ssh @('--user', $admin, '--key', $key.Path, '--passphrase', "wrong-$([guid]::NewGuid().ToString('N'))", '--trusted', $script:first.accepted)
            $r.noPassphrase = Ssh @('--user', $admin, '--key', $key.Path, '--trusted', $script:first.accepted)
            $r.unknownKey = Ssh @('--user', $admin, '--key', $unknownKey.Path, '--trusted', $script:first.accepted)
            $r.unknownKeyAndPassword = Ssh @('--user', $admin, '--key', $unknownKey.Path, '--password', $password, '--trusted', $script:first.accepted,
                '--command', 'hostname', '--expect', (NameLine $computer))
            Save 'keys' $r
            $r.keyOnly.ok | Should -BeTrue -Because "the key is in administrators_authorized_keys; status: $($r.keyOnly.connected); screen: $($r.keyOnly.screen)"
            $r.wrongPassphrase.connected | Should -Match '^The passphrase is wrong'
            $r.noPassphrase.connected | Should -Match 'protected by a passphrase'
            $r.unknownKey.connected | Should -Match 'The server didn.t accept the private key for this user \(is its public key in the user.s authorized_keys\?\)'
            # a key the server refuses, then the password: the key goes first, the password still signs in
            $r.unknownKeyAndPassword.ok | Should -BeTrue -Because "status: $($r.unknownKeyAndPassword.connected)"
        }

        It 'S-SSH-05 SFTP signs in with the private key alone and copies both ways' -Tag 'S-SSH-05' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            $r = Harness @('share', '--protocol', 'sftp', '--host', $ip, '--port', '2222', '--user', $admin, '--key', $key.Path, '--passphrase', $phrase,
                '--trusted', $script:first.accepted) -TimeoutSec 600
            Save 'sftp-key' $r
            $r.ok | Should -BeTrue -Because ((@($r.steps) | ForEach-Object { "$($_.step): $(if ($_.ok) { 'ok' } else { $_.error })" }) -join "`n")
            @($r.asked).Count | Should -Be 0 -Because 'SFTP and SSH meet the same server key'
        }
    }

    Context 'the diagnostic log' {
        It 'S-SSH-07 the SSH sessions are logged without the device, its address or the account' -Tag 'S-SSH-07' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            Mark 'S-SSH-07: reading the diagnostic log'
            $lines = @(Get-LabHostDiagLog -Since $logSince)
            Mark "S-SSH-07: $($lines.Count) lines read"
            Save 'diaglog' @{ Count = $lines.Count; Ssh = @($lines | Where-Object { $_ -match 'SSH' } | Select-Object -First 40) }
            @($lines | Where-Object { $_ -match 'SSH: connected' }).Count | Should -BeGreaterThan 0 -Because 'the sessions are logged'
            $user = ($admin -split '\\')[-1]
            foreach ($bad in @($ip, $computer, $user)) {
                @($lines | Where-Object { $_ -like "*$bad*" }) | Should -BeNullOrEmpty -Because "the log must not name '$bad'"
            }
        }
    }
}

Describe 'S-SSH UI <VmName>' -Tag Client, UI, Ssh, 'S-SSH' {

    Context 'New > New connection > SSH and the terminal tab' {
        BeforeAll {
            if (-not $isClient) { return }
            $script:u = [ordered]@{}
            try {
                $null = Ui 'NewButton' -Value 'pick:New connection>SSH'
                $t = 'New SSH connection'
                $script:u.editor = (Ui 'Name' -Title $t -Value exists -TimeoutSec 30).found
                $script:u.pathField = (Ui 'Start folder' -Title $t -Value exists -Optional -TimeoutSec 3).found
                $null = Ui 'Name' -Title $t -Value 'setnext:Lab SSH'
                $null = Ui 'Host' -Title $t -Value "setnext:$deviceIp"
                $null = Ui 'Port' -Title $t -Value 'setnext:2222'
                $null = Ui 'Enter an account for this connection' -Title $t -Value check
                $null = Ui 'Username' -Title $t -Value "setnext:$devAdmin"
                $null = Ui 'Password' -Title $t -Value "setnext:$password"
                $script:u.keyButton = (Ui 'Import key*' -Title $t -Value exists -Optional -TimeoutSec 5).found
                $null = Ui 'Add' -Title $t
                $script:u.inTree = (Ui 'Lab SSH' -Value exists -TimeoutSec 20).found
                $null = Menu 'Open' 'Lab SSH'
                # by the button in any window, as S-SHARE2 does: the dialog was on screen in run 20261030-194701 but not
                # found under the title 'Trust this server?'
                $trust = Ui 'Trust and connect' -Title '?*' -TimeoutSec 60 -Optional
                $script:u.trust = $trust.found
                $script:u.trustWindow = $trust.window
                $script:u.prompt = Wait-Terminal '>\s*$|\$\s*$' 'a prompt'
                $null = Ui 'Terminal' -Value 'keys:hostname{ENTER}'
                $script:u.output = Wait-Terminal (NameLine $deviceComputer) "the device's name"
                Shot 'terminal'
                # Ctrl+W is the shell's (delete a word): the tab stays open
                $null = Ui 'Terminal' -Value 'keys:^w'
                $script:u.tabAfterCtrlW = (Ui 'Terminal' -Value exists -Optional -TimeoutSec 5).found
                # Enter first: the device's shell is cmd, where Ctrl+W isn't "delete a word" but a character left on the
                # line - "exit" after it was no command (run 20261031-075755: the session never ended)
                $null = Ui 'Terminal' -Value 'keys:{ENTER}'
                $null = Wait-Terminal '>\s*$|\$\s*$' 'a prompt after Ctrl+W' 15
                $null = Ui 'Terminal' -Value 'keys:exit{ENTER}'
                $script:u.ended = (Ui 'The session ended.' -Value exists -Optional -TimeoutSec 30).found
            }
            catch {
                $script:u.error = "$_"
                Shot 'fail-ui'
                try { $script:u.dialogs = @((Invoke-LabUi -Name $VmName -Text '*' -Title '?*' -Value list -Optional -TimeoutSec 3).items | Select-Object -First 60) } catch { }
            }
            Save 'ui' $script:u
        }

        It 'S-SSH-06 New > New connection > SSH: the editor, the trust dialog, a command typed into the terminal and its output' -Tag 'S-SSH-06' {
            if (-not $isClient) { Set-ItResult -Skipped -Because $notClient; return }
            $script:u.error | Should -BeNullOrEmpty
            $script:u.editor | Should -BeTrue -Because 'the editor opens as "New SSH connection"'
            $script:u.pathField | Should -BeFalse -Because 'SSH has no start folder'
            $script:u.keyButton | Should -BeTrue -Because 'the account has Import key...'
            $script:u.inTree | Should -BeTrue
            $script:u.trust | Should -BeTrue -Because 'the host key is asked about the first time'
            $script:u.output | Should -Match (NameLine $deviceComputer)
            $script:u.tabAfterCtrlW | Should -BeTrue -Because 'Ctrl+W goes to the terminal, not to the window'
            $script:u.ended | Should -BeTrue -Because 'exit ends the session and the tab says so'
        }
    }
}
