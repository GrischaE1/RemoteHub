<#
.SYNOPSIS
    Reports Windows optional features, Features on Demand capabilities and provisioned Store packages.
.DESCRIPTION
    Lists optional features, capabilities (RSAT, OpenSSH, language resources) and provisioned Appx packages
    of the running image, and checks that required capabilities are installed. The capability listing can
    be slow because it may query the update source. Since Windows 10 1809 RSAT ships as Features on Demand;
    devices on WSUS need the optional component source policy pointing at Windows Update or a local source.
.NOTES
    RemoteHub: id=SRV-02; context=System; os=Client; risk=R0
#>
[CmdletBinding()]
param(
    # Capability names or wildcard patterns that must be installed, e.g. 'Rsat.ActiveDirectory*'.
    [string[]]$RequiredCapability = @(),
    [switch]$SkipCapabilities,
    [switch]$SkipProvisionedPackages,
    # Sensor mode writes a single Boolean: every required capability is installed.
    [switch]$Sensor
)

$ErrorActionPreference = 'Stop'

function Get-OcSourcePolicy {
    # Group Policy "Specify settings for optional component installation and component repair".
    $key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Servicing'
    if (-not (Test-Path -LiteralPath $key)) { return 'Not configured' }
    $p = Get-ItemProperty -LiteralPath $key
    $useWu = 'Not set'
    if ($null -ne $p.PSObject.Properties['RepairContentServerSource']) { $useWu = [string]$p.RepairContentServerSource }
    $path = 'Not set'
    if ($null -ne $p.PSObject.Properties['LocalSourcePath']) { $path = [string]$p.LocalSourcePath }
    return "RepairContentServerSource=$useWu; LocalSourcePath=$path"
}

try {
    $features = @(Get-WindowsOptionalFeature -Online | Sort-Object FeatureName | ForEach-Object {
        [pscustomobject]@{ Name = $_.FeatureName; State = [string]$_.State }
    })
    Write-Verbose "Read $($features.Count) optional features."

    $capabilities = @()
    $capabilityStatus = 'Skipped'
    if (-not $SkipCapabilities) {
        try {
            $capabilities = @(Get-WindowsCapability -Online | Sort-Object Name | ForEach-Object {
                [pscustomobject]@{ Name = $_.Name; State = [string]$_.State }
            })
            $capabilityStatus = 'OK'
        } catch {
            $capabilityStatus = "Data unavailable: $($_.Exception.Message)"
        }
    }

    $provisioned = @()
    if (-not $SkipProvisionedPackages) {
        $provisioned = @(Get-AppxProvisionedPackage -Online | Sort-Object DisplayName | ForEach-Object {
            [pscustomobject]@{ DisplayName = $_.DisplayName; Version = [string]$_.Version; PackageName = $_.PackageName }
        })
    }

    $requiredResults = @()
    foreach ($pattern in $RequiredCapability) {
        $match = @($capabilities | Where-Object { $_.Name -like $pattern })
        $state = 'Data unavailable'
        if ($capabilityStatus -eq 'OK') {
            if ($match.Count -eq 0) { $state = 'NotFound' }
            elseif (@($match | Where-Object { $_.State -ne 'Installed' }).Count -eq 0) { $state = 'Installed' }
            else { $state = 'NotInstalled' }
        }
        $requiredResults += [pscustomobject]@{ Capability = $pattern; State = $state }
    }
    $compliant = @($requiredResults | Where-Object { $_.State -ne 'Installed' }).Count -eq 0

    if ($Sensor) {
        if ($RequiredCapability.Count -gt 0 -and $capabilityStatus -ne 'OK') { Write-Output 'Data unavailable' }
        else { Write-Output $compliant }
    } else {
        [ordered]@{
            Computer             = $env:COMPUTERNAME
            OptionalFeatures     = $features
            EnabledFeatureCount  = @($features | Where-Object { $_.State -eq 'Enabled' }).Count
            CapabilityStatus     = $capabilityStatus
            Capabilities         = $capabilities
            InstalledCapabilities = @($capabilities | Where-Object { $_.State -eq 'Installed' }).Count
            ProvisionedPackages  = $provisioned
            OptionalComponentSourcePolicy = Get-OcSourcePolicy
            RequiredCapabilities = $requiredResults
            Compliant            = $compliant
        } | ConvertTo-Json -Depth 5
    }

    if ($compliant) { exit 0 } else { exit 1 }
} catch {
    Write-Error "Optional feature inventory failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
