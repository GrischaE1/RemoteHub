<#
.SYNOPSIS
    Checks the health of the local AD CS certification authority and optionally backs up its database.
.DESCRIPTION
    Checks the CA and OCSP responder services, CA ping, CRL validity and next update, reachability of the HTTP
    CDP and AIA URLs, published templates, certificates nearing expiry and failed or pending requests. Flags
    templates that let the requester supply the subject with an authentication EKU, no manager approval and
    enrollment for low-privilege groups. Backup mode saves the CA database and configuration registry only:
    a private key backup needs a password at a secure prompt, so it is left to a manual step. HSM keys cannot
    be exported. For an offline root CA, raise -CrlWarningHours so CRL expiry is caught well ahead.
.NOTES
    RemoteHub: id=SRV-20; context=System; os=Server; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Health', 'Backup')]
    [string]$Mode = 'Health',
    [int]$ExpiryDays = 30,
    [int]$CrlWarningHours = 72,
    [int]$RequestLookbackDays = 7,
    [string]$BackupFolder = "$env:ProgramData\RemoteHub\CABackup"
)

$ErrorActionPreference = 'Stop'

$findings = @()
function Add-Finding([string]$Area, [string]$Item, [string]$Detail, [string]$Severity) {
    $script:findings += [pscustomobject]@{ Area = $Area; Item = $Item; Detail = $Detail; Severity = $Severity }
}

function Get-ViewCount([string]$Config, [string]$Column, [scriptblock]$Restrict) {
    # ICertView avoids parsing localized certutil -view output.
    $view = New-Object -ComObject CertificateAuthority.View
    $view.OpenConnection($Config)
    & $Restrict $view
    $view.SetResultColumnCount(1)
    $view.SetResultColumn($view.GetColumnIndex(0, $Column))
    $rows = $view.OpenView()
    $n = 0
    while ($rows.Next() -ne -1) { $n++ }
    return $n
}

$exitCode = 0
try {
    $cfgRoot = 'HKLM:\SYSTEM\CurrentControlSet\Services\CertSvc\Configuration'
    if (-not (Test-Path -LiteralPath $cfgRoot)) { Write-Output 'Data unavailable: no certification authority is installed here.'; exit 1 }
    $caName = (Get-ItemProperty -LiteralPath $cfgRoot).Active
    $caKey = Get-ItemProperty -LiteralPath (Join-Path $cfgRoot $caName)
    $fqdn = ([System.Net.Dns]::GetHostEntry($env:COMPUTERNAME)).HostName
    $config = "$fqdn\$caName"

    foreach ($svcName in @('CertSvc', 'OcspSvc')) {
        $svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
        if ($svc) {
            $sev = 'Info'
            if ([string]$svc.Status -ne 'Running') { $sev = 'High' }
            Add-Finding 'Service' $svcName ([string]$svc.Status) $sev
        }
    }
    $null = & certutil.exe -config $config -ping 2>&1
    $pingSev = 'Info'
    if ($LASTEXITCODE -ne 0) { $pingSev = 'High' }
    Add-Finding 'Service' 'CA ping' "exit code $LASTEXITCODE" $pingSev

    $enroll = Join-Path $env:SystemRoot 'System32\CertSrv\CertEnroll'
    foreach ($crlFile in @(Get-ChildItem -LiteralPath $enroll -Filter '*.crl' -ErrorAction SilentlyContinue)) {
        try {
            $crl = New-Object -ComObject X509Enrollment.CX509CertificateRevocationList
            $crl.InitializeDecode([Convert]::ToBase64String([IO.File]::ReadAllBytes($crlFile.FullName)), 1)
            $left = ($crl.NextUpdate.ToLocalTime() - (Get-Date)).TotalHours
            $sev = 'Info'
            if ($left -lt 0) { $sev = 'High' } elseif ($left -lt $CrlWarningHours) { $sev = 'Medium' }
            Add-Finding 'CRL' $crlFile.Name ("next update {0:yyyy-MM-dd HH:mm} ({1:N0} h left)" -f $crl.NextUpdate.ToLocalTime(), $left) $sev
        } catch { Add-Finding 'CRL' $crlFile.Name 'Data unavailable (COM decode failed, e.g. Constrained Language Mode)' 'Info' }
    }

    # Publication URLs are "flags:url" with %n tokens; only HTTP URLs flagged for the extension (bit 2) are tested.
    $short = $caName -replace '[^A-Za-z0-9 _.-]', ''
    $urls = @($caKey.CRLPublicationURLs) + @($caKey.CACertPublicationURLs) | Where-Object { $_ -match '^(\d+):(http://.*)$' -and ([int]$Matches[1] -band 2) } | ForEach-Object {
        ($_ -replace '^\d+:', '') -replace '%1', $fqdn -replace '%2', $env:COMPUTERNAME -replace '%3', $short -replace '%4', '' -replace '%8', '' -replace '%9', ''
    }
    foreach ($u in @($urls | Sort-Object -Unique)) {
        try {
            $r = Invoke-WebRequest -Uri $u -UseBasicParsing -Method Head -TimeoutSec 15
            Add-Finding 'URL' $u "HTTP $($r.StatusCode)" 'Info'
        } catch { Add-Finding 'URL' $u "Unreachable: $($_.Exception.Message)" 'High' }
    }

    $published = @()
    if (Get-Module -ListAvailable -Name ADCSAdministration) {
        try { $published = @(Get-CATemplate | ForEach-Object { $_.Name }) } catch { Write-Verbose 'Get-CATemplate failed.' }
    }
    Add-Finding 'Templates' 'Published' ("{0} templates" -f $published.Count) 'Info'
    try {
        $root = [ADSI]'LDAP://RootDSE'
        $tplBase = [ADSI]("LDAP://CN=Certificate Templates,CN=Public Key Services,CN=Services," + $root.configurationNamingContext)
        $authEku = @('1.3.6.1.5.5.7.3.2', '1.3.6.1.4.1.311.20.2.2', '1.3.6.1.5.2.3.4', '2.5.29.37.0')
        $lowPriv = '^(S-1-1-0|S-1-5-11|S-1-5-21-.+-(513|515))$'
        foreach ($t in $tplBase.Children) {
            $name = [string]$t.Properties['cn'].Value
            if ($published -notcontains $name) { continue }
            $suppliesSubject = ([int]$t.Properties['msPKI-Certificate-Name-Flag'].Value -band 1) -ne 0
            $approval = ([int]$t.Properties['msPKI-Enrollment-Flag'].Value -band 2) -ne 0
            $ekus = @($t.Properties['pKIExtendedKeyUsage'] | ForEach-Object { [string]$_ })
            $auth = ($ekus.Count -eq 0) -or (@($ekus | Where-Object { $authEku -contains $_ }).Count -gt 0)
            if (-not ($suppliesSubject -and $auth -and -not $approval)) { continue }
            $sd = $t.psbase.ObjectSecurity
            $open = @($sd.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object {
                $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -match $lowPriv -and
                (([string]$_.ObjectType -eq '0e10c968-78fb-11d2-90d4-00c04f79dc55') -or ($_.ActiveDirectoryRights -band [DirectoryServices.ActiveDirectoryRights]::GenericAll)) })
            if ($open.Count -gt 0) { Add-Finding 'Templates' $name 'Requester supplies subject, authentication EKU, low-privilege enrollment, no approval' 'High' }
        }
    } catch { Add-Finding 'Templates' 'Permissions' "Data unavailable: $($_.Exception.Message)" 'Info' }

    try {
        $soon = Get-ViewCount $config 'Request.RequestID' { param($v)
            $v.SetRestriction($v.GetColumnIndex(0, 'Request.Disposition'), 1, 0, 20)
            $v.SetRestriction($v.GetColumnIndex(0, 'NotAfter'), 8, 0, (Get-Date))
            $v.SetRestriction($v.GetColumnIndex(0, 'NotAfter'), 4, 0, (Get-Date).AddDays($ExpiryDays)) }
        Add-Finding 'Certificates' "Expiring within $ExpiryDays days" "$soon" 'Info'
        $pending = Get-ViewCount $config 'Request.RequestID' { param($v) $v.SetRestriction($v.GetColumnIndex(0, 'Request.Disposition'), 1, 0, 9) }
        Add-Finding 'Requests' 'Pending' "$pending" 'Info'
        $failedReq = Get-ViewCount $config 'Request.RequestID' { param($v)
            # Disposition 30 is failed, 31 denied.
            $v.SetRestriction($v.GetColumnIndex(0, 'Request.Disposition'), 8, 0, 30)
            $v.SetRestriction($v.GetColumnIndex(0, 'Request.SubmittedWhen'), 8, 0, (Get-Date).AddDays(-$RequestLookbackDays)) }
        $sev = 'Info'
        if ($failedReq -gt 0) { $sev = 'Medium' }
        Add-Finding 'Requests' "Failed or denied in $RequestLookbackDays days" "$failedReq" $sev
    } catch { Add-Finding 'Database' 'View' "Data unavailable: $($_.Exception.Message)" 'Info' }

    if ($Mode -eq 'Backup') {
        $target = Join-Path $BackupFolder (Get-Date -Format 'yyyyMMdd-HHmmss')
        if ($PSCmdlet.ShouldProcess($target, 'Back up CA database (no private key) and configuration registry')) {
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            Backup-CARoleService -Path $target -DatabaseOnly
            & reg.exe export 'HKLM\SYSTEM\CurrentControlSet\Services\CertSvc\Configuration' (Join-Path $target 'CAConfiguration.reg') /y | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "reg export failed with exit code $LASTEXITCODE" }
            Add-Finding 'Backup' $target 'Database and configuration saved; back up the private key separately' 'Info'
        }
    }

    $findings | Format-Table -AutoSize -Wrap | Out-String -Width 250
    if (@($findings | Where-Object { $_.Severity -eq 'High' }).Count -gt 0) { $exitCode = 1 }
} catch {
    if ($findings.Count) { $findings | Format-Table -AutoSize -Wrap | Out-String -Width 250 }
    Write-Error "CA health check failed: $($_.Exception.Message)" -ErrorAction Continue
    $exitCode = 1
}
exit $exitCode
