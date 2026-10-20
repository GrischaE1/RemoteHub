#Requires -Version 5.1
<#
.SYNOPSIS
    Runs ONE scenario file against ONE VM - the child process Invoke-LabTests.ps1 starts per file, so a file that hangs
    can be ended after its time limit without losing the rest of the run. Writes the NUnit result to -OutputPath and a
    one-line summary next to it (<OutputPath>.summary.json).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TestFile,
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$Tag,
    # Invoke-LabTests.ps1 -FailFast: a Describe block skips its other cases after its first failure
    [switch]$FailFast
)
$ErrorActionPreference = 'Stop'
# the module times its slow helpers (written to <OutputPath>.timing.json below: where the file's time went)
$env:RHLAB_TIMING = '1'
$clock = [Diagnostics.Stopwatch]::StartNew()
Import-Module (Join-Path $PSScriptRoot 'RemoteHub.TestLab') -Force
$cfg = Get-LabConfig
$env:PSModulePath = (Join-Path $cfg.LabRoot 'modules') + [IO.Path]::PathSeparator + $env:PSModulePath
Import-Module Pester -MinimumVersion 5.5 -Force

$pc = New-PesterConfiguration
$pc.Run.Container = New-PesterContainer -Path $TestFile -Data @{ VmName = $VmName; EvidenceDir = $EvidenceDir }
$pc.Run.PassThru = $true
$tags = @($Tag -split ',' | Where-Object { $_ })
if ($tags) { $pc.Filter.Tag = $tags }
if ($FailFast) { $pc.Run.SkipRemainingOnFailure = 'Block' }
$pc.TestResult.Enabled = $true
$pc.TestResult.OutputFormat = 'NUnitXml'
$pc.TestResult.OutputPath = $OutputPath
$pc.Output.Verbosity = 'Detailed'

$r = Invoke-Pester -Configuration $pc
[pscustomobject]@{
    Passed  = $r.PassedCount
    Failed  = $r.FailedCount + $r.FailedBlocksCount + $r.FailedContainersCount
    Skipped = $r.SkippedCount
    Total   = $r.TotalCount
} | ConvertTo-Json -Compress | Set-Content "$OutputPath.summary.json" -Encoding UTF8

# minutes per helper (outermost calls only) and what the file did besides them (its own waits and checks)
$calls = @(if (Get-Variable RhLabTiming -Scope Global -ErrorAction SilentlyContinue) {
        foreach ($c in $global:RhLabTiming.Calls.GetEnumerator()) {
            [pscustomobject]@{ Helper = $c.Key; Calls = $c.Value.Count; Minutes = [math]::Round($c.Value.Seconds / 60, 2) }
        }
    }) | Sort-Object Minutes -Descending
$total = [math]::Round($clock.Elapsed.TotalMinutes, 2)
[pscustomobject]@{
    Minutes = $total
    Rest    = [math]::Round($total - ($calls | Measure-Object Minutes -Sum).Sum, 2)
    Helpers = @($calls)
} | ConvertTo-Json -Depth 3 | Set-Content "$OutputPath.timing.json" -Encoding UTF8
