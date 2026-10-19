# Adds Visual Studio's Spectre-mitigated libraries, which the credential provider's hardening flags need (MSB8040;
# credential-provider-design.md §8.1). GitHub's windows-latest image comes without them, so ci.yml and release.yml run
# this before building the provider. The provider builds with v143 (MSVC 14.44 on the image).
#   build\Install-SpectreLibraries.ps1
$ErrorActionPreference = 'Stop'

$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$vs = & "$installer\vswhere.exe" -latest -property installationPath
if (-not $vs) { throw 'No Visual Studio installation found (vswhere).' }
$p = Start-Process "$installer\vs_installer.exe" -Wait -PassThru -ArgumentList 'modify', '--installPath', "`"$vs`"",
    '--add', 'Microsoft.VisualStudio.Component.VC.14.44.17.14.x86.x64.Spectre', '--quiet', '--norestart', '--nocache'
if ($p.ExitCode -notin 0, 3010) { throw "vs_installer exited with $($p.ExitCode)" }
