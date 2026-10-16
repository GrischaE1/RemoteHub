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
    /// (policy <c>NoUserBehavior</c> = AutoSignIn must allow it too; also needs Desktop and SecureDesktop). Default: nobody.
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
/// <item><c>NoUserBehavior</c> (REG_SZ) – <c>SignInScreen</c> (default): nobody on the console shows the sign-in screen; <c>AutoSignIn</c>: the admin may sign in automatically there (right AutoSignIn).</item>
/// <item><c>AutoSignInRequirePrivacyScreen</c> (DWORD) – 1: no automatic sign-in where the privacy screen can't be shown.</item>
/// <item><c>AutoSignInEndAction</c> (REG_SZ) – <c>Lock</c> or <c>SignOut</c>: what happens to an automatically signed-in session when its connection ends; wins over RemoteHub's setting.</item>
/// <item>Subkey <c>Rights</c>: PowerShell, Desktop, DesktopViewOnly, SecureDesktop, ConsentBypass, PrivacyScreen, Tunnel, AutoSignIn (REG_MULTI_SZ) – group SIDs or names.</item>
/// </list>
/// </summary>
public sealed class AgentPolicy
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
    /// <summary>NoUserBehavior: what the agent may do when nobody is signed in on the console (<see cref="NoUserBehaviors"/>).</summary>
    public string NoUserBehavior { get; init; } = NoUserBehaviors.SignInScreen;
    /// <summary>AutoSignInRequirePrivacyScreen: automatic sign-in only where the privacy screen can be shown.</summary>
    public bool AutoSignInRequirePrivacyScreen { get; init; }
    /// <summary>AutoSignInEndAction: Lock or SignOut, or null when the device leaves it to RemoteHub's setting.</summary>
    public string? AutoSignInEndAction { get; init; }
    public IReadOnlyDictionary<AgentRight, IReadOnlyList<string>> Rights { get; init; } = new Dictionary<AgentRight, IReadOnlyList<string>>();

    /// <summary>Set when the policy key couldn't be read: then nothing is allowed (see <see cref="Load"/>).</summary>
    public string? LoadError { get; init; }

    public IReadOnlyList<string> Groups(AgentRight right) => Rights.TryGetValue(right, out var g) ? g : [];

    /// <summary>
    /// Defaults: everything for the groups in agent.json (Administrators), nothing view-only – except connecting without
    /// asking: the signed-in user is asked (or shown a PIN) unless a policy allows the mode None and grants ConsentBypass.
    /// </summary>
    public static AgentPolicy Defaults(AgentConfig config) => new()
    {
        ConsentModes = [DesktopConsent.Ask, DesktopConsent.Pin],
        Rights = new Dictionary<AgentRight, IReadOnlyList<string>>
        {
            [AgentRight.PowerShell] = config.AllowedGroups,
            [AgentRight.Desktop] = config.AllowedGroups,
            [AgentRight.DesktopViewOnly] = [],
            [AgentRight.SecureDesktop] = config.AllowedGroups,
            [AgentRight.ConsentBypass] = [],
            [AgentRight.PrivacyScreen] = config.AllowedGroups,
            [AgentRight.Tunnel] = config.AllowedGroups,
            [AgentRight.AutoSignIn] = [],
        },
    };

    /// <summary>
    /// The defaults with every value from the policy key applied (missing or invalid values keep the default). If the key
    /// exists but can't be read, nothing is allowed – a policy that restricts access must not turn into the defaults.
    /// </summary>
    public static AgentPolicy Load(AgentConfig config)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
            return From(config, key is null ? null : new RegistryValues(key));
        }
        catch (Exception ex) { return Refused(ex.Message); }
    }

    /// <summary>No right for anyone, no remote desktop.</summary>
    internal static AgentPolicy Refused(string reason) => new()
    {
        DesktopEnabled = false,
        ConsentModes = [],
        PrivacyScreen = PrivacyScreenLevel.Off,
        TunnelEnabled = false,
        LoadError = reason,
        Rights = Enum.GetValues<AgentRight>().ToDictionary(r => r, _ => (IReadOnlyList<string>)[]),
    };

    /// <summary>Applies policy values (tests pass their own).</summary>
    internal static AgentPolicy From(AgentConfig config, IPolicyValues? values)
    {
        var d = Defaults(config);
        if (values is null) return d;
        var rights = new Dictionary<AgentRight, IReadOnlyList<string>>(d.Rights);
        foreach (var right in Enum.GetValues<AgentRight>())
            if (values.Strings("Rights", right.ToString()) is { } groups) rights[right] = groups.Where(g => g.Trim().Length > 0).Select(g => g.Trim()).ToList();
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
            NoUserBehavior = NoUserBehaviors.Valid(Strings(PolicyCatalog.NoUserBehavior)?.FirstOrDefault()),
            AutoSignInRequirePrivacyScreen = Raw(PolicyCatalog.AutoSignInRequirePrivacyScreen) is 1,
            AutoSignInEndAction = AutoSignInEndActions.Valid(Strings(PolicyCatalog.AutoSignInEndAction)?.FirstOrDefault()),
            Rights = rights,
        };

        // Names and ranges come from the catalogue the ADMX template is checked against; a number outside its range keeps the default.
        int? Raw(PolicyEntry e) => values.Number(null, e.Name);
        int? Number(PolicyEntry e) => Raw(e) is { } n && n >= e.Min && n <= e.Max ? n : null;
        IReadOnlyList<string>? Strings(PolicyEntry e) => values.Strings(null, e.Name);
    }

    internal interface IPolicyValues
    {
        int? Number(string? subKey, string name);
        IReadOnlyList<string>? Strings(string? subKey, string name);
    }

    private sealed class RegistryValues(RegistryKey root) : IPolicyValues
    {
        public int? Number(string? subKey, string name) => Read(subKey, name) is int i ? i : null;

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
