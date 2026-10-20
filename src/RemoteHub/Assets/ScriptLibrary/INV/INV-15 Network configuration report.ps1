<#
.SYNOPSIS
    Reports the device's network configuration: adapters, IP, DNS, routes, proxy, Wi-Fi and SMB.
.DESCRIPTION
    Covers adapter status, speed and driver version, IP configuration, DNS servers, default route,
    network category, WinHTTP proxy, saved Wi-Fi profile names, IPv6 and NetBIOS state, SMB client
    settings and the time source. Wi-Fi keys are never requested. MAC addresses are device
    identifiers and are left out unless -IncludeMac is set. The WinHTTP proxy is read from the
    registry and the time source from the W32Time parameters, not from localised tool text.
    WinINET proxy is per user and belongs to INV-16.
.NOTES
    RemoteHub: id=INV-15; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [switch]$IncludeMac,
    [switch]$IncludeHidden
)

$ErrorActionPreference = 'Stop'

function Get-WinHttpProxy {
    # WinHttpSettings is a binary blob: version, counter, flags, then length-prefixed proxy and bypass strings.
    try {
        $b = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Connections' -Name 'WinHttpSettings' -ErrorAction Stop).WinHttpSettings
    } catch { return [PSCustomObject]@{ Mode = 'Direct'; Proxy = ''; Bypass = '' } }
    if ($b.Length -lt 16) { return [PSCustomObject]@{ Mode = 'Data unavailable'; Proxy = ''; Bypass = '' } }
    $len = [BitConverter]::ToInt32($b, 12)
    $proxy = ''
    $bypass = ''
    if ($len -gt 0 -and (16 + $len) -le $b.Length) {
        $proxy = [Text.Encoding]::ASCII.GetString($b, 16, $len)
        $off = 16 + $len
        if ($off + 4 -le $b.Length) {
            $blen = [BitConverter]::ToInt32($b, $off)
            if ($blen -gt 0 -and ($off + 4 + $blen) -le $b.Length) { $bypass = [Text.Encoding]::ASCII.GetString($b, $off + 4, $blen) }
        }
    }
    $mode = 'Direct'
    if ($proxy) { $mode = 'Proxy' }
    [PSCustomObject]@{ Mode = $mode; Proxy = $proxy; Bypass = $bypass }
}

try {
    $adapters = @(Get-NetAdapter -IncludeHidden:$IncludeHidden | Sort-Object Name)
    $profiles = @{}
    foreach ($p in @(Get-NetConnectionProfile -ErrorAction SilentlyContinue)) { $profiles[[int]$p.InterfaceIndex] = $p }

    $nbMap = @{}
    foreach ($cfg in @(Get-CimInstance -ClassName Win32_NetworkAdapterConfiguration -Filter 'IPEnabled = TRUE')) {
        $nb = switch ([int]$cfg.TcpipNetbiosOptions) { 0 { 'Default (DHCP)' } 1 { 'Enabled' } 2 { 'Disabled' } default { 'Data unavailable' } }
        $nbMap[[int]$cfg.InterfaceIndex] = $nb
    }

    $ipv6Bindings = @{}
    foreach ($bnd in @(Get-NetAdapterBinding -ComponentID 'ms_tcpip6' -ErrorAction SilentlyContinue)) { $ipv6Bindings[$bnd.Name] = $bnd.Enabled }

    $adapterInfo = foreach ($a in $adapters) {
        $ip = Get-NetIPConfiguration -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue
        $prof = $profiles[[int]$a.ifIndex]
        $o = [ordered]@{
            Name          = $a.Name
            Description   = $a.InterfaceDescription
            Status        = [string]$a.Status
            LinkSpeed     = $a.LinkSpeed
            DriverVersion = $a.DriverVersionString
            IPv4          = @($ip.IPv4Address | ForEach-Object { "$($_.IPAddress)/$($_.PrefixLength)" })
            IPv6          = @($ip.IPv6Address | ForEach-Object { "$($_.IPAddress)/$($_.PrefixLength)" })
            Gateway       = @($ip.IPv4DefaultGateway | ForEach-Object { $_.NextHop })
            DnsServers    = @(Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue | ForEach-Object { $_.ServerAddresses })
            Category      = if ($prof) { [string]$prof.NetworkCategory } else { '' }
            IPv6Enabled   = if ($ipv6Bindings.ContainsKey($a.Name)) { $ipv6Bindings[$a.Name] } else { 'Data unavailable' }
            NetBIOS       = if ($nbMap.ContainsKey([int]$a.ifIndex)) { $nbMap[[int]$a.ifIndex] } else { '' }
        }
        if ($IncludeMac) { $o.MacAddress = $a.MacAddress }
        [PSCustomObject]$o
    }

    $defaultRoutes = @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric |
        ForEach-Object { [PSCustomObject]@{ InterfaceAlias = $_.InterfaceAlias; NextHop = $_.NextHop; Metric = $_.RouteMetric } })

    $wifi = 'Data unavailable'
    $wlanDir = Join-Path $env:ProgramData 'Microsoft\Wlansvc\Profiles\Interfaces'
    if (Test-Path -LiteralPath $wlanDir) {
        # Only the profile name is read from each XML; key material is never touched.
        $wifi = @(Get-ChildItem -LiteralPath $wlanDir -Recurse -Filter '*.xml' -ErrorAction SilentlyContinue | ForEach-Object {
            try { ([xml](Get-Content -LiteralPath $_.FullName -Raw)).WLANProfile.name } catch { $null }
        } | Where-Object { $_ } | Sort-Object -Unique)
    }

    $smb = 'Data unavailable'
    try {
        $c = Get-SmbClientConfiguration
        $smb = [PSCustomObject]@{
            RequireSecuritySignature = $c.RequireSecuritySignature
            EnableSecuritySignature  = $c.EnableSecuritySignature
            EnableInsecureGuestLogons = $c.EnableInsecureGuestLogons
            Smb1ClientEnabled        = ((Get-Service -Name 'mrxsmb10' -ErrorAction SilentlyContinue).StartType -ne 'Disabled' -and [bool](Get-Service -Name 'mrxsmb10' -ErrorAction SilentlyContinue))
        }
    } catch { Write-Verbose "SMB client settings unavailable: $($_.Exception.Message)" }

    $timeSource = 'Data unavailable'
    $tp = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\Parameters' -ErrorAction SilentlyContinue
    if ($tp) { $timeSource = "$($tp.Type): $($tp.NtpServer)" }

    [PSCustomObject]@{
        Adapters       = @($adapterInfo)
        DefaultRoutes  = $defaultRoutes
        WinHttpProxy   = Get-WinHttpProxy
        WiFiProfiles   = $wifi
        SmbClient      = $smb
        TimeSource     = $timeSource
    } | ConvertTo-Json -Depth 5
    exit 0
} catch {
    Write-Error "Network report failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
