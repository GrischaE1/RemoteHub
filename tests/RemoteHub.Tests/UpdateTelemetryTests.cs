using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Models;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>Version comparison, update check and download verification, and what telemetry sends.</summary>
[Collection("Updates")]
public class UpdateTelemetryTests
{
    private const string Sample = """
        {"version":"0.9.0","channel":"beta","prerelease":true,"notes":"**Full Changelog**: https://github.com/GrischaE1/RemoteHub/commits/v0.9.0","publishedAt":"2026-10-08T04:54:11Z","mandatory":false,"minSupportedVersion":"0.0.0","download":{"name":"RemoteHub-0.9.0-x64.msi","url":"https://api.github.com/repositories/1409080301/releases/assets/620767795","sha256":"c7d55bcf5dcabfc7a9c118a98bc42a3b430657a06a1c5a30ab942aea71b2cda9","size":83304102}}
        """;

    private static T WithVersion<T>(string current, Func<T> test)
    {
        var old = UpdateService.Current;
        UpdateService.Current = AppVersion.Parse(current)!;
        try { return test(); }
        finally { UpdateService.Current = old; }
    }

    [Fact]
    public void Versions_compare_like_semver()
    {
        string[] ordered = ["0.9.0", "0.9.1-beta.1", "0.9.1-beta.2", "0.9.1-beta.10", "0.9.1-rc.1", "0.9.1", "0.10.0", "1.0.0-beta", "1.0.0"];
        for (var i = 0; i < ordered.Length - 1; i++)
            Assert.True(AppVersion.Parse(ordered[i])! < AppVersion.Parse(ordered[i + 1])!, $"{ordered[i]} < {ordered[i + 1]}");
        Assert.Equal(AppVersion.Parse("0.9.0"), AppVersion.Parse("v0.9.0+abc123"));
        foreach (var bad in new[] { "", "1.2", "1.2.3.4", "x.y.z", "1.2.3-", "1.2.3-beta..1", "-1.0.0" })
            Assert.Null(AppVersion.Parse(bad));
    }

    [Fact]
    public void Only_a_higher_version_is_offered()
    {
        var info = UpdateService.Parse(Sample);
        Assert.Equal("RemoteHub-0.9.0-x64.msi", info.Download!.Name);
        Assert.Equal(83304102, info.Download.Size);

        Assert.Equal(UpdateStatus.UpToDate, WithVersion("0.9.0", () => UpdateService.Evaluate(info).Status));
        Assert.Equal(UpdateStatus.UpToDate, WithVersion("0.9.1-beta.1", () => UpdateService.Evaluate(info).Status)); // GA 0.9.0 is older
        Assert.Equal(UpdateStatus.Available, WithVersion("0.8.5", () => UpdateService.Evaluate(info).Status));
        Assert.Equal(UpdateStatus.Available, WithVersion("0.9.0-beta.3", () => UpdateService.Evaluate(info).Status));
        Assert.False(WithVersion("0.8.5", () => UpdateService.Evaluate(info).Required));

        Assert.True(WithVersion("0.8.5", () => UpdateService.Evaluate(info with { Mandatory = true }).Required));
        Assert.True(WithVersion("0.8.5", () => UpdateService.Evaluate(info with { MinSupportedVersion = "0.9.0" }).Required));
        Assert.Throws<FormatException>(() => UpdateService.Parse("""{"version":"latest"}"""));
    }

    [Fact]
    public void Channels_and_endpoints()
    {
        Assert.Equal("https://update.remotehub.dev/latest", UpdateService.LatestUri(UpdateService.Stable).ToString());
        Assert.Equal("https://update.remotehub.dev/latest?channel=beta", UpdateService.LatestUri(UpdateService.Beta).ToString());
        Assert.Equal(UpdateService.Stable, UpdateService.NormalizeChannel(" GA "));
        Assert.Null(UpdateService.NormalizeChannel("nightly"));

        // Default: beta below 1.0 or for a pre-release, GA otherwise; the user's choice wins.
        Assert.Equal(UpdateService.Beta, WithVersion("0.9.0", () => UpdateService.EffectiveChannel(new UiState())));
        Assert.Equal(UpdateService.Stable, WithVersion("1.0.0", () => UpdateService.EffectiveChannel(new UiState())));
        Assert.Equal(UpdateService.Beta, WithVersion("1.1.0-beta.1", () => UpdateService.EffectiveChannel(new UiState())));
        Assert.Equal(UpdateService.Stable, WithVersion("0.9.0", () => UpdateService.EffectiveChannel(new UiState { UpdateChannel = "stable" })));
    }

    [Theory]
    [InlineData("https://api.github.com/repositories/1/releases/assets/2", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://update.remotehub.dev/download/x.msi", true)]
    [InlineData("http://api.github.com/x", false)]
    [InlineData("https://github.com.evil.example/x", false)]
    [InlineData("https://evilgithub.com/x", false)]
    [InlineData("file:///C:/x.msi", false)]
    public void Downloads_only_from_trusted_https_hosts(string url, bool allowed) =>
        Assert.Equal(allowed, UpdateService.IsAllowedDownload(new Uri(url)));

    // ------------------------------------------------------------------ download verification

    private sealed class FakeHandler(byte[] body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Accept;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Accept = request.Headers.Accept.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body), RequestMessage = request });
        }
    }

    private static async Task<(string? Path, Exception? Error, string Folder)> Download(byte[] served, UpdateInfo info, string current = "0.8.0")
    {
        var folder = Path.Combine(Path.GetTempPath(), "rh-update-test-" + Guid.NewGuid().ToString("N"));
        var oldHttp = UpdateService.Http;
        var oldVersion = UpdateService.Current;
        UpdateService.Http = new HttpClient(new FakeHandler(served));
        UpdateService.Current = AppVersion.Parse(current)!;
        try { return (await UpdateService.DownloadAsync(info, null, CancellationToken.None, folder), null, folder); }
        catch (Exception ex) { return (null, ex, folder); }
        finally { UpdateService.Http = oldHttp; UpdateService.Current = oldVersion; }
    }

    private static UpdateInfo Info(byte[] content, string? sha = null, long? size = null, string url = "https://api.github.com/repositories/1/releases/assets/2") =>
        new("0.9.0", "beta", true, null, null, false, null,
            new UpdateDownload("RemoteHub-0.9.0-x64.msi", url, sha ?? Convert.ToHexString(SHA256.HashData(content)), size ?? content.Length));

    [Fact]
    public async Task A_download_is_kept_only_when_checksum_and_size_match()
    {
        var msi = new byte[200_000];
        Random.Shared.NextBytes(msi);
        var folders = new List<string>();
        try
        {
            var ok = await Download(msi, Info(msi));
            folders.Add(ok.Folder);
            Assert.Null(ok.Error);
            Assert.Equal(msi, File.ReadAllBytes(ok.Path!));
            Assert.Equal("RemoteHub-0.9.0-x64.msi", Path.GetFileName(ok.Path));

            var tampered = (byte[])msi.Clone();
            tampered[1234] ^= 0xFF;
            var bad = await Download(tampered, Info(msi));
            folders.Add(bad.Folder);
            Assert.IsType<InvalidDataException>(bad.Error);
            Assert.Empty(Directory.GetFiles(bad.Folder));

            var truncated = await Download(msi[..1000], Info(msi));
            folders.Add(truncated.Folder);
            Assert.IsType<InvalidDataException>(truncated.Error);
            Assert.Empty(Directory.GetFiles(truncated.Folder));

            var longer = await Download([.. msi, 1, 2, 3], Info(msi));
            folders.Add(longer.Folder);
            Assert.IsType<InvalidDataException>(longer.Error);

            Assert.IsType<InvalidOperationException>((await Download(msi, Info(msi, sha: ""))).Error);
            Assert.IsType<InvalidOperationException>((await Download(msi, Info(msi, url: "http://api.github.com/x"))).Error);
            Assert.IsType<InvalidOperationException>((await Download(msi, Info(msi), current: "0.9.0")).Error); // not newer
        }
        finally
        {
            foreach (var f in folders) try { Directory.Delete(f, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Install_helper_waits_runs_the_msi_and_restarts()
    {
        var script = UpdateService.InstallScript(@"C:\Temp\100%\RemoteHub-0.9.1-x64.msi", 4242, @"C:\Program Files\RemoteHub\RemoteHub.exe", @"D:\Tools\RemoteHub\RemoteHub.exe");
        Assert.Contains("PID eq 4242", script);
        Assert.Contains(@"msiexec.exe"" /i ""C:\Temp\100%%\RemoteHub-0.9.1-x64.msi"" /passive /norestart", script);
        Assert.Contains(@"start """" ""C:\Program Files\RemoteHub\RemoteHub.exe""", script);
        Assert.Contains(@"start """" ""D:\Tools\RemoteHub\RemoteHub.exe""", script);
    }

    // ------------------------------------------------------------------ telemetry

    [Fact]
    public void Install_id_is_random_stored_once_and_removable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rh-telemetry-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = Path.Combine(dir, "a", "install-id");
            var b = Path.Combine(dir, "b", "install-id");
            var id = Telemetry.GetOrCreateInstallId(a);
            Assert.True(Guid.TryParse(id, out var g) && g != Guid.Empty);
            Assert.Equal(id, Telemetry.GetOrCreateInstallId(a));          // stored, not recomputed
            Assert.NotEqual(id, Telemetry.GetOrCreateInstallId(b));       // random, not derived from this machine
            Assert.DoesNotContain(Environment.MachineName, id, StringComparison.OrdinalIgnoreCase);

            Telemetry.ForgetInstallId(a);
            Assert.False(File.Exists(a));
            Assert.NotEqual(id, Telemetry.GetOrCreateInstallId(a));

            File.WriteAllText(b, "not a guid");
            Assert.True(Guid.TryParse(Telemetry.GetOrCreateInstallId(b), out _));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp */ } }
    }

    [Fact]
    public void Telemetry_sends_exactly_the_documented_fields()
    {
        var id = Guid.NewGuid().ToString("D");
        using var doc = JsonDocument.Parse(Telemetry.Payload("app_start", "0.9.0", "beta", id));
        var fields = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        Assert.Equal(["arch", "channel", "event", "installId", "os", "version"], fields.Keys.Order());
        Assert.Equal("app_start", fields["event"]);
        Assert.Equal("0.9.0", fields["version"]);
        Assert.Equal("beta", fields["channel"]);
        Assert.Equal(id, fields["installId"]);
        Assert.Contains(fields["os"], new[] { "Windows 10", "Windows 11", "Windows Server" });
        Assert.Contains(fields["arch"], new[] { "x64", "arm64", "x86" });
        Assert.True(new UiState().TelemetryEnabled);
        Assert.True(new UiState().UpdateCheckEnabled);
    }
}
