using System.Text;
using RemoteHub.Models;
using RemoteHub.Services;
using RemoteHub.ViewModels;

namespace RemoteHub.Tests;

public class CoreTests
{
    private static string TempFile(string ext) => Path.Combine(Path.GetTempPath(), $"rdpm-test-{Guid.NewGuid():N}{ext}");

    [Fact]
    public void Options_and_credentials_inherit_through_folders()
    {
        var data = new AppData();
        data.Defaults.RedirectDrives = false;
        var root = new Folder { Name = "Prod", InheritCredentials = false, Credentials = { Username = "admin", Domain = "CORP", Password = Secret.From("pw") } };
        root.Options.RedirectDrives = true;
        var sub = new Folder { Name = "Web", Parent = root };
        sub.Options.Resize = DisplayResize.SmartSizing;
        var conn = new Connection { Name = "web01", Host = "web01", Parent = sub };
        root.Children.Add(sub); sub.Children.Add(conn); data.Items.Add(root);

        var o = Inheritance.ResolveOptions(conn, data);
        Assert.True(o.RedirectDrives);
        Assert.Equal(DisplayResize.SmartSizing, o.Resize);
        Assert.True(o.RedirectClipboard); // built-in default
        var (cred, src) = Inheritance.ResolveCredentials(conn, data);
        Assert.Equal("admin", cred.Username);
        Assert.Contains("Prod", src);

        // Override on the connection wins
        conn.Options.RedirectDrives = false;
        conn.InheritCredentials = false;
        conn.Credentials = new CredentialSet { Username = "local" };
        Assert.False(Inheritance.ResolveOptions(conn, data).RedirectDrives);
        Assert.Equal("local", Inheritance.ResolveCredentials(conn, data).Credentials.Username);
    }

    [Fact]
    public void Opened_rdp_file_lists_what_it_would_do()
    {
        var path = TempFile(".rdp");
        File.WriteAllText(path, string.Join("\r\n", "full address:s:evil.example.com", "authentication level:i:0",
            "enablecredsspsupport:i:0", "drivestoredirect:s:*", "redirectsmartcards:i:1"), Encoding.Unicode);
        try
        {
            var risks = RdpFileParser.Risks(RdpFileParser.Parse(path));
            Assert.Contains(risks, r => r.Contains("default credentials"));
            Assert.Contains(risks, r => r.Contains("identity"));
            Assert.Contains(risks, r => r.Contains("Network Level Authentication"));
            Assert.Contains(risks, r => r.Contains("drives"));
            Assert.Contains(risks, r => r.Contains("smart cards"));
            Assert.DoesNotContain(risks, r => r.Contains("printers"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Parses_utf16_rdp_file()
    {
        var path = TempFile(".rdp");
        File.WriteAllText(path, string.Join("\r\n",
            "screen mode id:i:1", "desktopwidth:i:1600", "desktopheight:i:900", "session bpp:i:24",
            "full address:s:srv42.contoso.local:3390", @"username:s:CONTOSO\jdoe", "redirectclipboard:i:1",
            "drivestoredirect:s:*", "audiomode:i:2", "keyboardhook:i:0", "authentication level:i:2"), Encoding.Unicode);
        try
        {
            var c = RdpFileParser.Parse(path);
            Assert.Equal("srv42.contoso.local", c.Host);
            Assert.Equal(3390, c.Port);
            Assert.False(c.InheritCredentials);
            Assert.Equal("jdoe", c.Credentials.Username);
            Assert.Equal("CONTOSO", c.Credentials.Domain);
            Assert.Equal("1600x900", c.Options.FixedResolution);
            Assert.Equal(DisplayResize.Fixed, c.Options.Resize);
            Assert.Equal(24, c.Options.ColorDepth);
            Assert.True(c.Options.RedirectDrives);
            Assert.Equal(AudioMode.None, c.Options.Audio);
            Assert.False(c.Options.ForwardWindowsKeys);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("host", "host", 3389)]
    [InlineData("host:3390", "host", 3390)]
    [InlineData("10.0.0.5:4000", "10.0.0.5", 4000)]
    [InlineData("[fe80::1]:3391", "fe80::1", 3391)]
    [InlineData("fe80::1", "fe80::1", 3389)]
    public void Splits_addresses(string input, string host, int port) =>
        Assert.Equal((host, port), RdpFileParser.SplitAddress(input));

    private static AppData BigData(int count)
    {
        var data = new AppData();
        for (int f = 0; f < 10; f++)
        {
            var folder = new Folder { Name = $"Site {f}", InheritCredentials = false, Credentials = { Username = $"svc{f}", Password = Secret.From("S3cret!") } };
            for (int i = 0; i < count / 10; i++)
                folder.Children.Add(new Connection { Name = $"Server {f}-{i}", Host = $"srv{f}-{i}.lab.local", Parent = folder });
            data.Items.Add(folder);
        }
        return data;
    }

    [Fact]
    public void Backup_roundtrip_and_wrong_password()
    {
        var path = TempFile(".rdpmbackup");
        try
        {
            Backup.Export(BigData(150), path, "correct horse");
            Assert.DoesNotContain("S3cret!", File.ReadAllText(path));
            var back = Backup.Import(path, "correct horse");
            Assert.Equal(150, back.Items.OfType<Folder>().Sum(f => f.Children.Count));
            var c = (Connection)((Folder)back.Items[3]).Children[7];
            Assert.Same(back.Items[3], c.Parent);
            Assert.Equal("S3cret!", Inheritance.ResolveCredentials(c, back).Credentials.Password?.Reveal());
            Assert.Throws<UnauthorizedAccessException>(() => Backup.Import(path, "wrong"));
        }
        finally { File.Delete(path); }
    }

    /// <summary>Rewrites a backup file's JSON (the envelope's fields are plain properties).</summary>
    private static void Rewrite(string path, Action<System.Text.Json.Nodes.JsonObject> change)
    {
        var env = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(env);
        File.WriteAllText(path, env.ToJsonString());
    }

    [Fact]
    public void A_backup_with_a_section_removed_moved_or_relabelled_is_refused()
    {
        var notes = new RemoteHub.Data.NotesExport { Notebooks = [new("b1", "Book", 0)], Pages = [new("p1", "s1", "Page", "body", "body", 0, 0, 0)] };
        var path = TempFile(".rhbackup");
        var other = TempFile(".rhbackup");
        try
        {
            void Fresh() => Backup.Export(BigData(10), path, "correct horse", notes);
            Fresh();
            Assert.Equal(Backup.AssociatedDataVersion, System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["Version"]!.GetValue<int>());
            Backup.Import(path, "correct horse", out var back);
            Assert.Equal("body", Assert.Single(back!.Pages).Body);

            // the notes taken out: the settings alone would import without them
            Rewrite(path, e => { e.Remove("NotesNonce"); e.Remove("NotesTag"); e.Remove("NotesData"); });
            Assert.Throws<UnauthorizedAccessException>(() => Backup.Import(path, "correct horse"));

            // the sections swapped within the file
            Fresh();
            Rewrite(path, e =>
            {
                foreach (var (a, b) in new[] { ("Nonce", "NotesNonce"), ("Tag", "NotesTag"), ("Data", "NotesData") })
                    (e[a], e[b]) = (e[b]!.DeepClone(), e[a]!.DeepClone());
            });
            Assert.Throws<UnauthorizedAccessException>(() => Backup.Import(path, "correct horse"));

            // relabelled as an older version (no associated data then): still doesn't open
            Fresh();
            Rewrite(path, e => e["Version"] = 3);
            Assert.Throws<UnauthorizedAccessException>(() => Backup.Import(path, "correct horse"));

            // the notes of another backup with the same password: another salt, another key
            Fresh();
            Backup.Export(BigData(10), other, "correct horse", new RemoteHub.Data.NotesExport { Notebooks = [new("b2", "Other", 0)] });
            var foreign = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(other))!.AsObject();
            Rewrite(path, e => { foreach (var f in new[] { "NotesNonce", "NotesTag", "NotesData" }) e[f] = foreign[f]!.DeepClone(); });
            Assert.Throws<UnauthorizedAccessException>(() => Backup.Import(path, "correct horse"));
        }
        finally { File.Delete(path); File.Delete(other); }
    }

    [Fact]
    public void An_older_backup_without_associated_data_still_imports()
    {
        // as RemoteHub wrote it before version 4 (and RdpManager's .rdpmbackup): sections sealed without associated data
        var path = TempFile(".rdpmbackup");
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var key = Backup.DeriveKey("correct horse", salt, 600_000);
        var (nonce, tag, data) = Backup.Seal(key, SettingsStore.ToJson(BigData(10)));
        try
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                Format = "rdpmanager-backup", Version = 1, Kdf = "PBKDF2-SHA256", Iterations = 600_000,
                Salt = Convert.ToBase64String(salt), Nonce = nonce, Tag = tag, Data = data,
            }));
            Assert.Equal(10, Backup.Import(path, "correct horse").Items.OfType<Folder>().Sum(f => f.Children.Count));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Move_prevents_cycles_and_reorders()
    {
        var vm = new MainViewModel(new AppData());
        var a = new Folder { Name = "A" }; var b = new Folder { Name = "B" };
        var c1 = new Connection { Name = "c1" }; var c2 = new Connection { Name = "c2" };
        vm.Add(a, null); vm.Add(b, a); vm.Add(c1, null); vm.Add(c2, null);
        Assert.False(vm.Move(a, b));          // folder into its own child
        Assert.True(vm.Move(c2, null, 0));    // reorder to top
        Assert.Same(c2, vm.Items[0]);
        Assert.True(vm.Move(c1, b));
        Assert.Same(b, c1.Parent);
        Assert.Contains(c1, b.Children);
    }
}
