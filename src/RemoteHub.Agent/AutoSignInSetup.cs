using Microsoft.Win32;

namespace RemoteHub.Agent;

/// <summary>
/// Switches automatic sign-in on or off for this device at install time (docs/credential-provider-design.md §5): the
/// policy NoUserBehavior = AutoSignIn and the right AutoSignIn for the agent's allowed groups – the two values IT would
/// otherwise set by Group Policy. Run by the MSI (enroll with AUTOSIGNIN) and by RemoteHub's "Install / Remove
/// credential provider" (<c>RemoteHubAgent.exe auto-sign-in on|off</c>, elevated).
/// <para>
/// A value that is already there is never changed – IT's policy wins. What this wrote is recorded next to agent.json, so
/// "off" (and uninstall) removes exactly that, and only while it is still as it was written.
/// </para>
/// </summary>
public static class AutoSignInSetup
{
    private const string BehaviorValue = "NoUserBehavior", RightValue = "AutoSignIn";

    internal static string MarkerPath => Path.Combine(AgentConfig.Folder, "autosignin.policy");

    /// <summary>Writes what isn't set yet; returns what happened, for the log and RemoteHub.</summary>
    public static string On(IReadOnlyList<string> groups)
    {
        var written = ReadMarker();
        var notes = new List<string>();
        using var key = Registry.LocalMachine.CreateSubKey(AgentPolicy.RegistryPath);
        switch (key.GetValue(BehaviorValue))
        {
            case null:
                key.SetValue(BehaviorValue, NoUserBehaviors.AutoSignIn, RegistryValueKind.String);
                written[BehaviorValue] = NoUserBehaviors.AutoSignIn;
                break;
            case string s when NoUserBehaviors.Valid(s) == NoUserBehaviors.AutoSignIn:
                break;
            case var other:
                notes.Add($"{BehaviorValue} is set to \"{other}\" on this device (Group Policy?) – left as it is, so automatic sign-in stays off");
                break;
        }
        using (var rights = key.CreateSubKey("Rights"))
        {
            if (rights.GetValue(RightValue) is null)
            {
                rights.SetValue(RightValue, groups.ToArray(), RegistryValueKind.MultiString);
                written[@"Rights\" + RightValue] = string.Join(";", groups);
            }
            else notes.Add($@"Rights\{RightValue} is already set on this device – left as it is");
        }
        WriteMarker(written);
        return notes.Count == 0
            ? $"Automatic sign-in allowed on this device for {string.Join(", ", groups.Select(Describe))}."
            : "Automatic sign-in: " + string.Join("; ", notes) + ".";
    }

    /// <summary>Removes what <see cref="On"/> wrote, if it is still unchanged. Nothing written → nothing to do.</summary>
    public static string Off()
    {
        var written = ReadMarker();
        if (written.Count == 0) return "Automatic sign-in: no policy values set by the agent.";
        using (var key = Registry.LocalMachine.OpenSubKey(AgentPolicy.RegistryPath, writable: true))
        {
            if (key is not null)
            {
                if (written.TryGetValue(BehaviorValue, out var behavior) && key.GetValue(BehaviorValue) is string now && now == behavior)
                    key.DeleteValue(BehaviorValue, throwOnMissingValue: false);
                using var rights = key.OpenSubKey("Rights", writable: true);
                if (rights is not null && written.TryGetValue(@"Rights\" + RightValue, out var groups)
                    && rights.GetValue(RightValue) is string[] current && string.Join(";", current) == groups)
                    rights.DeleteValue(RightValue, throwOnMissingValue: false);
            }
        }
        try { File.Delete(MarkerPath); } catch { /* gone */ }
        return "Automatic sign-in: the policy values set by the agent were removed.";
    }

    private static string Describe(string group) => group == AgentConfig.AdministratorsSid ? "Administrators" : group;

    private static Dictionary<string, string> ReadMarker()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadAllLines(MarkerPath))
                if (line.IndexOf('=') is var i and > 0) values[line[..i]] = line[(i + 1)..];
        }
        catch { /* none */ }
        return values;
    }

    private static void WriteMarker(Dictionary<string, string> values)
    {
        if (values.Count == 0) return;
        Directory.CreateDirectory(AgentConfig.Folder); // protected at enrollment (SYSTEM and Administrators only)
        File.WriteAllLines(MarkerPath, values.Select(v => $"{v.Key}={v.Value}"));
    }
}
