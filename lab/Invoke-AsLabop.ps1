#Requires -Version 5.1
<#
.SYNOPSIS
    Runs a command as labop through the scheduled task 'RemoteHub Lab', from any shell, no elevation, and prints its
    output. For poking at the lab by hand; the module is already imported.
.EXAMPLE
    .\lab\Invoke-AsLabop.ps1 { Get-LabVm | Format-Table Name, Id }
    .\lab\Invoke-AsLabop.ps1 { Invoke-LabGuest W11-26H2-en-US { whoami } }
#>
[CmdletBinding()]
param([Parameter(Mandatory)][scriptblock]$ScriptBlock)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LabQueue.ps1')

Publish-LabFiles
$id = [guid]::NewGuid().ToString('N')
$base = Join-Path $LabRoot "queue\$id"
$null = New-Item -ItemType Directory -Path (Split-Path $base) -Force
@"
`$ErrorActionPreference = 'Stop'
`$env:PSModulePath = '$LabRoot\modules;' + `$env:PSModulePath
Import-Module '$LabRoot\lab\RemoteHub.TestLab' -Force
try { & { $ScriptBlock } *>&1 | Out-String -Width 220 }
catch { "ERROR: `$(`$_.Exception.Message)"; `$_.ScriptStackTrace; exit 1 }
"@ | Set-Content -Path "$base.ps1" -Encoding UTF8

try {
    Submit-LabRequest -Request @{ Type = 'script'; Id = $id } -DoneFile "$base.done" -TimeoutMin 60
    Get-Content "$base.out.txt" -Raw
    exit [int](Get-Content "$base.done" -Raw)
}
finally { Remove-Item "$base.ps1", "$base.out.txt", "$base.done" -ErrorAction SilentlyContinue }
