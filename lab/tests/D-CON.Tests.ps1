# D-CON - the agent's remote desktop: Hello, the session list, the start, the picture, the end and the helper process,
# through RemoteHub's desktop client (harness "desktop", DesktopHarness.cs). Connections to the console account's own
# session need no question; PSAdmin connecting to it would be asked (that is D-ASK). See docs/test-lab-cases.md
# section 9.1.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "d-con-$name.json") -Encoding UTF8 }
    function Out([string]$name) { Join-Path $EvidenceDir "d-con-$name" }
    function States($r) { @($r.statuses | ForEach-Object state) }
    # the desktop helper processes ("RemoteHubAgent.exe desktop"): session, owner, job object
    function Get-Helpers {
        Invoke-LabGuest -Name $VmName -ScriptBlock {
            Add-Type -Namespace RhLab -Name Job -MemberDefinition '[DllImport("kernel32.dll")] public static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);'
            @(Get-CimInstance Win32_Process -Filter "Name='RemoteHubAgent.exe'" | Where-Object { $_.CommandLine -match '\sdesktop\s*$' } | ForEach-Object {
                    $inJob = $false
                    try { $p = Get-Process -Id $_.ProcessId; [void][RhLab.Job]::IsProcessInJob($p.Handle, [IntPtr]::Zero, [ref]$inJob) } catch { }
                    $owner = Invoke-CimMethod -InputObject $_ -MethodName GetOwner
                    [pscustomobject]@{ Id = $_.ProcessId; Session = $_.SessionId; Owner = "$($owner.Domain)\$($owner.User)"; InJob = $inJob }
                })
        }
    }
    # the console session as Windows says (the first explorer.exe may be a disconnected session's)
    function Get-ConsoleSession {
        Invoke-LabGuest -Name $VmName -ScriptBlock {
            Add-Type -Namespace RhLab -Name Wts -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();'
            [int][RhLab.Wts]::WTSGetActiveConsoleSessionId()
        }
    }

    $init = Initialize-LabVm -Name $VmName -Agent
    $install = $init.Install
    $pin = $init.Pin
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-con-agent-logs')
    # normaluser's disconnected session, the auto logon set and cleared, the group 'RH Others': not for the next file
    Set-LabVmDirty -Name $VmName
}

Describe 'D-CON <VmName>' -Tag Desktop, 'D-CON' {

    Context 'Hello' {
        BeforeAll {
            $r = Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out 'hello') -Steps @(@{ do = 'connect' }, @{ do = 'close' })
            # automatic sign-in is on by default; it is offered when the MSI under test carries the credential provider
            $provider = [bool](Get-LabAgentState -Name $VmName).AutoSignIn.Provider
            Save 'hello' $r
        }

        It 'D-CON-01 Hello: account, rights (autoSignIn by default), modes Ask+Pin, sessions, limits 60 fps / 100, timeout 30, clipboard 3, features, version' -Tag 'D-CON-01' {
            $h = $r.hello
            $h.account | Should -Match ([regex]::Escape($vm.AdminAccount))
            $h.rights.control | Should -BeTrue
            $h.rights.secureDesktop | Should -BeTrue
            $h.rights.consentBypass | Should -BeFalse
            $h.rights.autoSignIn | Should -Be $provider
            @($h.consentModes) | Should -Be @('Ask', 'Pin')
            @($h.sessions).Count | Should -BeGreaterThan 0
            $h.maxFramesPerSecond | Should -Be 60
            $h.maxQuality | Should -Be 100
            $h.consentTimeoutSeconds | Should -Be 30
            $h.clipboard | Should -Be 3
            @($h.features) | Should -Contain 'resume'
            (@($h.features) -contains 'autoSignIn') | Should -Be $provider
            $h.version | Should -Be (Get-LabMsiVersion (Get-LabAgentMsi))
        }
    }

    Context 'a session: picture, helper, end' {
        BeforeAll {
            $ready = Join-Path (Out 'session') 'ready.txt'; $done = Join-Path (Out 'session') 'done.txt'
            $null = New-Item -ItemType Directory -Path (Out 'session') -Force
            $since = Get-LabGuestTime -Name $VmName
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out (Out 'session') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; state = 'Connected'; timeout = 60 }
                @{ do = 'sleep'; seconds = 3 }, @{ do = 'frame'; out = 'frame.png'; timeout = 30 }
                @{ do = 'markFile'; path = $ready }, @{ do = 'waitFile'; path = $done; timeout = 180 }, @{ do = 'close' })
            Wait-LabHostFile -Path $ready -TimeoutSec 150
            $console = Get-ConsoleSession
            $during = @(Get-Helpers)
            $shot = Get-LabScreenshot -Name $VmName -Path (Join-Path (Out 'session') 'console.png')
            $resolution = Get-LabResolution -Name $VmName
            Set-Content -Encoding ASCII $done 'done'
            $r = Receive-LabDesktop $job
            # the helper ends with the session
            $gone = $null
            $sw = [Diagnostics.Stopwatch]::StartNew()
            while ($sw.Elapsed.TotalSeconds -lt 20) { if (@(Get-Helpers).Count -eq 0) { $gone = [math]::Round($sw.Elapsed.TotalSeconds, 1); break }; Start-Sleep -Milliseconds 500 }
            # the end event (1101) is written as the connection closes: wait until both are in the log
            $events = @(Wait-LabCondition -NoThrow -TimeoutSec 20 -Because 'the agent did not log events 1100 and 1101' -Until {
                    $e = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1100, 1101)
                    if (@($e | Where-Object Id -eq 1100).Count -gt 0 -and @($e | Where-Object Id -eq 1101).Count -gt 0) { , $e } } | Where-Object { $_ })
            $similar = if (@($r.images).Count -gt 0) { Compare-LabImage -A @($r.images)[0].path -B (Join-Path (Out 'session') 'console.png') } else { 0 }
            Save 'session' @{ R = $r; Helpers = $during; Console = $console; Gone = $gone; Events = $events; Similar = $similar; Resolution = $resolution; Shot = $shot }
        }

        It 'D-CON-08 frames arrive after Connected: a key frame at the guest''s resolution, the picture matches the console (>= 95 %)' -Tag 'D-CON-08' {
            States $r | Should -Contain 'Connected'
            $r.frames.keyFrames | Should -BeGreaterThan 0
            $img = @($r.images)[0]
            "$($img.width)x$($img.height)" | Should -Be $resolution
            $similar | Should -BeGreaterOrEqual 0.95
        }

        It 'D-CON-09 closed by RemoteHub: event 1101 with the duration, the helper gone within 5 s' -Tag 'D-CON-09' {
            @($events | Where-Object Id -eq 1100).Count | Should -BeGreaterThan 0
            @($events | Where-Object { $_.Id -eq 1101 -and $_.Message -match '\d{2}:\d{2}:\d{2}' }).Count | Should -BeGreaterThan 0
            $gone | Should -Not -BeNullOrEmpty
            $gone | Should -BeLessOrEqual 5
        }

        It 'D-CON-10 the helper runs as SYSTEM in the chosen session, in a job object' -Tag 'D-CON-10' {
            $during.Count | Should -Be 1
            $during[0].Session | Should -Be $console
            $during[0].Owner | Should -Match 'SYSTEM$'
            $during[0].InJob | Should -BeTrue
        }
    }

    Context 'protocol misuse' {
        BeforeAll {
            # Settings (6) before Start
            $notStart = Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out 'not-start') -Steps @(
                @{ do = 'connect' }, @{ do = 'rawJson'; type = 6; json = '{"monitor":0,"framesPerSecond":30,"quality":60}' }, @{ do = 'sleep'; seconds = 10 })
            # nothing at all for 5 minutes
            $idle = Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out 'no-start') -TimeoutSec 500 -Steps @(@{ do = 'connect' }, @{ do = 'sleep'; seconds = 320 })
            Save 'misuse' @{ NotStart = $notStart; Idle = $idle }
        }

        It 'D-CON-05 a first message that isn''t Start closes the connection' -Tag 'D-CON-05' {
            @($notStart.closes | Where-Object by -eq 'agent').Count | Should -BeGreaterThan 0
        }

        It 'D-CON-04 no Start within 5 minutes: the agent closes the connection' -Tag 'D-CON-04' {
            $c = @($idle.closes | Where-Object by -eq 'agent')
            $c.Count | Should -BeGreaterThan 0
            $c[0].ms | Should -BeGreaterThan 290000
        }
    }

    Context 'policy' {
        BeforeAll {
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock { if (-not (Get-LocalGroup 'RH Others' -ErrorAction SilentlyContinue)) { New-LocalGroup 'RH Others' } }
            Set-LabAgentPolicy -Name $VmName -Clear -Values @{ DesktopEnabled = 0 }
            $off = Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out 'off') -Steps @(@{ do = 'connect' })
            $offHealth = Get-LabAgentHealth -Name $VmName -Pin $pin
            Set-LabAgentPolicy -Name $VmName -Clear -Rights @{ Desktop = @('RH Others') }
            $noRight = Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out 'no-right') -Steps @(@{ do = 'connect' })
            $ps = Invoke-LabTool -Name $VmName -Op ps -Pin $pin -Params @{ script = '$PID' }
            Set-LabAgentPolicy -Name $VmName -Clear
            Save 'policy' @{ Off = $off; OffHealth = $offHealth; NoRight = $noRight; Ps = $ps }
        }

        It 'D-CON-06 DesktopEnabled=0: /v1/desktop 404, health without "desktop"' -Tag 'D-CON-06' {
            $off.connectError | Should -Not -BeNullOrEmpty
            $off.steps[0].detail.status | Should -Be 404
            @($offHealth.features) | Should -Not -Contain 'desktop'
            @($offHealth.features) | Should -Contain 'powershell'
        }

        It 'D-CON-07 Rights\Desktop without PSAdmin: 403 on the desktop, PowerShell still works' -Tag 'D-CON-07' {
            $noRight.steps[0].detail.status | Should -Be 403
            @($ps.result.output).Count | Should -Be 1
        }
    }

    Context 'sessions' {
        BeforeAll {
            # normaluser on the console, then disconnected: the console shows the sign-in screen, normaluser's session stays
            Set-LabAutoLogon -Name $VmName -Account User
            $null = Wait-LabAgent -Name $VmName -Pin $pin
            $null = Invoke-LabInSession -Name $VmName -Account User -ScriptBlock { tsdiscon.exe }
            $null = Wait-LabCondition -TimeoutSec 60 -Because "$($vm.UserAccount)'s session did not become Disconnected after tsdiscon" -Until {
                @(Get-LabSessions -Name $VmName | Where-Object { $_.State -eq 'Disconnected' -and $_.User -match "\\?$([regex]::Escape($vm.UserAccount))$" }).Count -gt 0 }
            $since = Get-LabGuestTime -Name $VmName
            $list = Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out 'sessions') -Steps @(@{ do = 'connect' }, @{ do = 'close' })
            $disc = @($list.hello.sessions | Where-Object { $_.user -match "\\?$([regex]::Escape($vm.UserAccount))$" -and $_.state -eq 'Disconnected' } | Select-Object -First 1)
            $start = if ($disc) {
                Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out 'disconnected') -Steps @(
                    @{ do = 'connect' }, @{ do = 'start'; session = [int]$disc[0].id; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Failed', 'Connected', 'WaitingForUser', 'Paused'); timeout = 30 })
            }
            $events = @(Wait-LabCondition -NoThrow -TimeoutSec 20 -Because 'the agent did not log event 2100' -Until {
                    $e = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2100)
                    if ($e.Count -gt 0) { , $e } } | Where-Object { $_ })
            Clear-LabAutoLogon -Name $VmName
            Save 'sessions' @{ List = $list; Start = $start; Events = $events }
        }

        It 'D-CON-02 the session list: console first, normaluser''s disconnected session listed as Disconnected, never session 0' -Tag 'D-CON-02' {
            $s = @($list.hello.sessions)
            $s[0].console | Should -BeTrue
            $disc | Should -Not -BeNullOrEmpty
            @($s | Where-Object id -eq 0) | Should -BeNullOrEmpty
        }

        It 'D-CON-03 Start on a disconnected session: Failed "can''t be shown", event 2100' -Tag 'D-CON-03' {
            $f = @($start.statuses | Where-Object state -eq 'Failed')
            $f.Count | Should -BeGreaterThan 0
            $f[0].message | Should -Match "can't be shown"
            $events.Count | Should -BeGreaterThan 0
        }
    }
}
