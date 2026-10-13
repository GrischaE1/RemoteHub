using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography;
using System.Threading;
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
    /// <summary>Speed for this file: current while running, average when done (files of 1 MB and more only).</summary>
    [ObservableProperty] private string _speedText = "";

    /// <summary>When this file started / stopped moving (for its average and the overall average).</summary>
    public DateTime? StartedUtc { get; private set; }
    public DateTime? FinishedUtc { get; private set; }

    private long _sampleBytes;
    private DateTime _sampleAt;
    private double _rate;

    /// <summary>Current speed in bytes/s (smoothed); 0 when not running.</summary>
    public double Rate => IsActive ? _rate : 0;

    /// <summary>Bytes per second from start to finish (or to now while running).</summary>
    public double AverageRate
    {
        get
        {
            if (StartedUtc is not { } start) return 0;
            var secs = ((FinishedUtc ?? DateTime.UtcNow) - start).TotalSeconds;
            return secs > 0.05 ? SentBytes / secs : 0;
        }
    }

    private const long SpeedMinimum = 1024 * 1024;

    partial void OnSentBytesChanged(long value)
    {
        var now = DateTime.UtcNow;
        StartedUtc ??= now;
        if (_sampleAt == default) { _sampleAt = now; _sampleBytes = value; }
        else if ((now - _sampleAt).TotalSeconds >= 0.25)
        {
            var current = (value - _sampleBytes) / (now - _sampleAt).TotalSeconds;
            _rate = _rate == 0 ? current : _rate * 0.6 + current * 0.4;
            _sampleAt = now;
            _sampleBytes = value;
        }
        UpdateSpeedText();
    }

    private void UpdateSpeedText() => SpeedText =
        TotalBytes < SpeedMinimum || SentBytes == 0 ? ""
        : IsActive ? (_rate > 0 ? $"{Size((long)_rate)}/s" : "")
        : State == TransferState.Done ? $"avg {Size((long)AverageRate)}/s"
        : "";

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
        var now = DateTime.UtcNow;
        if (value == TransferState.Running) { StartedUtc ??= now; _sampleAt = now; _sampleBytes = SentBytes; }
        else if (value != TransferState.Queued && StartedUtc is not null) FinishedUtc ??= now;
        UpdateSpeedText();
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
/// Sends files over a PowerShell session (WinRM or the RemoteHub Agent) as one stream per batch: every file is read in
/// 1 MB chunks, gzip-compressed where that helps, and streamed into a single remote pipeline without waiting for an
/// answer per chunk. The remote side always decompresses, writes "&lt;name&gt;.remotehub-part", checks the SHA-256 of
/// what it wrote against this PC's hash of the original, and only then renames it – no archive or compressed file is
/// ever left on the target, and nothing needs to be installed there (.NET's GZipStream is part of Windows).
/// Many small files share one pipeline (no per-file round trips); at most <see cref="WindowBytes"/> are in flight.
/// </summary>
public static class FileUploader
{
    public const int ChunkSize = 1024 * 1024;

    /// <summary>Data sent but not yet written on the remote side (bounds memory for big files).</summary>
    internal static int WindowBytes = 8 * 1024 * 1024;

    /// <summary>A new pipeline after this many files or bytes, so one failure can't take down a huge batch.</summary>
    private const int BatchFiles = 1000;
    private const long BatchBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Data per remote command. WinRM refuses more than 50 MB of input per command (MaxReceivedDataSizePerCommandMB,
    /// counted after serialization, which adds a third for byte arrays), so a batch is sent as several commands of this
    /// size; the open files and their hashes stay in the session in between.
    /// </summary>
    internal static long CommandBytes = 30L * 1024 * 1024;

    private const string ResolveFolderScript = """
        param([string]$Folder)
        $f = $Folder.Trim()
        if ($f.StartsWith('~')) { $f = $HOME + $f.Substring(1) }
        $f = [Environment]::ExpandEnvironmentVariables($f)
        if (-not [IO.Path]::IsPathRooted($f)) { $f = Join-Path (Get-Location).ProviderPath $f }
        [IO.Directory]::CreateDirectory($f).FullName
        """;

    private const string ExistingScript = """
        param([string[]]$Paths)
        foreach ($p in $Paths) { if (Test-Path -LiteralPath $p) { $p } }
        """;

    /// <summary>
    /// Input messages: begin|i|path, data|i|bytes|gzip, end|i|sha256, abort|i, and for files that fit in one chunk
    /// file|i|path|bytes|gzip|sha256 (all in one). Output lines: ack|i|bytes, done|i, skip|i, fail|i|reason,
    /// cancelled|i. A failing file never stops the others. Plain .NET calls only (cmdlets cost milliseconds per file).
    /// One batch can span several commands (<see cref="CommandBytes"/>): the open files live in a session variable,
    /// the batch's first command drops what an earlier, broken batch left, and close|0 ends the batch.
    /// </summary>
    private const string StreamScript = """
        param([bool]$Overwrite, [bool]$First)
        begin {
            function Drop($i) {
                $f = $files[$i]
                if ($f -and $f.S) { try { $f.S.Dispose() } catch { }; try { [IO.File]::Delete($f.P + '.remotehub-part') } catch { } }
                $files[$i] = @{ Dead = $true }
            }
            $files = $global:RemoteHubUpload
            if ($First -and $files) { foreach ($k in @($files.Keys)) { Drop $k } }
            if ($First -or -not $files) { $files = $global:RemoteHubUpload = @{} }
            $sha = [Security.Cryptography.SHA256]::Create()
            function Unzip([byte[]]$d) {
                $z = New-Object IO.Compression.GZipStream((New-Object IO.MemoryStream(,$d)), [IO.Compression.CompressionMode]::Decompress)
                $o = New-Object IO.MemoryStream
                try { $z.CopyTo($o) } finally { $z.Dispose() }
                ,$o.ToArray()
            }
            function Open($i, [string]$p) {
                if (-not $Overwrite -and [IO.File]::Exists($p)) { $files[$i] = @{ Dead = $true }; return $false }
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p))
                $files[$i] = @{ P = $p; Dead = $false; H = [Security.Cryptography.SHA256]::Create()
                                S = [IO.File]::Open($p + '.remotehub-part', [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None) }
                $true
            }
            function Finish($i, [string]$hash) {
                $f = $files[$i]
                [void]$f.H.TransformFinalBlock([byte[]]@(), 0, 0)
                $f.S.Dispose(); $f.S = $null
                $part = $f.P + '.remotehub-part'
                if ([BitConverter]::ToString($f.H.Hash).Replace('-', '') -ne $hash) {
                    [IO.File]::Delete($part)
                    $files[$i] = @{ Dead = $true }
                    return "fail|$i|Checksum mismatch – the file was damaged during the transfer."
                }
                if ([IO.File]::Exists($f.P)) { [IO.File]::Delete($f.P) }
                [IO.File]::Move($part, $f.P)
                $files.Remove($i)
                "done|$i"
            }
        }
        process {
            $m = $_; $i = [int]$m[1]
            try {
                switch ([string]$m[0]) {
                    'file' {
                        # Whole file in memory: verified before anything is written, so it goes straight to its final
                        # name (a rename per file costs ~10 ms where a virus scanner checks renamed files).
                        $p = [string]$m[2]
                        if (-not $Overwrite -and [IO.File]::Exists($p)) { "skip|$i"; break }
                        $d = [byte[]]$m[3]; if ($m[4]) { $d = Unzip $d }
                        if ([BitConverter]::ToString($sha.ComputeHash($d)).Replace('-', '') -ne [string]$m[5]) { "fail|$i|Checksum mismatch – the file was damaged during the transfer."; break }
                        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p))
                        try { [IO.File]::WriteAllBytes($p, $d) } catch { try { [IO.File]::Delete($p) } catch { }; throw }
                        "done|$i"
                    }
                    'begin' { if (-not (Open $i ([string]$m[2]))) { "skip|$i" } }
                    'data' {
                        $f = $files[$i]; if ($null -eq $f -or $f.Dead) { break }
                        $d = [byte[]]$m[2]; if ($m[3]) { $d = Unzip $d }
                        $f.S.Write($d, 0, $d.Length)
                        [void]$f.H.TransformBlock($d, 0, $d.Length, $null, 0)
                        "ack|$i|$($d.Length)"
                    }
                    'end' {
                        $f = $files[$i]; if ($null -eq $f -or $f.Dead) { break }
                        Finish $i ([string]$m[2])
                    }
                    'abort' { Drop $i; "cancelled|$i" }
                    'close' { foreach ($k in @($files.Keys)) { if ($files[$k].S) { Drop $k } }; $global:RemoteHubUpload = $null }
                }
            } catch {
                Drop $i
                "fail|$i|$($_.Exception.Message)"
            }
        }
        """;

    /// <summary>
    /// Uploads <paramref name="items"/>. <paramref name="post"/> runs UI updates on the UI thread.
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

            var work = new List<(TransferItem Item, string Target)>();
            foreach (var item in items)
            {
                var target = folder.TrimEnd('\\') + "\\" + item.RelativePath.Replace('/', '\\').TrimStart('\\');
                post(() => { item.RemotePath = target; if (item.State == TransferState.Queued) item.StatusText = "Waiting…"; });
                work.Add((item, target));
            }

            // Without overwrite: find existing files first, so they aren't sent at all (one round trip for all).
            if (!overwrite && work.Count > 0)
            {
                var existing = new HashSet<string>(Invoke(rs, ExistingScript, ("Paths", work.Select(w => w.Target).ToArray())).Select(o => o.ToString()),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var (item, _) in work.Where(w => existing.Contains(w.Target)))
                    post(() => { item.State = TransferState.Skipped; item.StatusText = "Skipped – the file already exists"; });
                work.RemoveAll(w => existing.Contains(w.Target));
            }

            foreach (var batch in Batches(work))
            {
                try { SendBatch(rs, batch, overwrite, post); }
                catch (Exception ex)
                {
                    var why = ex is RemoteCommandException ? ex.Message : explain(ex);
                    foreach (var (item, _) in batch) post(() => { if (item.IsActive) Fail(item, why); });
                    if (rs.RunspaceStateInfo.State != RunspaceState.Opened)
                    {
                        foreach (var (item, _) in work) post(() => { if (item.IsActive) Fail(item, why); });
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

    private static IEnumerable<List<(TransferItem Item, string Target)>> Batches(List<(TransferItem Item, string Target)> work)
    {
        var batch = new List<(TransferItem, string)>();
        long bytes = 0;
        foreach (var w in work)
        {
            if (batch.Count > 0 && (batch.Count >= BatchFiles || bytes + w.Item.TotalBytes > BatchBytes)) { yield return batch; batch = []; bytes = 0; }
            batch.Add(w);
            bytes += w.Item.TotalBytes;
        }
        if (batch.Count > 0) yield return batch;
    }

    /// <summary>Per-file progress of one batch, updated from the remote answers.</summary>
    private sealed class Progress(TransferItem item)
    {
        public readonly TransferItem Item = item;
        public long Written, Sent, Wire;
        public bool Finished;
        public readonly Stopwatch Clock = new();
    }

    private static void SendBatch(Runspace rs, List<(TransferItem Item, string Target)> batch, bool overwrite, Action<Action> post)
    {
        var files = batch.Select(b => new Progress(b.Item)).ToArray();
        var gate = new object();
        long inFlight = 0;

        void Answer(string line)
        {
            var parts = line.Split('|', 3);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var i) || i < 0 || i >= files.Length) return;
            var f = files[i];
            var item = f.Item;
            switch (parts[0])
            {
                case "ack":
                    var n = long.Parse(parts[2]);
                    long written;
                    lock (gate) { f.Written += n; written = f.Written; inFlight -= n; Monitor.PulseAll(gate); }
                    post(() =>
                    {
                        item.SentBytes = written;
                        item.Percent = item.TotalBytes == 0 ? 100 : written * 100.0 / item.TotalBytes;
                        item.StatusText = $"{TransferItem.Size(written)} of {TransferItem.Size(item.TotalBytes)}";
                    });
                    break;
                case "done":
                    lock (gate) { f.Finished = true; Monitor.PulseAll(gate); }
                    var secs = f.Clock.Elapsed.TotalSeconds;
                    var total = item.TotalBytes;
                    var saved = f.Sent > 0 && f.Wire < f.Sent * 0.9 ? $" · sent compressed ({f.Wire * 100 / f.Sent}%)" : "";
                    post(() =>
                    {
                        item.SentBytes = total;
                        item.Percent = 100;
                        item.State = TransferState.Done;
                        item.StatusText = $"Done · {TransferItem.Size(total)} in {secs:0.#} s{saved} · SHA-256 verified";
                    });
                    break;
                case "skip":
                    lock (gate) { f.Finished = true; Monitor.PulseAll(gate); }
                    post(() => { item.State = TransferState.Skipped; item.StatusText = "Skipped – the file already exists"; });
                    break;
                case "cancelled":
                    lock (gate) { f.Finished = true; Monitor.PulseAll(gate); }
                    post(() => { item.State = TransferState.Cancelled; item.StatusText = "Cancelled"; });
                    break;
                case "fail":
                    lock (gate) { f.Finished = true; Monitor.PulseAll(gate); }
                    var why = parts.Length > 2 ? parts[2] : "the remote computer reported an error";
                    post(() => Fail(item, why));
                    break;
            }
        }

        // One remote command per CommandBytes: the next one starts once the previous one has answered everything.
        PowerShell ps = null!;
        PSDataCollection<object> input = null!;
        IAsyncResult run = null!;
        long commandBytes = 0;
        void Start(bool first)
        {
            ps = PowerShell.Create();
            ps.Runspace = rs;
            ps.AddScript(StreamScript).AddParameter("Overwrite", overwrite).AddParameter("First", first);
            input = new PSDataCollection<object>();
            var output = new PSDataCollection<PSObject>();
            output.DataAdded += (_, e) => Answer(output[e.Index]?.ToString() ?? "");
            run = ps.BeginInvoke(input, output);
            commandBytes = 0;
        }
        void End()
        {
            input.Complete();
            try { ps.EndInvoke(run); }
            catch (RuntimeException ex) when (ex is not System.Management.Automation.Remoting.PSRemotingTransportException)
            {
                throw new RemoteCommandException(ex.ErrorRecord?.Exception?.Message ?? ex.Message);
            }
            finally { ps.Dispose(); }
        }
        void Send(object[] message, long bytes)
        {
            if (commandBytes > 0 && commandBytes + bytes > CommandBytes) { End(); Start(first: false); }
            input.Add(message);
            commandBytes += bytes;
        }

        Start(first: true);
        try
        {
            SendFiles();
            Send(["close", 0], 0);
            End();
        }
        catch
        {
            try { ps.Stop(); ps.Dispose(); } catch { /* already ended */ }
            throw;
        }
        foreach (var f in files.Where(f => !f.Finished))
            post(() => { if (f.Item.IsActive) Fail(f.Item, "no answer from the remote computer for this file"); });
        return;

        void SendFiles()
        {
            var buffer = new byte[ChunkSize];
            for (var i = 0; i < files.Length && !run.IsCompleted; i++)
            {
                var f = files[i];
                var item = f.Item;
                if (item.CancelRequested) { post(() => { item.State = TransferState.Cancelled; item.StatusText = "Cancelled"; }); f.Finished = true; continue; }
                post(() => { item.State = TransferState.Running; item.StatusText = "Starting…"; });
                f.Clock.Start();
                var tryCompress = !LikelyCompressed(item.LocalPath);

                if (item.TotalBytes <= ChunkSize)
                {
                    // Small file: begin, data and checksum in one message – no extra round trips per file.
                    var whole = File.ReadAllBytes(item.LocalPath);
                    var (small, zipped) = tryCompress ? Compress(whole) : (whole, false);
                    lock (gate) { f.Sent += whole.Length; f.Wire += small.Length; }
                    Send(["file", i, batch[i].Target, small, zipped, Convert.ToHexString(SHA256.HashData(whole))], small.Length);
                    continue;
                }
                Send(["begin", i, batch[i].Target], 0);

                var misses = 0;
                using var file = new FileStream(item.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.SequentialScan);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var aborted = false;
                int read;
                while ((read = ReadFull(file, buffer)) > 0)
                {
                    lock (gate)
                    {
                        // Bounded: wait while too much is unwritten on the remote side (or stop if this file already failed).
                        while (inFlight > WindowBytes && !f.Finished && !run.IsCompleted) Monitor.Wait(gate, 200);
                        if (f.Finished || run.IsCompleted) { aborted = true; break; }
                    }
                    if (item.CancelRequested) { Send(["abort", i], 0); aborted = true; break; }
                    var chunk = buffer.AsSpan(0, read).ToArray();
                    sha.AppendData(chunk);
                    var (payload, gzip) = tryCompress ? Compress(chunk) : (chunk, false);
                    if (tryCompress && !gzip && ++misses >= 2) tryCompress = false; // incompressible content: stop trying
                    lock (gate) { inFlight += read; f.Sent += read; f.Wire += payload.Length; }
                    Send(["data", i, payload, gzip], payload.Length);
                }
                if (aborted) continue;
                if (item.CancelRequested) { Send(["abort", i], 0); continue; }
                Send(["end", i, Convert.ToHexString(sha.GetHashAndReset())], 0);
            }
        }
    }

    /// <summary>Files that are already compressed: gzip would only cost time.</summary>
    internal static bool LikelyCompressed(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".zip" or ".7z" or ".rar" or ".gz" or ".tgz" or ".bz2" or ".xz" or ".zst" or ".cab" or ".msi" or ".msix" or ".msixbundle" or ".appx"
        or ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".heic" or ".mp3" or ".aac" or ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv"
        or ".docx" or ".xlsx" or ".pptx" or ".nupkg" or ".jar" or ".apk" or ".wim" or ".esd";

    /// <summary>gzip (fastest level); kept only if it saves at least 10 %.</summary>
    internal static (byte[] Payload, bool Gzip) Compress(byte[] chunk)
    {
        using var ms = new MemoryStream(chunk.Length / 2 + 64);
        using (var z = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) z.Write(chunk);
        return ms.Length < chunk.Length * 0.9 ? (ms.ToArray(), true) : (chunk, false);
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
                post(() =>
                {
                    item.SentBytes = g;
                    item.Percent = total == 0 ? 100 : g * 100.0 / total;
                    item.StatusText = $"{TransferItem.Size(g)} of {TransferItem.Size(total)}";
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
