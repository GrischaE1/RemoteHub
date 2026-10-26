# B-BRK - the RemoteHub Broker on a device: the agent MSI with BROKER_URL / BROKER_SECRET / BROKER_ONLY on a device
# without an enrollment file (it enrolls itself and registers), RemoteHub's way to it through the broker (Connect with
# the console secret, the sealed record, the agent's health through a relayed session pinned to that record), the
# device's policy BrokerEnabled, broker leave and broker join with the secret on standard input (what Assign to
# RemoteHub Broker runs), the direct path (QUIC on a punched port through the device firewall, relayed with the policy
# BrokerDirect = 0 or with UDP blocked), and that the device secret is kept nowhere on the device (SEC-BRK).
# Needs the lab's broker: lab.json "Broker" and lab.local.json "BrokerToken" (docs/test-lab-spec.md); without them
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
    $noBroker = 'the lab has no RemoteHub Broker (lab.json Broker, lab.local.json BrokerToken)'
    $org = $null
    if ($broker) {
        Reset-LabVm -Name $VmName
        Install-LabSigningCertificate -Name $VmName
        Copy-LabFile -Name $VmName -Path (Get-LabAgentMsi) -Destination 'C:\LabBroker'
        $org = New-LabBrokerOrg -Broker $broker
        $computer = Invoke-LabGuest -Name $VmName -ScriptBlock { $env:COMPUTERNAME }
        # msiexec as PSAdmin with the broker's properties; the secret is a test value of this run's organisation
        $install = Invoke-LabGuest -Name $VmName -ArgumentList $broker.Url, $org.deviceSecret -ScriptBlock {
            param($url, $secret)
            $msi = (Get-ChildItem 'C:\LabBroker' -Filter *.msi | Select-Object -First 1).FullName
            $log = 'C:\LabBroker\install.log'
            $a = @('/i', "`"$msi`"", '/qn', '/norestart', '/l*v', "`"$log`"", "BROKER_URL=$url", "BROKER_SECRET=$secret", 'BROKER_ONLY=1')
            $p = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList $a -Wait -PassThru -WindowStyle Hidden
            [pscustomobject]@{
                ExitCode     = $p.ExitCode
                LogHasSecret = [bool](Select-String -Path $log -SimpleMatch $secret -Quiet -ErrorAction SilentlyContinue)
                Firewall     = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'RemoteHub Agent*' }).Count
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
        # the agent's own command on the device (as PSAdmin): output and exit code; -Secret goes to standard input
        function Invoke-AgentExe([string[]]$Arguments, [string]$Secret) {
            Invoke-LabGuest -Name $VmName -ArgumentList (, $Arguments), $Secret -ScriptBlock {
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


    It 'B-BRK-01 the MSI with BROKER_URL and BROKER_SECRET enrolls the device, registers it and brings it online' -Tag 'B-BRK-01' {
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

    It 'B-BRK-03 the device policy BrokerEnabled = 0 takes it off the broker, and back when cleared' -Tag 'B-BRK-03' {
        if (-not $broker) { Set-ItResult -Skipped -Because $noBroker; return }
        Set-LabAgentPolicy -Name $VmName -Values @{ BrokerEnabled = 0 }
        $null = Wait-LabBrokerDevice -Broker $broker -Org $org.org -ComputerName $computer -Offline
        Set-LabAgentPolicy -Name $VmName -Clear
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

    It 'SEC-BRK-01 the device secret is kept nowhere on the device' -Tag 'SEC-BRK-01' {
        if (-not $broker) { Set-ItResult -Skipped -Because $noBroker; return }
        $install.LogHasSecret | Should -BeFalse -Because 'BROKER_SECRET is hidden in the installer log'
        $kept = Get-Kept
        $kept.JsonHasSecret | Should -BeFalse -Because 'agent.json holds only the keys derived from it'
        $kept.Broker.enrollKey | Should -Not -BeNullOrEmpty
        $kept.Broker.recordKey | Should -Not -BeNullOrEmpty
        $kept.LogsHaveSecret | Should -BeFalse
        $kept.EventsHaveKey | Should -BeFalse
        (Invoke-AgentExe @('broker', 'status')).Output | Should -Not -Match 'rhd1\.'
    }
}
