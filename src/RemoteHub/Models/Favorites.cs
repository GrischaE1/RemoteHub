using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteHub.Models;

/// <summary>
/// The Favourites group at the top of the navigation. It holds links to pinned connections; the connections themselves
/// stay in their folders, so what they inherit doesn't change. Not part of the saved tree: it is built from
/// <see cref="UiState.FavoriteConnections"/>.
/// </summary>
public sealed partial class FavoritesGroup : ObservableObject
{
    public ObservableCollection<FavoriteLink> Children { get; } = [];
    [ObservableProperty] private bool _isExpanded = true;
    /// <summary>What a screen reader (and UI Automation) calls the group.</summary>
    public override string ToString() => "Favourites";
    [ObservableProperty] private bool _isSelected;
}

/// <summary>A pinned connection in the Favourites group: opening or editing it acts on the connection itself.</summary>
public sealed partial class FavoriteLink(Connection target) : ObservableObject
{
    public Connection Target { get; } = target;
    [ObservableProperty] private bool _isSelected;
    public bool IsExpanded { get; set; }
    /// <summary>What a screen reader (and UI Automation) calls the link: the connection, marked as a favourite.</summary>
    public override string ToString() => $"{Target.Name} (favourite)";
}

public static class Favorites
{
    /// <summary>
    /// The pinned connections that exist in this tree, in pin order. Ids it doesn't have are kept, not dropped: the
    /// UI settings stay when another settings database is opened (a shared folder, a backup), and switching back
    /// finds the favourites again. An id goes only when its connection is deleted (<see cref="Forget"/>).
    /// </summary>
    public static List<Connection> Resolve(IEnumerable<Connection> connections, IEnumerable<Guid> ids)
    {
        var byId = new Dictionary<Guid, Connection>();
        foreach (var c in connections) byId.TryAdd(c.Id, c);
        var seen = new HashSet<Guid>();
        return ids.Where(id => seen.Add(id) && byId.ContainsKey(id)).Select(id => byId[id]).ToList();
    }

    /// <summary>Unpins deleted connections (a deleted folder's too). True when the list changed.</summary>
    public static bool Forget(List<Guid> ids, IEnumerable<Connection> deleted)
    {
        var gone = deleted.Select(c => c.Id).ToHashSet();
        return ids.RemoveAll(gone.Contains) > 0;
    }

    /// <summary>
    /// Brings <paramref name="links"/> in line with <paramref name="pinned"/> in place: links that stay keep their
    /// selection, new ones are inserted where they belong, unpinned ones are removed.
    /// </summary>
    public static void Sync(ObservableCollection<FavoriteLink> links, IReadOnlyList<Connection> pinned)
    {
        for (var i = links.Count - 1; i >= 0; i--)
            if (!pinned.Contains(links[i].Target)) links.RemoveAt(i);
        for (var i = 0; i < pinned.Count; i++)
        {
            var at = -1;
            for (var j = i; j < links.Count; j++) if (links[j].Target == pinned[i]) { at = j; break; }
            if (at == i) continue;
            if (at > i) links.Move(at, i);
            else links.Insert(i, new FavoriteLink(pinned[i]));
        }
    }
}
