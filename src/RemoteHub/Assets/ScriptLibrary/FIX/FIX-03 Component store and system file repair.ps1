<#
.SYNOPSIS
    Checks and repairs the Windows component store and protected system files.
.DESCRIPTION
    Checks component store health (check, scan, restore), runs the system file checker and reads the CBS log for
    files that could not be repaired. Can analyse the component store size and run a standard component cleanup.
    Servicing can take a long time, so schedule it with a generous timeout. WSUS clients may need the component
    repair policy (or -Source) to reach a repair source. The irreversible ResetBase cleanup is never offered.
    Exits 3010 when a restart is needed.
.NOTES
    RemoteHub: id=FIX-03; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [bool]$ScanHealth = $true,
    [bool]$RunSfc = $true,
    [string]$Source = '',
    [switch]$AnalyzeComponentStore,
    [switch]$StartComponentCleanup,
    [int]$TimeoutMinutes = 120
)

$ErrorActionPreference = 'Stop'
$results = New-Object System.Collections.Generic.List[object]
function Add-Result([string]$Step, [string]$Result, [string]$Detail) {
    $results.Add([pscustomobject]@{ Step = $Step; Result = $Result; Detail = $Detail })
}

function Invoke-Native([string]$FilePath, [string]$Arguments, [int]$TimeoutSec) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath; $psi.Arguments = $Arguments; $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { try { $p.Kill() } catch { }; throw "$FilePath timed out after $TimeoutSec seconds." }
    $p.WaitForExit()
    [pscustomobject]@{ ExitCode = $p.ExitCode; StdOut = $out.Result; StdErr = $err.Result }
}

$restart = $false
try {
    if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { throw 'Run this script in 64-bit PowerShell.' }
    if (-not (Get-Module -ListAvailable -Name Dism)) { throw 'The DISM module is not available (Data unavailable).' }
    Import-Module Dism

    $health = (Repair-WindowsImage -Online -CheckHealth).ImageHealthState
    Add-Result 'CheckHealth' "$health" ''
    if ($ScanHealth -and "$health" -eq 'Healthy') {
        $health = (Repair-WindowsImage -Online -ScanHealth).ImageHealthState
        Add-Result 'ScanHealth' "$health" ''
    }
    if ("$health" -eq 'NonRepairable') { throw 'Component store is not repairable; an in-place upgrade or rebuild is needed.' }
    if ("$health" -eq 'Repairable') {
        if ($PSCmdlet.ShouldProcess('Component store', 'Repair-WindowsImage -RestoreHealth')) {
            $repairArgs = @{ Online = $true; RestoreHealth = $true; NoRestart = $true }
            if ($Source) { $repairArgs['Source'] = $Source; $repairArgs['LimitAccess'] = $true }
            $r = Repair-WindowsImage @repairArgs
            Add-Result 'RestoreHealth' "$($r.ImageHealthState)" ''
            if ($r.RestartNeeded) { $restart = $true }
            if ("$($r.ImageHealthState)" -ne 'Healthy') { throw 'RestoreHealth did not return a healthy store; stopping before sfc.' }
        }
    }

    if ($RunSfc) {
        if ($PSCmdlet.ShouldProcess('Protected system files', 'sfc /scannow')) {
            $started = Get-Date
            $sfc = Invoke-Native (Join-Path $env:windir 'System32\sfc.exe') '/scannow' ($TimeoutMinutes * 60)
            # sfc writes UTF-16 when redirected and its text is localised, so the CBS log (always English) is the evidence.
            $cbs = Join-Path $env:windir 'Logs\CBS\CBS.log'
            $repaired = 0; $unrepaired = New-Object System.Collections.Generic.List[string]
            if (Test-Path -LiteralPath $cbs) {
                # TrustedInstaller keeps CBS.log open, so read it with shared access.
                $fs = New-Object IO.FileStream($cbs, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                try { $lines = (New-Object IO.StreamReader($fs)).ReadToEnd() -split "`r?`n" } finally { $fs.Dispose() }
                foreach ($line in $lines) {
                    if ($line.Length -lt 19) { continue }
                    $ts = [datetime]::MinValue
                    if (-not [datetime]::TryParse($line.Substring(0, 19), [ref]$ts) -or $ts -lt $started.AddMinutes(-1)) { continue }
                    if ($line -match '\[SR\] Cannot repair member file \[l:\d+\]"([^"]+)"') { $unrepaired.Add($Matches[1]) }
                    elseif ($line -match '\[SR\] Repairing corrupted file') { $repaired++ }
                }
            }
            $detail = "Repaired: $repaired; unrepaired: $($unrepaired.Count)"
            if ($unrepaired.Count -gt 0) { $detail += ' (' + (($unrepaired | Select-Object -Unique -First 10) -join ', ') + ')' }
            Add-Result 'sfc /scannow' "Exit $($sfc.ExitCode)" $detail
            if ($repaired -gt 0) { $restart = $true }
            if ($unrepaired.Count -gt 0) { throw 'System file checker found files it could not repair.' }
        }
    }

    if ($AnalyzeComponentStore) {
        $a = Invoke-Native (Join-Path $env:windir 'System32\dism.exe') '/Online /Cleanup-Image /AnalyzeComponentStore /English' ($TimeoutMinutes * 60)
        $size = ($a.StdOut -split "`r?`n" | Where-Object { $_ -match 'Actual Size of Component Store|Cleanup Recommended' }) -join '; '
        if (-not $size) { $size = 'Data unavailable' }
        Add-Result 'AnalyzeComponentStore' "Exit $($a.ExitCode)" $size
    }
    if ($StartComponentCleanup) {
        if ($PSCmdlet.ShouldProcess('Component store', 'Standard component cleanup (no ResetBase)')) {
            $null = Repair-WindowsImage -Online -StartComponentCleanup -NoRestart
            Add-Result 'StartComponentCleanup' 'OK' ''
        }
    }

    if (Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') { $restart = $true }
    $results
    if ($restart) { Write-Output 'A restart is required to finish.'; exit 3010 }
    exit 0
}
catch {
    $results
    Write-Output "FIX-03 failed: $($_.Exception.Message)"
    exit 1
}
