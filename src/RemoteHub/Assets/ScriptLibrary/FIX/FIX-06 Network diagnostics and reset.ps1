<#
.SYNOPSIS
    Diagnoses network reachability and name resolution, or resets the network stack.
.DESCRIPTION
    Diagnose mode (default, read-only) tests the gateway, DNS servers and an admin-supplied target list (read from
    a file, one host or host:port per line), path MTU to the gateway, the WinHTTP proxy, the network category, the
    certificate issuer of TLS endpoints (to spot TLS inspection) and clock skew. Reset mode flushes the DNS cache,
    renews DHCP leases, removes persistent routes for interfaces that no longer exist, restarts adapters and, with
    -StackReset, resets Winsock and the IP stack (restart needed, custom settings can be lost). Reset interrupts
    connectivity: never run it remotely without console or out-of-band access. Reset needs -Apply.
.NOTES
    RemoteHub: id=FIX-06; context=System; os=Both; risk=R2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Diagnose', 'Reset')]
    [string]$Mode = 'Diagnose',
    [string]$TargetFile = "$env:ProgramData\RemoteHub\FIX-06-targets.txt",
    [string[]]$TlsEndpoint = @('login.microsoftonline.com', 'www.microsoft.com'),
    [string[]]$ExpectedIssuerPattern = @('Microsoft', 'DigiCert', 'GlobalSign', 'Sectigo', 'Entrust', 'GeoTrust', 'Amazon', 'Google Trust'),
    [switch]$StackReset,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if (-not $Apply) { $WhatIfPreference = $true }
$results = New-Object System.Collections.Generic.List[object]
function Add-Test([string]$Test, [string]$Target, [bool]$Ok, [string]$Detail, [object]$Ms) {
    $results.Add([pscustomobject]@{ Test = $Test; Target = $Target; Ok = $Ok; TimeMs = $Ms; Detail = $Detail })
}
function Invoke-Native([string]$FilePath, [string]$Arguments) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $FilePath, $Arguments
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $null = $p.StandardOutput.ReadToEndAsync(); $null = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit(120000)) { try { $p.Kill() } catch { }; throw "$FilePath timed out." }
    return $p.ExitCode
}

try {
    if ($Mode -eq 'Diagnose') {
        $ping = New-Object System.Net.NetworkInformation.Ping
        $gateways = @(Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway } | ForEach-Object { $_.IPv4DefaultGateway.NextHop } | Select-Object -Unique)
        foreach ($gw in $gateways) {
            $r = $ping.Send($gw, 2000)
            Add-Test 'Gateway ping' $gw ($r.Status -eq 'Success') "$($r.Status)" $r.RoundtripTime
            # Largest unfragmented payload: binary search between 548 and 1472 bytes (IPv4 + ICMP headers add 28).
            $lo = 548; $hi = 1472; $opt = New-Object System.Net.NetworkInformation.PingOptions 64, $true
            while ($lo -lt $hi) {
                $mid = [int][math]::Ceiling(($lo + $hi) / 2)
                if ($ping.Send($gw, 1000, (New-Object byte[] $mid), $opt).Status -eq 'Success') { $lo = $mid } else { $hi = $mid - 1 }
            }
            Add-Test 'Path MTU' $gw ($lo + 28 -ge 1280) ("{0} bytes" -f ($lo + 28)) $null
        }
        if ($gateways.Count -eq 0) { Add-Test 'Gateway' '-' $false 'No IPv4 default gateway' $null }

        $targets = @()
        if (Test-Path -LiteralPath $TargetFile) { $targets = @(Get-Content -LiteralPath $TargetFile | Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() }) }
        if ($targets.Count -eq 0) { $targets = @($TlsEndpoint | ForEach-Object { "${_}:443" }) }
        $probeName = ($targets[0] -split ':')[0]

        foreach ($dns in @(Get-DnsClientServerAddress -AddressFamily IPv4 | ForEach-Object { $_.ServerAddresses } | Select-Object -Unique)) {
            $sw = [Diagnostics.Stopwatch]::StartNew()
            try { $null = Resolve-DnsName -Name $probeName -Server $dns -DnsOnly -QuickTimeout; Add-Test 'DNS server' $dns $true "Resolved $probeName" $sw.ElapsedMilliseconds }
            catch { Add-Test 'DNS server' $dns $false $_.Exception.Message $sw.ElapsedMilliseconds }
        }

        foreach ($t in $targets) {
            $parts = $t -split ':'; $hostName = $parts[0]; $port = 443; if ($parts.Count -gt 1) { $port = [int]$parts[1] }
            $sw = [Diagnostics.Stopwatch]::StartNew()
            try { $null = Resolve-DnsName -Name $hostName -DnsOnly -QuickTimeout; Add-Test 'Name resolution' $hostName $true '' $sw.ElapsedMilliseconds }
            catch { Add-Test 'Name resolution' $hostName $false $_.Exception.Message $sw.ElapsedMilliseconds; continue }
            $client = New-Object System.Net.Sockets.TcpClient
            $sw.Restart()
            try { $ok = $client.ConnectAsync($hostName, $port).Wait(5000) } catch { $ok = $false } finally { $client.Dispose() }
            Add-Test 'TCP connect' $t $ok '' $sw.ElapsedMilliseconds
        }

        $wh = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Connections' -ErrorAction SilentlyContinue
        $proxy = 'Direct'
        if ($wh -and $wh.PSObject.Properties['WinHttpSettings']) {
            $b = [byte[]]$wh.WinHttpSettings
            if ($b.Length -ge 16 -and ($b[8] -band 2)) { $proxy = [Text.Encoding]::ASCII.GetString($b, 16, [BitConverter]::ToInt32($b, 12)) }
        }
        Add-Test 'WinHTTP proxy' '-' $true $proxy $null
        foreach ($p in @(Get-NetConnectionProfile)) { Add-Test 'Network category' $p.InterfaceAlias $true "$($p.NetworkCategory)" $null }

        foreach ($ep in $TlsEndpoint) {
            $client = New-Object System.Net.Sockets.TcpClient
            try {
                if (-not $client.ConnectAsync($ep, 443).Wait(5000)) { throw 'TCP connect timed out' }
                # Accept any certificate: the issuer is only read, nothing is sent over this connection.
                $ssl = New-Object System.Net.Security.SslStream($client.GetStream(), $false, ([System.Net.Security.RemoteCertificateValidationCallback] { $true }))
                $ssl.AuthenticateAsClient($ep, $null, [Security.Authentication.SslProtocols]::Tls12, $false)
                $issuer = $ssl.RemoteCertificate.Issuer
                $known = @($ExpectedIssuerPattern | Where-Object { $issuer -like "*$_*" }).Count -gt 0
                $detail = $issuer; if (-not $known) { $detail = "Possible TLS inspection: $issuer" }
                Add-Test 'TLS issuer' $ep $known $detail $null
                $ssl.Dispose()
            }
            catch { Add-Test 'TLS issuer' $ep $false $_.Exception.Message $null }
            finally { $client.Dispose() }
        }

        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $req = [Net.HttpWebRequest]::Create("https://$($TlsEndpoint[0])/"); $req.Method = 'HEAD'; $req.Timeout = 5000; $req.AllowAutoRedirect = $false
            try { $resp = $req.GetResponse() } catch [Net.WebException] { $resp = $_.Exception.Response }
            # The Date header is RFC 1123 in UTC; parse it culture-invariant so non-English builds work.
            $serverUtc = [datetime]::ParseExact($resp.Headers['Date'], 'r', [Globalization.CultureInfo]::InvariantCulture)
            $skew = [math]::Round(($serverUtc - [datetime]::UtcNow).TotalSeconds)
            $resp.Close()
            Add-Test 'Clock skew' $TlsEndpoint[0] ([math]::Abs($skew) -le 120) "$skew seconds" $null
        }
        catch { Add-Test 'Clock skew' $TlsEndpoint[0] $false 'Data unavailable' $null }

        $results
        if (@($results | Where-Object { -not $_.Ok }).Count -gt 0) { exit 1 }
        exit 0
    }

    # Reset mode.
    $restart = $false
    Write-Output 'Warning: reset interrupts connectivity. Make sure console or out-of-band access exists.'
    if ($PSCmdlet.ShouldProcess('DNS client cache', 'Flush')) { Clear-DnsClientCache; Add-Test 'Flush DNS cache' '-' $true '' $null }
    $stale = @(Get-NetRoute -PolicyStore PersistentStore -ErrorAction SilentlyContinue | Where-Object { -not (Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue) })
    foreach ($r in $stale) {
        if ($PSCmdlet.ShouldProcess("$($r.DestinationPrefix) via $($r.NextHop)", 'Remove persistent route for a missing interface')) {
            Remove-NetRoute -InputObject $r -Confirm:$false; Add-Test 'Remove stale route' $r.DestinationPrefix $true '' $null
        }
    }
    foreach ($cfg in @(Get-CimInstance -ClassName Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=TRUE AND DHCPEnabled=TRUE')) {
        if ($PSCmdlet.ShouldProcess($cfg.Description, 'Renew DHCP lease')) {
            $rv = (Invoke-CimMethod -InputObject $cfg -MethodName RenewDHCPLease).ReturnValue
            Add-Test 'Renew DHCP lease' $cfg.Description ($rv -eq 0) "Return $rv" $null
        }
    }
    foreach ($a in @(Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' })) {
        if ($PSCmdlet.ShouldProcess($a.Name, 'Restart adapter')) { Restart-NetAdapter -Name $a.Name -Confirm:$false; Add-Test 'Restart adapter' $a.Name $true '' $null }
    }
    if ($StackReset) {
        $netsh = Join-Path $env:windir 'System32\netsh.exe'
        if ($PSCmdlet.ShouldProcess('Winsock catalog and IP stack', 'netsh winsock reset; netsh int ip reset (restart needed)')) {
            $w = Invoke-Native $netsh 'winsock reset'; Add-Test 'Winsock reset' '-' ($w -eq 0) "Exit $w" $null
            if ($w -ne 0) { throw 'Winsock reset failed; IP reset not attempted.' }
            $i = Invoke-Native $netsh 'int ip reset'; Add-Test 'IP stack reset' '-' ($i -eq 0) "Exit $i" $null
            if ($i -ne 0) { throw 'IP stack reset failed.' }
            $restart = $true
        }
    }
    $results
    if (-not $Apply) { Write-Output 'Dry run: nothing changed. Run with -Apply to make the changes.' }
    if (@($results | Where-Object { -not $_.Ok }).Count -gt 0) { exit 1 }
    if ($restart) { Write-Output 'A restart is required to finish the stack reset.'; exit 3010 }
    exit 0
}
catch {
    $results
    Write-Output "FIX-06 failed: $($_.Exception.Message)"
    exit 1
}
