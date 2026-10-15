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

    It 'A-INST-01 reports the pinned fingerprint back from the device' -Tag 'A-INST-01' {
        $install.installed.fingerprint | Should -Be $install.pin
        $install.installed.deviceId | Should -Be $install.deviceId
        $install.installed.port | Should -Be 58732
    }

    It 'A-INST-02 runs the service RemoteHubAgent as LocalSystem, automatic (delayed)' -Tag 'A-INST-02' {
        $state.Service.State | Should -Be 'Running'
        $state.Service.Account | Should -Be 'LocalSystem'
        $state.Service.StartMode | Should -Be 'Auto'
        $state.Service.Delayed | Should -BeTrue
        $state.Service.Path | Should -Match 'RemoteHub Agent\\RemoteHubAgent\.exe'
    }

    It 'A-INST-03 answers /v1/health over TLS pinned to the enrolled certificate' -Tag 'A-INST-03' {
        $health = Get-LabAgentHealth -Name $VmName -Pin $install.pin
        $health.product | Should -Be 'RemoteHub Agent'
        $health.deviceId | Should -Be $install.deviceId
        $health.version | Should -Be (Get-LabMsiVersion (Get-LabAgentMsi))
        $health.protocol | Should -Be 1
        # jump hosts are on by default (policy TunnelEnabled)
        @($health.features) | Should -Be @('powershell', 'desktop', 'tunnel')
    }

    It 'A-INST-04 keeps the enrolled certificate in LocalMachine\My with a non-exportable key' -Tag 'A-INST-04' {
        $state.Certificate | Should -Not -BeNullOrEmpty
        $state.Certificate.Sha256 | Should -Be $install.pin
        $state.Certificate.FriendlyName | Should -Be 'RemoteHub Agent'
        $state.Certificate.Subject | Should -Be "CN=$($install.deviceId), O=RemoteHub Agent"
        $state.Certificate.HasKey | Should -Be $true
        $state.Certificate.Exportable | Should -Be $false   # strict: $null (key type not recognised) must fail
    }

    It 'A-INST-05 writes agent.json with the enrolled identity' -Tag 'A-INST-05' {
        $state.Config.deviceId | Should -Be $install.deviceId
        $state.Config.listenPort | Should -Be 58732
        @($state.Config.allowedGroups) | Should -Be @('S-1-5-32-544')
    }

    It 'A-INST-06 adds one inbound firewall rule for the agent program, scoped to the given address' -Tag 'A-INST-06' {
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

    It 'A-INST-07 locks the ProgramData folder to SYSTEM and Administrators (owner Administrators, no Users ACE)' -Tag 'A-INST-07' {
        $state.FolderAcl.Owner | Should -Be 'S-1-5-32-544'
        $state.FolderAcl.Protected | Should -BeTrue
        $sids = @($state.FolderAcl.Rules | ForEach-Object Sid | Sort-Object -Unique)
        $sids | Should -Be @('S-1-5-18', 'S-1-5-32-544')
        @($state.FolderAcl.Rules | Where-Object { $_.Sid -in 'S-1-5-32-545', 'S-1-5-11', 'S-1-1-0' }) | Should -BeNullOrEmpty
    }

    It 'A-INST-08 leaves no .rhagent and no staging folder behind' -Tag 'A-INST-08' {
        @($state.Leftovers) | Should -BeNullOrEmpty
    }

    It 'A-INST-09 registers the event source and logs the enrollment' -Tag 'A-INST-09' {
        $state.EventSource | Should -BeTrue
        ($state.LogLines -join "`n") | Should -Match "Enrolled as device $([regex]::Escape($install.deviceId))"
    }
}
