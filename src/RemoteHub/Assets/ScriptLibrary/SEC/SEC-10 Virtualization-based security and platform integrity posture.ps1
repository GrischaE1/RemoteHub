<#
.SYNOPSIS
    Reports virtualization-based security, Credential Guard, memory integrity, TPM, Secure Boot and LSA protection.
.DESCRIPTION
    Reads Win32_DeviceGuard for VBS status, configured and running security services and code integrity
    enforcement, plus Get-Tpm, Confirm-SecureBootUEFI and the LSA RunAsPPL value. Gives a compliant
    Boolean per feature. A feature can be configured but not running until the next restart. Credential
    Guard is not supported on domain controllers. Drivers that blocked memory integrity are listed from
    the Code Integrity log as findings.
.NOTES
    RemoteHub: id=SEC-10; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [ValidateSet('VBS','CredentialGuard','MemoryIntegrity','SecureLaunch','SecureBoot','Tpm','LsaProtection')]
    [string[]]$RequiredFeature = @('VBS','SecureBoot','Tpm','LsaProtection'),
    [int[]]$BlockedDriverEventId = @(3087),
    [int]$EventDays = 30
)

$ErrorActionPreference = 'Stop'
$unavailable = 'Data unavailable'

try {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    $isDc = ($os.ProductType -eq 2)

    $dg = $null
    try { $dg = Get-CimInstance -Namespace 'root\Microsoft\Windows\DeviceGuard' -ClassName Win32_DeviceGuard } catch { Write-Verbose "Win32_DeviceGuard unavailable: $($_.Exception.Message)" }
    $vbsState = $unavailable; $configured = @(); $running = @(); $kmci = $unavailable; $umci = $unavailable
    if ($dg) {
        # Values as documented for Win32_DeviceGuard.
        $vbsState = @{ 0 = 'Off'; 1 = 'Configured, not running'; 2 = 'Running' }[[int]$dg.VirtualizationBasedSecurityStatus]
        $configured = @($dg.SecurityServicesConfigured | ForEach-Object { [int]$_ })
        $running = @($dg.SecurityServicesRunning | ForEach-Object { [int]$_ })
        $ciMap = @{ 0 = 'Off'; 1 = 'Audit'; 2 = 'Enforced' }
        $kmci = $ciMap[[int]$dg.CodeIntegrityPolicyEnforcementStatus]
        $umci = $ciMap[[int]$dg.UsermodeCodeIntegrityPolicyEnforcementStatus]
    }
    function Get-ServiceState([int]$id) {
        if (-not $dg) { return $unavailable }
        if ($running -contains $id) { return 'Running' }
        if ($configured -contains $id) { return 'Configured, not running (restart pending?)' }
        return 'Off'
    }

    $tpm = $unavailable
    try { $t = Get-Tpm; $tpm = "Present=$($t.TpmPresent); Ready=$($t.TpmReady)"; $tpmOk = ($t.TpmPresent -and $t.TpmReady) } catch { $tpmOk = $null }

    $secureBoot = $unavailable; $sbOk = $null
    try { $sbOk = [bool](Confirm-SecureBootUEFI); $secureBoot = $(if ($sbOk) { 'On' } else { 'Off' }) }
    catch [System.PlatformNotSupportedException] { $secureBoot = 'Not supported (legacy BIOS)'; $sbOk = $false }
    catch { Write-Verbose "Secure Boot query failed: $($_.Exception.Message)" }

    $ppl = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -ErrorAction SilentlyContinue).RunAsPPL
    $lsa = 'Off'; if ($ppl -eq 1 -or $ppl -eq 2) { $lsa = 'On' }

    $credGuard = Get-ServiceState 1
    if ($isDc) { $credGuard = 'Not supported on domain controllers' }
    $features = [ordered]@{
        VBS = @{ State = $vbsState; Ok = $(if ($dg) { $vbsState -eq 'Running' } else { $null }) }
        CredentialGuard = @{ State = $credGuard; Ok = $(if ($isDc) { $true } elseif ($dg) { $running -contains 1 } else { $null }) }
        MemoryIntegrity = @{ State = (Get-ServiceState 2); Ok = $(if ($dg) { $running -contains 2 } else { $null }) }
        SecureLaunch = @{ State = (Get-ServiceState 3); Ok = $(if ($dg) { $running -contains 3 } else { $null }) }
        SecureBoot = @{ State = $secureBoot; Ok = $sbOk }
        Tpm = @{ State = $tpm; Ok = $tpmOk }
        LsaProtection = @{ State = $lsa; Ok = ($lsa -eq 'On') }
    }

    # Drivers that stop memory integrity are reported by Code Integrity; verify the event ID on your builds.
    $blocking = @()
    try {
        $since = (Get-Date).AddDays(-$EventDays)
        $blocking = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-CodeIntegrity/Operational'; Id = $BlockedDriverEventId; StartTime = $since } -ErrorAction SilentlyContinue |
            ForEach-Object { if ($_.Message -match '([^\\\s]+\.sys)') { $Matches[1] } } | Sort-Object -Unique)
    } catch { Write-Verbose "Code Integrity log not readable: $($_.Exception.Message)" }

    $report = [ordered]@{}
    foreach ($k in $features.Keys) {
        $ok = $features[$k].Ok
        $report[$k] = [PSCustomObject]@{ State = $features[$k].State; Compliant = $(if ($null -eq $ok) { $unavailable } else { [bool]$ok }); Required = ($RequiredFeature -contains $k) }
    }
    $failing = @($RequiredFeature | Where-Object { $features[$_].Ok -ne $true })
    [PSCustomObject]@{
        Compliant = ($failing.Count -eq 0); NotCompliant = ($failing -join ', ')
        DomainController = $isDc; KernelCodeIntegrity = $kmci; UserModeCodeIntegrity = $umci
        Features = [PSCustomObject]$report
        MemoryIntegrityBlockingDrivers = $blocking
    } | ConvertTo-Json -Depth 5
    if ($failing.Count -eq 0) { exit 0 }
    exit 1
} catch {
    Write-Error "VBS posture check failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
