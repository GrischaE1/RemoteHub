using RemoteHub.Models;

namespace RemoteHub.Services;

/// <summary>One search result: a folder or connection plus its folder path.</summary>
public sealed record SearchHit(NodeBase Node, string Path, int Score)
{
    public bool IsFolder => Node is Folder;
    public bool IsConnection => Node is Connection;

    public string Subtitle => Node switch
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
