<#
.SYNOPSIS
    Inventories the signed-in user's certificates and flags expired, expiring, SHA-1 and weak ones.
.DESCRIPTION
    Same checks as INV-12 for the CurrentUser stores, focused on authentication certificates used
    for VPN, Wi-Fi, S/MIME and certificate-based sign-in (Client Authentication, Smart Card Logon,
    Secure Email). SYSTEM cannot see the CurrentUser store, so run this as the user. Windows Hello
    for Business keys live in the NGC container and will not appear. Subjects are redacted unless
    -IncludeIdentity is set. -Sensor returns the days to the nearest auth certificate expiry.
.NOTES
    RemoteHub: id=INV-13; context=User; os=Client; risk=R0
#>
[CmdletBinding()]
param(
    [string[]]$Stores = @('My', 'TrustedPeople', 'Root', 'CA'),
    [int]$ExpiryDays = 30,
    [switch]$AuthenticationOnly,
    [switch]$IncludeIdentity,
    [switch]$Sensor,
    [ValidateSet('Table', 'Json', 'Csv')]
    [string]$OutputFormat = 'Table'
)

$ErrorActionPreference = 'Stop'

$authOids = @('1.3.6.1.5.5.7.3.2', '1.3.6.1.4.1.311.20.2.2', '1.3.6.1.5.5.7.3.4')

try {
    if ([Security.Principal.WindowsIdentity]::GetCurrent().IsSystem) {
        'Data unavailable: the CurrentUser store must be read in the user''s context, not as SYSTEM.'
        exit 1
    }

    $now = Get-Date
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($store in $Stores) {
        $path = "Cert:\CurrentUser\$store"
        if (-not (Test-Path -LiteralPath $path)) { continue }
        foreach ($c in Get-ChildItem -LiteralPath $path) {
            if ($c -isnot [System.Security.Cryptography.X509Certificates.X509Certificate2]) { continue }
            $ekuOids = @($c.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId })
            $isAuth = $false
            foreach ($o in $ekuOids) { if ($authOids -contains $o) { $isAuth = $true } }
            if ($AuthenticationOnly -and -not $isAuth) { continue }

            $keySize = 0
            try { $keySize = $c.PublicKey.Key.KeySize } catch { Write-Verbose 'Key size unreadable.' }
            $sigAlg = $c.SignatureAlgorithm.FriendlyName
            $daysLeft = [int][math]::Floor(($c.NotAfter - $now).TotalDays)
            $flags = @()
            if ($daysLeft -lt 0) { $flags += 'Expired' } elseif ($daysLeft -le $ExpiryDays) { $flags += 'Expiring' }
            if ($sigAlg -match 'sha1' -and $c.Subject -ne $c.Issuer) { $flags += 'SHA-1' }
            if ($c.PublicKey.Oid.FriendlyName -eq 'RSA' -and $keySize -gt 0 -and $keySize -lt 2048) { $flags += 'Weak RSA' }

            $subject = $c.Subject
            # Personal certificates name the user; issuers are CAs and are kept.
            if (-not $IncludeIdentity -and $store -in @('My', 'TrustedPeople')) { $subject = '[REDACTED]' }

            $rows.Add([PSCustomObject]@{
                Store          = $store
                Subject        = $subject
                Issuer         = $c.Issuer
                Thumbprint     = $c.Thumbprint
                NotAfter       = $c.NotAfter.ToString('yyyy-MM-dd')
                DaysLeft       = $daysLeft
                HasPrivateKey  = $c.HasPrivateKey
                Authentication = $isAuth
                Eku            = (@($c.EnhancedKeyUsageList | ForEach-Object { $_.FriendlyName }) -join ', ')
                KeySize        = $keySize
                SignatureAlg   = $sigAlg
                Flags          = ($flags -join ', ')
            })
        }
    }

    if ($Sensor) {
        $auth = @($rows | Where-Object { $_.Authentication -and $_.HasPrivateKey -and $_.Store -eq 'My' })
        if ($auth.Count -eq 0) { 'Data unavailable'; exit 1 }
        $nearest = [int]($auth | Measure-Object -Property DaysLeft -Minimum).Minimum
        $nearest
        if ($nearest -lt 0) { exit 1 }
        exit 0
    }

    switch ($OutputFormat) {
        'Json'  { ConvertTo-Json -InputObject @($rows) -Depth 5 }
        'Csv'   { $rows | ConvertTo-Csv -NoTypeInformation }
        default { $rows | Select-Object Store, Subject, Issuer, NotAfter, DaysLeft, Authentication, Flags | Format-Table -AutoSize | Out-String -Width 4096 }
    }
    exit 0
} catch {
    Write-Error "User certificate inventory failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
