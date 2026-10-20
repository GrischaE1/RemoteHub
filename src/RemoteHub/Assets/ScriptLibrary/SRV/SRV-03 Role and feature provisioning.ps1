<#
.SYNOPSIS
    Installs (or removes) server roles and features from a role definition, with a dry run first.
.DESCRIPTION
    Reads a role definition (feature list, management tools flag, optional source path), validates the OS,
    refuses to run while a reboot is pending, prints the plan and installs only with -Apply. Reports a result
    per feature and exits 3010 when a restart is needed; the script never restarts the server itself.
    Management tools are added only where a GUI shell exists. Removing with payload deletion means later
    re-installs need a source.
.NOTES
    RemoteHub: id=SRV-03; context=System; os=Server; risk=R2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # JSON: { "Features": [], "Remove": [], "IncludeManagementTools": true, "Source": "", "RemovePayload": false }
    [string]$DefinitionPath = "$env:ProgramData\RemoteHub\ScriptData\SRV-03-role-definition.json",
    # Overrides the definition file's feature list when given.
    [string[]]$Feature = @(),
    [string[]]$RemoveFeature = @(),
    [bool]$IncludeManagementTools = $true,
    [string]$Source = '',
    [switch]$RemovePayload,
    [switch]$IgnorePendingReboot,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if (-not $Apply) {
    $WhatIfPreference = $true
    Write-Output 'Dry run: nothing changed. Run with -Apply to make the changes.'
}

function Test-PendingReboot {
    $keys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
    )
    foreach ($k in $keys) { if (Test-Path -LiteralPath $k) { return $true } }
    $sm = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager'
    return ($null -ne $sm.PSObject.Properties['PendingFileRenameOperations'])
}

$exitCode = 0
try {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    if ($os.ProductType -eq 1) { throw 'This script needs Windows Server (ProductType is workstation).' }
    if (-not (Get-Command -Name Install-WindowsFeature -ErrorAction SilentlyContinue)) {
        throw 'Install-WindowsFeature is not available (ServerManager module missing).'
    }

    $def = $null
    if ($DefinitionPath -and (Test-Path -LiteralPath $DefinitionPath)) {
        $def = Get-Content -LiteralPath $DefinitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    if ($Feature.Count -eq 0 -and $null -ne $def -and $def.Features) { $Feature = @($def.Features | ForEach-Object { [string]$_ }) }
    if ($RemoveFeature.Count -eq 0 -and $null -ne $def -and $def.Remove) { $RemoveFeature = @($def.Remove | ForEach-Object { [string]$_ }) }
    if (-not $PSBoundParameters.ContainsKey('IncludeManagementTools') -and $null -ne $def -and $null -ne $def.IncludeManagementTools) {
        $IncludeManagementTools = [bool]$def.IncludeManagementTools
    }
    if (-not $Source -and $null -ne $def -and $def.Source) { $Source = [string]$def.Source }
    if (-not $RemovePayload -and $null -ne $def -and $def.RemovePayload) { $RemovePayload = [switch]$true }

    if ($Feature.Count -eq 0 -and $RemoveFeature.Count -eq 0) {
        Write-Output 'Nothing to do: no features in the definition file or parameters.'
        exit 0
    }

    $installType = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').InstallationType
    if ($installType -eq 'Server Core' -and $IncludeManagementTools) {
        Write-Verbose 'Server Core: graphical management tools cannot be installed, so they are left out.'
        $IncludeManagementTools = $false
    }

    if ((Test-PendingReboot) -and -not $IgnorePendingReboot) {
        throw 'A reboot is pending (see INV-05). Restart first or run with -IgnorePendingReboot.'
    }
    if ($Source -and -not (Test-Path -LiteralPath $Source)) { throw "Source path '$Source' does not exist." }

    $results = @()
    $restart = $false
    foreach ($name in $Feature) {
        $f = Get-WindowsFeature -Name $name
        if ($null -eq $f) {
            $results += [pscustomobject]@{ Feature = $name; Action = 'Install'; Result = 'Unknown feature name'; RestartNeeded = $false }
            $exitCode = 1
            continue
        }
        if ($f.Installed) {
            $results += [pscustomobject]@{ Feature = $name; Action = 'Install'; Result = 'No change (already installed)'; RestartNeeded = $false }
            continue
        }
        if ($f.InstallState -eq 'Removed' -and -not $Source) {
            Write-Warning "$name has its payload removed; without -Source it is fetched from Windows Update if allowed."
        }
        if ($PSCmdlet.ShouldProcess($name, 'Install-WindowsFeature')) {
            $p = @{ Name = $name; IncludeManagementTools = $IncludeManagementTools; ErrorAction = 'Stop' }
            if ($Source) { $p.Source = $Source }
            $r = Install-WindowsFeature @p
            $needs = ([string]$r.RestartNeeded -eq 'Yes')
            if ($needs) { $restart = $true }
            $verified = (Get-WindowsFeature -Name $name).InstallState
            $state = 'Failed'
            if ($r.Success) { $state = "Installed (state now $verified)" } else { $exitCode = 1 }
            $results += [pscustomobject]@{ Feature = $name; Action = 'Install'; Result = $state; RestartNeeded = $needs }
        } else {
            $results += [pscustomobject]@{ Feature = $name; Action = 'Install'; Result = 'Planned'; RestartNeeded = 'Unknown' }
        }
    }

    foreach ($name in $RemoveFeature) {
        $f = Get-WindowsFeature -Name $name
        if ($null -eq $f -or (-not $f.Installed -and -not ($RemovePayload -and $f.InstallState -ne 'Removed'))) {
            $results += [pscustomobject]@{ Feature = $name; Action = 'Remove'; Result = 'No change'; RestartNeeded = $false }
            continue
        }
        $what = 'Uninstall-WindowsFeature'
        if ($RemovePayload) { $what = 'Uninstall-WindowsFeature -Remove (deletes payload)' }
        if ($PSCmdlet.ShouldProcess($name, $what)) {
            $r = Uninstall-WindowsFeature -Name $name -Remove:$RemovePayload -ErrorAction Stop
            $needs = ([string]$r.RestartNeeded -eq 'Yes')
            if ($needs) { $restart = $true }
            $state = 'Failed'
            if ($r.Success) { $state = 'Removed' } else { $exitCode = 1 }
            $results += [pscustomobject]@{ Feature = $name; Action = 'Remove'; Result = $state; RestartNeeded = $needs }
        } else {
            $results += [pscustomobject]@{ Feature = $name; Action = 'Remove'; Result = 'Planned'; RestartNeeded = 'Unknown' }
        }
    }

    $results | Format-Table -AutoSize | Out-String -Width 200
    if ($exitCode -eq 0 -and $restart) {
        Write-Output 'Restart required to finish. The script does not restart the server.'
        $exitCode = 3010
    }
} catch {
    Write-Error "Feature provisioning failed: $($_.Exception.Message)" -ErrorAction Continue
    $exitCode = 1
}
exit $exitCode
