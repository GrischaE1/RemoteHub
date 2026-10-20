<#
.SYNOPSIS
    Inventories LocalMachine certificates and flags expired, expiring, SHA-1 and weak RSA ones.
.DESCRIPTION
    Records subject, issuer, thumbprint, validity, private key presence, key usage, EKU, key size,
    signature algorithm and template for the chosen LocalMachine stores. Flags certificates that
    are expired or expire within -ExpiryDays, SHA-1 signatures and RSA keys under 2048 bits.
    Private keys are never exported or opened. Large server stores are slow, so filter with
    -Stores and -OnlyFlagged. With -Issuer or -Template the script returns one sensor value:
    days until the nearest expiry among matching certificates.
.NOTES
    RemoteHub: id=INV-12; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [string[]]$Stores = @('My', 'Root', 'CA', 'TrustedPublisher', 'WebHosting', 'Remote Desktop'),
    [int]$ExpiryDays = 30,
    [switch]$OnlyFlagged,
    [string]$Issuer = '',
    [string]$Template = '',
    [ValidateSet('Table', 'Json', 'Csv')]
    [string]$OutputFormat = 'Table'
)

$ErrorActionPreference = 'Stop'
$location = 'LocalMachine'

function Get-TemplateName($Cert) {
    # V2 templates carry OID 1.3.6.1.4.1.311.21.7, V1 templates the name extension 1.3.6.1.4.1.311.20.2.
    foreach ($oid in @('1.3.6.1.4.1.311.21.7', '1.3.6.1.4.1.311.20.2')) {
        $ext = $Cert.Extensions | Where-Object { $_.Oid.Value -eq $oid } | Select-Object -First 1
        if ($ext) {
            $text = $ext.Format($false)
            $m = [regex]::Match($text, '^(?:[^=]+=)?([^,(]+)')
            if ($m.Success) { return $m.Groups[1].Value.Trim() }
        }
    }
    return ''
}

try {
    $now = Get-Date
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($store in $Stores) {
        $path = "Cert:\$location\$store"
        if (-not (Test-Path -LiteralPath $path)) { Write-Verbose "Store $store not present."; continue }
        foreach ($c in Get-ChildItem -LiteralPath $path) {
            if ($c -isnot [System.Security.Cryptography.X509Certificates.X509Certificate2]) { continue }
            $keySize = 0
            try { $keySize = $c.PublicKey.Key.KeySize } catch { Write-Verbose "Key size unreadable for $($c.Thumbprint)." }
            $keyAlg = $c.PublicKey.Oid.FriendlyName
            $sigAlg = $c.SignatureAlgorithm.FriendlyName
            $eku = @($c.EnhancedKeyUsageList | ForEach-Object { $_.FriendlyName }) -join ', '
            $ku = $c.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.15' } | Select-Object -First 1
            $daysLeft = [int][math]::Floor(($c.NotAfter - $now).TotalDays)

            $flags = @()
            if ($daysLeft -lt 0) { $flags += 'Expired' } elseif ($daysLeft -le $ExpiryDays) { $flags += 'Expiring' }
            # Self-signed roots are trusted by thumbprint, so their SHA-1 signature is not a weakness.
            if ($sigAlg -match 'sha1' -and $c.Subject -ne $c.Issuer) { $flags += 'SHA-1' }
            if ($keyAlg -eq 'RSA' -and $keySize -gt 0 -and $keySize -lt 2048) { $flags += 'Weak RSA' }

            $rows.Add([PSCustomObject]@{
                Store         = $store
                Subject       = $c.Subject
                Issuer        = $c.Issuer
                Thumbprint    = $c.Thumbprint
                NotBefore     = $c.NotBefore.ToString('yyyy-MM-dd')
                NotAfter      = $c.NotAfter.ToString('yyyy-MM-dd')
                DaysLeft      = $daysLeft
                HasPrivateKey = $c.HasPrivateKey
                KeyUsage      = if ($ku) { $ku.Format($false) } else { '' }
                Eku           = $eku
                KeyAlgorithm  = $keyAlg
                KeySize       = $keySize
                SignatureAlg  = $sigAlg
                Template      = Get-TemplateName $c
                Flags         = ($flags -join ', ')
            })
        }
    }

    if ($Issuer -or $Template) {
        $sel = @($rows | Where-Object {
            ((-not $Issuer) -or $_.Issuer -like "*$Issuer*") -and ((-not $Template) -or $_.Template -like "*$Template*")
        })
        if ($sel.Count -eq 0) { 'Data unavailable'; exit 1 }
        $nearest = ($sel | Measure-Object -Property DaysLeft -Minimum).Minimum
        [int]$nearest
        if ($nearest -lt 0) { exit 1 }
        exit 0
    }

    $out = $rows
    if ($OnlyFlagged) { $out = @($rows | Where-Object { $_.Flags }) }
    switch ($OutputFormat) {
        'Json'  { ConvertTo-Json -InputObject @($out) -Depth 5 }
        'Csv'   { $out | ConvertTo-Csv -NoTypeInformation }
        default { $out | Select-Object Store, Subject, NotAfter, DaysLeft, HasPrivateKey, SignatureAlg, KeySize, Flags | Format-Table -AutoSize | Out-String -Width 4096 }
    }
    Write-Verbose "$($rows.Count) certificates examined."
    exit 0
} catch {
    Write-Error "Certificate inventory failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
