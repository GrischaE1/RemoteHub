using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RemoteHub.Agent;

/// <summary>
/// The RemoteHub Broker's wire contract on this side (this file is compiled into both RemoteHub and the agent):
/// encodings, the keys derived from an organisation's secrets, MACs, seals and proofs. It matches the broker's own
/// implementation byte for byte – <c>docs/broker-protocol.md</c> §1–§4 – and the test vectors there are checked in
/// <c>BrokerProtocolTests</c>.
/// <para>
/// The broker never holds what it would need to forge a device: it gets the enroll and join keys only; record, layout
/// and keyring keys stay with devices and consoles. So everything that decides where credentials go is sealed here and
/// checked here, and the broker only stores it.
/// </para>
/// </summary>
public static class BrokerProtocol
{
    public const string Protocol = "remotehub-broker/1";

    /// <summary>Close codes the broker uses (broker-protocol.md §9).</summary>
    public static class Close
    {
        public const int BadMessage = 4400, AuthFailed = 4401, Refused = 4403, Unknown = 4404, Timeout = 4408,
            Replaced = 4409, OrgDeleted = 4410, RateLimited = 4429;
    }

    public const int MaxMessageBytes = 64 * 1024;
    public const int MaxCandidates = 8;
    public const int OfferLifetimeSeconds = 5 * 60;
    public const int ClockSkewSeconds = 2 * 60;
    public const int KeepAliveDefault = 60, KeepAliveMin = 30, KeepAliveMax = 90;

    // ---- Encodings (§1) -------------------------------------------------------------------------------------------

    public static string B64u(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    /// <summary>Base64url without padding, the one canonical spelling only; null for anything else.</summary>
    public static byte[]? FromB64u(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length % 4 == 1) return text == "" ? [] : null;
        foreach (var c in text)
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return null;
        byte[] bytes;
        try { bytes = Base64Url.DecodeFromChars(text); }
        catch (FormatException) { return null; }
        return B64u(bytes) == text ? bytes : null;
    }

    private const string B32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string B32(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        int bits = 0, value = 0;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(B32Alphabet[(value >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(B32Alphabet[(value << (5 - bits)) & 31]);
        return sb.ToString();
    }

    // ---- Identifiers (§2) -----------------------------------------------------------------------------------------

    public static bool IsOrgId(string? s) => s is { Length: 18 } && s.StartsWith("o_", StringComparison.Ordinal) && s[2..].All(IsB32Char);
    public static bool IsSecretId(string? s) => s is { Length: 10 } && s.StartsWith("s_", StringComparison.Ordinal) && s[2..].All(IsB32Char);
    private static bool IsB32Char(char c) => c is >= 'a' and <= 'z' or >= '2' and <= '7';

    /// <summary>Lower-case GUID, the form device, console, folder and target ids take on the wire.</summary>
    public static bool IsId(string? s) => s is { Length: 36 } && Guid.TryParseExact(s, "D", out _) && s == s.ToLowerInvariant();

    public static string NewId() => Guid.NewGuid().ToString("D");

    /// <summary>
    /// A broker's address: <c>https://rh.contoso.com</c>, or just the name as the MSI and the policy take it. http is
    /// accepted for a test lab's local broker – its signalling is then readable, while pins and seals still hold.
    /// </summary>
    public static Uri? ParseBrokerUrl(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length > 0 && !t.Contains("://", StringComparison.Ordinal)) t = "https://" + t;
        return Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" && u.AbsolutePath == "/" && u.Query.Length == 0 && u.UserInfo.Length == 0
            ? u : null;
    }

    // ---- Secrets (§3.1) -------------------------------------------------------------------------------------------

    public enum SecretKind { Device, Console }

    public sealed record Secret(SecretKind Kind, string Org, string Id, byte[] Key)
    {
        public override string ToString() => $"{(Kind == SecretKind.Device ? "rhd1" : "rhc1")}.{Org}.{Id}.{B64u(Key)}";
    }

    /// <summary><c>rhd1.&lt;org&gt;.&lt;secret id&gt;.&lt;key&gt;</c> or <c>rhc1.…</c>; null for anything else.</summary>
    public static Secret? ParseSecret(string? text)
    {
        var parts = (text ?? "").Trim().Split('.');
        if (parts.Length != 4) return null;
        SecretKind? kind = parts[0] switch { "rhd1" => SecretKind.Device, "rhc1" => SecretKind.Console, _ => null };
        var key = FromB64u(parts[3]);
        if (kind is null || !IsOrgId(parts[1]) || !IsSecretId(parts[2]) || key is not { Length: 32 }) return null;
        return new Secret(kind.Value, parts[1], parts[2], key);
    }

    // ---- Keys and MACs (§3.2, §3.3) -------------------------------------------------------------------------------

    public enum KeyLabel { Enroll, Record, Join, Layout, Keyring }

    /// <summary>HKDF-SHA256(secret, salt "remotehub-broker/1|&lt;org&gt;", info = label), 32 bytes.</summary>
    public static byte[] DeriveKey(byte[] secret, string org, KeyLabel label) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, Encoding.UTF8.GetBytes($"{Protocol}|{org}"),
            Encoding.UTF8.GetBytes(label.ToString().ToLowerInvariant()));

    /// <summary>MAC over <c>remotehub-broker/1|&lt;org&gt;|&lt;parts joined by |&gt;</c>.</summary>
    public static string Mac(byte[] key, string org, params string[] parts) =>
        B64u(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(string.Join('|', [Protocol, org, .. parts]))));

    public static bool VerifyMac(byte[] key, string? expected, string org, params string[] parts) =>
        expected is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(key, org, parts)), Encoding.ASCII.GetBytes(expected));

    // ---- Seals and canonical JSON (§4) ----------------------------------------------------------------------------

    public static string Seal(byte[] key, string org, string kind, JsonNode body) => Mac(key, org, "seal", kind, Canonical(body));

    public static bool VerifySeal(byte[] key, string? expected, string org, string kind, JsonNode? body)
    {
        if (body is null) return false;
        try { return VerifyMac(key, expected, org, "seal", kind, Canonical(body)); }
        catch (FormatException) { return false; } // a body that can't be sealed can't carry a valid seal
    }

    /// <summary>
    /// Canonical JSON as the broker writes it (ECMAScript JSON.stringify for strings, keys in ordinal UTF-16 order,
    /// integers only). Throws <see cref="FormatException"/> for fractions and values outside the safe integer range,
    /// which JavaScript couldn't round-trip.
    /// </summary>
    public static string Canonical(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }

    private const long MaxSafeInteger = 9_007_199_254_740_991;

    private static void Write(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var key in o.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, key);
                    sb.Append(':');
                    Write(sb, o[key]);
                }
                sb.Append('}');
                return;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(sb, a[i]);
                }
                sb.Append(']');
                return;
            case JsonValue v:
                if (v.TryGetValue<string>(out var s)) WriteString(sb, s);
                else if (v.TryGetValue<bool>(out var b)) sb.Append(b ? "true" : "false");
                else if (v.TryGetValue<long>(out var l) && Math.Abs(l) <= MaxSafeInteger) sb.Append(l.ToString(System.Globalization.CultureInfo.InvariantCulture));
                else if (v.TryGetValue<int>(out var n)) sb.Append(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                else throw new FormatException("Only strings, booleans, null and safe integers can be sealed.");
                return;
        }
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    var lone = char.IsHighSurrogate(c) ? !(i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                        : char.IsLowSurrogate(c) && !(i > 0 && char.IsHighSurrogate(s[i - 1]));
                    if (c < 0x20 || lone) sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    // ---- Proofs and signatures (§3.5) ----------------------------------------------------------------------------

    public static string ProofText(string org, string role, string id, string nonce, string host) =>
        string.Join('|', Protocol, org, role, id, nonce, host);

    /// <summary>SHA-256 of a public key's SPKI DER, base64url – how registrations and offers name a key.</summary>
    public static string KeyHash(byte[] spki) => B64u(SHA256.HashData(spki));

    /// <summary>PS256: RSASSA-PSS with SHA-256, salt 32 (.NET's PSS uses the hash length). Works with a non-exportable CNG key.</summary>
    public static string SignPs256(RSA key, string text) =>
        B64u(key.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));

    /// <summary>ES256: ECDSA P-256 with SHA-256, signature in IEEE P1363 form (r‖s) – .NET's default.</summary>
    public static string SignEs256(ECDsa key, string text) =>
        B64u(key.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256));

    public static bool VerifyEs256(byte[] spki, string text, string? signature)
    {
        var sig = FromB64u(signature);
        if (sig is null) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(text), sig, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException) { return false; }
    }

    // ---- Keyring (§3.4) -------------------------------------------------------------------------------------------

    /// <summary>Opens a keyring blob with the console secret's keyring key; the device secrets in it, or null if it isn't this one.</summary>
    public static string[]? OpenKeyring(byte[] keyringKey, string org, string consoleSecretId, string? blob)
    {
        var raw = FromB64u(blob);
        if (raw is null || raw.Length < 12 + 16) return null;
        var plain = new byte[raw.Length - 12 - 16];
        try
        {
            using var aes = new AesGcm(keyringKey, 16);
            aes.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(12, plain.Length), raw.AsSpan(raw.Length - 16), plain,
                Encoding.UTF8.GetBytes($"{Protocol}|{org}|keyring|{consoleSecretId}"));
            var list = JsonNode.Parse(plain)?["deviceSecrets"]?.AsArray();
            return list?.Select(n => n?.GetValue<string>() ?? "").Where(s => ParseSecret(s) is not null).ToArray();
        }
        catch (Exception e) when (e is CryptographicException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
