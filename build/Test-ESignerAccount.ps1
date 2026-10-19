# Checks the eSigner account without signing anything (check-signing.yml): CodeSignTool's get_credential_ids signs in
# with ES_USERNAME / ES_PASSWORD and lists the account's signing credentials, which must include ES_CREDENTIAL_ID;
# credential_info then reads that credential's certificate, which must be the one with -Thumbprint (when CodeSignTool
# prints it). The TOTP secret isn't used by either command - only a signing checks it.
#   build\Test-ESignerAccount.ps1 -Thumbprint <SIGNING_CERTIFICATE_THUMBPRINT>
# Needs CODE_SIGN_TOOL_PATH and JAVA_HOME (Get-CodeSignTool.ps1). Nothing it prints contains the password or TOTP
# secret; GitHub masks the secrets in the log as well.
param([Parameter(Mandatory)][string]$Thumbprint)
$ErrorActionPreference = "Stop"

foreach ($name in 'CODE_SIGN_TOOL_PATH', 'JAVA_HOME', 'ES_USERNAME', 'ES_PASSWORD', 'ES_CREDENTIAL_ID') {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name isn't set." }
}
$tool = $env:CODE_SIGN_TOOL_PATH
$jar = @(Get-ChildItem (Join-Path $tool 'jar') -Filter 'code_sign_tool-*.jar') | Select-Object -First 1
if (-not $jar) { throw "No code_sign_tool-*.jar in $tool\jar." }
$java = Join-Path $env:JAVA_HOME 'bin\java.exe'

function Invoke-CodeSignTool([string[]]$Arguments) {
    # CodeSignTool reads conf\code_sign_tool.properties relative to the working folder; Java logs to stderr.
    Push-Location $tool
    $ErrorActionPreference = 'Continue'
    try { $output = & $java -jar $jar.FullName @Arguments 2>&1; $exit = $LASTEXITCODE }
    finally { $ErrorActionPreference = 'Stop'; Pop-Location }
    $text = ($output | Out-String).Trim()
    if ($exit -ne 0 -or $text -match '(?im)^\s*Error') { throw "CodeSignTool $($Arguments[0]) failed: $text" }
    $text
}

$account = @("-username=$env:ES_USERNAME", "-password=$env:ES_PASSWORD")
$ids = Invoke-CodeSignTool (@('get_credential_ids') + $account)
if ($ids -notmatch [regex]::Escape($env:ES_CREDENTIAL_ID)) {
    throw "Signed in, but ES_CREDENTIAL_ID isn't one of the account's signing credentials:`n$ids"
}
"Username and password: OK. ES_CREDENTIAL_ID is one of the account's signing credentials."

$info = Invoke-CodeSignTool (@('credential_info') + $account + @("-credential_id=$env:ES_CREDENTIAL_ID"))
$pem = [regex]::Match($info, '-----BEGIN CERTIFICATE-----[\s\S]+?-----END CERTIFICATE-----')
if ($pem.Success) {
    $body = $pem.Value -replace '-----(BEGIN|END) CERTIFICATE-----', '' -replace '\s', ''
    $cert = New-Object Security.Cryptography.X509Certificates.X509Certificate2 (, [Convert]::FromBase64String($body))
    if ($cert.Thumbprint -ne ($Thumbprint -replace '\s', '')) {
        throw "The credential's certificate is $($cert.Subject) ($($cert.Thumbprint)), not $Thumbprint (SIGNING_CERTIFICATE_THUMBPRINT)."
    }
    "Certificate: $($cert.Subject), valid until $($cert.NotAfter.ToString('yyyy-MM-dd')) - matches SIGNING_CERTIFICATE_THUMBPRINT."
}
else {
    "Credential info (no certificate in it to compare with SIGNING_CERTIFICATE_THUMBPRINT):"
    $info
}
