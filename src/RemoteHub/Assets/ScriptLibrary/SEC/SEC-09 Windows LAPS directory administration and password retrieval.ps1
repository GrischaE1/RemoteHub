<#
.SYNOPSIS
    Audits and administers Windows LAPS in Active Directory: rights audit, schema, permissions, rotation.
.DESCRIPTION
    Actions: AuditPermissions (default, read-only), ExtendSchema, GrantSelfPermission, GrantReadPermission,
    ExpirePassword and PasswordStatus. Without -Apply every change is a dry run. The schema extension is
    forest-wide and cannot be undone: rehearse in a lab, have a current system state backup and named
    approval, and pass -AcknowledgeIrreversible. Break-glass retrieval is an audited action: this script
    records the ticket reference and password metadata only and never prints the password - read it in
    the LAPS UI or AD Users and Computers, and expire it afterwards so it rotates.
.NOTES
    RemoteHub: id=SEC-09; context=AsMe; os=Server; risk=R2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('AuditPermissions','ExtendSchema','GrantSelfPermission','GrantReadPermission','ExpirePassword','PasswordStatus')]
    [string]$Action = 'AuditPermissions',
    [string]$OrganizationalUnit = '',
    [string[]]$AllowedPrincipal = @(),
    [string]$ComputerName = '',
    [string]$TicketReference = '',
    [switch]$AcknowledgeIrreversible,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if (-not $Apply) { $WhatIfPreference = $true }

function Get-DefaultNamingContext {
    $root = [ADSI]'LDAP://RootDSE'
    return [string]$root.defaultNamingContext
}

try {
    if (-not (Get-Module -ListAvailable -Name LAPS)) {
        [PSCustomObject]@{ Status = 'Data unavailable'; Reason = 'The LAPS PowerShell module is not installed (April 2023 update or later)' }
        exit 1
    }
    Import-Module LAPS
    if (-not $OrganizationalUnit) { $OrganizationalUnit = Get-DefaultNamingContext }

    switch ($Action) {
        'AuditPermissions' {
            $rows = @(Find-LapsADExtendedRights -Identity $OrganizationalUnit -IncludeComputers:$false | ForEach-Object {
                [PSCustomObject]@{ Object = $_.ObjectDN; ReadPasswordHolders = (@($_.ExtendedRightHolders) -join '; ') }
            })
            $rows
            [PSCustomObject]@{ Scope = $OrganizationalUnit; ObjectsChecked = $rows.Count }
        }
        'ExtendSchema' {
            $schemaNc = [string]([ADSI]'LDAP://RootDSE').schemaNamingContext
            $present = [ADSI]::Exists("LDAP://CN=ms-LAPS-Password,$schemaNc")
            if ($present) { [PSCustomObject]@{ Result = 'No change: the Windows LAPS schema attributes already exist' }; break }
            [PSCustomObject]@{ Plan = 'Extend the Active Directory schema with the Windows LAPS attributes (forest-wide, irreversible)'; Schema = $schemaNc }
            if ($Apply -and -not $AcknowledgeIrreversible) { throw 'The schema extension cannot be undone; rerun with -AcknowledgeIrreversible after approval and a tested backup.' }
            if ($PSCmdlet.ShouldProcess($schemaNc, 'Extend schema for Windows LAPS')) {
                Update-LapsADSchema -Confirm:$false
                [PSCustomObject]@{ Result = 'Schema extended' }
            }
        }
        'GrantSelfPermission' {
            if ($PSCmdlet.ShouldProcess($OrganizationalUnit, 'Let computers write their own LAPS password')) {
                $r = Set-LapsADComputerSelfPermission -Identity $OrganizationalUnit
                [PSCustomObject]@{ Result = 'Self permission set'; Scope = $r.DistinguishedName }
            }
        }
        'GrantReadPermission' {
            if ($AllowedPrincipal.Count -eq 0) { throw 'Name the groups that may read passwords with -AllowedPrincipal.' }
            $current = @(Find-LapsADExtendedRights -Identity $OrganizationalUnit -IncludeComputers:$false |
                Where-Object { $_.ObjectDN -eq $OrganizationalUnit } | ForEach-Object { $_.ExtendedRightHolders })
            $missing = @($AllowedPrincipal | Where-Object { $p = $_; -not ($current | Where-Object { $_ -like "*\$p" -or $_ -eq $p }) })
            if ($missing.Count -eq 0) { [PSCustomObject]@{ Result = 'No change: every principal already holds the read right' }; break }
            if ($PSCmdlet.ShouldProcess($OrganizationalUnit, "Grant password read to $($missing -join ', ')")) {
                $null = Set-LapsADReadPasswordPermission -Identity $OrganizationalUnit -AllowedPrincipals $missing
                [PSCustomObject]@{ Result = 'Read permission granted'; Principals = ($missing -join ', ') }
            }
        }
        'ExpirePassword' {
            if (-not $ComputerName) { throw 'Name the device with -ComputerName.' }
            if ($PSCmdlet.ShouldProcess($ComputerName, 'Expire the LAPS password so the device rotates it at its next policy cycle')) {
                Set-LapsADPasswordExpirationTime -Identity $ComputerName -WhenEffective (Get-Date)
                [PSCustomObject]@{ Result = 'Password expired; it rotates at the next policy cycle'; Computer = $ComputerName; Ticket = $TicketReference }
            }
        }
        'PasswordStatus' {
            if (-not $ComputerName) { throw 'Name the device with -ComputerName.' }
            if (-not $TicketReference) { throw 'Password access is audited; pass -TicketReference.' }
            # Without -AsPlainText the password stays a SecureString and is dropped here; only metadata is shown.
            $info = Get-LapsADPassword -Identity $ComputerName
            if (-not $info) { [PSCustomObject]@{ Computer = $ComputerName; Status = 'Data unavailable (no AD-backed password; Entra ID backup is read in the Entra portal)' }; exit 1 }
            [PSCustomObject]@{
                Computer = $ComputerName; Ticket = $TicketReference; Account = $info.Account
                PasswordUpdateTime = $info.PasswordUpdateTime; ExpirationTimestamp = $info.ExpirationTimestamp
                Source = [string]$info.Source; DecryptionStatus = [string]$info.DecryptionStatus
                Note = 'The password is not printed. Read it in the LAPS UI, then run ExpirePassword.'
            }
        }
    }
    if ($WhatIfPreference -and $Action -ne 'AuditPermissions' -and $Action -ne 'PasswordStatus') {
        Write-Output 'Dry run: nothing changed. Run with -Apply to make the changes.'
    }
    exit 0
} catch {
    Write-Error "LAPS directory task failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
