using Microsoft.Win32;

namespace RemoteHub.Agent;

/// <summary>
/// Switches automatic sign-in on or off for this device at install time (docs/credential-provider-design.md §5): "allow it"
/// and the groups that may use it – the two things IT can also decide by policy (NoUserBehavior, Rights\AutoSignIn), which
/// then win. Run by the MSI (enroll with AUTOSIGNIN) and by RemoteHub's "Install / Remove credential provider"
/// (<c>RemoteHubAgent.exe auto-sign-in on|off</c>, elevated); the running service picks it up at the next connection.
/// <para>
/// Since 0.18.12 the install's choice is kept in agent.json (<see cref="AgentConfig.NoUserBehavior"/>,
/// <see cref="AgentConfig.AutoSignInGroups"/>). Before, it was written into the policy key, where Group Policy would delete
/// it again when IT sets the policy back to "Not configured" (docs/admx-design.md §11.3); <see cref="Migrate"/> moves it.
/// </para>
/// </summary>
public static class AutoSignInSetup
{
    private const string BehaviorValue = "NoUserBehavior", RightValue = "AutoSignIn";

    /// <summary>What versions before 0.18.12 wrote into the policy key, so it could be removed again.</summary>
    internal static string MarkerPath => Path.Combine(AgentConfig.Folder, "autosignin.policy");

    /// <summary>Allows automatic sign-in for <paramref name="groups"/>; returns what happened, for the log and RemoteHub.</summary>
    public static string On(IReadOnlyList<string> groups) => On(groups, AgentConfig.DefaultPath, ReadPolicy());

    /// <summary>Turns automatic sign-in off in agent.json. The policy, if IT set one, stays as it is and still wins.</summary>
    public static string Off() => Off(AgentConfig.DefaultPath, ReadPolicy());

    internal static string On(IReadOnlyList<string> groups, string configPath, (string? Behavior, string[]? Right) policy)
    {
        Migrate(configPath);
        var config = AgentConfig.Load(configPath);
        config.NoUserBehavior = NoUserBehaviors.AutoSignIn;
        config.AutoSignInGroups = [.. groups];
        config.Save(configPath);
        var notes = new List<string>();
        if (policy.Behavior is { } behavior && NoUserBehaviors.Valid(behavior) != NoUserBehaviors.AutoSignIn)
            notes.Add($"the policy {BehaviorValue} is \"{behavior}\" on this device (Group Policy?) – it decides, so automatic sign-in stays off");
        if (policy.Right is not null)
            notes.Add($@"the policy Rights\{RightValue} is set on this device – it decides who may sign in automatically");
        return notes.Count == 0
            ? $"Automatic sign-in allowed on this device for {string.Join(", ", groups.Select(Describe))}."
            : "Automatic sign-in allowed by the install, but " + string.Join("; ", notes) + ".";
    }

    internal static string Off(string configPath, (string? Behavior, string[]? Right) policy)
    {
        Migrate(configPath);
        if (File.Exists(configPath))
        {
            // Said explicitly: not set means allowed (on by default, AgentPolicy.Defaults).
            var config = AgentConfig.Load(configPath);
            config.NoUserBehavior = NoUserBehaviors.SignInScreen;
            config.AutoSignInGroups = null;
            config.Save(configPath);
        }
        return policy.Behavior is { } behavior && NoUserBehaviors.Valid(behavior) == NoUserBehaviors.AutoSignIn
            ? $"Automatic sign-in: turned off by the install, but the policy {BehaviorValue} on this device still allows it."
            : "Automatic sign-in: turned off on this device.";
    }

    /// <summary>
    /// Once, after an update from a version before 0.18.12: the values that version wrote into the policy key – recorded in
    /// <see cref="MarkerPath"/> – move to agent.json, if they are still as written. A value IT changed since stays in the
    /// policy key: IT has taken it over. Null when there was nothing to move; otherwise what happened, for the log.
    /// </summary>
    public static string? Migrate(AgentConfig config)
    {
        if (config.SourcePath is not { } path) return null;
        var result = Migrate(path);
        if (result is not null)
        {
            var now = AgentConfig.Load(path);
            config.NoUserBehavior = now.NoUserBehavior;
            config.AutoSignInGroups = now.AutoSignInGroups;
        }
        return result;
    }

    /// <param name="key">The policy key (tests pass their own; null: HKLM).</param>
    /// <param name="markerPath">What the older version wrote (tests pass their own; null: <see cref="MarkerPath"/>).</param>
    internal static string? Migrate(string configPath, IPolicyKey? key = null, string? markerPath = null)
    {
        markerPath ??= MarkerPath;
        var written = ReadMarker(markerPath);
        if (written.Count == 0) return null;
        key ??= RegistryPolicyKey.Open();
        var moved = new List<string>();
        var kept = new List<string>();
        AgentConfig? config = File.Exists(configPath) ? AgentConfig.Load(configPath) : null;
        if (written.TryGetValue(BehaviorValue, out var behavior))
        {
            var now = key?.Text(BehaviorValue);
            if (now == behavior)
            {
                key!.Delete(null, BehaviorValue);
                config?.NoUserBehavior = behavior;
                moved.Add(BehaviorValue);
            }
            else if (now is not null) kept.Add(BehaviorValue); // changed: IT's now. Gone: removed since – not brought back.
        }
        if (written.TryGetValue(@"Rights\" + RightValue, out var groups))
        {
            var now = key?.List("Rights", RightValue);
            if (now is not null && string.Join(";", now) == groups)
            {
                key!.Delete("Rights", RightValue);
                config?.AutoSignInGroups = [.. groups.Split(';', StringSplitOptions.RemoveEmptyEntries)];
                moved.Add(@"Rights\" + RightValue);
            }
            else if (now is not null) kept.Add(@"Rights\" + RightValue);
        }
        config?.Save(configPath);
        try { File.Delete(markerPath); } catch { /* gone */ }
        return "Automatic sign-in: " +
               (moved.Count > 0 ? $"moved {string.Join(" and ", moved)} from the policy key to agent.json" : "nothing to move from the policy key") +
               (kept.Count > 0 ? $"; {string.Join(" and ", kept)} changed since the install – left in the policy key (IT's now)" : "") + ".";
    }

    /// <summary>The policy key, as far as the migration needs it (tests pass their own).</summary>
    internal interface IPolicyKey
    {
        string? Text(string name);
        string[]? List(string subKey, string name);
        void Delete(string? subKey, string name);
    }

    private sealed class RegistryPolicyKey(RegistryKey key) : IPolicyKey
    {
        public static RegistryPolicyKey? Open() => Registry.LocalMachine.OpenSubKey(AgentPolicy.RegistryPath, writable: true) is { } k ? new(k) : null;

        public string? Text(string name) => key.GetValue(name) as string;

        public string[]? List(string subKey, string name)
        {
            using var sub = key.OpenSubKey(subKey);
            return sub?.GetValue(name) as string[];
        }

        public void Delete(string? subKey, string name)
        {
            if (subKey is null) { key.DeleteValue(name, throwOnMissingValue: false); return; }
            using var sub = key.OpenSubKey(subKey, writable: true);
            sub?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    /// <summary>What the policy sets for automatic sign-in now (for the notes in the answer).</summary>
    private static (string? Behavior, string[]? Right) ReadPolicy()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AgentPolicy.RegistryPath);
            using var rights = key?.OpenSubKey("Rights");
            return (key?.GetValue(BehaviorValue) as string, rights?.GetValue(RightValue) as string[]);
        }
        catch { return (null, null); }
    }

    private static string Describe(string group) => group == AgentConfig.AdministratorsSid ? "Administrators" : group;

    private static Dictionary<string, string> ReadMarker(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadAllLines(path))
                if (line.IndexOf('=') is var i and > 0) values[line[..i]] = line[(i + 1)..];
        }
        catch { /* none */ }
        return values;
    }
}
