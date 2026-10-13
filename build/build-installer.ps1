# Builds the RemoteHub Agent (self-contained, trimmed) and its MSI, then RemoteHub (self-contained) and its MSI.
# The agent MSI is also placed inside RemoteHub (Agent\RemoteHub-Agent-x64.msi), so RemoteHub can install it on devices.
#   powershell -ExecutionPolicy Bypass -File build\build-installer.ps1 [-Version 0.9.0] [-Configuration Release] [-OutputPath artifacts]
#
# -Version     Defaults to <Version> in src\RemoteHub\RemoteHub.csproj. CI passes the version from the git tag (v0.9.0 -> 0.9.0).
#              A pre-release suffix (0.9.0-beta.1) goes into the app; the MSI gets the numeric part, because MSI versions
#              are numbers only (major.minor.build, each part at most 255.255.65535).
# -OutputPath  Where the MSIs end up (default: artifacts). Publish folders: <OutputPath>\publish, <OutputPath>\publish-agent.
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$OutputPath,
    # Leaves out the start-time check (Test-StartTime.ps1), e.g. on a machine without a screen session
    [switch]$SkipStartTime
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

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
& "$publishAgent\RemoteHubAgent.exe" selftest
if ($LASTEXITCODE) { throw "agent self-test failed" }

# -t:Rebuild: WiX's incremental build doesn't notice a new MSI name (another version) and then fails to copy it.
dotnet build "$root\installer\RemoteHub.Agent.Installer\RemoteHub.Agent.Installer.wixproj" -t:Rebuild -c $Configuration `
    -p:Version=$msiVersion -p:ReleaseVersion=$Version -p:AgentPublishDir=$publishAgent -o $out
if ($LASTEXITCODE) { throw "agent installer build failed" }
$agentMsi = Join-Path $out "RemoteHub-Agent-$Version-x64.msi"
if (-not (Test-Path $agentMsi)) { throw "Expected $agentMsi was not produced." }

# --- RemoteHub (with the agent MSI inside)

dotnet publish "$root\src\RemoteHub\RemoteHub.csproj" -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=none -o $publish
if ($LASTEXITCODE) { throw "publish failed" }
New-Item -ItemType Directory -Force -Path "$publish\Agent" | Out-Null
Copy-Item $agentMsi "$publish\Agent\RemoteHub-Agent-x64.msi"

# Start-time baseline (< 3 s with 500 connections). Not on CI runners: their speed varies too much for a time limit.
if ($SkipStartTime -or $env:GITHUB_ACTIONS) { Write-Warning "Start-time check skipped." }
else {
    & "$PSScriptRoot\Test-StartTime.ps1" -Publish $publish
}

dotnet build "$root\installer\RemoteHub.Installer.wixproj" -t:Rebuild -c $Configuration `
    -p:Version=$msiVersion -p:ReleaseVersion=$Version -p:PublishDir=$publish -o $out
if ($LASTEXITCODE) { throw "installer build failed" }

$msi = Join-Path $out "RemoteHub-$Version-x64.msi"
if (-not (Test-Path $msi)) { throw "Expected $msi was not produced." }
"Built: $agentMsi  ($([math]::Round((Get-Item $agentMsi).Length / 1MB, 1)) MB)"
"Built: $msi  ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB)"
