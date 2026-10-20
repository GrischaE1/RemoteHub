# D-AUD - the agent's audit across the scenarios of this run on this kind of VM, both lanes (named to run last): every scenario saved the
# agent's log files and Application events into its evidence folder (Save-LabAgentEvidence). Checks that each audit ID
# the scenarios cause was written with who, from where, what and how long - and that no screen content, keystrokes or
# clipboard text (the marker strings other scenarios typed and copied) and no password is in any log or event.
# See docs/test-lab-cases.md section 9.11.
# The VM itself isn't touched (only the evidence folder is read): no Initialize-LabVm, its reuse state stays as it is.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    # with two lanes a kind's scenarios are split between <kind> and <kind>-2 (Invoke-LabTests: EvidenceDir is
    # runDir\<VM id>), so the audit IDs they cause are read from both lanes' folders
    $kind = (Split-Path $EvidenceDir -Leaf) -replace '-2$', ''
    $runDir = Split-Path $EvidenceDir -Parent
    $dirs = @(@($kind, "$kind-2") | ForEach-Object { Join-Path $runDir $_ } | Where-Object { Test-Path $_ })
    if ($dirs.Count -eq 0) { $dirs = @($EvidenceDir) }
    $events = @(Get-ChildItem $dirs -Recurse -Filter 'events.json' -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json } | ForEach-Object { $_ })
    $logText = (@(Get-ChildItem $dirs -Recurse -Filter 'agent-*.log' -ErrorAction SilentlyContinue | Get-Content -Raw) + @($events | ForEach-Object Message)) -join "`n"
    $byId = @{}; foreach ($e in $events) { if (-not $byId.ContainsKey([int]$e.Id)) { $byId[[int]$e.Id] = @() }; $byId[[int]$e.Id] += $e }
}

Describe 'D-AUD <VmName>' -Tag Desktop, 'D-AUD' {

    It 'D-AUD-01 each audit ID the scenarios cause was written, with account, source, session, consent, view-only and duration' -Tag 'D-AUD-01' {
        if ($events.Count -eq 0) { Set-ItResult -Skipped -Because 'no other scenario ran on this VM in this run (run it with the D scenarios)'; return }
        # the IDs of the scenarios in this run (others - jump host, automatic sign-in - only when those ran too)
        $expected = @(1000, 1001, 1100, 1101, 1102, 1103, 1104, 1105, 2000, 2100, 2101, 2102, 2103)
        if (Get-ChildItem $dirs -Directory -Filter 'j-*' -ErrorAction SilentlyContinue) { $expected += 1200, 1201, 1202, 2200 }
        if (Get-ChildItem $dirs -Directory -Filter 'c-auto*' -ErrorAction SilentlyContinue) { $expected += 1106, 2105 }
        if (Get-ChildItem $dirs -Directory -Filter 'c-auto2*' -ErrorAction SilentlyContinue) { $expected += 2104 }
        if (Get-ChildItem $dirs -Directory -Filter 'c-end*' -ErrorAction SilentlyContinue) { $expected += 1107 }
        $missing = @($expected | Where-Object { -not $byId.ContainsKey($_) })
        $missing | Should -BeNullOrEmpty -Because "IDs never written: $($missing -join ', ')"
        # '... from <address> - control, session 3 (sign-in screen), consent None ...': each part, not their order
        foreach ($m in @($byId[1100])) {
            $m.Message | Should -Match 'from \S+'
            $m.Message | Should -Match 'session \d+'
            $m.Message | Should -Match '\b(control|view-only)\b'
            $m.Message | Should -Match 'consent'
        }
        foreach ($m in @($byId[1101])) { $m.Message | Should -Match '\d{2}:\d{2}:\d{2}' }
        # 'Refused <account> from <address> ...', or the throttle's 'Refused <address>: too many failed sign-ins'
        foreach ($m in @($byId[2000])) { $m.Message | Should -Match 'Refused .+ from \S+|Refused \S+: too many failed sign-ins' }
    }

    It 'D-AUD-02 no screen content, keystrokes or clipboard text in logs or events' -Tag 'D-AUD-02' {
        if ($events.Count -eq 0) { Set-ItResult -Skipped -Because 'no other scenario ran on this VM in this run'; return }
        foreach ($marker in 'The quick brown fox', 'after the flood', 'RHLABDEVICECLIP', 'text at level', 'from RemoteHub in view-only', 'RHLABSECRETAPPTEXT', 'viewonly', '0123456789012345') {
            $logText | Should -Not -Match ([regex]::Escape($marker)) -Because "'$marker' was typed or copied in a session"
        }
        $logText | Should -Not -Match ([regex]::Escape((Get-LabConfig).Password))
    }
}
