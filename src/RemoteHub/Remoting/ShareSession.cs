using RemoteHub.Models;
using RemoteHub.Rdp;
using RemoteHub.Remoting.Shares;
using SessionState = RemoteHub.Rdp.SessionState;

namespace RemoteHub.Remoting;

/// <summary>
/// Tab of a network share: this PC on one side, the share on the other – the file transfer tab's layout. RemoteHub
/// connects to the share itself (no agent, no PowerShell). The connection used for browsing is closed after
/// <see cref="IdleTime"/> without use and opened again by the next action; transfers use their own connections, closed
/// when they are done. Closing the tab closes everything.
/// </summary>
public sealed class ShareSession : SessionBase, IRemoteFiles
{
    public static TimeSpan IdleTime { get; set; } = TimeSpan.FromMinutes(5);

    private readonly System.Threading.SemaphoreSlim _lock = new(1, 1);
    private readonly System.Windows.Threading.DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(30) };
    private Func<ShareFileSystem>? _open;
    private ShareFileSystem? _fs;
    private DateTime _lastUse;
    private bool _closed;
    private int _generation;

    public ShareSession(Connection server, NetworkShare share) : base(server)
    {
        Share = share;
        Title = server.Name;
        RemoteFilesRegistry.Add(this);
        _idle.Tick += (_, _) => CloseIfIdle();
    }

    public NetworkShare Share { get; }
    /// <summary>The account last used (with a password entered for it – memory only), for reconnecting without asking again.</summary>
    public CredentialSet? Account { get; set; }
    public override string KindSymbol => Share.DisplaySymbol;
    public override string KindLabel => Share.Name;
    public string DragId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The path rules of the open share (kept after the connection closes for being idle).</summary>
    private ShareFileSystem? _paths;

    SessionBase IRemoteFiles.Session => this;
    public string RemoteTitle => $"{Share.Name}  ({Share.ProtocolLabel})";
    public string RemoteSymbol => Share.DisplaySymbol;
    public string KindText => "Network share";
    public string WhereText => Share.Address;
    public string? StartRemotePath => _paths?.Start;
    public string FallbackPath => _paths?.Start ?? "";
    public string PathHint => Share.Protocol == ShareProtocol.Smb ? @"Path on the share, e.g. \\server\share\folder – Enter opens it" : "Path, e.g. /folder – Enter opens it";
    public string ParentOf(string path) => _paths?.Parent(path) ?? path;
    public string Join(string folder, string name) => _paths?.Join(folder, name) ?? folder + "/" + name;

    public event Action? Ready;
    public event Action? FocusRequested;

    /// <summary>Connects in the background with <paramref name="open"/> (which knows the account and asks about keys).</summary>
    public void Connect(Func<ShareFileSystem> open, string credentialSource, System.Windows.Media.Brush accent)
    {
        if (_closed) return;
        _open = open;
        AccentBrush = accent;
        CredentialInfo = $"Account from {credentialSource} · {Share.Address}";
        Drop();
        var gen = ++_generation;
        State = SessionState.Connecting;
        StatusText = $"Connecting to {Share.Address}…";
        Task.Run(() =>
        {
            try { return (open(), (string?)null); }
            catch (Exception ex) { return ((ShareFileSystem?)null, Explain(ex)); }
        }).ContinueWith(t =>
        {
            var (fs, error) = t.Result;
            if (_closed || gen != _generation) { fs?.Dispose(); return; }
            if (fs is null)
            {
                State = SessionState.Failed;
                StatusText = error!;
                return;
            }
            _fs = _paths = fs;
            _lastUse = DateTime.UtcNow;
            Transport = Reached(fs);
            State = SessionState.Connected;
            StatusText = "Connected – " + Reached(fs);
            _idle.Start();
            Services.DiagLog.Write($"Share ({Share.ProtocolLabel}): connected ({Reached(fs)})");
            Ready?.Invoke();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>A separate connection for a transfer (the caller disposes it).</summary>
    public ShareFileSystem OpenAnother() => (_open ?? throw new InvalidOperationException("Not connected."))();

    /// <summary>How the share is reached, for the tab: "SFTP · OpenSSH_9.5 · via JUMP01" behind a jump host.</summary>
    private string Reached(ShareFileSystem fs) => Connection.JumpHost is { } jump ? $"{fs.Description} · via {jump.Name}" : fs.Description;

    /// <summary>Runs one call at a time; opens the connection again if it was closed for being idle.</summary>
    private async Task<T> Run<T>(Func<ShareFileSystem, T> call)
    {
        if (_open is null || State != SessionState.Connected) throw new ShareException("Not connected.");
        await _lock.WaitAsync();
        try
        {
            var reopened = false;
            var result = await Task.Run(() =>
            {
                if (_closed || State != SessionState.Connected) throw new ShareException("Not connected.");
                if (_fs is null) { _fs = _paths = _open(); reopened = true; }
                try { return call(_fs); }
                catch (Exception ex) when (ex is not (ShareException or OperationCanceledException))
                {
                    var fs = _fs;
                    _fs = null;
                    try { fs?.Dispose(); } catch { /* already gone */ }
                    throw new ShareException("The connection to the share was lost: " + Explain(ex) + " – try again to reconnect.", ex);
                }
            });
            if (reopened && _fs is { } now) StatusText = "Connected – " + Reached(now);
            return result;
        }
        catch (Exception ex) when (ex is not (ShareException or OperationCanceledException)) { throw new ShareException(Explain(ex), ex); }
        finally
        {
            _lastUse = DateTime.UtcNow;
            _lock.Release();
        }
    }

    private void CloseIfIdle()
    {
        if (_fs is null || DateTime.UtcNow - _lastUse < IdleTime || !_lock.Wait(0)) return;
        try
        {
            var fs = _fs;
            _fs = null;
            fs?.Dispose();
            StatusText = "Idle – the connection to the share was closed and opens again when you use it.";
            Services.DiagLog.Write($"Share ({Share.ProtocolLabel}): closed after {IdleTime.TotalMinutes:0} min without use");
        }
        finally { _lock.Release(); }
    }

    public static string Explain(Exception ex) => ex switch
    {
        ShareException or IOException or UnauthorizedAccessException => ex.Message,
        System.Net.Sockets.SocketException s => s.SocketErrorCode switch
        {
            System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData => "The host name can't be resolved.",
            System.Net.Sockets.SocketError.ConnectionRefused => "The server refused the connection (nothing listens on that port?).",
            System.Net.Sockets.SocketError.TimedOut => "The server doesn't answer.",
            _ => s.Message,
        },
        System.Net.Http.HttpRequestException { InnerException: { } inner } => Explain(inner),
        AggregateException { InnerException: { } inner } => Explain(inner),
        TimeoutException => "The server doesn't answer.",
        _ => ex.Message,
    };

    public Task<Listing> ListAsync(string path) => Run(fs =>
    {
        var p = fs.Normalize(path);
        return new Listing(p, FileEntry.Sort(fs.List(p)));
    });

    public Task<List<string>> ExistingAsync(string folder, IEnumerable<string> names) =>
        Run(fs => names.Where(n => fs.Stat(fs.Join(fs.Normalize(folder), n)) is not null).ToList());

    public Task<string> MkdirAsync(string path) => Run(fs => { var p = fs.Normalize(path); fs.CreateFolder(p); return p; });

    public Task DeleteAsync(IEnumerable<string> paths) => Run(fs =>
    {
        foreach (var p in paths)
        {
            if (fs.SamePath(p, fs.Top)) throw new ShareException("The share itself can't be deleted.");
            var e = fs.Stat(p) ?? throw new ShareException($"Not found: {p}");
            fs.Delete(p, e.Kind == EntryKind.Folder);
        }
        return true;
    });

    public Task<string> RenameAsync(string path, string newName) => Run(fs =>
    {
        var target = fs.Join(fs.Parent(path), newName);
        if (fs.Stat(target) is not null) throw new ShareException($"\"{newName}\" already exists.");
        fs.Move(path, target);
        return target;
    });

    public Task<List<(long Size, string FullPath, string Relative)>> ExpandAsync(IEnumerable<string> paths) => Run(fs => fs.Expand(paths));

    public override void FocusSession() => FocusRequested?.Invoke();

    private void Drop()
    {
        var fs = _fs;
        _fs = null;
        try { fs?.Dispose(); } catch { /* already gone */ }
    }

    public override void Disconnect()
    {
        _generation++;
        _idle.Stop();
        if (State is SessionState.Connected or SessionState.Connecting)
        {
            DropSoon();
            State = SessionState.Disconnected;
            StatusText = "Disconnected.";
        }
    }

    public override void Close()
    {
        if (_closed) return;
        Dispose();
        RaiseClosed();
    }

    public override void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _generation++;
        _idle.Stop();
        RemoteFilesRegistry.Remove(this);
        DropSoon();
        Services.DiagLog.Write($"Share ({Share.ProtocolLabel}): closed");
    }

    /// <summary>Closes the connection now, or – while a call still runs on it – right after that call.</summary>
    private void DropSoon()
    {
        if (_lock.Wait(0)) { try { Drop(); } finally { _lock.Release(); } }
        else Task.Run(() => { _lock.Wait(); try { Drop(); } finally { _lock.Release(); } });
    }
}
