using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Remoting.Shares;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>Network shares: model and storage, path rules, WebDAV answers, transfers, and SMB through Windows.</summary>
[Collection("SettingsStore")]
public class NetworkShareTests : IDisposable
{
    private const string Marker = "Sh4re-plain-text-marker";
    private static string TestSecret(string name) => string.Concat(name, "-", Marker);
    private static Secret StoredPassword(string name) => Secret.From(TestSecret(name))!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rh-share-" + Guid.NewGuid().ToString("N")[..8]);

    public NetworkShareTests()
    {
        Directory.CreateDirectory(_dir);
        StoreReset.Clean();
    }

    public void Dispose()
    {
        StoreReset.Clean();
        try { Directory.Delete(_dir, true); } catch { /* still in use */ }
    }

    // ------------------------------------------------------------------ model and storage

    [Fact]
    public void Shares_are_stored_with_their_server_and_the_password_sealed()
    {
        var data = new AppData();
        var nuc = new Connection { Name = "NUC10", Host = "hv02" };
        nuc.Shares.Add(new NetworkShare { Name = "share", Path = "share", InheritCredentials = false, Credentials = { Username = "lab", Password = StoredPassword("Share") } });
        nuc.Shares.Add(new NetworkShare { Name = "nas", Protocol = ShareProtocol.Sftp, Host = "nas.lab", Port = 2222, TrustedFingerprint = "SHA256:abc" });
        data.Items.Add(nuc);
        SettingsStore.RelinkParents(data.Items, null);
        _ = SettingsStore.Load(); // like a start: reads the (clean) local config, so a shared folder of an earlier test is gone
        SettingsStore.Save(data);
        Data.LocalDb.Use((c, _) => Assert.Equal(0L, Data.LocalDb.Scalar(c, "SELECT count(*) FROM nodes WHERE data LIKE '%Password%'")));
        Data.LocalDb.Close();

        var back = Assert.IsType<Connection>(SettingsStore.Load().Items.Single());
        Assert.Equal(["share", "nas"], back.Shares.Select(s => s.Name));
        Assert.All(back.Shares, s => Assert.Same(back, s.Server));
        Assert.Equal(TestSecret("Share"), back.Shares[0].Credentials.Password?.Reveal());
        Assert.Equal(@"\\hv02\share", back.Shares[0].Address);
        Assert.Equal("sftp://nas.lab:2222", back.Shares[1].Address);
        Assert.Equal("SHA256:abc", back.Shares[1].TrustedFingerprint);
    }

    [Fact]
    public void A_share_uses_its_servers_account_unless_it_has_its_own()
    {
        var data = new AppData();
        var saved = new SavedCredential { Name = "NAS", Username = "nasadmin" };
        data.SavedCredentials.Add(saved);
        var server = new Connection { Name = "HV02", Host = "hv02", InheritCredentials = false, Credentials = { Username = "admin" } };
        var inherit = new NetworkShare { Name = "a", Server = server };
        var own = new NetworkShare { Name = "b", Server = server, InheritCredentials = false, CredentialId = saved.Id };
        server.Shares.Add(inherit);
        server.Shares.Add(own);
        data.Items.Add(server);

        var (c1, src1) = Inheritance.ResolveCredentials(inherit, data);
        Assert.Equal("admin", c1.Username);
        Assert.Equal("server \"HV02\"", src1);
        Assert.Equal("nasadmin", Inheritance.ResolveCredentials(own, data).Credentials.Username);
        Assert.Equal(1, Inheritance.UsageCount(data, saved.Id, data.Items));
    }

    [Fact]
    public void Duplicating_a_server_gives_its_shares_new_ids()
    {
        var vm = new ViewModels.MainViewModel(new AppData());
        var c = new Connection { Name = "HV02", Host = "hv02" };
        c.Shares.Add(new NetworkShare { Name = "share", Path = "share" });
        vm.Add(c, null);
        var copy = Assert.IsType<Connection>(vm.Duplicate(c));
        Assert.Equal("share", copy.Shares.Single().Name);
        Assert.NotEqual(c.Shares[0].Id, copy.Shares[0].Id);
        Assert.Same(copy, copy.Shares[0].Server);
    }

    // ------------------------------------------------------------------ shares on their own (no server above them)

    [Fact]
    public void A_share_on_its_own_is_stored_in_its_folder_with_the_password_sealed()
    {
        var data = new AppData();
        var folder = new Folder { Name = "NAS" };
        folder.Children.Add(new NetworkShare { Name = "media", Host = "nas", Path = "media", InheritCredentials = false, Credentials = { Username = "lab", Password = StoredPassword("Alone") } });
        folder.Children.Add(new Connection { Name = "HV02", Host = "hv02" });
        data.Items.Add(folder);
        data.Items.Add(new NetworkShare { Name = "files", Protocol = ShareProtocol.Sftp, Host = "files.lab", Path = "/data" });
        SettingsStore.RelinkParents(data.Items, null);
        _ = SettingsStore.Load();
        SettingsStore.Save(data);
        Data.LocalDb.Use((c, _) =>
        {
            Assert.Equal(0L, Data.LocalDb.Scalar(c, "SELECT count(*) FROM nodes WHERE data LIKE '%Password%'"));
            Assert.Equal(2L, Data.LocalDb.Scalar(c, "SELECT count(*) FROM nodes WHERE kind = 'share'"));
        });
        Data.LocalDb.Close();

        var back = SettingsStore.Load();
        var media = Assert.IsType<NetworkShare>(Assert.IsType<Folder>(back.Items[0]).Children[0]);
        Assert.True(media.IsStandalone);
        Assert.Same(back.Items[0], media.Parent);
        Assert.Equal(TestSecret("Alone"), media.Credentials.Password?.Reveal());
        Assert.Equal(@"\\nas\media", media.Address);
        var files = Assert.IsType<NetworkShare>(back.Items[1]);
        Assert.Null(files.Parent);
        Assert.Equal("sftp://files.lab/data", files.Address);
    }

    [Fact]
    public void A_share_on_its_own_takes_the_account_of_its_folders()
    {
        var data = new AppData();
        var folder = new Folder { Name = "NAS", InheritCredentials = false, Credentials = { Username = "nasuser" } };
        var share = new NetworkShare { Name = "media", Host = "nas", Path = "media" };
        folder.Children.Add(share);
        data.Items.Add(folder);
        SettingsStore.RelinkParents(data.Items, null);

        var (cred, source) = Inheritance.ResolveCredentials(share, data);
        Assert.Equal("nasuser", cred.Username);
        Assert.Equal("folder \"NAS\"", source);

        folder.InheritCredentials = true;
        Assert.Equal("none", Inheritance.ResolveCredentials(share, data).Source);
    }

    [Fact]
    public void A_share_on_its_own_is_found_moved_and_duplicated_like_a_connection()
    {
        var vm = new ViewModels.MainViewModel(new AppData());
        var target = new Folder { Name = "Storage" };
        vm.Add(target, null);
        var share = new NetworkShare { Name = "media", Host = "nas01", Path = "media" };
        vm.Add(share, null);

        var hit = Assert.Single(NodeSearch.Find(vm.AllNodes(), "nas01"));
        Assert.Same(share, hit.Node);
        Assert.Contains(@"\\nas01\media", hit.Subtitle);

        Assert.True(vm.Move(share, target));
        Assert.Same(target, share.Parent);
        share.IsSelected = true;
        vm.SelectedNode = share;
        Assert.Same(target, vm.TargetFolderForNew());

        var copy = Assert.IsType<NetworkShare>(vm.Duplicate(share));
        Assert.NotEqual(share.Id, copy.Id);
        Assert.Same(target, copy.Parent);
        Assert.True(copy.IsStandalone);
        Assert.Equal("nas01", copy.Host);
    }

    [Theory]
    [InlineData(ShareProtocol.Smb, FtpSecurity.Explicit, true, 445)]
    [InlineData(ShareProtocol.Sftp, FtpSecurity.Explicit, true, 22)]
    [InlineData(ShareProtocol.Ftp, FtpSecurity.Explicit, true, 21)]
    [InlineData(ShareProtocol.Ftp, FtpSecurity.Implicit, true, 990)]
    [InlineData(ShareProtocol.WebDav, FtpSecurity.Explicit, true, 443)]
    [InlineData(ShareProtocol.WebDav, FtpSecurity.Explicit, false, 80)]
    public void Default_ports(ShareProtocol p, FtpSecurity ftp, bool https, int port) => Assert.Equal(port, NetworkShare.DefaultPort(p, ftp, https));

    [Theory]
    [InlineData("share", "share", "")]
    [InlineData(@"\share\sub\deeper\", "share", @"sub\deeper")]
    [InlineData("/share/sub", "share", "sub")]
    [InlineData("", "", "")]
    public void Smb_path_is_share_name_then_folder(string path, string share, string sub) =>
        Assert.Equal((share, sub), ShareConnector.SplitSmbPath(path));

    [Fact]
    public void The_address_follows_the_servers_host()
    {
        var server = new Connection { Name = "HV02", Host = "hv02" };
        var share = new NetworkShare { Name = "s", Path = "share", Server = server };
        server.Shares.Add(share);
        var seen = new List<string>();
        share.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NetworkShare.Address)) seen.Add(share.Address); };
        server.Host = "hv02.lab.local";
        Assert.Equal([@"\\hv02.lab.local\share"], seen);
    }

    // ------------------------------------------------------------------ path rules

    [Theory]
    [InlineData("/a/b", "/a")]
    [InlineData("/a", "/")]
    [InlineData("/", "/")]
    public void Up_never_goes_above_the_top_of_a_posix_share(string path, string parent) =>
        Assert.Equal(parent, new LocalFolderShare(_dir).Parent(path));

    [Fact]
    public void Up_stops_at_the_smb_share()
    {
        using var fs = new LocalFolderShare(_dir, top: @"\\srv\share", separator: '\\');
        Assert.Equal(@"\\srv\share", fs.Parent(@"\\srv\share\a"));
        Assert.Equal(@"\\srv\share", fs.Parent(@"\\srv\share"));
        Assert.Equal(@"\\srv\share", fs.Parent(@"\\other\x\y"));
        Assert.Equal(@"\\srv\share\a", fs.Parent(@"\\srv\share\a\b"));
        Assert.Equal(@"\\srv\share\a\b", fs.Normalize(@"\\srv\share/a/b\"));
        Assert.Equal(@"\\srv\share\x", fs.Normalize("x"));
    }

    [Fact]
    public void Webdav_parent_stays_below_the_base_path()
    {
        using var fs = new LocalFolderShare(_dir, top: "/remote.php/dav/files/me");
        Assert.Equal("/remote.php/dav/files/me", fs.Parent("/remote.php/dav/files/me/Docs"));
        Assert.Equal("/remote.php/dav/files/me", fs.Parent("/remote.php/dav/files/me"));
        Assert.Equal("/remote.php/dav/files/me/Docs", fs.Parent("/remote.php/dav/files/me/Docs/x.txt"));
    }

    // ------------------------------------------------------------------ WebDAV

    [Fact]
    public void Webdav_listing_is_read_from_the_multistatus_answer()
    {
        const string xml = """
            <?xml version="1.0"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>/dav/Docs/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
              <d:response><d:href>/dav/Docs/My%20File.txt</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>1234</d:getcontentlength>
                <d:getlastmodified>Mon, 12 Oct 2026 10:00:00 GMT</d:getlastmodified></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
                <d:propstat><d:prop><d:quota/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat></d:response>
              <d:response><d:href>https://cloud.example/dav/Docs/Sub/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
            </d:multistatus>
            """;
        var list = WebDavShare.ParseMultistatus(XDocument.Parse(xml), new Uri("https://cloud.example/"));
        Assert.Equal(["/dav/Docs", "/dav/Docs/My File.txt", "/dav/Docs/Sub"], list.Select(e => e.FullPath));
        Assert.Equal([EntryKind.Folder, EntryKind.File, EntryKind.Folder], list.Select(e => e.Kind));
        Assert.Equal(1234, list[1].Size);
        Assert.Equal(new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc), list[1].Modified);
        Assert.Equal("My File.txt", list[1].Name);
    }

    // ------------------------------------------------------------------ transfers

    private static string Source(string root, string relative, int bytes)
    {
        var p = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var data = new byte[bytes];
        new Random(bytes).NextBytes(data);
        File.WriteAllBytes(p, data);
        return p;
    }

    private static void Post(Action a) => a();

    [Fact]
    public async Task Upload_creates_folders_and_leaves_no_part_files()
    {
        var local = Path.Combine(_dir, "local");
        var share = Path.Combine(_dir, "share");
        Directory.CreateDirectory(share);
        Source(local, @"pack\a.bin", 3 * 1024 * 1024 + 5);
        Source(local, @"pack\sub\b.txt", 10);
        var items = FileUploader.Expand([Path.Combine(local, "pack")]).Select(x => new TransferItem(x.Local, x.Relative, "HV02 › share")).ToList();

        await ShareTransfer.UploadAsync(() => new LocalFolderShare(share), "/in", false, items, Post);

        Assert.All(items, i => Assert.Equal(TransferState.Done, i.State));
        Assert.Equal(File.ReadAllBytes(Path.Combine(local, @"pack\a.bin")), File.ReadAllBytes(Path.Combine(share, @"in\pack\a.bin")));
        Assert.True(File.Exists(Path.Combine(share, @"in\pack\sub\b.txt")));
        Assert.Empty(Directory.GetFiles(share, "*.remotehub-part", SearchOption.AllDirectories));
        Assert.Equal("/in/pack/sub/b.txt", items.Single(i => i.FileName == "b.txt").RemotePath);
    }

    [Fact]
    public async Task Upload_skips_or_replaces_existing_files()
    {
        var local = Path.Combine(_dir, "local");
        var share = Path.Combine(_dir, "share");
        Directory.CreateDirectory(share);
        var src = Source(local, "x.bin", 100);
        File.WriteAllText(Path.Combine(share, "x.bin"), "old");

        var skip = new TransferItem(src, "x.bin", "s");
        await ShareTransfer.UploadAsync(() => new LocalFolderShare(share), "/", false, [skip], Post);
        Assert.Equal(TransferState.Skipped, skip.State);
        Assert.Equal("old", File.ReadAllText(Path.Combine(share, "x.bin")));

        var replace = new TransferItem(src, "x.bin", "s");
        await ShareTransfer.UploadAsync(() => new LocalFolderShare(share), "/", true, [replace], Post);
        Assert.Equal(TransferState.Done, replace.State);
        Assert.Equal(100, new FileInfo(Path.Combine(share, "x.bin")).Length);
    }

    [Fact]
    public async Task Download_checks_the_size_and_renames_at_the_end()
    {
        var share = Path.Combine(_dir, "share");
        Source(share, @"docs\r.bin", 2 * 1024 * 1024);
        var target = Path.Combine(_dir, "down", "r.bin");
        var fs = new LocalFolderShare(share);
        var files = fs.Expand(["/docs"]);
        Assert.Equal(@"docs\r.bin", files.Single().Relative);
        var item = TransferItem.Download(files[0].FullPath, target, files[0].Relative, "s", files[0].Size);

        await ShareTransfer.DownloadAsync(() => new LocalFolderShare(share), false, [item], Post);

        Assert.Equal(TransferState.Done, item.State);
        Assert.Equal(File.ReadAllBytes(Path.Combine(share, @"docs\r.bin")), File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".remotehub-part"));
    }

    [Fact]
    public async Task A_cancelled_upload_leaves_nothing_behind_and_the_next_file_still_goes()
    {
        var local = Path.Combine(_dir, "local");
        var share = Path.Combine(_dir, "share");
        Directory.CreateDirectory(share);
        var big = new TransferItem(Source(local, "big.bin", 4 * 1024 * 1024), "big.bin", "s");
        var small = new TransferItem(Source(local, "small.bin", 10), "small.bin", "s");
        var opened = 0;
        // Cancel as soon as the first bytes arrive.
        big.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TransferItem.SentBytes) && big.SentBytes > 0) big.Cancel(); };

        await ShareTransfer.UploadAsync(() => { opened++; return new LocalFolderShare(share); }, "/", false, [big, small], Post);

        Assert.Equal(TransferState.Cancelled, big.State);
        Assert.Equal(TransferState.Done, small.State);
        Assert.Equal(["small.bin"], Directory.GetFiles(share).Select(Path.GetFileName));
        Assert.Equal(2, opened); // after a stop the protocol may be mid-transfer: a fresh connection for the next file
    }

    [Fact]
    public async Task A_share_that_cant_be_reached_fails_each_file_with_the_reason()
    {
        var local = Path.Combine(_dir, "local");
        var items = new[] { new TransferItem(Source(local, "a.bin", 1), "a.bin", "s"), new TransferItem(Source(local, "b.bin", 1), "b.bin", "s") };
        await ShareTransfer.UploadAsync(() => throw new ShareException("The user name or password is incorrect."), "/", false, items, Post);
        Assert.All(items, i => Assert.Equal("Failed – The user name or password is incorrect.", i.StatusText));
    }

    // ------------------------------------------------------------------ SMB through Windows (this computer's admin share)

    [SkippableFact]
    public async Task Windows_smb_lists_writes_renames_and_deletes()
    {
        var folder = Path.Combine(_dir, "smb");
        Directory.CreateDirectory(folder);
        var drive = folder[0];
        var sub = folder[3..];
        Skip.IfNot(Directory.Exists($@"\\localhost\{drive}$"), "the admin share of this computer isn't reachable");

        using var fs = new WindowsSmbShare("localhost", drive + "$", sub, new CredentialSet());
        Assert.Equal($@"\\localhost\{drive}$\{sub}", fs.Start);
        Assert.Contains("your Windows account", fs.Description);
        fs.CreateFolder(fs.Join(fs.Start, "new"));
        var local = Source(_dir, "up.bin", 300_000);
        var up = new TransferItem(local, "up.bin", "s");
        await ShareTransfer.UploadAsync(() => new WindowsSmbShare("localhost", drive + "$", sub, new CredentialSet()), fs.Join(fs.Start, "new"), false, [up], Post);
        Assert.Equal(TransferState.Done, up.State);

        var listed = fs.List(fs.Join(fs.Start, "new"));
        Assert.Equal(300_000, Assert.Single(listed).Size);
        fs.Move(listed[0].FullPath, fs.Join(fs.Join(fs.Start, "new"), "renamed.bin"));
        Assert.True(File.Exists(Path.Combine(folder, "new", "renamed.bin")));
        Assert.Null(fs.Stat(fs.Join(fs.Start, "missing")));
        Assert.Throws<ShareException>(() => fs.List(fs.Join(fs.Start, "missing")));
        fs.Delete(fs.Join(fs.Start, "new"), folder: true);
        Assert.False(Directory.Exists(Path.Combine(folder, "new")));
        Assert.Equal(@$"\\localhost\{drive}$", fs.Parent(fs.Parent(fs.Top)));
    }

    /// <summary>A local folder that behaves like a "/"-share (or a UNC share) – for the parts that don't depend on a protocol.</summary>
    private sealed class LocalFolderShare(string root, string top = "/", char separator = '/') : ShareFileSystem
    {
        public override string Start => top;
        public override string Top => top;
        public override char Separator => separator;
        public override string Description => "test";

        private string Local(string path)
        {
            var rel = path.Length > top.Length && path.StartsWith(top) ? path[top.Length..] : path == top ? "" : path;
            return Path.Combine(root, rel.Trim('/', '\\').Replace('/', '\\'));
        }

        public override List<FileEntry> List(string folder)
        {
            var dir = new DirectoryInfo(Local(folder));
            if (!dir.Exists) throw new ShareException($"Folder not found: {folder}");
            return dir.EnumerateFileSystemInfos().Select(e => new FileEntry(e.Name, Join(folder, e.Name),
                e is DirectoryInfo ? EntryKind.Folder : EntryKind.File, e is FileInfo f ? f.Length : 0, e.LastWriteTimeUtc, false)).ToList();
        }

        public override void CreateFolder(string path) => Directory.CreateDirectory(Local(path));
        public override void DeleteFile(string path) => File.Delete(Local(path));
        public override void DeleteEmptyFolder(string path) => Directory.Delete(Local(path));
        public override void Move(string from, string to) => File.Move(Local(from), Local(to));
        public override Stream OpenRead(string path) => File.OpenRead(Local(path));
        public override Stream Create(string path) => File.Create(Local(path));
        public override void Dispose() { }
    }
}
