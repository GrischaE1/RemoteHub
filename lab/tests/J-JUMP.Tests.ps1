# J - jump hosts (docs/jump-host-design.md section 11): the VM under test is moved behind the jump host VM (lab.json
# "JumpHost": its adapters onto the private jump switch, a static address there), so this PC reaches it only through the
# jump host's RemoteHub Agent - the topology is built at run time on the provided VMs, the revert undoes it. Everything
# goes through RemoteHub's own client code with the route set (AgentEndpoint.Via / PsTarget.Via, harness --jump):
# agent health, PowerShell and tools, the remote desktop, WinRM, RDP's certificate, the local forwarder, installs and
# updates, shares, the jump host's policy and audit, a dropped tunnel. The jump VM is turned off at the end.
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
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "j-$name.json") -Encoding UTF8 }
    function Open([string]$To, [int]$Port, [hashtable]$P = @{}) { Invoke-LabJump -Kind open -TargetHost $To -Port $Port -Params $P }

    Reset-LabVm -Name $VmName
    $jump = Start-LabJumpHost
    $target = Move-LabVmBehindJump -Name $VmName
    $computer = Invoke-LabGuest -Name $VmName -ScriptBlock {
        # RDP on, for the RDP cases (the firewall group by its resource name - display names are localized)
        Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
        Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -ErrorAction SilentlyContinue
        $env:COMPUTERNAME
    }
    # the device sees connections from the jump host's address on the jump switch: that is the agent's firewall scope
    $install = Install-LabAgent -Name $VmName -Scope $jump.Inner
    $pin = $install.pin
    $setup = Invoke-LabSetupScript -Name $VmName -Allow $jump.Inner
    $cert = (Get-LabWinRmState -Name $VmName).Certificate.Thumbprint
    Use-LabJump -Jump $jump
    Save 'setup' @{ Jump = $jump; Target = $target; Install = $install; Setup = $setup; Cert = $cert }
}

AfterAll {
    Use-LabJump -Off
    try { Save-LabAgentEvidence -Name 'JumpHost' -Destination (Join-Path $EvidenceDir 'j-jumphost-logs') } catch { }
    try { Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'j-target-logs') } catch { }
    Stop-LabJumpHost
    Restore-LabVmNetwork -Name $VmName
    # moved behind the jump host, agent scoped to it, RDP / WinRM set up: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'J <VmName>' -Tag Jump, 'J' {

    Context 'isolation' {
        BeforeAll {
            $direct = [ordered]@{}
            foreach ($p in 3389, 5986, 58732) { $direct["$p"] = Invoke-LabJump -Kind direct -TargetHost $target -Port $p }
            $jumpDirect = Invoke-LabJump -Kind direct -TargetHost $jump.Address -Port 58732
            Save 'isolation' @{ Direct = $direct; Jump = $jumpDirect }
        }

        It 'J-00 this PC reaches the jump host, but not the device behind it (RDP, WinRM, agent port)' -Tag 'J-00' {
            $jumpDirect.connected | Should -BeTrue
            foreach ($p in $direct.Keys) { $direct[$p].connected | Should -BeFalse -Because "port $p" }
        }
    }

    Context 'RDP and WinRM through the jump host' {
        BeforeAll {
            $rdp = Invoke-LabJump -Kind rdp -TargetHost $target -Port 3389
            $winrmTest = Invoke-LabTool -Name $VmName -Op test -Via WinRM -Params @{ cert = $cert }
            $winrmPs = Invoke-LabTool -Name $VmName -Op ps -Via WinRM -Params @{ cert = $cert; script = '$env:COMPUTERNAME; whoami' }
            Save 'rdp-winrm' @{ Rdp = $rdp; Test = $winrmTest; Ps = $winrmPs }
        }

        It 'J-01 RDP through the forwarder: the device''s RDP certificate comes back through the tunnel' -Tag 'J-01' {
            $rdp.connected | Should -BeTrue
            $rdp.certificate.subject | Should -Match "CN=$([regex]::Escape($computer))"
        }

        It 'J-02 WinRM HTTPS through the forwarder (127.0.0.1, name not checked): the session opens' -Tag 'J-02' {
            # "test" answers { state, message, transport } itself, not wrapped in "result" like the other ops
            $winrmTest.state | Should -Be 'Available' -Because $winrmTest.message
            $winrmTest.message | Should -Match ([regex]::Escape($computer))
        }

        It 'J-04 PowerShell without the agent (WinRM) works through the jump host' -Tag 'J-04' {
            @($winrmPs.result.output)[0] | Should -Be $computer
            @($winrmPs.result.output)[1] | Should -Match "\\$([regex]::Escape($vm.AdminAccount))$"
        }
    }

    Context 'the agent through the jump host' {
        BeforeAll {
            $jumpSince = Get-LabGuestTime -Name 'JumpHost'
            $targetSince = Get-LabGuestTime -Name $VmName
            $r = [ordered]@{}
            $r.health = Get-LabAgentHealth -Name $VmName -Pin $pin
            $r.ps = Invoke-LabTool -Name $VmName -Op ps -Pin $pin -Params @{ script = '$env:COMPUTERNAME; whoami' }
            $small = Join-Path $EvidenceDir 'j-small'
            $null = New-Item -ItemType Directory -Path $small -Force
            1..20 | ForEach-Object { Set-Content (Join-Path $small "f$_.txt") "jump file $_ $([guid]::NewGuid())" }
            $r.upload = Invoke-LabTool -Name $VmName -Op fs-upload -Pin $pin -Params @{ files = $small; remote = 'C:\LabJump' }
            $r.download = Invoke-LabTool -Name $VmName -Op fs-download -Pin $pin -Params @{ remote = 'C:\LabJump\j-small'; local = (Join-Path $EvidenceDir 'j-down') }
            $r.reg = Invoke-LabTool -Name $VmName -Op reg-set -Pin $pin -Params @{ key = 'HKLM\SOFTWARE'; name = 'RemoteHubLabJump'; kind = 'String'; data = 'through the jump host' }
            $r.svc = Invoke-LabTool -Name $VmName -Op svc-list -Pin $pin
            $r.proc = Invoke-LabTool -Name $VmName -Op proc-list -Pin $pin
            # the remote desktop of the console account's own session (no question for the own account)
            $r.desktop = Invoke-LabDesktop -Name $VmName -Pin $pin -Account Console -Out (Join-Path $EvidenceDir 'j-desktop') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }
                @{ do = 'wait'; state = 'Connected'; timeout = 60 }, @{ do = 'frame'; out = 'through-jump.png'; timeout = 30 }, @{ do = 'close' })
            Start-Sleep -Seconds 5
            $r.device = Invoke-LabGuest -Name $VmName -ScriptBlock {
                [pscustomobject]@{ Files = @(Get-ChildItem 'C:\LabJump' -Recurse -File).Count; Reg = (Get-ItemProperty HKLM:\SOFTWARE).RemoteHubLabJump }
            }
            $jumpEvents = @(Get-LabAgentEvent -Name 'JumpHost' -Since $jumpSince -Id 1200, 1201, 1202)
            $targetEvents = @(Get-LabAgentEvent -Name $VmName -Since $targetSince -Id 1000, 1100)
            Save 'agent' @{ R = $r; JumpEvents = $jumpEvents; TargetEvents = $targetEvents }
        }

        It 'J-03 desktop, PowerShell, files, registry, services and processes work through the jump host; both sides audit it' -Tag 'J-03' {
            $r.health.deviceId | Should -Be $install.deviceId
            @($r.ps.result.output)[0] | Should -Be $computer
            $r.upload.result.failed | Should -Be 0
            $r.device.Files | Should -Be 20
            @($r.download.result.items | Where-Object state -ne 'Done') | Should -BeNullOrEmpty
            $r.device.Reg | Should -Be 'through the jump host'
            @($r.svc.result.services).Count | Should -BeGreaterThan 50
            @($r.proc.result.processes).Count | Should -BeGreaterThan 10
            @($r.desktop.statuses | Where-Object state -eq 'Connected').Count | Should -BeGreaterThan 0
            @($r.desktop.images).Count | Should -Be 1
            @($r.desktop.images)[0].width | Should -BeGreaterThan 0
            # the jump host: connections and their streams to the device's agent port
            @($jumpEvents | Where-Object Id -eq 1200).Count | Should -BeGreaterThan 0
            @($jumpEvents | Where-Object { $_.Id -eq 1201 -and $_.Message -match "$([regex]::Escape($target)).*58732|58732.*$([regex]::Escape($target))" }).Count | Should -BeGreaterThan 0
            # the device: the sessions come from the jump host
            @($targetEvents | Where-Object { $_.Message -match "from (::ffff:)?$([regex]::Escape($jump.Inner))\b" }).Count | Should -BeGreaterThan 0
        }
    }

    Context 'the local forwarder' {
        BeforeAll {
            $fwd = Invoke-LabJump -Kind forward -TargetHost $target -Port 5986
            Save 'forward' $fwd
        }

        It 'J-07 another program on this PC can''t use the forwarder''s local port; RemoteHub''s own connection works' -Tag 'J-07' {
            $fwd.localPort | Should -BeGreaterThan 0
            $fwd.other | Should -Not -Be 'open'
            $fwd.own | Should -Be 'open'
        }
    }

    Context 'the jump host''s policy' {
        BeforeAll {
            $jumpSince = Get-LabGuestTime -Name 'JumpHost'
            $null = Invoke-LabGuest -Name 'JumpHost' -ScriptBlock { if (-not (Get-LocalGroup 'RH Others' -ErrorAction SilentlyContinue)) { New-LocalGroup 'RH Others' } }
            $p = [ordered]@{}
            $p.default = Open $target 58732
            $p.loopback = Open '127.0.0.1' 58732
            $p.metadata = Open '169.254.169.254' 80
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Values @{ TunnelEnabled = 0 }
            $p.disabled = Open $target 58732
            $p.disabledHealth = Get-LabAgentHealth -Name 'JumpHost' -Pin $jump.Pin
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Values @{ TunnelTargets = @('10.0.0.0/8') }
            $p.targets = Open $target 58732
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Values @{ TunnelPorts = @('3389') }
            $p.portOther = Open $target 5986
            $p.portAllowed = Open $target 3389
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Rights @{ Tunnel = @('RH Others') }
            $p.noRight = Open $target 58732
            $p.noRightPs = Invoke-LabAgentCommand -Name 'JumpHost' -Pin $jump.Pin -Script '$env:COMPUTERNAME'
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Values @{ TunnelTargets = @('localhost') }
            $p.nameToLoopback = Open 'localhost' 58732
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Values @{ TunnelTargets = @('127.0.0.0/8') }
            $p.loopbackListed = Open '127.0.0.1' 58732
            Set-LabAgentPolicy -Name 'JumpHost' -Clear
            Start-Sleep -Seconds 3
            $refusedEvents = @(Get-LabAgentEvent -Name 'JumpHost' -Since $jumpSince -Id 2200)
            Save 'policy' @{ P = $p; Events = $refusedEvents }
        }

        It 'J-05 TunnelEnabled, TunnelTargets, TunnelPorts and the right Tunnel each refuse with their message and an audit entry; loopback and 169.254.169.254 only when listed' -Tag 'J-05' {
            $p.default.connected | Should -BeTrue
            $p.loopback.connected | Should -BeFalse
            $p.loopback.reason | Should -Be 'policy'
            $p.metadata.connected | Should -BeFalse
            $p.metadata.reason | Should -Be 'policy'
            $p.disabled.connected | Should -BeFalse
            $p.disabled.message | Should -Match 'TunnelEnabled' -Because '/v1/tunnel answers 404: turned off by a policy on the device'
            @($p.disabledHealth.features) | Should -Not -Contain 'tunnel'
            $p.targets.connected | Should -BeFalse
            $p.targets.reason | Should -Be 'policy'
            $p.targets.message | Should -Match 'TunnelTargets'
            $p.portOther.connected | Should -BeFalse
            $p.portOther.message | Should -Match 'TunnelPorts'
            $p.portAllowed.connected | Should -BeTrue
            $p.noRight.connected | Should -BeFalse
            $p.noRight.message | Should -Match 'right Tunnel' -Because '403: may not use it as a jump host'
            @($p.noRightPs.output)[0] | Should -Not -BeNullOrEmpty -Because 'PowerShell on the jump host still works without the right Tunnel'
            $p.nameToLoopback.connected | Should -BeFalse -Because 'a name never opens a special address'
            $p.loopbackListed.connected | Should -BeTrue
            @($refusedEvents).Count | Should -BeGreaterOrEqual 5
            @($refusedEvents | Where-Object { $_.Message -match 'TunnelPorts' }).Count | Should -BeGreaterThan 0
        }
    }

    Context 'other refusals' {
        BeforeAll {
            $o = [ordered]@{}
            $o.dns = Open 'no-such-host.invalid' 58732
            $o.closed = Open $target 1
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Values @{ MaxTunnelStreams = 3 }
            $o.streams = Invoke-LabJump -Kind streams -TargetHost $target -Port 58732 -Params @{ count = 5 }
            Set-LabAgentPolicy -Name 'JumpHost' -Clear
            Save 'refusals' $o
        }

        It 'J-13 a name that doesn''t resolve (dns), a closed port (unreachable / timeout) and more than MaxTunnelStreams (limit) are refused with their reason' -Tag 'J-13' {
            $o.dns.reason | Should -Be 'dns'
            $o.closed.reason | Should -BeIn 'unreachable', 'timeout'
            @($o.streams.results | Where-Object connected).Count | Should -Be 3
            @($o.streams.results | Where-Object { -not $_.connected } | ForEach-Object reason) | Should -Contain 'limit'
        }
    }

    Context 'limits shared with sessions' {
        BeforeAll {
            $s = [ordered]@{}
            # maxSessions 1 on the jump host: a jump connection held open, then a PowerShell session there gets 503
            Invoke-LabGuest -Name 'JumpHost' -ScriptBlock {
                $f = 'C:\ProgramData\RemoteHub\Agent\agent.json'; $c = Get-Content $f -Raw | ConvertFrom-Json
                $c | Add-Member -NotePropertyName maxSessions -NotePropertyValue 1 -Force; [IO.File]::WriteAllText($f, ($c | ConvertTo-Json -Depth 5))
            }
            $null = Restart-LabAgent -Name 'JumpHost' -Pin $jump.Pin
            $mod = (Get-Module RemoteHub.TestLab).Path
            $held = Start-Job -ArgumentList $mod, $jump, $target -ScriptBlock {
                param($mod, $jump, $target) Import-Module $mod; Use-LabJump -Jump $jump; Invoke-LabJump -Kind open -TargetHost $target -Port 58732 -Params @{ seconds = 20 }
            }
            Start-Sleep -Seconds 8
            $s.ps = (Invoke-LabProbe -Name 'JumpHost' -Kind hold -Params @{ pin = $jump.Pin; user = $jump.User; password = (Get-LabConfig).Password; count = 1; seconds = 1 }).results[0]
            $s.held = Receive-Job $held -Wait -AutoRemoveJob
            Invoke-LabGuest -Name 'JumpHost' -ScriptBlock {
                $f = 'C:\ProgramData\RemoteHub\Agent\agent.json'; $c = Get-Content $f -Raw | ConvertFrom-Json; $c.maxSessions = 20; [IO.File]::WriteAllText($f, ($c | ConvertTo-Json -Depth 5))
            }
            $null = Restart-LabAgent -Name 'JumpHost' -Pin $jump.Pin
            # the policy key unreadable: no jump connections
            Set-LabAgentPolicy -Name 'JumpHost' -Clear -Values @{ TunnelEnabled = 1 }
            Invoke-LabGuest -Name 'JumpHost' -ScriptBlock {
                $k = 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent'; $acl = Get-Acl $k
                $acl.AddAccessRule((New-Object Security.AccessControl.RegistryAccessRule ((New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'), 'ReadKey', 'ContainerInherit', 'None', 'Deny')))
                Set-Acl $k $acl
            }
            $s.unreadable = Open $target 58732
            Invoke-LabGuest -Name 'JumpHost' -ScriptBlock {
                $k = 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent'; $acl = Get-Acl $k
                foreach ($r in @($acl.Access | Where-Object { $_.AccessControlType -eq 'Deny' })) { $null = $acl.RemoveAccessRule($r) }
                Set-Acl $k $acl; Remove-Item $k -Recurse -Force
            }
            Save 'limits' $s
        }

        It 'J-14 a jump connection counts toward maxSessions; an unreadable policy key turns jump connections off' -Tag 'J-14' {
            $s.held.connected | Should -BeTrue
            $s.ps.status | Should -Be 503
            $s.unreadable.connected | Should -BeFalse
        }
    }

    Context 'install and update through the jump host' {
        BeforeAll {
            $i = [ordered]@{}
            $i.local = Invoke-LabJump -Kind local -TargetHost $target -Port 58732
            $null = Uninstall-LabAgent -Name $VmName
            $i.install = Install-LabAgent -Name $VmName -ViaWinRm -Cert $cert -Scope $i.local.local
            $i.state = Get-LabAgentState -Name $VmName
            $i.health = Get-LabAgentHealth -Name $VmName -Pin $i.install.pin
            $rebuilt = New-LabRebuiltMsi -Source (Get-LabAgentMsi) -Path (Join-Path $EvidenceDir 'rebuilt\RemoteHub-Agent-rebuilt-x64.msi')
            $i.update = Update-LabAgent -Name $VmName -Pin $i.install.pin -Msi $rebuilt.FullName
            $script:pinAfter = $i.install.pin
            Save 'install' $i
        }

        It 'J-09 RemoteHub offers the jump host''s address as the scope, installs over WinRM through it, and updates through it' -Tag 'J-09' {
            $i.local.local | Should -Be $jump.Inner
            $i.install.installed.fingerprint | Should -Be $i.install.pin
            @($i.state.Firewall.RemoteAddress) | Should -Be @($jump.Inner)
            $i.health.deviceId | Should -Be $i.install.deviceId
            $i.update.verdict | Should -Be 'updated' -Because $i.update.message
        }
    }

    Context 'a dropped tunnel' {
        BeforeAll {
            $pinNow = (Get-Content (Join-Path $EvidenceDir 'j-install.json') -Raw | ConvertFrom-Json).install.pin
            $ready = Join-Path $EvidenceDir 'j-drop-ready.txt'; $back = Join-Path $EvidenceDir 'j-drop-back.txt'
            Remove-Item $ready, $back -ErrorAction SilentlyContinue
            $steps = @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; state = 'Connected'; timeout = 60 }
                @{ do = 'markFile'; path = $ready }, @{ do = 'waitFile'; path = $back; timeout = 240 }
                @{ do = 'reconnect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; resume = 'last' }
                @{ do = 'wait'; anyOf = @('Connected', 'WaitingForUser', 'Denied', 'Failed'); timeout = 90 }, @{ do = 'frame'; out = 'after-drop.png'; timeout = 30 }, @{ do = 'close' })
            $mod = (Get-Module RemoteHub.TestLab).Path
            $job = Start-Job -ArgumentList $mod, $jump, $VmName, $pinNow, $steps, (Join-Path $EvidenceDir 'j-drop') -ScriptBlock {
                param($mod, $jump, $vmName, $pin, $steps, $out)
                Import-Module $mod
                Use-LabJump -Jump $jump
                Invoke-LabDesktop -Name $vmName -Pin $pin -Account Console -Steps $steps -Out $out
            }
            $until = (Get-Date).AddSeconds(120)
            while (-not (Test-Path $ready) -and (Get-Date) -lt $until) { Start-Sleep -Seconds 2 }
            # the jump host loses its side towards this PC for 30 s (its adapter on the jump switch stays connected)
            $cut = Disconnect-LabNetwork -Name 'JumpHost' -ExceptSwitch (Get-LabConfig).JumpHost.Switch
            try { Start-Sleep -Seconds 30 } finally { Connect-LabNetwork -Name 'JumpHost' -Switches $cut }
            try {
                $null = Wait-LabAgent -Name 'JumpHost' -Pin $jump.Pin -TimeoutSec 120
                # and the device behind it again: a failure here names the broken path instead of a later tool call
                $null = Wait-LabAgent -Name $VmName -Pin $pinNow -TimeoutSec 120
            }
            finally { Set-Content $back 'back' -Encoding ASCII }
            $desk = Receive-Job $job -Wait -AutoRemoveJob
            $after = Invoke-LabTool -Name $VmName -Op ps -Pin $pinNow -Params @{ script = '$env:COMPUTERNAME' }
            Save 'drop' @{ Desktop = $desk; After = $after }
        }

        It 'J-06 after the jump host''s network was gone 30 s: the desktop resumes without asking again, tools connect again' -Tag 'J-06' {
            $desk | Should -Not -BeNullOrEmpty
            $resumed = @($desk.statuses | Where-Object { $_.state -eq 'Connected' })
            $resumed.Count | Should -BeGreaterOrEqual 2 -Because 'connected before the drop and again after the resume'
            @($desk.statuses | Where-Object state -in 'WaitingForUser', 'Denied', 'Failed') | Should -BeNullOrEmpty
            @($after.result.output)[0] | Should -Be $computer
        }
    }

    Context 'throughput' {
        BeforeAll {
            $pinNow = (Get-Content (Join-Path $EvidenceDir 'j-install.json') -Raw | ConvertFrom-Json).install.pin
            $big = Join-Path $env:TEMP "rhlab-jump-2g-$([guid]::NewGuid().ToString('N').Substring(0, 6)).bin"
            $rng = [Security.Cryptography.RandomNumberGenerator]::Create(); $buf = New-Object byte[] (4MB)
            $fs = [IO.File]::Create($big); try { for ($k = 0; $k -lt 512; $k++) { $rng.GetBytes($buf); $fs.Write($buf, 0, $buf.Length) } } finally { $fs.Dispose() }
            $hash = (Get-FileHash $big -Algorithm SHA256).Hash
            # the desktop runs meanwhile, for the whole copy: frames while the copy goes through the same jump host. The
            # agent sends a picture only when the screen changes (the mouse pointer isn't part of it), so a window keeps
            # changing; the desktop stays open until the copy has ended (the done file)
            $change = Start-LabChangingWindow -Name $VmName
            $copyDone = Join-Path $env:TEMP "rhlab-jump-copied-$([guid]::NewGuid().ToString('N').Substring(0, 6)).txt"
            $mod = (Get-Module RemoteHub.TestLab).Path
            $deskJob = Start-Job -ArgumentList $mod, $jump, $VmName, $pinNow, (Join-Path $EvidenceDir 'j-throughput'), $copyDone -ScriptBlock {
                param($mod, $jump, $vmName, $pin, $out, $copyDone)
                Import-Module $mod
                Use-LabJump -Jump $jump
                Invoke-LabDesktop -Name $vmName -Pin $pin -Account Console -Out $out -Steps @(
                    @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; fps = 30 }, @{ do = 'wait'; state = 'Connected'; timeout = 60 }
                    @{ do = 'waitFile'; path = $copyDone; timeout = 1800 }, @{ do = 'close' })
            }
            try {
                $sw = [Diagnostics.Stopwatch]::StartNew()
                $up = Invoke-LabTool -Name $VmName -Op fs-upload -Pin $pinNow -Params @{ files = $big; remote = 'C:\LabJumpBig' } -TimeoutSec 1800
                $seconds = $sw.Elapsed.TotalSeconds
            }
            finally {
                Set-Content $copyDone 'done' -Encoding ASCII
                $desk = Receive-Job $deskJob -Wait -AutoRemoveJob
                $changed = Stop-LabChangingWindow -Change $change
                Remove-Item $copyDone -ErrorAction SilentlyContinue
            }
            # when the copy ended, in the desktop's own clock (the end of its waitFile step)
            $wait = @($desk.steps | Where-Object { $_.do -eq 'waitFile' })[0]
            $copyEndMs = if ($wait) { [double]$wait.atMs + [double]$wait.seconds * 1000 } else { 0 }
            # no file when the upload failed: $null here, so J-08 reports the upload's answer instead of Get-FileHash's error
            $deviceHash = Invoke-LabGuest -Name $VmName -ArgumentList (Split-Path $big -Leaf) -ScriptBlock {
                param($n) $p = "C:\LabJumpBig\$n"; if (Test-Path -LiteralPath $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }
            }
            Remove-Item $big -ErrorAction SilentlyContinue
            Save 'throughput' @{ Upload = $up; Seconds = $seconds; CopyEndMs = $copyEndMs; Changed = $changed; Desktop = @{ frames = $desk.frames; stats = $desk.stats; steps = $desk.steps } }
        }

        It 'J-08 2 GB to the device through the jump host arrive unchanged while its remote desktop keeps sending pictures' -Tag 'J-08' {
            $up.result.failed | Should -Be 0 -Because ($up | ConvertTo-Json -Depth 4 -Compress)
            $deviceHash | Should -Be $hash
            $changed.perSecond | Should -BeGreaterThan 10 -Because "the window must keep changing (it changed $($changed.perSecond) times a second)"
            $copyEndMs | Should -BeGreaterThan 0 -Because 'the desktop must stay open until the copy has ended'
            $desk.frames.count | Should -BeGreaterThan 100 -Because "the picture must keep coming during the copy ($([int]$seconds) s; fps: $(@($desk.stats | ForEach-Object { $_.stats.framesPerSecond }) -join ', '))"
            # still coming at the end of the copy, not only before the tunnel got busy
            [double]$desk.frames.lastAtMs | Should -BeGreaterThan ($copyEndMs - 30000) -Because 'pictures must still come in the copy''s last 30 s'
        }
    }

    Context 'shares of the device' {
        BeforeAll {
            $isServer = $vm.Os -eq 'Server'
            if ($isServer) {
                $servers = Enable-LabShareServers -Name $VmName
                $sh = [ordered]@{}
                $sh.smb = Invoke-LabShare @('--protocol', 'smb', '--host', $target, '--path', 'rhshare', '--user', $vm.AdminAccount)
                $sh.sftp = Invoke-LabShare @('--protocol', 'sftp', '--host', $target, '--port', '2222', '--user', $vm.AdminAccount, '--accept', '1')
                $sh.dav = Invoke-LabShare @('--protocol', 'webdav', '--host', $target, '--path', '/dav', '--user', $vm.AdminAccount, '--accept', '1')
                # FTP can't go through a jump host (its data connections go to addresses the server announces)
                $ftp = try { Invoke-LabShare @('--protocol', 'ftp', '--host', $target, '--user', $vm.AdminAccount, '--ftp-security', 'explicit', '--accept', '1') } catch { [pscustomobject]@{ ok = $false; error = "$_" } }
                Save 'shares' @{ Servers = $servers; Shares = $sh; Ftp = $ftp }
            }
        }

        It 'J-10 SMB (RemoteHub''s own client), SFTP and WebDAV shares of a device behind the jump host copy both ways; FTP is refused' -Tag 'J-10' {
            if (-not $isServer) { Set-ItResult -Skipped -Because 'the share servers are set up on Windows Server VMs only (as in S-SHARE)'; return }
            foreach ($k in $sh.Keys) { $sh[$k].ok | Should -BeTrue -Because $k }
            $sh.smb.description | Should -Match 'RemoteHub client' -Because 'SMB behind a jump host always uses RemoteHub''s own client'
            $ftp.ok | Should -BeFalse
            # (Unescape: Windows PowerShell's ConvertTo-Json writes ' as ')
            [regex]::Unescape(($ftp | ConvertTo-Json -Depth 5)) | Should -Match 'FTP can''t go through a jump host'
        }
    }
}
