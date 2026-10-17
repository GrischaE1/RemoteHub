# C-PROV - automatic sign-in, phase 1 (credential provider spike, docs/credential-provider-design.md section 13 build order).
# The agent MSI under test must be built with the provider (build-installer.ps1 -LabSigning -CredentialProvider), and
# the provider's lab build staged (build-credential-provider.ps1 -Configuration Lab); Start-LabRun.ps1 copies both.
#
#  1. Install the agent like RemoteHub does, with AUTOSIGNIN=1: the provider is installed, registered and signed.
#  2. Inert: with the provider registered and no ticket, signing out and back in on the console works as before.
#  3. Sign-in: the Lab DLL with the test server (standing in for the agent service) signs the console account in.
#  4. Installer: an update (a rebuild of the same version, New-LabRebuiltMsi) without AUTOSIGNIN keeps the provider;
#     another one with AUTOSIGNIN=0 removes the DLL and both keys.
#  5. RemoteHub's "Install / Remove credential provider" (AgentInstaller.ChangeCredentialProvider over the agent):
#     refused on another build; on the build under test it adds and removes the feature while the agent keeps running.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $cfg = Get-LabConfig
    $clsid = '{718FFFD2-6671-4FA4-9FA8-86D1CD4D84C3}'
    $guestDir = 'C:\ProgramData\RemoteHubLab\cprov'
    $labBuild = Join-Path $cfg.LabRoot 'credential-provider'
    $work = Join-Path $cfg.LabRoot "work\c-prov-$VmName"

    # An update as RemoteHub runs it over remoting (identity kept), with a rebuild of the MSI under test.
    function Update-AgentRebuilt([string]$Tag, [Nullable[bool]]$AutoSignIn) {
        $rebuilt = New-LabRebuiltMsi -Source (Get-LabAgentMsi) -Path (Join-Path $work "rebuilt-$Tag\$(Split-Path (Get-LabAgentMsi) -Leaf)")
        $installArgs = @{ Name = $VmName; Msi = $rebuilt.FullName; KeepIdentity = $true }
        if ($null -ne $AutoSignIn) { $installArgs.AutoSignIn = $AutoSignIn }
        try { $null = Install-LabAgent @installArgs } catch { "$_" }
    }

    function Get-ProviderState {
        Invoke-LabGuest -Name $VmName -ArgumentList $clsid -ScriptBlock {
            param($clsid)
            $dll = 'C:\Program Files\RemoteHub Agent\RemoteHubCredentialProvider.dll'
            $sig = if (Test-Path $dll) { Get-AuthenticodeSignature $dll } else { $null }
            [pscustomobject]@{
                Dll          = Test-Path $dll
                Signature    = if ($sig) { "$($sig.Status)" } else { $null }
                Provider     = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$clsid" -ErrorAction SilentlyContinue).'(default)'
                InprocServer = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$clsid\InprocServer32" -ErrorAction SilentlyContinue).'(default)'
                Threading    = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$clsid\InprocServer32" -ErrorAction SilentlyContinue).ThreadingModel
                Behavior     = (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent' -ErrorAction SilentlyContinue).NoUserBehavior
                Right        = @((Get-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent\Rights' -ErrorAction SilentlyContinue).AutoSignIn)
            }
        }
    }

    # ---- 1. Install with AUTOSIGNIN=1
    Reset-LabVm -Name $VmName
    $installError = $null
    try { $install = Install-LabAgent -Name $VmName -AutoSignIn $true } catch { $installError = "$_" }
    $registered = Get-ProviderState
    Invoke-LabGuest -Name $VmName -ArgumentList $guestDir -ScriptBlock { param($d) $null = New-Item -ItemType Directory -Path $d -Force }

    # ---- 2. Inert: sign out, sign in by hand with the Release provider registered
    Disconnect-LabConsole -Name $VmName
    Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir 'c-prov-signin-screen.png') | Out-Null
    $inertError = $null
    try { Unlock-LabSession -Name $VmName -Account Console -TimeoutSec 120 }
    catch { $inertError = "$_" }

    # ---- 3. Sign-in through the Lab DLL and the test server
    $signInError = $null
    $serverLog = $null
    if (Test-Path (Join-Path $labBuild 'RemoteHubCredentialProvider.Lab.dll')) {
        Copy-LabFile -Name $VmName -Path (Join-Path $labBuild '*') -Destination 'C:\Program Files\RemoteHub Agent'
        Invoke-LabGuest -Name $VmName -ArgumentList $clsid -ScriptBlock {
            param($clsid)
            Set-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$clsid\InprocServer32" -Name '(default)' `
                -Value 'C:\Program Files\RemoteHub Agent\RemoteHubCredentialProvider.Lab.dll'
            # The agent owns the pipe since phase 2: the test server can only have it while the service is stopped.
            Stop-Service RemoteHubAgent -Force
        }
        Disconnect-LabConsole -Name $VmName
        # The ticket: the console account with the lab password; the server reads and deletes it.
        Invoke-LabGuest -Name $VmName -ArgumentList $guestDir, $vm.ConsoleAccount, $cfg.Password -ScriptBlock {
            param($dir, $user, $password)
            $ticket = Join-Path $dir 'ticket.txt'
            [IO.File]::WriteAllText($ticket, "$user`n$password", [Text.UTF8Encoding]::new($false))
            icacls $dir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
            $action = New-ScheduledTaskAction -Execute 'C:\Program Files\RemoteHub Agent\RemoteHubCpTestServer.exe' `
                -Argument "`"$ticket`" `"$(Join-Path $dir 'server.log')`" 180"
            $null = Register-ScheduledTask -TaskName 'RHLab-CpTestServer' -Action $action -User 'NT AUTHORITY\SYSTEM' -RunLevel Highest -Force
            Start-ScheduledTask -TaskName 'RHLab-CpTestServer'
        }
        # Dismiss the lock-screen curtain (and satisfy "Press Ctrl+Alt+Delete" where it is required).
        Send-LabCtrlAltDel -Name $VmName
        try { Wait-LabDesktop -Name $VmName -Account Console -TimeoutSec 180 }
        catch { $signInError = "$_" }
        Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir 'c-prov-after-signin.png') | Out-Null
        $serverLog = Invoke-LabGuest -Name $VmName -ArgumentList $guestDir -ScriptBlock {
            param($dir)
            $deadline = (Get-Date).AddSeconds(30)
            while ((Get-Date) -lt $deadline -and -not (Select-String -Path (Join-Path $dir 'server.log') -Pattern '(result|error) ' -Quiet -ErrorAction SilentlyContinue)) {
                Start-Sleep -Seconds 1
            }
            Unregister-ScheduledTask -TaskName 'RHLab-CpTestServer' -Confirm:$false -ErrorAction SilentlyContinue
            [pscustomobject]@{
                Log          = @(Get-Content (Join-Path $dir 'server.log') -ErrorAction SilentlyContinue | ForEach-Object { "$_" })
                TicketLeft   = Test-Path (Join-Path $dir 'ticket.txt')
                ConsoleUser  = (Get-CimInstance Win32_ComputerSystem).UserName
            }
        }
        # Back to the Release DLL for the installer cases.
        Invoke-LabGuest -Name $VmName -ArgumentList $clsid -ScriptBlock {
            param($clsid)
            Set-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$clsid\InprocServer32" -Name '(default)' `
                -Value 'C:\Program Files\RemoteHub Agent\RemoteHubCredentialProvider.dll'
            Start-Service RemoteHubAgent
        }
    }

    # ---- 4. Installer: update without the property keeps it, AUTOSIGNIN=0 removes it
    $updateError = Update-AgentRebuilt 'keep' $null
    $afterUpdate = Get-ProviderState
    $removeError = Update-AgentRebuilt 'remove' $false
    $afterRemove = Get-ProviderState

    # ---- 5. RemoteHub's "Install / Remove credential provider" on a running agent
    function Get-AgentProcess {
        Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-CimInstance Win32_Service -Filter "Name='RemoteHubAgent'").ProcessId }
    }
    # The device runs the rebuild from step 4 now: another build than RemoteHub's package - refused, nothing changed.
    $otherBuild = Set-LabCredentialProvider -Name $VmName -Pin $install.pin -Install $true
    $afterOtherBuild = Get-ProviderState
    # Back to the build under test (an update with the identity kept), then add and remove the provider.
    $null = Install-LabAgent -Name $VmName -KeepIdentity
    $pidBefore = Get-AgentProcess
    $actionInstall = Set-LabCredentialProvider -Name $VmName -Pin $install.pin -Install $true
    $afterActionInstall = Get-ProviderState
    $actionRemove = Set-LabCredentialProvider -Name $VmName -Pin $install.pin -Install $false
    $afterActionRemove = Get-ProviderState
    $pidAfter = Get-AgentProcess
    $healthAfter = try { Get-LabAgentHealth -Name $VmName -Pin $install.pin } catch { $null }

    @{ Install = $install; InstallError = $installError; Registered = $registered; InertError = $inertError
       SignInError = $signInError; Server = $serverLog; UpdateError = $updateError; AfterUpdate = $afterUpdate
       RemoveError = $removeError; AfterRemove = $afterRemove; OtherBuild = $otherBuild; AfterOtherBuild = $afterOtherBuild
       ActionInstall = $actionInstall; AfterActionInstall = $afterActionInstall; ActionRemove = $actionRemove
       AfterActionRemove = $afterActionRemove; PidBefore = $pidBefore; PidAfter = $pidAfter; HealthAfter = $healthAfter } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $EvidenceDir 'c-prov-state.json') -Encoding UTF8
}

AfterAll {
    # the provider swapped, rebuilt agents installed: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
    Get-LabFile -Name $VmName -Path "$guestDir\*.log" -Destination (Join-Path $EvidenceDir 'c-prov-logs')
}

Describe 'C-PROV <VmName>' -Tag Agent, 'C-PROV' {

    It 'C-PROV-01 installs, registers and signs the provider with AUTOSIGNIN=1' -Tag 'C-PROV-01' {
        $installError | Should -BeNullOrEmpty
        $registered.Dll | Should -Be $true
        $registered.Signature | Should -Be 'Valid'   # the lab certificate is trusted on the VM (Install-LabSigningCertificate)
        $registered.Provider | Should -Be 'RemoteHub Agent'
        $registered.InprocServer | Should -Be 'C:\Program Files\RemoteHub Agent\RemoteHubCredentialProvider.dll'
        $registered.Threading | Should -Be 'Apartment'
    }

    It 'C-PROV-02 leaves the sign-in screen working when no ticket is pending (inert)' -Tag 'C-PROV-02' {
        $inertError | Should -BeNullOrEmpty
    }

    It 'C-PROV-03 signs the console account in through the provider (Lab DLL + test server)' -Tag 'C-PROV-03' {
        if (-not $serverLog) { Set-ItResult -Skipped -Because 'no lab build staged (build-credential-provider.ps1 -Configuration Lab)'; return }
        ($serverLog.Log -join "`n") | Should -Match 'client .* -> accepted'
        ($serverLog.Log -join "`n") | Should -Match 'result status=0x00000000'
        $signInError | Should -BeNullOrEmpty
        $serverLog.ConsoleUser | Should -Match "\\$([regex]::Escape($vm.ConsoleAccount))$"
        $serverLog.TicketLeft | Should -Be $false
    }

    It 'C-PROV-04 keeps the provider on an update without AUTOSIGNIN' -Tag 'C-PROV-04' {
        $updateError | Should -BeNullOrEmpty
        $afterUpdate.Dll | Should -Be $true
        $afterUpdate.Provider | Should -Be 'RemoteHub Agent'
    }

    It 'C-PROV-05 removes the DLL and both keys with AUTOSIGNIN=0' -Tag 'C-PROV-05' {
        $removeError | Should -BeNullOrEmpty
        $afterRemove.Dll | Should -Be $false
        $afterRemove.Provider | Should -BeNullOrEmpty
        $afterRemove.InprocServer | Should -BeNullOrEmpty
        $afterRemove.Behavior | Should -BeNullOrEmpty                     # AUTOSIGNIN=0 also removes the policy the install set
    }

    It 'C-PROV-06 RemoteHub installs the provider on a running agent, which keeps running' -Tag 'C-PROV-06' {
        $actionInstall.ok | Should -Be $true
        $actionInstall.installed | Should -Be $true
        $afterActionInstall.Dll | Should -Be $true
        $afterActionInstall.Signature | Should -Be 'Valid'
        $afterActionInstall.Provider | Should -Be 'RemoteHub Agent'
        $afterActionInstall.InprocServer | Should -Be 'C:\Program Files\RemoteHub Agent\RemoteHubCredentialProvider.dll'
        $afterActionInstall.Behavior | Should -Be 'AutoSignIn'          # allowed by policy for the agent's groups
        $afterActionInstall.Right | Should -Be @('S-1-5-32-544')
        $actionInstall.message | Should -Match 'Automatic sign-in allowed on this device'
    }

    It 'C-PROV-07 RemoteHub removes it again; the agent keeps running with the same certificate' -Tag 'C-PROV-07' {
        $actionRemove.ok | Should -Be $true
        $actionRemove.installed | Should -Be $false
        $afterActionRemove.Dll | Should -Be $false
        $afterActionRemove.Provider | Should -BeNullOrEmpty
        $afterActionRemove.Behavior | Should -BeNullOrEmpty               # what the agent had set is gone again
        @($afterActionRemove.Right | Where-Object { $_ }) | Should -BeNullOrEmpty
        $pidAfter | Should -Be $pidBefore   # the service was never restarted
        $healthAfter | Should -Not -BeNullOrEmpty
    }

    It 'C-PROV-08 refuses to install on another build of the agent and changes nothing' -Tag 'C-PROV-08' {
        $otherBuild.ok | Should -Be $false
        $otherBuild.message | Should -Match 'different build'
        $afterOtherBuild.Dll | Should -Be $false
    }
}
