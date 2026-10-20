<#
.SYNOPSIS
    Applies a Microsoft Defender profile and attack surface reduction (ASR) rules, audit mode first.
.DESCRIPTION
    Sets cloud protection, sample submission, PUA protection, network protection, controlled folder
    access, scan and signature update schedules and ASR rules, exporting preferences before and after.
    Tamper protection and Intune, security settings management or Group Policy override local values: the
    script detects this and exits "managed elsewhere" without changing anything. ASR rule GUIDs must come
    from current Microsoft documentation. Moving rules from audit to block or warn needs -AuditReviewed.
.NOTES
    RemoteHub: id=SEC-05; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Default','Moderate','High','HighPlus','ZeroTolerance')]
    [string]$CloudBlockLevel = 'High',
    [ValidateSet('SendSafeSamples','NeverSend','SendAllSamples')]
    [string]$SampleSubmission = 'SendSafeSamples',
    [ValidateSet('Enabled','AuditMode','Disabled')]
    [string]$PuaProtection = 'Enabled',
    [ValidateSet('Enabled','AuditMode','Disabled')]
    [string]$NetworkProtection = 'AuditMode',
    [ValidateSet('Enabled','AuditMode','Disabled')]
    [string]$ControlledFolderAccess = 'AuditMode',
    [ValidateRange(0, 7)]
    [int]$ScanDay = 0,
    [ValidateRange(1, 24)]
    [int]$SignatureUpdateIntervalHours = 4,
    [string[]]$AsrRuleIds = @(),
    [ValidateSet('AuditMode','Warn','Enabled')]
    [string]$AsrAction = 'AuditMode',
    [switch]$AuditReviewed,
    [int]$AuditEventDays = 14,
    [string]$ExportFolder = "$env:ProgramData\RemoteHub\ScriptLogs"
)

$ErrorActionPreference = 'Stop'
$changes = New-Object System.Collections.Generic.List[object]

function Export-Pref($tag) {
    if (-not (Test-Path -LiteralPath $ExportFolder)) { $null = New-Item -ItemType Directory -Path $ExportFolder -Force }
    $file = Join-Path $ExportFolder ("SEC-05-preferences-{0}-{1:yyyyMMdd-HHmmss}.json" -f $tag, (Get-Date))
    Get-MpPreference | Select-Object -Property * -ExcludeProperty Cim*, PSComputerName, Exclusion* |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $file -Encoding UTF8
    return $file
}

# Get-MpPreference returns numbers, so each wanted name is compared through its numeric code.
$codes = @{
    MAPSReporting = @{ Disabled = 0; Basic = 1; Advanced = 2 }
    CloudBlockLevel = @{ Default = 0; Moderate = 1; High = 2; HighPlus = 4; ZeroTolerance = 6 }
    SubmitSamplesConsent = @{ AlwaysPrompt = 0; SendSafeSamples = 1; NeverSend = 2; SendAllSamples = 3 }
    PUAProtection = @{ Disabled = 0; Enabled = 1; AuditMode = 2 }
    EnableNetworkProtection = @{ Disabled = 0; Enabled = 1; AuditMode = 2 }
    EnableControlledFolderAccess = @{ Disabled = 0; Enabled = 1; AuditMode = 2 }
}

function Set-Pref($name, $wanted, $current) {
    $wantedCode = $wanted
    if ($codes.ContainsKey($name)) { $wantedCode = $codes[$name][[string]$wanted] }
    $row = [PSCustomObject]@{ Setting = $name; Before = [string]$current; After = [string]$wanted; Result = 'No change' }
    if ([string]$current -ne [string]$wantedCode) {
        if ($PSCmdlet.ShouldProcess("Defender $name", "Set to $wanted")) {
            $p = @{ $name = $wanted }
            Set-MpPreference @p
            $row.Result = 'Changed'
        } else { $row.Result = 'Planned' }
    }
    $changes.Add($row)
}

try {
    if (-not (Get-Command -Name Set-MpPreference -ErrorAction SilentlyContinue)) { [PSCustomObject]@{ Status = 'Data unavailable'; Reason = 'Defender module not present' }; exit 1 }
    $st = Get-MpComputerStatus

    # Local changes are blocked or silently reverted when another authority owns Defender.
    $managed = New-Object System.Collections.Generic.List[string]
    if ($st.PSObject.Properties['IsTamperProtected'] -and $st.IsTamperProtected) { $managed.Add('Tamper protection') }
    $gp = Get-Item -LiteralPath 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender' -ErrorAction SilentlyContinue
    if ($gp -and ($gp.ValueCount -gt 0 -or $gp.SubKeyCount -gt 0)) { $managed.Add('Group Policy') }
    $mdm = Get-Item -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Defender\Policy Manager' -ErrorAction SilentlyContinue
    if ($mdm -and $mdm.ValueCount -gt 0) { $managed.Add('MDM (Intune or security settings management)') }
    if ($managed.Count -gt 0) {
        [PSCustomObject]@{ Status = 'Managed elsewhere'; ManagedBy = ($managed -join ', '); Changed = $false }
        exit 1
    }

    if ($AsrAction -ne 'AuditMode' -and $AsrRuleIds.Count -gt 0 -and -not $AuditReviewed) {
        throw "ASR rules move to $AsrAction only after the audit events were reviewed; rerun with -AuditReviewed."
    }

    $before = $null
    if (-not $WhatIfPreference) { $before = Export-Pref 'before' }
    $pref = Get-MpPreference

    Set-Pref 'MAPSReporting' 'Advanced' $pref.MAPSReporting
    Set-Pref 'CloudBlockLevel' $CloudBlockLevel $pref.CloudBlockLevel
    Set-Pref 'SubmitSamplesConsent' $SampleSubmission $pref.SubmitSamplesConsent
    Set-Pref 'PUAProtection' $PuaProtection $pref.PUAProtection
    Set-Pref 'EnableNetworkProtection' $NetworkProtection $pref.EnableNetworkProtection
    Set-Pref 'EnableControlledFolderAccess' $ControlledFolderAccess $pref.EnableControlledFolderAccess
    Set-Pref 'ScanScheduleDay' $ScanDay $pref.ScanScheduleDay
    Set-Pref 'SignatureUpdateInterval' $SignatureUpdateIntervalHours $pref.SignatureUpdateInterval

    $actionCode = @{ 'AuditMode' = 2; 'Warn' = 6; 'Enabled' = 1 }[$AsrAction]
    $ids = @($pref.AttackSurfaceReductionRules_Ids)
    $acts = @($pref.AttackSurfaceReductionRules_Actions)
    foreach ($rule in $AsrRuleIds) {
        $id = $rule.Trim('{} ').ToLowerInvariant()
        $current = 'NotConfigured'
        for ($i = 0; $i -lt $ids.Count; $i++) { if ([string]$ids[$i] -and $ids[$i].ToLowerInvariant() -eq $id) { $current = [string]$acts[$i] } }
        if ($current -eq [string]$actionCode) { $changes.Add([PSCustomObject]@{ Setting = "ASR $id"; Before = $current; After = $actionCode; Result = 'No change' }); continue }
        if ($PSCmdlet.ShouldProcess("ASR rule $id", "Set to $AsrAction")) {
            Add-MpPreference -AttackSurfaceReductionRules_Ids $id -AttackSurfaceReductionRules_Actions $AsrAction
            $changes.Add([PSCustomObject]@{ Setting = "ASR $id"; Before = $current; After = $actionCode; Result = 'Changed' })
        } else { $changes.Add([PSCustomObject]@{ Setting = "ASR $id"; Before = $current; After = $actionCode; Result = 'Planned' }) }
    }

    # Audit (1122) and block (1121) events are the evidence needed before moving a rule to block.
    $events = @{ Audit = 'Data unavailable'; Block = 'Data unavailable' }
    try {
        $since = (Get-Date).AddDays(-$AuditEventDays)
        $ev = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Windows Defender/Operational'; Id = 1121, 1122; StartTime = $since } -ErrorAction SilentlyContinue)
        $events.Audit = @($ev | Where-Object { $_.Id -eq 1122 }).Count
        $events.Block = @($ev | Where-Object { $_.Id -eq 1121 }).Count
    } catch { Write-Verbose "Defender event log not readable: $($_.Exception.Message)" }

    $after = $null
    if (-not $WhatIfPreference -and @($changes | Where-Object { $_.Result -eq 'Changed' }).Count -gt 0) { $after = Export-Pref 'after' }
    $changes
    [PSCustomObject]@{
        Changed = @($changes | Where-Object { $_.Result -eq 'Changed' }).Count
        AsrAuditEvents = $events.Audit; AsrBlockEvents = $events.Block; EventWindowDays = $AuditEventDays
        ExportBefore = $before; ExportAfter = $after
    }
    exit 0
} catch {
    Write-Error "Defender configuration failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
