# SEC-ENR - a copied enrollment file (docs/security-test-lab-design.md section 7, docs/test-lab-cases.md section 13,
# docs/agent-spec.md sections 5.1 and 5.5).
#   SEC-ENR-03  one .rhagent, copied before it was used, installed on the VM and on a second device: each device makes
#               its own key and device ID, so the copy gives no second device the first one's identity. RemoteHub claims
#               each as itself; the second can't pass for the first (its pin is refused, its claim under the first's name
#               is refused); the keys can't be exported; the file holds no key; once the secret is revoked neither can be
#               claimed any more.
# Both devices start from their checkpoint; the file is kept outside the evidence folder (it holds the lab's secret).
# LabRole: device
# LabNeeds: lane
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "sec-enr-$name.json") -Encoding UTF8 }
    # a second device of the same kind, else any other one
    $second = Get-LabDevice -For $VmName -Os $vm.Os
    if (-not $second) { $second = Get-LabDevice -For $VmName }
    $work = Join-Path ([IO.Path]::GetTempPath()) ('sec-enr-' + [guid]::NewGuid().ToString('N'))
    $r = [ordered]@{ Second = $(if ($second) { $second.Name }) }

    if ($second) {
        Reset-LabVm -Name $VmName
        Reset-LabVm -Name $second.Name
        # the copy: the very same file goes to both devices
        $file = New-LabEnrollment -Name $VmName -Path (Join-Path $work 'device.rhagent')
        $r.Text = [IO.File]::ReadAllText($file.file)
        $r.Fields = @(($r.Text | ConvertFrom-Json).PSObject.Properties.Name)
        $r.Text = $null   # the secret stays out of the evidence
        $r.A = Install-LabAgent -Name $VmName -EnrollmentFile $file.file
        $r.B = Install-LabAgent -Name $second.Name -EnrollmentFile $file.file
        $r.StateA = Get-LabAgentState -Name $VmName
        $r.StateB = Get-LabAgentState -Name $second.Name
        $r.ClaimA = Get-LabAgentClaim -Name $VmName
        $r.ClaimB = Get-LabAgentClaim -Name $second.Name
        # the second device at the first one's name (a DNS name pointing at it)
        $r.Impostor = Get-LabAgentClaim -Name $second.Name -HostName $r.ClaimA.computerName
        # RemoteHub's pin of the first device, presented to the second
        $r.PinOnSecond = try { $null = Get-LabAgentHealth -Name $second.Name -Pin $r.ClaimA.pin; 'answered' } catch { "refused: $($_.Exception.Message)" }
        $r.RevokedA = Get-LabAgentClaim -Name $VmName -RevokeCurrent
        $r.RevokedB = Get-LabAgentClaim -Name $second.Name -RevokeCurrent
        $r.KeyId = $file.keyId
        Save 'copied' $r
    }
}

AfterAll {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    if ($second) {
        Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'sec-enr-agent-logs-a')
        Save-LabAgentEvidence -Name $second.Name -Destination (Join-Path $EvidenceDir 'sec-enr-agent-logs-b')
    }
}

Describe 'SEC-ENR <VmName>' -Tag Security, 'SEC-ENR' {

    It 'SEC-ENR-03 a copied .rhagent: each device makes its own identity; RemoteHub claims each as itself and the second can''t pass for the first' -Tag 'SEC-ENR-03' {
        if (-not $second) { Set-ItResult -Skipped -Because 'the lab has no second device for this VM'; return }
        # the file holds the secret and the settings, no key and no device ID
        $r.Fields | Should -Contain 'secret'
        $r.Fields | Should -Not -Contain 'pfx'
        $r.Fields | Should -Not -Contain 'deviceId'
        # two identities from one file
        $r.StateA.Config.deviceId | Should -Not -Be $r.StateB.Config.deviceId
        $r.StateA.Certificate.Sha256 | Should -Not -Be $r.StateB.Certificate.Sha256
        $r.StateA.Config.enrollmentKeyId | Should -Be $r.KeyId
        $r.StateA.Certificate.Exportable | Should -BeFalse
        $r.StateB.Certificate.Exportable | Should -BeFalse
        # the claims: each device as itself, with the certificate it has
        $r.ClaimA.outcome | Should -Be 'Claimed' -Because $r.ClaimA.message
        $r.ClaimA.pin | Should -Be $r.StateA.Certificate.Sha256
        $r.ClaimA.pin | Should -Be $r.A.pin
        $r.ClaimB.outcome | Should -Be 'Claimed' -Because $r.ClaimB.message
        $r.ClaimB.pin | Should -Be $r.StateB.Certificate.Sha256
        # the second device can't be taken for the first
        $r.ClaimB.computerName | Should -Not -Be $r.ClaimA.computerName -Because 'the two devices need their own computer names for this case'
        $r.Impostor.outcome | Should -Be 'OtherName'
        $r.PinOnSecond | Should -Match '^refused'
        # after "Replace and revoke" neither is claimed
        $r.RevokedA.outcome | Should -Be 'RevokedSecret'
        $r.RevokedB.outcome | Should -Be 'RevokedSecret'
    }
}
