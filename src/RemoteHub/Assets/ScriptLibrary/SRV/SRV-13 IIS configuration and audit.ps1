<#
.SYNOPSIS
    Audits IIS sites, bindings, application pools, certificates and hardening, and optionally configures a site.
.DESCRIPTION
    Audit covers sites and bindings, application pool identity, recycling, idle timeout and pipeline mode,
    bound certificates and their expiry, authentication modes, request filtering limits, directory browsing,
    server header exposure, logging and the SCHANNEL TLS protocol settings. Configure mode installs Web-Server
    if missing, backs up the configuration, then creates the pool, site and binding and binds a certificate.
    Pool identities use ApplicationPoolIdentity; never store a pool password. Binding and certificate changes
    can interrupt a live site. WebAdministration needs the IIS scripting tools feature.
.NOTES
    RemoteHub: id=SRV-13; context=System; os=Server; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Audit', 'Configure')]
    [string]$Mode = 'Audit',
    [int]$CertificateWarningDays = 30,
    [string]$SiteName = '',
    [string]$PhysicalPath = '',
    [string]$AppPoolName = '',
    [ValidateSet('http', 'https')]
    [string]$Protocol = 'https',
    [int]$Port = 443,
    [string]$HostHeader = '',
    [string]$CertificateThumbprint = ''
)

$ErrorActionPreference = 'Stop'

$findings = @()
function Add-Finding([string]$Area, [string]$Item, [string]$Value, [string]$Finding) {
    $script:findings += [pscustomobject]@{ Area = $Area; Item = $Item; Value = $Value; Finding = $Finding }
}

function Get-Cfg([string]$Location, [string]$Filter, [string]$Name) {
    # Depending on the attribute, the cmdlet returns a ConfigurationAttribute or the plain value.
    $p = @{ PSPath = 'IIS:\'; Filter = $Filter; Name = $Name }
    if ($Location) { $p.Location = $Location }
    $r = Get-WebConfigurationProperty @p
    if ($null -ne $r -and $null -ne $r.PSObject.Properties['Value']) { return $r.Value }
    return $r
}

try {
    $changes = @()
    if ($Mode -eq 'Configure') {
        if (-not $SiteName -or -not $PhysicalPath) { throw 'Configure mode needs -SiteName and -PhysicalPath.' }
        foreach ($feat in @('Web-Server', 'Web-Scripting-Tools')) {
            if (-not (Get-WindowsFeature -Name $feat).Installed) {
                if ($PSCmdlet.ShouldProcess($feat, 'Install-WindowsFeature')) {
                    $r = Install-WindowsFeature -Name $feat
                    if (-not $r.Success) { throw "Installing $feat failed." }
                    $changes += "Installed $feat"
                } else { $changes += "Planned: install $feat" }
            }
        }
    }

    if (-not (Get-Module -ListAvailable -Name WebAdministration)) { Write-Output 'Data unavailable: WebAdministration module missing (install Web-Scripting-Tools).'; exit 1 }
    Import-Module WebAdministration -Verbose:$false

    if ($Mode -eq 'Configure') {
        if (-not $AppPoolName) { $AppPoolName = $SiteName }
        $site = Get-Website | Where-Object { $_.Name -eq $SiteName }
        $pool = Test-Path -LiteralPath "IIS:\AppPools\$AppPoolName"
        $bindingInfo = "*:${Port}:$HostHeader"
        $hasBinding = $false
        if ($site) { $hasBinding = @(Get-WebBinding -Name $SiteName -Protocol $Protocol | Where-Object { $_.bindingInformation -eq $bindingInfo }).Count -gt 0 }
        $needsCert = ($Protocol -eq 'https' -and $CertificateThumbprint)
        $certBound = $false
        if ($needsCert -and $hasBinding) {
            $b = Get-WebBinding -Name $SiteName -Protocol https | Where-Object { $_.bindingInformation -eq $bindingInfo }
            $certBound = ([string]$b.certificateHash -eq $CertificateThumbprint.ToUpperInvariant())
        }
        if ($pool -and $site -and $hasBinding -and (-not $needsCert -or $certBound)) {
            $changes += 'No change: pool, site, binding and certificate already as requested.'
        } elseif ($PSCmdlet.ShouldProcess($SiteName, "Back up IIS configuration and create pool/site/binding $Protocol $bindingInfo")) {
            $backup = 'RemoteHub-SRV13-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
            Backup-WebConfiguration -Name $backup | Out-Null
            $changes += "Configuration backup: $backup (Restore-WebConfiguration -Name $backup)"
            if (-not $pool) { New-WebAppPool -Name $AppPoolName | Out-Null; $changes += "Created pool $AppPoolName (ApplicationPoolIdentity)" }
            if (-not $site) {
                New-Website -Name $SiteName -PhysicalPath $PhysicalPath -ApplicationPool $AppPoolName -Port $Port -HostHeader $HostHeader -Ssl:($Protocol -eq 'https') | Out-Null
                $changes += "Created site $SiteName"
            } elseif (-not $hasBinding) {
                New-WebBinding -Name $SiteName -Protocol $Protocol -Port $Port -HostHeader $HostHeader | Out-Null
                $changes += "Added binding $Protocol $bindingInfo"
            }
            if ($needsCert -and -not $certBound) {
                $cert = Get-Item -LiteralPath "Cert:\LocalMachine\My\$CertificateThumbprint"
                (Get-WebBinding -Name $SiteName -Protocol https | Where-Object { $_.bindingInformation -eq $bindingInfo }).AddSslCertificate($cert.Thumbprint, 'My')
                $changes += "Bound certificate expiring $($cert.NotAfter.ToString('yyyy-MM-dd'))"
            }
        } else { $changes += "Planned: back up configuration and create or update $SiteName" }
    }

    foreach ($s in @(Get-Website)) {
        Add-Finding 'Site' $s.Name ([string]$s.State) ''
        foreach ($b in @(Get-WebBinding -Name $s.Name)) {
            $f = ''
            if ($b.protocol -eq 'http') { $f = 'Plain HTTP binding' }
            Add-Finding 'Binding' $s.Name "$($b.protocol) $($b.bindingInformation)" $f
            if ($b.protocol -eq 'https' -and $b.certificateHash) {
                $c = Get-ChildItem -LiteralPath "Cert:\LocalMachine\$($b.certificateStoreName)" | Where-Object { $_.Thumbprint -eq $b.certificateHash }
                if ($c) {
                    $days = [int]($c.NotAfter - (Get-Date)).TotalDays
                    $cf = ''
                    if ($days -lt $CertificateWarningDays) { $cf = "Expires in $days days" }
                    Add-Finding 'Certificate' $s.Name $c.NotAfter.ToString('yyyy-MM-dd') $cf
                } else { Add-Finding 'Certificate' $s.Name 'Data unavailable' 'Bound certificate not found in store' }
            }
        }
        $loc = $s.Name
        $anon = Get-Cfg $loc 'system.webServer/security/authentication/anonymousAuthentication' 'enabled'
        $basic = Get-Cfg $loc 'system.webServer/security/authentication/basicAuthentication' 'enabled'
        $win = Get-Cfg $loc 'system.webServer/security/authentication/windowsAuthentication' 'enabled'
        $fa = ''
        if ($basic) { $fa = 'Basic authentication enabled' }
        Add-Finding 'Authentication' $s.Name "Anonymous=$anon Basic=$basic Windows=$win" $fa
        $dirb = Get-Cfg $loc 'system.webServer/directoryBrowse' 'enabled'
        if ($dirb) { Add-Finding 'DirectoryBrowsing' $s.Name 'Enabled' 'Directory browsing is on' }
        $max = Get-Cfg $loc 'system.webServer/security/requestFiltering/requestLimits' 'maxAllowedContentLength'
        Add-Finding 'RequestLimits' $s.Name "maxAllowedContentLength=$max" ''
        Add-Finding 'Logging' $s.Name "$($s.logFile.directory) fields=$($s.logFile.logExtFileFlags)" ''
    }

    foreach ($p in @(Get-ChildItem -LiteralPath 'IIS:\AppPools')) {
        $f = @()
        if ($p.processModel.identityType -eq 'LocalSystem') { $f += 'Runs as LocalSystem' }
        if ($p.processModel.identityType -eq 'SpecificUser') { $f += 'Specific user identity (prefer a gMSA)' }
        if ($p.managedPipelineMode -eq 'Classic') { $f += 'Classic pipeline' }
        Add-Finding 'AppPool' $p.Name ("identity=$($p.processModel.identityType) idle=$($p.processModel.idleTimeout) recycle=$($p.recycling.periodicRestart.time) pipeline=$($p.managedPipelineMode)") ($f -join '; ')
    }

    $hdr = 'Data unavailable'
    try { $hdr = Get-Cfg '' 'system.webServer/security/requestFiltering' 'removeServerHeader' } catch { Write-Verbose 'removeServerHeader needs IIS 10.' }
    $hf = ''
    if ($hdr -ne $true) { $hf = 'Server header is sent' }
    Add-Finding 'Server' 'removeServerHeader' ([string]$hdr) $hf

    foreach ($proto in @('SSL 3.0', 'TLS 1.0', 'TLS 1.1', 'TLS 1.2')) {
        $k = "HKLM:\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\$proto\Server"
        $val = 'OS default'
        if (Test-Path -LiteralPath $k) { $e = (Get-ItemProperty -LiteralPath $k).Enabled; if ($null -ne $e) { $val = "Enabled=$e" } }
        $tf = ''
        if ($proto -ne 'TLS 1.2' -and $val -ne 'Enabled=0') { $tf = 'Legacy protocol not explicitly disabled' }
        Add-Finding 'TLS' $proto $val $tf
    }

    $findings | Format-Table Area, Item, Value, Finding -AutoSize -Wrap | Out-String -Width 250
    if ($changes.Count -gt 0) { 'Change log:'; $changes | ForEach-Object { "  $_" } }
    exit 0
} catch {
    Write-Error "IIS audit or configuration failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
