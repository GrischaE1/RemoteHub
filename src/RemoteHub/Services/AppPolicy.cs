using RemoteHub.Agent;
using RemoteHub.Models;

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

    /// <summary>DWORD DisableTelemetry: 1 = no usage data is sent, 0 = sent and users can't turn it off (since 0.18.12), null = the user decides.</summary>
    public static bool? Telemetry => PolicyCatalog.Number(PolicyCatalog.DisableTelemetry, Source) switch { 1 => false, 0 => true, _ => null };

    public static bool TelemetryDisabled => Telemetry == false;

    /// <summary>
    /// DWORD DisableUpdateCheck: 1 = RemoteHub never contacts the update server (e.g. when updates come via software
    /// deployment), 0 = it checks and users can't turn it off (since 0.18.12), null = the user decides.
    /// </summary>
    public static bool? UpdateCheck => PolicyCatalog.Number(PolicyCatalog.DisableUpdateCheck, Source) switch { 1 => false, 0 => true, _ => null };

    public static bool UpdateCheckDisabled => UpdateCheck == false;

    // Phase 4 (docs/admx-design.md §6): settings IT fixes. Null: the user's choice applies.
    public static string? Theme => PolicyCatalog.Text(PolicyCatalog.Theme, Source);
    public static string? SwitchKeys => PolicyCatalog.Text(PolicyCatalog.SwitchKeys, Source);
    public static int? RecordingFps => PolicyCatalog.Number(PolicyCatalog.RecordingFps, Source);
    public static string? RecordingResolution => PolicyCatalog.Text(PolicyCatalog.RecordingResolution, Source);
    public static bool? RecordingUseGpu => Flag(PolicyCatalog.RecordingUseGpu);
    public static bool? RecordingIncludeDetails => Flag(PolicyCatalog.RecordingIncludeDetails);
    public static int? ProcessRefreshSeconds => PolicyCatalog.Number(PolicyCatalog.ProcessRefreshSeconds, Source);
    public static int? ServiceRefreshSeconds => PolicyCatalog.Number(PolicyCatalog.ServiceRefreshSeconds, Source);
    public static string? AgentDesktopQuality => PolicyCatalog.Text(PolicyCatalog.AgentDesktopQuality, Source);
    public static string? AgentDesktopConsent => PolicyCatalog.Text(PolicyCatalog.AgentDesktopConsent, Source);
    /// <summary>1: every agent desktop starts view only, and it can't be switched to control.</summary>
    public static bool AgentDesktopViewOnly => Flag(PolicyCatalog.AgentDesktopViewOnly) == true;
    public static bool? AgentDesktopFitToWindow => Flag(PolicyCatalog.AgentDesktopFitToWindow);
    public static bool? AgentDesktopShowInfo => Flag(PolicyCatalog.AgentDesktopShowInfo);
    public static bool? AgentDesktopKeyCombinations => Flag(PolicyCatalog.AgentDesktopKeyCombinations);
    /// <summary>The highest clipboard level RemoteHub uses in an agent desktop (0–3); the lower of this and the device's applies.</summary>
    public static int? AgentDesktopClipboard => PolicyCatalog.Number(PolicyCatalog.AgentDesktopClipboard, Source);

    /// <summary>A value of the agent install defaults (subkey AgentInstall, §6.9), by name.</summary>
    public static object? AgentInstall(string name) =>
        PolicyCatalog.AgentInstallEntries.FirstOrDefault(e => e.Name == name) is { } e ? PolicyCatalog.Value(e, Source) : null;

    private static bool? Flag(PolicyEntry entry) => PolicyCatalog.Number(entry, Source) is { } n ? n == 1 : null;

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

    /// <summary>One read of the Connections key; a class, so it is replaced as a whole (no torn read from another thread).</summary>
    private sealed record ConnectionsRead(IPolicySource Source, long At, RdpOptions Options);
    private static ConnectionsRead? _connections;

    /// <summary>
    /// The connection settings IT fixes for every connection (subkey Connections, docs/admx-design.md §7): only the configured
    /// properties are set. Applied last when a connection's settings are resolved, so they win over the connection, its
    /// folders and the global defaults, without changing what the settings file stores (§4.3). Read again after 2 s at most:
    /// it is asked for every row of the tree, and a gpupdate still takes effect without a restart. Each caller gets its own copy.
    /// </summary>
    public static RdpOptions Connections()
    {
        var source = Source;
        if (_connections is not { } c || !ReferenceEquals(c.Source, source) || Environment.TickCount64 - c.At >= 2000)
            _connections = c = new ConnectionsRead(source, Environment.TickCount64, ConnectionsFrom(source));
        return c.Options.Clone();
    }

    internal static RdpOptions ConnectionsFrom(IPolicySource source)
    {
        var o = new RdpOptions();
        foreach (var e in PolicyCatalog.ConnectionEntries)
        {
            if (PolicyCatalog.Value(e, source) is not { } v) continue;
            var p = typeof(RdpOptions).GetProperty(e.Name)!;
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            object? value = t == typeof(bool) ? (int)v == 1
                : t == typeof(int) ? v
                : t.IsEnum ? Enum.Parse(t, (string)v, ignoreCase: true)
                : v; // FixedResolution: the catalogue's rule took only "<width>x<height>"
            p.SetValue(o, value);
        }
        return o;
    }

    /// <summary>The value IT fixes for a connection property (<c>nameof(RdpOptions.…)</c>), for the editor's lock; null: not fixed.</summary>
    public static object? Connection(string property) => typeof(RdpOptions).GetProperty(property)?.GetValue(Connections());
}
