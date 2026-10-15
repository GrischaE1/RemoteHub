# Authenticode-signs files (SHA-256) with a code-signing certificate from the certificate store, and checks that each
# file now carries a signature by exactly that certificate.
#   build\Sign-Files.ps1 -Path a.exe, b.msi -Certificate <thumbprint> [-TimestampServer http://timestamp.digicert.com]
#
# -Certificate      Thumbprint of a certificate with a private key in CurrentUser\My or LocalMachine\My (CI imports the
#                   release PFX there first; the lab certificate comes from lab\New-LabSigningCertificate.ps1).
# -TimestampServer  RFC 3161 server. Empty: no timestamp (the lab certificate; a release always passes one, so the
#                   signature stays valid after the certificate expires).
#
# The check compares the signer's thumbprint, not the signature status: the lab certificate is trusted only on the lab
# VMs, so on the build machine its status is "not trusted" although the file is signed correctly.
param(
    [Parameter(Mandatory)][string[]]$Path,
    [Parameter(Mandatory)][string]$Certificate,
    [string]$TimestampServer
)
$ErrorActionPreference = "Stop"

$thumbprint = $Certificate -replace '\s', ''
$cert = @(Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My |
        Where-Object { $_.Thumbprint -eq $thumbprint -and $_.HasPrivateKey }) | Select-Object -First 1
if (-not $cert) { throw "No certificate with thumbprint $thumbprint and a private key in CurrentUser\My or LocalMachine\My." }
if (-not ($cert.EnhancedKeyUsageList | Where-Object ObjectId -eq '1.3.6.1.5.5.7.3.3')) {
    throw "Certificate $thumbprint ($($cert.Subject)) is not a code-signing certificate."
}

foreach ($file in $Path) {
    if (-not (Test-Path $file)) { throw "Can't sign $file - it doesn't exist." }
    $signArgs = @{ FilePath = $file; Certificate = $cert; HashAlgorithm = 'SHA256'; IncludeChain = 'NotRoot' }
    if ($TimestampServer) { $signArgs.TimestampServer = $TimestampServer }
    # Timestamp servers fail now and then: a few tries before giving up.
    for ($try = 1; ; $try++) {
        $result = Set-AuthenticodeSignature @signArgs
        if ($result.SignerCertificate) { break }
        if ($try -ge 3) { throw "Signing $file failed: $($result.StatusMessage)" }
        Start-Sleep -Seconds (5 * $try)
    }
    $signer = (Get-AuthenticodeSignature $file).SignerCertificate
    if (-not $signer -or $signer.Thumbprint -ne $thumbprint) { throw "$file is not signed by $thumbprint after signing." }
    "Signed: $(Split-Path $file -Leaf)"
}
