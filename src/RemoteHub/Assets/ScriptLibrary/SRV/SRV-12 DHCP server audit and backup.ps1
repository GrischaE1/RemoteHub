<#
.SYNOPSIS
    Audits the local DHCP server and, with -Apply, backs up or restores its configuration.
.DESCRIPTION
    Reports scope utilization against an alert threshold, failover relationship state, authorization in
    Active Directory, option and reservation counts and conflict detection. Backup mode runs Backup-DhcpServer
    and Export-DhcpServer; Restore mode imports an export (overwrites the current configuration, so the
    current state is backed up first). Exports with leases contain MAC addresses and host names, so leases
    are left out unless -IncludeLeases. With failover, replicate after changes and never edit both partners.
.NOTES
    RemoteHub: id=SRV-12; context=System; os=Server; risk=R2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Audit', 'Backup', 'Restore')]
    [string]$Mode = 'Audit',
    [int]$UtilizationThreshold = 80,
    [string]$BackupFolder = "$env:ProgramData\RemoteHub\DhcpBackup",
    # Export XML to import in Restore mode.
    [string]$RestoreFile = '',
    [switch]$IncludeLeases,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if (-not $Apply) {
    $WhatIfPreference = $true
    Write-Output 'Dry run: nothing changed. Run with -Apply to make the changes.'
}

$exitCode = 0
try {
    if (-not (Get-Module -ListAvailable -Name DhcpServer)) { Write-Output 'Data unavailable: the DhcpServer module is not installed.'; exit 1 }
    Import-Module DhcpServer -Verbose:$false

    $fqdn = ([System.Net.Dns]::GetHostEntry($env:COMPUTERNAME)).HostName
    $authorized = 'Data unavailable'
    try {
        $inDc = @(Get-DhcpServerInDC)
        $authorized = (@($inDc | Where-Object { $_.DnsName -eq $fqdn }).Count -gt 0)
    } catch { Write-Verbose 'Authorization list not readable (workgroup server or no directory access).' }
    $setting = Get-DhcpServerSetting
    $conflict = $setting.ConflictDetectionAttempts
    $failover = @()
    try { $failover = @(Get-DhcpServerv4Failover -ErrorAction SilentlyContinue) } catch { Write-Verbose 'No failover data.' }

    $scopes = @(Get-DhcpServerv4Scope)
    $alerts = 0
    $rows = foreach ($s in $scopes) {
        $st = Get-DhcpServerv4ScopeStatistics -ScopeId $s.ScopeId
        $res = @(Get-DhcpServerv4Reservation -ScopeId $s.ScopeId -ErrorAction SilentlyContinue).Count
        $opt = @(Get-DhcpServerv4OptionValue -ScopeId $s.ScopeId -ErrorAction SilentlyContinue).Count
        $fo = @($failover | Where-Object { @($_.ScopeId | ForEach-Object { $_.ToString() }) -contains $s.ScopeId.ToString() } | Select-Object -First 1)
        $alert = ($s.State -eq 'Active' -and $st.PercentageInUse -ge $UtilizationThreshold)
        if ($alert) { $alerts++ }
        [pscustomobject]@{ ScopeId = $s.ScopeId.ToString(); Name = $s.Name; State = [string]$s.State; InUse = $st.InUse; Free = $st.Free
            PercentInUse = [Math]::Round($st.PercentageInUse, 1); Alert = $alert; Reservations = $res; Options = $opt
            Failover = $(if ($fo.Count -gt 0) { "$($fo[0].Name) ($($fo[0].State))" } else { 'None' }) }
    }

    [pscustomobject]@{
        Server = $fqdn; AuthorizedInAD = $authorized; ConflictDetectionAttempts = $conflict
        DynamicBootp = $setting.DynamicBootp; Scopes = $scopes.Count; ScopesOverThreshold = $alerts
        FailoverRelationships = (@($failover | ForEach-Object { "$($_.Name)=$($_.State) partner $($_.PartnerServer)" }) -join '; ')
    } | Format-List | Out-String -Width 200
    $rows | Format-Table -AutoSize | Out-String -Width 250

    if ($authorized -eq $false -or $alerts -gt 0 -or @($failover | Where-Object { [string]$_.State -ne 'Normal' }).Count -gt 0) { $exitCode = 1 }

    if ($Mode -ne 'Audit') {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $target = Join-Path $BackupFolder $stamp
        if ($PSCmdlet.ShouldProcess($target, 'Back up DHCP database and export configuration')) {
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            Backup-DhcpServer -Path (Join-Path $target 'Database')
            $exportFile = Join-Path $target 'DhcpConfig.xml'
            Export-DhcpServer -File $exportFile -Leases:$IncludeLeases -Force
            Write-Output "Backup written to $target (leases included: $([bool]$IncludeLeases))"
        }
        if ($Mode -eq 'Restore') {
            if (-not $RestoreFile -or -not (Test-Path -LiteralPath $RestoreFile)) { throw 'Restore needs -RestoreFile pointing at an Export-DhcpServer XML file.' }
            Write-Warning 'Import overwrites the existing DHCP configuration of this server.'
            if ($PSCmdlet.ShouldProcess($env:COMPUTERNAME, "Import DHCP configuration from $RestoreFile")) {
                Import-DhcpServer -File $RestoreFile -BackupPath (Join-Path $target 'PreImport') -Leases:$IncludeLeases -Force
                Write-Output 'Restore finished.'
                if ($failover.Count -gt 0) { Write-Warning 'Failover is configured: run Invoke-DhcpServerv4FailoverReplication from this server only, after review.' }
            }
        }
        if ($exitCode -eq 1) { Write-Verbose 'Audit findings keep the exit code at 1 even though the backup step succeeded.' }
    }
} catch {
    Write-Error "DHCP audit or backup failed: $($_.Exception.Message)" -ErrorAction Continue
    $exitCode = 1
}
exit $exitCode
