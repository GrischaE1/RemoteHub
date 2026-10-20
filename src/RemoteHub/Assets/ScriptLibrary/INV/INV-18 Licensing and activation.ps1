<#
.SYNOPSIS
    Reports Windows edition, license status, channel, grace time and the KMS host in use.
.DESCRIPTION
    Queries SoftwareLicensingProduct for the Windows product that has a partial key, plus
    Get-WindowsEdition and the subscription activation indicator. Only status and channel are
    reported: the full product key, including the firmware-embedded OEM key, is never read,
    logged or returned, and the partial key is left out as well. -Sensor returns a Boolean
    "licensed".
.NOTES
    RemoteHub: id=INV-18; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [switch]$Sensor
)

$ErrorActionPreference = 'Stop'

$windowsAppId = '55c92734-d682-4d71-983e-d6ec3f16059f'
$statusNames = @{
    0 = 'Unlicensed'; 1 = 'Licensed'; 2 = 'Out-of-box grace'; 3 = 'Out-of-tolerance grace'
    4 = 'Non-genuine grace'; 5 = 'Notification'; 6 = 'Extended grace'
}

try {
    # Only the listed properties are fetched, so no key material is ever pulled into the session.
    $props = @('Name', 'Description', 'LicenseStatus', 'GracePeriodRemaining', 'KeyManagementServiceMachine',
               'DiscoveredKeyManagementServiceMachineName', 'KeyManagementServicePort', 'ProductKeyChannel')
    $products = @(Get-CimInstance -ClassName SoftwareLicensingProduct -Property $props `
        -Filter "ApplicationID = '$windowsAppId' AND PartialProductKey IS NOT NULL")

    if ($products.Count -eq 0) {
        if ($Sensor) { 'Data unavailable'; exit 1 }
        [PSCustomObject]@{ LicenseStatus = 'Data unavailable'; Detail = 'No Windows license with an installed key was found.' }
        exit 1
    }
    $p = $products | Sort-Object LicenseStatus -Descending | Select-Object -First 1

    $licensed = ([int]$p.LicenseStatus -eq 1)
    if ($Sensor) {
        $licensed
        if ($licensed) { exit 0 }
        exit 1
    }

    $channel = [string]$p.ProductKeyChannel
    if (-not $channel) {
        if ($p.Description -match 'VOLUME_KMSCLIENT') { $channel = 'Volume:GVLK' }
        elseif ($p.Description -match 'OEM') { $channel = 'OEM' }
        elseif ($p.Description -match 'RETAIL') { $channel = 'Retail' }
        else { $channel = 'Data unavailable' }
    }

    $kms = [string]$p.KeyManagementServiceMachine
    if (-not $kms) { $kms = [string]$p.DiscoveredKeyManagementServiceMachineName }
    if ($kms -and $p.KeyManagementServicePort) { $kms = "${kms}:$($p.KeyManagementServicePort)" }
    if (-not $kms) { $kms = 'None' }

    $edition = 'Data unavailable'
    try { $edition = (Get-WindowsEdition -Online).Edition } catch {
        $edition = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID
    }

    # Subscription activation steps a Pro OEM/retail license up to Enterprise without a new key,
    # so an Enterprise edition on a non-volume channel is the visible indicator.
    $subscription = 'Not detected'
    if ([string]$edition -match 'Enterprise' -and $channel -match 'OEM|Retail') { $subscription = 'Likely' }

    $status = $statusNames[[int]$p.LicenseStatus]
    if (-not $status) { $status = "Unknown ($($p.LicenseStatus))" }

    [PSCustomObject]@{
        Edition                  = $edition
        Product                  = $p.Name
        Licensed                 = $licensed
        LicenseStatus            = $status
        Channel                  = $channel
        GraceMinutesRemaining    = [int]$p.GracePeriodRemaining
        KmsHost                  = $kms
        SubscriptionActivation   = $subscription
    } | ConvertTo-Json -Depth 5

    if ($licensed) { exit 0 }
    exit 1
} catch {
    Write-Error "Licensing check failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
