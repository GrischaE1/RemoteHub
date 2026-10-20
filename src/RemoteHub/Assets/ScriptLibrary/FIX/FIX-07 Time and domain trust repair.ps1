<#
.SYNOPSIS
    Verifies time sync and the domain secure channel, resyncs time and repairs a broken trust.
.DESCRIPTION
    Checks the time source, clock skew against a domain controller (LDAP rootDSE currentTime), domain controller
    reachability, recent Netlogon errors and the secure channel. Resyncs time when it is off, and repairs a broken
    secure channel by resetting the computer account password. Verification works as SYSTEM; repair uses the
    caller's own token, so run it as a delegated admin limited to this task - no credential is prompted or stored.
    If the computer account is disabled or missing, repair cannot help and a rejoin needs approval. Cloned VMs
    without proper preparation are a common cause. Changes need -Apply.
.NOTES
    RemoteHub: id=FIX-07; context=Both; os=Both; risk=R2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$DomainController = '',
    [int]$MaxSkewSeconds = 120,
    [int]$EventLookbackDays = 7,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if (-not $Apply) { $WhatIfPreference = $true }
$results = New-Object System.Collections.Generic.List[object]
function Add-Check([string]$Check, [bool]$Ok, [string]$Detail) { $results.Add([pscustomobject]@{ Check = $Check; Ok = $Ok; Detail = $Detail }) }

function Test-Tcp([string]$HostName, [int]$Port) {
    $c = New-Object System.Net.Sockets.TcpClient
    try { return $c.ConnectAsync($HostName, $Port).Wait(3000) } catch { return $false } finally { $c.Dispose() }
}

function Get-DcSkew([string]$Dc) {
    # rootDSE currentTime is generalised time in UTC, so no localised tool output is parsed.
    $root = New-Object System.DirectoryServices.DirectoryEntry("LDAP://$Dc/RootDSE")
    $raw = [string]$root.Properties['currentTime'].Value
    $dcTime = [datetime]::ParseExact($raw.Substring(0, 14), 'yyyyMMddHHmmss', [Globalization.CultureInfo]::InvariantCulture)
    return [math]::Round(($dcTime - [datetime]::UtcNow).TotalSeconds)
}

try {
    $cs = Get-CimInstance -ClassName Win32_ComputerSystem
    if (-not $cs.PartOfDomain) { Write-Output 'Not domain joined: nothing to verify.'; exit 0 }
    $isSystem = [Security.Principal.WindowsIdentity]::GetCurrent().IsSystem

    $dc = $DomainController
    if (-not $dc) {
        $nt = Get-CimInstance -ClassName Win32_NTDomain | Where-Object { $_.DnsForestName -and $_.DomainControllerName } | Select-Object -First 1
        if ($nt) { $dc = $nt.DomainControllerName.TrimStart('\') }
    }
    if (-not $dc) { Add-Check 'Domain controller' $false 'Data unavailable: no domain controller found' }
    else {
        $ldap = Test-Tcp $dc 389; $kdc = Test-Tcp $dc 88
        Add-Check 'Domain controller reachable' ($ldap -and $kdc) "$dc LDAP=$ldap Kerberos=$kdc"
    }

    $tp = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\Parameters'
    Add-Check 'Time source type' ($tp.Type -eq 'NT5DS') "$($tp.Type) (NT5DS expected on domain members)"
    $skew = $null
    if ($dc) {
        try { $skew = Get-DcSkew $dc; Add-Check 'Clock skew to DC' ([math]::Abs($skew) -le $MaxSkewSeconds) "$skew seconds" }
        catch { Add-Check 'Clock skew to DC' $false "Data unavailable: $($_.Exception.Message)" }
    }

    $netlogon = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'NETLOGON'; Id = 3210, 5719, 5722, 5723, 5805; StartTime = (Get-Date).AddDays(-$EventLookbackDays) } -ErrorAction SilentlyContinue)
    $ids = ($netlogon | Group-Object Id | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ', '
    if (-not $ids) { $ids = 'none' }
    Add-Check 'Netlogon trust events' ($netlogon.Count -eq 0) "Last $EventLookbackDays days: $ids"

    $secure = $false
    try {
        if ($dc) { $secure = Test-ComputerSecureChannel -Server $dc } else { $secure = Test-ComputerSecureChannel }
        Add-Check 'Secure channel' $secure ''
    }
    catch { Add-Check 'Secure channel' $false $_.Exception.Message }

    # Time first: Kerberos fails with more than five minutes of skew, which looks like a broken trust.
    $timeBad = ($null -ne $skew -and [math]::Abs($skew) -gt $MaxSkewSeconds) -or $tp.Type -ne 'NT5DS'
    if ($timeBad -and $PSCmdlet.ShouldProcess('Windows Time', 'w32tm /resync against the domain hierarchy')) {
        $w32 = Join-Path $env:windir 'System32\w32tm.exe'
        if ($tp.Type -ne 'NT5DS') {
            $p = Start-Process -FilePath $w32 -ArgumentList '/config /syncfromflags:domhier /update' -PassThru -Wait -WindowStyle Hidden
            if ($p.ExitCode -ne 0) { throw "w32tm /config failed with exit $($p.ExitCode)." }
        }
        $p = Start-Process -FilePath $w32 -ArgumentList '/resync /rediscover' -PassThru -Wait -WindowStyle Hidden
        Add-Check 'Time resync' ($p.ExitCode -eq 0) "w32tm exit $($p.ExitCode)"
        if ($p.ExitCode -ne 0) { throw 'Time resync failed; trust repair not attempted.' }
    }

    if (-not $secure) {
        if ($PSCmdlet.ShouldProcess($cs.Name, 'Repair the secure channel (reset the computer account password)')) {
            if ($isSystem) { Write-Verbose 'Running as SYSTEM: repair uses the computer account and fails if the trust is fully broken.' }
            $repairArgs = @{ Repair = $true }
            if ($dc) { $repairArgs['Server'] = $dc }
            $ok = $false
            try { $ok = Test-ComputerSecureChannel @repairArgs } catch { Write-Verbose $_.Exception.Message }
            Add-Check 'Secure channel repair' $ok ''
            if (-not $ok) {
                Add-Check 'Next step' $false 'Repair failed. Check that the computer account exists and is enabled, run as a delegated admin, or rejoin with approval. Cloned VMs need sysprep.'
            }
        }
    }

    $results
    if (-not $Apply) { Write-Output 'Dry run: nothing changed. Run with -Apply to make the changes.' }
    $finalSecure = @($results | Where-Object { $_.Check -like 'Secure channel*' } | Select-Object -Last 1)
    if ($finalSecure.Count -eq 0 -or -not $finalSecure[0].Ok) { exit 1 }
    exit 0
}
catch {
    $results
    Write-Output "FIX-07 failed: $($_.Exception.Message)"
    exit 1
}
