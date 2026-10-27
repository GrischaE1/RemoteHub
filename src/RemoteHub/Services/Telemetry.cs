using System.Net.Http;
using System.Threading;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using RemoteHub.Models;

namespace RemoteHub.Services;

/// <summary>
/// Anonymous usage data (and crash reports, see <see cref="CrashReporter"/>): small POSTs at start-up to
/// update.remotehub.dev/telemetry, fire-and-forget with a short timeout; failures are swallowed. "app_start" carries how
/// many connections, agents, shares and folders there are (counts only); "app_session" how long the previous session
/// ran. The install ID is a random GUID created once on this PC – never derived from the computer name, MAC address or
/// machine SID – so it only counts installs and can't identify a machine or person. See PRIVACY.md.
/// </summary>
public static class Telemetry
{
    public const string Endpoint = UpdateService.BaseUrl + "/telemetry";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>%LOCALAPPDATA%\RemoteHub\install-id: per PC and Windows user, not roamed with the profile.</summary>
    public static string InstallIdPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteHub", "install-id");

    public static bool Enabled(UiState ui) => AppPolicy.Telemetry ?? ui.TelemetryEnabled;

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

    /// <summary>
    /// Telemetry is off – by the setting or the DisableTelemetry policy: pending crash reports and the install ID are
    /// deleted, so nothing that could link this install to earlier data stays on the PC (PRIVACY.md).
    /// </summary>
    internal static void DiscardLocalRecords(IEnumerable<CrashReport> crashes, string? installIdPath = null)
    {
        CrashReporter.Discard(crashes);
        ForgetInstallId(installIdPath);
    }

    /// <summary>
    /// Exactly what is sent – nothing else. <paramref name="extra"/>: the counts of "app_start", the run time of
    /// "app_session" or the crash fields (errorType, message, stack).
    /// </summary>
    internal static byte[] Payload(string evt, string version, string channel, string installId, IReadOnlyDictionary<string, object>? extra = null)
    {
        var body = new Dictionary<string, object>
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

    /// <summary>How many of each kind of item the navigation holds – numbers only, no names, hosts or settings.</summary>
    public sealed record UsageCounts(int Connections, int Agents, int Shares, int Folders, int Ssh = 0)
    {
        public IReadOnlyDictionary<string, object> ToFields() => new Dictionary<string, object>
        {
            ["connections"] = Connections,
            ["agents"] = Agents,
            ["shares"] = Shares,
            ["folders"] = Folders,
            ["ssh"] = Ssh,
        };
    }

    /// <summary>
    /// Counts the tree: every connection, those with an installed RemoteHub Agent, every network share (below a server or
    /// on its own), every SSH connection and every folder. Call on the UI thread (the collections are the view model's).
    /// </summary>
    public static UsageCounts Count(IEnumerable<NodeBase> items)
    {
        int connections = 0, agents = 0, shares = 0, folders = 0, ssh = 0;
        void Walk(IEnumerable<NodeBase> nodes)
        {
            foreach (var n in nodes)
            {
                switch (n)
                {
                    case Folder f: folders++; Walk(f.Children); break;
                    case Connection c:
                        connections++;
                        if (c.Agent is { Status: AgentStatus.Installed }) agents++;
                        shares += c.Shares.Count;
                        Walk(c.Children);
                        break;
                    case NetworkShare { IsSsh: true }: ssh++; break;
                    case NetworkShare: shares++; break;
                }
            }
        }
        Walk(items);
        return new UsageCounts(connections, agents, shares, folders, ssh);
    }

    /// <summary>
    /// At start-up, in the background if telemetry is on: "app_start" (with <paramref name="counts"/>), "app_session" for
    /// the previous session's run time, then an "app_crash" for each crash of an earlier session that
    /// <see cref="CrashReporter"/> recorded. Never throws, never waits. With telemetry off, recorded crashes, the
    /// previous run time and the install ID are discarded and this session's run time isn't recorded.
    /// </summary>
    public static void AppStart(UiState ui, UsageCounts counts)
    {
        // Fire-and-forget on a background thread: the policy lookup, files and network calls never touch the UI thread.
        _ = Task.Run(async () =>
        {
            try
            {
                var crashes = CrashReporter.TakePending();
                var previous = RunTime.TakePrevious();
                if (!Enabled(ui)) { DiscardLocalRecords(crashes); return; }
                RunTime.Start();
                var channel = UpdateService.EffectiveChannel(ui);
                var id = GetOrCreateInstallId();
                await SendAsync(Payload("app_start", AppVersion.Current.ToString(), channel, id, counts.ToFields())).ConfigureAwait(false);
                if (previous is { Seconds: > 0 })
                    await SendAsync(Payload("app_session", previous.Version, channel, id, new Dictionary<string, object> { ["runSeconds"] = previous.Seconds })).ConfigureAwait(false);
                foreach (var crash in crashes)
                {
                    var extra = new Dictionary<string, object> { ["errorType"] = crash.ErrorType, ["message"] = crash.Message, ["stack"] = crash.Stack };
                    // Kept for the next start if it couldn't be sent (CrashReporter ages old ones out).
                    if (await SendAsync(Payload("app_crash", crash.Version, channel, id, extra)).ConfigureAwait(false)) CrashReporter.Sent(crash);
                }
            }
            catch { /* telemetry is invisible: success or failure doesn't matter to the user */ }
        });
    }

    /// <summary>
    /// How long RemoteHub runs: %LOCALAPPDATA%\RemoteHub\run-time.json holds this session's version and seconds so far,
    /// updated once a minute and at a clean exit, and is sent at the next start (so a crash or power loss still counts up
    /// to the last minute). Time the PC sleeps doesn't count: a tick adds at most two minutes.
    /// </summary>
    public static class RunTime
    {
        internal sealed record Session(string Version, long Seconds);

        /// <summary>Settable for tests.</summary>
        internal static string FilePath { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteHub", "run-time.json");

        private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1), MaxStep = TimeSpan.FromMinutes(2);
        private static readonly object Gate = new();
        private static Timer? _timer;
        private static long _seconds, _lastTick;
        private static bool _running;

        /// <summary>The last session's run time (the file is removed), or null if none was recorded.</summary>
        internal static Session? TakePrevious()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var s = JsonSerializer.Deserialize<Session>(File.ReadAllText(FilePath));
                File.Delete(FilePath);
                return s;
            }
            catch
            {
                try { File.Delete(FilePath); } catch { /* ignore */ }
                return null;
            }
        }

        internal static void Start()
        {
            lock (Gate)
            {
                if (_running) return;
                _running = true;
                _seconds = 0;
                _lastTick = Environment.TickCount64;
                Save();
                _timer = new Timer(_ => Update(), null, Tick, Tick);
            }
        }

        /// <summary>Clean exit: the last partial minute is added. Does nothing if run time isn't recorded this session.</summary>
        public static void Stop()
        {
            lock (Gate)
            {
                if (!_running) return;
                _timer?.Dispose();
                _timer = null;
                Advance();
                Save();
                _running = false;
            }
        }

        /// <summary>Telemetry was turned off: stop recording and remove what this session recorded.</summary>
        public static void Forget()
        {
            lock (Gate)
            {
                _timer?.Dispose();
                _timer = null;
                _running = false;
                try { File.Delete(FilePath); } catch { /* best effort */ }
            }
        }

        private static void Update()
        {
            lock (Gate)
            {
                if (!_running) return;
                Advance();
                Save();
            }
        }

        private static void Advance()
        {
            var now = Environment.TickCount64;
            _seconds += (long)Step(TimeSpan.FromMilliseconds(now - _lastTick), MaxStep).TotalSeconds;
            _lastTick = now;
        }

        /// <summary>The time a tick adds: the elapsed time, but no more than <paramref name="max"/> (the PC was asleep).</summary>
        internal static TimeSpan Step(TimeSpan elapsed, TimeSpan max) => elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed > max ? max : elapsed;

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(new Session(AppVersion.Current.ToString(), _seconds)));
            }
            catch { /* not recorded this time */ }
        }
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
