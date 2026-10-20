#Requires -Version 5.1
<#
.SYNOPSIS
    Builds RemoteHub Lab (lab\LabConsole) into one startable file, lab\RemoteHubLab.exe (git-ignored): double-click it,
    pin it, or put a shortcut to it anywhere. Needs the .NET 10 desktop runtime, as RemoteHub does. Run it again after
    changes to lab\LabConsole.
.EXAMPLE
    .\build\build-lab-tool.ps1
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$project = Join-Path $repo 'lab\LabConsole\RemoteHub.LabConsole.csproj'
$out = Join-Path $repo 'lab\LabConsole\bin\publish'
$exe = Join-Path $repo 'lab\RemoteHubLab.exe'

if (Get-Process RemoteHubLab -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
    throw "RemoteHub Lab is open ($exe) - close it first, its file can't be replaced while it runs."
}
dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Building RemoteHub Lab failed (exit $LASTEXITCODE)." }
Copy-Item (Join-Path $out 'RemoteHubLab.exe') $exe -Force
Write-Host "RemoteHub Lab: $exe"
