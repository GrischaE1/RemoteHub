using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RemoteHub.Models;
using RemoteHub.Remoting.Shares;
using RemoteHub.Services;
using Renci.SshNet;

namespace RemoteHub.Tests;

/// <summary>Private keys for SSH and SFTP: reading key files, what they are, signing in with them, and storing them sealed.</summary>
[Collection("SettingsStore")]
public class SshKeyTests : IDisposable
{
    private static readonly string KeyGen = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe");
    // Test passwords and passphrases are put together at run time, so no literal reads like a secret.
    private const string Marker = "Ssh-plain-text-marker";
    private static string TestSecret(string name) => string.Concat(name, "-", Marker);
    private static Secret Stored(string name) => Secret.From(TestSecret(name))!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rh-sshkey-" + Guid.NewGuid().ToString("N")[..8]);

    public SshKeyTests()
    {
        Directory.CreateDirectory(_dir);
        StoreReset.Clean();
    }

    public void Dispose()
    {
        StoreReset.Clean();
        try { Directory.Delete(_dir, true); } catch { /* still in use */ }
    }

    private static Secret RsaPem() { using var rsa = RSA.Create(2048); return Secret.From(rsa.ExportRSAPrivateKeyPem())!; }

    [Theory]
    [InlineData("SSH-2.0-OpenSSH_for_Windows_9.5", "OpenSSH_for_Windows_9.5")]
    [InlineData("SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13", "OpenSSH_9.6p1 Ubuntu-3ubuntu13")]
    [InlineData("OpenSSH_9.5", "OpenSSH_9.5")]
    [InlineData(null, "")]
    public void The_tab_and_the_share_name_the_server_software_without_the_protocol_part(string? identification, string shown) =>
        Assert.Equal(shown, RemoteHub.Remoting.SshShell.Software(identification));

    private string File(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    /// <summary>An OpenSSH key made by Windows' ssh-keygen, and the fingerprint ssh-keygen gives for it.</summary>
    private (string Path, string Fingerprint) OpenSshKey(string type, string passphrase)
    {
        var path = Path.Combine(_dir, "id_" + type);
        Run(KeyGen, $"-q -t {type} -N \"{passphrase}\" -C rh-test -f \"{path}\"");
        var line = Run(KeyGen, $"-l -E sha256 -f \"{path}.pub\""); // "256 SHA256:… rh-test (ED25519)"
        return (path, line.Split(' ')[1]);
    }

    private static string Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
        var output = p.StandardOutput.ReadToEndAsync();
        Assert.True(p.WaitForExit(30_000), "ssh-keygen didn't finish");
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
        return output.Result.Trim();
    }

    // ------------------------------------------------------------------ reading and describing

    [SkippableFact]
    public void An_openssh_key_is_described_with_the_fingerprint_ssh_keygen_gives()
    {
        Skip.IfNot(System.IO.File.Exists(KeyGen), "Windows' OpenSSH client (ssh-keygen) isn't installed");
        var (path, fingerprint) = OpenSshKey("ed25519", "");
        var key = SshSignIn.ReadKeyFile(path);
        var info = SshSignIn.Describe(key, null);
        Assert.Equal("ssh-ed25519", info.Type);
        Assert.Equal(fingerprint, info.Fingerprint);
        Assert.False(info.Encrypted);
        Assert.DoesNotContain("PRIVATE", info.Summary);
        Assert.False(SshSignIn.NeedsPassphrase(key, null));
    }

    [SkippableFact]
    public void A_protected_key_needs_its_passphrase_and_a_wrong_one_is_said_as_such()
    {
        Skip.IfNot(System.IO.File.Exists(KeyGen), "Windows' OpenSSH client (ssh-keygen) isn't installed");
        var (path, fingerprint) = OpenSshKey("ed25519", TestSecret("Phrase"));
        var key = SshSignIn.ReadKeyFile(path);
        Assert.True(SshSignIn.NeedsPassphrase(key, null));
        Assert.False(SshSignIn.NeedsPassphrase(key, Stored("Other")));
        var missing = Assert.Throws<SshKeyException>(() => SshSignIn.Describe(key, null));
        Assert.True(missing.NeedsPassphrase);
        Assert.Equal(SshSignIn.WrongPassphrase, Assert.Throws<SshKeyException>(() => SshSignIn.Describe(key, Stored("Wrong"))).Message);
        var info = SshSignIn.Describe(key, Stored("Phrase"));
        Assert.Equal(fingerprint, info.Fingerprint);
        Assert.True(info.Encrypted);
    }

    [Fact]
    public void A_pem_key_is_read_too()
    {
        var info = SshSignIn.Describe(SshSignIn.ReadKeyFile(File("key.pem", RsaPem().Reveal())), null);
        Assert.Equal("ssh-rsa", info.Type);
        Assert.Equal(2048, info.Bits);
        Assert.StartsWith("SHA256:", info.Fingerprint);
        Assert.DoesNotContain("=", info.Fingerprint); // unpadded, as ssh-keygen writes it
    }

    [Fact]
    public void Files_that_arent_private_keys_are_refused_without_quoting_them()
    {
        const string publicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl rh-test";
        var pub = Assert.Throws<SshKeyException>(() => SshSignIn.Describe(SshSignIn.ReadKeyFile(File("id.pub", publicKey)), null));
        Assert.Equal(SshSignIn.NotAKey, pub.Message);
        Assert.DoesNotContain("AAAA", pub.Message);
        System.IO.File.WriteAllBytes(Path.Combine(_dir, "key.pfx"), [0x30, 0x82, 0x00, 0x00, 0x02]);
        Assert.Throws<SshKeyException>(() => SshSignIn.ReadKeyFile(Path.Combine(_dir, "key.pfx")));
        Assert.Throws<SshKeyException>(() => SshSignIn.ReadKeyFile(File("empty", "")));
        Assert.Throws<SshKeyException>(() => SshSignIn.ReadKeyFile(File("big", new string('A', SshSignIn.MaxKeyFileBytes + 1))));
    }

    // ------------------------------------------------------------------ signing in

    [Fact]
    public void Sign_in_tries_the_key_first_and_the_password_only_when_there_is_one()
    {
        var key = RsaPem();
        var (keyOnly, k1) = SshSignIn.Connection("web01", 22, new CredentialSet { Username = "deploy", PrivateKey = key }, TimeSpan.FromSeconds(5));
        Assert.Equal(["publickey"], keyOnly.AuthenticationMethods.Select(m => m.Name));
        var (both, k2) = SshSignIn.Connection("web01", 22, new CredentialSet { Username = "deploy", PrivateKey = key, Password = Stored("Password") }, TimeSpan.FromSeconds(5));
        Assert.Equal(["publickey", "password", "keyboard-interactive"], both.AuthenticationMethods.Select(m => m.Name));
        var (password, k3) = SshSignIn.Connection("web01", 22, new CredentialSet { Username = "deploy", Password = Stored("Password") }, TimeSpan.FromSeconds(5));
        Assert.Equal(["password", "keyboard-interactive"], password.AuthenticationMethods.Select(m => m.Name));
        Assert.Null(k3);
        k1?.Dispose();
        k2?.Dispose();

        Assert.Contains("authorized_keys", SshSignIn.Refused(new CredentialSet { Username = "u", PrivateKey = key }));
        Assert.Contains("neither", SshSignIn.Refused(new CredentialSet { Username = "u", PrivateKey = key, Password = Stored("Password") }));
        Assert.Contains("password is incorrect", SshSignIn.Refused(new CredentialSet { Username = "u", Password = Stored("Password") }));
    }

    [Fact]
    public void A_key_that_cant_be_used_stops_before_connecting()
    {
        var damaged = Secret.From("-----BEGIN OPENSSH PRIVATE KEY-----\nnot base64\n-----END OPENSSH PRIVATE KEY-----")!;
        var ex = Assert.Throws<SshKeyException>(() => SshSignIn.Connection("web01", 22, new CredentialSet { Username = "u", PrivateKey = damaged }, TimeSpan.FromSeconds(5)));
        Assert.Equal(SshSignIn.NotAKey, ex.Message);
    }

    // ------------------------------------------------------------------ storage

    [Fact]
    public void Keys_and_passphrases_are_stored_sealed_and_never_in_the_records()
    {
        var key = RsaPem();
        var data = new AppData();
        var ssh = new NetworkShare
        {
            Name = "web01", Protocol = ShareProtocol.Ssh, Host = "web01.lab", InheritCredentials = false, TrustedFingerprint = "SHA256:abc",
            Credentials = { Username = "deploy", PrivateKey = key, KeyPassphrase = Stored("KeyPhrase") },
        };
        data.Items.Add(ssh);
        data.SavedCredentials.Add(new SavedCredential { Name = "deploy key", Username = "deploy", PrivateKey = key });
        SettingsStore.RelinkParents(data.Items, null);
        _ = SettingsStore.Load(); // like a start: reads the (clean) local config
        SettingsStore.Save(data);
        Data.LocalDb.Use((c, _) =>
        {
            foreach (var table in new[] { "nodes", "credentials", "settings" })
                Assert.Equal(0L, Data.LocalDb.Scalar(c, $"SELECT count(*) FROM {table} WHERE data LIKE '%PRIVATE KEY%' OR data LIKE '%PrivateKey%' OR data LIKE '%{Marker}%' OR data LIKE '%secret:%'"));
            Assert.Equal(3L, Data.LocalDb.Scalar(c, "SELECT count(*) FROM secrets WHERE path IN ('Credentials/PrivateKey', 'Credentials/KeyPassphrase', 'PrivateKey')"));
        });
        Data.LocalDb.Close();

        var back = SettingsStore.Load();
        var entry = Assert.IsType<NetworkShare>(back.Items.Single());
        Assert.Equal(ShareProtocol.Ssh, entry.Protocol);
        Assert.Equal("ssh://web01.lab", entry.Address);
        Assert.Equal(key.Reveal(), entry.Credentials.PrivateKey?.Reveal());
        Assert.Equal(TestSecret("KeyPhrase"), entry.Credentials.KeyPassphrase?.Reveal());
        Assert.Equal(key.Reveal(), back.SavedCredentials.Single().PrivateKey?.Reveal());
        Assert.Null(back.SavedCredentials.Single().KeyPassphrase);
    }

    [Fact]
    public void Removing_stored_passwords_takes_passphrases_too_but_keeps_the_keys()
    {
        var key = RsaPem();
        var data = new AppData();
        var ssh = new NetworkShare { Name = "web01", Protocol = ShareProtocol.Ssh, Host = "web01", InheritCredentials = false,
            Credentials = { Username = "deploy", PrivateKey = key, KeyPassphrase = Stored("P1"), Password = Stored("P2") } };
        data.SavedCredentials.Add(new SavedCredential { Name = "k", Username = "deploy", PrivateKey = key, KeyPassphrase = Stored("P3") });
        Assert.Equal(3, SavedPasswords.Remove(data, [ssh]));
        Assert.Null(ssh.Credentials.Password);
        Assert.Null(ssh.Credentials.KeyPassphrase);
        Assert.Same(key, ssh.Credentials.PrivateKey);
        Assert.Null(data.SavedCredentials[0].KeyPassphrase);
        Assert.Same(key, data.SavedCredentials[0].PrivateKey);
    }

    [Fact]
    public void A_saved_credential_passes_its_key_on()
    {
        var key = RsaPem();
        var data = new AppData();
        var saved = new SavedCredential { Name = "k", Username = "deploy", PrivateKey = key, KeyPassphrase = Stored("P") };
        data.SavedCredentials.Add(saved);
        var ssh = new NetworkShare { Name = "web01", Protocol = ShareProtocol.Ssh, Host = "web01", InheritCredentials = false, CredentialId = saved.Id };
        var (cred, source) = Inheritance.ResolveCredentials(ssh, data);
        Assert.Same(key, cred.PrivateKey);
        Assert.Equal(TestSecret("P"), cred.KeyPassphrase?.Reveal());
        Assert.Contains("saved credential", source);
    }
}
