using RemoteHub.Models;

namespace RemoteHub.Remoting;

/// <summary>
/// File transfer tab: this PC on one side, the remote computer (over PowerShell remoting) on the other.
/// One remote runspace is used for browsing; transfers run over their own sessions so browsing stays responsive.
/// </summary>
public sealed class FileBrowserSession : RemoteToolSession
{
    public FileBrowserSession(Connection connection, string? startRemotePath) : base(connection)
    {
        StartRemotePath = startRemotePath;
        Live[DragId] = new WeakReference<FileBrowserSession>(this);
    }

    public override string KindSymbol => "FolderSwap24";
    public override string KindLabel => "File transfer";
    /// <summary>Identifies this session in drag &amp; drop data (only plain text crosses the drag boundary).</summary>
    public string DragId { get; } = Guid.NewGuid().ToString("N");
    private static readonly Dictionary<string, WeakReference<FileBrowserSession>> Live = new();
    public static FileBrowserSession? FindByDragId(string id) => Live.TryGetValue(id, out var w) && w.TryGetTarget(out var s) ? s : null;
    /// <summary>Remote folder shown first (e.g. the current folder of a PowerShell tab).</summary>
    public string? StartRemotePath { get; set; }

    public Task<Listing> ListAsync(string path) => Run(rs => RemoteFs.ListRemote(rs, path));
    public Task<List<string>> ExistingAsync(string folder, IEnumerable<string> names) => Run(rs => RemoteFs.ExistingRemote(rs, folder, names));
    public Task<string> MkdirAsync(string path) => Run(rs => RemoteFs.MkdirRemote(rs, path));
    public Task DeleteAsync(IEnumerable<string> paths) => Run(rs => { RemoteFs.DeleteRemote(rs, paths); return true; });
    public Task<string> RenameAsync(string path, string newName) => Run(rs => RemoteFs.RenameRemote(rs, path, newName));
    public Task<List<(long Size, string FullPath, string Relative)>> ExpandAsync(IEnumerable<string> paths) => Run(rs => RemoteFs.ExpandRemote(rs, paths));

    protected override void OnDisposed() => Live.Remove(DragId);
}
