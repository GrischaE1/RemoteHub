<#
.SYNOPSIS
    Tests a Windows 10/11 client against a light DSC baseline, reports drift and applies it only when asked.
.DESCRIPTION
    Declares registry values, services, optional features, local group members, folders and (with
    ComputerManagementDsc) disabled scheduled tasks. Drift is written to an inventory file; -Sensor returns
    only True/False. Define one owner per setting (DSC, Group Policy or MDM): overlap causes drift loops, and
    settings the MDM supports belong in MDM. Local push needs WinRM; where it is stopped use DSC-13 (DSC v3).
.NOTES
    RemoteHub: id=DSC-06; context=System; os=Client; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Test', 'Apply')]
    [string]$Mode = 'Test',
    [string[]]$OptionalFeaturesDisabled = @('SMB1Protocol', 'MicrosoftWindowsPowerShellV2Root'),
    [string[]]$OptionalFeaturesEnabled = @(),
    [string[]]$ServicesDisabled = @('RemoteRegistry'),
    [string[]]$RegistryValues = @(),
    [string[]]$GroupMembers = @(),
    [string[]]$Directories = @(),
    [string[]]$ScheduledTasksDisabled = @(),
    [bool]$ReplaceCurrentConfiguration = $false,
    [switch]$Sensor
)

$ErrorActionPreference = 'Stop'
$id = 'DSC-06'
$workDir = Join-Path $env:ProgramData "RemoteHub\DSC\$id"

function Get-DriftIds {
    param($TestResult)
    $ids = @()
    foreach ($r in @($TestResult)) {
        if ($r.PSObject.Properties['ResourcesNotInDesiredState']) { $ids += @($r.ResourcesNotInDesiredState | ForEach-Object { [string]$_.ResourceId }) }
        elseif ($r.PSObject.Properties['InDesiredState'] -and -not $r.InDesiredState) { $ids += 'Data unavailable (resource list not returned)' }
    }
    , $ids
}

function ConvertTo-SafeId { param([string]$Text) ($Text -replace '[^A-Za-z0-9]', '_') }

try {
    if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Run this script in Windows PowerShell 5.1 (DSC 1.1).' }
    if ((Get-CimInstance -ClassName Win32_OperatingSystem).ProductType -ne 1) { throw 'This baseline is for Windows 10 and 11; use DSC-05 on servers.' }

    $registry = foreach ($line in $RegistryValues) {
        $p = $line -split '\|'
        if ($p.Count -ne 4) { throw "Registry entry '$line' must be Key|ValueName|Type|Data." }
        @{ Id = 'Reg_' + (ConvertTo-SafeId ($p[0] + '_' + $p[1])); Key = $p[0]; ValueName = $p[1]; ValueType = $p[2]; ValueData = $p[3] }
    }
    $groups = foreach ($line in $GroupMembers) {
        $p = $line -split '\|'
        if ($p.Count -ne 2) { throw "Group entry '$line' must be Group|member1,member2." }
        @{ Id = 'Grp_' + (ConvertTo-SafeId $p[0]); Name = $p[0]; Members = @($p[1] -split ',' | ForEach-Object { $_.Trim() }) }
    }
    $dirs = foreach ($d in $Directories) { @{ Id = 'Dir_' + (ConvertTo-SafeId $d); Path = $d } }
    $tasks = foreach ($line in $ScheduledTasksDisabled) {
        $p = $line -split '\|'
        if ($p.Count -ne 2) { throw "Scheduled task entry '$line' must be TaskPath|TaskName." }
        @{ Id = 'Task_' + (ConvertTo-SafeId ($p[0] + $p[1])); TaskPath = $p[0]; TaskName = $p[1] }
    }

    $extraImport = ''
    $taskBlock = ''
    if ($tasks) {
        if (-not (Get-Module -ListAvailable -Name ComputerManagementDsc)) { throw 'Scheduled tasks need ComputerManagementDsc (install with DSC-04).' }
        $extraImport = 'Import-DscResource -ModuleName ComputerManagementDsc'
        $taskBlock = 'foreach ($t in $Node.Tasks) { ScheduledTask "$($t.Id)" { TaskName = $t.TaskName; TaskPath = $t.TaskPath; Enable = $false } }'
    }

    $configText = @"
Configuration RhClientBaseline {
    Import-DscResource -ModuleName PSDesiredStateConfiguration
    $extraImport
    Node `$AllNodes.NodeName {
        foreach (`$f in `$Node.FeaturesOff) { WindowsOptionalFeature "Off_`$f" { Name = `$f; Ensure = 'Disable' } }
        foreach (`$f in `$Node.FeaturesOn) { WindowsOptionalFeature "On_`$f" { Name = `$f; Ensure = 'Enable' } }
        foreach (`$s in `$Node.ServicesDisabled) { Service "Disabled_`$s" { Name = `$s; State = 'Stopped'; StartupType = 'Disabled' } }
        foreach (`$r in `$Node.Registry) { Registry "`$(`$r.Id)" { Key = `$r.Key; ValueName = `$r.ValueName; ValueType = `$r.ValueType; ValueData = `$r.ValueData; Ensure = 'Present' } }
        foreach (`$g in `$Node.Groups) { Group "`$(`$g.Id)" { GroupName = `$g.Name; MembersToInclude = `$g.Members; Ensure = 'Present' } }
        foreach (`$d in `$Node.Dirs) { File "`$(`$d.Id)" { DestinationPath = `$d.Path; Type = 'Directory'; Ensure = 'Present' } }
        $taskBlock
    }
}
"@
    $configData = @{ AllNodes = @(@{
                NodeName = 'localhost'; FeaturesOff = $OptionalFeaturesDisabled; FeaturesOn = $OptionalFeaturesEnabled
                ServicesDisabled = $ServicesDisabled; Registry = @($registry); Groups = @($groups); Dirs = @($dirs); Tasks = @($tasks)
            })
    }

    $mofDir = Join-Path $workDir 'Mof'
    $savedWhatIf = $WhatIfPreference
    $WhatIfPreference = $false
    try {
        if (-not (Test-Path $mofDir)) { New-Item -ItemType Directory -Path $mofDir -Force | Out-Null }
        . ([scriptblock]::Create($configText))
        RhClientBaseline -ConfigurationData $configData -OutputPath $mofDir | Out-Null
    } finally { $WhatIfPreference = $savedWhatIf }
    $mofPath = Join-Path $mofDir 'localhost.mof'

    $lcm = Get-DscLocalConfigurationManager
    $blocked = ''
    if ($lcm.LCMState -eq 'Busy') { $blocked = 'The LCM is busy applying a configuration' }
    elseif ((Get-Service -Name WinRM).Status -ne 'Running') { $blocked = 'WinRM is not running; use DSC-13 (DSC v3) on this client' }
    if ($blocked) {
        if ($Sensor) { Write-Output 'Data unavailable' } else { [pscustomobject]@{ Id = $id; InDesiredState = 'Data unavailable'; Result = $blocked } }
        exit 1
    }

    $drift = Get-DriftIds (Test-DscConfiguration -ReferenceConfiguration $mofPath)
    $inState = ($drift.Count -eq 0)
    $savedWhatIf = $WhatIfPreference
    $WhatIfPreference = $false
    try {
        # Inventory picks up the drift list from here, so it is refreshed on every run.
        [pscustomobject]@{ Computer = $env:COMPUTERNAME; Checked = (Get-Date -Format 's'); InDesiredState = $inState; Drift = $drift } |
            ConvertTo-Json -Depth 3 | Set-Content -Path (Join-Path $workDir 'drift.json') -Encoding UTF8
    } finally { $WhatIfPreference = $savedWhatIf }

    if ($Sensor) { Write-Output $inState; if ($inState) { exit 0 } else { exit 1 } }
    foreach ($d in $drift) { [pscustomobject]@{ Id = $id; Resource = $d; InDesiredState = $false } }

    $summary = [ordered]@{ Id = $id; Computer = $env:COMPUTERNAME; Mode = $Mode; InDesiredState = $inState; Drifted = $drift.Count
        LcmMode = [string]$lcm.ConfigurationMode; Result = ''; RollbackFile = '' }
    if ($Mode -eq 'Test' -or $inState) {
        if ($inState) { $summary.Result = 'No change: in desired state' } else { $summary.Result = 'Not in desired state (test only)' }
        [pscustomobject]$summary
        if ($inState) { exit 0 } else { exit 1 }
    }

    if ($lcm.ConfigurationMode -eq 'ApplyAndAutoCorrect') {
        throw 'The LCM is in ApplyAndAutoCorrect, which flaps on clients. Set ApplyAndMonitor with DSC-02 first.'
    }
    if (-not $PSCmdlet.ShouldProcess("DSC configuration on $env:COMPUTERNAME", "Apply client baseline ($($drift.Count) resource(s) to change)")) {
        $summary.Result = 'WhatIf: nothing applied'
        [pscustomobject]$summary
        exit 0
    }
    # The MOFs the LCM keeps are encrypted at rest, so the prior state is captured through Get-DscConfiguration.
    $snapshot = @()
    try { $snapshot = @(Get-DscConfiguration -ErrorAction Stop) } catch { Write-Verbose 'No current configuration on this node.' }
    $foreign = @($snapshot | ForEach-Object { [string]$_.ConfigurationName } | Where-Object { $_ -and $_ -ne 'RhClientBaseline' } | Select-Object -Unique)
    if ($foreign.Count -gt 0 -and -not $ReplaceCurrentConfiguration) {
        throw "This node already runs configuration '$($foreign -join ', ')'; applying replaces it. Pass -ReplaceCurrentConfiguration `$true after review."
    }
    $summary.RollbackFile = 'None (no previous configuration)'
    if ($snapshot.Count -gt 0) {
        $summary.RollbackFile = Join-Path $workDir ('Before-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
        $snapshot | ForEach-Object {
            $h = [ordered]@{}
            foreach ($prop in $_.CimInstanceProperties) { $h[$prop.Name] = [string]$prop.Value }
            [pscustomobject]$h
        } | ConvertTo-Json -Depth 3 | Set-Content -Path $summary.RollbackFile -Encoding UTF8
    }
    Start-DscConfiguration -Path $mofDir -Wait -ErrorAction Stop -Verbose:$false
    $after = Get-DriftIds (Test-DscConfiguration -ReferenceConfiguration $mofPath)
    if ((Get-DscLocalConfigurationManager).LCMState -eq 'PendingReboot') {
        $summary.Result = 'Applied; restart needed, then test again'
        [pscustomobject]$summary
        exit 3010
    }
    if ($after.Count -gt 0) {
        $summary.Result = 'Applied, but still not in desired state: ' + ($after -join '; ')
        [pscustomobject]$summary
        exit 1
    }
    $summary.InDesiredState = $true
    $summary.Result = 'Applied'
    [pscustomobject]$summary
    exit 0
} catch {
    if ($Sensor) { Write-Output 'Data unavailable' }
    Write-Error "$id failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
