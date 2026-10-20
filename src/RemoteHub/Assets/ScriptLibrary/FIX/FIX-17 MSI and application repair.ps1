<#
.SYNOPSIS
    Repairs (full file reinstall) or silently uninstalls a per-machine MSI product by product code.
.DESCRIPTION
    Detects the product in the uninstall registry keys (both 64-bit and 32-bit views, never the MSI WMI class), then
    runs msiexec silently with verbose logging to repair it with a full file reinstall or to uninstall it. Waits
    for the Windows Installer mutex and retries error 1618 (another installation running) with back-off; installer
    processes are never killed. Maps exit codes 0, 1603, 1605, 1618, 1641 and 3010. Per-user MSI installs must be
    repaired in the user context. Verbose logs can contain paths and user names, so they stay in ProgramData.
.NOTES
    RemoteHub: id=FIX-17; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ProductCode = '',
    [ValidateSet('Repair', 'Uninstall')]
    [string]$Action = 'Repair',
    [int]$MaxRetries = 3,
    [int]$RetryDelaySec = 60,
    [int]$TimeoutMinutes = 60
)

$ErrorActionPreference = 'Stop'
$codes = @{
    0 = 'Success'; 1603 = 'Fatal error during installation'; 1605 = 'Product is not installed'
    1618 = 'Another installation is in progress'; 1641 = 'Success, restart initiated'; 3010 = 'Success, restart required'
    1619 = 'Package could not be opened'; 1612 = 'Installation source is not available'
}

function Get-Product([string]$Code) {
    $views = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')
    foreach ($v in $views) {
        $k = Join-Path $v $Code
        if (Test-Path -LiteralPath $k) {
            $p = Get-ItemProperty -LiteralPath $k
            return [pscustomobject]@{ ProductCode = $Code; Name = $p.DisplayName; Version = $p.DisplayVersion; Key = $v }
        }
    }
    return $null
}

function Test-InstallerBusy {
    # Windows Installer holds this global mutex while any installation runs.
    $m = $null
    if ([System.Threading.Mutex]::TryOpenExisting('Global\_MSIExecute', [ref]$m)) { $m.Dispose(); return $true }
    return $false
}

try {
    if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { throw 'Run this script in 64-bit PowerShell.' }
    if (-not $ProductCode) { Write-Output 'No change: pass -ProductCode {GUID} to choose the product to repair or uninstall.'; exit 1 }
    $guid = [guid]::Empty
    if (-not [guid]::TryParse($ProductCode, [ref]$guid)) { throw "Product code '$ProductCode' is not a GUID." }
    $code = $guid.ToString('B').ToUpper()

    $before = Get-Product $code
    if (-not $before) {
        if ($Action -eq 'Uninstall') { Write-Output "No change: $code is not installed per machine."; exit 0 }
        Write-Output "$code is not installed per machine. A per-user install must be repaired in the user context."
        exit 1
    }
    Write-Verbose "Found $($before.Name) $($before.Version)"

    $logDir = Join-Path $env:ProgramData 'RemoteHub\ScriptLogs'
    $log = Join-Path $logDir ('FIX-17-{0}-{1}-{2}.log' -f $Action.ToLower(), $guid.ToString('N'), (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $msiSwitch = '/famus'; if ($Action -eq 'Uninstall') { $msiSwitch = '/x' }
    $arguments = '{0} {1} /qn /norestart /l*v "{2}"' -f $msiSwitch, $code, $log

    if (-not $PSCmdlet.ShouldProcess("$($before.Name) $($before.Version) $code", "msiexec $arguments")) {
        $before; exit 0
    }
    if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

    $exit = -1; $attempt = 0; $delay = $RetryDelaySec
    while ($true) {
        $attempt++
        if (Test-InstallerBusy) { $exit = 1618 }
        else {
            $p = Start-Process -FilePath (Join-Path $env:windir 'System32\msiexec.exe') -ArgumentList $arguments -PassThru -WindowStyle Hidden
            if (-not $p.WaitForExit($TimeoutMinutes * 60000)) { throw "msiexec did not finish within $TimeoutMinutes minutes (not killed; check $log)." }
            $exit = $p.ExitCode
        }
        if ($exit -ne 1618 -or $attempt -gt $MaxRetries) { break }
        Write-Verbose "Installer busy (1618); retry $attempt of $MaxRetries in $delay seconds."
        Start-Sleep -Seconds $delay
        $delay = $delay * 2
    }

    $text = $codes[$exit]; if (-not $text) { $text = 'See the verbose log' }
    $after = Get-Product $code
    $state = 'Not installed'; if ($after) { $state = "Installed $($after.Version)" }
    [pscustomobject]@{ Product = $before.Name; ProductCode = $code; Action = $Action; ExitCode = $exit; Meaning = $text; Attempts = $attempt; StateAfter = $state; Log = $log }
    if ($exit -eq 3010 -or $exit -eq 1641) { exit 3010 }
    if ($exit -eq 0) { exit 0 }
    exit 1
}
catch {
    Write-Output "FIX-17 failed: $($_.Exception.Message)"
    exit 1
}
