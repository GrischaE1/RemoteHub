# S-TLS / S-AUTH - the agent's listener seen from the network: TLS versions, plain HTTP, the pin, what health tells,
# and every way a sign-in can be refused (wrong password, no or bad header, standard user, deny network logon, lockout,
# disabled / expired / blank-password accounts) - plus the full token a non-RID-500 admin gets. Raw requests come from
# the harness' probes (LabProbes.cs), the device side is read over PowerShell Direct. Accounts that get locked, denied
# or disabled are made by the test (the checkpoint removes them): PSAdmin carries PowerShell Direct and must stay
# usable. See docs/test-lab-cases.md section 6.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $password = (Get-LabConfig).Password
    $wrong = 'wrong-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "s-auth-$name.json") -Encoding UTF8 }
    # one WebSocket attempt (PowerShell endpoint unless -Path); answers the probe's result object
    function Ws([string]$User, [string]$Pass, [hashtable]$More = @{}) {
        $p = @{ pin = $pin; user = $User; password = $Pass }
        foreach ($k in $More.Keys) { $p[$k] = $More[$k] }
        (Invoke-LabProbe -Name $VmName -Kind ws -Params $p).results[0]
    }

    $init = Initialize-LabVm -Name $VmName -Agent
    $install = $init.Install
    $pin = $init.Pin
    $hostIp = Get-LabHostAddress -Name $VmName
    $computer = Invoke-LabGuest -Name $VmName -ScriptBlock {
        # failed sign-ins must reach the Security log (4625); by GUID - subcategory names are localized
        $null = auditpol.exe /set /subcategory:'{0CCE9215-69AE-11D9-BED3-505054503030}' /failure:enable /success:enable
        # no lockout while this file sends wrong passwords (S-AUTH-07 turns it on for its own account)
        $null = net.exe accounts /lockoutthreshold:0
        $env:COMPUTERNAME
    }
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 's-auth-agent-logs')
    # audit policy, lockout threshold and test accounts changed: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'S-AUTH <VmName>' -Tag Agent, Security, 'S-TLS', 'S-AUTH' {

    Context 'transport' {
        BeforeAll {
            $tls = [ordered]@{}
            foreach ($p in 'tls10', 'tls11', 'tls12raw', 'tls12', 'tls13') { $tls[$p] = Invoke-LabProbe -Name $VmName -Kind tls -Params @{ protocol = $p } }
            $http = Invoke-LabProbe -Name $VmName -Kind http
            $health = Invoke-LabProbe -Name $VmName -Kind health -Params @{ pin = $pin }
            Save 'transport' @{ Tls = $tls; Http = $http; Health = $health }
        }

        It 'S-TLS-01 accepts TLS 1.2 and 1.3, refuses TLS 1.0 and 1.1' -Tag 'S-TLS-01' {
            $tls.tls12.handshake | Should -BeTrue
            $tls.tls12.negotiated | Should -Be 'Tls12'
            $tls.tls13.handshake | Should -BeTrue
            $tls.tls13.negotiated | Should -Be 'Tls13'
            # 1.0 / 1.1 are hand-written hellos (the host's own TLS stack won't send them); the same hello as 1.2 is the
            # control that they are well-formed - so their refusal is the device's
            $tls.tls12raw.handshake | Should -BeTrue
            $tls.tls12raw.negotiated | Should -Be '3.3'
            $tls.tls10.handshake | Should -BeFalse
            $tls.tls11.handshake | Should -BeFalse
        }

        It 'S-TLS-02 answers nothing over plain HTTP' -Tag 'S-TLS-02' {
            if ($http.answered) { $http.status | Should -Not -Be 200 }
            else { $http.answered | Should -BeFalse }
        }

        It 'S-TLS-04 health tells only product, version, protocol, device, features, managed names and its start id; no Server header' -Tag 'S-TLS-04' {
            $health.status | Should -Be 200
            # managed (admx-design.md: health instead of Hello): only the names of policy-set actions, never values
            @($health.fields | Sort-Object) | Should -Be @('deviceId', 'features', 'instance', 'managed', 'product', 'protocol', 'version')
            # instance (since 0.21.4): a random id per agent start - nothing about the device or when it started
            ($health.body | ConvertFrom-Json).instance | Should -Match '^[0-9a-f]{32}$'
            $managed = ($health.body | ConvertFrom-Json).managed
            @($managed | Where-Object { $_ -and $_ -notin 'autoSignIn', 'virtualDisplay', 'port' }) | Should -BeNullOrEmpty -Because "managed may only name autoSignIn, virtualDisplay or port (got: $(@($managed) -join ', '))"
            $health.server | Should -BeNullOrEmpty
            @($health.headers) | Should -Not -Contain 'Server'
        }
    }

    Context 'pin mismatch' {
        BeforeAll {
            $since = Get-LabGuestTime -Name $VmName
            $r = Ws $vm.AdminAccount $password @{ pin = ('0' * 64) }
            Start-Sleep -Seconds 3
            $failed = Get-LabFailedLogon -Name $VmName -Since $since
            $log = @(Get-LabAgentLog -Name $VmName -Since $since)
            Save 'pin-mismatch' @{ Result = $r; Failed = $failed; Log = $log }
        }

        It 'S-TLS-03 a wrong pin stops before the credentials are sent: no 4625, no sign-in line' -Tag 'S-TLS-03' {
            $r.upgraded | Should -BeFalse
            $r.pinMatched | Should -BeFalse
            @($failed) | Should -BeNullOrEmpty
            @($log | Where-Object { $_ -match 'Refused|Session started' }) | Should -BeNullOrEmpty
        }
    }

    Context 'refused sign-ins' {
        BeforeAll {
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            $since = Get-LabGuestTime -Name $VmName
            $bad = Ws $vm.AdminAccount $wrong
            $none = Ws $vm.AdminAccount $password @{ auth = 'none' }
            $malformed = @(1..6 | ForEach-Object { Ws $vm.AdminAccount $password @{ auth = 'malformed' } })
            $plain = Ws $vm.AdminAccount $password @{ upgrade = '0' }
            $user = Ws $vm.UserAccount $password
            # 1 wrong password + 1 standard user so far: the 6 malformed headers must not have counted
            $rightAfter = Ws $vm.AdminAccount $password
            Start-Sleep -Seconds 3
            $events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2000)
            $failed = @(Get-LabFailedLogon -Name $VmName -Since $since -Account $vm.AdminAccount)
            Save 'refused' @{ Bad = $bad; None = $none; Malformed = $malformed; Plain = $plain; User = $user; RightAfter = $rightAfter; Events = $events; Failed = $failed }
        }

        It 'S-AUTH-01 a wrong password gets 401, event 2000 and Security 4625' -Tag 'S-AUTH-01' {
            $bad.status | Should -Be 401
            # the agent writes the IPv4 caller as an IPv4-mapped IPv6 address (::ffff:a.b.c.d)
            @($events | Where-Object { $_.Message -match "Refused $([regex]::Escape($vm.AdminAccount)) from (::ffff:)?$([regex]::Escape($hostIp)):" }).Count | Should -BeGreaterThan 0
            $failed.Count | Should -BeGreaterThan 0
            $failed[0].LogonType | Should -Be '3'
        }

        It 'S-AUTH-02 a missing or malformed Basic header gets 401 with the realm and is not counted' -Tag 'S-AUTH-02' {
            foreach ($r in @($none) + $malformed) {
                $r.status | Should -Be 401
                $r.wwwAuthenticate | Should -Be 'Basic realm="RemoteHub Agent"'
            }
            $rightAfter.status | Should -Be 101 -Because '6 malformed headers must not block the right password'
        }

        It 'S-AUTH-03 a request that is not a WebSocket gets 400' -Tag 'S-AUTH-03' {
            $plain.status | Should -Be 400
        }

        It 'S-AUTH-04 a standard user gets 403 "not a member of an allowed group", event 2000' -Tag 'S-AUTH-04' {
            $user.status | Should -Be 403
            @($events | Where-Object { $_.Message -match "Refused $([regex]::Escape($vm.UserAccount)) .*not a member of an allowed group" }).Count | Should -Be 1
        }
    }

    Context 'user name formats' {
        BeforeAll {
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            $formats = [ordered]@{}
            foreach ($n in $vm.AdminAccount, ".\$($vm.AdminAccount)", "$computer\$($vm.AdminAccount)", "$($computer.ToLowerInvariant())\$($vm.AdminAccount)", "$($vm.AdminAccount)@$computer") {
                $formats[$n] = Ws $n $password
            }
            Save 'formats' $formats
        }

        It 'S-AUTH-05 PSAdmin, .\PSAdmin and COMPUTER\PSAdmin work; PSAdmin@COMPUTER is a sign-in attempt, never an error' -Tag 'S-AUTH-05' {
            $formats[$vm.AdminAccount].status | Should -Be 101
            $formats[".\$($vm.AdminAccount)"].status | Should -Be 101
            $formats["$computer\$($vm.AdminAccount)"].status | Should -Be 101
            $formats["$($computer.ToLowerInvariant())\$($vm.AdminAccount)"].status | Should -Be 101
            # a UPN goes to Windows as typed (domain null); for a local account Windows decides - the agent must not fail
            $formats["$($vm.AdminAccount)@$computer"].status | Should -BeIn @(101, 401)
        }
    }

    Context 'accounts Windows refuses' {
        BeforeAll {
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            foreach ($u in 'rhdeny', 'rhdisabled', 'rhexpired', 'rhlock') { $null = New-LabGuestUser -Name $VmName -User $u -Admin }
            $null = New-LabGuestUser -Name $VmName -User 'rhempty' -Admin -NoPassword
            $before = [ordered]@{ deny = Ws 'rhdeny' $password; disabled = Ws 'rhdisabled' $password; expired = Ws 'rhexpired' $password }
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            Invoke-LabGuest -Name $VmName -ScriptBlock {
                # Deny access to this computer from the network: rhdeny
                $sid = (Get-LocalUser rhdeny).SID.Value
                $cfg = Join-Path $env:TEMP 'rh-rights.inf'; $db = Join-Path $env:TEMP 'rh-rights.sdb'
                $null = secedit.exe /export /cfg $cfg /areas USER_RIGHTS
                $lines = @(Get-Content $cfg)
                $i = [array]::FindIndex($lines, [Predicate[string]] { param($l) $l -like 'SeDenyNetworkLogonRight*' })
                if ($i -ge 0) { $lines[$i] = $lines[$i] + ",*$sid" }
                else { $j = [array]::IndexOf($lines, '[Privilege Rights]'); $lines = $lines[0..$j] + "SeDenyNetworkLogonRight = *$sid" + $lines[($j + 1)..($lines.Count - 1)] }
                Set-Content $cfg -Value $lines -Encoding Unicode
                $null = secedit.exe /configure /db $db /cfg $cfg /areas USER_RIGHTS
                Disable-LocalUser rhdisabled
                Set-LocalUser rhexpired -AccountExpires (Get-Date).AddDays(-1)
            }
            $since = Get-LabGuestTime -Name $VmName
            $after = [ordered]@{ deny = Ws 'rhdeny' $password; disabled = Ws 'rhdisabled' $password; expired = Ws 'rhexpired' $password; empty = Ws 'rhempty' '' }
            Start-Sleep -Seconds 3
            $events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2000)
            $log = @(Get-LabAgentLog -Name $VmName -Since $since)

            # lockout: threshold 3 for this context only
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock { net.exe accounts /lockoutthreshold:3 /lockoutwindow:30 /lockoutduration:30 }
            $lock = [ordered]@{ first = Ws 'rhlock' $password }
            $lock.wrong = @(1..3 | ForEach-Object { Ws 'rhlock' $wrong })
            $lock.right = Ws 'rhlock' $password
            $lock.lockedOut = Invoke-LabGuest -Name $VmName -ScriptBlock { [bool](Get-CimInstance Win32_UserAccount -Filter "LocalAccount=True AND Name='rhlock'").Lockout }
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock { net.exe accounts /lockoutthreshold:0 }
            Save 'windows-refuses' @{ Before = $before; After = $after; Events = $events; Log = $log; Lock = $lock }
        }

        It 'S-AUTH-06 "Deny access from the network" gets 403 (1385), not bypassed' -Tag 'S-AUTH-06' {
            $before.deny.status | Should -Be 101
            $after.deny.status | Should -Be 403
            @($events | Where-Object { $_.Message -match 'Refused rhdeny ' }).Count | Should -Be 1
        }

        It 'S-AUTH-07 a locked-out account is refused with the right password too (401)' -Tag 'S-AUTH-07' {
            $lock.first.status | Should -Be 101
            @($lock.wrong | ForEach-Object status) | Should -Be @(401, 401, 401)
            $lock.lockedOut | Should -BeTrue
            $lock.right.status | Should -Be 401
        }

        It 'S-AUTH-08 a disabled or expired account gets 401 and is logged' -Tag 'S-AUTH-08' {
            $before.disabled.status | Should -Be 101
            $before.expired.status | Should -Be 101
            $after.disabled.status | Should -Be 401
            $after.expired.status | Should -Be 401
            @($events | Where-Object { $_.Message -match 'Refused rhdisabled ' }).Count | Should -Be 1
            @($events | Where-Object { $_.Message -match 'Refused rhexpired ' }).Count | Should -Be 1
        }

        It 'S-AUTH-10 an account with an empty password is refused (401), no session started' -Tag 'S-AUTH-10' {
            $after.empty.status | Should -Be 401
            @($log | Where-Object { $_ -match 'Session started: .*rhempty' }) | Should -BeNullOrEmpty
        }
    }

    Context 'non-RID-500 administrator' {
        BeforeAll {
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            $null = New-LabGuestUser -Name $VmName -User 'lapsadmin' -Admin
            $latfp = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $k = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
                Remove-ItemProperty $k -Name LocalAccountTokenFilterPolicy -ErrorAction SilentlyContinue
                (Get-ItemProperty $k).PSObject.Properties['LocalAccountTokenFilterPolicy']
            }
            $s = Invoke-LabTool -Name $VmName -Op ps -Pin $pin -UserName 'lapsadmin' -Params @{
                script = 'whoami; [bool](whoami /groups | Select-String "S-1-16-12288"); New-Item HKLM:\SOFTWARE\RemoteHubLabS9 -Force | Out-Null; Test-Path HKLM:\SOFTWARE\RemoteHubLabS9'
            }
            Save 'lapsadmin' @{ Latfp = $latfp; Session = $s }
        }

        It 'S-AUTH-09 a non-RID-500 admin gets a full token (High integrity, writes HKLM) without LocalAccountTokenFilterPolicy' -Tag 'S-AUTH-09' {
            $latfp | Should -BeNullOrEmpty
            $s.refused | Should -BeNullOrEmpty
            $out = @($s.result.output)
            $out[0] | Should -Match '\\lapsadmin$'
            $out[1] | Should -Be 'True'
            $out[2] | Should -Be 'True'
        }
    }
}
