# B-BRK - the RemoteHub Broker on a device: the agent MSI with BROKER_URL / BROKER_SECRET_FILE / BROKER_ONLY on a device
# without an enrollment file (it enrolls itself and registers), RemoteHub's way to it through the broker (Connect with
# the console secret, the sealed record, the agent's health through a relayed session pinned to that record), the
# device's policy BrokerEnabled, broker leave and broker join with the secret on standard input (what Assign to
# RemoteHub Broker runs), the direct path (QUIC on a punched port through the device firewall, relayed with the policy
# BrokerDirect = 0 or with UDP blocked), the addresses the agent reports and RemoteHub trying them before the broker
# path (B-BRK-09 to 11), a broker connection that silently stops carrying anything given up and made again (B-BRK-08),
# and that the device secret is kept nowhere on the device (SEC-BRK).
# Needs the lab's broker: "Broker" and "BrokerToken" in lab.local.json (docs/test-lab-spec.md); without them
# every case is skipped with that reason. Each run makes its own organisation and deletes it again.
# See docs/test-lab-cases.md section 9.14.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "b-brk-$name.json") -Encoding UTF8 }
    $broker = Get-LabBroker -Name $VmName
    $noBroker = 'the lab has no RemoteHub Broker (lab.local.json Broker and BrokerToken)'
    $org = $null
    if ($broker) {
        Reset-LabVm -Name $VmName
        Install-LabSigningCertificate -Name $VmName
        Copy-LabFile -Name $VmName -Path (Get-LabAgentMsi) -Destination 'C:\LabBroker'
        $org = New-LabBrokerOrg -Broker $broker
        $computer = Invoke-LabGuest -Name $VmName -ScriptBlock { $env:COMPUTERNAME }
        # msiexec as PSAdmin with the broker's properties; the secret is a test value of this run's organisation and goes
        # in a file (BROKER_SECRET_FILE), so no process's command line carries it - checked while the install runs
        $install = Invoke-LabGuest -Name $VmName -ArgumentList $broker.Url, $org.deviceSecret -ScriptBlock {
            param($url, $secret)
            $msi = (Get-ChildItem 'C:\LabBroker' -Filter *.msi | Select-Object -First 1).FullName
            $log = 'C:\LabBroker\install.log'
            $file = 'C:\LabBroker\device-secret.txt'
            Set-Content -Path $file -Value $secret -Encoding ASCII
            $key = $secret.Split('.')[-1]
            $a = @('/i', "`"$msi`"", '/qn', '/norestart', '/l*v', "`"$log`"", "BROKER_URL=$url", "BROKER_SECRET_FILE=$file", 'BROKER_ONLY=1')
            $p = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList $a -PassThru -WindowStyle Hidden
            $seen = @{}
            $deadline = (Get-Date).AddMinutes(10)
            while (-not $p.HasExited -and (Get-Date) -lt $deadline) {
                foreach ($proc in @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)) {
                    if ($proc.CommandLine -and $proc.CommandLine.Contains($key)) { $seen[$proc.Name] = $true }
                }
                Start-Sleep -Milliseconds 200
            }
            if (-not $p.HasExited) { throw 'the agent MSI did not finish within 10 minutes' }
            [pscustomobject]@{
                ExitCode          = $p.ExitCode
                LogHasSecret      = [bool](Select-String -Path $log -SimpleMatch $key -Quiet -ErrorAction SilentlyContinue)
                CommandLinesWith  = @($seen.Keys)
                SecretFileLeft    = Test-Path $file
                Firewall          = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'RemoteHub Agent*' }).Count
            }
        }
        # what the device keeps: agent.json's broker entry, and whether the secret is anywhere in it, the agent's logs or
        # the Application log
        function Get-Kept {
            Invoke-LabGuest -Name $VmName -ArgumentList $org.deviceSecret -ScriptBlock {
                param($secret)
                $f = 'C:\ProgramData\RemoteHub\Agent\agent.json'
                $json = if (Test-Path $f) { Get-Content $f -Raw } else { '' }
                $logs = @(Get-ChildItem 'C:\ProgramData\RemoteHub\Agent\logs' -Filter 'agent-*.log' -ErrorAction SilentlyContinue | Get-Content -ErrorAction SilentlyContinue)
                $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'RemoteHub Agent' } -MaxEvents 500 -ErrorAction SilentlyContinue)
                $key = $secret.Split('.')[-1]
                [pscustomobject]@{
                    Broker         = $(if ($json) { ($json | ConvertFrom-Json).broker } else { $null })
                    JsonHasSecret  = $json.Contains($key)
                    LogsHaveSecret = [bool]($logs | Where-Object { $_.Contains($key) })
                    EventsHaveKey  = [bool]($events | Where-Object { $_.Message -and $_.Message.Contains($key) })
                    EventIds       = @($events | ForEach-Object Id | Sort-Object -Unique)
                }
            }
        }
        # the newest BrokerSessionStarted (1301) in the Application log: its message names the path
        function Get-StartedSession {
            Invoke-LabGuest -Name $VmName -ScriptBlock {
                Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'RemoteHub Agent'; Id = 1301 } -MaxEvents 1 -ErrorAction SilentlyContinue |
                    Select-Object TimeCreated, Message
            }
        }
        # the agent's own command on the device (as PSAdmin): output and exit code; -Secret goes to standard input.
        # $Arguments is already one element of the list: wrapped once more, the agent got "broker leave" as one argument.
        function Invoke-AgentExe([string[]]$Arguments, [string]$Secret) {
            Invoke-LabGuest -Name $VmName -ArgumentList $Arguments, $Secret -ScriptBlock {
                param($argv, $secret)
                $psi = New-Object Diagnostics.ProcessStartInfo (Join-Path $env:ProgramFiles 'RemoteHub Agent\RemoteHubAgent.exe')
                $psi.Arguments = ($argv | ForEach-Object { "`"$_`"" }) -join ' '
                $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
                $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
                $p = [Diagnostics.Process]::Start($psi)
                if ($secret) { $p.StandardInput.WriteLine($secret) }
                $p.StandardInput.Close()
                $out = $p.StandardOutput.ReadToEnd() + $p.StandardError.ReadToEnd()
                $p.WaitForExit()
                [pscustomobject]@{ ExitCode = $p.ExitCode; Output = $out.Trim() }
            }
        }
    }
}

AfterAll {
    if ($broker -and $org) { try { Remove-LabBrokerOrg -Broker $broker -Org $org.org } catch { Write-Warning "The lab organisation $($org.org) wasn't deleted: $($_.Exception.Message)" } }
    if ($broker) {
        Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'b-brk-agent-logs')
        # installed by hand, joined, policy set: the next file starts from the checkpoint
        Set-LabVmDirty -Name $VmName
    }
}

Describe 'B-BRK <VmName>' -Tag Agent, Broker, 'B-BRK' {


    It 'B-BRK-01 the MSI with BROKER_URL and BROKER_SECRET_FILE enrolls the device, registers it and brings it online' -Tag 'B-BRK-01' {
        if (-not $broker) { Set-ItResult -Skipped -Because $noBroker; return }
        Save 'install' $install
        $install.ExitCode | Should -BeIn 0, 3010
        $install.Firewall | Should -Be 0 -Because 'BROKER_ONLY=1 leaves the inbound rule out'
        $script:device = Wait-LabBrokerDevice -Broker $broker -Org $org.org -ComputerName $computer
        $script:device.secretId | Should -Be $org.deviceSecretId
        $script:device.record.fingerprint | Should -Match '^[0-9a-f]{64}$'
        $kept = Get-Kept
        Save 'kept' ($kept | Select-Object Broker, EventIds)
        $kept.Broker.org | Should -Be $org.org
        $kept.EventIds | Should -Contain 1303 -Because 'BrokerRegistered'
        $kept.EventIds | Should -Contain 1300 -Because 'BrokerConnected'
    }

    It 'B-BRK-02 RemoteHub reaches the agent through the broker, pinned to the sealed record' -Tag 'B-BRK-02' {
        if (-not $broker) { Set-ItResult -Skipped -Because $noBroker; return }
        $script:device | Should -Not -BeNullOrEmpty -Because 'B-BRK-01 registered the device'
        $r = Test-LabBrokerHealth -Broker $broker -OrgInfo $org -DeviceId $script:device.id
        Save 'health' ($r | Select-Object joined, connected, isSealed, online, pin, health)
        $r.isSealed | Should -BeTrue
        $r.pin | Should -Be $script:device.record.fingerprint
        $r.health.deviceId | Should -Be $script:device.id
        (Get-Kept).EventIds | Should -Contain 1301 -Because 'BrokerSessionStarted'
    }

    It 'B-BRK-05 a session on the lab network goes direct (QUIC on a punched port) with the firewall on' -Tag 'B-BRK-05' {
        if (-not $broker) { Set-ItResult -Skipped -Because $noBroker; return }
        $script:device | Should -Not -BeNullOrEmpty -Because 'B-BRK-01 registered the device'
        $fw = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-NetFirewallProfile | Where-Object Enabled).Count }
        $fw | Should -BeGreaterThan 0 -Because 'the case is about the device firewall letting the direct path in'
        $r = Test-LabBrokerHealth -Broker $broker -OrgInfo $org -DeviceId $script:device.id
        Save 'direct' ($r | Select-Object path, seconds, pin, health)
        $r.health.deviceId | Should -Be $script:device.id
        $r.path | Should -Be 'DirectLocal' -Because 'host candidates on the lab network answer and the agent opens the punched port for the console'
        (Get-StartedSession).Message | Should -Match 'direct local'
        # the rule for the try is gone again once the session has ended (the harness closes it when it is done)
        $left = Invoke-LabGuest -Name $VmName -ScriptBlock {
            $until = (Get-Date).AddSeconds(30)
            do {
                $n = @(Get-NetFirewallRule -DisplayName 'RemoteHub Agent direct path' -ErrorAction SilentlyContinue).Count
                if ($n -eq 0) { break }
                Start-Sleep -Milliseconds 500
            } while ((Get-Date) -lt $until)
            $n
        }
        $left | Should -Be 0 -Because 'the direct path rule is removed within 30 s of the session ending'
    }

    It 'B-BRK-06 the device policy BrokerDirect = 0 keeps sessions relayed' -Tag 'B-BRK-06' {
        if (-not $broker) { Set-ItResult -Skipped -Because $noBroker; return }
        Set-LabAgentPolicy -Name $VmName -Values @{ BrokerDirect = 0 }
        try {
            $r = Test-LabBrokerHealth -Broker $broker -OrgInfo $org -DeviceId $script:device.id
            Save 'relay-only' ($r | Select-Object path, seconds)
            $r.health.deviceId | Should -Be $script:device.id
            $r.path | Should -Be 'Relayed'
            (Get-StartedSession).Message | Should -Match 'relayed'
        }
        finally { Set-LabAgentPolicy -Name $VmName -Clear }
    }

    It 'B-BRK-07 UDP blocked on the device: the session is relayed, without an error and without waiting long' -Tag 'B-BRK-07' {
        if (-not $broker) { Set-ItResult -Skipped -Because $noBroker; return }
        Invoke-LabGuest -Name $VmName -ScriptBlock {
            $exe = Join-Path $env:ProgramFiles 'RemoteHub Agent\RemoteHubAgent.exe'
            foreach ($d in 'Inbound', 'Outbound') {
                $null = New-NetFirewallRule -DisplayName "LabBlockAgentUdp$d" -Direction $d -Protocol UDP -Action Block -Program $exe
            }
        }
        try {
            $r = Test-LabBrokerHealth -Broker $broker -OrgInfo $org -DeviceId $script:device.id
            Save 'udp-blocked' ($r | Select-Object path, seconds)
            $r.health.deviceId | Should -Be $script:device.id
            $r.path | Should -Be 'Relayed'
            $r.seconds | Should -BeLessThan 10 -Because 'the relay starts at the same time as the punching'
        }
        finally {
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-NetFirewallRule -DisplayName 'LabBlockAgentUdp*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule }
        }
    }

    It 'B-BRK-09 a broker-only device (no inbound rule) reports its addresses and goes straight to the broker path' -Tag 'B-BRK-09' {
        if (-not $broker) { Set-ItResult -Skipped -Because $noBroker; return }
        $script:device | Should -Not -BeNullOrEmpty -Because 'B-BRK-01 registered the device'
        $own = @(Invoke-LabGuest -Name $VmName -ScriptBlock { Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } | ForEach-Object IPAddress })
        $r = Test-LabBrokerHealth -Broker $broker -OrgInfo $org -DeviceId $script:device.id -DirectFirst
        Save 'reach-broker-only' ($r | Select-Object path, seconds, addresses, inbound)
        $r.health.deviceId | Should -Be $script:device.id
        $r.inbound | Should -BeFalse -Because 'BROKER_ONLY=1 left the inbound rule out, and the agent says so'
        @($r.addresses | Where-Object { $_ -in $own }).Count | Should -BeGreaterThan 0 -Because "the agent reports the device's own addresses ($($own -join ', ')) - reported: $(@($r.addresses) -join ', ')"
        $r.path | Should -BeIn 'DirectLocal', 'Relayed' -Because 'nothing takes a direct connection, so the broker path'
    }

    It 'B-BRK-10 with the agent inbound rule, RemoteHub reaches the device at its own address and the broker carries nothing' -Tag 'B-BRK-10' {
        if (-not $broker) { Set-ItResult -Skipped -Because $noBroker; return }
        $script:device | Should -Not -BeNullOrEmpty -Because 'B-BRK-01 registered the device'
        # the rule an install without BROKER_ONLY makes; the agent tells the broker so when it connects again
        Invoke-LabGuest -Name $VmName -ScriptBlock {
            $exe = Join-Path $env:ProgramFiles 'RemoteHub Agent\RemoteHubAgent.exe'
            $null = New-NetFirewallRule -DisplayName 'RemoteHub Agent' -Direction Inbound -Protocol TCP -LocalPort 58732 -Program $exe -Action Allow
            Restart-Service RemoteHubAgent
        }
        try {
            $null = Wait-LabBrokerDevice -Broker $broker -Org $org.org -ComputerName $computer
            $before = (Get-StartedSession).TimeCreated
            $r = Test-LabBrokerHealth -Broker $broker -OrgInfo $org -DeviceId $script:device.id -DirectFirst
            Save 'reach-network' ($r | Select-Object path, directHost, name, addresses, inbound, seconds)
            $r.health.deviceId | Should -Be $script:device.id
            $r.inbound | Should -BeTrue
            $r.path | Should -Be 'Network' -Because "the device answers at its name or a reported address (reported: $(@($r.addresses) -join ', '))"
            $r.directHost | Should -BeIn (@($r.name) + @($r.addresses))
            (Get-StartedSession).TimeCreated | Should -Be $before -Because 'no session through the broker started (nothing relayed, no punching)'
        }
        finally {
            Invoke-LabGuest -Name $VmName -ScriptBlock {
                Get-NetFirewallRule -DisplayName 'RemoteHub Agent' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
                Restart-Service RemoteHubAgent
            }
            $null = Wait-LabBrokerDevice -Broker $broker -Org $org.org -ComputerName $computer
        }
    }

    It 'B-BRK-11 an address the device gets is at the broker within seconds, and gone again when it goes' -Tag 'B-BRK-11' {
        if (-not $broker) { Set-ItResult -Skipped -Because $noBroker; return }
        $script:device | Should -Not -BeNullOrEmpty -Because 'B-BRK-01 registered the device'
        # a second address on the device's adapter, in a private range the lab doesn't use: a network change as a new
        # DHCP lease or a VPN would bring (the agent tells the broker once the addresses have settled for 2 s)
        $extra = "10.250.$(Get-Random -Minimum 1 -Maximum 254).$(Get-Random -Minimum 1 -Maximum 254)"
        Invoke-LabGuest -Name $VmName -ArgumentList $extra -ScriptBlock {
            param($ip)
            $if = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } | Select-Object -First 1).InterfaceIndex
            $null = New-NetIPAddress -InterfaceIndex $if -IPAddress $ip -PrefixLength 24 -SkipAsSource $true
        }
        function Reported {
            (Test-LabBrokerHealth -Broker $broker -OrgInfo $org -DeviceId $script:device.id -DirectFirst).addresses
        }
        try {
            $sw = [Diagnostics.Stopwatch]::StartNew()
            do { $now = @(Reported); if ($now -contains $extra) { break }; Start-Sleep -Seconds 3 } while ($sw.Elapsed.TotalSeconds -lt 60)
            $added = [int]$sw.Elapsed.TotalSeconds
            $now | Should -Contain $extra -Because "the agent reports a new address (after $added s: $($now -join ', '))"
            $added | Should -BeLessThan 45 -Because 'reported once the network settled, not with the next reconnect'
        }
        finally {
            Invoke-LabGuest -Name $VmName -ArgumentList $extra -ScriptBlock { param($ip) Remove-NetIPAddress -IPAddress $ip -Confirm:$false -ErrorAction SilentlyContinue }
        }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        do { $now = @(Reported); if ($now -notcontains $extra) { break }; Start-Sleep -Seconds 3 } while ($sw.Elapsed.TotalSeconds -lt 60)
        $now | Should -Not -Contain $extra -Because 'an address the device no longer has is taken off'
    }

    It 'B-BRK-03 the device policy BrokerEnabled = 0 takes it off the broker, and back when cleared' -Tag 'B-BRK-03' {
        if (-not $broker) { Set-ItResult -Skipped -Because $noBroker; return }
        Set-LabAgentPolicy -Name $VmName -Values @{ BrokerEnabled = 0 }
        # cleared also when the device doesn't go offline: the cases after this one need the broker
        try { $null = Wait-LabBrokerDevice -Broker $broker -Org $org.org -ComputerName $computer -Offline }
        finally { Set-LabAgentPolicy -Name $VmName -Clear }
        $null = Wait-LabBrokerDevice -Broker $broker -Org $org.org -ComputerName $computer
    }

    It 'B-BRK-04 broker leave takes it off; broker join with the secret on standard input brings it back' -Tag 'B-BRK-04' {
        if (-not $broker) { Set-ItResult -Skipped -Because $noBroker; return }
        $leave = Invoke-AgentExe @('broker', 'leave')
        $leave.ExitCode | Should -Be 0
        $leave.Output | Should -Match 'Left the RemoteHub Broker'
        $null = Wait-LabBrokerDevice -Broker $broker -Org $org.org -ComputerName $computer -Offline
        (Invoke-AgentExe @('broker', 'status')).Output | Should -Be 'No RemoteHub Broker.'
        $join = Invoke-AgentExe @('broker', 'join', $broker.Url) -Secret $org.deviceSecret
        $join.ExitCode | Should -Be 0
        $join.Output | Should -Match 'Joined the RemoteHub Broker'
        $null = Wait-LabBrokerDevice -Broker $broker -Org $org.org -ComputerName $computer
        $wrong = Invoke-AgentExe @('broker', 'join', $broker.Url) -Secret $org.consoleSecret
        $wrong.ExitCode | Should -Be 1
        $wrong.Output | Should -Match 'console secret'
    }

    It 'B-BRK-08 a broker connection that silently stops carrying anything is given up and made again' -Tag 'B-BRK-08' {
        if (-not $broker) { Set-ItResult -Skipped -Because $noBroker; return }
        $script:device | Should -Not -BeNullOrEmpty -Because 'B-BRK-01 registered the device'
        # a 30 s keep-alive (the policy's minimum): given up after 75 s without a frame (two intervals and 15 s)
        Set-LabAgentPolicy -Name $VmName -Values @{ BrokerKeepAliveSeconds = 30 }
        try {
            Invoke-LabGuest -Name $VmName -ScriptBlock { Restart-Service RemoteHubAgent }
            $null = Wait-LabBrokerDevice -Broker $broker -Org $org.org -ComputerName $computer
            # The connection dies without a close and without a network change: the next hop's address answers to a MAC
            # nobody has, so the device's frames go nowhere (PowerShell Direct runs over VMBus and keeps working).
            $cut = Invoke-LabGuest -Name $VmName -ArgumentList $broker.Url -ScriptBlock {
                param($url)
                $agent = (Get-CimInstance Win32_Service -Filter "Name='RemoteHubAgent'").ProcessId
                $conn = Get-NetTCPConnection -OwningProcess $agent -State Established -RemotePort ([Uri]$url).Port -ErrorAction SilentlyContinue | Select-Object -First 1
                if (-not $conn) { return [pscustomobject]@{ error = 'the agent has no connection to the broker' } }
                $route = Find-NetRoute -RemoteIPAddress $conn.RemoteAddress | Where-Object { $_.PSObject.Properties['NextHop'] } | Select-Object -First 1
                $hop = if ($route.NextHop -and $route.NextHop -notin '0.0.0.0', '::') { $route.NextHop } else { $conn.RemoteAddress }
                $log = Join-Path 'C:\ProgramData\RemoteHub\Agent\logs' "agent-$(Get-Date -Format yyyyMMdd).log"
                $lines = @(Get-Content $log -ErrorAction SilentlyContinue).Count
                Remove-NetNeighbor -IPAddress $hop -InterfaceIndex $route.InterfaceIndex -Confirm:$false -ErrorAction SilentlyContinue
                $null = New-NetNeighbor -IPAddress $hop -InterfaceIndex $route.InterfaceIndex -LinkLayerAddress '02-00-5e-10-00-01' -State Permanent
                [pscustomobject]@{ hop = "$hop"; ifIndex = $route.InterfaceIndex; log = $log; lines = $lines; remote = "$($conn.RemoteAddress):$($conn.RemotePort)" }
            }
            $cut.PSObject.Properties['error'] | Should -BeNullOrEmpty -Because "$(if ($cut.PSObject.Properties['error']) { $cut.error })"
            # the agent's log: the line saying it gave the connection up (an en dash in it: matched with '.')
            $gaveUp = Invoke-LabGuest -Name $VmName -ArgumentList $cut.log, $cut.lines -ScriptBlock {
                param($log, $skip)
                $sw = [Diagnostics.Stopwatch]::StartNew()
                do {
                    $line = @(Get-Content $log -ErrorAction SilentlyContinue | Select-Object -Skip $skip | Where-Object { $_ -match 'the connection was given up . nothing from the broker for' })[0]
                    if ($line) { break }
                    Start-Sleep -Seconds 2
                } while ($sw.Elapsed.TotalSeconds -lt 150)
                [pscustomobject]@{ line = $line; seconds = [int]$sw.Elapsed.TotalSeconds }
            }
            # the way back: the neighbour entry removed, the agent connects again and is reachable through the broker
            Invoke-LabGuest -Name $VmName -ArgumentList $cut.hop, $cut.ifIndex -ScriptBlock {
                param($hop, $if)
                Remove-NetNeighbor -IPAddress $hop -InterfaceIndex $if -Confirm:$false -ErrorAction SilentlyContinue
            }
            # the attempts while cut off grew the back-off (at most 60 s and as much jitter)
            $null = Wait-LabBrokerDevice -Broker $broker -Org $org.org -ComputerName $computer -TimeoutSec 240
            $again = Invoke-LabGuest -Name $VmName -ArgumentList $cut.log, $cut.lines -ScriptBlock {
                param($log, $skip)
                $sw = [Diagnostics.Stopwatch]::StartNew()
                do {
                    $line = @(Get-Content $log -ErrorAction SilentlyContinue | Select-Object -Skip $skip | Where-Object { $_ -match 'RemoteHub Broker .*: connected again' })[0]
                    if ($line) { break }
                    Start-Sleep -Seconds 2
                } while ($sw.Elapsed.TotalSeconds -lt 180)
                $line
            }
            $r = Test-LabBrokerHealth -Broker $broker -OrgInfo $org -DeviceId $script:device.id
            Save 'silent-link' ([pscustomobject]@{ cut = $cut; gaveUp = $gaveUp; again = $again; health = $r.health })
            $gaveUp.line | Should -Not -BeNullOrEmpty -Because "the agent gives a connection up that carries nothing (waited $($gaveUp.seconds) s)"
            $gaveUp.seconds | Should -BeLessThan 110 -Because 'two 30 s intervals and 15 s, read every 2 s'
            $again | Should -Not -BeNullOrEmpty -Because 'the agent logs that it is connected again'
            $r.health.deviceId | Should -Be $script:device.id
        }
        finally {
            if ($cut -and $cut.PSObject.Properties['hop']) {
                Invoke-LabGuest -Name $VmName -ArgumentList $cut.hop, $cut.ifIndex -ScriptBlock { param($hop, $if) Remove-NetNeighbor -IPAddress $hop -InterfaceIndex $if -Confirm:$false -ErrorAction SilentlyContinue }
            }
            Set-LabAgentPolicy -Name $VmName -Clear
        }
    }

    It 'SEC-BRK-01 the device secret is kept nowhere on the device' -Tag 'SEC-BRK-01' {
        if (-not $broker) { Set-ItResult -Skipped -Because $noBroker; return }
        $install.LogHasSecret | Should -BeFalse -Because 'the installer log names only the secret file'
        @($install.CommandLinesWith) | Should -BeNullOrEmpty -Because "BROKER_SECRET_FILE keeps the secret off every command line - seen in: $(@($install.CommandLinesWith) -join ', ')"
        $install.SecretFileLeft | Should -BeFalse -Because 'the agent deletes the secret file once the device has registered'
        $kept = Get-Kept
        $kept.JsonHasSecret | Should -BeFalse -Because 'agent.json holds only the keys derived from it'
        $kept.Broker.enrollKey | Should -Not -BeNullOrEmpty
        $kept.Broker.recordKey | Should -Not -BeNullOrEmpty
        $kept.LogsHaveSecret | Should -BeFalse
        $kept.EventsHaveKey | Should -BeFalse
        (Invoke-AgentExe @('broker', 'status')).Output | Should -Not -Match 'rhd1\.'
    }
}
