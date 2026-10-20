<#
.SYNOPSIS
    Declares organizational units and groups with ActiveDirectoryDsc on a domain controller; dry run unless -Apply.
.DESCRIPTION
    Compiles a configuration of OUs and groups, tests it and prints the plan. With -Apply it applies it after
    checking a recent system state backup and that the LCM is not in auto-correct, because auto-correct
    recreates objects an administrator deleted on purpose. Identity infrastructure: follow the SRV-07 controls
    (lab rehearsal, approval, backup, no unattended run). Forest and domain promotion are not done here: they
    need the DSRM credential encrypted in the MOF (DSC-03) and an LCM that resumes after the reboot.
.NOTES
    RemoteHub: id=DSC-07; context=System; os=Server; risk=R2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string[]]$OrganizationalUnits = @('Servers', 'Workstations', 'Service Accounts'),
    [string[]]$Groups = @(),
    [int]$MaxBackupAgeHours = 24,
    [switch]$SkipBackupCheck,
    [bool]$ReplaceCurrentConfiguration = $false,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$id = 'DSC-07'
$configName = 'RhDirectoryObjects'
$workDir = Join-Path $env:ProgramData "RemoteHub\DSC\$id"
$logFile = Join-Path $env:ProgramData "RemoteHub\ScriptLogs\$id.log"
if (-not $Apply) { $WhatIfPreference = $true }

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
    $role = (Get-CimInstance -ClassName Win32_ComputerSystem).DomainRole
    if ($role -lt 4) { throw 'Run this on a domain controller; directory objects are applied by its LCM.' }
    if (-not (Get-Module -ListAvailable -Name ActiveDirectoryDsc)) { throw 'Data unavailable: ActiveDirectoryDsc is not installed (DSC-04).' }

    $domainDn = [string]([ADSI]'LDAP://RootDSE').defaultNamingContext
    if (-not $domainDn) { throw 'Data unavailable: could not read the domain naming context.' }

    $ous = foreach ($line in $OrganizationalUnits) {
        $p = $line -split '\|'
        $parent = $domainDn
        if ($p.Count -ge 2 -and $p[1]) { $parent = $p[1] }
        @{ Id = 'OU_' + (ConvertTo-SafeId ($p[0] + '_' + $parent)); Name = $p[0]; Path = $parent }
    }
    $grps = foreach ($line in $Groups) {
        $p = $line -split '\|'
        if ($p.Count -ne 4) { throw "Group entry '$line' must be Name|DomainLocal,Global,Universal|Security,Distribution|ParentDN." }
        if ($p[1] -notin @('DomainLocal', 'Global', 'Universal')) { throw "Group scope '$($p[1])' is not valid." }
        if ($p[2] -notin @('Security', 'Distribution')) { throw "Group category '$($p[2])' is not valid." }
        $parent = $domainDn
        if ($p[3]) { $parent = $p[3] }
        @{ Id = 'Grp_' + (ConvertTo-SafeId $p[0]); Name = $p[0]; Scope = $p[1]; Category = $p[2]; Path = $parent }
    }

    $configText = @'
Configuration RhDirectoryObjects {
    Import-DscResource -ModuleName ActiveDirectoryDsc
    Node $AllNodes.NodeName {
        foreach ($o in $Node.Ous) {
            ADOrganizationalUnit "$($o.Id)" { Name = $o.Name; Path = $o.Path; ProtectedFromAccidentalDeletion = $true; Ensure = 'Present' }
        }
        foreach ($g in $Node.Groups) {
            ADGroup "$($g.Id)" { GroupName = $g.Name; GroupScope = $g.Scope; Category = $g.Category; Path = $g.Path; Ensure = 'Present' }
        }
    }
}
'@
    $configData = @{ AllNodes = @(@{ NodeName = 'localhost'; Ous = @($ous); Groups = @($grps) }) }

    $mofDir = Join-Path $workDir 'Mof'
    $savedWhatIf = $WhatIfPreference
    $WhatIfPreference = $false
    try {
        if (-not (Test-Path $mofDir)) { New-Item -ItemType Directory -Path $mofDir -Force | Out-Null }
        . ([scriptblock]::Create($configText))
        RhDirectoryObjects -ConfigurationData $configData -OutputPath $mofDir | Out-Null
    } finally { $WhatIfPreference = $savedWhatIf }
    $mofPath = Join-Path $mofDir 'localhost.mof'

    $lcm = Get-DscLocalConfigurationManager
    if ($lcm.LCMState -eq 'Busy') { throw 'The LCM is busy applying a configuration; try again later.' }
    if ((Get-Service -Name WinRM).Status -ne 'Running') { throw 'WinRM is not running; DSC push and test need it.' }

    $drift = Get-DriftIds (Test-DscConfiguration -ReferenceConfiguration $mofPath)
    foreach ($o in $ous) { [pscustomobject]@{ Plan = 'OU'; Name = $o.Name; Path = $o.Path; Change = ($drift -contains "[ADOrganizationalUnit]$($o.Id)") } }
    foreach ($g in $grps) { [pscustomobject]@{ Plan = 'Group'; Name = $g.Name; Path = $g.Path; Change = ($drift -contains "[ADGroup]$($g.Id)") } }
    Write-RhLog ("Test: {0} object(s) to create or change" -f $drift.Count)

    if ($drift.Count -eq 0) { Write-Output 'No change: all declared directory objects are in the desired state.'; exit 0 }
    if (-not $Apply) {
        Write-Output 'Dry run: nothing changed. Run with -Apply to make the changes.'
        exit 0
    }

    if ($lcm.ConfigurationMode -eq 'ApplyAndAutoCorrect') {
        throw 'The LCM is in ApplyAndAutoCorrect, which would recreate deliberately deleted objects. Set ApplyAndMonitor with DSC-02 first.'
    }
    if (-not $SkipBackupCheck) {
        if (-not (Get-Command -Name Get-WBSummary -ErrorAction SilentlyContinue)) { throw 'Data unavailable: Windows Server Backup is not installed, so no recent backup can be confirmed. Use -SkipBackupCheck only with a verified backup.' }
        $last = (Get-WBSummary).LastSuccessfulBackupTime
        if (-not $last -or $last -lt (Get-Date).AddHours(-$MaxBackupAgeHours)) { throw "No successful backup in the last $MaxBackupAgeHours hours; take a system state backup first." }
    }

    $snapshot = @()
    try { $snapshot = @(Get-DscConfiguration -ErrorAction Stop) } catch { Write-Verbose 'No current configuration on this node.' }
    $foreign = @($snapshot | ForEach-Object { [string]$_.ConfigurationName } | Where-Object { $_ -and $_ -ne $configName } | Select-Object -Unique)
    if ($foreign.Count -gt 0 -and -not $ReplaceCurrentConfiguration) {
        throw "This node already runs configuration '$($foreign -join ', ')'; applying replaces it. Pass -ReplaceCurrentConfiguration `$true after review."
    }

    if ($PSCmdlet.ShouldProcess("Domain $domainDn", "Create or update $($drift.Count) directory object(s) via DSC")) {
        $rollback = 'None (no previous configuration)'
        if ($snapshot.Count -gt 0) {
            $rollback = Join-Path $workDir ('Before-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
            $snapshot | ForEach-Object {
                $h = [ordered]@{}
                foreach ($prop in $_.CimInstanceProperties) { $h[$prop.Name] = [string]$prop.Value }
                [pscustomobject]$h
            } | ConvertTo-Json -Depth 3 | Set-Content -Path $rollback -Encoding UTF8
        }
        Write-RhLog "Applying $configName; previous state: $rollback; Restore-DscConfiguration returns to the previous MOF"
        Start-DscConfiguration -Path $mofDir -Wait -ErrorAction Stop -Verbose:$false
        $after = Get-DriftIds (Test-DscConfiguration -ReferenceConfiguration $mofPath)
        Write-RhLog ("Applied. Remaining drift: {0}" -f $after.Count)
        if ($after.Count -gt 0) {
            [pscustomobject]@{ Id = $id; Result = 'Applied, but still not in desired state'; Remaining = ($after -join '; '); Rollback = $rollback }
            exit 1
        }
        [pscustomobject]@{ Id = $id; Result = 'Applied'; Changed = $drift.Count; Rollback = $rollback }
    }
    exit 0
} catch {
    Write-RhLog "Error: $($_.Exception.Message)"
    Write-Error "$id failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
