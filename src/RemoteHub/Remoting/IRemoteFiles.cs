using RemoteHub.Rdp;

namespace RemoteHub.Remoting;

/// <summary>
/// The remote side of a file tab: a computer over PowerShell remoting (<see cref="FileBrowserSession"/>) or a network
/// share (<see cref="ShareSession"/>). The tab's layout, menus and drag &amp; drop are the same for both.
/// </summary>
public interface IRemoteFiles
{
    SessionBase Session { get; }
    /// <summary>Identifies the tab in drag &amp; drop data (only plain text crosses the drag boundary).</summary>
    string DragId { get; }
    /// <summary>Title and icon of the remote pane.</summary>
    string RemoteTitle { get; }
    string RemoteSymbol { get; }
    /// <summary>What the tab is, in its header ("File transfer", "Network share").</summary>
    string KindText { get; }
    /// <summary>Where it is, in its header (host, or the share's address).</summary>
    string WhereText { get; }
    /// <summary>Folder shown first, and where to go if that fails.</summary>
    string? StartRemotePath { get; }
    string FallbackPath { get; }
    string PathHint { get; }

    string ParentOf(string path);
    string Join(string folder, string name);

    Task<Listing> ListAsync(string path);
    Task<List<string>> ExistingAsync(string folder, IEnumerable<string> names);
    Task<string> MkdirAsync(string path);
    Task DeleteAsync(IEnumerable<string> paths);
    Task<string> RenameAsync(string path, string newName);
    Task<List<(long Size, string FullPath, string Relative)>> ExpandAsync(IEnumerable<string> paths);

    /// <summary>The remote side is ready (raised on the UI thread after connecting).</summary>
    event Action? Ready;
    event Action? FocusRequested;
}

/// <summary>Open file tabs by their drag id (weak: a closed tab is gone).</summary>
public static class RemoteFilesRegistry
{
    private static readonly Dictionary<string, WeakReference<IRemoteFiles>> Live = new();
    public static void Add(IRemoteFiles f) => Live[f.DragId] = new WeakReference<IRemoteFiles>(f);
    public static void Remove(IRemoteFiles f) => Live.Remove(f.DragId);
    public static IRemoteFiles? Find(string id) => Live.TryGetValue(id, out var w) && w.TryGetTarget(out var f) ? f : null;
}
