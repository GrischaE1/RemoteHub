<#
.SYNOPSIS
    Reports Windows Update status: last scan, last install, pending updates and the update source.
.DESCRIPTION
    Uses the Windows Update Agent COM API for history and pending updates, the Auto Update\Results
    registry values and the WindowsUpdate policy key, with Get-HotFix only as a secondary source
    because it does not list every update. The pending search runs in a background job with a
    timeout because an unreachable WSUS server can hang (0x8024402C). It never installs anything.
.NOTES
    RemoteHub: id=INV-04; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [int]$SearchTimeoutSeconds = 180,
    [switch]$SkipPendingSearch
)

$ErrorActionPreference = 'Stop'

function Get-RegValue([string]$Path, [string]$Name) {
    try { return (Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop).$Name } catch { return $null }
}

try {
    $wuPolicy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
    $policy = [ordered]@{}
    foreach ($p in @($wuPolicy, "$wuPolicy\AU")) {
        if (Test-Path -LiteralPath $p) {
            $props = Get-ItemProperty -LiteralPath $p
            foreach ($n in $props.PSObject.Properties.Name) {
                if ($n -notlike 'PS*') { $policy["$(Split-Path $p -Leaf)\$n"] = $props.$n }
            }
        }
    }

    $source = 'Windows Update'
    if (Test-Path 'HKLM:\SOFTWARE\Microsoft\CCM') { $source = 'Configuration Manager' }
    elseif ((Get-RegValue "$wuPolicy\AU" 'UseWUServer') -eq 1 -and (Get-RegValue $wuPolicy 'WUServer')) { $source = 'WSUS' }
    elseif ($null -ne (Get-RegValue $wuPolicy 'DeferQualityUpdatesPeriodInDays') -or $null -ne (Get-RegValue $wuPolicy 'DeferFeatureUpdatesPeriodInDays')) { $source = 'Windows Update for Business' }

    $results = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results'
    $lastScanReg    = Get-RegValue "$results\Detect" 'LastSuccessTime'
    $lastInstallReg = Get-RegValue "$results\Install" 'LastSuccessTime'

    $lastInstall = $null
    $lastKb = 'Data unavailable'
    try {
        $session  = New-Object -ComObject Microsoft.Update.Session
        $searcher = $session.CreateUpdateSearcher()
        $count = $searcher.GetTotalHistoryCount()
        if ($count -gt 0) {
            # ResultCode 2 = succeeded; Operation 1 = installation.
            $hist = @($searcher.QueryHistory(0, [math]::Min($count, 200)) | Where-Object { $_.Operation -eq 1 -and $_.ResultCode -eq 2 } | Sort-Object Date -Descending)
            if ($hist.Count -gt 0) {
                $lastInstall = $hist[0].Date
                $kb = [regex]::Match($hist[0].Title, 'KB\d+')
                if ($kb.Success) { $lastKb = $kb.Value } else { $lastKb = $hist[0].Title }
            }
        }
    } catch {
        Write-Verbose "Update history via COM failed (blocked in Constrained Language Mode?): $($_.Exception.Message)"
        $hf = Get-HotFix -ErrorAction SilentlyContinue | Where-Object { $_.InstalledOn } | Sort-Object InstalledOn -Descending | Select-Object -First 1
        if ($hf) { $lastInstall = $hf.InstalledOn; $lastKb = $hf.HotFixID }
    }
    if (-not $lastInstall -and $lastInstallReg) { $lastInstall = [datetime]$lastInstallReg }

    $pending = 'Data unavailable'
    $categories = @()
    if (-not $SkipPendingSearch) {
        $job = Start-Job -ScriptBlock {
            $s = New-Object -ComObject Microsoft.Update.Session
            $r = $s.CreateUpdateSearcher().Search("IsInstalled=0 and IsHidden=0 and Type='Software'")
            foreach ($u in $r.Updates) {
                $cat = @($u.Categories | ForEach-Object { $_.Name }) -join ', '
                [PSCustomObject]@{ Title = $u.Title; Categories = $cat }
            }
        }
        if (Wait-Job -Job $job -Timeout $SearchTimeoutSeconds) {
            try {
                $found = @(Receive-Job -Job $job -ErrorAction Stop)
                $pending = $found.Count
                $categories = @($found | ForEach-Object { $_.Categories -split ', ' } | Where-Object { $_ } | Group-Object | ForEach-Object { "$($_.Name) ($($_.Count))" })
            } catch {
                Write-Verbose "Pending search failed: $($_.Exception.Message)"
            }
        } else {
            Write-Verbose "Pending search did not finish within $SearchTimeoutSeconds seconds."
            Stop-Job -Job $job
        }
        Remove-Job -Job $job -Force
    }

    $daysSince = 'Data unavailable'
    if ($lastInstall) { $daysSince = [int]((Get-Date) - [datetime]$lastInstall).TotalDays }

    [PSCustomObject]@{
        UpdateSource           = $source
        LastSuccessfulScan     = if ($lastScanReg) { [string]$lastScanReg } else { 'Data unavailable' }
        LastSuccessfulInstall  = if ($lastInstall) { ([datetime]$lastInstall).ToString('yyyy-MM-dd HH:mm') } else { 'Data unavailable' }
        DaysSinceLastInstall   = $daysSince
        LastInstalledUpdate    = $lastKb
        PendingUpdateCount     = $pending
        PendingCategories      = $categories
        UpdatePolicy           = [PSCustomObject]$policy
    } | ConvertTo-Json -Depth 5
    exit 0
} catch {
    Write-Error "Update status failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
