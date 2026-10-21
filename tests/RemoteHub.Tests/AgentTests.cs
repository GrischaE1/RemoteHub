extern alias agent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using RemoteHub.Models;
using RemoteHub.Agent;
using RemoteHub.Remoting;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// The agent and RemoteHub's transport against each other on this PC. Sign-in is a test double and PowerShell runs as
/// the current user (the real Windows sign-in and start-as-user need LocalSystem).
/// </summary>
public class AgentTests
{
    private sealed class FakeAuth : A.IShellAuthenticator
    {
        public int Calls;
        public A.AuthResult Authenticate(string userName, string password)
        {
            Interlocked.Increment(ref Calls);
            return (userName, password) switch
            {
                (@".\tester", "secret") => new A.AuthResult(A.AuthOutcome.Ok, "", new A.ShellIdentity("TEST\\tester")),
                (@".\guest", "secret") => A.AuthResult.Fail(A.AuthOutcome.NotAllowed, "not allowed"),
                (@".\expired", "secret") => A.SignInRefusal.FromWin32(1793), // ERROR_ACCOUNT_EXPIRED
                (@".\nodc", "secret") => A.SignInRefusal.FromWin32(1311),    // ERROR_NO_LOGON_SERVERS
                _ => A.AuthResult.Fail(A.AuthOutcome.BadCredentials, "bad"),
            };
        }
    }

    /// <summary>powershell.exe -s as the current user.</summary>
    private sealed class CurrentUserLauncher : A.IShellLauncher
    {
        public readonly List<int> Started = [];
        public A.IShellProcess Start(A.ShellIdentity identity)
        {
            var p = Process.Start(new ProcessStartInfo(A.ShellCommand.PowerShellExe, A.ShellCommand.Arguments)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            })!;
            lock (Started) Started.Add(p.Id);
            return new Proc(p);
        }

        private sealed class Proc(Process p) : A.IShellProcess
        {
            public Stream Input => p.StandardInput.BaseStream;
            public Stream Output => p.StandardOutput.BaseStream;
            public int ProcessId => p.Id;
            public void Dispose()
            {
                try { p.StandardInput.Close(); } catch { /* gone */ }
                if (!p.WaitForExit(3000)) try { p.Kill(true); } catch { /* gone */ }
                p.Dispose();
            }
        }
    }

    private sealed class Agent : IAsyncDisposable
    {
        public required WebApplication App;
        public required int Port;
        public required string Fingerprint;
        public required FakeAuth Auth;
        public required CurrentUserLauncher Launcher;
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=" + Guid.NewGuid(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null); // SChannel needs a persisted key
    }

    private static async Task<Agent> StartAgent(Func<A.AgentConfig, A.AgentPolicy>? policy = null, Action<A.AgentConfig>? configure = null)
    {
        var cert = SelfSigned();
        var auth = new FakeAuth();
        var launcher = new CurrentUserLauncher();
        var config = new A.AgentConfig { DeviceId = "dev-1", ListenPort = 0, MaxSessions = 3 };
        configure?.Invoke(config);
        var app = A.AgentHost.Build(new A.AgentHostOptions(config, cert, auth, launcher, A.AgentLog.Null, IPAddress.Loopback,
            Policy: policy is null ? null : () => policy(config)));
        await app.StartAsync();
        var port = new Uri(app.Urls.First()).Port;
        return new Agent { App = app, Port = port, Fingerprint = AgentProtocol.Fingerprint(cert.RawData), Auth = auth, Launcher = launcher };
    }

    private static PsTarget Target(Agent a, string user = "tester", string password = "secret", string? fingerprint = null) =>
        new("127.0.0.1", false, new CredentialSet { Username = user, Password = password, Domain = "." },
            new AgentEndpoint(a.Port, fingerprint ?? a.Fingerprint));

    [Fact]
    public async Task A_full_PowerShell_session_runs_through_the_agent()
    {
        await using var a = await StartAgent();
        var target = Target(a);
        var rs = await Task.Run(() => PsRemoting.Open(target));
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = rs;
            ps.AddScript("$PSVersionTable.PSVersion.Major; $PID");
            var result = await Task.Run(() => ps.Invoke());
            Assert.Equal(5, Convert.ToInt32(result[0].BaseObject));
            Assert.Contains(Convert.ToInt32(result[1].BaseObject), a.Launcher.Started);

            // Large arguments, like file transfer chunks.
            ps.Commands.Clear();
            ps.AddScript("param($b) ($b | Measure-Object -Sum).Sum").AddArgument(Enumerable.Repeat((byte)1, 2 * 1024 * 1024).ToArray());
            Assert.Equal(2 * 1024 * 1024, Convert.ToInt64((await Task.Run(() => ps.Invoke()))[0].BaseObject));
        }
        finally { rs.Dispose(); }

        // Closing the session ends the PowerShell process on the device.
        var pid = a.Launcher.Started.Single();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && Process.GetProcesses().Any(p => p.Id == pid && p.ProcessName == "powershell")) await Task.Delay(200);
        Assert.DoesNotContain(Process.GetProcesses(), p => p.Id == pid && p.ProcessName == "powershell");
    }

    [Fact]
    public async Task A_different_certificate_stops_before_credentials_are_sent()
    {
        await using var a = await StartAgent();
        var target = Target(a, fingerprint: new string('A', 64));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => PsRemoting.Open(target)));
        Assert.Contains("different certificate", PsRemoting.Explain(ex, target));
        Assert.Equal(0, a.Auth.Calls);
    }

    [Fact]
    public async Task Wrong_password_and_not_allowed_accounts_are_refused_and_guessing_is_throttled()
    {
        await using var a = await StartAgent();
        var bad = Target(a, password: "wrong");
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => PsRemoting.Open(bad)));
        Assert.Contains("wrong user name or password", PsRemoting.Explain(ex, bad));

        var guest = Target(a, user: "guest");
        ex = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => PsRemoting.Open(guest)));
        Assert.Contains("not allowed", PsRemoting.Explain(ex, guest));

        for (var i = 0; i < 3; i++) await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => PsRemoting.Open(bad)));
        var good = Target(a);
        ex = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => PsRemoting.Open(good)));
        Assert.Contains("too many failed attempts", PsRemoting.Explain(ex, good)); // blocked, even with the right password
        Assert.Empty(a.Launcher.Started);
    }

    [Fact]
    public async Task An_expired_account_is_refused_as_a_sign_in_and_a_missing_domain_controller_as_an_error()
    {
        await using var a = await StartAgent();
        var expired = Target(a, user: "expired");
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => PsRemoting.Open(expired)));
        Assert.Contains("rejected the sign-in", PsRemoting.Explain(ex, expired)); // 401, not 500

        var noDc = Target(a, user: "nodc");
        ex = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => PsRemoting.Open(noDc)));
        Assert.Contains("could not check the sign-in with Windows", PsRemoting.Explain(ex, noDc));
        Assert.Empty(a.Launcher.Started);
    }

    [Fact]
    public async Task The_agent_does_not_start_on_a_port_another_program_listens_on()
    {
        // An ordinary listener (shared use allowed) on IPv4 any - Windows would let the agent bind next to it (A-INST-25).
        var other = new System.Net.Sockets.TcpListener(IPAddress.Any, 0);
        other.Start();
        try
        {
            var port = ((IPEndPoint)other.LocalEndpoint).Port;
            var config = new A.AgentConfig { DeviceId = "dev-1", ListenPort = port };
            await using var app = A.AgentHost.Build(new A.AgentHostOptions(config, SelfSigned(), new FakeAuth(), new CurrentUserLauncher(), A.AgentLog.Null));
            await Assert.ThrowsAnyAsync<IOException>(() => app.StartAsync());
        }
        finally { other.Stop(); }
    }

    [Fact]
    public async Task PowerShell_turned_off_by_policy_is_explained_and_nothing_is_signed_in()
    {
        // The 404's text doesn't name the agent; it must still reach the user instead of "status code '404'" (POL-05).
        await using var a = await StartAgent(c => A.AgentPolicy.Defaults(c) with { PowerShellEnabled = false });
        var target = Target(a);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => PsRemoting.Open(target)));
        var text = PsRemoting.Explain(ex, target);
        Assert.Contains("(PowerShellEnabled)", text);
        Assert.DoesNotContain("status code", text);
        Assert.Equal(0, a.Auth.Calls);
        Assert.Empty(a.Launcher.Started);
    }

    [Fact]
    public async Task Health_answers_without_credentials()
    {
        await using var a = await StartAgent();
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => AgentProtocol.Fingerprint(c!.RawData) == a.Fingerprint };
        using var http = new HttpClient(handler);
        var json = await http.GetStringAsync($"https://127.0.0.1:{a.Port}{AgentProtocol.HealthPath}");
        Assert.Contains("\"protocol\":1", json);
        Assert.Contains("\"deviceId\":\"dev-1\"", json);
    }

    [Theory]
    [InlineData(@"CONTOSO\admin", "CONTOSO", "admin")]
    [InlineData(@".\admin", ".", "admin")]
    [InlineData("admin", ".", "admin")]
    [InlineData("admin@contoso.com", null, "admin@contoso.com")]
    public void User_names_are_split_for_the_Windows_sign_in(string input, string? domain, string user) =>
        Assert.Equal((domain, user), A.WindowsAuthenticator.SplitUser(input));

    [Fact]
    public void Machine_name_means_local_account() =>
        Assert.Equal(".", A.WindowsAuthenticator.SplitUser(Environment.MachineName + @"\admin").Domain);

    [Fact]
    public void Basic_header_and_fingerprints_parse()
    {
        Assert.True(A.AgentHost.TryBasic("Basic " + Convert.ToBase64String("DOM\\u:pa:ss"u8.ToArray()), out var u, out var p));
        Assert.Equal(("DOM\\u", "pa:ss"), (u, p));
        Assert.False(A.AgentHost.TryBasic("Bearer x", out _, out _));
        Assert.False(A.AgentHost.TryBasic("Basic !!", out _, out _));
        var hex = new string('a', 64);
        Assert.Equal(hex.ToUpperInvariant(), AgentProtocol.NormalizeFingerprint(string.Join(":", Enumerable.Range(0, 32).Select(_ => "aa"))));
        Assert.Equal("", AgentProtocol.NormalizeFingerprint("abc"));
    }

    [Fact]
    public void Agent_user_names_use_dot_for_local_accounts()
    {
        Assert.Equal(@".\admin", PsRemoting.AgentUserName(new CredentialSet { Username = "admin", Domain = "." }));
        Assert.Equal(@"CONTOSO\admin", PsRemoting.AgentUserName(new CredentialSet { Username = "admin", Domain = "CONTOSO" }));
        Assert.Equal("admin@contoso.com", PsRemoting.AgentUserName(new CredentialSet { Username = "admin@contoso.com" }));
        Assert.Null(PsRemoting.AgentUserName(new CredentialSet()));
    }

    [Fact]
    public void Sign_in_gate_blocks_after_repeated_failures_and_forgets_after_the_window()
    {
        var now = DateTime.UtcNow;
        var gate = new A.SignInGate(3, TimeSpan.FromMinutes(5), () => now);
        gate.Failed("1.2.3.4"); gate.Failed("1.2.3.4");
        Assert.False(gate.IsBlocked("1.2.3.4"));
        gate.Failed("1.2.3.4");
        Assert.True(gate.IsBlocked("1.2.3.4"));
        Assert.False(gate.IsBlocked("5.6.7.8"));
        now = now.AddMinutes(6);
        Assert.False(gate.IsBlocked("1.2.3.4"));
        gate.Failed("1.2.3.4");
        Assert.False(gate.IsBlocked("1.2.3.4")); // a fresh window
    }

    [SkippableFact]
    public void The_agent_folder_belongs_to_administrators_only()
    {
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        Skip.IfNot(new System.Security.Principal.WindowsPrincipal(me).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),
            "Needs an elevated test run (sets Administrators as owner).");
        var folder = Path.Combine(Path.GetTempPath(), "rh-acl-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "logs", "agent.log");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        // Prepared by a user before the install: an explicit entry for everyone.
        var everyone = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null);
        var pre = new FileInfo(file).GetAccessControl();
        pre.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(everyone, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(pre);
        try
        {
            A.Enrollment.SecureFolder(folder);
            var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            var acl = new FileInfo(file).GetAccessControl();
            Assert.Equal(admins, acl.GetOwner(typeof(System.Security.Principal.SecurityIdentifier)));
            var rules = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
            Assert.DoesNotContain(rules, r => !r.IsInherited);
            Assert.All(rules, r => Assert.Contains(r.IdentityReference.Value, new[] { "S-1-5-18", "S-1-5-32-544" }));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Parallel_sign_ins_get_no_more_tries_than_sequential_ones()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var gate = new A.SignInGate(3, TimeSpan.FromMinutes(5), () => now);
        gate.Failed("1.2.3.4");
        // One failure so far: two more attempts may run at the same time, a third has to wait for them.
        Assert.True(gate.TryBegin("1.2.3.4"));
        Assert.True(gate.TryBegin("1.2.3.4"));
        Assert.False(gate.TryBegin("1.2.3.4"));
        Assert.True(gate.TryBegin("5.6.7.8")); // other addresses aren't affected
        gate.Succeeded("1.2.3.4"); // one of them had the right password
        gate.End("1.2.3.4");
        Assert.True(gate.TryBegin("1.2.3.4"));
    }

    [Fact]
    public void Old_addresses_are_forgotten()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var gate = new A.SignInGate(5, TimeSpan.FromMinutes(5), () => now);
        for (var i = 0; i < 1000; i++) gate.Failed($"10.0.{i / 256}.{i % 256}");
        now = now.AddMinutes(10);
        gate.Failed("1.2.3.4"); // over the limit: the expired ones are removed
        Assert.Equal(1, gate.Tracked);
    }

    [Fact]
    public async Task The_agent_proves_its_enrollment_for_its_own_certificate()
    {
        var keys = new AgentEnrollmentKeys();
        var key = keys.EnsureCurrent(DateTime.UtcNow);
        await using var a = await StartAgent(configure: c => { c.EnrollmentKeyId = key.Id.ToString(); c.EnrollmentSecret = key.Secret; c.DeviceId = Guid.NewGuid().ToString(); });
        var r = await AgentInstaller.ClaimAsync("127.0.0.1", a.Port, null, keys, CancellationToken.None);
        Assert.Equal(AgentInstaller.ClaimOutcome.Claimed, r.Outcome);
        Assert.Equal(a.Fingerprint, r.Claim!.Fingerprint);
        Assert.Equal(Environment.MachineName, r.Claim.ComputerName);

        // Another RemoteHub (other secrets) can't claim it; a revoked secret isn't accepted any more.
        Assert.Equal(AgentInstaller.ClaimOutcome.UnknownSecret, (await AgentInstaller.ClaimAsync("127.0.0.1", a.Port, null, new AgentEnrollmentKeys(), CancellationToken.None)).Outcome);
        keys.Replace(DateTime.UtcNow, revokeEarlier: true);
        Assert.Equal(AgentInstaller.ClaimOutcome.RevokedSecret, (await AgentInstaller.ClaimAsync("127.0.0.1", a.Port, null, keys, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task An_agent_enrolled_without_a_secret_has_nothing_to_claim()
    {
        await using var a = await StartAgent();
        var keys = new AgentEnrollmentKeys();
        keys.EnsureCurrent(DateTime.UtcNow);
        Assert.Equal(AgentInstaller.ClaimOutcome.NoEnrollment, (await AgentInstaller.ClaimAsync("127.0.0.1", a.Port, null, keys, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task The_claim_refuses_a_malformed_nonce()
    {
        var key = new AgentEnrollmentKeys().EnsureCurrent(DateTime.UtcNow);
        await using var a = await StartAgent(configure: c => { c.EnrollmentKeyId = key.Id.ToString(); c.EnrollmentSecret = key.Secret; });
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var http = new HttpClient(handler);
        foreach (var nonce in new[] { "", "short", new string('A', 65), "a%2Fb0123456789012345" })
        {
            using var response = await http.GetAsync($"https://127.0.0.1:{a.Port}{EnrollmentProof.Path}?nonce={nonce}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("10.0.0.0/8, 192.168.1.20", true)]
    [InlineData("10.0.0.1-10.0.0.9", true)]
    [InlineData("fe80::1/64", true)]
    [InlineData("10.0.0.0/33", false)]
    [InlineData("server01", false)]
    [InlineData("10.0.0.1,,10.0.0.2", false)]
    public void Firewall_scopes_are_validated(string scope, bool valid) => Assert.Equal(valid, AgentEnrollment.IsValidAddressList(scope));

    [Fact]
    public void Enrollment_file_names_are_safe() =>
        Assert.Equal("WKS-07-Accounting.rhagent", AgentEnrollment.FileName(" WKS 07/Accounting "));

    [Fact]
    public void The_agent_finds_the_enrollment_file_next_to_its_installer()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rh-enroll-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var msi = Path.Combine(dir, "RemoteHub-Agent-x64.msi");
            File.WriteAllText(msi, "");
            Assert.Null(A.Enrollment.Locate(msi));
            var file = Path.Combine(dir, "WKS-07.rhagent");
            File.WriteAllText(file, "{}");
            Assert.Equal(file, A.Enrollment.Locate(msi));   // MSI path (OriginalDatabase)
            Assert.Equal(file, A.Enrollment.Locate(dir));   // folder
            Assert.Equal(file, A.Enrollment.Locate(file));  // the file itself
            File.WriteAllText(Path.Combine(dir, "other.rhagent"), "{}");
            Assert.Throws<InvalidOperationException>(() => A.Enrollment.Locate(msi)); // ambiguous: refuse
            Assert.Null(A.Enrollment.Locate(""));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Firewall_rule_is_limited_to_the_program_port_and_scope()
    {
        var args = A.Enrollment.FirewallAddArguments(@"C:\Program Files\RemoteHub Agent\RemoteHubAgent.exe", 58732, "10.0.0.0/8, 192.168.1.20");
        Assert.Contains("name=\"RemoteHub Agent\" dir=in action=allow protocol=TCP localport=58732", args);
        Assert.Contains(@"program=""C:\Program Files\RemoteHub Agent\RemoteHubAgent.exe""", args);
        Assert.EndsWith("remoteip=10.0.0.0/8,192.168.1.20", args);
        Assert.EndsWith("remoteip=any", A.Enrollment.FirewallAddArguments("x.exe", 1, ""));
    }

    [Fact]
    public void Allowed_groups_default_to_the_language_independent_administrators_sid()
    {
        Assert.Equal(["S-1-5-32-544"], AgentInstaller.ParseGroups(""));
        Assert.Equal(["S-1-5-32-544", "Server Admins"], AgentInstaller.ParseGroups("administrators; Server Admins, S-1-5-32-544"));
    }

    [Fact]
    public void Staging_creates_a_private_folder_with_both_files()
    {
        var package = Path.Combine(Path.GetTempPath(), "rh-pkg-" + Guid.NewGuid().ToString("N") + ".msi");
        File.WriteAllBytes(package, [1, 2, 3]);
        try
        {
            var key = new AgentEnrollmentKeys().EnsureCurrent(DateTime.UtcNow);
            var e = AgentEnrollment.Create(key.Id.ToString(), key.Secret, "WKS 07", 58732);
            var (folder, msi, enrollment) = AgentInstaller.Prepare(package, e);
            Assert.Equal(AgentInstaller.PackageName, Path.GetFileName(msi));
            Assert.Equal("WKS-07.rhagent", Path.GetFileName(enrollment));
            Assert.Equal(key.Secret, AgentEnrollment.Parse(File.ReadAllBytes(enrollment!)).Secret);
            Assert.StartsWith(AgentInstaller.TempRoot, folder);
            Directory.Delete(folder, true);
        }
        finally { File.Delete(package); }
    }

    [Fact]
    public void An_update_with_a_file_that_is_not_a_package_is_refused_before_anything_is_sent()
    {
        // No version to wait for: the old agent's next answer would read as "updated" (A-UPDFAIL-04).
        var package = Path.Combine(Path.GetTempPath(), "rh-pkg-" + Guid.NewGuid().ToString("N") + ".msi");
        File.WriteAllBytes(package, [0xD0, 0xCF, 0x11, 0xE0, 1, 2, 3]);
        try
        {
            Assert.Null(AgentInstaller.PackageVersion(package));
            var ex = Assert.Throws<RemoteCommandException>(() => AgentInstaller.StartUpdateThroughAgent(null!, package, null, _ => { }));
            Assert.Contains("nothing was sent", ex.Message);
        }
        finally { File.Delete(package); }
    }

    [Fact]
    public void The_agent_transport_can_raise_PowerShells_fragment_size()
    {
        // Relies on an SDK internal (32 KB default, one acknowledgement per fragment): if an SDK update breaks it, this fails.
        var info = new AgentConnectionInfo("127.0.0.1", new AgentEndpoint(1, new string('A', 64)), null);
        var transport = info.CreateClientSessionTransportManager(Guid.NewGuid(), "test", null!);
        Assert.True(AgentClientTransport.SetFragmentSize(transport, AgentClientTransport.FragmentSize));
    }

    [Fact]
    public async Task Folder_upload_through_the_agent_runs_on_Windows_PowerShell_5_1()
    {
        await using var a = await StartAgent();
        var src = Path.Combine(Path.GetTempPath(), "rh-agent-up-" + Guid.NewGuid().ToString("N"));
        var dst = Path.Combine(Path.GetTempPath(), "rh-agent-dst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "big.log"), string.Concat(Enumerable.Range(0, 80000).Select(i => $"line {i} INFO all good\r\n")));
        var noise = new byte[FileUploader.ChunkSize * 2 + 5]; RandomNumberGenerator.Fill(noise);
        File.WriteAllBytes(Path.Combine(src, "sub", "noise.bin"), noise);
        File.WriteAllBytes(Path.Combine(src, "sub", "empty.txt"), []);
        for (var i = 0; i < 40; i++) File.WriteAllText(Path.Combine(src, "sub", $"f{i}.txt"), $"file {i}");
        try
        {
            var target = Target(a);
            var items = FileUploader.Expand([src]).Select(x => new TransferItem(x.Local, x.Relative, "test")).ToList();
            await FileUploader.UploadAsync(() => PsRemoting.Open(target), ex => PsRemoting.Explain(ex, target), dst, false, items, x => x());
            Assert.All(items, i => Assert.True(i.State == TransferState.Done, $"{i.RelativePath}: {i.StatusText}"));
            foreach (var (local, rel) in FileUploader.Expand([src]))
                Assert.Equal(File.ReadAllBytes(local), File.ReadAllBytes(Path.Combine(dst, rel)));
            Assert.Empty(Directory.GetFiles(dst, "*.remotehub-part", SearchOption.AllDirectories));

            // Second run without overwrite: everything is skipped before any data is sent.
            var again = FileUploader.Expand([src]).Select(x => new TransferItem(x.Local, x.Relative, "test")).ToList();
            await FileUploader.UploadAsync(() => PsRemoting.Open(target), ex => PsRemoting.Explain(ex, target), dst, false, again, x => x());
            Assert.All(again, i => Assert.Equal(TransferState.Skipped, i.State));
        }
        finally
        {
            Directory.Delete(src, true);
            if (Directory.Exists(dst)) Directory.Delete(dst, true);
        }
    }
}
