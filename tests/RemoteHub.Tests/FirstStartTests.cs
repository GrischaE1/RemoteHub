using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>What the first start shows: the welcome tour after a new install, What's new after an upgrade, then nothing.</summary>
public class FirstStartTests
{
    private static AppVersion V(string v) => AppVersion.Parse(v)!;

    private static string Newest => FirstStart.Entries.Select(e => e.ParsedVersion).Max()!.ToString();

    [Fact]
    public void A_new_install_gets_the_welcome_tour_and_an_upgrade_gets_whats_new()
    {
        Assert.Equal(FirstStartKind.Welcome, FirstStart.Decide(null, hasConnections: false, V(Newest)));
        // Settings from a version before What's new existed: an upgrade.
        Assert.Equal(FirstStartKind.WhatsNew, FirstStart.Decide(null, hasConnections: true, V(Newest)));
        Assert.Equal(FirstStartKind.WhatsNew, FirstStart.Decide("0.20.2", hasConnections: true, V(Newest)));
        Assert.Equal(FirstStartKind.WhatsNew, FirstStart.Decide("0.20.2", hasConnections: false, V(Newest)));
    }

    [Fact]
    public void Each_is_shown_only_once()
    {
        Assert.Equal(FirstStartKind.None, FirstStart.Decide(Newest, hasConnections: true, V(Newest)));
        Assert.Equal(FirstStartKind.None, FirstStart.Decide(Newest, hasConnections: false, V(Newest)));
        // An older version started after a newer one shows nothing either.
        Assert.Equal(FirstStartKind.None, FirstStart.Decide("99.0.0", hasConnections: true, V(Newest)));
    }

    [Fact]
    public void An_upgrade_without_highlights_shows_nothing()
    {
        var newest = V(Newest);
        var patch = V($"{newest.Major}.{newest.Minor}.{newest.Patch + 1}");
        Assert.Empty(FirstStart.Since(newest, patch));
        Assert.Equal(FirstStartKind.None, FirstStart.Decide(newest.ToString(), hasConnections: true, patch));
    }

    [Fact]
    public void Whats_new_lists_every_version_since_the_last_one_newest_first()
    {
        var all = FirstStart.Since(V("0.0.1"), V("99.0.0"));
        Assert.Equal(FirstStart.Entries.Length, all.Count);
        Assert.Equal(all.OrderByDescending(e => e.ParsedVersion), all);
        // Without a known last version, only the newest entry up to this version.
        Assert.Equal([Newest], FirstStart.Since(null, V("99.0.0")).Select(e => e.Version));
        Assert.Empty(FirstStart.Since(null, V("0.0.1")));
    }

    [Fact]
    public void Every_entry_has_a_version_and_highlights()
    {
        Assert.NotEmpty(FirstStart.Entries);
        foreach (var e in FirstStart.Entries)
        {
            Assert.NotNull(AppVersion.Parse(e.Version));
            Assert.NotEmpty(e.Highlights);
            Assert.All(e.Highlights, h => Assert.False(string.IsNullOrWhiteSpace(h)));
        }
        Assert.Equal(FirstStart.Entries.Length, FirstStart.Entries.Select(e => e.ParsedVersion).Distinct().Count());
    }
}
