using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RemoteHub.Services;

namespace RemoteHub.Data;

/// <summary>The events in the audit log, with how the viewer shows them.</summary>
public static class AuditEvents
{
    public const string AppUnlocked = "app.unlocked", AppUnlockFailed = "app.unlock_failed", ProtectionChanged = "app.protection_changed",
        AppLocked = "app.locked", AuditRetentionChanged = "audit.retention_changed";
    public const string SessionConnected = "session.connected", SessionEnded = "session.ended", SessionFailed = "session.failed";
    public const string PasswordCopied = "password.copied";
    public const string DataAdded = "data.added", DataChanged = "data.changed", DataDeleted = "data.deleted";
    public const string BackupExported = "backup.exported", BackupImported = "backup.imported";
    public const string AgentInstalled = "agent.installed";
    public const string FilesTransferred = "files.transferred";
    public const string RecordingStarted = "recording.started", RecordingStopped = "recording.stopped";
    public const string StorageMigrated = "storage.migrated", StorageCheckFailed = "storage.check_failed", StorageRestored = "storage.restored",
        StorageLocationChanged = "storage.location_changed";

    public static string Label(string e) => e switch
    {
        AppUnlocked => "RemoteHub unlocked",
        AppUnlockFailed => "Unlock failed",
        AppLocked => "RemoteHub locked",
        ProtectionChanged => "Startup protection changed",
        AuditRetentionChanged => "Audit log retention changed",
        SessionConnected => "Connected",
        SessionEnded => "Disconnected",
        SessionFailed => "Connection failed",
        PasswordCopied => "Password copied",
        DataAdded => "Added",
        DataChanged => "Changed",
        DataDeleted => "Deleted",
        BackupExported => "Backup exported",
        BackupImported => "Backup imported",
        AgentInstalled => "Agent installed",
        FilesTransferred => "Files transferred",
        RecordingStarted => "Recording started",
        RecordingStopped => "Recording saved",
        StorageMigrated => "Settings moved to the database",
        StorageCheckFailed => "Database check failed",
        StorageRestored => "Database restored",
        StorageLocationChanged => "Settings location changed",
        _ => e,
    };

    /// <summary>The viewer's event filter.</summary>
    public static readonly (string Name, string[] Events)[] Categories =
    [
        ("Sessions", [SessionConnected, SessionEnded, SessionFailed]),
        ("Changes to connections and settings", [DataAdded, DataChanged, DataDeleted]),
        ("Passwords and protection", [PasswordCopied, AppUnlocked, AppUnlockFailed, AppLocked, ProtectionChanged, AuditRetentionChanged]),
        ("Files, recordings and agents", [FilesTransferred, RecordingStarted, RecordingStopped, AgentInstalled]),
        ("Backups and storage", [BackupExported, BackupImported, StorageMigrated, StorageCheckFailed, StorageRestored, StorageLocationChanged]),
    ];
}

public sealed record AuditEntry(long Id, DateTime TimeUtc, string DeviceId, string? DeviceName, string? Account, string Event,
    string? ConnectionId, string? Target, string? Detail)
{
    public string Time => TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string EventLabel => AuditEvents.Label(Event);
}

/// <summary>Result of <see cref="AuditLog.Verify"/>: how many entries were checked, and the first problem found (null = none).</summary>
public sealed record AuditVerifyResult(int Checked, string? Problem);

/// <summary>
/// RemoteHub's audit log on this PC: who did what, where and when – never passwords, clipboard or file contents.
/// <list type="bullet">
/// <item>Append-only: the database rejects changes, and deletes inside the retention period (triggers).</item>
/// <item>Hash chain: each entry includes the hash of the one before, so a changed or removed entry is detected by
/// <see cref="Verify"/>. This makes changes detectable – not impossible – for someone holding this account's key.</item>
/// <item>Written in batches (every second, or at 100 entries) on a background thread; entries written before the
/// database is open (e.g. a failed unlock) wait in memory until it is.</item>
/// </list>
/// </summary>
public static class AuditLog
{
    private sealed record Pending(DateTime TimeUtc, string Event, string? ConnectionId, string? Target, string? Detail);

    private static readonly ConcurrentQueue<Pending> Queue = new();
    private static readonly object FlushGate = new(), TimerGate = new();
    private static System.Threading.Timer? _timer;
    /// <summary>Entries of a batch that couldn't be written: tried again first with the next flush (under FlushGate).</summary>
    private static readonly List<Pending> Retry = [];
    private const int MaxRetry = 10_000;

    public const int DefaultRetentionDays = 365;

    /// <summary>Days entries are kept: policy AuditRetentionDays, else the setting (Settings → Logs), else 365.</summary>
    public static int RetentionDays => AppPolicy.AuditRetentionDays ?? ConfiguredRetentionDays ?? DefaultRetentionDays;

    /// <summary>The setting <see cref="Models.UiState.AuditRetentionDays"/>, set when the settings are loaded or saved.</summary>
    public static int? ConfiguredRetentionDays
    {
        get => _configured;
        set => _configured = value is > 0 ? Math.Min(value.Value, 36_500) : null;
    }
    private static int? _configured;

    /// <summary>The choices offered in the settings (days, label).</summary>
    public static readonly (int Days, string Label)[] RetentionChoices =
        [(30, "30 days"), (90, "90 days"), (180, "6 months"), (365, "1 year"), (730, "2 years"), (1825, "5 years"), (3650, "10 years")];

    /// <summary>"1 year", "6 months" – or "400 days" for a period that isn't one of the choices.</summary>
    public static string RetentionLabel(int days) =>
        RetentionChoices.FirstOrDefault(c => c.Days == days) is { Label: { } label } ? label : $"{days} days";

    public static void Write(string evt, string? connectionId, string? target, string? detail)
    {
        Queue.Enqueue(new Pending(DateTime.UtcNow, evt, connectionId, target, detail));
        if (Queue.Count >= 100) System.Threading.ThreadPool.QueueUserWorkItem(_ => Flush());
        if (_timer is null) lock (TimerGate) _timer ??= new System.Threading.Timer(_ => Flush(), null, 1000, 1000);
    }

    public static void Write(string evt, Models.Connection? c, string? detail = null) => Write(evt, c?.Id.ToString(), c?.Name, detail);

    /// <summary>Writes what is waiting (also before closing). Does nothing while the database isn't open.</summary>
    public static void Flush()
    {
        if (Queue.IsEmpty && Retry.Count == 0) return;
        lock (FlushGate)
        {
            // Taken out of the queue before the transaction: if it fails, the batch is kept and tried again – not lost.
            var batch = new List<Pending>(Retry);
            Retry.Clear();
            while (Queue.TryDequeue(out var queued)) batch.Add(queued);
            if (batch.Count == 0) return;
            var written = false;
            try
            {
                written = LocalDb.TryUse(c =>
                {
                    var device = LocalDb.DeviceId;
                    var deviceName = Environment.MachineName;
                    var account = $"{Environment.UserDomainName}\\{Environment.UserName}";
                    var prev = LocalDb.Scalar(c, "SELECT hash FROM audit WHERE device_id = $d ORDER BY id DESC LIMIT 1", null, ("$d", device)) as byte[]
                               ?? new byte[32];
                    using var t = c.BeginTransaction();
                    using var cmd = LocalDb.Command(c, """
                        INSERT INTO audit (time_utc, device_id, device_name, account, event, connection_id, target, detail, prev_hash, hash)
                        VALUES ($t, $d, $dn, $a, $e, $c, $tg, $dt, $p, $h)
                        """, t);
                    var pt = cmd.Parameters.Add("$t", SqliteType.Integer);
                    cmd.Parameters.AddWithValue("$d", device);
                    cmd.Parameters.AddWithValue("$dn", deviceName);
                    cmd.Parameters.AddWithValue("$a", account);
                    var pe = cmd.Parameters.Add("$e", SqliteType.Text);
                    var pc = cmd.Parameters.Add("$c", SqliteType.Text);
                    var ptg = cmd.Parameters.Add("$tg", SqliteType.Text);
                    var pdt = cmd.Parameters.Add("$dt", SqliteType.Text);
                    var pp = cmd.Parameters.Add("$p", SqliteType.Blob);
                    var ph = cmd.Parameters.Add("$h", SqliteType.Blob);
                    long ms = 0;
                    foreach (var e in batch)
                    {
                        ms = new DateTimeOffset(e.TimeUtc).ToUnixTimeMilliseconds();
                        var hash = Hash(prev, ms, device, deviceName, account, e.Event, e.ConnectionId, e.Target, e.Detail);
                        pt.Value = ms; pe.Value = e.Event; pc.Value = (object?)e.ConnectionId ?? DBNull.Value;
                        ptg.Value = (object?)e.Target ?? DBNull.Value; pdt.Value = (object?)e.Detail ?? DBNull.Value;
                        pp.Value = prev; ph.Value = hash;
                        cmd.ExecuteNonQuery();
                        prev = hash;
                    }
                    // The newest entry, so removing entries from the end is detected too (see Verify).
                    var last = Convert.ToInt64(LocalDb.Scalar(c, "SELECT last_insert_rowid()", t));
                    LocalDb.SetMeta(c, HeadKey(device), $"{last}:{ms}:{Convert.ToHexString(prev)}", t);
                    t.Commit();
                });
            }
            catch (Exception ex) { DiagLog.Write("Audit log write failed: " + ex.Message); }
            if (!written) Retry.AddRange(batch.TakeLast(MaxRetry)); // not open yet, or the write failed
        }
    }

    private static string HeadKey(string device) => "audit_head_" + device;

    /// <summary>SHA-256 over the previous hash and the entry's fields (each length-prefixed, so fields can't run into each other).</summary>
    internal static byte[] Hash(byte[] prev, long timeMs, string device, string? deviceName, string? account, string evt,
        string? connectionId, string? target, string? detail)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(prev);
        h.AppendData(BitConverter.GetBytes(timeMs));
        foreach (var s in new[] { device, deviceName, account, evt, connectionId, target, detail })
        {
            if (s is null) { h.AppendData(BitConverter.GetBytes(-1)); continue; }
            var b = Encoding.UTF8.GetBytes(s);
            h.AppendData(BitConverter.GetBytes(b.Length));
            h.AppendData(b);
        }
        return h.GetHashAndReset();
    }

    // ------------------------------------------------------------------ reading

    /// <summary>Newest first. <paramref name="text"/> matches the connection or item name, the details and the account.</summary>
    public static IReadOnlyList<AuditEntry> Query(DateTime? fromUtc = null, IReadOnlyCollection<string>? events = null, string? text = null,
        string? connectionId = null, int limit = 10_000)
    {
        Flush();
        return LocalDb.Read(c =>
        {
            var where = new List<string>();
            var args = new List<(string, object?)>();
            if (fromUtc is { } from) { where.Add("time_utc >= $from"); args.Add(("$from", new DateTimeOffset(from).ToUnixTimeMilliseconds())); }
            if (events is { Count: > 0 })
            {
                var names = events.Select((e, i) => { args.Add(($"$e{i}", e)); return $"$e{i}"; });
                where.Add($"event IN ({string.Join(", ", names)})");
            }
            if (connectionId is not null) { where.Add("connection_id = $conn"); args.Add(("$conn", connectionId)); }
            if (!string.IsNullOrWhiteSpace(text))
            {
                where.Add(@"(target LIKE $text ESCAPE '\' OR detail LIKE $text ESCAPE '\' OR account LIKE $text ESCAPE '\')");
                args.Add(("$text", "%" + text.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%"));
            }
            args.Add(("$limit", limit));
            var sql = "SELECT id, time_utc, device_id, device_name, account, event, connection_id, target, detail FROM audit"
                      + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                      + " ORDER BY time_utc DESC, id DESC LIMIT $limit";
            var list = new List<AuditEntry>();
            using var cmd = LocalDb.Command(c, sql, null, [.. args]);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new AuditEntry(r.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)).UtcDateTime, r.GetString(2),
                    Str(r, 3), Str(r, 4), r.GetString(5), Str(r, 6), Str(r, 7), Str(r, 8)));
            return (IReadOnlyList<AuditEntry>)list;
        });
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>Checks the hash chain of every device's entries: each entry must follow the one before, unchanged.</summary>
    public static AuditVerifyResult Verify()
    {
        Flush();
        return LocalDb.Read(c =>
        {
            using var cmd = LocalDb.Command(c, """
                SELECT id, time_utc, device_id, device_name, account, event, connection_id, target, detail, prev_hash, hash
                FROM audit ORDER BY device_id, id
                """, null);
            using var r = cmd.ExecuteReader();
            var count = 0;
            string? device = null;
            byte[]? prev = null;
            var newest = new Dictionary<string, (long Id, byte[] Hash)>();
            while (r.Read())
            {
                count++;
                var id = r.GetInt64(0);
                var dev = r.GetString(2);
                newest[dev] = (id, (byte[])r.GetValue(10));
                var storedPrev = (byte[])r.GetValue(9);
                var stored = (byte[])r.GetValue(10);
                if (dev != device) { device = dev; prev = null; } // the oldest entry kept: its predecessor may be past the retention period
                if (prev is not null && !storedPrev.AsSpan().SequenceEqual(prev))
                    return new AuditVerifyResult(count, $"Entry {id} doesn't follow the entry before it – an entry was removed or inserted.");
                var hash = Hash(storedPrev, r.GetInt64(1), dev, Str(r, 3), Str(r, 4), r.GetString(5), Str(r, 6), Str(r, 7), Str(r, 8));
                if (!hash.AsSpan().SequenceEqual(stored))
                    return new AuditVerifyResult(count, $"Entry {id} ({DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)).LocalDateTime:g}) was changed after it was written.");
                prev = stored;
            }
            r.Close();
            return new AuditVerifyResult(count, VerifyHeads(c, newest));
        });
    }

    /// <summary>
    /// Each device's newest entry must still be the one recorded when it was written – otherwise entries were removed from
    /// the end, which the chain alone can't show. All of a device's entries may only be gone through the retention period.
    /// </summary>
    private static string? VerifyHeads(SqliteConnection c, Dictionary<string, (long Id, byte[] Hash)> newest)
    {
        var cutoff = long.TryParse(LocalDb.Meta(c, "audit_cutoff"), out var cut) ? cut : 0;
        using var cmd = LocalDb.Command(c, "SELECT key, value FROM meta WHERE key LIKE 'audit\\_head\\_%' ESCAPE '\\'", null);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var device = r.GetString(0)["audit_head_".Length..];
            var parts = (r.IsDBNull(1) ? "" : r.GetString(1)).Split(':');
            if (parts.Length != 3 || !long.TryParse(parts[0], out var id) || !long.TryParse(parts[1], out var timeMs))
                return "The record of the newest audit entry is damaged.";
            if (newest.TryGetValue(device, out var last))
            {
                if (last.Id != id || !Convert.ToHexString(last.Hash).Equals(parts[2], StringComparison.OrdinalIgnoreCase))
                    return last.Id < id
                        ? $"The newest entries were removed: the log ends at entry {last.Id}, but entry {id} was written."
                        : $"Entry {last.Id} isn't the newest entry that was written ({id}).";
            }
            else if (timeMs >= cutoff)
                return "Entries were removed: none are left, although the newest one is still inside the retention period.";
        }
        return null;
    }

    /// <summary>Removes entries (and connection history) older than <paramref name="days"/>, in small steps.</summary>
    public static int ApplyRetention(int days)
    {
        Flush();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 36_500)).ToUnixTimeMilliseconds();
        LocalDb.Use((c, _) => LocalDb.SetMeta(c, "audit_cutoff", cutoff.ToString()));
        var removed = 0;
        while (true)
        {
            var n = LocalDb.Use((c, _) =>
            {
                // Per device only the oldest entries, up to the first one inside the period – so the hash chain of what
                // stays is unbroken even if the clock once went back.
                using var cmd = LocalDb.Command(c, """
                    DELETE FROM audit WHERE id IN (
                        SELECT id FROM audit a WHERE time_utc < $c
                          AND id < COALESCE((SELECT min(id) FROM audit b WHERE b.device_id = a.device_id AND b.time_utc >= $c), 9223372036854775807)
                        LIMIT 1000)
                    """, null, ("$c", cutoff));
                return cmd.ExecuteNonQuery();
            });
            removed += n;
            if (n < 1000) break;
        }
        LocalDb.Use((c, _) => LocalDb.Exec(c, "DELETE FROM connection_history WHERE start_utc < $c", null, ("$c", cutoff)));
        return removed;
    }

    /// <summary>For tests: drops what is waiting without writing it.</summary>
    internal static void DiscardPending()
    {
        lock (FlushGate)
        {
            Queue.Clear();
            Retry.Clear();
        }
    }
}

/// <summary>When each connection was used, and how: one row per session (kind, start, end, result).</summary>
public static class ConnectionHistory
{
    public sealed record Summary(int Sessions, TimeSpan Total, DateTime? LastUtc);

    /// <summary>Records the start of a session; returns its id (0 while the database isn't open).</summary>
    public static long Start(Guid connectionId, string kind)
    {
        long id = 0;
        try
        {
            LocalDb.TryUse(c =>
            {
                LocalDb.Exec(c, "INSERT INTO connection_history (connection_id, kind, start_utc) VALUES ($c, $k, $s)", null,
                    ("$c", connectionId.ToString()), ("$k", kind), ("$s", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                id = Convert.ToInt64(LocalDb.Scalar(c, "SELECT last_insert_rowid()"));
            });
        }
        catch (Exception ex) { DiagLog.Write("Connection history write failed: " + ex.Message); }
        return id;
    }

    public static void End(long id, string result)
    {
        if (id == 0) return;
        try
        {
            LocalDb.TryUse(c => LocalDb.Exec(c, "UPDATE connection_history SET end_utc = $e, result = $r WHERE id = $id AND end_utc IS NULL", null,
                ("$e", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$r", result), ("$id", id)));
        }
        catch (Exception ex) { DiagLog.Write("Connection history write failed: " + ex.Message); }
    }

    /// <summary>Sessions of a connection since <paramref name="sinceUtc"/>: how many, how long in total, and the last one.</summary>
    public static Summary For(Guid connectionId, DateTime sinceUtc) => LocalDb.Read(c =>
    {
        using var cmd = LocalDb.Command(c, """
            SELECT count(*), COALESCE(sum(COALESCE(end_utc, start_utc) - start_utc), 0), max(start_utc)
            FROM connection_history WHERE connection_id = $c AND start_utc >= $s
            """, null, ("$c", connectionId.ToString()), ("$s", new DateTimeOffset(sinceUtc).ToUnixTimeMilliseconds()));
        using var r = cmd.ExecuteReader();
        r.Read();
        return new Summary(r.GetInt32(0), TimeSpan.FromMilliseconds(r.GetInt64(1)),
            r.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)).UtcDateTime);
    });
}
