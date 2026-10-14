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
/// <item>Subkey <c>Rights</c>: PowerShell, Desktop, DesktopViewOnly, SecureDesktop, ConsentBypass, PrivacyScreen, Tunnel (REG_MULTI_SZ) – group SIDs or names.</item>
/// </list>
/// </summary>
public sealed class AgentPolicy
{
    public const string RegistryPath = @"SOFTWARE\Policies\RemoteHub\Agent";

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
        var modes = values.Strings(null, "AllowedConsentModes")?
            .Select(m => DesktopConsent.All.FirstOrDefault(c => c.Equals(m.Trim(), StringComparison.OrdinalIgnoreCase)))
            .OfType<string>().Distinct().ToList();
        return new AgentPolicy
        {
            DesktopEnabled = values.Number(null, "DesktopEnabled") is not 0,
            ConsentModes = modes is { Count: > 0 } ? modes : d.ConsentModes,
            ConsentTimeoutSeconds = values.Number(null, "ConsentTimeoutSeconds") is { } t and >= 5 and <= 600 ? t : d.ConsentTimeoutSeconds,
            MaxFramesPerSecond = values.Number(null, "MaxFramesPerSecond") is { } f and >= 1 and <= 60 ? f : d.MaxFramesPerSecond,
            MaxQuality = values.Number(null, "MaxQuality") is { } q and >= 1 and <= 100 ? q : d.MaxQuality,
            Clipboard = values.Number(null, "DesktopClipboard") is { } c and >= 0 and <= 3 ? c : d.Clipboard,
            IdleTimeout = values.Number(null, "DesktopIdleTimeoutMinutes") is { } idle and >= 0 and <= 1440 ? TimeSpan.FromMinutes(idle) : d.IdleTimeout,
            AuditToEventLog = values.Number(null, "AuditToEventLog") is not 0,
            PrivacyScreen = values.Number(null, "PrivacyScreen") is { } p and >= PrivacyScreenLevel.Off and <= PrivacyScreenLevel.WithInputBlock ? p : d.PrivacyScreen,
            HiddenApps = values.Strings(null, "HiddenApps")?.Where(a => a.Trim().Length > 0).Select(AppIds.Normalize).Distinct().ToList() ?? d.HiddenApps,
            TunnelEnabled = values.Number(null, "TunnelEnabled") is not 0,
            Tunnel = TunnelRules.Parse(values.Strings(null, "TunnelTargets"), values.Strings(null, "TunnelPorts")),
            MaxTunnelStreams = values.Number(null, "MaxTunnelStreams") is { } streams and >= 1 and <= 1024 ? streams : d.MaxTunnelStreams,
            Rights = rights,
        };
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
