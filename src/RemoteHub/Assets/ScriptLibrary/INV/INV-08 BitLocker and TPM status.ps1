<#
.SYNOPSIS
    Reports BitLocker protection per volume, TPM state and Secure Boot state.
.DESCRIPTION
    Per volume: protection status, encryption method, percentage encrypted and key protector types.
    Also TPM presence, readiness, spec and manufacturer version, and Secure Boot. Recovery
    passwords are never read, printed or stored - only protector types are reported. On Windows
    Server the BitLocker feature must be installed for the cmdlets to exist; on Home editions
    device encryption may be all that is available, which is reported as "Data unavailable".
.NOTES
    RemoteHub: id=INV-08; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [string[]]$ApprovedMethods = @('XtsAes256', 'XtsAes128')
)

$ErrorActionPreference = 'Stop'

try {
    $volumes = 'Data unavailable'
    $osProtected = $false
    if (Get-Command -Name Get-BitLockerVolume -ErrorAction SilentlyContinue) {
        try {
            $volumes = @(Get-BitLockerVolume | ForEach-Object {
                # Only the protector type is taken; the protector objects carry recovery passwords.
                $types = @($_.KeyProtector | ForEach-Object { [string]$_.KeyProtectorType }) | Sort-Object -Unique
                [PSCustomObject]@{
                    MountPoint       = $_.MountPoint
                    VolumeType       = [string]$_.VolumeType
                    ProtectionStatus = [string]$_.ProtectionStatus
                    EncryptionMethod = [string]$_.EncryptionMethod
                    PercentEncrypted = $_.EncryptionPercentage
                    VolumeStatus     = [string]$_.VolumeStatus
                    ProtectorTypes   = @($types)
                }
            })
            $os = $volumes | Where-Object { $_.VolumeType -eq 'OperatingSystem' } | Select-Object -First 1
            if ($os -and $os.ProtectionStatus -eq 'On' -and $ApprovedMethods -contains $os.EncryptionMethod) { $osProtected = $true }
        } catch {
            Write-Verbose "Get-BitLockerVolume failed: $($_.Exception.Message)"
        }
    } else {
        Write-Verbose 'BitLocker cmdlets are not available (feature not installed or Home edition).'
    }

    $tpm = [ordered]@{ Present = 'Data unavailable'; Ready = 'Data unavailable'; SpecVersion = 'Data unavailable'; ManufacturerVersion = 'Data unavailable' }
    try {
        $t = Get-Tpm
        $tpm.Present = [bool]$t.TpmPresent
        $tpm.Ready = [bool]$t.TpmReady
        $tpm.ManufacturerVersion = [string]$t.ManufacturerVersion
    } catch {
        Write-Verbose "Get-Tpm failed: $($_.Exception.Message)"
    }
    try {
        $w = Get-CimInstance -Namespace 'root\CIMV2\Security\MicrosoftTpm' -ClassName Win32_Tpm
        if ($w) {
            $tpm.SpecVersion = ([string]$w.SpecVersion -split ',')[0].Trim()
            if ($tpm.Present -eq 'Data unavailable') { $tpm.Present = $true }
        } elseif ($tpm.Present -eq 'Data unavailable') {
            $tpm.Present = $false
        }
    } catch {
        Write-Verbose "Win32_Tpm query failed: $($_.Exception.Message)"
    }

    $secureBoot = 'Data unavailable'
    try {
        if (Confirm-SecureBootUEFI) { $secureBoot = 'On' } else { $secureBoot = 'Off' }
    } catch {
        if ($_.Exception.Message -match 'not supported') { $secureBoot = 'Not supported' }
    }

    $result = [PSCustomObject]@{
        OsVolumeProtectedApproved = $osProtected
        ApprovedMethods           = $ApprovedMethods
        Volumes                   = $volumes
        Tpm                       = [PSCustomObject]$tpm
        SecureBoot                = $secureBoot
    }
    $json = $result | ConvertTo-Json -Depth 5
    # Belt and braces: a 48-digit recovery password must never leave this script.
    $json -replace '\d{6}(-\d{6}){7}', '[REDACTED]'

    if ($osProtected) { exit 0 }
    exit 1
} catch {
    Write-Error "BitLocker and TPM status failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
