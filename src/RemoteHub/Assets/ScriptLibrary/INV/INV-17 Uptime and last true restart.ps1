<#
.SYNOPSIS
    Reports days since the last boot and days since the last true (full) restart.
.DESCRIPTION
    With Fast Startup (hybrid boot) a normal shutdown does not reset the kernel session, so the
    uptime from LastBootUpTime overstates how long since a real restart. The script reads
    HiberbootEnabled and the Kernel-Boot event 27 (boot type: 0 cold boot, 1 hybrid boot,
    2 resume from hibernation) to find the last full restart; verify the event on your builds.
    Paused or resumed VMs and clock corrections can distort the figures. -Sensor returns only the
    days since the last true restart.
.NOTES
    RemoteHub: id=INV-17; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [switch]$Sensor,
    [int]$MaxEvents = 200
)

$ErrorActionPreference = 'Stop'

try {
    $now = Get-Date
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    $lastBoot = $os.LastBootUpTime
    $daysSinceBoot = [math]::Round(($now - $lastBoot).TotalDays, 1)

    $hiberboot = 'Data unavailable'
    try {
        $v = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -Name 'HiberbootEnabled' -ErrorAction Stop).HiberbootEnabled
        $hiberboot = ($v -eq 1)
    } catch { Write-Verbose 'HiberbootEnabled not set.' }

    $lastTrue = $null
    $lastBootType = 'Data unavailable'
    try {
        $events = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Boot'; Id = 27 } -MaxEvents $MaxEvents -ErrorAction Stop
        $first = $true
        foreach ($e in $events) {
            $type = $null
            try {
                $xml = [xml]$e.ToXml()
                $node = $xml.Event.EventData.Data | Where-Object { $_.Name -eq 'BootType' } | Select-Object -First 1
                if ($node) { $type = [int]$node.'#text' }
            } catch { $type = $null }
            if ($first) {
                $first = $false
                if ($type -eq 0) { $lastBootType = 'Cold boot' } elseif ($type -eq 1) { $lastBootType = 'Hybrid boot (Fast Startup)' } elseif ($type -eq 2) { $lastBootType = 'Resume from hibernation' }
            }
            if ($type -eq 0) { $lastTrue = $e.TimeCreated; break }
        }
    } catch {
        Write-Verbose "Kernel-Boot events unavailable: $($_.Exception.Message)"
    }

    # Without Fast Startup every boot is a full restart, so LastBootUpTime is already the answer.
    if (-not $lastTrue -and $hiberboot -eq $false) { $lastTrue = $lastBoot }

    $daysSinceTrue = 'Data unavailable'
    if ($lastTrue) { $daysSinceTrue = [math]::Round(($now - $lastTrue).TotalDays, 1) }

    Write-Verbose "Last boot $lastBoot; last true restart $lastTrue; Fast Startup $hiberboot."
    if ($Sensor) {
        if ($lastTrue) { [int][math]::Floor(($now - $lastTrue).TotalDays) } else { 'Data unavailable' }
        exit 0
    }

    [PSCustomObject]@{
        LastBoot               = $lastBoot.ToString('yyyy-MM-dd HH:mm')
        DaysSinceBoot          = $daysSinceBoot
        LastBootType           = $lastBootType
        LastTrueRestart        = if ($lastTrue) { ([datetime]$lastTrue).ToString('yyyy-MM-dd HH:mm') } else { 'Data unavailable' }
        DaysSinceTrueRestart   = $daysSinceTrue
        FastStartupEnabled     = $hiberboot
    }
    exit 0
} catch {
    Write-Error "Uptime check failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
