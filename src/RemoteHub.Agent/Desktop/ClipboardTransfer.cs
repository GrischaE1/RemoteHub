namespace RemoteHub.Agent;

/// <summary>
/// Clipboard images and files between RemoteHub and the device (compiled into both): what is sent (<see cref="Plan"/>,
/// <see cref="Chunks"/>) and how it is put together on the other side (<see cref="TransferReceiver"/>). Files go one
/// after the other in <see cref="DesktopProtocol.TransferChunkBytes"/> pieces; folders are created, never followed
/// outside the copied ones.
/// </summary>
public static class ClipboardTransfer
{
    /// <summary>What to send, with the source file of each item (null for folders and images in memory).</summary>
    public sealed record Outgoing(DesktopTransfer Transfer, string?[] Sources, byte[]? Image = null);

    /// <summary>An image (PNG or BMP bytes) as a transfer.</summary>
    public static Outgoing Image(int id, byte[] data, bool png) =>
        new(new DesktopTransfer(id, DesktopTransferKind.Image, [new DesktopTransferItem(png ? "image.png" : "image.bmp", data.Length)], data.Length), [null], data);

    /// <summary>Copied files and folders (folders with everything in them); null with a reason when it is too much.</summary>
    public static Outgoing? Files(int id, IReadOnlyList<string> paths, out string? error)
    {
        error = null;
        var items = new List<DesktopTransferItem>();
        var sources = new List<string?>();
        long total = 0;
        bool Add(DesktopTransferItem item, string? source)
        {
            items.Add(item);
            sources.Add(source);
            total += item.Size;
            return items.Count <= DesktopProtocol.MaxTransferItems && total <= DesktopProtocol.MaxTransferBytes;
        }
        try
        {
            foreach (var path in paths)
            {
                var name = Path.GetFileName(path.TrimEnd('\\', '/'));
                if (name.Length == 0) continue;
                if (File.Exists(path))
                {
                    if (!Add(new DesktopTransferItem(name, new FileInfo(path).Length), path)) return TooMuch(out error);
                }
                else if (Directory.Exists(path))
                {
                    if (!Add(new DesktopTransferItem(name, 0, Folder: true), null)) return TooMuch(out error);
                    // No reparse points (junctions, links): only what is really inside the copied folder.
                    var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
                    foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
                    {
                        var relative = name + "/" + Path.GetRelativePath(path, entry.FullName).Replace('\\', '/');
                        var ok = entry is FileInfo f
                            ? Add(new DesktopTransferItem(relative, f.Length), f.FullName)
                            : Add(new DesktopTransferItem(relative, 0, Folder: true), null);
                        if (!ok) return TooMuch(out error);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "The copied files can't be read: " + ex.Message;
            return null;
        }
        if (items.Count == 0) { error = "Nothing to copy."; return null; }
        return new Outgoing(new DesktopTransfer(id, DesktopTransferKind.Files, [.. items], total), [.. sources]);
    }

    private static Outgoing? TooMuch(out string? error)
    {
        error = $"Too much to copy through the remote desktop (at most {DesktopProtocol.MaxTransferBytes / (1024 * 1024)} MB and {DesktopProtocol.MaxTransferItems} files) – use the file transfer instead.";
        return null;
    }

    /// <summary>The data messages: every file in order, in chunks.</summary>
    public static IEnumerable<byte[]> Chunks(Outgoing o)
    {
        var id = o.Transfer.Id;
        long offset = 0;
        if (o.Image is { } image)
        {
            for (var i = 0; i < image.Length; i += DesktopProtocol.TransferChunkBytes)
            {
                var n = Math.Min(DesktopProtocol.TransferChunkBytes, image.Length - i);
                yield return DesktopWire.TransferData(id, i, image.AsSpan(i, n));
            }
            yield break;
        }
        var buffer = new byte[DesktopProtocol.TransferChunkBytes];
        for (var i = 0; i < o.Sources.Length; i++)
        {
            var item = o.Transfer.Items[i];
            if (item.Folder || o.Sources[i] is not { } source || item.Size == 0) continue;
            using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var left = item.Size;
            while (left > 0)
            {
                var read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read <= 0) throw new IOException($"{item.Path} got shorter while it was copied.");
                yield return DesktopWire.TransferData(id, offset, buffer.AsSpan(0, read));
                offset += read;
                left -= read;
            }
        }
    }
}

/// <summary>
/// Puts a transfer together: an image in memory, or files below <c>folder</c> (paths made safe, nothing written outside
/// it). Data must come in order; anything else fails the transfer.
/// </summary>
public sealed class TransferReceiver : IDisposable
{
    private readonly DesktopTransfer _t;
    private readonly string? _folder;
    private readonly MemoryStream? _image;
    private readonly string[] _targets;
    private int _item = -1;
    private long _received, _itemLeft;
    private FileStream? _file;

    public TransferReceiver(DesktopTransfer t, string? folder)
    {
        // Checked on the receiving side too, whatever the sender (or the service in between) checked.
        if (t.Items is null || t.Items.Length == 0 || t.Items.Length > DesktopProtocol.MaxTransferItems || t.Total < 0 || t.Total > DesktopProtocol.MaxTransferBytes ||
            t.Items.Any(i => i is null || i.Size < 0 || i.Folder && i.Size != 0) || t.Items.Sum(i => i.Size) != t.Total ||
            t.Kind == DesktopTransferKind.Image && (t.Items.Length != 1 || t.Items[0].Folder))
            throw new InvalidDataException($"Too much at once or incomplete (at most {DesktopProtocol.MaxTransferBytes / (1024 * 1024)} MB and {DesktopProtocol.MaxTransferItems} files).");
        _t = t;
        _folder = folder;
        if (t.Kind == DesktopTransferKind.Image)
        {
            _image = new MemoryStream((int)Math.Min(t.Total, DesktopProtocol.MaxTransferBytes));
            _targets = [];
        }
        else
        {
            if (folder is null) throw new InvalidOperationException("No folder for the copied files.");
            Directory.CreateDirectory(folder);
            var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            _targets = t.Items.Select(i =>
            {
                var relative = DesktopWire.SafeRelativePath(i.Path) ?? throw new InvalidDataException($"Unusable file name: {i.Path}");
                var full = Path.GetFullPath(Path.Combine(folder, relative));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Unusable file name: {i.Path}");
                return full;
            }).ToArray();
        }
        Advance();
    }

    public DesktopTransfer Transfer => _t;
    public bool Complete => _received == _t.Total && _item >= _t.Items.Length;
    public double Progress => _t.Total == 0 ? 1 : _received / (double)_t.Total;

    /// <summary>The image bytes (after <see cref="Complete"/>).</summary>
    public byte[] Image => _image?.ToArray() ?? [];

    /// <summary>The copied files and folders at the top level (what goes on the clipboard).</summary>
    /// <summary>The local path of every item, in the transfer's order (what a paste of virtual files reads).</summary>
    public IReadOnlyList<string> Targets => _targets;

    public IReadOnlyList<string> TopLevel => _t.Items.Select((it, i) => (it, i)).Where(x => !x.it.Path.Replace('\\', '/').Trim('/').Contains('/'))
        .Select(x => _targets[x.i]).ToList();

    public void Add(long offset, ReadOnlySpan<byte> data)
    {
        if (offset != _received || _received + data.Length > _t.Total) throw new InvalidDataException("The copied data came out of order.");
        if (_image is not null)
        {
            _image.Write(data);
            _received += data.Length;
            if (_received == _t.Total) _item = _t.Items.Length;
            return;
        }
        while (data.Length > 0)
        {
            if (_file is null) throw new InvalidDataException("More data than files.");
            var n = (int)Math.Min(data.Length, _itemLeft);
            _file.Write(data[..n]);
            data = data[n..];
            _received += n;
            _itemLeft -= n;
            if (_itemLeft == 0) Advance();
        }
    }

    /// <summary>Next item: folders are created, empty files written; stops at the first file with data.</summary>
    private void Advance()
    {
        _file?.Dispose();
        _file = null;
        if (_image is not null) { if (_t.Total == 0) _item = _t.Items.Length; return; }
        while (++_item < _t.Items.Length)
        {
            var item = _t.Items[_item];
            var target = _targets[_item];
            if (item.Folder) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            _file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            _itemLeft = item.Size;
            if (_itemLeft > 0) return;
            _file.Dispose();
            _file = null;
        }
    }

    /// <summary>The image is a PNG (else BMP bytes), by the name the sender gave it.</summary>
    public bool IsPng => _t.Items is [{ Path: var p }] && p.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    /// <summary>Ends the transfer. Unfinished files are removed again – the folder is this transfer's own.</summary>
    public void Dispose()
    {
        _file?.Dispose();
        _file = null;
        _image?.Dispose();
        if (_folder is not null && !Complete)
            try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); } catch { /* in use: the temp folder's own clean-up */ }
    }
}
