<#
.SYNOPSIS
    Builds a JSON timeline of crashes, unexpected shutdowns, hardware and disk errors, and boot performance.
.DESCRIPTION
    Collects unexpected shutdowns and bug checks (with codes), WHEA hardware errors, disk errors, recent driver and
    update installs, boot duration events and reliability records, and lists dump files with size and hash plus
    the dump configuration (type, automatic restart, overwrite) and pagefile. Dump files contain memory and possibly
    secrets: they are only listed, never copied or uploaded. A pagefile that is too small can prevent dumps. Bug
    check names come from a small maintained table; dump analysis is a separate manual step. Read-only.
.NOTES
    RemoteHub: id=FIX-13; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [int]$Days = 30,
    [int]$MaxEventsPerQuery = 50,
    [bool]$HashDumps = $true,
    [int]$MaxHashSizeMB = 4096
)

$ErrorActionPreference = 'Stop'
$bugChecks = @{
    '0x0000000a' = 'IRQL_NOT_LESS_OR_EQUAL'; '0x0000001e' = 'KMODE_EXCEPTION_NOT_HANDLED'; '0x0000003b' = 'SYSTEM_SERVICE_EXCEPTION'
    '0x00000050' = 'PAGE_FAULT_IN_NONPAGED_AREA'; '0x0000007e' = 'SYSTEM_THREAD_EXCEPTION_NOT_HANDLED'; '0x0000009f' = 'DRIVER_POWER_STATE_FAILURE'
    '0x000000d1' = 'DRIVER_IRQL_NOT_LESS_OR_EQUAL'; '0x000000ef' = 'CRITICAL_PROCESS_DIED'; '0x00000124' = 'WHEA_UNCORRECTABLE_ERROR'
    '0x00000133' = 'DPC_WATCHDOG_VIOLATION'; '0x00000139' = 'KERNEL_SECURITY_CHECK_FAILURE'; '0x0000001a' = 'MEMORY_MANAGEMENT'
    '0x000000c2' = 'BAD_POOL_CALLER'; '0x00000116' = 'VIDEO_TDR_FAILURE'; '0x0000007b' = 'INACCESSIBLE_BOOT_DEVICE'
}

function Get-EventData($Record) {
    # Named EventData fields are locale-neutral, unlike the rendered message.
    $data = @{}
    foreach ($d in ([xml]$Record.ToXml()).Event.EventData.Data) { if ($d.Name) { $data[$d.Name] = $d.'#text' } }
    $data
}

function Get-Events([hashtable]$Filter) {
    $Filter['StartTime'] = (Get-Date).AddDays(-$Days)
    @(Get-WinEvent -FilterHashtable $Filter -MaxEvents $MaxEventsPerQuery -ErrorAction SilentlyContinue)
}

try {
    $timeline = New-Object System.Collections.Generic.List[object]
    function Add-Entry($E, [string]$Category, [string]$Detail) {
        $timeline.Add([pscustomobject]@{ Time = $E.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss'); Category = $Category; Source = $E.ProviderName; Id = $E.Id; Detail = $Detail })
    }

    foreach ($e in Get-Events @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Power'; Id = 41 }) {
        $d = Get-EventData $e
        $code = '0x{0:x8}' -f [int64]$d['BugcheckCode']
        Add-Entry $e 'Unexpected shutdown' ("Kernel-Power 41, bug check {0}" -f $code)
    }
    foreach ($e in Get-Events @{ LogName = 'System'; ProviderName = 'EventLog'; Id = 6008 }) { Add-Entry $e 'Unexpected shutdown' 'Previous shutdown was unexpected' }
    foreach ($e in Get-Events @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WER-SystemErrorReporting'; Id = 1001 }) {
        $d = Get-EventData $e
        $raw = "$($d['param1'])"
        $code = ''; if ($raw -match '^(0x[0-9a-fA-F]{8})') { $code = $Matches[1].ToLower() }
        $name = $bugChecks[$code]; if (-not $name) { $name = 'not in table' }
        Add-Entry $e 'Bug check' ("{0} ({1})" -f $code, $name)
    }
    foreach ($e in Get-Events @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WHEA-Logger' }) { Add-Entry $e 'Hardware error' ("WHEA event {0}, level {1}" -f $e.Id, $e.LevelDisplayName) }
    foreach ($e in Get-Events @{ LogName = 'System'; ProviderName = 'disk', 'Ntfs', 'volmgr', 'stornvme', 'storahci'; Level = 1, 2, 3 }) { Add-Entry $e 'Disk' ("{0} event {1}" -f $e.ProviderName, $e.Id) }
    foreach ($e in Get-Events @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WindowsUpdateClient'; Id = 19 }) {
        $d = Get-EventData $e; Add-Entry $e 'Update installed' "$($d['updateTitle'])"
    }
    foreach ($e in Get-Events @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-UserPnp'; Id = 20001 }) { Add-Entry $e 'Driver installed' "$((Get-EventData $e)['DriverName'])" }
    foreach ($e in Get-Events @{ LogName = 'Microsoft-Windows-Diagnostics-Performance/Operational'; Id = 100 }) {
        $d = Get-EventData $e; Add-Entry $e 'Boot duration' ("{0} ms (main path {1} ms)" -f $d['BootTime'], $d['MainPathBootTime'])
    }

    $since = (Get-Date).AddDays(-$Days)
    $reliability = @(Get-CimInstance -ClassName Win32_ReliabilityRecords -ErrorAction SilentlyContinue | Where-Object { $_.TimeGenerated -ge $since } |
        Sort-Object TimeGenerated -Descending | Select-Object -First $MaxEventsPerQuery | ForEach-Object {
            [pscustomobject]@{ Time = $_.TimeGenerated.ToString('yyyy-MM-dd HH:mm:ss'); Source = $_.SourceName; EventId = $_.EventIdentifier; Product = $_.ProductName }
        })

    $cc = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl'
    $types = @{ 0 = 'None'; 1 = 'Complete'; 2 = 'Kernel'; 3 = 'Small'; 7 = 'Automatic' }
    $dumpType = $types[[int]$cc.CrashDumpEnabled]; if (-not $dumpType) { $dumpType = "Other ($($cc.CrashDumpEnabled))" }
    if ($cc.PSObject.Properties['FilterPages'] -and $cc.FilterPages -eq 1 -and $cc.CrashDumpEnabled -eq 1) { $dumpType = 'Active' }
    $pagefile = @(Get-CimInstance -ClassName Win32_PageFileUsage | ForEach-Object { [pscustomobject]@{ Path = $_.Name; AllocatedMB = $_.AllocatedBaseSize; PeakUsageMB = $_.PeakUsage } })

    $dumpPaths = @([Environment]::ExpandEnvironmentVariables("$($cc.DumpFile)"))
    $miniDir = [Environment]::ExpandEnvironmentVariables("$($cc.MinidumpDir)")
    if ($miniDir -and (Test-Path -LiteralPath $miniDir)) { $dumpPaths += @(Get-ChildItem -LiteralPath $miniDir -Filter *.dmp -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }) }
    $dumpPaths += @(Get-ChildItem -LiteralPath (Join-Path $env:windir 'LiveKernelReports') -Filter *.dmp -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    $dumps = @(foreach ($p in ($dumpPaths | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) })) {
        $f = Get-Item -LiteralPath $p -Force
        $hash = 'Not hashed (over size limit)'
        if ($HashDumps -and $f.Length -le ($MaxHashSizeMB * 1MB)) { $hash = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }
        [pscustomobject]@{ Path = $p; SizeMB = [math]::Round($f.Length / 1MB, 1); Written = $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'); SHA256 = $hash }
    })

    $report = [pscustomobject]@{
        Computer = $env:COMPUTERNAME
        Collected = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        WindowDays = $Days
        Summary = [pscustomobject]@{
            UnexpectedShutdowns = @($timeline | Where-Object { $_.Category -eq 'Unexpected shutdown' }).Count
            BugChecks = @($timeline | Where-Object { $_.Category -eq 'Bug check' }).Count
            HardwareErrors = @($timeline | Where-Object { $_.Category -eq 'Hardware error' }).Count
            DiskEvents = @($timeline | Where-Object { $_.Category -eq 'Disk' }).Count
            DumpFiles = $dumps.Count
        }
        DumpConfiguration = [pscustomobject]@{ Type = $dumpType; AutoReboot = $cc.AutoReboot; Overwrite = $cc.Overwrite; Pagefile = $pagefile }
        Timeline = @($timeline | Sort-Object Time -Descending)
        ReliabilityRecords = $reliability
        Dumps = $dumps
    }
    $report | ConvertTo-Json -Depth 5
    exit 0
}
catch {
    Write-Output "FIX-13 failed: $($_.Exception.Message)"
    exit 1
}
