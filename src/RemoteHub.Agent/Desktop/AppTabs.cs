namespace RemoteHub.Agent;

/// <summary>
/// App tabs (docs/app-tabs-design.md): who may open them on this device and what may be started in them. Decided here
/// in the service – RemoteHub only offers what <see cref="DesktopRights.Apps"/> allows, and every start is checked again.
/// </summary>
internal static class AppTabs
{
    /// <summary>An app's name as shown in messages and the audit (<see cref="DesktopAppStart.Name"/>).</summary>
    public const int MaxNameLength = 200;

    /// <summary>The longest command line Windows starts (CreateProcess), in characters.</summary>
    public const int MaxCommandLine = 32767;

    /// <summary>
    /// Why the account may not open app tabs on this device, or null when it may (<see cref="DesktopRights.Apps"/>).
    /// Automatic sign-in must be available here: app tabs run in the admin's own session, and without automatic sign-in a
    /// device nobody is signed in on (the usual server) couldn't give them one (app-tabs-design §4.1).
    /// </summary>
    /// <param name="autoSignInWhyNot">Why automatic sign-in isn't available to this account here (null: it is).</param>
    public static string? Refusal(AgentPolicy p, bool control, string? autoSignInWhyNot, bool virtualDisplay) =>
        !control ? "Your account may only see the screen on this device – app tabs need the right Desktop."
        : !p.DesktopApps ? "App tabs are turned off on this device (policy DesktopApps)."
        : !virtualDisplay ? "App tabs need the agent's virtual display, which isn't installed on this device."
        : autoSignInWhyNot is { } why ? "App tabs need automatic sign-in on this device. " + why
        : null;

    /// <summary>A checked start: the program to run, its whole command line, where it starts and whether elevated.</summary>
    public sealed record Launch(string Program, string CommandLine, string WorkingFolder, bool Elevated, string Name);

    /// <summary>
    /// Checks an app start from the wire (§6.1): the program must be a fully qualified <c>.exe</c> – or an <c>.msc</c>
    /// console, started as <c>mmc.exe</c> with the console as its first argument – after <paramref name="expand"/> (the
    /// account's environment); no device paths; on the device (<paramref name="exists"/>); among
    /// <see cref="AgentPolicy.DesktopAppsAllowed"/> when that is set; elevated only where the policy allows it; and the
    /// command line within Windows' limit. The refusal never contains the arguments.
    /// </summary>
    public static (Launch? Launch, string? Refusal) Check(DesktopAppStart app, AgentPolicy p, Func<string, string> expand, Func<string, bool> exists)
    {
        var name = app.Name.Trim();
        if (name.Length == 0 || name.Length > MaxNameLength || name.Any(char.IsControl)) return (null, "The app's name is empty or too long.");
        if (app.Elevated && !p.DesktopAppsElevated) return (null, $"{name} can't be started as administrator on this device (policy DesktopAppsElevated).");

        if (Path(app.Program, expand) is not { } program) return (null, $"{name}: the program isn't a full path on the device.");
        var extension = System.IO.Path.GetExtension(program);
        string? console = null;
        if (extension.Equals(".msc", StringComparison.OrdinalIgnoreCase))
        {
            console = program;
            program = System.IO.Path.Combine(Environment.SystemDirectory, "mmc.exe");
        }
        else if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return (null, $"{name}: only programs (.exe) and consoles (.msc) can be started.");

        if (p.DesktopAppsAllowed is { } allowed)
        {
            var list = allowed.Select(a => AppIds.Normalize(Environment.ExpandEnvironmentVariables(a))).ToHashSet(StringComparer.Ordinal);
            if (!list.Contains(AppIds.Normalize(console ?? program)) && !list.Contains(AppIds.Normalize(program)))
                return (null, $"{name} isn't among the programs this device allows in app tabs (policy DesktopAppsAllowed).");
        }
        if (!exists(console ?? program)) return (null, $"{name}: {console ?? program} isn't on the device.");

        if (app.Arguments.Contains('\0')) return (null, $"{name}: the arguments can't be used.");
        var arguments = console is null ? app.Arguments.Trim() : (Quote(console) + " " + app.Arguments.Trim()).Trim();
        var commandLine = Quote(program) + (arguments.Length > 0 ? " " + arguments : "");
        if (commandLine.Length > MaxCommandLine) return (null, $"{name}: the arguments are too long.");

        string folder;
        if (app.WorkingFolder.Trim().Length == 0) folder = System.IO.Path.GetDirectoryName(console ?? program)!;
        else if (Path(app.WorkingFolder, expand) is { } f) folder = f;
        else return (null, $"{name}: the folder to start in isn't a full path on the device.");

        return (new Launch(program, commandLine, folder, app.Elevated, name), null);
    }

    /// <summary>A fully qualified path after expanding, without device paths (<c>\\.\</c>, <c>\\?\</c>, <c>\??\</c>), quotes or control characters; else null.</summary>
    private static string? Path(string raw, Func<string, string> expand)
    {
        var s = raw.Trim().Trim('"');
        if (s.Length == 0 || s.Length > 32767 || s.Any(char.IsControl)) return null;
        s = expand(s);
        if (s.Any(char.IsControl) || s.Contains('"') || s.StartsWith(@"\\.\", StringComparison.Ordinal) || s.StartsWith(@"\\?\", StringComparison.Ordinal)
            || s.StartsWith(@"\??\", StringComparison.Ordinal) || !System.IO.Path.IsPathFullyQualified(s))
            return null;
        try { return System.IO.Path.GetFullPath(s); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string Quote(string path) => "\"" + path + "\"";
}
