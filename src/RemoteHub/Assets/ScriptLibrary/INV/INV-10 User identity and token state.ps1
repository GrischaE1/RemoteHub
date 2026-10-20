<#
.SYNOPSIS
    Reports the signed-in user's primary refresh token, SSO and Windows Hello for Business state.
.DESCRIPTION
    Reads the user state and SSO state of dsregcmd /status: whether a primary refresh token is
    present, whether SSO works, whether Windows Hello for Business is provisioned and when the
    token was last updated. Only the fixed field names (AzureAdPrt, NgcSet ...) are matched, not the
    localised section headings. Under SYSTEM these values are absent, not false, so the script
    returns "Data unavailable". No token material is read or printed.
.NOTES
    RemoteHub: id=INV-10; context=User; os=Client; risk=R0
#>
[CmdletBinding()]
param(
    [switch]$SensorString
)

$ErrorActionPreference = 'Stop'

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($identity.IsSystem) {
        'Data unavailable: no user context (running as SYSTEM).'
        exit 1
    }

    $exe = Join-Path $env:SystemRoot 'System32\dsregcmd.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        'Data unavailable: dsregcmd.exe not found.'
        exit 1
    }

    $lines = & $exe /status 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $lines) {
        'Data unavailable: dsregcmd /status returned no data.'
        exit 1
    }

    # Keep only the documented fields; dsregcmd prints no token values but nothing else is needed.
    $wanted = @('AzureAdJoined', 'DomainJoined', 'WorkplaceJoined', 'NgcSet', 'NgcKeyId', 'WamDefaultSet',
                'AzureAdPrt', 'AzureAdPrtUpdateTime', 'AzureAdPrtExpiryTime', 'EnterprisePrt', 'EnterprisePrtUpdateTime')
    $fields = @{}
    foreach ($line in $lines) {
        $m = [regex]::Match([string]$line, '^\s*([A-Za-z]+)\s*:\s*(.*?)\s*$')
        if ($m.Success -and $wanted -contains $m.Groups[1].Value -and -not $fields.ContainsKey($m.Groups[1].Value)) {
            $fields[$m.Groups[1].Value] = $m.Groups[2].Value
        }
    }

    function Get-Field([string]$Name) {
        if ($fields.ContainsKey($Name) -and $fields[$Name]) { return $fields[$Name] }
        return 'Data unavailable'
    }

    $prt = Get-Field 'AzureAdPrt'
    $entPrt = Get-Field 'EnterprisePrt'
    $ngc = Get-Field 'NgcSet'

    $sso = 'Data unavailable'
    if ($prt -eq 'YES' -or $entPrt -eq 'YES') { $sso = 'Working' }
    elseif ($prt -eq 'NO') { $sso = 'Not working' }

    if ($SensorString) {
        "PRT=$prt; SSO=$sso; WHfB=$ngc"
        exit 0
    }

    [PSCustomObject]@{
        PrimaryRefreshToken     = $prt
        PrtLastUpdated          = Get-Field 'AzureAdPrtUpdateTime'
        PrtExpires              = Get-Field 'AzureAdPrtExpiryTime'
        EnterprisePrt           = $entPrt
        SingleSignOn            = $sso
        WindowsHelloProvisioned = $ngc
        WamDefaultSet           = Get-Field 'WamDefaultSet'
        DeviceEntraJoined       = Get-Field 'AzureAdJoined'
        UserWorkplaceJoined     = Get-Field 'WorkplaceJoined'
    }
    exit 0
} catch {
    Write-Error "Token state check failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
