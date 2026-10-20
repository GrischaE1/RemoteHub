<#
.SYNOPSIS
    Disables and quarantines, or later deletes, stale AD objects listed in a reviewed CSV from SRV-09.
.DESCRIPTION
    Acts only on an approved CSV (column DistinguishedName), never on a live query. Disable mode disables the
    account, stamps the description with date and ticket and moves it to a quarantine OU. Delete mode removes
    objects that sit disabled in the quarantine OU longer than the retention period, only with the Recycle
    Bin enabled. Attributes are exported before any change. Cluster name objects, virtual computer objects,
    protected and critical objects are skipped, as are computers holding BitLocker recovery information.
    A batch cap aborts the run when the list is larger than expected. Without -Apply it is a dry run.
.NOTES
    RemoteHub: id=SRV-10; context=AsMe; os=Server; risk=R2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$CsvPath = '',
    [ValidateSet('Disable', 'Delete')]
    [string]$Action = 'Disable',
    [string]$QuarantineOU = '',
    [string]$TicketReference = '',
    [int]$RetentionDays = 30,
    [int]$MaxObjects = 50,
    [string]$Server = '',
    [string]$ExportFolder = "$env:LOCALAPPDATA\RemoteHub\ScriptLogs",
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if (-not $Apply) {
    $WhatIfPreference = $true
    Write-Output 'Dry run: nothing changed. Run with -Apply to make the changes.'
}

$log = @()
$failed = $false
try {
    if (-not $CsvPath) { Write-Output 'Nothing to do: give -CsvPath with the reviewed and approved SRV-09 list.'; exit 0 }
    if (-not (Test-Path -LiteralPath $CsvPath)) { throw "CSV '$CsvPath' not found." }
    if (-not $QuarantineOU) { throw '-QuarantineOU is required.' }
    if ($Action -eq 'Disable' -and -not $TicketReference) { throw '-TicketReference is required to stamp the objects.' }
    if (-not (Get-Module -ListAvailable -Name ActiveDirectory)) { throw 'Data unavailable: the ActiveDirectory module is not installed.' }
    Import-Module ActiveDirectory -Verbose:$false

    $sp = @{}
    if ($Server) { $sp.Server = $Server } else { $sp.Server = (Get-ADDomain).PDCEmulator }
    $null = Get-ADOrganizationalUnit -Identity $QuarantineOU @sp
    $rows = @(Import-Csv -LiteralPath $CsvPath | Where-Object { $_.DistinguishedName })
    if ($rows.Count -eq 0) { Write-Output 'Nothing to do: the CSV lists no objects.'; exit 0 }
    if ($rows.Count -gt $MaxObjects) { throw "The CSV lists $($rows.Count) objects, above the cap of $MaxObjects. Aborting." }

    if ($Action -eq 'Delete') {
        $rb = Get-ADOptionalFeature -Filter "Name -eq 'Recycle Bin Feature'" @sp
        if (@($rb.EnabledScopes).Count -eq 0) { throw 'The AD Recycle Bin is not enabled; deletion is refused.' }
    }

    $props = @('description', 'userAccountControl', 'servicePrincipalName', 'isCriticalSystemObject', 'adminCount', 'ProtectedFromAccidentalDeletion', 'objectClass', 'whenChanged', 'sAMAccountName')
    $objects = @()
    foreach ($r in $rows) {
        try { $objects += Get-ADObject -Identity $r.DistinguishedName -Properties $props @sp }
        catch { $log += [pscustomobject]@{ Object = $r.DistinguishedName; Result = 'Not found (skipped)' } }
    }

    if ($Apply) {
        New-Item -ItemType Directory -Path $ExportFolder -Force -WhatIf:$false | Out-Null
        $exportFile = Join-Path $ExportFolder ("SRV-10-{0}-{1}.csv" -f $Action, (Get-Date -Format 'yyyyMMdd-HHmmss'))
        $objects | Select-Object DistinguishedName, sAMAccountName, ObjectClass, userAccountControl, description, whenChanged, adminCount |
            Export-Csv -LiteralPath $exportFile -NoTypeInformation -Encoding UTF8 -WhatIf:$false
        Write-Output "Pre-change attributes exported to $exportFile"
    }

    $failures = 0
    foreach ($o in $objects) {
        $dn = $o.DistinguishedName
        $spn = @($o.servicePrincipalName)
        $skip = ''
        if ($o.isCriticalSystemObject -or $o.ProtectedFromAccidentalDeletion -or $o.adminCount -eq 1) { $skip = 'protected or critical object' }
        elseif (@($spn | Where-Object { $_ -like 'MSClusterVirtualServer/*' -or $_ -like 'MSServerClusterMgmtAPI/*' -or $_ -like 'MSServerCluster/*' }).Count -gt 0) { $skip = 'cluster name or virtual computer object' }
        elseif ($o.ObjectClass -notin @('user', 'computer')) { $skip = "object class $($o.ObjectClass) not handled" }
        elseif ($o.ObjectClass -eq 'computer' -and @(Get-ADObject -SearchBase $dn -SearchScope OneLevel -LDAPFilter '(objectClass=msFVE-RecoveryInformation)' @sp).Count -gt 0) {
            $skip = 'holds BitLocker recovery information; escrow it first'
        }
        if ($skip) { $log += [pscustomobject]@{ Object = $dn; Result = "Skipped: $skip" }; continue }

        $disabled = ([int]$o.userAccountControl -band 2) -eq 2
        $inQuarantine = $dn -like "*,$QuarantineOU"
        try {
            if ($Action -eq 'Disable') {
                if ($disabled -and $inQuarantine) { $log += [pscustomobject]@{ Object = $dn; Result = 'No change' }; continue }
                if ($PSCmdlet.ShouldProcess($dn, "Disable, stamp ticket $TicketReference and move to $QuarantineOU")) {
                    if (-not $disabled) { Disable-ADAccount -Identity $dn @sp }
                    $stamp = "Disabled $(Get-Date -Format 'yyyy-MM-dd') ticket $TicketReference by SRV-10"
                    Set-ADObject -Identity $dn -Replace @{ description = $stamp } @sp
                    if (-not $inQuarantine) { Move-ADObject -Identity $dn -TargetPath $QuarantineOU @sp }
                    $log += [pscustomobject]@{ Object = $dn; Result = 'Disabled and quarantined' }
                } else { $log += [pscustomobject]@{ Object = $dn; Result = 'Planned: disable and quarantine' } }
            } else {
                $stampDate = $null
                if ([string]$o.description -match '^Disabled (\d{4}-\d{2}-\d{2}) ') { $stampDate = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture) }
                if (-not $disabled -or -not $inQuarantine -or $null -eq $stampDate) { $log += [pscustomobject]@{ Object = $dn; Result = 'Skipped: not disabled in quarantine by SRV-10' }; continue }
                if ($stampDate -gt (Get-Date).AddDays(-$RetentionDays)) { $log += [pscustomobject]@{ Object = $dn; Result = "Skipped: retention until $($stampDate.AddDays($RetentionDays).ToString('yyyy-MM-dd'))" }; continue }
                if ($PSCmdlet.ShouldProcess($dn, 'Remove-ADObject including child objects')) {
                    Remove-ADObject -Identity $dn -Recursive -Confirm:$false @sp
                    $log += [pscustomobject]@{ Object = $dn; Result = 'Deleted (restorable from Recycle Bin)' }
                } else { $log += [pscustomobject]@{ Object = $dn; Result = 'Planned: delete' } }
            }
        } catch {
            $failures++
            $failed = $true
            $log += [pscustomobject]@{ Object = $dn; Result = "Failed: $($_.Exception.Message)" }
            if ($failures -ge 3) { $log += [pscustomobject]@{ Object = '(run)'; Result = 'Aborted after 3 failures' }; break }
        }
    }
} catch {
    $failed = $true
    $log += [pscustomobject]@{ Object = '(run)'; Result = "Failed: $($_.Exception.Message)" }
}

$log | Format-Table -AutoSize -Wrap | Out-String -Width 250
if ($failed) { exit 1 }
exit 0
