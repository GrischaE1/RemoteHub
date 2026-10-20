<#
.SYNOPSIS
    Determines the device's join type, tenant, device ID and MDM enrolment state.
.DESCRIPTION
    Reports Entra joined, hybrid joined, domain joined or workgroup, the tenant name and device
    ID, a pending hybrid join, and MDM enrolments with their provider. Uses the CloudDomainJoin and
    Enrollments registry keys and Win32_ComputerSystem rather than localised dsregcmd text.
    Enrolment keys can contain user principal names; they are redacted unless -IncludeIdentity is
    set. Entra registration is per user and is not visible from SYSTEM.
.NOTES
    RemoteHub: id=INV-09; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [switch]$IncludeIdentity,
    [switch]$SensorString
)

$ErrorActionPreference = 'Stop'

function Get-RegValue([string]$Path, [string]$Name) {
    try { return (Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop).$Name } catch { return $null }
}

function Protect-Identity([string]$Value) {
    if ($IncludeIdentity -or [string]::IsNullOrEmpty($Value)) { return $Value }
    return '[REDACTED]'
}

try {
    $cs = Get-CimInstance -ClassName Win32_ComputerSystem
    $domainJoined = [bool]$cs.PartOfDomain

    $cdj = 'HKLM:\SYSTEM\CurrentControlSet\Control\CloudDomainJoin'
    $joinKeys = @()
    if (Test-Path -LiteralPath "$cdj\JoinInfo") { $joinKeys = @(Get-ChildItem -LiteralPath "$cdj\JoinInfo") }
    $entraJoined = $joinKeys.Count -gt 0

    $tenantId = 'Data unavailable'
    $tenantName = 'Data unavailable'
    $deviceId = 'Data unavailable'
    if ($entraJoined) {
        $ji = $joinKeys[0]
        $tid = Get-RegValue $ji.PSPath 'TenantId'
        if ($tid) {
            $tenantId = $tid
            $dn = Get-RegValue "$cdj\TenantInfo\$tid" 'DisplayName'
            if ($dn) { $tenantName = $dn }
        }
        # The JoinInfo subkey is named after the device certificate thumbprint; its subject is CN=<device id>.
        $cert = Get-ChildItem -LiteralPath "Cert:\LocalMachine\My\$($ji.PSChildName)" -ErrorAction SilentlyContinue
        if ($cert -and $cert.Subject -match 'CN=([0-9a-fA-F-]{36})') { $deviceId = $Matches[1] }
    }

    $joinType = 'Workgroup'
    if ($entraJoined -and $domainJoined) { $joinType = 'Hybrid joined' }
    elseif ($entraJoined) { $joinType = 'Entra joined' }
    elseif ($domainJoined) { $joinType = 'Domain joined' }

    $hybridPending = $false
    if ($domainJoined -and -not $entraJoined) {
        try {
            $task = Get-ScheduledTask -TaskPath '\Microsoft\Windows\Workplace Join\' -TaskName 'Automatic-Device-Join' -ErrorAction Stop
            if ($task.State -ne 'Disabled') { $hybridPending = $true }
        } catch {
            Write-Verbose 'Automatic-Device-Join task not found.'
        }
    }

    $enrollments = @()
    $enrRoot = 'HKLM:\SOFTWARE\Microsoft\Enrollments'
    if (Test-Path -LiteralPath $enrRoot) {
        foreach ($k in Get-ChildItem -LiteralPath $enrRoot -ErrorAction SilentlyContinue) {
            if ($k.PSChildName -notmatch '^[0-9A-Fa-f-]{36}$') { continue }
            $provider = Get-RegValue $k.PSPath 'ProviderID'
            $state = Get-RegValue $k.PSPath 'EnrollmentState'
            if (-not $provider) { continue }
            $enrollments += [PSCustomObject]@{
                Provider        = $provider
                EnrollmentState = $state
                Active          = ($state -eq 1)
                Upn             = Protect-Identity ([string](Get-RegValue $k.PSPath 'UPN'))
            }
        }
    }
    $mdmEnrolled = @($enrollments | Where-Object { $_.Active }).Count -gt 0

    if ($SensorString) {
        $s = $joinType
        if ($hybridPending) { $s = 'Domain joined, hybrid join pending' }
        if ($mdmEnrolled) { $s += ', MDM enrolled' } else { $s += ', not MDM enrolled' }
        $s
        exit 0
    }

    [PSCustomObject]@{
        JoinType           = $joinType
        HybridJoinPending  = $hybridPending
        Domain             = if ($domainJoined) { $cs.Domain } else { '' }
        TenantName         = $tenantName
        TenantId           = $tenantId
        DeviceId           = $deviceId
        MdmEnrolled        = $mdmEnrolled
        Enrollments        = $enrollments
        EntraRegistered    = 'Data unavailable (per-user state, run INV-10 as the user)'
    } | ConvertTo-Json -Depth 5
    exit 0
} catch {
    Write-Error "Join state check failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
