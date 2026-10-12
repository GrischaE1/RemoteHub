using RemoteHub.Remoting;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>After the rename from "RDP Manager": existing settings, shared files and target rules keep working.</summary>
public class RebrandTests
{
    [Fact]
    public void Old_settings_folder_is_copied_once_and_kept()
    {
        var root = Path.Combine(Path.GetTempPath(), "rh-migrate-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "RdpManager");
        var dir = Path.Combine(root, "RemoteHub");
        try
        {
            Directory.CreateDirectory(Path.Combine(legacy, "logs"));
            File.WriteAllText(Path.Combine(legacy, "settings.dat"), "data");
            File.WriteAllText(Path.Combine(legacy, "logs", "rdpmanager.log"), "log");

            Assert.True(SettingsStore.MigrateFolder(legacy, dir));
            Assert.Equal("data", File.ReadAllText(Path.Combine(dir, "settings.dat")));
            Assert.True(File.Exists(Path.Combine(dir, "logs", "rdpmanager.log")));
            Assert.True(File.Exists(Path.Combine(legacy, "settings.dat")));   // the old folder stays as a backup
            Assert.False(Directory.Exists(dir + ".migrating"));

            // Only once: an existing RemoteHub folder is never overwritten.
            File.WriteAllText(Path.Combine(legacy, "settings.dat"), "older");
            Assert.False(SettingsStore.MigrateFolder(legacy, dir));
            Assert.Equal("data", File.ReadAllText(Path.Combine(dir, "settings.dat")));
            // Nothing to migrate.
            Assert.False(SettingsStore.MigrateFolder(Path.Combine(root, "missing"), Path.Combine(root, "new")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Shared_folder_keeps_using_an_existing_RdpManager_settings_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rh-shared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Equal(Path.Combine(folder, "RemoteHub.settings"), SettingsStore.SharedFilePathFor(folder));
            File.WriteAllText(Path.Combine(folder, "RdpManager.settings"), "{}");
            Assert.Equal(Path.Combine(folder, "RdpManager.settings"), SettingsStore.SharedFilePathFor(folder));
            File.WriteAllText(Path.Combine(folder, "RemoteHub.settings"), "{}");
            Assert.Equal(Path.Combine(folder, "RemoteHub.settings"), SettingsStore.SharedFilePathFor(folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Setup_script_replaces_the_firewall_rule_of_the_old_name()
    {
        var script = RemotingSetup.BuildScript("Lab", "host1", ["10.0.0.1"], false, DateTime.Now);
        Assert.Contains("'RemoteHub-WinRM-HTTP-In', 'RdpManager-WinRM-HTTP-In'", script);
        Assert.Contains("New-NetFirewallRule -Name 'RemoteHub-WinRM-HTTP-In'", script);
        Assert.DoesNotContain("RDP Manager", script);
    }
}
