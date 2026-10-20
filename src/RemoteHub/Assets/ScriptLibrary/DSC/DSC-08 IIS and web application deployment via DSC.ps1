<#
.SYNOPSIS
    Declares the IIS role, an application pool, a site with its binding and content, and the default site's state; tests and applies on request.
.DESCRIPTION
    Uses WindowsFeature for the role and WebAdministrationDsc (WebAppPool, WebSite) plus File for content.
    The default site is kept, stopped or removed only when chosen. The pool runs as ApplicationPoolIdentity;
    a gMSA identity needs a credential in an encrypted MOF (DSC-03). HTTPS uses a certificate that is already
    in LocalMachine\My; importing a PFX with a password is not done here. Verify module names on your feed.
.NOTES
    RemoteHub: id=DSC-08; context=System; os=Server; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Test', 'Apply')]
    [string]$Mode = 'Test',
    [string[]]$Features = @('Web-Server'),
    [ValidateSet('Keep', 'Stop', 'Remove')]
    [string]$DefaultSite = 'Keep',
    [string]$SiteName = '',
    [string]$AppPoolName = '',
    [string]$PhysicalPath = '',
    [string]$ContentSource = '',
    [ValidateSet('http', 'https')]
    [string]$Protocol = 'https',
    [ValidateRange(1, 65535)]
    [int]$Port = 443,
    [string]$HostName = '',
    [string]$CertificateThumbprint = '',
    [bool]$ReplaceCurrentConfiguration = $false
)

$ErrorActionPreference = 'Stop'
$id = 'DSC-08'
$configName = 'RhIisSite'
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

function ConvertTo-Literal { param([string]$Text) "'" + ($Text -replace "'", "''") + "'" }

try {
    if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Run this script in Windows PowerShell 5.1 (DSC 1.1).' }
    if ((Get-CimInstance -ClassName Win32_OperatingSystem).ProductType -eq 1) { throw 'This configuration is for Windows Server.' }

    $body = New-Object System.Text.StringBuilder
    $import = ''
    $roleDep = "; DependsOn = '[WindowsFeature]Feature_Web-Server'"
    if ($Features -notcontains 'Web-Server') { $roleDep = '' }
    if ($SiteName -or $DefaultSite -ne 'Keep') {
        $res = Get-DscResource -Module WebAdministrationDsc -Name WebSite -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $res) { throw 'Data unavailable: WebAdministrationDsc with the WebSite resource is not installed (DSC-04).' }
        $import = 'Import-DscResource -ModuleName WebAdministrationDsc'
        # The binding class name changed between module generations, so it is read from the installed schema.
        $bindType = ($res.Properties | Where-Object { $_.Name -eq 'BindingInfo' }).PropertyType
        if ($bindType -notmatch '^\[(\w+)\[\]\]$') { throw 'Data unavailable: cannot read the BindingInfo type from WebSite.' }
        $bindClass = $Matches[1]

        if ($DefaultSite -eq 'Stop') {
            [void]$body.AppendLine("WebSite DefaultSite { Name = 'Default Web Site'; State = 'Stopped'; Ensure = 'Present'$roleDep }")
        } elseif ($DefaultSite -eq 'Remove') {
            [void]$body.AppendLine("WebSite DefaultSite { Name = 'Default Web Site'; Ensure = 'Absent'$roleDep }")
        }

        if ($SiteName) {
            $pool = $AppPoolName
            if (-not $pool) { $pool = $SiteName }
            $path = $PhysicalPath
            if (-not $path) { $path = Join-Path $env:SystemDrive ('inetpub\' + ($SiteName -replace '[^A-Za-z0-9\-_]', '_')) }
            $bind = "Protocol = '$Protocol'; Port = $Port"
            if ($HostName) { $bind += '; HostName = ' + (ConvertTo-Literal $HostName) }
            if ($Protocol -eq 'https') {
                $thumb = $CertificateThumbprint.Trim().ToUpperInvariant()
                if ($thumb -notmatch '^[0-9A-F]{40}$') { throw 'HTTPS needs -CertificateThumbprint of a certificate in LocalMachine\My.' }
                $cert = Get-Item -Path ('Cert:\LocalMachine\My\' + $thumb) -ErrorAction SilentlyContinue
                if (-not $cert -or -not $cert.HasPrivateKey) { throw "Certificate $thumb with a private key is not in LocalMachine\My." }
                if ($cert.NotAfter -lt (Get-Date).AddDays(14)) { Write-Warning "Certificate $thumb expires on $($cert.NotAfter.ToString('yyyy-MM-dd'))." }
                $bind += "; CertificateThumbprint = '$thumb'; CertificateStoreName = 'My'"
            }
            $fileProps = "DestinationPath = $(ConvertTo-Literal $path); Type = 'Directory'; Ensure = 'Present'"
            if ($ContentSource) { $fileProps += "; SourcePath = $(ConvertTo-Literal $ContentSource); Recurse = `$true; Checksum = 'SHA-256'; MatchSource = `$true" }
            [void]$body.AppendLine("WebAppPool Pool { Name = $(ConvertTo-Literal $pool); Ensure = 'Present'; State = 'Started'; identityType = 'ApplicationPoolIdentity'$roleDep }")
            [void]$body.AppendLine("File Content { $fileProps }")
            [void]$body.AppendLine("WebSite Site { Name = $(ConvertTo-Literal $SiteName); PhysicalPath = $(ConvertTo-Literal $path); ApplicationPool = $(ConvertTo-Literal $pool); State = 'Started'; Ensure = 'Present'; BindingInfo = @($bindClass { $bind }); DependsOn = @('[WebAppPool]Pool', '[File]Content') }")
        }
    }

    $configText = @"
Configuration $configName {
    Import-DscResource -ModuleName PSDesiredStateConfiguration
    $import
    Node 'localhost' {
        foreach (`$f in @($(($Features | ForEach-Object { ConvertTo-Literal $_ }) -join ', '))) { WindowsFeature "Feature_`$f" { Name = `$f; Ensure = 'Present' } }
        $($body.ToString())
    }
}
"@
    $mofDir = Join-Path $workDir 'Mof'
    $savedWhatIf = $WhatIfPreference
    $WhatIfPreference = $false
    try {
        if (-not (Test-Path $mofDir)) { New-Item -ItemType Directory -Path $mofDir -Force | Out-Null }
        . ([scriptblock]::Create($configText))
        & $configName -OutputPath $mofDir | Out-Null
    } finally { $WhatIfPreference = $savedWhatIf }
    $mofPath = Join-Path $mofDir 'localhost.mof'
    Write-RhLog "Compiled $mofPath"

    $lcm = Get-DscLocalConfigurationManager
    if ($lcm.LCMState -eq 'Busy') { throw 'The LCM is busy applying a configuration; try again later.' }
    if ((Get-Service -Name WinRM).Status -ne 'Running') { throw 'WinRM is not running; DSC push and test need it.' }

    $drift = Get-DriftIds (Test-DscConfiguration -ReferenceConfiguration $mofPath)
    foreach ($d in $drift) { [pscustomobject]@{ Id = $id; Resource = $d; InDesiredState = $false } }
    $summary = [ordered]@{ Id = $id; Mode = $Mode; InDesiredState = ($drift.Count -eq 0); Drifted = $drift.Count; Result = ''; RollbackFile = '' }
    if ($Mode -eq 'Test' -or $drift.Count -eq 0) {
        if ($drift.Count -eq 0) { $summary.Result = 'No change: in desired state' } else { $summary.Result = 'Not in desired state (test only)' }
        [pscustomobject]$summary
        if ($drift.Count -eq 0) { exit 0 } else { exit 1 }
    }

    if (-not $PSCmdlet.ShouldProcess("IIS on $env:COMPUTERNAME", "Apply web configuration ($($drift.Count) resource(s) to change)")) {
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
    # IIS keeps its own configuration history; a backup gives a rollback point independent of DSC.
    $backupName = 'RemoteHub-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
    $appcmd = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'
    if (Test-Path -LiteralPath $appcmd) {
        & $appcmd add backup $backupName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "IIS configuration backup failed (appcmd exit $LASTEXITCODE)." }
        $summary.RollbackFile = "IIS backup $backupName (appcmd restore backup $backupName)"
    } else {
        $summary.RollbackFile = 'IIS not installed before this run'
    }
    Write-RhLog "Applying; rollback: $($summary.RollbackFile)"
    Start-DscConfiguration -Path $mofDir -Wait -ErrorAction Stop -Verbose:$false
    $after = Get-DriftIds (Test-DscConfiguration -ReferenceConfiguration $mofPath)
    Write-RhLog ("Applied. Remaining drift: {0}" -f $after.Count)
    if ((Get-DscLocalConfigurationManager).LCMState -eq 'PendingReboot') { $summary.Result = 'Applied; restart needed'; [pscustomobject]$summary; exit 3010 }
    if ($after.Count -gt 0) { $summary.Result = 'Applied, but still not in desired state: ' + ($after -join '; '); [pscustomobject]$summary; exit 1 }
    $summary.InDesiredState = $true
    $summary.Result = 'Applied'
    [pscustomobject]$summary
    exit 0
} catch {
    Write-RhLog "Error: $($_.Exception.Message)"
    Write-Error "$id failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
