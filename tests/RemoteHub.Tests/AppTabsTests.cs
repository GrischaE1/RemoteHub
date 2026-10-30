extern alias agent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// App tabs in the agent (docs/app-tabs-design.md §6.1, §9): who may open them, what a start from the wire may run, and
/// the policy values that limit them.
/// </summary>
public class AppTabsTests
{
    private static readonly A.AgentConfig Config = new() { AllowedGroups = ["S-1-5-32-544"] };
    private static readonly A.AgentPolicy Defaults = A.AgentPolicy.Defaults(Config);
    private static string Mmc => Path.Combine(Environment.SystemDirectory, "mmc.exe");
    private static string EventViewer => Path.Combine(Environment.SystemDirectory, "eventvwr.msc");

    private static A.AgentPolicy From(Dictionary<string, object> values) => A.AgentPolicy.From(Config, new PolicyTemplateTests.Values(values));

    /// <summary>The account's environment, as ExpandEnvironmentStringsForUser would give it.</summary>
    private static string Expand(string s) => s.Replace("%SystemRoot%", Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase);

    private static (A.AppTabs.Launch? Launch, string? Refusal) Check(A.DesktopAppStart app, A.AgentPolicy? policy = null, bool exists = true) =>
        A.AppTabs.Check(app, policy ?? Defaults, Expand, _ => exists);

    private static A.DesktopAppStart App(string program, string arguments = "", string folder = "", bool elevated = false, string name = "Tool") =>
        new(program, arguments, folder, elevated, name);

    [Fact]
    public void App_tabs_need_control_the_policy_the_virtual_display_and_automatic_sign_in()
    {
        Assert.Null(A.AppTabs.Refusal(Defaults, control: true, autoSignInWhyNot: null, virtualDisplay: true));
        Assert.Contains("right Desktop", A.AppTabs.Refusal(Defaults, false, null, true));
        Assert.Contains("policy DesktopApps", A.AppTabs.Refusal(From(new() { ["DesktopApps"] = 0 }), true, null, true));
        Assert.Contains("virtual display", A.AppTabs.Refusal(Defaults, true, null, false));
        // without automatic sign-in a device nobody is signed in on couldn't give the admin a session: no app tabs at all
        Assert.Equal("App tabs need automatic sign-in on this device. Automatic sign-in isn't available on this device.",
            A.AppTabs.Refusal(Defaults, true, "Automatic sign-in isn't available on this device.", true));
    }

    [Fact]
    public void A_program_starts_directly_from_its_folder_with_its_arguments()
    {
        var (launch, why) = Check(App(@"%SystemRoot%\System32\notepad.exe", "C:\\temp\\a b.txt"));
        Assert.Null(why);
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.Equal(notepad, launch!.Program, ignoreCase: true);
        Assert.Equal($"\"{launch.Program}\" C:\\temp\\a b.txt", launch.CommandLine);
        Assert.Equal(Environment.SystemDirectory, launch.WorkingFolder, ignoreCase: true);
        Assert.False(launch.Elevated);
    }

    [Fact]
    public void A_console_starts_as_mmc_with_the_console_first()
    {
        var (launch, why) = Check(App(EventViewer, "/s", elevated: true, name: "Event Viewer"));
        Assert.Null(why);
        Assert.Equal(Mmc, launch!.Program, ignoreCase: true);
        Assert.Equal($"\"{Mmc}\" \"{EventViewer}\" /s", launch.CommandLine, ignoreCase: true);
        Assert.True(launch.Elevated);
        Assert.Equal("Event Viewer", launch.Name);
    }

    [Theory]
    [InlineData(@"notepad.exe")]                         // not a full path: no search through PATH
    [InlineData(@"System32\notepad.exe")]
    [InlineData(@"\\.\C:\Windows\notepad.exe")]           // device paths
    [InlineData(@"\\?\C:\Windows\notepad.exe")]
    [InlineData(@"\??\C:\Windows\notepad.exe")]
    [InlineData("C:\\Windows\\note\npad.exe")]            // control characters
    [InlineData("")]
    public void Anything_but_a_full_path_is_refused(string program) =>
        Assert.Contains("isn't a full path", Check(App(program)).Refusal);

    [Theory]
    [InlineData(@"C:\Tools\run.bat")]
    [InlineData(@"C:\Tools\run.cmd")]
    [InlineData(@"C:\Tools\readme.txt")]
    [InlineData(@"C:\Tools\script.ps1")]
    public void Only_programs_and_consoles_are_started_never_through_the_shell(string program) =>
        Assert.Equal("Tool: only programs (.exe) and consoles (.msc) can be started.", Check(App(program)).Refusal);

    [Fact]
    public void A_program_that_isnt_on_the_device_is_refused() =>
        Assert.Equal(@"Tool: C:\Tools\gone.exe isn't on the device.", Check(App(@"C:\Tools\gone.exe"), exists: false).Refusal);

    [Fact]
    public void The_allow_list_limits_the_programs_and_set_but_empty_allows_none()
    {
        var list = From(new() { ["DesktopAppsAllowed"] = new[] { @"%SystemRoot%\System32\mmc.exe", @"C:\Tools\Tool.EXE" } });
        Assert.Null(Check(App(EventViewer), list).Refusal); // consoles run as mmc.exe, which is listed
        Assert.Null(Check(App(@"c:\tools\..\tools\tool.exe"), list).Refusal);
        Assert.Equal("Tool isn't among the programs this device allows in app tabs (policy DesktopAppsAllowed).",
            Check(App(@"C:\Tools\Other.exe"), list).Refusal);

        var console = From(new() { ["DesktopAppsAllowed"] = new[] { EventViewer } });
        Assert.Null(Check(App(EventViewer), console).Refusal); // or the console itself is listed
        Assert.NotNull(Check(App(Path.Combine(Environment.SystemDirectory, "services.msc")), console).Refusal);

        var none = From(new() { ["DesktopAppsAllowed"] = new[] { " " } });
        Assert.NotNull(none.DesktopAppsAllowed);
        Assert.NotNull(Check(App(@"C:\Tools\Tool.exe"), none).Refusal); // a list that restricts never turns into "any"
        Assert.Null(Defaults.DesktopAppsAllowed);
    }

    [Fact]
    public void Run_as_administrator_is_refused_where_the_policy_turns_it_off() =>
        Assert.Equal("Tool can't be started as administrator on this device (policy DesktopAppsElevated).",
            Check(App(@"C:\Tools\Tool.exe", elevated: true), From(new() { ["DesktopAppsElevated"] = 0 })).Refusal);

    [Fact]
    public void Arguments_beyond_windows_limit_are_refused_and_never_appear_in_a_refusal()
    {
        var secret = "--password=" + new string('x', A.AppTabs.MaxCommandLine);
        var why = Check(App(@"C:\Tools\Tool.exe", secret)).Refusal;
        Assert.Equal("Tool: the arguments are too long.", why);
        Assert.DoesNotContain("password", Check(App(@"C:\Tools\Tool.exe", "--password=1\0"), exists: true).Refusal);
        Assert.DoesNotContain("password", Check(App(@"C:\Tools\gone.exe", "--password=1"), exists: false).Refusal);
    }

    [Fact]
    public void The_folder_to_start_in_must_be_a_full_path_too()
    {
        Assert.Equal(@"C:\Work", Check(App(@"C:\Tools\Tool.exe", folder: @"C:\Work")).Launch!.WorkingFolder);
        Assert.Equal(@"C:\Program Files\Tool", Check(App(@"C:\Tools\Tool.exe", folder: @"%ProgramFiles%\Tool")).Launch!.WorkingFolder);
        Assert.Equal("Tool: the folder to start in isn't a full path on the device.", Check(App(@"C:\Tools\Tool.exe", folder: @"..\Work")).Refusal);
        Assert.Equal(@"C:\Tools", Check(App(@"C:\Tools\Tool.exe")).Launch!.WorkingFolder);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Line\nbreak")]
    public void A_name_that_cant_be_shown_is_refused(string name) =>
        Assert.Equal("The app's name is empty or too long.", Check(App(@"C:\Tools\Tool.exe", name: name)).Refusal);

    [Fact]
    public void The_policy_values_for_app_tabs_are_read_with_their_limits()
    {
        Assert.Equal((true, true, 7), (Defaults.DesktopApps, Defaults.DesktopAppsElevated, Defaults.MaxAppTabs));
        var p = From(new() { ["DesktopApps"] = 0, ["DesktopAppsElevated"] = 0, ["MaxAppTabs"] = 3 });
        Assert.Equal((false, false, 3), (p.DesktopApps, p.DesktopAppsElevated, p.MaxAppTabs));
        Assert.Equal(7, From(new() { ["MaxAppTabs"] = 8 }).MaxAppTabs); // out of range: the default (the driver's monitors but the first)
        Assert.False(A.AgentPolicy.Refused("unreadable").DesktopApps);
    }
}
