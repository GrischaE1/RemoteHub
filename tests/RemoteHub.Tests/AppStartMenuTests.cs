extern alias agent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// The Start menu list for app tabs (docs/app-tabs-design.md §6.5): which shortcut files are read, which become apps,
/// and the limits. Shortcuts are stood in for by their fields; the folder walk runs on a temporary folder.
/// </summary>
public class AppStartMenuTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rh-startmenu-" + Guid.NewGuid().ToString("N"));
    private static readonly A.AgentPolicy Defaults = A.AgentPolicy.Defaults(new A.AgentConfig { AllowedGroups = ["S-1-5-32-544"] });

    public AppStartMenuTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var d in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).Where(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)))
            Directory.Delete(d); // the junction itself, never what it points to
        try { Directory.Delete(_root, true); } catch { /* none */ }
    }

    private static string Expand(string s) => s.Replace("%SystemRoot%", @"C:\Windows", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase);

    private static A.DesktopListedApp? Entry(A.AppStartMenu.Shortcut s, A.AgentPolicy? p = null, bool exists = true) =>
        A.AppStartMenu.Entry(s, p ?? Defaults, Expand, _ => exists);

    private static A.AppStartMenu.Shortcut Link(string target, string args = "", string name = "Tool", string group = "", bool runAs = false, string folder = "") =>
        new(name, group, target, args, folder, runAs);

    private void Touch(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    [Fact]
    public void Shortcut_files_are_found_with_their_folder_as_group_but_nothing_behind_a_junction()
    {
        Touch("Top.lnk");
        Touch(@"Administrative Tools\Event Viewer.lnk");
        Touch(@"Office\Tools\Deep.lnk");
        Touch("readme.txt");
        Touch("Web.url");
        var target = Path.Combine(_root, "..", "rh-junction-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(target, "Behind.lnk"), "");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(_root, "Junction")}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();

            var found = A.AppStartMenu.Files(_root).Select(f => (Path.GetFileName(f.File), f.Group)).ToList();
            Assert.Equal([("Top.lnk", ""), ("Event Viewer.lnk", "Administrative Tools"), ("Deep.lnk", @"Office\Tools")], found);
        }
        finally { Directory.Delete(target, true); }
    }

    [Fact]
    public void Of_two_shortcuts_to_one_app_the_plain_name_comes_first_and_is_kept()
    {
        Touch("Tool Copy.lnk");
        Touch("Tool.lnk"); // "Tool Copy.lnk" sorts before "Tool.lnk" by file name (space before dot)
        Assert.Equal(["Tool.lnk", "Tool Copy.lnk"], A.AppStartMenu.Files(_root).Select(f => Path.GetFileName(f.File)));
    }

    [Fact]
    public void The_walk_stops_at_its_depth()
    {
        Touch(string.Join('\\', Enumerable.Range(1, A.AppStartMenu.MaxDepth)) + @"\Too deep.lnk");
        Touch(string.Join('\\', Enumerable.Range(1, A.AppStartMenu.MaxDepth - 1)) + @"\Deep enough.lnk");
        Assert.Equal(["Deep enough.lnk"], A.AppStartMenu.Files(_root).Select(f => Path.GetFileName(f.File)));
    }

    [Fact]
    public void Programs_are_listed_as_the_shortcut_has_them_and_consoles_as_mmc()
    {
        var tool = Entry(Link(@"%ProgramFiles%\Tool\tool.exe", "/x", folder: @"C:\Work", runAs: true))!;
        Assert.Equal((@"%ProgramFiles%\Tool\tool.exe", "/x", @"C:\Work", true), (tool.Program, tool.Arguments, tool.WorkingFolder, tool.Elevated));

        var events = Entry(Link(@"%SystemRoot%\system32\eventvwr.msc", "/s", name: "Event Viewer", group: "Administrative Tools"))!;
        Assert.Equal(@"%SystemRoot%\system32\mmc.exe", events.Program);
        Assert.Equal(@"""%SystemRoot%\system32\eventvwr.msc"" /s", events.Arguments);
        Assert.Equal(("Event Viewer", "Administrative Tools"), (events.Name, events.Group));
    }

    [Theory]
    [InlineData(@"C:\Docs\readme.txt")]
    [InlineData(@"C:\Docs\help.chm")]
    [InlineData(@"C:\Tools\run.bat")]
    [InlineData(@"C:\Tools")]
    [InlineData(@"\\server\share\tool.exe")]  // on another machine: never looked up by the service
    [InlineData(@"tool.exe")]
    [InlineData("")]                           // an advertised shortcut: no path
    public void Anything_but_a_local_program_or_console_is_left_out(string target) => Assert.Null(Entry(Link(target)));

    [Fact]
    public void A_target_that_isnt_on_the_device_is_left_out() => Assert.Null(Entry(Link(@"C:\Tools\gone.exe"), exists: false));

    [Fact]
    public void Fields_over_their_caps_are_left_out_not_cut()
    {
        Assert.Null(Entry(Link(@"C:\Tools\tool.exe", new string('a', A.AppStartMenu.MaxArguments + 1))));
        Assert.Null(Entry(Link(@"C:\" + new string('a', A.AppStartMenu.MaxPath) + ".exe")));
        Assert.Null(Entry(Link(@"C:\Tools\tool.exe", name: new string('n', A.AppStartMenu.MaxName + 1))));
        Assert.Null(Entry(Link(@"C:\Tools\tool.exe", name: "Two\nlines")));
        Assert.NotNull(Entry(Link(@"C:\Tools\tool.exe", new string('a', A.AppStartMenu.MaxArguments))));
    }

    [Fact]
    public void The_policy_filters_the_list_and_clears_run_as_administrator()
    {
        var p = A.AgentPolicy.From(new A.AgentConfig { AllowedGroups = ["S-1-5-32-544"] }, new PolicyTemplateTests.Values(new()
        {
            ["DesktopAppsAllowed"] = new[] { @"%SystemRoot%\system32\mmc.exe" }, ["DesktopAppsElevated"] = 0,
        }));
        Assert.Null(Entry(Link(@"C:\Tools\tool.exe"), p));
        var console = Entry(Link(@"%SystemRoot%\system32\services.msc", runAs: true), p)!;
        Assert.False(console.Elevated);
    }

    [Fact]
    public void Duplicates_go_and_the_list_says_when_it_was_cut()
    {
        var shortcuts = new List<A.AppStartMenu.Shortcut?>
        {
            Link(@"C:\Tools\tool.exe", "/a", name: "Tool"),
            Link(@"c:\tools\TOOL.exe", "/a", name: "Tool copy"),    // the same program and arguments
            Link(@"C:\Tools\tool.exe", "/b", name: "Tool B"),
            null,                                                   // unreadable
            Link(@"C:\Docs\readme.txt"),
        };
        var list = A.AppStartMenu.List([_root], Defaults, _ => shortcuts, Expand, _ => true);
        Assert.Equal(["Tool", "Tool B"], list.Apps.Select(a => a.Name));
        Assert.False(list.Truncated);

        var many = Enumerable.Range(0, A.AppStartMenu.MaxEntries + 5).Select(i => (A.AppStartMenu.Shortcut?)Link($@"C:\Tools\t{i}.exe", name: $"T{i}")).ToList();
        var cut = A.AppStartMenu.List([_root], Defaults, _ => many, Expand, _ => true);
        Assert.Equal(A.AppStartMenu.MaxEntries, cut.Apps.Length);
        Assert.True(cut.Truncated);
    }
}
