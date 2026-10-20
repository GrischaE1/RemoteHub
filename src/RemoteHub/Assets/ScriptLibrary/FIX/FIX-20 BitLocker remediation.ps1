<#
.SYNOPSIS
    Remediates BitLocker on a volume: resumes protection, adds a TPM protector, escrows and rotates recovery keys.
.DESCRIPTION
    Handles the states found by INV-08: suspended protection (resume), missing TPM protector (add), recovery key
    not escrowed (back up to Entra ID or Active Directory and confirm), and on request rotation of a used recovery
    password and enabling encryption. Warning: enabling encryption without a confirmed escrow path risks permanent
    data loss, so encryption only starts after the new recovery password is escrowed and confirmed. Prerequisites
    are checked first: TPM ready, recovery environment enabled (FIX-21), no pending restart. Changing the
    encryption method is out of scope. No key material is ever written to output or logs.
.NOTES
    RemoteHub: id=FIX-20; context=System; os=Both; risk=R2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$MountPoint = $env:SystemDrive,
    [ValidateSet('Auto', 'EntraID', 'ActiveDirectory')]
    [string]$EscrowTarget = 'Auto',
    [switch]$EnableEncryption,
    [ValidateSet('XtsAes128', 'XtsAes256')]
    [string]$EncryptionMethod = 'XtsAes128',
    [switch]$RotateRecoveryPassword,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if (-not $Apply) { $WhatIfPreference = $true }
$logDir = Join-Path $env:ProgramData 'RemoteHub\ScriptLogs'
$escrowFile = Join-Path $logDir 'FIX-20-escrowed-protectors.json'
$steps = New-Object System.Collections.Generic.List[object]
function Add-Step([string]$Step, [string]$Result) { $steps.Add([pscustomobject]@{ Step = $Step; Result = $Result }) }

function Get-SafeState([string]$Mp) {
    # Only states and protector types; the volume object also carries recovery passwords, which must not be output.
    $v = Get-BitLockerVolume -MountPoint $Mp
    [pscustomobject]@{
        MountPoint = $v.MountPoint; VolumeStatus = "$($v.VolumeStatus)"; Protection = "$($v.ProtectionStatus)"
        Method = "$($v.EncryptionMethod)"; Percent = $v.EncryptionPercentage
        Protectors = (@($v.KeyProtector | ForEach-Object { "$($_.KeyProtectorType)" }) -join ', ')
    }
}

function Get-EscrowedIds {
    if (Test-Path -LiteralPath $escrowFile) { return @((Get-Content -LiteralPath $escrowFile -Raw | ConvertFrom-Json)) }
    return @()
}

function Invoke-Escrow([string]$Mp, [string]$ProtectorId, [string]$Target) {
    $start = Get-Date
    if ($Target -eq 'EntraID') {
        $null = BackupToAAD-BitLockerKeyProtector -MountPoint $Mp -KeyProtectorId $ProtectorId
        # Event 845 in the BitLocker management log is the client's confirmation that Entra ID accepted the key.
        $deadline = (Get-Date).AddSeconds(60); $ok = $false
        while (-not $ok -and (Get-Date) -lt $deadline) {
            $ok = [bool](Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-BitLocker/BitLocker Management'; Id = 845; StartTime = $start } -MaxEvents 1 -ErrorAction SilentlyContinue)
            if (-not $ok) { Start-Sleep -Seconds 2 }
        }
        if (-not $ok) { throw "Escrow to Entra ID not confirmed for protector $ProtectorId." }
    }
    else { $null = Backup-BitLockerKeyProtector -MountPoint $Mp -KeyProtectorId $ProtectorId }
    if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
    $ids = @(Get-EscrowedIds) + $ProtectorId | Select-Object -Unique
    ConvertTo-Json -InputObject @($ids) | Set-Content -LiteralPath $escrowFile -Encoding UTF8
}

try {
    if (-not (Get-Command -Name Get-BitLockerVolume -ErrorAction SilentlyContinue)) { Write-Output 'Data unavailable: the BitLocker module is not installed (BitLocker feature missing on this server?).'; exit 1 }
    $before = Get-SafeState $MountPoint
    $vol = Get-BitLockerVolume -MountPoint $MountPoint
    $isOs = "$($vol.VolumeType)" -eq 'OperatingSystem'

    $tpm = Get-Tpm
    $reagent = Join-Path $env:windir 'System32\Recovery\ReAgent.xml'
    $winre = 'Data unavailable'
    if (Test-Path -LiteralPath $reagent) {
        # ReAgent.xml is not localised, unlike reagentc output; InstallState 1 means WinRE is enabled.
        $winre = (([xml](Get-Content -LiteralPath $reagent -Raw)).WindowsRE.InstallState.state -eq '1')
    }
    $pending = (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') -or (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')
    Add-Step 'Prerequisites' ("TPM ready={0}; WinRE enabled={1}; restart pending={2}" -f $tpm.TpmReady, $winre, $pending)

    $target = $EscrowTarget
    if ($target -eq 'Auto') {
        if (Test-Path -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo') { $target = 'EntraID' }
        elseif ((Get-CimInstance -ClassName Win32_ComputerSystem).PartOfDomain) { $target = 'ActiveDirectory' }
        else { $target = 'None' }
    }
    Add-Step 'Escrow path' $target

    if ("$($vol.VolumeStatus)" -eq 'FullyDecrypted') {
        if (-not $EnableEncryption) { Add-Step 'Encryption' 'Not encrypted; use -EnableEncryption to encrypt (separately approved).' }
        elseif ($target -eq 'None') { throw 'No escrow path (not Entra ID or domain joined): encryption not started.' }
        elseif (-not $tpm.TpmReady -or $winre -ne $true -or $pending) { throw 'Prerequisites not met (TPM, WinRE or pending restart): encryption not started.' }
        elseif ($PSCmdlet.ShouldProcess($MountPoint, "Add recovery password, escrow to $target, then enable $EncryptionMethod with TPM (used space only)")) {
            $null = Add-BitLockerKeyProtector -MountPoint $MountPoint -RecoveryPasswordProtector
            $rp = @((Get-BitLockerVolume -MountPoint $MountPoint).KeyProtector | Where-Object { "$($_.KeyProtectorType)" -eq 'RecoveryPassword' })
            foreach ($p in $rp) { Invoke-Escrow $MountPoint $p.KeyProtectorId $target }
            $null = Enable-BitLocker -MountPoint $MountPoint -EncryptionMethod $EncryptionMethod -TpmProtector -UsedSpaceOnly -SkipHardwareTest
            Add-Step 'Encryption' 'Recovery password escrowed and confirmed; encryption started'
        }
    }
    else {
        if ("$($vol.ProtectionStatus)" -eq 'Off' -and "$($vol.VolumeStatus)" -eq 'FullyEncrypted') {
            if ($PSCmdlet.ShouldProcess($MountPoint, 'Resume BitLocker protection')) { $null = Resume-BitLocker -MountPoint $MountPoint; Add-Step 'Resume protection' 'Done' }
        }
        $types = @($vol.KeyProtector | ForEach-Object { "$($_.KeyProtectorType)" })
        if ($isOs -and -not ($types | Where-Object { $_ -like 'Tpm*' })) {
            if (-not $tpm.TpmReady) { Add-Step 'TPM protector' 'Missing, but the TPM is not ready; not added' }
            elseif ($PSCmdlet.ShouldProcess($MountPoint, 'Add TPM protector')) { $null = Add-BitLockerKeyProtector -MountPoint $MountPoint -TpmProtector; Add-Step 'TPM protector' 'Added' }
        }
        $rp = @($vol.KeyProtector | Where-Object { "$($_.KeyProtectorType)" -eq 'RecoveryPassword' })
        if ($rp.Count -eq 0) {
            if ($target -eq 'None') { throw 'No recovery password protector and no escrow path.' }
            if ($PSCmdlet.ShouldProcess($MountPoint, "Add recovery password protector and escrow to $target")) {
                $null = Add-BitLockerKeyProtector -MountPoint $MountPoint -RecoveryPasswordProtector
                $rp = @((Get-BitLockerVolume -MountPoint $MountPoint).KeyProtector | Where-Object { "$($_.KeyProtectorType)" -eq 'RecoveryPassword' })
            }
        }
        if ($RotateRecoveryPassword -and $rp.Count -gt 0 -and $target -ne 'None') {
            if ($PSCmdlet.ShouldProcess($MountPoint, "Rotate recovery password: add new, escrow to $target, confirm, then remove the old one(s)")) {
                $oldIds = @($rp | ForEach-Object { $_.KeyProtectorId })
                $null = Add-BitLockerKeyProtector -MountPoint $MountPoint -RecoveryPasswordProtector
                $new = @((Get-BitLockerVolume -MountPoint $MountPoint).KeyProtector | Where-Object { "$($_.KeyProtectorType)" -eq 'RecoveryPassword' -and $oldIds -notcontains $_.KeyProtectorId })
                foreach ($p in $new) { Invoke-Escrow $MountPoint $p.KeyProtectorId $target }
                foreach ($id in $oldIds) { $null = Remove-BitLockerKeyProtector -MountPoint $MountPoint -KeyProtectorId $id }
                Add-Step 'Rotate recovery password' 'New password escrowed and confirmed; old protector(s) removed'
                $rp = $new
            }
        }
        $escrowed = @(Get-EscrowedIds)
        foreach ($p in $rp) {
            if ($escrowed -contains $p.KeyProtectorId) { Add-Step "Escrow $($p.KeyProtectorId)" 'Already confirmed earlier, no change'; continue }
            if ($target -eq 'None') { Add-Step "Escrow $($p.KeyProtectorId)" 'No escrow path'; continue }
            if ($PSCmdlet.ShouldProcess($p.KeyProtectorId, "Back up recovery password to $target and confirm")) {
                Invoke-Escrow $MountPoint $p.KeyProtectorId $target
                Add-Step "Escrow $($p.KeyProtectorId)" "Backed up to $target and confirmed"
            }
        }
    }

    $after = Get-SafeState $MountPoint
    $before | Add-Member -NotePropertyName When -NotePropertyValue 'Before' -PassThru
    $after | Add-Member -NotePropertyName When -NotePropertyValue 'After' -PassThru
    $steps
    if (-not $Apply) { Write-Output 'Dry run: nothing changed. Run with -Apply to make the changes.'; exit 0 }
    $unescrowed = @((Get-BitLockerVolume -MountPoint $MountPoint).KeyProtector | Where-Object { "$($_.KeyProtectorType)" -eq 'RecoveryPassword' -and @(Get-EscrowedIds) -notcontains $_.KeyProtectorId })
    $compliant = $after.Protection -eq 'On' -and $unescrowed.Count -eq 0
    if ($compliant -or $after.VolumeStatus -eq 'EncryptionInProgress') { exit 0 }
    exit 1
}
catch {
    $steps
    Write-Output "FIX-20 failed: $($_.Exception.Message)"
    exit 1
}
