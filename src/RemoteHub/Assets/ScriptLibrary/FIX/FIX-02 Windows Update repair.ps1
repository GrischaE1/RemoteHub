<#
.SYNOPSIS
    Diagnoses and repairs the Windows Update client in escalating stages.
.DESCRIPTION
    Stage 1 diagnoses recent update errors, the last successful scan, the update source and its reachability,
    the WinHTTP proxy and WSUS/MDM policy conflicts. Stage 2 restarts the update services and triggers a scan.
    Stage 3 (only with -MaxStage 3, and only when stage 2 did not fix the scan) stops the services, renames (never
    deletes) SoftwareDistribution and catroot2, runs component store repair, restarts the services and rescans.
    Renaming resets the update history and forces a re-download. Policy from MDM or WSUS can be the real cause.
    The run stops after any failed stage. Without -Apply only the diagnosis and the plan are shown.
.NOTES
    RemoteHub: id=FIX-02; context=System; os=Both; risk=R2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet(1, 2, 3)]
    [int]$MaxStage = 2,
    [int]$ErrorLookbackDays = 14,
    [int]$DismTimeoutMinutes = 90,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if (-not $Apply) { $WhatIfPreference = $true }
$logDir = Join-Path $env:ProgramData 'RemoteHub\ScriptLogs'
$report = New-Object System.Collections.Generic.List[object]
function Add-Result([int]$Stage, [string]$Step, [string]$Result, [string]$Detail) {
    $report.Add([pscustomobject]@{ Stage = $Stage; Step = $Step; Result = $Result; Detail = $Detail })
}

$knownCodes = @{
    '0x8024402C' = 'Proxy or name resolution failure'; '0x80244022' = 'Update server returned 503'
    '0x8024401C' = 'Request to update server timed out'; '0x80072EE2' = 'Connection timed out'
    '0x80072EFD' = 'Cannot connect to the update server'; '0x8024002E' = 'Windows Update access disabled by policy'
    '0x80240438' = 'No route to the update service'; '0x80070422' = 'Update service disabled'
    '0x8024001E' = 'Service or system shutting down'; '0x80240022' = 'All updates in the operation failed'
}

function Invoke-Scan {
    # The WUA COM search is the same scan the client runs; its HRESULT is the error code we map.
    try {
        $searcher = (New-Object -ComObject Microsoft.Update.Session).CreateUpdateSearcher()
        $r = $searcher.Search("IsInstalled=0 and Type='Software'")
        return [pscustomobject]@{ Ok = $true; Code = '0x00000000'; Detail = "$($r.Updates.Count) applicable update(s)" }
    }
    catch {
        $code = '0x{0:X8}' -f $_.Exception.HResult
        $text = $knownCodes[$code]; if (-not $text) { $text = 'Unmapped error code' }
        return [pscustomobject]@{ Ok = $false; Code = $code; Detail = $text }
    }
}

function Test-Tcp([string]$HostName, [int]$Port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try { return $client.ConnectAsync($HostName, $Port).Wait(5000) } catch { return $false } finally { $client.Dispose() }
}

$updateServices = @('wuauserv', 'BITS', 'CryptSvc')
try {
    if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { throw 'Run this script in 64-bit PowerShell.' }

    # Stage 1: diagnosis (read-only).
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WindowsUpdateClient'; Level = 2; StartTime = (Get-Date).AddDays(-$ErrorLookbackDays) } -MaxEvents 5 -ErrorAction SilentlyContinue)
    Add-Result 1 'Recent update errors' "$($events.Count)" (($events | ForEach-Object { '{0:yyyy-MM-dd} id {1}' -f $_.TimeCreated, $_.Id }) -join '; ')
    try {
        $au = (New-Object -ComObject Microsoft.Update.AutoUpdate).Results
        Add-Result 1 'Last successful scan' "$($au.LastSearchSuccessDate)" "Last install: $($au.LastInstallationSuccessDate)"
    }
    catch { Add-Result 1 'Last successful scan' 'Data unavailable' $_.Exception.Message }

    $wuPol = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' -ErrorAction SilentlyContinue
    $auPol = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' -ErrorAction SilentlyContinue
    $mdmPol = Get-Item -LiteralPath 'HKLM:\SOFTWARE\Microsoft\PolicyManager\current\device\Update' -ErrorAction SilentlyContinue
    $useWsus = $auPol -and $auPol.PSObject.Properties['UseWUServer'] -and $auPol.UseWUServer -eq 1 -and $wuPol.PSObject.Properties['WUServer']
    if ($useWsus) {
        $uri = [Uri]$wuPol.WUServer
        Add-Result 1 'Update source' 'WSUS' "$($uri.Host):$($uri.Port) reachable=$(Test-Tcp $uri.Host $uri.Port)"
    }
    else {
        Add-Result 1 'Update source' 'Windows Update' "sls.update.microsoft.com:443 reachable=$(Test-Tcp 'sls.update.microsoft.com' 443)"
    }
    if ($useWsus -and $mdmPol -and $mdmPol.ValueCount -gt 0) { Add-Result 1 'Policy conflict' 'Warning' 'WSUS Group Policy and MDM update policy are both present; fix the policy, not the machine.' }
    if ($wuPol -and $wuPol.PSObject.Properties['DoNotConnectToWindowsUpdateInternetLocations'] -and $wuPol.DoNotConnectToWindowsUpdateInternetLocations -eq 1) { Add-Result 1 'Policy' 'Info' 'Internet update locations blocked by policy.' }

    # WinHttpSettings: flags at offset 8 (1 = direct), proxy string length at 12, proxy string from 16.
    $wh = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Connections' -ErrorAction SilentlyContinue)
    $proxy = 'Direct'
    if ($wh -and $wh.PSObject.Properties['WinHttpSettings']) {
        $b = [byte[]]$wh.WinHttpSettings
        if ($b.Length -ge 16 -and ($b[8] -band 2)) { $len = [BitConverter]::ToInt32($b, 12); $proxy = [Text.Encoding]::ASCII.GetString($b, 16, $len) }
    }
    Add-Result 1 'WinHTTP proxy' $proxy 'Not changed by this script.'

    $exitCode = 0
    if ($MaxStage -ge 2) {
        $fixed = $false
        if ($PSCmdlet.ShouldProcess(($updateServices -join ', '), 'Stage 2: restart update services and trigger a scan')) {
            try { foreach ($s in $updateServices) { Restart-Service -Name $s -Force } ; Add-Result 2 'Restart services' 'OK' '' }
            catch { Add-Result 2 'Restart services' 'Failed' $_.Exception.Message; throw 'Stage 2 failed; stopping.' }
            $scan = Invoke-Scan
            Add-Result 2 'Scan' $scan.Code $scan.Detail
            $fixed = $scan.Ok
        }
        if ($MaxStage -ge 3 -and -not $fixed) {
            $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
            $folders = @((Join-Path $env:windir 'SoftwareDistribution'), (Join-Path $env:windir 'System32\catroot2'))
            if ($PSCmdlet.ShouldProcess(($folders -join ', '), "Stage 3: stop services, rename folders to *.bak-$stamp, run DISM RestoreHealth, rescan")) {
                foreach ($s in $updateServices) { Stop-Service -Name $s -Force; (Get-Service -Name $s).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) }
                if ((Get-Service -Name CryptSvc).Status -ne 'Stopped') { throw 'Cryptographic Services is still running; catroot2 not touched.' }
                if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
                $undo = foreach ($f in $folders) {
                    if (Test-Path -LiteralPath $f) {
                        $newName = (Split-Path -Leaf $f) + ".bak-$stamp"
                        Rename-Item -LiteralPath $f -NewName $newName
                        [pscustomobject]@{ Original = $f; RenamedTo = (Join-Path (Split-Path -Parent $f) $newName) }
                    }
                }
                $undoFile = Join-Path $logDir "FIX-02-undo-$stamp.json"
                ConvertTo-Json -InputObject @($undo) -Depth 3 | Set-Content -LiteralPath $undoFile -Encoding UTF8
                Add-Result 3 'Rename folders' 'OK' "Undo information: $undoFile"
                foreach ($s in $updateServices) { Start-Service -Name $s }
                $psi = New-Object System.Diagnostics.ProcessStartInfo (Join-Path $env:windir 'System32\dism.exe'), '/Online /Cleanup-Image /RestoreHealth /NoRestart /English'
                $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
                $p = [System.Diagnostics.Process]::Start($psi)
                if (-not $p.WaitForExit($DismTimeoutMinutes * 60000)) { throw 'DISM RestoreHealth timed out; stopping.' }
                Add-Result 3 'DISM RestoreHealth' "$($p.ExitCode)" ''
                if ($p.ExitCode -eq 3010) { $exitCode = 3010 } elseif ($p.ExitCode -ne 0) { throw "DISM failed with exit $($p.ExitCode); stopping." }
                $scan = Invoke-Scan
                Add-Result 3 'Rescan' $scan.Code $scan.Detail
                if (-not $scan.Ok) { throw 'Scan still fails after stage 3.' }
            }
        }
        elseif ($MaxStage -lt 3 -and -not $fixed -and -not $WhatIfPreference) { $exitCode = 1 }
    }

    $rebootKeys = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending')
    if ($exitCode -eq 0 -and @($rebootKeys | Where-Object { Test-Path -LiteralPath $_ }).Count -gt 0) { $exitCode = 3010 }
    $report
    if ($exitCode -eq 3010) { Write-Output 'A restart is required to finish.' }
    if (-not $Apply) { Write-Output 'Dry run: nothing changed. Run with -Apply to make the changes.' }
    exit $exitCode
}
catch {
    $report
    Write-Output "FIX-02 failed: $($_.Exception.Message)"
    exit 1
}
