<#
.SYNOPSIS
    Enables and hardens WinRM / PowerShell remoting and verifies it with a loopback test.
.DESCRIPTION
    Enables remoting, adds an HTTPS listener with a server authentication certificate whose subject or SAN
    matches the FQDN, scopes the WinRM firewall rules to management networks, removes wildcard trusted
    hosts, turns off Basic, CredSSP and unencrypted traffic unless allowed, and sets shell memory and idle
    limits. Prints the configuration before and after. Group Policy can override these settings and is
    reported. Remoting is not opened on a Public network profile unless the firewall is scoped.
.NOTES
    RemoteHub: id=SRV-21; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$CertificateThumbprint = '',
    [bool]$CreateHttpsListener = $true,
    # Remote addresses allowed to reach WinRM, e.g. '10.20.0.0/16'. Empty keeps LocalSubnet.
    [string[]]$ManagementNetwork = @(),
    [bool]$AllowBasic = $false,
    [bool]$AllowCredSSP = $false,
    [int]$MaxMemoryPerShellMB = 1024,
    [int]$IdleTimeoutMinutes = 120
)

$ErrorActionPreference = 'Stop'

function Get-State {
    $l = @(Get-ChildItem -Path WSMan:\localhost\Listener -ErrorAction SilentlyContinue | ForEach-Object {
        $keys = @($_.Keys)
        ($keys | Where-Object { $_ -like 'Transport=*' }) -replace 'Transport=', '' })
    [pscustomobject]@{
        WinRMService   = [string](Get-Service -Name WinRM).Status
        Listeners      = ($l -join ',')
        Basic          = [string](Get-Item WSMan:\localhost\Service\Auth\Basic -ErrorAction SilentlyContinue).Value
        CredSSP        = [string](Get-Item WSMan:\localhost\Service\Auth\CredSSP -ErrorAction SilentlyContinue).Value
        Kerberos       = [string](Get-Item WSMan:\localhost\Service\Auth\Kerberos -ErrorAction SilentlyContinue).Value
        AllowUnencrypted = [string](Get-Item WSMan:\localhost\Service\AllowUnencrypted -ErrorAction SilentlyContinue).Value
        TrustedHosts   = [string](Get-Item WSMan:\localhost\Client\TrustedHosts -ErrorAction SilentlyContinue).Value
        MaxMemoryPerShellMB = [string](Get-Item WSMan:\localhost\Shell\MaxMemoryPerShellMB -ErrorAction SilentlyContinue).Value
        IdleTimeoutMs  = [string](Get-Item WSMan:\localhost\Shell\IdleTimeout -ErrorAction SilentlyContinue).Value
    }
}

$changes = @()
function Set-WsmanValue([string]$Path, [string]$Desired) {
    $cur = [string](Get-Item -Path $Path).Value
    if ($cur -eq $Desired) { return }
    if ($PSCmdlet.ShouldProcess($Path, "Set '$cur' -> '$Desired'")) {
        Set-Item -Path $Path -Value $Desired -Force
        $script:changes += "$Path = $Desired"
    } else { $script:changes += "Planned: $Path = $Desired" }
}

$exitCode = 0
try {
    if (Test-Path -LiteralPath 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WinRM\Service') {
        Write-Warning 'WinRM service settings are set by Group Policy; local changes to those values may be overwritten.'
    }
    $fqdn = ([System.Net.Dns]::GetHostEntry($env:COMPUTERNAME)).HostName
    $public = @(Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object { [string]$_.NetworkCategory -eq 'Public' })
    if ($public.Count -gt 0 -and $ManagementNetwork.Count -eq 0) {
        throw 'A network uses the Public profile; give -ManagementNetwork to scope the firewall before enabling remoting.'
    }

    $before = Get-State
    'Before:'; $before | Format-List | Out-String -Width 200

    $listening = $before.WinRMService -eq 'Running' -and $before.Listeners -match 'HTTP'
    if (-not $listening) {
        if ($PSCmdlet.ShouldProcess($env:COMPUTERNAME, 'Enable-PSRemoting')) {
            Enable-PSRemoting -Force -SkipNetworkProfileCheck | Out-Null
            $changes += 'Enabled PowerShell remoting'
        } else { $changes += 'Planned: Enable-PSRemoting' }
    }

    if (Test-Path WSMan:\localhost\Service) {
        Set-WsmanValue 'WSMan:\localhost\Service\Auth\Basic' ([string]$AllowBasic).ToLowerInvariant()
        Set-WsmanValue 'WSMan:\localhost\Service\Auth\CredSSP' ([string]$AllowCredSSP).ToLowerInvariant()
        Set-WsmanValue 'WSMan:\localhost\Service\Auth\Kerberos' 'true'
        Set-WsmanValue 'WSMan:\localhost\Service\AllowUnencrypted' 'false'
        Set-WsmanValue 'WSMan:\localhost\Shell\MaxMemoryPerShellMB' ([string]$MaxMemoryPerShellMB)
        Set-WsmanValue 'WSMan:\localhost\Shell\IdleTimeout' ([string]($IdleTimeoutMinutes * 60000))
        $th = [string](Get-Item WSMan:\localhost\Client\TrustedHosts).Value
        if ($th -match '\*') {
            $clean = (@($th -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -notmatch '\*' }) -join ',')
            Set-WsmanValue 'WSMan:\localhost\Client\TrustedHosts' $clean
        }
    }

    if ($CreateHttpsListener -and $before.Listeners -notmatch 'HTTPS') {
        $cert = $null
        if ($CertificateThumbprint) { $cert = Get-Item -LiteralPath "Cert:\LocalMachine\My\$CertificateThumbprint" }
        else {
            $cert = Get-ChildItem -LiteralPath Cert:\LocalMachine\My | Where-Object {
                $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) -and
                @($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.1' }).Count -gt 0 -and
                (@($_.DnsNameList | Where-Object { $_.Unicode -eq $fqdn }).Count -gt 0) } | Sort-Object NotAfter -Descending | Select-Object -First 1
        }
        if (-not $cert) {
            Write-Warning "No server authentication certificate for $fqdn found; HTTPS listener not created."
            $exitCode = 1
        } elseif ($PSCmdlet.ShouldProcess("HTTPS listener on $fqdn", "Create with certificate expiring $($cert.NotAfter.ToString('yyyy-MM-dd'))")) {
            New-WSManInstance -ResourceURI 'winrm/config/Listener' -SelectorSet @{ Address = '*'; Transport = 'HTTPS' } -ValueSet @{ Hostname = $fqdn; CertificateThumbprint = $cert.Thumbprint } | Out-Null
            $changes += 'Created HTTPS listener'
        } else { $changes += 'Planned: create HTTPS listener' }
    }

    $scope = @('LocalSubnet')
    if ($ManagementNetwork.Count -gt 0) { $scope = $ManagementNetwork }
    $httpRules = @(Get-NetFirewallRule -Name 'WINRM-HTTP-In-TCP*' -ErrorAction SilentlyContinue)
    foreach ($r in $httpRules) {
        $cur = @(($r | Get-NetFirewallAddressFilter).RemoteAddress)
        if (($cur -join ',') -ne ($scope -join ',')) {
            if ($PSCmdlet.ShouldProcess($r.Name, "Scope remote addresses to $($scope -join ',')")) {
                Set-NetFirewallRule -Name $r.Name -RemoteAddress $scope
                $changes += "Firewall $($r.Name) scoped to $($scope -join ',')"
            } else { $changes += "Planned: scope $($r.Name)" }
        }
    }
    if ($CreateHttpsListener -and -not (Get-NetFirewallRule -Name 'RemoteHub-WinRM-HTTPS-In' -ErrorAction SilentlyContinue)) {
        if ($PSCmdlet.ShouldProcess('RemoteHub-WinRM-HTTPS-In', 'Create firewall rule TCP 5986')) {
            New-NetFirewallRule -Name 'RemoteHub-WinRM-HTTPS-In' -DisplayName 'Windows Remote Management (HTTPS-In)' -Direction Inbound -Protocol TCP -LocalPort 5986 -RemoteAddress $scope -Action Allow -Profile Domain, Private | Out-Null
            $changes += 'Created firewall rule for TCP 5986'
        } else { $changes += 'Planned: firewall rule for TCP 5986' }
    }

    if ($changes.Count -eq 0) { 'No change: the baseline is already in place.' } else { 'Changes:'; $changes | ForEach-Object { "  $_" } }
    'After:'; Get-State | Format-List | Out-String -Width 200

    if (-not $WhatIfPreference) {
        try {
            $null = Test-WSMan -ComputerName localhost
            $who = Invoke-Command -ComputerName localhost -ScriptBlock { $PSVersionTable.PSVersion.ToString() }
            "Loopback test: OK (remote PowerShell $who)"
        } catch {
            "Loopback test: FAILED - $($_.Exception.Message)"
            $exitCode = 1
        }
    }
} catch {
    Write-Error "Remoting baseline failed: $($_.Exception.Message)" -ErrorAction Continue
    $exitCode = 1
}
exit $exitCode
