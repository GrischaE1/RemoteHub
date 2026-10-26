using System.Security.Principal;
using Microsoft.Win32;

namespace RemoteHub.Agent;

/// <summary>What a signed-in account may do through the agent. Each right is granted to Windows groups of the device.</summary>
public enum AgentRight
{
    /// <summary>PowerShell session (files, registry, processes, services, console).</summary>
    PowerShell,
    /// <summary>See and control the screen.</summary>
    Desktop,
    /// <summary>See the screen without controlling it.</summary>
    DesktopViewOnly,
    /// <summary>See and use the sign-in screen, the lock screen and UAC prompts.</summary>
    SecureDesktop,
    /// <summary>Connect to a signed-in user's screen without asking them.</summary>
    ConsentBypass,
    /// <summary>Black out the device's screen while working on it (policy <c>PrivacyScreen</c> must allow it too).</summary>
    PrivacyScreen,
    /// <summary>Use this device as a jump host: connect from it to other devices (policy <c>TunnelEnabled</c>, <c>TunnelTargets</c>, <c>TunnelPorts</c>).</summary>
    Tunnel,
    /// <summary>
    /// Sign in automatically on the console when nobody is signed in, with the account that signed in to the agent
    /// (policy <c>NoUserBehavior</c> = AutoSignIn must allow it too; also needs Desktop and SecureDesktop). Default: the
    /// allowed groups, like the other rights – unless the install chose its own (agent.json <c>autoSignInGroups</c>).
    /// </summary>
    AutoSignIn,
}

/// <summary>
/// Settings IT can enforce under HKLM\SOFTWARE\Policies\RemoteHub\Agent (ADMX-ready). A value there wins over agent.json.
/// Read again for every connection, so a changed policy applies without restarting the service.
/// <list type="bullet">
/// <item><c>DesktopEnabled</c> (DWORD) – 0 turns the remote desktop off.</item>
/// <item><c>AllowedConsentModes</c> (REG_MULTI_SZ) – any of Ask, Pin, None (default: Ask, Pin; None also needs the ConsentBypass right).</item>
/// <item><c>ConsentTimeoutSeconds</c> (DWORD) – how long the user is asked before it counts as "deny".</item>
/// <item><c>MaxFramesPerSecond</c>, <c>MaxQuality</c> (DWORD) – upper limits for what RemoteHub may choose.</item>
/// <item><c>DesktopClipboard</c> (DWORD) – 0 off, 1 text, 2 text and images, 3 text, images and files (default).</item>
/// <item><c>DesktopIdleTimeoutMinutes</c> (DWORD) – a remote desktop without mouse or keyboard input for this long ends (0 = never, default).</item>
/// <item><c>AuditToEventLog</c> (DWORD) – 0: audit entries only in the agent's log file, not in the Windows Application log.</item>
/// <item><c>PrivacyScreen</c> (DWORD) – 0: the admin can't black out the device's screen; 1: can, but the mouse and keyboard at the device stay usable; 2: can, and the mouse and keyboard at the device are blocked with it (default – the admin can still leave them usable).</item>
/// <item><c>HiddenApps</c> (REG_MULTI_SZ) – apps never shown to an admin, whatever the user chooses: full program paths, or <c>aumid:</c> + a Store app's AppUserModelID.</item>
/// <item><c>TunnelEnabled</c> (DWORD) – 0: this device is never a jump host.</item>
/// <item><c>TunnelTargets</c> (REG_MULTI_SZ) – where a jump connection may go: address ranges (<c>10.20.0.0/16</c>, <c>fd00::/8</c>, <c>10.1.2.3</c>) and names (<c>sql01.contoso.local</c>, <c>*.contoso.local</c>). Unset: anywhere – except this device's own loopback, link-local and multicast addresses, which only an address range naming them allows.</item>
/// <item><c>TunnelPorts</c> (REG_MULTI_SZ) – ports a jump connection may go to: <c>3389</c>, <c>5985-5986</c>, <c>*</c>. Unset: every port.</item>
/// <item><c>MaxTunnelStreams</c> (DWORD) – connections open at once through one jump connection (default 64).</item>
/// <item><c>NoUserBehavior</c> (REG_SZ) – <c>SignInScreen</c>: nobody on the console shows the sign-in screen; <c>AutoSignIn</c> (default, unless agent.json turns it off): the admin may sign in automatically there (right AutoSignIn).</item>
/// <item><c>AutoSignInRequirePrivacyScreen</c> (DWORD) – 1: no automatic sign-in where the privacy screen can't be shown.</item>
/// <item><c>AutoSignInEndAction</c> (REG_SZ) – <c>Lock</c> or <c>SignOut</c>: what happens to an automatically signed-in session when its connection ends; wins over RemoteHub's setting.</item>
/// <item>Subkey <c>Rights</c>: PowerShell, Desktop, DesktopViewOnly, SecureDesktop, ConsentBypass, PrivacyScreen, Tunnel, AutoSignIn (REG_MULTI_SZ) – group SIDs or names.</item>
/// </list>
/// </summary>
public sealed record AgentPolicy
{
    public const string RegistryPath = PolicyCatalog.Root + @"\" + PolicyCatalog.AgentKey;

    public bool DesktopEnabled { get; init; } = true;
    public IReadOnlyList<string> ConsentModes { get; init; } = [DesktopConsent.Ask, DesktopConsent.Pin, DesktopConsent.None];
    public int ConsentTimeoutSeconds { get; init; } = 30;
    public int MaxFramesPerSecond { get; init; } = 60;
    public int MaxQuality { get; init; } = 100;
    public int Clipboard { get; init; } = DesktopClipboardLevel.Files;
    /// <summary>DesktopIdleTimeoutMinutes; zero = never.</summary>
    public TimeSpan IdleTimeout { get; init; }
    public bool AuditToEventLog { get; init; } = true;
    /// <summary>PrivacyScreen: see <see cref="PrivacyScreenLevel"/>.</summary>
    public int PrivacyScreen { get; init; } = PrivacyScreenLevel.WithInputBlock;
    /// <summary>HiddenApps: programs (full path) or Store apps (<c>aumid:</c> + AppUserModelID) never shown to an admin.</summary>
    public IReadOnlyList<string> HiddenApps { get; init; } = [];
    /// <summary>TunnelEnabled: this device may be used as a jump host.</summary>
    public bool TunnelEnabled { get; init; } = true;
    /// <summary>TunnelTargets and TunnelPorts: where jump connections may go.</summary>
    public TunnelRules Tunnel { get; init; } = TunnelRules.Any;
    /// <summary>MaxTunnelStreams: connections open at once through one jump connection.</summary>
    public int MaxTunnelStreams { get; init; } = 64;
    /// <summary>TunnelOwnPorts: ports on this device itself the tunnel opens ("RDP" = its RDP port); empty = none.</summary>
    public IReadOnlyList<string> TunnelOwnPorts { get; init; } = ["RDP"];
    /// <summary>BrokerEnabled: the agent connects to the RemoteHub Broker in agent.json.</summary>
    public bool BrokerEnabled { get; init; } = true;
    /// <summary>BrokerUrl: another address for the broker (same organisation); null = agent.json's.</summary>
    public string? BrokerUrl { get; init; }
    /// <summary>BrokerConsoles: console ids allowed through the broker; empty = every registered console.</summary>
    public IReadOnlyList<string> BrokerConsoles { get; init; } = [];
    /// <summary>BrokerProxy: http://proxy:port for the broker's connections; null = the system's.</summary>
    public string? BrokerProxy { get; init; }
    /// <summary>BrokerKeepAliveSeconds: keep-alive on the broker connection (30–90).</summary>
    public int BrokerKeepAliveSeconds { get; init; } = BrokerProtocol.KeepAliveDefault;
    /// <summary>NoUserBehavior: what the agent may do when nobody is signed in on the console (<see cref="NoUserBehaviors"/>).</summary>
    public string NoUserBehavior { get; init; } = NoUserBehaviors.SignInScreen;
    /// <summary>AutoSignInRequirePrivacyScreen: automatic sign-in only where the privacy screen can be shown.</summary>
    public bool AutoSignInRequirePrivacyScreen { get; init; }
    /// <summary>AutoSignInEndAction: Lock or SignOut, or null when the device leaves it to RemoteHub's setting.</summary>
    public string? AutoSignInEndAction { get; init; }
    public IReadOnlyDictionary<AgentRight, IReadOnlyList<string>> Rights { get; init; } = new Dictionary<AgentRight, IReadOnlyList<string>>();

    /// <summary>PowerShellEnabled: PowerShell sessions – and so files, registry, services and processes – through the agent.</summary>
    public bool PowerShellEnabled { get; init; } = true;
    /// <summary>VirtualDisplay: auto, always or off (policy, else agent.json).</summary>
    public string VirtualDisplay { get; init; } = VirtualDisplayModes.Auto;
    /// <summary>PrivacyScreenMessage: the heading of the privacy screen; null = the built-in one.</summary>
    public string? PrivacyScreenMessage { get; init; }
    /// <summary>ListenPort (policy, else agent.json); read when the service starts.</summary>
    public int ListenPort { get; init; } = AgentProtocol.DefaultPort;
    /// <summary>LogRetentionDays (policy, else agent.json).</summary>
    public int LogRetentionDays { get; init; } = 30;
    /// <summary>MaxSessions (policy, else agent.json).</summary>
    public int MaxSessions { get; init; } = 20;

    /// <summary>Policy values that are set but invalid, as "name: problem" – they keep the default (<see cref="Load"/> fills it).</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>
    /// What IT decides on this device that RemoteHub's device actions would otherwise change (agent health,
    /// <see cref="AgentHealth.Managed"/>): only these names, never values or other policies.
    /// </summary>
    public IReadOnlyList<string> Managed { get; init; } = [];

    /// <summary>Set when the policy key couldn't be read: then nothing is allowed (see <see cref="Load"/>).</summary>
    public string? LoadError { get; init; }

    public IReadOnlyList<string> Groups(AgentRight right) => Rights.TryGetValue(right, out var g) ? g : [];

    /// <summary>
    /// Defaults: everything for the groups in agent.json (Administrators), nothing view-only – except connecting without
    /// asking: the signed-in user is asked (or shown a PIN) unless a policy allows the mode None and grants ConsentBypass.
    /// </summary>
    public static AgentPolicy Defaults(AgentConfig config) => Defaults(config, config.AllowedGroups);

    /// <summary><paramref name="allowed"/>: the groups for every right that isn't set on its own (policy AllowedGroups, else agent.json).</summary>
    private static AgentPolicy Defaults(AgentConfig config, IReadOnlyList<string> allowed) => new()
    {
        ConsentModes = [DesktopConsent.Ask, DesktopConsent.Pin],
        Rights = new Dictionary<AgentRight, IReadOnlyList<string>>
        {
            [AgentRight.PowerShell] = allowed,
            [AgentRight.Desktop] = allowed,
            [AgentRight.DesktopViewOnly] = [],
            [AgentRight.SecureDesktop] = allowed,
            [AgentRight.ConsentBypass] = [],
            [AgentRight.PrivacyScreen] = allowed,
            [AgentRight.Tunnel] = allowed,
            // On by default like every feature (admins turn it off by policy); the install may have chosen other groups.
            [AgentRight.AutoSignIn] = config.AutoSignInGroups ?? allowed,
        },
        // Not chosen by the install: allowed. Chosen but not one of the two words: the sign-in screen, the stricter one.
        NoUserBehavior = config.NoUserBehavior is null ? NoUserBehaviors.AutoSignIn : NoUserBehaviors.Valid(config.NoUserBehavior),
        VirtualDisplay = VirtualDisplayModes.IsValid(config.VirtualDisplay) ? config.VirtualDisplay : VirtualDisplayModes.Auto,
        ListenPort = config.ListenPort,
        LogRetentionDays = Math.Max(1, config.LogRetentionDays),
        MaxSessions = Math.Max(1, config.MaxSessions),
    };

    /// <summary>
    /// The defaults with every value from the policy key applied (missing or invalid values keep the default). If the key
    /// exists but can't be read, nothing is allowed – a policy that restricts access must not turn into the defaults.
    /// </summary>
    public static AgentPolicy Load(AgentConfig config)
    {
        config = config.WithCurrentAutoSignIn(); // changed by "auto-sign-in on|off" while the service runs
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
            if (key is null) return From(config, null);
            var configured = PolicyCatalog.Configured(PolicyCatalog.AgentEntries);
            return From(config, new RegistryValues(key)) with
            {
                Problems = [.. configured.Where(v => !v.Valid).Select(v => $"{v.Entry.Name}: {v.Problem}")],
                Managed = ManagedNames(configured),
            };
        }
        catch (Exception ex) { return Refused(ex.Message); }
    }

    /// <summary>The device actions RemoteHub offers that a valid policy value decides here (see <see cref="Managed"/>).</summary>
    internal static IReadOnlyList<string> ManagedNames(IEnumerable<PolicyValue> configured)
    {
        var names = configured.Where(v => v.Valid).Select(v => v.Entry).ToList();
        var managed = new List<string>();
        if (names.Contains(PolicyCatalog.NoUserBehavior) || names.Any(e => e.Key == PolicyCatalog.RightsKey && e.Name == nameof(AgentRight.AutoSignIn)))
            managed.Add(AgentManaged.AutoSignIn);
        if (names.Contains(PolicyCatalog.VirtualDisplay)) managed.Add(AgentManaged.VirtualDisplay);
        if (names.Contains(PolicyCatalog.ListenPort)) managed.Add(AgentManaged.Port);
        return managed;
    }

    /// <summary>No right for anyone, no remote desktop, no PowerShell.</summary>
    internal static AgentPolicy Refused(string reason) => new()
    {
        DesktopEnabled = false,
        PowerShellEnabled = false,
        ConsentModes = [],
        PrivacyScreen = PrivacyScreenLevel.Off,
        TunnelEnabled = false,
        TunnelOwnPorts = [],
        BrokerEnabled = false,
        LoadError = reason,
        Rights = Enum.GetValues<AgentRight>().ToDictionary(r => r, _ => (IReadOnlyList<string>)[]),
    };

    /// <summary>Applies policy values (tests pass their own).</summary>
    internal static AgentPolicy From(AgentConfig config, IPolicyValues? values)
    {
        if (values is null) return Defaults(config);
        var allowed = Groups(values.Strings(null, PolicyCatalog.AllowedGroups.Name));
        var d = Defaults(config, allowed is { Count: > 0 } ? allowed : config.AllowedGroups);
        var rights = new Dictionary<AgentRight, IReadOnlyList<string>>(d.Rights);
        foreach (var right in Enum.GetValues<AgentRight>())
            if (Groups(values.Strings("Rights", right.ToString())) is { } groups) rights[right] = groups;
        var modes = Strings(PolicyCatalog.AllowedConsentModes)?
            .Select(m => DesktopConsent.All.FirstOrDefault(c => c.Equals(m.Trim(), StringComparison.OrdinalIgnoreCase)))
            .OfType<string>().Distinct().ToList();
        return new AgentPolicy
        {
            DesktopEnabled = Raw(PolicyCatalog.DesktopEnabled) is not 0,
            ConsentModes = modes is { Count: > 0 } ? modes : d.ConsentModes,
            ConsentTimeoutSeconds = Number(PolicyCatalog.ConsentTimeoutSeconds) ?? d.ConsentTimeoutSeconds,
            MaxFramesPerSecond = Number(PolicyCatalog.MaxFramesPerSecond) ?? d.MaxFramesPerSecond,
            MaxQuality = Number(PolicyCatalog.MaxQuality) ?? d.MaxQuality,
            Clipboard = Number(PolicyCatalog.DesktopClipboard) ?? d.Clipboard,
            IdleTimeout = Number(PolicyCatalog.DesktopIdleTimeoutMinutes) is { } idle ? TimeSpan.FromMinutes(idle) : d.IdleTimeout,
            AuditToEventLog = Raw(PolicyCatalog.AuditToEventLog) is not 0,
            PrivacyScreen = Number(PolicyCatalog.PrivacyScreen) ?? d.PrivacyScreen,
            HiddenApps = Strings(PolicyCatalog.HiddenApps)?.Where(a => a.Trim().Length > 0).Select(AppIds.Normalize).Distinct().ToList() ?? d.HiddenApps,
            TunnelEnabled = Raw(PolicyCatalog.TunnelEnabled) is not 0,
            Tunnel = TunnelRules.Parse(Strings(PolicyCatalog.TunnelTargets), Strings(PolicyCatalog.TunnelPorts)),
            MaxTunnelStreams = Number(PolicyCatalog.MaxTunnelStreams) ?? d.MaxTunnelStreams,
            // Set but with nothing usable in it: no own ports – a value that restricts must not turn into the default.
            TunnelOwnPorts = Strings(PolicyCatalog.TunnelOwnPorts) is { } own ? [.. own.Select(p => p.Trim()).Where(p => p.Length > 0)] : d.TunnelOwnPorts,
            BrokerEnabled = Raw(PolicyCatalog.BrokerEnabled) is not 0,
            BrokerUrl = Checked(PolicyCatalog.BrokerUrl),
            BrokerConsoles = Strings(PolicyCatalog.BrokerConsoles)?.Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0).ToList() ?? d.BrokerConsoles,
            BrokerProxy = Checked(PolicyCatalog.BrokerProxy),
            BrokerKeepAliveSeconds = Number(PolicyCatalog.BrokerKeepAliveSeconds) ?? d.BrokerKeepAliveSeconds,
            // Set by the policy: that value; not set (or not one of the two words): what the install chose (agent.json).
            // (an invalid policy value still means the sign-in screen – a policy that restricts must not turn into a permission)
            NoUserBehavior = Strings(PolicyCatalog.NoUserBehavior)?.FirstOrDefault() is { } behavior ? NoUserBehaviors.Valid(behavior) : d.NoUserBehavior,
            AutoSignInRequirePrivacyScreen = Raw(PolicyCatalog.AutoSignInRequirePrivacyScreen) is 1,
            AutoSignInEndAction = AutoSignInEndActions.Valid(Strings(PolicyCatalog.AutoSignInEndAction)?.FirstOrDefault()),
            Rights = rights,
            PowerShellEnabled = Raw(PolicyCatalog.PowerShellEnabled) is not 0,
            VirtualDisplay = Word(PolicyCatalog.VirtualDisplay) ?? d.VirtualDisplay,
            PrivacyScreenMessage = values.Text(null, PolicyCatalog.PrivacyScreenMessage.Name) is { } heading
                && PolicyCatalog.PrivacyScreenMessage.Check(heading) is { Problem: null, Value: string checkedHeading } ? checkedHeading : null,
            ListenPort = Number(PolicyCatalog.ListenPort) ?? d.ListenPort,
            LogRetentionDays = Number(PolicyCatalog.LogRetentionDays) ?? d.LogRetentionDays,
            MaxSessions = Number(PolicyCatalog.MaxSessions) ?? d.MaxSessions,
        };

        // Names and ranges come from the catalogue the ADMX template is checked against; a number outside its range keeps the default.
        int? Raw(PolicyEntry e) => values.Number(null, e.Name);
        int? Number(PolicyEntry e) => Raw(e) is { } n && n >= e.Min && n <= e.Max ? n : null;
        IReadOnlyList<string>? Strings(PolicyEntry e) => values.Strings(null, e.Name);
        string? Word(PolicyEntry e) => Strings(e)?.FirstOrDefault() is { } w && e.Check(w) is { Problem: null, Value: string word } ? word : null;
        string? Checked(PolicyEntry e) => values.Text(null, e.Name) is { } t && e.Check(t) is { Problem: null, Value: string ok } ? ok.Trim() : null;
    }

    /// <summary>A list of groups as set: trimmed, empty entries dropped; null when not set.</summary>
    private static List<string>? Groups(IReadOnlyList<string>? groups) =>
        groups?.Where(g => g.Trim().Length > 0).Select(g => g.Trim()).ToList();

    internal interface IPolicyValues
    {
        int? Number(string? subKey, string name);
        IReadOnlyList<string>? Strings(string? subKey, string name);
        /// <summary>A REG_SZ as it is (not split into a list) – for free text such as the privacy screen heading.</summary>
        string? Text(string? subKey, string name) => Strings(subKey, name) is { Count: > 0 } s ? string.Join(", ", s) : null;
    }

    private sealed class RegistryValues(RegistryKey root) : IPolicyValues
    {
        public int? Number(string? subKey, string name) => Read(subKey, name) is int i ? i : null;

        public string? Text(string? subKey, string name) => Read(subKey, name) switch { string s => s, string[] { Length: > 0 } l => l[0], _ => null };

        public IReadOnlyList<string>? Strings(string? subKey, string name) => Read(subKey, name) switch
        {
            string[] list => list,
            string s => s.Split([';', ','], StringSplitOptions.RemoveEmptyEntries),
            _ => null,
        };

        private object? Read(string? subKey, string name)
        {
            if (subKey is null) return root.GetValue(name);
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(name);
        }
    }
}

/// <summary>Policy <c>NoUserBehavior</c>. Anything else, or nothing, is <see cref="SignInScreen"/>.</summary>
public static class NoUserBehaviors
{
    public const string SignInScreen = "SignInScreen";
    public const string AutoSignIn = "AutoSignIn";

    public static string Valid(string? value) => string.Equals(value?.Trim(), AutoSignIn, StringComparison.OrdinalIgnoreCase) ? AutoSignIn : SignInScreen;
}

/// <summary>Policy <c>PrivacyScreen</c>: what the admin's privacy screen may do.</summary>
public static class PrivacyScreenLevel
{
    public const int Off = 0, Allowed = 1, WithInputBlock = 2;
}

/// <summary>Group names or SIDs as configured → SIDs.</summary>
internal static class GroupNames
{
    public static SecurityIdentifier? Resolve(string group)
    {
        try
        {
            return group.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(group)
                : (SecurityIdentifier)new NTAccount(group).Translate(typeof(SecurityIdentifier));
        }
        catch { return null; }
    }
}
