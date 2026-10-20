<#
.SYNOPSIS
    Sets the Local Configuration Manager meta-configuration and reports the settings before and after.
.DESCRIPTION
    Applies configuration mode, refresh mode, consistency check interval, self-restart, action after reboot
    and the document encryption certificate. Defaults to ApplyAndMonitor so drift is reported, not corrected;
    auto-correct can fight Group Policy, MDM and administrators and flap on clients. Self-restart stays off
    unless explicitly approved. The interval cannot be below 15 minutes. Pull registration is DSC-12.
    The previous settings are saved to a rollback file before any change.
.NOTES
    RemoteHub: id=DSC-02; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('ApplyOnly', 'ApplyAndMonitor', 'ApplyAndAutoCorrect')]
    [string]$ConfigurationMode = 'ApplyAndMonitor',
    [ValidateSet('Push', 'Disabled')]
    [string]$RefreshMode = 'Push',
    [ValidateRange(15, 44640)]
    [int]$ConfigurationModeFrequencyMins = 15,
    [bool]$RebootNodeIfNeeded = $false,
    [ValidateSet('ContinueConfiguration', 'StopConfiguration')]
    [string]$ActionAfterReboot = 'ContinueConfiguration',
    [string]$CertificateId = ''
)

$ErrorActionPreference = 'Stop'
$workDir = Join-Path $env:ProgramData 'RemoteHub\DSC\DSC-02'

function Get-LcmMeta {
    $r = Invoke-CimMethod -Namespace 'root/Microsoft/Windows/DesiredStateConfiguration' `
        -ClassName 'MSFT_DSCLocalConfigurationManager' -MethodName 'GetMetaConfiguration'
    $m = $r.MetaConfiguration
    [pscustomobject]@{
        LCMState                       = [string]$m.LCMState
        ConfigurationMode              = [string]$m.ConfigurationMode
        RefreshMode                    = [string]$m.RefreshMode
        ConfigurationModeFrequencyMins = [int]$m.ConfigurationModeFrequencyMins
        RebootNodeIfNeeded             = [bool]$m.RebootNodeIfNeeded
        ActionAfterReboot              = [string]$m.ActionAfterReboot
        CertificateID                  = [string]$m.CertificateID
    }
}

try {
    if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Run this script in Windows PowerShell 5.1; the LCM cmdlets are not in PowerShell 7.' }

    $before = Get-LcmMeta
    if ($before.LCMState -eq 'Busy') { throw 'The LCM is busy applying a configuration; try again later.' }

    $certId = $CertificateId.Trim().ToUpperInvariant()
    if (-not $certId) { $certId = $before.CertificateID }
    if ($certId) {
        if ($certId -notmatch '^[0-9A-F]{40}$') { throw 'CertificateId must be a 40 character thumbprint.' }
        $cert = Get-Item -Path ("Cert:\LocalMachine\My\" + $certId) -ErrorAction SilentlyContinue
        if (-not $cert) { throw "No certificate with thumbprint $certId in LocalMachine\My." }
        if (-not $cert.HasPrivateKey) { throw 'The certificate has no private key on this node, so MOF credentials could not be decrypted.' }
        if ($cert.NotAfter -lt (Get-Date)) { throw "The certificate expired on $($cert.NotAfter.ToString('yyyy-MM-dd'))." }
        if (@($cert.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.4.1.311.80.1' }).Count -eq 0) {
            Write-Warning 'The certificate has no Document Encryption EKU; see DSC-03.'
        }
    }

    $desired = [pscustomobject]@{
        ConfigurationMode              = $ConfigurationMode
        RefreshMode                    = $RefreshMode
        ConfigurationModeFrequencyMins = $ConfigurationModeFrequencyMins
        RebootNodeIfNeeded             = $RebootNodeIfNeeded
        ActionAfterReboot              = $ActionAfterReboot
        CertificateID                  = $certId
    }

    $diff = @()
    foreach ($p in $desired.PSObject.Properties) {
        if ([string]$before.($p.Name) -ne [string]$p.Value) { $diff += $p.Name }
    }

    if ($diff.Count -eq 0) {
        [pscustomobject]@{ Id = 'DSC-02'; Result = 'No change'; Before = $before; After = $before }
        exit 0
    }

    if ($ConfigurationMode -eq 'ApplyAndAutoCorrect') {
        Write-Warning 'ApplyAndAutoCorrect reverts changes made by Group Policy, MDM or administrators. Start in ApplyAndMonitor.'
    }
    if ($RebootNodeIfNeeded) { Write-Warning 'The node will be allowed to restart itself. This must be an approved decision.' }
    if ($before.RefreshMode -eq 'Pull') {
        # A Settings-only meta-configuration drops the pull server blocks, so leaving pull must be asked for.
        if (-not $PSBoundParameters.ContainsKey('RefreshMode')) {
            throw 'The node is registered for pull (DSC-12). Pass -RefreshMode explicitly to replace that registration.'
        }
        Write-Warning 'The node is registered for pull; this meta-configuration replaces that registration.'
    }

    $target = "LCM on $env:COMPUTERNAME"
    $action = 'Set ' + ($diff -join ', ')
    if (-not $PSCmdlet.ShouldProcess($target, $action)) {
        [pscustomobject]@{ Id = 'DSC-02'; Result = 'WhatIf: no change made'; Changes = ($diff -join ', '); Before = $before; Desired = $desired }
        exit 0
    }

    if (-not (Test-Path $workDir)) { New-Item -ItemType Directory -Path $workDir -Force | Out-Null }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $rollback = Join-Path $workDir "lcm-before-$stamp.json"
    $before | ConvertTo-Json | Set-Content -Path $rollback -Encoding UTF8

    $certLine = ''
    if ($certId) { $certLine = "CertificateID = '$certId'" }
    # Values are interpolated only after ValidateSet, ValidateRange and the thumbprint pattern have checked them.
    $metaText = @"
[DscLocalConfigurationManager()]
Configuration RhLcmSettings {
    Node 'localhost' {
        Settings {
            ConfigurationMode = '$ConfigurationMode'
            RefreshMode = '$RefreshMode'
            ConfigurationModeFrequencyMins = $ConfigurationModeFrequencyMins
            RebootNodeIfNeeded = `$$RebootNodeIfNeeded
            ActionAfterReboot = '$ActionAfterReboot'
            $certLine
        }
    }
}
"@
    $mofDir = Join-Path $workDir "meta-$stamp"
    . ([scriptblock]::Create($metaText))
    RhLcmSettings -OutputPath $mofDir | Out-Null
    Set-DscLocalConfigurationManager -Path $mofDir -ErrorAction Stop

    $after = Get-LcmMeta
    $stillDiff = @()
    foreach ($p in $desired.PSObject.Properties) {
        if ([string]$after.($p.Name) -ne [string]$p.Value) { $stillDiff += $p.Name }
    }
    if ($stillDiff.Count -gt 0) {
        [pscustomobject]@{ Id = 'DSC-02'; Result = 'Failed'; NotApplied = ($stillDiff -join ', '); Before = $before; After = $after; RollbackFile = $rollback }
        exit 1
    }

    [pscustomobject]@{ Id = 'DSC-02'; Result = 'Changed'; Changes = ($diff -join ', '); Before = $before; After = $after; RollbackFile = $rollback }
    exit 0
} catch {
    Write-Error "DSC-02 failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
