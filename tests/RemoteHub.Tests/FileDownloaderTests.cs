using System.Collections.Generic;
using System.IO.Compression;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Remoting;

namespace RemoteHub.Tests;

/// <summary>
/// Downloads streamed from the device (<see cref="FileDownloader"/>), with a local runspace standing in for it – the
/// same script runs over WinRM and the RemoteHub Agent.
/// </summary>
[Collection("FileDownloader")]
public sealed class FileDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rh-download-" + Guid.NewGuid().ToString("N"));
    private string Src => Path.Combine(_root, "src");
    private string Dest => Path.Combine(_root, "dest");
    private int _pipelines;

    public FileDownloaderTests() => Directory.CreateDirectory(Src);

    public void Dispose()
    {
        FileDownloader.Script = DeviceScript;
        try { Directory.Delete(_root, true); } catch (IOException) { /* a scanner still has a file open */ }
    }

    private static readonly string DeviceScript = FileDownloader.Script;

    /// <summary>A fresh runspace, as RemoteHub opens one per transfer; counts the pipelines run in it.</summary>
    private Runspace Device()
    {
        var rs = RunspaceFactory.CreateRunspace();
        rs.Open();
        rs.AvailabilityChanged += (_, e) => { if (e.RunspaceAvailability == RunspaceAvailability.Busy) Interlocked.Increment(ref _pipelines); };
        return rs;
    }

    private string Source(string relative, byte[] content)
    {
        var path = Path.Combine(Src, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private List<TransferItem> Items() =>
        [.. Directory.EnumerateFiles(Src, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).Select(f =>
        {
            var relative = Path.GetRelativePath(Src, f);
            return TransferItem.Download(f, Path.Combine(Dest, relative), relative, "test", new FileInfo(f).Length);
        })];

    private Task Download(List<TransferItem> items, bool overwrite = false, Action<Action>? post = null) =>
        FileDownloader.DownloadAsync(Device, ex => ex.Message, overwrite, items, post ?? (a => a()));

    private static byte[] Noise(int length) { var b = new byte[length]; RandomNumberGenerator.Fill(b); return b; }

    private static byte[] Text(int length) =>
        System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, length / 40 + 1).Select(i => $"{i:D8} INFO service started normally\r\n")))[..length];

    private string[] PartFiles() => Directory.Exists(Dest) ? Directory.GetFiles(Dest, "*.remotehub-part", SearchOption.AllDirectories) : [];

    [Fact]
    public async Task A_folder_with_300_small_files_downloads_in_one_pipeline()
    {
        for (var i = 0; i < 300; i++) Source($@"logs\{i:D3}.log", System.Text.Encoding.UTF8.GetBytes($"line {i}"));
        var items = Items();

        await Download(items);

        Assert.All(items, i => Assert.Equal(TransferState.Done, i.State));
        Assert.Equal(1, _pipelines); // not one or two commands per file, as before
        Assert.Equal("line 299", File.ReadAllText(Path.Combine(Dest, @"logs\299.log")));
        Assert.Empty(PartFiles());
    }

    [Fact]
    public async Task A_file_larger_than_1_MB_is_written_as_a_part_file_and_renamed_after_the_hash_matches()
    {
        var content = Noise(FileUploader.ChunkSize * 3 + 5);
        Source("big.bin", content);
        var items = Items();
        var target = items[0].LocalPath;
        var partSeen = false;

        await Download(items, post: a =>
        {
            a();
            if (items[0].State == TransferState.Running && items[0].SentBytes > 0)
                partSeen |= File.Exists(target + ".remotehub-part") && !File.Exists(target);
        });

        Assert.True(partSeen, "while the chunks arrive, only the part file is there");
        Assert.Equal(TransferState.Done, items[0].State);
        Assert.Equal(content, File.ReadAllBytes(target));
        Assert.Contains("SHA-256 verified", items[0].StatusText);
        Assert.Empty(PartFiles());
    }

    [Fact]
    public async Task A_hash_mismatch_leaves_no_file_behind()
    {
        Source("big.bin", new byte[5]);
        Source("small.txt", new byte[2]);
        // A device (or a connection) that damages what it sends: the hashes don't match the data.
        FileDownloader.Script = """
            param($Paths, $Zip, $Chunk)
            ,@('file', 0, [byte[]](1, 2, 3, 4, 5), $false, '00')
            ,@('begin', 1, [long]2)
            ,@('data', 1, [byte[]](1, 2), $false)
            ,@('end', 1, '00')
            """;
        var items = Items();

        await Download(items);

        Assert.All(items, i =>
        {
            Assert.Equal(TransferState.Failed, i.State);
            Assert.Contains("Checksum mismatch", i.StatusText);
            Assert.False(File.Exists(i.LocalPath));
        });
        Assert.Empty(PartFiles());
    }

    [Fact]
    public async Task Cancel_removes_the_part_file_and_the_next_file_still_comes()
    {
        Source("a-big.bin", Noise(FileUploader.ChunkSize * 8));
        Source("b-next.txt", System.Text.Encoding.UTF8.GetBytes("next"));
        var items = Items();
        var big = items[0];

        await Download(items, post: a =>
        {
            a();
            if (big.SentBytes >= FileUploader.ChunkSize && !big.CancelRequested) big.Cancel();
        });

        Assert.Equal(TransferState.Cancelled, big.State);
        Assert.False(File.Exists(big.LocalPath));
        Assert.Empty(PartFiles());
        Assert.Equal(TransferState.Done, items[1].State);
        Assert.Equal("next", File.ReadAllText(items[1].LocalPath));
        Assert.Equal(2, _pipelines); // the cancel stopped the first; the rest of the batch came in a second one
    }

    [Fact]
    public async Task Text_goes_compressed_and_random_data_raw_and_both_count_their_original_size()
    {
        var text = Text(FileUploader.ChunkSize * 3 + 100);
        var noise = Noise(FileUploader.ChunkSize * 3 + 100);
        Source("app.log", text);
        Source("random.bin", noise);
        var items = Items();

        await Download(items);

        Assert.All(items, i => Assert.Equal(TransferState.Done, i.State));
        Assert.Equal(text, File.ReadAllBytes(items[0].LocalPath));
        Assert.Equal(noise, File.ReadAllBytes(items[1].LocalPath));
        Assert.Contains("sent compressed", items[0].StatusText);
        Assert.DoesNotContain("compressed", items[1].StatusText); // two misses, then raw
        // The speed and the overall average count what was downloaded, not what crossed the wire.
        Assert.Equal(text.Length, items[0].SentBytes);
        Assert.Equal(noise.Length, items[1].SentBytes);
    }

    [Fact]
    public async Task A_compressed_chunk_that_unpacks_beyond_the_chunk_size_is_refused()
    {
        static byte[] Gzip(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var z = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(data);
            return ms.ToArray();
        }
        Assert.Equal(new byte[1000], FileDownloader.Unpack(Gzip(new byte[1000]), true));
        Assert.Equal(FileUploader.ChunkSize, FileDownloader.Unpack(Gzip(new byte[FileUploader.ChunkSize]), true).Length);
        Assert.Throws<InvalidDataException>(() => FileDownloader.Unpack(Gzip(new byte[FileUploader.ChunkSize + 1]), true));
        Assert.Throws<InvalidDataException>(() => FileDownloader.Unpack(new byte[FileUploader.ChunkSize + 1], false));
        Assert.Throws<InvalidDataException>(() => FileDownloader.Unpack("not data", false));

        // End to end: a device that sends 2 KB which unpack to 64 MB.
        Source("bomb.bin", new byte[1]);
        FileDownloader.Script = """
            param($Paths, $Zip, $Chunk)
            $ms = New-Object IO.MemoryStream
            $z = New-Object IO.Compression.GZipStream($ms, [IO.Compression.CompressionMode]::Compress, $true)
            $zeros = New-Object byte[] 1048576
            for ($k = 0; $k -lt 64; $k++) { $z.Write($zeros, 0, $zeros.Length) }
            $z.Dispose()
            ,@('begin', 0, [long]67108864)
            ,@('data', 0, $ms.ToArray(), $true)
            ,@('end', 0, '00')
            """;
        var items = Items();

        await Download(items);

        Assert.Equal(TransferState.Failed, items[0].State);
        Assert.Contains("refused", items[0].StatusText);
        Assert.False(File.Exists(items[0].LocalPath));
        Assert.Empty(PartFiles());
    }

    [Fact]
    public async Task Skip_existing_requests_nothing_for_existing_files()
    {
        Source("a.txt", "new"u8.ToArray());
        Source(@"sub\b.txt", "new"u8.ToArray());
        await Download(Items());
        Assert.Equal(1, _pipelines);

        _pipelines = 0;
        var again = Items();
        await Download(again);
        Assert.All(again, i => Assert.Equal(TransferState.Skipped, i.State));
        Assert.Equal(0, _pipelines); // nothing was asked of the device

        File.WriteAllText(Path.Combine(Dest, "a.txt"), "old");
        var replace = Items();
        await Download(replace, overwrite: true);
        Assert.All(replace, i => Assert.Equal(TransferState.Done, i.State));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Dest, "a.txt")));
    }

    [Fact]
    public async Task A_file_the_device_cant_read_fails_alone()
    {
        Source("a-locked.bin", new byte[100]);
        Source("b-fine.txt", "fine"u8.ToArray());
        var items = Items();

        using (new FileStream(items[0].RemotePath!, FileMode.Open, FileAccess.Read, FileShare.None))
            await Download(items);

        Assert.Equal(TransferState.Failed, items[0].State);
        Assert.False(File.Exists(items[0].LocalPath));
        Assert.Equal(TransferState.Done, items[1].State);
        Assert.Equal("fine", File.ReadAllText(items[1].LocalPath));
        Assert.Empty(PartFiles());
    }
}
