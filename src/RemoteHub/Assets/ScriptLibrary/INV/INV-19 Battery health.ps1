<#
.SYNOPSIS
    Computes battery wear from designed versus full-charge capacity, with cycle count and state.
.DESCRIPTION
    Reads root\wmi BatteryStaticData, BatteryFullChargedCapacity and BatteryCycleCount and
    Win32_Battery for the charging state, per battery. Wear percent is
    100 - (full-charge capacity / designed capacity * 100). Returns "Not applicable" on devices
    without a battery and "Data unavailable" where firmware does not expose capacity or cycles.
    -Sensor returns the highest wear percent across batteries as an integer.
.NOTES
    RemoteHub: id=INV-19; context=System; os=Client; risk=R0
#>
[CmdletBinding()]
param(
    [switch]$Sensor,
    [int]$MaxWearPercent = 40
)

$ErrorActionPreference = 'Stop'

function Get-WmiSafe([string]$ClassName) {
    try { return @(Get-CimInstance -Namespace 'root\wmi' -ClassName $ClassName -ErrorAction Stop) } catch {
        Write-Verbose "$ClassName unavailable: $($_.Exception.Message)"
        return @()
    }
}

try {
    $w32 = @(Get-CimInstance -ClassName Win32_Battery)
    if ($w32.Count -eq 0) {
        'Not applicable'
        exit 0
    }

    $static = Get-WmiSafe 'BatteryStaticData'
    $full   = Get-WmiSafe 'BatteryFullChargedCapacity'
    $cycles = Get-WmiSafe 'BatteryCycleCount'

    $chargeNames = @{ 1 = 'Discharging'; 2 = 'On AC'; 3 = 'Fully charged'; 4 = 'Low'; 5 = 'Critical'; 6 = 'Charging'
                      7 = 'Charging (high)'; 8 = 'Charging (low)'; 9 = 'Charging (critical)'; 10 = 'Undefined'; 11 = 'Partially charged' }

    # root\wmi instances of one battery share an InstanceName, which pairs the three classes.
    $names = @($static | ForEach-Object { $_.InstanceName })
    if ($names.Count -eq 0) { $names = @($full | ForEach-Object { $_.InstanceName }) }

    $batteries = New-Object System.Collections.Generic.List[object]
    $i = 0
    foreach ($n in $names) {
        $s = $static | Where-Object { $_.InstanceName -eq $n } | Select-Object -First 1
        $f = $full   | Where-Object { $_.InstanceName -eq $n } | Select-Object -First 1
        $c = $cycles | Where-Object { $_.InstanceName -eq $n } | Select-Object -First 1
        $design = 0
        if ($s) { $design = [long]$s.DesignedCapacity }
        $fullCap = 0
        if ($f) { $fullCap = [long]$f.FullChargedCapacity }

        $wear = 'Data unavailable'
        if ($design -gt 0 -and $fullCap -gt 0) {
            $wear = [int][math]::Round(100 - ($fullCap / $design * 100))
            if ($wear -lt 0) { $wear = 0 }
        }
        $cycleCount = 'Data unavailable'
        if ($c -and [long]$c.CycleCount -gt 0) { $cycleCount = [long]$c.CycleCount }

        $state = 'Data unavailable'
        if ($i -lt $w32.Count) {
            $st = $chargeNames[[int]$w32[$i].BatteryStatus]
            if ($st) { $state = $st }
        }

        $batteries.Add([PSCustomObject]@{
            Battery             = $i + 1
            DesignedCapacityMWh = if ($design -gt 0) { $design } else { 'Data unavailable' }
            FullChargeCapacityMWh = if ($fullCap -gt 0) { $fullCap } else { 'Data unavailable' }
            WearPercent         = $wear
            CycleCount          = $cycleCount
            ChargingState       = $state
            ChargePercent       = if ($i -lt $w32.Count) { $w32[$i].EstimatedChargeRemaining } else { 'Data unavailable' }
        })
        $i++
    }

    $known = @($batteries | Where-Object { $_.WearPercent -is [int] })
    $worst = $null
    if ($known.Count -gt 0) { $worst = ($known | Measure-Object -Property WearPercent -Maximum).Maximum }

    if ($Sensor) {
        if ($null -eq $worst) { 'Data unavailable'; exit 1 }
        [int]$worst
        exit 0
    }

    if ($batteries.Count -eq 0) {
        'Data unavailable: a battery is present but the firmware does not expose capacity data.'
        exit 1
    }
    $batteries | Format-Table -AutoSize | Out-String -Width 4096
    if ($null -ne $worst -and $worst -gt $MaxWearPercent) { exit 1 }
    exit 0
} catch {
    Write-Error "Battery health check failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
