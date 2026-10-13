# A-UPDFAIL - updates through the agent that must fail cleanly: RemoteHub reports them as failed with the old version
# (never "updated"), and the old agent keeps running with its identity. Three ways to fail: a package that refuses to
# install (false launch condition, same version), an upgrade whose enrollment step fails after the old version was
# removed (rollback), and a file that isn't a package at all. See docs/test-lab-cases.md section 5.3.
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $newMsi = Get-LabAgentMsi
    $oldMsi = Get-LabAgentMsi -Previous
    $oldVersion = Get-LabMsiVersion $oldMsi
    $work = Join-Path $env:TEMP "RemoteHubLab-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $null = New-Item -ItemType Directory -Path $work -Force
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "a-updfail-$name.json") -Encoding UTF8 }

    # revert, install the previous release, try the broken update, read everything back
    function Invoke-FailedUpdate([string]$Case, [hashtable]$UpdateArgs) {
        Reset-LabVm -Name $VmName
        $install = Install-LabAgent -Name $VmName -Msi $oldMsi
        $before = Get-LabAgentState -Name $VmName
        $update = Update-LabAgent -Name $VmName -Pin $install.pin @UpdateArgs
        # the old agent may restart after a rollback - give it the same time RemoteHub would
        $health = $null
        for ($i = 0; $i -lt 36 -and -not $health; $i++) {
            try { $health = Get-LabAgentHealth -Name $VmName -Pin $install.pin } catch { Start-Sleep -Seconds 5 }
        }
        $after = Get-LabAgentState -Name $VmName
        $msiLog = Invoke-LabGuest -Name $VmName -ScriptBlock {
            $log = Join-Path $env:TEMP 'RemoteHub-Agent-update.log'
            if (Test-Path $log) { @(Get-Content $log | Where-Object { $_ -match 'Return value 3|success or error status|WixQuietExec|Error \d{4}|Lab: ' } | ForEach-Object { [string]$_ }) }
        }
        Get-LabFile -Name $VmName -Path 'C:\ProgramData\RemoteHub\Agent\logs' -Destination (Join-Path $EvidenceDir "a-updfail-$Case-logs")
        # the whole verbose MSI log: the rollback part is what tells why the old agent did or didn't come back
        try { Get-LabFile -Name $VmName -Path 'C:\Users\PSAdmin\AppData\Local\Temp\RemoteHub-Agent-update.log' -Destination (Join-Path $EvidenceDir "a-updfail-$Case-logs") } catch { }
        $r = @{ Install = $install; Before = $before; Update = $update; Health = $health; After = $after; MsiLog = $msiLog }
        Save $Case $r
        [pscustomobject]$r
    }

    # what every failed update must leave: the old version running with the old identity
    function Assert-OldAgentKept($r) {
        $r.Update.verdict | Should -Not -Be 'updated' -Because "RemoteHub must not report a failed update as done ($($r.Update.message))"
        $r.Health | Should -Not -BeNullOrEmpty -Because 'the old agent must answer again'
        $r.Health.version | Should -Be $oldVersion
        $r.Health.deviceId | Should -Be $r.Install.deviceId
        $r.After.Product.Version | Should -Match "^$([regex]::Escape($oldVersion))"
        $r.After.Service.State | Should -Be 'Running'
        $r.After.Certificate.Sha256 | Should -Be $r.Install.pin
        @($r.After.OurCertificates).Count | Should -Be 1
        $r.After.Firewall.Count | Should -Be 1
        $r.After.Config.deviceId | Should -Be $r.Install.deviceId
    }
}

AfterAll {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'A-UPDFAIL <VmName>' -Tag Agent, 'A-UPDFAIL' {

    Context 'package that refuses to install' {
        BeforeAll {
            $broken = New-LabBrokenMsi -Source $newMsi -Path (Join-Path $work (Split-Path $newMsi -Leaf)) -Kind LaunchCondition
            $r = Invoke-FailedUpdate 'launch-condition' @{ Msi = $broken.FullName }
        }

        It 'A-UPDFAIL-01 reports "didn''t install" with the old version, and the old agent keeps running' -Tag 'A-UPDFAIL-01' {
            $r.Update.verdict | Should -Be 'notInstalled' -Because $r.Update.message
            $r.Update.message | Should -Match ([regex]::Escape("still runs RemoteHub Agent $oldVersion"))
            ($r.MsiLog -join "`n") | Should -Match 'Lab: this package is broken on purpose'
            Assert-OldAgentKept $r
        }
    }

    Context 'upgrade whose enrollment fails' {
        BeforeAll {
            # a damaged enrollment file makes "RemoteHubAgent.exe enroll" fail inside the upgrade, after the old
            # version was taken off - the whole upgrade must roll back
            $bad = Join-Path $work 'damaged.rhagent'
            Set-Content $bad '{ "format": 1, "this is": "not an enrollment" }' -Encoding ASCII
            $r = Invoke-FailedUpdate 'enroll-fails' @{ Msi = $newMsi; EnrollmentFile = $bad }
        }

        It 'A-UPDFAIL-02 rolls back to the old version with the old enrollment' -Tag 'A-UPDFAIL-02' {
            ($r.MsiLog -join "`n") | Should -Match 'Return value 3'
            Assert-OldAgentKept $r
            @($r.After.Leftovers) | Should -BeNullOrEmpty
        }
    }

    Context 'file that is not a package' {
        BeforeAll {
            $broken = New-LabBrokenMsi -Source $newMsi -Path (Join-Path $work 'truncated\RemoteHub-Agent-x64.msi') -Kind Truncated
            $r = Invoke-FailedUpdate 'not-a-package' @{ Msi = $broken.FullName }
        }

        It 'A-UPDFAIL-04 doesn''t report an unreadable package as updated' -Tag 'A-UPDFAIL-04' {
            Assert-OldAgentKept $r
        }
    }
}
