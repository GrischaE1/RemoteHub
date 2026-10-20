<#
.SYNOPSIS
    Reports fixed volume capacity and physical disk health, and flags volumes low on free space.
.DESCRIPTION
    Lists each fixed volume's size, free space, percent free and file system, plus health,
    operational status and media type of the physical disks underneath. A volume is flagged when
    it is under -MinPercentFree or -MinFreeGB. Removable, network and optical drives are excluded
    by drive type; mount points without a letter are included by path. Deduplicated volumes show
    logical sizes and cluster shared volumes are not covered here.
.NOTES
    RemoteHub: id=INV-07; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [int]$MinPercentFree = 10,
    [int]$MinFreeGB = 10
)

$ErrorActionPreference = 'Stop'

try {
    $physical = @{}
    try {
        foreach ($pd in Get-PhysicalDisk) {
            $physical[[string]$pd.DeviceId] = $pd
        }
    } catch {
        Write-Verbose "Get-PhysicalDisk failed: $($_.Exception.Message)"
    }

    $volumes = @(Get-Volume | Where-Object { $_.DriveType -eq 'Fixed' -and $_.Size -gt 0 -and ($_.DriveLetter -or $_.Path) })
    $report = foreach ($v in $volumes) {
        $diskHealth = 'Data unavailable'
        $diskStatus = 'Data unavailable'
        $media = 'Data unavailable'
        try {
            $part = Get-Partition -Volume $v -ErrorAction Stop | Select-Object -First 1
            $pd = $physical[[string]$part.DiskNumber]
            if ($pd) {
                $diskHealth = [string]$pd.HealthStatus
                $diskStatus = [string]$pd.OperationalStatus
                $media = [string]$pd.MediaType
            } else {
                $d = Get-Disk -Number $part.DiskNumber
                $diskHealth = [string]$d.HealthStatus
                $diskStatus = [string]$d.OperationalStatus
            }
        } catch {
            Write-Verbose "No partition mapping for volume $($v.UniqueId): $($_.Exception.Message)"
        }

        $pct = [math]::Round(($v.SizeRemaining / $v.Size) * 100, 1)
        $freeGB = [math]::Round($v.SizeRemaining / 1GB, 1)
        $low = ($pct -lt $MinPercentFree) -or ($freeGB -lt $MinFreeGB)
        $name = if ($v.DriveLetter) { "$($v.DriveLetter):" } else { $v.FileSystemLabel }

        [PSCustomObject]@{
            Volume      = $name
            Label       = $v.FileSystemLabel
            FileSystem  = $v.FileSystem
            SizeGB      = [math]::Round($v.Size / 1GB, 1)
            FreeGB      = $freeGB
            PercentFree = $pct
            LowSpace    = $low
            VolumeHealth = [string]$v.HealthStatus
            DiskHealth  = $diskHealth
            DiskStatus  = $diskStatus
            MediaType   = $media
        }
    }
    $report = @($report)

    $systemLetter = $env:SystemDrive.TrimEnd(':')
    $systemVol = $report | Where-Object { $_.Volume -eq "$($systemLetter):" } | Select-Object -First 1
    $unhealthy = @($physical.Values | Where-Object { [string]$_.HealthStatus -ne 'Healthy' })

    [PSCustomObject]@{
        SystemDrivePercentFree = if ($systemVol) { $systemVol.PercentFree } else { 'Data unavailable' }
        LowSpaceVolumes        = @($report | Where-Object { $_.LowSpace } | ForEach-Object { $_.Volume })
        DisksHealthy           = ($unhealthy.Count -eq 0)
        Volumes                = $report
    } | ConvertTo-Json -Depth 5

    if (@($report | Where-Object { $_.LowSpace }).Count -gt 0 -or $unhealthy.Count -gt 0) { exit 1 }
    exit 0
} catch {
    Write-Error "Disk capacity report failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
