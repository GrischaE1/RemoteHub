# POL (agent) - the agent's policy values of phase 2 (docs/admx-design.md sections 9 and 17): RemoteHubAgent.exe policy,
# MaxSessions / PrivacyScreenMessage without a restart, PowerShellEnabled, AllowedGroups, ListenPort and the firewall
# rule, and automatic sign-in moving from the policy key to agent.json on an update. Values are written the way the
# ADMX template writes them (Set-LabAgentPolicy). Every VM. See docs/test-lab-cases.md section 10.7.
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $cfg = Get-LabConfig
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "pol-agent-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "pol-agent-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    # RemoteHubAgent.exe policy, run elevated in the guest; answers its output as one string
    function Policy-Report {
        Invoke-LabGuest -Name $VmName -ScriptBlock { (& 'C:\Program Files\RemoteHub Agent\RemoteHubAgent.exe' policy) -join "`n" }
    }
    # a PowerShell session through the agent: the harness' answer, or the error it threw
    function Ps([string]$Pin, [string]$Account = 'Admin', [string]$Script = '$PID', [int]$Port = 58732) {
        try { Invoke-LabAgentCommand -Name $VmName -Pin $Pin -Account $Account -Script $Script -Port $Port } catch { [pscustomobject]@{ error = "$_" } }
    }
    function Desktop([string]$Pin, [string]$Account = 'Admin', [string]$Name = 'desktop') {
        try { (Invoke-LabDesktop -Name $VmName -Pin $Pin -Account $Account -Out (Out $Name) -Steps @(@{ do = 'connect' }, @{ do = 'close' })).hello }
        catch { [pscustomobject]@{ error = "$_" } }
    }
    function Rule {
        Invoke-LabGuest -Name $VmName -ScriptBlock {
            $r = Get-NetFirewallRule -DisplayName 'RemoteHub Agent' -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $r) { return $null }
            [pscustomobject]@{
                Port   = [string](($r | Get-NetFirewallPortFilter).LocalPort)
                Remote = [string](@(($r | Get-NetFirewallAddressFilter).RemoteAddress) -join ',')
            }
        }
    }
    function Listening {
        Invoke-LabGuest -Name $VmName -ScriptBlock {
            $agentPid = (Get-CimInstance Win32_Service -Filter "Name='RemoteHubAgent'").ProcessId
            @(Get-NetTCPConnection -State Listen -OwningProcess $agentPid -ErrorAction SilentlyContinue | ForEach-Object LocalPort | Sort-Object -Unique)
        }
    }
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'pol-agent-logs')
}

Describe 'POL-AGENT <VmName>' -Tag Agent, 'POL' {

    Context 'policy command, session limit, invalid values, privacy heading' {
        BeforeAll {
            $r = [ordered]@{}
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName
            $pin = $install.pin

            # POL-03: the report, with a valid, a switch and an invalid value
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ MaxSessions = 50; PowerShellEnabled = 0; ConsentTimeoutSeconds = 9999 }
            $r.report = Policy-Report
            # the key unreadable for SYSTEM (the service and the command run as it): a deny ACE, removed again after
            $r.unreadable = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $key = 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent'
                $acl = Get-Acl $key
                $deny = New-Object Security.AccessControl.RegistryAccessRule('SYSTEM', 'ReadKey', 'Deny')
                $acl.AddAccessRule($deny); Set-Acl $key $acl
                try {
                    # as SYSTEM, like the service (a one-off scheduled task)
                    $out = 'C:\Windows\Temp\rh-policy.txt'
                    $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"`"C:\Program Files\RemoteHub Agent\RemoteHubAgent.exe`" policy > $out 2>&1`""
                    Register-ScheduledTask -TaskName 'RhPolicyReport' -Action $action -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
                    Start-ScheduledTask -TaskName 'RhPolicyReport'
                    $deadline = (Get-Date).AddSeconds(60)
                    while ((Get-ScheduledTask -TaskName 'RhPolicyReport').State -eq 'Running' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
                    Unregister-ScheduledTask -TaskName 'RhPolicyReport' -Confirm:$false
                    Get-Content $out -Raw
                }
                finally { $acl.RemoveAccessRule($deny) | Out-Null; Set-Acl $key $acl }
            }

            # POL-04: MaxSessions = 1, no restart - one session held, a second one refused
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ MaxSessions = 1 }
            $since = Get-LabGuestTime -Name $VmName
            $job = Start-Job -ArgumentList (Join-Path $PSScriptRoot '..\RemoteHub.TestLab'), $VmName, $pin -ScriptBlock {
                param($mod, $name, $pin)
                Import-Module $mod
                Invoke-LabAgentCommand -Name $name -Pin $pin -Script 'Start-Sleep -Seconds 25; $PID'
            }
            Start-Sleep -Seconds 8
            $r.second = Ps $pin
            $null = Receive-Job $job -Wait -AutoRemoveJob -ErrorAction SilentlyContinue
            $r.limitLog = @(Get-LabAgentLog -Name $VmName -Since $since | Where-Object { $_ -match 'session limit' })

            # an invalid value: the default applies and the log says so once, however many connections
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ MaxSessions = 0 }
            $since = Get-LabGuestTime -Name $VmName
            $r.invalidFirst = Ps $pin
            $r.invalidSecond = Ps $pin
            $r.invalidLog = @(Get-LabAgentLog -Name $VmName -Since $since | Where-Object { $_ -match 'Policy MaxSessions: 0 is outside 1' })

            # the heading of the privacy screen with message, on the device's own screen (it is never in the picture)
            Set-LabAutoLogon -Name $VmName -Account User
            $null = Wait-LabAgent -Name $VmName -Pin $pin
            function Heading([string]$Name) {
                $o = Out $Name
                $job = Start-LabDesktop -Name $VmName -Pin $pin -Out $o -Steps @(
                    @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 120 },
                    @{ do = 'privacy'; style = 'Message' }, @{ do = 'sleep'; seconds = 4 }, @{ do = 'markFile'; path = (Join-Path $o 'on.txt') },
                    @{ do = 'waitFile'; path = (Join-Path $o 'shot.txt'); timeout = 120 }, @{ do = 'close' })
                $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
                Wait-LabHostFile (Join-Path $o 'on.txt') -TimeoutSec 180
                $png = Join-Path $o 'console.png'
                $null = Get-LabScreenshot -Name $VmName -Path $png
                Set-Content (Join-Path $o 'shot.txt') 'go'
                $null = Receive-LabDesktop $job
                [string](Get-LabOcr -Path $png).text
            }
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ PrivacyScreenMessage = 'Contoso IT is working on this PC' }
            $r.headingPolicy = Heading 'heading-policy'
            Set-LabAgentPolicy -Name $VmName -Clear
            $r.headingBuiltIn = Heading 'heading-builtin'
            Save 'command-limits' $r
        }

        It 'POL-03 RemoteHubAgent.exe policy shows each value with its source and flags invalid ones' -Tag 'POL-03' {
            $r.report | Should -Match 'MaxSessions\s+50\s+\[policy\]'
            $r.report | Should -Match 'PowerShellEnabled\s+Off\s+\[policy\]'
            $r.report | Should -Match 'ListenPort\s+58732\s+\[agent\.json\]'
            $r.report | Should -Match 'ConsentTimeoutSeconds\s+30\s+\[default\]'
            $r.report | Should -Match 'INVALID in the policy: 9999 is outside 5.600 . ignored'
            foreach ($name in 'AllowedGroups', 'Rights\\AutoSignIn', 'VirtualDisplay', 'PrivacyScreenMessage', 'LogRetentionDays', 'TunnelPorts', 'AuditToEventLog') {
                $r.report | Should -Match $name -Because 'every agent value of the template is listed'
            }
            $r.unreadable | Should -Match "THE POLICY KEY CAN'T BE READ"
        }

        It 'POL-04 MaxSessions, an invalid value and the privacy heading apply without a restart' -Tag 'POL-04' {
            $r.second.error | Should -Match 'limit of simultaneous sessions'
            @($r.limitLog | Where-Object { $_ -match 'session limit \(1\) reached' }).Count | Should -BeGreaterThan 0
            $r.invalidFirst.error | Should -BeNullOrEmpty -Because 'MaxSessions = 0 is invalid: the default (agent.json) applies'
            $r.invalidSecond.error | Should -BeNullOrEmpty
            @($r.invalidLog).Count | Should -Be 1 -Because 'an invalid value is logged once per service run'
            $r.headingPolicy | Should -Match 'Contoso IT is working on this PC'
            $r.headingPolicy | Should -Not -Match 'Remote Support Session in Progress'
            $r.headingBuiltIn | Should -Match 'Remote Support Session in Progress'
        }
    }

    Context 'PowerShellEnabled and AllowedGroups' {
        BeforeAll {
            $g = [ordered]@{}
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName
            $pin = $install.pin

            # POL-05
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ PowerShellEnabled = 0 }
            $since = Get-LabGuestTime -Name $VmName
            $g.health = Get-LabAgentHealth -Name $VmName -Pin $pin
            $g.ps = Ps $pin
            $g.probe = (Invoke-LabProbe -Name $VmName -Kind ws -Params @{ pin = $pin; user = $vm.AdminAccount; password = $cfg.Password }).results[0]
            $g.log = @(Get-LabAgentLog -Name $VmName -Since $since | Where-Object { $_ -match 'Refused PowerShell' })
            $g.failedLogons = @(Get-LabFailedLogon -Name $VmName -Since $since)
            $g.desktop = Desktop $pin -Name 'ps-off-desktop'
            Set-LabAgentPolicy -Name $VmName -Clear
            $g.psAgain = Ps $pin

            # POL-06: a new local group with a new user is the default for every right
            $groupUser = New-LabGuestUser -Name $VmName -User 'polgroupuser' -Group 'RH Policy Operators'
            $groupSid = Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-LocalGroup 'RH Policy Operators').SID.Value }
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ AllowedGroups = @($groupSid) }
            $g.memberPs = try { Invoke-LabHarness @('run', '--host', (Get-LabVmAddress -Name $VmName), '--port', '58732', '--pin', $pin,
                        '--user', ".\polgroupuser", '--password', $cfg.Password, '--script', '$PID') -VmName $VmName } catch { [pscustomobject]@{ error = "$_" } }
            $g.adminPs = Ps $pin
            $g.adminDesktop = Desktop $pin -Name 'allowed-admin'
            $g.report = Policy-Report
            # Rights\Desktop on its own wins over AllowedGroups for that right only
            Set-LabAgentPolicy -Name $VmName -Values @{ AllowedGroups = @($groupSid) } -Rights @{ Desktop = @('S-1-5-32-544') }
            $g.adminDesktopRight = Desktop $pin -Name 'right-admin'
            $g.adminPsRight = Ps $pin
            Set-LabAgentPolicy -Name $VmName -Clear
            Save 'powershell-groups' @{ g = $g; groupUser = $groupUser }
        }

        It 'POL-05 PowerShellEnabled=0 refuses PowerShell before any sign-in, the desktop still works, and it is back without a restart' -Tag 'POL-05' {
            @($g.health.features) | Should -Not -Contain 'powershell'
            $g.ps.error | Should -Match 'PowerShellEnabled'
            $g.probe.status | Should -Be 404
            @($g.log | Where-Object { $_ -match 'Refused PowerShell from .*\(policy PowerShellEnabled\)' }).Count | Should -BeGreaterThan 0
            @($g.failedLogons).Count | Should -Be 0 -Because 'nothing is tried: refused before the sign-in'
            $g.desktop.error | Should -BeNullOrEmpty
            $g.desktop.rights.control | Should -BeTrue
            $g.psAgain.error | Should -BeNullOrEmpty
        }

        It 'POL-06 AllowedGroups gives every right not set on its own to its groups, never view-only, ConsentBypass or AutoSignIn' -Tag 'POL-06' {
            $g.memberPs.error | Should -BeNullOrEmpty
            $g.adminPs.error | Should -Match 'not allowed'
            $g.adminDesktop.error | Should -Match 'may not use the remote desktop|403'
            $g.adminDesktopRight.error | Should -BeNullOrEmpty
            $g.adminPsRight.error | Should -Match 'not allowed'
            $g.report | Should -Match 'Rights\\DesktopViewOnly\s+nobody'
            $g.report | Should -Match 'Rights\\ConsentBypass\s+nobody'
            $g.report | Should -Match 'Rights\\AutoSignIn\s+nobody'
        }
    }

    Context 'what health names' {
        BeforeAll {
            $h = [ordered]@{}
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName
            $pin = $install.pin
            $h.none = Get-LabAgentHealth -Name $VmName -Pin $pin
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ VirtualDisplay = 'off'; NoUserBehavior = 'SignInScreen'; MaxSessions = 10; DesktopClipboard = 1 }
            $h.set = Get-LabAgentHealth -Name $VmName -Pin $pin
            # ListenPort is read at start: set and restarted, then health on the new port
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ ListenPort = 58800 }
            $h.withPort = Restart-LabAgent -Name $VmName -Pin $pin -Port 58800
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ ListenPort = 0 }
            $h.invalid = Restart-LabAgent -Name $VmName -Pin $pin
            Set-LabAgentPolicy -Name $VmName -Clear
            Save 'health' $h
        }

        It 'POL-09 health names only which install options the device''s policy decides - no values, nothing else' -Tag 'POL-09' {
            $h.none.managed | Should -BeNullOrEmpty
            @($h.set.managed) | Should -Be @('autoSignIn', 'virtualDisplay')
            @($h.withPort.managed) | Should -Be @('port')
            $h.invalid.managed | Should -BeNullOrEmpty -Because 'an invalid value decides nothing'
            ($h | ConvertTo-Json -Depth 6) | Should -Not -Match 'SignInScreen|58800"' -Because 'never values'
        }
    }

    Context 'ListenPort and the firewall rule' {
        BeforeAll {
            $l = [ordered]@{}
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName
            $pin = $install.pin
            $l.ruleBefore = Rule
            $since = Get-LabGuestTime -Name $VmName
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ ListenPort = 58800 }
            $l.health = Restart-LabAgent -Name $VmName -Pin $pin -Port 58800
            $l.listen = Listening
            $l.rule = Rule
            $l.log = @(Get-LabAgentLog -Name $VmName -Since $since | Where-Object { $_ -match 'Port 58800 set by policy|moved to port 58800' })
            Set-LabAgentPolicy -Name $VmName -Clear
            $l.healthBack = Restart-LabAgent -Name $VmName -Pin $pin
            $l.listenBack = Listening
            $l.ruleBack = Rule
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ ListenPort = 0 }
            $l.healthInvalid = Restart-LabAgent -Name $VmName -Pin $pin
            $l.listenInvalid = Listening
            Set-LabAgentPolicy -Name $VmName -Clear
            Save 'listenport' $l
        }

        It 'POL-07 ListenPort moves the listener and the firewall rule (keeping its scope), and back without the policy' -Tag 'POL-07' {
            $l.health.deviceId | Should -Not -BeNullOrEmpty
            @($l.listen) | Should -Be @(58800)
            $l.rule.Port | Should -Be '58800'
            $l.rule.Remote | Should -Be $l.ruleBefore.Remote -Because 'the rule keeps its address scope'
            @($l.log | Where-Object { $_ -match 'moved to port 58800' }).Count | Should -Be 1
            @($l.listenBack) | Should -Be @(58732)
            $l.ruleBack.Port | Should -Be '58732'
            @($l.listenInvalid) | Should -Be @(58732) -Because 'ListenPort = 0 is invalid: agent.json applies'
        }
    }

    Context 'automatic sign-in moves out of the policy key on update' {
        BeforeAll {
            $m = [ordered]@{}
            $previous = Get-LabAgentMsi -Previous
            $current = Get-LabAgentMsi
            # values as a version before 0.18.12 leaves them: in the policy key, recorded in autosignin.policy
            function Old-State {
                Invoke-LabGuest -Name $VmName -ScriptBlock {
                    $key = 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent'
                    $json = try { Get-Content 'C:\ProgramData\RemoteHub\Agent\agent.json' -Raw | ConvertFrom-Json } catch { $null }
                    [pscustomobject]@{
                        PolicyBehavior = (Get-ItemProperty $key -ErrorAction SilentlyContinue).NoUserBehavior
                        PolicyRight    = @((Get-ItemProperty "$key\Rights" -ErrorAction SilentlyContinue).AutoSignIn)
                        Marker         = Test-Path 'C:\ProgramData\RemoteHub\Agent\autosignin.policy'
                        JsonBehavior   = $json.noUserBehavior
                        JsonGroups     = @($json.autoSignInGroups)
                    }
                }
            }

            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName -Msi $previous -AutoSignIn $true
            $m.before = Old-State
            $since = Get-LabGuestTime -Name $VmName
            $update = Install-LabAgent -Name $VmName -Msi $current -KeepIdentity
            $null = Wait-LabAgent -Name $VmName -Pin $install.pin
            $m.after = Old-State
            $m.log = @(Get-LabAgentLog -Name $VmName -Since $since | Where-Object { $_ -match 'Automatic sign-in: ' })
            $m.report = Policy-Report
            $m.health = Get-LabAgentHealth -Name $VmName -Pin $install.pin
            Disconnect-LabConsole -Name $VmName
            $m.signIn = try { Invoke-LabAutoSignIn -Name $VmName -Pin $install.pin } catch { [pscustomobject]@{ error = "$_"; statuses = @() } }

            # variant: IT changed NoUserBehavior before the update - it stays IT's
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName -Msi $previous -AutoSignIn $true
            Set-LabAgentPolicy -Name $VmName -Values @{ NoUserBehavior = 'SignInScreen' }
            $since = Get-LabGuestTime -Name $VmName
            $null = Install-LabAgent -Name $VmName -Msi $current -KeepIdentity
            $null = Wait-LabAgent -Name $VmName -Pin $install.pin
            $m.itAfter = Old-State
            $m.itLog = @(Get-LabAgentLog -Name $VmName -Since $since | Where-Object { $_ -match 'Automatic sign-in: ' })
            $m.itHealth = Get-LabAgentHealth -Name $VmName -Pin $install.pin
            Disconnect-LabConsole -Name $VmName
            $m.itSignIn = try { Invoke-LabAutoSignIn -Name $VmName -Pin $install.pin } catch { [pscustomobject]@{ error = "$_"; statuses = @() } }
            Save 'migration' @{ m = $m; update = $update }
        }

        It 'POL-08 an update moves NoUserBehavior and Rights\AutoSignIn to agent.json; values IT changed stay in the policy key' -Tag 'POL-08' {
            # the previous release wrote into the policy key
            $m.before.PolicyBehavior | Should -Be 'AutoSignIn'
            $m.before.Marker | Should -BeTrue
            # moved
            $m.after.PolicyBehavior | Should -BeNullOrEmpty
            @($m.after.PolicyRight | Where-Object { $_ }) | Should -BeNullOrEmpty
            $m.after.Marker | Should -BeFalse
            $m.after.JsonBehavior | Should -Be 'AutoSignIn'
            @($m.after.JsonGroups).Count | Should -BeGreaterThan 0
            ($m.log -join "`n") | Should -Match 'moved NoUserBehavior and Rights\\AutoSignIn from the policy key to agent\.json'
            $m.report | Should -Match 'NoUserBehavior\s+AutoSignIn\s+\[agent\.json\]'
            @($m.health.managed) | Should -Not -Contain 'autoSignIn' -Because 'no policy decides it any more'
            ($m.signIn.statuses -join "`n") | Should -Match 'SignedIn: Signed in as'
            # IT's value stays
            $m.itAfter.PolicyBehavior | Should -Be 'SignInScreen'
            ($m.itLog -join "`n") | Should -Match 'NoUserBehavior changed since the install . left in the policy key'
            @($m.itHealth.managed) | Should -Contain 'autoSignIn'
            ($m.itSignIn.statuses -join "`n") | Should -Not -Match 'SignedIn: Signed in as'
        }
    }
}
