<#
.SYNOPSIS
    Reports storage pools, virtual disks, physical disks, storage jobs and Storage Spaces Direct / Replica state.
.DESCRIPTION
    Collects pool and virtual disk health and resiliency, physical disk health, usage, firmware, wear,
    temperature and error counters, running repair and rebuild jobs, and Storage Spaces Direct and Storage
    Replica status where present. Not every controller exposes reliability counters; missing values are
    reported as "Data unavailable". Storage Spaces Direct commands need the cluster components.
.NOTES
    RemoteHub: id=SRV-15; context=System; os=Server; risk=R0
#>
[CmdletBinding()]
param(
    [int]$MaxTemperatureCelsius = 60,
    [int]$MaxWearPercent = 80,
    # Sensor mode writes a single integer: the number of unhealthy physical disks.
    [switch]$Sensor
)

$ErrorActionPreference = 'Stop'

function Get-Value($Object, [string]$Name) {
    if ($null -eq $Object) { return 'Data unavailable' }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p -or $null -eq $p.Value) { return 'Data unavailable' }
    return $p.Value
}

try {
    $pools = @(Get-StoragePool -IsPrimordial $false -ErrorAction SilentlyContinue | ForEach-Object {
        [ordered]@{ Name = $_.FriendlyName; Health = [string]$_.HealthStatus; Status = [string]$_.OperationalStatus
            SizeGB = [Math]::Round($_.Size / 1GB, 1); AllocatedGB = [Math]::Round($_.AllocatedSize / 1GB, 1); ReadOnly = $_.IsReadOnly }
    })
    $vdisks = @(Get-VirtualDisk -ErrorAction SilentlyContinue | ForEach-Object {
        [ordered]@{ Name = $_.FriendlyName; Health = [string]$_.HealthStatus; Status = [string]$_.OperationalStatus
            Resiliency = $_.ResiliencySettingName; Copies = $_.NumberOfDataCopies; SizeGB = [Math]::Round($_.Size / 1GB, 1) }
    })

    $unhealthy = 0
    $disks = @(Get-PhysicalDisk | ForEach-Object {
        $rc = $null
        try { $rc = $_ | Get-StorageReliabilityCounter -ErrorAction Stop } catch { Write-Verbose "No reliability counters for $($_.FriendlyName)." }
        $issues = @()
        if ([string]$_.HealthStatus -ne 'Healthy') { $issues += "health $($_.HealthStatus)" }
        $temp = Get-Value $rc 'Temperature'
        $wear = Get-Value $rc 'Wear'
        if ($temp -is [ValueType] -and $temp -gt $MaxTemperatureCelsius) { $issues += "temperature $temp C" }
        if ($wear -is [ValueType] -and $wear -gt $MaxWearPercent) { $issues += "wear $wear %" }
        $unc = Get-Value $rc 'ReadErrorsUncorrected'
        if ($unc -is [ValueType] -and $unc -gt 0) { $issues += "uncorrected read errors $unc" }
        if ($issues.Count -gt 0) { $unhealthy++ }
        [ordered]@{ Name = $_.FriendlyName; Serial = $_.SerialNumber; Media = [string]$_.MediaType; Bus = [string]$_.BusType
            Health = [string]$_.HealthStatus; Status = (@($_.OperationalStatus) -join ','); Usage = [string]$_.Usage
            Firmware = $_.FirmwareVersion; SizeGB = [Math]::Round($_.Size / 1GB, 1); TemperatureC = $temp; WearPercent = $wear
            ReadErrorsUncorrected = $unc; WriteErrorsUncorrected = (Get-Value $rc 'WriteErrorsUncorrected'); PowerOnHours = (Get-Value $rc 'PowerOnHours')
            Issues = ($issues -join '; ') }
    })

    $jobs = @(Get-StorageJob -ErrorAction SilentlyContinue | Where-Object { [string]$_.JobState -ne 'Completed' } | ForEach-Object {
        [ordered]@{ Name = $_.Name; State = [string]$_.JobState; PercentComplete = $_.PercentComplete; ElapsedTime = [string]$_.ElapsedTime }
    })

    $s2d = 'Not present'
    if (Get-Command -Name Get-ClusterStorageSpacesDirect -ErrorAction SilentlyContinue) {
        try {
            $c = Get-ClusterStorageSpacesDirect -ErrorAction Stop
            $s2d = "State=$($c.State) CacheState=$($c.CacheState)"
            try {
                $hr = Get-StorageSubSystem -FriendlyName 'Clustered*' | Get-StorageHealthReport -Count 1
                $s2d += '; health report available'
                Write-Verbose ($hr | Out-String)
            } catch { Write-Verbose 'Storage health report unavailable.' }
        } catch { $s2d = 'Not enabled' }
    }

    $sr = 'Not present'
    if (Get-Command -Name Get-SRGroup -ErrorAction SilentlyContinue) {
        try {
            $groups = @(Get-SRGroup -ErrorAction Stop)
            $partners = @(Get-SRPartnership -ErrorAction SilentlyContinue)
            $sr = @($groups | ForEach-Object {
                $g = $_
                "$($g.Name): replicas=" + (@($g.Replicas | ForEach-Object { "$($_.DataVolume)=$($_.ReplicationStatus)" }) -join ',')
            }) + @($partners | ForEach-Object { "Partnership $($_.SourceRGName) -> $($_.DestinationComputerName)\$($_.DestinationRGName)" })
            if (@($sr).Count -eq 0) { $sr = 'No replication groups' }
        } catch { $sr = 'Data unavailable' }
    }

    $badVdisks = @($vdisks | Where-Object { $_.Health -ne 'Healthy' }).Count
    $badPools = @($pools | Where-Object { $_.Health -ne 'Healthy' }).Count

    if ($Sensor) { Write-Output $unhealthy } else {
        [ordered]@{
            Computer = $env:COMPUTERNAME; UnhealthyPhysicalDisks = $unhealthy; UnhealthyVirtualDisks = $badVdisks; UnhealthyPools = $badPools
            StoragePools = $pools; VirtualDisks = $vdisks; PhysicalDisks = $disks; RunningStorageJobs = $jobs
            StorageSpacesDirect = $s2d; StorageReplica = $sr
        } | ConvertTo-Json -Depth 5
    }
    if (($unhealthy + $badVdisks + $badPools) -gt 0) { exit 1 }
    exit 0
} catch {
    Write-Error "Storage health check failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
