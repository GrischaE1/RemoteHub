<#
.SYNOPSIS
    Checks Active Directory health per domain controller and reports an overall result.
.DESCRIPTION
    For every domain controller: replication failures and last successful inbound replication, SYSVOL and
    NETLOGON shares, SYSVOL DFSR state, time offset, DC locator SRV records, Global Catalog and recent
    critical directory events; plus FSMO holders, Recycle Bin, tombstone lifetime, last backup age and the
    local secure channel. Uses object-returning cmdlets instead of localized dcdiag/repadmin text. One
    failing DC does not stop the report; each DC gets its own row.
.NOTES
    RemoteHub: id=SRV-08; context=System; os=Server; risk=R0
#>
[CmdletBinding()]
param(
    [string]$DomainName = '',
    [int]$MaxReplicationAgeHours = 24,
    [int]$MaxTimeOffsetSeconds = 5,
    [int]$MaxBackupAgeDays = 7,
    [int]$EventHours = 24,
    # Sensor mode writes a single integer: the number of failing DCs.
    [switch]$Sensor
)

$ErrorActionPreference = 'Stop'

function Get-OffsetSeconds([string]$Server) {
    # The /dataonly sample line is numeric ("hh:mm:ss, +00.0012345s"); only the offset is taken from it.
    $out = & w32tm.exe /stripchart "/computer:$Server" /samples:1 /dataonly 2>$null
    foreach ($line in @($out)) {
        if ($line -match ',\s*([+-]\d+[\.,]\d+)s') { return [Math]::Abs([double]($Matches[1] -replace ',', '.')) }
    }
    return $null
}

try {
    if (-not (Get-Module -ListAvailable -Name ActiveDirectory)) {
        if ($Sensor) { Write-Output 'Data unavailable' } else { Write-Output 'Data unavailable: the ActiveDirectory module is not installed.' }
        exit 1
    }
    Import-Module ActiveDirectory -Verbose:$false
    $dp = @{}
    if ($DomainName) { $dp.Server = $DomainName }
    $domain = Get-ADDomain @dp
    $forest = Get-ADForest @dp
    $dcs = @(Get-ADDomainController -Filter * -Server $domain.DNSRoot)

    $configNc = (Get-ADRootDSE -Server $domain.DNSRoot).configurationNamingContext
    $ds = Get-ADObject -Identity "CN=Directory Service,CN=Windows NT,CN=Services,$configNc" -Properties tombstoneLifetime -Server $domain.DNSRoot
    $tombstone = 'Data unavailable'
    if ($ds.tombstoneLifetime) { $tombstone = $ds.tombstoneLifetime } else { $tombstone = '60 (default)' }
    $rb = Get-ADOptionalFeature -Filter "Name -eq 'Recycle Bin Feature'" -Server $domain.DNSRoot
    $recycleBin = (@($rb.EnabledScopes).Count -gt 0)

    $srv = @()
    try { $srv = @(Resolve-DnsName -Name "_ldap._tcp.dc._msdcs.$($domain.DNSRoot)" -Type SRV -DnsOnly | Where-Object { $_.Type -eq 'SRV' } | ForEach-Object { $_.NameTarget.ToLowerInvariant() }) } catch { Write-Verbose 'SRV lookup failed.' }

    $rows = @()
    foreach ($dc in $dcs) {
        $name = $dc.HostName
        $issues = @()
        $row = [ordered]@{ DC = $name; Site = $dc.Site; GlobalCatalog = $dc.IsGlobalCatalog; ReplFailures = 'Data unavailable'
            LastInboundRepl = 'Data unavailable'; Sysvol = 'Data unavailable'; Netlogon = 'Data unavailable'; SysvolDfsr = 'Data unavailable'
            TimeOffsetSec = 'Data unavailable'; SrvRecord = ($srv -contains $name.ToLowerInvariant()); CriticalEvents = 'Data unavailable'; LastBackup = 'Data unavailable'; Status = 'OK'; Issues = '' }
        try {
            $fail = @(Get-ADReplicationFailure -Target $name | Where-Object { $_.FailureCount -gt 0 })
            $row.ReplFailures = $fail.Count
            if ($fail.Count -gt 0) { $issues += "$($fail.Count) replication failure(s)" }
            $meta = @(Get-ADReplicationPartnerMetadata -Target $name -Partition * -ErrorAction SilentlyContinue)
            if ($meta.Count -gt 0) {
                $oldest = ($meta | Measure-Object -Property LastReplicationSuccess -Minimum).Minimum
                $row.LastInboundRepl = $oldest
                if ($oldest -lt (Get-Date).AddHours(-$MaxReplicationAgeHours)) { $issues += 'inbound replication older than threshold' }
            }
        } catch { $issues += "replication query failed: $($_.Exception.Message)" }

        $row.Sysvol = Test-Path -LiteralPath "\\$name\SYSVOL"
        $row.Netlogon = Test-Path -LiteralPath "\\$name\NETLOGON"
        if (-not $row.Sysvol -or -not $row.Netlogon) { $issues += 'SYSVOL or NETLOGON not shared' }
        if (-not $row.SrvRecord) { $issues += 'DC locator SRV record missing' }

        try {
            $rf = Get-CimInstance -ComputerName $name -Namespace 'root/microsoftdfs' -ClassName DfsrReplicatedFolderInfo -Filter "ReplicatedFolderName='SYSVOL Share'" -OperationTimeoutSec 30
            # State 4 is Normal; anything else means initial sync, error or recovery.
            $row.SysvolDfsr = [int]$rf.State
            if ($rf.State -ne 4) { $issues += "SYSVOL DFSR state $($rf.State)" }
        } catch { Write-Verbose "DFSR state unavailable on $name." }

        $off = Get-OffsetSeconds $name
        if ($null -ne $off) {
            $row.TimeOffsetSec = [Math]::Round($off, 3)
            if ($off -gt $MaxTimeOffsetSeconds) { $issues += 'time offset above threshold' }
        }

        try {
            $ev = @(Get-WinEvent -ComputerName $name -FilterHashtable @{ LogName = 'Directory Service', 'DNS Server', 'DFS Replication'; Level = 1, 2; StartTime = (Get-Date).AddHours(-$EventHours) } -MaxEvents 200 -ErrorAction SilentlyContinue)
            $row.CriticalEvents = $ev.Count
        } catch { Write-Verbose "Event query failed on $name." }

        try {
            $bk = Get-ADReplicationAttributeMetadata -Object $domain.DistinguishedName -Server $name -Properties dSASignature
            $row.LastBackup = $bk.LastOriginatingChangeTime
            if ($bk.LastOriginatingChangeTime -lt (Get-Date).AddDays(-$MaxBackupAgeDays)) { $issues += 'last backup older than threshold' }
        } catch { Write-Verbose "Backup metadata unavailable on $name." }

        if ($issues.Count -gt 0) { $row.Status = 'Failing'; $row.Issues = $issues -join '; ' }
        $rows += [pscustomobject]$row
    }

    $secure = 'Data unavailable'
    try { $secure = Test-ComputerSecureChannel } catch { Write-Verbose 'Secure channel test failed to run.' }
    $failing = @($rows | Where-Object { $_.Status -ne 'OK' }).Count
    $healthy = ($failing -eq 0) -and ($secure -ne $false)

    if ($Sensor) { Write-Output $failing } else {
        [pscustomobject]@{
            Domain = $domain.DNSRoot; Forest = $forest.Name
            PDCEmulator = $domain.PDCEmulator; RIDMaster = $domain.RIDMaster; InfrastructureMaster = $domain.InfrastructureMaster
            SchemaMaster = $forest.SchemaMaster; DomainNamingMaster = $forest.DomainNamingMaster
            RecycleBinEnabled = $recycleBin; TombstoneLifetimeDays = $tombstone
            LocalSecureChannel = $secure; DomainControllers = $dcs.Count; FailingDCs = $failing; Healthy = $healthy
        } | Format-List | Out-String -Width 200
        $rows | Format-Table DC, Site, GlobalCatalog, ReplFailures, LastInboundRepl, Sysvol, Netlogon, SysvolDfsr, TimeOffsetSec, SrvRecord, CriticalEvents, LastBackup, Status -AutoSize | Out-String -Width 300
        $rows | Where-Object { $_.Issues } | Format-Table DC, Issues -Wrap | Out-String -Width 200
    }
    if ($healthy) { exit 0 } else { exit 1 }
} catch {
    Write-Error "AD health check failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
