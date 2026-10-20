<#
.SYNOPSIS
    Audits the local DNS server and, in Configure mode, sets forwarders, scavenging, aging and new zones.
.DESCRIPTION
    Audit reports zones (type, dynamic update, DNSSEC, zone transfers), forwarders, scavenging and aging,
    stale dynamic record candidates and a few server statistics. Configure mode exports the affected zones
    first, then changes only what differs. Many DNS cmdlets do not implement WhatIf, so every change goes
    through this script's own -WhatIf check. Scavenging deletes records: enable it on one server only, after
    reviewing the refresh and no-refresh intervals. Zone changes replicate and are hard to undo.
.NOTES
    RemoteHub: id=SRV-11; context=System; os=Server; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Audit', 'Configure')]
    [string]$Mode = 'Audit',
    [int]$StaleRecordDays = 30,
    [string[]]$Forwarder = @(),
    [switch]$EnableScavenging,
    [int]$ScavengingIntervalDays = 7,
    [string[]]$AgingZone = @(),
    [int]$RefreshIntervalDays = 7,
    [int]$NoRefreshIntervalDays = 7,
    [string]$NewPrimaryZone = '',
    [ValidateSet('Forest', 'Domain', 'Legacy', 'File')]
    [string]$ZoneReplicationScope = 'Domain'
)

$ErrorActionPreference = 'Stop'

$changes = @()
function Invoke-Change([string]$Target, [string]$What, [scriptblock]$Do) {
    if ($PSCmdlet.ShouldProcess($Target, $What)) {
        & $Do
        $script:changes += [pscustomobject]@{ Target = $Target; Change = $What; Result = 'Done' }
    } else {
        $script:changes += [pscustomobject]@{ Target = $Target; Change = $What; Result = 'Planned' }
    }
}

try {
    if (-not (Get-Module -ListAvailable -Name DnsServer)) { Write-Output 'Data unavailable: the DnsServer module is not installed.'; exit 1 }
    Import-Module DnsServer -Verbose:$false
    $zones = @(Get-DnsServerZone | Where-Object { -not $_.IsAutoCreated -and $_.ZoneName -ne 'TrustAnchors' })
    $cutoff = (Get-Date).AddDays(-$StaleRecordDays)
    $findings = @()

    $zoneRows = foreach ($z in $zones) {
        $aging = $null
        $stale = 'n/a'
        if ($z.ZoneType -eq 'Primary') {
            try { $aging = Get-DnsServerZoneAging -Name $z.ZoneName } catch { Write-Verbose "No aging data for $($z.ZoneName)." }
            $stale = @(Get-DnsServerResourceRecord -ZoneName $z.ZoneName | Where-Object { $_.Timestamp -and $_.Timestamp -lt $cutoff }).Count
        }
        if ($z.ZoneType -eq 'Primary' -and [string]$z.DynamicUpdate -eq 'NonsecureAndSecure') { $findings += "Zone $($z.ZoneName) accepts non-secure dynamic updates." }
        if ([string]$z.SecureSecondaries -eq 'TransferAnyServer') { $findings += "Zone $($z.ZoneName) allows zone transfers to any server." }
        $agingText = 'n/a'
        if ($aging) { $agingText = "$($aging.AgingEnabled) ($($aging.NoRefreshInterval.Days)/$($aging.RefreshInterval.Days) d)" }
        [pscustomobject]@{ Zone = $z.ZoneName; Type = [string]$z.ZoneType; AdIntegrated = $z.IsDsIntegrated; DynamicUpdate = [string]$z.DynamicUpdate
            Signed = $z.IsSigned; Transfers = [string]$z.SecureSecondaries; Aging = $agingText; StaleCandidates = $stale }
    }

    $fwd = Get-DnsServerForwarder
    $scav = Get-DnsServerScavenging
    $stats = $null
    try { $stats = Get-DnsServerStatistics } catch { Write-Verbose 'Statistics unavailable.' }
    $diag = Get-DnsServerDiagnostics
    if (-not $scav.ScavengingState) { $findings += 'Scavenging is off on this server (expected unless it is the designated scavenger).' }
    if (@($fwd.IPAddress).Count -eq 0) { $findings += 'No forwarders configured (root hints only).' }

    [pscustomobject]@{
        Server             = $env:COMPUTERNAME
        Forwarders         = (@($fwd.IPAddress | ForEach-Object { $_.IPAddressToString }) -join ', ')
        UseRootHint        = $fwd.UseRootHint
        ScavengingEnabled  = $scav.ScavengingState
        ScavengingInterval = $scav.ScavengingInterval
        LastScavengeTime   = $scav.LastScavengeTime
        DebugLogging       = $diag.EnableLoggingToFile
        TotalQueries       = $(if ($stats) { $stats.Query2Statistics.TotalQueries } else { 'Data unavailable' })
    } | Format-List | Out-String -Width 200
    $zoneRows | Format-Table -AutoSize | Out-String -Width 250
    if ($findings.Count -gt 0) { 'Findings:'; $findings | ForEach-Object { "  - $_" } }

    if ($Mode -eq 'Configure') {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        # Export-DnsServerZone writes to %windir%\System32\dns; the copy is the rollback point for records
        # that scavenging later deletes, so it runs right before each aging or scavenging change.
        $exportZone = { param([string]$Name) Export-DnsServerZone -Name $Name -FileName "$Name.$stamp.bak" }

        if ($Forwarder.Count -gt 0) {
            $cur = @($fwd.IPAddress | ForEach-Object { $_.IPAddressToString }) -join ','
            if ($cur -ne ($Forwarder -join ',')) {
                Invoke-Change 'Forwarders' "Set forwarders '$cur' -> '$($Forwarder -join ',')'" { Set-DnsServerForwarder -IPAddress $Forwarder }
            } else { $changes += [pscustomobject]@{ Target = 'Forwarders'; Change = 'Already set'; Result = 'No change' } }
        }
        if ($EnableScavenging) {
            $iv = New-TimeSpan -Days $ScavengingIntervalDays
            if (-not $scav.ScavengingState -or $scav.ScavengingInterval -ne $iv) {
                Invoke-Change 'Server scavenging' "Export primary zones, enable scavenging every $ScavengingIntervalDays days" {
                    foreach ($pz in @($zones | Where-Object { $_.ZoneType -eq 'Primary' })) { & $exportZone $pz.ZoneName }
                    Set-DnsServerScavenging -ScavengingState $true -ScavengingInterval $iv }
            } else { $changes += [pscustomobject]@{ Target = 'Server scavenging'; Change = 'Already set'; Result = 'No change' } }
        }
        foreach ($zn in $AgingZone) {
            $a = Get-DnsServerZoneAging -Name $zn
            if (-not $a.AgingEnabled -or $a.RefreshInterval.Days -ne $RefreshIntervalDays -or $a.NoRefreshInterval.Days -ne $NoRefreshIntervalDays) {
                Invoke-Change $zn "Export zone, enable aging ($NoRefreshIntervalDays/$RefreshIntervalDays days)" {
                    & $exportZone $zn
                    Set-DnsServerZoneAging -Name $zn -Aging $true -RefreshInterval (New-TimeSpan -Days $RefreshIntervalDays) -NoRefreshInterval (New-TimeSpan -Days $NoRefreshIntervalDays) }
            } else { $changes += [pscustomobject]@{ Target = $zn; Change = 'Aging already set'; Result = 'No change' } }
        }
        if ($NewPrimaryZone) {
            if (@($zones | Where-Object { $_.ZoneName -eq $NewPrimaryZone }).Count -gt 0) {
                $changes += [pscustomobject]@{ Target = $NewPrimaryZone; Change = 'Zone exists'; Result = 'No change' }
            } elseif ($ZoneReplicationScope -eq 'File') {
                Invoke-Change $NewPrimaryZone 'Create file-backed primary zone' { Add-DnsServerPrimaryZone -Name $NewPrimaryZone -ZoneFile "$NewPrimaryZone.dns" -DynamicUpdate None }
            } else {
                Invoke-Change $NewPrimaryZone "Create AD-integrated zone ($ZoneReplicationScope)" { Add-DnsServerPrimaryZone -Name $NewPrimaryZone -ReplicationScope $ZoneReplicationScope -DynamicUpdate Secure }
            }
        }
        'Change log:'
        $changes | Format-Table -AutoSize -Wrap | Out-String -Width 200
    }
    exit 0
} catch {
    if ($changes.Count -gt 0) { $changes | Format-Table -AutoSize | Out-String -Width 200 }
    Write-Error "DNS audit or configuration failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
