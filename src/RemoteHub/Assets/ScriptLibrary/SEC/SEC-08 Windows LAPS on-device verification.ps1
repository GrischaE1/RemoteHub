<#
.SYNOPSIS
    Verifies that Windows LAPS is configured and healthy on this device without reading the password.
.DESCRIPTION
    Reports the policy source (MDM, Group Policy, local or legacy LAPS), backup target, managed account,
    password age, length and complexity, post-authentication action and the last successful rotation.
    Windows LAPS needs the April 2023 updates or later on Windows 10/11 and Server 2019 and newer. Legacy
    Microsoft LAPS and Windows LAPS must not manage the same account. -InvokePolicyProcessing runs a policy
    cycle, which can rotate the password if it is due. The password value is never read.
.NOTES
    RemoteHub: id=SEC-08; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$InvokePolicyProcessing,
    [int]$MaxRotationAgeDays = 45,
    [switch]$Sensor,
    [int[]]$RotationEventId = @(10018, 10020, 10029)
)

$ErrorActionPreference = 'Stop'
$unavailable = 'Data unavailable'
$logDir = "$env:ProgramData\RemoteHub\ScriptLogs"

function Get-Values($path) {
    $item = Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue
    if (-not $item) { return $null }
    $names = @($item.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' })
    if ($names.Count -eq 0) { return $null }
    return $item
}

function Write-Log([string]$line) {
    try {
        if (-not (Test-Path -LiteralPath $logDir)) { $null = New-Item -ItemType Directory -Path $logDir -Force }
        $file = Join-Path $logDir 'SEC-08.log'
        if ((Test-Path -LiteralPath $file) -and (Get-Item -LiteralPath $file).Length -gt 1MB) { Move-Item -LiteralPath $file -Destination "$file.old" -Force }
        Add-Content -LiteralPath $file -Value ("{0:s} {1}" -f (Get-Date), $line) -Encoding UTF8
    } catch { Write-Verbose "Log write failed: $($_.Exception.Message)" }
}

try {
    $available = [bool](Get-Command -Name Get-LapsDiagnostics -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath "$env:SystemRoot\System32\laps.dll")
    if (-not $available) {
        if ($Sensor) { $false } else { [PSCustomObject]@{ Healthy = $false; Reason = 'Windows LAPS not present; check the patch level (April 2023 or later)' } }
        Write-Log 'Windows LAPS not present'
        exit 1
    }

    if ($InvokePolicyProcessing) {
        if ($PSCmdlet.ShouldProcess('Windows LAPS', 'Run policy processing (may rotate the password if due)')) {
            Invoke-LapsPolicyProcessing
        }
    }

    # Windows LAPS uses the first source that has values: MDM, then Group Policy, then local configuration.
    $sources = [ordered]@{
        'MDM' = 'HKLM:\SOFTWARE\Microsoft\Policies\LAPS'
        'Group Policy' = 'HKLM:\SOFTWARE\Policies\Microsoft Windows\LAPS'
        'Local' = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\LAPS\Config'
    }
    $source = 'None'; $cfg = $null
    foreach ($k in $sources.Keys) { $v = Get-Values $sources[$k]; if ($v) { $source = $k; $cfg = $v; break } }

    $legacy = Get-Values 'HKLM:\SOFTWARE\Policies\Microsoft Services\AdmPwd'
    $legacyOn = ($legacy -and $legacy.PSObject.Properties['AdmPwdEnabled'] -and $legacy.AdmPwdEnabled -eq 1)

    $backup = $unavailable; $account = $unavailable; $ageDays = $unavailable; $length = $unavailable
    $complexity = $unavailable; $postAuth = $unavailable
    if ($cfg) {
        $backup = 'Disabled'
        if ($cfg.PSObject.Properties['BackupDirectory']) { $backup = @{ 0 = 'Disabled'; 1 = 'Entra ID'; 2 = 'Active Directory' }[[int]$cfg.BackupDirectory] }
        $account = 'Built-in administrator'
        if ($cfg.PSObject.Properties['AdministratorAccountName'] -and $cfg.AdministratorAccountName) { $account = $cfg.AdministratorAccountName }
        $ageDays = 30; if ($cfg.PSObject.Properties['PasswordAgeDays']) { $ageDays = [int]$cfg.PasswordAgeDays }
        $length = 14; if ($cfg.PSObject.Properties['PasswordLength']) { $length = [int]$cfg.PasswordLength }
        $complexity = 4; if ($cfg.PSObject.Properties['PasswordComplexity']) { $complexity = [int]$cfg.PasswordComplexity }
        $postAuth = 3; if ($cfg.PSObject.Properties['PostAuthenticationActions']) { $postAuth = [int]$cfg.PostAuthenticationActions }
        $postAuth = @{ 0 = 'None'; 1 = 'Reset password'; 3 = 'Reset password and sign out'; 5 = 'Reset password and restart'; 11 = 'Reset password, sign out and end processes' }[[int]$postAuth]
    }

    # Rotation time comes from the LAPS state key, with the operational log as a fallback.
    $lastRotation = $null
    $state = Get-Values 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\LAPS\State'
    if ($state -and $state.PSObject.Properties['LastPasswordUpdateTime']) {
        try { $lastRotation = [DateTime]::FromFileTime([long]$state.LastPasswordUpdateTime) } catch { $lastRotation = $null }
    }
    if (-not $lastRotation) {
        $ev = Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-LAPS/Operational'; Id = $RotationEventId } -MaxEvents 1 -ErrorAction SilentlyContinue
        if ($ev) { $lastRotation = $ev.TimeCreated }
    }
    $rotationAge = $null
    if ($lastRotation) { $rotationAge = [math]::Round(((Get-Date) - $lastRotation).TotalDays, 1) }

    $issues = New-Object System.Collections.Generic.List[string]
    if ($source -eq 'None') { $issues.Add('No Windows LAPS policy') }
    elseif ($backup -eq 'Disabled') { $issues.Add('Backup directory is disabled') }
    if ($legacyOn -and $source -ne 'None') { $issues.Add('Legacy Microsoft LAPS is also enabled; make sure they manage different accounts') }
    if ($source -ne 'None' -and ($null -eq $rotationAge -or $rotationAge -gt $MaxRotationAgeDays)) { $issues.Add("No successful rotation in $MaxRotationAgeDays days") }
    $healthy = ($issues.Count -eq 0)

    $rotationText = $unavailable; if ($lastRotation) { $rotationText = $lastRotation.ToString('s') }
    Write-Log ("Healthy={0}; Source={1}; Backup={2}; LastRotation={3}" -f $healthy, $source, $backup, $rotationText)
    if ($Sensor) { $healthy }
    else {
        [PSCustomObject]@{
            Healthy = $healthy; Issues = ($issues -join '; ')
            PolicySource = $source; BackupTarget = $backup; ManagedAccount = $account
            PasswordAgeDays = $ageDays; PasswordLength = $length; PasswordComplexity = $complexity
            PostAuthenticationAction = $postAuth; LegacyLapsEnabled = [bool]$legacyOn
            LastRotation = $rotationText; RotationAgeDays = $rotationAge
            PolicyProcessingRun = [bool]($InvokePolicyProcessing -and -not $WhatIfPreference)
        }
    }
    if ($healthy) { exit 0 }
    exit 1
} catch {
    Write-Error "LAPS verification failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
