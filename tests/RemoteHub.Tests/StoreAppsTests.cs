extern alias agent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// Store apps in app tabs (docs/app-tabs-design.md §6.6): the check of a start by AppUserModelID, and what the agent
/// accepts from its <c>store-apps</c> process – which runs as the user, so its answer is not trusted.
/// </summary>
public class StoreAppsTests
{
    private const string Terminal = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App";
    private static readonly A.AgentConfig Config = new() { AllowedGroups = ["S-1-5-32-544"] };
    private static readonly A.AgentPolicy Defaults = A.AgentPolicy.Defaults(Config);
    private static A.AgentPolicy From(Dictionary<string, object> values) => A.AgentPolicy.From(Config, new PolicyTemplateTests.Values(values));

    private static (A.AppTabs.Launch? Launch, string? Refusal) Check(string program, string arguments = "", bool elevated = false, A.AgentPolicy? policy = null) =>
        A.AppTabs.Check(new A.DesktopAppStart(program, arguments, "", elevated, "Terminal"), policy ?? Defaults, s => s, _ => false);

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void A_store_app_is_started_by_its_id_with_only_its_arguments()
    {
        var (launch, why) = Check(@"shell:AppsFolder\" + Terminal, " -p PowerShell ");
        Assert.Null(why);
        Assert.Equal(Terminal, launch!.AppId);
        Assert.Equal(@"shell:AppsFolder\" + Terminal, launch.Program);
        Assert.Equal("-p PowerShell", launch.CommandLine);
        Assert.Equal("", launch.WorkingFolder);
        Assert.False(launch.Elevated);
        // The prefix in any case, as a connection may have it typed
        Assert.Equal(Terminal, Check(@"SHELL:appsfolder\" + Terminal).Launch?.AppId);
    }

    [Theory]
    [InlineData(@"shell:AppsFolder\")]
    [InlineData(@"shell:AppsFolder\NoBang")]
    [InlineData(@"shell:AppsFolder\a!b!c")]
    [InlineData(@"shell:AppsFolder\..\..\Windows!App")]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App /c calc")]
    [InlineData("shell:AppsFolder\\Pkg!App\0")]
    public void A_store_app_id_that_cant_be_used_is_refused(string program)
    {
        var (launch, why) = Check(program);
        Assert.Null(launch);
        Assert.Equal("Terminal: the Store app's ID can't be used.", why);
    }

    [Fact]
    public void A_store_app_id_longer_than_256_characters_is_refused()
    {
        Assert.NotNull(Check(@"shell:AppsFolder\" + new string('a', 252) + "!App").Launch);
        Assert.Null(Check(@"shell:AppsFolder\" + new string('a', 253) + "!App").Launch);
    }

    [Fact]
    public void A_store_app_is_never_started_as_administrator()
    {
        var (launch, why) = Check(@"shell:AppsFolder\" + Terminal, elevated: true);
        Assert.Null(launch);
        Assert.Equal("Terminal is a Store app – Store apps can't be started as administrator.", why);
    }

    [Fact]
    public void The_allow_list_names_a_store_app_by_its_id_or_as_shell_apps_folder()
    {
        var byId = From(new() { ["DesktopAppsAllowed"] = new[] { Terminal.ToUpperInvariant() } });
        var byPath = From(new() { ["DesktopAppsAllowed"] = new[] { @"shell:AppsFolder\" + Terminal } });
        var other = From(new() { ["DesktopAppsAllowed"] = new[] { @"%SystemRoot%\System32\mmc.exe" } });
        Assert.NotNull(Check(@"shell:AppsFolder\" + Terminal, policy: byId).Launch);
        Assert.NotNull(Check(@"shell:AppsFolder\" + Terminal, policy: byPath).Launch);
        Assert.Equal("Terminal isn't among the programs this device allows in app tabs (policy DesktopAppsAllowed).",
            Check(@"shell:AppsFolder\" + Terminal, policy: other).Refusal);
        Assert.True(A.AppTabs.StoreAppAllowed(Defaults, Terminal));
        Assert.False(A.AppTabs.StoreAppAllowed(other, Terminal));
    }

    [Fact]
    public void A_store_app_start_refuses_a_nul_in_its_arguments_and_never_says_them()
    {
        var (launch, why) = Check(@"shell:AppsFolder\" + Terminal, "secret\0more");
        Assert.Null(launch);
        Assert.Equal("Terminal: the arguments can't be used.", why);
        Assert.DoesNotContain("secret", why);
        Assert.Equal("Terminal: the arguments are too long.", Check(@"shell:AppsFolder\" + Terminal, new string('x', A.AppTabs.MaxCommandLine + 1)).Refusal);
    }

    [Fact]
    public void The_answer_to_a_start_is_a_positive_process_id_or_why_not()
    {
        Assert.Equal((4242, ""), A.StoreApps.ParseStarted(Utf8("started 4242\n")));
        Assert.Equal((4242, "reactivated"), A.StoreApps.ParseStarted(Utf8("started 4242 reactivated\n")));
        Assert.Equal((4242, ""), A.StoreApps.ParseStarted(Utf8("started 4242 whatever\n"))); // only the known words are passed on
        Assert.Equal((0, "ActivateApplication 0x80270254"), A.StoreApps.ParseStarted(Utf8("none ActivateApplication 0x80270254\n")));
        Assert.Equal(0, A.StoreApps.ParseStarted(Utf8("started 0\n")).Pid);
        Assert.Equal(0, A.StoreApps.ParseStarted(Utf8("started -5\n")).Pid);
        Assert.Equal(0, A.StoreApps.ParseStarted(Utf8("started 12 shown more\n")).Pid);
        Assert.Equal((0, "the answer wasn't understood"), A.StoreApps.ParseStarted(Utf8("hello\n")));
        Assert.Equal((0, "the answer wasn't understood"), A.StoreApps.ParseStarted([0xC3, 0x28])); // not UTF-8
        Assert.Equal(80, A.StoreApps.ParseStarted(Utf8("none " + new string('x', 500))).Why.Length);
    }

    [Fact]
    public void The_list_keeps_only_usable_ids_each_once_with_clean_names()
    {
        var text = "apps\n" + Terminal + "\tTerminal\n" + "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App\tCalculator\r\n"
                   + Terminal.ToLowerInvariant() + "\tTerminal again\n" // the same app twice
                   + @"C:\Windows\notepad.exe" + "\tNotepad\n" // a desktop program's parsing name: not a Store app
                   + "Pkg!App\t\n" // no name: the ID stands in
                   + "Long!App\t" + new string('n', 300) + "\n"
                   + "no tab here\n";
        var (apps, truncated, why) = A.StoreApps.ParseList(Utf8(text));
        Assert.Equal("", why);
        Assert.False(truncated);
        Assert.Equal([(Terminal, "Terminal"), ("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Calculator"), ("Pkg!App", "Pkg!App"), ("Long!App", new string('n', 256))],
            apps!.Select(a => (a.Id, a.Name)));
        Assert.All(apps!, a => Assert.Null(a.Icon));
    }

    [Fact]
    public void A_listed_store_app_keeps_its_icon_only_when_it_is_a_small_png()
    {
        var png = A.IconPng.Encode(2, 2, new byte[16]);
        var text = "apps\n"
                   + "One!App\tOne\t" + Convert.ToBase64String(png) + "\n"
                   + "Two!App\tTwo\t" + Convert.ToBase64String(Utf8("not a picture")) + "\n"
                   + "Three!App\tThree\t%%%not base64\n"
                   + "Four!App\tFour\t" + Convert.ToBase64String([.. png, .. new byte[A.IconPng.MaxBytes]]) + "\n";
        var apps = A.StoreApps.ParseList(Utf8(text)).Apps!;
        Assert.Equal(["One!App", "Two!App", "Three!App", "Four!App"], apps.Select(a => a.Id));
        Assert.Equal(png, apps[0].Icon);
        Assert.All(apps.Skip(1), a => Assert.Null(a.Icon));
    }

    [Fact]
    public void The_list_stops_at_its_limit_and_refuses_what_it_cant_read()
    {
        var many = "apps\n" + string.Concat(Enumerable.Range(0, A.AppStartMenu.MaxEntries + 5).Select(i => $"Pkg{i}!App\tApp {i}\n"));
        var (apps, truncated, _) = A.StoreApps.ParseList(Utf8(many));
        Assert.Equal(A.AppStartMenu.MaxEntries, apps!.Count);
        Assert.True(truncated);

        Assert.Equal((null, false, "the apps folder can't be read"), A.StoreApps.ParseList(Utf8("none the apps folder can't be read\n")));
        Assert.Null(A.StoreApps.ParseList(Utf8("files\nx")).Apps);
        Assert.Null(A.StoreApps.ParseList([0xFF, 0xFE]).Apps);
        Assert.Null(A.StoreApps.ParseList(new byte[A.StoreApps.MaxOutputBytes + 1]).Apps);
    }

    [Fact]
    public void The_store_apps_process_starts_nothing_for_an_id_it_cant_use_or_an_unknown_request()
    {
        var output = new StringWriter();
        Assert.Equal(2, A.StoreApps.Run(new StringReader("start\nnot an id\nargs"), output));
        Assert.Equal("none the app's ID can't be used\n", output.ToString());
        output = new StringWriter();
        Assert.Equal(2, A.StoreApps.Run(new StringReader("delete everything\n"), output));
        Assert.Equal("none nothing was asked\n", output.ToString());
    }
}
