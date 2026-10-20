<#
.SYNOPSIS
    Summarises critical and error events and specific failure signals over a time window.
.DESCRIPTION
    Counts critical and error events in System and Application grouped by provider and ID, and
    checks failure signals: unexpected shutdown, bug check, disk and NTFS errors, WHEA hardware
    errors, service failures, application crashes, Netlogon 5719 and log clearing (Security 1102,
    System 104). Also reports log size and retention. Only IDs and counts are reported because
    message text can contain user names, paths and host names. Reading the Security log needs
    SYSTEM or an administrator. -Sensor returns the count of critical events in the last 24 hours.
.NOTES
    RemoteHub: id=INV-20; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [int]$Hours = 24,
    [int]$TopSources = 10,
    [switch]$Sensor
)

$ErrorActionPreference = 'Stop'

$signals = @(
    @{ Name = 'Unexpected shutdown (Kernel-Power 41)'; Log = 'System';      Provider = 'Microsoft-Windows-Kernel-Power'; Id = @(41) },
    @{ Name = 'Unexpected shutdown (EventLog 6008)';   Log = 'System';      Provider = 'EventLog'; Id = @(6008) },
    @{ Name = 'Bug check (1001)';                       Log = 'System';      Provider = 'Microsoft-Windows-WER-SystemErrorReporting'; Id = @(1001) },
    @{ Name = 'Disk errors (7, 11, 51, 153)';           Log = 'System';      Provider = 'disk'; Id = @(7, 11, 51, 153) },
    @{ Name = 'NTFS corruption (55)';                   Log = 'System';      Provider = 'Ntfs'; Id = @(55) },
    @{ Name = 'Hardware errors (WHEA-Logger)';          Log = 'System';      Provider = 'Microsoft-Windows-WHEA-Logger'; Id = @() },
    @{ Name = 'Service failures (7000, 7001, 7031, 7034)'; Log = 'System';   Provider = 'Service Control Manager'; Id = @(7000, 7001, 7031, 7034) },
    @{ Name = 'Application crashes (1000)';             Log = 'Application'; Provider = 'Application Error'; Id = @(1000) },
    @{ Name = 'Netlogon no DC (5719)';                  Log = 'System';      Provider = 'NETLOGON'; Id = @(5719) },
    @{ Name = 'Security log cleared (1102)';            Log = 'Security';    Provider = 'Microsoft-Windows-Eventlog'; Id = @(1102) },
    @{ Name = 'System log cleared (104)';               Log = 'System';      Provider = 'Microsoft-Windows-Eventlog'; Id = @(104) }
)

function Get-EventsSafe([hashtable]$Filter) {
    try { return @(Get-WinEvent -FilterHashtable $Filter -ErrorAction Stop) } catch {
        # "No events were found" is a normal empty result, not a failure.
        if ($_.FullyQualifiedErrorId -match 'NoMatchingEventsFound') { return @() }
        throw
    }
}

try {
    $start = (Get-Date).AddHours(-$Hours)

    if ($Sensor) {
        $crit = 0
        foreach ($log in @('System', 'Application')) {
            $crit += (Get-EventsSafe @{ LogName = $log; Level = 1; StartTime = (Get-Date).AddHours(-24) }).Count
        }
        $crit
        exit 0
    }

    # Level 1 = Critical, 2 = Error; the filter runs inside the event log service, not here.
    $errors = @()
    foreach ($log in @('System', 'Application')) {
        $errors += Get-EventsSafe @{ LogName = $log; Level = @(1, 2); StartTime = $start }
    }

    $signalCounts = foreach ($s in $signals) {
        $f = @{ LogName = $s.Log; ProviderName = $s.Provider; StartTime = $start }
        if ($s.Id.Count -gt 0) { $f.Id = $s.Id }
        $count = 'Data unavailable'
        try { $count = (Get-EventsSafe $f).Count } catch { Write-Verbose "$($s.Name): $($_.Exception.Message)" }
        [PSCustomObject]@{ Signal = $s.Name; Count = $count }
    }

    $top = @($errors | Group-Object -Property LogName, ProviderName, Id | Sort-Object Count -Descending |
        Select-Object -First $TopSources | ForEach-Object {
            $e = $_.Group[0]
            [PSCustomObject]@{ Log = $e.LogName; Provider = $e.ProviderName; EventId = $e.Id; Count = $_.Count }
        })

    $logs = foreach ($name in @('System', 'Application', 'Security')) {
        try {
            $l = Get-WinEvent -ListLog $name -ErrorAction Stop
            [PSCustomObject]@{
                Log          = $l.LogName
                MaxSizeMB    = [math]::Round($l.MaximumSizeInBytes / 1MB, 1)
                UsedMB       = [math]::Round($l.FileSize / 1MB, 1)
                Records      = $l.RecordCount
                LogMode      = [string]$l.LogMode
                Enabled      = $l.IsEnabled
            }
        } catch {
            [PSCustomObject]@{ Log = $name; MaxSizeMB = 'Data unavailable'; UsedMB = ''; Records = ''; LogMode = ''; Enabled = '' }
        }
    }

    [PSCustomObject]@{
        WindowHours   = $Hours
        CriticalCount = @($errors | Where-Object { $_.Level -eq 1 }).Count
        ErrorCount    = @($errors | Where-Object { $_.Level -eq 2 }).Count
        Signals       = @($signalCounts)
        TopSources    = $top
        Logs          = @($logs)
    } | ConvertTo-Json -Depth 5
    exit 0
} catch {
    Write-Error "Event log summary failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
