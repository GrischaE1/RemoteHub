<#
.SYNOPSIS
    Lists installed server roles and features and compares them with a desired set.
.DESCRIPTION
    Reads installed roles, role services and features with Get-WindowsFeature and compares them with the
    desired set for one server role from a JSON file. Reports missing, extra and "Removed" (payload deleted)
    items and flags features that should never be present, such as SMBv1 and Windows PowerShell 2.0.
    Feature names differ between Server versions, so keep one desired-state file per version. A "Removed"
    feature needs an install source before it can be re-installed. Server only; on clients use SRV-02.
.NOTES
    RemoteHub: id=SRV-01; context=System; os=Server; risk=R0
#>
[CmdletBinding()]
param(
    # JSON file: { "<Role>": ["Feature-Name", ...], ... }. Missing file = inventory and forbidden check only.
    [string]$DesiredStatePath = "$env:ProgramData\RemoteHub\ScriptData\SRV-01-desired-features.json",
    [string]$Role = 'Default',
    [string[]]$ForbiddenFeature = @('FS-SMB1', 'PowerShell-V2'),
    # Sensor mode writes a single Boolean: installed set matches desired set and nothing forbidden is present.
    [switch]$Sensor
)

$ErrorActionPreference = 'Stop'

try {
    if (-not (Get-Command -Name Get-WindowsFeature -ErrorAction SilentlyContinue)) {
        if ($Sensor) { Write-Output 'Data unavailable' } else {
            Write-Output 'Data unavailable: Get-WindowsFeature is not available (client SKU or ServerManager module missing). Use SRV-02.'
        }
        exit 1
    }

    $features = @(Get-WindowsFeature)
    $installed = @($features | Where-Object { $_.InstallState -eq 'Installed' })
    $removed = @($features | Where-Object { $_.InstallState -eq 'Removed' } | ForEach-Object { $_.Name })
    $installedNames = @($installed | ForEach-Object { $_.Name })

    $desired = $null
    if ($DesiredStatePath -and (Test-Path -LiteralPath $DesiredStatePath)) {
        $json = Get-Content -LiteralPath $DesiredStatePath -Raw -Encoding UTF8 | ConvertFrom-Json
        $entry = $json.PSObject.Properties | Where-Object { $_.Name -eq $Role } | Select-Object -First 1
        if ($null -eq $entry) {
            throw "Role '$Role' is not defined in the desired-state file."
        }
        $desired = @($entry.Value | ForEach-Object { [string]$_ })
        Write-Verbose "Loaded $($desired.Count) desired features for role '$Role'."
    } else {
        Write-Verbose 'No desired-state file found; drift comparison skipped.'
    }

    $missing = @()
    $extra = @()
    if ($null -ne $desired) {
        $missing = @($desired | Where-Object { $installedNames -notcontains $_ })
        # Parent roles and default features appear installed on every server, so only top-level
        # items that are not in the desired list count as extra.
        $extra = @($installed | Where-Object { $_.Depth -eq 1 -and $desired -notcontains $_.Name } | ForEach-Object { $_.Name })
    }
    $forbiddenPresent = @($ForbiddenFeature | Where-Object { $installedNames -contains $_ })
    $removedDesired = @()
    if ($null -ne $desired) { $removedDesired = @($missing | Where-Object { $removed -contains $_ }) }

    $compliant = ($missing.Count -eq 0) -and ($forbiddenPresent.Count -eq 0)
    if ($null -ne $desired) { $compliant = $compliant -and ($extra.Count -eq 0) }

    if ($Sensor) {
        Write-Output $compliant
    } else {
        $os = Get-CimInstance -ClassName Win32_OperatingSystem
        $desiredState = 'Data unavailable'
        if ($null -ne $desired) { $desiredState = $DesiredStatePath }
        $report = [ordered]@{
            Computer          = $env:COMPUTERNAME
            OsCaption         = $os.Caption
            OsBuild           = $os.BuildNumber
            Role              = $Role
            DesiredStateFile  = $desiredState
            Compliant         = $compliant
            Installed         = @($installed | Sort-Object Name | ForEach-Object {
                [ordered]@{ Name = $_.Name; DisplayName = $_.DisplayName; FeatureType = [string]$_.FeatureType; Path = $_.Path }
            })
            Missing           = $missing
            Extra             = $extra
            ForbiddenPresent  = $forbiddenPresent
            RemovedPayload    = $removed
            RemovedButDesired = $removedDesired
        }
        $report | ConvertTo-Json -Depth 5
    }

    if ($compliant) { exit 0 } else { exit 1 }
} catch {
    Write-Error "Role and feature inventory failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
