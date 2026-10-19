#Requires -Version 5.1
<#
.SYNOPSIS
    Each case's latest lab result: Id, Result (Passed / Failed / Skipped), Run, Date and the VMs it failed on - from
    every finished run's pester XMLs. A case that never ran is not listed.
.DESCRIPTION
    A case's result is the one of the newest run that ran it: Failed if it failed on any VM there, Passed if it passed
    on every VM it ran on, Skipped if it was only skipped. Finished runs don't change, so each is read once and kept
    in a cache (%LOCALAPPDATA%\RemoteHubLab\case-status.json); later calls read only new runs.
    Used by LabConsole.ps1 (the status in the test tree) and Update-LabCaseStatus.ps1 (the cases document).
.PARAMETER Root
    The folder holding the runs. Default: the lab's own results (LabRoot\results from lab.json), which keep every
    run; artifacts\lab of a checkout has only the runs started from it.
.EXAMPLE
    .\lab\Get-LabCaseStatus.ps1 | Where-Object Result -eq Failed
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path (Get-Content (Join-Path $PSScriptRoot 'lab.json') -Raw | ConvertFrom-Json).LabRoot 'results'),
    [string]$Cache = (Join-Path $env:LOCALAPPDATA 'RemoteHubLab\case-status.json')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Root)) { throw "No lab results in $Root." }

# run -> { id -> "P" / "F:<vm>,<vm>" / "S" } for every finished run read before
$known = @{}
if (Test-Path $Cache) {
    try {
        $c = Get-Content $Cache -Raw | ConvertFrom-Json
        if ($c.PSObject.Properties['Root'] -and $c.Root -eq $Root) {
            foreach ($r in $c.Runs.PSObject.Properties) {
                $ids = @{}
                foreach ($p in $r.Value.PSObject.Properties) { $ids[$p.Name] = [string]$p.Value }
                $known[$r.Name] = $ids
            }
        }
    }
    catch { $known = @{} }   # a damaged cache is read again from the runs
}

$changed = $false
$runs = @(Get-ChildItem $Root -Directory | Where-Object Name -match '^\d{8}-\d{6}$' | Sort-Object Name)
foreach ($run in $runs) {
    if ($known.ContainsKey($run.Name)) { continue }
    $finished = Test-Path (Join-Path $run.FullName 'done.json')
    $ids = @{}
    foreach ($xml in @(Get-ChildItem $run.FullName -Recurse -Filter 'pester-*.xml')) {
        try { [xml]$x = Get-Content $xml.FullName -Raw } catch { continue }
        foreach ($tc in @($x.SelectNodes('//test-case'))) {
            $text = $tc.GetAttribute('description')
            if (-not $text) { $text = ($tc.GetAttribute('name') -split '\.')[-1] }
            if ($text -notmatch '^([A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d{2,})\b') { continue }
            $id = $Matches[1]
            $vm = ($tc.GetAttribute('name') -split '\.')[0] -replace '^\S+ (TestAutomation - )?', ''
            $now = switch ($tc.GetAttribute('result')) { 'Success' { 'P' } 'Failure' { "F:$vm" } default { 'S' } }
            $was = if ($ids.ContainsKey($id)) { $ids[$id] } else { '' }
            # a failure anywhere wins, then a pass; failed VMs are collected
            $ids[$id] = if ($now -like 'F:*' -and $was -like 'F:*') { "$was,$vm" } elseif ($now -like 'F:*') { $now }
            elseif ($was -like 'F:*') { $was } elseif ($now -eq 'P' -or $was -eq 'P') { 'P' } else { 'S' }
        }
    }
    $known[$run.Name] = $ids
    # a run still going is read again next time
    if ($finished) { $changed = $true } else { $known.Remove($run.Name) | Out-Null; $known["~$($run.Name)"] = $ids }
}

if ($changed) {
    $save = [ordered]@{ Root = $Root; Runs = [ordered]@{} }
    foreach ($k in @($known.Keys | Where-Object { $_ -notlike '~*' } | Sort-Object)) { $save.Runs[$k] = $known[$k] }
    $null = New-Item -ItemType Directory -Path (Split-Path $Cache) -Force
    $save | ConvertTo-Json -Depth 4 -Compress | Set-Content $Cache -Encoding UTF8
}

# the newest run decides
$latest = @{}
foreach ($k in @($known.Keys | Sort-Object { $_.TrimStart('~') })) {
    foreach ($id in $known[$k].Keys) { $latest[$id] = @($k.TrimStart('~'), $known[$k][$id]) }
}
foreach ($id in @($latest.Keys | Sort-Object)) {
    $run, $r = $latest[$id]
    [pscustomobject]@{
        Id = $id
        Result = switch -Wildcard ($r) { 'P' { 'Passed' } 'F:*' { 'Failed' } default { 'Skipped' } }
        Run = $run
        Date = [datetime]::ParseExact($run, 'yyyyMMdd-HHmmss', $null)
        FailedOn = if ($r -like 'F:*') { $r.Substring(2) } else { '' }
    }
}
