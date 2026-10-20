<#
.SYNOPSIS
    Takes a short performance snapshot (CPU, memory, disk, network, top processes) and reports it as JSON.
.DESCRIPTION
    Samples CPU, processor queue, available and committed memory, paging, disk latency and queue length, network
    throughput and TCP retransmits over a short window, lists the top N processes by CPU, memory, handles and
    threads, reports the last boot duration and startup items, and flags CPU throttling. Uses the CIM performance
    classes because counter names are localised. Process command lines and startup commands are never captured,
    as they can contain secrets. Keep the window short so the script does not cause load. Read-only.
.NOTES
    RemoteHub: id=FIX-14; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [ValidateRange(2, 30)]
    [int]$SampleSeconds = 5,
    [int]$Top = 5,
    [ValidateSet('None', 'CpuPercent', 'AvailableMB')]
    [string]$Sensor = 'None'
)

$ErrorActionPreference = 'Stop'

function Get-Perf([string]$Class, [string]$Filter) {
    if ($Filter) { Get-CimInstance -ClassName $Class -Filter $Filter } else { Get-CimInstance -ClassName $Class }
}

try {
    $cpuCount = (Get-CimInstance -ClassName Win32_ComputerSystem).NumberOfLogicalProcessors
    $disk0 = Get-Perf 'Win32_PerfRawData_PerfDisk_PhysicalDisk' "Name='_Total'"
    $tcp0 = Get-Perf 'Win32_PerfRawData_Tcpip_TCPv4' ''
    $cpu = @(); $queue = @(); $net = @()
    for ($i = 0; $i -lt $SampleSeconds; $i++) {
        Start-Sleep -Seconds 1
        $cpu += [double](Get-Perf 'Win32_PerfFormattedData_PerfOS_Processor' "Name='_Total'").PercentProcessorTime
        $queue += [double](Get-Perf 'Win32_PerfFormattedData_PerfOS_System' '').ProcessorQueueLength
        $sum = 0; foreach ($n in @(Get-Perf 'Win32_PerfFormattedData_Tcpip_NetworkInterface' '')) { $sum += [double]$n.BytesTotalPersec }
        $net += $sum
    }
    $disk1 = Get-Perf 'Win32_PerfRawData_PerfDisk_PhysicalDisk' "Name='_Total'"
    $tcp1 = Get-Perf 'Win32_PerfRawData_Tcpip_TCPv4' ''
    $mem = Get-Perf 'Win32_PerfFormattedData_PerfOS_Memory' ''

    # Average-timer counters: (delta time ticks / frequency) / delta operations gives seconds per operation.
    $freq = [double]$disk1.Frequency_PerfTime
    $readOps = [double]$disk1.AvgDisksecPerRead_Base - [double]$disk0.AvgDisksecPerRead_Base
    $writeOps = [double]$disk1.AvgDisksecPerWrite_Base - [double]$disk0.AvgDisksecPerWrite_Base
    $readMs = 0; if ($readOps -gt 0) { $readMs = [math]::Round((([double]$disk1.AvgDisksecPerRead - [double]$disk0.AvgDisksecPerRead) / $freq) / $readOps * 1000, 2) }
    $writeMs = 0; if ($writeOps -gt 0) { $writeMs = [math]::Round((([double]$disk1.AvgDisksecPerWrite - [double]$disk0.AvgDisksecPerWrite) / $freq) / $writeOps * 1000, 2) }
    $segSent = [double]$tcp1.SegmentsSentPersec - [double]$tcp0.SegmentsSentPersec
    $segRetx = [double]$tcp1.SegmentsRetransmittedPersec - [double]$tcp0.SegmentsRetransmittedPersec
    $retxPct = 0; if ($segSent -gt 0) { $retxPct = [math]::Round($segRetx / $segSent * 100, 2) }

    $cpuAvg = [math]::Round(($cpu | Measure-Object -Average).Average, 1)
    if ($Sensor -eq 'CpuPercent') { Write-Output $cpuAvg; exit 0 }
    if ($Sensor -eq 'AvailableMB') { Write-Output ([int]$mem.AvailableMBytes); exit 0 }

    $throttle = 'Data unavailable'
    $pi = Get-CimInstance -ClassName Win32_PerfFormattedData_Counters_ProcessorInformation -Filter "Name='_Total'" -ErrorAction SilentlyContinue
    if ($pi) {
        $throttle = [pscustomobject]@{
            PercentOfMaximumFrequency = $pi.PercentofMaximumFrequency
            PercentPerformanceLimit = $pi.PercentPerformanceLimit
            Throttled = ([int]$pi.PercentPerformanceLimit -lt 90 -and [int]$pi.PercentPerformanceLimit -gt 0)
        }
    }

    $procs = @(Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process | Where-Object { $_.Name -ne '_Total' -and $_.Name -ne 'Idle' } | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Name; Id = $_.IDProcess
            CpuPercent = [math]::Round([double]$_.PercentProcessorTime / [math]::Max(1, $cpuCount), 1)
            PrivateMB = [math]::Round([double]$_.WorkingSetPrivate / 1MB, 1)
            Handles = $_.HandleCount; Threads = $_.ThreadCount
        }
    })
    $boot = Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Diagnostics-Performance/Operational'; Id = 100 } -MaxEvents 1 -ErrorAction SilentlyContinue
    $bootMs = 'Data unavailable'
    if ($boot) { foreach ($d in ([xml]$boot.ToXml()).Event.EventData.Data) { if ($d.Name -eq 'BootTime') { $bootMs = [int]$d.'#text' } } }
    $startup = @(Get-CimInstance -ClassName Win32_StartupCommand -ErrorAction SilentlyContinue | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Location = $_.Location } })

    [pscustomobject]@{
        Computer = $env:COMPUTERNAME
        Collected = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        WindowSeconds = $SampleSeconds
        Cpu = [pscustomobject]@{ AveragePercent = $cpuAvg; MaxPercent = ($cpu | Measure-Object -Maximum).Maximum; AverageQueueLength = [math]::Round(($queue | Measure-Object -Average).Average, 1); LogicalProcessors = $cpuCount; Throttling = $throttle }
        Memory = [pscustomobject]@{ AvailableMB = [int]$mem.AvailableMBytes; CommittedMB = [math]::Round([double]$mem.CommittedBytes / 1MB); CommitLimitMB = [math]::Round([double]$mem.CommitLimit / 1MB); CommittedPercent = $mem.PercentCommittedBytesInUse; PagesPerSec = $mem.PagesPersec }
        Disk = [pscustomobject]@{ ReadLatencyMs = $readMs; WriteLatencyMs = $writeMs; QueueLength = $disk1.CurrentDiskQueueLength }
        Network = [pscustomobject]@{ AverageBytesPerSec = [math]::Round(($net | Measure-Object -Average).Average); TcpRetransmitPercent = $retxPct }
        TopByCpu = @($procs | Sort-Object CpuPercent -Descending | Select-Object -First $Top)
        TopByMemory = @($procs | Sort-Object PrivateMB -Descending | Select-Object -First $Top)
        TopByHandles = @($procs | Sort-Object Handles -Descending | Select-Object -First $Top)
        TopByThreads = @($procs | Sort-Object Threads -Descending | Select-Object -First $Top)
        LastBootDurationMs = $bootMs
        StartupItems = $startup
    } | ConvertTo-Json -Depth 5
    exit 0
}
catch {
    Write-Output "FIX-14 failed: $($_.Exception.Message)"
    exit 1
}
