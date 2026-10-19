extern alias agent;
using System.Collections.Generic;
using System.Text;

using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// agent.json survives a power loss (lab finding A-CFG-JSON, docs/agent-spec.md §7.2): saves are atomic and keep a last
/// good copy, and a damaged file at service start falls back to that copy or to a fresh install's defaults.
/// </summary>
public class AgentConfigDurabilityTests
{
    private const string Admins = "S-1-5-32-544", Helpdesk = "S-1-5-21-1-2-3-1100";
    private const string DeviceId = "3f1a9e2c-7b44-4b2a-9e21-0c6a2f9b7e10";

    private static readonly (string, string, int?)? NoIdentity = null;

    public static TheoryData<string> Damage => ["zero-filled", "empty", "truncated", "invalid", "null"];

    private static byte[] Damaged(string kind, string path) => kind switch
    {
        "zero-filled" => new byte[File.ReadAllBytes(path).Length], // what the lab VM had after the power-off
        "empty" => [],
        "truncated" => File.ReadAllBytes(path)[..(File.ReadAllBytes(path).Length / 2)],
        "invalid" => Encoding.UTF8.GetBytes("{ \"listenPort\": 58732, allowedGroups: ["),
        _ => Encoding.UTF8.GetBytes("null"),
    };

    /// <summary>The install's choices – all different from a fresh install, so a fallback is visible.</summary>
    private static A.AgentConfig Chosen() => new()
    {
        DeviceId = DeviceId, CertificateThumbprint = "AB12", ListenPort = 60000, AllowedGroups = [Helpdesk],
        VirtualDisplay = "off", MaxSessions = 3, NoUserBehavior = "AutoSignIn", AutoSignInGroups = [Helpdesk],
    };

    [Theory]
    [MemberData(nameof(Damage))]
    public void A_damaged_agent_json_starts_the_service_with_its_last_good_copy(string kind)
    {
        using var dir = new TempFolder();
        var path = dir.File("agent.json");
        Chosen().Save(path);
        var bad = Damaged(kind, path);
        File.WriteAllBytes(path, bad);

        var config = A.AgentConfig.LoadForService(path, () => throw new InvalidOperationException("not needed with a backup"));

        Assert.Equal((60000, "off", 3, "AutoSignIn"), (config.ListenPort, config.VirtualDisplay, config.MaxSessions, config.NoUserBehavior));
        Assert.Equal([Helpdesk], config.AllowedGroups);
        Assert.False(config.IsSafeDefaults);
        Assert.Contains("agent.json could not be read", config.Recovery);
        Assert.Contains("started with agent.json.bak, its last good copy, and wrote it back to agent.json", config.Recovery);
        Assert.Equal(path, config.SourcePath);
        Assert.Equal(60000, A.AgentConfig.Load(path).ListenPort); // repaired for the next start and for auto-sign-in on|off
        Assert.Equal(bad, File.ReadAllBytes(A.AgentConfig.DamagedPath(path))); // kept for support
    }

    [Theory]
    [MemberData(nameof(Damage))]
    public void Without_a_backup_a_damaged_agent_json_starts_with_a_fresh_installs_defaults(string kind)
    {
        using var dir = new TempFolder();
        var path = dir.File("agent.json");
        Chosen().Save(path);
        File.Delete(A.AgentConfig.BackupPath(path));
        var bad = Damaged(kind, path);
        File.WriteAllBytes(path, bad);

        var config = A.AgentConfig.LoadForService(path, () => (DeviceId, "CD34", 60000));

        Assert.True(config.IsSafeDefaults);
        Assert.Equal((DeviceId, "CD34", 60000), (config.DeviceId, config.CertificateThumbprint, config.ListenPort));
        Assert.Equal([Admins], config.AllowedGroups);
        Assert.Null(config.NoUserBehavior); // automatic sign-in off
        Assert.Null(config.AutoSignInGroups);
        var fresh = new A.AgentConfig();
        Assert.Equal((fresh.VirtualDisplay, fresh.MaxSessions, fresh.LogRetentionDays), (config.VirtualDisplay, config.MaxSessions, config.LogRetentionDays));
        Assert.Null(config.SourcePath); // automatic sign-in is never read from the damaged file
        Assert.Same(config, config.WithCurrentAutoSignIn());
        Assert.Contains("there is no usable backup", config.Recovery);
        Assert.Contains("fresh install's defaults: port 60000 (the firewall rule's", config.Recovery);
        Assert.Contains("automatic sign-in off", config.Recovery);
        Assert.Equal(bad, File.ReadAllBytes(path)); // the defaults aren't saved over it
        Assert.False(File.Exists(A.AgentConfig.BackupPath(path))); // nor made the last good copy
    }

    [Fact]
    public void A_damaged_backup_is_no_fallback_and_without_a_certificate_the_service_does_not_start()
    {
        using var dir = new TempFolder();
        var path = dir.File("agent.json");
        Chosen().Save(path);
        File.WriteAllBytes(path, new byte[300]);
        File.WriteAllBytes(A.AgentConfig.BackupPath(path), new byte[300]);

        var defaults = A.AgentConfig.LoadForService(path, () => (DeviceId, "CD34", null));
        Assert.True(defaults.IsSafeDefaults);
        Assert.Equal(new A.AgentConfig().ListenPort, defaults.ListenPort); // no firewall rule port: the default
        Assert.Contains("(the default; the policy ListenPort wins)", defaults.Recovery);

        var ex = Assert.Throws<InvalidDataException>(() => A.AgentConfig.LoadForService(path, () => NoIdentity));
        Assert.Contains("install the agent again with the device's enrollment file", ex.Message);
    }

    [Fact]
    public void An_invalid_value_is_not_damage_and_never_replaced_by_the_backup()
    {
        using var dir = new TempFolder();
        var path = dir.File("agent.json");
        Chosen().Save(path);
        var edited = File.ReadAllText(path).Replace("60000", "0"); // a hand edit with a mistake
        File.WriteAllText(path, edited);
        var backup = File.ReadAllBytes(A.AgentConfig.BackupPath(path));

        var ex = Assert.Throws<InvalidDataException>(() => A.AgentConfig.LoadForService(path, () => (DeviceId, "CD34", null)));
        Assert.Contains("listenPort 0", ex.Message);
        Assert.Equal(edited, File.ReadAllText(path));
        Assert.Equal(backup, File.ReadAllBytes(A.AgentConfig.BackupPath(path)));
        Assert.False(File.Exists(A.AgentConfig.DamagedPath(path)));
    }

    [Fact]
    public void A_missing_agent_json_still_means_not_enrolled()
    {
        using var dir = new TempFolder();
        var path = dir.File("agent.json");
        Chosen().Save(path);
        File.Delete(path); // uninstall, or never enrolled: the backup is not brought back
        Assert.Throws<FileNotFoundException>(() => A.AgentConfig.LoadForService(path, () => (DeviceId, "CD34", null)));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_crash_between_the_temporary_file_and_the_replace_leaves_the_old_file_and_backup()
    {
        using var dir = new TempFolder();
        var path = dir.File("agent.json");
        Chosen().Save(path);
        var before = File.ReadAllBytes(path);
        var next = Chosen();
        next.ListenPort = 61000;
        string? temp = null;

        Assert.Throws<IOException>(() => next.Save(path, () =>
        {
            temp = Assert.Single(Directory.GetFiles(dir.Path, "agent.json.*.tmp"));
            Assert.Contains("61000", File.ReadAllText(temp)); // fully written before the replace
            throw new IOException("power lost");
        }));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(before, File.ReadAllBytes(A.AgentConfig.BackupPath(path)));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        Assert.Equal(60000, A.AgentConfig.LoadForService(path, () => NoIdentity).ListenPort);
    }

    [Fact]
    public void The_backup_follows_only_a_successfully_written_or_read_file()
    {
        using var dir = new TempFolder();
        var path = dir.File("agent.json");
        var backup = A.AgentConfig.BackupPath(path);
        Chosen().Save(path);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(backup));

        // A readable hand edit becomes the last good copy at the next start.
        File.WriteAllText(path, File.ReadAllText(path).Replace("60000", "62000"));
        Assert.Equal(62000, A.AgentConfig.LoadForService(path, () => NoIdentity).ListenPort);
        Assert.Contains("62000", File.ReadAllText(backup));

        // A damaged file never does.
        File.WriteAllBytes(path, new byte[100]);
        Assert.Equal(62000, A.AgentConfig.LoadForService(path, () => NoIdentity).ListenPort);
        Assert.Contains("62000", File.ReadAllText(backup));

        // Nor does an invalid one.
        File.WriteAllText(path, File.ReadAllText(path).Replace("62000", "70000"));
        Assert.Throws<InvalidDataException>(() => A.AgentConfig.LoadForService(path, () => NoIdentity));
        Assert.Contains("62000", File.ReadAllText(backup));
    }

    [Fact]
    public void Agent_json_written_with_a_BOM_still_loads()
    {
        using var dir = new TempFolder();
        var path = dir.File("agent.json");
        Chosen().Save(path);
        File.WriteAllText(path, File.ReadAllText(path), new UTF8Encoding(true)); // Notepad, PowerShell
        var config = A.AgentConfig.LoadForService(path, () => NoIdentity);
        Assert.Null(config.Recovery);
        Assert.Equal(60000, config.ListenPort);
    }

    [Fact]
    public void A_durable_write_replaces_an_existing_file_and_creates_a_new_one()
    {
        using var dir = new TempFolder();
        var file = dir.File(Path.Combine("sub", "state.txt"));
        A.DurableFile.WriteAllText(file, "one");
        A.DurableFile.WriteAllLines(file, ["a|b|1", "c|d|absent"]);
        Assert.Equal(["a|b|1", "c|d|absent"], File.ReadAllLines(file));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!));
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rh-cfg-" + Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { /* in use */ } }
    }
}
