using Microsoft.Win32;

namespace RemoteHub.Agent;

// Compiled into the agent and into RemoteHub (linked in RemoteHub.csproj): both products read their policy values through
// this catalogue, RemoteHub's Settings → Policies lists them from it, and a test checks policy\RemoteHub.admx against it
// (docs/admx-design.md §11.1).

/// <summary>Which product reads a policy value.</summary>
public enum PolicyProduct { Console, Agent }

/// <summary>Registry type of a policy value.</summary>
public enum PolicyKind
{
    /// <summary>REG_DWORD within <see cref="PolicyEntry.Min"/>–<see cref="PolicyEntry.Max"/>.</summary>
    Number,
    /// <summary>REG_SZ (or REG_EXPAND_SZ); with <see cref="PolicyEntry.Words"/> only one of them (case-insensitive).</summary>
    Text,
    /// <summary>REG_MULTI_SZ (a REG_SZ is split at ';' and ','); with <see cref="PolicyEntry.Words"/> each item one of them.</summary>
    List,
}

/// <summary>
/// One registry value IT can set. <see cref="Key"/> is relative to <see cref="PolicyCatalog.Root"/> ("" = the root itself).
/// <see cref="Labels"/> turns numbers or words into text for Settings → Policies ("1" → "Turned off").
/// </summary>
public sealed record PolicyEntry(
    PolicyProduct Product,
    string Key,
    string Name,
    PolicyKind Kind,
    string Group,
    string Title,
    int Min = 0,
    int Max = 0,
    IReadOnlyList<string>? Words = null,
    IReadOnlyDictionary<string, string>? Labels = null,
    bool Expand = false,
    string Since = "0.18.11")
{
    public string FullKey => Key.Length == 0 ? PolicyCatalog.Root : PolicyCatalog.Root + "\\" + Key;

    /// <summary>Console values are read from HKLM and HKCU (HKLM wins), agent values from HKLM only.</summary>
    public bool UserScope => Product == PolicyProduct.Console;

    /// <summary>Checks a raw registry value. Returns the normalised value (int, string or string[]) or the reason it's invalid.</summary>
    public (object? Value, string? Problem) Check(object raw)
    {
        switch (Kind)
        {
            case PolicyKind.Number:
                if (raw is not int n) return (null, $"must be a DWORD, not {TypeName(raw)}");
                return n >= Min && n <= Max ? (n, null) : (null, $"{n} is outside {Min}–{Max}");
            case PolicyKind.Text:
                var s = raw switch { string t => t, string[] { Length: > 0 } l => l[0], _ => null };
                if (s is null) return (null, $"must be a string, not {TypeName(raw)}");
                s = s.Trim();
                if (s.Length == 0) return (null, "is empty");
                if (Words is null) return (Expand ? Environment.ExpandEnvironmentVariables(s) : s, null);
                return Words.FirstOrDefault(w => w.Equals(s, StringComparison.OrdinalIgnoreCase)) is { } word
                    ? (word, null) : (null, $"\"{s}\" isn't one of {string.Join(", ", Words)}");
            default:
                var items = raw switch
                {
                    string[] l => l,
                    string t => t.Split([';', ','], StringSplitOptions.RemoveEmptyEntries),
                    _ => null,
                };
                if (items is null) return (null, $"must be a list (REG_MULTI_SZ), not {TypeName(raw)}");
                var list = items.Select(i => i.Trim()).Where(i => i.Length > 0).ToArray();
                if (Words is not null && list.FirstOrDefault(i => !Words.Contains(i, StringComparer.OrdinalIgnoreCase)) is { } bad)
                    return (null, $"\"{bad}\" isn't one of {string.Join(", ", Words)}");
                return (list, null);
        }
    }

    /// <summary>A checked value as text for people ("Turned off", "30", "Ask; Pin").</summary>
    public string Describe(object value)
    {
        var text = value is string[] l ? string.Join("; ", l) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        return Labels is not null && Labels.TryGetValue(text, out var label) ? label : text;
    }

    private static string TypeName(object raw) => raw switch
    {
        int => "a DWORD",
        long => "a QWORD",
        string => "a string",
        string[] => "a list",
        byte[] => "binary data",
        _ => raw.GetType().Name,
    };
}

/// <summary>Where a configured value came from.</summary>
public enum PolicyScope { Computer, User }

/// <summary>
/// A policy value as found in the registry. <see cref="Problem"/> set: the value is there but invalid, so it counts as not
/// configured (the product's own setting or default applies).
/// </summary>
public sealed record PolicyValue(PolicyEntry Entry, object? Value, PolicyScope Scope, string? Problem, string? FromKey = null)
{
    public bool Valid => Problem is null;
}

/// <summary>Reads raw values; the registry in the products, a dictionary in tests.</summary>
public interface IPolicySource
{
    /// <summary>The raw value, or null when it (or its key) isn't there. Throws when the key exists but can't be read.</summary>
    object? Read(PolicyScope scope, string key, string name);
}

/// <summary>The registry under HKLM / HKCU.</summary>
public sealed class RegistryPolicySource : IPolicySource
{
    public static readonly RegistryPolicySource Instance = new();

    public object? Read(PolicyScope scope, string key, string name)
    {
        var hive = scope == PolicyScope.Computer ? Registry.LocalMachine : Registry.CurrentUser;
        using var k = hive.OpenSubKey(key);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }
}

/// <summary>Every value IT can set for RemoteHub and the RemoteHub Agent (docs/admx-design.md, phase 1: what exists today).</summary>
public static class PolicyCatalog
{
    public const string Root = @"SOFTWARE\Policies\RemoteHub";
    public const string AgentKey = "Agent";
    public const string RightsKey = @"Agent\Rights";
    /// <summary>The console's key from before the rename; only <see cref="SettingsFolder"/> is still read there.</summary>
    public const string LegacyRoot = @"SOFTWARE\Policies\RdpManager";

    private static readonly Dictionary<string, string> OnOff = new() { ["0"] = "Off", ["1"] = "On" };
    private static readonly Dictionary<string, string> TurnedOff = new() { ["0"] = "Not turned off (the user decides)", ["1"] = "Turned off" };

    // ------------------------------------------------------------------ RemoteHub (console)

    public static readonly PolicyEntry SettingsFolder = Console("SettingsFolder", PolicyKind.Text, "Settings storage", "Shared settings folder", expand: true);
    public static readonly PolicyEntry DisableUpdateCheck = Console("DisableUpdateCheck", PolicyKind.Number, "Updates and usage data", "Turn off the update check", 0, 1, labels: TurnedOff);
    public static readonly PolicyEntry UpdateChannel = Console("UpdateChannel", PolicyKind.Text, "Updates and usage data", "Update channel",
        words: ["stable", "beta", "ga", "release", "prerelease", "pre-release"],
        labels: new Dictionary<string, string> { ["stable"] = "GA – stable releases", ["ga"] = "GA – stable releases", ["release"] = "GA – stable releases", ["beta"] = "Beta", ["prerelease"] = "Beta", ["pre-release"] = "Beta" });
    public static readonly PolicyEntry DisableTelemetry = Console("DisableTelemetry", PolicyKind.Number, "Updates and usage data", "Turn off usage data and crash reports", 0, 1, labels: TurnedOff);
    public static readonly PolicyEntry DisableRecording = Console("DisableRecording", PolicyKind.Number, "Recordings", "Turn off recording", 0, 1, labels: TurnedOff);
    public static readonly PolicyEntry RecordingFolder = Console("RecordingFolder", PolicyKind.Text, "Recordings", "Recording folder", expand: true);
    public static readonly PolicyEntry AuditRetentionDays = Console("AuditRetentionDays", PolicyKind.Number, "Logs", "Keep audit log entries (days)", 1, 36_500);
    public static readonly PolicyEntry DisableAgentAutoSignIn = Console("DisableAgentAutoSignIn", PolicyKind.Number, "RemoteHub Agent", "Turn off automatic sign-in through the agent", 0, 1, labels: TurnedOff);

    // ------------------------------------------------------------------ RemoteHub Agent

    public static readonly PolicyEntry DesktopEnabled = Agent("DesktopEnabled", PolicyKind.Number, "Remote desktop", "Allow remote desktop", 0, 1, labels: OnOff);
    public static readonly PolicyEntry MaxFramesPerSecond = Agent("MaxFramesPerSecond", PolicyKind.Number, "Remote desktop", "Highest frame rate", 1, 60);
    public static readonly PolicyEntry MaxQuality = Agent("MaxQuality", PolicyKind.Number, "Remote desktop", "Highest picture quality", 1, 100);
    public static readonly PolicyEntry DesktopClipboard = Agent("DesktopClipboard", PolicyKind.Number, "Remote desktop", "Clipboard", 0, 3,
        labels: new Dictionary<string, string> { ["0"] = "Off", ["1"] = "Text", ["2"] = "Text and images", ["3"] = "Text, images and files" });
    public static readonly PolicyEntry DesktopIdleTimeoutMinutes = Agent("DesktopIdleTimeoutMinutes", PolicyKind.Number, "Remote desktop", "End idle remote desktops after (minutes, 0 = never)", 0, 1440);
    public static readonly PolicyEntry AllowedConsentModes = Agent("AllowedConsentModes", PolicyKind.List, "Consent", "Allowed ways to get the user's consent", words: ["Ask", "Pin", "None"]);
    public static readonly PolicyEntry ConsentTimeoutSeconds = Agent("ConsentTimeoutSeconds", PolicyKind.Number, "Consent", "Time to answer (seconds)", 5, 600);
    public static readonly PolicyEntry PrivacyScreen = Agent("PrivacyScreen", PolicyKind.Number, "Privacy", "Privacy screen", 0, 2,
        labels: new Dictionary<string, string> { ["0"] = "Not allowed", ["1"] = "Allowed, the device's mouse and keyboard stay usable", ["2"] = "Allowed, the device's mouse and keyboard can be blocked" });
    public static readonly PolicyEntry HiddenApps = Agent("HiddenApps", PolicyKind.List, "Privacy", "Apps never shown to an admin");
    public static readonly PolicyEntry NoUserBehavior = Agent("NoUserBehavior", PolicyKind.Text, "Automatic sign-in", "When nobody is signed in", words: ["SignInScreen", "AutoSignIn"],
        labels: new Dictionary<string, string> { ["SignInScreen"] = "Show the sign-in screen", ["AutoSignIn"] = "Allow automatic sign-in" });
    public static readonly PolicyEntry AutoSignInRequirePrivacyScreen = Agent("AutoSignInRequirePrivacyScreen", PolicyKind.Number, "Automatic sign-in", "Only with the privacy screen", 0, 1, labels: OnOff);
    public static readonly PolicyEntry AutoSignInEndAction = Agent("AutoSignInEndAction", PolicyKind.Text, "Automatic sign-in", "When the connection ends", words: ["Lock", "SignOut"],
        labels: new Dictionary<string, string> { ["Lock"] = "Lock the session", ["SignOut"] = "Sign out" });
    public static readonly PolicyEntry TunnelEnabled = Agent("TunnelEnabled", PolicyKind.Number, "Jump host", "Allow this device as a jump host", 0, 1, labels: OnOff);
    public static readonly PolicyEntry TunnelTargets = Agent("TunnelTargets", PolicyKind.List, "Jump host", "Where jump connections may go");
    public static readonly PolicyEntry TunnelPorts = Agent("TunnelPorts", PolicyKind.List, "Jump host", "Ports jump connections may go to");
    public static readonly PolicyEntry MaxTunnelStreams = Agent("MaxTunnelStreams", PolicyKind.Number, "Jump host", "Connections per jump connection", 1, 1024);
    public static readonly PolicyEntry AuditToEventLog = Agent("AuditToEventLog", PolicyKind.Number, "Service", "Audit to the Windows Application log", 0, 1, labels: OnOff);

    /// <summary>Subkey Rights: one list of groups per AgentRight, in the enum's order.</summary>
    public static readonly IReadOnlyList<PolicyEntry> Rights =
    [
        Right("PowerShell", "Who may use PowerShell, files, registry, services and processes"),
        Right("Desktop", "Who may see and control the screen"),
        Right("DesktopViewOnly", "Who may only see the screen"),
        Right("SecureDesktop", "Who may use the sign-in screen, lock screen and UAC prompts"),
        Right("ConsentBypass", "Who may connect without asking the signed-in user"),
        Right("PrivacyScreen", "Who may turn on the privacy screen"),
        Right("Tunnel", "Who may use this device as a jump host"),
        Right("AutoSignIn", "Who may sign in automatically"),
    ];

    public static readonly IReadOnlyList<PolicyEntry> ConsoleEntries =
        [SettingsFolder, DisableUpdateCheck, UpdateChannel, DisableTelemetry, DisableRecording, RecordingFolder, AuditRetentionDays, DisableAgentAutoSignIn];

    public static readonly IReadOnlyList<PolicyEntry> AgentEntries =
    [
        .. Rights,
        DesktopEnabled, MaxFramesPerSecond, MaxQuality, DesktopClipboard, DesktopIdleTimeoutMinutes,
        AllowedConsentModes, ConsentTimeoutSeconds,
        PrivacyScreen, HiddenApps,
        NoUserBehavior, AutoSignInRequirePrivacyScreen, AutoSignInEndAction,
        TunnelEnabled, TunnelTargets, TunnelPorts, MaxTunnelStreams,
        AuditToEventLog,
    ];

    public static IEnumerable<PolicyEntry> All => ConsoleEntries.Concat(AgentEntries);

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// The value in effect for an entry: HKLM first, then (console only) HKCU; for <see cref="SettingsFolder"/> each hive's
    /// legacy key after its own. An invalid value is returned with its problem and doesn't hide a valid one further down.
    /// Null when nothing is set. A key that can't be read counts as not set here; the agent treats that case itself.
    /// </summary>
    public static PolicyValue? Read(PolicyEntry entry, IPolicySource? source = null)
    {
        source ??= RegistryPolicySource.Instance;
        PolicyValue? invalid = null;
        foreach (var scope in entry.UserScope ? new[] { PolicyScope.Computer, PolicyScope.User } : [PolicyScope.Computer])
        {
            foreach (var key in ReferenceEquals(entry, SettingsFolder) ? new[] { entry.FullKey, LegacyRoot } : [entry.FullKey])
            {
                object? raw;
                try { raw = source.Read(scope, key, entry.Name); }
                catch { continue; }
                if (raw is null) continue;
                var (value, problem) = entry.Check(raw);
                var found = new PolicyValue(entry, value ?? raw, scope, problem, key == entry.FullKey ? null : key);
                if (found.Valid) return found;
                invalid ??= found;
            }
        }
        return invalid;
    }

    /// <summary>The valid value in effect, or null (not set or invalid).</summary>
    public static object? Value(PolicyEntry entry, IPolicySource? source = null) => Read(entry, source) is { Valid: true } v ? v.Value : null;

    public static int? Number(PolicyEntry entry, IPolicySource? source = null) => Value(entry, source) as int?;

    public static string? Text(PolicyEntry entry, IPolicySource? source = null) => Value(entry, source) as string;

    /// <summary>Every entry of a product that is set (valid or not), in catalogue order.</summary>
    public static IReadOnlyList<PolicyValue> Configured(IEnumerable<PolicyEntry> entries, IPolicySource? source = null) =>
        entries.Select(e => Read(e, source)).OfType<PolicyValue>().ToList();

    private static PolicyEntry Console(string name, PolicyKind kind, string group, string title, int min = 0, int max = 0,
        IReadOnlyList<string>? words = null, IReadOnlyDictionary<string, string>? labels = null, bool expand = false) =>
        new(PolicyProduct.Console, "", name, kind, group, title, min, max, words, labels, expand);

    private static PolicyEntry Agent(string name, PolicyKind kind, string group, string title, int min = 0, int max = 0,
        IReadOnlyList<string>? words = null, IReadOnlyDictionary<string, string>? labels = null) =>
        new(PolicyProduct.Agent, AgentKey, name, kind, group, title, min, max, words, labels);

    private static PolicyEntry Right(string name, string title) => new(PolicyProduct.Agent, RightsKey, name, PolicyKind.List, "Access rights", title);
}
