# C-AUTO (3) - automatic sign-in: what goes wrong between the agent, the credential provider and Windows
# (docs/test-lab-cases.md section 9.12, docs/credential-provider-design.md sections 6-8; AutoSignInBroker.cs,
# DesktopEndpoint.AutoSignInAsync, AutoSignIn.cs).
#
# Covers: C-AUTO-14 (only with a provider test build that signs in another account - skipped otherwise), C-AUTO-15,
#         C-AUTO-16, C-AUTO-17, C-AUTO-18, C-AUTO-19, C-AUTO-20, C-AUTO-21.
# Needs:  one VM (runs on the client and the Server VM alike), no RemoteHub UI. The agent MSI under test built with the
#         provider and signed (build-installer.ps1 -LabSigning -CredentialProvider). Every context starts from the
#         checkpoint (Reset-LabVm) with a fresh AUTOSIGNIN=1 install: the console's LogonUI must be a new one where a
#         context changes what it loads, and accounts / lockout / user rights changed here must not leak.
#
# What the code does (and this file checks):
#  - C-AUTO-14: the console's new session belongs to another SID -> signed out, 3101, Ended "couldn't be confirmed".
#    Needs a provider build that receives the agent's ticket but signs in another account (not in the repo; the Lab
#    configuration talks only to RemoteHubCpTestServer.exe, see Channel.cpp RHCP_LAB_SERVER, so it can't stand in).
#  - C-AUTO-15: the console changes between SignInReady and the ticket -> SessionChanged (Recheck right before the ticket).
#  - C-AUTO-16: LogonUI doesn't load the provider (its Credential Providers key "Disabled" = 1 - CheckInstallation
#    doesn't look at it) -> NoProvider after 3 s + Ctrl+Alt+Del + 20 s; a failed attempt's message still on the
#    sign-in screen -> the result never comes (50 s) -> "didn't take the sign-in".
#  - C-AUTO-17: the broker runs one sign-in at a time (_busy) -> the second gets Busy at once.
#  - C-AUTO-18: the pipe is created with FIRST_PIPE_INSTANCE -> a squatter turns the feature off (3102).
#  - C-AUTO-19: only LogonUI.exe from System32, as SYSTEM, in the console session is kept (CheckClient); the pipe's ACL
#    lets only SYSTEM open it, so the foreign clients here run as SYSTEM: in session 0, and in the console session.
#  - C-AUTO-20: Windows' NTSTATUS from LogonUI -> AutoSignInStatus.Describe -> SignInFailed + 2105.
#  - C-AUTO-21: the DLL must be signed by the same certificate as the agent (Authenticode.Signer).
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $cfg = Get-LabConfig
    $account = $vm.ConsoleAccount
    $clsid = '{718FFFD2-6671-4FA4-9FA8-86D1CD4D84C3}'
    $dll = 'C:\Program Files\RemoteHub Agent\RemoteHubCredentialProvider.dll'
    $labDir = 'C:\ProgramData\RemoteHubLab'
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "c-auto3-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "c-auto3-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Mark([string]$o, [string]$n) { @{ do = 'markFile'; path = (Join-Path $o "$n.txt") } }
    function Park([string]$o, [string]$n, [int]$t = 300) { @{ do = 'waitFile'; path = (Join-Path $o "$n.txt"); timeout = $t } }
    function Reached([string]$o, [string]$n, [int]$t = 300) { Wait-LabHostFile (Join-Path $o "$n.txt") -TimeoutSec $t }
    function Release([string]$o, [string]$n) { Set-Content (Join-Path $o "$n.txt") 'go' }
    function Shot([string]$name) { $p = Join-Path $EvidenceDir "c-auto3-$name.png"; $null = Get-LabScreenshot -Name $VmName -Path $p }
    # a harness "desktop" record: "State: message" per status, then errors
    function Text($r) { (@($r.statuses | ForEach-Object { "$($_.state): $($_.message)" }) + @($r.errors)) -join "`n" }
    # a harness "auto-sign-in" record (Invoke-LabAutoSignIn): statuses are "State: message" already
    function RunText($r) { (@($r.statuses) + @($r.error)) -join "`n" }
    function Run([string]$Pin, [hashtable]$Options = @{}) {
        try { Invoke-LabAutoSignIn -Name $VmName -Pin $Pin @Options } catch { [pscustomobject]@{ error = "$_"; statuses = @(); rights = $null; features = @() } }
    }
    # from the checkpoint (the console account signed in), the agent installed with AUTOSIGNIN=1; answers the pin
    function Fresh { Reset-LabVm -Name $VmName; (Install-LabAgent -Name $VmName -AutoSignIn $true).pin }
    # the policy an AUTOSIGNIN=1 install writes, a fresh agent (rate limit, cached checks), nobody at the console
    function Prepare([string]$Pin) {
        Set-LabAgentPolicy -Name $VmName -Clear -Values @{ NoUserBehavior = 'AutoSignIn' } -Rights @{ AutoSignIn = @('S-1-5-32-544') }
        $null = Restart-LabAgent -Name $VmName -Pin $Pin
        Disconnect-LabConsole -Name $VmName
    }
    # who owns an explorer.exe now (a signed-in desktop), per session
    function Desktops {
        Invoke-LabGuest -Name $VmName -ScriptBlock {
            @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | ForEach-Object {
                    $o = Invoke-CimMethod -InputObject $_ -MethodName GetOwner
                    [pscustomobject]@{ Session = [int]$_.SessionId; User = "$($o.Domain)\$($o.User)" }
                })
        }
    }
    # the start of an automatic sign-in that stops at SignInReady until the test releases "go" (the password by hand)
    function ParkedSignIn([string]$o, [int]$GoTimeout = 60) {
        @(
            @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'None'; autoSignIn = $true; sendSignIn = $false }
            @{ do = 'wait'; anyOf = @('SignInReady', 'SignInFailed', 'Failed', 'Denied'); timeout = 90 }, (Mark $o 'ready'), (Park $o 'go' $GoTimeout)
            @{ do = 'signIn' }, @{ do = 'wait'; anyOf = @('SignedIn', 'SignInFailed', 'Failed', 'Ended'); timeout = 150 }, @{ do = 'sleep'; seconds = 3 }, @{ do = 'close' })
    }
    # a scheduled task as SYSTEM that runs PowerShell code (encoded, no quoting trouble); not waited for
    function Start-SystemTask([string]$Task, [string]$Code) {
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Code))
        Invoke-LabGuest -Name $VmName -ArgumentList $Task, $encoded, $labDir -ScriptBlock {
            param($task, $encoded, $dir)
            $null = New-Item -ItemType Directory -Path $dir -Force
            $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded"
            $null = Register-ScheduledTask -TaskName $task -Action $action -User 'NT AUTHORITY\SYSTEM' -RunLevel Highest -Force
            Start-ScheduledTask -TaskName $task
        }
    }
    function Stop-SystemTask([string]$Task) {
        Invoke-LabGuest -Name $VmName -ArgumentList $Task -ScriptBlock {
            param($t)
            Stop-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $t -Confirm:$false -ErrorAction SilentlyContinue
        }
    }
    # a guest file's text once it exists (a task's answer); $null after the timeout
    function Wait-GuestFile([string]$Path, [int]$TimeoutSec = 60) {
        Invoke-LabGuest -Name $VmName -ArgumentList $Path, $TimeoutSec -ScriptBlock {
            param($p, $t)
            $until = (Get-Date).AddSeconds($t)
            while ((Get-Date) -lt $until) { if (Test-Path $p) { Start-Sleep -Milliseconds 300; return (Get-Content $p -Raw) }; Start-Sleep -Milliseconds 500 }
            $null
        }
    }
}

AfterAll {
    Get-Job | Where-Object State -eq 'Running' | Stop-Job
    Get-Job | Remove-Job -Force
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'c-auto3-agent-logs')
    # accounts, lockout, user rights and the provider's registration changed: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'C-AUTO3 <VmName>' -Tag Agent, 'C-AUTO' {

    Context 'sign-in not confirmed' {
        BeforeAll {
            # A Release-configured provider (same CLSID, signed with the lab certificate like the agent) built so that it
            # signs in rhother instead of the ticket's account - every lab account has the same password. Staged by hand.
            $otherBuild = Join-Path $cfg.LabRoot 'credential-provider-otheraccount\RemoteHubCredentialProvider.dll'
            $m = $null
            if (Test-Path $otherBuild) {
                $pin = Fresh
                $otherSid = New-LabGuestUser -Name $VmName -User 'rhother' -Admin
                Copy-LabFile -Name $VmName -Path $otherBuild -Destination "$labDir\cprov-other"
                Invoke-LabGuest -Name $VmName -ArgumentList $dll, "$labDir\cprov-other\RemoteHubCredentialProvider.dll" -ScriptBlock {
                    param($d, $other)
                    # the broker holds the DLL's check, LogonUI the DLL itself: renamed, not overwritten
                    Stop-Service RemoteHubAgent -Force
                    Move-Item $d "$d.rhlab" -Force
                    Copy-Item $other $d -Force
                    Start-Service RemoteHubAgent
                }
                Prepare $pin
                $m = [ordered]@{ OtherSid = $otherSid }
                $since = Get-LabGuestTime -Name $VmName
                $m.R = Invoke-LabDesktop -Name $VmName -Pin $pin -Account Console -Out (Out 'mismatch') -Steps @(
                    @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'None'; autoSignIn = $true }
                    @{ do = 'wait'; anyOf = @('Ended', 'SignedIn', 'SignInFailed', 'Failed'); timeout = 150 }, @{ do = 'sleep'; seconds = 10 }, @{ do = 'close' })
                Start-Sleep -Seconds 10
                $m.Mismatch = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 3101)
                $m.SignedInEvents = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1106)
                $m.Desktops = Desktops
                Save 'mismatch' $m
            }
        }

        It 'C-AUTO-14 the console''s new session has another SID: signed out, 3101, Ended "couldn''t be confirmed"' -Tag 'C-AUTO-14' {
            if (-not $m) {
                Set-ItResult -Skipped -Because "needs a provider test build that takes the agent's ticket and signs in another account (staged at $otherBuild); the Lab build only talks to RemoteHubCpTestServer.exe"
                return
            }
            Text $m.R | Should -Match "Ended: The automatic sign-in couldn't be confirmed, so it was undone\."
            Text $m.R | Should -Not -Match 'SignedIn:'
            ($m.Mismatch.Message -join "`n") | Should -Match "the console session \d+ has user $([regex]::Escape($m.OtherSid)), not .*$([regex]::Escape($account)) .* signed out"
            @($m.SignedInEvents) | Should -BeNullOrEmpty
            @($m.Desktops | Where-Object { $_.User -match '\\rhother$' }) | Should -BeNullOrEmpty -Because 'the other account was signed out at once'
        }
    }

    Context 'console changed before the ticket' {
        BeforeAll {
            $pin = Fresh
            Prepare $pin
            $c = [ordered]@{}
            $since = Get-LabGuestTime -Name $VmName
            $o = Out 'changed'
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps (ParkedSignIn $o)
            Reached $o 'ready' 150
            # Someone at the device signs in by hand (the console account, the tile the sign-in screen offers) - within
            # the 30 s the agent waits for RemoteHub's password after SignInReady.
            Send-LabCtrlAltDel -Name $VmName
            Start-Sleep -Seconds 2
            Send-LabText -Name $VmName -Text $cfg.Password
            Send-LabKey -Name $VmName -Key Enter
            $until = (Get-Date).AddSeconds(15)
            do { Start-Sleep -Seconds 1; $c.ByHand = @(Desktops) } while ($c.ByHand.Count -eq 0 -and (Get-Date) -lt $until)
            Release $o 'go'
            $c.R = Receive-LabDesktop $job
            Start-Sleep -Seconds 3
            $c.Refused = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2104)
            $c.Ticketed = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1106, 2105, 3101)
            $c.Desktops = Desktops
            Shot 'changed-console'
            Save 'changed' $c
        }

        # The broker checks again only after the provider's Claim (Recheck), and waits for the console's LogonUI first:
        # if LogonUI is already gone when the password arrives, the result may be NoProvider instead (see the report).
        It 'C-AUTO-15 signed in at the console between SignInReady and the ticket: SignInFailed "Someone is signed in", 2104 sessionChanged, no ticket' -Tag 'C-AUTO-15' {
            Text $c.R | Should -Match 'SignInReady:'
            @($c.ByHand).Count | Should -BeGreaterThan 0 -Because 'lab: the sign-in by hand must have happened before the password was sent'
            Text $c.R | Should -Match 'SignInFailed: Someone is signed in on the console now'
            ($c.Refused.Message -join "`n") | Should -Match 'Someone is signed in on the console now.*\(sessionChanged\)'
            @($c.Ticketed) | Should -BeNullOrEmpty -Because 'no ticket went out: Windows was never asked (no 1106 / 2105 / 3101)'
        }
    }

    Context 'provider never loads, sign-in screen takes nothing' {
        BeforeAll {
            $p = [ordered]@{}
            # LogonUI skips a provider whose key has Disabled = 1; the agent's installation check doesn't look at it
            $pin = Fresh
            Invoke-LabGuest -Name $VmName -ArgumentList $clsid -ScriptBlock {
                param($c) Set-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$c" -Name Disabled -Value 1 -Type DWord
            }
            Prepare $pin   # signed out: a new LogonUI that doesn't load it
            $since = Get-LabGuestTime -Name $VmName
            $p.NoProvider = Run $pin
            $p.NoProviderEvents = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2104)
            Shot 'noprovider-console'

            # a failed attempt's message on the sign-in screen: the provider takes the ticket, LogonUI does nothing with it
            $pin = Fresh
            Prepare $pin
            $p.Wrong = Run $pin @{ SignInPassword = 'not-the-password-' + [guid]::NewGuid().ToString('N').Substring(0, 6) }
            $since = Get-LabGuestTime -Name $VmName
            $p.Stuck = Run $pin   # the message is not closed this time
            $p.StuckEvents = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2104, 2105)
            Shot 'stuck-console'
            Send-LabKey -Name $VmName -Key Enter
            Save 'noprovider' $p
        }

        It 'C-AUTO-16 LogonUI doesn''t load the provider: "didn''t load" (2104 noProvider); no result in time: "didn''t take the sign-in"' -Tag 'C-AUTO-16' {
            $p.NoProvider.rights.autoSignIn | Should -BeTrue -Because 'the agent can''t tell before it waits for LogonUI'
            RunText $p.NoProvider | Should -Match "SignInFailed: The device's sign-in screen didn't load the RemoteHub credential provider\."
            ($p.NoProviderEvents.Message -join "`n") | Should -Match "didn't load the RemoteHub credential provider.*\(noProvider\)"
            RunText $p.Wrong | Should -Match 'SignInFailed: The password was wrong'
            RunText $p.Stuck | Should -Match "SignInFailed: The sign-in screen didn't take the sign-in\. If it shows a message"
            ($p.StuckEvents.Message -join "`n") | Should -Match "didn't take the sign-in.*\(timeout\)"
        }
    }

    Context 'two at once' {
        BeforeAll {
            $pin = Fresh
            Prepare $pin
            $t = [ordered]@{}
            $since = Get-LabGuestTime -Name $VmName
            $oa = Out 'twice-a'; $ob = Out 'twice-b'
            $ja = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $oa -Steps (ParkedSignIn $oa 120)
            $jb = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $ob -Steps (ParkedSignIn $ob 120)
            Reached $oa 'ready' 150
            Reached $ob 'ready' 150
            # both passwords at the same moment (within the 30 s of the first SignInReady)
            Release $oa 'go'; Release $ob 'go'
            $t.A = Receive-LabDesktop $ja
            $t.B = Receive-LabDesktop $jb
            $t.Refused = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2104)
            Save 'twice' $t
        }

        It 'C-AUTO-17 two connections ask at the same time: the second gets "Another automatic sign-in is running on this device."' -Tag 'C-AUTO-17' {
            Text $t.A | Should -Match 'SignInReady:'
            Text $t.B | Should -Match 'SignInReady:'
            $busy = @(@($t.A, $t.B) | Where-Object { (Text $_) -match 'SignInFailed: Another automatic sign-in is running on this device\.' })
            $busy.Count | Should -Be 1 -Because 'exactly one of the two is refused'
            $other = @(@($t.A, $t.B) | Where-Object { (Text $_) -notmatch 'Another automatic sign-in is running' })
            Text $other[0] | Should -Match 'SignedIn: Signed in as' -Because 'the first one goes on'
            ($t.Refused.Message -join "`n") | Should -Match 'Another automatic sign-in is running on this device\..*\(busy\)'
        }
    }

    Context 'pipe squatted' {
        BeforeAll {
            $pin = Fresh
            $q = [ordered]@{}
            # a process (here as SYSTEM) creates the pipe while the service is stopped, then the service starts
            $squat = @"
`$p = New-Object System.IO.Pipes.NamedPipeServerStream('RemoteHubAgent.AutoSignIn', [System.IO.Pipes.PipeDirection]::InOut, 1, [System.IO.Pipes.PipeTransmissionMode]::Message)
Set-Content '$labDir\squat.txt' 'up'
Start-Sleep -Seconds 1800
"@
            Invoke-LabGuest -Name $VmName -ScriptBlock { Stop-Service RemoteHubAgent -Force }
            $since = Get-LabGuestTime -Name $VmName
            Start-SystemTask 'RHLab-PipeSquat' $squat
            $q.Squatting = Wait-GuestFile "$labDir\squat.txt" 60
            Invoke-LabGuest -Name $VmName -ScriptBlock { Start-Service RemoteHubAgent }
            $null = Wait-LabAgent -Name $VmName -Pin $pin
            Disconnect-LabConsole -Name $VmName
            $q.R = Run $pin
            $q.Unavailable = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 3102)
            Stop-SystemTask 'RHLab-PipeSquat'
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            Save 'squat' $q
        }

        It 'C-AUTO-18 the pipe created by another process first: 3102 "the pipe ... couldn''t be created", Hello autoSignIn false' -Tag 'C-AUTO-18' {
            $q.Squatting | Should -Match 'up' -Because 'lab: the squatter must hold the pipe before the service starts'
            ($q.Unavailable.Message -join "`n") | Should -Match "Automatic sign-in unavailable: the pipe RemoteHubAgent\.AutoSignIn couldn't be created \("
            $q.R.rights.autoSignIn | Should -BeFalse
            @($q.R.features) | Should -Not -Contain 'autoSignIn'
            RunText $q.R | Should -Match "isn't available on this device: the pipe RemoteHubAgent\.AutoSignIn couldn't be created"
        }
    }

    Context 'foreign pipe clients' {
        BeforeAll {
            $pin = Fresh
            Prepare $pin
            $f = [ordered]@{}
            # the client: connects, reads what comes (0 bytes once the service drops it), writes the outcome
            function ClientCode([string]$Tag) {
                @"
`$c = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'RemoteHubAgent.AutoSignIn', [System.IO.Pipes.PipeDirection]::InOut)
try { `$c.Connect(10000); `$r = 'connected' } catch { `$r = 'connect failed: ' + `$_.Exception.Message }
if (`$c.IsConnected) { try { `$b = New-Object byte[] 64; `$n = `$c.Read(`$b, 0, 64); `$r += "; read `$n" } catch { `$r += '; read failed: ' + `$_.Exception.Message } }
`$c.Dispose()
Set-Content '$labDir\pipe-$Tag.txt' ("pid `$PID session " + (Get-Process -Id `$PID).SessionId + ': ' + `$r)
"@
            }
            $since = Get-LabGuestTime -Name $VmName
            # 1. SYSTEM in session 0 (a scheduled task): not the console's session
            Start-SystemTask 'RHLab-PipeClient0' (ClientCode 'session0')
            $f.Session0 = Wait-GuestFile "$labDir\pipe-session0.txt" 60
            Stop-SystemTask 'RHLab-PipeClient0'
            # 2. SYSTEM in the console session (the sign-in screen's session), not LogonUI: the task starts PowerShell
            #    there with its own token moved to that session (TokenSessionId), as a service would
            $client = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes((ClientCode 'console')))
            $launch = @"
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RhLabLaunch {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo { public int cb; public string reserved, desktop, title; public int x, y, w, h, cx, cy, fill, flags; public short show, reserved2; public IntPtr reserved3, stdIn, stdOut, stdErr; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr process, thread; public int pid, tid; }
    [DllImport("ntdll.dll")] private static extern int RtlAdjustPrivilege(int privilege, bool enable, bool thread, out bool was);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern int WTSGetActiveConsoleSessionId();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, int access, IntPtr attributes, int level, int type, out IntPtr copy);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int infoClass, ref int value, int length);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, int flags, IntPtr env, string dir, ref StartupInfo si, out ProcessInfo pi);
    public static string Start(string cmd) {
        bool was;
        foreach (var p in new[] { 3, 5, 7 }) RtlAdjustPrivilege(p, true, false, out was); // AssignPrimaryToken, IncreaseQuota, Tcb
        IntPtr token, copy;
        if (!OpenProcessToken(GetCurrentProcess(), 0x01EF, out token)) return "OpenProcessToken " + Marshal.GetLastWin32Error();
        if (!DuplicateTokenEx(token, 0x02000000, IntPtr.Zero, 2, 1, out copy)) return "DuplicateTokenEx " + Marshal.GetLastWin32Error();
        var session = WTSGetActiveConsoleSessionId();
        if (!SetTokenInformation(copy, 12, ref session, 4)) return "SetTokenInformation " + Marshal.GetLastWin32Error();
        var si = new StartupInfo(); si.cb = Marshal.SizeOf(typeof(StartupInfo)); si.desktop = "winsta0\\default";
        ProcessInfo pi;
        if (!CreateProcessAsUserW(copy, null, cmd, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, null, ref si, out pi)) return "CreateProcessAsUser " + Marshal.GetLastWin32Error();
        return "started pid " + pi.pid + " in session " + session;
    }
}
'@
Set-Content '$labDir\launch-console.txt' ([RhLabLaunch]::Start('powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand $client'))
"@
            Start-SystemTask 'RHLab-PipeLaunch' $launch
            $f.Launch = Wait-GuestFile "$labDir\launch-console.txt" 90
            $f.Console = Wait-GuestFile "$labDir\pipe-console.txt" 60
            Stop-SystemTask 'RHLab-PipeLaunch'
            Start-Sleep -Seconds 3
            $f.Log = @(Get-LabAgentLog -Name $VmName -Since $since | Where-Object { $_ -match 'Automatic sign-in: pipe client refused' })
            # 3. the console's own LogonUI still gets the next automatic sign-in
            Disconnect-LabConsole -Name $VmName
            $f.Later = Run $pin
            Save 'foreign' $f
        }

        # Not covered here: an RDP session's LogonUI on Server ("session N isn't the console") - needs an RDP connection
        # to the device's sign-in screen from another machine while the console waits.
        It 'C-AUTO-19 pipe clients that aren''t the console''s LogonUI are refused and logged; a later automatic sign-in still works' -Tag 'C-AUTO-19' {
            $f.Session0 | Should -Match 'connected' -Because 'lab: the SYSTEM client in session 0 reached the pipe'
            $f.Session0 | Should -Match 'read 0' -Because 'the service drops it'
            ($f.Log -join "`n") | Should -Match "Automatic sign-in: pipe client refused \(session 0 isn't the console\)"
            $f.Launch | Should -Match 'started pid' -Because 'lab: a SYSTEM process in the console session'
            $f.Console | Should -Match 'connected; read 0'
            ($f.Log -join "`n") | Should -Match "Automatic sign-in: pipe client refused \(.*\\powershell\.exe isn't LogonUI\)"
            RunText $f.Later | Should -Match 'SignedIn: Signed in as'
        }
    }

    Context 'Windows refusals' {
        BeforeAll {
            $pin = Fresh
            Prepare $pin
            $w = [ordered]@{}
            # One fresh administrator per refusal (the rate limit counts per account). The agent signs each one in over
            # the network first; the account is changed only while the sign-in waits at SignInReady, so only LogonUI
            # meets the refusal.
            $changes = [ordered]@{
                rhaslock     = {
                    param($u)
                    $null = net.exe accounts /lockoutthreshold:3 /lockoutwindow:30 /lockoutduration:30
                    if (-not ('RhLabLogon' -as [type])) {
                        Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class RhLabLogon { [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool LogonUserW(string u, string d, string p, int type, int provider, out IntPtr token); }'
                    }
                    foreach ($i in 1..3) { $h = [IntPtr]::Zero; $null = [RhLabLogon]::LogonUserW($u, '.', 'not-the-password', 3, 0, [ref]$h) }
                    "locked out: $([bool](Get-CimInstance Win32_UserAccount -Filter "LocalAccount=True AND Name='$u'").Lockout)"
                }
                rhasdisabled = { param($u) Disable-LocalUser -Name $u; "enabled: $((Get-LocalUser $u).Enabled)" }
                rhasdeny     = {
                    param($u)
                    # "Deny log on locally" for this account only (a fresh security database with just this right)
                    $sid = (Get-LocalUser $u).SID.Value
                    $inf = 'C:\Windows\Temp\rhlab-deny.inf'; $db = 'C:\Windows\Temp\rhlab-deny.sdb'
                    Set-Content $inf -Encoding Unicode -Value @('[Unicode]', 'Unicode=yes', '[Version]', 'signature="$CHICAGO$"', 'Revision=1', '[Privilege Rights]', "SeDenyInteractiveLogonRight = *$sid")
                    $null = secedit.exe /configure /db $db /cfg $inf /areas USER_RIGHTS
                    $check = 'C:\Windows\Temp\rhlab-deny-check.inf'
                    $null = secedit.exe /export /cfg $check /areas USER_RIGHTS
                    @(Get-Content $check | Where-Object { $_ -like 'SeDenyInteractiveLogonRight*' }) -join ' '
                }
                rhasexpired  = {
                    param($u)
                    $a = [ADSI]"WinNT://$env:COMPUTERNAME/$u,user"
                    $a.UserFlags = [int]$a.UserFlags.Value -band (-bnot 0x10000)   # no "password never expires"
                    $a.PasswordExpired = 1
                    $a.SetInfo()
                    "expired: $($a.PasswordExpired)"
                }
            }
            foreach ($u in $changes.Keys) { $null = New-LabGuestUser -Name $VmName -User $u -Admin }
            $since = Get-LabGuestTime -Name $VmName
            foreach ($u in $changes.Keys) {
                Disconnect-LabConsole -Name $VmName
                $o = Out "refused-$u"
                $job = Start-LabDesktop -Name $VmName -Pin $pin -Account $u -Out $o -Steps (ParkedSignIn $o)
                $x = [ordered]@{}
                try {
                    Reached $o 'ready' 150
                    $x.Changed = Invoke-LabGuest -Name $VmName -ArgumentList $u -ScriptBlock $changes[$u]
                }
                finally { Release $o 'go' }
                $x.R = Receive-LabDesktop $job
                Shot "refused-$u"
                # Windows' message on the sign-in screen: OK (the expired password, last, leaves the change-password screen)
                Send-LabKey -Name $VmName -Key Enter
                Start-Sleep -Seconds 3
                $w[$u] = $x
            }
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock { net.exe accounts /lockoutthreshold:0 }
            $w.Failed = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2105)
            Save 'windows' $w
        }

        # Windows hands LogonUI an NTSTATUS and a substatus; a disabled account may come as 0xC000006E (account
        # restriction) with the substatus 0xC0000072 - the message must still name the reason.
        It 'C-AUTO-20 locked / disabled / expired password / "Deny log on locally": SignInFailed with Windows'' reason, 2105 with the status' -Tag 'C-AUTO-20' {
            $expected = [ordered]@{
                rhaslock     = @{ Text = 'The account is locked\.'; Status = 'C0000234' }
                rhasdisabled = @{ Text = 'The account is disabled\.'; Status = 'C0000072' }
                rhasdeny     = @{ Text = 'may not sign in on this device.s screen'; Status = 'C000015B' }
                rhasexpired  = @{ Text = 'The password has expired and must be changed at the device\.'; Status = 'C0000224|C0000071' }
            }
            foreach ($u in $expected.Keys) {
                Text $w[$u].R | Should -Match 'SignInReady:' -Because "$u reached the sign-in"
                Text $w[$u].R | Should -Match "SignInFailed: $($expected[$u].Text)" -Because $u
                Text $w[$u].R | Should -Not -Match 'SignedIn:' -Because $u
                (@($w.Failed | Where-Object { $_.Message -match "\\$u from " }).Message -join "`n") | Should -Match "Windows reported .*0x($($expected[$u].Status))" -Because "2105 for $u"
            }
        }
    }

    Context 'DLL signed by another certificate' {
        BeforeAll {
            $pin = Fresh
            # another code-signing certificate, trusted on the device (Root), signs a copy of the provider in its place
            $s = Invoke-LabGuest -Name $VmName -ArgumentList $dll -ScriptBlock {
                param($d)
                $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=RemoteHub Lab Other Publisher' -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddDays(7)
                $cer = 'C:\Windows\Temp\rhlab-other-publisher.cer'
                $null = Export-Certificate -Cert $cert -FilePath $cer
                foreach ($store in 'Root', 'TrustedPublisher') { $null = Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\LocalMachine\$store" }
                Stop-Service RemoteHubAgent -Force
                Move-Item $d "$d.rhlab" -Force
                Copy-Item "$d.rhlab" $d -Force
                $signed = Set-AuthenticodeSignature -FilePath $d -Certificate $cert -HashAlgorithm SHA256
                Start-Service RemoteHubAgent
                $sig = Get-AuthenticodeSignature $d
                $agentExe = (Get-CimInstance Win32_Service -Filter "Name='RemoteHubAgent'").PathName.Trim('"')
                [pscustomobject]@{
                    SignStatus = "$($signed.Status)"; Status = "$($sig.Status)"; Signer = $sig.SignerCertificate.Thumbprint
                    Other = $cert.Thumbprint; AgentSigner = (Get-AuthenticodeSignature $agentExe).SignerCertificate.Thumbprint
                }
            }
            $null = Wait-LabAgent -Name $VmName -Pin $pin
            Disconnect-LabConsole -Name $VmName
            $r = Run $pin
            Invoke-LabGuest -Name $VmName -ArgumentList $dll -ScriptBlock {
                param($d) Stop-Service RemoteHubAgent -Force; Move-Item $d "$d.other" -Force; Move-Item "$d.rhlab" $d -Force; Start-Service RemoteHubAgent
            }
            $null = Wait-LabAgent -Name $VmName -Pin $pin
            Save 'other-cert' @{ Signature = $s; Run = $r }
        }

        It 'C-AUTO-21 trusted, but not signed by the agent''s certificate: "isn''t signed by the same certificate as the agent", Hello autoSignIn false' -Tag 'C-AUTO-21' {
            $s.Status | Should -Be 'Valid' -Because 'lab: the copy is signed by a trusted certificate'
            $s.Signer | Should -Be $s.Other
            $s.Signer | Should -Not -Be $s.AgentSigner
            $r.rights.autoSignIn | Should -BeFalse
            RunText $r | Should -Match "RemoteHubCredentialProvider\.dll isn't signed by the same certificate as the agent"
        }
    }
}
