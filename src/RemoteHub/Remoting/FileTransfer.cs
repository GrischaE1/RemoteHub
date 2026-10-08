using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteHub.Remoting;

public enum TransferState { Queued, Running, Done, Failed, Cancelled, Skipped }

/// <summary>One file being sent to a remote computer (shown in the transfers panel).</summary>
public sealed partial class TransferItem : ObservableObject
{
    /// <summary>Upload of a local file.</summary>
    public TransferItem(string localPath, string relativePath, string connectionName)
        : this(localPath, relativePath, connectionName, new FileInfo(localPath).Length, false) { }

    private TransferItem(string localPath, string relativePath, string connectionName, long total, bool download)
    {
        LocalPath = localPath;
        RelativePath = relativePath;
        ConnectionName = connectionName;
        TotalBytes = total;
        IsDownload = download;
        _statusText = "Waiting…";
    }

    /// <summary>Download of <paramref name="remotePath"/> to <paramref name="localPath"/>.</summary>
    public static TransferItem Download(string remotePath, string localPath, string relativePath, string connectionName, long size) =>
        new(localPath, relativePath, connectionName, size, true) { RemotePath = remotePath };

    public bool IsDownload { get; }

    public string LocalPath { get; }
    /// <summary>Path below the destination folder ("file.txt" or "sub\file.txt" for dropped folders).</summary>
    public string RelativePath { get; }
    public string FileName => Path.GetFileName(LocalPath);
    public string ConnectionName { get; }
    public long TotalBytes { get; }

    [ObservableProperty] private TransferState _state;
    [ObservableProperty] private long _sentBytes;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _statusText;
    [ObservableProperty] private string? _remotePath;

    public bool IsActive => State is TransferState.Queued or TransferState.Running;
    public bool IsDone => State == TransferState.Done;
    public bool IsProblem => State is TransferState.Failed or TransferState.Cancelled;
    public string Destination => IsDownload ? $"{ConnectionName} → this PC: {LocalPath}" : $"This PC → {ConnectionName}: {RemotePath ?? RelativePath}";

    internal volatile bool CancelRequested;
    public void Cancel()
    {
        CancelRequested = true;
        if (State == TransferState.Queued) { State = TransferState.Cancelled; StatusText = "Cancelled"; }
    }

    partial void OnStateChanged(TransferState value)
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsProblem));
    }

    partial void OnRemotePathChanged(string? value) => OnPropertyChanged(nameof(Destination));

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}

/// <summary>
/// Sends files over a PowerShell remoting session in 1 MB chunks: written to "&lt;name&gt;.remotehub-part" on the remote
/// side, checked with SHA-256 and only then renamed to the final name. No PowerShell window is needed.
/// </summary>
public static class FileUploader
{
    public const int ChunkSize = 1024 * 1024;
    private const string PartSuffix = ".remotehub-part";

    private const string ResolveFolderScript = """
        param([string]$Folder)
        $f = $Folder.Trim()
        if ($f.StartsWith('~')) { $f = $HOME + $f.Substring(1) }
        $f = [Environment]::ExpandEnvironmentVariables($f)
        if (-not [IO.Path]::IsPathRooted($f)) { $f = Join-Path (Get-Location).ProviderPath $f }
        [IO.Directory]::CreateDirectory($f).FullName
        """;

    private const string BeginScript = """
        param([string]$Path, [bool]$Overwrite)
        if ((Test-Path -LiteralPath $Path) -and -not $Overwrite) { 'exists'; return }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
        [IO.File]::WriteAllBytes($Path + '.remotehub-part', [byte[]]@())
        'ok'
        """;

    private const string ChunkScript = """
        param([string]$Path, [byte[]]$Data)
        $s = [IO.File]::Open($Path, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $s.Write($Data, 0, $Data.Length) } finally { $s.Dispose() }
        """;

    private const string FinishScript = """
        param([string]$Path, [string]$Hash)
        $part = $Path + '.remotehub-part'
        $s = [IO.File]::OpenRead($part)
        try { $h = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($s)).Replace('-', '') } finally { $s.Dispose() }
        if ($h -ne $Hash) { Remove-Item -LiteralPath $part -Force; throw 'Checksum mismatch – the file was damaged during the transfer.' }
        Move-Item -LiteralPath $part -Destination $Path -Force
        """;

    private const string CleanupScript = """
        param([string]$Path)
        Remove-Item -LiteralPath ($Path + '.remotehub-part') -Force -ErrorAction SilentlyContinue
        """;

    /// <summary>
    /// Uploads <paramref name="items"/> one after another. <paramref name="post"/> runs UI updates on the UI thread.
    /// Never throws: failures end up in the items' state.
    /// </summary>
    public static Task UploadAsync(Func<Runspace> openRunspace, Func<Exception, string> explain, string remoteFolder, bool overwrite,
                                   IReadOnlyList<TransferItem> items, Action<Action> post) => Task.Run(() =>
    {
        Runspace? rs = null;
        try
        {
            foreach (var i in items) post(() => { if (i.State == TransferState.Queued) i.StatusText = "Connecting…"; });
            string folder;
            try
            {
                rs = openRunspace();
                folder = Invoke(rs, ResolveFolderScript, ("Folder", remoteFolder)).FirstOrDefault()?.ToString()
                         ?? throw new InvalidOperationException("The remote folder could not be created.");
            }
            catch (Exception ex)
            {
                var why = ex is RemoteCommandException ? ex.Message : explain(ex);
                foreach (var i in items) post(() => { if (i.IsActive) Fail(i, why); });
                return;
            }

            foreach (var item in items)
            {
                if (item.CancelRequested) { post(() => { item.State = TransferState.Cancelled; item.StatusText = "Cancelled"; }); continue; }
                var target = folder.TrimEnd('\\') + "\\" + item.RelativePath.Replace('/', '\\').TrimStart('\\');
                post(() => { item.RemotePath = target; item.State = TransferState.Running; item.StatusText = "Starting…"; });
                try { SendOne(rs, item, target, overwrite, post); }
                catch (Exception ex)
                {
                    TryCleanup(rs, target);
                    var why = ex is RemoteCommandException ? ex.Message : explain(ex);
                    post(() => Fail(item, why));
                    if (rs.RunspaceStateInfo.State != RunspaceState.Opened)
                    {
                        foreach (var rest in items.Where(x => x != item)) post(() => { if (rest.IsActive) Fail(rest, why); });
                        return;
                    }
                }
            }
        }
        finally
        {
            try { rs?.Dispose(); } catch { /* connection already gone */ }
        }
    });

    private static void SendOne(Runspace rs, TransferItem item, string target, bool overwrite, Action<Action> post)
    {
        if (Invoke(rs, BeginScript, ("Path", target), ("Overwrite", overwrite)).FirstOrDefault()?.ToString() == "exists")
        {
            post(() => { item.State = TransferState.Skipped; item.StatusText = "Skipped – the file already exists"; });
            return;
        }

        using var file = new FileStream(item.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkSize];
        long sent = 0;
        var sw = Stopwatch.StartNew();
        int read;
        while ((read = ReadFull(file, buffer)) > 0)
        {
            if (item.CancelRequested)
            {
                TryCleanup(rs, target);
                post(() => { item.State = TransferState.Cancelled; item.StatusText = "Cancelled"; });
                return;
            }
            var chunk = read == buffer.Length ? buffer : buffer[..read];
            sha.AppendData(chunk);
            Invoke(rs, ChunkScript, ("Path", target + PartSuffix), ("Data", chunk));
            sent += read;
            var s = sent;
            var rate = s / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            post(() =>
            {
                item.SentBytes = s;
                item.Percent = item.TotalBytes == 0 ? 100 : s * 100.0 / item.TotalBytes;
                item.StatusText = $"{TransferItem.Size(s)} of {TransferItem.Size(item.TotalBytes)} · {TransferItem.Size((long)rate)}/s";
            });
        }

        post(() => item.StatusText = "Verifying…");
        var hash = Convert.ToHexString(sha.GetHashAndReset());
        Invoke(rs, FinishScript, ("Path", target), ("Hash", hash));
        var total = item.TotalBytes;
        var secs = sw.Elapsed.TotalSeconds;
        post(() =>
        {
            item.SentBytes = total;
            item.Percent = 100;
            item.State = TransferState.Done;
            item.StatusText = $"Done · {TransferItem.Size(total)} in {secs:0.#} s · SHA-256 verified";
        });
    }

    private static int ReadFull(Stream s, byte[] buffer)
    {
        int total = 0, n;
        while (total < buffer.Length && (n = s.Read(buffer, total, buffer.Length - total)) > 0) total += n;
        return total;
    }

    private static void Fail(TransferItem i, string why)
    {
        i.State = TransferState.Failed;
        i.StatusText = "Failed – " + why;
    }

    private static void TryCleanup(Runspace rs, string target)
    {
        try { if (rs.RunspaceStateInfo.State == RunspaceState.Opened) Invoke(rs, CleanupScript, ("Path", target)); }
        catch { /* best effort */ }
    }

    /// <summary>Runs a script with parameters; remote errors become a <see cref="RemoteCommandException"/>.</summary>
    private static List<PSObject> Invoke(Runspace rs, string script, params (string Name, object Value)[] args)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        ps.AddScript(script);
        foreach (var (n, v) in args) ps.AddParameter(n, v);
        List<PSObject> result;
        try { result = ps.Invoke().ToList(); }
        catch (RuntimeException ex) when (ex is not System.Management.Automation.Remoting.PSRemotingTransportException)
        {
            throw new RemoteCommandException(ex.ErrorRecord?.Exception?.Message ?? ex.Message);
        }
        if (ps.HadErrors && ps.Streams.Error.Count > 0)
            throw new RemoteCommandException(ps.Streams.Error[0].Exception?.Message ?? ps.Streams.Error[0].ToString());
        return result;
    }

    /// <summary>Expands dropped files and folders into (local file, relative remote path) pairs.</summary>
    public static List<(string Local, string Relative)> Expand(IEnumerable<string> paths)
    {
        var list = new List<(string, string)>();
        foreach (var p in paths)
        {
            if (File.Exists(p)) list.Add((p, Path.GetFileName(p)));
            else if (Directory.Exists(p))
            {
                var root = Path.GetDirectoryName(Path.GetFullPath(p).TrimEnd('\\')) ?? p;
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    list.Add((f, Path.GetRelativePath(root, f)));
            }
        }
        return list;
    }
}

/// <summary>
/// Gets files from a remote computer in 1 MB chunks into "&lt;name&gt;.remotehub-part", compares the SHA-256 with the
/// remote file and only then renames it to the final name.
/// </summary>
public static class FileDownloader
{
    private const string ReadScript = """
        param([string]$Path, [long]$Offset, [int]$Count)
        $s = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            [void]$s.Seek($Offset, [IO.SeekOrigin]::Begin)
            $b = New-Object byte[] $Count
            $n = 0
            while ($n -lt $Count) { $r = $s.Read($b, $n, $Count - $n); if ($r -le 0) { break }; $n += $r }
            if ($n -lt $Count) { $c = New-Object byte[] $n; [Array]::Copy($b, $c, $n); $b = $c }
            ,$b
        } finally { $s.Dispose() }
        """;

    private const string HashScript = """
        param([string]$Path)
        $s = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($s)).Replace('-', '') } finally { $s.Dispose() }
        """;

    /// <summary>Downloads the items (RemotePath → LocalPath) one after another. Never throws.</summary>
    public static Task DownloadAsync(Func<Runspace> openRunspace, Func<Exception, string> explain, bool overwrite,
                                     IReadOnlyList<TransferItem> items, Action<Action> post) => Task.Run(() =>
    {
        Runspace? rs = null;
        try
        {
            try { rs = openRunspace(); }
            catch (Exception ex)
            {
                var why = explain(ex);
                foreach (var i in items) post(() => { if (i.IsActive) { i.State = TransferState.Failed; i.StatusText = "Failed – " + why; } });
                return;
            }
            foreach (var item in items)
            {
                if (item.CancelRequested) { post(() => { item.State = TransferState.Cancelled; item.StatusText = "Cancelled"; }); continue; }
                post(() => { item.State = TransferState.Running; item.StatusText = "Starting…"; });
                var part = item.LocalPath + ".remotehub-part";
                try { GetOne(rs, item, part, overwrite, post); }
                catch (Exception ex)
                {
                    TryDelete(part);
                    var why = ex is RemoteCommandException or IOException or UnauthorizedAccessException ? ex.Message : explain(ex);
                    post(() => { item.State = TransferState.Failed; item.StatusText = "Failed – " + why; });
                    if (rs.RunspaceStateInfo.State != RunspaceState.Opened)
                    {
                        foreach (var rest in items.Where(x => x != item))
                            post(() => { if (rest.IsActive) { rest.State = TransferState.Failed; rest.StatusText = "Failed – " + why; } });
                        return;
                    }
                }
            }
        }
        finally { try { rs?.Dispose(); } catch { /* connection already gone */ } }
    });

    private static void GetOne(Runspace rs, TransferItem item, string part, bool overwrite, Action<Action> post)
    {
        if (File.Exists(item.LocalPath) && !overwrite)
        {
            post(() => { item.State = TransferState.Skipped; item.StatusText = "Skipped – the file already exists"; });
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sw = Stopwatch.StartNew();
        long got = 0;
        using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            while (true)
            {
                if (item.CancelRequested)
                {
                    file.Dispose();
                    TryDelete(part);
                    post(() => { item.State = TransferState.Cancelled; item.StatusText = "Cancelled"; });
                    return;
                }
                var data = RemoteFs.Invoke(rs, ReadScript, ("Path", item.RemotePath!), ("Offset", got), ("Count", FileUploader.ChunkSize))
                    .FirstOrDefault()?.BaseObject as byte[] ?? [];
                if (data.Length == 0) break;
                file.Write(data);
                sha.AppendData(data);
                got += data.Length;
                var g = got;
                var total = Math.Max(item.TotalBytes, g);
                var rate = g / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                post(() =>
                {
                    item.SentBytes = g;
                    item.Percent = total == 0 ? 100 : g * 100.0 / total;
                    item.StatusText = $"{TransferItem.Size(g)} of {TransferItem.Size(total)} · {TransferItem.Size((long)rate)}/s";
                });
                if (data.Length < FileUploader.ChunkSize) break;
            }
        }
        post(() => item.StatusText = "Verifying…");
        var remoteHash = RemoteFs.Invoke(rs, HashScript, ("Path", item.RemotePath!)).FirstOrDefault()?.ToString();
        if (!string.Equals(remoteHash, Convert.ToHexString(sha.GetHashAndReset()), StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(part);
            throw new IOException("Checksum mismatch – the file changed during the transfer or was damaged.");
        }
        File.Move(part, item.LocalPath, overwrite: true);
        var secs = sw.Elapsed.TotalSeconds;
        var size = got;
        post(() =>
        {
            item.SentBytes = size;
            item.Percent = 100;
            item.State = TransferState.Done;
            item.StatusText = $"Done · {TransferItem.Size(size)} in {secs:0.#} s · SHA-256 verified";
        });
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { /* best effort */ } }
}

/// <summary>A command in the remote session failed (message is shown to the user).</summary>
public sealed class RemoteCommandException(string message) : Exception(message);
