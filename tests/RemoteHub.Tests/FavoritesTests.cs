using System.Collections.Generic;
using RemoteHub.Models;

namespace RemoteHub.Tests;

/// <summary>Favourites: links to pinned connections, kept per user in the UI state.</summary>
public class FavoritesTests
{
    [Fact]
    public void Pinned_connections_come_back_in_pin_order()
    {
        var a = new Connection { Name = "A" };
        var b = new Connection { Name = "B" };
        var c = new Connection { Name = "C" };
        var ids = new List<Guid> { c.Id, a.Id };
        var (pinned, changed) = Favorites.Resolve([a, b, c], ids);
        Assert.Equal(["C", "A"], pinned.Select(x => x.Name));
        Assert.False(changed);
    }

    [Fact]
    public void A_deleted_connection_loses_its_link_and_duplicates_are_dropped()
    {
        var a = new Connection { Name = "A" };
        var gone = Guid.NewGuid();
        var ids = new List<Guid> { gone, a.Id, a.Id };
        var (pinned, changed) = Favorites.Resolve([a], ids);
        Assert.Equal(["A"], pinned.Select(x => x.Name));
        Assert.Equal([a.Id], ids);
        Assert.True(changed);
    }

    [Fact]
    public void A_link_says_where_the_connection_lives()
    {
        var prod = new Folder { Name = "Production" };
        var web = new Folder { Name = "Web", Parent = prod };
        var c = new Connection { Name = "WEB01", Parent = web };
        Assert.Equal("Production › Web", new FavoriteLink(c).Location);
        Assert.Equal("Top level", new FavoriteLink(new Connection { Name = "X" }).Location);
    }

    [Fact]
    public void Older_settings_without_favourites_read_as_none()
    {
        var ui = System.Text.Json.JsonSerializer.Deserialize<UiState>("""{"Theme":"Dark"}""")!;
        Assert.Empty(ui.FavoriteConnections);
        Assert.False(ui.FavoritesCollapsed);
    }
}
