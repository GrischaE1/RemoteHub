using System.Collections.Generic;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography;
using System.Threading.Tasks;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Services;

namespace RemoteHub.Tests;

public class PsRemotingTests
{
    [Theory]
    [InlineData("admin", "CORP", "srv01.corp.local", @"CORP\admin")]
    [InlineData("admin", null, "srv01", "admin")]
    [InlineData("admin@corp.local", null, "srv01", "admin@corp.local")]
    [InlineData(@"CORP\admin", null, "srv01", @"CORP\admin")]
    [InlineData("localadmin", ".", "srv01.corp.local", @"srv01\localadmin")]
    [InlineData("localadmin", ".", "10.0.0.5", @".\localadmin")]
    public void User_name_is_formatted_for_WinRM(string user, string? domain, string host, string expected) =>
        Assert.Equal(expected, PsRemoting.UserName(new CredentialSet { Username = user, Domain = domain }, host));

    [Fact]
    public void No_username_means_current_Windows_user()
    {
        Assert.Null(PsRemoting.UserName(new CredentialSet(), "srv01"));
        Assert.False(PsRemoting.NeedsPassword(new CredentialSet()));
        Assert.True(PsRemoting.NeedsPassword(new CredentialSet { Username = "a" }));
        Assert.False(PsRemoting.NeedsPassword(new CredentialSet { Username = "a", Password = "x" }));
    }

    [Fact]
    public void Connection_info_uses_http_or_https_port()
    {
        var http = PsRemoting.ConnectionInfo(new PsTarget("srv01", false, new CredentialSet()));
        Assert.Equal("http://srv01:5985/wsman", http.ConnectionUri.ToString());
        Assert.Null(http.Credential);
        var https = PsRemoting.ConnectionInfo(new PsTarget("srv01", true, new CredentialSet { Username = "u", Domain = "D", Password = "p" }));
        Assert.Equal("https://srv01:5986/wsman", https.ConnectionUri.ToString());
        Assert.Equal(@"D\u", https.Credential.UserName);
        Assert.Equal("p", https.Credential.GetNetworkCredential().Password);
    }

    [Fact]
    public void Check_is_off_by_default_and_inherits()
    {
        var data = new AppData();
        var folder = new Folder { Name = "Servers" };
        var conn = new Connection { Name = "c", Host = "h", Parent = folder };
        folder.Children.Add(conn);
        data.Items.Add(folder);
        Assert.False(Inheritance.ResolveOptions(conn, data).PsRemoting);
        folder.Options.PsRemoting = true;
        Assert.True(Inheritance.ResolveOptions(conn, data).PsRemoting);
        Assert.False(Inheritance.ResolveOptions(conn, data).PsUseSsl);
    }

    [Fact]
    public void Remoting_status_is_saved_with_the_connection()
    {
        var data = new AppData();
        var when = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);
        data.Items.Add(new Connection
        {
            Name = "c", Host = "h",
            PsRemoting = new PsRemotingStatus { State = PsRemotingState.Available, CheckedUtc = when, Message = "PowerShell 5.1 on H", UploadFolder = @"C:\Temp" },
            PsChecking = true,
        });
        var back = (Connection)SettingsStore.FromJson(SettingsStore.ToJson(data)).Items[0];
        Assert.Equal(PsRemotingState.Available, back.PsRemoting!.State);
        Assert.Equal(when, back.PsRemoting.CheckedUtc);
        Assert.Equal(@"C:\Temp", back.PsRemoting.UploadFolder);
        Assert.False(back.PsChecking); // runtime only
    }

    [Fact]
    public void TrustedHosts_error_gets_an_explanation()
    {
        var ex = new System.Management.Automation.Remoting.PSRemotingTransportException(
            "The WinRM client cannot process the request ... the destination machine must be added to the TrustedHosts configuration setting.");
        var text = PsRemoting.Explain(ex, new PsTarget("10.0.0.5", false, new CredentialSet()));
        Assert.Contains("HTTPS", text);
        Assert.Contains("TrustedHosts", text);
    }

    [Fact]
    public void Dropped_folders_keep_their_structure()
    {
        var root = Path.Combine(Path.GetTempPath(), "rdpm-expand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "logs", "old"));
        File.WriteAllText(Path.Combine(root, "logs", "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "logs", "old", "b.txt"), "b");
        File.WriteAllText(Path.Combine(root, "single.txt"), "s");
        try
        {
            var list = FileUploader.Expand([Path.Combine(root, "logs"), Path.Combine(root, "single.txt")]).Select(x => x.Relative).OrderBy(x => x).ToList();
            Assert.Equal([@"logs\a.txt", @"logs\old\b.txt", "single.txt"], list);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>The upload protocol, run against a local runspace (same scripts as over WinRM).</summary>
    [Fact]
    public async Task Upload_writes_verifies_and_skips_existing_files()
    {
        var src = Path.Combine(Path.GetTempPath(), "rdpm-up-src-" + Guid.NewGuid().ToString("N"));
        var dst = Path.Combine(Path.GetTempPath(), "rdpm-up-dst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        var big = new byte[FileUploader.ChunkSize * 2 + 12345];
        RandomNumberGenerator.Fill(big);
        File.WriteAllBytes(Path.Combine(src, "big.bin"), big);
        File.WriteAllBytes(Path.Combine(src, "empty.txt"), []);
        File.WriteAllText(Path.Combine(src, "sub", "note.txt"), "hello");
        try
        {
            List<TransferItem> Items() => FileUploader.Expand([Path.Combine(src, "big.bin"), Path.Combine(src, "empty.txt"), Path.Combine(src, "sub")])
                .Select(x => new TransferItem(x.Local, x.Relative, "test")).ToList();
            Runspace Local() { var rs = RunspaceFactory.CreateRunspace(); rs.Open(); return rs; }

            var first = Items();
            await FileUploader.UploadAsync(Local, ex => ex.Message, dst, overwrite: false, first, a => a());
            Assert.All(first, i => Assert.Equal(TransferState.Done, i.State));
            Assert.Equal(big, File.ReadAllBytes(Path.Combine(dst, "big.bin")));
            Assert.Empty(File.ReadAllBytes(Path.Combine(dst, "empty.txt")));
            Assert.Equal("hello", File.ReadAllText(Path.Combine(dst, "sub", "note.txt")));
            Assert.Empty(Directory.GetFiles(dst, "*.remotehub-part", SearchOption.AllDirectories));
            Assert.Equal(Path.Combine(dst, "big.bin"), first[0].RemotePath);

            var second = Items();
            await FileUploader.UploadAsync(Local, ex => ex.Message, dst, overwrite: false, second, a => a());
            Assert.All(second, i => Assert.Equal(TransferState.Skipped, i.State));

            File.WriteAllText(Path.Combine(src, "sub", "note.txt"), "changed");
            var third = Items();
            await FileUploader.UploadAsync(Local, ex => ex.Message, dst, overwrite: true, third, a => a());
            Assert.All(third, i => Assert.Equal(TransferState.Done, i.State));
            Assert.Equal("changed", File.ReadAllText(Path.Combine(dst, "sub", "note.txt")));
        }
        finally
        {
            Directory.Delete(src, true);
            if (Directory.Exists(dst)) Directory.Delete(dst, true);
        }
    }

    [Fact]
    public async Task Cancelled_upload_leaves_no_partial_file()
    {
        var src = Path.Combine(Path.GetTempPath(), "rdpm-cancel-" + Guid.NewGuid().ToString("N") + ".bin");
        var dst = Path.Combine(Path.GetTempPath(), "rdpm-cancel-dst-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(src, new byte[FileUploader.ChunkSize * 3]);
        try
        {
            var item = new TransferItem(src, Path.GetFileName(src), "test");
            // Cancel as soon as the first chunk has been reported.
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TransferItem.SentBytes)) item.Cancel(); };
            Runspace Local() { var rs = RunspaceFactory.CreateRunspace(); rs.Open(); return rs; }
            await FileUploader.UploadAsync(Local, ex => ex.Message, dst, false, [item], a => a());
            Assert.Equal(TransferState.Cancelled, item.State);
            Assert.False(File.Exists(Path.Combine(dst, item.RelativePath)));
            Assert.Empty(Directory.GetFiles(dst, "*.remotehub-part"));
        }
        finally
        {
            File.Delete(src);
            if (Directory.Exists(dst)) Directory.Delete(dst, true);
        }
    }

    [Fact]
    public async Task Unreachable_host_fails_every_item_with_a_reason()
    {
        var src = Path.Combine(Path.GetTempPath(), "rdpm-unreach-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(src, "x");
        try
        {
            var items = new List<TransferItem> { new(src, "x.txt", "test") };
            await FileUploader.UploadAsync(() => throw new InvalidOperationException("nope"), ex => "explained: " + ex.Message, @"C:\Temp", false, items, a => a());
            Assert.Equal(TransferState.Failed, items[0].State);
            Assert.Contains("explained: nope", items[0].StatusText);
        }
        finally { File.Delete(src); }
    }
}
