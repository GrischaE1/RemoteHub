<#
.SYNOPSIS
    Reports whether this node is in its DSC desired state, which resources drifted, the last run and the LCM mode.
.DESCRIPTION
    Uses Test-DscConfiguration -Detailed, Get-DscConfigurationStatus -All, Get-DscConfiguration for the current
    state of drifted resources and the DSC operational event log. -Sensor returns only True, False or
    "Data unavailable". Testing while the LCM is applying returns a busy error, so the LCM state is checked first
    and a busy node reports "Data unavailable" rather than a failure. In ApplyOnly mode the node is tested only
    on request. Testing can be heavy for large configurations; schedule it, or use -SkipTest for status only.
.NOTES
    RemoteHub: id=DSC-10; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [switch]$Sensor,
    [switch]$SkipTest,
    [ValidateRange(1, 720)]
    [int]$EventHours = 24
)

$ErrorActionPreference = 'Stop'
$na = 'Data unavailable'

try {
    if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Run this script in Windows PowerShell 5.1 (DSC 1.1).' }

    $meta = (Invoke-CimMethod -Namespace 'root/Microsoft/Windows/DesiredStateConfiguration' `
            -ClassName 'MSFT_DSCLocalConfigurationManager' -MethodName 'GetMetaConfiguration').MetaConfiguration
    $lcmState = [string]$meta.LCMState

    $history = @()
    try { $history = @(Get-DscConfigurationStatus -All -ErrorAction Stop | Sort-Object StartDate -Descending) }
    catch { Write-Verbose "No configuration status: $($_.Exception.Message)" }
    $last = $history | Select-Object -First 1

    $inState = $na
    $drift = @()
    $reason = ''
    if ($history.Count -eq 0) {
        $reason = 'No configuration has been applied to this node'
    } elseif ($lcmState -eq 'Busy') {
        $reason = 'The LCM is busy applying a configuration'
    } elseif ($SkipTest) {
        $reason = 'Test skipped; last run status shown'
    } elseif ((Get-Service -Name WinRM).Status -ne 'Running') {
        $reason = 'WinRM is not running, so the configuration cannot be tested'
    } else {
        $test = Test-DscConfiguration -Detailed -ErrorAction Stop
        $inState = [bool]$test.InDesiredState
        $notIn = @($test.ResourcesNotInDesiredState)
        if ($notIn.Count -gt 0) {
            # Get-DscConfiguration runs Get on every resource, so it is only called when something drifted.
            $current = @{}
            try {
                foreach ($c in @(Get-DscConfiguration -ErrorAction Stop)) { $current[[string]$c.ResourceId] = $c }
            } catch { Write-Verbose "Get-DscConfiguration failed: $($_.Exception.Message)" }
            $drift = foreach ($r in $notIn) {
                $state = $na
                $c = $current[[string]$r.ResourceId]
                if ($c) {
                    $props = [ordered]@{}
                    foreach ($p in $c.CimInstanceProperties) {
                        if ($p.Name -in @('ResourceId', 'ConfigurationName', 'ModuleName', 'ModuleVersion', 'SourceInfo', 'PSComputerName', 'DependsOn')) { continue }
                        if ($p.Name -match 'Credential|Password|Secret') { continue }
                        if ($null -ne $p.Value -and [string]$p.Value -ne '') { $props[$p.Name] = [string]$p.Value }
                    }
                    $state = [pscustomobject]$props
                }
                [pscustomobject]@{
                    ResourceId   = [string]$r.ResourceId
                    Configuration = [string]$r.ConfigurationName
                    CurrentState = $state
                    Desired      = 'As declared in the node configuration (MOF)'
                }
            }
        }
    }

    $errors = $na
    try {
        $since = (Get-Date).AddHours(-$EventHours)
        $errors = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-DSC/Operational'; Level = 2; StartTime = $since } -ErrorAction Stop).Count
    } catch {
        if ($_.Exception.Message -match 'No events were found') { $errors = 0 } else { Write-Verbose "Event log read failed: $($_.Exception.Message)" }
    }

    if ($Sensor) {
        Write-Output $inState
        if ($inState -eq $true -or $inState -eq $na) { exit 0 } else { exit 1 }
    }

    $failedRuns = @($history | Where-Object { $_.Status -eq 'Failure' }).Count
    $report = [ordered]@{
        Id                 = 'DSC-10'
        Computer           = $env:COMPUTERNAME
        InDesiredState     = $inState
        Note               = $reason
        LcmState           = $lcmState
        ConfigurationMode  = [string]$meta.ConfigurationMode
        RefreshMode        = [string]$meta.RefreshMode
        LastRun            = $(if ($last) { $last.StartDate.ToString('s') } else { $na })
        LastRunType        = $(if ($last) { [string]$last.Type } else { $na })
        LastRunStatus      = $(if ($last) { [string]$last.Status } else { $na })
        RunsInHistory      = $history.Count
        FailedRunsInHistory = $failedRuns
        DscErrorEvents     = $errors
        EventWindowHours   = $EventHours
        Drifted            = @($drift)
    }
    Write-Output ([pscustomobject]$report | ConvertTo-Json -Depth 5)
    if ($inState -eq $false) { exit 1 }
    exit 0
} catch {
    if ($Sensor) { Write-Output $na }
    Write-Error "DSC-10 failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
