using System.Diagnostics;
using System.Threading;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteHub.Models;

namespace RemoteHub.Services;

public sealed record UpdateDownload(string? Name, string? Url, string? Sha256, long Size);

/// <summary>The answer of update.remotehub.dev/latest.</summary>
public sealed record UpdateInfo(
    string Version, string? Channel, bool Prerelease, string? Notes, DateTimeOffset? PublishedAt,
    bool Mandatory, string? MinSupportedVersion, UpdateDownload? Download)
{
    [JsonIgnore] public AppVersion? ParsedVersion => AppVersion.Parse(Version);
}

public enum UpdateStatus { UpToDate, Available, NoRelease, Failed }

public sealed record UpdateCheckResult(UpdateStatus Status, UpdateInfo? Info = null, string? Error = null)
{
    /// <summary>The server marks the update as required, or this version is below its minimum supported version.</summary>
    public bool Required => Status == UpdateStatus.Available && Info is { } i &&
        (i.Mandatory || AppVersion.Parse(i.MinSupportedVersion) is { } min && UpdateService.Current < min);
}

/// <summary>
/// Update check against update.remotehub.dev and installing a newer MSI. Only a higher version than the running one is
/// ever offered or installed; the download must match the SHA-256 and size the server announced and, when the running
/// build is signed, be signed by the same publisher (checked again by the elevated step on a copy only it can write).
/// </summary>
public static class UpdateService
{
    public const string Stable = "stable", Beta = "beta";
    public const string BaseUrl = "https://update.remotehub.dev";
    public const string ReleasesPage = "https://github.com/GrischaE1/RemoteHub/releases";

    /// <summary>The running version (settable for tests).</summary>
    internal static AppVersion Current { get; set; } = AppVersion.Current;

    internal static HttpClient Http { get; set; } = CreateClient();

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan, // each call brings its own deadline
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RemoteHub", AppVersion.Current.ToString()));
        return http;
    }

    // ------------------------------------------------------------------ settings

    public static string? NormalizeChannel(string? channel) => channel?.Trim().ToLowerInvariant() switch
    {
        "stable" or "ga" or "release" => Stable,
        "beta" or "prerelease" or "pre-release" => Beta,
        _ => null,
    };

    /// <summary>Beta while the running version is a pre-release or below 1.0 (there is no GA release before 1.0.0).</summary>
    public static string DefaultChannel => Current.IsPreRelease || Current.Major == 0 ? Beta : Stable;

    /// <summary>The channel in use: IT policy, else the user's choice, else <see cref="DefaultChannel"/>.</summary>
    public static string EffectiveChannel(UiState ui) => AppPolicy.UpdateChannel ?? NormalizeChannel(ui.UpdateChannel) ?? DefaultChannel;

    public static bool CheckEnabled(UiState ui) => AppPolicy.UpdateCheck ?? ui.UpdateCheckEnabled;

    public static string ChannelName(string channel) => channel == Beta ? "Beta" : "GA (stable)";

    public static Uri LatestUri(string channel) => new(channel == Beta ? $"{BaseUrl}/latest?channel=beta" : $"{BaseUrl}/latest");

    // ------------------------------------------------------------------ check

    /// <summary>
    /// The update for <paramref name="channel"/>. Beta also asks for the newest GA release, because a GA release can be
    /// newer than the last pre-release (0.21.0 after 0.21.0-beta.2), and Beta must not stay behind it.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(string channel, CancellationToken ct = default)
    {
        if (channel != Beta) return await CheckChannelAsync(Stable, ct).ConfigureAwait(false);
        var beta = CheckChannelAsync(Beta, ct);
        var ga = CheckChannelAsync(Stable, ct);
        return Newer(await beta.ConfigureAwait(false), await ga.ConfigureAwait(false));
    }

    /// <summary>
    /// Beta's answer, unless GA offers a higher version. GA only adds: when it can't be reached, Beta's answer stands,
    /// and a Beta failure isn't hidden behind a GA "up to date".
    /// </summary>
    internal static UpdateCheckResult Newer(UpdateCheckResult beta, UpdateCheckResult ga)
    {
        // An Available result always carries a parsed version (Evaluate only gives Available for one).
        if (ga.Status == UpdateStatus.Available && (beta.Status != UpdateStatus.Available || ga.Info!.ParsedVersion! > beta.Info!.ParsedVersion!))
            return ga;
        if (beta.Status == UpdateStatus.NoRelease && ga.Status == UpdateStatus.UpToDate) return ga;
        return beta;
    }

    private static async Task<UpdateCheckResult> CheckChannelAsync(string channel, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await Http.GetAsync(LatestUri(channel), cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(UpdateStatus.NoRelease);
            if (!response.IsSuccessStatusCode) return new(UpdateStatus.Failed, Error: $"The update server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            return Evaluate(Parse(body));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(UpdateStatus.Failed, Error: "The update server didn't answer in time.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException)
        {
            return new(UpdateStatus.Failed, Error: ex is HttpRequestException ? "The update server couldn't be reached: " + ex.Message : "The update server sent an answer RemoteHub doesn't understand.");
        }
    }

    internal static UpdateInfo Parse(string json) =>
        JsonSerializer.Deserialize<UpdateInfo>(json, Json) is { Version.Length: > 0 } info && info.ParsedVersion is not null
            ? info : throw new FormatException("No valid version in the update information.");

    /// <summary>Only a higher version than the running one counts as an update.</summary>
    internal static UpdateCheckResult Evaluate(UpdateInfo info) =>
        info.ParsedVersion is { } v && v > Current ? new(UpdateStatus.Available, info) : new(UpdateStatus.UpToDate, info);

    // ------------------------------------------------------------------ download & install

    /// <summary>RemoteHub's own GitHub repository (GrischaE1/RemoteHub) – its id never changes, even if it is renamed.</summary>
    private const string RepositoryId = "1409080301";

    /// <summary>Where GitHub may redirect a release download to (its file storage).</summary>
    private static readonly string[] RedirectHosts = ["github.com", "githubusercontent.com", "remotehub.dev"];

    /// <summary>
    /// The download must be one of RemoteHub's own release files over HTTPS: from remotehub.dev, or a release asset of
    /// RemoteHub's GitHub repository – not just anything on GitHub, where anyone can publish files.
    /// </summary>
    internal static bool IsAllowedDownload(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        var path = uri.AbsolutePath;
        if (IsHost(host, "remotehub.dev")) return true;
        if (host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            return path.StartsWith($"/repositories/{RepositoryId}/releases/assets/", StringComparison.Ordinal)
                || path.StartsWith("/repos/GrischaE1/RemoteHub/releases/assets/", StringComparison.OrdinalIgnoreCase);
        if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return path.StartsWith("/GrischaE1/RemoteHub/releases/download/", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    /// <summary>Where the download may end up after redirects (the checksum is what proves the file).</summary>
    internal static bool IsAllowedRedirect(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && RedirectHosts.Any(h => IsHost(uri.Host, h));

    private static bool IsHost(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    public static string DownloadFolder => Path.Combine(Path.GetTempPath(), "RemoteHub", "Updates");

    /// <summary>
    /// Downloads the MSI of <paramref name="info"/> and checks it against the announced SHA-256 and size.
    /// Returns the path of the verified file; throws with a readable message otherwise (nothing is kept then).
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct, string? folder = null)
    {
        if (info.ParsedVersion is not { } version || version <= Current)
            throw new InvalidOperationException("Only a newer version than the running one can be installed.");
        var d = info.Download ?? throw new InvalidOperationException("The update has no download.");
        if (!Uri.TryCreate(d.Url, UriKind.Absolute, out var uri) || !IsAllowedDownload(uri))
            throw new InvalidOperationException("The download address isn't trusted (only RemoteHub's own releases on GitHub or remotehub.dev, over HTTPS).");
        if (d.Sha256 is not { Length: 64 } expectedHex || !expectedHex.All(Uri.IsHexDigit))
            throw new InvalidOperationException("The update has no valid SHA-256 checksum, so it can't be verified.");

        folder ??= DownloadFolder;
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(d.Name ?? "");
        if (!name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            name = $"RemoteHub-{version}-x64.msi";
        var target = Path.Combine(folder, name);
        var partial = target + ".partial";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } final && !IsAllowedRedirect(final))
                throw new InvalidOperationException("The download was redirected to an address that isn't trusted.");
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"The download isn't available ({(int)response.StatusCode} {response.ReasonPhrase}).");

            var total = d.Size > 0 ? d.Size : response.Content.Headers.ContentLength ?? 0;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long read = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    read += n;
                    if (d.Size > 0 && read > d.Size) throw new InvalidDataException("The download is larger than announced.");
                    sha.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    if (total > 0) progress?.Report(Math.Min(1, read / (double)total));
                }
            }
            if (d.Size > 0 && read != d.Size) throw new InvalidDataException($"The download is incomplete ({read:N0} of {d.Size:N0} bytes).");
            var actual = Convert.ToHexString(sha.GetHashAndReset());
            if (!actual.Equals(expectedHex, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded file doesn't match the published SHA-256 checksum. It was deleted and won't be installed.");
            File.Move(partial, target, overwrite: true);
            // The checksum comes from the same server as the file: only the signature proves who made it. Checked here,
            // before Windows asks for approval, and again by the elevated step on its own copy (InstallCommand).
            if (Authenticode.Refusal(target, Authenticode.RunningPublisher) is { } why)
            {
                try { File.Delete(target); } catch { /* best effort */ }
                throw new InvalidDataException($"The downloaded installer {why}. It was deleted and won't be installed.");
            }
            return target;
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { /* best effort */ }
        }
    }

    /// <summary>Where the MSI installs RemoteHub (per-machine, Program Files).</summary>
    public static string InstalledExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteHub", "RemoteHub.exe");

    /// <summary>
    /// Where the elevated step keeps its copy of the MSI: under Program Files, so only administrators and SYSTEM can
    /// write there from the moment the folder exists. The download folder in %TEMP% is the user's: anything running as
    /// the user could swap the file there between the checks and the install.
    /// </summary>
    public static string StagingFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteHub Updates");

    /// <summary>The elevated step's outcomes, one file per install attempt; readable by users, written only by the step.</summary>
    public static string ResultsFolder => Path.Combine(StagingFolder, "results");

    /// <summary>
    /// The elevated step (Windows PowerShell 5.1, given as the command line, so nothing on disk can change it): waits for
    /// RemoteHub to exit, copies the MSI into <see cref="StagingFolder"/> (administrators and SYSTEM only), checks the
    /// copy's SHA-256 and – when the running build is signed – its signature and publisher again, installs that copy and
    /// writes the outcome to <see cref="ResultsFolder"/>. Only cmdlets and icacls, so it also runs in Constrained Language Mode.
    /// </summary>
    internal static string InstallCommand(string msi, string sha256, string? publisher, int pid, string resultId, string? stage = null)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        return string.Join("\n",
            "$ErrorActionPreference = 'Stop'",
            // RemoteHub hosts PowerShell 7, which puts its own module folders into PSModulePath; inherited, Windows
            // PowerShell would look for Get-FileHash there and not find it. Its own folders only, and nothing a user can write.
            "$env:PSModulePath = \"$env:SystemRoot\\System32\\WindowsPowerShell\\v1.0\\Modules;$env:ProgramFiles\\WindowsPowerShell\\Modules\"",
            $"$source = {Q(msi)}; $sha = {Q(sha256.ToUpperInvariant())}; $publisher = {Q(publisher ?? "")}; $id = {Q(resultId)}",
            stage is null ? "$stage = Join-Path $env:ProgramFiles 'RemoteHub Updates'" : $"$stage = {Q(stage)}", // another folder: tests only
            "$results = Join-Path $stage 'results'",
            "$target = Join-Path $stage ($id + '.msi')",
            "function Result([string]$text) { Set-Content -LiteralPath (Join-Path $results ($id + '.txt')) -Value $text -Encoding UTF8 }",
            "try {",
            "  foreach ($dir in $stage, $results) {",
            "    if (Test-Path -LiteralPath $dir) { if (\"$((Get-Item -LiteralPath $dir -Force).Attributes)\" -match 'ReparsePoint') { throw \"$dir is a link\" } }",
            "    else { $null = New-Item -ItemType Directory -Path $dir }",
            "  }",
            // Administrators and SYSTEM only; users may read the outcomes, nothing else.
            "  $null = & \"$env:SystemRoot\\System32\\icacls.exe\" $stage /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F'",
            "  if ($LASTEXITCODE -ne 0) { throw 'the staging folder could not be locked down' }",
            "  $null = & \"$env:SystemRoot\\System32\\icacls.exe\" $results /grant '*S-1-5-32-545:(OI)(CI)RX'",
            "  Get-ChildItem -LiteralPath $stage -Filter *.msi -File -Force | Remove-Item -Force",
            "  Get-ChildItem -LiteralPath $results -File -Force | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-7) } | Remove-Item -Force",
            $"  Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue",
            "  Copy-Item -LiteralPath $source -Destination $target -Force",
            "  if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $sha) { Result 'refused changed'; return }",
            "  if ($publisher) {",
            "    $sig = Get-AuthenticodeSignature -LiteralPath $target",
            "    if ($sig.Status -ne 'Valid' -or -not $sig.SignerCertificate -or $sig.SignerCertificate.Subject -cne $publisher) { Result 'refused signature'; return }",
            "  }",
            "  $p = Start-Process -FilePath \"$env:SystemRoot\\System32\\msiexec.exe\" -ArgumentList '/i', ('\"' + $target + '\"'), '/passive', '/norestart' -Wait -PassThru",
            "  Result ('installed ' + $p.ExitCode)",
            "}",
            "catch { try { Result ('failed ' + $_.Exception.Message) } catch { } }",
            "finally { if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue } }");
    }

    /// <summary>
    /// The helper that starts RemoteHub again once the elevated step has ended (it runs as the user, so RemoteHub doesn't
    /// run elevated): the new version, or this one if the install didn't happen; it shows the step's outcome.
    /// </summary>
    internal static string RestartScript(int elevatedPid, string installedExe, string currentExe, string resultId)
    {
        static string Q(string path) => path.Replace("%", "%%"); // quoted paths can't contain '"'; only % needs escaping in a batch file
        return string.Join("\r\n",
            "@echo off",
            "chcp 65001 >nul",
            "set /a n=0",
            ":wait",
            $"tasklist /FI \"PID eq {elevatedPid}\" /NH 2>nul | findstr /I /C:\"powershell.exe\" >nul || goto start",
            "set /a n+=1",
            "if %n% GEQ 900 goto start",
            "ping -n 2 127.0.0.1 >nul",
            "goto wait",
            ":start",
            $"if exist \"{Q(installedExe)}\" (start \"\" \"{Q(installedExe)}\" {UpdateResultArg} {resultId}) else (start \"\" \"{Q(currentExe)}\" {UpdateResultArg} {resultId})",
            "");
    }

    /// <summary>The argument the restart helper passes, followed by the attempt's id.</summary>
    public const string UpdateResultArg = "--update-result";

    /// <summary>
    /// Starts the elevated install step – Windows asks for administrator approval now, while RemoteHub still runs – and
    /// the restart helper. Throws if the approval is declined; then nothing was started. The caller closes RemoteHub right after.
    /// </summary>
    public static void StartInstall(string msi, string sha256)
    {
        var id = Guid.NewGuid().ToString("N");
        var command = InstallCommand(msi, sha256, Authenticode.RunningPublisher, Environment.ProcessId, id);
        Process elevated;
        try
        {
            elevated = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new InvalidOperationException("The installer step didn't start.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            throw new InvalidOperationException("Administrator approval was declined – nothing was installed.");
        }
        using (elevated)
        {
            var current = Environment.ProcessPath ?? InstalledExe;
            var script = Path.Combine(Path.GetDirectoryName(msi)!, "install-update.cmd");
            File.WriteAllText(script, RestartScript(elevated.Id, InstalledExe, current, id), new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c \"\"{script}\"\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(msi)!,
            });
        }
    }

    /// <summary>
    /// What the user is told after the restart about install attempt <paramref name="resultId"/>, or null when there is
    /// nothing to say (installed, or no outcome recorded).
    /// </summary>
    public static string? InstallResultMessage(string resultId)
    {
        if (resultId.Length != 32 || !resultId.All(Uri.IsHexDigit)) return null;
        var file = Path.Combine(ResultsFolder, resultId + ".txt");
        try { return File.Exists(file) ? DescribeInstallResult(File.ReadAllText(file)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string? DescribeInstallResult(string result)
    {
        var text = result.Trim();
        if (text.StartsWith("installed ", StringComparison.Ordinal))
            return text[10..] switch
            {
                "0" or "3010" or "1641" => null,
                "1602" => "The update was cancelled in the installer – RemoteHub wasn't updated.",
                var code => $"The installer ended with error {code} – RemoteHub wasn't updated.",
            };
        return text switch
        {
            "refused changed" => "The update wasn't installed: the downloaded file changed after RemoteHub had checked it. Something on this PC " +
                                 "replaced it – nothing was installed. Download the update again.",
            "refused signature" => "The update wasn't installed: the installer isn't signed by RemoteHub's publisher.",
            // the elevated step's own error text (Windows PowerShell's, possibly in the system's language)
            _ when text.StartsWith("failed ", StringComparison.Ordinal) => $"The update couldn't be installed – RemoteHub wasn't updated ({text[7..].TrimEnd('.')}).",
            _ => null,
        };
    }

    /// <summary>Release notes as plain text (the server sends Markdown).</summary>
    public static string PlainNotes(string? notes) =>
        string.IsNullOrWhiteSpace(notes) ? "No release notes." : notes.Replace("**", "").Replace("\r\n", "\n").Trim();
}
