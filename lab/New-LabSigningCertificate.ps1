#Requires -Version 5.1
<#
.SYNOPSIS
    Once per build machine, no elevation: creates the lab's self-signed code-signing certificate in CurrentUser\My and
    exports its public part to lab\lab-signing.cer.
.DESCRIPTION
    Builds for the lab are signed with it (build\build-installer.ps1 -LabSigning), so the agent's signature checks run
    unchanged in the lab (credential-provider-design.md section 8.4). Install-LabAgent makes each VM trust it (LocalMachine
    Root + TrustedPublisher) before installing; nothing else trusts it. An existing certificate is reused.
.EXAMPLE
    .\lab\New-LabSigningCertificate.ps1
#>
[CmdletBinding()]
param([int]$Years = 5)
$ErrorActionPreference = 'Stop'

$subject = 'CN=RemoteHub Lab Code Signing'
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Subject $subject -Type CodeSigningCert -CertStoreLocation Cert:\CurrentUser\My `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears($Years)
    Write-Host "Created $subject ($($cert.Thumbprint))"
}
else { Write-Host "Using the existing $subject ($($cert.Thumbprint), valid until $($cert.NotAfter.ToString('yyyy-MM-dd')))" }

$cer = Join-Path $PSScriptRoot 'lab-signing.cer'
[IO.File]::WriteAllBytes($cer, $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
Write-Host "Public certificate: $cer"
