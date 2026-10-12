#Requires -Version 5.1
<#
.SYNOPSIS
    Runs the lab scenarios (lab\tests\*.Tests.ps1) once per VM. Runs as labop - from the scheduled task or through
    Start-LabRun.ps1. Every scenario file reverts its VM first.
.EXAMPLE
    .\Invoke-LabTests.ps1                         # all VMs in lab.json, all scenarios
    .\Invoke-LabTests.ps1 -Vm W11-26H2-en-US -Tag Smoke
#>
[CmdletBinding()]
param(
    [string[]]$Vm,
    [string[]]$Tag,
    [string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss')
)
$ErrorActionPreference = 'Stop'
# powershell -File passes "-Vm a,b" as one string
$Vm = @($Vm | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Tag = @($Tag | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

Import-Module (Join-Path $PSScriptRoot 'RemoteHub.TestLab') -Force
$cfg = Get-LabConfig

# Pester 5 lives in LabRoot\modules; the built-in Pester 3.4 is never used.
$env:PSModulePath = (Join-Path $cfg.LabRoot 'modules') + [IO.Path]::PathSeparator + $env:PSModulePath
Import-Module Pester -MinimumVersion 5.5 -Force

$vms = @(if ($Vm) { Get-LabVm $Vm } else { Get-LabVm })
$runDir = Join-Path $cfg.LabRoot "results\$RunId"
$null = New-Item -ItemType Directory -Path $runDir -Force
$testFiles = @(Get-ChildItem (Join-Path $PSScriptRoot 'tests') -Filter '*.Tests.ps1' | ForEach-Object FullName)
$msi = Get-ChildItem (Join-Path $cfg.LabRoot 'msi') -Filter 'RemoteHub-Agent-*.msi' -ErrorAction SilentlyContinue |
    Select-Object -First 1

Write-Host "Run $RunId - VMs: $(($vms | ForEach-Object Id) -join ', ') - agent: $(if ($msi) { $msi.Name } else { 'none' })"

# VMs run one after another: only the VM under test is on. Every scenario reverts first, so turning off loses nothing.
Stop-LabVm

$summary = foreach ($v in $vms) {
    $vmDir = Join-Path $runDir $v.Id
    $null = New-Item -ItemType Directory -Path $vmDir -Force

    $pc = New-PesterConfiguration
    $pc.Run.Container = New-PesterContainer -Path $testFiles -Data @{ VmName = $v.Name; EvidenceDir = $vmDir }
    $pc.Run.PassThru = $true
    if ($Tag) { $pc.Filter.Tag = $Tag }
    $pc.TestResult.Enabled = $true
    $pc.TestResult.OutputFormat = 'NUnitXml'
    $pc.TestResult.OutputPath = Join-Path $vmDir 'pester.xml'
    $pc.Output.Verbosity = 'Detailed'

    try { $r = Invoke-Pester -Configuration $pc }
    finally { Stop-LabVm -Name $v.Name }
    [pscustomobject]@{
        Vm      = $v.Id
        Name    = $v.Name
        Passed  = $r.PassedCount
        Failed  = $r.FailedCount + $r.FailedBlocksCount + $r.FailedContainersCount
        Skipped = $r.SkippedCount
        Result  = $r.Result
    }
}

$summary | ConvertTo-Json | Set-Content (Join-Path $runDir 'summary.json') -Encoding UTF8
$summary | Format-Table -AutoSize | Out-String | Write-Host
exit [int](($summary | Measure-Object Failed -Sum).Sum)
