<#
.SYNOPSIS
    Forces a rebuild of the Windows Search index and reports the index state.
.DESCRIPTION
    Stops the Windows Search service, clears the setup-completed marker under HKLM\SOFTWARE\Microsoft\Windows Search
    so the index is rebuilt from scratch, starts the service again and reports the service state and index
    database size. A rebuild is CPU and disk heavy and can run for hours, so schedule it off hours; search in
    Outlook depends on this index. A second run within -SkipIfRebuiltWithinHours reports no change instead of
    restarting the rebuild. On servers the Windows Search service feature must be installed.
.NOTES
    RemoteHub: id=FIX-10; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [int]$SkipIfRebuiltWithinHours = 24,
    [int]$ServiceTimeoutSec = 120
)

$ErrorActionPreference = 'Stop'
$searchKey = 'HKLM:\SOFTWARE\Microsoft\Windows Search'
$logDir = Join-Path $env:ProgramData 'RemoteHub\ScriptLogs'
$marker = Join-Path $logDir 'FIX-10-last-rebuild.txt'

function Get-IndexState {
    $svc = Get-Service -Name WSearch
    $reg = Get-ItemProperty -LiteralPath $searchKey
    $dataDir = Join-Path $env:ProgramData 'Microsoft\Search\Data'
    if ($reg.PSObject.Properties['DataDirectory'] -and $reg.DataDirectory) { $dataDir = [Environment]::ExpandEnvironmentVariables($reg.DataDirectory) }
    $db = @(Get-ChildItem -LiteralPath (Join-Path $dataDir 'Applications\Windows') -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Windows.edb' -or $_.Name -like 'Windows*.db' })
    $size = 'Data unavailable'
    if ($db.Count -gt 0) { $sum = [long]0; foreach ($f in $db) { $sum += $f.Length }; $size = [math]::Round($sum / 1MB, 1) }
    $setup = 'Data unavailable'
    if ($reg.PSObject.Properties['SetupCompletedSuccessfully']) { $setup = $reg.SetupCompletedSuccessfully }
    [pscustomobject]@{ Service = "$($svc.Status)"; StartType = "$($svc.StartType)"; SetupCompleted = $setup; IndexSizeMB = $size }
}

try {
    if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { throw 'Run this script in 64-bit PowerShell (the Windows Search key is redirected for 32-bit hosts).' }
    if (-not (Get-Service -Name WSearch -ErrorAction SilentlyContinue)) { Write-Output 'Windows Search service is not installed: nothing to rebuild.'; exit 0 }
    $before = Get-IndexState
    if ($before.StartType -eq 'Disabled') { $before; Write-Output 'Windows Search is disabled; not changed (it may be disabled on purpose).'; exit 1 }

    if (Test-Path -LiteralPath $marker) {
        $last = [datetime]::ParseExact((Get-Content -LiteralPath $marker -TotalCount 1), 'o', [Globalization.CultureInfo]::InvariantCulture)
        if ($last -gt (Get-Date).AddHours(-$SkipIfRebuiltWithinHours)) {
            $before
            Write-Output ('No change: a rebuild was started at {0:yyyy-MM-dd HH:mm}; it can take hours to finish.' -f $last)
            exit 0
        }
    }

    if ($PSCmdlet.ShouldProcess('Windows Search index', 'Stop service, clear SetupCompletedSuccessfully, start service (full rebuild)')) {
        if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
        Stop-Service -Name WSearch -Force
        (Get-Service -Name WSearch).WaitForStatus('Stopped', [TimeSpan]::FromSeconds($ServiceTimeoutSec))
        try {
            # With the marker at 0 the indexer discards its catalog and builds a new one on the next start.
            Set-ItemProperty -LiteralPath $searchKey -Name SetupCompletedSuccessfully -Value 0 -Type DWord
        }
        finally {
            Start-Service -Name WSearch
            (Get-Service -Name WSearch).WaitForStatus('Running', [TimeSpan]::FromSeconds($ServiceTimeoutSec))
        }
        Set-Content -LiteralPath $marker -Value (Get-Date).ToString('o') -Encoding ASCII
        $after = Get-IndexState
        [pscustomobject]@{ When = 'Before'; Service = $before.Service; SetupCompleted = $before.SetupCompleted; IndexSizeMB = $before.IndexSizeMB }
        [pscustomobject]@{ When = 'After'; Service = $after.Service; SetupCompleted = $after.SetupCompleted; IndexSizeMB = $after.IndexSizeMB }
        Write-Output 'Rebuild started. The index fills in the background; run again later to see the size grow.'
    }
    else { $before }
    exit 0
}
catch {
    Write-Output "FIX-10 failed: $($_.Exception.Message)"
    exit 1
}
