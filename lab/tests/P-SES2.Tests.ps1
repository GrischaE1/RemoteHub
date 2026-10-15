# P-SES (more) / P-CLM - RemoteHub's PowerShell connection through the agent beyond the basics in P-SES.Tests.ps1:
# large arguments and output, a message over the agent's 16 MiB limit, a network drop during a session (the agent's
# WebSocket keep-alive of 30 + 30 s ends it), prompts answered through the PowerShell tab's host (PsConsoleHost), and
# Constrained Language Mode (on a CLM VM only). See docs/test-lab-cases.md section 7.
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "p-ses2-$name.json") -Encoding UTF8 }
    Reset-LabVm -Name $VmName
    $install = Install-LabAgent -Name $VmName
    $pin = $install.pin
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'p-ses2-agent-logs')
}

Describe 'P-SES2 <VmName>' -Tag Agent, 'P-SES', 'P-CLM' {

    Context 'large data' {
        BeforeAll {
            $big = Invoke-LabTool -Name $VmName -Op ps-big -Pin $pin -Params @{ 'in-mb' = 2; 'out-mb' = 10 }
            # one raw 17 MiB message (RemoteHub sends 1 MiB fragments, so only a raw probe can)
            $since = Get-LabGuestTime -Name $VmName
            $tooBig = Invoke-LabProbe -Name $VmName -Kind big -Params @{ pin = $pin; user = $vm.AdminAccount; password = (Get-LabConfig).Password; mb = 17 }
            Start-Sleep -Seconds 2
            $tooBigEvents = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1001)
            $health = Get-LabAgentHealth -Name $VmName -Pin $pin
            $after = Invoke-LabTool -Name $VmName -Op ps -Pin $pin -Params @{ script = '$PID' }
            Save 'large' @{ Big = $big; TooBig = $tooBig; TooBigEvents = $tooBigEvents; Health = $health; After = $after }
        }

        It 'P-SES-04 a 2 MB argument and 10 MB of output round-trip unchanged' -Tag 'P-SES-04' {
            $big.result.deviceChars | Should -Be $big.result.sentChars
            $big.result.hashEqual | Should -BeTrue
            $big.result.receivedOk | Should -BeTrue
        }

        It 'P-SES-05 a message over 16 MiB ends the session with "Message too large", the agent stays healthy' -Tag 'P-SES-05' {
            $tooBig.ended | Should -BeTrue
            @($tooBigEvents | Where-Object { $_.Message -match 'Message too large' }).Count | Should -BeGreaterThan 0
            $health.deviceId | Should -Be $install.deviceId
            @($after.result.output).Count | Should -Be 1
        }
    }

    Context 'prompts through the console host' {
        BeforeAll {
            $script = @'
$name = Read-Host 'Your name'
$choice = $Host.UI.PromptForChoice('Pick', 'Which one?', [Management.Automation.Host.ChoiceDescription[]]@('&Yes', '&No'), 1)
$cred = Get-Credential -UserName 'labuser' -Message 'For the lab'
$secret = Read-Host 'Secret' -AsSecureString
"name=$name"
"choice=$choice"
"user=$($cred.UserName)"
"password=$($cred.GetNetworkCredential().Password)"
"secret=$([Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)))"
'@
            $r = Invoke-LabTool -Name $VmName -Op ps-host -Pin $pin -Params @{ script = $script; lines = 'Alice|Y'; secrets = 'pw-one|pw-two' }
            Save 'prompts' $r
        }

        It 'P-SES-08 Read-Host, choices, Get-Credential and secure input are answered through the host''s callbacks' -Tag 'P-SES-08' {
            $out = @($r.result.output)
            $out | Should -Contain 'name=Alice'
            $out | Should -Contain 'choice=0'
            $out | Should -Contain 'user=labuser'
            $out | Should -Contain 'password=pw-one'
            $out | Should -Contain 'secret=pw-two'
            @($r.result.secretPrompts).Count | Should -Be 2
            $r.result.written | Should -Match 'Your name'
            $r.result.written | Should -Match '\[Y\] Yes'
        }
    }

    Context 'network drop' {
        BeforeAll {
            $since = Get-LabGuestTime -Name $VmName
            # a session that would run 5 minutes, in the background
            $job = Start-Job -ArgumentList (Get-LabVmAddress -Name $VmName), $pin, $vm.AdminAccount, (Get-LabConfig).Password, (Join-Path (Get-LabConfig).LabRoot 'harness\RemoteHub.LabHarness.exe') -ScriptBlock {
                param($ip, $pin, $user, $pw, $exe)
                & $exe run --host $ip --port 58732 --pin $pin --user $user --password $pw --script 'Start-Sleep -Seconds 300'
            }
            $sessionPid = $null
            for ($i = 0; $i -lt 30 -and -not $sessionPid; $i++) {
                Start-Sleep -Seconds 2
                $started = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1000)
                if ($started -and $started[-1].Message -match 'PowerShell PID (\d+)') { $sessionPid = [int]$Matches[1] }
            }
            $switches = Disconnect-LabNetwork -Name $VmName
            try {
                $cutAt = Get-Date
                $gone = $null
                while (((Get-Date) - $cutAt).TotalSeconds -lt 120) {
                    Start-Sleep -Seconds 5
                    $alive = Invoke-LabGuest -Name $VmName -ArgumentList $sessionPid -ScriptBlock { param($p) [bool](Get-Process -Id $p -ErrorAction SilentlyContinue) }
                    if (-not $alive) { $gone = [math]::Round(((Get-Date) - $cutAt).TotalSeconds); break }
                }
                $ended = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1001)
            }
            finally { Connect-LabNetwork -Name $VmName -Switches $switches }
            Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue
            Save 'drop' @{ Pid = $sessionPid; Gone = $gone; Ended = $ended }
        }

        It 'P-SES-06 a network drop ends the session within ~60 s: powershell.exe gone, event 1001 "connection lost"' -Tag 'P-SES-06' {
            $sessionPid | Should -Not -BeNullOrEmpty
            $gone | Should -Not -BeNullOrEmpty -Because 'powershell.exe must end without the network'
            $gone | Should -BeLessOrEqual 75
            @($ended | Where-Object { $_.Message -match 'connection lost' }).Count | Should -BeGreaterThan 0
        }
    }

    Context 'Constrained Language Mode' {
        BeforeAll {
            $mode = Invoke-LabGuest -Name $VmName -ScriptBlock { [string](Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard -ErrorAction SilentlyContinue).CodeIntegrityPolicyEnforcementStatus }
            $r = Invoke-LabTool -Name $VmName -Op ps -Pin $pin -Params @{ script = '$ExecutionContext.SessionState.LanguageMode.ToString()' }
            Save 'clm' @{ Enforcement = $mode; Session = $r }
        }

        It 'P-CLM-01 on a CLM VM the session opens in ConstrainedLanguage' -Tag 'P-CLM-01' {
            if ($vm.Id -notlike '*-CLM-*') { Set-ItResult -Skipped -Because 'needs the CLM VM (WDAC policy forcing Constrained Language Mode)'; return }
            @($r.result.output)[0] | Should -Be 'ConstrainedLanguage'
        }
    }
}
