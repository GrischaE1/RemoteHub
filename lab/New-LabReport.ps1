#Requires -Version 5.1
<#
.SYNOPSIS
    Writes the lab report for a pull request: every case of docs\test-lab-cases.md against every VM of a run, the
    packages tested (with SHA-256) and the VMs' checkpoints. Reads artifacts\lab\<run>\run.json and <vm>\pester.xml.
    Exit code 0 = the release gate (docs\test-lab-cases.md section 1) is met, 1 = it isn't.
.EXAMPLE
    .\lab\New-LabReport.ps1                         # newest run in artifacts\lab
    .\lab\New-LabReport.ps1 -Run 20261012-125322
    .\lab\New-LabReport.ps1 -AllowNotAutomated      # gate ignores cases that have no test yet (partial runs)
#>
[CmdletBinding()]
param(
    [string]$Run,
    [string]$ResultsRoot = (Join-Path (Split-Path $PSScriptRoot) 'artifacts\lab'),
    [string]$Sheet = (Join-Path (Split-Path $PSScriptRoot) 'docs\test-lab-cases.md'),
    [switch]$AllowNotAutomated
)
$ErrorActionPreference = 'Stop'

# The script stays ASCII (Windows PowerShell 5.1 reads BOM-less files as ANSI); the report itself is UTF-8.
$ok = [char]0x2705; $bad = [char]0x274C; $skip = [char]0x23ED; $none = [char]0x2014

if (-not $Run) {
    $Run = Get-ChildItem $ResultsRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
    if (-not $Run) { throw "No runs in $ResultsRoot." }
}
$runDir = Join-Path $ResultsRoot $Run
if (-not (Test-Path $runDir)) { throw "Run folder $runDir not found." }

# ---- the sheet: ID, title, priority, area (first part of the ID)
$areaNames = [ordered]@{
    SMOKE = 'Lab self-check'; A = 'Agent install / update / uninstall'; S = 'Sign-in and transport security'
    P = 'PowerShell over the agent'; T = 'Remote tools'; W = 'WinRM setup script'; D = 'Agent remote desktop'
    C = 'RemoteHub on a client'; R = 'RDP and agent through RemoteHub'; UI = 'End to end in RemoteHub''s window'
}
$cases = [ordered]@{}
foreach ($line in [IO.File]::ReadAllLines($Sheet, [Text.Encoding]::UTF8)) {
    if ($line -notmatch '^\|\s*([A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d{2,})\s*\|') { continue }
    $cells = @($line.Trim().Trim('|') -split '\|' | ForEach-Object { $_.Trim() })
    $id = $cells[0]
    $prio = @($cells | Select-Object -Skip 3 | Where-Object { $_ -match '^[123]$' } | Select-Object -First 1)
    $cases[$id] = [pscustomobject]@{
        Id       = $id
        Title    = ($cells[1] -replace '\*\*', '')
        Priority = $(if ($prio) { [int]$prio[0] } else { $null })
        Area     = ($id -split '-')[0]
    }
}

# ---- results: pester-<file>.xml per scenario file in each VM folder (pester.xml in runs before the per-file limit)
$meta = $null
$metaFile = Join-Path $runDir 'run.json'
if (Test-Path $metaFile) { $meta = Get-Content $metaFile -Raw | ConvertFrom-Json }
$vmDirs = @(Get-ChildItem $runDir -Directory | Where-Object { Get-ChildItem $_.FullName -Filter 'pester*.xml' })
if ($vmDirs.Count -eq 0) { throw "No pester*.xml in $runDir." }
$rank = @{ Passed = 0; Skipped = 1; Failed = 2 }
$vmIds = @($vmDirs | ForEach-Object Name)

$results = @{}     # "<id>|<vm>" -> result object
$unknown = New-Object System.Collections.Generic.List[string]
foreach ($dir in $vmDirs) {
    foreach ($tc in @(Get-ChildItem $dir.FullName -Filter 'pester*.xml' | ForEach-Object { ([xml](Get-Content $_.FullName -Raw)).SelectNodes('//test-case') })) {
        $desc = [string]$tc.description
        if ($desc -notmatch '^([A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d{2,})\s') { $unknown.Add("$($dir.Name): $desc (no case ID)"); continue }
        $id = $Matches[1]
        if (-not $cases.Contains($id)) { $unknown.Add("$($dir.Name): $id is not in the sheet") }
        $outcome = switch ([string]$tc.result) {
            'Success' { 'Passed' }
            { $_ -in 'Failure', 'Error' } { 'Failed' }
            default { 'Skipped' }   # Ignored, Skipped, Inconclusive, NotRunnable
        }
        $msg = $tc.SelectSingleNode('failure/message|reason/message')
        # one case can run more than once (T over the agent and over WinRM): the worst outcome counts
        $prev = $results["$id|$($dir.Name)"]
        if ($prev -and $rank[$prev.Outcome] -ge $rank[$outcome]) { continue }
        $results["$id|$($dir.Name)"] = [pscustomobject]@{
            Outcome = $outcome
            Message = $(if ($msg) { ($msg.InnerText -replace '\s+', ' ').Trim() } else { '' })
        }
    }
    # a file ended by the time limit: its cases without a result failed
    $toFile = Join-Path $dir.FullName 'timeouts.json'
    if (Test-Path $toFile) {
        foreach ($to in @(Get-Content $toFile -Raw | ConvertFrom-Json)) {
            $src = Join-Path $PSScriptRoot "tests\$($to.File).ps1"
            if (-not (Test-Path $src)) { continue }
            foreach ($m in [regex]::Matches((Get-Content $src -Raw), "-Tag '([A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d{2,})'")) {
                $id = $m.Groups[1].Value
                if ($results.ContainsKey("$id|$($dir.Name)")) { continue }
                if ($meta -and @($meta.Tags | Where-Object { $_ }).Count -gt 0) {
                    # filtered run: only cases the filter selected (by case ID or a tag in the same file)
                    if (-not (@($meta.Tags) | Where-Object { $id -like "$_*" -or (Get-Content $src -Raw) -match "-Tag[^\n]*'$([regex]::Escape($_))'" })) { continue }
                }
                $results["$id|$($dir.Name)"] = [pscustomobject]@{ Outcome = 'Failed'; Message = "$($to.File) was ended after $($to.Minutes) min (time limit) before this case finished." }
            }
        }
    }
}

# ---- gate
$failed = @(); $skipped = @(); $notRun = @()
foreach ($id in $cases.Keys) {
    foreach ($vm in $vmIds) {
        $r = $results["$id|$vm"]
        if (-not $r) { continue }
        if ($r.Outcome -eq 'Failed') { $failed += [pscustomobject]@{ Id = $id; Vm = $vm; Message = $r.Message } }
        elseif ($r.Outcome -eq 'Skipped') { $skipped += [pscustomobject]@{ Id = $id; Vm = $vm; Message = $r.Message } }
    }
    if (-not ($vmIds | Where-Object { $results.ContainsKey("$id|$_") })) { $notRun += $cases[$id] }
}
$filtered = $meta -and @($meta.Tags | Where-Object { $_ }).Count -gt 0
$gateMet = $failed.Count -eq 0 -and $unknown.Count -eq 0 -and ($AllowNotAutomated -or $notRun.Count -eq 0)

# ---- markdown
$sb = New-Object System.Text.StringBuilder
function Add([string]$s = '') { [void]$sb.AppendLine($s) }
function Cell($id, $vm) {
    $r = $results["$id|$vm"]
    if (-not $r) { return $none }
    switch ($r.Outcome) { 'Passed' { $ok } 'Failed' { $bad } default { $skip } }
}

Add "### Lab report $([char]0x2013) run $Run"
Add
if ($meta) {
    $pkg = if ($meta.Agent) { "Agent: ``$($meta.Agent.Name)`` (SHA-256 ``$($meta.Agent.Sha256)``)" } else { 'Agent: none' }
    if ($meta.PreviousAgent) { $pkg += " $([char]0x00B7) previous: ``$($meta.PreviousAgent.Name)``" }
    Add $pkg
    Add
    Add ('VMs: ' + (($meta.Vms | ForEach-Object { "``$($_.Id)`` (checkpoint $($_.CheckpointDate))" }) -join " $([char]0x00B7) "))
    if ($filtered) { Add; Add "Filtered run: ``-Tag $($meta.Tags -join ',')``" }
    Add
}
$gateText = if ($gateMet) { "$ok **Release gate met**" } else { "$bad **Release gate not met**" }
Add "$gateText $([char]0x2013) failed: $($failed.Count), skipped: $($skipped.Count), not automated: $($notRun.Count) of $($cases.Count) cases"
Add

# per area
Add ('| Area | Cases | ' + ($vmIds -join ' | ') + ' |')
Add ('|---|---|' + (($vmIds | ForEach-Object { '---' }) -join '|') + '|')
foreach ($area in $areaNames.Keys) {
    $ids = @($cases.Values | Where-Object Area -eq $area | ForEach-Object Id)
    if ($ids.Count -eq 0) { continue }
    $cols = foreach ($vm in $vmIds) {
        $p = @($ids | Where-Object { $results["$_|$vm"].Outcome -eq 'Passed' }).Count
        $f = @($ids | Where-Object { $results["$_|$vm"].Outcome -eq 'Failed' }).Count
        $s = @($ids | Where-Object { $results["$_|$vm"].Outcome -eq 'Skipped' }).Count
        $parts = @()
        if ($p) { $parts += "$p $ok" }
        if ($f) { $parts += "$f $bad" }
        if ($s) { $parts += "$s $skip" }
        if ($parts.Count -eq 0) { $none } else { $parts -join ' ' }
    }
    Add ("| $($areaNames[$area]) ($area) | $($ids.Count) | " + ($cols -join ' | ') + ' |')
}
Add

# cases that failed the first time and passed on the file's second attempt (flaky.json, LabRetry.ps1): they don't fail
# the gate, but are named - each one is a case to make reliable (Get-LabFlaky.ps1 has their history)
$flakyFile = Join-Path $runDir 'flaky.json'
$flaky = @(if (Test-Path $flakyFile) { Get-Content $flakyFile -Raw | ConvertFrom-Json | Where-Object { $_ -and $_.Id } })
if ($flaky.Count) {
    Add "**Flaky** $([char]0x2013) passed on the file's second attempt"
    Add
    foreach ($f in $flaky) { Add "- ``$($f.Id)`` on ``$($f.Vm)`` $([char]0x2013) first attempt: $($f.Message)" }
    Add
}
if ($failed.Count) {
    Add '**Failed**'
    Add
    foreach ($f in $failed) { Add "- ``$($f.Id)`` on ``$($f.Vm)`` $([char]0x2013) $($cases[$f.Id].Title): $($f.Message)" }
    Add
}
if ($skipped.Count) {
    Add '**Skipped**'
    Add
    foreach ($s in $skipped) { Add "- ``$($s.Id)`` on ``$($s.Vm)``: $(if ($s.Message) { $s.Message } else { 'no reason given' })" }
    Add
}
if ($unknown.Count) {
    Add '**Results that don''t match the sheet** (fix the test name or add the case)'
    Add
    foreach ($u in $unknown) { Add "- $u" }
    Add
}

# all executed cases
Add '<details><summary>All executed cases</summary>'
Add
Add ('| Case | Test | P | ' + ($vmIds -join ' | ') + ' |')
Add ('|---|---|---|' + (($vmIds | ForEach-Object { '---' }) -join '|') + '|')
foreach ($c in $cases.Values) {
    if ($notRun -contains $c) { continue }
    Add ("| $($c.Id) | $($c.Title) | $($c.Priority) | " + (($vmIds | ForEach-Object { Cell $c.Id $_ }) -join ' | ') + ' |')
}
Add
Add '</details>'
Add
Add "<details><summary>Not automated yet ($($notRun.Count))</summary>"
Add
foreach ($area in $areaNames.Keys) {
    $ids = @($notRun | Where-Object Area -eq $area | ForEach-Object Id)
    if ($ids.Count) { Add "- **$($areaNames[$area])** ($($ids.Count)): $($ids -join ', ')" }
}
Add
Add '</details>'

$out = Join-Path $runDir 'report.md'
[IO.File]::WriteAllText($out, $sb.ToString(), (New-Object Text.UTF8Encoding $false))
Write-Host "Report: $out"
Write-Host ($gateText -replace '\*\*', '') "- failed $($failed.Count), skipped $($skipped.Count), not automated $($notRun.Count)/$($cases.Count)"
exit [int](-not $gateMet)
