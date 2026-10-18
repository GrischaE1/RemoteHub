# A-UNINST - install the agent like RemoteHub does, then uninstall it like Windows does (msiexec /x, which runs
# "RemoteHubAgent.exe cleanup"). Checks: rule, certificate, config removed; logs stay; nothing answers on the port;
# the Ctrl+Alt+Del setting the agent turned on is back as it was. See docs/test-lab-spec.md section 9.
# Always from the checkpoint (Reset-LabVm): the state before the install is the baseline.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    Reset-LabVm -Name $VmName
    $clean = Get-LabAgentState -Name $VmName
    $install = Install-LabAgent -Name $VmName
    $installed = Get-LabAgentState -Name $VmName
    $uninstall = Uninstall-LabAgent -Name $VmName
    $after = Get-LabAgentState -Name $VmName
    @{ Clean = $clean; Install = $install; Installed = $installed; Uninstall = $uninstall; After = $after } |
        ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir 'a-uninst-state.json') -Encoding UTF8
}

AfterAll {
    Get-LabFile -Name $VmName -Path 'C:\ProgramData\RemoteHub\Agent\logs', $uninstall.Log `
        -Destination (Join-Path $EvidenceDir 'a-uninst-logs')
    # the agent uninstalled: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'A-UNINST <VmName>' -Tag Agent, 'A-UNINST' {

    It 'A-UNINST-01 had the agent installed and running before the uninstall' -Tag 'A-UNINST-01' {
        $installed.Product | Should -Not -BeNullOrEmpty
        $installed.Service.State | Should -Be 'Running'
        @($installed.OurCertificates).Count | Should -Be 1
        $installed.Firewall | Should -Not -BeNullOrEmpty
    }

    It 'A-UNINST-02 uninstalls silently (msiexec exit code 0)' -Tag 'A-UNINST-02' {
        $uninstall.ExitCode | Should -BeIn 0, 3010
    }

    It 'A-UNINST-03 removes the product, the service and the program folder' -Tag 'A-UNINST-03' {
        $after.Product | Should -BeNullOrEmpty
        $after.Service | Should -BeNullOrEmpty
        $after.InstallFolder | Should -Be $false
    }

    It 'A-UNINST-04 removes the firewall rule' -Tag 'A-UNINST-04' {
        $after.Firewall | Should -BeNullOrEmpty
    }

    It 'A-UNINST-05 removes the certificate from LocalMachine\My' -Tag 'A-UNINST-05' {
        @($after.OurCertificates) | Should -BeNullOrEmpty
    }

    It 'A-UNINST-06 removes agent.json' -Tag 'A-UNINST-06' {
        $after.Config | Should -BeNullOrEmpty
    }

    It 'A-UNINST-07 keeps the logs, ending with the uninstall line' -Tag 'A-UNINST-07' {
        $after.LogFolder | Should -Be $true
        # wording grew over versions ("..., configuration and Ctrl+Alt+Del setting removed" from 0.15.8)
        ($after.LogLines -join "`n") | Should -Match 'Uninstalled: firewall rule, certificate.* removed'
    }

    It 'A-UNINST-08 removes the event source' -Tag 'A-UNINST-08' {
        $after.EventSource | Should -Be $false
    }

    It 'A-UNINST-09 no longer answers on the agent port' -Tag 'A-UNINST-09' {
        { Get-LabAgentHealth -Name $VmName -Pin $install.pin } | Should -Throw
    }

    It 'A-UNINST-10 puts the Ctrl+Alt+Del setting (SoftwareSASGeneration) back as it was before the install' -Tag 'A-UNINST-10' {
        # agents up to 0.15.5 only switch it temporarily; later ones set it at service start (SecureAttention.EnsureAllowed)
        if ("$($installed.SoftwareSas)" -eq "$($clean.SoftwareSas)") {
            Set-ItResult -Skipped -Because "agent $($installed.Product.Version) left SoftwareSASGeneration unchanged ('$($clean.SoftwareSas)')"
            return
        }
        $after.SoftwareSas | Should -Be $clean.SoftwareSas
    }
}

Describe 'A-UNINST auto sign-in <VmName>' -Tag Agent, 'A-UNINST' {
    BeforeAll {
        Reset-LabVm -Name $VmName
        $null = Install-LabAgent -Name $VmName -AutoSignIn $true
        $withProvider = Get-LabAgentState -Name $VmName
        $removed = Uninstall-LabAgent -Name $VmName
        $afterProvider = Get-LabAgentState -Name $VmName
        @{ Installed = $withProvider; Uninstall = $removed; After = $afterProvider } |
            ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir 'a-uninst-autosignin.json') -Encoding UTF8
    }

    # since 0.18.12 the install keeps its choice in agent.json, not in the policy key (docs/admx-design.md 11.3)
    It 'A-UNINST-15 after an AUTOSIGNIN=1 install: provider DLL and keys gone, agent.json with the install''s choice removed, the policy key left empty' -Tag 'A-UNINST-15' {
        $withProvider.AutoSignIn.Dll | Should -BeTrue
        $withProvider.AutoSignIn.Provider | Should -BeTrue
        $withProvider.AutoSignIn.ConfigBehavior | Should -Be 'AutoSignIn'
        @($withProvider.AutoSignIn.ConfigGroups) | Should -Contain 'S-1-5-32-544'
        $withProvider.AutoSignIn.Behavior | Should -BeNullOrEmpty -Because 'the install no longer writes the policy key'
        $withProvider.AutoSignIn.Marker | Should -BeFalse
        $afterProvider.Config | Should -BeNullOrEmpty -Because 'uninstall removes agent.json and with it the install''s choice'
        $afterProvider.AutoSignIn.ConfigBehavior | Should -BeNullOrEmpty
        $removed.ExitCode | Should -BeIn 0, 3010
        $afterProvider.AutoSignIn.Dll | Should -BeFalse
        $afterProvider.AutoSignIn.Provider | Should -BeFalse
        $afterProvider.AutoSignIn.InprocServer | Should -BeNullOrEmpty
        $afterProvider.AutoSignIn.Behavior | Should -BeNullOrEmpty
        @($afterProvider.AutoSignIn.Right) | Should -BeNullOrEmpty
        $afterProvider.AutoSignIn.Marker | Should -BeFalse
    }
}
