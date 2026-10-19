namespace RemoteHub.Agent;

/// <summary>
/// <c>RemoteHubAgent.exe policy</c>: every agent policy value in effect and where it comes from – the policy key, agent.json
/// or the built-in default – with invalid values flagged (docs/admx-design.md §11.3). For support and the lab tests.
/// Reads only; the service reads the same values for every connection.
/// </summary>
public static class PolicyCommand
{
    public static int Run(TextWriter output)
    {
        AgentConfig? config = null;
        string? configError = null;
        try { config = AgentConfig.Load(AgentConfig.DefaultPath); }
        catch (Exception ex) { configError = ex.Message; }
        output.Write(Report(config, configError, AgentPolicy.Load(config ?? new AgentConfig()), PolicyCatalog.Configured(PolicyCatalog.AgentEntries)));
        return 0;
    }

    /// <summary>The report (tests pass their own values).</summary>
    internal static string Report(AgentConfig? config, string? configError, AgentPolicy effective, IReadOnlyList<PolicyValue> configured)
    {
        var sw = new StringWriter();
        sw.WriteLine($"RemoteHub Agent {AgentHost.AgentVersion} – policy ({@"HKLM\" + AgentPolicy.RegistryPath})");
        if (configError is not null) sw.WriteLine($"agent.json can't be read ({configError}) – its values are shown as defaults.");
        if (effective.LoadError is { } error) sw.WriteLine($"THE POLICY KEY CAN'T BE READ ({error}): the agent allows nothing until it can.");
        var byEntry = configured.ToDictionary(v => v.Entry);
        foreach (var group in PolicyCatalog.AgentEntries.GroupBy(e => e.Key == PolicyCatalog.RightsKey ? "Access rights" : e.Group))
        {
            sw.WriteLine();
            sw.WriteLine(group.Key);
            foreach (var entry in group)
            {
                var name = entry.Key == PolicyCatalog.RightsKey ? @"Rights\" + entry.Name : entry.Name;
                byEntry.TryGetValue(entry, out var set);
                var (value, source) = set is { Valid: true }
                    ? (entry.Describe(set.Value!), "policy")
                    : Effective(entry, effective, config);
                sw.WriteLine($"  {name,-32} {value}  [{source}]");
                if (set is { Valid: false }) sw.WriteLine($"  {"",-32} INVALID in the policy: {set.Problem} – ignored");
            }
        }
        return sw.ToString();
    }

    /// <summary>The value in effect when the policy doesn't set it, and whether agent.json or the default gave it.</summary>
    private static (string Value, string Source) Effective(PolicyEntry e, AgentPolicy p, AgentConfig? config)
    {
        string Groups(AgentRight right) => p.Groups(right) is { Count: > 0 } g ? string.Join("; ", g) : "nobody";
        var json = config is null ? "default" : "agent.json";
        return e.Name switch
        {
            _ when e.Key == PolicyCatalog.RightsKey => (Groups(Enum.Parse<AgentRight>(e.Name)),
                e.Name is nameof(AgentRight.DesktopViewOnly) or nameof(AgentRight.ConsentBypass) ? "default" : json),
            "AllowedGroups" => (config is null ? AgentConfig.AdministratorsSid : string.Join("; ", config.AllowedGroups), json),
            "NoUserBehavior" => (p.NoUserBehavior, config?.NoUserBehavior is not null ? "agent.json" : "default"),
            "VirtualDisplay" => (p.VirtualDisplay, json),
            "ListenPort" => (p.ListenPort.ToString(System.Globalization.CultureInfo.InvariantCulture), json),
            "LogRetentionDays" => (p.LogRetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture), json),
            "MaxSessions" => (p.MaxSessions.ToString(System.Globalization.CultureInfo.InvariantCulture), json),
            "PowerShellEnabled" => (p.PowerShellEnabled ? "On" : "Off", "default"),
            "DesktopEnabled" => (p.DesktopEnabled ? "On" : "Off", "default"),
            "TunnelEnabled" => (p.TunnelEnabled ? "On" : "Off", "default"),
            "AuditToEventLog" => (p.AuditToEventLog ? "On" : "Off", "default"),
            "AutoSignInRequirePrivacyScreen" => (p.AutoSignInRequirePrivacyScreen ? "On" : "Off", "default"),
            "MaxFramesPerSecond" => (Num(p.MaxFramesPerSecond), "default"),
            "MaxQuality" => (Num(p.MaxQuality), "default"),
            "DesktopClipboard" => (e.Describe(p.Clipboard), "default"),
            "DesktopIdleTimeoutMinutes" => (p.IdleTimeout == TimeSpan.Zero ? "0 (never)" : Num((int)p.IdleTimeout.TotalMinutes), "default"),
            "AllowedConsentModes" => (string.Join("; ", p.ConsentModes), "default"),
            "ConsentTimeoutSeconds" => (Num(p.ConsentTimeoutSeconds), "default"),
            "PrivacyScreen" => (e.Describe(p.PrivacyScreen), "default"),
            "PrivacyScreenMessage" => ("(built-in text)", "default"),
            "HiddenApps" => ("none", "default"),
            "AutoSignInEndAction" => ("RemoteHub's setting decides", "default"),
            "TunnelTargets" => ("anywhere (except this device's special addresses)", "default"),
            "TunnelPorts" => ("every port", "default"),
            "MaxTunnelStreams" => (Num(p.MaxTunnelStreams), "default"),
            _ => ("", "default"),
        };

        static string Num(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
