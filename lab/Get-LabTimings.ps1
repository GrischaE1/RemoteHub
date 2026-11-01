#Requires -Version 5.1
<#
.SYNOPSIS
    Where a lab run's time went, from the <result>.timing.json files Invoke-LabTestFile.ps1 writes: per helper (VM
    resets, installs, UI steps ...) the minutes over the whole run, then the files that took longest with their top
    helpers. "Rest" is the files' own time - their waits and checks between helper calls.
.EXAMPLE
    .\lab\Get-LabTimings.ps1                     # the newest run in artifacts\lab
    .\lab\Get-LabTimings.ps1 -Run 20261020-090000 -Top 15
#>
[CmdletBinding()]
param(
    [string]$Run,
    [string]$Root,
    [int]$Top = 10
)
$ErrorActionPreference = 'Stop'
# not a parameter default: under powershell.exe -File, 5.1 leaves $PSScriptRoot empty there
if (-not $Root) { $Root = Join-Path (Split-Path $PSScriptRoot) 'artifacts\lab' }
if (-not $Run) {
    $Run = Get-ChildItem $Root -Directory | Where-Object Name -match '^\d{8}-\d{6}$' | Sort-Object Name -Descending |
        Select-Object -First 1 -ExpandProperty Name
}
$dir = Join-Path $Root $Run
$files = @(Get-ChildItem $dir -Recurse -Filter 'pester-*.xml.timing.json' -ErrorAction SilentlyContinue)
if (-not $files.Count) { Write-Output "Run $Run - no timings (runs before timing was added have none)"; return }

$runs = foreach ($f in $files) {
    $t = Get-Content $f.FullName -Raw | ConvertFrom-Json
    [pscustomobject]@{
        File    = $f.Name -replace '^pester-(.+)\.xml\.timing\.json$', '$1'
        Kind    = $f.Directory.Name
        Minutes = [double]$t.Minutes
        Rest    = [double]$t.Rest
        Helpers = @($t.Helpers)
    }
}
$all = ($runs | Measure-Object Minutes -Sum).Sum
Write-Output ("Run {0} - {1} file runs, {2:N0} VM-minutes" -f $Run, $runs.Count, $all)
Write-Output ''
Write-Output 'Per helper over the run (outermost calls):'
$byHelper = @($runs | ForEach-Object { $_.Helpers } | Group-Object Helper | ForEach-Object {
        [pscustomobject]@{ Helper = $_.Name; Calls = ($_.Group | Measure-Object Calls -Sum).Sum; Minutes = ($_.Group | Measure-Object Minutes -Sum).Sum }
    }) + @([pscustomobject]@{ Helper = '(the files'' own time)'; Calls = $null; Minutes = ($runs | Measure-Object Rest -Sum).Sum })
foreach ($h in $byHelper | Sort-Object Minutes -Descending) {
    Write-Output ("  {0,-26} {1,7:N0} min {2,4:N0}%  {3}" -f $h.Helper, $h.Minutes, (100 * $h.Minutes / [math]::Max(1, $all)), $(if ($h.Calls) { "$($h.Calls) calls" }))
}
Write-Output ''
Write-Output "Longest file runs:"
foreach ($r in $runs | Sort-Object Minutes -Descending | Select-Object -First $Top) {
    $top3 = @($r.Helpers | Sort-Object Minutes -Descending | Select-Object -First 3 | ForEach-Object { '{0} {1:N1}' -f $_.Helper, $_.Minutes })
    Write-Output ("  {0,-12} {1,-22} {2,5:N0} min  rest {3:N1} | {4}" -f $r.File, $r.Kind, $r.Minutes, $r.Rest, ($top3 -join ', '))
}
