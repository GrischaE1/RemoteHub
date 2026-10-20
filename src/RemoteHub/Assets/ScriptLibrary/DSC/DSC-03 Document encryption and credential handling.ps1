<#
.SYNOPSIS
    Sets up and checks the DSC document encryption certificate on a node, or checks MOFs and public keys on an authoring machine.
.DESCRIPTION
    As SYSTEM (node): finds or creates a Document Encryption certificate, checks expiry, private key access
    (SYSTEM and Administrators only) and whether the LCM points at it, and exports only the public key.
    As a user (authoring): lists the exported public keys per node and checks that MOF files contain no
    plaintext password. Never allow plaintext passwords in MOFs; any MOF that held one means rotating that
    secret. Prefer gMSAs and certificate identities so no password is needed. Set the LCM certificate with DSC-02.
.NOTES
    RemoteHub: id=DSC-03; context=Both; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Auto', 'Node', 'Authoring')]
    [string]$Mode = 'Auto',
    [string]$CertificateSubject = 'CN=DscEncryptionCert',
    [int]$MinValidDays = 60,
    [string]$ExportFolder = (Join-Path $env:ProgramData 'RemoteHub\DSC\DSC-03'),
    [string]$PublicKeyFolder = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'RemoteHub\DSC\PublicKeys'),
    [string]$MofFolder = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'RemoteHub\DSC\Output')
)

$ErrorActionPreference = 'Stop'
$docEncOid = '1.3.6.1.4.1.311.80.1'

function Get-DocEncCert {
    Get-ChildItem -Path 'Cert:\LocalMachine\My' |
        Where-Object { $_.HasPrivateKey -and @($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq $docEncOid }).Count -gt 0 } |
        Sort-Object NotAfter -Descending
}

function Invoke-NodeMode {
    $certs = @(Get-DocEncCert)
    $cert = $certs | Where-Object { $_.NotAfter -gt (Get-Date).AddDays($MinValidDays) } | Select-Object -First 1
    $created = $false
    if (-not $cert) {
        $why = 'none present'
        if ($certs.Count -gt 0) { $why = "the newest expires $($certs[0].NotAfter.ToString('yyyy-MM-dd'))" }
        if (-not $PSCmdlet.ShouldProcess("LocalMachine\My on $env:COMPUTERNAME", "Create Document Encryption certificate ($why)")) {
            [pscustomobject]@{ Id = 'DSC-03'; Mode = 'Node'; Result = 'WhatIf: certificate would be created'; Reason = $why }
            $script:ExitCode = 0
            return
        }
        # The legacy CSP type is what DSC 1.1 on Windows PowerShell 5.1 can decrypt with.
        $cert = New-SelfSignedCertificate -Type DocumentEncryptionCertLegacyCsp -Subject $CertificateSubject `
            -HashAlgorithm SHA256 -KeyLength 2048 -CertStoreLocation 'Cert:\LocalMachine\My' -NotAfter (Get-Date).AddYears(2)
        $created = $true
    }

    $aclFinding = 'Data unavailable'
    try {
        $container = $cert.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName
        $keyFile = Join-Path $env:ProgramData ("Microsoft\Crypto\RSA\MachineKeys\" + $container)
        $extra = @((Get-Acl -LiteralPath $keyFile).Access | Where-Object {
                $_.AccessControlType -eq 'Allow' -and
                $_.IdentityReference.Value -notmatch '(SYSTEM|Administrators)$'
            } | ForEach-Object { $_.IdentityReference.Value })
        if ($extra.Count -eq 0) { $aclFinding = 'SYSTEM and Administrators only' }
        else { $aclFinding = 'Also readable by: ' + (($extra | Select-Object -Unique) -join ', ') }
    } catch { Write-Verbose "Private key ACL check failed: $($_.Exception.Message)" }

    $lcmCert = 'Data unavailable'
    try {
        $r = Invoke-CimMethod -Namespace 'root/Microsoft/Windows/DesiredStateConfiguration' `
            -ClassName 'MSFT_DSCLocalConfigurationManager' -MethodName 'GetMetaConfiguration'
        $lcmCert = [string]$r.MetaConfiguration.CertificateID
    } catch { Write-Verbose "LCM query failed: $($_.Exception.Message)" }

    $cerPath = Join-Path $ExportFolder ("$env:COMPUTERNAME.cer")
    $exported = $false
    $needExport = $true
    if (Test-Path -LiteralPath $cerPath) {
        $existing = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $cerPath
        if ($existing.Thumbprint -eq $cert.Thumbprint) { $needExport = $false }
    }
    if ($needExport -and $PSCmdlet.ShouldProcess($cerPath, 'Export public key only')) {
        if (-not (Test-Path $ExportFolder)) { New-Item -ItemType Directory -Path $ExportFolder -Force | Out-Null }
        Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT -Force | Out-Null
        $exported = $true
    }

    $lcmMatches = ($lcmCert -eq $cert.Thumbprint)
    [pscustomobject]@{
        Id              = 'DSC-03'
        Mode            = 'Node'
        Computer        = $env:COMPUTERNAME
        Thumbprint      = $cert.Thumbprint
        NotAfter        = $cert.NotAfter.ToString('yyyy-MM-dd')
        Created         = $created
        PrivateKeyAcl   = $aclFinding
        LcmCertificate  = $lcmCert
        LcmUsesThisCert = $lcmMatches
        PublicKeyFile   = $cerPath
        Exported        = $exported
        Next            = $(if ($lcmMatches) { 'None' } else { 'Run DSC-02 with -CertificateId ' + $cert.Thumbprint })
    }
    if ($lcmMatches -and $aclFinding -eq 'SYSTEM and Administrators only') { $script:ExitCode = 0 }
}

function Invoke-AuthoringMode {
    $failed = $false
    if (Test-Path -LiteralPath $PublicKeyFolder) {
        foreach ($cer in Get-ChildItem -LiteralPath $PublicKeyFolder -Filter '*.cer') {
            $c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $cer.FullName
            $days = [int]($c.NotAfter - (Get-Date)).TotalDays
            if ($days -lt $MinValidDays) { $failed = $true }
            [pscustomobject]@{ Id = 'DSC-03'; Check = 'PublicKey'; Node = $cer.BaseName; Thumbprint = $c.Thumbprint; NotAfter = $c.NotAfter.ToString('yyyy-MM-dd'); DaysLeft = $days }
        }
    } else {
        [pscustomobject]@{ Id = 'DSC-03'; Check = 'PublicKey'; Node = 'Data unavailable'; Thumbprint = "Folder not found: $PublicKeyFolder" }
    }

    if (-not (Test-Path -LiteralPath $MofFolder)) {
        [pscustomobject]@{ Id = 'DSC-03'; Check = 'Mof'; Result = "Data unavailable: folder not found: $MofFolder" }
        return
    }
    foreach ($mof in Get-ChildItem -LiteralPath $MofFolder -Filter '*.mof' -Recurse) {
        $text = Get-Content -LiteralPath $mof.FullName -Raw
        $blocks = [regex]::Matches($text, 'instance of MSFT_Credential[^{]*\{(?<body>[^}]*)\}')
        $plain = 0
        foreach ($b in $blocks) {
            # Only counts are reported; the value itself is never read into output.
            if ($b.Groups['body'].Value -match 'Password\s*=\s*"(?!-----BEGIN CMS-----)') { $plain++ }
        }
        if ($plain -gt 0) { $failed = $true }
        [pscustomobject]@{
            Id = 'DSC-03'; Check = 'Mof'; File = $mof.Name; Credentials = $blocks.Count; Plaintext = $plain
            Result = $(if ($plain -gt 0) { 'FAIL: plaintext password - rotate that secret and recompile with encryption' } else { 'OK' })
        }
    }
    if (-not $failed) { $script:ExitCode = 0 }
}

try {
    $isSystem = [Security.Principal.WindowsIdentity]::GetCurrent().IsSystem
    $effective = $Mode
    if ($Mode -eq 'Auto') { $effective = $(if ($isSystem) { 'Node' } else { 'Authoring' }) }
    $script:ExitCode = 1
    if ($effective -eq 'Node') { Invoke-NodeMode } else { Invoke-AuthoringMode }
    exit $script:ExitCode
} catch {
    Write-Error "DSC-03 failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
