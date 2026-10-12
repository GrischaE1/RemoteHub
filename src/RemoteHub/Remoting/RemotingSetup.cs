using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace RemoteHub.Remoting;

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

    public const string CertificateName = "WinRM HTTPS (RemoteHub)";

    /// <summary>
    /// The PowerShell command that creates the target's HTTPS certificate on the target itself, so its private key
    /// never leaves it (not exportable): end-entity only (not a CA), server authentication only, 2 years, with the host
    /// as subject alternative name. Works in Windows PowerShell 5.1 (Windows 10 / Server 2016 and later).
    /// </summary>
    public static string CertificateCommand(string host, string store)
    {
        host = host.Trim().Trim('[', ']');
        string san;
        if (IPAddress.TryParse(host, out var ip)) san = "IPAddress=" + ip;
        else
        {
            // It goes into the script: a host name has nothing else.
            if (host.Length == 0 || host.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-')))
                throw new ArgumentException($"\"{host}\" isn't a computer name or IP address.");
            var shortName = host.Split('.')[0];
            san = "DNS=" + host + (shortName.Equals(host, StringComparison.OrdinalIgnoreCase) ? "" : "&DNS=" + shortName);
        }
        return $"New-SelfSignedCertificate -Subject 'CN={host}' -CertStoreLocation '{store}' -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 " +
               "-KeyExportPolicy NonExportable -KeyUsage DigitalSignature, KeyEncipherment -NotAfter (Get-Date).AddYears(2) " +
               $"-FriendlyName '{CertificateName}' -TextExtension @('2.5.29.19={{critical}}{{text}}ca=0', '2.5.29.37={{text}}1.3.6.1.5.5.7.3.1', '2.5.29.17={{text}}{san}')";
    }

    /// <summary>
    /// The certificate the target's WinRM HTTPS listener presents (public part; nothing is validated – the user compares
    /// its thumbprint with the one the setup script printed before it is trusted).
    /// </summary>
    public static async Task<X509Certificate2> FetchCertificateAsync(string host, int port, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host.Trim().Trim('[', ']'), port, ct);
        X509Certificate2? seen = null;
        await using var tls = new System.Net.Security.SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
        {
            if (cert is not null) seen = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
            return true; // only looked at, nothing is sent
        });
        await tls.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions { TargetHost = host.Trim().Trim('[', ']') }, ct);
        return seen ?? throw new InvalidDataException("The computer didn't present a certificate.");
    }

    /// <summary>"AB12 CD34 …" – the thumbprint as the setup script prints it, in groups for comparing by eye.</summary>
    public static string Grouped(string thumbprint) =>
        string.Join(" ", Enumerable.Range(0, (thumbprint.Length + 3) / 4).Select(i => thumbprint.Substring(i * 4, Math.Min(4, thumbprint.Length - i * 4))));

    /// <summary>
    /// Adds the target's certificate (public part only) to the current user's trusted roots, so WinRM on this PC
    /// accepts it. Windows asks the user to confirm. It is an end-entity certificate: it can't vouch for anything else.
    /// </summary>
    public static void TrustOnThisPc(X509Certificate2 cert, string host)
    {
        using var c = X509CertificateLoader.LoadCertificate(cert.RawData);
        c.FriendlyName = $"RemoteHub – WinRM {host}";
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(c); // throws if the user declines the Windows prompt
    }

    private static string Comment(string s) => new(s.Where(ch => !char.IsControl(ch)).ToArray());

    /// <summary>
    /// The setup script for the target: HTTPS with a certificate made there, or HTTP (domain / Kerberos) only.
    /// <paramref name="allowLocalAdmins"/>: local administrators keep their admin rights over the network
    /// (LocalAccountTokenFilterPolicy = 1) – without it WinRM refuses them where UAC remote restrictions are on; otherwise
    /// the script only checks and warns.
    /// </summary>
    public static string BuildScript(string connectionName, string host, string[] allowFrom, bool https, DateTime now, bool allowLocalAdmins = true)
    {
        var sources = string.Join(",", allowFrom.Select(s => $"'{s}'"));
        var sb = new StringBuilder();
        sb.AppendLine("#Requires -RunAsAdministrator");
        sb.AppendLine($"# RemoteHub - enable PowerShell remoting on this computer for \"{Comment(connectionName)}\" ({Comment(host)}), generated {now:yyyy-MM-dd HH:mm}.");
        sb.AppendLine("# Run in an ELEVATED PowerShell on the target computer. Review first; set $DryRun = $true to only show what would change.");
        sb.AppendLine("# Changes: WinRM service + PowerShell remoting (Enable-PSRemoting)" + (https
            ? ", a certificate made here in LocalMachine\\My (its key never leaves this computer), an HTTPS listener on 5986, a firewall rule for 5986"
            : ", a firewall rule for 5985") + " allowing only the addresses below.");
        sb.AppendLine("# Enable-PSRemoting also turns on Windows' own WinRM HTTP rule for everyone: if it wasn't on before, it is turned off again.");
        if (allowLocalAdmins)
            sb.AppendLine("# Local administrators keep their admin rights over the network (LocalAccountTokenFilterPolicy = 1) - needed for local accounts where UAC remote restrictions are on.");
        sb.AppendLine("# Undo: Disable-PSRemoting -Force; Remove-NetFirewallRule -Name " + (https ? RuleHttps : RuleHttp) +
                      (https ? $"; remove the HTTPS listener (Get-ChildItem WSMan:\\localhost\\Listener) and the certificate '{CertificateName}' in LocalMachine\\My" : "") +
                      (allowLocalAdmins ? "; set LocalAccountTokenFilterPolicy back to 0 (HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System)." : "."));
        sb.AppendLine("$DryRun = $false");
        sb.AppendLine($"$AllowFrom = @({sources})   # address(es) RemoteHub connects from");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("function Step([string]$What, [scriptblock]$Do) { if ($DryRun) { Write-Host \"WOULD: $What\" -ForegroundColor Yellow } else { Write-Host \"$What...\" -ForegroundColor Cyan; & $Do } }");
        sb.AppendLine();
        sb.AppendLine("# Windows' own WinRM HTTP rules that are on already (e.g. set up by IT) are left alone.");
        sb.AppendLine("$BuiltinOn = @(try { Get-NetFirewallRule -Name 'WINRM-HTTP-In-TCP*' -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -eq 'True' } | ForEach-Object { $_.Name } } catch { })");
        sb.AppendLine("Step 'Enable PowerShell remoting (WinRM service, automatic start)' {");
        sb.AppendLine("    Enable-PSRemoting -Force -SkipNetworkProfileCheck | Out-Null");
        sb.AppendLine("    Set-Service -Name WinRM -StartupType Automatic");
        sb.AppendLine("}");
        sb.AppendLine("$UacKey = 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System'");
        if (allowLocalAdmins)
        {
            sb.AppendLine("Step 'Let local administrators use their admin rights over the network (LocalAccountTokenFilterPolicy = 1)' {");
            sb.AppendLine("    Set-ItemProperty -Path $UacKey -Name LocalAccountTokenFilterPolicy -Type DWord -Value 1");
            sb.AppendLine("}");
        }
        sb.AppendLine("Step \"Firewall: turn Windows' own WinRM HTTP rule off again (it allows everyone)\" {");
        sb.AppendLine("    Get-NetFirewallRule -Name 'WINRM-HTTP-In-TCP*' -ErrorAction SilentlyContinue | Where-Object { $BuiltinOn -notcontains $_.Name } | Disable-NetFirewallRule");
        sb.AppendLine("    if ($BuiltinOn) { Write-Warning \"Windows' own WinRM rule was already on before ($($BuiltinOn -join ', ')) - left as it is.\" }");
        sb.AppendLine("}");
        if (https)
        {
            sb.AppendLine();
            sb.AppendLine($"$Cert = $null");
            sb.AppendLine($"Step 'Create a certificate for this computer in LocalMachine\\My (key not exportable, reused if one is still valid)' {{");
            sb.AppendLine($"    $script:Cert = Get-ChildItem Cert:\\LocalMachine\\My | Where-Object {{ $_.FriendlyName -eq '{CertificateName}' -and $_.Subject -eq 'CN={host.Trim().Trim('[', ']')}' -and $_.NotAfter -gt (Get-Date).AddDays(30) }} | Sort-Object NotAfter -Descending | Select-Object -First 1");
            sb.AppendLine($"    if (-not $script:Cert) {{ $script:Cert = {CertificateCommand(host, "Cert:\\LocalMachine\\My")} }}");
            sb.AppendLine("}");
            sb.AppendLine("Step 'Bind the certificate to the WinRM HTTPS listener (port 5986)' {");
            sb.AppendLine("    $Thumbprint = $script:Cert.Thumbprint");
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
        if (https)
        {
            sb.AppendLine("    $t = $script:Cert.Thumbprint; $grouped = (0..9 | ForEach-Object { $t.Substring($_ * 4, 4) }) -join ' '");
            sb.AppendLine("    Write-Host ''; Write-Host \"Certificate thumbprint:  $grouped\" -ForegroundColor Yellow");
            sb.AppendLine("    Write-Host 'RemoteHub shows a thumbprint when you click \"Test again\": trust the certificate only if both are the same.'");
        }
        sb.AppendLine("    Write-Host 'The account RemoteHub uses must be a local administrator here or a member of \"Remote Management Users\".'");
        // A policy (security baseline, Intune, GPO) may set it back – or it wasn't chosen: say so instead of failing later with "access denied".
        sb.AppendLine("    if ((Get-ItemProperty -Path $UacKey -Name LocalAccountTokenFilterPolicy -ErrorAction SilentlyContinue).LocalAccountTokenFilterPolicy -ne 1) {");
        sb.AppendLine("        Write-Warning 'Local administrators are restricted over the network here (LocalAccountTokenFilterPolicy is not 1): use a domain account or the RemoteHub Agent - or allow local administrators in RemoteHub''s setup and run it again.'");
        sb.AppendLine("    }");
        if (allowLocalAdmins)
            sb.AppendLine("    Write-Host 'If a policy (security baseline, Intune, GPO) manages \"UAC restrictions for local accounts on network logons\", it may set LocalAccountTokenFilterPolicy back to 0.'");
        sb.AppendLine("    Write-Host 'Now click \"Test again\" in RemoteHub.'");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
