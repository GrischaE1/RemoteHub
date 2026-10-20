<#
.SYNOPSIS
    Prepares, tests and (only with -Apply) promotes or demotes a domain controller.
.DESCRIPTION
    Installs the AD DS role, runs the Test-ADDS* prerequisite cmdlets as the dry run, prints the plan and,
    with -Apply, promotes a new forest, a new domain or an additional domain controller (optionally from
    install media) or demotes this one. Warning: this changes the identity infrastructure - rehearse in a
    lab, confirm backups, use a window and named approval, and never run it unattended. The DSRM password is
    read from a SecretManagement vault, never from a parameter or file. Raising functional levels is not
    reversible. The server is not restarted by the script; it exits 3010 instead.
.NOTES
    RemoteHub: id=SRV-07; context=AsMe; os=Server; risk=R2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Check', 'NewForest', 'NewDomain', 'AdditionalDC', 'Demote')]
    [string]$Mode = 'Check',
    # FQDN of the new forest or domain, or of the domain to join as an additional DC.
    [string]$DomainName = '',
    # For NewDomain: FQDN of the parent domain.
    [string]$ParentDomainName = '',
    [string]$NetbiosName = '',
    [string]$SiteName = '',
    [string]$InstallationMediaPath = '',
    # Leave empty to use the cmdlet's default functional level.
    [string]$ForestMode = '',
    [string]$DomainMode = '',
    [bool]$InstallDns = $true,
    # SecretManagement secret holding the DSRM (or, for Demote, the new local Administrator) password.
    [string]$SecretName = 'RemoteHub-DSRM',
    [string]$VaultName = '',
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if (-not $Apply) {
    $WhatIfPreference = $true
    Write-Output 'Dry run: nothing changed. Run with -Apply to make the changes.'
}

function Get-VaultPassword {
    if (-not (Get-Module -ListAvailable -Name Microsoft.PowerShell.SecretManagement)) {
        throw 'Data unavailable: the SecretManagement module is not installed, so no DSRM password can be read.'
    }
    Import-Module Microsoft.PowerShell.SecretManagement
    $p = @{ Name = $SecretName }
    if ($VaultName) { $p.Vault = $VaultName }
    $s = Get-Secret @p
    if ($s -isnot [securestring]) { throw "Secret '$SecretName' is not a SecureString." }
    return $s
}

$exitCode = 0
try {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    $cs = Get-CimInstance -ClassName Win32_ComputerSystem
    if ($os.ProductType -eq 1) { throw 'AD DS needs Windows Server.' }
    $role = Get-WindowsFeature -Name AD-Domain-Services
    $isDc = ($cs.DomainRole -ge 4)
    $state = [pscustomobject]@{
        Computer        = $env:COMPUTERNAME
        OsCaption       = $os.Caption
        AdDsRole        = [string]$role.InstallState
        IsDomainController = $isDc
        PartOfDomain    = $cs.PartOfDomain
        StaticIPv4      = @(Get-NetIPInterface -AddressFamily IPv4 -Dhcp Disabled -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceAlias -notlike 'Loopback*' }).Count -gt 0
        Mode            = $Mode
    }
    $state

    if ($Mode -eq 'Check') {
        if (-not $state.StaticIPv4) { Write-Warning 'No interface has a static IPv4 address; plan IP and DNS before promotion.' }
        Write-Output 'Check only. Choose -Mode NewForest, NewDomain, AdditionalDC or Demote to run the prerequisite tests.'
        exit 0
    }
    if ($Mode -ne 'Demote' -and -not $DomainName) { throw '-DomainName is required for this mode.' }
    if ($Mode -eq 'Demote' -and -not $isDc) { throw 'This server is not a domain controller.' }
    if ($Mode -ne 'Demote' -and $isDc) { Write-Output 'No change: this server is already a domain controller.'; exit 0 }
    if ($InstallationMediaPath -and -not (Test-Path -LiteralPath $InstallationMediaPath)) { throw 'Installation media path not found.' }

    if (-not $role.Installed) {
        if ($PSCmdlet.ShouldProcess('AD-Domain-Services', 'Install-WindowsFeature with management tools')) {
            $r = Install-WindowsFeature -Name AD-Domain-Services -IncludeManagementTools
            if (-not $r.Success) { throw 'Installing the AD DS role failed.' }
        } else {
            Write-Output 'Plan: install the AD-Domain-Services role. The prerequisite tests need the role, so they run after -Apply installs it.'
            exit 0
        }
    }
    Import-Module ADDSDeployment

    $dsrmPassword = Get-VaultPassword
    $common = @{ Force = $true }
    if ($Mode -ne 'Demote') { $common.SafeModeAdministratorPassword = $dsrmPassword; $common.InstallDns = $InstallDns; $common.NoRebootOnCompletion = $true }

    switch ($Mode) {
        'NewForest' {
            $p = $common.Clone(); $p.DomainName = $DomainName
            if ($NetbiosName) { $p.DomainNetbiosName = $NetbiosName }
            if ($ForestMode) { $p.ForestMode = $ForestMode }
            if ($DomainMode) { $p.DomainMode = $DomainMode }
            $test = Test-ADDSForestInstallation @p
            $action = { Install-ADDSForest @p -Confirm:$false }
        }
        'NewDomain' {
            if (-not $ParentDomainName) { throw '-ParentDomainName is required for NewDomain.' }
            $p = $common.Clone(); $p.NewDomainName = ($DomainName -split '\.')[0]; $p.ParentDomainName = $ParentDomainName
            if ($NetbiosName) { $p.NewDomainNetbiosName = $NetbiosName }
            if ($DomainMode) { $p.DomainMode = $DomainMode }
            if ($SiteName) { $p.SiteName = $SiteName }
            $test = Test-ADDSDomainInstallation @p
            $action = { Install-ADDSDomain @p -Confirm:$false }
        }
        'AdditionalDC' {
            $p = $common.Clone(); $p.DomainName = $DomainName
            if ($SiteName) { $p.SiteName = $SiteName }
            if ($InstallationMediaPath) { $p.InstallationMediaPath = $InstallationMediaPath }
            $test = Test-ADDSDomainControllerInstallation @p
            $action = { Install-ADDSDomainController @p -Confirm:$false }
        }
        'Demote' {
            $p = @{ LocalAdministratorPassword = $dsrmPassword; NoRebootOnCompletion = $true; Force = $true }
            $test = Test-ADDSDomainControllerUninstallation @p
            $action = { Uninstall-ADDSDomainController @p -Confirm:$false }
        }
    }

    $test | Select-Object Status, Message | Format-List | Out-String -Width 200
    if ([string]$test.Status -ne 'Success') { throw 'The prerequisite test did not succeed; nothing was changed.' }

    $target = $DomainName
    if ($Mode -eq 'Demote') { $target = $env:COMPUTERNAME }
    if ($PSCmdlet.ShouldProcess($target, "AD DS $Mode")) {
        $result = & $action
        $result | Select-Object Status, Message | Format-List | Out-String -Width 200
        if ([string]$result.Status -ne 'Success') { throw "AD DS $Mode reported status $($result.Status)." }
        Write-Output "AD DS $Mode finished. Restart the server in the approved window to complete it."
        $exitCode = 3010
    } else {
        Write-Output "Plan: AD DS $Mode for '$target' passed the prerequisite test."
    }
} catch {
    Write-Error "AD DS deployment failed: $($_.Exception.Message)" -ErrorAction Continue
    $exitCode = 1
} finally {
    $dsrmPassword = $null
}
exit $exitCode
