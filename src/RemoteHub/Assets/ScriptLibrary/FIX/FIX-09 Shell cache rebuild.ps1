<#
.SYNOPSIS
    Rebuilds the signed-in user's icon and thumbnail caches by restarting Explorer.
.DESCRIPTION
    Stops Explorer in the user's own session, deletes the icon and thumbnail cache databases, starts Explorer again
    and confirms it is back. With -ReregisterShellPackages it also re-registers the Start menu and shell experience
    packages for the user (see FIX-18). Restarting Explorer closes File Explorer windows and can interrupt copy
    operations, so run it only while the user is present or at sign-in. The cache files are rebuilt by Windows,
    so no rollback copy is kept.
.NOTES
    RemoteHub: id=FIX-09; context=User; os=Client; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$ReregisterShellPackages,
    [int]$RestartTimeoutSec = 30
)

$ErrorActionPreference = 'Stop'
$results = New-Object System.Collections.Generic.List[object]
function Add-Row([string]$Step, [string]$Result) { $results.Add([pscustomobject]@{ Step = $Step; Result = $Result }) }

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSec) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) { if (& $Condition) { return $true }; Start-Sleep -Milliseconds 250 }
    return $false
}

try {
    if ([Security.Principal.WindowsIdentity]::GetCurrent().IsSystem) { Write-Output 'Data unavailable: run this script in the signed-in user context.'; exit 1 }
    $session = (Get-Process -Id $PID).SessionId
    $explorerDir = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Explorer'
    $getCache = {
        $files = @(Get-ChildItem -LiteralPath $explorerDir -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'iconcache_*.db' -or $_.Name -like 'thumbcache_*.db' })
        $legacy = Join-Path $env:LOCALAPPDATA 'IconCache.db'
        if (Test-Path -LiteralPath $legacy) { $files += Get-Item -LiteralPath $legacy -Force }
        $files
    }
    $cache = @(& $getCache)
    $bytes = [long]0; foreach ($f in $cache) { $bytes += $f.Length }
    Add-Row 'Cache files found' ('{0} file(s), {1:N1} MB' -f $cache.Count, ($bytes / 1MB))

    if ($cache.Count -gt 0 -and $PSCmdlet.ShouldProcess('Explorer (this session)', 'Stop Explorer, delete icon and thumbnail caches, start Explorer')) {
        $explorer = @(Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })
        if ($explorer.Count -eq 0) { Write-Verbose 'Explorer is not running in this session; caches are deleted without a restart.' }
        $explorer | Stop-Process -Force
        $null = Wait-Until { @(Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session -and $explorer.Id -contains $_.Id }).Count -eq 0 } 10
        $deleted = 0; $locked = 0
        foreach ($f in @(& $getCache)) {
            try { Remove-Item -LiteralPath $f.FullName -Force; $deleted++ } catch { $locked++ }
        }
        Add-Row 'Cache files deleted' "$deleted deleted, $locked in use (skipped)"

        # Winlogon restarts the shell on its own when AutoRestartShell is set; only start it when that did not happen.
        $back = Wait-Until { @(Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session }).Count -gt 0 } 5
        if (-not $back) { Start-Process -FilePath (Join-Path $env:windir 'explorer.exe') }
        $back = Wait-Until { @(Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session }).Count -gt 0 } $RestartTimeoutSec
        if (-not $back) { throw "Explorer did not come back within $RestartTimeoutSec seconds. Start it with Ctrl+Shift+Esc > Run new task > explorer." }
        Add-Row 'Explorer restart' 'Confirmed running'
    }

    if ($ReregisterShellPackages) {
        foreach ($name in 'Microsoft.Windows.StartMenuExperienceHost', 'Microsoft.Windows.ShellExperienceHost') {
            $pkg = Get-AppxPackage -Name $name | Select-Object -First 1
            if (-not $pkg) { Add-Row "Re-register $name" 'Not installed'; continue }
            $manifest = Join-Path $pkg.InstallLocation 'AppxManifest.xml'
            if ($PSCmdlet.ShouldProcess($name, 'Re-register for the current user from its installed manifest')) {
                Add-AppxPackage -DisableDevelopmentMode -Register $manifest
                Add-Row "Re-register $name" 'Done'
            }
        }
    }

    $results
    exit 0
}
catch {
    $results
    Write-Output "FIX-09 failed: $($_.Exception.Message)"
    exit 1
}
