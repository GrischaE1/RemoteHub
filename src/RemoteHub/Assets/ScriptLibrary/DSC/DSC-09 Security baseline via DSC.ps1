<#
.SYNOPSIS
    Checks (default) or applies account policy, security options, audit subcategories, user rights and registry values through DSC.
.DESCRIPTION
    Uses SecurityPolicyDsc, AuditPolicyDsc and the Registry resource. The default test-only mode doubles as a
    compliance check with a per-resource result. Property names are checked against the installed resource
    schema because security options need exact policy strings. User rights changes can lock out administrators
    or break services, so none are declared by default and identities are only added, never removed. Group
    Policy overrides local policy; settings that a GPO also defines are listed because the GPO wins.
    Audit subcategories are named in English as AuditPolicyDsc expects; test on non-English builds first.
.NOTES
    RemoteHub: id=DSC-09; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Test', 'Apply')]
    [string]$Mode = 'Test',
    [string[]]$AccountPolicies = @('Minimum_Password_Length=14', 'Password_must_meet_complexity_requirements=Enabled',
        'Account_lockout_threshold=10', 'Account_lockout_duration=15', 'Reset_account_lockout_counter_after=15'),
    [string[]]$SecurityOptions = @(),
    [string[]]$AuditSubcategories = @('Credential Validation|Success,Failure', 'Logon|Success,Failure',
        'Security Group Management|Success', 'User Account Management|Success,Failure', 'Process Creation|Success'),
    [string[]]$UserRights = @(),
    [string[]]$RegistryValues = @(),
    [bool]$ReplaceCurrentConfiguration = $false
)

$ErrorActionPreference = 'Stop'
$id = 'DSC-09'
$configName = 'RhSecurityBaseline'
$workDir = Join-Path $env:ProgramData "RemoteHub\DSC\$id"

function ConvertTo-Literal { param([string]$Text) "'" + ($Text -replace "'", "''") + "'" }
function ConvertTo-SafeId { param([string]$Text) ($Text -replace '[^A-Za-z0-9]', '_') }

function ConvertTo-PropertyText {
    param([string[]]$Pairs, [string]$Resource, [string]$Module)
    $schema = Get-DscResource -Module $Module -Name $Resource -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $schema) { throw "Data unavailable: $Module ($Resource) is not installed (DSC-04)." }
    $known = @($schema.Properties | ForEach-Object { $_.Name })
    $parts = foreach ($pair in $Pairs) {
        if ($pair -notmatch '^([^=]+)=(.*)$') { throw "'$pair' must be Property=Value." }
        $name = $Matches[1].Trim(); $value = $Matches[2].Trim()
        if ($known -notcontains $name) { throw "$Resource has no property '$name'; use the exact name from the resource schema." }
        if ($value -match '^\d+$') { "$name = $value" } else { "$name = $(ConvertTo-Literal $value)" }
    }
    $parts -join '; '
}

try {
    if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Run this script in Windows PowerShell 5.1 (DSC 1.1).' }

    $imports = New-Object System.Collections.Generic.List[string]
    $body = New-Object System.Text.StringBuilder
    if ($AccountPolicies.Count -gt 0 -or $SecurityOptions.Count -gt 0 -or $UserRights.Count -gt 0) { $imports.Add('SecurityPolicyDsc') }
    if ($AccountPolicies.Count -gt 0) {
        [void]$body.AppendLine("AccountPolicy Account { Name = 'RhAccountPolicy'; $(ConvertTo-PropertyText $AccountPolicies 'AccountPolicy' 'SecurityPolicyDsc') }")
    }
    if ($SecurityOptions.Count -gt 0) {
        [void]$body.AppendLine("SecurityOption Options { Name = 'RhSecurityOptions'; $(ConvertTo-PropertyText $SecurityOptions 'SecurityOption' 'SecurityPolicyDsc') }")
    }
    foreach ($line in $UserRights) {
        $p = $line -split '\|'
        if ($p.Count -ne 2) { throw "User right entry '$line' must be Policy|Identity1,Identity2." }
        $ids = ($p[1] -split ',' | ForEach-Object { ConvertTo-Literal $_.Trim() }) -join ', '
        [void]$body.AppendLine("UserRightsAssignment UR_$(ConvertTo-SafeId $p[0]) { Policy = $(ConvertTo-Literal $p[0]); Identity = @($ids); Force = `$false }")
    }
    if ($AuditSubcategories.Count -gt 0) {
        if (-not (Get-Module -ListAvailable -Name AuditPolicyDsc)) { throw 'Data unavailable: AuditPolicyDsc is not installed (DSC-04).' }
        $imports.Add('AuditPolicyDsc')
        foreach ($line in $AuditSubcategories) {
            $p = $line -split '\|'
            if ($p.Count -ne 2) { throw "Audit entry '$line' must be Subcategory|Success,Failure." }
            foreach ($flag in ($p[1] -split ',' | ForEach-Object { $_.Trim() })) {
                if ($flag -notin @('Success', 'Failure')) { throw "Audit flag '$flag' must be Success or Failure." }
                [void]$body.AppendLine("AuditPolicySubcategory Audit_$(ConvertTo-SafeId ($p[0] + '_' + $flag)) { Name = $(ConvertTo-Literal $p[0]); AuditFlag = '$flag'; Ensure = 'Present' }")
            }
        }
    }
    foreach ($line in $RegistryValues) {
        $p = $line -split '\|'
        if ($p.Count -ne 4) { throw "Registry entry '$line' must be Key|ValueName|Type|Data." }
        [void]$body.AppendLine("Registry Reg_$(ConvertTo-SafeId ($p[0] + '_' + $p[1])) { Key = $(ConvertTo-Literal $p[0]); ValueName = $(ConvertTo-Literal $p[1]); ValueType = $(ConvertTo-Literal $p[2]); ValueData = $(ConvertTo-Literal $p[3]); Ensure = 'Present' }")
    }
    if ($body.Length -eq 0) { throw 'Nothing is declared.' }

    $importText = ($imports | ForEach-Object { "Import-DscResource -ModuleName $_" }) -join "`n"
    $configText = "Configuration $configName {`n Import-DscResource -ModuleName PSDesiredStateConfiguration`n $importText`n Node 'localhost' {`n$($body.ToString())`n }`n}"
    $mofDir = Join-Path $workDir 'Mof'
    $savedWhatIf = $WhatIfPreference
    $WhatIfPreference = $false
    try {
        if (-not (Test-Path $mofDir)) { New-Item -ItemType Directory -Path $mofDir -Force | Out-Null }
        . ([scriptblock]::Create($configText))
        & $configName -OutputPath $mofDir | Out-Null
    } finally { $WhatIfPreference = $savedWhatIf }
    $mofPath = Join-Path $mofDir 'localhost.mof'

    $lcm = Get-DscLocalConfigurationManager
    if ($lcm.LCMState -eq 'Busy') { throw 'The LCM is busy applying a configuration; try again later.' }
    if ((Get-Service -Name WinRM).Status -ne 'Running') { throw 'WinRM is not running; DSC push and test need it.' }

    $gpoNote = 'Not domain joined: local policy applies'
    if ((Get-CimInstance -ClassName Win32_ComputerSystem).PartOfDomain) {
        try {
            $gpoKeys = @()
            foreach ($cls in 'RSOP_SecuritySettingNumeric', 'RSOP_SecuritySettingBoolean', 'RSOP_SecuritySettingString', 'RSOP_UserPrivilegeRight', 'RSOP_AuditPolicy') {
                foreach ($s in @(Get-CimInstance -Namespace 'root\rsop\computer' -ClassName $cls -Filter 'precedence=1' -ErrorAction Stop)) {
                    if ($s.PSObject.Properties['KeyName']) { $gpoKeys += [string]$s.KeyName }
                    elseif ($s.PSObject.Properties['UserRight']) { $gpoKeys += [string]$s.UserRight }
                    elseif ($s.PSObject.Properties['Category']) { $gpoKeys += 'Audit ' + [string]$s.Category }
                }
            }
            if ($gpoKeys.Count -eq 0) { $gpoNote = 'No security settings from Group Policy' }
            else { $gpoNote = 'Group Policy wins for: ' + (($gpoKeys | Select-Object -Unique) -join ', ') }
        } catch { $gpoNote = 'Data unavailable: Group Policy results could not be read' }
    }

    $result = Test-DscConfiguration -ReferenceConfiguration $mofPath
    $rows = @()
    foreach ($r in @($result)) {
        if ($r.PSObject.Properties['ResourcesInDesiredState']) { $rows += @($r.ResourcesInDesiredState | ForEach-Object { [pscustomobject]@{ Resource = [string]$_.ResourceId; Compliant = $true } }) }
        if ($r.PSObject.Properties['ResourcesNotInDesiredState']) { $rows += @($r.ResourcesNotInDesiredState | ForEach-Object { [pscustomobject]@{ Resource = [string]$_.ResourceId; Compliant = $false } }) }
    }
    $rows
    $drift = @($rows | Where-Object { -not $_.Compliant })
    $compliant = ($drift.Count -eq 0) -and ($rows.Count -gt 0)
    $summary = [ordered]@{ Id = $id; Mode = $Mode; Compliant = $compliant; NotCompliant = $drift.Count; PolicySource = $gpoNote; Result = '' }
    if ($rows.Count -eq 0) { $summary.Result = 'Data unavailable: no per-resource result returned'; [pscustomobject]$summary; exit 1 }

    if ($Mode -eq 'Test' -or $compliant) {
        if ($compliant) { $summary.Result = 'No change: compliant' } else { $summary.Result = 'Not compliant (test only)' }
        [pscustomobject]$summary
        if ($compliant) { exit 0 } else { exit 1 }
    }

    if ($lcm.ConfigurationMode -eq 'ApplyAndAutoCorrect') { throw 'The LCM is in ApplyAndAutoCorrect; set ApplyAndMonitor with DSC-02 before applying security settings.' }
    if (-not $PSCmdlet.ShouldProcess("Local security policy on $env:COMPUTERNAME", "Apply $($drift.Count) security setting(s)")) {
        $summary.Result = 'WhatIf: nothing applied'
        [pscustomobject]$summary
        exit 0
    }
    $snapshot = @()
    try { $snapshot = @(Get-DscConfiguration -ErrorAction Stop) } catch { Write-Verbose 'No current configuration on this node.' }
    $foreign = @($snapshot | ForEach-Object { [string]$_.ConfigurationName } | Where-Object { $_ -and $_ -ne $configName } | Select-Object -Unique)
    if ($foreign.Count -gt 0 -and -not $ReplaceCurrentConfiguration) {
        throw "This node already runs configuration '$($foreign -join ', ')'; applying replaces it. Pass -ReplaceCurrentConfiguration `$true after review."
    }
    # A full security policy export is the rollback point (secedit /configure with this file restores it).
    $rollback = Join-Path $workDir ('secpol-before-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.inf')
    & (Join-Path $env:windir 'System32\secedit.exe') /export /cfg $rollback /quiet
    if ($LASTEXITCODE -ne 0) { throw "Security policy export failed (secedit exit $LASTEXITCODE); nothing applied." }
    Start-DscConfiguration -Path $mofDir -Wait -ErrorAction Stop -Verbose:$false
    $after = @()
    foreach ($r in @(Test-DscConfiguration -ReferenceConfiguration $mofPath)) {
        if ($r.PSObject.Properties['ResourcesNotInDesiredState']) { $after += @($r.ResourcesNotInDesiredState | ForEach-Object { [string]$_.ResourceId }) }
    }
    $summary.Result = 'Applied; rollback export: ' + $rollback
    if ($after.Count -gt 0) { $summary.Result = 'Applied, but not compliant: ' + ($after -join '; '); [pscustomobject]$summary; exit 1 }
    $summary.Compliant = $true
    $summary.NotCompliant = 0
    [pscustomobject]$summary
    exit 0
} catch {
    Write-Error "$id failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
