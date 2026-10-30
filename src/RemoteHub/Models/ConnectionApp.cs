using RemoteHub.Agent;

namespace RemoteHub.Models;

/// <summary>
/// An app a connection opens in a tab of its own through the device's RemoteHub Agent (docs/app-tabs-design.md §8.1):
/// typed in, taken from a preset, or picked from the device's Start menu. Checked again by the agent when it starts.
/// </summary>
/// <param name="Name">Shown in the menu and the tab's title ("Event Viewer").</param>
/// <param name="Program">Full path on the device; environment variables are expanded there (<c>%SystemRoot%\system32\mmc.exe</c>).</param>
/// <param name="Arguments">Command line arguments; may hold paths or server names, so never logged.</param>
/// <param name="WorkingFolder">Start in this folder ("" = the program's own).</param>
/// <param name="Elevated">Run as administrator (the device's policy may refuse it).</param>
/// <param name="Icon">32×32 PNG from the device's Start menu, when it came from there.</param>
public sealed record ConnectionApp(string Name, string Program, string Arguments = "", string WorkingFolder = "", bool Elevated = false, byte[]? Icon = null)
{
    /// <summary>What the agent is asked to start.</summary>
    public DesktopAppStart ToStart() => new(Program, Arguments, WorkingFolder, Elevated, Name);

    /// <summary>The same app as far as starting it goes (name and icon aside): program and arguments, ignoring case in the path.</summary>
    public bool SameApp(ConnectionApp other) =>
        string.Equals(Program.Trim(), other.Program.Trim(), StringComparison.OrdinalIgnoreCase) && Arguments.Trim() == other.Arguments.Trim();

    /// <summary>An entry of the device's Start menu as a connection keeps it.</summary>
    public static ConnectionApp From(DesktopListedApp listed) =>
        new(listed.Name, listed.Program, listed.Arguments, listed.WorkingFolder, listed.Elevated, listed.Icon);
}
