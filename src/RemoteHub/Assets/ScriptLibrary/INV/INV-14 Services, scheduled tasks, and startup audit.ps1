<#
.SYNOPSIS
    Flags risky or drifting autostart configuration in services, scheduled tasks and Run keys.
.DESCRIPTION
    Finds services with unquoted paths containing spaces, services under non-standard accounts,
    non-Microsoft automatic services that are stopped, scheduled tasks running as SYSTEM from
    user-writable locations, tasks whose last run failed, and machine-wide startup entries.
    Microsoft task paths are excluded unless -IncludeMicrosoftTasks is set. Stored task
    credentials are never read. Per-user Run keys and user tasks need a separate USER pass.
.NOTES
    RemoteHub: id=INV-14; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [switch]$IncludeMicrosoftTasks,
    [ValidateSet('Table', 'Json', 'Csv')]
    [string]$OutputFormat = 'Table'
)

$ErrorActionPreference = 'Stop'

$standardAccounts = @('localsystem', 'nt authority\localservice', 'nt authority\networkservice', 'nt authority\system')
$userWritable = '(?i)\\Users\\|\\ProgramData\\|\\Windows\\Temp\\|\\AppData\\|^[A-Z]:\\Temp\\|\\Public\\'

function Get-ExePath([string]$CommandLine) {
    if (-not $CommandLine) { return '' }
    $c = [Environment]::ExpandEnvironmentVariables($CommandLine.Trim())
    if ($c.StartsWith('"')) {
        $end = $c.IndexOf('"', 1)
        if ($end -lt 0) { return $c.Trim('"') }
        return $c.Substring(1, $end - 1)
    }
    $m = [regex]::Match($c, '^(.+?\.(exe|com|bat|cmd|ps1|vbs|js|dll))', 'IgnoreCase')
    if ($m.Success) { return $m.Groups[1].Value }
    return ($c -split ' ')[0]
}

try {
    $findings = New-Object System.Collections.Generic.List[object]
    function Add-Finding($Severity, $Area, $Name, $Detail) {
        $findings.Add([PSCustomObject]@{ Severity = $Severity; Area = $Area; Name = $Name; Detail = $Detail })
    }

    foreach ($s in Get-CimInstance -ClassName Win32_Service) {
        $path = [string]$s.PathName
        $exe = Get-ExePath $path
        if ($path -and -not $path.Trim().StartsWith('"') -and $exe -match ' ' -and $exe -notmatch '^[A-Za-z]:\\Windows\\') {
            Add-Finding 'High' 'Service' $s.Name 'Unquoted path containing spaces'
        }
        $acct = ([string]$s.StartName).ToLowerInvariant()
        if ($acct -and $standardAccounts -notcontains $acct -and $acct -notlike 'nt service\*') {
            Add-Finding 'Medium' 'Service' $s.Name 'Runs under a non-standard account'
        }
        if ($s.StartMode -eq 'Auto' -and $s.State -ne 'Running' -and $exe -and $exe -notmatch '(?i)\\Windows\\') {
            $company = ''
            try { if (Test-Path -LiteralPath $exe) { $company = (Get-Item -LiteralPath $exe).VersionInfo.CompanyName } } catch { $company = '' }
            if ($company -notmatch 'Microsoft') { Add-Finding 'Low' 'Service' $s.Name 'Automatic but not running (non-Microsoft)' }
        }
    }

    foreach ($t in Get-ScheduledTask) {
        if (-not $IncludeMicrosoftTasks -and $t.TaskPath -like '\Microsoft\*') { continue }
        $full = "$($t.TaskPath)$($t.TaskName)"
        $principal = [string]$t.Principal.UserId
        if ($principal -match '^(SYSTEM|S-1-5-18|NT AUTHORITY\\SYSTEM)$') {
            foreach ($a in @($t.Actions)) {
                $exe = Get-ExePath ([string]$a.Execute)
                if ($exe -match $userWritable) { Add-Finding 'High' 'Task' $full 'Runs as SYSTEM from a user-writable location' }
            }
        }
        if ($t.State -ne 'Disabled') {
            try {
                $info = Get-ScheduledTaskInfo -InputObject $t
                # 267011 = never run, 267009 = currently running; neither is a failure.
                if ($info.LastRunTime -and $info.LastTaskResult -ne 0 -and @(267011, 267009) -notcontains $info.LastTaskResult) {
                    Add-Finding 'Low' 'Task' $full ('Last run failed (0x{0:X8})' -f $info.LastTaskResult)
                }
            } catch { Write-Verbose "No run info for $full." }
        }
    }

    $runKeys = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce',
                 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce')
    foreach ($k in $runKeys) {
        if (-not (Test-Path -LiteralPath $k)) { continue }
        $p = Get-ItemProperty -LiteralPath $k
        foreach ($n in $p.PSObject.Properties.Name | Where-Object { $_ -notlike 'PS*' }) {
            $sev = 'Info'
            if ((Get-ExePath ([string]$p.$n)) -match $userWritable) { $sev = 'Medium' }
            Add-Finding $sev 'Startup' $n ("$(Split-Path $k -Leaf) entry: " + (Get-ExePath ([string]$p.$n)))
        }
    }
    foreach ($sc in @(Get-CimInstance -ClassName Win32_StartupCommand | Where-Object { $_.Location -match 'Startup' -and $_.User -match 'Public|All Users' })) {
        Add-Finding 'Info' 'Startup' $sc.Name 'Common Startup folder entry'
    }

    $sorted = $findings | Sort-Object @{ Expression = { @('High', 'Medium', 'Low', 'Info').IndexOf($_.Severity) } }, Area, Name
    switch ($OutputFormat) {
        'Json'  { ConvertTo-Json -InputObject @($sorted) -Depth 5 }
        'Csv'   { $sorted | ConvertTo-Csv -NoTypeInformation }
        default { if ($findings.Count) { $sorted | Format-Table -AutoSize | Out-String -Width 4096 } else { 'No findings.' } }
    }
    if (@($findings | Where-Object { $_.Severity -eq 'High' }).Count -gt 0) { exit 1 }
    exit 0
} catch {
    Write-Error "Autostart audit failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
