<#
.SYNOPSIS
    Audits SMB shares, share-root permissions, SMB settings, FSRM, DFS and dedup; optionally creates a share or quota.
.DESCRIPTION
    Reports shares and share access, NTFS ACLs on share roots (broad write access, broken inheritance,
    orphaned SIDs), SMB server signing, encryption and SMBv1, session and open file counts, FSRM quotas and
    file screens, DFS namespaces, DFS Replication backlog and deduplication. Configure mode creates a share,
    grants share access and applies an FSRM quota; it never changes NTFS permissions, so no recursive ACL
    change (R2) can happen. Only share roots are scanned; deep ACL scans are slow and belong off peak.
.NOTES
    RemoteHub: id=SRV-14; context=System; os=Server; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Audit', 'Configure')]
    [string]$Mode = 'Audit',
    [string]$ShareName = '',
    [string]$SharePath = '',
    [string[]]$FullAccess = @(),
    [string[]]$ChangeAccess = @(),
    [string[]]$ReadAccess = @(),
    [string]$QuotaPath = '',
    [int]$QuotaSizeGB = 0,
    [bool]$CheckDfsrBacklog = $true
)

$ErrorActionPreference = 'Stop'

$findings = @()
function Add-Finding([string]$Area, [string]$Item, [string]$Detail, [string]$Severity) {
    $script:findings += [pscustomobject]@{ Area = $Area; Item = $Item; Detail = $Detail; Severity = $Severity }
}
$broad = @('Everyone', 'NT AUTHORITY\Authenticated Users', 'BUILTIN\Users', 'Domain Users')
function Test-Broad([string]$Name) { foreach ($b in $broad) { if ($Name -eq $b -or $Name -like "*\$b") { return $true } }; return $false }

$changes = @()
$exitCode = 0
try {
    if ($Mode -eq 'Configure') {
        if ($ShareName -and $SharePath) {
            if (-not (Test-Path -LiteralPath $SharePath)) { throw "Share path '$SharePath' does not exist." }
            $existing = Get-SmbShare -Name $ShareName -ErrorAction SilentlyContinue
            if (-not $existing) {
                if ($PSCmdlet.ShouldProcess($ShareName, "Create SMB share on $SharePath")) {
                    $p = @{ Name = $ShareName; Path = $SharePath; FolderEnumerationMode = 'AccessBased' }
                    if ($FullAccess.Count) { $p.FullAccess = $FullAccess }
                    if ($ChangeAccess.Count) { $p.ChangeAccess = $ChangeAccess }
                    if ($ReadAccess.Count) { $p.ReadAccess = $ReadAccess }
                    if (-not ($FullAccess.Count + $ChangeAccess.Count + $ReadAccess.Count)) { $p.ReadAccess = 'BUILTIN\Administrators' }
                    New-SmbShare @p | Out-Null
                    $changes += "Created share $ShareName"
                } else { $changes += "Planned: create share $ShareName" }
            } else {
                $acc = @(Get-SmbShareAccess -Name $ShareName)
                foreach ($pair in @(@('Full', $FullAccess), @('Change', $ChangeAccess), @('Read', $ReadAccess))) {
                    foreach ($acct in $pair[1]) {
                        if (@($acc | Where-Object { $_.AccountName -eq $acct -and [string]$_.AccessRight -eq $pair[0] -and [string]$_.AccessControlType -eq 'Allow' }).Count -gt 0) { continue }
                        if ($PSCmdlet.ShouldProcess($ShareName, "Grant $($pair[0]) share access to $acct")) {
                            Grant-SmbShareAccess -Name $ShareName -AccountName $acct -AccessRight $pair[0] -Force | Out-Null
                            $changes += "Granted $($pair[0]) on $ShareName to $acct"
                        } else { $changes += "Planned: grant $($pair[0]) on $ShareName to $acct" }
                    }
                }
            }
        }
        if ($QuotaPath -and $QuotaSizeGB -gt 0) {
            if (-not (Get-Module -ListAvailable -Name FileServerResourceManager)) { throw 'FSRM is not installed; quotas cannot be applied.' }
            $q = Get-FsrmQuota -Path $QuotaPath -ErrorAction SilentlyContinue
            $size = [uint64]$QuotaSizeGB * 1GB
            if ($q -and $q.Size -eq $size) { $changes += "No change: quota on $QuotaPath already $QuotaSizeGB GB" }
            elseif ($PSCmdlet.ShouldProcess($QuotaPath, "Set hard quota $QuotaSizeGB GB")) {
                if ($q) { Set-FsrmQuota -Path $QuotaPath -Size $size | Out-Null } else { New-FsrmQuota -Path $QuotaPath -Size $size | Out-Null }
                $changes += "Quota on $QuotaPath set to $QuotaSizeGB GB"
            } else { $changes += "Planned: quota $QuotaSizeGB GB on $QuotaPath" }
        }
        if ($changes.Count -eq 0) { $changes += 'Nothing requested: give -ShareName/-SharePath or -QuotaPath/-QuotaSizeGB.' }
    }

    $cfg = Get-SmbServerConfiguration
    if ($cfg.EnableSMB1Protocol) { Add-Finding 'SMB' 'SMBv1' 'Enabled' 'High' }
    if (-not $cfg.RequireSecuritySignature) { Add-Finding 'SMB' 'Signing' 'Not required' 'Medium' }
    Add-Finding 'SMB' 'Encryption' ("EncryptData=$($cfg.EncryptData) RejectUnencrypted=$($cfg.RejectUnencryptedAccess)") 'Info'
    Add-Finding 'SMB' 'Sessions' ("$(@(Get-SmbSession).Count) sessions, $(@(Get-SmbOpenFile).Count) open files") 'Info'

    foreach ($s in @(Get-SmbShare | Where-Object { -not $_.Special -and $_.Path })) {
        foreach ($a in @(Get-SmbShareAccess -Name $s.Name)) {
            if ([string]$a.AccessControlType -eq 'Allow' -and [string]$a.AccessRight -ne 'Read' -and (Test-Broad $a.AccountName)) {
                Add-Finding 'ShareAccess' $s.Name "$($a.AccountName) has $($a.AccessRight)" 'High'
            }
        }
        try {
            $acl = Get-Acl -LiteralPath $s.Path
            if ($acl.AreAccessRulesProtected) { Add-Finding 'NTFS' $s.Name 'Inheritance disabled on share root' 'Info' }
            foreach ($r in $acl.Access) {
                $id = [string]$r.IdentityReference
                if ($id -match '^S-1-5-21-') { Add-Finding 'NTFS' $s.Name "Orphaned SID $id" 'Medium' }
                # Write, append, delete, change-permission and take-ownership bits plus GENERIC_ALL and GENERIC_WRITE.
                $write = ([int64]$r.FileSystemRights -band 0x500D0156) -ne 0
                if ([string]$r.AccessControlType -eq 'Allow' -and $write -and (Test-Broad $id)) { Add-Finding 'NTFS' $s.Name "$id has $($r.FileSystemRights)" 'High' }
            }
        } catch { Add-Finding 'NTFS' $s.Name "Data unavailable: $($_.Exception.Message)" 'Info' }
    }

    if (Get-Module -ListAvailable -Name FileServerResourceManager) {
        foreach ($q in @(Get-FsrmQuota)) { Add-Finding 'FSRM' $q.Path ("Quota {0:N1} GB, used {1:N0} %" -f ($q.Size / 1GB), ($q.Usage * 100 / [Math]::Max($q.Size, 1))) 'Info' }
        Add-Finding 'FSRM' 'File screens' "$(@(Get-FsrmFileScreen).Count) screens" 'Info'
    } else { Add-Finding 'FSRM' 'Module' 'Data unavailable (FSRM not installed)' 'Info' }

    if (Get-Module -ListAvailable -Name DFSN) {
        try { foreach ($n in @(Get-DfsnRoot -ErrorAction Stop)) { Add-Finding 'DFSN' $n.Path ([string]$n.State) 'Info' } } catch { Write-Verbose 'No DFS namespaces hosted.' }
    }
    if ($CheckDfsrBacklog -and (Get-Module -ListAvailable -Name DFSR)) {
        try {
            foreach ($c in @(Get-DfsrConnection -ErrorAction Stop | Where-Object { $_.DestinationComputerName -like "$env:COMPUTERNAME*" })) {
                foreach ($f in @(Get-DfsReplicatedFolder -GroupName $c.GroupName)) {
                    $bl = @(Get-DfsrBacklog -GroupName $c.GroupName -FolderName $f.FolderName -SourceComputerName $c.SourceComputerName -DestinationComputerName $c.DestinationComputerName -ErrorAction SilentlyContinue).Count
                    $sev = 'Info'
                    if ($bl -gt 100) { $sev = 'Medium' }
                    Add-Finding 'DFSR' "$($c.GroupName)\$($f.FolderName)" "Backlog from $($c.SourceComputerName): $bl (first 100 counted)" $sev
                }
            }
        } catch { Write-Verbose 'No DFS Replication groups or backlog unavailable.' }
    }
    if (Get-Module -ListAvailable -Name Deduplication) {
        foreach ($d in @(Get-DedupStatus -ErrorAction SilentlyContinue)) { Add-Finding 'Dedup' $d.Volume ("Saved {0:N1} GB, last optimization {1}" -f ($d.SavedSpace / 1GB), $d.LastOptimizationTime) 'Info' }
    }

    $findings | Sort-Object Area, Item | Format-Table -AutoSize -Wrap | Out-String -Width 250
    if ($changes.Count -gt 0) { 'Change log:'; $changes | ForEach-Object { "  $_" } }
    if (@($findings | Where-Object { $_.Severity -eq 'High' }).Count -gt 0) { $exitCode = 1 }
} catch {
    Write-Error "File services audit failed: $($_.Exception.Message)" -ErrorAction Continue
    $exitCode = 1
}
exit $exitCode
