# A-UNINST - install the agent like RemoteHub does, then uninstall it like Windows does (msiexec /x, which runs
# "RemoteHubAgent.exe cleanup"). Checks: rule, certificate, config removed; logs stay; nothing answers on the port;
# the Ctrl+Alt+Del setting the agent turned on is back as it was. See docs/test-lab-spec.md section 9.
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
}

Describe 'A-UNINST <VmName>' -Tag Agent, 'A-UNINST' {

    It 'had the agent installed and running before the uninstall' {
        $installed.Product | Should -Not -BeNullOrEmpty
        $installed.Service.State | Should -Be 'Running'
        @($installed.OurCertificates).Count | Should -Be 1
        $installed.Firewall | Should -Not -BeNullOrEmpty
    }

    It 'uninstalls silently (msiexec exit code 0)' {
        $uninstall.ExitCode | Should -BeIn 0, 3010
    }

    It 'removes the product, the service and the program folder' {
        $after.Product | Should -BeNullOrEmpty
        $after.Service | Should -BeNullOrEmpty
        $after.InstallFolder | Should -Be $false
    }

    It 'removes the firewall rule' {
        $after.Firewall | Should -BeNullOrEmpty
    }

    It 'removes the certificate from LocalMachine\My' {
        @($after.OurCertificates) | Should -BeNullOrEmpty
    }

    It 'removes agent.json' {
        $after.Config | Should -BeNullOrEmpty
    }

    It 'keeps the logs, ending with the uninstall line' {
        $after.LogFolder | Should -Be $true
        # wording grew over versions ("..., configuration and Ctrl+Alt+Del setting removed" from 0.15.8)
        ($after.LogLines -join "`n") | Should -Match 'Uninstalled: firewall rule, certificate.* removed'
    }

    It 'removes the event source' {
        $after.EventSource | Should -Be $false
    }

    It 'no longer answers on the agent port' {
        { Get-LabAgentHealth -Name $VmName -Pin $install.pin } | Should -Throw
    }

    It 'puts the Ctrl+Alt+Del setting (SoftwareSASGeneration) back as it was before the install' {
        # agents up to 0.15.5 only switch it temporarily; later ones set it at service start (SecureAttention.EnsureAllowed)
        if ("$($installed.SoftwareSas)" -eq "$($clean.SoftwareSas)") {
            Set-ItResult -Skipped -Because "agent $($installed.Product.Version) left SoftwareSASGeneration unchanged ('$($clean.SoftwareSas)')"
            return
        }
        $after.SoftwareSas | Should -Be $clean.SoftwareSas
    }
}
