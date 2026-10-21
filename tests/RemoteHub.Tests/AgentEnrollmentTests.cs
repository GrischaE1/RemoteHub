using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RemoteHub.Agent;
using RemoteHub.Data;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>
/// Enrollment without a device key (SEC-ENR-03, docs/agent-spec.md §5.1, §5.5): the format-2 file, the certificate a
/// device makes for itself, the claim's proof and how RemoteHub judges a claim, and RemoteHub's enrollment secrets.
/// </summary>
public class AgentEnrollmentTests
{
    private static readonly AgentEnrollmentKeys Keys = KeysWithOne(out _);

    private static AgentEnrollmentKeys KeysWithOne(out AgentEnrollmentKey key)
    {
        var keys = new AgentEnrollmentKeys();
        key = keys.EnsureCurrent(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        return keys;
    }

    private static string Fingerprint() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>The claim an agent enrolled with <paramref name="key"/> gives for <paramref name="nonce"/>.</summary>
    private static AgentClaim Claim(AgentEnrollmentKey key, string fingerprint, string nonce, string computer = "WKS-07", string dns = "WKS-07.contoso.test") =>
        EnrollmentProof.Answer(key.Secret, key.Id.ToString(), Guid.NewGuid().ToString(), fingerprint, computer, dns, nonce);

    [Fact]
    public void The_enrollment_file_carries_the_secret_and_the_settings_but_no_key()
    {
        var keys = KeysWithOne(out var key);
        var e = AgentEnrollment.Create(key.Id.ToString(), key.Secret, "WKS-ACCOUNTING-07", 58800, ["S-1-5-32-544", " "], "10.0.0.0/8, 192.168.1.20", virtualDisplay: false);
        var bytes = e.Serialize();
        var parsed = AgentEnrollment.Parse(bytes);
        Assert.Equal(2, parsed.Format);
        Assert.Equal(key.Id.ToString(), parsed.KeyId);
        Assert.Equal(key.Secret, parsed.Secret);
        Assert.Equal(58800, parsed.ListenPort);
        Assert.Equal(["S-1-5-32-544"], parsed.AllowedGroups);
        Assert.Equal("10.0.0.0/8, 192.168.1.20", parsed.FirewallRemoteAddresses);
        Assert.False(parsed.VirtualDisplay);

        var json = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("pfx", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deviceId", json, StringComparison.OrdinalIgnoreCase); // the device makes its own
        Assert.Same(key, keys.Current);
    }

    [Fact]
    public void A_file_from_an_older_RemoteHub_with_a_device_key_is_refused()
    {
        var formatOne = """{"format":1,"deviceId":"7c1f0f3e-8d0e-4f43-9c39-1a4c6f1f1f11","deviceLabel":"dev","listenPort":58732,"allowedGroups":["S-1-5-32-544"],"pfx":"AAAA","pfxPassword":"x"}""";
        var ex = Assert.Throws<InvalidDataException>(() => AgentEnrollment.Parse(Encoding.UTF8.GetBytes(formatOne)));
        Assert.Contains("older RemoteHub", ex.Message);
    }

    [Fact]
    public void Broken_enrollments_are_rejected()
    {
        KeysWithOne(out var key);
        Assert.Throws<InvalidDataException>(() => AgentEnrollment.Parse("{}"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => AgentEnrollment.Parse("not json"u8.ToArray()));
        var e = AgentEnrollment.Create(key.Id.ToString(), key.Secret, "dev", 58732);
        e.Format = 99;
        Assert.Throws<InvalidDataException>(() => AgentEnrollment.Parse(e.Serialize()));
        Assert.Throws<InvalidDataException>(() => AgentEnrollment.Create(key.Id.ToString(), key.Secret, "dev", 58732, firewallRemoteAddresses: "10.0.0.0/33"));
        Assert.Throws<InvalidDataException>(() => AgentEnrollment.Create(key.Id.ToString(), "c2hvcnQ=", "dev", 58732));   // 5 bytes, not 32
        Assert.Throws<InvalidDataException>(() => AgentEnrollment.Create("not-a-guid", key.Secret, "dev", 58732));
    }

    [Fact]
    public void The_device_certificate_has_the_agent_profile()
    {
        using var key = RSA.Create(3072);
        var id = Guid.NewGuid().ToString();
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        using var cert = AgentEnrollment.CreateCertificate(key, id, "WKS-07", now);
        Assert.Equal($"CN={id}, O=RemoteHub Agent", cert.Subject);
        Assert.Equal(now.AddMinutes(-10), cert.NotBefore.ToUniversalTime());
        Assert.Equal(now.AddYears(2), cert.NotAfter.ToUniversalTime());
        Assert.False(cert.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value).ToList();
        Assert.Equal(["1.3.6.1.5.5.7.3.1", "1.3.6.1.5.5.7.3.2"], eku);
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("WKS-07", san.EnumerateDnsNames());
        Assert.Contains($"urn:remotehub:device:{id}", Encoding.ASCII.GetString(san.RawData));

        using var other = RSA.Create(3072);
        using var second = AgentEnrollment.CreateCertificate(other, id, "WKS-07", now);
        Assert.NotEqual(AgentProtocol.Fingerprint(cert.RawData), AgentProtocol.Fingerprint(second.RawData)); // a copied file makes another identity
    }

    [Fact]
    public void The_proof_binds_the_secret_the_certificate_the_device_and_the_nonce()
    {
        KeysWithOne(out var key);
        var nonce = EnrollmentProof.NewNonce();
        var claim = Claim(key, Fingerprint(), nonce);
        Assert.True(EnrollmentProof.Verify(key.Secret, claim, nonce));
        Assert.False(EnrollmentProof.Verify(AgentEnrollment.NewSecret(), claim, nonce));
        Assert.False(EnrollmentProof.Verify(key.Secret, claim, EnrollmentProof.NewNonce()));
        Assert.False(EnrollmentProof.Verify(key.Secret, claim with { Fingerprint = Fingerprint() }, nonce));
        Assert.False(EnrollmentProof.Verify(key.Secret, claim with { DeviceId = Guid.NewGuid().ToString() }, nonce));
        Assert.False(EnrollmentProof.Verify(key.Secret, claim with { ComputerName = "OTHER" }, nonce));
        Assert.False(EnrollmentProof.Verify(key.Secret, claim with { Proof = "not base64!" }, nonce));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("short", false)]
    [InlineData("AAAAAAAAAAAAAAAA", true)]
    [InlineData("abc-DEF_0123456789abcdef", true)]
    [InlineData("has+plus/and/slash=======", false)]
    [InlineData("a%2Fb0123456789012345", false)]
    public void Only_short_base64url_nonces_are_accepted(string nonce, bool valid) => Assert.Equal(valid, EnrollmentProof.IsValidNonce(nonce));

    [Fact]
    public void A_claim_is_pinned_only_for_the_certificate_the_connection_presented()
    {
        var keys = KeysWithOne(out var key);
        var nonce = EnrollmentProof.NewNonce();
        var fp = Fingerprint();
        var claim = Claim(key, fp, nonce);

        var ok = AgentInstaller.EvaluateClaim(claim, fp, nonce, keys, "wks-07.contoso.test");
        Assert.Equal(AgentInstaller.ClaimOutcome.Claimed, ok.Outcome);
        Assert.Equal(fp, ok.Claim!.Fingerprint);

        // Someone in between who has no secret presents their own certificate, but can't make a proof for it.
        Assert.Equal(AgentInstaller.ClaimOutcome.OtherCertificate, AgentInstaller.EvaluateClaim(claim, Fingerprint(), nonce, keys, "wks-07").Outcome);
        Assert.Equal(AgentInstaller.ClaimOutcome.OtherCertificate, AgentInstaller.EvaluateClaim(claim, null, nonce, keys, "wks-07").Outcome);
        Assert.Equal(AgentInstaller.ClaimOutcome.WrongProof, AgentInstaller.EvaluateClaim(claim, fp, EnrollmentProof.NewNonce(), keys, "wks-07").Outcome); // a replayed answer
        Assert.Equal(AgentInstaller.ClaimOutcome.WrongProof, AgentInstaller.EvaluateClaim(null, fp, nonce, keys, "wks-07").Outcome);
    }

    [Fact]
    public void A_claim_with_another_RemoteHubs_or_a_revoked_secret_is_refused()
    {
        var keys = KeysWithOne(out var key);
        var nonce = EnrollmentProof.NewNonce();
        var fp = Fingerprint();
        KeysWithOne(out var foreign);
        Assert.Equal(AgentInstaller.ClaimOutcome.UnknownSecret, AgentInstaller.EvaluateClaim(Claim(foreign, fp, nonce), fp, nonce, keys, "wks-07").Outcome);

        // A forged claim naming this RemoteHub's key ID but signed with another secret.
        var forged = EnrollmentProof.Answer(foreign.Secret, key.Id.ToString(), Guid.NewGuid().ToString(), fp, "WKS-07", "WKS-07", nonce);
        Assert.Equal(AgentInstaller.ClaimOutcome.WrongProof, AgentInstaller.EvaluateClaim(forged, fp, nonce, keys, "wks-07").Outcome);

        keys.Replace(DateTime.UtcNow, revokeEarlier: true);
        Assert.Equal(AgentInstaller.ClaimOutcome.RevokedSecret, AgentInstaller.EvaluateClaim(Claim(key, fp, nonce), fp, nonce, keys, "wks-07").Outcome);
    }

    [Theory]
    [InlineData("wks-07", true)]
    [InlineData("WKS-07.contoso.test", true)]
    [InlineData("wks-07.other.test", true)]     // the first part is the device's name
    [InlineData("10.1.2.3", true)]              // an address can't be checked by name
    [InlineData("[fe80::1]", true)]
    [InlineData("srv-db01", false)]
    [InlineData("srv-db01.contoso.test", false)]
    public void The_device_must_be_the_host_RemoteHub_asked(string host, bool matches)
    {
        var claim = new AgentClaim(Guid.NewGuid().ToString(), Fingerprint(), Guid.NewGuid().ToString(), "WKS-07", "wks-07.contoso.test", "");
        Assert.Equal(matches, AgentInstaller.NameMatches(host, claim));
    }

    [Fact]
    public void A_claim_from_another_device_name_is_refused_automatically()
    {
        var keys = KeysWithOne(out var key);
        var nonce = EnrollmentProof.NewNonce();
        var fp = Fingerprint();
        var r = AgentInstaller.EvaluateClaim(Claim(key, fp, nonce, "SRV-DB01", "srv-db01.contoso.test"), fp, nonce, keys, "wks-07.contoso.test");
        Assert.Equal(AgentInstaller.ClaimOutcome.OtherName, r.Outcome);
        Assert.Contains("SRV-DB01", AgentInstaller.Explain(r, "WKS-07"));
    }

    [Fact]
    public void A_replaced_secret_stays_accepted_until_it_is_revoked()
    {
        var keys = new AgentEnrollmentKeys();
        var now = DateTime.UtcNow;
        var first = keys.EnsureCurrent(now);
        Assert.Same(first, keys.EnsureCurrent(now));                     // made once
        var second = keys.Replace(now, revokeEarlier: false);              // same clock tick: still the newer one
        Assert.Same(second, keys.Current);
        Assert.Equal([first], keys.Earlier);
        Assert.Null(first.RevokedUtc);
        Assert.Equal(1, keys.RevokeEarlier(now));
        Assert.NotNull(first.RevokedUtc);
        Assert.Empty(keys.Earlier);
        Assert.Same(first, keys.Find(first.Id.ToString()));                // still known – so a claim with it says "revoked"
        Assert.Null(keys.Find("nonsense"));
        var third = keys.Replace(now, revokeEarlier: true);
        Assert.NotNull(second.RevokedUtc);
        Assert.Same(third, keys.Current);
    }

    [Fact]
    public void The_enrollment_secret_is_stored_like_a_password()
    {
        var data = new AppData();
        var key = data.AgentEnrollment.EnsureCurrent(DateTime.UtcNow);
        var record = AppDataRecords.From(data).Single(r => r.Id == nameof(AppData.AgentEnrollment));
        Assert.DoesNotContain(key.Secret, record.Data);
        Assert.Contains(record.Secrets, s => s.Item2 == key.Secret);
        Assert.Contains(key.Id.ToString(), record.Data);
    }

    [Fact]
    public void Settings_from_before_the_enrollment_secret_still_load()
    {
        var old = """{"Version":1,"Items":[],"SavedCredentials":[]}""";
        var data = SettingsStore.FromJson(Encoding.UTF8.GetBytes(old));
        Assert.NotNull(data.AgentEnrollment);
        Assert.Null(data.AgentEnrollment.Current);

        data.AgentEnrollment.EnsureCurrent(DateTime.UtcNow);
        var again = SettingsStore.FromJson(SettingsStore.ToJson(data)); // the shared file keeps it, so other PCs can claim too
        Assert.Equal(data.AgentEnrollment.Current!.Secret, again.AgentEnrollment.Current!.Secret);
    }

    [Fact]
    public void The_old_agent_of_a_device_enrolled_again_is_not_pinned()
    {
        var old = Fingerprint();
        var d = new AgentDevice { Status = AgentStatus.NotInstalled, ReplacesFingerprint = old };
        var claim = new AgentClaim(Guid.NewGuid().ToString(), old, Guid.NewGuid().ToString(), "WKS-07", "wks-07", "");
        // until the new install has run, the old agent answers – with the same secret, so the claim itself is valid
        Assert.False(MainWindow.IsNewClaim(d, new(AgentInstaller.ClaimOutcome.Claimed, claim)));
        Assert.False(MainWindow.IsNewClaim(d, new(AgentInstaller.ClaimOutcome.Claimed, claim with { Fingerprint = old.ToLowerInvariant() })));
        Assert.True(MainWindow.IsNewClaim(d, new(AgentInstaller.ClaimOutcome.Claimed, claim with { Fingerprint = Fingerprint() })));
        Assert.False(MainWindow.IsNewClaim(d, new(AgentInstaller.ClaimOutcome.OtherName, claim with { Fingerprint = Fingerprint() })));
        Assert.False(MainWindow.IsNewClaim(d, null));
    }

    [Fact]
    public void A_connection_waiting_for_its_claim_has_no_pin()
    {
        Assert.True(MainWindow.AwaitsClaim(new AgentDevice { Status = AgentStatus.NotInstalled }));
        Assert.False(MainWindow.AwaitsClaim(new AgentDevice { Status = AgentStatus.NotInstalled, Fingerprint = Fingerprint() })); // a format-1 install in progress
        Assert.False(MainWindow.AwaitsClaim(new AgentDevice { Status = AgentStatus.Installed, Fingerprint = Fingerprint() }));
        Assert.False(MainWindow.AwaitsClaim(null));
    }
}
