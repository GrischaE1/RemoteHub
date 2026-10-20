<#
.SYNOPSIS
    Reports DSC resource modules against a pinned manifest and installs missing versions from a trusted repository.
.DESCRIPTION
    Compares installed modules (all-users path) with a manifest of Name=Version pairs, checks Authenticode
    signatures, and installs the exact missing versions with Install-Module -RequiredVersion -Scope AllUsers.
    A compiled MOF records module versions, so the node must have exactly those. Installs only come from a
    named repository that is registered as Trusted (an internal, reviewed feed); trust is never bypassed and
    without -Repository the script only reports. Older versions are kept because existing MOFs may need them.
.NOTES
    RemoteHub: id=DSC-04; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string[]]$Manifest = @('PSDscResources=2.12.0.0', 'ComputerManagementDsc=9.1.0', 'NetworkingDsc=9.0.0',
        'SecurityPolicyDsc=2.10.0.0', 'AuditPolicyDsc=1.4.0.0'),
    [string]$Repository = '',
    [bool]$RequireValidSignature = $false
)

$ErrorActionPreference = 'Stop'
$logFile = Join-Path $env:ProgramData 'RemoteHub\ScriptLogs\DSC-04.log'

function Write-RhLog {
    param([string]$Message)
    Write-Verbose $Message
    try {
        $dir = Split-Path $logFile -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force -WhatIf:$false | Out-Null }
        if ((Test-Path $logFile) -and ((Get-Item $logFile).Length -gt 1MB)) { Move-Item $logFile "$logFile.1" -Force -WhatIf:$false }
        Add-Content -Path $logFile -Value ('{0} {1}' -f (Get-Date -Format 's'), $Message) -Encoding UTF8 -WhatIf:$false
    } catch { Write-Verbose "Log write failed: $($_.Exception.Message)" }
}

function Get-ModuleState {
    param([string]$Name, [string]$Version)
    $mod = Get-Module -ListAvailable -Name $Name | Where-Object { $_.Version -eq [version]$Version } | Select-Object -First 1
    if (-not $mod) { return [pscustomobject]@{ Installed = $false; Signature = 'Not installed'; Path = '' } }
    $sig = 'Data unavailable'
    $file = Join-Path $mod.ModuleBase ($Name + '.psd1')
    if (Test-Path -LiteralPath $file) { $sig = [string](Get-AuthenticodeSignature -FilePath $file).Status }
    [pscustomobject]@{ Installed = $true; Signature = $sig; Path = $mod.ModuleBase }
}

try {
    $entries = foreach ($line in $Manifest) {
        if ($line -notmatch '^\s*([A-Za-z0-9\.\-_]+)\s*=\s*(\d+(\.\d+){1,3})\s*$') { throw "Manifest entry '$line' is not Name=Version." }
        [pscustomobject]@{ Name = $Matches[1]; Version = $Matches[2] }
    }

    $repoOk = $false
    $repoNote = 'Report only: no -Repository given'
    if ($Repository) {
        $repo = Get-PSRepository -Name $Repository -ErrorAction SilentlyContinue
        if (-not $repo) { $repoNote = "Repository '$Repository' is not registered" }
        elseif ($repo.InstallationPolicy -ne 'Trusted') { $repoNote = "Repository '$Repository' is not Trusted; register a reviewed feed as Trusted first" }
        elseif (-not (Get-PackageProvider -ListAvailable -Name NuGet -ErrorAction SilentlyContinue)) { $repoNote = 'The NuGet package provider is missing; install it from the internal feed first' }
        else { $repoOk = $true; $repoNote = "Installing from '$Repository'" }
    }
    Write-RhLog "Run started. $repoNote"

    $results = @()
    $failed = $false
    foreach ($e in $entries) {
        $state = Get-ModuleState -Name $e.Name -Version $e.Version
        $action = 'None'
        if (-not $state.Installed) {
            if (-not $repoOk) {
                $action = 'Missing'
                $failed = $true
            } elseif ($PSCmdlet.ShouldProcess("$($e.Name) $($e.Version)", "Install-Module from $Repository (AllUsers)")) {
                try {
                    # PowerShellGet on 5.1 can default to TLS 1.0, which current feeds refuse.
                    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
                    Install-Module -Name $e.Name -RequiredVersion $e.Version -Repository $Repository -Scope AllUsers -ErrorAction Stop
                    $state = Get-ModuleState -Name $e.Name -Version $e.Version
                    if ($state.Installed) { $action = 'Installed' } else { $action = 'Install reported success but module not found'; $failed = $true }
                    Write-RhLog "$($e.Name) $($e.Version): $action"
                } catch {
                    $action = 'Install failed: ' + $_.Exception.Message
                    $failed = $true
                    Write-RhLog "$($e.Name) $($e.Version): $action"
                }
            } else {
                $action = 'WhatIf: would install'
            }
        }
        if ($state.Installed -and $RequireValidSignature -and $state.Signature -ne 'Valid') { $failed = $true }

        $other = @(Get-Module -ListAvailable -Name $e.Name | Where-Object { $_.Version -ne [version]$e.Version } |
                ForEach-Object { $_.Version.ToString() })
        $results += [pscustomobject]@{
            Module        = $e.Name
            Pinned        = $e.Version
            Installed     = $state.Installed
            Signature     = $state.Signature
            OtherVersions = ($other -join ', ')
            Action        = $action
        }
    }

    $results
    [pscustomobject]@{ Id = 'DSC-04'; Repository = $repoNote; Compliant = (-not $failed); Log = $logFile }
    Write-RhLog ('Run finished. Failed=' + $failed)
    if ($failed) { exit 1 }
    exit 0
} catch {
    Write-RhLog "Error: $($_.Exception.Message)"
    Write-Error "DSC-04 failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
