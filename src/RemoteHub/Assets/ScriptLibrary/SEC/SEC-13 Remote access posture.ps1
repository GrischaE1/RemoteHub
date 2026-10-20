<#
.SYNOPSIS
    Reports how this device can be reached remotely: Remote Desktop, WinRM, OpenSSH, Remote Registry and SMB shares.
.DESCRIPTION
    Remote Desktop: state, network level authentication, security layer, port, firewall scope and the
    Remote Desktop Users group. WinRM: listeners, Basic authentication, unencrypted traffic, trusted hosts
    and session configurations. Also OpenSSH server, Remote Registry and non-default SMB shares open to
    Everyone. The WSMan drive needs the WinRM service, so a stopped service is reported as a state.
    Group member names are redacted unless -IncludeIdentity.
.NOTES
    RemoteHub: id=SEC-13; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [switch]$IncludeIdentity
)

$ErrorActionPreference = 'Stop'
$unavailable = 'Data unavailable'
$findings = New-Object System.Collections.Generic.List[object]
function Add-Finding($area, $item, $value, $severity, $note) {
    $findings.Add([PSCustomObject]@{ Area = $area; Item = $item; Value = [string]$value; Severity = $severity; Note = $note })
}
function Get-Reg($path, $name) {
    try { return (Get-ItemProperty -LiteralPath $path -Name $name -ErrorAction Stop).$name } catch { return $null }
}
# Policy values win over the local configuration, as Remote Desktop itself applies them.
function Get-Effective($policyPath, $localPath, $name) {
    $p = Get-Reg $policyPath $name
    if ($null -ne $p) { return $p }
    return Get-Reg $localPath $name
}

try {
    # Remote Desktop
    $tsPol = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
    $ts = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
    $rdpTcp = "$ts\WinStations\RDP-Tcp"
    $deny = Get-Effective $tsPol $ts 'fDenyTSConnections'
    $rdpOn = ($deny -eq 0)
    $nla = Get-Effective $tsPol $rdpTcp 'UserAuthentication'
    $layer = Get-Effective $tsPol $rdpTcp 'SecurityLayer'
    $port = Get-Reg $rdpTcp 'PortNumber'
    Add-Finding 'Remote Desktop' 'Enabled' $rdpOn 'Info' ''
    if ($rdpOn) {
        if ($nla -ne 1) { Add-Finding 'Remote Desktop' 'Network level authentication' 'Off' 'High' 'Remote Desktop on with NLA off' } else { Add-Finding 'Remote Desktop' 'Network level authentication' 'On' 'Info' '' }
        $layerText = @{ 0 = 'RDP (no TLS)'; 1 = 'Negotiate'; 2 = 'TLS' }[[int]$layer]
        $sev = 'Info'; if ($layer -eq 0) { $sev = 'Medium' }
        Add-Finding 'Remote Desktop' 'Security layer' $layerText $sev ''
        Add-Finding 'Remote Desktop' 'Port' $port 'Info' ''
        $rules = @(Get-NetFirewallRule -PolicyStore ActiveStore -Name 'RemoteDesktop-*' -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' })
        foreach ($r in $rules) {
            $scope = @((Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $r).RemoteAddress) -join ', '
            $sev = 'Info'; if ($scope -eq 'Any') { $sev = 'Medium' }
            Add-Finding 'Remote Desktop' "Firewall $($r.DisplayName)" $scope $sev 'Remote address scope'
        }
    }
    try {
        $members = @(Get-LocalGroupMember -SID 'S-1-5-32-555')
        $names = '[REDACTED]'; if ($IncludeIdentity) { $names = ($members | ForEach-Object { $_.Name }) -join ', ' }
        Add-Finding 'Remote Desktop' 'Remote Desktop Users members' ("{0} ({1})" -f $members.Count, $names) 'Info' ''
    } catch { Add-Finding 'Remote Desktop' 'Remote Desktop Users members' $unavailable 'Info' 'Local groups are not available (for example on a domain controller)' }

    # WinRM
    $winrm = Get-Service -Name WinRM -ErrorAction SilentlyContinue
    if (-not $winrm -or $winrm.Status -ne 'Running') {
        $state = 'NotInstalled'; if ($winrm) { $state = [string]$winrm.Status }
        Add-Finding 'WinRM' 'Service' $state 'Info' 'Listener details need the WinRM service running'
    } else {
        Add-Finding 'WinRM' 'Service' 'Running' 'Info' ''
        foreach ($l in @(Get-ChildItem -Path 'WSMan:\localhost\Listener')) {
            $keys = @($l.Keys) -join '; '
            $sev = 'Info'; if ($keys -match 'Transport=HTTP$|Transport=HTTP;') { $sev = 'Low' }
            Add-Finding 'WinRM' 'Listener' $keys $sev ''
        }
        $basic = (Get-Item -Path 'WSMan:\localhost\Service\Auth\Basic').Value
        if ($basic -eq 'true') { Add-Finding 'WinRM' 'Basic authentication' 'Enabled' 'High' 'WinRM Basic authentication enabled' } else { Add-Finding 'WinRM' 'Basic authentication' 'Disabled' 'Info' '' }
        $unenc = (Get-Item -Path 'WSMan:\localhost\Service\AllowUnencrypted').Value
        if ($unenc -eq 'true') { Add-Finding 'WinRM' 'Unencrypted traffic' 'Allowed' 'High' '' } else { Add-Finding 'WinRM' 'Unencrypted traffic' 'Not allowed' 'Info' '' }
        $trusted = (Get-Item -Path 'WSMan:\localhost\Client\TrustedHosts').Value
        if ($trusted -eq '*') { Add-Finding 'WinRM' 'Trusted hosts' '*' 'High' 'Wildcard trusted hosts' }
        elseif ($trusted) { Add-Finding 'WinRM' 'Trusted hosts' $trusted 'Low' '' }
        try {
            $configs = @(Get-PSSessionConfiguration | ForEach-Object { $_.Name })
            Add-Finding 'WinRM' 'Session configurations' ($configs -join ', ') 'Info' ''
        } catch { Add-Finding 'WinRM' 'Session configurations' $unavailable 'Info' '' }
    }

    # OpenSSH server
    $sshd = Get-Service -Name sshd -ErrorAction SilentlyContinue
    if ($sshd) { Add-Finding 'OpenSSH' 'Server service' ("{0} ({1})" -f $sshd.Status, $sshd.StartType) 'Info' '' }
    else {
        $cap = $null
        try { $cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object -First 1 } catch { Write-Verbose "Capability query failed: $($_.Exception.Message)" }
        $state = 'Not installed'; if ($cap) { $state = [string]$cap.State }
        Add-Finding 'OpenSSH' 'Server' $state 'Info' ''
    }

    # Remote Registry
    $rr = Get-Service -Name RemoteRegistry -ErrorAction SilentlyContinue
    if ($rr) {
        $sev = 'Info'; if ($rr.Status -eq 'Running') { $sev = 'Low' }
        Add-Finding 'Remote Registry' 'Service' ("{0} ({1})" -f $rr.Status, $rr.StartType) $sev ''
    }

    # SMB shares other than the administrative ones; Everyone is matched by SID because the name is localized.
    $everyoneName = (New-Object System.Security.Principal.SecurityIdentifier 'S-1-1-0').Translate([System.Security.Principal.NTAccount]).Value
    try {
        foreach ($s in @(Get-SmbShare | Where-Object { -not $_.Special })) {
            $everyone = @(Get-SmbShareAccess -Name $s.Name | Where-Object { $_.AccountName -eq $everyoneName -and $_.AccessControlType -eq 'Allow' })
            $sev = 'Info'; $note = ''
            if ($everyone | Where-Object { $_.AccessRight -match 'Full|Change' }) { $sev = 'Medium'; $note = 'Everyone can write' }
            Add-Finding 'SMB' "Share $($s.Name)" $s.Path $sev $note
        }
    } catch { Add-Finding 'SMB' 'Shares' $unavailable 'Info' $_.Exception.Message }

    $findings | Sort-Object @{ Expression = { @{ High = 0; Medium = 1; Low = 2; Info = 3 }[$_.Severity] } }, Area
    if (@($findings | Where-Object { $_.Severity -eq 'High' }).Count -gt 0) { exit 1 }
    exit 0
} catch {
    Write-Error "Remote access posture check failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
