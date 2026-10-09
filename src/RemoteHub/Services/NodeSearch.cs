using RemoteHub.Models;

namespace RemoteHub.Services;

/// <summary>One search result: a folder or connection plus its folder path.</summary>
/// <param name="Note">Shown first in the subtitle (e.g. when a connection was last used, in the "not used" filter).</param>
public sealed record SearchHit(NodeBase Node, string Path, int Score, string? Note = null)
{
    public bool IsFolder => Node is Folder;
    public bool IsConnection => Node is Connection;

    public string Subtitle => (Note is { Length: > 0 } ? Note + "  ·  " : "") + Node switch
    {
        Connection c => Path.Length > 0 ? $"{c.Address}  ·  {Path}" : c.Address,
        Folder f => $"Folder · {f.Descendants().OfType<Connection>().Count()} connection(s)" + (Path.Length > 0 ? $"  ·  {Path}" : ""),
        _ => Path,
    };
}

/// <summary>Searches folders and connections by name, host, notes and folder path. All words must match.</summary>
public static class NodeSearch
{
    public static string PathOf(NodeBase n) => string.Join(" › ", n.Ancestors().Reverse().Select(a => a.Name));

    /// <summary>
    /// Connections not used for <paramref name="days"/> days (oldest first), optionally narrowed by a search. Use is recorded
    /// since <paramref name="trackedSinceUtc"/>: a connection without a recorded use counts only when that is long enough ago –
    /// otherwise every connection would look unused right after the update.
    /// </summary>
    public static List<SearchHit> Unused(IEnumerable<NodeBase> nodes, int days, DateTime trackedSinceUtc, DateTime nowUtc, string query = "")
    {
        var all = nodes.ToList();
        var cutoff = nowUtc.AddDays(-days);
        var candidates = all.OfType<Connection>()
            .Where(c => c.LastConnectedUtc is { } last ? last < cutoff : trackedSinceUtc <= cutoff);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var matching = Find(all, query, int.MaxValue).Select(h => h.Node).ToHashSet();
            candidates = candidates.Where(matching.Contains);
        }
        return candidates
            .OrderBy(c => c.LastConnectedUtc ?? DateTime.MinValue)
            .Select(c => new SearchHit(c, PathOf(c), 0, c.LastConnectedUtc is { } last
                ? "last used " + Connection.When(last, nowUtc.ToLocalTime())
                : "not used since " + trackedSinceUtc.ToLocalTime().ToString("d MMM yyyy")))
            .ToList();
    }

    /// <summary>Connections without any recorded use, optionally narrowed by a search.</summary>
    public static List<SearchHit> NeverConnected(IEnumerable<NodeBase> nodes, DateTime trackedSinceUtc, string query = "")
    {
        var all = nodes.ToList();
        var candidates = all.OfType<Connection>().Where(c => c.LastConnectedUtc is null);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var matching = Find(all, query, int.MaxValue).Select(h => h.Node).ToHashSet();
            candidates = candidates.Where(matching.Contains);
        }
        var note = "not used since " + trackedSinceUtc.ToLocalTime().ToString("d MMM yyyy");
        return candidates.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Select(c => new SearchHit(c, PathOf(c), 0, note)).ToList();
    }

    public static List<SearchHit> Find(IEnumerable<NodeBase> nodes, string query, int max = 200)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return [];
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;

        var hits = new List<SearchHit>();
        foreach (var n in nodes)
        {
            var path = PathOf(n);
            var host = (n as Connection)?.Host ?? "";
            int score = 0;
            bool all = true;
            foreach (var t in terms)
            {
                int s = n.Name.Equals(t, ic) ? 100
                      : n.Name.StartsWith(t, ic) ? 60
                      : n.Name.Contains(t, ic) ? 40
                      : host.StartsWith(t, ic) ? 35
                      : host.Contains(t, ic) ? 30
                      : path.Contains(t, ic) ? 10
                      : (n.Notes?.Contains(t, ic) ?? false) ? 5
                      : 0;
                if (s == 0) { all = false; break; }
                score += s;
            }
            if (all) hits.Add(new SearchHit(n, path, score));
        }
        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.IsFolder ? 0 : 1)      // on equal score, folders first (they summarize their contents)
            .ThenBy(h => h.Node.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .ToList();
    }
}
