# A-INST - agent install the way RemoteHub does it (AgentInstaller.InstallOverRemoting; the runspace is PowerShell
# Direct instead of WinRM). Checks: service running, fingerprint = pinned, firewall rule scoped, ProgramData folder
# owned by Administrators without a Users ACE, .rhagent gone. See docs/test-lab-spec.md section 9 and agent-spec 5.
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    Reset-LabVm -Name $VmName
    $hostIp = Get-LabHostAddress -Name $VmName
    $install = Install-LabAgent -Name $VmName -Scope $hostIp
    $install | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $EvidenceDir 'a-inst-install.json') -Encoding UTF8
    $state = Get-LabAgentState -Name $VmName
    $state | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $EvidenceDir 'a-inst-state.json') -Encoding UTF8
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'a-inst-agent-logs')
}

Describe 'A-INST <VmName>' -Tag Agent, 'A-INST' {

    It 'reports the pinned fingerprint back from the device' {
        $install.installed.fingerprint | Should -Be $install.pin
        $install.installed.deviceId | Should -Be $install.deviceId
        $install.installed.port | Should -Be 58732
    }

    It 'runs the service RemoteHubAgent as LocalSystem, automatic (delayed)' {
        $state.Service.State | Should -Be 'Running'
        $state.Service.Account | Should -Be 'LocalSystem'
        $state.Service.StartMode | Should -Be 'Auto'
        $state.Service.Delayed | Should -BeTrue
        $state.Service.Path | Should -Match 'RemoteHub Agent\\RemoteHubAgent\.exe'
    }

    It 'answers /v1/health over TLS pinned to the enrolled certificate' {
        $health = Get-LabAgentHealth -Name $VmName -Pin $install.pin
        $health.product | Should -Be 'RemoteHub Agent'
        $health.deviceId | Should -Be $install.deviceId
    }

    It 'keeps the enrolled certificate in LocalMachine\My with a non-exportable key' {
        $state.Certificate | Should -Not -BeNullOrEmpty
        $state.Certificate.Sha256 | Should -Be $install.pin
        $state.Certificate.FriendlyName | Should -Be 'RemoteHub Agent'
        $state.Certificate.Subject | Should -Be "CN=$($install.deviceId), O=RemoteHub Agent"
        $state.Certificate.HasKey | Should -Be $true
        $state.Certificate.Exportable | Should -Be $false   # strict: $null (key type not recognised) must fail
    }

    It 'writes agent.json with the enrolled identity' {
        $state.Config.deviceId | Should -Be $install.deviceId
        $state.Config.listenPort | Should -Be 58732
        @($state.Config.allowedGroups) | Should -Be @('S-1-5-32-544')
    }

    It 'adds one inbound firewall rule for the agent program, scoped to the given address' {
        $state.Firewall | Should -Not -BeNullOrEmpty
        $state.Firewall.Count | Should -Be 1
        $state.Firewall.Enabled | Should -Be 'True'
        $state.Firewall.Direction | Should -Be 'Inbound'
        $state.Firewall.Action | Should -Be 'Allow'
        $state.Firewall.Protocol | Should -Be 'TCP'
        @($state.Firewall.LocalPort) | Should -Be @('58732')
        $state.Firewall.Program | Should -Match 'RemoteHub Agent\\RemoteHubAgent\.exe$'
        @($state.Firewall.RemoteAddress) | Should -Be @($hostIp)
    }

    It 'locks the ProgramData folder to SYSTEM and Administrators (owner Administrators, no Users ACE)' {
        $state.FolderAcl.Owner | Should -Be 'S-1-5-32-544'
        $state.FolderAcl.Protected | Should -BeTrue
        $sids = @($state.FolderAcl.Rules | ForEach-Object Sid | Sort-Object -Unique)
        $sids | Should -Be @('S-1-5-18', 'S-1-5-32-544')
        @($state.FolderAcl.Rules | Where-Object { $_.Sid -in 'S-1-5-32-545', 'S-1-5-11', 'S-1-1-0' }) | Should -BeNullOrEmpty
    }

    It 'leaves no .rhagent and no staging folder behind' {
        @($state.Leftovers) | Should -BeNullOrEmpty
    }

    It 'registers the event source and logs the enrollment' {
        $state.EventSource | Should -BeTrue
        ($state.LogLines -join "`n") | Should -Match "Enrolled as device $([regex]::Escape($install.deviceId))"
    }
}
