# Builds a self-contained release and the MSI installer.
#   powershell -ExecutionPolicy Bypass -File build\build-installer.ps1 [-Version 0.9.0] [-Configuration Release] [-OutputPath artifacts]
#
# -Version     Defaults to <Version> in src\RemoteHub\RemoteHub.csproj. CI passes the version from the git tag (v0.9.0 -> 0.9.0).
#              A pre-release suffix (0.9.0-beta.1) goes into the app; the MSI gets the numeric part, because MSI versions
#              are numbers only (major.minor.build, each part at most 255.255.65535).
# -OutputPath  Where the MSI ends up (default: artifacts). The publish folder is <OutputPath>\publish.
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$OutputPath
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
New-Item -ItemType Directory -Force -Path $out | Out-Null
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

dotnet publish "$root\src\RemoteHub\RemoteHub.csproj" -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=none -o $publish
if ($LASTEXITCODE) { throw "publish failed" }

dotnet build "$root\installer\RemoteHub.Installer.wixproj" -c $Configuration `
    -p:Version=$msiVersion -p:ReleaseVersion=$Version -p:PublishDir=$publish -o $out
if ($LASTEXITCODE) { throw "installer build failed" }

$msi = Join-Path $out "RemoteHub-$Version-x64.msi"
if (-not (Test-Path $msi)) { throw "Expected $msi was not produced." }
"Built: $msi  ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB)"
