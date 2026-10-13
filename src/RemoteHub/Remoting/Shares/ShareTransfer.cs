using System.Diagnostics;

namespace RemoteHub.Remoting.Shares;

/// <summary>
/// Copies files between this PC and a network share over a connection of their own (closed when done). Each file is
/// written as "&lt;name&gt;.remotehub-part", its size checked, and only then renamed – a broken transfer never leaves a
/// half file under the real name. After a lost connection the next file connects again.
/// </summary>
public static class ShareTransfer
{
    private const string PartSuffix = ".remotehub-part";

    /// <summary>Uploads <paramref name="items"/> into <paramref name="folder"/> on the share. Never throws.</summary>
    public static Task UploadAsync(Func<ShareFileSystem> open, string folder, bool overwrite, IReadOnlyList<TransferItem> items, Action<Action> post) =>
        Run(open, items, post, (fs, item, created) =>
        {
            var target = fs.JoinRelative(fs.Normalize(folder), item.RelativePath);
            post(() => item.RemotePath = target);
            var exists = fs.Stat(target);
            if (exists is not null && (!overwrite || exists.Kind == EntryKind.Folder))
            {
                post(() => { item.State = TransferState.Skipped; item.StatusText = exists.Kind == EntryKind.Folder ? "Skipped – a folder has that name" : "Skipped – the file already exists"; });
                return;
            }
            EnsureFolder(fs, fs.Parent(target), created);
            var part = target + PartSuffix;
            var sw = Stopwatch.StartNew();
            try
            {
                using (var source = new FileStream(item.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                    fs.Upload(source, item.TotalBytes, part, Progress(item, post), () => item.CancelRequested);
                post(() => item.StatusText = "Checking…");
                var written = fs.Stat(part)?.Size ?? -1;
                if (written != item.TotalBytes)
                    throw new ShareException($"The share has {(written < 0 ? "no file" : TransferItem.Size(written))} instead of {TransferItem.Size(item.TotalBytes)} – the transfer was incomplete.");
                if (exists is not null) fs.DeleteFile(target);
                fs.Move(part, target);
            }
            catch
            {
                TryQuietly(() => fs.DeleteFile(part));
                throw;
            }
            Done(item, sw, post);
        });

    /// <summary>Downloads the items (RemotePath → LocalPath). Never throws.</summary>
    public static Task DownloadAsync(Func<ShareFileSystem> open, bool overwrite, IReadOnlyList<TransferItem> items, Action<Action> post) =>
        Run(open, items, post, (fs, item, _) =>
        {
            if (File.Exists(item.LocalPath) && !overwrite)
            {
                post(() => { item.State = TransferState.Skipped; item.StatusText = "Skipped – the file already exists"; });
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);
            var part = item.LocalPath + PartSuffix;
            var sw = Stopwatch.StartNew();
            try
            {
                long got;
                using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                {
                    fs.Download(item.RemotePath!, target, Progress(item, post), () => item.CancelRequested);
                    got = target.Length;
                }
                if (got != item.TotalBytes)
                    throw new ShareException($"Got {TransferItem.Size(got)} instead of {TransferItem.Size(item.TotalBytes)} – the file changed during the transfer or the transfer was incomplete.");
                File.Move(part, item.LocalPath, overwrite: true);
            }
            catch
            {
                TryQuietly(() => File.Delete(part));
                throw;
            }
            Done(item, sw, post);
        });

    private static Task Run(Func<ShareFileSystem> open, IReadOnlyList<TransferItem> items, Action<Action> post,
                            Action<ShareFileSystem, TransferItem, HashSet<string>> one) => Task.Run(() =>
    {
        ShareFileSystem? fs = null;
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var item in items)
            {
                if (item.CancelRequested) { post(() => { item.State = TransferState.Cancelled; item.StatusText = "Cancelled"; }); continue; }
                post(() => { item.State = TransferState.Running; item.StatusText = fs is null ? "Connecting…" : "Starting…"; });
                try
                {
                    fs ??= open();
                    one(fs, item, created);
                }
                catch (OperationCanceledException)
                {
                    post(() => { item.State = TransferState.Cancelled; item.StatusText = "Cancelled"; });
                    Reset(ref fs); // the protocol may still be busy with the stopped transfer
                }
                catch (Exception ex)
                {
                    var why = ShareSession.Explain(ex);
                    post(() => { item.State = TransferState.Failed; item.StatusText = "Failed – " + why; });
                    if (ex is not ShareException) Reset(ref fs);
                }
            }
        }
        finally { Reset(ref fs); }
    });

    private static void Reset(ref ShareFileSystem? fs)
    {
        var f = fs;
        fs = null;
        TryQuietly(() => f?.Dispose());
    }

    /// <summary>Creates the missing folders above a file (each one only once per batch).</summary>
    private static void EnsureFolder(ShareFileSystem fs, string folder, HashSet<string> created)
    {
        if (created.Contains(folder) || fs.SamePath(folder, fs.Top)) return;
        var e = fs.Stat(folder);
        if (e is null)
        {
            EnsureFolder(fs, fs.Parent(folder), created);
            fs.CreateFolder(folder);
        }
        else if (e.Kind != EntryKind.Folder) throw new ShareException($"A file is in the way of the folder {folder}");
        created.Add(folder);
    }

    /// <summary>Progress for the transfers panel, at most every 100 ms.</summary>
    private static Action<long> Progress(TransferItem item, Action<Action> post)
    {
        var last = Stopwatch.StartNew();
        var first = true;
        return done =>
        {
            if (!first && last.ElapsedMilliseconds < 100 && done < item.TotalBytes) return;
            first = false;
            last.Restart();
            var total = Math.Max(item.TotalBytes, done);
            post(() =>
            {
                item.SentBytes = done;
                item.Percent = total == 0 ? 100 : done * 100.0 / total;
                item.StatusText = $"{TransferItem.Size(done)} of {TransferItem.Size(total)}";
            });
        };
    }

    private static void Done(TransferItem item, Stopwatch sw, Action<Action> post)
    {
        var secs = sw.Elapsed.TotalSeconds;
        var size = item.TotalBytes;
        post(() =>
        {
            item.SentBytes = size;
            item.Percent = 100;
            item.State = TransferState.Done;
            item.StatusText = $"Done · {TransferItem.Size(size)} in {secs:0.#} s · size checked";
        });
    }

    private static void TryQuietly(Action a) { try { a(); } catch { /* best effort */ } }
}
