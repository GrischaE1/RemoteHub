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
    /// <summary>REG_DWORD within <see cref="PolicyEntry.Min"/>–<see cref="PolicyEntry.Max"/>; with <see cref="PolicyEntry.Words"/> only those numbers.</summary>
    Number,
    /// <summary>REG_SZ (or REG_EXPAND_SZ); with <see cref="PolicyEntry.Words"/> only one of them (case-insensitive); <see cref="PolicyEntry.Max"/> &gt; 0 limits the length.</summary>
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
    string Since = "0.18.11",
    Func<string, string?>? Rule = null)
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
                if (n < Min || n > Max) return (null, $"{n} is outside {Min}–{Max}");
                // A fixed set within the range (colour depth, frame rates): only those.
                return Words is null || Words.Contains(n.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    ? (n, null) : (null, $"{n} isn't one of {string.Join(", ", Words)}");
            case PolicyKind.Text:
                var s = raw switch { string t => t, string[] { Length: > 0 } l => l[0], _ => null };
                if (s is null) return (null, $"must be a string, not {TypeName(raw)}");
                s = s.Trim();
                if (s.Length == 0) return (null, "is empty");
                if (Max > 0 && s.Length > Max) return (null, $"is longer than {Max} characters");
                // A free text with a form of its own (a resolution, an address list): Rule says what is wrong with it.
                if (Words is null && Rule?.Invoke(s) is { } wrong) return (null, wrong);
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

/// <summary>Every value IT can set for RemoteHub and the RemoteHub Agent (docs/admx-design.md; phases 1–3 are built).</summary>
public static class PolicyCatalog
{
    public const string Root = @"SOFTWARE\Policies\RemoteHub";
    public const string AgentKey = "Agent";
    public const string RightsKey = @"Agent\Rights";
    /// <summary>The console's key from before the rename; only <see cref="SettingsFolder"/> is still read there.</summary>
    public const string LegacyRoot = @"SOFTWARE\Policies\RdpManager";

    private static readonly Dictionary<string, string> OnOff = new() { ["0"] = "Off", ["1"] = "On" };
    private static readonly Dictionary<string, string> TurnedOff = new() { ["0"] = "Not turned off (the user decides)", ["1"] = "Turned off" };
    private static readonly Dictionary<string, string> Allowed = new() { ["0"] = "Blocked", ["1"] = "Allowed" };
    private static readonly Dictionary<string, string> YesNo = new() { ["0"] = "No", ["1"] = "Yes" };
    // DisableTelemetry and DisableUpdateCheck: 0 locks the feature on since 0.19.0 (§12.3, Q3).
    private static readonly Dictionary<string, string> OffOrLockedOn = new() { ["0"] = "On (users can't turn it off)", ["1"] = "Turned off" };

    // ------------------------------------------------------------------ RemoteHub (console)

    public static readonly PolicyEntry SettingsFolder = Console("SettingsFolder", PolicyKind.Text, "Settings storage", "Shared settings folder", expand: true);
    public static readonly PolicyEntry DisableUpdateCheck = Console("DisableUpdateCheck", PolicyKind.Number, "Updates and usage data", "Turn off the update check", 0, 1, labels: OffOrLockedOn);
    public static readonly PolicyEntry UpdateChannel = Console("UpdateChannel", PolicyKind.Text, "Updates and usage data", "Update channel",
        words: ["stable", "beta", "ga", "release", "prerelease", "pre-release"],
        labels: new Dictionary<string, string> { ["stable"] = "GA – stable releases", ["ga"] = "GA – stable releases", ["release"] = "GA – stable releases", ["beta"] = "Beta", ["prerelease"] = "Beta", ["pre-release"] = "Beta" });
    public static readonly PolicyEntry DisableTelemetry = Console("DisableTelemetry", PolicyKind.Number, "Updates and usage data", "Turn off usage data and crash reports", 0, 1, labels: OffOrLockedOn);
    public static readonly PolicyEntry DisableRecording = Console("DisableRecording", PolicyKind.Number, "Recordings", "Turn off recording", 0, 1, labels: TurnedOff);
    public static readonly PolicyEntry RecordingFolder = Console("RecordingFolder", PolicyKind.Text, "Recordings", "Recording folder", expand: true);
    public static readonly PolicyEntry AuditRetentionDays = Console("AuditRetentionDays", PolicyKind.Number, "Logs", "Keep audit log entries (days)", 1, 36_500);
    public static readonly PolicyEntry DisableAgentAutoSignIn = Console("DisableAgentAutoSignIn", PolicyKind.Number, "RemoteHub Agent", "Turn off automatic sign-in through the agent", 0, 1, labels: TurnedOff);

    // Phase 4 (docs/admx-design.md §6): the console's settings. Each one set here is greyed out where it is changed.
    public static readonly PolicyEntry Theme = Setting("Theme", PolicyKind.Text, "General", "Theme", words: ["Dark", "Light", "System"]);
    public static readonly PolicyEntry SwitchKeys = Setting("SwitchKeys", PolicyKind.Text, "General", "Keys to switch between sessions",
        words: ["Less", "Caret", "F1F2", "PgUpPgDn", "LeftRight"],
        labels: new Dictionary<string, string> { ["Less"] = "Ctrl+Alt+<", ["Caret"] = "Ctrl+Alt+^", ["F1F2"] = "Ctrl+Alt+F1 / F2", ["PgUpPgDn"] = "Ctrl+Alt+PageUp / PageDown", ["LeftRight"] = "Ctrl+Alt+Left / Right" });
    public static readonly PolicyEntry RecordingFps = Setting("RecordingFps", PolicyKind.Number, "Recordings", "Recording frame rate (fps)", 5, 60, words: ["5", "10", "15", "30", "60"]);
    public static readonly PolicyEntry RecordingResolution = Setting("RecordingResolution", PolicyKind.Text, "Recordings", "Recording size", words: ["Tab", "FullHd", "Uhd4K"],
        labels: new Dictionary<string, string> { ["Tab"] = "The tab's size", ["FullHd"] = "1920 × 1080", ["Uhd4K"] = "3840 × 2160" });
    public static readonly PolicyEntry RecordingUseGpu = Setting("RecordingUseGpu", PolicyKind.Number, "Recordings", "Encode recordings on the graphics card", 0, 1, labels: OnOff);
    public static readonly PolicyEntry RecordingIncludeDetails = Setting("RecordingIncludeDetails", PolicyKind.Number, "Recordings", "Show the connection's details in recordings", 0, 1, labels: OnOff);
    public static readonly PolicyEntry ProcessRefreshSeconds = Setting("ProcessRefreshSeconds", PolicyKind.Number, "Remote tools", "Refresh the processes tab every (seconds, 0 = by hand)", 0, 3600);
    public static readonly PolicyEntry ServiceRefreshSeconds = Setting("ServiceRefreshSeconds", PolicyKind.Number, "Remote tools", "Refresh the services tab every (seconds, 0 = by hand)", 0, 3600);
    public static readonly PolicyEntry AgentDesktopQuality = Setting("AgentDesktopQuality", PolicyKind.Text, "Agent remote desktop", "Picture", words: ["Low", "Admin", "High", "Smooth"],
        labels: new Dictionary<string, string> { ["Low"] = "Slow connection – 15 fps", ["Admin"] = "Admin work – 30 fps", ["High"] = "Sharp – 30 fps, best picture", ["Smooth"] = "Smooth – 60 fps, best picture" });
    public static readonly PolicyEntry AgentDesktopConsent = Setting("AgentDesktopConsent", PolicyKind.Text, "Agent remote desktop", "How the signed-in user is asked", words: ["Ask", "Pin", "None"],
        labels: new Dictionary<string, string> { ["Ask"] = "Ask", ["Pin"] = "With a code", ["None"] = "Don't ask" });
    public static readonly PolicyEntry AgentDesktopViewOnly = Setting("AgentDesktopViewOnly", PolicyKind.Number, "Agent remote desktop", "Always view only", 0, 1, labels: YesNo);
    public static readonly PolicyEntry AgentDesktopFitToWindow = Setting("AgentDesktopFitToWindow", PolicyKind.Number, "Agent remote desktop", "Fit the device's resolution to the window", 0, 1, labels: YesNo);
    public static readonly PolicyEntry AgentDesktopShowInfo = Setting("AgentDesktopShowInfo", PolicyKind.Number, "Agent remote desktop", "Show connection details", 0, 1, labels: OnOff);
    public static readonly PolicyEntry AgentDesktopKeyCombinations = Setting("AgentDesktopKeyCombinations", PolicyKind.Number, "Agent remote desktop", "Send Windows key combinations", 0, 1, labels: OnOff);
    public static readonly PolicyEntry AgentDesktopClipboard = Setting("AgentDesktopClipboard", PolicyKind.Number, "Agent remote desktop", "Clipboard", 0, 3,
        labels: new Dictionary<string, string> { ["0"] = "Off", ["1"] = "Text", ["2"] = "Text and images", ["3"] = "Text, images and files" });

    public const string AgentInstallKey = "AgentInstall";
    public static readonly IReadOnlyList<PolicyEntry> AgentInstallEntries =
    [
        new(PolicyProduct.Console, AgentInstallKey, "Methods", PolicyKind.List, "Agent installation", "Install methods offered", Words: ["Remoting", "ThroughAgent", "Clipboard", "SaveFiles"], Since: Phase4),
        new(PolicyProduct.Console, AgentInstallKey, "Port", PolicyKind.Number, "Agent installation", "Port", 1, 65535, Since: Phase4),
        new(PolicyProduct.Console, AgentInstallKey, "FirewallScope", PolicyKind.Text, "Agent installation", "Allow connections from", Max: 1000, Since: Phase4,
            Rule: s => AgentEnrollment.IsValidAddressList(s.Replace(';', ',')) ? null : "isn't a list of addresses or ranges (10.1.0.0/16, 192.168.1.20, …)"),
        new(PolicyProduct.Console, AgentInstallKey, "AllowedGroups", PolicyKind.List, "Agent installation", "Allowed accounts", Since: Phase4),
        new(PolicyProduct.Console, AgentInstallKey, "VirtualDisplay", PolicyKind.Number, "Agent installation", "Virtual display for servers without a monitor", 0, 1, Labels: YesNo, Since: Phase4),
        new(PolicyProduct.Console, AgentInstallKey, "AutoSignIn", PolicyKind.Number, "Agent installation", "Allow automatic sign-in", 0, 1, Labels: YesNo, Since: Phase4),
        new(PolicyProduct.Console, AgentInstallKey, "UseAgentAfterwards", PolicyKind.Number, "Agent installation", "Use the agent for the connection afterwards", 0, 1, Labels: YesNo, Since: Phase4),
    ];

    // Phase 3 (docs/admx-design.md §5): what RemoteHub may do at all. 0 blocks a feature, 1 or not set allows it; a main
    // switch (AllowAgent, AllowPowerShellRemoting, AllowNetworkShares) at 0 blocks everything below it.
    public static readonly PolicyEntry AllowRdp = Feature("AllowRdp", "RDP", "Allow remote desktop over RDP");
    public static readonly PolicyEntry AllowAgent = Feature("AllowAgent", "RemoteHub Agent", "Allow the RemoteHub Agent");
    public static readonly PolicyEntry AllowAgentDesktop = Feature("AllowAgentDesktop", "RemoteHub Agent", "Allow remote desktop through the agent");
    public static readonly PolicyEntry AllowAgentPowerShell = Feature("AllowAgentPowerShell", "RemoteHub Agent", "Allow PowerShell through the agent");
    public static readonly PolicyEntry AllowAgentFileTransfer = Feature("AllowAgentFileTransfer", "RemoteHub Agent", "Allow file transfer through the agent");
    public static readonly PolicyEntry AllowAgentRegistry = Feature("AllowAgentRegistry", "RemoteHub Agent", "Allow the registry through the agent");
    public static readonly PolicyEntry AllowAgentServices = Feature("AllowAgentServices", "RemoteHub Agent", "Allow services through the agent");
    public static readonly PolicyEntry AllowAgentProcesses = Feature("AllowAgentProcesses", "RemoteHub Agent", "Allow processes through the agent");
    public static readonly PolicyEntry AllowAgentDeviceActions = Feature("AllowAgentDeviceActions", "RemoteHub Agent", "Allow device actions through the agent", DeviceActionsSince);
    public static readonly PolicyEntry AllowAgentInstall = Feature("AllowAgentInstall", "RemoteHub Agent", "Allow installing and updating the agent");
    public static readonly PolicyEntry AllowCredentialProviderInstall = Feature("AllowCredentialProviderInstall", "RemoteHub Agent", "Allow installing and removing the credential provider");
    public static readonly PolicyEntry AllowPowerShellRemoting = Feature("AllowPowerShellRemoting", "PowerShell remoting", "Allow PowerShell remoting (WinRM)");
    public static readonly PolicyEntry AllowRemotingPowerShell = Feature("AllowRemotingPowerShell", "PowerShell remoting", "Allow PowerShell over WinRM");
    public static readonly PolicyEntry AllowRemotingFileTransfer = Feature("AllowRemotingFileTransfer", "PowerShell remoting", "Allow file transfer over WinRM");
    public static readonly PolicyEntry AllowRemotingRegistry = Feature("AllowRemotingRegistry", "PowerShell remoting", "Allow the registry over WinRM");
    public static readonly PolicyEntry AllowRemotingServices = Feature("AllowRemotingServices", "PowerShell remoting", "Allow services over WinRM");
    public static readonly PolicyEntry AllowRemotingProcesses = Feature("AllowRemotingProcesses", "PowerShell remoting", "Allow processes over WinRM");
    public static readonly PolicyEntry AllowRemotingDeviceActions = Feature("AllowRemotingDeviceActions", "PowerShell remoting", "Allow device actions over WinRM", DeviceActionsSince);
    public static readonly PolicyEntry AllowRemotingSetup = Feature("AllowRemotingSetup", "PowerShell remoting", "Allow setting up PowerShell remoting on a target");
    public static readonly PolicyEntry AllowQuickConnect = Feature("AllowQuickConnect", "Connections", "Allow quick connect to a typed address");
    public static readonly PolicyEntry AllowJumpHosts = Feature("AllowJumpHosts", "Jump hosts and network shares", "Allow jump hosts");
    /// <summary>Reaching devices through a RemoteHub Broker and its synced folders (broker-design.md §13.2).</summary>
    public static readonly PolicyEntry AllowBroker = Feature("AllowBroker", "RemoteHub Agent", "Allow the RemoteHub Broker", "0.22.0");
    public static readonly PolicyEntry AllowNetworkShares = Feature("AllowNetworkShares", "Jump hosts and network shares", "Allow network shares");
    public static readonly PolicyEntry AllowSmb = Feature("AllowSmb", "Jump hosts and network shares", "Allow SMB shares");
    public static readonly PolicyEntry AllowSftp = Feature("AllowSftp", "Jump hosts and network shares", "Allow SFTP shares");
    public static readonly PolicyEntry AllowFtp = Feature("AllowFtp", "Jump hosts and network shares", "Allow FTP shares");
    public static readonly PolicyEntry AllowWebDav = Feature("AllowWebDav", "Jump hosts and network shares", "Allow WebDAV shares");
    public static readonly PolicyEntry AllowRdpFileImport = Feature("AllowRdpFileImport", "Passwords, import and export", "Allow importing .rdp files");
    public static readonly PolicyEntry AllowRdpFileExport = Feature("AllowRdpFileExport", "Passwords, import and export", "Allow saving .rdp files");
    public static readonly PolicyEntry AllowBackupExport = Feature("AllowBackupExport", "Passwords, import and export", "Allow exporting a backup");
    public static readonly PolicyEntry AllowBackupImport = Feature("AllowBackupImport", "Passwords, import and export", "Allow importing a backup");
    public static readonly PolicyEntry AllowSharedSettings = Feature("AllowSharedSettings", "Settings storage", "Allow a shared settings folder");
    // notes-view-design.md §8: hides the view only; stored notes are kept and still go into backups.
    public static readonly PolicyEntry AllowNotes = Feature("AllowNotes", "Notes", "Allow Notes");
    // scripts-view-design.md §9: the view, running (and each context), drafts under test, the library, import and export.
    // Blocking the view keeps the stored scripts; a run is the PowerShell tool used another way, so it needs that too.
    public static readonly PolicyEntry AllowScripts = ScriptsFeature("AllowScripts", "Allow Scripts");
    public static readonly PolicyEntry AllowScriptRun = ScriptsFeature("AllowScriptRun", "Allow running scripts on devices");
    public static readonly PolicyEntry AllowScriptRunSystem = ScriptsFeature("AllowScriptRunSystem", "Allow running scripts as System");
    public static readonly PolicyEntry AllowScriptRunUser = ScriptsFeature("AllowScriptRunUser", "Allow running scripts as the signed-in user");
    public static readonly PolicyEntry AllowScriptRunAsMe = ScriptsFeature("AllowScriptRunAsMe", "Allow running scripts as the connection's account");
    public static readonly PolicyEntry AllowScriptDraftRun = ScriptsFeature("AllowScriptDraftRun", "Allow testing a draft on a device");
    public static readonly PolicyEntry AllowScriptLibrary = ScriptsFeature("AllowScriptLibrary", "Allow the built-in script library");
    public static readonly PolicyEntry AllowScriptImport = ScriptsFeature("AllowScriptImport", "Allow importing scripts");
    public static readonly PolicyEntry AllowScriptExport = ScriptsFeature("AllowScriptExport", "Allow exporting scripts");

    private const string ScriptsSince = "0.21.0";

    private static PolicyEntry ScriptsFeature(string name, string title) =>
        Console(name, PolicyKind.Number, "Scripts", title, 0, 1, labels: Allowed, since: ScriptsSince);

    // Phase 5 (docs/admx-design.md §5.4, §5.6, §6.3–§6.5): values behind new behaviour of the console.
    public static readonly PolicyEntry AllowConnectionEditing = Console("AllowConnectionEditing", PolicyKind.Number, "Connections", "Allow changing folders, connections and shares", 0, 1, labels: Allowed, since: Phase5);
    public static readonly PolicyEntry AllowSavedPasswords = Console("AllowSavedPasswords", PolicyKind.Number, "Passwords, import and export", "Allow saving passwords", 0, 1, labels: Allowed, since: Phase5);
    public static readonly PolicyEntry AllowPasswordCopy = Console("AllowPasswordCopy", PolicyKind.Number, "Passwords, import and export", "Allow copying a stored password to the clipboard", 0, 1, labels: Allowed, since: Phase5);
    public static readonly PolicyEntry StartupProtection = Console("StartupProtection", PolicyKind.Text, "Startup protection", "Startup protection", words: ["Required", "Off"],
        labels: new Dictionary<string, string> { ["Required"] = "Required – RemoteHub opens nothing until it is set up", ["Off"] = "Off – it can't be turned on" }, since: Phase5);
    public static readonly PolicyEntry AllowWindowsHello = Console("AllowWindowsHello", PolicyKind.Number, "Startup protection", "Allow Windows Hello for startup protection", 0, 1, labels: Allowed, since: Phase5);
    public static readonly PolicyEntry AutoLockMinutes = Console("AutoLockMinutes", PolicyKind.Number, "Startup protection", "Lock RemoteHub after (minutes without use)", 1, 1440, since: Phase5);
    public static readonly PolicyEntry SettingsLocation = Console("SettingsLocation", PolicyKind.Text, "Settings storage", "Where settings are stored", words: ["Local", "Shared"],
        labels: new Dictionary<string, string> { ["Local"] = "This PC only", ["Shared"] = "The shared settings folder" }, since: Phase5);
    public static readonly PolicyEntry DiagnosticLog = Console("DiagnosticLog", PolicyKind.Number, "Logs", "Write the diagnostic log", 0, 1, labels: OnOff, since: Phase5);
    public static readonly PolicyEntry DiagnosticLogRetentionDays = Console("DiagnosticLogRetentionDays", PolicyKind.Number, "Logs", "Keep the diagnostic log (days)", 1, 3650, since: Phase5);

    private const string Phase5 = "0.20.2"; // the first release with these

    private const string Phase3 = "0.19.0";

    // Phase 4 (docs/admx-design.md §7): one property of every connection's settings each, in the subkey Connections. The
    // value name is the RdpOptions property name; words are the enum members' names, so a policy doesn't depend on their order.
    public static readonly IReadOnlyList<PolicyEntry> ConnectionEntries =
    [
        ConnWords("Resize", "Display", "Resize behaviour", "DynamicResolution", "SmartSizing", "Fixed"),
        new(PolicyProduct.Console, ConnectionsKey, "FixedResolution", PolicyKind.Text, "Connection settings – Display", "Fixed resolution", Max: 11, Since: Phase4,
            Rule: s => s.Split('x') is [var w, var h] && int.TryParse(w, out var width) && int.TryParse(h, out var height) && width is >= 200 and <= 8192 && height is >= 200 and <= 8192
                ? null : "isn't <width>x<height> between 200 and 8192, for example 1920x1080"),
        new(PolicyProduct.Console, ConnectionsKey, "ColorDepth", PolicyKind.Number, "Connection settings – Display", "Colour depth", 15, 32, ["15", "16", "24", "32"], Since: Phase4),
        ConnFlag("UseMultimon", "Display", "Use all monitors in full screen"),
        ConnWords("Quality", "Display", "Visual quality", "Best", "Balanced", "Fast"),
        ConnFlag("RedirectClipboard", "Devices and resources", "Clipboard (copy and paste)"),
        ConnFlag("RedirectDrives", "Devices and resources", "Local drives"),
        ConnFlag("RedirectPrinters", "Devices and resources", "Printers"),
        ConnFlag("RedirectSmartCards", "Devices and resources", "Smart cards"),
        ConnWords("Audio", "Devices and resources", "Audio", "PlayLocal", "PlayRemote", "None"),
        ConnFlag("ForwardWindowsKeys", "Devices and resources", "Send Windows key combinations"),
        ConnFlag("EnableNla", "Security", "Network Level Authentication"),
        ConnWords("ServerAuthentication", "Security", "Server authentication", "ConnectWithoutWarning", "DoNotConnect", "Warn"),
        ConnFlag("AdminSession", "Security", "Administrative (console) session"),
        ConnFlag("AutoReconnect", "Security", "Reconnect automatically"),
        ConnWords("PasswordPrompt", "Security", "When Windows asks for the password", "Clipboard", "Off"),
        ConnFlag("PsRemoting", "Remoting and agent", "Check PowerShell remoting after the RDP sign-in"),
        ConnFlag("PsUseSsl", "Remoting and agent", "PowerShell remoting over HTTPS"),
        ConnFlag("UseAgent", "Remoting and agent", "Tools through the RemoteHub Agent"),
        ConnWords("RemoteDesktopVia", "Remoting and agent", "Remote desktop through", "Automatic", "Rdp", "Agent"),
        ConnFlag("AgentAutoSignIn", "Remoting and agent", "Sign in automatically when nobody is signed in"),
        ConnWords("AgentAutoSignInEndAction", "Remoting and agent", "When an automatic sign-in's connection ends", "Lock", "SignOut"),
    ];

    public const string ConnectionsKey = "Connections";
    private const string Phase4 = "0.19.0";

    private static PolicyEntry ConnFlag(string name, string group, string title) =>
        new(PolicyProduct.Console, ConnectionsKey, name, PolicyKind.Number, "Connection settings – " + group, title, 0, 1, Labels: YesNo, Since: Phase4);

    private static PolicyEntry ConnWords(string name, string group, string title, params string[] words) =>
        new(PolicyProduct.Console, ConnectionsKey, name, PolicyKind.Text, "Connection settings – " + group, title, Words: words, Since: Phase4);

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

    // Phase 2 (docs/admx-design.md §9): the device-side switch for PowerShell, and what agent.json had alone so far.
    public static readonly PolicyEntry PowerShellEnabled = Agent("PowerShellEnabled", PolicyKind.Number, "Features", "Allow PowerShell, files, registry, services and processes", 0, 1, labels: OnOff, since: Phase2);
    public static readonly PolicyEntry AllowedGroups = Agent("AllowedGroups", PolicyKind.List, "Access rights", "Default groups for every right", since: Phase2);
    public static readonly PolicyEntry VirtualDisplay = Agent("VirtualDisplay", PolicyKind.Text, "Remote desktop", "Virtual display for devices without a monitor", words: ["auto", "always", "off"],
        labels: new Dictionary<string, string> { ["auto"] = "Only when the device has no usable resolution", ["always"] = "Always", ["off"] = "Never" }, since: Phase2);
    public static readonly PolicyEntry PrivacyScreenMessage = Agent("PrivacyScreenMessage", PolicyKind.Text, "Privacy", "Privacy screen heading", max: 120, since: Phase2);
    public static readonly PolicyEntry ListenPort = Agent("ListenPort", PolicyKind.Number, "Service", "Port", 1, 65535, since: Phase2);
    public static readonly PolicyEntry LogRetentionDays = Agent("LogRetentionDays", PolicyKind.Number, "Service", "Keep log files (days)", 1, 3650, since: Phase2);
    public static readonly PolicyEntry MaxSessions = Agent("MaxSessions", PolicyKind.Number, "Service", "Sessions at once", 1, 200, since: Phase2);

    private const string Phase2 = "0.19.0";

    // The RemoteHub Broker on the device (broker-design.md §13.1).
    public static readonly PolicyEntry BrokerEnabled = Agent("BrokerEnabled", PolicyKind.Number, "RemoteHub Broker", "Allow the RemoteHub Broker", 0, 1, labels: OnOff, since: BrokerSince);
    public static readonly PolicyEntry BrokerUrl = Agent("BrokerUrl", PolicyKind.Text, "RemoteHub Broker", "Broker address", max: 200, since: BrokerSince,
        rule: s => BrokerProtocol.ParseBrokerUrl(s) is null ? "isn't a broker address (https://name)" : null);
    public static readonly PolicyEntry BrokerConsoles = Agent("BrokerConsoles", PolicyKind.List, "RemoteHub Broker", "Consoles allowed through the broker", since: BrokerSince);
    public static readonly PolicyEntry BrokerProxy = Agent("BrokerProxy", PolicyKind.Text, "RemoteHub Broker", "Proxy for the broker", max: 200, since: BrokerSince,
        rule: s => Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? null : "isn't a proxy address (http://proxy:8080)");
    public static readonly PolicyEntry BrokerKeepAliveSeconds = Agent("BrokerKeepAliveSeconds", PolicyKind.Number, "RemoteHub Broker", "Keep-alive interval (seconds)", 30, 90, since: BrokerSince);
    /// <summary>Ports on the device itself that its tunnel opens (RDP through the agent, port forwarding) – "RDP" = its RDP port.</summary>
    public static readonly PolicyEntry TunnelOwnPorts = Agent("TunnelOwnPorts", PolicyKind.List, "Jump host", "Ports on this device itself", since: BrokerSince);

    private const string BrokerSince = "0.22.0";

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

    /// <summary>The console's features (§5), in the template's order.</summary>
    public static readonly IReadOnlyList<PolicyEntry> FeatureEntries =
    [
        AllowRdp,
        AllowAgent, AllowAgentDesktop, AllowAgentPowerShell, AllowAgentFileTransfer, AllowAgentRegistry, AllowAgentServices, AllowAgentProcesses,
        AllowAgentDeviceActions, AllowAgentInstall, AllowCredentialProviderInstall, DisableAgentAutoSignIn,
        AllowPowerShellRemoting, AllowRemotingPowerShell, AllowRemotingFileTransfer, AllowRemotingRegistry, AllowRemotingServices, AllowRemotingProcesses,
        AllowRemotingDeviceActions, AllowRemotingSetup,
        AllowQuickConnect, AllowConnectionEditing,
        AllowJumpHosts, AllowNetworkShares, AllowSmb, AllowSftp, AllowFtp, AllowWebDav, AllowBroker,
        AllowSavedPasswords, AllowPasswordCopy,
        AllowRdpFileImport, AllowRdpFileExport, AllowBackupExport, AllowBackupImport,
        AllowNotes,
        AllowScripts, AllowScriptRun, AllowScriptRunSystem, AllowScriptRunUser, AllowScriptRunAsMe, AllowScriptDraftRun,
        AllowScriptLibrary, AllowScriptImport, AllowScriptExport,
    ];

    public static readonly IReadOnlyList<PolicyEntry> ConsoleEntries =
    [
        .. FeatureEntries,
        Theme, SwitchKeys,
        DisableUpdateCheck, UpdateChannel, DisableTelemetry,
        StartupProtection, AllowWindowsHello, AutoLockMinutes,
        SettingsLocation, SettingsFolder, AllowSharedSettings,
        AuditRetentionDays, DiagnosticLog, DiagnosticLogRetentionDays,
        DisableRecording, RecordingFolder, RecordingFps, RecordingResolution, RecordingUseGpu, RecordingIncludeDetails,
        ProcessRefreshSeconds, ServiceRefreshSeconds,
        AgentDesktopQuality, AgentDesktopConsent, AgentDesktopViewOnly, AgentDesktopFitToWindow, AgentDesktopShowInfo, AgentDesktopKeyCombinations,
        AgentDesktopClipboard,
        .. AgentInstallEntries,
        .. ConnectionEntries,
    ];

    public static readonly IReadOnlyList<PolicyEntry> AgentEntries =
    [
        PowerShellEnabled,
        AllowedGroups, .. Rights,
        DesktopEnabled, MaxFramesPerSecond, MaxQuality, DesktopClipboard, DesktopIdleTimeoutMinutes, VirtualDisplay,
        AllowedConsentModes, ConsentTimeoutSeconds,
        PrivacyScreen, PrivacyScreenMessage, HiddenApps,
        NoUserBehavior, AutoSignInRequirePrivacyScreen, AutoSignInEndAction,
        TunnelEnabled, TunnelTargets, TunnelPorts, MaxTunnelStreams, TunnelOwnPorts,
        BrokerEnabled, BrokerUrl, BrokerConsoles, BrokerProxy, BrokerKeepAliveSeconds,
        AuditToEventLog, ListenPort, LogRetentionDays, MaxSessions,
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
        IReadOnlyList<string>? words = null, IReadOnlyDictionary<string, string>? labels = null, bool expand = false, string since = "0.18.11") =>
        new(PolicyProduct.Console, "", name, kind, group, title, min, max, words, labels, expand, since);

    /// <summary>A console setting of §6, in the root key.</summary>
    private static PolicyEntry Setting(string name, PolicyKind kind, string group, string title, int min = 0, int max = 0,
        IReadOnlyList<string>? words = null, IReadOnlyDictionary<string, string>? labels = null) =>
        Console(name, kind, group, title, min, max, words, labels, since: Phase4);

    /// <summary>A console feature: DWORD 0 = blocked, 1 = allowed.</summary>
    private static PolicyEntry Feature(string name, string group, string title, string since = Phase3) =>
        Console(name, PolicyKind.Number, group, title, 0, 1, labels: Allowed, since: since);

    /// <summary>Lock, restart, shut down and the other device actions in the connection's menu.</summary>
    private const string DeviceActionsSince = "0.21.0";

    private static PolicyEntry Agent(string name, PolicyKind kind, string group, string title, int min = 0, int max = 0,
        IReadOnlyList<string>? words = null, IReadOnlyDictionary<string, string>? labels = null, string since = "0.18.11",
        Func<string, string?>? rule = null) =>
        new(PolicyProduct.Agent, AgentKey, name, kind, group, title, min, max, words, labels, Since: since, Rule: rule);

    private static PolicyEntry Right(string name, string title) => new(PolicyProduct.Agent, RightsKey, name, PolicyKind.List, "Access rights", title);
}
