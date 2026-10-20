<#
.SYNOPSIS
    Reclaims disk space on the system drive in approved categories when free space is below a threshold.
.DESCRIPTION
    Removes system temp files and profile temp files older than N days, downloaded update content (with the update
    services stopped), the Delivery Optimization cache, error reporting queues, old CBS log archives, old IIS logs
    and the recycle bin for all users. Crash dumps, the previous Windows installation and hibernation are only
    touched when their switch is given. Never touches the Windows Installer folder or the component store directly.
    Locked files are skipped, not forced, and junctions are never followed. Without -Apply it runs in report-only
    mode and lists what would be removed and how much space that frees.
.NOTES
    RemoteHub: id=FIX-04; context=System; os=Both; risk=R2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [int]$FreeSpaceThresholdGB = 10,
    [switch]$IgnoreThreshold,
    [int]$AgeDays = 7,
    [int]$IisLogAgeDays = 30,
    [switch]$IncludeCrashDumps,
    [switch]$IncludePreviousWindows,
    [switch]$DisableHibernation,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if (-not $Apply) { $WhatIfPreference = $true }
$cutoff = (Get-Date).AddDays(-$AgeDays)
$results = New-Object System.Collections.Generic.List[object]
$failed = $false

function Get-OldFile([string]$Root, [datetime]$Before) {
    # Own walk instead of Get-ChildItem -Recurse, which follows junctions in Windows PowerShell 5.1.
    if (-not (Test-Path -LiteralPath $Root)) { return }
    $rootItem = Get-Item -LiteralPath $Root -Force
    if ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { return }
    $stack = New-Object System.Collections.Stack
    $stack.Push($rootItem)
    while ($stack.Count -gt 0) {
        $dir = $stack.Pop()
        try { $items = $dir.GetFileSystemInfos() } catch { continue }
        foreach ($i in $items) {
            if ($i.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            if ($i -is [IO.DirectoryInfo]) { $stack.Push($i) } elseif ($i.LastWriteTime -lt $Before) { $i }
        }
    }
}

function Clear-Category([string]$Category, [string[]]$Roots, [datetime]$Before) {
    $files = @(foreach ($r in $Roots) { Get-OldFile $r $Before })
    $bytes = [long]0; foreach ($f in $files) { $bytes += $f.Length }
    $removed = [long]0; $skipped = 0; $status = 'Nothing to remove'
    if ($files.Count -gt 0) {
        $status = 'Planned'
        if ($PSCmdlet.ShouldProcess($Category, ('Delete {0} file(s), {1:N0} MB' -f $files.Count, ($bytes / 1MB)))) {
            foreach ($f in $files) {
                try { Remove-Item -LiteralPath $f.FullName -Force; $removed += $f.Length } catch { $skipped++ }
            }
            $status = 'Done'
        }
    }
    $results.Add([pscustomobject]@{ Category = $Category; Files = $files.Count; SizeMB = [math]::Round($bytes / 1MB, 1); FreedMB = [math]::Round($removed / 1MB, 1); SkippedLocked = $skipped; Status = $status })
}

try {
    $sysDrive = Get-CimInstance -ClassName Win32_LogicalDisk -Filter ("DeviceID='{0}'" -f $env:SystemDrive)
    $freeBefore = [math]::Round($sysDrive.FreeSpace / 1GB, 2)
    if (-not $IgnoreThreshold -and $freeBefore -ge $FreeSpaceThresholdGB) {
        [pscustomobject]@{ Drive = $env:SystemDrive; FreeGB = $freeBefore; ThresholdGB = $FreeSpaceThresholdGB; Result = 'Above threshold, no change' }
        exit 0
    }

    $profiles = @(Get-CimInstance -ClassName Win32_UserProfile | Where-Object { -not $_.Special -and $_.LocalPath })
    Clear-Category 'System temp' @(Join-Path $env:windir 'Temp') $cutoff
    Clear-Category 'Profile temp' @($profiles | ForEach-Object { Join-Path $_.LocalPath 'AppData\Local\Temp' }) $cutoff
    Clear-Category 'Error reporting queues' @((Join-Path $env:ProgramData 'Microsoft\Windows\WER\ReportQueue'), (Join-Path $env:ProgramData 'Microsoft\Windows\WER\ReportArchive')) $cutoff
    Clear-Category 'CBS log archives' @(Join-Path $env:windir 'Logs\CBS') $cutoff
    Clear-Category 'IIS logs' @(Join-Path $env:SystemDrive 'inetpub\logs\LogFiles') ((Get-Date).AddDays(-$IisLogAgeDays))
    Clear-Category 'Recycle bin (all users)' @(Join-Path $env:SystemDrive '$Recycle.Bin') (Get-Date)
    if ($IncludeCrashDumps) { Clear-Category 'Crash dumps' @((Join-Path $env:windir 'Minidump'), (Join-Path $env:windir 'LiveKernelReports')) $cutoff }

    # Update content can only be deleted with the update services stopped; they are restarted on every path.
    $dl = Join-Path $env:windir 'SoftwareDistribution\Download'
    $stopped = @()
    try {
        if (@(Get-OldFile $dl (Get-Date)).Count -gt 0 -and -not $WhatIfPreference) {
            foreach ($s in 'wuauserv', 'BITS') { if ((Get-Service $s).Status -eq 'Running') { Stop-Service $s -Force; $stopped += $s } }
        }
        Clear-Category 'Downloaded update content' @($dl) (Get-Date)
    }
    finally { foreach ($s in $stopped) { Start-Service $s } }

    if (Get-Command -Name Delete-DeliveryOptimizationCache -ErrorAction SilentlyContinue) {
        $doPath = Join-Path $env:windir 'ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache'
        $doBytes = [long]0; foreach ($f in @(Get-OldFile $doPath (Get-Date))) { $doBytes += $f.Length }
        $status = 'Nothing to remove'
        if ($doBytes -gt 0) {
            $status = 'Planned'
            if ($PSCmdlet.ShouldProcess('Delivery Optimization cache', 'Delete-DeliveryOptimizationCache')) { Delete-DeliveryOptimizationCache -Force; $status = 'Done' }
        }
        $results.Add([pscustomobject]@{ Category = 'Delivery Optimization cache'; Files = '-'; SizeMB = [math]::Round($doBytes / 1MB, 1); FreedMB = '-'; SkippedLocked = 0; Status = $status })
    }

    $oldWin = Join-Path $env:SystemDrive 'Windows.old'
    if ($IncludePreviousWindows -and (Test-Path -LiteralPath $oldWin)) {
        # Windows.old is owned by TrustedInstaller; the supported removal is the Disk Cleanup handler.
        $handler = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\Previous Installations'
        $status = 'Planned'
        if ($PSCmdlet.ShouldProcess($oldWin, 'Remove previous Windows installation through cleanmgr /sagerun:4711')) {
            Set-ItemProperty -LiteralPath $handler -Name StateFlags4711 -Value 2 -Type DWord
            try {
                $p = Start-Process -FilePath (Join-Path $env:windir 'System32\cleanmgr.exe') -ArgumentList '/sagerun:4711' -PassThru -WindowStyle Hidden
                if (-not $p.WaitForExit(3600000)) { throw 'cleanmgr timed out.' }
            }
            finally { Remove-ItemProperty -LiteralPath $handler -Name StateFlags4711 -ErrorAction SilentlyContinue }
            $status = 'Done'; if (Test-Path -LiteralPath $oldWin) { $status = 'Failed: Windows.old still present'; $failed = $true }
        }
        $results.Add([pscustomobject]@{ Category = 'Previous Windows installation'; Files = '-'; SizeMB = '-'; FreedMB = '-'; SkippedLocked = 0; Status = $status })
    }

    if ($DisableHibernation) {
        $power = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' -ErrorAction SilentlyContinue
        $hiberOn = $power -and $power.PSObject.Properties['HibernateEnabled'] -and $power.HibernateEnabled -eq 1
        $status = 'Already off'
        if ($hiberOn) {
            $status = 'Planned (also disables Fast Startup)'
            if ($PSCmdlet.ShouldProcess('Hibernation', 'powercfg /hibernate off')) {
                $p = Start-Process -FilePath (Join-Path $env:windir 'System32\powercfg.exe') -ArgumentList '/hibernate off' -PassThru -Wait -WindowStyle Hidden
                if ($p.ExitCode -eq 0) { $status = 'Done' } else { $status = "Failed: powercfg exit $($p.ExitCode)"; $failed = $true }
            }
        }
        $results.Add([pscustomobject]@{ Category = 'Hibernation file'; Files = '-'; SizeMB = '-'; FreedMB = '-'; SkippedLocked = 0; Status = $status })
    }

    $results
    $freeAfter = [math]::Round((Get-CimInstance -ClassName Win32_LogicalDisk -Filter ("DeviceID='{0}'" -f $env:SystemDrive)).FreeSpace / 1GB, 2)
    Write-Output ('Free space on {0}: {1} GB before, {2} GB after.' -f $env:SystemDrive, $freeBefore, $freeAfter)
    if (-not $Apply) { Write-Output 'Dry run: nothing changed. Run with -Apply to make the changes.' }
    if ($failed) { exit 1 }
    exit 0
}
catch {
    $results
    Write-Output "FIX-04 failed: $($_.Exception.Message)"
    exit 1
}
