# Builds RemoteHubUpdate.exe (src\RemoteHub.Updater) and its tests with MSBuild from the Visual Studio C++ build tools
# (vswhere finds them; dotnet's MSBuild can't build C++), and runs the tests.
#   powershell -ExecutionPolicy Bypass -File build\build-updater.ps1 [-Configuration Release|Debug] [-Version 0.22.0.0] [-SkipTests]
#
# -Version  Four numbers for the file properties (the MSI's version); default 0.0.0.0.
# Prints the path of the exe last.
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version = '0.0.0.0',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio or the Build Tools with the C++ workload are needed (vswhere.exe not found).' }
$msbuild = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'No MSBuild with the C++ tools (component Microsoft.VisualStudio.Component.VC.Tools.x86.x64).' }

function Build([string]$Project, [string]$Config, [string[]]$More = @()) {
    & $msbuild $Project -nologo -v:minimal -m "-p:Configuration=$Config" -p:Platform=x64 @More
    if ($LASTEXITCODE) { throw "Building $(Split-Path $Project -Leaf) ($Config) failed." }
}

# A rebuild: the version is in the resources, which an incremental build wouldn't update for another -Version.
Build (Join-Path $root 'src\RemoteHub.Updater\RemoteHub.Updater.vcxproj') $Configuration '-t:Rebuild', "-p:RhVersion=$Version"

if (-not $SkipTests) {
    Build (Join-Path $root 'tests\RemoteHub.Updater.Tests\RemoteHub.Updater.Tests.vcxproj') 'Release'
    & (Join-Path $root 'tests\RemoteHub.Updater.Tests\bin\Release\RemoteHub.Updater.Tests.exe')
    if ($LASTEXITCODE) { throw 'Updater tests failed.' }
}

Join-Path $root "src\RemoteHub.Updater\bin\$Configuration\RemoteHubUpdate.exe"
