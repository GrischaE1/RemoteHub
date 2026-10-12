#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Once, elevated: registers and starts the scheduled task 'RemoteHub Lab' - the lab watcher. It runs as labop
    (stored password, whether or not anyone is signed in), starts at boot and keeps watching LabRoot\queue for the
    requests Start-LabRun.ps1 / Invoke-AsLabop.ps1 drop there. Nobody has to start it, so no elevation is needed
    afterwards. Run it again after changing the labop password, or to restart the watcher.
.EXAMPLE
    & D:\RemoteHubLab\lab\Install-LabTask.ps1
    (the copy Start-LabRun.ps1 publishes - an elevated shell doesn't see the mapped drive the repo is on)
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LabQueue.ps1')

$account = "$env:COMPUTERNAME\$($LabCfg.HostAccount)"
$password = (Get-Content (Join-Path $PSScriptRoot 'lab.local.json') -Raw | ConvertFrom-Json).Password

# Working folder: labop runs there, whoever starts a run reads the results.
$null = New-Item -ItemType Directory -Path $LabRoot -Force
icacls.exe $LabRoot '/grant' "$($account):(OI)(CI)M" | Out-Null
icacls.exe $LabRoot '/grant' "*S-1-5-11:(OI)(CI)M" | Out-Null   # Authenticated Users
Publish-LabFiles

# labop needs "Log on as a batch job" to run a task with a stored password (by default only Administrators, Backup
# Operators and Performance Log Users have it on Server).
$sid = (New-Object System.Security.Principal.NTAccount $account).Translate([System.Security.Principal.SecurityIdentifier]).Value
$export = Join-Path $env:TEMP 'rhlab-rights.inf'
secedit.exe /export /cfg $export /areas USER_RIGHTS | Out-Null
$line = Select-String -Path $export -Pattern '^SeBatchLogonRight\s*=\s*(.*)$' | Select-Object -First 1
$current = if ($line) { $line.Matches[0].Groups[1].Value.Trim() } else { '' }
if ($current -notmatch [regex]::Escape("*$sid")) {
    $new = (@($current -split ',' | Where-Object { $_ }) + "*$sid") -join ','
    $inf = Join-Path $env:TEMP 'rhlab-batch.inf'
    @"
[Unicode]
Unicode=yes
[Version]
signature="`$CHICAGO`$"
Revision=1
[Privilege Rights]
SeBatchLogonRight = $new
"@ | Set-Content $inf -Encoding Unicode
    secedit.exe /configure /db (Join-Path $env:TEMP 'rhlab-batch.sdb') /cfg $inf /areas USER_RIGHTS /quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Granting 'Log on as a batch job' to $account failed (secedit $LASTEXITCODE)." }
    Remove-Item $inf, (Join-Path $env:TEMP 'rhlab-batch.sdb') -ErrorAction SilentlyContinue
    Write-Host "Granted 'Log on as a batch job' to $account."
}
Remove-Item $export -ErrorAction SilentlyContinue

# Stop a running watcher and drop requests nobody waits for any more.
if (Get-ScheduledTask -TaskName $LabTask -ErrorAction SilentlyContinue) { Stop-ScheduledTask -TaskName $LabTask }
Get-ChildItem (Join-Path $LabRoot 'queue') -File -ErrorAction SilentlyContinue | Remove-Item

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -WorkingDirectory $LabRoot `
    -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$LabRoot\lab\Invoke-LabQueue.ps1`""
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew `
    -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $LabTask -Action $action -Settings $settings -Trigger (New-ScheduledTaskTrigger -AtStartup) `
    -User $account -Password $password -RunLevel Limited -Force | Out-Null

# Let Authenticated Users see the task's state (Start-LabRun.ps1 checks that the watcher runs).
$svc = New-Object -ComObject Schedule.Service
$svc.Connect()
$task = $svc.GetFolder('\').GetTask($LabTask)
$sddl = $task.GetSecurityDescriptor(4)   # DACL only
if ($sddl -notmatch ';;;AU\)') { $task.SetSecurityDescriptor($sddl + '(A;;GRGX;;;AU)', 0) }

Start-ScheduledTask -TaskName $LabTask
$deadline = (Get-Date).AddSeconds(15)
while ((Get-ScheduledTask -TaskName $LabTask).State -ne 'Running' -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
$info = Get-ScheduledTaskInfo -TaskName $LabTask
if ((Get-ScheduledTask -TaskName $LabTask).State -ne 'Running') {
    throw "'$LabTask' registered but not running (last result 0x$('{0:X}' -f $info.LastTaskResult)) - check the labop password in lab.local.json."
}
Write-Host "'$LabTask' runs as $account and watches $LabRoot\queue (starts at boot). Start runs with lab\Start-LabRun.ps1."
