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
/// ever offered or installed; the download must match the SHA-256 and size the server announced.
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

    public static async Task<UpdateCheckResult> CheckAsync(string channel, CancellationToken ct = default)
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
    /// The helper that runs once RemoteHub has exited: waits for this process to end, runs the MSI (Windows asks for
    /// administrator approval) and starts RemoteHub again – the new version, or this one if the install was cancelled.
    /// </summary>
    internal static string InstallScript(string msi, int pid, string installedExe, string currentExe)
    {
        static string Q(string path) => path.Replace("%", "%%"); // quoted paths can't contain '"'; only % needs escaping in a batch file
        return string.Join("\r\n",
            "@echo off",
            "chcp 65001 >nul",
            "set /a n=0",
            ":wait",
            $"tasklist /FI \"PID eq {pid}\" /NH 2>nul | findstr /I /C:\"RemoteHub.exe\" >nul || goto install",
            "set /a n+=1",
            "if %n% GEQ 60 goto install",
            "ping -n 2 127.0.0.1 >nul",
            "goto wait",
            ":install",
            $"\"%SystemRoot%\\System32\\msiexec.exe\" /i \"{Q(msi)}\" /passive /norestart",
            $"if exist \"{Q(installedExe)}\" (start \"\" \"{Q(installedExe)}\") else (start \"\" \"{Q(currentExe)}\")",
            "");
    }

    /// <summary>Starts the install helper. The caller closes RemoteHub right after.</summary>
    public static void StartInstall(string msi)
    {
        var current = Environment.ProcessPath ?? InstalledExe;
        var script = Path.Combine(Path.GetDirectoryName(msi)!, "install-update.cmd");
        File.WriteAllText(script, InstallScript(msi, Environment.ProcessId, InstalledExe, current), new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c \"\"{script}\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(msi)!,
        });
    }

    /// <summary>Release notes as plain text (the server sends Markdown).</summary>
    public static string PlainNotes(string? notes) =>
        string.IsNullOrWhiteSpace(notes) ? "No release notes." : notes.Replace("**", "").Replace("\r\n", "\n").Trim();
}
