using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using RemoteHub.Models;
using RemoteHub.Rdp;
using RemoteHub.Remoting;
using RemoteHub.Remoting.Shares;

/// <summary>
/// Network shares against a lab VM with RemoteHub's own code (ShareConnector, ShareTransfer, ShareSession):
///   share --protocol smb|sftp|ftp|webdav --host .. [--port n] --path .. [--user .. --password ..] [--https 0|1]
///         [--ftp-security explicit|implicit|none] [--trusted &lt;fingerprint&gt;] [--accept 0|1]
///         [--windows-user &lt;COMPUTER\user&gt;] [--safety 1]
///       Connects (a key or certificate seen for the first time is accepted only with --accept 1, as a user would),
///       then: list, new folder, upload (a 3 MB file and a small one in a subfolder), check, rename, download and
///       compare SHA-256, delete everything again. Each step is answered with ok/error and its time.
///       --windows-user: the whole scenario runs with that account as this process' Windows account for the network
///       (LogonUser LOGON32_LOGON_NEW_CREDENTIALS + impersonation, like "runas /netonly"; password from --password).
///       Without --user the share has no account: SMB and WebDAV then use the Windows account.
///       --safety 1: before the delete, ShareTransfer's safety on a wrapped connection that records what is written
///       and moved: answers safety.{writes, moves, doneText, existingFile, existingFolder, mismatch, partLeft}.
///   share-conflict --host .. --share1 .. --user1 .. --share2 .. --user2 .. --password ..
///       Two SMB shares of one server with two accounts: the second has to fall back to RemoteHub's own client.
///   share-lifetime --host .. --path &lt;share&gt; --user .. --password .. [--existing 1]
///       Windows SMB connection lifetime (WindowsSmbShare through ShareConnector.Open): open A, open B, close A,
///       close B, open C, WindowsSmbShare.ReleaseAll (what RemoteHub does when it exits). Answers uses.&lt;moment&gt; =
///       [{ remote, local }] from WNetOpenEnum(RESOURCE_CONNECTED) (and netUse.&lt;moment&gt; from NetUseEnum, evidence).
///       --existing 1: the harness connects \\host\share itself first (WNetAddConnection2, like "net use") and removes
///       it again at the end. Always direct (no jump host).
///   share-session --protocol .. --host .. [--port n] [--path ..] --user .. --password .. [--accept 1] [--idle-sec n]
///       RemoteHub's ShareSession (the share tab's connection) on a WPF dispatcher thread, ShareSession.IdleTime = n s:
///       connect, delete the share's top folder (refused), a.txt/b.txt and rename a.txt to b.txt (refused), wait until
///       the idle timer closed the connection (its timer ticks every 30 s), list again. Answers connected / idle /
///       afterList (StatusText), deleteRoot / renameExisting (messages), listedAfterIdle, opens, asked.
/// </summary>
internal static class ShareHarness
{
    private const string PartSuffix = ".remotehub-part";

    public static object Run(Dictionary<string, string> o) =>
        o.TryGetValue("windows-user", out var account)
            ? AsWindowsUser(account, o.GetValueOrDefault("password") ?? "", () => Scenario(o))
            : Scenario(o);

    private static NetworkShare ShareFrom(Dictionary<string, string> o) => new()
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

    private static object Scenario(Dictionary<string, string> o)
    {
        var share = ShareFrom(o);
        var cred = new CredentialSet { Username = o.GetValueOrDefault("user"), Password = o.GetValueOrDefault("password") };
        var accept = o.GetValueOrDefault("accept") == "1";
        var windowsUser = o.GetValueOrDefault("windows-user");
        var asked = new List<object>();
        string? accepted = null;
        bool Trust(TrustRequest r) { asked.Add(new { r.What, r.Fingerprint }); return accept; }
        ShareFileSystem Open() => ShareConnector.Open(share, cred, Trust, fp => { accepted = fp; share.TrustedFingerprint = fp; }, LabJump.Route(o));

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
        if (fs is null) return new { ok, protocol = share.ProtocolLabel, windowsUser, asked, accepted, steps };
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
                var parts = fs.Expand([root]).Where(f => f.FullPath.EndsWith(PartSuffix)).ToList();
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
            SafetyAnswer? safety = null;
            if (o.GetValueOrDefault("safety") == "1")
                safety = Step("safety", () => Safety(fs, Open, root, Path.Combine(work, "safety"), share.Protocol == ShareProtocol.Smb));
            Step("delete", () => { fs.Delete(root, folder: true); return fs.Stat(root) is null ? "gone" : throw new InvalidOperationException("still there"); });
            return new { ok, protocol = share.ProtocolLabel, windowsUser, description, start, asked, accepted, steps, safety };
        }
        finally
        {
            fs.Dispose();
            try { Directory.Delete(work, true); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------ --safety 1

    private sealed record SafetyAnswer(List<string> Writes, List<string> Moves, string DoneText, string ExistingFile, string ExistingFolder,
                                       string Mismatch, bool PartLeft, bool MismatchFileLeft, string Folder)
    {
        public override string ToString() => $"done: {DoneText}; file: {ExistingFile}; folder: {ExistingFolder}; mismatch: {Mismatch}; part left: {PartLeft}";
    }

    /// <summary>
    /// ShareTransfer's safety in <paramref name="root"/>\safety, over connections that record what is written (Upload/Create)
    /// and moved: a normal upload, the same file again (no overwrite), a file onto a folder of that name, and a file that
    /// grew after its transfer item was made (the share then has more than the item's size: "… the transfer was incomplete.").
    /// </summary>
    private static SafetyAnswer Safety(ShareFileSystem fs, Func<ShareFileSystem> open, string root, string local, bool ignoreCase)
    {
        var writes = new List<string>();
        var moves = new List<string>();
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        ShareFileSystem Recorded() => new Wrapped(open(), comparison, writes, moves);
        var folder = fs.Join(root, "safety");
        fs.CreateFolder(folder);
        Directory.CreateDirectory(local);

        TransferItem Upload(TransferItem item)
        {
            ShareTransfer.UploadAsync(Recorded, folder, false, [item], a => a()).GetAwaiter().GetResult();
            return item;
        }

        var safe = Path.Combine(local, "safe.txt");
        File.WriteAllText(safe, "RemoteHub lab: written as .remotehub-part, size checked, renamed");
        var done = Upload(new TransferItem(safe, "safe.txt", "lab"));
        var existingFile = Upload(new TransferItem(safe, "safe.txt", "lab"));

        fs.CreateFolder(fs.Join(folder, "clash"));
        var clash = Path.Combine(local, "clash");
        File.WriteAllText(clash, "a file named like a folder on the share");
        var existingFolder = Upload(new TransferItem(clash, "clash", "lab"));

        var grow = Path.Combine(local, "grow.bin");
        File.WriteAllBytes(grow, new byte[64 * 1024]);
        var grown = new TransferItem(grow, "grow.bin", "lab"); // its size is taken now …
        using (var f = new FileStream(grow, FileMode.Append)) f.Write(new byte[4096]); // … and the file grows after that
        var mismatch = Upload(grown);
        var target = fs.Join(folder, "grow.bin");
        var partLeft = fs.Stat(target + PartSuffix) is not null;
        var mismatchFileLeft = fs.Stat(target) is not null;

        return new SafetyAnswer(writes, moves, done.StatusText, existingFile.StatusText, existingFolder.StatusText,
                                mismatch.StatusText, partLeft, mismatchFileLeft, folder);
    }

    /// <summary>
    /// A share connection passed through to another one, recording writes (Upload/Create) and moves ("from -> to"); with
    /// <paramref name="mayDelete"/> deletes outside what it allows are refused (a guard against deleting a whole share
    /// should RemoteHub's own check ever be missing).
    /// </summary>
    private sealed class Wrapped(ShareFileSystem inner, StringComparison comparison, List<string>? writes = null, List<string>? moves = null,
                                 Func<string, bool>? mayDelete = null) : ShareFileSystem
    {
        public override string Start => inner.Start;
        public override string Top => inner.Top;
        public override char Separator => inner.Separator;
        public override string Description => inner.Description;
        protected override StringComparison PathComparison => comparison;
        public override List<FileEntry> List(string folder) => inner.List(folder);
        public override FileEntry? Stat(string path) => inner.Stat(path);
        public override void CreateFolder(string path) => inner.CreateFolder(path);
        public override void DeleteFile(string path) { Deleting(path); inner.DeleteFile(path); }
        public override void DeleteEmptyFolder(string path) { Deleting(path); inner.DeleteEmptyFolder(path); }
        public override void Delete(string path, bool folder) { Deleting(path); inner.Delete(path, folder); }
        public override void Move(string from, string to)
        {
            if (moves is not null) lock (moves) moves.Add($"{from} -> {to}");
            inner.Move(from, to);
        }
        public override Stream OpenRead(string path) => inner.OpenRead(path);
        public override Stream Create(string path) { Wrote(path); return inner.Create(path); }
        public override void Upload(Stream source, long length, string path, Action<long> progress, Func<bool> cancelled)
        {
            Wrote(path);
            inner.Upload(source, length, path, progress, cancelled);
        }
        public override void Download(string path, Stream target, Action<long> progress, Func<bool> cancelled) => inner.Download(path, target, progress, cancelled);
        public override void Dispose() => inner.Dispose();

        private void Wrote(string path) { if (writes is not null) lock (writes) writes.Add(path); }
        private void Deleting(string path)
        {
            if (mayDelete is not null && !mayDelete(path)) throw new ShareException($"Lab harness guard: not deleting {path} (outside the harness' own folder).");
        }
    }

    // ------------------------------------------------------------------ --windows-user

    private const int LogonNewCredentials = 9; // LOGON32_LOGON_NEW_CREDENTIALS: the account is used for the network only
    private const int ProviderWinNt50 = 3;     // LOGON32_PROVIDER_WINNT50

    /// <summary>Runs <paramref name="run"/> with <paramref name="account"/> ("COMPUTER\user") as the Windows account for the network.</summary>
    private static T AsWindowsUser<T>(string account, string password, Func<T> run)
    {
        var i = account.IndexOf('\\');
        var domain = i > 0 ? account[..i] : ".";
        var user = i > 0 ? account[(i + 1)..] : account;
        if (!LogonUserW(user, domain, password, LogonNewCredentials, ProviderWinNt50, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"LogonUser({account}, NEW_CREDENTIALS): {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        using (token) return WindowsIdentity.RunImpersonated(token, run);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUserW(string user, string? domain, string password, int logonType, int provider, out SafeAccessTokenHandle token);

    // ------------------------------------------------------------------ share-conflict

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

    // ------------------------------------------------------------------ share-lifetime

    public static object Lifetime(Dictionary<string, string> o)
    {
        var host = o["host"];
        var share = new NetworkShare { Name = "lab", Protocol = ShareProtocol.Smb, Host = host, Path = o["path"] };
        var cred = new CredentialSet { Username = o["user"], Password = o["password"] };
        var unc = $@"\\{host}\{ShareConnector.SplitSmbPath(share.Path).Share}";
        // the account as WindowsSmbShare.SmbUser writes it (no domain set): the same account as RemoteHub's, no conflict
        var user = cred.Username!.Trim();
        if (user.StartsWith(@".\")) user = $@"{host}\{user[2..]}";

        var uses = new Dictionary<string, List<Use>>();
        var netUse = new Dictionary<string, List<Use>>();
        var kinds = new List<string>();
        void Note(string moment) { uses[moment] = Connected(); netUse[moment] = NetUses(); }
        ShareFileSystem OpenOne()
        {
            var f = ShareConnector.Open(share, cred, _ => false, _ => { });
            kinds.Add(f.GetType().Name);
            return f;
        }

        Note("before");
        var existing = o.GetValueOrDefault("existing") == "1";
        string? existingError = null;
        var existingMade = false;
        if (existing)
        {
            var nr = new NETRESOURCE { dwType = 1 /* RESOURCETYPE_DISK */, lpRemoteName = unc };
            var rc = WNetAddConnection2W(ref nr, cred.Password, user, 0);
            if (rc == 0) existingMade = true;
            else existingError = $"{rc}: {new Win32Exception(rc).Message}";
            Note("afterExisting");
        }
        try
        {
            var a = OpenOne();
            Note("afterFirst");
            var b = OpenOne();
            Note("afterSecond");
            a.Dispose();
            Note("afterCloseFirst");
            b.Dispose();
            Note("afterCloseSecond");
            var c = OpenOne();
            Note("afterThird");
            WindowsSmbShare.ReleaseAll(); // what RemoteHub does when it exits
            Note("afterReleaseAll");
            c.Dispose();
        }
        finally
        {
            if (existingMade) _ = WNetCancelConnection2W(unc, 0, true);
        }
        Note("afterCleanup");
        var ok = kinds.All(k => k == nameof(WindowsSmbShare)) && existingError is null;
        return new { ok, unc, user, existing, existingError, kinds, uses, netUse };
    }

    private sealed record Use(string? Remote, string? Local);

    /// <summary>Windows' connections of this logon session, as WNetOpenEnum(RESOURCE_CONNECTED) lists them.</summary>
    private static List<Use> Connected()
    {
        var list = new List<Use>();
        var rc = WNetOpenEnumW(1 /* RESOURCE_CONNECTED */, 0 /* RESOURCETYPE_ANY */, 0, IntPtr.Zero, out var handle);
        if (rc != 0) throw new Win32Exception(rc, "WNetOpenEnum");
        var size = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            while (true)
            {
                var count = -1;
                var bytes = size;
                rc = WNetEnumResourceW(handle, ref count, buffer, ref bytes);
                if (rc == 259 /* ERROR_NO_MORE_ITEMS */) break;
                if (rc == 234 /* ERROR_MORE_DATA */)
                {
                    Marshal.FreeHGlobal(buffer);
                    size = Math.Max(bytes, size * 2);
                    buffer = Marshal.AllocHGlobal(size);
                    continue;
                }
                if (rc != 0) throw new Win32Exception(rc, "WNetEnumResource");
                for (var i = 0; i < count; i++)
                {
                    var nr = Marshal.PtrToStructure<NETRESOURCE>(buffer + i * Marshal.SizeOf<NETRESOURCE>());
                    list.Add(new Use(nr.lpRemoteName, string.IsNullOrEmpty(nr.lpLocalName) ? null : nr.lpLocalName));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            _ = WNetCloseEnum(handle);
        }
        return list;
    }

    /// <summary>The same as "net use" lists it (NetUseEnum level 0) - evidence next to the WNet list.</summary>
    private static List<Use> NetUses()
    {
        var list = new List<Use>();
        var rc = NetUseEnum(null, 0, out var buf, -1, out var read, out _, IntPtr.Zero);
        try
        {
            if (rc != 0) return list;
            for (var i = 0; i < read; i++)
            {
                var u = Marshal.PtrToStructure<USE_INFO_0>(buf + i * Marshal.SizeOf<USE_INFO_0>());
                list.Add(new Use(u.ui0_remote, string.IsNullOrEmpty(u.ui0_local) ? null : u.ui0_local));
            }
        }
        finally { if (buf != IntPtr.Zero) _ = NetApiBufferFree(buf); }
        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public int dwScope, dwType, dwDisplayType, dwUsage;
        public string? lpLocalName, lpRemoteName, lpComment, lpProvider;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USE_INFO_0
    {
        public string? ui0_local, ui0_remote;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetAddConnection2W(ref NETRESOURCE netResource, string? password, string? userName, int flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetCancelConnection2W(string name, int flags, bool force);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetOpenEnumW(int scope, int type, int usage, IntPtr netResource, out IntPtr handle);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetEnumResourceW(IntPtr handle, ref int count, IntPtr buffer, ref int bufferSize);
    [DllImport("mpr.dll")] private static extern int WNetCloseEnum(IntPtr handle);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetUseEnum(string? server, int level, out IntPtr buffer, int preferredMaxLength, out int entriesRead, out int totalEntries, IntPtr resumeHandle);
    [DllImport("netapi32.dll")] private static extern int NetApiBufferFree(IntPtr buffer);

    // ------------------------------------------------------------------ share-session

    public static object Session(Dictionary<string, string> o)
    {
        object? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { result = await SessionAsync(o); }
                catch (Exception ex) { error = ex; }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Throw(error);
        return result!;
    }

    private static async Task<object> SessionAsync(Dictionary<string, string> o)
    {
        var idleSec = int.Parse(o.GetValueOrDefault("idle-sec") ?? "5");
        ShareSession.IdleTime = TimeSpan.FromSeconds(idleSec); // a settable static property: no test seam needed
        var share = ShareFrom(o);
        var cred = new CredentialSet { Username = o.GetValueOrDefault("user"), Password = o.GetValueOrDefault("password") };
        var accept = o.GetValueOrDefault("accept") == "1";
        var via = LabJump.Route(o);
        var asked = new List<object>();
        string? accepted = null;
        bool Trust(TrustRequest r) { lock (asked) asked.Add(new { r.What, r.Fingerprint }); return accept; }
        var comparison = share.Protocol == ShareProtocol.Smb ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string? work = null;
        bool MayDelete(string path) => work is not null && (path.Equals(work, comparison) || path.StartsWith(work.TrimEnd('/', '\\') + (share.Protocol == ShareProtocol.Smb ? "\\" : "/"), comparison));
        var opens = 0;
        ShareFileSystem? last = null;
        ShareFileSystem Direct() => ShareConnector.Open(share, cred, Trust, fp => { accepted = fp; share.TrustedFingerprint = fp; }, via);
        // what the share tab connects with (counted); deletes only below the harness' own folder
        ShareFileSystem Open()
        {
            Interlocked.Increment(ref opens);
            var f = new Wrapped(Direct(), comparison, mayDelete: MayDelete);
            last = f;
            return f;
        }
        static async Task<bool> Until(Func<bool> done, TimeSpan max)
        {
            var sw = Stopwatch.StartNew();
            while (!done())
            {
                if (sw.Elapsed > max) return false;
                await Task.Delay(200);
            }
            return true;
        }
        static string Message(Exception ex) => ex is ShareException ? ex.Message : ex.GetType().Name + ": " + ex.Message;

        var server = new Connection { Name = "lab", Host = o["host"] };
        var session = new ShareSession(server, share);
        try
        {
            Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} connect…");
            session.Connect(Open, "lab harness", System.Windows.Media.Brushes.Transparent);
            await Until(() => session.State != SessionState.Connecting, TimeSpan.FromMinutes(2));
            var connected = session.StatusText;
            if (session.State != SessionState.Connected || last is null)
                return new { ok = false, connected, opens, asked, accepted };

            var top = last.Top;
            var start = session.StartRemotePath ?? last.Start;
            work = last.Join(start, "rh-lab-sess-" + Guid.NewGuid().ToString("N")[..6]);

            string deleteRoot;
            Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} delete the top folder {top}…");
            try { await session.DeleteAsync([top]); deleteRoot = "(no error – the top folder was not refused)"; }
            catch (Exception ex) { deleteRoot = Message(ex); }

            Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} rename onto an existing name…");
            await session.MkdirAsync(work);
            var a = last.Join(work, "a.txt");
            var b = last.Join(work, "b.txt");
            using (var setup = Direct()) // not the tab's connection: not counted in opens
            {
                using (var s = setup.Create(a)) s.Write("a"u8);
                using (var s = setup.Create(b)) s.Write("b"u8);
            }
            string renameExisting;
            try { await session.RenameAsync(a, "b.txt"); renameExisting = "(no error – renamed onto the existing b.txt)"; }
            catch (Exception ex) { renameExisting = Message(ex); }

            Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} wait for the idle close ({idleSec} s, the idle timer ticks every 30 s)…");
            var idleWatch = Stopwatch.StartNew();
            var wentIdle = await Until(() => session.StatusText.StartsWith("Idle", StringComparison.Ordinal), TimeSpan.FromSeconds(idleSec + 90));
            var idle = session.StatusText;
            var idleAfterMs = idleWatch.ElapsedMilliseconds;
            var opensBeforeList = opens;

            Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} list after idle…");
            bool listedAfterIdle;
            string? listError = null;
            List<string>? listed = null;
            try
            {
                var listing = await session.ListAsync(work);
                listed = listing.Entries.Select(e => e.Name).ToList();
                listedAfterIdle = true;
            }
            catch (Exception ex) { listedAfterIdle = false; listError = Message(ex); }
            var afterList = session.StatusText;

            try { await session.DeleteAsync([work]); }
            catch (Exception ex) { Console.Error.WriteLine("cleanup: " + Message(ex)); }

            var ok = wentIdle && listedAfterIdle;
            return new
            {
                ok, connected, idle, wentIdle, idleAfterMs, afterList, listedAfterIdle, listError, listed, deleteRoot, renameExisting,
                opens, opensBeforeList, asked, accepted, top, start, work, idleSec,
            };
        }
        finally
        {
            session.Dispose();
        }
    }
}
