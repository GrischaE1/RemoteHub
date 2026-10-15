# Builds the credential provider (src\RemoteHub.CredentialProvider) and its tests with MSBuild from the Visual Studio
# C++ build tools (vswhere finds them; dotnet's MSBuild can't build C++). Runs the unit tests, and the fuzzer for a while.
#   powershell -ExecutionPolicy Bypass -File build\build-credential-provider.ps1 [-Configuration Release|Lab|Debug] [-FuzzSeconds 60]
#
# -Configuration  Release (default) is the DLL that goes into the agent MSI; Lab is the lab-only build (see the vcxproj).
# -FuzzSeconds    How long libFuzzer runs on the pipe message parser (0: not at all).
# Prints the path of the DLL last.
param(
    [ValidateSet('Release', 'Lab', 'Debug')][string]$Configuration = 'Release',
    [int]$FuzzSeconds = 0,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio or the Build Tools with the C++ workload are needed (vswhere.exe not found).' }
$msbuild = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'No MSBuild with the C++ tools (component Microsoft.VisualStudio.Component.VC.Tools.x86.x64).' }

function Build([string]$Project, [string]$Config) {
    & $msbuild $Project -nologo -v:minimal -m "-p:Configuration=$Config" -p:Platform=x64
    if ($LASTEXITCODE) { throw "Building $(Split-Path $Project -Leaf) ($Config) failed." }
}

$provider = Join-Path $root 'src\RemoteHub.CredentialProvider\RemoteHub.CredentialProvider.vcxproj'
Build $provider $Configuration
# The Lab DLL talks to the phase 1 test server instead of the agent service: build it alongside.
if ($Configuration -eq 'Lab') { Build (Join-Path $root 'tests\RemoteHub.CredentialProvider.TestServer\RemoteHub.CredentialProvider.TestServer.vcxproj') 'Release' }

$tests = Join-Path $root 'tests\RemoteHub.CredentialProvider.Tests\RemoteHub.CredentialProvider.Tests.vcxproj'
$testExe = Join-Path $root 'tests\RemoteHub.CredentialProvider.Tests\bin\Release\RemoteHub.CredentialProvider.Tests.exe'
if (-not $SkipTests -or $FuzzSeconds -gt 0) { Build $tests 'Release' }
if (-not $SkipTests) {
    & $testExe
    if ($LASTEXITCODE) { throw 'Credential provider tests failed.' }
}

if ($FuzzSeconds -gt 0) {
    Build $tests 'Fuzz'
    $fuzzer = Join-Path $root 'tests\RemoteHub.CredentialProvider.Tests\bin\Fuzz\RemoteHub.CredentialProvider.Tests.exe'
    # Seeds: well-formed messages written by the unit tests; the fuzzer adds what it finds (artifacts, not committed).
    $work = Join-Path $root 'artifacts\fuzz-corpus'
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    & $testExe --corpus $work
    # The ASan runtime DLL sits next to the compiler; the fuzzer needs it on the PATH.
    $env:PATH = (Split-Path ((& $vswhere -latest -products * -find 'VC\Tools\MSVC\**\bin\Hostx64\x64\clang_rt.asan_dynamic-x86_64.dll') | Select-Object -First 1)) + ';' + $env:PATH
    & $fuzzer $work "-max_total_time=$FuzzSeconds" -max_len=4096
    if ($LASTEXITCODE) { throw 'The fuzzer found a crash (see the crash-* file in the current folder).' }
}

if ($Configuration -eq 'Lab') {
    # Staged for lab\Start-LabRun.ps1 (copied to LabRoot\credential-provider), signed with the lab certificate when
    # there is one, like everything else the lab installs.
    $stage = Join-Path $root 'artifacts\credential-provider-lab'
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    $files = @(
        (Join-Path $root 'src\RemoteHub.CredentialProvider\bin\Lab\RemoteHubCredentialProvider.Lab.dll'),
        (Join-Path $root 'tests\RemoteHub.CredentialProvider.TestServer\bin\Release\RemoteHubCpTestServer.exe'))
    Copy-Item $files $stage -Force
    $labCert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=RemoteHub Lab Code Signing' -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if ($labCert) {
        & "$PSScriptRoot\Sign-Files.ps1" -Path ($files | ForEach-Object { Join-Path $stage (Split-Path $_ -Leaf) }) -Certificate $labCert.Thumbprint
    }
    Join-Path $stage 'RemoteHubCredentialProvider.Lab.dll'
}
else { Join-Path $root "src\RemoteHub.CredentialProvider\bin\$Configuration\RemoteHubCredentialProvider.dll" }
