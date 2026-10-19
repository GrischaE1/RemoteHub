using RemoteHub.Models;
using RemoteHub.Rdp;

namespace RemoteHub.Remoting;

/// <summary>
/// File transfer tab: this PC on one side, the remote computer (over PowerShell remoting) on the other.
/// One remote runspace is used for browsing; transfers run over their own sessions so browsing stays responsive.
/// </summary>
public sealed class FileBrowserSession : RemoteToolSession, IRemoteFiles
{
    public FileBrowserSession(Connection connection, string? startRemotePath) : base(connection)
    {
        StartRemotePath = startRemotePath;
        RemoteFilesRegistry.Add(this);
    }

    public override string KindSymbol => "FolderSwap24";
    public override string KindLabel => "File transfer";
    public override Services.RemoteTool Tool => Services.RemoteTool.FileTransfer;
    public string DragId { get; } = Guid.NewGuid().ToString("N");
    /// <summary>Remote folder shown first (e.g. the current folder of a PowerShell tab).</summary>
    public string? StartRemotePath { get; set; }

    SessionBase IRemoteFiles.Session => this;
    public string RemoteTitle => $"{Connection.Name}  ({Connection.Host})";
    public string RemoteSymbol => "Server24";
    public string KindText => "File transfer";
    public string WhereText => Connection.Host;
    public string FallbackPath => @"C:\";
    public string PathHint => "Path, e.g. C:\\Temp or %TEMP% – Enter opens it";
    public string ParentOf(string path) => RemoteFs.Parent(path);
    public string Join(string folder, string name) => folder.TrimEnd('\\') + "\\" + name;

    public Task<Listing> ListAsync(string path) => Run(rs => RemoteFs.ListRemote(rs, path));
    public Task<List<string>> ExistingAsync(string folder, IEnumerable<string> names) => Run(rs => RemoteFs.ExistingRemote(rs, folder, names));
    public Task<string> MkdirAsync(string path) => Run(rs => RemoteFs.MkdirRemote(rs, path));
    public Task DeleteAsync(IEnumerable<string> paths) => Run(rs => { RemoteFs.DeleteRemote(rs, paths); return true; });
    public Task<string> RenameAsync(string path, string newName) => Run(rs => RemoteFs.RenameRemote(rs, path, newName));
    public Task<List<(long Size, string FullPath, string Relative)>> ExpandAsync(IEnumerable<string> paths) => Run(rs => RemoteFs.ExpandRemote(rs, paths));

    protected override void OnDisposed() => RemoteFilesRegistry.Remove(this);
}
