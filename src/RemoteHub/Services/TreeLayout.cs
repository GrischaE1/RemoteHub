using System.Collections.ObjectModel;
using RemoteHub.Models;

namespace RemoteHub.Services;

/// <summary>
/// Items under a connection – reached through it as a jump host – and how they are saved: next to their jump host, with
/// its id (<see cref="NodeBase.JumpHostId"/>), so a RemoteHub version without jump hosts still shows them (as ordinary
/// items) instead of losing them. Loading puts them back under the jump host.
/// </summary>
public static class TreeLayout
{
    /// <summary>The saved order of one level: each item, then the items under it if it is a jump host.</summary>
    public static IList<NodeBase> Flatten(IEnumerable<NodeBase> items)
    {
        var list = new List<NodeBase>();
        foreach (var n in items)
        {
            list.Add(n);
            if (n is Connection c) list.AddRange(c.Children);
        }
        return list;
    }

    /// <summary>
    /// After loading: moves saved items back under their jump host (on the same level, one hop only), on every level.
    /// An item whose jump host is missing stays where it is – reached directly.
    /// </summary>
    public static void Nest(IList<NodeBase> items)
    {
        var hosts = items.OfType<Connection>().Where(c => c.JumpHostId is null).ToDictionary(c => c.Id);
        foreach (var n in items.ToList())
        {
            if (n.JumpHostId is { } id && id != n.Id && hosts.TryGetValue(id, out var host) && n is Connection or Folder or NetworkShare
                && !n.Descendants().Any(d => d.JumpHostId is not null))
            {
                items.Remove(n);
                host.Children.Add(n);
            }
            n.ForgetStoredJumpHost();
        }
        foreach (var n in items)
        {
            if (n is Folder f) Nest(f.Children);
            if (n is Connection c) foreach (var sub in c.Children.OfType<Folder>()) NestBelowJumpHost(sub.Children);
        }
    }

    /// <summary>Below a jump host there are no jump hosts: saved ids there are forgotten.</summary>
    private static void NestBelowJumpHost(IList<NodeBase> items)
    {
        foreach (var n in items)
        {
            n.ForgetStoredJumpHost();
            if (n is Folder f) NestBelowJumpHost(f.Children);
        }
    }

    /// <summary>The list an item is in (its folder's, its jump host's, or the top level).</summary>
    public static ObservableCollection<NodeBase> ListOf(NodeBase? container, ObservableCollection<NodeBase> topLevel) =>
        container?.Contents ?? topLevel;

    /// <summary>
    /// Whether <paramref name="node"/> may go into <paramref name="container"/> (null = top level), and if not, why – in
    /// words for a tooltip. One hop: nothing below a jump host is a jump host itself.
    /// </summary>
    public static string? WhyNot(NodeBase node, NodeBase? container)
    {
        if (container is null) return null;
        if (node == container || Containers(container).Contains(node)) return "An item can't be moved into itself.";
        if (container is NetworkShare) return "Only connections and folders can be placed here.";
        if (container is Connection target)
        {
            if (node is not (Connection or Folder or NetworkShare { Server: null }))
                return "Only connections, folders and network shares can be placed under a connection.";
            if (target.JumpHost is { } outer)
                return $"{target.Name} is reached through {outer.Name}. Only one jump host is supported for now.";
        }
        if (container.JumpHost is not null || container is Connection)
        {
            if (node is Connection { Children.Count: > 0 } j)
                return $"{j.Name} is a jump host itself. Only one jump host is supported for now.";
            if (node is Folder && node.Descendants().OfType<Connection>().FirstOrDefault(c => c.Children.Count > 0) is { } inner)
                return $"{inner.Name} in this folder is a jump host itself. Only one jump host is supported for now.";
        }
        return null;
    }

    /// <summary><paramref name="container"/>, or the nearest container above it where <paramref name="node"/> may go.</summary>
    public static NodeBase? Allowed(NodeBase node, NodeBase? container)
    {
        while (container is not null && WhyNot(node, container) is not null) container = container.Parent;
        return container;
    }

    /// <summary>All containers <paramref name="n"/> is in (folders and jump hosts), nearest first.</summary>
    public static IEnumerable<NodeBase> Containers(NodeBase n)
    {
        for (var p = n.Parent; p is not null; p = p.Parent) yield return p;
    }
}
