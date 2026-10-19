using RemoteHub.Agent;

namespace RemoteHub.Services;

/// <summary>
/// Settings IT can enforce under HKLM (or HKCU) \SOFTWARE\Policies\RemoteHub. A value set there wins over the user's
/// choice, and the settings window shows it as managed. Names, types and ranges come from <see cref="PolicyCatalog"/>, which
/// the ADMX template (policy\RemoteHub.admx) is checked against; an invalid value counts as not set.
/// </summary>
public static class AppPolicy
{
    /// <summary>Where the values are read from: the registry; a dictionary in tests.</summary>
    internal static IPolicySource Source { get; set; } = RegistryPolicySource.Instance;

    /// <summary>DWORD DisableTelemetry = 1: no usage data is sent.</summary>
    public static bool TelemetryDisabled => PolicyCatalog.Number(PolicyCatalog.DisableTelemetry, Source) == 1;

    /// <summary>DWORD DisableUpdateCheck = 1: RemoteHub never contacts the update server (e.g. when updates come via software deployment).</summary>
    public static bool UpdateCheckDisabled => PolicyCatalog.Number(PolicyCatalog.DisableUpdateCheck, Source) == 1;

    /// <summary>String UpdateChannel = "stable" or "beta": fixes the update channel.</summary>
    public static string? UpdateChannel => UpdateService.NormalizeChannel(PolicyCatalog.Text(PolicyCatalog.UpdateChannel, Source));

    /// <summary>
    /// DWORD DisableAgentAutoSignIn = 1 (or AllowAgent = 0): RemoteHub never signs in automatically through the agent (the
    /// setting is greyed out).
    /// </summary>
    public static bool AgentAutoSignInDisabled => !Features.IsAllowed(Feature.AgentAutoSignIn);

    /// <summary>DWORD DisableRecording = 1: tabs can't be recorded (the Record button is hidden).</summary>
    public static bool RecordingDisabled => !Features.IsAllowed(Feature.Recording);

    /// <summary>String RecordingFolder (environment variables allowed): where recordings are saved; users can't change it.</summary>
    public static string? RecordingFolder => PolicyCatalog.Text(PolicyCatalog.RecordingFolder, Source);

    /// <summary>DWORD AuditRetentionDays (1–36500): how long the audit log keeps entries; default 365.</summary>
    public static int? AuditRetentionDays => PolicyCatalog.Number(PolicyCatalog.AuditRetentionDays, Source);

    /// <summary>String SettingsFolder (environment variables allowed): the shared settings folder; also read from the old RdpManager key.</summary>
    public static string? SettingsFolder => PolicyCatalog.Text(PolicyCatalog.SettingsFolder, Source);

    /// <summary>Every console value that is set (valid or not), for Settings → Policies.</summary>
    public static IReadOnlyList<PolicyValue> Configured() => PolicyCatalog.Configured(PolicyCatalog.ConsoleEntries, Source);
}
