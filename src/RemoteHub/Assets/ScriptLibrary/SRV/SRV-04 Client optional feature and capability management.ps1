<#
.SYNOPSIS
    Enables or disables optional features and adds or removes capabilities from a defined list.
.DESCRIPTION
    Brings optional features (Hyper-V, Windows Sandbox, WSL components, .NET Framework 3.5) and Features on
    Demand capabilities (RSAT, OpenSSH client, language features) to the requested state without restarting.
    Use -WhatIf for a dry run. .NET 3.5 and some capabilities need -Source when Windows Update is not
    reachable. Hyper-V and Sandbox need a Pro or higher edition and virtualization enabled in firmware.
    Enabling features can change the boot configuration and trigger BitLocker recovery on some devices.
.NOTES
    RemoteHub: id=SRV-04; context=System; os=Client; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string[]]$EnableFeature = @(),
    [string[]]$DisableFeature = @(),
    [string[]]$AddCapability = @(),
    [string[]]$RemoveCapability = @(),
    # Local or staged source (mounted image \sources\sxs or FoD repository).
    [string]$Source = ''
)

$ErrorActionPreference = 'Stop'

$results = @()
$restart = $false
$failed = $false

function Add-Result([string]$Item, [string]$Action, [string]$Result, $Restart) {
    $script:results += [pscustomobject]@{ Item = $Item; Action = $Action; Result = $Result; RestartNeeded = $Restart }
}

try {
    if (($EnableFeature.Count + $DisableFeature.Count + $AddCapability.Count + $RemoveCapability.Count) -eq 0) {
        Write-Output 'Nothing to do: no features or capabilities were requested.'
        exit 0
    }
    if ($Source -and -not (Test-Path -LiteralPath $Source)) { throw "Source path '$Source' does not exist." }

    $edition = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID
    $virtNeeded = @($EnableFeature | Where-Object { $_ -like 'Microsoft-Hyper-V*' -or $_ -eq 'Containers-DisposableClientVM' })
    if ($virtNeeded.Count -gt 0) {
        if ($edition -like 'Core*') { throw "Edition '$edition' (Home) does not support Hyper-V or Windows Sandbox." }
        $cs = Get-CimInstance -ClassName Win32_ComputerSystem
        $cpu = @(Get-CimInstance -ClassName Win32_Processor)[0]
        if (-not $cs.HypervisorPresent -and -not $cpu.VirtualizationFirmwareEnabled) {
            throw 'Virtualization is not enabled in firmware; Hyper-V and Sandbox cannot run.'
        }
        try {
            $bl = Get-CimInstance -Namespace 'root/cimv2/security/microsoftvolumeencryption' -ClassName Win32_EncryptableVolume -Filter "DriveLetter='$env:SystemDrive'"
            if ($bl -and $bl.ProtectionStatus -eq 1) {
                Write-Warning 'BitLocker protects the OS volume; a boot configuration change can trigger recovery. Make sure the key is escrowed.'
            }
        } catch { Write-Verbose 'BitLocker state could not be read.' }
    }

    foreach ($name in $EnableFeature) {
        $f = Get-WindowsOptionalFeature -Online -FeatureName $name -ErrorAction SilentlyContinue
        if ($null -eq $f) { Add-Result $name 'Enable' 'Unknown feature' $false; $failed = $true; continue }
        if ([string]$f.State -eq 'Enabled') { Add-Result $name 'Enable' 'No change' $false; continue }
        if ($PSCmdlet.ShouldProcess($name, 'Enable-WindowsOptionalFeature')) {
            $p = @{ Online = $true; FeatureName = $name; All = $true; NoRestart = $true }
            if ($Source) { $p.Source = $Source; $p.LimitAccess = $true }
            $r = Enable-WindowsOptionalFeature @p
            if ($r.RestartNeeded) { $restart = $true }
            Add-Result $name 'Enable' 'Enabled' $r.RestartNeeded
        } else { Add-Result $name 'Enable' 'Planned' 'Unknown' }
    }

    foreach ($name in $DisableFeature) {
        $f = Get-WindowsOptionalFeature -Online -FeatureName $name -ErrorAction SilentlyContinue
        if ($null -eq $f) { Add-Result $name 'Disable' 'Unknown feature' $false; $failed = $true; continue }
        if ([string]$f.State -ne 'Enabled') { Add-Result $name 'Disable' 'No change' $false; continue }
        if ($PSCmdlet.ShouldProcess($name, 'Disable-WindowsOptionalFeature')) {
            $r = Disable-WindowsOptionalFeature -Online -FeatureName $name -NoRestart
            if ($r.RestartNeeded) { $restart = $true }
            Add-Result $name 'Disable' 'Disabled' $r.RestartNeeded
        } else { Add-Result $name 'Disable' 'Planned' 'Unknown' }
    }

    foreach ($pattern in $AddCapability) {
        $caps = @(Get-WindowsCapability -Online -Name $pattern)
        if ($caps.Count -eq 0) { Add-Result $pattern 'Add' 'Unknown capability' $false; $failed = $true; continue }
        foreach ($c in $caps) {
            if ([string]$c.State -eq 'Installed') { Add-Result $c.Name 'Add' 'No change' $false; continue }
            if ($PSCmdlet.ShouldProcess($c.Name, 'Add-WindowsCapability')) {
                $p = @{ Online = $true; Name = $c.Name }
                if ($Source) { $p.Source = $Source; $p.LimitAccess = $true }
                $r = Add-WindowsCapability @p
                if ($r.RestartNeeded) { $restart = $true }
                Add-Result $c.Name 'Add' 'Installed' $r.RestartNeeded
            } else { Add-Result $c.Name 'Add' 'Planned' 'Unknown' }
        }
    }

    foreach ($pattern in $RemoveCapability) {
        $caps = @(Get-WindowsCapability -Online -Name $pattern)
        if ($caps.Count -eq 0) { Add-Result $pattern 'Remove' 'Unknown capability' $false; $failed = $true; continue }
        foreach ($c in $caps) {
            if ([string]$c.State -ne 'Installed') { Add-Result $c.Name 'Remove' 'No change' $false; continue }
            if ($PSCmdlet.ShouldProcess($c.Name, 'Remove-WindowsCapability')) {
                $r = Remove-WindowsCapability -Online -Name $c.Name
                if ($r.RestartNeeded) { $restart = $true }
                Add-Result $c.Name 'Remove' 'Removed' $r.RestartNeeded
            } else { Add-Result $c.Name 'Remove' 'Planned' 'Unknown' }
        }
    }
} catch {
    Add-Result 'Run' 'Error' $_.Exception.Message $false
    $failed = $true
}

$results | Format-Table -AutoSize | Out-String -Width 200
if ($failed) { exit 1 }
if ($restart) { Write-Output 'Restart required to finish the changes.'; exit 3010 }
exit 0
