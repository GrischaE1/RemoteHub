using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Security.Cryptography.X509Certificates;
using RemoteHub.Remoting;

namespace RemoteHub.Tests;

public class RemotingSetupTests
{
    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.0/24, 10.1.2.3", true)]
    [InlineData("fe80::1/64", true)]
    [InlineData("", false)]
    [InlineData("Any", false)]
    [InlineData("10.0.0.0/33", false)]
    [InlineData("1.2.3.4'; Remove-Item C:\\x", false)]
    public void Allowed_sources_are_only_ips_or_subnets(string text, bool ok) =>
        Assert.Equal(ok, RemotingSetup.TryParseSources(text, out _, out _));

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("srv01.corp.local")]
    public void Certificate_is_a_server_only_leaf_for_the_host(string host)
    {
        var now = DateTimeOffset.Now;
        var c = RemotingSetup.CreateCertificate(host, now);
        var pub = c.PublicCertificate;
        Assert.False(pub.HasPrivateKey);
        Assert.Equal($"CN={host}", pub.Subject);
        Assert.False(pub.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.Equal("1.3.6.1.5.5.7.3.1", pub.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Single().Value);
        Assert.True(pub.MatchesHostname(host));
        Assert.InRange(pub.NotAfter, now.AddYears(2).AddDays(-2).DateTime, now.AddYears(2).AddDays(1).DateTime);

        using var withKey = X509CertificateLoader.LoadPkcs12(c.Pfx, c.PfxPassword, X509KeyStorageFlags.EphemeralKeySet);
        Assert.True(withKey.HasPrivateKey);
        Assert.Equal(pub.Thumbprint, withKey.Thumbprint);
    }

    private static string Script(bool https) => RemotingSetup.BuildScript("Lab \"NUC\"\r\nnext", "192.168.1.50", ["192.168.1.20", "10.0.0.0/24"],
        https ? RemotingSetup.CreateCertificate("192.168.1.50", DateTimeOffset.Now) : null, new DateTime(2026, 10, 7, 12, 0, 0));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Script_parses_is_ascii_and_contains_the_settings(bool https)
    {
        var s = Script(https);
        Parser.ParseInput(s, out _, out var errors);
        Assert.Empty(errors);
        Assert.All(s, ch => Assert.True(ch < 128, $"non-ASCII character U+{(int)ch:X4}"));
        Assert.Contains("$AllowFrom = @('192.168.1.20','10.0.0.0/24')", s);
        Assert.Contains("#Requires -RunAsAdministrator", s);
        Assert.Contains("$DryRun = $false", s);
        Assert.Equal(https, s.Contains("-Transport HTTPS"));
        Assert.Contains(https ? "-LocalPort 5986" : "-LocalPort 5985", s);
        Assert.DoesNotContain("\nnext", s); // the connection name cannot break out of the comment line
    }

    /// <summary>Targets usually run Windows PowerShell 5.1: run the script's own certificate lines there (key not persisted).</summary>
    [Fact]
    public void Certificate_lines_load_in_Windows_PowerShell_51()
    {
        var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(exe)) return;
        var lines = Script(https: true).Split('\n').Select(l => l.Trim()).ToList();
        var test = Path.Combine(Path.GetTempPath(), $"rdpm-cert-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(test, string.Join("\n",
            lines.First(l => l.StartsWith("$Thumbprint = ")),
            lines.First(l => l.StartsWith("$pfx = ")),
            lines.First(l => l.StartsWith("$c = New-Object")).Replace("'MachineKeySet,PersistKeySet'", "'UserKeySet'"),
            "if ($c.Thumbprint -eq $Thumbprint -and $c.HasPrivateKey) { 'OK' } else { 'BAD' }",
            "$c.Reset()"));
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{test}\"")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.Equal("OK", output.Trim());
        }
        finally { File.Delete(test); }
    }

    [Fact]
    public void Dry_run_changes_nothing_and_lists_every_step()
    {
        var s = Script(https: true).Replace("#Requires -RunAsAdministrator", "").Replace("$DryRun = $false", "$DryRun = $true");
        using var ps = PowerShell.Create();
        ps.AddScript(s);
        ps.Invoke();
        Assert.False(ps.HadErrors, string.Join("\n", ps.Streams.Error));
        var lines = ps.Streams.Information.Select(i => i.MessageData.ToString()!).ToList();
        Assert.Equal(4, lines.Count(l => l.StartsWith("WOULD:")));
        Assert.Contains(lines, l => l.Contains("Firewall: allow TCP 5986 from 192.168.1.20, 10.0.0.0/24"));
    }
}
