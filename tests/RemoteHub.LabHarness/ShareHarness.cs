using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Remoting.Shares;

/// <summary>
/// Network shares against a lab VM with RemoteHub's own code (ShareConnector, ShareTransfer):
///   share --protocol smb|sftp|ftp|webdav --host .. [--port n] --path .. [--user .. --password ..] [--https 0|1]
///         [--ftp-security explicit|implicit|none] [--trusted &lt;fingerprint&gt;] [--accept 0|1]
///       Connects (a key or certificate seen for the first time is accepted only with --accept 1, as a user would),
///       then: list, new folder, upload (a 3 MB file and a small one in a subfolder), check, rename, download and
///       compare SHA-256, delete everything again. Each step is answered with ok/error and its time.
///   share-conflict --host .. --share1 .. --user1 .. --share2 .. --user2 .. --password ..
///       Two SMB shares of one server with two accounts: the second has to fall back to RemoteHub's own client.
/// </summary>
internal static class ShareHarness
{
    public static object Run(Dictionary<string, string> o)
    {
        var share = new NetworkShare
        {
            Name = "lab",
            Protocol = Enum.Parse<ShareProtocol>(o["protocol"], ignoreCase: true),
            Host = o["host"],
            Port = o.TryGetValue("port", out var p) ? int.Parse(p) : null,
            Path = o.GetValueOrDefault("path") ?? "",
            WebDavHttps = o.GetValueOrDefault("https") != "0",
            FtpSecurity = Enum.Parse<FtpSecurity>(o.GetValueOrDefault("ftp-security") ?? "explicit", ignoreCase: true),
            TrustedFingerprint = o.GetValueOrDefault("trusted"),
        };
        var cred = new CredentialSet { Username = o.GetValueOrDefault("user"), Password = o.GetValueOrDefault("password") };
        var accept = o.GetValueOrDefault("accept") == "1";
        var asked = new List<object>();
        string? accepted = null;
        bool Trust(TrustRequest r) { asked.Add(new { r.What, r.Fingerprint }); return accept; }
        ShareFileSystem Open() => ShareConnector.Open(share, cred, Trust, fp => { accepted = fp; share.TrustedFingerprint = fp; });

        var steps = new List<object>();
        var ok = true;
        T? Step<T>(string name, Func<T> f)
        {
            var sw = Stopwatch.StartNew();
            Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {name}…"); // shown by Invoke-LabHarness when the call times out
            try
            {
                var r = f();
                steps.Add(new { step = name, ok = true, ms = sw.ElapsedMilliseconds, detail = r?.ToString() });
                return r;
            }
            catch (Exception ex)
            {
                ok = false;
                steps.Add(new { step = name, ok = false, ms = sw.ElapsedMilliseconds, error = ex.GetType().Name + ": " + ex.Message });
                return default;
            }
        }

        var fs = Step("connect", Open);
        if (fs is null) return new { ok, protocol = share.ProtocolLabel, asked, accepted, steps };
        var work = Path.Combine(Path.GetTempPath(), "rh-lab-share-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var description = fs.Description;
            var start = fs.Start;
            var root = fs.Join(start, "rh-lab-" + Guid.NewGuid().ToString("N")[..6]);
            Step("list start", () => fs.List(start).Count);
            Step("new folder", () => { fs.CreateFolder(root); return fs.Stat(root)?.Kind; });

            // upload through ShareTransfer (its own connection, as the transfers panel does it)
            var local = Path.Combine(work, "up");
            Directory.CreateDirectory(Path.Combine(local, "pack", "sub"));
            var big = new byte[3 * 1024 * 1024 + 17];
            new Random(7).NextBytes(big);
            File.WriteAllBytes(Path.Combine(local, "pack", "big.bin"), big);
            File.WriteAllText(Path.Combine(local, "pack", "sub", "small.txt"), "RemoteHub lab ä ö ü");
            var items = FileUploader.Expand([Path.Combine(local, "pack")]).Select(x => new TransferItem(x.Local, x.Relative, "lab")).ToList();
            Step("upload", () =>
            {
                ShareTransfer.UploadAsync(Open, root, false, items, a => a()).GetAwaiter().GetResult();
                return string.Join(", ", items.Select(i => $"{i.RelativePath}: {i.State} ({i.StatusText})"));
            });
            if (items.Any(i => i.State != TransferState.Done)) { ok = false; }

            var bigRemote = fs.JoinRelative(root, @"pack\big.bin");
            Step("size on the share", () =>
            {
                var size = fs.Stat(bigRemote)?.Size;
                if (size != big.Length) throw new InvalidOperationException($"size {size}, expected {big.Length}");
                return size;
            });
            Step("no part files", () =>
            {
                var parts = fs.Expand([root]).Where(f => f.FullPath.EndsWith(".remotehub-part")).ToList();
                if (parts.Count > 0) throw new InvalidOperationException(string.Join(", ", parts.Select(x => x.FullPath)));
                return 0;
            });
            var renamed = fs.JoinRelative(root, @"pack\big-renamed.bin");
            Step("rename", () => { fs.Move(bigRemote, renamed); return fs.Stat(renamed)?.Size; });

            var files = Step("expand", () => fs.Expand([fs.Join(root, "pack")])) ?? [];
            var down = Path.Combine(work, "down");
            var dl = files.Select(f => TransferItem.Download(f.FullPath, Path.Combine(down, f.Relative), f.Relative, "lab", f.Size)).ToList();
            Step("download", () =>
            {
                ShareTransfer.DownloadAsync(Open, false, dl, a => a()).GetAwaiter().GetResult();
                return string.Join(", ", dl.Select(i => $"{i.RelativePath}: {i.State} ({i.StatusText})"));
            });
            Step("same content", () =>
            {
                var got = File.ReadAllBytes(Path.Combine(down, "pack", "big-renamed.bin"));
                if (!SHA256.HashData(got).AsSpan().SequenceEqual(SHA256.HashData(big))) throw new InvalidOperationException("SHA-256 differs");
                var text = File.ReadAllText(Path.Combine(down, "pack", "sub", "small.txt"));
                if (text != "RemoteHub lab ä ö ü") throw new InvalidOperationException("small.txt differs: " + text);
                return "SHA-256 equal";
            });
            Step("delete", () => { fs.Delete(root, folder: true); return fs.Stat(root) is null ? "gone" : throw new InvalidOperationException("still there"); });
            return new { ok, protocol = share.ProtocolLabel, description, start, asked, accepted, steps };
        }
        finally
        {
            fs.Dispose();
            try { Directory.Delete(work, true); } catch { /* best effort */ }
        }
    }

    public static object Conflict(Dictionary<string, string> o)
    {
        CredentialSet Cred(string user) => new() { Username = user, Password = o["password"] };
        NetworkShare Share(string name) => new() { Name = name, Protocol = ShareProtocol.Smb, Host = o["host"], Path = name };
        static bool Never(TrustRequest _) => false;
        using var first = ShareConnector.Open(Share(o["share1"]), Cred(o["user1"]), Never, _ => { });
        using var second = ShareConnector.Open(Share(o["share2"]), Cred(o["user2"]), Never, _ => { });
        var list1 = first.List(first.Start).Count;
        var list2 = second.List(second.Start).Count;
        // write with the second account, read it with the first: two accounts at the same time on one server
        var name = second.Join(second.Start, "conflict-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        using (var s = second.Create(name)) s.Write("second account"u8);
        var size = second.Stat(name)?.Size;
        second.DeleteFile(name);
        return new { ok = true, first = first.Description, second = second.Description, list1, list2, writtenBySecond = size };
    }
}
