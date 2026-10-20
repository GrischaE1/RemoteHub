<#
.SYNOPSIS
    Builds an Azure Machine Configuration package from a compiled MOF and tests it locally before it is published.
.DESCRIPTION
    Uses the GuestConfiguration module (New-GuestConfigurationPackage, Test-GuestConfigurationPackage), which
    needs PowerShell 7; from Windows PowerShell the work runs in pwsh.exe. Packages are Audit or AuditAndSet.
    -RemediateLocally applies an AuditAndSet package to this machine for a rehearsal. Reboot handling,
    credentials inside configurations and parameters behave differently or are limited there; verify against
    current documentation. Publishing and the Azure Policy assignment happen in Azure, not through endpoint
    tooling, so the Azure compliance state is reported as Data unavailable here.
.NOTES
    RemoteHub: id=DSC-14; context=AsMe; os=Server; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ConfigurationMofPath = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'RemoteHub\DSC\Output\localhost.mof'),
    [string]$PackageName = 'RemoteHubBaseline',
    [ValidateSet('Audit', 'AuditAndSet')]
    [string]$Type = 'Audit',
    [string]$OutputPath = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'RemoteHub\DSC\Packages'),
    [switch]$RemediateLocally,
    [string]$PwshPath = ''
)

$ErrorActionPreference = 'Stop'
$id = 'DSC-14'

function ConvertTo-Literal { param([string]$Text) "'" + ($Text -replace "'", "''") + "'" }

function Invoke-Pwsh {
    param([string]$Exe, [string]$Script)
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script))
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = '-NoProfile -NonInteractive -EncodedCommand ' + $encoded
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $errTask = $p.StandardError.ReadToEndAsync()
    $out = $p.StandardOutput.ReadToEnd()
    if (-not $p.WaitForExit(900000)) { $p.Kill(); throw 'pwsh did not finish within 15 minutes.' }
    [pscustomobject]@{ ExitCode = $p.ExitCode; StdOut = $out; StdErr = $errTask.Result }
}

try {
    if ($PackageName -notmatch '^[A-Za-z0-9_\-]{1,64}$') { throw 'PackageName may use letters, digits, hyphen and underscore only.' }
    if (-not (Test-Path -LiteralPath $ConfigurationMofPath)) { throw "Data unavailable: MOF not found: $ConfigurationMofPath (compile with DSC-11)." }
    if ($RemediateLocally -and $Type -ne 'AuditAndSet') { throw '-RemediateLocally needs -Type AuditAndSet.' }

    $mofText = Get-Content -LiteralPath $ConfigurationMofPath -Raw
    if ($mofText -match 'instance of MSFT_Credential') { throw 'The MOF contains credentials, which Machine Configuration packages do not support safely; use a managed identity or gMSA.' }

    $remediate = $false
    if ($RemediateLocally) {
        $remediate = $PSCmdlet.ShouldProcess($env:COMPUTERNAME, "Apply package $PackageName locally (AuditAndSet)")
    }

    # The package is rebuilt only when the MOF or type changed, so a second run reports no change.
    $inner = @"
`$ErrorActionPreference = 'Stop'
`$r = [ordered]@{ Package = ''; Built = `$false; Compliant = 'Data unavailable'; Resources = @(); Remediated = `$false; Error = '' }
try {
    if (-not (Get-Module -ListAvailable -Name GuestConfiguration)) { throw 'Data unavailable: the GuestConfiguration module is not installed for PowerShell 7.' }
    Import-Module GuestConfiguration
    `$mof = $(ConvertTo-Literal $ConfigurationMofPath)
    `$out = $(ConvertTo-Literal $OutputPath)
    `$name = $(ConvertTo-Literal $PackageName)
    `$type = $(ConvertTo-Literal $Type)
    if (-not (Test-Path `$out)) { New-Item -ItemType Directory -Path `$out -Force | Out-Null }
    `$zip = Join-Path `$out (`$name + '.zip')
    `$stampFile = Join-Path `$out (`$name + '.source')
    `$stamp = (Get-FileHash -LiteralPath `$mof -Algorithm SHA256).Hash + '|' + `$type
    if (-not (Test-Path `$zip) -or -not (Test-Path `$stampFile) -or (Get-Content `$stampFile -Raw).Trim() -ne `$stamp) {
        `$pkg = New-GuestConfigurationPackage -Name `$name -Configuration `$mof -Type `$type -Path `$out -Force
        `$zip = [string]`$pkg.Path
        Set-Content -Path `$stampFile -Value `$stamp -Encoding utf8
        `$r.Built = `$true
    }
    `$r.Package = `$zip
    `$t = Test-GuestConfigurationPackage -Path `$zip
    `$r.Compliant = [string]`$t.complianceStatus
    `$r.Resources = @(`$t.resources | ForEach-Object { [ordered]@{ Resource = [string]`$_.ResourceId; Compliant = [string]`$_.complianceStatus } })
    if (`$$remediate) {
        Start-GuestConfigurationPackageRemediation -Path `$zip | Out-Null
        `$r.Remediated = `$true
        `$r.Compliant = [string](Test-GuestConfigurationPackage -Path `$zip).complianceStatus
    }
} catch { `$r.Error = `$_.Exception.Message }
'RHRESULT:' + (`$r | ConvertTo-Json -Depth 5 -Compress)
"@

    if ($PSVersionTable.PSEdition -eq 'Core') {
        $text = (& ([scriptblock]::Create($inner))) -join "`n"
    } else {
        $exe = $PwshPath
        if (-not $exe) {
            $cmd = Get-Command -Name 'pwsh.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($cmd) { $exe = $cmd.Source }
        }
        if (-not $exe -or -not (Test-Path -LiteralPath $exe)) { throw 'Data unavailable: PowerShell 7 (pwsh.exe) is needed for the GuestConfiguration module.' }
        $run = Invoke-Pwsh -Exe $exe -Script $inner
        $text = $run.StdOut
        if ($run.ExitCode -ne 0 -and $text -notmatch 'RHRESULT:') { throw "pwsh failed (exit $($run.ExitCode)): $($run.StdErr.Trim())" }
    }

    $line = ($text -split "`r?`n") | Where-Object { $_ -like 'RHRESULT:*' } | Select-Object -Last 1
    if (-not $line) { throw 'Data unavailable: no result from the packaging step.' }
    $res = $line.Substring(9) | ConvertFrom-Json
    if ($res.Error) { throw $res.Error }

    foreach ($r in @($res.Resources)) { [pscustomobject]@{ Resource = $r.Resource; Compliant = $r.Compliant } }
    [pscustomobject]@{
        Id              = $id
        Package         = $res.Package
        Type            = $Type
        Change          = $(if ($res.Built) { 'Package built' } else { 'No change: package already matches the MOF' })
        LocalCompliance = $res.Compliant
        Remediated      = $res.Remediated
        AzureCompliance = 'Data unavailable (publish the package and assign it through Azure Policy)'
    }
    if ($res.Compliant -eq 'Compliant') { exit 0 }
    exit 1
} catch {
    Write-Error "$id failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
