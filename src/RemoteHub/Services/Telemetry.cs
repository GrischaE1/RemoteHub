using System.Net.Http;
using System.Threading;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using RemoteHub.Models;

namespace RemoteHub.Services;

/// <summary>
/// Anonymous start counter (and crash reports, see <see cref="CrashReporter"/>): one small POST per start to update.remotehub.dev/telemetry, fire-and-forget with a short
/// timeout; failures are swallowed. The install ID is a random GUID created once on this PC – never derived from the
/// computer name, MAC address or machine SID – so it only counts installs and can't identify a machine or person.
/// </summary>
public static class Telemetry
{
    public const string Endpoint = UpdateService.BaseUrl + "/telemetry";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>%LOCALAPPDATA%\RemoteHub\install-id: per PC and Windows user, not roamed with the profile.</summary>
    public static string InstallIdPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteHub", "install-id");

    public static bool Enabled(UiState ui) => !AppPolicy.TelemetryDisabled && ui.TelemetryEnabled;

    /// <summary>The stored install ID, or a new random one (stored for next time).</summary>
    public static string GetOrCreateInstallId(string? path = null)
    {
        path ??= InstallIdPath;
        try
        {
            if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var existing) && existing != Guid.Empty)
                return existing.ToString("D");
        }
        catch { /* unreadable – make a new one */ }
        var id = Guid.NewGuid().ToString("D");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, id);
        }
        catch { /* not stored: counts as a new install next time, which is harmless */ }
        return id;
    }

    /// <summary>Removes the install ID (when telemetry is turned off), so turning it on again starts as a new, unrelated install.</summary>
    public static void ForgetInstallId(string? path = null)
    {
        try { File.Delete(path ?? InstallIdPath); } catch { /* best effort */ }
    }

    /// <summary>Exactly what is sent – nothing else. <paramref name="extra"/>: the crash fields (errorType, message, stack).</summary>
    internal static byte[] Payload(string evt, string version, string channel, string installId, IReadOnlyDictionary<string, string>? extra = null)
    {
        var body = new Dictionary<string, string>
        {
            ["event"] = evt,
            ["version"] = version,
            ["channel"] = channel,
            ["os"] = OsName(),
            ["arch"] = Arch(),
            ["installId"] = installId,
        };
        if (extra is not null) foreach (var (k, v) in extra) body[k] = v;
        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    /// <summary>"Windows 11", "Windows 10" or "Windows Server" – no build or edition details.</summary>
    internal static string OsName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (k?.GetValue("InstallationType") is string t && t.Equals("Server", StringComparison.OrdinalIgnoreCase)) return "Windows Server";
        }
        catch { /* fall through */ }
        return Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10";
    }

    internal static string Arch() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        var a => a.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// At start-up, in the background if telemetry is on: "app_start", then an "app_crash" for each crash of an earlier
    /// session that <see cref="CrashReporter"/> recorded. Never throws, never waits. With telemetry off, recorded crashes
    /// are discarded.
    /// </summary>
    public static void AppStart(UiState ui)
    {
        // Fire-and-forget on a background thread: the policy lookup, files and network calls never touch the UI thread.
        _ = Task.Run(async () =>
        {
            try
            {
                var crashes = CrashReporter.TakePending();
                if (!Enabled(ui)) { CrashReporter.Discard(crashes); return; }
                var channel = UpdateService.EffectiveChannel(ui);
                var id = GetOrCreateInstallId();
                await SendAsync(Payload("app_start", AppVersion.Current.ToString(), channel, id)).ConfigureAwait(false);
                foreach (var crash in crashes)
                {
                    var extra = new Dictionary<string, string> { ["errorType"] = crash.ErrorType, ["message"] = crash.Message, ["stack"] = crash.Stack };
                    // Kept for the next start if it couldn't be sent (CrashReporter ages old ones out).
                    if (await SendAsync(Payload("app_crash", crash.Version, channel, id, extra)).ConfigureAwait(false)) CrashReporter.Sent(crash);
                }
            }
            catch { /* telemetry is invisible: success or failure doesn't matter to the user */ }
        });
    }

    /// <summary>One POST with a short deadline. True when the server accepted it.</summary>
    private static async Task<bool> SendAsync(byte[] body)
    {
#if DEBUG
        DiagLog.Write("Telemetry: not sent from a debug build");
        await Task.CompletedTask;
        return false;
#else
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var response = await UpdateService.Http.PostAsync(Endpoint, content, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
#endif
    }
}
