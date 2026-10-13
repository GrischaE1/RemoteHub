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
    [string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    # per scenario file and VM: a file that runs longer is ended and its cases are reported as failed
    [int]$FileTimeoutMin = 60
)
$ErrorActionPreference = 'Stop'
# powershell -File passes "-Vm a,b" as one string
$Vm = @($Vm | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Tag = @($Tag | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

Import-Module (Join-Path $PSScriptRoot 'RemoteHub.TestLab') -Force
$cfg = Get-LabConfig


$vms = @(if ($Vm) { Get-LabVm $Vm } else { Get-LabVm })
$runDir = Join-Path $cfg.LabRoot "results\$RunId"
$null = New-Item -ItemType Directory -Path $runDir -Force
$testFiles = @(Get-ChildItem (Join-Path $PSScriptRoot 'tests') -Filter '*.Tests.ps1' | ForEach-Object FullName)
$msi = Get-ChildItem (Join-Path $cfg.LabRoot 'msi') -Filter 'RemoteHub-Agent-*.msi' -ErrorAction SilentlyContinue |
    Select-Object -First 1

$previous = Get-ChildItem (Join-Path $cfg.LabRoot 'msi\previous') -Filter 'RemoteHub-Agent-*.msi' -ErrorAction SilentlyContinue |
    Select-Object -First 1

Write-Host "Run $RunId - VMs: $(($vms | ForEach-Object Id) -join ', ') - agent: $(if ($msi) { $msi.Name } else { 'none' })$(if ($previous) { " (previous: $($previous.Name))" })"

# What was tested, for the PR report (New-LabReport.ps1): the packages with their checksums and each VM's checkpoint.
function Get-MsiInfo($file) {
    if (-not $file) { return $null }
    [pscustomobject]@{ Name = $file.Name; Sha256 = (Get-FileHash $file.FullName -Algorithm SHA256).Hash }
}
[pscustomobject]@{
    RunId         = $RunId
    Started       = (Get-Date).ToString('s')
    Host          = $env:COMPUTERNAME
    Tags          = @($Tag)
    Agent         = Get-MsiInfo $msi
    PreviousAgent = Get-MsiInfo $previous
    Vms           = @($vms | ForEach-Object {
            $cp = Get-VMSnapshot -VMName $_.Name | Sort-Object CreationTime -Descending | Select-Object -First 1
            [pscustomobject]@{
                Id             = $_.Id
                Name           = $_.Name
                Checkpoint     = $(if ($cp) { $cp.Name } else { $null })
                CheckpointDate = $(if ($cp) { $cp.CreationTime.ToString('yyyy-MM-dd HH:mm') } else { $null })
            }
        })
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $runDir 'run.json') -Encoding UTF8

# VMs run one after another: only the VM under test is on. Every scenario reverts first, so turning off loses nothing.
Stop-LabVm

# One child process per scenario file and VM (Invoke-LabTestFile.ps1): a file that hangs is ended after
# -FileTimeoutMin, the run goes on with the next file. Its output is copied into this console as it comes.
function Invoke-TestFile([string]$File, $V, [string]$VmDir) {
    $base = [IO.Path]::GetFileNameWithoutExtension([IO.Path]::GetFileNameWithoutExtension($File))
    $xml = Join-Path $VmDir "pester-$base.xml"
    $log = Join-Path $VmDir "console-$base.log"
    $runner = Join-Path $PSScriptRoot 'Invoke-LabTestFile.ps1'
    $cmd = "& '$runner' -TestFile '$File' -VmName '$($V.Name)' -EvidenceDir '$VmDir' -OutputPath '$xml' -Tag '$($Tag -join ',')' *> '$log'"
    $p = Start-Process powershell.exe -ArgumentList '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', $cmd -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddMinutes($FileTimeoutMin)
    $shown = 0
    $timedOut = $false
    while (-not $p.HasExited) {
        if ((Get-Date) -gt $deadline) {
            $timedOut = $true
            & taskkill.exe /PID $p.Id /T /F 2>&1 | Out-Null
            break
        }
        Start-Sleep -Seconds 2
        $shown = Show-NewLines $log $shown
    }
    $null = Show-NewLines $log $shown
    if ($timedOut) {
        Write-Host "$base on $($V.Id): ended after $FileTimeoutMin min (time limit)" -ForegroundColor Red
        [pscustomobject]@{ File = $base; Minutes = $FileTimeoutMin; Ended = (Get-Date).ToString('s') }
    }
}
function Show-NewLines([string]$Log, [int]$Shown) {
    if (-not (Test-Path $Log)) { return $Shown }
    $lines = @(try { Get-Content $Log -ErrorAction Stop } catch { @() })
    for ($i = $Shown; $i -lt $lines.Count; $i++) { Write-Host $lines[$i] }
    [Math]::Max($Shown, $lines.Count)
}

$summary = foreach ($v in $vms) {
    $vmDir = Join-Path $runDir $v.Id
    $null = New-Item -ItemType Directory -Path $vmDir -Force
    $timeouts = @()
    try {
        foreach ($file in $testFiles) {
            $timeouts += @(Invoke-TestFile $file $v $vmDir)
        }
    }
    finally { Stop-LabVm -Name $v.Name }
    if ($timeouts) { ConvertTo-Json @($timeouts) | Set-Content (Join-Path $vmDir 'timeouts.json') -Encoding UTF8 }
    $counts = @(Get-ChildItem $vmDir -Filter 'pester-*.xml.summary.json' | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json })
    $failed = [int]($counts | Measure-Object Failed -Sum).Sum + $timeouts.Count
    [pscustomobject]@{
        Vm       = $v.Id
        Name     = $v.Name
        Passed   = [int]($counts | Measure-Object Passed -Sum).Sum
        Failed   = $failed
        Skipped  = [int]($counts | Measure-Object Skipped -Sum).Sum
        TimedOut = @($timeouts | ForEach-Object File) -join ', '
        Result   = $(if ($failed) { 'Failed' } else { 'Passed' })
    }
}
$summary | ConvertTo-Json | Set-Content (Join-Path $runDir 'summary.json') -Encoding UTF8
$summary | Format-Table -AutoSize | Out-String | Write-Host
exit [int](($summary | Measure-Object Failed -Sum).Sum)
