using System.Collections.Generic;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography;
using System.Threading.Tasks;
using RemoteHub.Remoting;

namespace RemoteHub.Tests;

/// <summary>The remote file scripts, run against a local runspace (same scripts as over WinRM).</summary>
public class RemoteFsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rdpm-fs-" + Guid.NewGuid().ToString("N"));
    private readonly Runspace _rs = RunspaceFactory.CreateRunspace();

    public RemoteFsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs", "old"));
        File.WriteAllText(Path.Combine(_root, "logs", "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "logs", "old", "b.txt"), "bb");
        File.WriteAllBytes(Path.Combine(_root, "zeta.bin"), new byte[10]);
        File.WriteAllText(Path.Combine(_root, "Alpha.txt"), "x");
        File.SetAttributes(Path.Combine(_root, "Alpha.txt"), FileAttributes.Hidden);
        _rs.Open();
    }

    public void Dispose()
    {
        _rs.Dispose();
        foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }

    [Fact]
    public void Remote_listing_shows_only_the_folder_folders_first()
    {
        var l = RemoteFs.ListRemote(_rs, _root);
        Assert.Equal(_root, l.Path);
        Assert.Equal(["logs", "Alpha.txt", "zeta.bin"], l.Entries.Select(e => e.Name));
        Assert.Equal(EntryKind.Folder, l.Entries[0].Kind);
        Assert.True(l.Entries[1].Hidden);
        Assert.Equal(10, l.Entries[2].Size);
        Assert.Equal(Path.Combine(_root, "zeta.bin"), l.Entries[2].FullPath);
        Assert.NotNull(l.Entries[2].Modified);
    }

    [Fact]
    public void Local_and_remote_listings_agree()
    {
        var remote = RemoteFs.ListRemote(_rs, _root);
        var local = RemoteFs.ListLocal(_root);
        Assert.Equal(remote.Entries.Select(e => (e.Name, e.Kind, e.Size, e.Hidden)), local.Entries.Select(e => (e.Name, e.Kind, e.Size, e.Hidden)));
    }

    [Fact]
    public void Empty_path_lists_drives_and_bad_path_fails()
    {
        var drives = RemoteFs.ListRemote(_rs, "");
        Assert.Equal("", drives.Path);
        Assert.Contains(drives.Entries, d => d.Kind == EntryKind.Drive && d.Name.StartsWith("C:", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<RemoteCommandException>(() => RemoteFs.ListRemote(_rs, Path.Combine(_root, "missing")));
        Assert.Equal(Environment.ExpandEnvironmentVariables("%TEMP%").TrimEnd('\\'), RemoteFs.ListRemote(_rs, "%TEMP%").Path.TrimEnd('\\'));
    }

    [Theory]
    [InlineData(@"C:\Users\x", @"C:\Users")]
    [InlineData(@"C:\Users", @"C:\")]
    [InlineData(@"C:\", "")]
    [InlineData("", "")]
    public void Parent_goes_up_to_the_drive_list(string path, string parent) => Assert.Equal(parent, RemoteFs.Parent(path));

    [Fact]
    public void Existing_names_and_folder_expansion()
    {
        Assert.Equal(["logs", "zeta.bin"], RemoteFs.ExistingRemote(_rs, _root, ["logs", "nope.txt", "zeta.bin"]));
        var files = RemoteFs.ExpandRemote(_rs, [Path.Combine(_root, "logs"), Path.Combine(_root, "zeta.bin")])
            .Select(f => (f.Size, f.Relative)).OrderBy(f => f.Relative).ToList();
        Assert.Equal([(1L, @"logs\a.txt"), (2L, @"logs\old\b.txt"), (10L, "zeta.bin")], files);
        Assert.EndsWith("newdir", RemoteFs.MkdirRemote(_rs, Path.Combine(_root, "newdir")));
        Assert.True(Directory.Exists(Path.Combine(_root, "newdir")));
    }

    [Fact]
    public async Task Download_verifies_and_skips_existing()
    {
        var big = new byte[FileUploader.ChunkSize * 2 + 777];
        RandomNumberGenerator.Fill(big);
        File.WriteAllBytes(Path.Combine(_root, "big.bin"), big);
        var dest = Path.Combine(_root, "dest");
        Runspace Local() { var rs = RunspaceFactory.CreateRunspace(); rs.Open(); return rs; }

        List<TransferItem> Items() => RemoteFs.ExpandRemote(_rs, [Path.Combine(_root, "big.bin"), Path.Combine(_root, "logs")])
            .Select(f => TransferItem.Download(f.FullPath, Path.Combine(dest, f.Relative), f.Relative, "test", f.Size)).ToList();

        var first = Items();
        await FileDownloader.DownloadAsync(Local, ex => ex.Message, false, first, a => a());
        Assert.All(first, i => Assert.Equal(TransferState.Done, i.State));
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(dest, "big.bin")));
        Assert.Equal("bb", File.ReadAllText(Path.Combine(dest, "logs", "old", "b.txt")));
        Assert.Empty(Directory.GetFiles(dest, "*.remotehub-part", SearchOption.AllDirectories));
        Assert.True(first[0].IsDownload);
        Assert.Contains("this PC", first[0].Destination);

        var second = Items();
        await FileDownloader.DownloadAsync(Local, ex => ex.Message, false, second, a => a());
        Assert.All(second, i => Assert.Equal(TransferState.Skipped, i.State));
    }

    [Fact]
    public void Remote_rename_and_delete()
    {
        var renamed = RemoteFs.RenameRemote(_rs, Path.Combine(_root, "zeta.bin"), "omega.bin");
        Assert.Equal(Path.Combine(_root, "omega.bin"), renamed);
        Assert.True(File.Exists(renamed));
        RemoteFs.DeleteRemote(_rs, [renamed, Path.Combine(_root, "logs")]);
        Assert.False(File.Exists(renamed));
        Assert.False(Directory.Exists(Path.Combine(_root, "logs")));
        Assert.Throws<RemoteCommandException>(() => RemoteFs.DeleteRemote(_rs, [@"C:\"]));
        Assert.True(Directory.Exists(@"C:\Windows"));
    }

    [Theory]
    [InlineData("report.txt", true)]
    [InlineData("a:b", false)]
    [InlineData("sub\\x", false)]
    [InlineData(" lead", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    public void Names_are_validated(string name, bool ok) => Assert.Equal(ok, RemoteFs.IsValidName(name));

    [Fact]
    public void Remote_drag_data_is_plain_text_and_round_trips()
    {
        var s = new FileBrowserSession(new RemoteHub.Models.Connection { Name = "c", Host = "h" }, null);
        var text = new RemoteHub.Views.RemoteDrag(s, [@"C:\a.txt", @"C:\b"]).Encode();
        var back = RemoteHub.Views.RemoteDrag.Decode(text)!;
        Assert.Same(s, back.Source);
        Assert.Equal([@"C:\a.txt", @"C:\b"], back.Paths);
        s.Dispose();
        Assert.Null(RemoteHub.Views.RemoteDrag.Decode(text)); // closed tab: drop is ignored
    }
    [Fact]
    public void All_local_addresses_are_valid_firewall_sources()
    {
        var all = RemotingSetup.AllLocalAddresses("10.99.99.99");
        Assert.Equal("10.99.99.99", all[0]);
        Assert.True(RemotingSetup.TryParseSources(string.Join(", ", all), out _, out var error), error);
        Assert.DoesNotContain(all, a => a.StartsWith("127.") || a == "::1" || a.StartsWith("fe80", StringComparison.OrdinalIgnoreCase) || a.StartsWith("169.254."));
    }
}
