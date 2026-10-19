using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteHub.Agent;

/// <summary>%ProgramData%\RemoteHub\Agent\agent.json – written by the enrollment, editable afterwards (e.g. the port).</summary>
public sealed class AgentConfig
{
    public string DeviceId { get; set; } = "";
    public string DeviceLabel { get; set; } = "";
    public int ListenPort { get; set; } = AgentProtocol.DefaultPort;

    /// <summary>Thumbprint (SHA-1, as Windows shows it) of this device's certificate in LocalMachine\My.</summary>
    public string CertificateThumbprint { get; set; } = "";

    /// <summary>Who may connect: group SIDs or names. Default: the local Administrators group.</summary>
    public List<string> AllowedGroups { get; set; } = [AdministratorsSid];

    /// <summary>
    /// Virtual monitor for the remote desktop (third-party driver, installed with the agent unless switched off at install):
    /// "auto" – only when the device offers no usable resolution (no monitor connected), "always", "off" (not installed).
    /// </summary>
    public string VirtualDisplay { get; set; } = VirtualDisplayModes.Auto;

    public int LogRetentionDays { get; set; } = 30;
    public int MaxSessions { get; set; } = 20;

    /// <summary>
    /// Automatic sign-in as the install chose it (MSI AUTOSIGNIN, RemoteHub's "Install credential provider"):
    /// "AutoSignIn" allows it, null leaves it off. The policy NoUserBehavior wins. Before 0.18.12 this was written into the
    /// policy key instead (<see cref="AutoSignInSetup.Migrate"/>).
    /// </summary>
    public string? NoUserBehavior { get; set; }

    /// <summary>Who may sign in automatically, as the install chose it (null: nobody). The policy Rights\AutoSignIn wins.</summary>
    public List<string>? AutoSignInGroups { get; set; }
    public const string AdministratorsSid = "S-1-5-32-544";

    [JsonIgnore]
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteHub", "Agent");

    [JsonIgnore]
    public static string DefaultPath => Path.Combine(Folder, "agent.json");

    /// <summary>The file this was loaded from (null: made in code). Automatic sign-in is read from it again for every connection.</summary>
    [JsonIgnore]
    public string? SourcePath { get; private set; }

    /// <summary>
    /// How the service came by this configuration when agent.json itself couldn't be read (null: from agent.json as it
    /// is) – for the log and the event, see <see cref="LoadForService"/>.
    /// </summary>
    [JsonIgnore]
    public string? Recovery { get; private set; }

    /// <summary>Whether this is a fresh install's defaults because neither agent.json nor its backup could be read.</summary>
    [JsonIgnore]
    public bool IsSafeDefaults { get; private set; }

    /// <summary>The last good copy of agent.json, written next to it with every save and refreshed at service start.</summary>
    public static string BackupPath(string path) => path + ".bak";

    /// <summary>A damaged agent.json, kept for support when the service started from the backup or defaults instead.</summary>
    public static string DamagedPath(string path) => path + ".damaged";

    /// <exception cref="DamagedConfigException">The file isn't a configuration at all (empty, zero-filled, cut off, not JSON).</exception>
    /// <exception cref="InvalidDataException">The file reads, but a value in it is invalid (e.g. a port edited by hand).</exception>
    public static AgentConfig Load(string path)
    {
        var config = Parse(File.ReadAllBytes(path));
        config.Validate();
        config.SourcePath = path;
        return config;
    }

    private static AgentConfig Parse(byte[] content)
    {
        try
        {
            // As File.ReadAllText reads it: a BOM (Notepad, PowerShell) is fine.
            using var reader = new StreamReader(new MemoryStream(content), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return JsonSerializer.Deserialize(reader.ReadToEnd(), AgentConfigJson.Default.AgentConfig) ?? throw new DamagedConfigException("Empty configuration.");
        }
        catch (JsonException ex) { throw new DamagedConfigException(ex.Message, ex); }
    }

    /// <summary>
    /// agent.json for the service at its start. A damaged file (lab finding A-CFG-JSON: zero-filled after a power loss)
    /// no longer stops the service for good, which would leave the device unreachable:
    /// <list type="number">
    /// <item>Readable: used, and its backup refreshed from it (so a hand edit is the last good copy from now on).</item>
    /// <item>Damaged, with a readable backup: the backup is used and written back to agent.json; the damaged file is kept
    /// as agent.json.damaged.</item>
    /// <item>Damaged, no usable backup: a fresh install's defaults with the device's own certificate
    /// and the port its firewall rule opens (<paramref name="identity"/>), not saved – see docs/agent-spec.md §7.2.</item>
    /// </list>
    /// A file that reads but holds an invalid value is not treated as damaged: it is what someone wrote, and replacing it
    /// with an older copy could undo a restriction they made – the service refuses to start, as before. A missing file
    /// means not enrolled (uninstall deletes it), so that still stops the service too.
    /// </summary>
    /// <param name="identity">The device ID and certificate thumbprint from the certificate store and the port of the agent's
    /// firewall rule (null: no single usable certificate; port null: no rule with one port).</param>
    public static AgentConfig LoadForService(string path, Func<(string DeviceId, string Thumbprint, int? Port)?> identity)
    {
        DamagedConfigException damage;
        try
        {
            var config = Load(path);
            RefreshBackup(path);
            return config;
        }
        catch (DamagedConfigException ex) { damage = ex; }

        var name = Path.GetFileName(path);
        try { File.Copy(path, DamagedPath(path), overwrite: true); } catch { /* support loses a copy, nothing else */ }

        if (TryLoadBackup(path) is { } good)
        {
            string written;
            try
            {
                DurableFile.WriteAllBytes(path, File.ReadAllBytes(BackupPath(path)));
                written = $"and wrote it back to {name}";
            }
            catch (Exception ex) { written = $"but could not write it back to {name} ({ex.Message})"; }
            good.SourcePath = path;
            good.Recovery = $"{name} could not be read ({damage.Message}); started with {Path.GetFileName(BackupPath(path))}, its last good copy, " +
                            $"{written}. The damaged file is kept as {Path.GetFileName(DamagedPath(path))}.";
            return good;
        }

        // docs/agent-spec.md §7.2: never more than a fresh install allows. Automatic sign-in off (null), the local
        // Administrators – who can edit agent.json anyway, so this gives nobody access they couldn't give themselves –
        // the port the firewall rule already opens (else the default) and the default limits. Policy values still win over
        // these, as over agent.json.
        if (identity() is not { } id)
            throw new InvalidDataException($"{name} could not be read ({damage.Message}), there is no usable backup and no single device " +
                                           "certificate of the agent – install the agent again with the device's enrollment file.", damage);
        var port = id.Port is >= 1 and <= 65535 ? id.Port.Value : AgentProtocol.DefaultPort;
        return new AgentConfig
        {
            DeviceId = id.DeviceId,
            CertificateThumbprint = id.Thumbprint,
            ListenPort = port,
            IsSafeDefaults = true,
            // SourcePath stays null: automatic sign-in isn't read from the damaged file again while the service runs.
            Recovery = $"{name} could not be read ({damage.Message}) and there is no usable backup. Started with a fresh install's " +
                       $"defaults: port {port} ({(id.Port == port ? "the firewall rule's" : "the default")}; the policy ListenPort wins), " +
                       "allowed groups Administrators, automatic sign-in off. Install the agent again with the device's enrollment file " +
                       "or repair agent.json, then restart the service.",
        };
    }

    private static AgentConfig? TryLoadBackup(string path)
    {
        try
        {
            var backup = Parse(File.ReadAllBytes(BackupPath(path)));
            backup.Validate();
            return backup;
        }
        catch { return null; }
    }

    /// <summary>The backup as the file reads now – only ever from a file that has just been read and validated.</summary>
    private static void RefreshBackup(string path)
    {
        try
        {
            var content = File.ReadAllBytes(path);
            Parse(content).Validate(); // the file may have changed since it was loaded
            var backup = BackupPath(path);
            if (File.Exists(backup) && File.ReadAllBytes(backup).AsSpan().SequenceEqual(content)) return;
            DurableFile.WriteAllBytes(backup, content);
        }
        catch { /* the old backup stays; the next save or start tries again */ }
    }

    /// <summary>
    /// This configuration with automatic sign-in as the file says now: RemoteHub's "Install / Remove credential provider"
    /// changes it while the service runs (<c>auto-sign-in on|off</c>), without a restart. Unreadable: as loaded.
    /// </summary>
    public AgentConfig WithCurrentAutoSignIn()
    {
        if (SourcePath is null) return this;
        try
        {
            var now = JsonSerializer.Deserialize(File.ReadAllText(SourcePath), AgentConfigJson.Default.AgentConfig);
            if (now is null || (now.NoUserBehavior == NoUserBehavior && Same(now.AutoSignInGroups, AutoSignInGroups))) return this;
            var copy = (AgentConfig)MemberwiseClone();
            copy.NoUserBehavior = now.NoUserBehavior;
            copy.AutoSignInGroups = now.AutoSignInGroups;
            return copy;
        }
        catch { return this; }

        static bool Same(List<string>? a, List<string>? b) => a is null ? b is null : b is not null && a.SequenceEqual(b);
    }

    /// <summary>
    /// Written in one step (<see cref="DurableFile"/>): a power loss leaves the old file or the new one. The backup follows
    /// only once agent.json has been replaced, so it never holds anything agent.json didn't hold.
    /// </summary>
    /// <param name="beforeReplace">For tests: a crash between the flushed temporary file and the replace.</param>
    public void Save(string path, Action? beforeReplace = null)
    {
        var json = JsonSerializer.Serialize(this, AgentConfigJson.Default.AgentConfig);
        DurableFile.WriteAllText(path, json, beforeReplace);
        try { DurableFile.WriteAllText(BackupPath(path), json); }
        catch { /* agent.json is saved; the next service start refreshes the backup from it */ }
    }

    public void Validate()
    {
        if (ListenPort is < 1 or > 65535) throw new InvalidDataException($"listenPort {ListenPort} is not a valid port.");
        if (AllowedGroups.Count == 0) throw new InvalidDataException("allowedGroups is empty – nobody could connect.");
        if (MaxSessions < 1) throw new InvalidDataException("maxSessions must be at least 1.");
        if (!VirtualDisplayModes.IsValid(VirtualDisplay)) throw new InvalidDataException($"virtualDisplay must be \"auto\", \"always\" or \"off\", not \"{VirtualDisplay}\".");
    }
}

/// <summary>agent.json isn't a configuration at all – empty, zero-filled, cut off or not JSON – as opposed to an invalid value.</summary>
public sealed class DamagedConfigException : IOException
{
    public DamagedConfigException(string message, Exception? inner = null) : base(message, inner) { }
}

public static class VirtualDisplayModes
{
    public const string Auto = "auto", Always = "always", Off = "off";
    public static bool IsValid(string? mode) => mode is Auto or Always or Off;
}

[JsonSerializable(typeof(AgentConfig))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
internal partial class AgentConfigJson : JsonSerializerContext;
