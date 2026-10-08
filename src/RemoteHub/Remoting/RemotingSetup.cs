using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RemoteHub.Remoting;

/// <summary>Server certificate created by RemoteHub for one target's WinRM HTTPS listener.</summary>
public sealed record ListenerCertificate(byte[] Pfx, string PfxPassword, X509Certificate2 PublicCertificate)
{
    public string Thumbprint => PublicCertificate.Thumbprint;
}

/// <summary>
/// Builds the script an administrator runs on a target to enable PowerShell remoting for RemoteHub.
/// The app never runs it – it is copied (or saved) for the admin to review and run in an elevated PowerShell.
/// </summary>
public static class RemotingSetup
{
    public const string RuleHttps = "RemoteHub-WinRM-HTTPS-In";
    public const string RuleHttp = "RemoteHub-WinRM-HTTP-In";
    /// <summary>Rule names from before the rename ("RDP Manager"); the script replaces them.</summary>
    public const string LegacyRuleHttps = "RdpManager-WinRM-HTTPS-In", LegacyRuleHttp = "RdpManager-WinRM-HTTP-In";

    /// <summary>Local address this PC uses to reach <paramref name="host"/> (no packet is sent). Null if unknown.</summary>
    public static string? LocalAddressFor(string host)
    {
        try
        {
            var target = IPAddress.TryParse(host.Trim('[', ']'), out var ip)
                ? ip
                : Dns.GetHostAddresses(host).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).First();
            using var s = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(target, 5986);
            var local = ((IPEndPoint)s.LocalEndPoint!).Address;
            return IPAddress.IsLoopback(local) ? null : local.ToString();
        }
        catch { return null; }
    }

    /// <summary>
    /// All addresses of this PC that a target could see: IPv4 and global/unique-local IPv6 of every network adapter
    /// that is up (no loopback, link-local or APIPA). <paramref name="preferred"/> (the address used to reach the
    /// target) comes first.
    /// </summary>
    public static List<string> AllLocalAddresses(string? preferred = null)
    {
        var list = new List<string>();
        if (preferred is not null) list.Add(preferred);
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    var a = ua.Address;
                    if (IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal || a.IsIPv6Multicast) continue;
                    if (a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is [169, 254, ..]) continue;
                    if (a.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) continue;
                    var text = new IPAddress(a.GetAddressBytes()).ToString(); // drops an IPv6 scope id
                    if (!list.Contains(text)) list.Add(text);
                }
            }
        }
        catch { /* best effort */ }
        // IPv4 first (the usual case), then IPv6; keep the preferred one in front.
        return list.Take(preferred is null ? 0 : 1)
            .Concat(list.Skip(preferred is null ? 0 : 1).OrderBy(x => x.Contains(':') ? 1 : 0))
            .ToList();
    }

    /// <summary>
    /// Parses "10.0.0.5, 10.1.0.0/24". Only IP addresses and IP/prefix are accepted, so the value is safe to put
    /// into the script and never opens WinRM to everyone by accident.
    /// </summary>
    public static bool TryParseSources(string text, out string[] sources, out string? error)
    {
        sources = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        error = null;
        if (sources.Length == 0) { error = "Enter at least one IP address or subnet."; return false; }
        foreach (var s in sources)
        {
            var parts = s.Split('/');
            if (!IPAddress.TryParse(parts[0], out var ip) || parts.Length > 2 ||
                (parts.Length == 2 && (!int.TryParse(parts[1], out var bits) || bits < 0 ||
                                       bits > (ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))))
            {
                error = $"\"{s}\" is not an IP address or subnet (e.g. 10.0.0.5 or 10.0.0.0/24).";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// New self-signed server certificate for <paramref name="host"/>: end-entity only (not a CA), server
    /// authentication only, valid for 2 years, with the host as subject alternative name.
    /// </summary>
    public static ListenerCertificate CreateCertificate(string host, DateTimeOffset now)
    {
        host = host.Trim().Trim('[', ']');
        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out var ip)) san.AddIpAddress(ip);
        else
        {
            san.AddDnsName(host);
            var shortName = host.Split('.')[0];
            if (!shortName.Equals(host, StringComparison.OrdinalIgnoreCase)) san.AddDnsName(shortName);
        }
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(2));
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        // 3DES/SHA1 PKCS#12 so that Windows Server 2016 / Windows PowerShell 5.1 can import it. The password travels
        // with the script, so this encryption is only a container format here, not a protection.
        var pfx = cert.ExportPkcs12(Pkcs12ExportPbeParameters.Pkcs12TripleDesSha1, password);
        var publicOnly = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        return new ListenerCertificate(pfx, password, publicOnly);
    }

    /// <summary>
    /// Adds the target's certificate (public part only) to the current user's trusted roots, so WinRM on this PC
    /// accepts it. Windows asks the user to confirm. It is an end-entity certificate: it can't vouch for anything else.
    /// </summary>
    public static void TrustOnThisPc(ListenerCertificate cert, string host)
    {
        using var c = X509CertificateLoader.LoadCertificate(cert.PublicCertificate.RawData);
        c.FriendlyName = $"RemoteHub – WinRM {host}";
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(c); // throws if the user declines the Windows prompt
    }

    private static string Comment(string s) => new(s.Where(ch => !char.IsControl(ch)).ToArray());

    /// <summary>The setup script for the target. <paramref name="cert"/> null = HTTP (domain / Kerberos) only.</summary>
    public static string BuildScript(string connectionName, string host, string[] allowFrom, ListenerCertificate? cert, DateTime now)
    {
        var https = cert is not null;
        var sources = string.Join(",", allowFrom.Select(s => $"'{s}'"));
        var sb = new StringBuilder();
        sb.AppendLine("#Requires -RunAsAdministrator");
        sb.AppendLine($"# RemoteHub - enable PowerShell remoting on this computer for \"{Comment(connectionName)}\" ({Comment(host)}), generated {now:yyyy-MM-dd HH:mm}.");
        sb.AppendLine("# Run in an ELEVATED PowerShell on the target computer. Review first; set $DryRun = $true to only show what would change.");
        sb.AppendLine("# Changes: WinRM service + PowerShell remoting (Enable-PSRemoting)" + (https
            ? ", a certificate in LocalMachine\\My, an HTTPS listener on 5986, a firewall rule for 5986"
            : ", a firewall rule for 5985") + " allowing only the addresses below.");
        sb.AppendLine("# Undo: Disable-PSRemoting -Force; Remove-NetFirewallRule -Name " + (https ? RuleHttps : RuleHttp) +
                      (https ? "; remove the HTTPS listener (Get-ChildItem WSMan:\\localhost\\Listener) and the certificate (thumbprint below)." : "."));
        sb.AppendLine("$DryRun = $false");
        sb.AppendLine($"$AllowFrom = @({sources})   # address(es) RemoteHub connects from");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("function Step([string]$What, [scriptblock]$Do) { if ($DryRun) { Write-Host \"WOULD: $What\" -ForegroundColor Yellow } else { Write-Host \"$What...\" -ForegroundColor Cyan; & $Do } }");
        sb.AppendLine();
        sb.AppendLine("Step 'Enable PowerShell remoting (WinRM service, automatic start)' {");
        sb.AppendLine("    Enable-PSRemoting -Force -SkipNetworkProfileCheck | Out-Null");
        sb.AppendLine("    Set-Service -Name WinRM -StartupType Automatic");
        sb.AppendLine("}");
        if (https)
        {
            sb.AppendLine();
            sb.AppendLine($"$Thumbprint = '{cert!.Thumbprint}'");
            sb.AppendLine("Step \"Install certificate $Thumbprint in LocalMachine\\My\" {");
            sb.AppendLine("    if (-not (Test-Path \"Cert:\\LocalMachine\\My\\$Thumbprint\")) {");
            sb.AppendLine($"        $pfx = [Convert]::FromBase64String('{Convert.ToBase64String(cert.Pfx)}')");
            sb.AppendLine($"        $c = New-Object -TypeName System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $pfx, '{cert.PfxPassword}', 'MachineKeySet,PersistKeySet'");
            sb.AppendLine($"        $c.FriendlyName = 'WinRM HTTPS (RemoteHub)'");
            sb.AppendLine("        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('My', 'LocalMachine')");
            sb.AppendLine("        $store.Open('ReadWrite'); $store.Add($c); $store.Close()");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine("Step 'Bind the certificate to the WinRM HTTPS listener (port 5986)' {");
            sb.AppendLine("    $existing = Get-ChildItem WSMan:\\localhost\\Listener | Where-Object { $_.Keys -contains 'Transport=HTTPS' }");
            sb.AppendLine("    if ($existing) {");
            sb.AppendLine("        Write-Warning 'An HTTPS listener already exists - its certificate is replaced.'");
            sb.AppendLine("        foreach ($l in $existing) { Set-Item -Path \"WSMan:\\localhost\\Listener\\$($l.Name)\\CertificateThumbprint\" -Value $Thumbprint -Force }");
            sb.AppendLine("    } else {");
            sb.AppendLine("        New-Item -Path WSMan:\\localhost\\Listener -Transport HTTPS -Address * -CertificateThumbPrint $Thumbprint -Force | Out-Null");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }
        var rule = https ? RuleHttps : RuleHttp;
        var port = https ? 5986 : 5985;
        sb.AppendLine();
        sb.AppendLine($"Step \"Firewall: allow TCP {port} from $($AllowFrom -join ', ')\" {{");
        var legacyRule = https ? LegacyRuleHttps : LegacyRuleHttp;
        sb.AppendLine($"    # Replaces the rule of an earlier run (also the one named '{legacyRule}' from before the rename to RemoteHub).");
        sb.AppendLine($"    Get-NetFirewallRule -Name '{rule}', '{legacyRule}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule");
        sb.AppendLine($"    New-NetFirewallRule -Name '{rule}' -DisplayName 'WinRM {(https ? "HTTPS" : "HTTP")} (RemoteHub)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {port} -RemoteAddress $AllowFrom -Profile Any | Out-Null");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("if (-not $DryRun) {");
        sb.AppendLine("    Test-WSMan -ComputerName localhost | Out-Null");
        sb.AppendLine($"    Write-Host 'Done. PowerShell remoting is enabled{(https ? " over HTTPS (5986)" : "")} for ' -NoNewline -ForegroundColor Green; Write-Host ($AllowFrom -join ', ') -ForegroundColor Green");
        sb.AppendLine("    Write-Host 'The account RemoteHub uses must be a local administrator here or a member of \"Remote Management Users\".'");
        sb.AppendLine("    Write-Host 'Now click \"Test again\" in RemoteHub.'");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
