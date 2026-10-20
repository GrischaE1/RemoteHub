using System.Management.Automation.Language;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.ViewModels;

namespace RemoteHub.Tests;

/// <summary>Favourites in the view model, and the console side of uninstalling the agent.</summary>
public class ConsoleFeaturesBatchTests
{
    private static (MainViewModel Vm, Folder Lab, Connection Dc, Connection Web) Tree()
    {
        var vm = new MainViewModel(new AppData());
        var lab = new Folder { Name = "Lab" };
        var dc = new Connection { Name = "DC01", Host = "dc01" };
        var web = new Connection { Name = "WEB01", Host = "web01" };
        vm.Add(lab, null);
        vm.Add(dc, lab);
        vm.Add(web, null);
        return (vm, lab, dc, web);
    }

    // ------------------------------------------------------------------ favourites

    [Fact]
    public void Pinning_shows_a_Favourites_group_with_links_in_pin_order_and_unpinning_removes_it()
    {
        var (vm, _, dc, web) = Tree();
        Assert.Empty(vm.FavoriteRoots);

        vm.SetFavorite(web, true);
        vm.SetFavorite(dc, true);
        var group = Assert.Single(vm.FavoriteRoots);
        Assert.Equal(["WEB01", "DC01"], group.Children.Select(l => l.Target.Name));
        Assert.True(vm.IsFavorite(dc));
        Assert.Equal([web.Id, dc.Id], vm.Data.Ui.FavoriteConnections);

        vm.SetFavorite(web, false);
        Assert.Equal(["DC01"], group.Children.Select(l => l.Target.Name));
        vm.SetFavorite(dc, false);
        Assert.Empty(vm.FavoriteRoots);
        Assert.Empty(vm.Data.Ui.FavoriteConnections);
    }

    [Fact]
    public void A_favourite_stays_in_its_folder_and_keeps_what_it_inherits()
    {
        var (vm, lab, dc, _) = Tree();
        lab.Options.ColorTag = "Red";
        vm.SetFavorite(dc, true);
        Assert.Same(lab, dc.Parent);
        Assert.Contains(dc, lab.Children);
        Assert.Equal("Red", RemoteHub.Services.Inheritance.ResolveOptions(dc, vm.Data).ColorTag);
        Assert.DoesNotContain(vm.Items, n => n == dc); // not copied to the top level
    }

    [Fact]
    public void Deleting_a_pinned_connection_or_its_folder_unpins_it_but_moving_it_doesnt()
    {
        var (vm, lab, dc, web) = Tree();
        vm.SetFavorite(dc, true);
        vm.SetFavorite(web, true);

        Assert.True(vm.Move(web, lab));
        Assert.True(vm.IsFavorite(web));

        vm.Remove(lab); // DC01 and WEB01 are inside it now
        Assert.Empty(vm.Data.Ui.FavoriteConnections);
        Assert.Empty(vm.FavoriteRoots);
    }

    [Fact]
    public void Opening_other_settings_and_coming_back_keeps_the_favourites()
    {
        var (vm, _, dc, _) = Tree();
        vm.SetFavorite(dc, true);
        var mine = vm.Data;
        var other = new AppData { Ui = mine.Ui }; // another database: the UI settings carry over (SettingsStore keepUi)
        other.Items.Add(new Connection { Name = "OTHER" });

        vm.ReplaceData(other, save: false);
        Assert.Empty(vm.FavoriteRoots);
        Assert.Equal([dc.Id], other.Ui.FavoriteConnections);

        vm.ReplaceData(mine, save: false);
        Assert.Equal(["DC01"], Assert.Single(vm.FavoriteRoots).Children.Select(l => l.Target.Name));
    }

    [Fact]
    public void Collapsing_the_group_is_remembered()
    {
        var (vm, _, dc, _) = Tree();
        vm.SetFavorite(dc, true);
        vm.FavoriteRoots[0].IsExpanded = false;
        Assert.True(vm.Data.Ui.FavoritesCollapsed);

        var again = new MainViewModel(vm.Data);
        Assert.False(again.FavoriteRoots[0].IsExpanded);
    }

    // ------------------------------------------------------------------ agent uninstall

    [Fact]
    public void Forgetting_the_agent_sends_the_connection_back_to_RDP_and_WinRM()
    {
        var (vm, lab, dc, _) = Tree();
        dc.Agent = new AgentDevice { Status = AgentStatus.Installed, Fingerprint = new string('A', 64) };
        dc.Options.UseAgent = true;
        dc.PsRemoting = new PsRemotingStatus { State = PsRemotingState.Available, ViaAgent = true };

        vm.ForgetAgent(dc);
        Assert.Null(dc.Agent);
        Assert.Null(dc.Options.UseAgent);
        Assert.Null(dc.PsRemoting);
        Assert.False(RemoteHub.Services.Inheritance.UsesAgent(dc, vm.Data));

        // A folder that says "use the agent" still says so: only the connection's own choice is reset.
        lab.Options.UseAgent = true;
        var other = new Connection { Name = "X", Options = { UseAgent = false } };
        vm.Add(other, lab);
        vm.ForgetAgent(other);
        Assert.False(other.Options.UseAgent);
    }

    [Theory]
    [InlineData("started", null)]
    [InlineData("notInstalled", "no RemoteHub Agent installed from its installer")]
    [InlineData("failed:9", "couldn't be started (failed:9)")]
    [InlineData("", "couldn't be started ()")]
    public void The_uninstall_answer_is_read_as_started_or_a_readable_failure(string answer, string? error)
    {
        if (error is null) { AgentInstaller.CheckUninstallAnswer(answer); return; }
        var ex = Assert.Throws<RemoteCommandException>(() => AgentInstaller.CheckUninstallAnswer(answer));
        Assert.Contains(error, ex.Message);
    }

    [Fact]
    public void The_uninstall_script_parses_and_runs_msiexec_detached()
    {
        Parser.ParseInput(AgentInstaller.DetachedUninstallScript, out _, out var errors);
        Assert.Empty(errors);
        Assert.Contains("msiexec.exe /x", AgentInstaller.DetachedUninstallScript);
        Assert.Contains("Win32_Process", AgentInstaller.DetachedUninstallScript); // outside the agent session it ends
        Assert.Contains("DisplayName -eq 'RemoteHub Agent'", AgentInstaller.DetachedUninstallScript);
        Assert.Contains(@"Temp\RemoteHub-Agent-uninstall.log", AgentInstaller.DetachedUninstallScript);
    }

    [Fact]
    public async Task An_agent_that_no_longer_answers_counts_as_stopped_after_the_quiet_time()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // nothing listens there any more: every health check fails at once
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var stopped = await AgentInstaller.WaitForStopAsync("127.0.0.1", new AgentEndpoint(port, new string('A', 64)),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3), _ => { }, CancellationToken.None);
        Assert.True(stopped);
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(3), $"stopped after {watch.Elapsed}, before the quiet time");
    }

    [Fact]
    public async Task The_wait_gives_up_without_a_stop_when_the_quiet_time_isnt_reached_in_time()
    {
        // The timeout ends before the quiet time could: the wait reports "still answering", never a stop it didn't see.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var stopped = await AgentInstaller.WaitForStopAsync("127.0.0.1", new AgentEndpoint(port, new string('A', 64)),
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(20), _ => { }, CancellationToken.None);
            Assert.False(stopped);
        }
        finally { listener.Stop(); }
    }
}
