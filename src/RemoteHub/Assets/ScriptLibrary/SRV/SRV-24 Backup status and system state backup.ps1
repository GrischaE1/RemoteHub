<#
.SYNOPSIS
    Reports Windows Server Backup status and VSS writer health, and can start a system state backup.
.DESCRIPTION
    Reports the backup policy, the last successful backup and its age, the last result and VSS writer state;
    on domain controllers also the last system state backup recorded in the directory. Backup mode starts a
    system state backup to an approved target. Covers Windows Server Backup only; third-party products need
    their own checks. Backups hold sensitive data, so protect and encrypt the target. Restores, especially
    directory restores, are planned manual operations and are out of scope.
.NOTES
    RemoteHub: id=SRV-24; context=System; os=Server; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Status', 'Backup')]
    [string]$Mode = 'Status',
    [int]$MaxAgeDays = 7,
    # Approved backup target: a volume ('E:') or a share ('\\server\share').
    [string]$BackupTarget = '',
    # Sensor mode writes a single integer: days since the last successful backup (-1 = never).
    [switch]$Sensor
)

$ErrorActionPreference = 'Stop'

function Get-VssWriters {
    # vssadmin labels are localized, but writer names are quoted and the state code is in brackets in every language.
    $out = & vssadmin.exe list writers 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    $writers = @()
    $current = $null
    foreach ($line in @($out)) {
        if ($line -match "'([^']+)'" -and $line -notmatch '\{') { $current = $Matches[1]; continue }
        if ($current -and $line -match '\[(\d+)\]\s*(.*)$') {
            $writers += [pscustomobject]@{ Writer = $current; StateCode = [int]$Matches[1]; State = $Matches[2].Trim() }
            $current = $null
        }
    }
    return $writers
}

$exitCode = 0
try {
    $age = -1
    $summary = $null
    $policy = 'Data unavailable'
    $lastJob = $null
    if (Get-Module -ListAvailable -Name WindowsServerBackup) {
        Import-Module WindowsServerBackup -Verbose:$false
        $summary = Get-WBSummary
        $pol = Get-WBPolicy -ErrorAction SilentlyContinue
        if ($pol) { $policy = "Schedule=$(@($pol.Schedule | ForEach-Object { $_.ToString('HH:mm') }) -join ',') SystemState=$($pol.SystemState) BareMetal=$($pol.BMR)" } else { $policy = 'No scheduled policy' }
        try { $lastJob = Get-WBJob -Previous 1 } catch { Write-Verbose 'No previous job.' }
        if ($summary.LastSuccessfulBackupTime -and $summary.LastSuccessfulBackupTime.Year -gt 2000) {
            $age = [int]((Get-Date) - $summary.LastSuccessfulBackupTime).TotalDays
        }
    }

    $dcBackup = 'Not a domain controller'
    if ((Get-CimInstance -ClassName Win32_ComputerSystem).DomainRole -ge 4) {
        $dcBackup = 'Data unavailable'
        if (Get-Module -ListAvailable -Name ActiveDirectory) {
            try {
                Import-Module ActiveDirectory -Verbose:$false
                $md = Get-ADReplicationAttributeMetadata -Object (Get-ADDomain).DistinguishedName -Server $env:COMPUTERNAME -Properties dSASignature
                $dcBackup = $md.LastOriginatingChangeTime
            } catch { Write-Verbose 'Directory backup metadata not readable.' }
        }
    }

    $writers = Get-VssWriters
    # State 1 is stable and 5 is waiting for completion; every other state means the writer needs attention.
    $badWriters = @()
    if ($null -ne $writers) { $badWriters = @($writers | Where-Object { $_.StateCode -ne 1 -and $_.StateCode -ne 5 }) }

    if ($Mode -eq 'Backup') {
        if (-not $BackupTarget) { throw 'Backup mode needs -BackupTarget (an approved volume or share).' }
        if ($BackupTarget -notmatch '^(\\\\[^\\]+\\[^\\]+|[A-Za-z]:)\\?$') { throw 'Backup target must be a volume (E:) or a share (\\server\share).' }
        if (-not (Test-Path -LiteralPath ($BackupTarget.TrimEnd('\') + '\'))) { throw "Backup target $BackupTarget is not reachable." }
        if (-not (Get-Command -Name wbadmin.exe -ErrorAction SilentlyContinue)) { throw 'Windows Server Backup (wbadmin) is not installed.' }
        if ($PSCmdlet.ShouldProcess($BackupTarget, 'wbadmin start systemstatebackup (long running)')) {
            $null = & wbadmin.exe start systemstatebackup "-backupTarget:$($BackupTarget.TrimEnd('\'))" -quiet
            if ($LASTEXITCODE -ne 0) { throw "wbadmin failed with exit code $LASTEXITCODE" }
            if (Get-Command -Name Get-WBSummary -ErrorAction SilentlyContinue) {
                $summary = Get-WBSummary
                $age = [int]((Get-Date) - $summary.LastSuccessfulBackupTime).TotalDays
            }
            if (-not $Sensor) { "System state backup to $BackupTarget finished." }
        }
    }

    if ($Sensor) {
        if ($null -eq $summary) { Write-Output 'Data unavailable' } else { Write-Output $age }
    } else {
        [pscustomobject]@{
            Computer = $env:COMPUTERNAME
            Policy = $policy
            LastSuccessfulBackup = $(if ($age -ge 0) { $summary.LastSuccessfulBackupTime } elseif ($summary) { 'Never' } else { 'Data unavailable' })
            DaysSinceLastSuccess = $(if ($null -eq $summary) { 'Data unavailable' } else { $age })
            LastResult = $(if ($summary) { "HResult=0x{0:X8}" -f $summary.LastBackupResultHR } else { 'Data unavailable' })
            LastJob = $(if ($lastJob) { "$($lastJob.JobType) ended $($lastJob.EndTime) HResult=0x{0:X8}" -f $lastJob.HResult } else { 'Data unavailable' })
            LastDirectoryBackup = $dcBackup
            VssWriters = $(if ($null -eq $writers) { 'Data unavailable' } else { "$(@($writers).Count) writers, $($badWriters.Count) not stable" })
        } | Format-List | Out-String -Width 200
        if ($badWriters.Count -gt 0) { $badWriters | Format-Table -AutoSize | Out-String -Width 200 }
    }

    if ($null -eq $summary -or $age -lt 0 -or $age -gt $MaxAgeDays -or $badWriters.Count -gt 0) { $exitCode = 1 }
} catch {
    Write-Error "Backup status failed: $($_.Exception.Message)" -ErrorAction Continue
    $exitCode = 1
}
exit $exitCode
