#Requires -Version 5.1
<#
.SYNOPSIS
    Direct call from any shell, no elevation: builds the lab harness (RemoteHub's client code) into LabRoot\harness,
    copies lab\ and the newest agent MSI to LabRoot, has the scheduled task 'RemoteHub Lab' run the scenarios as
    labop, waits, then copies the results back to artifacts\lab\<run>\.
.EXAMPLE
    .\lab\Start-LabRun.ps1
    .\lab\Start-LabRun.ps1 -Vm W11-26H2-en-US -Tag Smoke
    .\lab\Start-LabRun.ps1 -Tag A-INST -NoBuild
#>
[CmdletBinding()]
param(
    [string[]]$Vm,
    [string[]]$Tag,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LabQueue.ps1')

$repo = Split-Path $PSScriptRoot
Publish-LabFiles

if (-not $NoBuild) {
    Write-Host 'Building the lab harness...'
    $build = dotnet build (Join-Path $repo 'tests\RemoteHub.LabHarness') -c Release -o (Join-Path $LabRoot 'harness') -nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) { $build | Select-Object -Last 30 | Write-Host; throw 'Building the lab harness failed.' }
}

# The agent under test: newest RemoteHub-Agent-<version>-x64.msi in artifacts\. The one before it goes to msi\previous
# (the update scenarios install it first, then update to the one under test).
$agentMsis = @(Get-ChildItem (Join-Path $repo 'artifacts') -Filter 'RemoteHub-Agent-*-x64.msi' |
        Where-Object { $_.Name -match '^RemoteHub-Agent-\d+\.\d+\.\d+-x64\.msi$' } |
        Sort-Object { [version]($_.Name -replace '^RemoteHub-Agent-(.+)-x64\.msi$', '$1') } -Descending)
function Publish-Msi($file, $dir) {
    $null = New-Item -ItemType Directory -Path $dir -Force
    Get-ChildItem $dir -Filter *.msi | Where-Object { -not $file -or $_.Name -ne $file.Name } | Remove-Item
    if ($file) {
        robocopy $file.DirectoryName $dir $file.Name /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy of $($file.Name) failed ($LASTEXITCODE)." }
    }
}
$msi = $agentMsis | Select-Object -First 1
$previousMsi = $agentMsis | Select-Object -Skip 1 -First 1
Publish-Msi $msi (Join-Path $LabRoot 'msi')
Publish-Msi $previousMsi (Join-Path $LabRoot 'msi\previous')

$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $LabRoot "results\$runId"
Write-Host "Run $runId$(if ($msi) { ", agent $($msi.Name)" }) - queued for '$LabTask'"
Submit-LabRequest -Request @{ Type = 'tests'; Id = $runId; Vm = @($Vm); Tag = @($Tag) } -DoneFile (Join-Path $runDir 'done.json')

$back = Join-Path $repo "artifacts\lab\$runId"
robocopy $runDir $back /E /NFL /NDL /NJH /NJS /NP | Out-Null
Get-Content (Join-Path $runDir 'console.log') -Tail 60
Write-Host "Results: $back"
# The PR report (case x VM, gate) - its verdict is printed; the exit code stays the number of failed tests.
try { & (Join-Path $PSScriptRoot 'New-LabReport.ps1') -Run $runId -ResultsRoot (Join-Path $repo 'artifacts\lab') }
catch { Write-Warning "No report: $($_.Exception.Message)" }
exit [int](Get-Content (Join-Path $runDir 'done.json') -Raw | ConvertFrom-Json).ExitCode
