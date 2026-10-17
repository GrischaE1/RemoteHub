# J (more) - the jump host's own refusals and rules (docs/jump-host-design.md section 11, docs/test-lab-cases.md J):
#   J-26  the agent refuses /v1/tunnel: 404 (TunnelEnabled = 0) and 403 (right Tunnel), as RemoteHub words them
#   J-27  the jump host's own adapter addresses: only an entry naming that single address opens them
#   J-28  TunnelTargets / TunnelPorts with only unreadable entries allow nothing (never "any")
#   J-29  bad tunnel traffic: window overrun, text / garbled frame, duplicate stream id, a target that never closes.
#         Uses the harness op "jump --kind raw" (raw frames on /v1/tunnel, LabJump.cs Raw).
#   J-30  one name with an allowed and a disallowed address (hosts file on the jump host)
# Needs: the jump host VM (lab.json "JumpHost"); the VM under test is moved behind it as in J-JUMP.Tests.ps1, the revert
# undoes it. Harness calls go through the jump host (Use-LabJump). The jump VM is turned off at the end.
# LabRole: device
# LabNeeds: jumphost
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $password = (Get-LabConfig).Password
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "j2-$name.json") -Encoding UTF8 }
    function Open([string]$To, [int]$Port, [hashtable]$P = @{}) { Invoke-LabJump -Kind open -TargetHost $To -Port $Port -Params $P }
    # Raw frames on the jump host's /v1/tunnel (wished-for harness op, see the header): answers the op's object, or
    # { missing = $true } while the harness doesn't know it
    function Raw([string]$Case, [string]$To, [int]$Port, [hashtable]$P = @{}) {
        $a = @('jump', '--kind', 'raw', '--case', $Case, '--host', $To, '--port', "$Port")
        foreach ($k in $P.Keys) { $a += "--$k", "$($P[$k])" }
        try { & (Get-Module RemoteHub.TestLab) { param($a) Invoke-LabHarness $a -TimeoutSec 300 } $a }
        catch { [pscustomobject]@{ missing = ("$_" -match 'unknown kind'); error = "$_" } }
    }

    Reset-LabVm -Name $VmName
    $jump = Start-LabJumpHost
    $target = Move-LabVmBehindJump -Name $VmName
    # the device sees connections from the jump host's address on the jump switch: that is the agent's firewall scope
    $install = Install-LabAgent -Name $VmName -Scope $jump.Inner
    $pin = $install.pin
    Use-LabJump -Jump $jump
    Save 'setup' @{ Jump = $jump; Target = $target; Install = $install }
}

AfterAll {
    Use-LabJump -Off
    try { Save-LabAgentEvidence -Name 'JumpHost' -Destination (Join-Path $EvidenceDir 'j2-jumphost-logs') } catch { }
    try { Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'j2-target-logs') } catch { }
    Stop-LabJumpHost
    Restore-LabVmNetwork -Name $VmName
    # moved behind the jump host, agent scoped to it: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'J2 <VmName>' -Tag Jump, 'J' {

    Context 'the agent refuses /v1/tunnel' {
        BeforeAll {
            $null = Invoke-LabGuest -Name 'JumpHost' -ScriptBlock { if (-not (Get-LocalGroup 'RH Others' -ErrorAction SilentlyContinue)) { New-LocalGroup 'RH Others' } }
            $r = [ordered]@{}
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Values @{ TunnelEnabled = 0 }
            $r.off = Open $target 58732
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Rights @{ Tunnel = @('RH Others') }
            $r.noRight = Open $target 58732
            Set-LabAgentPolicy -Name 'JumpHost' -Clear
            Save 'refusals' $r
        }

        It 'J-26 404 "can''t be used as a jump host ... (TunnelEnabled), or the agent there is older"; 403 "may not use ... as a jump host ... (right Tunnel)"; both "Can''t use the jump host [j]: ..."' -Tag 'J-26' {
            # AgentErrors.Explain (AgentTransport.cs), wrapped by JumpTunnel.ConnectAsync; the harness names the route "jump <address>"
            $where = [regex]::Escape("$($jump.Address):58732")
            $r.off.connected | Should -BeFalse
            $r.off.message | Should -Match "^Can't use the jump host jump $([regex]::Escape($jump.Address)): "
            $r.off.message | Should -Match "The RemoteHub Agent on $where can't be used as a jump host: jump connections are turned off by a policy on the device \(TunnelEnabled\), or the agent there is older than this RemoteHub"
            $r.noRight.connected | Should -BeFalse
            $r.noRight.message | Should -Match "^Can't use the jump host jump $([regex]::Escape($jump.Address)): "
            $r.noRight.message | Should -Match "The account may not use the RemoteHub Agent on $where as a jump host: .*\(right Tunnel . by default the administrators\)\."
        }
    }

    Context 'the jump host''s own address' {
        BeforeAll {
            $since = Get-LabGuestTime -Name 'JumpHost'
            # the jump switch's /24 (as the module's Get-LabJumpPrefix reads it from lab.json)
            $subnet = ((($jump.Inner -split '\.')[0..2]) -join '.') + '.0/24'
            $o = [ordered]@{}
            Set-LabAgentPolicy -Name 'JumpHost' -Clear
            $o.innerDefault = Open $jump.Inner 58732
            $o.outerDefault = Open $jump.Address 58732
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Values @{ TunnelTargets = @($subnet) }
            $o.innerSubnet = Open $jump.Inner 58732
            $o.targetSubnet = Open $target 58732
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Values @{ TunnelTargets = @($jump.Inner) }
            $o.innerNamed = Open $jump.Inner 58732
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Values @{ TunnelTargets = @("$($jump.Inner)/32") }
            $o.innerNamed32 = Open $jump.Inner 58732
            Set-LabAgentPolicy -Name 'JumpHost' -Clear
            Start-Sleep -Seconds 3
            $o.events = @(Get-LabAgentEvent -Name 'JumpHost' -Since $since -Id 2200)
            Save 'own-address' $o
        }

        It 'J-27 its own adapter address is refused by default and when its subnet is listed; only an entry naming that address allows it; reason policy, 2200 "(own or special address)"' -Tag 'J-27' {
            foreach ($k in 'innerDefault', 'outerDefault', 'innerSubnet') {
                $o[$k].connected | Should -BeFalse -Because $k
                $o[$k].reason | Should -Be 'policy' -Because $k
                $o[$k].message | Should -Match 'is this jump host itself or a special address' -Because $k
            }
            $o.targetSubnet.connected | Should -BeTrue -Because 'the subnet entry itself works for other devices'
            $o.innerNamed.connected | Should -BeTrue
            $o.innerNamed32.connected | Should -BeTrue
            @($o.events | Where-Object { $_.Message -match '\(own or special address\)' }).Count | Should -BeGreaterOrEqual 3
        }
    }

    Context 'unreadable policy entries' {
        BeforeAll {
            $u = [ordered]@{}
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Values @{ TunnelTargets = @('not a host!') }
            $u.targets = Open $target 58732
            $u.targetsOther = Open $target 3389
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Values @{ TunnelPorts = @('0', '99999') }
            $u.ports = Open $target 58732
            $u.portsOther = Open $target 3389
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Values @{ TunnelTargets = @('not a host!'); TunnelPorts = @('0', '99999') }
            $u.both = Open $target 58732
            Set-LabAgentPolicy -Name 'JumpHost' -Clear
            $u.cleared = Open $target 58732
            Save 'unreadable' $u
        }

        It 'J-28 TunnelTargets / TunnelPorts with only invalid entries refuse every Open with policy, never "any"' -Tag 'J-28' {
            foreach ($k in 'targets', 'targetsOther', 'ports', 'portsOther', 'both') {
                $u[$k].connected | Should -BeFalse -Because $k
                $u[$k].reason | Should -Be 'policy' -Because $k
            }
            $u.targets.message | Should -Match 'TunnelTargets'
            $u.ports.message | Should -Match 'TunnelPorts'
            $u.cleared.connected | Should -BeTrue -Because 'without the values every target is allowed again'
        }
    }

    Context 'bad tunnel traffic' {
        BeforeAll {
            # a target that accepts and then never reads and never closes (port 5999 on the device, from the jump host only)
            Invoke-LabGuest -Name $VmName -ArgumentList $jump.Inner -ScriptBlock {
                param($from)
                $null = New-NetFirewallRule -Name 'RemoteHubLab-Sink' -DisplayName 'RemoteHub lab sink' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5999 -RemoteAddress $from -Profile Any
                $code = '$l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Any, 5999); $l.Start(); $keep = New-Object Collections.ArrayList; ' +
                    '$until = (Get-Date).AddMinutes(20); while ((Get-Date) -lt $until) { if ($l.Pending()) { [void]$keep.Add($l.AcceptTcpClient()) } else { Start-Sleep -Milliseconds 100 } }'
                $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))
                $null = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "powershell.exe -NoProfile -EncodedCommand $enc" }
            }
            Start-Sleep -Seconds 5
            $since = Get-LabGuestTime -Name 'JumpHost'
            $b = [ordered]@{}
            # Data frames of 64 KB on one stream without waiting for credit, the target not reading: more than 256 KB
            # unacknowledged -> the agent resets that stream (the tunnel stays)
            $b.window = Raw 'window' $target 5999 @{ bytes = 16MB }
            # a text message / a frame shorter than the 5-byte header / type 99 / the same stream id opened twice: the tunnel ends
            $b.text = Raw 'text' $target 5999
            $b.garbled = Raw 'garbled' $target 5999
            $b.unknown = Raw 'unknown' $target 5999
            $b.duplicate = Raw 'duplicate' $target 5999
            # Close from RemoteHub's side, the target never closes: reset after 60 s (DrainTimeout)
            $b.drain = Raw 'drain' $target 5999 @{ wait = 90 }
            Start-Sleep -Seconds 3
            $b.events = @(Get-LabAgentEvent -Name 'JumpHost' -Since $since -Id 1202)
            Save 'bad-traffic' $b
        }

        It 'J-29 more than 256 KB unacknowledged resets the stream; a text, garbled or unknown frame or a duplicate stream id ends the tunnel, 1202 "(protocol error: ...)"; a target not closing within 60 s after Close is reset' -Tag 'J-29' {
            if ($b.window.PSObject.Properties['missing'] -and $b.window.missing) {
                Set-ItResult -Skipped -Because 'needs the harness op "jump --kind raw --case window|text|garbled|unknown|duplicate|drain" (raw frames on /v1/tunnel; LabJump.cs has none yet)'
                return
            }
            foreach ($k in 'window', 'text', 'garbled', 'unknown', 'duplicate', 'drain') { $b[$k].PSObject.Properties['error'] | Should -BeNullOrEmpty -Because "$k $($b[$k].error)" }
            # TunnelEndpoint.Relay.Received: over TunnelProtocol.Window -> Reset; the tunnel itself goes on
            $b.window.opened | Should -BeTrue
            $b.window.reset | Should -BeTrue
            $b.window.ended | Should -BeFalse
            # ReceiveLoop: "not a tunnel frame" (text, < 5 bytes), "unexpected frame 99", Open: "stream <id> is already open"
            foreach ($k in 'text', 'garbled', 'unknown', 'duplicate') { $b[$k].ended | Should -BeTrue -Because $k }
            $protocol = @($b.events | Where-Object { $_.Message -match '\(protocol error: ' })
            @($protocol | Where-Object { $_.Message -match 'not a tunnel frame' }).Count | Should -BeGreaterOrEqual 2
            @($protocol | Where-Object { $_.Message -match 'unexpected frame 99' }).Count | Should -BeGreaterOrEqual 1
            @($protocol | Where-Object { $_.Message -match 'stream \d+ is already open' }).Count | Should -BeGreaterOrEqual 1
            # DrainTimeout 60 s
            $b.drain.reset | Should -BeTrue
            $b.drain.resetAfterSeconds | Should -BeGreaterOrEqual 55
            $b.drain.resetAfterSeconds | Should -BeLessOrEqual 80
        }
    }

    Context 'a name with an allowed and a disallowed address' {
        BeforeAll {
            $name = 'rhdual.rhlab.test'
            $other = '10.99.99.99'
            $since = Get-LabGuestTime -Name 'JumpHost'
            # the disallowed address first: each address is checked on its own
            Invoke-LabGuest -Name 'JumpHost' -ArgumentList $name, $other, $target -ScriptBlock {
                param($name, $other, $target)
                $hosts = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
                Add-Content $hosts "`r`n$other $name`r`n$target $name" -Encoding ASCII
                Clear-DnsClientCache
            }
            $n = [ordered]@{}
            $n.resolved = Invoke-LabGuest -Name 'JumpHost' -ArgumentList $name -ScriptBlock { param($name) @([Net.Dns]::GetHostAddresses($name) | ForEach-Object { $_.ToString() }) }
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Values @{ TunnelTargets = @("$target/32") }
            $n.open = Open $name 58732
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Values @{ TunnelTargets = @('192.0.2.1/32') }
            $n.neither = Open $name 58732
            Set-LabAgentPolicy -Name 'JumpHost' -Clear
            Start-Sleep -Seconds 3
            $n.events = @(Get-LabAgentEvent -Name 'JumpHost' -Since $since -Id 1201)
            Save 'dual-name' $n
        }

        It 'J-30 a name with two addresses (hosts file), one in TunnelTargets: connects to the allowed one' -Tag 'J-30' {
            @($n.resolved) | Should -Contain $other
            @($n.resolved) | Should -Contain $target
            $n.open.connected | Should -BeTrue -Because "$($n.open.message)"
            # the stream's audit names the address it went to
            @($n.events | Where-Object { $_.Message -match "to $([regex]::Escape($name)):58732 \($([regex]::Escape($target))\)" }).Count | Should -BeGreaterThan 0
            $n.neither.connected | Should -BeFalse
            $n.neither.reason | Should -Be 'policy'
            $n.neither.message | Should -Match 'TunnelTargets'
        }
    }
}
