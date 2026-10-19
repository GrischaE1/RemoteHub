namespace RemoteHub.Services;

/// <summary>
/// Small local diagnostic log (%APPDATA%\RemoteHub\logs\remotehub.log), capped at ~1 MB and kept for
/// <see cref="RetentionDays"/>. Never write passwords, usernames or host names here – only states, counts and yes/no facts.
/// IT can turn it off (policy DiagnosticLog = 0, docs/admx-design.md §6.5).
/// </summary>
public static class DiagLog
{
    private static readonly object Gate = new();
    public static string FilePath => Path.Combine(SettingsStore.Directory, "logs", "remotehub.log");

    public const int DefaultRetentionDays = 30;

    /// <summary>The policy's days, else 30 (there is no user setting for it).</summary>
    public static int RetentionDays => AppPolicy.DiagnosticLogRetentionDays ?? DefaultRetentionDays;

    /// <summary>The policy as last read; a class, so it is replaced as a whole when another thread reads it again.</summary>
    private sealed record EnabledRead(long At, bool On);
    private static EnabledRead? _enabled; // null: not read yet

    /// <summary>Whether lines are written (policy DiagnosticLog; on by default). Read at most every 5 s: Write is called often.</summary>
    public static bool Enabled
    {
        get
        {
            if (_enabled is { } e && Environment.TickCount64 - e.At < 5000) return e.On;
            _enabled = e = new EnabledRead(Environment.TickCount64, AppPolicy.DiagnosticLog);
            return e.On;
        }
    }

    /// <summary>For tests: read the policy again at the next line.</summary>
    internal static void ForgetPolicy() => _enabled = null;

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }

    /// <summary>
    /// Removes what is older than <paramref name="days"/> (at start, in the background): the rotated file when it is older
    /// as a whole, and the old lines at the start of the current one. A line without a date (the rest of a message that had a
    /// line break) goes with the dated line before it.
    /// </summary>
    public static void ApplyRetention(int days, string? path = null)
    {
        path ??= FilePath;
        var cutoff = DateTime.Now.AddDays(-days);
        try
        {
            lock (Gate)
            {
                if (File.Exists(path + ".old") && File.GetLastWriteTime(path + ".old") < cutoff) File.Delete(path + ".old");
                if (!File.Exists(path)) return;
                var lines = File.ReadAllLines(path);
                // Lines are in time order: drop up to the first dated line that is new enough.
                var keepFrom = lines.Length;
                for (var i = 0; i < lines.Length; i++)
                    if (lines[i].Length >= 19 && DateTime.TryParseExact(lines[i][..19], "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var at) && at >= cutoff)
                    {
                        keepFrom = i;
                        break;
                    }
                if (keepFrom > 0) File.WriteAllLines(path, lines[keepFrom..]);
            }
        }
        catch { /* best effort, like writing */ }
    }
}
