<#
.SYNOPSIS
    Compiles a server baseline to a MOF, tests this server against it and applies it only when asked.
.DESCRIPTION
    Declares roles and features, services, registry values (event log sizes by default), environment variables,
    local group members and optionally the time zone and power plan, using built-in DSC resources.
    Flow: compile, test, then apply with -Mode Apply only after review; the LCM should be in ApplyAndMonitor
    (DSC-02) so drift is reported, not corrected. The Script resource is not used. The node is never allowed to
    restart itself from here. Applying replaces the node's current DSC configuration; the previous MOF is kept.
.NOTES
    RemoteHub: id=DSC-05; context=System; os=Server; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Test', 'Apply')]
    [string]$Mode = 'Test',
    [string[]]$FeaturesPresent = @(),
    [string[]]$FeaturesAbsent = @('FS-SMB1', 'PowerShell-V2'),
    [string[]]$ServicesRunning = @('EventLog', 'W32Time'),
    [string[]]$ServicesDisabled = @(),
    [string[]]$RegistryValues = @(
        'HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\EventLog\Security|MaxSize|Dword|201326592',
        'HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\EventLog\System|MaxSize|Dword|67108864',
        'HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\EventLog\Application|MaxSize|Dword|67108864'),
    [string[]]$EnvironmentVariables = @(),
    [string[]]$GroupMembers = @(),
    [string]$TimeZone = '',
    [ValidateSet('None', 'Balanced', 'High performance')]
    [string]$PowerPlan = 'None',
    [bool]$AllowAutoCorrect = $false,
    [bool]$ReplaceCurrentConfiguration = $false
)

$ErrorActionPreference = 'Stop'
$id = 'DSC-05'
$workDir = Join-Path $env:ProgramData "RemoteHub\DSC\$id"
$logFile = Join-Path $env:ProgramData "RemoteHub\ScriptLogs\$id.log"

function Write-RhLog {
    param([string]$Message)
    Write-Verbose $Message
    try {
        $dir = Split-Path $logFile -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force -WhatIf:$false | Out-Null }
        if ((Test-Path $logFile) -and ((Get-Item $logFile).Length -gt 1MB)) { Move-Item $logFile "$logFile.1" -Force -WhatIf:$false }
        Add-Content -Path $logFile -Value ('{0} {1}' -f (Get-Date -Format 's'), $Message) -Encoding UTF8 -WhatIf:$false
    } catch { Write-Verbose "Log write failed: $($_.Exception.Message)" }
}

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
    if ((Get-CimInstance -ClassName Win32_OperatingSystem).ProductType -eq 1) { throw 'This baseline is for Windows Server; use DSC-06 on clients.' }

    $registry = foreach ($line in $RegistryValues) {
        $p = $line -split '\|'
        if ($p.Count -ne 4) { throw "Registry entry '$line' must be Key|ValueName|Type|Data." }
        @{ Id = 'Reg_' + (ConvertTo-SafeId ($p[0] + '_' + $p[1])); Key = $p[0]; ValueName = $p[1]; ValueType = $p[2]; ValueData = $p[3] }
    }
    $envVars = foreach ($line in $EnvironmentVariables) {
        if ($line -notmatch '^([^=]+)=(.*)$') { throw "Environment entry '$line' must be Name=Value." }
        @{ Id = 'Env_' + (ConvertTo-SafeId $Matches[1]); Name = $Matches[1]; Value = $Matches[2] }
    }
    $groups = foreach ($line in $GroupMembers) {
        $p = $line -split '\|'
        if ($p.Count -ne 2) { throw "Group entry '$line' must be Group|member1,member2." }
        @{ Id = 'Grp_' + (ConvertTo-SafeId $p[0]); Name = $p[0]; Members = @($p[1] -split ',' | ForEach-Object { $_.Trim() }) }
    }

    $extraImport = ''
    $extraBlock = ''
    if ($TimeZone -or $PowerPlan -ne 'None') {
        if (-not (Get-Module -ListAvailable -Name ComputerManagementDsc)) { throw 'TimeZone and PowerPlan need ComputerManagementDsc (install with DSC-04).' }
        $extraImport = 'Import-DscResource -ModuleName ComputerManagementDsc'
        if ($TimeZone) { $extraBlock += "TimeZone Tz { IsSingleInstance = 'Yes'; TimeZone = `$Node.TimeZone }`n" }
        if ($PowerPlan -ne 'None') { $extraBlock += "PowerPlan Plan { IsSingleInstance = 'Yes'; Name = `$Node.PowerPlan }`n" }
    }

    # Features are declared first; the LCM applies resources in declaration order.
    $configText = @"
Configuration RhServerBaseline {
    Import-DscResource -ModuleName PSDesiredStateConfiguration
    $extraImport
    Node `$AllNodes.NodeName {
        foreach (`$f in `$Node.FeaturesPresent) { WindowsFeature "Present_`$f" { Name = `$f; Ensure = 'Present' } }
        foreach (`$f in `$Node.FeaturesAbsent) { WindowsFeature "Absent_`$f" { Name = `$f; Ensure = 'Absent' } }
        foreach (`$s in `$Node.ServicesRunning) { Service "Running_`$s" { Name = `$s; State = 'Running' } }
        foreach (`$s in `$Node.ServicesDisabled) { Service "Disabled_`$s" { Name = `$s; State = 'Stopped'; StartupType = 'Disabled' } }
        foreach (`$r in `$Node.Registry) { Registry "`$(`$r.Id)" { Key = `$r.Key; ValueName = `$r.ValueName; ValueType = `$r.ValueType; ValueData = `$r.ValueData; Ensure = 'Present' } }
        foreach (`$e in `$Node.Environment) { Environment "`$(`$e.Id)" { Name = `$e.Name; Value = `$e.Value; Ensure = 'Present' } }
        foreach (`$g in `$Node.Groups) { Group "`$(`$g.Id)" { GroupName = `$g.Name; MembersToInclude = `$g.Members; Ensure = 'Present' } }
        $extraBlock
    }
}
"@
    $configData = @{ AllNodes = @(@{
                NodeName = 'localhost'; FeaturesPresent = $FeaturesPresent; FeaturesAbsent = $FeaturesAbsent
                ServicesRunning = $ServicesRunning; ServicesDisabled = $ServicesDisabled; Registry = @($registry)
                Environment = @($envVars); Groups = @($groups); TimeZone = $TimeZone; PowerPlan = $PowerPlan
            })
    }

    $mofDir = Join-Path $workDir 'Mof'
    # Compiling only writes the MOF, so it runs even under -WhatIf to give a real test result.
    $savedWhatIf = $WhatIfPreference
    $WhatIfPreference = $false
    try {
        if (-not (Test-Path $mofDir)) { New-Item -ItemType Directory -Path $mofDir -Force | Out-Null }
        . ([scriptblock]::Create($configText))
        RhServerBaseline -ConfigurationData $configData -OutputPath $mofDir | Out-Null
    } finally { $WhatIfPreference = $savedWhatIf }
    $mofPath = Join-Path $mofDir 'localhost.mof'
    Write-RhLog "Compiled $mofPath"

    $lcm = Get-DscLocalConfigurationManager
    if ($lcm.LCMState -eq 'Busy') { throw 'The LCM is busy applying a configuration; try again later.' }
    if ((Get-Service -Name WinRM).Status -ne 'Running') { throw 'WinRM is not running; DSC push and test need it on this node.' }

    $drift = Get-DriftIds (Test-DscConfiguration -ReferenceConfiguration $mofPath)
    foreach ($d in $drift) { [pscustomobject]@{ Id = $id; Resource = $d; InDesiredState = $false } }
    Write-RhLog ("Test: {0} resource(s) not in desired state" -f $drift.Count)

    $summary = [ordered]@{ Id = $id; Computer = $env:COMPUTERNAME; Mode = $Mode; InDesiredState = ($drift.Count -eq 0)
        Drifted = $drift.Count; LcmMode = [string]$lcm.ConfigurationMode; Result = ''; RollbackFile = ''; Mof = $mofPath }

    if ($Mode -eq 'Test' -or $drift.Count -eq 0) {
        if ($drift.Count -eq 0) { $summary.Result = 'No change: in desired state' } else { $summary.Result = 'Not in desired state (test only)' }
        [pscustomobject]$summary
        if ($drift.Count -eq 0) { exit 0 } else { exit 1 }
    }

    if ($lcm.ConfigurationMode -eq 'ApplyAndAutoCorrect' -and -not $AllowAutoCorrect) {
        throw 'The LCM is in ApplyAndAutoCorrect. Set ApplyAndMonitor with DSC-02 first, or pass -AllowAutoCorrect $true after approval.'
    }
    if (-not $PSCmdlet.ShouldProcess("DSC configuration on $env:COMPUTERNAME", "Apply server baseline ($($drift.Count) resource(s) to change)")) {
        $summary.Result = 'WhatIf: nothing applied'
        [pscustomobject]$summary
        exit 0
    }

    # The MOFs the LCM keeps are encrypted at rest, so the prior state is captured through Get-DscConfiguration.
    $snapshot = @()
    try { $snapshot = @(Get-DscConfiguration -ErrorAction Stop) } catch { Write-Verbose 'No current configuration on this node.' }
    $foreign = @($snapshot | ForEach-Object { [string]$_.ConfigurationName } | Where-Object { $_ -and $_ -ne 'RhServerBaseline' } | Select-Object -Unique)
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
    Write-RhLog "Applying; prior state: $($summary.RollbackFile); Restore-DscConfiguration returns to the previous MOF"
    Start-DscConfiguration -Path $mofDir -Wait -ErrorAction Stop -Verbose:$false
    $after = Get-DriftIds (Test-DscConfiguration -ReferenceConfiguration $mofPath)
    $state = (Get-DscLocalConfigurationManager).LCMState
    Write-RhLog ("Applied. Remaining drift: {0}; LCM state: {1}" -f $after.Count, $state)

    # Feature removal often stays out of state until the restart, so a pending reboot wins over remaining drift.
    if ($state -eq 'PendingReboot') { $summary.Result = 'Applied; restart needed, then test again'; [pscustomobject]$summary; exit 3010 }
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
    Write-RhLog "Error: $($_.Exception.Message)"
    Write-Error "$id failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
