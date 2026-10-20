<#
.SYNOPSIS
    Lists Microsoft Defender custom exclusions and flags the risky ones with a severity.
.DESCRIPTION
    Reads path, extension, process and IP address exclusions and flags whole drives, user profile roots,
    temp folders, executable or script extensions and scripting host processes. Entries in the approved
    list for the chosen role are marked as approved. Also counts exclusion changes (event 5007).
    Automatic exclusions for server roles are not listed here. Only administrators and SYSTEM can read
    exclusions. Treat the output as sensitive: it maps where malware would go unscanned.
.NOTES
    RemoteHub: id=SEC-06; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [string]$ApprovedListPath = "$env:ProgramData\RemoteHub\Baselines\SEC-06-approved.json",
    [string]$Role = 'Default',
    [int]$ChangeEventDays = 30
)

$ErrorActionPreference = 'Stop'

# Approved list format: { "Default": [ "C:\\App\\Data" ], "SqlServer": [ "D:\\SQLData", ".mdf" ] }
function Get-Severity([string]$type, [string]$value) {
    $v = $value.Trim().TrimEnd('\').ToLowerInvariant()
    switch ($type) {
        'Path' {
            if ($v -match '^[a-z]:$' -or $v -eq '*' -or $v -match '^[a-z]:\\\*$') { return 'High', 'Whole drive' }
            if ($v -match '^[a-z]:\\users(\\[^\\]*)?$' -or $v -match '%userprofile%$' -or $v -match '^%systemdrive%\\users') { return 'High', 'User profile root' }
            if ($v -match '\\temp(\\|$)' -or $v -match '%temp%|%tmp%' -or $v -match '\\appdata\\local\\temp') { return 'High', 'Temp folder' }
            if ($v -match '\\downloads(\\|$)' -or $v -match '\\appdata(\\roaming|\\local)?$') { return 'Medium', 'User writable folder' }
            if ($v -match '\*') { return 'Medium', 'Wildcard path' }
            return 'Low', ''
        }
        'Extension' {
            if ($v.TrimStart('.', '*') -in 'exe','dll','sys','scr','com','bat','cmd','ps1','psm1','vbs','vbe','js','jse','wsf','hta','msi','lnk') {
                return 'High', 'Executable or script extension'
            }
            return 'Low', ''
        }
        'Process' {
            $leaf = ($v -split '\\')[-1]
            if ($leaf -in 'powershell.exe','pwsh.exe','cmd.exe','wscript.exe','cscript.exe','mshta.exe','rundll32.exe','regsvr32.exe','python.exe','node.exe') {
                return 'High', 'Scripting host or loader process'
            }
            if ($v -match '\*') { return 'Medium', 'Wildcard process' }
            return 'Low', ''
        }
        'IpAddress' { if ($v -match '/(0|8|16)$' -or $v -eq '*') { return 'Medium', 'Wide address range' }; return 'Low', '' }
    }
    return 'Low', ''
}

try {
    if (-not (Get-Command -Name Get-MpPreference -ErrorAction SilentlyContinue)) { 'Status,Reason'; 'Data unavailable,Defender module not present'; exit 1 }
    $pref = Get-MpPreference
    $all = @{ Path = $pref.ExclusionPath; Extension = $pref.ExclusionExtension; Process = $pref.ExclusionProcess; IpAddress = $pref.ExclusionIpAddress }

    # Non-elevated callers get a placeholder string instead of the list.
    $blocked = @($all.Values | ForEach-Object { $_ } | Where-Object { $_ -match '^N/A' })
    if ($blocked.Count -gt 0) { 'Status,Reason'; 'Data unavailable,Exclusions are readable only by administrators or SYSTEM'; exit 1 }

    $approved = @()
    if (Test-Path -LiteralPath $ApprovedListPath) {
        $list = Get-Content -LiteralPath $ApprovedListPath -Raw | ConvertFrom-Json
        if ($list.PSObject.Properties[$Role]) { $approved = @($list.$Role | ForEach-Object { ([string]$_).TrimEnd('\').ToLowerInvariant() }) }
        else { Write-Verbose "Role '$Role' not found in the approved list." }
    } else { Write-Verbose "No approved list at $ApprovedListPath; every exclusion is unapproved." }

    $findings = foreach ($type in 'Path','Extension','Process','IpAddress') {
        foreach ($value in @($all[$type])) {
            if (-not $value) { continue }
            $sev, $reason = Get-Severity $type ([string]$value)
            $isApproved = $approved -contains ([string]$value).TrimEnd('\').ToLowerInvariant()
            if (-not $isApproved -and $sev -eq 'Low') { $sev = 'Medium'; $reason = 'Not in approved list' }
            elseif (-not $isApproved) { $reason = "$reason; not in approved list" }
            [PSCustomObject]@{ Type = $type; Value = [string]$value; Severity = $sev; Reason = $reason; Approved = $isApproved }
        }
    }
    $findings = @($findings)

    $changes = 'Data unavailable'
    try {
        $since = (Get-Date).AddDays(-$ChangeEventDays)
        $ev = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Windows Defender/Operational'; Id = 5007; StartTime = $since } -ErrorAction SilentlyContinue)
        $changes = @($ev | Where-Object { $_.Message -match 'Exclusions' }).Count
    } catch { Write-Verbose "Defender event log not readable: $($_.Exception.Message)" }

    if ($findings.Count -eq 0) { 'Type,Value,Severity,Reason,Approved' }
    else { $findings | Sort-Object @{ Expression = { @{ High = 0; Medium = 1; Low = 2 }[$_.Severity] } }, Type | ConvertTo-Csv -NoTypeInformation }
    Write-Output ''
    Write-Output ("Exclusions: {0}; high: {1}; exclusion change events (5007) in {2} days: {3}" -f $findings.Count,
        @($findings | Where-Object { $_.Severity -eq 'High' }).Count, $ChangeEventDays, $changes)

    if (@($findings | Where-Object { $_.Severity -eq 'High' -and -not $_.Approved }).Count -gt 0) { exit 1 }
    exit 0
} catch {
    Write-Error "Exclusion audit failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
