using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using RemoteHub.Data;
using RemoteHub.Models;
using RemoteHub.Services;
using RemoteHub.Views;

namespace RemoteHub.Tests;

/// <summary>The encrypted local database: encryption, saving only changes, the move from settings.dat, recovery.</summary>
[Collection("SettingsStore")]
public class LocalDbTests : IDisposable
{
    private const string Marker = "Mk7Q-plain-text-marker";

    /// <summary>A made-up password for the tests, built at run time (no credential literals in the source).</summary>
    private static string TestSecret(string name) => string.Concat(name, "-", Marker);
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public LocalDbTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _out = output;
        Assert.StartsWith(Path.GetTempPath(), LocalDb.Directory); // isolation guard
        StoreReset.Clean();
    }

    public void Dispose() => StoreReset.Clean();

    private static AppData Sample()
    {
        var data = new AppData();
        data.Defaults.ColorTag = "Blue";
        data.Tools.ProcessRefreshSeconds = 7;
        var cred = new SavedCredential { Name = "Domain admin", Username = "adm", Domain = "CONTOSO", Password = TestSecret("Saved") };
        data.SavedCredentials.Add(cred);
        data.DefaultCredentialId = cred.Id;
        var prod = new Folder { Name = "Prod " + Marker, InheritCredentials = false, Credentials = { Username = "fadm", Password = TestSecret("Folder") } };
        var web = new Folder { Name = "Web" };
        web.Children.Add(new Connection { Name = "web01", Host = "web01." + Marker, Port = 3390 });
        web.Children.Add(new Connection { Name = "web02", Host = "web02.contoso.test", InheritCredentials = false, CredentialId = cred.Id });
        prod.Children.Add(web);
        prod.Children.Add(new Connection { Name = "db01", Host = "db01.contoso.test", Notes = "Notes " + Marker,
                                           Credentials = { Username = "sa", Password = TestSecret("Conn") }, InheritCredentials = false });
        data.Items.Add(prod);
        data.Items.Add(new Connection { Name = "jump", Host = "jump.contoso.test" });
        SettingsStore.RelinkParents(data.Items, null);
        return data;
    }

    private static bool Contains(byte[] hay, string needle) =>
        hay.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0 || hay.AsSpan().IndexOf(Encoding.Unicode.GetBytes(needle)) >= 0;

    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var b = new byte[fs.Length];
        fs.ReadExactly(b);
        return b;
    }

    [Fact]
    public void Nothing_readable_on_disk_and_passwords_are_encrypted_twice()
    {
        SettingsStore.Save(Sample());
        // While open (with the write-ahead log) and after closing: no name, host, note or password in any file.
        foreach (var f in Directory.GetFiles(LocalDb.Directory, "remotehub.db*"))
            Assert.False(Contains(ReadShared(f), Marker), Path.GetFileName(f));

        // Inside the database: the records hold no password; the secrets table holds them sealed a second time.
        LocalDb.Use((c, _) =>
        {
            Assert.Equal(0L, LocalDb.Scalar(c, "SELECT count(*) FROM nodes WHERE data LIKE '%Password%'"));
            Assert.Equal(0L, LocalDb.Scalar(c, "SELECT count(*) FROM credentials WHERE data LIKE '%Password%'"));
            Assert.Equal(3L, LocalDb.Scalar(c, "SELECT count(*) FROM secrets"));
            using var cmd = LocalDb.Command(c, "SELECT value FROM secrets", null);
            using var r = cmd.ExecuteReader();
            while (r.Read()) Assert.False(Contains((byte[])r.GetValue(0), Marker));
        });
        LocalDb.Close();
        foreach (var f in Directory.GetFiles(LocalDb.Directory, "remotehub.db*"))
            Assert.False(Contains(File.ReadAllBytes(f), Marker), Path.GetFileName(f));
        Assert.False(Encoding.ASCII.GetString(File.ReadAllBytes(LocalDb.FilePath)[..16]).StartsWith("SQLite format 3"));
    }

    [Fact]
    public void Everything_round_trips_in_order()
    {
        var data = Sample();
        SettingsStore.Save(data);
        LocalDb.Close();
        var back = SettingsStore.Load();
        Assert.Equal(SettingsStore.ToJson(data), SettingsStore.ToJson(back));
        var prod = Assert.IsType<Folder>(back.Items[0]);
        Assert.Equal(["Web", "db01"], prod.Children.Select(n => n.Name));
        Assert.Same(prod, prod.Children[0].Parent);
        Assert.Equal(TestSecret("Conn"), prod.Children[1].Credentials.Password);
        Assert.Equal(TestSecret("Saved"), back.SavedCredentials[0].Password);
        Assert.Equal(7, back.Tools.ProcessRefreshSeconds);
    }

    [Fact]
    public void A_save_writes_only_what_changed()
    {
        var data = Sample();
        SettingsStore.Save(data);
        long Version(string name) => LocalDb.Use((c, _) => (long)LocalDb.Scalar(c, "SELECT version FROM nodes WHERE name = $n", null, ("$n", name))!);

        var web01 = (Connection)((Folder)((Folder)data.Items[0]).Children[0]).Children[0];
        web01.Host = "web01-new.contoso.test";
        SettingsStore.Save(data);
        Assert.Equal(2, Version("web01"));
        Assert.Equal(1, Version("web02"));
        Assert.Equal(1, Version("jump"));

        SettingsStore.Save(data); // nothing changed: nothing written
        Assert.Equal(2, Version("web01"));

        // Delete: the record and its passwords go.
        var prod = (Folder)data.Items[0];
        prod.Children.RemoveAt(1); // db01 with its own password
        SettingsStore.Save(data);
        LocalDb.Use((c, _) =>
        {
            Assert.Equal(0L, LocalDb.Scalar(c, "SELECT count(*) FROM nodes WHERE name = 'db01'"));
            Assert.Equal(2L, LocalDb.Scalar(c, "SELECT count(*) FROM secrets"));
        });
    }

    [Fact]
    public void Repeated_ids_get_new_ones()
    {
        var data = new AppData();
        var a = new Connection { Name = "a", Host = "a" };
        var b = new Connection { Name = "b", Host = "b", Id = a.Id }; // e.g. a backup imported next to the original
        data.Items.Add(a);
        data.Items.Add(b);
        SettingsStore.Save(data);
        LocalDb.Close();
        var back = SettingsStore.Load();
        Assert.Equal(["a", "b"], back.Items.Select(n => n.Name));
        Assert.NotEqual(back.Items[0].Id, back.Items[1].Id);
    }

    [Fact]
    public void Settings_dat_is_moved_in_verified_and_kept_as_a_copy()
    {
        var data = Sample();
        LocalDb.DeleteAll();
        var encrypt = typeof(SettingsStore).GetMethod("EncryptLocal", BindingFlags.NonPublic | BindingFlags.Static)!;
        File.WriteAllBytes(SettingsStore.LocalFilePath, (byte[])encrypt.Invoke(null, [data])!);

        var loaded = SettingsStore.Load();
        Assert.Equal(SettingsStore.ToJson(data), SettingsStore.ToJson(loaded));
        Assert.False(File.Exists(SettingsStore.LocalFilePath));
        Assert.True(File.Exists(SettingsStore.LocalFilePath + ".migrated"));
        Assert.Equal(LocalDb.FilePath, SettingsStore.FilePath);
        Assert.Contains(AuditLog.Query(), e => e.Event == AuditEvents.StorageMigrated);

        // Next start: straight from the database.
        LocalDb.Close();
        Assert.Equal(SettingsStore.ToJson(data), SettingsStore.ToJson(SettingsStore.Load()));
    }

    [Fact]
    public void Changes_are_audited_by_field_name_never_by_value()
    {
        var data = Sample();
        SettingsStore.Save(data);
        AuditLog.Flush();
        var db01 = (Connection)((Folder)data.Items[0]).Children[1];
        db01.Host = "db01-new." + Marker;
        db01.Credentials.Password = TestSecret("New");
        db01.LastConnectedUtc = DateTime.UtcNow; // just using it: not audited
        data.Items.Add(new Connection { Name = "new01", Host = "new01" });
        SettingsStore.Save(data);

        var entries = AuditLog.Query(events: [AuditEvents.DataChanged, AuditEvents.DataAdded]);
        var changed = Assert.Single(entries, e => e.Event == AuditEvents.DataChanged && e.Target == "db01");
        Assert.Equal("Connection: Host, Credentials.Password", changed.Detail);
        Assert.Equal(db01.Id.ToString(), changed.ConnectionId);
        Assert.Contains(entries, e => e.Event == AuditEvents.DataAdded && e.Target == "new01");
        Assert.DoesNotContain(AuditLog.Query(), e => (e.Detail ?? "").Contains(Marker) || (e.Target ?? "").Contains(TestSecret("New")));

        // Only using a connection changes nothing worth an entry.
        var before = AuditLog.Query().Count;
        db01.LastConnectedUtc = DateTime.UtcNow.AddMinutes(1);
        SettingsStore.Save(data);
        Assert.Equal(before, AuditLog.Query().Count);
    }

    [Fact]
    public void Audit_log_is_append_only_and_tampering_is_detected()
    {
        LocalDb.Open();
        for (var i = 0; i < 5; i++) AuditLog.Write(AuditEvents.SessionConnected, Guid.NewGuid().ToString(), $"srv{i}", "Remote desktop");
        Assert.Null(AuditLog.Verify().Problem);
        Assert.Equal(5, AuditLog.Verify().Checked);

        LocalDb.Use((c, _) =>
        {
            Assert.Throws<SqliteException>(() => LocalDb.Exec(c, "UPDATE audit SET detail = 'x' WHERE target = 'srv2'"));
            Assert.Throws<SqliteException>(() => LocalDb.Exec(c, "DELETE FROM audit WHERE target = 'srv2'"));
        });

        // Someone with the key drops the protection and edits an entry: Verify finds it.
        LocalDb.Use((c, _) =>
        {
            LocalDb.Exec(c, "DROP TRIGGER audit_no_update");
            LocalDb.Exec(c, "DROP TRIGGER audit_no_delete");
            LocalDb.Exec(c, "UPDATE audit SET target = 'other' WHERE target = 'srv2'");
        });
        Assert.Contains("was changed", AuditLog.Verify().Problem);

        LocalDb.Use((c, _) =>
        {
            LocalDb.Exec(c, "DELETE FROM audit WHERE target = 'other'"); // ... or removes it
        });
        Assert.Contains("doesn't follow", AuditLog.Verify().Problem);
    }

    [Fact]
    public void Retention_removes_only_old_entries_and_the_chain_stays_valid()
    {
        LocalDb.Open();
        var old = DateTimeOffset.UtcNow.AddDays(-400).ToUnixTimeMilliseconds();
        byte[] prev = new byte[32];
        LocalDb.Use((c, _) =>
        {
            for (var i = 0; i < 3; i++)
            {
                var hash = AuditLog.Hash(prev, old + i, LocalDb.DeviceId, "PC", "acc", AuditEvents.SessionConnected, null, "old" + i, null);
                LocalDb.Exec(c, "INSERT INTO audit (time_utc, device_id, device_name, account, event, target, prev_hash, hash) VALUES ($t, $d, 'PC', 'acc', $e, $tg, $p, $h)", null,
                    ("$t", old + i), ("$d", LocalDb.DeviceId), ("$e", AuditEvents.SessionConnected), ("$tg", "old" + i), ("$p", prev), ("$h", hash));
                prev = hash;
            }
        });
        AuditLog.Write(AuditEvents.SessionConnected, null, "new", null);
        Assert.Null(AuditLog.Verify().Problem);
        Assert.Equal(3, AuditLog.ApplyRetention(365));
        Assert.Equal(["new"], AuditLog.Query(events: [AuditEvents.SessionConnected]).Select(e => e.Target));
        Assert.Null(AuditLog.Verify().Problem); // the oldest kept entry starts the chain
        // Entries inside the retention period still can't be deleted.
        LocalDb.Use((c, _) => Assert.Throws<SqliteException>(() => LocalDb.Exec(c, "DELETE FROM audit")));
    }

    [Fact]
    public void A_damaged_database_is_reported_and_a_backup_restores_it()
    {
        var data = Sample();
        SettingsStore.Save(data);
        LocalDb.BackupIfDue();
        var (backup, _) = Assert.Single(LocalDb.Backups());
        LocalDb.Close();

        var bytes = File.ReadAllBytes(LocalDb.FilePath);
        for (var page = 1; page < bytes.Length / 4096; page++) bytes[page * 4096 + 100] ^= 0x01; // every page but the first
        File.WriteAllBytes(LocalDb.FilePath, bytes);
        Assert.Throws<DatabaseUnreadableException>(() => SettingsStore.Load());
        Assert.False(LocalDb.IsOpen);

        LocalDb.RestoreBackup(backup);
        Assert.Equal(SettingsStore.ToJson(data), SettingsStore.ToJson(SettingsStore.Load()));
        Assert.Single(Directory.GetFiles(LocalDb.Directory, "remotehub.db.damaged-*"));
    }

    [Fact]
    public void A_missing_key_or_a_key_of_another_database_is_reported()
    {
        SettingsStore.Save(Sample());
        LocalDb.Close();
        var key = Path.Combine(LocalDb.Directory, "db.key");
        var saved = File.ReadAllBytes(key);
        File.Delete(key);
        Assert.Throws<DatabaseUnreadableException>(() => SettingsStore.Load());

        using (var other = DbKeys.Keys.New()) DbKeys.Write(key, other);
        Assert.Throws<DatabaseUnreadableException>(() => SettingsStore.Load());

        File.WriteAllBytes(key, saved);
        Assert.Equal("jump", SettingsStore.Load().Items[1].Name);
    }

    [Fact]
    public void An_interrupted_re_encryption_finds_the_right_key()
    {
        var data = Sample();
        SettingsStore.Save(data);
        var key = Path.Combine(LocalDb.Directory, "db.key");
        var oldKey = File.ReadAllBytes(key);

        // Stopped before the database was re-encrypted: db.key.next doesn't fit and is dropped.
        LocalDb.Close();
        using (var next = DbKeys.Keys.New()) DbKeys.Write(key + ".next", next);
        Assert.Equal(SettingsStore.ToJson(data), SettingsStore.ToJson(SettingsStore.Load()));
        Assert.False(File.Exists(key + ".next"));

        // Stopped after re-encrypting, before db.key was replaced: db.key.next fits and becomes db.key.
        LocalDb.Rekey();
        var newKey = File.ReadAllBytes(key);
        LocalDb.Close();
        File.WriteAllBytes(key + ".next", newKey);
        File.WriteAllBytes(key, oldKey);
        Assert.Equal(SettingsStore.ToJson(data), SettingsStore.ToJson(SettingsStore.Load()));
        Assert.Equal(newKey, File.ReadAllBytes(key));
        Assert.False(File.Exists(key + ".next"));
    }

    [Fact]
    public void Connection_history_sums_sessions()
    {
        SettingsStore.Save(new AppData());
        var id = Guid.NewGuid();
        var a = ConnectionHistory.Start(id, "Remote desktop");
        ConnectionHistory.End(a, "closed");
        ConnectionHistory.Start(Guid.NewGuid(), "PowerShell");
        var s = ConnectionHistory.For(id, DateTime.UtcNow.AddDays(-1));
        Assert.Equal(1, s.Sessions);
        Assert.NotNull(s.LastUtc);
        Assert.Equal(0, ConnectionHistory.For(id, DateTime.UtcNow.AddMinutes(1)).Sessions);
    }

    [Fact]
    public void Five_thousand_connections_stay_fast()
    {
        var data = new AppData();
        for (var f = 0; f < 50; f++)
        {
            var folder = new Folder { Name = $"Folder {f}" };
            for (var i = 0; i < 100; i++)
                folder.Children.Add(new Connection { Name = $"srv-{f}-{i}", Host = $"srv-{f}-{i}.contoso.test", Notes = new string('n', 300),
                                                     InheritCredentials = i % 10 != 0, Credentials = { Username = "u", Password = i % 10 == 0 ? "p" + i : null } });
            data.Items.Add(folder);
        }
        SettingsStore.RelinkParents(data.Items, null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SettingsStore.Save(data);
        var first = sw.ElapsedMilliseconds;
        LocalDb.Close();
        sw.Restart();
        var loaded = SettingsStore.Load();
        var load = sw.ElapsedMilliseconds;
        var c = (Connection)((Folder)loaded.Items[3]).Children[7];
        var times = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            c.Host = $"changed-{i}";
            sw.Restart();
            SettingsStore.Save(loaded);
            times.Add(sw.ElapsedMilliseconds);
        }
        times.Sort();
        _out.WriteLine($"first save {first} ms, open + load {load} ms, save one change (median of 5) {times[2]} ms");
        // Generous limits for slow CI machines – a regression guard, not the targets (see docs/local-db-design.md).
        Assert.True(load < 3000, $"load {load} ms");
        Assert.True(times[2] < 1000, $"save {times[2]} ms");
    }

    [Fact]
    public void Csv_export_defuses_formulas()
    {
        var csv = AuditLogWindow.ToCsv([new AuditEntry(1, new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc), "dev", "PC", "acc",
            AuditEvents.DataChanged, null, "=HYPERLINK(\"x\")", "a,b")]);
        var line = csv.Split("\r\n")[1];
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", line);
        Assert.Contains("\"a,b\"", line);
        Assert.StartsWith("2026-10-10 12:00:00,Changed,data.changed,", line);
    }
}
