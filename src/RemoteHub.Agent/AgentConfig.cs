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
    public const string AdministratorsSid = "S-1-5-32-544";

    [JsonIgnore]
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteHub", "Agent");

    [JsonIgnore]
    public static string DefaultPath => Path.Combine(Folder, "agent.json");

    public static AgentConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize(File.ReadAllText(path), AgentConfigJson.Default.AgentConfig) ?? throw new InvalidDataException("Empty configuration.");
        config.Validate();
        return config;
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
