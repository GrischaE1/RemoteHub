# A-CFG - the installed agent's configuration and service behaviour: agent.json that is invalid (no fallback to
# defaults) or has comments, a missing certificate, log rotation, what the log says (and never says), who can read it,
# no outbound connections, and audit entries kept out of the event log by policy. See docs/test-lab-cases.md
# section 5.5.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $password = (Get-LabConfig).Password
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "a-cfg-$name.json") -Encoding UTF8 }
    function Ws([string]$User, [string]$Pass) {
        (Invoke-LabProbe -Name $VmName -Kind ws -Params @{ pin = $pin; user = $User; password = $Pass }).results[0]
    }
    # writes agent.json (raw text), restarts the service; answers whether it runs, its listeners and the new log lines
    function Set-AgentJson([string]$Text) {
        Invoke-LabGuest -Name $VmName -ArgumentList $Text -ScriptBlock {
            param($text)
            $f = 'C:\ProgramData\RemoteHub\Agent\agent.json'
            $since = Get-Date
            Stop-Service RemoteHubAgent -Force -ErrorAction SilentlyContinue
            [IO.File]::WriteAllText($f, $text)
            try { Start-Service RemoteHubAgent -ErrorAction Stop } catch { }
            Start-Sleep -Seconds 8
            $svc = Get-CimInstance Win32_Service -Filter "Name='RemoteHubAgent'"
            $listen = @(if ($svc.ProcessId) { Get-NetTCPConnection -State Listen -OwningProcess $svc.ProcessId -ErrorAction SilentlyContinue | ForEach-Object LocalPort | Sort-Object -Unique })
            $lines = @(Get-ChildItem 'C:\ProgramData\RemoteHub\Agent\logs' -Filter 'agent-*.log' | Get-Content |
                    Where-Object { $_ -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})' -and [datetime]$Matches[1] -ge $since.AddSeconds(-1) } | ForEach-Object { [string]$_ })
            [pscustomobject]@{ State = $svc.State; Listen = $listen; Any58732 = @(Get-NetTCPConnection -State Listen -LocalPort 58732 -ErrorAction SilentlyContinue).Count; Lines = $lines }
        }
    }

    # -Clean: the log checks read the whole log and the original agent.json - only this install's
    $init = Initialize-LabVm -Name $VmName -Agent -Clean
    $install = $init.Install
    $pin = $init.Pin
    $original = Invoke-LabGuest -Name $VmName -ScriptBlock { [IO.File]::ReadAllText('C:\ProgramData\RemoteHub\Agent\agent.json') }
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'a-cfg-agent-logs')
    # agent.json, the certificate and the logs changed, auto-logon set: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'A-CFG <VmName>' -Tag Agent, 'A-CFG' {

    Context 'log content, readers and connections' {
        BeforeAll {
            $since = Get-LabGuestTime -Name $VmName
            $hostIp = Get-LabHostAddress -Name $VmName
            $marker = 'RHMARKER' + [guid]::NewGuid().ToString('N').Substring(0, 10)
            $wrong = 'wrongpw' + [guid]::NewGuid().ToString('N').Substring(0, 10)
            $refused = Ws $vm.AdminAccount $wrong
            # a session that runs for a while: the agent's connections are sampled meanwhile
            $job = Start-Job -ArgumentList (Get-LabVmAddress -Name $VmName), $pin, $vm.AdminAccount, $password, $marker, (Join-Path (Get-LabConfig).LabRoot 'harness\RemoteHub.LabHarness.exe') -ScriptBlock {
                param($ip, $pin, $user, $pw, $marker, $exe)
                & $exe run --host $ip --port 58732 --pin $pin --user $user --password $pw --script "Write-Output '$marker'; Start-Sleep -Seconds 20; `$PID"
            }
            Start-Sleep -Seconds 6
            $connections = @(1..5 | ForEach-Object {
                    Invoke-LabGuest -Name $VmName -ScriptBlock {
                        $agent = (Get-CimInstance Win32_Service -Filter "Name='RemoteHubAgent'").ProcessId
                        @(Get-NetTCPConnection -OwningProcess $agent -ErrorAction SilentlyContinue | Where-Object State -ne 'Listen' |
                                ForEach-Object { [pscustomobject]@{ Local = "$($_.LocalAddress):$($_.LocalPort)"; Remote = "$($_.RemoteAddress):$($_.RemotePort)"; LocalPort = $_.LocalPort; State = [string]$_.State } })
                    }
                    Start-Sleep -Seconds 2
                })
            $run = (Receive-Job $job -Wait -AutoRemoveJob) -join "`n" | ConvertFrom-Json
            Start-Sleep -Seconds 3
            $log = @(Get-LabAgentLog -Name $VmName -Since $since)
            $allLog = (Get-LabAgentLog -Name $VmName) -join "`n"
            # the standard user tries to read the log folder in their own session
            Set-LabAutoLogon -Name $VmName -Account User
            # the auto-logon restarted the VM: the agent starts delayed
            $null = Wait-LabAgent -Name $VmName -Pin $pin
            $userRead = Invoke-LabInSession -Name $VmName -Account User -ScriptBlock {
                try { Get-ChildItem 'C:\ProgramData\RemoteHub\Agent\logs' -ErrorAction Stop | Out-Null; 'READ' } catch { 'DENIED: ' + $_.Exception.GetType().Name }
                try { Get-Content (Get-ChildItem 'C:\ProgramData\RemoteHub\Agent\logs\agent-*.log' -ErrorAction Stop | Select-Object -First 1).FullName -ErrorAction Stop | Out-Null; 'READFILE' } catch { 'DENIEDFILE' }
            }
            Save 'log' @{ Refused = $refused; Run = $run; Connections = $connections; Log = $log; UserRead = $userRead }
        }

        It 'A-CFG-05 logs the refusal (account, address, reason) and the session (PID, duration) - never the password or the command' -Tag 'A-CFG-05' {
            $refused.status | Should -Be 401
            @($log | Where-Object { $_ -match "Refused $([regex]::Escape($vm.AdminAccount)) from (::ffff:)?$([regex]::Escape($hostIp)): \S" }).Count | Should -BeGreaterThan 0
            $sessionPid = @($run.output)[-1]
            @($log | Where-Object { $_ -match "Session started: .*$([regex]::Escape($vm.AdminAccount)).*PowerShell PID $sessionPid\)" }).Count | Should -Be 1
            @($log | Where-Object { $_ -match "Session ended: .*$([regex]::Escape($vm.AdminAccount)) .* after \d{2}:\d{2}:\d{2}" }).Count | Should -BeGreaterThan 0
            $allLog | Should -Not -Match ([regex]::Escape($wrong))
            $allLog | Should -Not -Match ([regex]::Escape($password))
            $allLog | Should -Not -Match $marker
        }

        It 'A-CFG-06 the standard user can''t read the log folder' -Tag 'A-CFG-06' {
            $userRead | Should -Match 'DENIED'
            $userRead | Should -Not -Match 'READFILE'
        }

        It 'A-CFG-07 the agent opens no outbound connection: every connection is to its own port' -Tag 'A-CFG-07' {
            $all = @($connections | ForEach-Object { $_ })
            $all.Count | Should -BeGreaterThan 0 -Because 'the session must have been seen'
            @($all | Where-Object { $_.LocalPort -ne 58732 }) | Should -BeNullOrEmpty
        }
    }

    Context 'event log writes off' {
        BeforeAll {
            Set-LabAgentPolicy -Name $VmName -Values @{ AuditToEventLog = 0 }
            $since = Get-LabGuestTime -Name $VmName
            $refused = Ws $vm.AdminAccount ('off-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
            Start-Sleep -Seconds 3
            $events = @(Get-LabAgentEvent -Name $VmName -Since $since)
            $log = @(Get-LabAgentLog -Name $VmName -Since $since)
            Set-LabAgentPolicy -Name $VmName -Clear
            Save 'eventlog-off' @{ Events = $events; Log = $log }
        }

        It 'A-CFG-08 AuditToEventLog=0: the refusal is in the file, not in the event log' -Tag 'A-CFG-08' {
            $refused.status | Should -Be 401
            @($log | Where-Object { $_ -match 'Refused ' }).Count | Should -BeGreaterThan 0
            @($events) | Should -BeNullOrEmpty
        }
    }

    Context 'log rotation' {
        BeforeAll {
            $rot = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $d = 'C:\ProgramData\RemoteHub\Agent\logs'
                $old = Join-Path $d 'agent-20200101.log'; Set-Content $old 'old'; (Get-Item $old).LastWriteTime = (Get-Date).AddDays(-31)
                $keep = Join-Path $d 'agent-20200102.log'; Set-Content $keep 'recent'; (Get-Item $keep).LastWriteTime = (Get-Date).AddDays(-2)
                Restart-Service RemoteHubAgent -Force; Start-Sleep -Seconds 5
                $r = [ordered]@{ OldGone = -not (Test-Path $old); RecentKept = Test-Path $keep }
                # retention 1 day: the 2-day-old file goes at the next start
                $f = 'C:\ProgramData\RemoteHub\Agent\agent.json'
                $c = Get-Content $f -Raw | ConvertFrom-Json
                $c | Add-Member -NotePropertyName logRetentionDays -NotePropertyValue 1 -Force
                [IO.File]::WriteAllText($f, ($c | ConvertTo-Json -Depth 5))
                Restart-Service RemoteHubAgent -Force; Start-Sleep -Seconds 5
                $r.RecentGoneWithOneDay = -not (Test-Path $keep)
                $r.TodayKept = [bool](Get-ChildItem $d -Filter "agent-$(Get-Date -Format yyyyMMdd).log")
                [pscustomobject]$r
            }
            $null = Set-AgentJson $original
            Save 'rotation' $rot
        }

        It 'A-CFG-04 deletes logs older than 30 days at the first write; logRetentionDays 1 is respected' -Tag 'A-CFG-04' {
            $rot.OldGone | Should -BeTrue
            $rot.RecentKept | Should -BeTrue
            $rot.RecentGoneWithOneDay | Should -BeTrue
            $rot.TodayKept | Should -BeTrue
        }
    }

    Context 'agent.json' {
        BeforeAll {
            $cfg = $original | ConvertFrom-Json
            $variants = [ordered]@{
                port           = @{ listenPort = 0 }
                groups         = @{ allowedGroups = @() }
                maxSessions    = @{ maxSessions = 0 }
                virtualDisplay = @{ virtualDisplay = 'x' }
            }
            $results = [ordered]@{}
            foreach ($k in $variants.Keys) {
                $c = $original | ConvertFrom-Json
                foreach ($p in $variants[$k].Keys) { $c | Add-Member -NotePropertyName $p -NotePropertyValue $variants[$k][$p] -Force }
                $results[$k] = Set-AgentJson ($c | ConvertTo-Json -Depth 5)
            }
            # comments and trailing commas
            # agent.json is "{", one property per line, "}": a comment after the brace, a comma after the last property
            $lines = @($original.TrimEnd() -split "`r?`n")
            $commented = @($lines[0], '  // written by hand') + $lines[1..($lines.Count - 2)]
            $commented[-1] = $commented[-1] + ','
            $results.comments = Set-AgentJson ((@($commented) + $lines[-1]) -join "`r`n")
            $results.restored = Set-AgentJson $original
            Save 'json' $results
        }

        It 'A-CFG-01 port 0, no groups, maxSessions 0, virtualDisplay "x": the service doesn''t start, the log says why, no fallback' -Tag 'A-CFG-01' {
            $why = @{ port = 'listenPort 0 is not a valid port'; groups = 'allowedGroups is empty'; maxSessions = 'maxSessions must be at least 1'; virtualDisplay = 'virtualDisplay must be' }
            foreach ($k in $why.Keys) {
                $results[$k].State | Should -Not -Be 'Running' -Because $k
                $results[$k].Any58732 | Should -Be 0 -Because "$k - no fallback to the default port"
                ($results[$k].Lines -join "`n") | Should -Match ([regex]::Escape($why[$k])) -Because $k
            }
        }

        It 'A-CFG-02 agent.json with comments and a trailing comma is accepted' -Tag 'A-CFG-02' {
            $results.comments.State | Should -Be 'Running'
            @($results.comments.Listen) | Should -Contain 58732
            $results.restored.State | Should -Be 'Running'
        }
    }

    Context 'certificate missing' {
        BeforeAll {
            $r = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $c = Get-Content 'C:\ProgramData\RemoteHub\Agent\agent.json' -Raw | ConvertFrom-Json
                $since = Get-Date
                Stop-Service RemoteHubAgent -Force
                Remove-Item "Cert:\LocalMachine\My\$($c.certificateThumbprint)" -DeleteKey
                try { Start-Service RemoteHubAgent -ErrorAction Stop } catch { }
                Start-Sleep -Seconds 8
                [pscustomobject]@{
                    State = (Get-Service RemoteHubAgent).Status.ToString()
                    Any58732 = @(Get-NetTCPConnection -State Listen -LocalPort 58732 -ErrorAction SilentlyContinue).Count
                    Lines = @(Get-ChildItem 'C:\ProgramData\RemoteHub\Agent\logs' -Filter 'agent-*.log' | Get-Content |
                            Where-Object { $_ -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})' -and [datetime]$Matches[1] -ge $since.AddSeconds(-1) } | ForEach-Object { [string]$_ })
                }
            }
            Save 'cert-missing' $r
        }

        It 'A-CFG-03 a deleted certificate: the service refuses to start and logs it' -Tag 'A-CFG-03' {
            $r.State | Should -Not -Be 'Running'
            $r.Any58732 | Should -Be 0
            ($r.Lines -join "`n") | Should -Match 'could not start'
        }
    }
}
