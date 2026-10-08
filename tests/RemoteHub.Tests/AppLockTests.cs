using System.Threading.Tasks;
using System.Reflection;
using RemoteHub.Models;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>
/// Startup protection: the settings file and the remembered shared-file key are sealed with a key that only the
/// startup password (or Windows Hello) unlocks. Windows Hello itself needs a person at the PC and isn't run here.
/// </summary>
[Collection("SettingsStore")]
public class AppLockTests
{
    private static byte[] Head(string path) => File.ReadAllBytes(path)[..5];

    [Fact]
    public async Task Settings_are_sealed_until_the_password_unlocks_them()
    {
        Assert.StartsWith(Path.GetTempPath(), SettingsStore.Directory); // isolation guard
        Directory.CreateDirectory(SettingsStore.Directory);
        foreach (var f in Directory.GetFiles(SettingsStore.Directory)) File.Delete(f);
        try
        {
            var data = new AppData();
            data.Items.Add(new Connection { Name = "web01", Host = "web01.contoso.local" });
            SettingsStore.Save(data, force: true);
            SettingsStore.Save(data, force: true); // leaves an unprotected settings.dat.bak behind
            var keyCacheSet = typeof(SettingsStore).GetMethod("KeyCacheSet", BindingFlags.NonPublic | BindingFlags.Static)!;
            keyCacheSet.Invoke(null, [new byte[16], new byte[32]]);
            Assert.Equal("RDPM\u0001"u8.ToArray(), Head(SettingsStore.LocalFilePath));

            // Turn on: everything on this PC is rewritten sealed, and no unprotected copy stays behind.
            await SettingsStore.Reprotect(data, () => { AppLock.Enable("correct horse battery"); return Task.CompletedTask; });
            Assert.True(AppLock.IsEnabled);
            Assert.Equal("RDPM\u0002"u8.ToArray(), Head(SettingsStore.LocalFilePath));
            Assert.False(File.Exists(SettingsStore.LocalFilePath + ".bak"));
            var keys = Path.Combine(SettingsStore.Directory, "keys.dat");
            Assert.Equal("RHK2"u8.ToArray(), File.ReadAllBytes(keys)[..4]);
            Assert.DoesNotContain("web01", File.ReadAllText(AppLock.FilePath));

            // After a restart the settings can't be read without unlocking.
            AppLock.Forget();
            Assert.Throws<AppLockedException>(() => SettingsStore.Load());
            Assert.False(AppLock.TryUnlock("wrong password"));
            Assert.False(AppLock.IsUnlocked);
            Assert.True(AppLock.TryUnlock("correct horse battery"));
            Assert.Equal("web01", Assert.Single(SettingsStore.Load().Items).Name);
            Assert.Equal("Startup password", AppLock.Describe());

            // Change the password: the old one stops working, the data stays readable.
            Assert.True(AppLock.VerifyPassword("correct horse battery"));
            AppLock.ChangePassword("new startup password");
            AppLock.Forget();
            Assert.False(AppLock.TryUnlock("correct horse battery"));
            Assert.True(AppLock.TryUnlock("new startup password"));
            Assert.Equal("web01", Assert.Single(SettingsStore.Load().Items).Name);

            // Turn off: written back with Windows (DPAPI) protection only and readable without unlocking.
            await SettingsStore.Reprotect(data, AppLock.Disable);
            Assert.False(AppLock.IsEnabled);
            Assert.False(AppLock.IsUnlocked);
            Assert.Equal("RDPM\u0001"u8.ToArray(), Head(SettingsStore.LocalFilePath));
            Assert.NotEqual("RHK2"u8.ToArray(), File.ReadAllBytes(keys)[..4]);
            Assert.Equal("web01", Assert.Single(SettingsStore.Load().Items).Name);
            Assert.Equal("Off", AppLock.Describe());
        }
        finally
        {
            if (AppLock.IsEnabled) File.Delete(AppLock.FilePath);
            AppLock.Forget();
            foreach (var f in Directory.GetFiles(SettingsStore.Directory)) File.Delete(f);
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
