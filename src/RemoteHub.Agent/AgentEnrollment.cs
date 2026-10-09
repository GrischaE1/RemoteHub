using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteHub.Agent;

/// <summary>
/// The per-device enrollment file (<c>&lt;label&gt;.rhagent</c>, compiled into RemoteHub and the agent). RemoteHub creates
/// it; the agent's MSI imports it on the device and deletes it.
/// <para>
/// It is a secret: it holds the device certificate's private key (as a PFX with a random one-time password). It is
/// only moved over encrypted channels (PowerShell remoting, the RDP clipboard) and deleted after the import. Trust is
/// established here, when the file is created – RemoteHub stores the certificate's fingerprint at the same time.
/// </para>
/// </summary>
public sealed class AgentEnrollment
{
    public const string Extension = ".rhagent";
    public const int CurrentFormat = 1;
    public const string CertificateFriendlyName = "RemoteHub Agent";

    public int Format { get; set; } = CurrentFormat;
    public string DeviceId { get; set; } = "";
    public string DeviceLabel { get; set; } = "";
    public int ListenPort { get; set; } = AgentProtocol.DefaultPort;
    public List<string> AllowedGroups { get; set; } = ["S-1-5-32-544"];

    /// <summary>Firewall rule scope, e.g. "10.0.0.0/8,192.168.1.20"; empty = any address.</summary>
    public string FirewallRemoteAddresses { get; set; } = "";

    public string Pfx { get; set; } = "";
    public string PfxPassword { get; set; } = "";
    public DateTime CreatedUtc { get; set; }

    /// <summary>A new device identity: GUID, RSA key, certificate (CN = device ID, server + client auth, 2 years).</summary>
    public static (AgentEnrollment Enrollment, string Fingerprint) Create(string label, int port, IEnumerable<string>? allowedGroups = null,
        string? firewallRemoteAddresses = null, DateTime? now = null)
    {
        var created = now ?? DateTime.UtcNow;
        var deviceId = Guid.NewGuid().ToString();
        using var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN={deviceId}, O=RemoteHub Agent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri("urn:remotehub:device:" + deviceId));
        if (Uri.CheckHostName(label) == UriHostNameType.Dns) san.AddDnsName(label);
        request.CertificateExtensions.Add(san.Build());

        using var cert = request.CreateSelfSigned(created.AddMinutes(-10), created.AddYears(2));
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var enrollment = new AgentEnrollment
        {
            DeviceId = deviceId,
            DeviceLabel = label.Trim(),
            ListenPort = port,
            AllowedGroups = (allowedGroups ?? ["S-1-5-32-544"]).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToList(),
            FirewallRemoteAddresses = (firewallRemoteAddresses ?? "").Trim(),
            Pfx = Convert.ToBase64String(cert.Export(X509ContentType.Pfx, password)),
            PfxPassword = password,
            CreatedUtc = created,
        };
        enrollment.Validate();
        return (enrollment, AgentProtocol.Fingerprint(cert.RawData));
    }

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, AgentEnrollmentJson.Default.AgentEnrollment);

    public static AgentEnrollment Parse(byte[] content)
    {
        var e = JsonSerializer.Deserialize(content, AgentEnrollmentJson.Default.AgentEnrollment) ?? throw new InvalidDataException("The enrollment file is empty.");
        e.Validate();
        return e;
    }

    public void Validate()
    {
        if (Format != CurrentFormat) throw new InvalidDataException($"Enrollment format {Format} is not supported by this agent version.");
        if (!Guid.TryParse(DeviceId, out _)) throw new InvalidDataException("The enrollment has no valid device ID.");
        if (ListenPort is < 1 or > 65535) throw new InvalidDataException($"Port {ListenPort} is not valid.");
        if (AllowedGroups.Count == 0) throw new InvalidDataException("No allowed groups – nobody could connect.");
        if (Pfx.Length == 0) throw new InvalidDataException("The enrollment has no certificate.");
        if (!IsValidAddressList(FirewallRemoteAddresses)) throw new InvalidDataException($"Firewall addresses '{FirewallRemoteAddresses}' are not valid.");
    }

    /// <summary>The certificate inside (public part only), e.g. to show or verify its fingerprint.</summary>
    public X509Certificate2 PublicCertificate()
    {
        using var full = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(Pfx), PfxPassword, X509KeyStorageFlags.EphemeralKeySet);
        return X509CertificateLoader.LoadCertificate(full.RawData);
    }

    /// <summary>"" (any), or a comma list of IPv4/IPv6 addresses, CIDR ranges ("10.0.0.0/8") or ranges ("10.0.0.1-10.0.0.9").</summary>
    public static bool IsValidAddressList(string? list)
    {
        if (string.IsNullOrWhiteSpace(list)) return true;
        foreach (var raw in list.Split(',', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0) return false;
            var dash = raw.Split('-');
            if (dash.Length == 2) { if (!IPAddress.TryParse(dash[0], out _) || !IPAddress.TryParse(dash[1], out _)) return false; continue; }
            var slash = raw.Split('/');
            if (!IPAddress.TryParse(slash[0], out var ip)) return false;
            if (slash.Length == 2 && (!int.TryParse(slash[1], out var bits) || bits < 0 || bits > (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32))) return false;
            if (slash.Length > 2) return false;
        }
        return true;
    }

    /// <summary>A file name for the label: "WKS-07.rhagent".</summary>
    public static string FileName(string label)
    {
        var safe = new string(label.Trim().Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '-' : ch).ToArray()).Trim('-', '.');
        return (safe.Length == 0 ? "device" : safe) + Extension;
    }
}

[JsonSerializable(typeof(AgentEnrollment))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class AgentEnrollmentJson : JsonSerializerContext;
