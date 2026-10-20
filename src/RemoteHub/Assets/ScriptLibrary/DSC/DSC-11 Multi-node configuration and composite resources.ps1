<#
.SYNOPSIS
    Compiles one role-based configuration for many nodes from configuration data into one MOF per node, then tests the output.
.DESCRIPTION
    Reads configuration data (a .psd1 with AllNodes, NodeName and Role), validates it, and compiles a
    configuration built from role configurations (Base, WebServer, FileServer) used as composite resources.
    Each MOF is then checked: present, not empty, and free of plaintext credentials. Applies nothing.
    Configuration data must not hold secrets; keys that look like secrets fail the run. MOF files describe the
    estate, so keep the output folder protected and under version control with a test compile on every change.
.NOTES
    RemoteHub: id=DSC-11; context=AsMe; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [string]$ConfigurationDataPath = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'RemoteHub\DSC\ConfigData.psd1'),
    [string]$OutputPath = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'RemoteHub\DSC\Output')
)

$ErrorActionPreference = 'Stop'
$knownRoles = @('WebServer', 'FileServer')

function Find-SecretKey {
    param($Table, [string]$Prefix)
    foreach ($k in @($Table.Keys)) {
        $name = "$Prefix$k"
        if ($k -match 'password|secret|token|apikey|credential|registrationkey|PSDscAllowPlainTextPassword') { $name }
        $v = $Table[$k]
        if ($v -is [hashtable]) { Find-SecretKey -Table $v -Prefix "$name." }
    }
}

try {
    if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Run this script in Windows PowerShell 5.1 (DSC 1.1 compile).' }

    $source = $ConfigurationDataPath
    if (Test-Path -LiteralPath $ConfigurationDataPath) {
        $data = Import-PowerShellDataFile -LiteralPath $ConfigurationDataPath
    } else {
        # A built-in sample keeps a first run useful and shows the expected data shape.
        $source = 'Built-in sample (no file at ' + $ConfigurationDataPath + ')'
        $data = @{ AllNodes = @(
                @{ NodeName = '*'; ShareRoot = 'D:\Shares' },
                @{ NodeName = 'WEB01'; Role = @('WebServer') },
                @{ NodeName = 'FS01'; Role = @('FileServer') }
            )
        }
    }

    $problems = @()
    if (-not $data.ContainsKey('AllNodes')) { throw 'Configuration data has no AllNodes.' }
    $nodes = @($data.AllNodes | Where-Object { $_.NodeName -ne '*' })
    $names = @($nodes | ForEach-Object { [string]$_.NodeName })
    if ($names.Count -eq 0) { $problems += 'No nodes defined' }
    $dupes = @($names | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
    if ($dupes.Count -gt 0) { $problems += 'Duplicate NodeName: ' + ($dupes -join ', ') }
    foreach ($n in $nodes) {
        if (-not $n.NodeName) { $problems += 'A node has no NodeName' }
        foreach ($r in @($n.Role)) { if ($r -and $knownRoles -notcontains $r) { $problems += "Node $($n.NodeName): unknown role '$r'" } }
    }
    foreach ($n in $data.AllNodes) { foreach ($s in @(Find-SecretKey -Table $n -Prefix "$($n.NodeName).")) { $problems += "Secret-like key in configuration data: $s" } }
    if ($problems.Count -gt 0) {
        foreach ($p in $problems) { [pscustomobject]@{ Check = 'ConfigurationData'; Result = 'FAIL'; Detail = $p } }
        exit 1
    }

    $configText = @'
Configuration RhRoleBase {
    param([string]$Roles = '')
    Import-DscResource -ModuleName PSDesiredStateConfiguration
    Service TimeService { Name = 'W32Time'; State = 'Running' }
    Registry RoleMarker { Key = 'HKEY_LOCAL_MACHINE\SOFTWARE\RemoteHub\DSC'; ValueName = 'Roles'; ValueType = 'String'; ValueData = $Roles; Ensure = 'Present' }
}
Configuration RhRoleWeb {
    Import-DscResource -ModuleName PSDesiredStateConfiguration
    WindowsFeature WebServer { Name = 'Web-Server'; Ensure = 'Present' }
}
Configuration RhRoleFile {
    param([string]$ShareRoot = 'D:\Shares')
    Import-DscResource -ModuleName PSDesiredStateConfiguration
    WindowsFeature FileServer { Name = 'FS-FileServer'; Ensure = 'Present' }
    File ShareRoot { DestinationPath = $ShareRoot; Type = 'Directory'; Ensure = 'Present'; DependsOn = '[WindowsFeature]FileServer' }
}
Configuration RhEstate {
    Import-DscResource -ModuleName PSDesiredStateConfiguration
    Node $AllNodes.NodeName {
        RhRoleBase Base { Roles = (@($Node.Role) -join ',') }
        if (@($Node.Role) -contains 'WebServer') { RhRoleWeb Web { } }
        if (@($Node.Role) -contains 'FileServer') {
            $root = 'D:\Shares'
            if ($Node.ShareRoot) { $root = $Node.ShareRoot }
            RhRoleFile File { ShareRoot = $root }
        }
    }
}
'@
    if (-not (Test-Path -LiteralPath $OutputPath)) { New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null }
    . ([scriptblock]::Create($configText))
    $compiled = @(RhEstate -ConfigurationData $data -OutputPath $OutputPath)
    Write-Verbose ("Compiled {0} MOF file(s) to {1}" -f $compiled.Count, $OutputPath)

    $failed = $false
    foreach ($n in $nodes) {
        $mof = Join-Path $OutputPath ("$($n.NodeName).mof")
        $result = 'OK'
        $resources = 0
        if (-not (Test-Path -LiteralPath $mof)) {
            $result = 'FAIL: MOF missing'
        } else {
            $text = Get-Content -LiteralPath $mof -Raw
            $resources = ([regex]::Matches($text, 'ResourceID\s*=')).Count
            $plain = [regex]::Matches($text, 'instance of MSFT_Credential[^{]*\{[^}]*Password\s*=\s*"(?!-----BEGIN CMS-----)').Count
            if ($resources -eq 0) { $result = 'FAIL: no resources' }
            elseif ($plain -gt 0) { $result = 'FAIL: plaintext credential in MOF' }
        }
        if ($result -ne 'OK') { $failed = $true }
        [pscustomobject]@{ Check = 'Mof'; Node = $n.NodeName; Roles = (@($n.Role) -join ','); Resources = $resources; Result = $result }
    }
    $stale = @(Get-ChildItem -LiteralPath $OutputPath -Filter '*.mof' | Where-Object { $names -notcontains $_.BaseName -and $_.Name -notlike '*.meta.mof' })
    foreach ($s in $stale) { [pscustomobject]@{ Check = 'Mof'; Node = $s.BaseName; Roles = ''; Resources = 0; Result = 'Stale: node no longer in configuration data' } }

    [pscustomobject]@{ Check = 'Summary'; Source = $source; Nodes = $nodes.Count; Output = $OutputPath; Passed = (-not $failed) }
    if ($failed) { exit 1 }
    exit 0
} catch {
    Write-Error "DSC-11 failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
