using System.Collections.Generic;
using RemoteHub.Agent;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Remoting.Shares;
using RemoteHub.Services;
using SessionState = RemoteHub.Rdp.SessionState;

namespace RemoteHub.Tests;

/// <summary>Tests that set this PC's policy for the whole process (AppPolicy.Source) run alone, not next to other tests.</summary>
[CollectionDefinition("Policies", DisableParallelization = true)]
public sealed class PoliciesCollection;

/// <summary>
/// The console's feature switches (docs/admx-design.md §5): main switches, the switch per transport for each tool, share
/// protocols, Settings → Policies, and the refusal where a session starts.
/// </summary>
[Collection("Policies")]
public class ConsoleFeatureTests
{
    private sealed class FakeSource(Dictionary<string, int> values) : IPolicySource
    {
        public object? Read(PolicyScope scope, string key, string name) =>
            scope == PolicyScope.Computer && key.Equals(PolicyCatalog.Root, StringComparison.OrdinalIgnoreCase) && values.TryGetValue(name, out var v) ? v : null;
    }

    private static FakeSource Set(params (string Name, int Value)[] values) => new(values.ToDictionary(v => v.Name, v => v.Value));

    /// <summary>Runs <paramref name="test"/> with these values as this PC's policy (AppPolicy reads them), then restores the registry.</summary>
    private static void WithPolicy(FakeSource source, Action test)
    {
        AppPolicy.Source = source;
        try { test(); }
        finally { AppPolicy.Source = RegistryPolicySource.Instance; }
    }

    [Fact]
    public void Every_feature_has_its_catalogue_value_and_every_feature_value_a_feature()
    {
        var switched = Features.All.Select(f => f.Entry).ToHashSet();
        Assert.Equal(Enum.GetValues<Feature>().Length, Features.All.Count());
        Assert.Empty(PolicyCatalog.FeatureEntries.Where(e => !switched.Contains(e)).Select(e => e.Name));
        Assert.All(switched, e => Assert.Contains(e, PolicyCatalog.ConsoleEntries));
    }

    [Fact]
    public void Nothing_set_allows_everything()
    {
        var none = Set();
        Assert.All(Enum.GetValues<Feature>(), f => Assert.True(Features.IsAllowed(f, none), f.ToString()));
        Assert.All(Enum.GetValues<Feature>(), f => Assert.Null(Features.WhyNot(f, none)));
    }

    [Fact]
    public void A_feature_set_to_0_is_blocked_and_1_allows_it()
    {
        Assert.False(Features.IsAllowed(Feature.Rdp, Set(("AllowRdp", 0))));
        Assert.True(Features.IsAllowed(Feature.Rdp, Set(("AllowRdp", 1))));
        Assert.True(Features.IsAllowed(Feature.Rdp, Set(("AllowRdp", 7))), "an invalid value counts as not set");
        Assert.False(Features.IsAllowed(Feature.QuickConnect, Set(("AllowQuickConnect", 0))));
        Assert.True(Features.IsAllowed(Feature.AgentDesktop, Set(("AllowRdp", 0))), "one switch doesn't touch another");
    }

    [Fact]
    public void The_existing_Disable_values_keep_their_meaning()
    {
        Assert.False(Features.IsAllowed(Feature.Recording, Set(("DisableRecording", 1))));
        Assert.True(Features.IsAllowed(Feature.Recording, Set(("DisableRecording", 0))));
        Assert.False(Features.IsAllowed(Feature.AgentAutoSignIn, Set(("DisableAgentAutoSignIn", 1))));
    }

    [Fact]
    public void Without_the_agent_every_agent_feature_is_blocked_even_when_allowed_on_its_own()
    {
        var src = Set(("AllowAgent", 0), ("AllowAgentDesktop", 1), ("AllowAgentPowerShell", 1), ("AllowJumpHosts", 1));
        foreach (var f in new[] { Feature.AgentDesktop, Feature.AgentPowerShell, Feature.AgentFileTransfer, Feature.AgentRegistry, Feature.AgentServices,
                     Feature.AgentProcesses, Feature.AgentInstall, Feature.CredentialProviderInstall, Feature.AgentAutoSignIn, Feature.JumpHosts })
        {
            Assert.False(Features.IsAllowed(f, src), f.ToString());
            Assert.Equal("The RemoteHub Agent is turned off by your organisation – see Settings → Policies.", Features.WhyNot(f, src));
        }
        Assert.All(Enum.GetValues<RemoteTool>(), t => Assert.True(Features.IsAllowed(t, viaAgent: false, src), t + " over WinRM"));
        Assert.True(Features.IsAllowed(Feature.Rdp, src));
    }

    [Fact]
    public void Without_PowerShell_remoting_every_WinRM_tool_and_the_setup_are_blocked_but_not_the_agent()
    {
        var src = Set(("AllowPowerShellRemoting", 0), ("AllowRemotingFileTransfer", 1));
        Assert.All(Enum.GetValues<RemoteTool>(), t => Assert.False(Features.IsAllowed(t, viaAgent: false, src), t + " over WinRM"));
        Assert.False(Features.IsAllowed(Feature.RemotingSetup, src));
        Assert.False(Features.AnyTool(viaAgent: false, src));
        Assert.All(Enum.GetValues<RemoteTool>(), t => Assert.True(Features.IsAllowed(t, viaAgent: true, src), t + " through the agent"));
        Assert.True(Features.AnyTool(viaAgent: true, src));
    }

    [Fact]
    public void A_tool_blocked_for_one_transport_still_works_over_the_other()
    {
        var src = Set(("AllowAgentFileTransfer", 0));
        Assert.False(Features.IsAllowed(RemoteTool.FileTransfer, viaAgent: true, src));
        Assert.True(Features.IsAllowed(RemoteTool.FileTransfer, viaAgent: false, src));
        Assert.True(Features.IsAllowed(RemoteTool.PowerShell, viaAgent: true, src));
        Assert.Equal("File transfer through the RemoteHub Agent is turned off by your organisation – see Settings → Policies.",
            Features.WhyNot(Features.For(RemoteTool.FileTransfer, viaAgent: true), src));
    }

    [Fact]
    public void Network_shares_off_wins_over_each_protocol_and_a_protocol_blocks_only_itself()
    {
        var off = Set(("AllowNetworkShares", 0), ("AllowSftp", 1));
        Assert.All(Enum.GetValues<ShareProtocol>(), p => Assert.False(Features.IsAllowed(p, off), p.ToString()));

        var ftpOff = Set(("AllowFtp", 0));
        Assert.False(Features.IsAllowed(ShareProtocol.Ftp, ftpOff));
        Assert.True(Features.IsAllowed(ShareProtocol.Smb, ftpOff));
        Assert.True(Features.IsAllowed(Feature.NetworkShares, ftpOff));
    }

    [Fact]
    public void The_policies_page_marks_a_value_its_main_switch_turns_off()
    {
        var report = PolicyReport.Build(agentInstalled: false, Set(("AllowAgent", 0), ("AllowAgentDesktop", 1), ("AllowRdp", 0)));
        var desktop = report.Console.Single(r => r.Registry.EndsWith(@"\AllowAgentDesktop"));
        Assert.Equal("Off because \"Allow the RemoteHub Agent\" is off", desktop.OffBecause);
        Assert.Equal(("Allowed", "RemoteHub Agent"), (desktop.Value, desktop.Group));
        Assert.Null(report.Console.Single(r => r.Registry.EndsWith(@"\AllowAgent")).OffBecause);
        Assert.Equal("Blocked", report.Console.Single(r => r.Registry.EndsWith(@"\AllowRdp")).Value);
        Assert.Contains("Off because \"Allow the RemoteHub Agent\" is off", report.ToText());
    }

    [Fact]
    public void Connections_set_to_the_agent_use_WinRM_while_the_agent_is_blocked()
    {
        var data = new AppData();
        var c = new Connection { Name = "srv", Host = "srv", Options = new RdpOptions { UseAgent = true } };
        Assert.True(Inheritance.UsesAgent(c, data));
        WithPolicy(Set(("AllowAgent", 0)), () => Assert.False(Inheritance.UsesAgent(c, data)));
    }

    [Fact]
    public void A_tool_tab_refuses_to_connect_over_a_blocked_transport_and_says_why()
    {
        var target = new PsTarget("srv", false, new CredentialSet());
        WithPolicy(Set(("AllowRemotingRegistry", 0)), () =>
        {
            var s = new RegistrySession(new Connection { Name = "srv", Host = "srv" }, null);
            s.Connect(target, "the test", System.Windows.Media.Brushes.Gray);
            Assert.Equal(SessionState.Failed, s.State);
            Assert.Equal("The registry over WinRM is turned off by your organisation – see Settings → Policies.", s.StatusText);
        });
    }

    [Fact]
    public void A_blocked_share_protocol_is_refused_before_anything_is_opened()
    {
        var share = new NetworkShare { Name = "data", Protocol = ShareProtocol.WebDav, Host = "files.example" };
        WithPolicy(Set(("AllowWebDav", 0)), () =>
        {
            var ex = Assert.Throws<ShareException>(() => ShareConnector.Open(share, new CredentialSet(), _ => false, _ => { }));
            Assert.Equal("WebDAV shares are turned off by your organisation – see Settings → Policies.", ex.Message);
        });
    }
}
