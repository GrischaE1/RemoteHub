using System.ComponentModel;
using System.Runtime.InteropServices;
using RemoteHub.Models;

namespace RemoteHub.Remoting.Shares;

/// <summary>
/// SMB through Windows' own client (Kerberos, signing, encryption – whatever the server asks for). With an account
/// set, the share is connected for that account (like "net use \\server\share" without a drive letter) and
/// disconnected again when the last user in RemoteHub is done with it; a connection that existed before is left alone.
/// Windows allows one account per server: a second account for the same server fails with
/// <see cref="CredentialConflictException"/>, and <see cref="ShareConnector"/> then uses <see cref="ManagedSmbShare"/>.
/// </summary>
public sealed class WindowsSmbShare : ShareFileSystem
{
    private const int ErrorSessionCredentialConflict = 1219;
    private static readonly Dictionary<string, Use> Uses = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private readonly string? _connected;
    private bool _disposed;

    static WindowsSmbShare() => AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseAll();

    public WindowsSmbShare(string host, string shareName, string subFolder, CredentialSet credentials)
    {
        Top = $@"\\{host}\{shareName}";
        Start = subFolder.Length == 0 ? Top : JoinRelative(Top, subFolder);
        if (!credentials.IsEmpty)
        {
            Acquire(Top, SmbUser(credentials, host), credentials.Password);
            _connected = Top;
        }
        try
        {
            if (!Directory.Exists(Start))
            {
                // Directory.Exists hides why: listing the share gives Windows' own reason (access denied, no such share …).
                try { _ = Directory.EnumerateFileSystemEntries(Top).FirstOrDefault(); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    throw new ShareException($"{Top}: {ex.Message}" + (ex is UnauthorizedAccessException
                        ? " A local administrator signing in over the network gets no administrator rights there (Windows' remote UAC) – give the account itself access to the share."
                        : ""), ex);
                }
                throw new ShareException($"Folder not found: {Start}");
            }
        }
        catch { Dispose(); throw; }
        Description = credentials.IsEmpty ? "SMB · Windows · your Windows account" : "SMB · Windows";
    }

    public override string Start { get; }
    public override string Top { get; }
    public override char Separator => '\\';
    public override string Description { get; }
    protected override StringComparison PathComparison => StringComparison.OrdinalIgnoreCase;

    /// <summary>"DOMAIN\user", "user@domain", or "server\user" for a local account of the server.</summary>
    internal static string SmbUser(CredentialSet c, string host)
    {
        var user = c.Username!.Trim();
        if (user.StartsWith(@".\")) return $"{host}\\{user[2..]}";
        if (user.Contains('\\') || user.Contains('@')) return user;
        if (c.IsLocalAccount) return $"{host}\\{user}";
        return string.IsNullOrEmpty(c.Domain) ? user : $"{c.Domain}\\{user}";
    }

    public override List<FileEntry> List(string folder) => Io(() =>
    {
        var dir = new DirectoryInfo(folder);
        if (!dir.Exists) throw new ShareException($"Folder not found: {folder}");
        var entries = new List<FileEntry>();
        foreach (var e in dir.EnumerateFileSystemInfos())
        {
            try
            {
                var hidden = e.Attributes.HasFlag(FileAttributes.Hidden);
                entries.Add(e is DirectoryInfo
                    ? new FileEntry(e.Name, Join(folder, e.Name), EntryKind.Folder, 0, e.LastWriteTimeUtc, hidden)
                    : new FileEntry(e.Name, Join(folder, e.Name), EntryKind.File, ((FileInfo)e).Length, e.LastWriteTimeUtc, hidden));
            }
            catch { /* entry vanished or no access */ }
        }
        return entries;
    });

    public override FileEntry? Stat(string path) => Io(() =>
    {
        if (File.Exists(path)) { var f = new FileInfo(path); return new FileEntry(f.Name, path, EntryKind.File, f.Length, f.LastWriteTimeUtc, false); }
        return Directory.Exists(path) ? new FileEntry(NameOf(path), path, EntryKind.Folder, 0, null, false) : null;
    });

    public override void CreateFolder(string path) => Io(() => Directory.CreateDirectory(path));
    public override void DeleteFile(string path) => Io(() => File.Delete(path));
    public override void DeleteEmptyFolder(string path) => Io(() => Directory.Delete(path));
    public override void Delete(string path, bool folder) => Io(() => { if (folder) Directory.Delete(path, true); else File.Delete(path); });

    public override void Move(string from, string to) => Io(() =>
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    });

    public override Stream OpenRead(string path) =>
        Io(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan));

    public override Stream Create(string path) =>
        Io(() => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan));

    /// <summary>Access denied, not found, file in use … are answers from the share: shown as they are.</summary>
    private static T Io<T>(Func<T> call)
    {
        try { return call(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException
                                       || (ex is IOException io && !IsNetworkGone(io)))
        {
            throw new ShareException(ex.Message, ex);
        }
    }

    private static void Io(Action call) => Io(() => { call(); return true; });

    /// <summary>The server or the network went away (as opposed to an answer from the share).</summary>
    private static bool IsNetworkGone(IOException ex) => (ex.HResult & 0xFFFF) is 53 or 59 or 64 or 67 or 121 or 1231 or 1236 or 1326;

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_connected is not null) Release(_connected);
    }

    // ------------------------------------------------------------------ connections (shared by all instances)

    /// <summary>
    /// Connects the share for the account (once per share, counted). A connection Windows already had (Explorer,
    /// "net use") is used but never disconnected. The account is checked by Windows on every new connection.
    /// </summary>
    private static void Acquire(string unc, string user, string? password)
    {
        lock (Gate)
        {
            if (Uses.TryGetValue(unc, out var u)) { Uses[unc] = u with { Count = u.Count + 1 }; return; }
            var existed = IsConnected(unc);
            var nr = new NETRESOURCE { dwType = 1 /* RESOURCETYPE_DISK */, lpRemoteName = unc };
            var rc = WNetAddConnection2W(ref nr, password, user, 0);
            if (rc == ErrorSessionCredentialConflict) throw new CredentialConflictException();
            if (rc != 0) throw new ShareException($"{unc}: {new Win32Exception(rc).Message}");
            Uses[unc] = new Use(1, Owned: !existed);
        }
    }

    private static void Release(string unc)
    {
        lock (Gate)
        {
            if (!Uses.TryGetValue(unc, out var u)) return;
            if (u.Count > 1) { Uses[unc] = u with { Count = u.Count - 1 }; return; }
            Uses.Remove(unc);
            if (u.Owned) _ = WNetCancelConnection2W(unc, 0, false);
        }
    }

    /// <summary>RemoteHub ends: disconnects whatever it connected (Windows would keep them for the logon session).</summary>
    public static void ReleaseAll()
    {
        lock (Gate)
        {
            foreach (var (unc, u) in Uses.ToList())
                if (u.Owned) _ = WNetCancelConnection2W(unc, 0, false);
            Uses.Clear();
        }
    }

    /// <summary>Open instances of a share, and whether RemoteHub made the Windows connection (and so removes it).</summary>
    private readonly record struct Use(int Count, bool Owned);

    /// <summary>Shares RemoteHub has connected right now (for tests and the log).</summary>
    internal static int OpenCount { get { lock (Gate) return Uses.Count; } }

    /// <summary>Whether Windows already has a connection to exactly this share (any account).</summary>
    private static bool IsConnected(string unc)
    {
        if (WNetOpenEnumW(1 /* RESOURCE_CONNECTED */, 1 /* DISK */, 0, IntPtr.Zero, out var handle) != 0) return false;
        var size = 16 * 1024;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            while (true)
            {
                var count = -1;
                var bytes = size;
                if (WNetEnumResourceW(handle, ref count, buffer, ref bytes) != 0) return false; // 259 = no more items
                for (var i = 0; i < count; i++)
                {
                    var nr = Marshal.PtrToStructure<NETRESOURCE>(buffer + i * Marshal.SizeOf<NETRESOURCE>());
                    if (string.Equals(nr.lpRemoteName?.TrimEnd('\\'), unc, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            _ = WNetCloseEnum(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public int dwScope, dwType, dwDisplayType, dwUsage;
        public string? lpLocalName, lpRemoteName, lpComment, lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetAddConnection2W(ref NETRESOURCE netResource, string? password, string? userName, int flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetCancelConnection2W(string name, int flags, bool force);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetOpenEnumW(int scope, int type, int usage, IntPtr netResource, out IntPtr handle);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetEnumResourceW(IntPtr handle, ref int count, IntPtr buffer, ref int bufferSize);
    [DllImport("mpr.dll")] private static extern int WNetCloseEnum(IntPtr handle);
}

/// <summary>Windows already has a connection to this server with another account (error 1219).</summary>
public sealed class CredentialConflictException() : Exception("Windows is already connected to this server with another account.");
