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
    [ObservableProperty] private bool _isSelected;
}

/// <summary>A pinned connection in the Favourites group: opening or editing it acts on the connection itself.</summary>
public sealed partial class FavoriteLink(Connection target) : ObservableObject
{
    public Connection Target { get; } = target;
    /// <summary>Where the connection lives, for the tooltip ("Production › Web").</summary>
    public string Location => string.Join(" › ", Target.Ancestors().Reverse().Select(a => a.Name)) is { Length: > 0 } path ? path : "Top level";
    [ObservableProperty] private bool _isSelected;
    public bool IsExpanded { get; set; }
}

public static class Favorites
{
    /// <summary>
    /// The pinned connections in pin order. Ids of connections that no longer exist are dropped from
    /// <paramref name="ids"/>; true when that changed the list.
    /// </summary>
    public static (List<Connection> Pinned, bool Changed) Resolve(IEnumerable<Connection> connections, List<Guid> ids)
    {
        var byId = new Dictionary<Guid, Connection>();
        foreach (var c in connections) byId.TryAdd(c.Id, c);
        var pinned = new List<Connection>();
        var seen = new HashSet<Guid>();
        var before = ids.Count;
        ids.RemoveAll(id => !byId.ContainsKey(id) || !seen.Add(id));
        pinned.AddRange(ids.Select(id => byId[id]));
        return (pinned, ids.Count != before);
    }
}
