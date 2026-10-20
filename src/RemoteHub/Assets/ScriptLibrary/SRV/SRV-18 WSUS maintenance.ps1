<#
.SYNOPSIS
    Reports WSUS server state and, in Maintain mode, declines superseded/expired/unmanaged updates and runs cleanup.
.DESCRIPTION
    Reports last synchronization, selected products and classifications, computer groups, clients that have
    not reported in N days, database and content size and the WsusPool memory limit. Maintain mode declines
    superseded and expired updates and updates for product lines you do not manage, then runs the server
    cleanup. Cleanup is long running and I/O heavy (run off peak), and deleting content forces re-download.
    WSUS is deprecated (no new features since 2024); check its status before building new dependencies on it.
.NOTES
    RemoteHub: id=SRV-18; context=System; os=Server; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Report', 'Maintain')]
    [string]$Mode = 'Report',
    [int]$StaleClientDays = 30,
    [bool]$DeclineSuperseded = $true,
    [bool]$DeclineExpired = $true,
    # Product titles to decline completely, e.g. 'Windows 7', 'Office 2010'.
    [string[]]$DeclineProduct = @(),
    [bool]$RunCleanup = $true
)

$ErrorActionPreference = 'Stop'

$log = @()
$exitCode = 0
try {
    if (-not (Get-Module -ListAvailable -Name UpdateServices)) { Write-Output 'Data unavailable: the UpdateServices module is not installed.'; exit 1 }
    Import-Module UpdateServices -Verbose:$false
    $wsus = Get-WsusServer
    $sub = $wsus.GetSubscription()
    $sync = $sub.GetLastSynchronizationInfo()
    $cfg = $wsus.GetConfiguration()

    $contentGB = 'Data unavailable'
    if ($cfg.LocalContentCachePath -and (Test-Path -LiteralPath $cfg.LocalContentCachePath)) {
        $contentGB = [Math]::Round((Get-ChildItem -LiteralPath $cfg.LocalContentCachePath -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum / 1GB, 1)
    }
    $dbGB = 'Data unavailable'
    $wid = Join-Path $env:SystemRoot 'WID\Data\SUSDB.mdf'
    if (Test-Path -LiteralPath $wid) { $dbGB = [Math]::Round((Get-Item -LiteralPath $wid).Length / 1GB, 1) }

    $poolMem = 'Data unavailable'
    if (Get-Module -ListAvailable -Name WebAdministration) {
        try {
            Import-Module WebAdministration -Verbose:$false
            $raw = Get-ItemProperty -LiteralPath 'IIS:\AppPools\WsusPool' -Name recycling.periodicRestart.privateMemory
            $pm = $raw
            if ($null -ne $raw.PSObject.Properties['Value']) { $pm = $raw.Value }
            $pm = [int64]$pm
            $poolMem = "$([int]($pm / 1KB)) MB"
            if ($pm -gt 0 -and $pm -lt 4GB / 1KB) { $log += 'WsusPool private memory limit is below 4 GB; a common cause of WSUS failures.' }
        } catch { Write-Verbose 'WsusPool settings not readable.' }
    }

    $stale = @(Get-WsusComputer -ToLastReportedStatusTime (Get-Date).AddDays(-$StaleClientDays) -ErrorAction SilentlyContinue)
    $groups = @($wsus.GetComputerTargetGroups() | ForEach-Object { "$($_.Name)=$(@($_.GetComputerTargets()).Count)" })

    [pscustomobject]@{
        Server = $env:COMPUTERNAME
        LastSync = $sync.StartTime; LastSyncResult = [string]$sync.Result
        Products = (@($sub.GetUpdateCategories() | ForEach-Object { $_.Title }) -join ', ')
        Classifications = (@($sub.GetUpdateClassifications() | ForEach-Object { $_.Title }) -join ', ')
        ComputerGroups = ($groups -join ', ')
        StaleClients = $stale.Count; StaleThresholdDays = $StaleClientDays
        ContentGB = $contentGB; WidDatabaseGB = $dbGB; WsusPoolPrivateMemory = $poolMem
        DatabaseIndexHealth = 'Data unavailable (check with SQL maintenance tools)'
    } | Format-List | Out-String -Width 250
    if ([string]$sync.Result -ne 'Succeeded') { $exitCode = 1 }

    if ($Mode -eq 'Maintain') {
        $scope = New-Object Microsoft.UpdateServices.Administration.UpdateScope
        $scope.ApprovedStates = [Microsoft.UpdateServices.Administration.ApprovedStates]::Any
        $candidates = @($wsus.GetUpdates($scope) | Where-Object { -not $_.IsDeclined })
        $toDecline = @($candidates | Where-Object {
            ($DeclineSuperseded -and $_.IsSuperseded) -or
            ($DeclineExpired -and [string]$_.PublicationState -eq 'Expired') -or
            (@($_.ProductTitles | Where-Object { $DeclineProduct -contains $_ }).Count -gt 0)
        })
        if ($toDecline.Count -eq 0) { $log += 'No change: no updates to decline.' }
        elseif ($PSCmdlet.ShouldProcess("$($toDecline.Count) updates", 'Decline superseded, expired and unmanaged-product updates')) {
            $n = 0
            foreach ($u in $toDecline) { $u.Decline(); $n++ }
            $log += "Declined $n updates."
        } else { $log += "Planned: decline $($toDecline.Count) updates." }

        if ($RunCleanup) {
            Write-Warning 'Cleanup is long running and I/O heavy; deleted content is downloaded again if needed later.'
            if ($PSCmdlet.ShouldProcess($env:COMPUTERNAME, 'Invoke-WsusServerCleanup (obsolete updates, unneeded content, compress revisions)')) {
                $r = Invoke-WsusServerCleanup -CleanupObsoleteUpdates -CleanupUnneededContentFiles -CompressUpdates -Confirm:$false
                $log += @($r | ForEach-Object { "Cleanup: $_" })
            } else { $log += 'Planned: server cleanup.' }
        }
    }
} catch {
    $log += "Failed: $($_.Exception.Message)"
    $exitCode = 1
}
if ($log.Count -gt 0) { $log | ForEach-Object { $_ } }
exit $exitCode
