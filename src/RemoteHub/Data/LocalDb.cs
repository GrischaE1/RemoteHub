using System.Threading;
using Microsoft.Data.Sqlite;
using RemoteHub.Services;
using SQLitePCL;

namespace RemoteHub.Data;

/// <summary>The database can't be opened: damaged, key missing or not readable with this Windows account.</summary>
public sealed class DatabaseUnreadableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// RemoteHub's local database: <c>%LOCALAPPDATA%\RemoteHub\remotehub.db</c> – SQLite with whole-file encryption
/// (SQLite3 Multiple Ciphers, ChaCha20-Poly1305: every page encrypted and authenticated, including indexes, full-text
/// search and the write-ahead log). The key is random and kept in <see cref="DbKeys"/>; it's passed as bytes, never as
/// a string, and the cipher's own password KDF is set to one round because the key is already random.
/// <para>Local only – never in OneDrive or on a network share (SQLite corrupts there); %LOCALAPPDATA% rather than
/// %APPDATA%, because Roaming can be redirected to a share.</para>
/// <para>One writer connection, used under a lock: writes are small and fast (well under a millisecond), so callers
/// never wait noticeably. Long reads – the audit log viewer, checks, backups – get read connections of their own
/// (<see cref="Read"/>): with the write-ahead log they neither wait for the writer nor block it. See docs/local-db-design.md.</para>
/// </summary>
public static class LocalDb
{
    public const int SchemaVersion = 2;
    private const int KeptBackups = 7;

    private static readonly object Gate = new();
    /// <summary>
    /// Readers hold it shared while their connection is open; opening, closing and re-encrypting hold it exclusively, so
    /// the key never changes under a reader. Always taken before <see cref="Gate"/>.
    /// </summary>
    private static readonly ReaderWriterLockSlim Lifetime = new(LockRecursionPolicy.SupportsRecursion);

    private readonly struct Exclusive : IDisposable
    {
        public Exclusive() => Lifetime.EnterWriteLock();
        public void Dispose() => Lifetime.ExitWriteLock();
    }
    private static SqliteConnection? _db;
    private static DbKeys.Keys? _keys;

    /// <summary>%LOCALAPPDATA%\RemoteHub, or the test/data folder in REMOTEHUB_DATA_DIR (RDPMANAGER_DATA_DIR still works).</summary>
    public static string Directory
    {
        get
        {
            foreach (var v in new[] { "REMOTEHUB_DATA_DIR", "RDPMANAGER_DATA_DIR" })
                if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } custom) return custom;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteHub");
        }
    }

    public static string FilePath => Path.Combine(Directory, "remotehub.db");
    private static string KeyPath => Path.Combine(Directory, "db.key");
    /// <summary>The next key while the database is being re-encrypted (see <see cref="Rekey"/>).</summary>
    private static string NextKeyPath => KeyPath + ".next";
    /// <summary>The keys from before, while the startup protection changes (see <see cref="BeginReprotect"/>).</summary>
    private static string ReprotectKeyPath => KeyPath + ".reprotect";
    public static string BackupFolder => Path.Combine(Directory, "backup");

    public static bool IsOpen { get { lock (Gate) return _db is not null; } }
    public static bool Exists => File.Exists(FilePath);

    /// <summary>Counts the opens: what was read before a re-open (restore, new database) doesn't describe the file any more.</summary>
    public static int Generation { get; private set; }

    /// <summary>This database's id (random, created with it) – identifies the device in the audit log.</summary>
    public static string DeviceId { get; private set; } = "";

    // ------------------------------------------------------------------ open / close

    /// <summary>
    /// Opens the database, creating it (and its key) if there is none. Throws <see cref="AppLockedException"/> while
    /// the startup protection is locked and <see cref="DatabaseUnreadableException"/> if it can't be read.
    /// </summary>
    public static void Open()
    {
        using var exclusive = new Exclusive();
        lock (Gate)
        {
            if (_db is not null) return;
            System.IO.Directory.CreateDirectory(Directory);
            var keys = ReadKeys();
            try
            {
                _db = Connect(FilePath, keys.Db, wal: true);
                Migrate(_db, keys.Db);
                _keys = keys;
                DeviceId = Meta(_db, "device_id") ?? "";
                Generation++;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
            {
                _db?.Dispose();
                _db = null;
                keys.Dispose();
                throw new DatabaseUnreadableException("The database is damaged or doesn't match its key.", ex);
            }
            catch
            {
                _db?.Dispose();
                _db = null;
                keys.Dispose();
                throw;
            }
        }
    }

    private static DbKeys.Keys ReadKeys()
    {
        var dbExists = File.Exists(FilePath);
        // A re-encryption was interrupted: the database has either the old or the next key.
        if (File.Exists(NextKeyPath))
        {
            try
            {
                var next = DbKeys.Read(NextKeyPath);
                if (dbExists && Opens(FilePath, next.Db))
                {
                    File.Move(NextKeyPath, KeyPath, overwrite: true);
                    TryDelete(ReprotectKeyPath);
                    return next;
                }
                next.Dispose();
            }
            catch (AppLockedException) { throw; }
            catch { /* fall back to db.key */ }
            File.Delete(NextKeyPath);
        }
        // Changing the startup protection was interrupted: db.key may be sealed with a lock that is gone (turning it
        // off) – the copy from before opens the database and is stored again with the protection that is on now.
        if (File.Exists(ReprotectKeyPath))
        {
            if (AppLock.IsEnabled && !AppLock.IsUnlocked) throw new AppLockedException();
            try
            {
                var copy = DbKeys.Read(ReprotectKeyPath);
                if (dbExists && Opens(FilePath, copy.Db))
                {
                    DbKeys.Write(KeyPath, copy);
                    File.Delete(ReprotectKeyPath);
                    return copy;
                }
                copy.Dispose();
            }
            catch (AppLockedException) { throw; }
            catch { /* the re-encryption finished: db.key is current */ }
            File.Delete(ReprotectKeyPath);
        }
        if (File.Exists(KeyPath))
        {
            try { return DbKeys.Read(KeyPath); }
            catch (AppLockedException) { throw; }
            catch (Exception ex) { throw new DatabaseUnreadableException("The database key can't be read with this Windows account.", ex); }
        }
        if (dbExists) throw new DatabaseUnreadableException("The database key file (db.key) is missing.");
        var keys = DbKeys.Keys.New();
        DbKeys.Write(KeyPath, keys);
        return keys;
    }

    private static bool Opens(string path, byte[] key)
    {
        try { using var c = Connect(path, key, wal: false); return true; }
        catch { return false; }
    }

    public static void Close()
    {
        using var exclusive = new Exclusive();
        lock (Gate)
        {
            _db?.Dispose();
            _db = null;
            _keys?.Dispose();
            _keys = null;
            DeviceId = "";
        }
    }

    /// <summary>Removes the database, its key and its backups (e.g. after a failed migration, or in tests).</summary>
    public static void DeleteAll()
    {
        using var exclusive = new Exclusive();
        lock (Gate)
        {
            Close();
            foreach (var f in new[] { FilePath, FilePath + "-wal", FilePath + "-shm", KeyPath, NextKeyPath, ReprotectKeyPath })
                if (File.Exists(f)) File.Delete(f);
            if (System.IO.Directory.Exists(BackupFolder)) System.IO.Directory.Delete(BackupFolder, true);
        }
    }

    internal static SqliteConnection Connect(string path, byte[] key, bool wal, bool readOnly = false)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false, // no keyed connection outlives Close()
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        try
        {
            c.Open();
            Exec(c, "PRAGMA cipher = 'chacha20'");
            Exec(c, "PRAGMA kdf_iter = 1"); // the key is random: the cipher's password KDF adds nothing but time
            var rc = raw.sqlite3_key(c.Handle, key);
            if (rc != raw.SQLITE_OK) throw new SqliteException("The database key could not be set.", rc);
            if (wal)
            {
                Exec(c, "PRAGMA journal_mode = WAL");
                Exec(c, "PRAGMA synchronous = NORMAL");
            }
            Exec(c, "PRAGMA temp_store = MEMORY");
            Exec(c, "PRAGMA secure_delete = ON");
            Exec(c, "PRAGMA trusted_schema = OFF");
            Exec(c, "PRAGMA foreign_keys = ON");
            Scalar(c, "SELECT count(*) FROM sqlite_master"); // reads page 1: fails here with a wrong key
            return c;
        }
        catch
        {
            c.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------ schema

    private static void Migrate(SqliteConnection c, byte[] key)
    {
        var version = Convert.ToInt32(Scalar(c, "PRAGMA user_version"));
        if (version > SchemaVersion)
            throw new DatabaseUnreadableException($"The database was created by a newer version of RemoteHub (schema {version}).");
        if (version == SchemaVersion) return;
        // An existing database gets a backup before its first step (it is one of the automatic backups).
        if (version >= 1) BackupBeforeMigration(c, key);
        if (version < 1)
        {
            using var t = c.BeginTransaction();
            Exec(c, Schema1, t);
            SetMeta(c, "device_id", Guid.NewGuid().ToString("N"), t);
            SetMeta(c, "device_name", Environment.MachineName, t);
            SetMeta(c, "created_utc", DateTime.UtcNow.ToString("O"), t);
            Exec(c, "PRAGMA user_version = 1", t);
            t.Commit();
        }
        if (version < 2)
        {
            using var t = c.BeginTransaction();
            Exec(c, Schema2, t);
            Exec(c, "PRAGMA user_version = 2", t);
            t.Commit();
        }
    }

    private static void BackupBeforeMigration(SqliteConnection c, byte[] key)
    {
        System.IO.Directory.CreateDirectory(BackupFolder);
        var path = Path.Combine(BackupFolder, $"remotehub-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
        using (var dst = Connect(path, key, wal: false)) c.BackupDatabase(dst);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
    }

    // Same columns for nodes, credentials and settings, so one code path reads and writes all records.
    // version/modified_utc/dirty are for the device sync (phase 4).
    private const string RecordColumns = """
        id TEXT PRIMARY KEY, parent_id TEXT, sort INTEGER NOT NULL, kind TEXT NOT NULL, name TEXT NOT NULL, host TEXT,
        data TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 1, modified_utc INTEGER NOT NULL, dirty INTEGER NOT NULL DEFAULT 1
        """;

    private const string Schema1 = $"""
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE nodes ({RecordColumns});
        CREATE INDEX ix_nodes_parent ON nodes(parent_id, sort);
        CREATE TABLE credentials ({RecordColumns});
        CREATE TABLE settings ({RecordColumns});
        -- Passwords, encrypted a second time with the secret key (see SecretBox); "path" is where they go in the record.
        CREATE TABLE secrets (owner_kind TEXT NOT NULL, owner_id TEXT NOT NULL, path TEXT NOT NULL, value BLOB NOT NULL,
                              PRIMARY KEY (owner_kind, owner_id, path));
        CREATE TABLE audit (id INTEGER PRIMARY KEY, time_utc INTEGER NOT NULL, device_id TEXT NOT NULL, device_name TEXT,
                            account TEXT, event TEXT NOT NULL, connection_id TEXT, target TEXT, detail TEXT,
                            prev_hash BLOB NOT NULL, hash BLOB NOT NULL);
        CREATE INDEX ix_audit_time ON audit(time_utc);
        CREATE INDEX ix_audit_connection ON audit(connection_id, time_utc);
        CREATE INDEX ix_audit_device ON audit(device_id, id);
        -- Append-only: no updates; deletes only past the retention cut-off (set by AuditLog.ApplyRetention).
        CREATE TRIGGER audit_no_update BEFORE UPDATE ON audit
            BEGIN SELECT RAISE(ABORT, 'The audit log is append-only.'); END;
        CREATE TRIGGER audit_no_delete BEFORE DELETE ON audit
            WHEN old.time_utc >= COALESCE((SELECT CAST(value AS INTEGER) FROM meta WHERE key = 'audit_cutoff'), 0)
            BEGIN SELECT RAISE(ABORT, 'Only audit entries past the retention period can be deleted.'); END;
        CREATE TABLE connection_history (id INTEGER PRIMARY KEY, connection_id TEXT NOT NULL, kind TEXT NOT NULL,
                                         start_utc INTEGER NOT NULL, end_utc INTEGER, result TEXT);
        CREATE INDEX ix_history_connection ON connection_history(connection_id, start_utc);
        """;

    // Notes (docs/notes-view-design.md §5.1). deleted_utc marks "Recently deleted"; rid gives the full-text index a rowid
    // that VACUUM can't renumber. notes_fts keeps its own copy of the text (inside the encrypted file like everything);
    // NoteStore writes it in the same transaction as the page – with trusted_schema off, triggers can't write to FTS5.
    private const string NoteColumns = "version INTEGER NOT NULL DEFAULT 1, modified_utc INTEGER NOT NULL, dirty INTEGER NOT NULL DEFAULT 1";

    private const string Schema2 = $"""
        CREATE TABLE notebooks (id TEXT PRIMARY KEY, name TEXT NOT NULL, sort INTEGER NOT NULL, deleted_utc INTEGER, {NoteColumns});
        CREATE TABLE note_sections (id TEXT PRIMARY KEY, notebook_id TEXT NOT NULL, name TEXT NOT NULL, color TEXT,
                                    sort INTEGER NOT NULL, deleted_utc INTEGER, {NoteColumns});
        CREATE INDEX ix_note_sections ON note_sections(notebook_id, sort);
        CREATE TABLE notes (rid INTEGER PRIMARY KEY, id TEXT NOT NULL UNIQUE, section_id TEXT NOT NULL, title TEXT NOT NULL,
                            body TEXT NOT NULL, body_text TEXT NOT NULL, sort INTEGER NOT NULL, created_utc INTEGER NOT NULL,
                            deleted_utc INTEGER, {NoteColumns});
        CREATE INDEX ix_notes_section ON notes(section_id, sort);
        CREATE INDEX ix_notes_modified ON notes(modified_utc);
        CREATE TABLE note_files (id TEXT PRIMARY KEY, note_id TEXT NOT NULL, mime TEXT NOT NULL, width INTEGER NOT NULL,
                                 height INTEGER NOT NULL, size INTEGER NOT NULL, sha256 BLOB NOT NULL, data BLOB NOT NULL, {NoteColumns});
        CREATE INDEX ix_note_files ON note_files(note_id);
        CREATE TABLE note_links (note_id TEXT NOT NULL, node_id TEXT NOT NULL, PRIMARY KEY (note_id, node_id));
        CREATE INDEX ix_note_links_node ON note_links(node_id);
        CREATE VIRTUAL TABLE notes_fts USING fts5(title, body_text, tokenize = 'unicode61 remove_diacritics 2');
        """;

    // ------------------------------------------------------------------ access

    /// <summary>Runs <paramref name="f"/> on the open database under the lock.</summary>
    internal static T Use<T>(Func<SqliteConnection, byte[], T> f)
    {
        lock (Gate)
        {
            if (_db is null || _keys is null) throw new InvalidOperationException("The database isn't open.");
            return f(_db, _keys.Secret);
        }
    }

    internal static void Use(Action<SqliteConnection, byte[]> f) => Use<object?>((c, k) => { f(c, k); return null; });

    /// <summary>
    /// Runs <paramref name="f"/> on a read-only connection of its own, outside the writer's lock: for long reads (the audit
    /// log viewer, checks, backups) that mustn't make saves wait. Sees what was committed when it started. Never call it
    /// from inside <see cref="Use"/>.
    /// </summary>
    internal static T Read<T>(Func<SqliteConnection, T> f)
    {
        Lifetime.EnterReadLock();
        try
        {
            if (_keys is null) throw new InvalidOperationException("The database isn't open.");
            using var c = Connect(FilePath, _keys.Db, wal: false, readOnly: true);
            return f(c);
        }
        finally { Lifetime.ExitReadLock(); }
    }

    /// <summary>Like <see cref="Use"/>, but does nothing (returns false) while the database isn't open.</summary>
    internal static bool TryUse(Action<SqliteConnection> f)
    {
        lock (Gate)
        {
            if (_db is null) return false;
            f(_db);
            return true;
        }
    }

    public static string? Meta(string key) => Use((c, _) => Meta(c, key));
    public static void SetMeta(string key, string? value) => Use((c, _) => SetMeta(c, key, value));

    internal static string? Meta(SqliteConnection c, string key) =>
        Scalar(c, "SELECT value FROM meta WHERE key = $k", null, ("$k", key)) as string;

    internal static void SetMeta(SqliteConnection c, string key, string? value, SqliteTransaction? t = null)
    {
        if (value is null) Exec(c, "DELETE FROM meta WHERE key = $k", t, ("$k", key));
        else Exec(c, "INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", t, ("$k", key), ("$v", value));
    }

    internal static void Exec(SqliteConnection c, string sql, SqliteTransaction? t = null, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, sql, t, args);
        cmd.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection c, string sql, SqliteTransaction? t = null, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, sql, t, args);
        return cmd.ExecuteScalar();
    }

    internal static SqliteCommand Command(SqliteConnection c, string sql, SqliteTransaction? t, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = t;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    // ------------------------------------------------------------------ re-encryption

    /// <summary>
    /// Before the startup protection is turned on or off: keeps a copy of the current keys with Windows (DPAPI)
    /// protection only – no weaker than db.key before turning it on or after turning it off – so the database stays
    /// readable if RemoteHub stops between switching the lock and <see cref="Rekey"/>. <see cref="ReadKeys"/> uses it.
    /// </summary>
    public static void BeginReprotect()
    {
        lock (Gate)
        {
            if (_keys is not null) DbKeys.Write(ReprotectKeyPath, _keys, dpapiOnly: true);
        }
    }

    /// <summary>
    /// Ends <see cref="BeginReprotect"/>. After a failure, db.key is stored again with the protection that is on now, so
    /// it matches the lock file whether or not the switch happened.
    /// </summary>
    public static void EndReprotect(bool failed)
    {
        lock (Gate)
        {
            if (failed && _keys is not null)
                try { DbKeys.Write(KeyPath, _keys); }
                catch (Exception ex) { DiagLog.Write("Database key could not be stored again: " + ex.Message); return; } // keep the copy
            TryDelete(ReprotectKeyPath);
        }
    }

    /// <summary>
    /// Encrypts the database with a new random key and stores it with the protection that is on now – after the
    /// startup protection was turned on or off, so a key copy from before can't open the database any more. The secret
    /// key stays: passwords are only reachable through the newly encrypted pages. Old backups can't be opened with the
    /// new key and are replaced by a fresh one.
    /// <para>Crash-safe: the new key is written to db.key.next first; <see cref="ReadKeys"/> finds whichever matches.</para>
    /// </summary>
    public static void Rekey()
    {
        using var exclusive = new Exclusive();
        lock (Gate)
        {
            if (_db is null || _keys is null) throw new InvalidOperationException("The database isn't open.");
            // A damaged database isn't re-encrypted: the existing backups (old key) would become useless while it can't
            // be copied into a new one. Restore a backup first.
            if (Scalar(_db, "PRAGMA quick_check") as string is var check && check != "ok")
                throw new DatabaseUnreadableException("The database check found a problem, so it wasn't re-encrypted: " + check);
            var next = DbKeys.Keys.New();
            _keys.Secret.CopyTo(next.Secret, 0);
            DbKeys.Write(NextKeyPath, next);
            var rc = raw.sqlite3_rekey(_db.Handle, next.Db);
            if (rc != raw.SQLITE_OK)
            {
                File.Delete(NextKeyPath);
                next.Dispose();
                throw new SqliteException("The database could not be re-encrypted: " + raw.sqlite3_errmsg(_db.Handle).utf8_to_string(), rc);
            }
            File.Move(NextKeyPath, KeyPath, overwrite: true);
            _keys.Dispose();
            _keys = next;
            if (System.IO.Directory.Exists(BackupFolder))
                foreach (var f in System.IO.Directory.GetFiles(BackupFolder, "remotehub-*.db")) TryDelete(f);
            BackupLocked();
        }
    }

    // ------------------------------------------------------------------ backups

    /// <summary>The automatic backups, newest first.</summary>
    public static IReadOnlyList<(string Path, DateTime TimeUtc)> Backups() =>
        System.IO.Directory.Exists(BackupFolder)
            ? System.IO.Directory.GetFiles(BackupFolder, "remotehub-*.db")
                .Select(f => (f, File.GetLastWriteTimeUtc(f))).OrderByDescending(b => b.Item2).ToList()
            : [];

    /// <summary>Set when the last integrity check found a problem.</summary>
    public static bool CheckFailed { get; private set; }

    /// <summary>Daily: checks the database (reads every page) and, if it's fine, copies it to the backup folder (last 7 kept).</summary>
    public static void BackupIfDue()
    {
        if (Backups() is [var newest, ..] && DateTime.UtcNow - newest.TimeUtc < TimeSpan.FromHours(24)) return;
        // On a read connection: checking every page and copying the file takes a while, and saves go on meanwhile.
        // (Read holds the lifetime lock, so the key can't change while it runs.)
        if (_keys is null) return;
        Read(c => { BackupFrom(c, _keys!.Db); return 0; });
    }

    private static void BackupLocked() => BackupFrom(_db!, _keys!.Db);

    private static void BackupFrom(SqliteConnection source, byte[] key)
    {
        var check = Scalar(source, "PRAGMA quick_check") as string;
        if (check != "ok")
        {
            CheckFailed = true;
            DiagLog.Write("Database check failed – no backup made: " + check);
            AuditLog.Write(AuditEvents.StorageCheckFailed, null, null, "The database check found a problem; the existing backups were kept.");
            return;
        }
        CheckFailed = false;
        System.IO.Directory.CreateDirectory(BackupFolder);
        var path = Path.Combine(BackupFolder, $"remotehub-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
        try
        {
            using (var dst = Connect(path, key, wal: false)) source.BackupDatabase(dst);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            TryDelete(path);
            DiagLog.Write("Database backup failed: " + ex.Message);
            return;
        }
        foreach (var (old, _) in Backups().Skip(KeptBackups)) TryDelete(old);
    }

    /// <summary>
    /// Replaces the database with a backup. The current file is kept beside it (remotehub.db.damaged-…), encrypted as
    /// it was. Works with the database closed (start-up after a failed open) or open.
    /// </summary>
    public static void RestoreBackup(string backupPath)
    {
        using var exclusive = new Exclusive();
        lock (Gate)
        {
            Close();
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var f in new[] { FilePath, FilePath + "-wal", FilePath + "-shm" })
                if (File.Exists(f)) File.Move(f, Path.Combine(Directory, Path.GetFileName(f).Replace("remotehub.db", $"remotehub.db.damaged-{stamp}")), overwrite: true);
            File.Copy(backupPath, FilePath);
            Open();
            AuditLog.Write(AuditEvents.StorageRestored, null, null, $"Restored the backup of {File.GetLastWriteTime(backupPath):g}.");
        }
    }

    /// <summary>
    /// Starts over with an empty database; the unreadable one is kept beside it (remotehub.db.damaged-…), with its key
    /// and its backups (backup.damaged-…) – they belong to the old key and can't be opened with the new one.
    /// </summary>
    public static void SetAsideAndCreateNew()
    {
        using var exclusive = new Exclusive();
        lock (Gate)
        {
            Close();
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var f in new[] { FilePath, FilePath + "-wal", FilePath + "-shm", KeyPath })
                if (File.Exists(f)) File.Move(f, Path.Combine(Directory, Path.GetFileName(f) + $".damaged-{stamp}"), overwrite: true);
            if (System.IO.Directory.Exists(BackupFolder)) System.IO.Directory.Move(BackupFolder, BackupFolder + $".damaged-{stamp}");
            Open();
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
