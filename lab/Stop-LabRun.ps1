#Requires -Version 5.1
<#
.SYNOPSIS
    Stops the request the lab watcher is running right now (a run that hangs), from any shell, no elevation: creates
    LabRoot\queue\cancel, which the watcher (Invoke-LabQueue.ps1) checks every 2 s. It ends the request's processes,
    marks it cancelled and goes on with the queue. The VMs stay as they are; the next scenario reverts them anyway.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$cfg = Get-Content (Join-Path $PSScriptRoot 'lab.json') -Raw | ConvertFrom-Json
$cancel = Join-Path $cfg.LabRoot 'queue\cancel'
Set-Content $cancel -Value (Get-Date -Format s)
for ($i = 0; $i -lt 30 -and (Test-Path $cancel); $i++) { Start-Sleep -Seconds 1 }
if (Test-Path $cancel) {
    Remove-Item $cancel -ErrorAction SilentlyContinue
    Write-Warning "The watcher didn't pick up the cancel within 30 s - nothing was running, or it runs an older Invoke-LabQueue.ps1 (restart it: D:\RemoteHubLab\lab\Install-LabTask.ps1, elevated)."
}
else { Write-Host 'Cancelled the running request.' }
