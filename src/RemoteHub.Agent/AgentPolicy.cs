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
}

/// <summary>
/// Settings IT can enforce under HKLM\SOFTWARE\Policies\RemoteHub\Agent (ADMX-ready). A value there wins over agent.json.
/// Read again for every connection, so a changed policy applies without restarting the service.
/// <list type="bullet">
/// <item><c>DesktopEnabled</c> (DWORD) – 0 turns the remote desktop off.</item>
/// <item><c>AllowedConsentModes</c> (REG_MULTI_SZ) – any of Ask, Pin, None.</item>
/// <item><c>ConsentTimeoutSeconds</c> (DWORD) – how long the user is asked before it counts as "deny".</item>
/// <item><c>MaxFramesPerSecond</c>, <c>MaxQuality</c> (DWORD) – upper limits for what RemoteHub may choose.</item>
/// <item><c>DesktopClipboard</c> (DWORD) – 0 off, 1 text, 2 text and images, 3 text, images and files (default).</item>
/// <item><c>DesktopIdleTimeoutMinutes</c> (DWORD) – a remote desktop without mouse or keyboard input for this long ends (0 = never, default).</item>
/// <item><c>AuditToEventLog</c> (DWORD) – 0: audit entries only in the agent's log file, not in the Windows Application log.</item>
/// <item>Subkey <c>Rights</c>: PowerShell, Desktop, DesktopViewOnly, SecureDesktop, ConsentBypass (REG_MULTI_SZ) – group SIDs or names.</item>
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
    public IReadOnlyDictionary<AgentRight, IReadOnlyList<string>> Rights { get; init; } = new Dictionary<AgentRight, IReadOnlyList<string>>();

    public IReadOnlyList<string> Groups(AgentRight right) => Rights.TryGetValue(right, out var g) ? g : [];

    /// <summary>Defaults: everything for the groups in agent.json (Administrators), nothing view-only.</summary>
    public static AgentPolicy Defaults(AgentConfig config) => new()
    {
        Rights = new Dictionary<AgentRight, IReadOnlyList<string>>
        {
            [AgentRight.PowerShell] = config.AllowedGroups,
            [AgentRight.Desktop] = config.AllowedGroups,
            [AgentRight.DesktopViewOnly] = [],
            [AgentRight.SecureDesktop] = config.AllowedGroups,
            [AgentRight.ConsentBypass] = config.AllowedGroups,
        },
    };

    /// <summary>The defaults with every value from the policy key applied (missing or invalid values keep the default).</summary>
    public static AgentPolicy Load(AgentConfig config)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
            return From(config, key is null ? null : new RegistryValues(key));
        }
        catch (Exception) { return Defaults(config); }
    }

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
