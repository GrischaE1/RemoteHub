using RemoteHub.Models;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>The last-connection label in the navigation and the "not used for" filter.</summary>
public class ConnectionUsageTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 15, 0, 0, DateTimeKind.Local);

    [Fact]
    public void The_last_connection_reads_naturally()
    {
        Assert.Equal("just now", Connection.When(Now.AddSeconds(-20).ToUniversalTime(), Now));
        Assert.StartsWith("today ", Connection.When(Now.AddHours(-2).ToUniversalTime(), Now));
        Assert.StartsWith("yesterday ", Connection.When(Now.AddDays(-1).ToUniversalTime(), Now));
        Assert.DoesNotContain("2026", Connection.When(Now.AddDays(-40).ToUniversalTime(), Now));
        Assert.Contains("2025", Connection.When(Now.AddYears(-1).ToUniversalTime(), Now));
        Assert.Equal("0.13.0", Connection.ShortVersion("0.13.0+4dd1aa1"));
    }

    [Fact]
    public void The_label_shows_the_last_use_and_the_tooltip_the_agent_version()
    {
        var c = new Connection { Host = "pc1", LastConnectedUtc = DateTime.UtcNow, Agent = new AgentDevice { Status = AgentStatus.Installed, Version = "0.13.0+abc" }, AgentMode = AgentMode.Active };
        Assert.Equal("just now", c.LastUsedText);
        Assert.StartsWith("RemoteHub Agent 0.13.0: active", c.AgentToolTip);
        var never = new Connection { Host = "pc2" };
        Assert.Equal("never connected", never.LastUsedText);
        Assert.True(never.NeverConnected);
        Assert.StartsWith("No RemoteHub Agent", never.AgentToolTip);
    }

    [Fact]
    public void Never_connected_connections_are_found()
    {
        var used = new Connection { Name = "used", Host = "a", LastConnectedUtc = DateTime.UtcNow.AddDays(-1) };
        var b = new Connection { Name = "b", Host = "b" };
        var a = new Connection { Name = "a", Host = "c" };
        Assert.Equal(["a", "b"], NodeSearch.NeverConnected([used, b, a], DateTime.UtcNow.AddDays(-3)).Select(h => h.Node.Name));
    }

    [Fact]
    public void Unused_connections_are_found_oldest_first()
    {
        var now = DateTime.UtcNow;
        var folder = new Folder { Name = "Lab" };
        var recent = new Connection { Name = "recent", Host = "a", LastConnectedUtc = now.AddDays(-5) };
        var old = new Connection { Name = "old", Host = "b", LastConnectedUtc = now.AddDays(-100) };
        var older = new Connection { Name = "older", Host = "c", LastConnectedUtc = now.AddDays(-200) };
        var never = new Connection { Name = "never", Host = "d" };
        foreach (var c in new[] { recent, old, older, never }) { folder.Children.Add(c); c.Parent = folder; }
        NodeBase[] nodes = [folder, .. folder.Children];

        // Use recorded for a year: a connection without a recorded use counts.
        var hits = NodeSearch.Unused(nodes, 90, now.AddDays(-365), now);
        Assert.Equal(["never", "older", "old"], hits.Select(h => h.Node.Name));
        Assert.StartsWith("not used since", hits[0].Subtitle);
        Assert.StartsWith("last used", hits[1].Subtitle);

        // Recorded only for a week: without a recorded use it isn't "unused for 90 days" (yet).
        Assert.Equal(["older", "old"], NodeSearch.Unused(nodes, 90, now.AddDays(-7), now).Select(h => h.Node.Name));

        // With a search on top.
        Assert.Equal(["older"], NodeSearch.Unused(nodes, 90, now.AddDays(-365), now, "older").Select(h => h.Node.Name));
    }
}
