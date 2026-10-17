# S-THR / S-LIM / S-POL - the agent's limits: the sign-in throttle per source address (5 failures in 5 minutes block
# that address for 5 minutes, attempts still running count), the session limit from agent.json, and the policy key
# (unreadable = nothing allowed; read live; per-right groups). Wrong passwords go to an account made by the test
# (rhwrong): the throttle counts per address, not per account, and PSAdmin must never come near a lockout - it carries
# PowerShell Direct. See docs/test-lab-cases.md section 6.
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
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "s-thr-$name.json") -Encoding UTF8 }
    function Ws([string]$User, [string]$Pass, [hashtable]$More = @{}) {
        $p = @{ pin = $pin; user = $User; password = $Pass }
        foreach ($k in $More.Keys) { $p[$k] = $More[$k] }
        @((Invoke-LabProbe -Name $VmName -Kind ws -Params $p).results)
    }
    function Status([object[]]$Results) { @($Results | ForEach-Object { [int]$_.status }) }

    $init = Initialize-LabVm -Name $VmName -Agent
    $install = $init.Install
    $pin = $init.Pin
    $null = New-LabGuestUser -Name $VmName -User 'rhwrong' -Admin
    Invoke-LabGuest -Name $VmName -ScriptBlock {
        $null = auditpol.exe /set /subcategory:'{0CCE9215-69AE-11D9-BED3-505054503030}' /failure:enable /success:enable
        $null = net.exe accounts /lockoutthreshold:0
    }
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 's-thr-agent-logs')
    # test account and group, audit policy, lockout threshold, agent.json and the policy key's ACL changed: the next
    # file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'S-THR <VmName>' -Tag Agent, Security, 'S-THR', 'S-LIM', 'S-POL' {

    Context 'blocked after 5 failures' {
        BeforeAll {
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            $since = Get-LabGuestTime -Name $VmName
            $five = Ws 'rhwrong' $wrong @{ count = 5 }
            $sixth = Ws 'rhwrong' $password
            Start-Sleep -Seconds 3
            $events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2000)
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            $afterRestart = Ws 'rhwrong' $password
            Save 'blocked' @{ Five = $five; Sixth = $sixth; Events = $events; AfterRestart = $afterRestart }
        }

        It 'S-THR-01 5 wrong passwords block the address: the 6th try gets 429 with the right password, event 2000' -Tag 'S-THR-01' {
            Status $five | Should -Be @(401, 401, 401, 401, 401)
            Status $sixth | Should -Be @(429)
            @($events | Where-Object { $_.Message -match 'too many failed sign-ins' }).Count | Should -BeGreaterThan 0
        }

        It 'S-THR-05 a service restart lifts the block' -Tag 'S-THR-05' {
            Status $afterRestart | Should -Be @(101)
        }
    }

    Context 'parallel attempts' {
        BeforeAll {
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            $since = Get-LabGuestTime -Name $VmName
            $parallel = Ws 'rhwrong' $wrong @{ count = 6; parallel = 1 }
            Start-Sleep -Seconds 3
            $failed = @(Get-LabFailedLogon -Name $VmName -Since $since -Account 'rhwrong')
            Save 'parallel' @{ Results = $parallel; Failed = $failed }
        }

        It 'S-THR-02 6 wrong passwords at once: at most 5 reach Windows, the rest gets 429' -Tag 'S-THR-02' {
            $s = Status $parallel
            $s.Count | Should -Be 6
            @($s | Where-Object { $_ -eq 401 }).Count | Should -BeLessOrEqual 5
            @($s | Where-Object { $_ -eq 429 }).Count | Should -BeGreaterOrEqual 1
            @($s | Where-Object { $_ -notin 401, 429 }) | Should -BeNullOrEmpty
            $failed.Count | Should -BeLessOrEqual 5
        }
    }

    Context 'success clears the count' {
        BeforeAll {
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            $first = Ws 'rhwrong' $wrong @{ count = 4 }
            $right = Ws 'rhwrong' $password
            $second = Ws 'rhwrong' $wrong @{ count = 4 }
            $last = Ws 'rhwrong' $password
            Save 'success-clears' @{ First = $first; Right = $right; Second = $second; Last = $last }
        }

        It 'S-THR-04 4 wrong, 1 right, 4 wrong: never 429' -Tag 'S-THR-04' {
            Status $first | Should -Be @(401, 401, 401, 401)
            Status $right | Should -Be @(101)
            Status $second | Should -Be @(401, 401, 401, 401)
            Status $last | Should -Be @(101)
        }
    }

    Context 'session limit' {
        BeforeAll {
            Invoke-LabGuest -Name $VmName -ScriptBlock {
                $f = 'C:\ProgramData\RemoteHub\Agent\agent.json'
                $c = Get-Content $f -Raw | ConvertFrom-Json
                $c | Add-Member -NotePropertyName maxSessions -NotePropertyValue 2 -Force
                Set-Content $f -Value ($c | ConvertTo-Json -Depth 5) -Encoding UTF8
            }
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            $since = Get-LabGuestTime -Name $VmName
            $hold = Invoke-LabProbe -Name $VmName -Kind hold -Params @{ pin = $pin; user = $vm.AdminAccount; password = $password; count = 3; seconds = 3 }
            $log = @(Get-LabAgentLog -Name $VmName -Since $since)
            Invoke-LabGuest -Name $VmName -ScriptBlock {
                $f = 'C:\ProgramData\RemoteHub\Agent\agent.json'
                $c = Get-Content $f -Raw | ConvertFrom-Json
                $c.maxSessions = 20
                Set-Content $f -Value ($c | ConvertTo-Json -Depth 5) -Encoding UTF8
            }
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            Save 'limit' @{ Hold = $hold; Log = $log }
        }

        It 'S-LIM-01 maxSessions 2: the 3rd session gets 503 and a log line' -Tag 'S-LIM-01' {
            @($hold.results | Where-Object open).Count | Should -Be 2
            $third = @($hold.results | Where-Object { -not $_.open })
            $third.Count | Should -Be 1
            $third[0].status | Should -Be 503
            @($log | Where-Object { $_ -match 'session limit \(2\) reached' }).Count | Should -Be 1
        }
    }

    Context 'policy' {
        BeforeAll {
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            $result = [ordered]@{}
            # read live: Rights\PowerShell to a group PSAdmin isn't in, no restart in between
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock { if (-not (Get-LocalGroup 'RH Others' -ErrorAction SilentlyContinue)) { New-LocalGroup 'RH Others' } }
            $result.psBefore = Ws $vm.AdminAccount $password
            Set-LabAgentPolicy -Name $VmName -Rights @{ PowerShell = @('RH Others') }
            $result.psDenied = Ws $vm.AdminAccount $password
            $result.desktopWhileDenied = Ws $vm.AdminAccount $password @{ path = '/v1/desktop' }
            Set-LabAgentPolicy -Name $VmName -Clear
            $result.psCleared = Ws $vm.AdminAccount $password

            # unreadable: SYSTEM may not read the policy key
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            $since = Get-LabGuestTime -Name $VmName
            Set-LabAgentPolicy -Name $VmName -Values @{ ConsentTimeoutSeconds = 45 }
            Invoke-LabGuest -Name $VmName -ScriptBlock {
                $k = 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent'
                $acl = Get-Acl $k
                $acl.AddAccessRule((New-Object Security.AccessControl.RegistryAccessRule ((New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'), 'ReadKey', 'ContainerInherit', 'None', 'Deny')))
                Set-Acl $k $acl
            }
            $result.healthUnreadable = Get-LabAgentHealth -Name $VmName -Pin $pin
            $result.psUnreadable = Ws $vm.AdminAccount $password
            $result.desktopUnreadable = Ws $vm.AdminAccount $password @{ path = '/v1/desktop' }
            $log = @(Get-LabAgentLog -Name $VmName -Since $since)
            Invoke-LabGuest -Name $VmName -ScriptBlock {
                $k = 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent'
                $acl = Get-Acl $k
                foreach ($r in @($acl.Access | Where-Object { $_.AccessControlType -eq 'Deny' })) { $null = $acl.RemoveAccessRule($r) }
                Set-Acl $k $acl
                Remove-Item $k -Recurse -Force
            }
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            $result.psRestored = Ws $vm.AdminAccount $password
            Save 'policy' @{ Result = $result; Log = $log }
        }

        It 'S-POL-01 an unreadable policy key allows nothing: health says powershell only, every sign-in refused, log says why' -Tag 'S-POL-01' {
            @($result.healthUnreadable.features) | Should -Be @('powershell')
            Status $result.psUnreadable | Should -Be @(403)
            Status $result.desktopUnreadable | Should -Be @(404)
            @($log | Where-Object { $_ -match "can't be read" }).Count | Should -BeGreaterThan 0
            Status $result.psRestored | Should -Be @(101)
        }

        It 'S-POL-02 a policy change applies to the next connection without a restart' -Tag 'S-POL-02' {
            Status $result.psBefore | Should -Be @(101)
            Status $result.psDenied | Should -Be @(403)
            Status $result.psCleared | Should -Be @(101)
        }

        It 'S-POL-03 Rights\PowerShell without PSAdmin: PowerShell 403, the desktop still works' -Tag 'S-POL-03' {
            Status $result.psDenied | Should -Be @(403)
            Status $result.desktopWhileDenied | Should -Be @(101)
        }
    }

    Context 'the block ends by itself' {
        BeforeAll {
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            $five = Ws 'rhwrong' $wrong @{ count = 5 }
            $blocked = Ws 'rhwrong' $password
            # the block runs 5 minutes from the 5th failure
            Start-Sleep -Seconds 315
            $later = Ws 'rhwrong' $password
            Save 'block-ends' @{ Five = $five; Blocked = $blocked; Later = $later }
        }

        It 'S-THR-03 after 5 minutes the right password works again' -Tag 'S-THR-03' {
            Status $blocked | Should -Be @(429)
            Status $later | Should -Be @(101)
        }
    }
}
