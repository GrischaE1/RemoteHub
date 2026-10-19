using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        Assert.Equal(["C", "A"], Favorites.Resolve([a, b, c], [c.Id, a.Id, c.Id]).Select(x => x.Name));
    }

    [Fact]
    public void Ids_of_another_settings_database_are_kept_for_when_it_is_opened_again()
    {
        var a = new Connection { Name = "A" };
        var elsewhere = Guid.NewGuid();
        var ids = new List<Guid> { elsewhere, a.Id };
        Assert.Equal(["A"], Favorites.Resolve([a], ids).Select(x => x.Name));
        Assert.Equal([elsewhere, a.Id], ids);
    }

    [Fact]
    public void Deleting_a_connection_or_its_folder_unpins_it()
    {
        var folder = new Folder { Name = "Lab" };
        var inside = new Connection { Name = "DC01", Parent = folder };
        folder.Children.Add(inside);
        var other = new Connection { Name = "WEB01" };
        var ids = new List<Guid> { inside.Id, other.Id };
        Assert.True(Favorites.Forget(ids, folder.Descendants().Prepend(folder).OfType<Connection>()));
        Assert.Equal([other.Id], ids);
        Assert.False(Favorites.Forget(ids, [inside]));
    }

    [Fact]
    public void The_group_is_updated_in_place_so_links_keep_their_selection()
    {
        var a = new Connection { Name = "A" };
        var b = new Connection { Name = "B" };
        var c = new Connection { Name = "C" };
        var links = new ObservableCollection<FavoriteLink>();
        Favorites.Sync(links, [a, b]);
        var keptB = links[1];
        keptB.IsSelected = true;

        Favorites.Sync(links, [c, b]);
        Assert.Equal(["C", "B"], links.Select(l => l.Target.Name));
        Assert.Same(keptB, links[1]);
        Assert.True(links[1].IsSelected);

        Favorites.Sync(links, [b, c]);
        Assert.Equal(["B", "C"], links.Select(l => l.Target.Name));
        Assert.Same(keptB, links[0]);
    }

    [Fact]
    public void Older_settings_without_favourites_read_as_none()
    {
        var ui = System.Text.Json.JsonSerializer.Deserialize<UiState>("""{"Theme":"Dark"}""")!;
        Assert.Empty(ui.FavoriteConnections);
        Assert.False(ui.FavoritesCollapsed);
    }
}
