using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteHub.Agent;

/// <summary>
/// The enrollment file (<c>&lt;label&gt;.rhagent</c>, compiled into RemoteHub and the agent), format 2. RemoteHub writes it;
/// the agent's MSI reads it on the device and deletes it.
/// <para>
/// It holds no key (docs/agent-spec.md §5.1, SEC-ENR-03): the device makes its own key and certificate when it is
/// enrolled, and RemoteHub learns and pins the certificate's fingerprint by a claim (§5.5). The file carries
/// RemoteHub's enrollment secret, with which the agent proves that it was enrolled from this RemoteHub's file, and the
/// settings for <c>agent.json</c>. The same secret serves every device and every way of installing (MDM, software
/// distribution, by hand); copying the file can't copy a device's identity, because every device that uses it makes
/// its own key. It is still kept out of reach: whoever holds it could enroll a device of their own.
/// </para>
/// </summary>
public sealed class AgentEnrollment
{
    public const string Extension = ".rhagent";
    public const int CurrentFormat = 2;
    public const string CertificateFriendlyName = "RemoteHub Agent";

    public int Format { get; set; } = CurrentFormat;

    /// <summary>Which of RemoteHub's enrollment secrets this is (so a replaced one can be refused).</summary>
    public string KeyId { get; set; } = "";

    /// <summary>The enrollment secret, 32 random bytes as base64.</summary>
    public string Secret { get; set; } = "";

    /// <summary>The device's name in RemoteHub; empty = the device's computer name.</summary>
    public string DeviceLabel { get; set; } = "";
    public int ListenPort { get; set; } = AgentProtocol.DefaultPort;
    public List<string> AllowedGroups { get; set; } = ["S-1-5-32-544"];

    /// <summary>Firewall rule scope, e.g. "10.0.0.0/8,192.168.1.20"; empty = any address.</summary>
    public string FirewallRemoteAddresses { get; set; } = "";

    /// <summary>Install the virtual monitor for devices without one (null: not chosen – the device keeps its setting, else on).</summary>
    public bool? VirtualDisplay { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>A new enrollment secret: 32 random bytes, base64.</summary>
    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>An enrollment file for the secret <paramref name="keyId"/>/<paramref name="secret"/> with these settings.</summary>
    public static AgentEnrollment Create(string keyId, string secret, string label, int port, IEnumerable<string>? allowedGroups = null,
        string? firewallRemoteAddresses = null, DateTime? now = null, bool? virtualDisplay = null)
    {
        var enrollment = new AgentEnrollment
        {
            KeyId = keyId,
            Secret = secret,
            DeviceLabel = label.Trim(),
            ListenPort = port,
            AllowedGroups = (allowedGroups ?? ["S-1-5-32-544"]).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToList(),
            FirewallRemoteAddresses = (firewallRemoteAddresses ?? "").Trim(),
            VirtualDisplay = virtualDisplay,
            CreatedUtc = now ?? DateTime.UtcNow,
        };
        enrollment.Validate();
        return enrollment;
    }

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, AgentEnrollmentJson.Default.AgentEnrollment);

    public static AgentEnrollment Parse(byte[] content)
    {
        AgentEnrollment? e;
        try { e = JsonSerializer.Deserialize(content, AgentEnrollmentJson.Default.AgentEnrollment); }
        catch (JsonException ex) { throw new InvalidDataException("The enrollment file can't be read: " + ex.Message, ex); }
        if (e is null) throw new InvalidDataException("The enrollment file is empty.");
        e.Validate();
        return e;
    }

    public void Validate()
    {
        if (Format == 1)
            throw new InvalidDataException("This enrollment file was made by an older RemoteHub and holds a device key, which this agent no longer " +
                                           "accepts. Save a new enrollment file in RemoteHub and use that one.");
        if (Format != CurrentFormat) throw new InvalidDataException($"Enrollment format {Format} is not supported by this agent version.");
        if (!Guid.TryParse(KeyId, out _)) throw new InvalidDataException("The enrollment has no valid key ID.");
        if (!IsValidSecret(Secret)) throw new InvalidDataException("The enrollment has no valid secret.");
        if (ListenPort is < 1 or > 65535) throw new InvalidDataException($"Port {ListenPort} is not valid.");
        if (AllowedGroups.Count == 0) throw new InvalidDataException("No allowed groups – nobody could connect.");
        if (!IsValidAddressList(FirewallRemoteAddresses)) throw new InvalidDataException($"Firewall addresses '{FirewallRemoteAddresses}' are not valid.");
    }

    /// <summary>32 bytes as base64.</summary>
    public static bool IsValidSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return false;
        var buffer = new byte[48];
        return Convert.TryFromBase64String(secret, buffer, out var length) && length == 32;
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

    /// <summary>
    /// The device's own certificate for <paramref name="key"/>: CN = device ID, O = RemoteHub Agent; SAN
    /// <c>urn:remotehub:device:&lt;id&gt;</c> (plus the label as DNS name when it is one); not a CA; server and client
    /// authentication; valid from 10 minutes before <paramref name="now"/> for 2 years.
    /// </summary>
    public static X509Certificate2 CreateCertificate(RSA key, string deviceId, string label, DateTime now)
    {
        var request = new CertificateRequest($"CN={deviceId}, O=RemoteHub Agent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri("urn:remotehub:device:" + deviceId));
        if (Uri.CheckHostName(label) == UriHostNameType.Dns) san.AddDnsName(label);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(now.AddMinutes(-10), now.AddYears(2));
    }
}

/// <summary>
/// GET /v1/enrollment?nonce=… – the claim (docs/agent-spec.md §5.5). No credentials: the agent says which device it is,
/// the fingerprint of its certificate and its computer name, and proves with <see cref="Proof"/> that it was enrolled
/// with the secret <see cref="KeyId"/>. RemoteHub pins the fingerprint only when it is the certificate this very TLS
/// connection presented and the proof checks out – nobody without the secret can make one for another certificate.
/// </summary>
public sealed record AgentClaim(string DeviceId, string Fingerprint, string KeyId, string ComputerName, string DnsHostName, string Proof);

/// <summary>The proof of a claim: HMAC-SHA256 with the enrollment secret over everything the claim states and the caller's nonce.</summary>
public static class EnrollmentProof
{
    public const string Path = "/v1/enrollment";

    /// <summary>A nonce of the caller: 32 random bytes, base64url (43 characters).</summary>
    public static string NewNonce() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Nonces the agent accepts: 16 to 64 base64url characters (anything else is refused before any work).</summary>
    public static bool IsValidNonce(string? nonce) =>
        nonce is { Length: >= 16 and <= 64 } && nonce.All(ch => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    public static string Compute(string secret, string keyId, string deviceId, string fingerprint, string computerName, string dnsHostName, string nonce)
    {
        var message = string.Join('\n', "RemoteHub enrollment 2", keyId, deviceId, fingerprint, computerName, dnsHostName, nonce);
        return Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(secret), Encoding.UTF8.GetBytes(message)));
    }

    public static AgentClaim Answer(string secret, string keyId, string deviceId, string fingerprint, string computerName, string dnsHostName, string nonce) =>
        new(deviceId, fingerprint, keyId, computerName, dnsHostName, Compute(secret, keyId, deviceId, fingerprint, computerName, dnsHostName, nonce));

    /// <summary>The proof matches (fixed-time comparison). A malformed proof or secret is a mismatch, not an error.</summary>
    public static bool Verify(string secret, AgentClaim claim, string nonce)
    {
        try
        {
            var expected = Convert.FromBase64String(Compute(secret, claim.KeyId, claim.DeviceId, claim.Fingerprint, claim.ComputerName, claim.DnsHostName, nonce));
            var given = Convert.FromBase64String(claim.Proof);
            return CryptographicOperations.FixedTimeEquals(expected, given);
        }
        catch (FormatException) { return false; }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

[JsonSerializable(typeof(AgentEnrollment))]
[JsonSerializable(typeof(AgentClaim))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class AgentEnrollmentJson : JsonSerializerContext;
