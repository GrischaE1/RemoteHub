using RemoteHub.Data;

namespace RemoteHub.Services;

/// <summary>
/// A script as the view, the run window and the favourites see it: one of the user's own (<see cref="Own"/>) or one of
/// the library's (<see cref="Library"/>). <see cref="Key"/> is the id used in favourites and the run history.
/// </summary>
public sealed record ScriptEntry(string Key, string Name, string Language, string? Color, string FolderPath, ScriptInfo? Own, LibraryScript? Library)
{
    public bool IsLibrary => Library is not null;
    public bool IsPowerShell => Language == ScriptStore.PowerShell;
    public string? RunContext => Own?.RunContext ?? Library?.Content.RunContext;
    public string? Risk => Own?.Risk ?? Library?.Content.Risk;
    public string? OsScope => Own?.OsScope ?? Library?.Content.OsScope;
    /// <summary>The catalog ID, for a library script or a copy of one.</summary>
    public string? CatalogId => Library?.Id ?? Own?.LibraryId;
    /// <summary>A library script is always deployed: its version is the RemoteHub release.</summary>
    public bool IsDeployed => IsLibrary || Own?.IsDeployed == true;
    public int? DeployedVersion => IsLibrary ? 1 : Own?.DeployedVersion;
    public bool HasDraftChanges => Own?.HasDraftChanges == true;
    /// <summary>Last changed, for sorting (library scripts: never).</summary>
    public DateTime ModifiedUtc => Own?.ModifiedUtc ?? DateTime.MinValue;

    /// <summary>What runs on a device: the latest deployed version (§6.4), or null when there is none.</summary>
    public ScriptContent? DeployedContent() => Library?.Content ?? (Own is { } s ? ScriptStore.Deployed(s.Id)?.Content : null);

    /// <summary>"Version 4" or "Library" for the summary and the run window.</summary>
    public string VersionLabel => IsLibrary ? $"Library ({AppVersion.Current})" : DeployedVersion is { } v ? $"Version {v}" : "Not deployed yet";
}

/// <summary>Lists scripts across the user's folders and the library (docs/scripts-view-design.md §4, §6.5).</summary>
public static class ScriptCatalog
{
    /// <summary>The library is part of what is listed: the policy AllowScriptLibrary (§9).</summary>
    public static bool LibraryAllowed => Features.IsAllowed(Feature.ScriptLibrary);

    /// <summary>Every script, the user's first (by folder path, then name), then the library's.</summary>
    public static IReadOnlyList<ScriptEntry> All(bool includeLibrary = true)
    {
        var folders = ScriptStore.Folders().ToDictionary(f => f.Id);
        var list = ScriptStore.Scripts().Select(s => Own(s, folders)).ToList();
        if (includeLibrary && LibraryAllowed) list.AddRange(ScriptLibrary.All.Select(FromLibrary));
        return list;
    }

    public static ScriptEntry? Find(string key)
    {
        if (ScriptLibrary.IsLibraryId(key))
            return LibraryAllowed && ScriptLibrary.Find(key[ScriptStore.LibraryPrefix.Length..]) is { } lib ? FromLibrary(lib) : null;
        return ScriptStore.Script(key) is { } s ? Own(s, ScriptStore.Folders().ToDictionary(f => f.Id)) : null;
    }

    /// <summary>The pinned scripts that exist, in pin order; ids that don't exist (any more, or blocked) are skipped, not dropped.</summary>
    public static IReadOnlyList<ScriptEntry> Favourites(IEnumerable<string> pinned)
    {
        var all = All().ToDictionary(e => e.Key);
        return pinned.Distinct().Where(all.ContainsKey).Select(k => all[k]).ToList();
    }

    public static ScriptEntry Own(ScriptInfo s, IReadOnlyDictionary<string, ScriptFolderInfo> folders) =>
        new(s.Id, s.Name, s.Language, s.Color ?? InheritedColor(s.FolderId, folders), FolderPath(s.FolderId, folders), s, null);

    public static ScriptEntry FromLibrary(LibraryScript l) =>
        new(l.ScriptId, l.Name, ScriptStore.PowerShell, ScriptLibrary.AreaColor(l.Area),
            "Library › " + (ScriptLibrary.Areas.FirstOrDefault(a => a.Key == l.Area).Name ?? l.Area), null, l);

    /// <summary>"Inventory › Disks" – the folder's path below My scripts.</summary>
    public static string FolderPath(string? folderId, IReadOnlyDictionary<string, ScriptFolderInfo> folders)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>();
        for (var f = folderId; f is not null && folders.TryGetValue(f, out var info) && seen.Add(f); f = info.ParentId) parts.Insert(0, info.Name);
        return string.Join(" › ", parts);
    }

    /// <summary>A script without a colour of its own takes its folder's (or that folder's parent's), as connections do.</summary>
    public static string? InheritedColor(string? folderId, IReadOnlyDictionary<string, ScriptFolderInfo> folders)
    {
        var seen = new HashSet<string>();
        for (var f = folderId; f is not null && folders.TryGetValue(f, out var info) && seen.Add(f); f = info.ParentId)
            if (info.Color is { } c && c != ColorTagsNone) return c;
        return null;
    }

    private const string ColorTagsNone = "None";

    /// <summary>
    /// Scripts matching a search: the user's through the full-text index, the library's by name, id, description or text
    /// containing every word.
    /// </summary>
    public static IReadOnlyList<ScriptEntry> Search(string text, bool includeLibrary = true)
    {
        var folders = ScriptStore.Folders().ToDictionary(f => f.Id);
        var list = ScriptStore.Search(text).Select(h => Own(h.Script, folders)).ToList();
        if (includeLibrary && LibraryAllowed)
        {
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            list.AddRange(ScriptLibrary.All.Where(l => words.All(w =>
                    l.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase) || l.Id.Contains(w, StringComparison.OrdinalIgnoreCase)
                    || l.Content.Description.Contains(w, StringComparison.CurrentCultureIgnoreCase) || l.Content.Body.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .Select(FromLibrary));
        }
        return list;
    }
}
