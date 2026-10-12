using System.Diagnostics.Eventing.Reader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

namespace RemoteHub.Services;

/// <summary>A crash of an earlier session, waiting to be sent as "app_crash".</summary>
public sealed class CrashReport
{
    public string Version { get; set; } = "";
    public string ErrorType { get; set; } = "";
    public string Message { get; set; } = "";
    public string Stack { get; set; } = "";
    public DateTime TimeUtc { get; set; }
    [JsonIgnore] public string? FilePath { get; set; }
}

/// <summary>
/// Records crashes locally and hands them to <see cref="Telemetry"/> at the next start – nothing is sent while the
/// process is going down.
/// <list type="bullet">
/// <item>Unhandled .NET exception: a small JSON file (version, error type, redacted message and stack) is written.</item>
/// <item>Native crash (e.g. inside the RDP client) or any other abnormal end: the "running" marker written at start is
/// still there next time. Windows' Application Error event (1000) of that session then names the faulting module.</item>
/// </list>
/// Messages and stacks are redacted: paths, user / computer / domain names, host names, IP and e-mail addresses and
/// quoted values are removed, so a report can't identify a server, a machine or a person.
/// </summary>
public static class CrashReporter
{
    /// <summary>%LOCALAPPDATA%\RemoteHub\crash (settable for tests).</summary>
    internal static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteHub", "crash");

    private static string SessionMarker => Path.Combine(Folder, "session.json");
    private const int MaxPending = 5, MaxMessage = 500, MaxStack = 8000;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal sealed record Session(DateTime StartedUtc, int Pid, string Version);

    private static Session? _previous;   // the last session, if it didn't end cleanly
    private static int _recorded;

    /// <summary>Call once at start-up: notes an unclean end of the last session, marks this one as running, hooks the handler.</summary>
    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (File.Exists(SessionMarker))
                _previous = JsonSerializer.Deserialize<Session>(File.ReadAllText(SessionMarker));
            File.WriteAllText(SessionMarker, JsonSerializer.Serialize(new Session(DateTime.UtcNow, Environment.ProcessId, AppVersion.Current.ToString())));
        }
        catch { /* no crash tracking this time */ }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Record(ex); };
    }

    /// <summary>Clean exit: the session didn't crash.</summary>
    public static void Stop()
    {
        try { File.Delete(SessionMarker); } catch { /* best effort */ }
    }

    /// <summary>Called from the crash handler: local file only, synchronous, no network.</summary>
    internal static void Record(Exception ex)
    {
        if (Interlocked.Exchange(ref _recorded, 1) == 1) return;
        try
        {
            Write(FromException(ex, AppVersion.Current.ToString(), DateTime.UtcNow));
            File.Delete(SessionMarker); // recorded with details – not also an "unexpected exit"
        }
        catch { /* nothing more can be done while crashing */ }
    }

    private static int _handled;

    /// <summary>
    /// An unexpected error the app survived (the user saw a message): recorded like a crash, so it's reported at the next
    /// start too – at most three per session, so one repeating error doesn't fill the folder.
    /// </summary>
    public static void RecordHandled(Exception ex)
    {
        if (Interlocked.Increment(ref _handled) > 3) return;
        try { Write(FromException(ex, AppVersion.Current.ToString(), DateTime.UtcNow)); }
        catch { /* best effort */ }
    }

    private static void Write(CrashReport report)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, $"crash-{report.TimeUtc.Ticks}-{Guid.NewGuid():N}.json"), JsonSerializer.Serialize(report, Json));
    }

    internal static CrashReport FromException(Exception ex, string version, DateTime timeUtc)
    {
        var stack = new StringBuilder(ex.StackTrace ?? "");
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            stack.Append("\n--- inner ").Append(inner.GetType().FullName).Append(" ---\n").Append(inner.StackTrace);
        return new CrashReport
        {
            Version = version,
            ErrorType = ex.GetType().FullName ?? ex.GetType().Name,
            Message = Redact(ex.Message, MaxMessage),
            Stack = Redact(stack.ToString().Trim(), MaxStack),
            TimeUtc = timeUtc,
        };
    }

    // ------------------------------------------------------------------ next start

    /// <summary>
    /// Crashes waiting to be sent (oldest first): files of earlier crashes plus an unclean end of the last session.
    /// Old ones (over 30 days, or beyond the newest 5) are deleted. Runs on a background thread.
    /// </summary>
    public static List<CrashReport> TakePending()
    {
        try
        {
            if (Interlocked.Exchange(ref _previous, null) is { } previous) Write(UnexpectedExit(previous));
            var reports = new List<CrashReport>();
            foreach (var file in Directory.GetFiles(Folder, "crash-*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    var r = JsonSerializer.Deserialize<CrashReport>(File.ReadAllText(file));
                    if (r is null || DateTime.UtcNow - r.TimeUtc > MaxAge) { File.Delete(file); continue; }
                    r.FilePath = file;
                    reports.Add(r);
                }
                catch { try { File.Delete(file); } catch { /* ignore */ } }
            }
            foreach (var old in reports.Take(Math.Max(0, reports.Count - MaxPending))) Discard([old]);
            return reports.TakeLast(MaxPending).ToList();
        }
        catch { return []; }
    }

    public static void Sent(CrashReport report) => Discard([report]);

    /// <summary>Deletes reports (sent, or telemetry is off – crashes aren't kept around then).</summary>
    public static void Discard(IEnumerable<CrashReport> reports)
    {
        foreach (var r in reports)
            try { if (r.FilePath is not null) File.Delete(r.FilePath); } catch { /* best effort */ }
    }

    /// <summary>The last session ended without a clean exit: look for Windows' record of a native crash of RemoteHub.</summary>
    internal static CrashReport UnexpectedExit(Session s)
    {
        try
        {
            var since = s.StartedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[Provider[@Name='Application Error'] and (EventID=1000) and TimeCreated[@SystemTime>='{since}']]]");
            using var reader = new EventLogReader(query);
            for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
            {
                using (e)
                {
                    var p = e.Properties;
                    if (p.Count < 8 || !string.Equals(p[0].Value?.ToString(), "RemoteHub.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    var module = Path.GetFileName(p[3].Value?.ToString() ?? "unknown");
                    var moduleVersion = p[4].Value?.ToString();
                    var code = Hex(p[6].Value);
                    var offset = Hex(p[7].Value);
                    return new CrashReport
                    {
                        Version = s.Version,
                        ErrorType = "NativeCrash",
                        Message = Redact($"Exception {code} in {module} {moduleVersion}".Trim(), MaxMessage),
                        Stack = Redact($"{module}+{offset}", MaxStack),
                        TimeUtc = e.TimeCreated?.ToUniversalTime() ?? s.StartedUtc,
                    };
                }
            }
        }
        catch { /* event log not readable – report without details */ }
        return new CrashReport
        {
            Version = s.Version,
            ErrorType = "UnexpectedExit",
            Message = "The previous session ended without closing normally and Windows recorded no crash details (e.g. ended in Task Manager, power loss).",
            Stack = "",
            TimeUtc = s.StartedUtc,
        };
    }

    private static string Hex(object? value) => value switch
    {
        uint u => $"0x{u:x8}",
        int i => $"0x{i:x8}",
        ulong ul => $"0x{ul:x}",
        long l => $"0x{l:x}",
        string str when str.StartsWith("0x", StringComparison.OrdinalIgnoreCase) => str.ToLowerInvariant(),
        string str => "0x" + str.ToLowerInvariant(),
        _ => "unknown",
    };

    // ------------------------------------------------------------------ redaction

    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        (new(@"\\\\[^\s'""<>|]+", RegexOptions.Compiled), "[path]"),                                   // \\server\share\...
        (new(@"\b[A-Za-z]:\\[^\s'""<>|]*", RegexOptions.Compiled), "[path]"),                         // C:\...
        (new(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+", RegexOptions.Compiled), "[email]"),
        (new(@"\b\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.Compiled), "[ip]"),
        (new(@"\b(?:[0-9A-Fa-f]{1,4}:){3,7}[0-9A-Fa-f]{1,4}\b", RegexOptions.Compiled), "[ip]"),
        // Shortened IPv6 (fe80::1, ::1, 2001:db8::5%12): only hex digits and colons, with "::" somewhere.
        (new(@"(?<![\w:])(?=[0-9A-Fa-f:]*::)[0-9A-Fa-f:]{2,39}(?:%\w+)?(?![\w:])", RegexOptions.Compiled), "[ip]"),
        // DOMAIN\user and COMPUTER\user of anyone (after the paths, which also contain backslashes).
        (new(@"\b[A-Za-z0-9][\w.-]{0,14}\\[\w.$-]+", RegexOptions.Compiled), "[account]"),
        (new(@"'[^']*'|""[^""]*""|“[^”]*”|‘[^’]*’", RegexOptions.Compiled), "'…'"),                        // quoted values
    ];

    /// <summary>Host names: dotted names with a typical suffix, in any case (SRV01.CONTOSO.COM, Web01.Contoso.local).</summary>
    private static readonly Regex HostName = new(
        @"\b[a-z0-9-]+(?:\.[a-z0-9-]+)*\.(?:local|lan|corp|internal|intra|intranet|home|domain|ad|com|net|org|de|eu|io|dev|cloud|biz|info|uk|us|ch|at|nl|fr)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>.NET namespaces look like host names (System.Net, Microsoft.Win32.dev…) and are what makes a report useful.</summary>
    private static readonly string[] CodeNamespaces = ["System.", "Microsoft.", "Windows.", "RemoteHub.", "MS.", "Wpf.", "Interop.", "CommunityToolkit."];

    /// <summary>Removes anything that could identify a server, a machine or a person, and limits the length.</summary>
    internal static string Redact(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text;
        foreach (var (value, label) in new[] { (Environment.UserName, "[user]"), (Environment.MachineName, "[computer]"), (Environment.UserDomainName, "[domain]") })
        {
            // Short names only as whole words, so "jd" doesn't eat parts of other words.
            if (value is { Length: >= 3 }) s = Regex.Replace(s, Regex.Escape(value), label, RegexOptions.IgnoreCase);
            else if (value is { Length: 2 }) s = Regex.Replace(s, $@"\b{Regex.Escape(value)}\b", label, RegexOptions.IgnoreCase);
        }
        foreach (var (pattern, replacement) in Rules) s = pattern.Replace(s, replacement);
        s = HostName.Replace(s, m => CodeNamespaces.Any(n => m.Value.StartsWith(n, StringComparison.OrdinalIgnoreCase)) ? m.Value : "[host]");
        return s.Length <= max ? s : s[..max] + "…";
    }
}
