<#
.SYNOPSIS
    Audits an RDS deployment (servers, collections, sessions, limits, licensing) and optionally logs off old sessions.
.DESCRIPTION
    Reports deployment servers, collections and their session limits, session counts, disconnected and idle
    sessions, the session limit policy, license server, licensing mode and available CALs. Housekeeping
    mode logs off sessions disconnected longer than the limit, skipping excluded accounts; a logoff loses
    unsaved work, so it runs only with -Apply after notice and approval. User names are redacted unless
    -IncludeIdentity. A missing license server is flagged because the licensing grace period is limited.
.NOTES
    RemoteHub: id=SRV-19; context=System; os=Server; risk=R2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Audit', 'Housekeeping')]
    [string]$Mode = 'Audit',
    [string]$ConnectionBroker = '',
    [int]$DisconnectedLimitMinutes = 480,
    [int]$IdleWarningMinutes = 120,
    # Accounts (DOMAIN\user or user) never logged off.
    [string[]]$ExcludeUser = @(),
    [switch]$IncludeIdentity,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if (-not $Apply) {
    $WhatIfPreference = $true
    Write-Output 'Dry run: nothing changed. Run with -Apply to make the changes.'
}

function Get-Name($s) {
    if ($IncludeIdentity) { return "$($s.DomainName)\$($s.UserName)" }
    return '[REDACTED]'
}

$exitCode = 0
try {
    if (-not (Get-Module -ListAvailable -Name RemoteDesktop)) { Write-Output 'Data unavailable: the RemoteDesktop module is not installed.'; exit 1 }
    Import-Module RemoteDesktop -Verbose:$false
    if (-not $ConnectionBroker) { $ConnectionBroker = ([System.Net.Dns]::GetHostEntry($env:COMPUTERNAME)).HostName }
    $cb = @{ ConnectionBroker = $ConnectionBroker }

    $servers = @(Get-RDServer @cb)
    $collections = @(Get-RDSessionCollection @cb)
    $sessions = @(Get-RDUserSession @cb)
    $now = Get-Date

    $collRows = foreach ($c in $collections) {
        $cfg = Get-RDSessionCollectionConfiguration -CollectionName $c.CollectionName -Connection @cb
        [pscustomobject]@{ Collection = $c.CollectionName; Sessions = @($sessions | Where-Object { $_.CollectionName -eq $c.CollectionName }).Count
            DisconnectedLimitMin = $cfg.DisconnectedSessionLimitMin; IdleLimitMin = $cfg.IdleSessionLimitMin; ActiveLimitMin = $cfg.ActiveSessionLimitMin
            EndOnLimit = $cfg.BrokenConnectionAction }
    }

    $sessRows = foreach ($s in $sessions) {
        $state = [string]$s.SessionState
        $idleMin = [int]([double]$s.IdleTime / 60000)
        $discMin = 0
        if ($state -eq 'STATE_DISCONNECTED' -and $s.DisconnectTime) { $discMin = [int]($now - $s.DisconnectTime).TotalMinutes }
        [pscustomobject]@{ User = (Get-Name $s); Host = $s.HostServer; Collection = $s.CollectionName; State = $state
            IdleMin = $idleMin; DisconnectedMin = $discMin; SessionId = $s.UnifiedSessionId; Raw = $s }
    }

    $policyKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
    $policy = 'Not configured'
    if (Test-Path -LiteralPath $policyKey) {
        $p = Get-ItemProperty -LiteralPath $policyKey
        $policy = "MaxIdleTime=$($p.MaxIdleTime) MaxDisconnectionTime=$($p.MaxDisconnectionTime) (ms)"
    }

    $lic = Get-RDLicenseConfiguration @cb
    $licServers = @($lic.LicenseServer | Where-Object { $_ })
    $cals = @()
    foreach ($ls in $licServers) {
        try {
            $cals += @(Get-CimInstance -ComputerName $ls -ClassName Win32_TSLicenseKeyPack -OperationTimeoutSec 30 | Where-Object { $_.TotalLicenses -gt 0 } | ForEach-Object {
                [pscustomobject]@{ LicenseServer = $ls; Product = $_.ProductVersion; Type = $_.TypeAndModel; Total = $_.TotalLicenses; Available = $_.AvailableLicenses; Issued = $_.IssuedLicenses } })
        } catch { $cals += [pscustomobject]@{ LicenseServer = $ls; Product = 'Data unavailable'; Type = ''; Total = ''; Available = ''; Issued = '' } }
    }

    [pscustomobject]@{
        ConnectionBroker = $ConnectionBroker
        Servers = (@($servers | ForEach-Object { "$($_.Server) [$(@($_.Roles) -join ',')]" }) -join '; ')
        Sessions = $sessions.Count; Disconnected = @($sessRows | Where-Object { $_.State -eq 'STATE_DISCONNECTED' }).Count
        IdleOverWarning = @($sessRows | Where-Object { $_.State -eq 'STATE_ACTIVE' -and $_.IdleMin -ge $IdleWarningMinutes }).Count
        LicensingMode = [string]$lic.Mode; LicenseServers = $(if ($licServers.Count) { $licServers -join ', ' } else { 'NONE CONFIGURED' })
        SessionLimitPolicy = $policy
    } | Format-List | Out-String -Width 250
    $collRows | Format-Table -AutoSize | Out-String -Width 250
    $sessRows | Select-Object User, Host, Collection, State, IdleMin, DisconnectedMin | Format-Table -AutoSize | Out-String -Width 250
    if ($cals.Count) { $cals | Format-Table -AutoSize | Out-String -Width 250 }
    if ($licServers.Count -eq 0 -or [string]$lic.Mode -eq 'NotConfigured') {
        Write-Warning 'No license server or licensing mode is configured; the grace period will run out.'
        $exitCode = 1
    }

    if ($Mode -eq 'Housekeeping') {
        $targets = @($sessRows | Where-Object { $_.State -eq 'STATE_DISCONNECTED' -and $_.DisconnectedMin -ge $DisconnectedLimitMinutes })
        $targets = @($targets | Where-Object {
            $full = "$($_.Raw.DomainName)\$($_.Raw.UserName)"
            -not ($ExcludeUser -contains $full -or $ExcludeUser -contains $_.Raw.UserName) })
        if ($targets.Count -eq 0) { Write-Output 'No change: no disconnected session exceeds the limit.' }
        foreach ($t in $targets) {
            if ($PSCmdlet.ShouldProcess("$($t.User) on $($t.Host) (session $($t.SessionId), disconnected $($t.DisconnectedMin) min)", 'Log off (unsaved work is lost)')) {
                Invoke-RDUserLogoff -HostServer $t.Host -UnifiedSessionID $t.SessionId -Force
                Write-Output "Logged off session $($t.SessionId) on $($t.Host)."
            }
        }
    }
} catch {
    Write-Error "RDS audit failed: $($_.Exception.Message)" -ErrorAction Continue
    $exitCode = 1
}
exit $exitCode
