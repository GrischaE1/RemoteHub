# Authenticode-signs files (SHA-256) and checks that each file now carries a signature by exactly the expected certificate.
#   build\Sign-Files.ps1 -Path a.exe, b.msi -Certificate <thumbprint> [-TimestampServer http://timestamp.digicert.com]
#   build\Sign-Files.ps1 -Path a.exe, b.msi -Certificate <thumbprint> -ESigner
#
# -Certificate      Thumbprint of the signing certificate. Without -ESigner it must be in CurrentUser\My or LocalMachine\My
#                   with its private key (the lab certificate comes from lab\New-LabSigningCertificate.ps1). With -ESigner
#                   it is only checked against: the key stays in SSL.com's cloud.
# -TimestampServer  RFC 3161 server. Empty: no timestamp (the lab certificate; a release always has one, so the
#                   signature stays valid after the certificate expires).
# -ESigner          Signs through SSL.com eSigner with CodeSignTool (release builds, release.yml). CodeSignTool's folder
#                   comes from CODE_SIGN_TOOL_PATH, the account from ES_USERNAME, ES_PASSWORD, ES_CREDENTIAL_ID and
#                   ES_TOTP_SECRET - environment variables, never parameters, so they don't end up in a log; they reach
#                   CodeSignTool through a Java argument file, not its command line (JavaArgFile.ps1). eSigner
#                   timestamps the signature itself (ts.ssl.com).
#
# eSigner signings are counted (240 a year): one attempt per file, no retries - a failure fails the build instead of
# signing again. The check compares the signer's thumbprint, not the signature status: the lab certificate is trusted
# only on the lab VMs, so on the build machine its status is "not trusted" although the file is signed correctly.
param(
    [Parameter(Mandatory)][string[]]$Path,
    [Parameter(Mandatory)][string]$Certificate,
    [string]$TimestampServer,
    [switch]$ESigner
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'JavaArgFile.ps1')

$thumbprint = $Certificate -replace '\s', ''
foreach ($file in $Path) { if (-not (Test-Path $file)) { throw "Can't sign $file - it doesn't exist." } }

if ($ESigner) {
    $tool = $env:CODE_SIGN_TOOL_PATH
    if (-not $tool -or -not (Test-Path (Join-Path $tool 'jar'))) { throw "CODE_SIGN_TOOL_PATH doesn't point to CodeSignTool." }
    $jar = @(Get-ChildItem (Join-Path $tool 'jar') -Filter 'code_sign_tool-*.jar') | Select-Object -First 1
    if (-not $jar) { throw "No code_sign_tool-*.jar in $tool\jar." }
    if (-not $env:JAVA_HOME) { throw "JAVA_HOME isn't set - CodeSignTool needs Java." }
    $java = Join-Path $env:JAVA_HOME 'bin\java.exe'
    foreach ($name in 'ES_USERNAME', 'ES_PASSWORD', 'ES_CREDENTIAL_ID', 'ES_TOTP_SECRET') {
        if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name isn't set - eSigner can't sign." }
    }
}
else {
    $cert = @(Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My |
            Where-Object { $_.Thumbprint -eq $thumbprint -and $_.HasPrivateKey }) | Select-Object -First 1
    if (-not $cert) { throw "No certificate with thumbprint $thumbprint and a private key in CurrentUser\My or LocalMachine\My." }
    if (-not ($cert.EnhancedKeyUsageList | Where-Object ObjectId -eq '1.3.6.1.5.5.7.3.3')) {
        throw "Certificate $thumbprint ($($cert.Subject)) is not a code-signing certificate."
    }
}

foreach ($file in $Path) {
    $full = (Resolve-Path $file).Path
    if ($ESigner) {
        # CodeSignTool reads conf\code_sign_tool.properties relative to the working folder.
        Push-Location $tool
        # Java writes its log to stderr: with 'Stop', Windows PowerShell would end at the first line of it.
        $ErrorActionPreference = 'Continue'
        # The credentials go through an argument file, not the command line (JavaArgFile.ps1), deleted right after.
        $argFile = Join-Path ([IO.Path]::GetTempPath()) ("esigner-" + [guid]::NewGuid().ToString('N') + '.args')
        try {
            Write-JavaArgFile -Path $argFile -Arguments '-jar', $jar.FullName, 'sign', "-username=$env:ES_USERNAME", "-password=$env:ES_PASSWORD",
                "-credential_id=$env:ES_CREDENTIAL_ID", "-totp_secret=$env:ES_TOTP_SECRET", "-input_file_path=$full", '-override=true'
            $output = & $java "@$argFile" 2>&1
            $exit = $LASTEXITCODE
        }
        finally { Remove-Item -LiteralPath $argFile -Force -ErrorAction SilentlyContinue; $ErrorActionPreference = 'Stop'; Pop-Location }
        # CodeSignTool doesn't echo the credentials; its own messages say why a signing failed.
        $text = ($output | Out-String).Trim()
        if ($exit -ne 0 -or $text -match '(?im)^\s*Error') { throw "eSigner couldn't sign $(Split-Path $file -Leaf): $text" }
    }
    else {
        $signArgs = @{ FilePath = $full; Certificate = $cert; HashAlgorithm = 'SHA256'; IncludeChain = 'NotRoot' }
        if ($TimestampServer) { $signArgs.TimestampServer = $TimestampServer }
        # Timestamp servers fail now and then: a few tries before giving up.
        for ($try = 1; ; $try++) {
            $result = Set-AuthenticodeSignature @signArgs
            if ($result.SignerCertificate) { break }
            if ($try -ge 3) { throw "Signing $file failed: $($result.StatusMessage)" }
            Start-Sleep -Seconds (5 * $try)
        }
    }
    $signer = (Get-AuthenticodeSignature $full).SignerCertificate
    if (-not $signer -or $signer.Thumbprint -ne $thumbprint) { throw "$file is not signed by $thumbprint after signing." }
    "Signed: $(Split-Path $file -Leaf)"
}
