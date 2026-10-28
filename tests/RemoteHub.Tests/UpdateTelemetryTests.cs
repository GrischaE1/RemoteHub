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

    /// <summary>Answers /latest (GA) and /latest?channel=beta; null means 404, "fail" a 500.</summary>
    private sealed class ServerHandler(string? ga, string? beta) : HttpMessageHandler
    {
        public readonly List<string> Requested = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            lock (Requested) Requested.Add(url);
            var version = url.Contains("channel=beta") ? beta : ga;
            var response = version switch
            {
                null => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") },
                "fail" => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("") },
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"version":"{{version}}"}""") },
            };
            return Task.FromResult(response);
        }
    }

    private static (UpdateCheckResult Result, List<string> Requested) Check(string channel, string current, string? ga, string? beta)
    {
        var oldHttp = UpdateService.Http;
        var handler = new ServerHandler(ga, beta);
        UpdateService.Http = new HttpClient(handler);
        try { return (WithVersion(current, () => UpdateService.CheckAsync(channel).GetAwaiter().GetResult()), handler.Requested); }
        finally { UpdateService.Http = oldHttp; }
    }

    [Fact]
    public void Beta_also_offers_a_GA_release_that_is_newer_than_the_last_pre_release()
    {
        // GA 0.21.0 is newer than beta 0.21.0-beta.2: Beta offers the GA release.
        var (result, requested) = Check(UpdateService.Beta, "0.21.0-beta.1", ga: "0.21.0", beta: "0.21.0-beta.2");
        Assert.Equal(UpdateStatus.Available, result.Status);
        Assert.Equal("0.21.0", result.Info!.Version);
        Assert.Equal(["https://update.remotehub.dev/latest", "https://update.remotehub.dev/latest?channel=beta"], requested.Order());

        // A newer pre-release wins over an older GA release.
        Assert.Equal("0.22.0-beta.1", Check(UpdateService.Beta, "0.21.0", ga: "0.21.0", beta: "0.22.0-beta.1").Result.Info!.Version);
        // No pre-release published at all: GA still counts.
        Assert.Equal("0.21.0", Check(UpdateService.Beta, "0.20.2", ga: "0.21.0", beta: null).Result.Info!.Version);
        Assert.Equal(UpdateStatus.UpToDate, Check(UpdateService.Beta, "0.21.0", ga: "0.21.0", beta: null).Result.Status);
        // GA unreachable: Beta's answer stands; a Beta failure isn't hidden behind GA's "up to date".
        Assert.Equal("0.22.0-beta.1", Check(UpdateService.Beta, "0.21.0", ga: "fail", beta: "0.22.0-beta.1").Result.Info!.Version);
        Assert.Equal(UpdateStatus.Failed, Check(UpdateService.Beta, "0.21.0", ga: "0.21.0", beta: "fail").Result.Status);
        Assert.Equal("0.21.1", Check(UpdateService.Beta, "0.21.0", ga: "0.21.1", beta: "fail").Result.Info!.Version);
    }

    [Fact]
    public void GA_never_asks_for_pre_releases()
    {
        var (result, requested) = Check(UpdateService.Stable, "0.21.0", ga: "0.21.0", beta: "0.22.0-beta.1");
        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.Equal(["https://update.remotehub.dev/latest"], requested);
    }

    [Theory]
    [InlineData("https://api.github.com/repositories/1409080301/releases/assets/2", true)]
    [InlineData("https://api.github.com/repos/GrischaE1/RemoteHub/releases/assets/2", true)]
    [InlineData("https://github.com/GrischaE1/RemoteHub/releases/download/v0.14.3/RemoteHub-0.14.3-x64.msi", true)]
    [InlineData("https://update.remotehub.dev/download/x.msi", true)]
    // Anything else on GitHub: anyone can publish release files there.
    [InlineData("https://api.github.com/repositories/1/releases/assets/2", false)]
    [InlineData("https://github.com/someone/else/releases/download/v1/RemoteHub-9.9.9-x64.msi", false)]
    [InlineData("https://objects.githubusercontent.com/x", false)]
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

    /// <summary>A download as RemoteHub makes it; <paramref name="publisher"/>: the running build's signer (null: an unsigned build).</summary>
    private static async Task<(string? Path, Exception? Error, string Folder)> Download(byte[] served, UpdateInfo info, string current = "0.8.0", Authenticode.Publisher? publisher = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "rh-update-test-" + Guid.NewGuid().ToString("N"));
        var oldHttp = UpdateService.Http;
        var oldVersion = UpdateService.Current;
        var oldPublisher = Authenticode.RunningPublisher;
        UpdateService.Http = new HttpClient(new FakeHandler(served));
        UpdateService.Current = AppVersion.Parse(current)!;
        Authenticode.RunningPublisher = publisher;
        try { return (await UpdateService.DownloadAsync(info, null, CancellationToken.None, folder), null, folder); }
        catch (Exception ex) { return (null, ex, folder); }
        finally { UpdateService.Http = oldHttp; UpdateService.Current = oldVersion; Authenticode.RunningPublisher = oldPublisher; }
    }

    /// <summary>A file with an embedded Authenticode signature that every Windows trusts: the .NET runtime's own coreclr.dll.</summary>
    private static string SignedFile => Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");

    [Fact]
    public async Task A_signed_build_installs_only_downloads_signed_by_its_own_publisher()
    {
        var signed = File.ReadAllBytes(SignedFile);
        var publisher = Authenticode.Signer(SignedFile);
        Assert.NotNull(publisher);
        var unsigned = new byte[100_000];
        Random.Shared.NextBytes(unsigned);
        var tampered = (byte[])signed.Clone();
        tampered[signed.Length / 3] ^= 0xFF; // in the code, not the signature: the signature no longer matches
        var folders = new List<string>();
        try
        {
            var ok = await Download(signed, Info(signed), publisher: publisher);
            folders.Add(ok.Folder);
            Assert.Null(ok.Error);
            Assert.True(File.Exists(ok.Path));

            foreach (var (served, signer, why) in new (byte[], Authenticode.Publisher, string)[]
                     {
                         (unsigned, publisher!, "isn't signed"),
                         (tampered, publisher!, "doesn't match the file"),
                         (signed, publisher! with { Subject = "CN=Someone Else, O=Someone Else, C=DE" }, "not by RemoteHub's publisher"),
                         // A certificate in the publisher's name from another CA the PC trusts: same subject, another key.
                         (signed, publisher! with { Key = "3082010A0282010100" + new string('0', 32) }, "not with its key"),
                     })
            {
                var refused = await Download(served, Info(served), publisher: signer);
                folders.Add(refused.Folder);
                var error = Assert.IsType<InvalidDataException>(refused.Error);
                Assert.Contains(why, error.Message);
                Assert.Empty(Directory.GetFiles(refused.Folder)); // deleted, nothing left to install
            }

            // An unsigned build (test release, local build) can't name a publisher: the checksum is all it has.
            var unsignedBuild = await Download(unsigned, Info(unsigned), publisher: null);
            folders.Add(unsignedBuild.Folder);
            Assert.Null(unsignedBuild.Error);
        }
        finally
        {
            foreach (var f in folders) try { Directory.Delete(f, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void The_publisher_is_compared_by_subject_and_key_with_a_trusted_chain()
    {
        var publisher = Authenticode.Signer(SignedFile)!;
        Assert.Null(Authenticode.Refusal(SignedFile, publisher));
        Assert.Null(Authenticode.Refusal(SignedFile, publisher with { Key = publisher.Key.ToLowerInvariant() }));
        Assert.Null(Authenticode.Refusal(SignedFile, null));
        Assert.Contains("not by RemoteHub's publisher", Authenticode.Refusal(SignedFile, publisher with { Subject = publisher.Subject.ToUpperInvariant() + "X" }));
        Assert.Contains("not with its key", Authenticode.Refusal(SignedFile, publisher with { Key = "00" }));
        Assert.Null(Authenticode.Signer(Path.Combine(Path.GetTempPath(), "rh-missing-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public async Task The_step_script_reads_a_signers_key_as_RemoteHub_does()
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, UpdateService.StepScriptName));
        var keyOf = System.Text.RegularExpressions.Regex.Match(script, @"^function KeyOf.*$", System.Text.RegularExpressions.RegexOptions.Multiline).Value.TrimEnd('\r');
        Assert.NotEmpty(keyOf);
        var command = keyOf + $"; KeyOf (Get-AuthenticodeSignature -LiteralPath '{SignedFile}').SignerCertificate";
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        // the PowerShell 7 SDK in this process changes PSModulePath; Windows PowerShell needs its own
        start.Environment.Remove("PSModulePath");
        using var p = System.Diagnostics.Process.Start(start)!;
        var output = await p.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(Authenticode.Signer(SignedFile)!.Key, output.Trim());
    }

    [Fact]
    public void The_step_script_accepts_the_same_next_publisher_keys_as_RemoteHub()
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, UpdateService.StepScriptName));
        var list = System.Text.RegularExpressions.Regex.Match(script, @"^\$NextKeys = @\(([^)]*)\)", System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.True(list.Success, "InstallUpdate.ps1 defines $NextKeys");
        var keys = System.Text.RegularExpressions.Regex.Matches(list.Groups[1].Value, "'([0-9A-Fa-f]+)'").Select(m => m.Groups[1].Value.ToUpperInvariant());
        Assert.Equal(Authenticode.NextPublisherKeys.Select(k => k.ToUpperInvariant()).Order(), keys.Order());
    }

    private static UpdateInfo Info(byte[] content, string? sha = null, long? size = null, string url = "https://api.github.com/repositories/1409080301/releases/assets/2") =>
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
    public void Restart_helper_waits_for_the_elevated_step_and_passes_its_result()
    {
        var id = Guid.NewGuid().ToString("N");
        var script = UpdateService.RestartScript(4242, @"C:\Program Files\RemoteHub\RemoteHub.exe", @"D:\Tools\100%\RemoteHub.exe", id);
        Assert.Contains("PID eq 4242", script);
        Assert.Contains("powershell.exe", script);
        Assert.DoesNotContain("msiexec", script); // the user's own helper never installs anything
        Assert.Contains($@"start """" ""C:\Program Files\RemoteHub\RemoteHub.exe"" --update-result {id}", script);
        Assert.Contains($@"start """" ""D:\Tools\100%%\RemoteHub.exe"" --update-result {id}", script);
    }

    [Fact]
    public void Elevated_step_is_valid_PowerShell_with_its_values_quoted()
    {
        var command = UpdateService.InstallCommand(@"C:\Users\O'Brien\AppData\Local\Temp\RemoteHub\Updates\RemoteHub-0.9.1-x64.msi", "ab" + new string('0', 62),
            new("CN=O'Brien GmbH, O=O'Brien GmbH, C=DE", "3082010A"), 4242, "0123456789abcdef0123456789abcdef");
        System.Management.Automation.Language.Parser.ParseInput(command, out _, out var errors);
        Assert.Empty(errors);
        // The step's own script, with its values as parameters
        Assert.StartsWith("& {\n" + UpdateService.StepScript, command);
        Assert.Contains(@"-Source 'C:\Users\O''Brien\AppData", command);
        Assert.Contains("-Sha256 'ab" + new string('0', 62) + "'", command);
        Assert.Contains("$sha = $Sha256.ToUpperInvariant()", command);
        Assert.Contains("-Publisher 'CN=O''Brien GmbH, O=O''Brien GmbH, C=DE'", command);
        Assert.Contains("-PublisherKey '3082010A'", command);
        Assert.Contains("-WaitPid 4242 -Id '0123456789abcdef0123456789abcdef'", command);
        Assert.Contains("Wait-Process -Id $WaitPid", command);
        Assert.Contains("Join-Path $env:ProgramFiles 'RemoteHub Updates'", command);
        Assert.Contains("/inheritance:r", command);
        // the copy is checked, never the user's download
        Assert.Contains("Get-FileHash -LiteralPath $target", command);
        Assert.Contains("Get-AuthenticodeSignature -LiteralPath $target", command);
        // msiexec writes its log next to the outcome, so a failed install says why
        Assert.Contains("'/l*v', ('\"' + $log + '\"')", command);
        Assert.Contains("$log = Join-Path $results ($Id + '.log')", command);
    }

    [Fact]
    public void The_step_script_is_ASCII_Windows_PowerShell_installed_next_to_RemoteHub()
    {
        // Windows PowerShell 5.1 reads a BOM-less file as ANSI: a non-ASCII character could end a string.
        var file = Path.Combine(AppContext.BaseDirectory, UpdateService.StepScriptName);
        Assert.True(File.Exists(file), "InstallUpdate.ps1 isn't copied next to RemoteHub");
        Assert.All(File.ReadAllBytes(file), b => Assert.True(b < 0x80, $"non-ASCII byte 0x{b:X2}"));
        Assert.Equal(File.ReadAllText(file), UpdateService.StepScript);
        System.Management.Automation.Language.Parser.ParseFile(file, out _, out var errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void RemoteHub_Update_asks_only_for_the_installed_RemoteHub_with_its_files_and_publisher()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rh-updater-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(dir, "RemoteHub.exe");
        var updater = Path.Combine(dir, UpdateService.UpdaterName);
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(installed, "");
            File.WriteAllText(updater, "");
            // without the step's script next to it: Windows PowerShell, as before
            Assert.Null(UpdateService.Updater(installed, installed, null));
            File.WriteAllText(Path.Combine(dir, UpdateService.StepScriptName), "");
            Assert.Equal(updater, UpdateService.Updater(installed, installed, null));
            Assert.Equal(updater, UpdateService.Updater(installed.ToUpperInvariant(), installed, null));
            // a copy run from elsewhere, or no path at all
            Assert.Null(UpdateService.Updater(Path.Combine(Path.GetTempPath(), "RemoteHub.exe"), installed, null));
            Assert.Null(UpdateService.Updater(null, installed, null));
            // a signed RemoteHub asks only through a program signed by its own publisher
            Assert.Null(UpdateService.Updater(installed, installed, Authenticode.Signer(SignedFile)));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void RemoteHub_Update_gets_the_values_only()
    {
        Assert.Equal(@"--msi ""C:\Users\O'Brien\AppData\Local\Temp\RemoteHub\Updates\RemoteHub-0.9.1-x64.msi"" --sha256 AB" + new string('0', 62) +
                     " --pid 4242 --id 0123456789abcdef0123456789abcdef",
            UpdateService.UpdaterArguments(@"C:\Users\O'Brien\AppData\Local\Temp\RemoteHub\Updates\RemoteHub-0.9.1-x64.msi", "ab" + new string('0', 62),
                4242, "0123456789abcdef0123456789abcdef"));
    }

    /// <summary>Runs the elevated step's script without elevation, in a temp folder instead of Program Files.</summary>
    private static string RunInstallCommand(string source, string sha, Authenticode.Publisher? publisher, string stage)
    {
        var id = Guid.NewGuid().ToString("N");
        var command = UpdateService.InstallCommand(source, sha, publisher, 0, id, stage);
        return RunStep("-EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)), stage, id);
    }

    /// <summary>Runs the installed script as RemoteHubUpdate.exe starts it (-File, -Signer), without elevation, in a temp folder.</summary>
    private static string RunStepFile(string source, string sha, string signer, string stage)
    {
        var id = Guid.NewGuid().ToString("N");
        var script = Path.Combine(AppContext.BaseDirectory, UpdateService.StepScriptName);
        return RunStep($"-File \"{script}\" -Source \"{source}\" -Sha256 {sha} -WaitPid 0 -Id {id} -Signer \"{signer}\" -Stage \"{stage}\"", stage, id);
    }

    private static string RunStep(string arguments, string stage, string id)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass " + arguments)
        { UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(start)!;
        Assert.True(p.WaitForExit(60_000), "the step didn't end within 60 s");
        return File.ReadAllText(Path.Combine(stage, "results", id + ".txt")).Trim();
    }

    [SkippableFact]
    public void Elevated_step_refuses_a_swapped_or_wrongly_signed_file_and_keeps_no_copy()
    {
        // The step locks its folder to administrators and SYSTEM: without elevation it can't use it (CI runs elevated).
        Skip.IfNot(new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator), "needs an elevated test run");
        var dir = Path.Combine(Path.GetTempPath(), "rh-update-stage-" + Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(dir, "stage");
        var download = Path.Combine(dir, "RemoteHub-9.9.9-x64.msi");
        try
        {
            Directory.CreateDirectory(dir);
            File.Copy(SignedFile, download);
            var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(download)));
            var publisher = Authenticode.Signer(SignedFile)!;

            // swapped after RemoteHub checked it: another file, another hash
            Assert.Equal("refused changed", RunInstallCommand(download, new string('0', 64), publisher, stage));
            // the right file, but RemoteHub's publisher is someone else
            Assert.Equal("refused signature", RunInstallCommand(download, sha, publisher with { Subject = "CN=RemoteHub Publisher" }, stage));
            // the publisher's name, but another certificate's key
            Assert.Equal("refused signature", RunInstallCommand(download, sha, publisher with { Key = "00" + publisher.Key[2..] }, stage));
            // As RemoteHubUpdate.exe starts it: the publisher is whoever signed the program, here someone other than
            // the download's signer (an unsigned copy of it).
            var unsigned = Path.Combine(dir, "RemoteHub-9.9.8-x64.msi");
            File.WriteAllBytes(unsigned, [.. File.ReadAllBytes(download).Take(4096)]);
            var unsignedSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(unsigned)));
            Assert.Equal("refused signature", RunStepFile(unsigned, unsignedSha, SignedFile, stage));
            Assert.Equal("refused changed", RunStepFile(download, new string('0', 64), SignedFile, stage));
            Assert.Empty(Directory.GetFiles(stage, "*.msi")); // the copy is always removed
            Assert.Equal("The update wasn't installed: the installer isn't signed by RemoteHub's publisher.",
                UpdateService.DescribeInstallResult("refused signature"));

            // only administrators and SYSTEM may write the staging folder
            var rules = new DirectoryInfo(stage).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
            Assert.All(rules, r => Assert.Contains(r.IdentityReference.Value, new[] { "S-1-5-18", "S-1-5-32-544" }));
            Assert.True(new DirectoryInfo(stage).GetAccessControl().AreAccessRulesProtected);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }

    [Theory]
    [InlineData("installed 0", null)]
    [InlineData("installed 3010", null)]
    [InlineData("installed 1602", "cancelled")]
    [InlineData("installed 1603", "error 1603")]
    [InlineData("refused changed", "changed after RemoteHub had checked it")]
    [InlineData("failed the staging folder could not be locked down.", "couldn't be installed – RemoteHub wasn't updated (the staging folder could not be locked down)")]
    [InlineData("something else", null)]
    public void The_restarted_RemoteHub_says_why_an_update_did_not_install(string result, string? expected)
    {
        var message = UpdateService.DescribeInstallResult(result);
        if (expected is null) Assert.Null(message);
        else Assert.Contains(expected, message);
        Assert.Null(UpdateService.InstallResultMessage(@"..\..\Windows\win")); // only an id names a result file
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
    public void Telemetry_off_deletes_pending_crash_reports_and_the_install_id()
    {
        var oldFolder = CrashReporter.Folder;
        var dir = Path.Combine(Path.GetTempPath(), "rh-telemetry-off-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            CrashReporter.Folder = Path.Combine(dir, "crash");
            Directory.CreateDirectory(CrashReporter.Folder);
            var t = DateTime.UtcNow;
            File.WriteAllText(Path.Combine(CrashReporter.Folder, $"crash-{t.Ticks}-{Guid.NewGuid():N}.json"),
                JsonSerializer.Serialize(new CrashReport { Version = "0.9.0", ErrorType = "E", Message = "m", Stack = "s", TimeUtc = t }));
            var idPath = Path.Combine(dir, "install-id");
            Telemetry.GetOrCreateInstallId(idPath);

            Telemetry.DiscardLocalRecords(CrashReporter.TakePending(), idPath);

            Assert.Empty(Directory.GetFiles(CrashReporter.Folder, "crash-*.json"));
            Assert.False(File.Exists(idPath));
        }
        finally
        {
            CrashReporter.Folder = oldFolder;
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
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

    [Fact]
    public void Start_payload_adds_only_the_counts_as_numbers()
    {
        var counts = new Telemetry.UsageCounts(200, 100, 232, 12, 7);
        using var doc = JsonDocument.Parse(Telemetry.Payload("app_start", "0.9.0", "beta", Guid.NewGuid().ToString(), counts.ToFields()));
        Assert.Equal(["agents", "arch", "channel", "connections", "event", "folders", "installId", "os", "shares", "ssh", "version"],
            doc.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(200, doc.RootElement.GetProperty("connections").GetInt32());
        Assert.Equal(100, doc.RootElement.GetProperty("agents").GetInt32());
        Assert.Equal(232, doc.RootElement.GetProperty("shares").GetInt32());
        Assert.Equal(12, doc.RootElement.GetProperty("folders").GetInt32());
        Assert.Equal(7, doc.RootElement.GetProperty("ssh").GetInt32());
    }

    [Fact]
    public void Session_payload_adds_only_the_run_time()
    {
        var extra = new Dictionary<string, object> { ["runSeconds"] = 5400L };
        using var doc = JsonDocument.Parse(Telemetry.Payload("app_session", "0.9.0", "beta", Guid.NewGuid().ToString(), extra));
        Assert.Equal(["arch", "channel", "event", "installId", "os", "runSeconds", "version"],
            doc.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(5400, doc.RootElement.GetProperty("runSeconds").GetInt64());
    }

    [Fact]
    public void Counts_cover_nested_folders_agents_and_both_kinds_of_share()
    {
        var withAgent = new Connection { Name = "a", Host = "srv-a", Agent = new AgentDevice { Status = AgentStatus.Installed } };
        withAgent.Shares.Add(new NetworkShare { Name = "s1" });
        withAgent.Shares.Add(new NetworkShare { Name = "s2" });
        var enrolledOnly = new Connection { Name = "b", Host = "srv-b", Agent = new AgentDevice { Status = AgentStatus.NotInstalled } };
        var inner = new Folder { Name = "inner" };
        inner.Children.Add(enrolledOnly);
        inner.Children.Add(new NetworkShare { Name = "standalone" });
        inner.Children.Add(new NetworkShare { Name = "web01", Protocol = ShareProtocol.Ssh, Host = "web01" });
        var outer = new Folder { Name = "outer" };
        outer.Children.Add(withAgent);
        outer.Children.Add(inner);

        Assert.Equal(new Telemetry.UsageCounts(Connections: 3, Agents: 1, Shares: 3, Folders: 2, Ssh: 1),
            Telemetry.Count([outer, new Connection { Name = "top", Host = "srv-c" }]));
        Assert.Equal(new Telemetry.UsageCounts(0, 0, 0, 0), Telemetry.Count([]));
    }

    [Fact]
    public void Run_time_skips_sleep_and_is_taken_once()
    {
        var max = TimeSpan.FromMinutes(2);
        Assert.Equal(TimeSpan.FromSeconds(61), Telemetry.RunTime.Step(TimeSpan.FromSeconds(61), max));
        Assert.Equal(max, Telemetry.RunTime.Step(TimeSpan.FromHours(8), max));          // PC slept with RemoteHub open
        Assert.Equal(TimeSpan.Zero, Telemetry.RunTime.Step(TimeSpan.FromSeconds(-5), max));

        var old = Telemetry.RunTime.FilePath;
        var dir = Path.Combine(Path.GetTempPath(), "rh-runtime-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Telemetry.RunTime.FilePath = Path.Combine(dir, "run-time.json");
            Assert.Null(Telemetry.RunTime.TakePrevious());
            Directory.CreateDirectory(dir);
            File.WriteAllText(Telemetry.RunTime.FilePath, """{"Version":"0.9.0","Seconds":3600}""");
            Assert.Equal(new Telemetry.RunTime.Session("0.9.0", 3600), Telemetry.RunTime.TakePrevious());
            Assert.Null(Telemetry.RunTime.TakePrevious());                                  // sent once, not again
            File.WriteAllText(Telemetry.RunTime.FilePath, "garbage");
            Assert.Null(Telemetry.RunTime.TakePrevious());
            Assert.False(File.Exists(Telemetry.RunTime.FilePath));
        }
        finally
        {
            Telemetry.RunTime.FilePath = old;
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }
}
