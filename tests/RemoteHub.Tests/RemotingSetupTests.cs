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
    [InlineData("srv01'; Remove-Item C:\\x")]
    [InlineData("a b")]
    [InlineData("")]
    public void Only_names_and_addresses_go_into_the_certificate_command(string host) =>
        Assert.Throws<ArgumentException>(() => RemotingSetup.CertificateCommand(host, @"Cert:\CurrentUser\My"));

    [Fact]
    public void Thumbprints_are_shown_in_groups() =>
        Assert.Equal("BF5A 729C 5A9C 1581 2990 7664 AEB8 E469 859F 620F", RemotingSetup.Grouped("BF5A729C5A9C158129907664AEB8E469859F620F"));

    private static string Script(bool https) => RemotingSetup.BuildScript("Lab \"NUC\"\r\nnext", "192.168.1.50", ["192.168.1.20", "10.0.0.0/24"],
        https, new DateTime(2026, 10, 7, 12, 0, 0));

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
        Assert.DoesNotContain("BEGIN", s);  // no key material: the certificate is made on the target
        Assert.DoesNotContain("FromBase64String", s);
        Assert.Contains("Disable-NetFirewallRule", s); // Windows' own rule for everyone doesn't stay on
        Assert.Contains("-Name LocalAccountTokenFilterPolicy -Type DWord -Value 1", s);
    }

    [Fact]
    public void Without_local_admins_the_script_only_checks_and_warns()
    {
        var s = RemotingSetup.BuildScript("Lab", "192.168.1.50", ["192.168.1.20"], true, new DateTime(2026, 10, 7), allowLocalAdmins: false);
        Parser.ParseInput(s, out _, out var errors);
        Assert.Empty(errors);
        Assert.DoesNotContain("Set-ItemProperty -Path $UacKey", s);
        Assert.Contains("LocalAccountTokenFilterPolicy is not 1", s);
    }

    /// <summary>
    /// Targets usually run Windows PowerShell 5.1: run the script's certificate command there (in the current user's store,
    /// removed again) and check what it makes – the same rules RemoteHub used to apply itself.
    /// </summary>
    [SkippableTheory]
    [InlineData("192.168.1.50")]
    [InlineData("srv01.corp.local")]
    public void Certificate_command_makes_a_server_only_leaf_in_Windows_PowerShell_51(string host)
    {
        var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        Skip.IfNot(File.Exists(exe), "Windows PowerShell 5.1 isn't installed.");
        var test = Path.Combine(Path.GetTempPath(), $"rdpm-cert-{Guid.NewGuid():N}.ps1");
        var cer = Path.ChangeExtension(test, ".cer");
        File.WriteAllText(test, string.Join("\n",
            "$ErrorActionPreference = 'Stop'",
            "$c = " + RemotingSetup.CertificateCommand(host, @"Cert:\CurrentUser\My"),
            $"try {{ [IO.File]::WriteAllBytes('{cer}', $c.RawData) }}",
            "finally { Remove-Item \"Cert:\\CurrentUser\\My\\$($c.Thumbprint)\" -DeleteKey }",
            "'OK'"));
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{test}\"")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            // The PowerShell 7 SDK in this test process changes PSModulePath; Windows PowerShell 5.1 would then load PS 7's
            // modules (no Cert: drive). On a target the script runs in a normal PowerShell, so give it its own path.
            psi.Environment.Remove("PSModulePath");
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.Contains("OK", output);
            using var pub = X509CertificateLoader.LoadCertificateFromFile(cer);
            Assert.Equal($"CN={host}", pub.Subject);
            Assert.False(pub.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
            Assert.Equal("1.3.6.1.5.5.7.3.1", pub.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Single().Value);
            Assert.True(pub.MatchesHostname(host));
            Assert.InRange(pub.NotAfter, DateTime.Now.AddYears(2).AddDays(-2), DateTime.Now.AddYears(2).AddDays(1));
        }
        finally { File.Delete(test); File.Delete(cer); }
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
        Assert.Equal(6, lines.Count(l => l.StartsWith("WOULD:")));
        Assert.Contains(lines, l => l.Contains("LocalAccountTokenFilterPolicy = 1"));
        Assert.Contains(lines, l => l.Contains("Firewall: allow TCP 5986 from 192.168.1.20, 10.0.0.0/24"));
    }
}
