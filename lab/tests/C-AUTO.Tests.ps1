# C-AUTO - automatic sign-in end to end, phases 2 and 3 (docs/credential-provider-design.md section 13 build order): the agent
# service hands the ticket to the credential provider in the console's LogonUI; RemoteHub's desktop client asks for it
# and sends the password when the agent says SignInReady. The agent MSI under test must be built with the provider and
# signed (build-installer.ps1 -LabSigning -CredentialProvider).
#
#  1. Installed with AUTOSIGNIN=1 (the install dialog's "Allow automatic sign-in"): the agent wrote the policy values;
#     IT setting NoUserBehavior = SignInScreen turns it off again (the right isn't offered, a request is refused).
#  2. Refused in view-only; refused when RemoteHub never sends the password (30 s).
#  3. A wrong password: Windows refuses it, the reason reaches RemoteHub, the console stays at the sign-in screen.
#  4. The right password: signed in, the console account's desktop, audit 1106.
#  5. The password never appears in the agent's log or the Application log.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $cfg = Get-LabConfig

    function Get-Console {
        Invoke-LabGuest -Name $VmName -ScriptBlock {
            [pscustomobject]@{
                User     = (Get-CimInstance Win32_ComputerSystem).UserName
                Explorer = @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'").Count
            }
        }
    }
    function Get-AutoSignInEvents {
        Invoke-LabGuest -Name $VmName -ScriptBlock {
            @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'RemoteHub Agent'; Id = 1106, 2104, 2105, 3101, 3102 } -ErrorAction SilentlyContinue |
                ForEach-Object { "$($_.Id): $($_.Message)" })
        }
    }
    function Run([hashtable]$Options = @{}) {
        try { Invoke-LabAutoSignIn -Name $VmName -Pin $install.pin @Options } catch { [pscustomobject]@{ error = "$_"; statuses = @() } }
    }

    # ---- 1. Installed with the option: the policy values are the agent's; IT can still turn it off
    # -Clean: the policy values must be the ones the install itself just wrote
    $init = Initialize-LabVm -Name $VmName -Agent -AutoSignIn $true -Clean
    $install = $init.Install
    $policyAfterInstall = Invoke-LabGuest -Name $VmName -ScriptBlock {
        [pscustomobject]@{
            Behavior = (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent' -ErrorAction SilentlyContinue).NoUserBehavior
            Right    = @((Get-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent\Rights' -ErrorAction SilentlyContinue).AutoSignIn)
            Marker   = Test-Path 'C:\ProgramData\RemoteHub\Agent\autosignin.policy'
        }
    }
    Disconnect-LabConsole -Name $VmName
    Set-LabAgentPolicy -Name $VmName -Values @{ NoUserBehavior = 'SignInScreen' }   # IT says no
    $noPolicy = Run
    Set-LabAgentPolicy -Name $VmName -Values @{ NoUserBehavior = 'AutoSignIn' }
    $policyAfterReset = Invoke-LabGuest -Name $VmName -ScriptBlock {
        [pscustomobject]@{
            Behavior = (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent' -ErrorAction SilentlyContinue).NoUserBehavior
            Right    = @((Get-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent\Rights' -ErrorAction SilentlyContinue).AutoSignIn)
            Subkeys  = @(Get-ChildItem 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent' -ErrorAction SilentlyContinue | ForEach-Object PSChildName)
        }
    }

    # ---- 2. Refusals that never reach Windows
    $viewOnly = Run @{ ViewOnly = $true }
    $noRequest = Run @{ NoRequest = $true }

    # ---- 3. Wrong password
    $wrong = Run @{ SignInPassword = 'not-the-password-' + [guid]::NewGuid().ToString('N').Substring(0, 6) }
    $afterWrong = Get-Console
    Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir 'c-auto-after-wrong.png') | Out-Null

    # ---- 4. The right password - after closing the failed attempt's message, as the operator does through the picture
    Send-LabKey -Name $VmName -Key Enter
    Start-Sleep -Seconds 3
    $right = Run
    $desktopError = $null
    try { Wait-LabDesktop -Name $VmName -Account Console -TimeoutSec 120 } catch { $desktopError = "$_" }
    $afterRight = Get-Console
    Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir 'c-auto-signed-in.png') | Out-Null
    $events = Get-AutoSignInEvents

    # ---- 5. No password in the logs
    $leaks = Invoke-LabGuest -Name $VmName -ArgumentList $cfg.Password -ScriptBlock {
        param($pw)
        $files = @(Get-ChildItem 'C:\ProgramData\RemoteHub\Agent\logs' -Filter *.log -ErrorAction SilentlyContinue |
            Where-Object { Select-String -Path $_.FullName -SimpleMatch -Pattern $pw -Quiet } | ForEach-Object Name)
        $eventHits = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'RemoteHub Agent' } -MaxEvents 500 -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -and $_.Message.Contains($pw) } | ForEach-Object Id)
        [pscustomobject]@{ Files = $files; Events = $eventHits }
    }

    @{ Install = $install; PolicyAfterInstall = $policyAfterInstall; PolicyAfterReset = $policyAfterReset; NoPolicy = $noPolicy; ViewOnly = $viewOnly; NoRequest = $noRequest; Wrong = $wrong; AfterWrong = $afterWrong
       Right = $right; DesktopError = $desktopError; AfterRight = $afterRight; Events = $events; Leaks = $leaks } |
        ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir 'c-auto-state.json') -Encoding UTF8
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'c-auto-agent') -ErrorAction SilentlyContinue
    # the console was signed out and in again by the credential provider: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'C-AUTO <VmName>' -Tag Agent, 'C-AUTO' {

    It 'C-AUTO-01 the install option allows it by policy; IT turning NoUserBehavior off stops it' -Tag 'C-AUTO-01' {
        $policyAfterInstall.Behavior | Should -Be 'AutoSignIn'
        $policyAfterInstall.Right | Should -Be @('S-1-5-32-544')
        $policyAfterInstall.Marker | Should -Be $true
        $noPolicy.rights.AutoSignIn | Should -Be $false
        ($noPolicy.statuses -join "`n") | Should -Match 'SignInFailed: Automatic sign-in is turned off on this device'
    }

    It 'C-AUTO-02 refuses in a view-only connection' -Tag 'C-AUTO-02' {
        $viewOnly.rights.AutoSignIn | Should -Be $true
        ($viewOnly.statuses -join "`n") | Should -Match "SignInFailed: Automatic sign-in isn't available in a view-only connection"
    }

    It 'C-AUTO-03 gives up when RemoteHub never sends the password' -Tag 'C-AUTO-03' {
        ($noRequest.statuses -join "`n") | Should -Match 'SignInReady'
        ($noRequest.statuses -join "`n") | Should -Match "SignInFailed: RemoteHub didn't send the sign-in in time"
    }

    It 'C-AUTO-04 reports a wrong password and stays on the sign-in screen' -Tag 'C-AUTO-04' {
        ($wrong.statuses -join "`n") | Should -Match 'SigningIn'
        ($wrong.statuses -join "`n") | Should -Match 'SignInFailed: The password was wrong'
        $afterWrong.Explorer | Should -Be 0
        ($events -join "`n") | Should -Match '2105: Automatic sign-in failed'
    }

    It 'C-AUTO-05 signs the console account in with the right password' -Tag 'C-AUTO-05' {
        ($right.statuses -join "`n") | Should -Match 'SignInReady'
        ($right.statuses -join "`n") | Should -Match 'SignedIn: Signed in as'
        $desktopError | Should -BeNullOrEmpty
        $afterRight.User | Should -Match "\\$([regex]::Escape($vm.ConsoleAccount))$"
        ($events -join "`n") | Should -Match '1106: Automatic sign-in'
        ($events -join "`n") | Should -Not -Match '3101|3102'
    }

    It 'C-AUTO-06 never writes the password to the agent log or the Application log' -Tag 'C-AUTO-06' {
        @($leaks.Files) | Should -BeNullOrEmpty
        @($leaks.Events) | Should -BeNullOrEmpty
    }
}
