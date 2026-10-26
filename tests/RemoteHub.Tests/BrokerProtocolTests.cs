using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RemoteHub.Agent;
using static RemoteHub.Agent.BrokerProtocol;

namespace RemoteHub.Tests;

/// <summary>
/// The broker's wire contract on this side: encodings, derived keys, MACs, seals, canonical JSON, secrets, proofs and
/// keyrings. The fixed values are the broker's test vectors – the broker's
/// own tests check the same ones, so a seal made here checks there and back.
/// </summary>
public class BrokerProtocolTests
{
    private const string Org = "o_aaaaaaaaaaaaaaaa";
    private static readonly byte[] TestSecret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Base64url_has_one_spelling_and_refuses_the_others()
    {
        Assert.Equal("-_8AEA", B64u([0xfb, 0xff, 0x00, 0x10]));
        Assert.Equal(new byte[] { 0xfb, 0xff, 0x00, 0x10 }, FromB64u("-_8AEA"));
        Assert.Null(FromB64u("-_8AEA==")); // padding
        Assert.Null(FromB64u("+/8AEA")); // standard alphabet
        Assert.Null(FromB64u("-_8AEB")); // unused bits set
        Assert.Null(FromB64u("A")); // impossible length
    }

    [Fact]
    public void Base32_uses_the_lower_case_alphabet_without_padding() =>
        Assert.Equal("mzxw6ytboi", B32("foobar"u8));

    [Fact]
    public void Derived_keys_match_the_documented_vectors()
    {
        Assert.Equal("p-KvhM8ZobVAIpE3nPHc2iCzLGnoPpbsQ_Zgb7mVYWI", B64u(DeriveKey(TestSecret, Org, KeyLabel.Enroll)));
        Assert.Equal("S_IQjOUjBaH0tXaNws0ygroPyXdhX2QzZ15Ntub9JVU", B64u(DeriveKey(TestSecret, Org, KeyLabel.Record)));
        Assert.Equal("LqDxw3Vg-4ekDMi6UgRzJ1ucz0p5MvaSCkO0zvDncQ8", B64u(DeriveKey(TestSecret, Org, KeyLabel.Join)));
        Assert.Equal("agHlfrHspM3Zk0bcNd303v6ZZ3Pal5T9W4FYQ73P3Ok", B64u(DeriveKey(TestSecret, Org, KeyLabel.Layout)));
        Assert.Equal("V8NwnsYlo53Fpc2QKSgaiVBi6f-8lY1RnNvP7Qs0a0M", B64u(DeriveKey(TestSecret, Org, KeyLabel.Keyring)));
    }

    [Fact]
    public void A_device_record_is_sealed_like_the_broker_seals_it()
    {
        var record = new JsonObject
        {
            ["deviceId"] = "3f1a9e2c-7b44-4b2a-9e21-0c6a2f9b7e10",
            ["fingerprint"] = string.Concat(Enumerable.Repeat("AB", 32)),
            ["computerName"] = "PC-0815",
            ["domain"] = "contoso.local",
            ["registered"] = 1790000000,
        };
        var key = DeriveKey(TestSecret, Org, KeyLabel.Record);
        var seal = Seal(key, Org, "device", record);
        Assert.Equal("yKkqub3rFuocZrgfS6T0f1Y2mzg4HnUKap1X-axjkBo", seal);
        Assert.True(VerifySeal(key, seal, Org, "device", record));
        record["computerName"] = "DC01";
        Assert.False(VerifySeal(key, seal, Org, "device", record));
        Assert.False(VerifySeal(key, seal, "o_bbbbbbbbbbbbbbbb", "device", JsonNode.Parse(record.ToJsonString())));
    }

    [Fact]
    public void The_enroll_mac_matches_the_documented_vector() =>
        Assert.Equal("pEvvn8W0OFIMj3ocfrzLTjd28ibkmiLVixZhqQAdqRM",
            Mac(DeriveKey(TestSecret, Org, KeyLabel.Enroll), Org, "enroll", "3f1a9e2c-7b44-4b2a-9e21-0c6a2f9b7e10", "keyhash", "nonce"));

    [Fact]
    public void Canonical_JSON_sorts_keys_ordinally_and_escapes_like_JSON_stringify()
    {
        var node = JsonNode.Parse("""{"b":1,"a":[true,null,"x"],"é":"ü\n\"","A":{"z":0,"y":-1}}""");
        Assert.Equal("""{"A":{"y":-1,"z":0},"a":[true,null,"x"],"b":1,"é":"ü\n\""}""", Canonical(node));
        Assert.Equal("\"a\\u0001\\ud800b\"", Canonical(JsonValue.Create("a\u0001\ud800b")));
        Assert.Throws<FormatException>(() => Canonical(JsonNode.Parse("""{"x":1.5}""")));
        Assert.Throws<FormatException>(() => Canonical(JsonNode.Parse("""{"x":9007199254740992}""")));
    }

    [Fact]
    public void Secrets_are_parsed_and_written_in_their_documented_form()
    {
        const string text = "rhd1.o_aaaaaaaaaaaaaaaa.s_abcdefgh.AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";
        var s = ParseSecret(text)!;
        Assert.Equal((SecretKind.Device, Org, "s_abcdefgh"), (s.Kind, s.Org, s.Id));
        Assert.Equal(TestSecret, s.Key);
        Assert.Equal(text, s.ToString());
        Assert.Null(ParseSecret(text.Replace("rhd1", "rhx1")));
        Assert.Null(ParseSecret(text[..^2]));
        Assert.Null(ParseSecret("rhc1.o_TOOSHORT.s_abcdefgh.AAAA"));
    }

    [Fact]
    public void Proofs_sign_and_verify_with_both_algorithms()
    {
        var text = ProofText(Org, "console", NewId(), "nonce", "rh.contoso.com");
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = ec.ExportSubjectPublicKeyInfo();
        var sig = SignEs256(ec, text);
        Assert.Equal(64, FromB64u(sig)!.Length); // IEEE P1363 r||s, as WebCrypto expects
        Assert.True(VerifyEs256(spki, text, sig));
        Assert.False(VerifyEs256(spki, text + "x", sig));
        Assert.False(VerifyEs256(spki, text, "not-base64url!"));

        using var rsa = RSA.Create(3072); // the agent's device key
        var ps = FromB64u(SignPs256(rsa, text))!;
        Assert.True(rsa.VerifyData(System.Text.Encoding.UTF8.GetBytes(text), ps, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        Assert.Equal(43, KeyHash(spki).Length);
    }

    [Fact]
    public void A_keyring_opens_only_for_its_organisation_and_console_secret()
    {
        var key = DeriveKey(TestSecret, Org, KeyLabel.Keyring);
        var blob = MakeKeyring(key, Org, "s_abcdefgh", ["rhd1.o_aaaaaaaaaaaaaaaa.s_abcdefgh.AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8"]);
        Assert.Single(OpenKeyring(key, Org, "s_abcdefgh", blob)!);
        Assert.Null(OpenKeyring(key, Org, "s_other234", blob));
        Assert.Null(OpenKeyring(DeriveKey(TestSecret, Org, KeyLabel.Layout), Org, "s_abcdefgh", blob));
        Assert.Null(OpenKeyring(key, Org, "s_abcdefgh", "garbage"));
    }

    [Fact]
    public void Identifiers_are_checked_by_their_exact_form()
    {
        Assert.True(IsOrgId("o_k7q2xmd4tpa3fhwz"));
        Assert.False(IsOrgId("o_K7Q29XMD4TPA3FHW"));
        Assert.False(IsOrgId("o_k7q29xmd4tpa3fh1")); // 1 isn't in the alphabet
        Assert.True(IsId(NewId()));
        Assert.False(IsId(Guid.NewGuid().ToString("D").ToUpperInvariant()));
    }

    /// <summary>The keyring as the broker's CLI makes it: iv ‖ AES-256-GCM(ciphertext ‖ tag).</summary>
    private static string MakeKeyring(byte[] key, string org, string secretId, string[] secrets)
    {
        var plain = System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["v"] = 1, ["deviceSecrets"] = new JsonArray([.. secrets.Select(s => JsonValue.Create(s))]) }.ToJsonString());
        var iv = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(iv, plain, ct, tag, System.Text.Encoding.UTF8.GetBytes($"remotehub-broker/1|{org}|keyring|{secretId}"));
        return B64u([.. iv, .. ct, .. tag]);
    }
}
