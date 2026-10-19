# Builds the RemoteHub Agent (self-contained, trimmed) and its MSI, then RemoteHub (self-contained) and its MSI.
# The agent MSI is also placed inside RemoteHub (Agent\RemoteHub-Agent-x64.msi), so RemoteHub can install it on devices.
#   powershell -ExecutionPolicy Bypass -File build\build-installer.ps1 [-Version 0.9.0] [-Configuration Release] [-OutputPath artifacts]
#
# -Version     Defaults to <Version> in src\RemoteHub\RemoteHub.csproj. CI passes the version from the git tag (v0.9.0 -> 0.9.0).
#              A pre-release suffix (0.9.0-beta.1) goes into the app; the MSI gets the numeric part, because MSI versions
#              are numbers only (major.minor.build, each part at most 255.255.65535).
# -OutputPath  Where the MSIs end up (default: artifacts). Publish folders: <OutputPath>\publish, <OutputPath>\publish-agent.
#
# Signing (Authenticode, SHA-256; Sign-Files.ps1): RemoteHubAgent.exe, RemoteHub.exe and both MSIs, each before it is
# packed into the next step.
# -SigningCertificate  Thumbprint of the code-signing certificate: in the certificate store, or with -ESigner the one
#                      every signature is checked against.
# -ESigner             Signs through SSL.com eSigner (release.yml; Sign-Files.ps1 has the environment it needs). Each
#                      signed file counts against the yearly signings: 4 per release, 5 with -CredentialProvider.
# -LabSigning          The lab certificate instead (lab\New-LabSigningCertificate.ps1); only the lab VMs trust it.
# -TimestampServer     Defaults to DigiCert's for -SigningCertificate; the lab signs without a timestamp, eSigner with its own.
# Without -SigningCertificate or -LabSigning, nothing is signed.
#
# -CredentialProvider  Builds the credential provider (build-credential-provider.ps1, needs the Visual Studio C++ tools)
#                      and adds it to the agent MSI behind AUTOSIGNIN (credential-provider-design.md §8.4). Only with a
#                      signing certificate: the provider is never built unsigned.
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$OutputPath,
    # Leaves out the start-time check (Test-StartTime.ps1), e.g. on a machine without a screen session (always left out
    # with -LabSigning)
    [switch]$SkipStartTime,
    [string]$SigningCertificate,
    [switch]$ESigner,
    [switch]$LabSigning,
    [string]$TimestampServer,
    [switch]$CredentialProvider
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

if ($ESigner -and -not $SigningCertificate) { throw "-ESigner needs -SigningCertificate: the thumbprint every signature is checked against." }
if ($LabSigning) {
    if ($SigningCertificate) { throw "Use either -SigningCertificate or -LabSigning, not both." }
    $SigningCertificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq 'CN=RemoteHub Lab Code Signing' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1 -ExpandProperty Thumbprint
    if (-not $SigningCertificate) { throw "No lab signing certificate in CurrentUser\My - run lab\New-LabSigningCertificate.ps1 first." }
}
elseif ($SigningCertificate -and -not $ESigner -and -not $PSBoundParameters.ContainsKey('TimestampServer')) { $TimestampServer = 'http://timestamp.digicert.com' }
function Sign([string[]]$Files) {
    if (-not $SigningCertificate) { return }
    & "$PSScriptRoot\Sign-Files.ps1" -Path $Files -Certificate $SigningCertificate -TimestampServer $TimestampServer -ESigner:$ESigner
}
if (-not $SigningCertificate) {
    if ($CredentialProvider) { throw "The credential provider is never built unsigned - pass -SigningCertificate or -LabSigning." }
    Write-Warning "No signing certificate given - the build is not signed."
}

if (-not $Version) {
    $csproj = [xml][IO.File]::ReadAllText("$root\src\RemoteHub\RemoteHub.csproj")
    $Version = @($csproj.Project.PropertyGroup.Version | Where-Object { $_ })[0]
}
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z][0-9A-Za-z.-]*)?$' -or
    [int]$Matches[1] -gt 255 -or [int]$Matches[2] -gt 255 -or [int]$Matches[3] -gt 65535) {
    throw "Version '$Version' must look like 0.9.0 or 0.9.0-beta.1 (major and minor at most 255, build at most 65535)."
}
$msiVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3])"

if (-not $OutputPath) { $OutputPath = "artifacts" }
$out = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $root $OutputPath }
$publish = Join-Path $out "publish"
$publishAgent = Join-Path $out "publish-agent"
New-Item -ItemType Directory -Force -Path $out | Out-Null
# .NET's delete with retries: Windows PowerShell's Remove-Item -Recurse sometimes fails on deep trees ("Could not find a part of the path").
foreach ($dir in $publish, $publishAgent) {
    for ($try = 1; Test-Path $dir; $try++) {
        try { [IO.Directory]::Delete($dir, $true) }
        catch { if ($try -ge 5) { throw }; Start-Sleep -Milliseconds 500 }
    }
}

# --- Agent
dotnet publish "$root\src\RemoteHub.Agent\RemoteHub.Agent.csproj" -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=none -o $publishAgent
if ($LASTEXITCODE) { throw "agent publish failed" }
Sign "$publishAgent\RemoteHubAgent.exe"
& "$publishAgent\RemoteHubAgent.exe" selftest
if ($LASTEXITCODE) { throw "agent self-test failed" }

$providerArgs = @()
if ($CredentialProvider) {
    $built = & "$PSScriptRoot\build-credential-provider.ps1" -Configuration Release | Select-Object -Last 1
    if ($LASTEXITCODE) { throw "credential provider build failed" }
    # A copy outside the publish folder: everything in there goes into the MSI unconditionally.
    $providerDll = Join-Path $out "credential-provider\RemoteHubCredentialProvider.dll"
    New-Item -ItemType Directory -Force -Path (Split-Path $providerDll) | Out-Null
    Copy-Item $built $providerDll -Force
    Sign $providerDll
    $providerArgs = @("-p:CredentialProviderDll=$providerDll")
}

# -t:Rebuild: WiX's incremental build doesn't notice a new MSI name (another version) and then fails to copy it.
dotnet build "$root\installer\RemoteHub.Agent.Installer\RemoteHub.Agent.Installer.wixproj" -t:Rebuild -c $Configuration `
    -p:Version=$msiVersion -p:ReleaseVersion=$Version -p:AgentPublishDir=$publishAgent -o $out @providerArgs
if ($LASTEXITCODE) { throw "agent installer build failed" }
$agentMsi = Join-Path $out "RemoteHub-Agent-$Version-x64.msi"
if (-not (Test-Path $agentMsi)) { throw "Expected $agentMsi was not produced." }
Sign $agentMsi

# --- RemoteHub (with the agent MSI inside)

dotnet publish "$root\src\RemoteHub\RemoteHub.csproj" -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=none -o $publish
if ($LASTEXITCODE) { throw "publish failed" }
Sign "$publish\RemoteHub.exe"
New-Item -ItemType Directory -Force -Path "$publish\Agent" | Out-Null
Copy-Item $agentMsi "$publish\Agent\RemoteHub-Agent-x64.msi"

# Start-time baseline (< 3 s with 500 connections). Not on CI runners: their speed varies too much for a time limit.
# Not for lab builds either: they are made on the lab host, often while VMs run, and a busy host fails the limit
# without anything being wrong with the build. Release builds keep the check.
if ($SkipStartTime -or $LabSigning -or $env:GITHUB_ACTIONS) { Write-Warning "Start-time check skipped." }
else {
    & "$PSScriptRoot\Test-StartTime.ps1" -Publish $publish
}

dotnet build "$root\installer\RemoteHub.Installer.wixproj" -t:Rebuild -c $Configuration `
    -p:Version=$msiVersion -p:ReleaseVersion=$Version -p:PublishDir=$publish -o $out
if ($LASTEXITCODE) { throw "installer build failed" }

$msi = Join-Path $out "RemoteHub-$Version-x64.msi"
if (-not (Test-Path $msi)) { throw "Expected $msi was not produced." }
Sign $msi
"Built: $agentMsi  ($([math]::Round((Get-Item $agentMsi).Length / 1MB, 1)) MB)"
"Built: $msi  ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB)"
