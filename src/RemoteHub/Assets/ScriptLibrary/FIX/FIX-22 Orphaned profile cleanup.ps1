<#
.SYNOPSIS
    Finds local user profiles unused for N days and, with -Apply, removes them cleanly through CIM.
.DESCRIPTION
    Lists profiles that are not loaded, not special, not excluded and not used for N days, with size and last-use
    evidence, then removes them with Remove-CimInstance so the profile list and folder stay consistent. Warning:
    deletion destroys local data that may not exist elsewhere - confirm cloud sync or folder redirection first.
    LastUseTime is refreshed by system activity, so the decision uses the hive file time and the recorded unload
    time instead. Administrator, service, system and listed shared-device accounts are always excluded. Without
    -Apply only the candidate list is shown.
.NOTES
    RemoteHub: id=FIX-22; context=System; os=Both; risk=R2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [int]$UnusedDays = 90,
    [string[]]$ExcludeAccount = @('Administrator', 'DefaultAccount', 'defaultuser0'),
    [bool]$IncludeSize = $true,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if (-not $Apply) { $WhatIfPreference = $true }
$cutoff = (Get-Date).AddDays(-$UnusedDays)

function Get-FolderSizeMB([string]$Root) {
    # Own walk without following junctions; profiles contain legacy junctions such as "Application Data".
    $total = [long]0
    $stack = New-Object System.Collections.Stack
    $stack.Push((New-Object IO.DirectoryInfo $Root))
    while ($stack.Count -gt 0) {
        $dir = $stack.Pop()
        try { $items = $dir.GetFileSystemInfos() } catch { continue }
        foreach ($i in $items) {
            if ($i.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            if ($i -is [IO.DirectoryInfo]) { $stack.Push($i) } else { $total += $i.Length }
        }
    }
    [math]::Round($total / 1MB, 1)
}

function Get-UnloadTime([string]$Sid) {
    # Windows 10 1903 and later record the last logoff here; it is not touched by background activity.
    $p = Get-ItemProperty -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$Sid" -ErrorAction SilentlyContinue
    if ($p -and $p.PSObject.Properties['LocalProfileUnloadTimeHigh'] -and $p.PSObject.Properties['LocalProfileUnloadTimeLow']) {
        $ft = ([int64][uint32]$p.LocalProfileUnloadTimeHigh -shl 32) -bor [int64][uint32]$p.LocalProfileUnloadTimeLow
        if ($ft -gt 0) { return [datetime]::FromFileTime($ft) }
    }
    return $null
}

try {
    $rows = New-Object System.Collections.Generic.List[object]
    $failed = $false
    foreach ($p in @(Get-CimInstance -ClassName Win32_UserProfile)) {
        $sid = $p.SID
        $name = Split-Path -Leaf "$($p.LocalPath)"
        # Only local, domain (S-1-5-21) and Entra ID (S-1-12-1) users; never built-in Administrator (RID 500).
        if ($p.Special -or -not ($sid -like 'S-1-5-21-*' -or $sid -like 'S-1-12-1-*') -or $sid -like '*-500') { continue }
        if ("$($p.LocalPath)" -like "$env:windir*") { continue }
        if ($ExcludeAccount -contains $name -or $ExcludeAccount -contains $sid) { continue }

        $hive = Join-Path "$($p.LocalPath)" 'NTUSER.DAT'
        $hiveTime = $null; if (Test-Path -LiteralPath $hive) { $hiveTime = (Get-Item -LiteralPath $hive -Force).LastWriteTime }
        $unload = Get-UnloadTime $sid
        $evidence = @($hiveTime, $unload) | Where-Object { $_ } | Sort-Object -Descending | Select-Object -First 1
        $reason = ''
        if ($p.Loaded) { $reason = 'Loaded' }
        elseif (-not $evidence) { $reason = 'No hive or unload time (Data unavailable)' }
        elseif ($evidence -ge $cutoff) { $reason = 'Used recently' }
        if ($reason) { Write-Verbose "$name skipped: $reason"; continue }

        $size = 'Not measured'
        if ($IncludeSize -and (Test-Path -LiteralPath $p.LocalPath)) { $size = Get-FolderSizeMB $p.LocalPath }
        $onedrive = @(Get-ChildItem -LiteralPath $p.LocalPath -Directory -Filter 'OneDrive*' -Force -ErrorAction SilentlyContinue).Count -gt 0
        $lastUse = 'Data unavailable'; if ($p.LastUseTime) { $lastUse = $p.LastUseTime.ToString('yyyy-MM-dd') }
        $row = [pscustomobject]@{
            Profile = $p.LocalPath; Sid = $sid; SizeMB = $size; HiveWritten = $(if ($hiveTime) { $hiveTime.ToString('yyyy-MM-dd') } else { '-' })
            LastLogoff = $(if ($unload) { $unload.ToString('yyyy-MM-dd') } else { '-' }); LastUseTime = $lastUse; HasOneDriveFolder = $onedrive; Result = 'Candidate'
        }
        if ($PSCmdlet.ShouldProcess($p.LocalPath, 'Remove profile (registry entry and folder) through Win32_UserProfile')) {
            try {
                Remove-CimInstance -InputObject $p
                if (Test-Path -LiteralPath $p.LocalPath) { $row.Result = 'Removed, folder remnants left (files in use)' } else { $row.Result = 'Removed' }
            }
            catch { $failed = $true; $row.Result = "Failed: $($_.Exception.Message)" }
        }
        $rows.Add($row)
    }

    if ($rows.Count -eq 0) { Write-Output "No change: no unloaded profile unused for $UnusedDays days outside the exclusion list." }
    else {
        $rows
        Write-Output 'Warning: removal destroys local data. Confirm cloud sync or folder redirection for each candidate first.'
    }
    if (-not $Apply) { Write-Output 'Dry run: nothing changed. Run with -Apply to make the changes.' }
    if ($failed) { exit 1 }
    exit 0
}
catch {
    Write-Output "FIX-22 failed: $($_.Exception.Message)"
    exit 1
}
