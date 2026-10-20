<#
.SYNOPSIS
    Applies advanced audit subcategories, command line auditing, PowerShell logging and event log sizes.
.DESCRIPTION
    Subcategories are set by GUID (no localized names) after an auditpol backup; registry and log size
    changes go to a snapshot that SEC-03 can restore with -SnapshotPath. Command line auditing records
    every process command line, so secrets passed as arguments land in the Security log: restrict access
    and forward to a protected collector. Transcripts must go to a folder users cannot read. Size the
    Security log for your forwarding interval. Group Policy advanced audit settings override local ones;
    the subcategory step is skipped when they are present unless -Force.
.NOTES
    RemoteHub: id=SEC-12; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # GUID=value, where value is 1 success, 2 failure, 3 both, 0 none.
    [string[]]$Subcategory = @(
        '{0CCE923F-69AE-11D9-BED3-505054503030}=3', # Credential Validation
        '{0CCE9235-69AE-11D9-BED3-505054503030}=3', # User Account Management
        '{0CCE9237-69AE-11D9-BED3-505054503030}=1', # Security Group Management
        '{0CCE922B-69AE-11D9-BED3-505054503030}=1', # Process Creation
        '{0CCE9215-69AE-11D9-BED3-505054503030}=3', # Logon
        '{0CCE9216-69AE-11D9-BED3-505054503030}=1', # Logoff
        '{0CCE921B-69AE-11D9-BED3-505054503030}=1', # Special Logon
        '{0CCE922F-69AE-11D9-BED3-505054503030}=3', # Audit Policy Change
        '{0CCE9228-69AE-11D9-BED3-505054503030}=3', # Sensitive Privilege Use
        '{0CCE9211-69AE-11D9-BED3-505054503030}=1'  # Security System Extension
    ),
    [bool]$ForceSubcategorySettings = $true,
    [bool]$CommandLineAuditing = $true,
    [bool]$ScriptBlockLogging = $true,
    [bool]$ModuleLogging = $true,
    [bool]$Transcription = $false,
    [string]$TranscriptPath = '',
    [string[]]$LogSizeMB = @('Security=1024', 'Microsoft-Windows-PowerShell/Operational=256', 'System=256'),
    [switch]$Force,
    [string]$SnapshotFolder = "$env:ProgramData\RemoteHub\ScriptLogs"
)

$ErrorActionPreference = 'Stop'
$hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
$stamp = '{0:yyyyMMdd-HHmmss}' -f (Get-Date)
$snapshotPath = Join-Path $SnapshotFolder "SEC-12-snapshot-$stamp.json"
$snapshot = New-Object System.Collections.Generic.List[object]
$log = New-Object System.Collections.Generic.List[object]

function Add-Log($setting, $before, $after, $result) { $log.Add([PSCustomObject]@{ Setting = $setting; Before = [string]$before; After = [string]$after; Result = $result }) }

function Save-Snapshot($entry) {
    $snapshot.Add($entry)
    if (-not (Test-Path -LiteralPath $SnapshotFolder)) { $null = New-Item -ItemType Directory -Path $SnapshotFolder -Force }
    [IO.File]::WriteAllText($snapshotPath, (ConvertTo-Json -InputObject @($snapshot) -Depth 5), (New-Object Text.UTF8Encoding $false))
    (Get-FileHash -LiteralPath $snapshotPath -Algorithm SHA256).Hash | Set-Content -LiteralPath "$snapshotPath.sha256" -Encoding ASCII
}

function Set-Reg($sub, $name, $value, [string]$kind = 'DWord') {
    $label = "HKLM\$sub\$name"
    $k = $hklm.OpenSubKey($sub); $existed = $false; $cur = $null; $curKind = $null
    if ($k) {
        try { if ($k.GetValueNames() -contains $name) { $existed = $true; $cur = $k.GetValue($name); $curKind = $k.GetValueKind($name).ToString() } } finally { $k.Close() }
    }
    if ($existed -and [string]$cur -eq [string]$value) { Add-Log $label $cur $value 'No change'; return }
    if (-not $PSCmdlet.ShouldProcess($label, "Set to $value")) { Add-Log $label $cur $value 'Planned'; return }
    Save-Snapshot ([PSCustomObject]@{ Kind = 'Registry'; Path = $sub; Name = $name; Existed = $existed; ValueKind = $curKind; Value = $cur })
    $k = $hklm.CreateSubKey($sub)
    try {
        if ($kind -eq 'DWord') { $k.SetValue($name, [int]$value, [Microsoft.Win32.RegistryValueKind]::DWord) }
        else { $k.SetValue($name, [string]$value, [Microsoft.Win32.RegistryValueKind]::String) }
    } finally { $k.Close() }
    Add-Log $label $cur $value 'Changed'
}

try {
    if ($Transcription -and -not $TranscriptPath) { throw 'Transcription needs -TranscriptPath pointing to a folder users cannot read.' }
    $failed = $false

    # Advanced audit subcategories, compared by GUID through the numeric value in an auditpol backup.
    $gpoAudit = Test-Path -LiteralPath "$env:SystemRoot\security\audit\audit.csv"
    if ($gpoAudit -and -not $Force) {
        Add-Log 'Advanced audit subcategories' '' '' 'Skipped: set by Group Policy (use -Force to apply locally anyway)'
    } else {
        if (-not (Test-Path -LiteralPath $SnapshotFolder)) { $null = New-Item -ItemType Directory -Path $SnapshotFolder -Force }
        $backup = Join-Path $SnapshotFolder "SEC-12-auditpol-$stamp.csv"
        $null = & auditpol.exe /backup /file:$backup
        if ($LASTEXITCODE -ne 0) { throw 'auditpol backup failed; nothing was changed.' }
        $current = @{}
        Get-Content -LiteralPath $backup | Select-Object -Skip 1 |
            ConvertFrom-Csv -Header 'Machine','Target','Subcategory','Guid','Inclusion','Exclusion','Value' |
            ForEach-Object { if ($_.Guid) { $current[$_.Guid.ToUpperInvariant()] = $_.Value } }
        $auditChanged = $false
        foreach ($entry in $Subcategory) {
            $guid, $want = $entry -split '=', 2
            $guid = '{' + $guid.Trim().Trim('{}').ToUpperInvariant() + '}'
            $have = $current[$guid]; if ($null -eq $have) { $have = '0' }
            if ([int]$have -eq [int]$want) { Add-Log "Audit $guid" $have $want 'No change'; continue }
            if (-not $PSCmdlet.ShouldProcess("Audit subcategory $guid", "Set to $want")) { Add-Log "Audit $guid" $have $want 'Planned'; continue }
            $succ = 'disable'; if ([int]$want -band 1) { $succ = 'enable' }
            $fail = 'disable'; if ([int]$want -band 2) { $fail = 'enable' }
            $null = & auditpol.exe /set "/subcategory:$guid" "/success:$succ" "/failure:$fail"
            if ($LASTEXITCODE -ne 0) { $failed = $true; Add-Log "Audit $guid" $have $want 'Failed'; continue }
            $auditChanged = $true
            Add-Log "Audit $guid" $have $want 'Changed'
        }
        if ($auditChanged) { Add-Log 'auditpol backup' '' $backup 'Restore with: auditpol /restore /file:<backup>' }
        else { Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue }
    }

    if ($ForceSubcategorySettings) { Set-Reg 'SYSTEM\CurrentControlSet\Control\Lsa' 'SCENoApplyLegacyAuditPolicy' 1 }
    if ($CommandLineAuditing) { Set-Reg 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\Audit' 'ProcessCreationIncludeCmdLine_Enabled' 1 }
    $ps = 'SOFTWARE\Policies\Microsoft\Windows\PowerShell'
    if ($ScriptBlockLogging) { Set-Reg "$ps\ScriptBlockLogging" 'EnableScriptBlockLogging' 1 }
    if ($ModuleLogging) {
        Set-Reg "$ps\ModuleLogging" 'EnableModuleLogging' 1
        Set-Reg "$ps\ModuleLogging\ModuleNames" '*' '*' 'String'
    }
    if ($Transcription) {
        Set-Reg "$ps\Transcription" 'EnableTranscripting' 1
        Set-Reg "$ps\Transcription" 'EnableInvocationHeader' 1
        Set-Reg "$ps\Transcription" 'OutputDirectory' $TranscriptPath 'String'
    }

    foreach ($entry in $LogSizeMB) {
        $name, $mb = $entry -split '=', 2
        $want = [long]$mb * 1MB
        $l = Get-WinEvent -ListLog $name -ErrorAction SilentlyContinue
        if (-not $l) { Add-Log "Log $name" '' $want 'Skipped: log not found'; continue }
        # Logs are only grown, never shrunk, so an admin's larger size is kept.
        if ($l.MaximumSizeInBytes -ge $want -and [string]$l.LogMode -eq 'Circular') { Add-Log "Log $name" $l.MaximumSizeInBytes $want 'No change'; continue }
        if (-not $PSCmdlet.ShouldProcess("Event log $name", "Set maximum size to $mb MB, circular")) { Add-Log "Log $name" $l.MaximumSizeInBytes $want 'Planned'; continue }
        $before = $l.MaximumSizeInBytes
        Save-Snapshot ([PSCustomObject]@{ Kind = 'EventLog'; Name = $name; MaximumSizeInBytes = $before; LogMode = [string]$l.LogMode })
        if ($before -lt $want) { $l.MaximumSizeInBytes = $want }
        $l.LogMode = 'Circular'
        $l.SaveChanges()
        Add-Log "Log $name" $before $l.MaximumSizeInBytes 'Changed'
    }

    $log
    if ($snapshot.Count -gt 0) { [PSCustomObject]@{ Snapshot = $snapshotPath; Rollback = 'Run SEC-03 with -SnapshotPath set to this file' } }
    if ($failed) { exit 1 }
    exit 0
} catch {
    $log
    Write-Error "Audit configuration failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
