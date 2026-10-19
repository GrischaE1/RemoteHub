# Downloads SSL.com's CodeSignTool (eSigner signing, Sign-Files.ps1 -ESigner) at a pinned version, checks its SHA-256 and
# finds a Java it can run on. Used by release.yml and check-signing.yml, so the pin lives in one place.
#   build\Get-CodeSignTool.ps1 -Destination <folder>
# Writes tool=<folder> and java=<JAVA_HOME> to GITHUB_OUTPUT when it runs in GitHub Actions, and returns both.
param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = "Stop"

# From SSL.com's own repository (github.com/SSLcom/CodeSignTool), pinned by hash like the actions.
$version = 'v1.3.2'
$sha256 = 'f14b1e1ef14bfa1fd00279c363aab0debbf5dcfba0e4bcdce5d22bb771de0e3a'

$zip = Join-Path ([IO.Path]::GetTempPath()) "CodeSignTool-$version-$([guid]::NewGuid().ToString('N')).zip"
try {
    Invoke-WebRequest "https://github.com/SSLcom/CodeSignTool/releases/download/$version/CodeSignTool-$version.zip" -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $sha256) { throw "CodeSignTool-$version.zip has the hash $hash, not the pinned one." }
    Expand-Archive $zip -DestinationPath $Destination -Force
}
finally { Remove-Item $zip -Force -ErrorAction SilentlyContinue }

# CodeSignTool 1.3 needs Java 11 or later; a GitHub runner's default JAVA_HOME may be Java 8.
$java = @($env:JAVA_HOME_21_X64, $env:JAVA_HOME_17_X64, $env:JAVA_HOME_11_X64 | Where-Object { $_ })[0]
if (-not $java) { throw "No Java 11 or later (JAVA_HOME_21_X64, _17_X64 or _11_X64) for CodeSignTool." }

$tool = (Resolve-Path $Destination).Path
if ($env:GITHUB_OUTPUT) {
    # No BOM: Windows PowerShell's -Encoding utf8 would put one before the first name.
    [IO.File]::AppendAllText($env:GITHUB_OUTPUT, "tool=$tool`njava=$java`n", (New-Object Text.UTF8Encoding $false))
}
[pscustomobject]@{ Tool = $tool; Java = $java }
