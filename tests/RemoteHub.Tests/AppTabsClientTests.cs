using RemoteHub.Agent;
using RemoteHub.Models;
using RemoteHub.Remoting;

namespace RemoteHub.Tests;

/// <summary>App tabs in RemoteHub (docs/app-tabs-design.md §8): a connection's apps, how they inherit, and when a device offers app tabs.</summary>
public class AppTabsClientTests
{
    private static DesktopHello Hello(string[]? features, bool apps) =>
        new(1, "FS01", @"CONTOSO\admin", new DesktopRights(true, true, false, Apps: apps), [DesktopConsent.Ask], [], 30, 100, 30, features);

    [Fact]
    public void An_app_tab_is_titled_by_the_connection_and_the_app()
    {
        var events = new ConnectionApp("Event Viewer", @"%SystemRoot%\system32\mmc.exe", "eventvwr.msc");
        var tab = new AgentDesktopSession(new Connection { Name = "FS01", Host = "fs01.contoso.local" }, events);
        Assert.Equal("FS01 – Event Viewer", tab.Title);
        Assert.Same(events, tab.App);
        Assert.Equal("FS01", new AgentDesktopSession(new Connection { Name = "FS01", Host = "fs01.contoso.local" }).Title);
    }

    [Fact]
    public void A_device_offers_app_tabs_only_with_the_feature_and_to_an_account_it_allows()
    {
        Assert.Null(AgentDesktopSession.AppTabsRefusal(Hello([DesktopFeatures.Apps], apps: true), "FS01"));
        Assert.Contains("needs version 0.22", AgentDesktopSession.AppTabsRefusal(Hello(null, apps: false), "FS01"));        // an older agent
        Assert.Contains("virtual display", AgentDesktopSession.AppTabsRefusal(Hello([DesktopFeatures.Resume], apps: false), "FS01"));
        Assert.Contains("automatic sign-in", AgentDesktopSession.AppTabsRefusal(Hello([DesktopFeatures.Apps], apps: false), "FS01"));
    }

    [Fact]
    public void An_app_becomes_the_agent_s_start_and_a_listed_app_keeps_its_fields()
    {
        var app = new ConnectionApp("Tool", @"C:\Tools\tool.exe", "/x", @"C:\Work", Elevated: true);
        Assert.Equal(new DesktopAppStart(@"C:\Tools\tool.exe", "/x", @"C:\Work", true, "Tool"), app.ToStart());

        var listed = new DesktopListedApp("Event Viewer", "Administrative Tools", @"%SystemRoot%\system32\mmc.exe", @"""%SystemRoot%\system32\eventvwr.msc""", "", false, [1, 2, 3]);
        var kept = ConnectionApp.From(listed);
        Assert.Equal(("Event Viewer", @"%SystemRoot%\system32\mmc.exe", @"""%SystemRoot%\system32\eventvwr.msc""", false), (kept.Name, kept.Program, kept.Arguments, kept.Elevated));
        Assert.Equal([1, 2, 3], kept.Icon);
        Assert.True(kept.SameApp(kept with { Name = "Events", Program = @"%systemroot%\System32\MMC.exe" }));
        Assert.False(kept.SameApp(kept with { Arguments = "services.msc" }));
    }

    [Fact]
    public void A_connection_inherits_its_folder_s_apps_unless_it_has_a_list_of_its_own()
    {
        ConnectionApp[] folderApps = [new("Event Viewer", @"%SystemRoot%\system32\mmc.exe", "eventvwr.msc")];
        var folder = new RdpOptions { Apps = folderApps };
        Assert.Same(folderApps, new RdpOptions().FillFrom(folder).Apps);
        ConnectionApp[] own = [new("Tool", @"C:\Tools\tool.exe")];
        Assert.Same(own, new RdpOptions { Apps = own }.FillFrom(folder).Apps); // replaces, never merges
        Assert.Empty(new RdpOptions { Apps = [] }.FillFrom(folder).Apps!);       // an empty list of its own: none
        Assert.Null(RdpOptions.BuiltInDefaults().Apps);
    }
}
