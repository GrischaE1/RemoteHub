using System.Threading.Tasks;
using System.Reflection;
using RemoteHub.Data;
using RemoteHub.Models;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>
/// Startup protection: the database key and the remembered shared-file key are sealed with a key that only the
/// startup password (or Windows Hello) unlocks. Windows Hello itself needs a person at the PC and isn't run here.
/// </summary>
[Collection("SettingsStore")]
public class AppLockTests
{
    private static byte[] Head(string path, int n = 5) => File.ReadAllBytes(path)[..n];
    private static string DbKey => Path.Combine(LocalDb.Directory, "db.key");

    [Fact]
    public async Task Settings_are_sealed_until_the_password_unlocks_them()
    {
        Assert.StartsWith(Path.GetTempPath(), SettingsStore.Directory); // isolation guard
        StoreReset.Clean();
        try
        {
            var data = new AppData();
            data.Items.Add(new Connection { Name = "web01", Host = "web01.contoso.local" });
            SettingsStore.Save(data, force: true);
            var keyCacheSet = typeof(SettingsStore).GetMethod("KeyCacheSet", BindingFlags.NonPublic | BindingFlags.Static)!;
            keyCacheSet.Invoke(null, [new byte[16], new byte[32]]);
            Assert.Equal("RHDK\u0001"u8.ToArray(), Head(DbKey));
            var keyBefore = File.ReadAllBytes(DbKey);

            // Turn on: the database gets a new key, sealed; the key cache too; no copy with the old protection stays.
            File.WriteAllText(SettingsStore.LocalFilePath + ".migrated", "old");
            await SettingsStore.Reprotect(data, () => { AppLock.Enable("correct horse battery"); return Task.CompletedTask; });
            Assert.True(AppLock.IsEnabled);
            Assert.Equal("RHDK\u0002"u8.ToArray(), Head(DbKey));
            Assert.False(File.Exists(SettingsStore.LocalFilePath + ".migrated"));
            Assert.Equal("RHK2"u8.ToArray(), Head(Path.Combine(SettingsStore.Directory, "keys.dat"), 4));
            Assert.DoesNotContain("web01", File.ReadAllText(AppLock.FilePath));
            Assert.Single(LocalDb.Backups()); // old backups (old key) replaced by one with the new key

            // The key from before can't open the database any more.
            LocalDb.Close();
            AppLock.Forget();
            File.WriteAllBytes(DbKey + ".old", keyBefore);
            using (var old = DbKeysFor(DbKey + ".old"))
                Assert.ThrowsAny<Exception>(() => LocalDb.Connect(LocalDb.FilePath, old.Db, wal: false).Dispose());
            File.Delete(DbKey + ".old");

            // After a restart the settings can't be read without unlocking.
            Assert.Throws<AppLockedException>(() => SettingsStore.Load());
            Assert.False(AppLock.TryUnlock("wrong password"));
            Assert.False(AppLock.IsUnlocked);
            Assert.True(AppLock.TryUnlock("correct horse battery"));
            Assert.Equal("web01", Assert.Single(SettingsStore.Load().Items).Name);
            Assert.Equal("Startup password", AppLock.Describe());

            // Change the password: the old one stops working, the data stays readable.
            Assert.True(AppLock.VerifyPassword("correct horse battery"));
            var oldLockFile = File.ReadAllBytes(AppLock.FilePath);
            await SettingsStore.Reprotect(data, () => AppLock.ChangePassword("new startup password"));
            LocalDb.Close();
            AppLock.Forget();
            Assert.False(AppLock.TryUnlock("correct horse battery"));
            Assert.True(AppLock.TryUnlock("new startup password"));
            Assert.Equal("web01", Assert.Single(SettingsStore.Load().Items).Name);

            // A copy of the old lock file with the old password opens nothing: the key itself was replaced.
            LocalDb.Close();
            AppLock.Forget();
            var newLockFile = File.ReadAllBytes(AppLock.FilePath);
            File.WriteAllBytes(AppLock.FilePath, oldLockFile);
            Assert.True(AppLock.TryUnlock("correct horse battery"));
            Assert.ThrowsAny<Exception>(() => SettingsStore.Load());
            LocalDb.Close();
            AppLock.Forget();
            File.WriteAllBytes(AppLock.FilePath, newLockFile);
            Assert.True(AppLock.TryUnlock("new startup password"));
            Assert.Equal("web01", Assert.Single(SettingsStore.Load().Items).Name);

            // Turn off: the key is stored with Windows (DPAPI) protection only and readable without unlocking.
            await SettingsStore.Reprotect(data, AppLock.Disable);
            Assert.False(AppLock.IsEnabled);
            Assert.False(AppLock.IsUnlocked);
            Assert.Equal("RHDK\u0001"u8.ToArray(), Head(DbKey));
            Assert.NotEqual("RHK2"u8.ToArray(), Head(Path.Combine(SettingsStore.Directory, "keys.dat"), 4));
            LocalDb.Close();
            Assert.Equal("web01", Assert.Single(SettingsStore.Load().Items).Name);
            Assert.Equal("Off", AppLock.Describe());
        }
        finally
        {
            if (AppLock.IsEnabled) File.Delete(AppLock.FilePath);
            AppLock.Forget();
            StoreReset.Clean();
        }
    }

    private static DbKeys.Keys DbKeysFor(string path) => DbKeys.Read(path);

    [Fact]
    public async Task A_stop_while_turning_the_protection_off_loses_nothing()
    {
        Assert.StartsWith(Path.GetTempPath(), SettingsStore.Directory); // isolation guard
        StoreReset.Clean();
        try
        {
            var data = new AppData();
            data.Items.Add(new Connection { Name = "web01", Host = "web01.contoso.local" });
            SettingsStore.Save(data, force: true);
            await SettingsStore.Reprotect(data, () => { AppLock.Enable("correct horse battery"); return Task.CompletedTask; });
            Assert.Equal("RHDK\u0002"u8.ToArray(), Head(DbKey));

            // The lock is gone, but RemoteHub stops before the database key is stored again (db.key still sealed).
            LocalDb.BeginReprotect();
            await AppLock.Disable();
            LocalDb.Close();
            Assert.Equal("RHDK\u0002"u8.ToArray(), Head(DbKey));

            // The next start opens the database with the copy and stores the key with DPAPI only.
            Assert.Equal("web01", Assert.Single(SettingsStore.Load().Items).Name);
            Assert.Equal("RHDK\u0001"u8.ToArray(), Head(DbKey));
            Assert.False(File.Exists(DbKey + ".reprotect"));
        }
        finally
        {
            if (AppLock.IsEnabled) File.Delete(AppLock.FilePath);
            AppLock.Forget();
            StoreReset.Clean();
        }
    }

    [Fact]
    public async Task A_stop_while_turning_the_protection_on_asks_for_the_new_password()
    {
        Assert.StartsWith(Path.GetTempPath(), SettingsStore.Directory); // isolation guard
        StoreReset.Clean();
        try
        {
            var data = new AppData();
            data.Items.Add(new Connection { Name = "web01", Host = "web01.contoso.local" });
            SettingsStore.Save(data, force: true);

            // The lock is on, but RemoteHub stops before the database key is sealed with it.
            LocalDb.BeginReprotect();
            AppLock.Enable("correct horse battery");
            LocalDb.Close();
            AppLock.Forget();

            Assert.Throws<AppLockedException>(() => SettingsStore.Load());
            Assert.True(AppLock.TryUnlock("correct horse battery"));
            Assert.Equal("web01", Assert.Single(SettingsStore.Load().Items).Name);
            Assert.Equal("RHDK\u0002"u8.ToArray(), Head(DbKey));
            Assert.False(File.Exists(DbKey + ".reprotect"));
        }
        finally
        {
            if (AppLock.IsEnabled) File.Delete(AppLock.FilePath);
            AppLock.Forget();
            StoreReset.Clean();
        }
    }

    [Fact]
    public void Nothing_can_be_sealed_while_locked()
    {
        AppLock.Forget();
        Assert.Throws<AppLockedException>(() => AppLock.Seal([1, 2, 3]));
        Assert.Throws<AppLockedException>(() => AppLock.Open(new byte[40]));
    }
}

/// <summary>Empties the test data folder: closes the database and deletes it, its key, backups and all settings files.</summary>
internal static class StoreReset
{
    public static void Clean()
    {
        LocalDb.DeleteAll();
        AuditLog.DiscardPending();
        SettingsStore.UsingLocalFallback = false;
        if (Directory.Exists(SettingsStore.Directory))
            foreach (var f in Directory.GetFiles(SettingsStore.Directory, "*", SearchOption.AllDirectories)) File.Delete(f);
        Directory.CreateDirectory(SettingsStore.Directory);
    }
}
