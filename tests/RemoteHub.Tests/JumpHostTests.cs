extern alias agent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management.Automation;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using RemoteHub.Agent;
using RemoteHub.Models;
using RemoteHub.Remoting;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// The agent as a jump host: RemoteHub's tunnel against a real agent on this PC, and through it to a second agent or a
/// plain TCP server. Everything listens on 127.0.0.1, which a jump host refuses unless its policy names it – so most
/// tests allow 127.0.0.0/8 explicitly.
/// </summary>
[Collection("Jump hosts")] // JumpTunnels is shared by the whole process
public class JumpHostTests : IAsyncLifetime
{
    private static readonly A.TunnelRules Loopback = A.TunnelRules.Parse(["127.0.0.0/8"], null);

    public Task InitializeAsync() { JumpTunnels.CloseAll(); return Task.CompletedTask; }
    public Task DisposeAsync() { JumpTunnels.CloseAll(); return Task.CompletedTask; }

    private sealed class FakeAuth : A.IShellAuthenticator
    {
        public int Calls;
        public A.AuthResult Authenticate(string userName, string password)
        {
            Interlocked.Increment(ref Calls);
            return (userName, password) switch
            {
                (@".\tester", "secret") => new A.AuthResult(A.AuthOutcome.Ok, "", new A.ShellIdentity("TEST\\tester")),
                _ => A.AuthResult.Fail(A.AuthOutcome.BadCredentials, "bad"),
            };
        }
    }

    private sealed class Launcher : A.IShellLauncher
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
        public required Launcher Launcher;
        public required List<string> Audited;
        public AgentEndpoint Endpoint(JumpRoute? via = null) => new(Port, Fingerprint, via);
        public ValueTask DisposeAsync() => App.DisposeAsync();

        public string Audit() { lock (Audited) return string.Join("\n", Audited); }
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=" + Guid.NewGuid(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private static async Task<Agent> StartAgent(Func<A.AgentPolicy>? policy = null, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        var cert = SelfSigned();
        var auth = new FakeAuth();
        var launcher = new Launcher();
        var audited = new List<string>();
        var log = new A.AgentLog("", 1);
        log.Audited += (_, message) => { lock (audited) audited.Add(message); };
        var config = new A.AgentConfig { DeviceId = "dev-" + Guid.NewGuid().ToString("N")[..6], ListenPort = 0, MaxSessions = 10 };
        var app = A.AgentHost.Build(new A.AgentHostOptions(config, cert, auth, launcher, log, IPAddress.Loopback, Policy: policy ?? (() => new A.AgentPolicy { Tunnel = Loopback }))
        {
            TunnelNetwork = resolve is null ? null : new A.TunnelNetwork(resolve),
        });
        await app.StartAsync();
        return new Agent
        {
            App = app, Port = new Uri(app.Urls.First()).Port, Fingerprint = AgentProtocol.Fingerprint(cert.RawData), Auth = auth, Launcher = launcher,
            Audited = audited,
        };
    }

    private static PSCredential Tester(string password = "secret")
    {
        var pw = new System.Security.SecureString();
        foreach (var ch in password) pw.AppendChar(ch);
        return new PSCredential(@".\tester", pw);
    }

    private static JumpRoute Route(Agent jump, string password = "secret") => new("JUMP01", "127.0.0.1", jump.Endpoint(), Tester(password));

    private static PsTarget Target(Agent target, JumpRoute via) =>
        new("127.0.0.1", false, new CredentialSet { Username = "tester", Password = "secret", Domain = "." }, target.Endpoint(via));

    /// <summary>A TCP server on 127.0.0.1 that sends back what it receives.</summary>
    private static (TcpListener Listener, int Port) Echo()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var s = client.GetStream();
                        try { await s.CopyToAsync(s); client.Client.Shutdown(SocketShutdown.Send); } catch { /* reset */ }
                    }
                });
            }
        });
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    [Fact]
    public async Task PowerShell_and_health_reach_a_device_through_the_jump_host()
    {
        await using var jump = await StartAgent();
        await using var device = await StartAgent();
        var target = Target(device, Route(jump));

        var health = await AgentInstaller.HealthAsync("127.0.0.1", target.Agent!, CancellationToken.None);
        Assert.Contains(AgentFeatures.PowerShell, health.Features!);

        var rs = await Task.Run(() => PsRemoting.Open(target));
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = rs;
            ps.AddScript("$PID");
            Assert.Contains(Convert.ToInt32((await Task.Run(() => ps.Invoke()))[0].BaseObject), device.Launcher.Started);
            // Large arguments (file transfer chunks) go through the flow control.
            ps.Commands.Clear();
            ps.AddScript("param($b) ($b | Measure-Object -Sum).Sum").AddArgument(Enumerable.Repeat((byte)1, 3 * 1024 * 1024).ToArray());
            Assert.Equal(3 * 1024 * 1024, Convert.ToInt64((await Task.Run(() => ps.Invoke()))[0].BaseObject));
        }
        finally { rs.Dispose(); }

        Assert.Empty(jump.Launcher.Started);   // the jump host runs nothing, it only relays
        Assert.Equal(1, jump.Auth.Calls);      // health and PowerShell share one signed-in tunnel
        Assert.Equal(1, JumpTunnels.OpenTunnels);
        var audit = jump.Audit();
        Assert.Contains("Jump connection started: TEST\\tester", audit);
        Assert.Contains($"to 127.0.0.1:{device.Port}", audit);
    }

    [Fact]
    public async Task Streams_carry_data_both_ways_at_once_and_many_share_one_tunnel()
    {
        await using var jump = await StartAgent();
        var (listener, port) = Echo();
        try
        {
            var data = RandomNumberGenerator.GetBytes(4 * 1024 * 1024 + 123);
            async Task RoundTrip()
            {
                await using var s = await JumpTunnels.OpenAsync(Route(jump), "127.0.0.1", port, CancellationToken.None);
                var writing = Task.Run(async () => { await s.WriteAsync(data); });
                var back = new MemoryStream();
                var buffer = new byte[100_000];
                while (back.Length < data.Length)
                {
                    var n = await s.ReadAsync(buffer);
                    Assert.NotEqual(0, n);
                    back.Write(buffer, 0, n);
                }
                await writing;
                Assert.Equal(data, back.ToArray());
            }
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RoundTrip()));
            Assert.Equal(1, jump.Auth.Calls);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Closing_the_stream_ends_the_targets_connection_and_the_stream_is_audited()
    {
        await using var jump = await StartAgent();
        var (listener, port) = Echo();
        try
        {
            var s = await JumpTunnels.OpenAsync(Route(jump), "127.0.0.1", port, CancellationToken.None);
            await s.WriteAsync("hello"u8.ToArray());
            var buffer = new byte[5];
            Assert.Equal(5, await s.ReadAsync(buffer));
            await s.DisposeAsync();
            await WaitFor(() => jump.Audit().Contains("5 bytes sent, 5 bytes received"), "the stream's audit entry");
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task A_target_that_resets_its_connection_is_an_error_not_a_normal_end()
    {
        await using var jump = await StartAgent();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var accepted = listener.AcceptSocketAsync();
            await using var s = await JumpTunnels.OpenAsync(Route(jump), "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, CancellationToken.None);
            using (var server = await accepted)
            {
                server.LingerState = new LingerOption(true, 0); // closing sends a reset
                server.Close();
            }
            var ex = await Assert.ThrowsAsync<IOException>(async () => { while (await s.ReadAsync(new byte[16]) > 0) { } });
            Assert.Contains("JUMP01", ex.Message);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task By_default_the_jump_host_refuses_its_own_and_special_addresses()
    {
        await using var jump = await StartAgent(() => new A.AgentPolicy());
        var ex = await Assert.ThrowsAsync<JumpHostException>(() => JumpTunnels.OpenAsync(Route(jump), "127.0.0.1", 3389, CancellationToken.None));
        Assert.Equal(TunnelRefusal.Policy, ex.Reason);
        Assert.Contains("JUMP01 doesn't allow", ex.Message);
        Assert.Contains("this jump host itself or a special address", ex.Message);
        Assert.Contains("(own or special address)", jump.Audit());
    }

    [Fact]
    public async Task A_name_allowed_by_pattern_that_resolves_to_a_special_address_is_refused()
    {
        var resolve = (string host, CancellationToken _) => Task.FromResult(host switch
        {
            "evil.contoso.local" => new[] { IPAddress.Loopback },
            "metadata.contoso.local" => [IPAddress.Parse("169.254.169.254")],
            _ => throw new SocketException((int)SocketError.HostNotFound),
        });
        await using var jump = await StartAgent(() => new A.AgentPolicy { Tunnel = A.TunnelRules.Parse(["*.contoso.local"], null) }, resolve);
        foreach (var name in new[] { "evil.contoso.local", "metadata.contoso.local" })
            Assert.Equal(TunnelRefusal.Policy, (await Assert.ThrowsAsync<JumpHostException>(() => JumpTunnels.OpenAsync(Route(jump), name, 443, CancellationToken.None))).Reason);
        var dns = await Assert.ThrowsAsync<JumpHostException>(() => JumpTunnels.OpenAsync(Route(jump), "missing.contoso.local", 443, CancellationToken.None));
        Assert.Equal(TunnelRefusal.Dns, dns.Reason);
        Assert.Contains("can't resolve missing.contoso.local", dns.Message);
    }

    [Fact]
    public async Task Ports_targets_and_the_switch_of_the_policy_are_checked_for_every_connection()
    {
        var policy = new A.AgentPolicy { Tunnel = A.TunnelRules.Parse(["127.0.0.0/8"], ["3389", "5985-5986"]) };
        await using var jump = await StartAgent(() => policy);
        var (listener, port) = Echo();
        try
        {
            var ex = await Assert.ThrowsAsync<JumpHostException>(() => JumpTunnels.OpenAsync(Route(jump), "127.0.0.1", port, CancellationToken.None));
            Assert.Contains($"port {port} (TunnelPorts)", ex.Message);

            policy = new A.AgentPolicy { Tunnel = A.TunnelRules.Parse(["10.0.0.0/8"], null) }; // applies to the next connection, same tunnel
            ex = await Assert.ThrowsAsync<JumpHostException>(() => JumpTunnels.OpenAsync(Route(jump), "127.0.0.1", port, CancellationToken.None));
            Assert.Equal(TunnelRefusal.Policy, ex.Reason);

            policy = new A.AgentPolicy { Tunnel = Loopback };
            await using (await JumpTunnels.OpenAsync(Route(jump), "127.0.0.1", port, CancellationToken.None)) { }

            policy = new A.AgentPolicy { TunnelEnabled = false };
            ex = await Assert.ThrowsAsync<JumpHostException>(() => JumpTunnels.OpenAsync(Route(jump), "127.0.0.1", port, CancellationToken.None));
            Assert.Contains("TunnelEnabled", ex.Message);
            Assert.Equal(1, jump.Auth.Calls);
        }
        finally { listener.Stop(); }

        // Turned off before the tunnel exists: the jump host doesn't offer it at all.
        JumpTunnels.CloseAll();
        var off = await Assert.ThrowsAsync<JumpHostException>(() => JumpTunnels.OpenAsync(Route(jump), "127.0.0.1", 3389, CancellationToken.None));
        Assert.Contains("can't be used as a jump host", off.Message);
        Assert.Null(off.Reason);
    }

    [Fact]
    public async Task A_closed_port_is_unreachable_and_the_jump_hosts_own_address_comes_back()
    {
        await using var jump = await StartAgent();
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        var ex = await Assert.ThrowsAsync<JumpHostException>(() => JumpTunnels.OpenAsync(Route(jump), "127.0.0.1", port, CancellationToken.None));
        Assert.Equal(TunnelRefusal.Unreachable, ex.Reason);
        Assert.Equal("127.0.0.1", ex.Local);
        Assert.Contains("Through the jump host JUMP01", ex.Message);
    }

    [Fact]
    public async Task A_probe_tells_the_jump_hosts_address_without_connecting()
    {
        await using var jump = await StartAgent();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var accepted = listener.AcceptSocketAsync();
            var local = await JumpTunnels.LocalAddressAsync(Route(jump), "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, CancellationToken.None);
            Assert.Equal("127.0.0.1", local);
            await Task.Delay(300);
            Assert.False(accepted.IsCompleted); // nothing connected
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task A_wrong_password_for_the_jump_host_names_the_jump_host()
    {
        await using var jump = await StartAgent();
        await using var device = await StartAgent();
        var target = Target(device, Route(jump, password: "wrong"));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => PsRemoting.Open(target)));
        var message = PsRemoting.Explain(ex, target);
        Assert.Contains("jump host JUMP01", message);
        Assert.Contains("wrong user name or password", message);
        Assert.Equal(0, device.Auth.Calls); // the device was never reached
    }

    [Fact]
    public async Task The_number_of_streams_per_tunnel_is_limited()
    {
        await using var jump = await StartAgent(() => new A.AgentPolicy { Tunnel = Loopback, MaxTunnelStreams = 2 });
        var (listener, port) = Echo();
        try
        {
            await using var one = await JumpTunnels.OpenAsync(Route(jump), "127.0.0.1", port, CancellationToken.None);
            await using var two = await JumpTunnels.OpenAsync(Route(jump), "127.0.0.1", port, CancellationToken.None);
            var ex = await Assert.ThrowsAsync<JumpHostException>(() => JumpTunnels.OpenAsync(Route(jump), "127.0.0.1", port, CancellationToken.None));
            Assert.Equal(TunnelRefusal.Limit, ex.Reason);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task An_idle_tunnel_closes_and_the_next_connection_opens_a_new_one()
    {
        var idle = JumpTunnel.IdleClose;
        JumpTunnel.IdleClose = TimeSpan.FromMilliseconds(300);
        try
        {
            await using var jump = await StartAgent();
            var (listener, port) = Echo();
            try
            {
                await using (await JumpTunnels.OpenAsync(Route(jump), "127.0.0.1", port, CancellationToken.None)) { }
                await WaitFor(() => JumpTunnels.OpenTunnels == 0, "the idle tunnel to close");
                await using (await JumpTunnels.OpenAsync(Route(jump), "127.0.0.1", port, CancellationToken.None)) { }
                Assert.Equal(2, jump.Auth.Calls);
                await WaitFor(() => jump.Audit().Contains("Jump connection ended"), "the tunnel's end in the audit");
            }
            finally { listener.Stop(); }
        }
        finally { JumpTunnel.IdleClose = idle; }
    }

    // ---- policy

    [Theory]
    [InlineData("10.20.1.5", "x", true)]
    [InlineData("10.21.0.1", "x", false)]
    [InlineData("192.168.1.10", "sql01.contoso.local", true)]   // by name
    [InlineData("192.168.1.10", "SQL01.Contoso.Local.", true)]  // case and trailing dot
    [InlineData("192.168.1.10", "web.branch.contoso.local", true)]
    [InlineData("192.168.1.10", "contoso.local", false)]        // *.contoso.local is only below it
    [InlineData("192.168.1.10", "192.168.1.10", false)]         // an address is never allowed "by name"
    [InlineData("127.0.0.1", "sql01.contoso.local", false)]     // special: only by a range
    [InlineData("fd00::12", "x", true)]
    [InlineData("::ffff:10.20.3.4", "x", true)]                 // IPv4-mapped
    public void Targets_are_allowed_by_range_or_name(string address, string name, bool allowed)
    {
        var rules = A.TunnelRules.Parse(["10.20.0.0/16", "fd00::/8", "sql01.contoso.local", "*.contoso.local"], null);
        Assert.Equal(allowed, rules.Allowed(name, [IPAddress.Parse(address)]).Count == 1);
    }

    [Fact]
    public void Special_addresses_need_a_range_that_names_them()
    {
        var any = A.TunnelRules.Any;
        foreach (var a in new[] { "127.0.0.1", "0.0.0.0", "169.254.169.254", "224.0.0.1", "255.255.255.255", "::1", "::", "fe80::1", "ff02::1" })
            Assert.Empty(any.Allowed("x", [IPAddress.Parse(a)]));
        Assert.Empty(any.Allowed("x", [IPAddress.Parse("::1%1")]));   // a scope id doesn't hide the loopback
        Assert.Empty(any.Allowed("x", [IPAddress.Parse("::%3")]));
        Assert.Single(any.Allowed("x", [IPAddress.Parse("10.0.0.1")]));
        Assert.Single(A.TunnelRules.Parse(["127.0.0.1"], null).Allowed("localhost", [IPAddress.Loopback]));
        Assert.Empty(A.TunnelRules.Parse(["0.0.0.0/0"], null).Allowed("x", [IPAddress.Loopback])); // "all IPv4" isn't about the loopback

        // The jump host's own adapter addresses: only an entry naming exactly that address allows them.
        var own = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(u => u.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && a.GetAddressBytes()[0] != 169);
        if (own is not null)
        {
            Assert.True(A.TunnelRules.IsSpecial(own));
            Assert.Empty(any.Allowed("x", [own]));
            Assert.Empty(A.TunnelRules.Parse([$"{own.GetAddressBytes()[0]}.0.0.0/8"], null).Allowed("x", [own]));
            Assert.Single(A.TunnelRules.Parse([own.ToString()], null).Allowed("x", [own]));
        }
        // Only the allowed addresses of a name are used.
        Assert.Equal([IPAddress.Parse("10.0.0.5")], A.TunnelRules.Any.Allowed("mixed", [IPAddress.Loopback, IPAddress.Parse("10.0.0.5")]));
    }

    [Fact]
    public void Ports_and_unreadable_values_fail_closed()
    {
        var rules = A.TunnelRules.Parse(null, ["3389", " 5985-5986 ", "x", "70000"]);
        Assert.True(rules.AnyTarget);
        Assert.True(rules.PortAllowed(3389));
        Assert.True(rules.PortAllowed(5986));
        Assert.False(rules.PortAllowed(22));
        Assert.Equal(["x", "70000"], rules.Ignored);
        Assert.True(A.TunnelRules.Parse(null, ["*"]).PortAllowed(22));

        var nothing = A.TunnelRules.Parse(["not a target!"], ["nope"]); // set, but nothing readable: nothing allowed
        Assert.Empty(nothing.Allowed("x", [IPAddress.Parse("10.0.0.1")]));
        Assert.False(nothing.PortAllowed(3389));
        Assert.Single(A.TunnelRules.Parse(["*"], null).Allowed("x", [IPAddress.Parse("10.0.0.1")]));
    }

    [Fact]
    public void The_tunnel_policy_is_read_and_a_policy_that_cant_be_read_turns_it_off()
    {
        var config = new A.AgentConfig { AllowedGroups = ["S-1-5-32-544"] };
        var d = A.AgentPolicy.From(config, null);
        Assert.True(d.TunnelEnabled);
        Assert.True(d.Tunnel.AnyTarget && d.Tunnel.AnyPort);
        Assert.Equal(64, d.MaxTunnelStreams);
        Assert.Equal(["S-1-5-32-544"], d.Groups(A.AgentRight.Tunnel));

        var set = A.AgentPolicy.From(config, new Values(new()
        {
            ["TunnelEnabled"] = 0, ["MaxTunnelStreams"] = 5, ["TunnelTargets"] = new[] { "10.0.0.0/8" }, ["TunnelPorts"] = new[] { "3389" },
        }));
        Assert.False(set.TunnelEnabled);
        Assert.Equal(5, set.MaxTunnelStreams);
        Assert.False(set.Tunnel.AnyTarget);
        Assert.False(set.Tunnel.PortAllowed(22));
        Assert.False(A.AgentPolicy.Refused("x").TunnelEnabled);
    }

    // ------------------------------------------------------------------ local forwarder (RDP, WinRM)

    private static async Task<byte[]> ReadToEnd(Stream s)
    {
        var all = new MemoryStream();
        await s.CopyToAsync(all);
        return all.ToArray();
    }

    [Fact]
    public async Task The_local_forwarder_carries_a_connection_through_the_jump_host_and_its_half_close()
    {
        await using var jump = await StartAgent();
        var (listener, port) = Echo();
        try
        {
            using var forward = JumpForwarders.Open(Route(jump), "127.0.0.1", port);
            using (var again = JumpForwarders.Open(Route(jump), "127.0.0.1", port)) Assert.Equal(forward.Port, again.Port); // one per target

            using var client = new TcpClient();
            await client.ConnectAsync(forward.Host, forward.Port);
            var s = client.GetStream();
            var data = RandomNumberGenerator.GetBytes(1024 * 1024 + 7);
            var back = ReadToEnd(s);
            await s.WriteAsync(data);
            client.Client.Shutdown(SocketShutdown.Send); // the echo server sees the end, sends the rest and closes
            Assert.Equal(data, await back.WaitAsync(TimeSpan.FromSeconds(20)));
            await WaitFor(() => jump.Audit().Contains($"to 127.0.0.1:{port}"), "the stream's audit");
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task The_local_forwarder_refuses_connections_from_other_processes()
    {
        var connects = 0;
        using var forward = JumpForwarders.Open("test-" + Guid.NewGuid(), _ => { Interlocked.Increment(ref connects); return Task.FromResult<Stream>(new MemoryStream()); });
        // Another process (on a terminal server: another user) connects and waits for an answer – it is closed at once.
        using var other = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -Command \"$c = New-Object Net.Sockets.TcpClient('127.0.0.1', {forward.Port}); $s = $c.GetStream(); $s.ReadTimeout = 10000; " +
            "try { exit (10 + $s.ReadByte()) } catch { exit 3 }\"") { UseShellExecute = false, CreateNoWindow = true })!;
        Assert.True(other.WaitForExit(30_000));
        Assert.Equal(9, other.ExitCode); // ReadByte = -1: closed
        Assert.Equal(1, forward.Forwarder.Refused);
        Assert.Equal(0, connects);

        // RemoteHub's own process is let through.
        using var client = new TcpClient();
        await client.ConnectAsync(forward.Host, forward.Port);
        await WaitFor(() => connects == 1, "the connection to go through");
    }

    [SkippableFact]
    public async Task The_forwarder_logs_neither_the_target_nor_the_jump_hosts_answer()
    {
        Skip.IfNot(RemoteHub.Services.DiagLog.Enabled, "the diagnostic log is turned off by policy on this PC");
        var secret = "secret-target-" + Guid.NewGuid().ToString("N");
        using var forward = JumpForwarders.Open($"jump.example|5800|fp|admin|{secret}|3389",
            _ => Task.FromException<Stream>(new JumpHostException($"{secret} couldn't be reached from the jump host.", "unreachable")));
        string? error = null;
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(forward.Host, forward.Port);
            await WaitFor(() => (error ??= forward.TakeError()) is not null, "the jump host's answer kept for the error message");
        }
        Assert.Contains(secret, error); // the user still learns what couldn't be reached
        var line = $"Jump host forwarder {forward.Forwarder.LogId}: not connected – unreachable";
        var log = "";
        await WaitFor(() => (log = ReadShared(RemoteHub.Services.DiagLog.FilePath)).Contains(line), "the forwarder's log line");
        Assert.DoesNotContain(secret, log);
    }

    /// <summary>The whole file, while other tests may be writing to it (DiagLog appends from many threads).</summary>
    private static string ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException) { return ""; } // being rotated or created: the next look reads it
    }

    [Fact]
    public async Task What_the_jump_host_refuses_is_the_forwarders_error_and_an_unused_forwarder_closes()
    {
        await using var jump = await StartAgent(() => new A.AgentPolicy { Tunnel = A.TunnelRules.Parse(["10.0.0.0/8"], null) });
        var idle = JumpForwarders.IdleClose;
        JumpForwarders.IdleClose = TimeSpan.FromMilliseconds(200);
        try
        {
            var forward = JumpForwarders.Open(Route(jump), "127.0.0.1", 3389);
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(forward.Host, forward.Port);
                Assert.Empty(await ReadToEnd(client.GetStream()).WaitAsync(TimeSpan.FromSeconds(20))); // only closed
            }
            Assert.Contains("doesn't allow", forward.TakeError());
            Assert.Null(forward.TakeError()); // once

            var count = JumpForwarders.Count;
            forward.Dispose();
            await WaitFor(() => JumpForwarders.Count == count - 1, "the forwarder to close");
        }
        finally { JumpForwarders.IdleClose = idle; }
    }

    /// <summary>A server that answers the RDP security negotiation (TLS chosen), then does TLS with <paramref name="cert"/>.</summary>
    private static (TcpListener Listener, int Port) RdpServer(X509Certificate2? cert)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var s = client.GetStream();
            var request = new byte[19];
            await s.ReadExactlyAsync(request);
            if (cert is null) { await s.WriteAsync(new byte[] { 3, 0, 0, 11, 6, 0xD0, 0, 0, 0x12, 0x34, 0 }); return; } // no negotiation: old RDP security
            await s.WriteAsync(new byte[] { 3, 0, 0, 19, 14, 0xD0, 0, 0, 0x12, 0x34, 0, 0x02, 0, 8, 0, 1, 0, 0, 0 }); // RDP_NEG_RSP: TLS
            await using var tls = new System.Net.Security.SslStream(s);
            try { await tls.AuthenticateAsServerAsync(cert); } catch { /* the client only looks */ }
        });
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    [Fact]
    public async Task The_RDP_certificate_is_read_after_the_security_negotiation()
    {
        using var cert = SelfSigned();
        var (listener, port) = RdpServer(cert);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var seen = await ServerCertificates.RdpAsync(client.GetStream(), "sql01.contoso.local", CancellationToken.None);
            Assert.NotNull(seen);
            Assert.Equal(cert.Thumbprint, seen.Value.Certificate.Thumbprint);
            Assert.False(seen.Value.Valid); // self-signed, another name: the user is asked
            Assert.Equal(ServerCertificates.Pin(cert), ServerCertificates.Pin(seen.Value.Certificate));
        }
        finally { listener.Stop(); }

        var (old, oldPort) = RdpServer(null);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, oldPort);
            Assert.Null(await ServerCertificates.RdpAsync(client.GetStream(), "old", CancellationToken.None));
        }
        finally { old.Stop(); }
    }

    /// <summary>A WebDAV server on 127.0.0.1 that answers every request with the folder /dav/ holding report.txt; records the Host headers.</summary>
    private static (TcpListener Listener, int Port, List<string> Hosts) WebDavServer()
    {
        var hosts = new List<string>();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        const string body = """<?xml version="1.0" encoding="utf-8"?><d:multistatus xmlns:d="DAV:">""" +
                            """<d:response><d:href>/dav/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>""" +
                            """<d:response><d:href>/dav/report.txt</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>5</d:getcontentlength></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>""" +
                            "</d:multistatus>";
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var s = client.GetStream();
                        var head = new List<byte>();
                        var one = new byte[1];
                        while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
                        {
                            if (await s.ReadAsync(one) == 0) return;
                            head.Add(one[0]);
                        }
                        var lines = System.Text.Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
                        lock (hosts) hosts.Add(lines.First(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))[5..].Trim());
                        var length = lines.FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) is { } cl ? int.Parse(cl[15..].Trim()) : 0;
                        await s.ReadExactlyAsync(new byte[length]);
                        var payload = System.Text.Encoding.UTF8.GetBytes(body);
                        await s.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 207 Multi-Status\r\nContent-Type: application/xml\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"));
                        await s.WriteAsync(payload);
                    }
                });
            }
        });
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port, hosts);
    }

    private static NetworkShare Share(ShareProtocol protocol, string host, int? port, string path) =>
        new() { Name = "files", Protocol = protocol, Host = host, Port = port, Path = path, WebDavHttps = false };

    private static readonly CredentialSet ShareAccount = new() { Username = "tester", Password = "secret", Domain = "." };

    [Fact]
    public async Task A_WebDAV_share_is_reached_through_the_jump_host_under_its_own_name()
    {
        // The name resolves only on the jump host (as names behind one often do).
        await using var jump = await StartAgent(resolve: (name, _) => Task.FromResult(name == "dav.branch.test" ? new[] { IPAddress.Loopback } : []));
        var (listener, port, hosts) = WebDavServer();
        try
        {
            using var fs = Remoting.Shares.ShareConnector.Open(Share(ShareProtocol.WebDav, "dav.branch.test", port, "dav"), ShareAccount,
                _ => false, _ => { }, Route(jump));
            var entry = Assert.Single(fs.List("/dav"));
            Assert.Equal(("report.txt", 5L), (entry.Name, entry.Size));
            lock (hosts) Assert.All(hosts, h => Assert.Equal($"dav.branch.test:{port}", h)); // the URL keeps the share's name
            await WaitFor(() => jump.Audit().Contains("dav.branch.test"), "the stream to be audited on the jump host");
        }
        finally { listener.Stop(); }
    }

    [Theory]
    [InlineData(ShareProtocol.Smb, 445, "files")]
    [InlineData(ShareProtocol.Sftp, 22, "")]
    [InlineData(ShareProtocol.WebDav, 80, "dav")]
    public async Task What_the_jump_host_refuses_for_a_share_is_its_error(ShareProtocol protocol, int port, string path)
    {
        await using var jump = await StartAgent(() => new A.AgentPolicy { Tunnel = A.TunnelRules.Parse(["10.0.0.0/8"], null) });
        var share = Share(protocol, "127.0.0.1", port, path);
        var ex = await Assert.ThrowsAsync<Remoting.Shares.ShareException>(() => Task.Run(() =>
            Remoting.Shares.ShareConnector.Open(share, ShareAccount, _ => false, _ => { }, Route(jump))).WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Contains("doesn't allow", ex.Message);
    }

    [Fact]
    public void An_FTP_share_isnt_tried_through_a_jump_host()
    {
        var route = new JumpRoute("JUMP01", "127.0.0.1", new AgentEndpoint(1, "x"), Tester());
        var ex = Assert.Throws<Remoting.Shares.ShareException>(() =>
            Remoting.Shares.ShareConnector.Open(Share(ShareProtocol.Ftp, "ftp.branch.test", null, ""), ShareAccount, _ => false, _ => { }, route));
        Assert.Equal(Remoting.Shares.ShareConnector.FtpNotThroughJumpHost, ex.Message);
    }

    [Fact]
    public async Task RemoteHubs_SMB_client_connects_to_another_port_under_the_shares_name()
    {
        // SMBLibrary's port overload is private: an update that removes it must fail here, not at a user.
        Assert.True(Remoting.Shares.ManagedSmbShare.CanUseOtherPort);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var accepted = listener.AcceptTcpClientAsync();
            var client = new SMBLibrary.Client.SMB2Client(2000, true);
            var connect = Task.Run(() => Remoting.Shares.ManagedSmbShare.ConnectToPort(client, "files.branch.test", ((IPEndPoint)listener.LocalEndpoint).Port));
            (await accepted.WaitAsync(TimeSpan.FromSeconds(10))).Dispose(); // reached the port, then closed without an SMB answer
            Assert.False(await connect.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal("files.branch.test", typeof(SMBLibrary.Client.SMB2Client)
                .GetField("m_serverName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(client));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void RemoteHubs_SMB_client_always_signs_and_refuses_a_guest_session()
    {
        // SMBLibrary signs only when the server asks; RemoteHub switches it on through private fields – an update that
        // renames them must fail here, not leave a user's connection unsigned.
        Assert.True(Remoting.Shares.ManagedSmbShare.CanRequireSigning);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var required = typeof(SMBLibrary.Client.SMB2Client).GetField("m_signingRequired", flags)!;
        var key = typeof(SMBLibrary.Client.SMB2Client).GetField("m_signingKey", flags)!;

        var guest = new SMBLibrary.Client.SMB2Client(2000, true); // a guest session has no signing key
        Assert.False(Remoting.Shares.ManagedSmbShare.RequireSigning(guest));
        Assert.False((bool)required.GetValue(guest)!);

        var user = new SMBLibrary.Client.SMB2Client(2000, true);
        key.SetValue(user, new byte[16]); // as Login leaves it for an account
        Assert.True(Remoting.Shares.ManagedSmbShare.RequireSigning(user));
        Assert.True((bool)required.GetValue(user)!);
    }

    [Fact]
    public void An_FTPS_data_connection_with_another_certificate_is_refused_without_asking()
    {
        const string pinned = "AA11", other = "BB22";
        Assert.Equal(Remoting.Shares.FtpShare.CertificateDecision.Accept, Remoting.Shares.FtpShare.Decide(System.Net.Security.SslPolicyErrors.None, other, pinned, dataConnection: true));
        Assert.Equal(Remoting.Shares.FtpShare.CertificateDecision.Accept, Remoting.Shares.FtpShare.Decide(System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, pinned, pinned, dataConnection: true));
        Assert.Equal(Remoting.Shares.FtpShare.CertificateDecision.Refuse, Remoting.Shares.FtpShare.Decide(System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, other, pinned, dataConnection: true));
        Assert.Equal(Remoting.Shares.FtpShare.CertificateDecision.Refuse, Remoting.Shares.FtpShare.Decide(System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, other, null, dataConnection: true));
        // the control connection may still ask the user, as before
        Assert.Equal(Remoting.Shares.FtpShare.CertificateDecision.Ask, Remoting.Shares.FtpShare.Decide(System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, other, pinned, dataConnection: false));
    }

    private sealed class Values(Dictionary<string, object> values) : A.AgentPolicy.IPolicyValues
    {
        public int? Number(string? subKey, string name) => subKey is null && values.TryGetValue(name, out var v) && v is int i ? i : null;
        public IReadOnlyList<string>? Strings(string? subKey, string name) => subKey is null && values.TryGetValue(name, out var v) && v is string[] s ? s : null;
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) Assert.Fail("Timed out waiting for " + what);
            await Task.Delay(50);
        }
    }
}

[CollectionDefinition("Jump hosts", DisableParallelization = true)]
public class JumpHostCollection;
