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
    internal const int BatchFiles = 1000;
    internal const long BatchBytes = 512L * 1024 * 1024;

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

    internal static int ReadFull(Stream s, byte[] buffer)
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
/// Gets files from a remote computer the way <see cref="FileUploader"/> sends them, in the other direction: one remote
/// pipeline per batch streams every file in 1 MB chunks, gzip-compressed where that helps, without waiting for an
/// answer per chunk. This PC writes "&lt;name&gt;.remotehub-part", checks the SHA-256 of what it wrote against the
/// device's hash of what it read, and only then renames it. Nothing is written on the device, so a cancelled or broken
/// download needs no clean-up there; here, its part file is deleted.
/// </summary>
public static class FileDownloader
{
    /// <summary>
    /// Output messages, one array each: file|i|data|gzip|sha256 for a file that fits in one chunk; begin|i|length,
    /// data|i|bytes|gzip …, end|i|sha256 for a larger one; fail|i|reason. A failing file never stops the others. Every
    /// chunk is a new array: the output is serialised after the script has moved on. Compression follows the upload's
    /// rule – kept when it saves 10 %, given up for a file after two misses.
    /// </summary>
    private const string StreamScript = """
        param([string[]]$Paths, [bool[]]$Zip, [int]$Chunk)
        function Hex([byte[]]$h) { [BitConverter]::ToString($h).Replace('-', '') }
        function ReadUp($s, [int]$n) {
            $b = New-Object byte[] $n; $got = 0
            while ($got -lt $n) { $r = $s.Read($b, $got, $n - $got); if ($r -le 0) { break }; $got += $r }
            if ($got -lt $n) { $c = New-Object byte[] $got; [Array]::Copy($b, $c, $got); $b = $c }
            ,$b
        }
        function Pack([byte[]]$d) {
            $ms = New-Object IO.MemoryStream
            $z = New-Object IO.Compression.GZipStream($ms, [IO.Compression.CompressionLevel]::Fastest, $true)
            $z.Write($d, 0, $d.Length); $z.Dispose()
            if ($ms.Length -lt $d.Length * 0.9) { ,$ms.ToArray() }
        }
        for ($i = 0; $i -lt $Paths.Count; $i++) {
            $s = $null
            try {
                $s = [IO.File]::Open($Paths[$i], [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
                $gz = $Zip[$i]
                $d = ReadUp $s $Chunk
                if ($s.Position -ge $s.Length) {
                    $h = Hex ([Security.Cryptography.SHA256]::Create().ComputeHash($d))
                    $p = $null; if ($gz) { $p = Pack $d }
                    if ($null -ne $p) { ,@('file', $i, $p, $true, $h) } else { ,@('file', $i, $d, $false, $h) }
                    continue
                }
                ,@('begin', $i, $s.Length)
                $sha = [Security.Cryptography.SHA256]::Create(); $misses = 0
                while ($d.Length -gt 0) {
                    [void]$sha.TransformBlock($d, 0, $d.Length, $null, 0)
                    $p = $null
                    if ($gz) { $p = Pack $d; if ($null -eq $p -and ++$misses -ge 2) { $gz = $false } }
                    if ($null -ne $p) { ,@('data', $i, $p, $true) } else { ,@('data', $i, $d, $false) }
                    if ($d.Length -lt $Chunk) { break }
                    $d = ReadUp $s $Chunk
                }
                [void]$sha.TransformFinalBlock([byte[]]@(), 0, 0)
                ,@('end', $i, (Hex $sha.Hash))
            } catch {
                ,@('fail', $i, $_.Exception.Message)
            } finally {
                if ($s) { $s.Dispose() }
            }
        }
        """;

    /// <summary>What runs on the device; tests stand in for one that sends a damaged file or a compressed bomb.</summary>
    internal static string Script = StreamScript;

    /// <summary>
    /// Downloads the items (RemotePath → LocalPath). <paramref name="post"/> runs UI updates on the UI thread.
    /// Never throws: failures end up in the items' state.
    /// </summary>
    public static Task DownloadAsync(Func<Runspace> openRunspace, Func<Exception, string> explain, bool overwrite,
                                     IReadOnlyList<TransferItem> items, Action<Action> post) => Task.Run(() =>
    {
        Runspace? rs = null;
        try
        {
            foreach (var i in items) post(() => { if (i.State == TransferState.Queued) i.StatusText = "Connecting…"; });
            try { rs = openRunspace(); }
            catch (Exception ex)
            {
                var why = explain(ex);
                foreach (var i in items) post(() => { if (i.IsActive) Fail(i, why); });
                return;
            }

            // Without overwrite, files that are already here aren't requested at all.
            var work = new List<TransferItem>();
            foreach (var item in items)
            {
                if (!overwrite && File.Exists(item.LocalPath))
                    post(() => { item.State = TransferState.Skipped; item.StatusText = "Skipped – the file already exists"; });
                else { work.Add(item); post(() => { if (item.State == TransferState.Queued) item.StatusText = "Waiting…"; }); }
            }

            foreach (var batch in Batches(work))
            {
                try { GetBatch(rs, batch, overwrite, post); }
                catch (Exception ex)
                {
                    var why = ex is RemoteCommandException ? ex.Message : explain(ex);
                    foreach (var item in batch) post(() => { if (item.IsActive) Fail(item, why); });
                    if (rs.RunspaceStateInfo.State != RunspaceState.Opened)
                    {
                        foreach (var item in work) post(() => { if (item.IsActive) Fail(item, why); });
                        return;
                    }
                }
            }
        }
        finally { try { rs?.Dispose(); } catch { /* connection already gone */ } }
    });

    private static IEnumerable<List<TransferItem>> Batches(List<TransferItem> work)
    {
        var batch = new List<TransferItem>();
        long bytes = 0;
        foreach (var item in work)
        {
            if (batch.Count > 0 && (batch.Count >= FileUploader.BatchFiles || bytes + item.TotalBytes > FileUploader.BatchBytes)) { yield return batch; batch = []; bytes = 0; }
            batch.Add(item);
            bytes += item.TotalBytes;
        }
        if (batch.Count > 0) yield return batch;
    }

    /// <summary>One file of a batch while its chunks arrive.</summary>
    private sealed class Receiving(TransferItem item)
    {
        public readonly TransferItem Item = item;
        public FileStream? Part;
        public IncrementalHash? Sha;
        public long Got, Wire;
        public bool Finished;
        public readonly Stopwatch Clock = new();
        public string PartPath => Item.LocalPath + ".remotehub-part";
    }

    /// <summary>
    /// Gets one batch. A file cancelled while its chunks arrive stops the pipeline (the device only reads, so nothing
    /// is left there), and the rest of the batch follows in a new one.
    /// </summary>
    private static void GetBatch(Runspace rs, List<TransferItem> batch, bool overwrite, Action<Action> post)
    {
        var from = 0;
        while (from < batch.Count)
        {
            var files = batch.Skip(from).Where(i => !i.CancelRequested || Cancelled(i, post)).Select(i => new Receiving(i)).ToArray();
            if (files.Length == 0) return;
            var stoppedAt = GetFiles(rs, files, overwrite, post);
            if (stoppedAt is not { } stopped) return;
            from = batch.IndexOf(files[stopped].Item) + 1;
        }
    }

    /// <summary>Marks a file cancelled before its turn; false, so it isn't requested.</summary>
    private static bool Cancelled(TransferItem item, Action<Action> post)
    {
        post(() => { item.State = TransferState.Cancelled; item.StatusText = "Cancelled"; });
        return false;
    }

    /// <summary>Runs one pipeline for <paramref name="files"/>; the index of the file a cancel stopped it at, else null.</summary>
    private static int? GetFiles(Runspace rs, Receiving[] files, bool overwrite, Action<Action> post)
    {
        var gate = new object();
        var stopping = false;
        int? current = null, stopAt = null;

        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        ps.AddScript(Script)
            .AddParameter("Paths", files.Select(f => f.Item.RemotePath!).ToArray())
            .AddParameter("Zip", files.Select(f => !FileUploader.LikelyCompressed(f.Item.RemotePath!)).ToArray())
            .AddParameter("Chunk", FileUploader.ChunkSize);
        var input = new PSDataCollection<PSObject>();
        input.Complete();
        var output = new PSDataCollection<PSObject>();
        // Written as it arrives, on the thread that receives it: nothing piles up here, and a slow disk slows the
        // sender rather than filling memory.
        output.DataAdded += (_, _) =>
        {
            lock (gate)
            {
                // After the file a cancel stops at, nothing is written: the next pipeline gets those files again.
                foreach (var o in output.ReadAll())
                    if (!stopping && stopAt is null) Handle(Message(o));
            }
        };

        var run = ps.BeginInvoke(input, output);
        while (!run.AsyncWaitHandle.WaitOne(100))
        {
            lock (gate)
            {
                if (stopAt is null && current is { } c && files[c].Item.CancelRequested) stopAt = c;
                if (stopAt is null) continue;
                stopping = true;
            }
            try { ps.Stop(); } catch { /* ended meanwhile */ }
            break;
        }
        try { ps.EndInvoke(run); }
        // The cancel above: a local pipeline reports it as PipelineStoppedException, a remote one as its own error.
        catch (Exception) when (stopping && stopAt is not null) { }
        catch (RuntimeException ex) when (ex is not System.Management.Automation.Remoting.PSRemotingTransportException)
        {
            Abandon();
            throw new RemoteCommandException(ex.ErrorRecord?.Exception?.Message ?? ex.Message);
        }
        catch { Abandon(); throw; }

        lock (gate)
        {
            if (stopAt is { } s)
            {
                Drop(files[s]);
                var item = files[s].Item;
                post(() => { item.State = TransferState.Cancelled; item.StatusText = "Cancelled"; });
                return s;
            }
            foreach (var f in files.Where(f => !f.Finished))
            {
                Drop(f);
                post(() => { if (f.Item.IsActive) Fail(f.Item, "no answer from the remote computer for this file"); });
            }
        }
        return null;

        // A broken pipeline: no part file stays behind; the caller marks the items failed.
        void Abandon() { lock (gate) { stopping = true; foreach (var f in files) Drop(f); } }

        void Handle(object?[]? m)
        {
            if (m is not { Length: >= 2 } || m[1] is not int i || i < 0 || i >= files.Length) return;
            var f = files[i];
            if (f.Finished) return;
            var item = f.Item;
            try
            {
                switch (m[0] as string)
                {
                    case "file" when m.Length >= 5:
                        f.Finished = true;
                        if (item.CancelRequested) { post(() => { item.State = TransferState.Cancelled; item.StatusText = "Cancelled"; }); return; }
                        f.Clock.Start();
                        var whole = Unpack(m[2], m[3]);
                        f.Wire = (m[2] as byte[])!.Length;
                        if (!string.Equals(Convert.ToHexString(SHA256.HashData(whole)), m[4] as string, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Checksum mismatch – the file was damaged during the transfer.");
                        Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);
                        File.WriteAllBytes(f.PartPath, whole);
                        f.Got = whole.Length;
                        Finish(f, overwrite, post);
                        break;
                    case "begin" when m.Length >= 3:
                        if (item.CancelRequested) { stopAt ??= i; return; }
                        current = i;
                        f.Clock.Start();
                        Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);
                        f.Part = new FileStream(f.PartPath, FileMode.Create, FileAccess.Write, FileShare.None, FileUploader.ChunkSize);
                        f.Sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var length = Convert.ToInt64(m[2], System.Globalization.CultureInfo.InvariantCulture);
                        post(() => { item.State = TransferState.Running; item.StatusText = $"0 B of {TransferItem.Size(Math.Max(item.TotalBytes, length))}"; });
                        break;
                    case "data" when m.Length >= 4 && f.Part is { } part:
                        if (item.CancelRequested) { stopAt ??= i; return; } // stops at the next chunk, not the next check
                        var chunk = Unpack(m[2], m[3]);
                        part.Write(chunk);
                        f.Sha!.AppendData(chunk);
                        f.Got += chunk.Length;
                        f.Wire += (m[2] as byte[])!.Length;
                        var got = f.Got;
                        post(() =>
                        {
                            var total = Math.Max(item.TotalBytes, got);
                            item.SentBytes = got;
                            item.Percent = total == 0 ? 100 : got * 100.0 / total;
                            item.StatusText = $"{TransferItem.Size(got)} of {TransferItem.Size(total)}";
                        });
                        break;
                    case "end" when m.Length >= 3 && f.Part is { } ended:
                        f.Finished = true;
                        current = null;
                        ended.Dispose();
                        f.Part = null;
                        if (!string.Equals(Convert.ToHexString(f.Sha!.GetHashAndReset()), m[2] as string, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Checksum mismatch – the file was damaged during the transfer.");
                        Finish(f, overwrite, post);
                        break;
                    case "fail":
                        f.Finished = true;
                        if (current == i) current = null;
                        Drop(f);
                        var why = m.Length >= 3 && m[2] is string reason ? reason : "the remote computer reported an error";
                        post(() => Fail(item, why));
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidCastException or FormatException or OverflowException)
            {
                // This file only: a full disk, a damaged chunk or a malformed message. The others carry on.
                f.Finished = true;
                if (current == i) current = null;
                Drop(f);
                var why = ex.Message;
                post(() => Fail(item, why));
            }
        }
    }

    /// <summary>One output message as plain values: PowerShell wraps the array's items, and remoting deserialises them.</summary>
    private static object?[]? Message(PSObject? o) =>
        o?.BaseObject is System.Collections.IList list ? [.. list.Cast<object?>().Select(x => x is PSObject p ? p.BaseObject : x)] : null;

    /// <summary>
    /// A chunk as it was read on the device. A compressed one may unpack to at most one chunk: a device that sends
    /// more (a compromised one could send a few KB that unpack to gigabytes) is refused.
    /// </summary>
    internal static byte[] Unpack(object? payload, object? gzip)
    {
        if (payload is not byte[] bytes) throw new InvalidDataException("The remote computer sent a chunk that isn't data.");
        if (bytes.Length > FileUploader.ChunkSize)
            throw new InvalidDataException("The remote computer sent a chunk larger than 1 MB – refused.");
        if (gzip is not true) return bytes;
        using var z = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
        var buffer = new byte[FileUploader.ChunkSize + 1];
        var n = FileUploader.ReadFull(z, buffer);
        if (n > FileUploader.ChunkSize) throw new InvalidDataException("The remote computer sent a compressed chunk that unpacks to more than 1 MB – refused.");
        return buffer[..n];
    }

    /// <summary>The part file is complete and verified: it becomes the file.</summary>
    private static void Finish(Receiving f, bool overwrite, Action<Action> post)
    {
        File.Move(f.PartPath, f.Item.LocalPath, overwrite);
        f.Sha?.Dispose();
        f.Sha = null;
        var item = f.Item;
        var size = f.Got;
        var secs = f.Clock.Elapsed.TotalSeconds;
        var saved = size > 0 && f.Wire < size * 0.9 ? $" · sent compressed ({f.Wire * 100 / size}%)" : "";
        post(() =>
        {
            item.SentBytes = size;
            item.Percent = 100;
            item.State = TransferState.Done;
            item.StatusText = $"Done · {TransferItem.Size(size)} in {secs:0.#} s{saved} · SHA-256 verified";
        });
    }

    /// <summary>A file that won't be finished: its part file goes.</summary>
    private static void Drop(Receiving f)
    {
        try { f.Part?.Dispose(); } catch { /* already gone */ }
        f.Part = null;
        f.Sha?.Dispose();
        f.Sha = null;
        try { File.Delete(f.PartPath); } catch { /* best effort */ }
    }

    private static void Fail(TransferItem i, string why)
    {
        i.State = TransferState.Failed;
        i.StatusText = "Failed – " + why;
    }
}

/// <summary>A command in the remote session failed (message is shown to the user).</summary>
public sealed class RemoteCommandException(string message) : Exception(message);
