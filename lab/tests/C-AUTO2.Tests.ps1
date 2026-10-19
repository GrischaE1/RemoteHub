# C-AUTO (more) - automatic sign-in refusals the agent decides before Windows is asked (DesktopEndpoint.AutoSignInRefusal
# / SessionRefusal, AutoSignInBroker.CheckInstallation) and the per-account rate limit, through RemoteHub's desktop
# client (harness "auto-sign-in", signed in to the agent as the console account). The agent MSI under test must be
# built with the provider and signed (build-installer.ps1 -LabSigning -CredentialProvider). The broker's check of the
# installation is cached for a minute: every variant restarts the agent. See docs/test-lab-cases.md section 9.12.
# Every context starts from the checkpoint (Reset-LabVm) with its own install.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $clsid = '{718FFFD2-6671-4FA4-9FA8-86D1CD4D84C3}'
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "c-auto2-$name.json") -Encoding UTF8 }
    function Run([hashtable]$Options = @{}) {
        try { Invoke-LabAutoSignIn -Name $VmName -Pin $pin @Options } catch { [pscustomobject]@{ error = "$_"; statuses = @(); rights = $null; features = @() } }
    }
    function Text($r) { (@($r.statuses) + @($r.error)) -join "`n" }
    # the policy key back to automatic sign-in allowed for Administrators, nothing else - set as IT would, on top of the
    # AUTOSIGNIN=1 install's own choice in agent.json (which allows the same)
    function Reset-Policy {
        Set-LabAgentPolicy -Name $VmName -Clear -Values @{ NoUserBehavior = 'AutoSignIn' } -Rights @{ AutoSignIn = @('S-1-5-32-544') }
    }
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'c-auto2-agent-logs')
    # installs, uninstalls, groups and console sign-ins changed the VM: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'C-AUTO2 <VmName>' -Tag Agent, 'C-AUTO' {

    # Automatic sign-in is on by default (credential-provider-design.md section 5): an install without the option has
    # the provider and allows it for the allowed groups; AUTOSIGNIN=0 turns it off.
    Context 'installed without the option, and with AUTOSIGNIN=0' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName
            $pin = $install.pin
            $state = Get-LabAgentState -Name $VmName
            Disconnect-LabConsole -Name $VmName
            $r = Run
            Save 'default-on' @{ State = $state.AutoSignIn; Run = $r }

            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName -AutoSignIn $false
            $pin = $install.pin
            $offState = Get-LabAgentState -Name $VmName
            Disconnect-LabConsole -Name $VmName
            $off = Run
            Save 'install-off' @{ State = $offState.AutoSignIn; Run = $off }
        }

        It 'C-AUTO-07 without AUTOSIGNIN: provider installed, nothing in agent.json or the policy key, Hello autoSignIn true, signs in; with AUTOSIGNIN=0: no provider, agent.json SignInScreen, Hello autoSignIn false' -Tag 'C-AUTO-07' {
            $state.AutoSignIn.Dll | Should -BeTrue
            $state.AutoSignIn.Provider | Should -BeTrue
            $state.AutoSignIn.ConfigBehavior | Should -BeNullOrEmpty
            @($state.AutoSignIn.ConfigGroups) | Should -BeNullOrEmpty
            $state.AutoSignIn.Behavior | Should -BeNullOrEmpty
            $state.AutoSignIn.Marker | Should -BeFalse
            $r.rights.autoSignIn | Should -BeTrue
            @($r.features) | Should -Contain 'autoSignIn'
            Text $r | Should -Match 'SignedIn: Signed in as'

            $offState.AutoSignIn.Dll | Should -BeFalse
            $offState.AutoSignIn.Provider | Should -BeFalse
            $offState.AutoSignIn.ConfigBehavior | Should -Be 'SignInScreen'
            $offState.AutoSignIn.Behavior | Should -BeNullOrEmpty
            $off.rights.autoSignIn | Should -BeFalse
            @($off.features) | Should -Not -Contain 'autoSignIn'
            Text $off | Should -Match 'turned off on this device \(policy NoUserBehavior\)'
        }
    }

    Context 'policy and rights' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName -AutoSignIn $true
            $pin = $install.pin
            Disconnect-LabConsole -Name $VmName
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock { if (-not (Get-LocalGroup 'RH Others' -ErrorAction SilentlyContinue)) { New-LocalGroup 'RH Others' } }
            $runs = [ordered]@{}
            Reset-Policy; Set-LabAgentPolicy -Name $VmName -Rights @{ AutoSignIn = @('RH Others') }
            $runs.noRight = Run
            Reset-Policy; Set-LabAgentPolicy -Name $VmName -Rights @{ SecureDesktop = @('RH Others') }
            $runs.noSecureDesktop = Run
            Reset-Policy; Set-LabAgentPolicy -Name $VmName -Values @{ AutoSignInRequirePrivacyScreen = 1; PrivacyScreen = 0 }
            $runs.needsPrivacy = Run
            Reset-Policy
            Save 'policy' $runs
        }

        It 'C-AUTO-08 without the right AutoSignIn, or without SecureDesktop: refused with the right named' -Tag 'C-AUTO-08' {
            $runs.noRight.rights.autoSignIn | Should -BeFalse
            Text $runs.noRight | Should -Match 'may not sign in automatically on this device \(right AutoSignIn\)'
            # without SecureDesktop the sign-in screen itself is refused first (Start: "may not see the sign-in screen");
            # Hello already says no automatic sign-in
            $runs.noSecureDesktop.rights.autoSignIn | Should -BeFalse
            # without SecureDesktop the Start on the sign-in screen is refused before any automatic sign-in check (the
            # 'needs the rights Desktop and SecureDesktop' reason is only in the agent log as 'not offered')
            Text $runs.noSecureDesktop | Should -Match 'may not see the sign-in screen'
        }

        It 'C-AUTO-09 AutoSignInRequirePrivacyScreen with the privacy screen off: refused, policy named' -Tag 'C-AUTO-09' {
            Text $runs.needsPrivacy | Should -Match 'needs the privacy screen.*\(policy AutoSignInRequirePrivacyScreen\)'
        }
    }

    Context 'session state' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName -AutoSignIn $true
            $pin = $install.pin
            # the console account is signed in at the checkpoint: its own session, connected without a question (None)
            $signedIn = Invoke-LabDesktop -Name $VmName -Pin $pin -Account Console -Out (Join-Path $EvidenceDir 'c-auto2-signed-in') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'None'; autoSignIn = $true }
                @{ do = 'wait'; anyOf = @('SignInFailed', 'SignedIn', 'Failed'); timeout = 60 }, @{ do = 'close' })
            $signedIn = [pscustomobject]@{ statuses = @($signedIn.statuses | ForEach-Object { "$($_.state): $($_.message)" }); error = $null }
            # the console account's session disconnected: the console shows the sign-in screen, the account still has a session
            $null = Invoke-LabInSession -Name $VmName -Account Console -ScriptBlock { tsdiscon.exe }
            Start-Sleep -Seconds 10
            $existing = Run
            Save 'session' @{ SignedIn = $signedIn; Existing = $existing }
        }

        It 'C-AUTO-10 someone signed in on the console, or the account already has a session: refused before Windows is asked' -Tag 'C-AUTO-10' {
            Text $signedIn | Should -Match 'Someone is signed in on the console now'
            Text $existing | Should -Match 'already has a session on the device'
        }
    }

    Context 'rate limit' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName -AutoSignIn $true
            $pin = $install.pin
            $since = Get-LabGuestTime -Name $VmName
            $attempts = @()
            foreach ($i in 1..4) {
                Disconnect-LabConsole -Name $VmName   # also closes a failed attempt's message on the sign-in screen
                $attempts += Run @{ SignInPassword = "wrong-$i-" + [guid]::NewGuid().ToString('N').Substring(0, 6) }
            }
            $events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 2104)
            Save 'rate' @{ Attempts = $attempts; Events = $events }
        }

        It 'C-AUTO-11 the 4th automatic sign-in within 15 minutes is refused "Too many automatic sign-ins", event 2104' -Tag 'C-AUTO-11' {
            foreach ($a in $attempts[0..2]) { Text $a | Should -Not -Match 'Too many automatic sign-ins' }
            Text $attempts[3] | Should -Match 'Too many automatic sign-ins for this account'
            @($events | Where-Object { $_.Message -match 'Too many automatic sign-ins' }).Count | Should -BeGreaterThan 0
        }
    }

    Context 'provider unusable' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName -AutoSignIn $true
            $pin = $install.pin
            $runs = [ordered]@{}
            $set = {
                param([scriptblock]$Change, [scriptblock]$Undo)
                Invoke-LabGuest -Name $VmName -ArgumentList $clsid -ScriptBlock $Change
                $null = Restart-LabAgent -Name $VmName -Pin $pin
                Disconnect-LabConsole -Name $VmName
                $r = Run
                Invoke-LabGuest -Name $VmName -ArgumentList $clsid -ScriptBlock $Undo
                $null = Restart-LabAgent -Name $VmName -Pin $pin
                $r
            }
            $runs.excluded = & $set {
                param($c) $k = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System'; $null = New-Item $k -Force -ErrorAction SilentlyContinue
                Set-ItemProperty $k -Name ExcludedCredentialProviders -Value $c
            } { param($c) Remove-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -Name ExcludedCredentialProviders -ErrorAction SilentlyContinue }
            $runs.elsewhere = & $set {
                param($c) $k = "HKLM:\SOFTWARE\Classes\CLSID\$c\InprocServer32"; Set-ItemProperty $k -Name RHLabWas -Value (Get-ItemProperty $k).'(default)'
                Copy-Item 'C:\Program Files\RemoteHub Agent\RemoteHubCredentialProvider.dll' 'C:\Windows\Temp\RemoteHubCredentialProvider.dll' -Force
                Set-ItemProperty $k -Name '(default)' -Value 'C:\Windows\Temp\RemoteHubCredentialProvider.dll'
            } { param($c) $k = "HKLM:\SOFTWARE\Classes\CLSID\$c\InprocServer32"; Set-ItemProperty $k -Name '(default)' -Value (Get-ItemProperty $k).RHLabWas; Remove-ItemProperty $k -Name RHLabWas }
            $runs.unsigned = & $set {
                # the agent's broker holds the DLL: the service is stopped while it is swapped (the caller restarts it)
                param($c) $d = 'C:\Program Files\RemoteHub Agent\RemoteHubCredentialProvider.dll'
                Stop-Service RemoteHubAgent -Force
                Move-Item $d "$d.rhlab" -Force
                $b = [IO.File]::ReadAllBytes("$d.rhlab"); $b[$b.Length - 100] = $b[$b.Length - 100] -bxor 0xFF; [IO.File]::WriteAllBytes($d, $b)
                Start-Service RemoteHubAgent
            } { param($c) Stop-Service RemoteHubAgent -Force; $d = 'C:\Program Files\RemoteHub Agent\RemoteHubCredentialProvider.dll'; Move-Item $d "$d.changed" -Force; Move-Item "$d.rhlab" $d -Force; Start-Service RemoteHubAgent }
            Save 'unusable' $runs
        }

        It 'C-AUTO-12 provider excluded, registered elsewhere or not signed: Hello autoSignIn false, refused with the reason' -Tag 'C-AUTO-12' {
            $reasons = @{ excluded = 'excluded by policy \(ExcludedCredentialProviders\)'; elsewhere = 'doesn''t point to the agent''s folder'; unsigned = 'isn''t signed by a trusted publisher' }
            foreach ($k in $reasons.Keys) {
                $runs[$k].rights.autoSignIn | Should -BeFalse -Because $k
                Text $runs[$k] | Should -Match $reasons[$k] -Because $k
            }
        }
    }

    Context 'IT''s own policy' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            Set-LabAgentPolicy -Name $VmName -Values @{ NoUserBehavior = 'SignInScreen' }
            $install = Install-LabAgent -Name $VmName -AutoSignIn $true
            $pin = $install.pin
            $installed = Get-LabAgentState -Name $VmName
            $uninstall = Uninstall-LabAgent -Name $VmName
            $after = Get-LabAgentPolicy -Name $VmName
            Save 'it-policy' @{ Installed = $installed.AutoSignIn; Log = @($installed.LogLines | Where-Object { $_ -match 'Automatic sign-in' }); After = $after }
        }

        It 'C-AUTO-13 IT''s NoUserBehavior=SignInScreen is left as it is by the install and by the uninstall' -Tag 'C-AUTO-13' {
            $installed.AutoSignIn.Behavior | Should -Be 'SignInScreen'
            # the install records its own choice in agent.json (since 0.18.12) and says the policy decides
            $installed.AutoSignIn.ConfigBehavior | Should -Be 'AutoSignIn'
            ($installed.LogLines -join "`n") | Should -Match 'policy NoUserBehavior is "SignInScreen" on this device.*it decides, so automatic sign-in stays off'
            $uninstall.ExitCode | Should -BeIn 0, 3010
            $after.NoUserBehavior | Should -Be 'SignInScreen'
        }
    }
}
