<#
.SYNOPSIS
    Reports a node's DSC pull registration and last contact, registers a node with an SMB pull share, or publishes MOFs and modules to it.
.DESCRIPTION
    Report (node, SYSTEM): refresh mode, pull source, AgentId and the last run. Azure Automation State
    Configuration retires on 30 September 2027 and deletes stored configurations and registrations, so plan
    the move to Azure Machine Configuration (DSC-14). The HTTPS pull service is legacy and not developed further.
    RegisterNode (SYSTEM): points the LCM at an SMB share with a ConfigurationID; no registration key is handled.
    Publish (USER): copies MOFs as <ConfigurationID>.mof and zipped modules to the share, with checksum files.
    Registration keys and share credentials are secrets: never put them in scripts or logs; rotate if exposed.
.NOTES
    RemoteHub: id=DSC-12; context=Both; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Report', 'RegisterNode', 'Publish')]
    [string]$Mode = 'Report',
    [string]$SharePath = '',
    [string]$ConfigurationId = '',
    [ValidateRange(30, 44640)]
    [int]$RefreshFrequencyMins = 30,
    [string]$MofFolder = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'RemoteHub\DSC\Output'),
    [string[]]$NodeConfigurationIds = @(),
    [string[]]$ModuleNames = @()
)

$ErrorActionPreference = 'Stop'
$id = 'DSC-12'

function Get-FileSha { param([string]$Path) (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

function Invoke-Report {
    $lcm = Get-DscLocalConfigurationManager
    $sources = @($lcm.ConfigurationDownloadManagers | ForEach-Object {
            if ($_.PSObject.Properties['ServerURL'] -and $_.ServerURL) { [string]$_.ServerURL }
            elseif ($_.PSObject.Properties['SourcePath'] -and $_.SourcePath) { [string]$_.SourcePath }
        })
    $kind = 'Push (no pull source)'
    if ($sources | Where-Object { $_ -match 'azure-automation\.net' }) { $kind = 'Azure Automation State Configuration (retires 30 September 2027)' }
    elseif ($sources | Where-Object { $_ -match '^https?://' }) { $kind = 'HTTPS pull service (legacy)' }
    elseif ($sources.Count -gt 0) { $kind = 'SMB share' }

    $last = $null
    try { $last = Get-DscConfigurationStatus -ErrorAction Stop } catch { Write-Verbose "No status: $($_.Exception.Message)" }
    [pscustomobject]@{
        Id              = $id
        Computer        = $env:COMPUTERNAME
        RefreshMode     = [string]$lcm.RefreshMode
        Delivery        = $kind
        PullSources     = ($sources -join '; ')
        ConfigurationID = [string]$lcm.ConfigurationID
        AgentId         = [string]$lcm.AgentId
        LastRun         = $(if ($last) { $last.StartDate.ToString('s') } else { 'Data unavailable' })
        LastRunStatus   = $(if ($last) { [string]$last.Status } else { 'Data unavailable' })
    }
    if ($lcm.RefreshMode -eq 'Pull' -and -not $last) { return 1 }
    if ($last -and $last.Status -eq 'Failure') { return 1 }
    return 0
}

function Invoke-Register {
    if ($SharePath -notmatch '^\\\\[^\\]+\\[^\\]+') { throw 'RegisterNode needs -SharePath as a UNC path (\\server\share).' }
    $guid = [guid]::Empty
    if (-not [guid]::TryParse($ConfigurationId, [ref]$guid)) { throw 'RegisterNode needs -ConfigurationId as a GUID; SMB pull finds the MOF by that ID.' }
    # As SYSTEM this checks the computer account's access, which is what the LCM uses.
    if (-not (Test-Path -LiteralPath $SharePath)) { throw "The share $SharePath is not reachable with this node's computer account." }

    $lcm = Get-DscLocalConfigurationManager
    $current = @($lcm.ConfigurationDownloadManagers | ForEach-Object { [string]$_.SourcePath })
    if ($lcm.RefreshMode -eq 'Pull' -and $current -contains $SharePath -and [string]$lcm.ConfigurationID -eq $guid.ToString()) {
        [pscustomobject]@{ Id = $id; Result = 'No change: already registered with this share and ConfigurationID' }
        return 0
    }
    if (-not $PSCmdlet.ShouldProcess("LCM on $env:COMPUTERNAME", "Register for SMB pull from $SharePath")) {
        [pscustomobject]@{ Id = $id; Result = 'WhatIf: not registered'; Share = $SharePath }
        return 0
    }
    $workDir = Join-Path $env:ProgramData "RemoteHub\DSC\$id"
    if (-not (Test-Path $workDir)) { New-Item -ItemType Directory -Path $workDir -Force | Out-Null }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $lcm | Select-Object RefreshMode, ConfigurationMode, ConfigurationID, ConfigurationModeFrequencyMins, RefreshFrequencyMins, RebootNodeIfNeeded, ActionAfterReboot, CertificateID |
        ConvertTo-Json | Set-Content -Path (Join-Path $workDir "lcm-before-$stamp.json") -Encoding UTF8

    $mode = [string]$lcm.ConfigurationMode
    $freq = [int]$lcm.ConfigurationModeFrequencyMins
    if ($freq -lt 15) { $freq = 15 }
    $cert = ''
    if ($lcm.CertificateID) { $cert = "CertificateID = '$([string]$lcm.CertificateID)'" }
    $share = $SharePath -replace "'", "''"
    # Settings the admin chose with DSC-02 are carried over; only the delivery changes.
    $metaText = @"
[DscLocalConfigurationManager()]
Configuration RhPullSmb {
    Node 'localhost' {
        Settings {
            RefreshMode = 'Pull'; ConfigurationID = '$($guid.ToString())'; ConfigurationMode = '$mode'
            ConfigurationModeFrequencyMins = $freq; RefreshFrequencyMins = $RefreshFrequencyMins
            RebootNodeIfNeeded = `$$([bool]$lcm.RebootNodeIfNeeded); $cert
        }
        ConfigurationRepositoryShare SmbConfig { SourcePath = '$share' }
        ResourceRepositoryShare SmbModules { SourcePath = '$share' }
    }
}
"@
    $mofDir = Join-Path $workDir "meta-$stamp"
    . ([scriptblock]::Create($metaText))
    RhPullSmb -OutputPath $mofDir | Out-Null
    Set-DscLocalConfigurationManager -Path $mofDir -ErrorAction Stop
    $after = Get-DscLocalConfigurationManager
    if ($after.RefreshMode -ne 'Pull' -or [string]$after.ConfigurationID -ne $guid.ToString()) { throw 'The LCM did not take the pull settings.' }
    [pscustomobject]@{ Id = $id; Result = 'Registered'; Share = $SharePath; ConfigurationID = $guid.ToString(); Rollback = (Join-Path $workDir "lcm-before-$stamp.json") }
    return 0
}

function Invoke-Publish {
    if (-not $SharePath) { throw 'Publish needs -SharePath.' }
    if (-not (Test-Path -LiteralPath $SharePath)) { throw "The share $SharePath is not reachable." }
    $failed = $false
    foreach ($pair in $NodeConfigurationIds) {
        if ($pair -notmatch '^([^=]+)=([0-9a-fA-F\-]{36})$') { throw "'$pair' must be NodeName=ConfigurationID." }
        $node = $Matches[1]; $cid = $Matches[2].ToLowerInvariant()
        $src = Join-Path $MofFolder "$node.mof"
        if (-not (Test-Path -LiteralPath $src)) {
            [pscustomobject]@{ Item = $node; Result = 'FAIL: MOF not found (compile with DSC-11)' }; $failed = $true; continue
        }
        $dst = Join-Path $SharePath "$cid.mof"
        if ((Test-Path -LiteralPath $dst) -and (Get-FileSha $src) -eq (Get-FileSha $dst) -and (Test-Path -LiteralPath "$dst.checksum")) {
            [pscustomobject]@{ Item = $node; Result = 'No change' }; continue
        }
        if ($PSCmdlet.ShouldProcess($dst, "Publish MOF for $node")) {
            Copy-Item -LiteralPath $src -Destination $dst -Force
            New-DscChecksum -Path $dst -OutPath $SharePath -Force
            [pscustomobject]@{ Item = $node; Result = "Published as $cid.mof" }
        }
    }
    foreach ($name in $ModuleNames) {
        $mod = Get-Module -ListAvailable -Name $name | Sort-Object Version -Descending | Select-Object -First 1
        if (-not $mod) { [pscustomobject]@{ Item = $name; Result = 'FAIL: module not installed' }; $failed = $true; continue }
        $zip = Join-Path $SharePath ("{0}_{1}.zip" -f $mod.Name, $mod.Version)
        if ((Test-Path -LiteralPath $zip) -and (Test-Path -LiteralPath "$zip.checksum")) {
            [pscustomobject]@{ Item = "$name $($mod.Version)"; Result = 'No change' }; continue
        }
        if ($PSCmdlet.ShouldProcess($zip, 'Publish module package')) {
            Compress-Archive -Path (Join-Path $mod.ModuleBase '*') -DestinationPath $zip -Force
            New-DscChecksum -Path $zip -OutPath $SharePath -Force
            [pscustomobject]@{ Item = "$name $($mod.Version)"; Result = 'Published' }
        }
    }
    if ($failed) { return 1 }
    return 0
}

try {
    if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Run this script in Windows PowerShell 5.1 (DSC 1.1).' }
    $isSystem = [Security.Principal.WindowsIdentity]::GetCurrent().IsSystem
    if ($Mode -eq 'Publish' -and $isSystem) { Write-Warning 'Publish normally runs as the publishing user; SYSTEM writes with the computer account.' }
    switch ($Mode) {
        'Report' { $results = @(Invoke-Report) }
        'RegisterNode' { $results = @(Invoke-Register) }
        'Publish' { $results = @(Invoke-Publish) }
    }
    $code = $results[-1]
    $results | Select-Object -SkipLast 1
    exit ([int]$code)
} catch {
    Write-Error "$id failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
