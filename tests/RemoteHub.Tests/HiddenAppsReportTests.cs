extern alias agent;
using System.Collections.Generic;
using System.Text.Json;
using RemoteHub.Agent;
using RemoteHub.Views;
using Helper = agent::RemoteHub.Agent.DesktopHelperSession;

namespace RemoteHub.Tests;

/// <summary>
/// Hidden apps as they are reported: the user's choice and the policy's HiddenApps counted apart (D-HIDE-03), and what
/// the viewer makes of them, also with an agent or a RemoteHub that doesn't know the policy's count.
/// </summary>
public class HiddenAppsReportTests
{
    private const string Vault = @"c:\apps\vault.exe", Notes = @"c:\apps\notes.exe";

    [Fact]
    public void Apps_only_the_policy_hides_are_not_counted_as_the_users()
    {
        Assert.Equal((0, 1), Helper.HiddenCounts(new HashSet<string>(), new HashSet<string> { Vault }));
        Assert.Equal((1, 1), Helper.HiddenCounts(new HashSet<string> { Notes }, new HashSet<string> { Vault }));
        Assert.Equal((0, 0), Helper.HiddenCounts(new HashSet<string>(), new HashSet<string>()));
    }

    [Fact]
    public void An_app_the_user_picked_that_the_policy_hides_too_counts_as_the_policys()
    {
        // E.g. kept from an earlier helper before the policy began to hide it.
        Assert.Equal((1, 1), Helper.HiddenCounts(new HashSet<string> { Vault, Notes }, new HashSet<string> { Vault }));
    }

    [Fact]
    public void The_viewer_names_the_user_or_the_policy_only_when_every_hidden_app_is_theirs()
    {
        var nothing = DesktopPrivacy.Nothing;
        Assert.Equal("Hidden by the user", AgentDesktopView.RegionLabel(nothing with { HiddenApps = 2 }));
        Assert.Equal("Hidden by IT policy", AgentDesktopView.RegionLabel(nothing with { PolicyHiddenApps = 1 }));
        Assert.Equal("Hidden", AgentDesktopView.RegionLabel(nothing with { HiddenApps = 1, PolicyHiddenApps = 1 }));

        Assert.Equal("", AgentDesktopView.HiddenAppsText(nothing));
        Assert.Equal("2 apps hidden by the user", AgentDesktopView.HiddenAppsText(nothing with { HiddenApps = 2 }));
        Assert.Equal("1 app hidden by IT policy", AgentDesktopView.HiddenAppsText(nothing with { PolicyHiddenApps = 1 }));
        Assert.Equal("1 app hidden by the user · 3 apps hidden by IT policy", AgentDesktopView.HiddenAppsText(nothing with { HiddenApps = 1, PolicyHiddenApps = 3 }));
    }

    [Fact]
    public void Privacy_messages_with_and_without_the_policys_count_still_read()
    {
        // From an older agent: no policyHiddenApps, so nothing is put down to the policy.
        var old = JsonSerializer.Deserialize("""{"user":"None","screen":"Off","localInputBlocked":false,"reason":"","hiddenApps":2}""", DesktopJson.Default.DesktopPrivacy)!;
        Assert.Equal((2, 0), (old.HiddenApps, old.PolicyHiddenApps));
        Assert.Equal("Hidden by the user", AgentDesktopView.RegionLabel(old));

        // To an older RemoteHub: the new field is just one more camelCase property, and unknown properties are skipped.
        var json = JsonSerializer.Serialize(DesktopPrivacy.Nothing with { PolicyHiddenApps = 1 }, DesktopJson.Default.DesktopPrivacy);
        Assert.Contains("\"hiddenApps\":0", json);
        Assert.Contains("\"policyHiddenApps\":1", json);
        var withExtra = JsonSerializer.Deserialize(json.Replace("\"policyHiddenApps\"", "\"somethingNewer\""), DesktopJson.Default.DesktopPrivacy)!;
        Assert.Equal(0, withExtra.HiddenApps);
    }
}
