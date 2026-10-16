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

    public static AgentConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize(File.ReadAllText(path), AgentConfigJson.Default.AgentConfig) ?? throw new InvalidDataException("Empty configuration.");
        config.Validate();
        config.SourcePath = path;
        return config;
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

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, AgentConfigJson.Default.AgentConfig));
    }

    public void Validate()
    {
        if (ListenPort is < 1 or > 65535) throw new InvalidDataException($"listenPort {ListenPort} is not a valid port.");
        if (AllowedGroups.Count == 0) throw new InvalidDataException("allowedGroups is empty – nobody could connect.");
        if (MaxSessions < 1) throw new InvalidDataException("maxSessions must be at least 1.");
        if (!VirtualDisplayModes.IsValid(VirtualDisplay)) throw new InvalidDataException($"virtualDisplay must be \"auto\", \"always\" or \"off\", not \"{VirtualDisplay}\".");
    }
}

public static class VirtualDisplayModes
{
    public const string Auto = "auto", Always = "always", Off = "off";
    public static bool IsValid(string? mode) => mode is Auto or Always or Off;
}

[JsonSerializable(typeof(AgentConfig))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
internal partial class AgentConfigJson : JsonSerializerContext;
