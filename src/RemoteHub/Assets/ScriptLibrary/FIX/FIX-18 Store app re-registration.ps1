<#
.SYNOPSIS
    Re-registers (or resets) selected Store and system experience apps for the signed-in user.
.DESCRIPTION
    For a broken Start menu, Settings, Photos, Calculator or Store, re-registers the chosen apps for the current
    user from their installed manifests, or resets them with -Reset where Reset-AppxPackage exists. Apps whose
    package status is OK are left alone unless -Force is given, so a second run reports no change. Reset removes
    the app's data. Re-registering for all users needs an elevated context, affects everyone and is deliberately
    not offered here. A mismatch between provisioned and installed packages is a separate fault.
.NOTES
    RemoteHub: id=FIX-18; context=User; os=Client; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('StartMenu', 'ShellExperience', 'Settings', 'Photos', 'Calculator', 'Store')]
    [string[]]$App = @('StartMenu', 'ShellExperience', 'Settings'),
    [switch]$Reset,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$packageNames = @{
    StartMenu = 'Microsoft.Windows.StartMenuExperienceHost'; ShellExperience = 'Microsoft.Windows.ShellExperienceHost'
    Settings = 'windows.immersivecontrolpanel'; Photos = 'Microsoft.Windows.Photos'
    Calculator = 'Microsoft.WindowsCalculator'; Store = 'Microsoft.WindowsStore'
}
$results = New-Object System.Collections.Generic.List[object]
$failed = $false

try {
    if ([Security.Principal.WindowsIdentity]::GetCurrent().IsSystem) { Write-Output 'Data unavailable: run this script in the signed-in user context.'; exit 1 }
    $canReset = [bool](Get-Command -Name Reset-AppxPackage -ErrorAction SilentlyContinue)

    foreach ($a in $App) {
        $name = $packageNames[$a]
        $pkg = Get-AppxPackage -Name $name | Sort-Object Version -Descending | Select-Object -First 1
        if (-not $pkg) { $results.Add([pscustomobject]@{ App = $a; Package = $name; StatusBefore = 'Not installed for this user'; Action = 'None'; Result = 'Skipped' }); continue }
        $statusBefore = "$($pkg.Status)"
        if ($statusBefore -eq 'Ok' -and -not $Force) {
            $results.Add([pscustomobject]@{ App = $a; Package = $pkg.PackageFullName; StatusBefore = $statusBefore; Action = 'None'; Result = 'Healthy, no change' })
            continue
        }
        $action = 'Re-register'
        if ($Reset) {
            if (-not $canReset) { $results.Add([pscustomobject]@{ App = $a; Package = $pkg.PackageFullName; StatusBefore = $statusBefore; Action = 'Reset'; Result = 'Skipped: Reset-AppxPackage not available on this build' }); continue }
            $action = 'Reset (removes app data)'
        }
        if (-not $PSCmdlet.ShouldProcess($pkg.PackageFullName, $action)) {
            $results.Add([pscustomobject]@{ App = $a; Package = $pkg.PackageFullName; StatusBefore = $statusBefore; Action = $action; Result = 'WhatIf' })
            continue
        }
        try {
            if ($Reset) { Reset-AppxPackage -Package $pkg.PackageFullName }
            else {
                $manifest = Join-Path $pkg.InstallLocation 'AppxManifest.xml'
                if (-not (Test-Path -LiteralPath $manifest)) { throw 'Installed manifest is missing; the package needs reinstalling (separate fault).' }
                Add-AppxPackage -DisableDevelopmentMode -Register $manifest
            }
            $after = Get-AppxPackage -Name $name | Sort-Object Version -Descending | Select-Object -First 1
            $results.Add([pscustomobject]@{ App = $a; Package = $pkg.PackageFullName; StatusBefore = $statusBefore; Action = $action; Result = "Done, status now $($after.Status)" })
        }
        catch {
            $failed = $true
            $results.Add([pscustomobject]@{ App = $a; Package = $pkg.PackageFullName; StatusBefore = $statusBefore; Action = $action; Result = "Failed: $($_.Exception.Message)" })
        }
    }

    $results
    if ($failed) { exit 1 }
    exit 0
}
catch {
    $results
    Write-Output "FIX-18 failed: $($_.Exception.Message)"
    exit 1
}
