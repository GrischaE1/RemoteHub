using RemoteHub.Models;
using SMBLibrary;
using SMBLibrary.Client;
using SmbAttributes = SMBLibrary.FileAttributes;

namespace RemoteHub.Remoting.Shares;

/// <summary>
/// SMB 2/3 through RemoteHub's own client connection (SMBLibrary), used when Windows is already connected to the same
/// server with another account. Each instance has its own connection and account, independent of Windows. Signs in
/// with NTLMv2 (no Kerberos) and always signs, even when the server doesn't require it; guest sessions are refused.
/// Paths are shown as UNC paths, like with Windows' client. Always used behind a jump host:
/// then it connects to <paramref name="forward"/>'s local port.
/// </summary>
public sealed class ManagedSmbShare : ShareFileSystem
{
    private readonly SMB2Client _client = new(30_000, true);
    private readonly ISMBFileStore _store;
    private readonly LocalForward? _forward;
    private bool _disposed;

    public ManagedSmbShare(string host, string shareName, string subFolder, CredentialSet credentials, LocalForward? forward = null)
    {
        Top = $@"\\{host}\{shareName}";
        Start = subFolder.Length == 0 ? Top : JoinRelative(Top, subFolder);
        _forward = forward;
        Description = forward is null ? "SMB · RemoteHub client (Windows already uses another account for this server)" : "SMB · RemoteHub client";
        if (!(forward is null ? _client.Connect(host, SMBTransportType.DirectTCPTransport) : ConnectToPort(_client, host, forward.Port)))
            throw new IOException($"{host} doesn't answer on the SMB port (445).");
        try
        {
            var (domain, user) = Split(credentials, host);
            var status = _client.Login(domain, user, credentials.Password?.Reveal() ?? "");
            if (status != NTStatus.STATUS_SUCCESS) throw new ShareException(Explain(status, $"{host}: sign-in failed"));
            if (!RequireSigning(_client))
                throw new ShareException($"{host} signed the account in as a guest, whose connection can't be signed – nothing was read or written. " +
                                         "Check the user name and password set for the share or its server.");
            _store = _client.TreeConnect(shareName, out status);
            if (status != NTStatus.STATUS_SUCCESS) throw new ShareException(Explain(status, Top));
            if (Stat(Start) is not { Kind: EntryKind.Folder }) throw new ShareException($"Folder not found: {Start}");
        }
        catch { Dispose(); throw; }
    }

    public override string Start { get; }
    public override string Top { get; }
    public override char Separator => '\\';
    public override string Description { get; }
    protected override StringComparison PathComparison => StringComparison.OrdinalIgnoreCase;

    // SMBLibrary (1.5.8.1) connects to port 445 only; its private overload with a port is the one the public ones call.
    private static readonly System.Reflection.MethodInfo? PortConnect = typeof(SMB2Client).GetMethod("Connect",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, [typeof(System.Net.IPAddress), typeof(SMBTransportType), typeof(int)]);
    private static readonly System.Reflection.FieldInfo? ServerName = typeof(SMB2Client).GetField("m_serverName",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    /// <summary>Whether this SMBLibrary can connect to another port (a test fails when an update takes it away).</summary>
    internal static bool CanUseOtherPort => PortConnect is not null && ServerName is not null;

    // SMBLibrary signs only when the server demands it ([MS-SMB2] SigningRequired); RemoteHub always wants it.
    private static readonly System.Reflection.FieldInfo? SigningRequired = typeof(SMB2Client).GetField("m_signingRequired",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    private static readonly System.Reflection.FieldInfo? SigningKey = typeof(SMB2Client).GetField("m_signingKey",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    /// <summary>Whether this SMBLibrary can be made to sign (a test fails when an update takes it away).</summary>
    internal static bool CanRequireSigning => SigningRequired?.FieldType == typeof(bool) && SigningKey?.FieldType == typeof(byte[]);

    /// <summary>
    /// After the sign-in: every request is signed and every answer's signature checked (an unsigned or wrongly signed
    /// answer is discarded), whatever the server asks for – so nobody in between can change the data or take over the
    /// session. False for a guest session: it has no key to sign with.
    /// </summary>
    internal static bool RequireSigning(SMB2Client client)
    {
        if (!CanRequireSigning) throw new ShareException("RemoteHub's SMB client can't sign its connection in this version – nothing was read or written.");
        if (SigningKey!.GetValue(client) is not byte[] { Length: > 0 }) return false;
        SigningRequired!.SetValue(client, true);
        return true;
    }

    /// <summary>
    /// Connects to 127.0.0.1:<paramref name="port"/> as if it were <paramref name="host"/>: the name is set first, so the
    /// sign-in (SPN cifs/host) and the share paths (\\host\share) use it, as with a direct connection.
    /// </summary>
    internal static bool ConnectToPort(SMB2Client client, string host, int port)
    {
        if (!CanUseOtherPort) throw new ShareException("RemoteHub's SMB client can't connect through a jump host in this version.");
        ServerName!.SetValue(client, host);
        try { return (bool)PortConnect!.Invoke(client, [System.Net.IPAddress.Loopback, SMBTransportType.DirectTCPTransport, port])!; }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(inner);
            throw;
        }
    }

    private static (string Domain, string User) Split(CredentialSet c, string host)
    {
        var user = c.Username!.Trim();
        var i = user.IndexOf('\\');
        if (i > 0) return (user[..i] == "." ? host : user[..i], user[(i + 1)..]);
        if (user.Contains('@')) return ("", user);
        return (c.IsLocalAccount ? host : c.Domain ?? "", user);
    }

    /// <summary>"\\server\share\a\b" → "a\b" (the path inside the share, as SMB wants it).</summary>
    private string Inside(string path)
    {
        var p = path.TrimEnd('\\');
        if (!p.StartsWith(Top, StringComparison.OrdinalIgnoreCase)) throw new ShareException($"Not on this share: {path}");
        return p.Length == Top.Length ? "" : p[(Top.Length + 1)..];
    }

    private object Open(string path, AccessMask access, CreateDisposition disposition, CreateOptions options, ShareAccess share = ShareAccess.Read | ShareAccess.Write)
    {
        var status = _store.CreateFile(out var handle, out _, Inside(path), access | AccessMask.SYNCHRONIZE, SmbAttributes.Normal, share, disposition,
            options | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        if (status != NTStatus.STATUS_SUCCESS) throw new ShareException(Explain(status, path));
        return handle;
    }

    public override List<FileEntry> List(string folder)
    {
        var h = Open(folder, AccessMask.GENERIC_READ, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE);
        try
        {
            var status = _store.QueryDirectory(out var items, h, "*", FileInformationClass.FileDirectoryInformation);
            if (status is not (NTStatus.STATUS_SUCCESS or NTStatus.STATUS_NO_MORE_FILES)) throw new ShareException(Explain(status, folder));
            var list = new List<FileEntry>();
            foreach (var i in items.OfType<FileDirectoryInformation>())
            {
                if (i.FileName is "." or "..") continue;
                var dir = i.FileAttributes.HasFlag(SmbAttributes.Directory);
                list.Add(new FileEntry(i.FileName, Join(folder, i.FileName), dir ? EntryKind.Folder : EntryKind.File, dir ? 0 : i.EndOfFile,
                    DateTime.SpecifyKind(i.LastWriteTime, DateTimeKind.Utc), i.FileAttributes.HasFlag(SmbAttributes.Hidden)));
            }
            return list;
        }
        finally { _store.CloseFile(h); }
    }

    public override FileEntry? Stat(string path)
    {
        var status = _store.CreateFile(out var h, out _, Inside(path), (AccessMask)0x80 /* FILE_READ_ATTRIBUTES */ | AccessMask.SYNCHRONIZE, SmbAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN, CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        if (status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND) return null;
        if (status != NTStatus.STATUS_SUCCESS) throw new ShareException(Explain(status, path));
        try
        {
            status = _store.GetFileInformation(out var info, h, FileInformationClass.FileStandardInformation);
            if (status != NTStatus.STATUS_SUCCESS || info is not FileStandardInformation std) throw new ShareException(Explain(status, path));
            return new FileEntry(NameOf(path), path, std.Directory ? EntryKind.Folder : EntryKind.File, std.Directory ? 0 : std.EndOfFile, null, false);
        }
        finally { _store.CloseFile(h); }
    }

    public override void CreateFolder(string path) =>
        _store.CloseFile(Open(path, AccessMask.GENERIC_READ | AccessMask.GENERIC_WRITE, CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE));

    public override void DeleteFile(string path) => MarkDeleted(path, CreateOptions.FILE_NON_DIRECTORY_FILE);
    public override void DeleteEmptyFolder(string path) => MarkDeleted(path, CreateOptions.FILE_DIRECTORY_FILE);

    private void MarkDeleted(string path, CreateOptions kind)
    {
        var h = Open(path, AccessMask.DELETE, CreateDisposition.FILE_OPEN, kind, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete);
        try
        {
            var status = _store.SetFileInformation(h, new FileDispositionInformation { DeletePending = true });
            if (status != NTStatus.STATUS_SUCCESS) throw new ShareException(Explain(status, path));
        }
        finally { _store.CloseFile(h); }
    }

    public override void Move(string from, string to)
    {
        var h = Open(from, AccessMask.DELETE | AccessMask.GENERIC_READ, CreateDisposition.FILE_OPEN, 0, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete);
        try
        {
            var status = _store.SetFileInformation(h, new FileRenameInformationType2 { FileName = Inside(to), ReplaceIfExists = false });
            if (status != NTStatus.STATUS_SUCCESS) throw new ShareException(Explain(status, to));
        }
        finally { _store.CloseFile(h); }
    }

    public override Stream OpenRead(string path) =>
        new SmbStream(this, Open(path, AccessMask.GENERIC_READ, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SEQUENTIAL_ONLY, ShareAccess.Read), path, write: false);

    public override Stream Create(string path) =>
        new SmbStream(this, Open(path, AccessMask.GENERIC_WRITE, CreateDisposition.FILE_OVERWRITE_IF, CreateOptions.FILE_NON_DIRECTORY_FILE, ShareAccess.None), path, write: true);

    internal static string Explain(NTStatus status, string what) => status switch
    {
        NTStatus.STATUS_LOGON_FAILURE => "The user name or password is incorrect.",
        NTStatus.STATUS_ACCESS_DENIED => $"Access denied: {what}",
        NTStatus.STATUS_BAD_NETWORK_NAME => $"The share doesn't exist: {what}",
        NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND => $"Not found: {what}",
        NTStatus.STATUS_OBJECT_NAME_COLLISION => $"Already exists: {what}",
        NTStatus.STATUS_SHARING_VIOLATION => $"In use by someone else: {what}",
        NTStatus.STATUS_DIRECTORY_NOT_EMPTY => $"The folder isn't empty: {what}",
        _ => $"{what}: {status}",
    };

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _store?.Disconnect(); } catch { /* connection already gone */ }
        try { _client.Logoff(); } catch { /* connection already gone */ }
        try { _client.Disconnect(); } catch { /* connection already gone */ }
        _forward?.Dispose();
    }

    /// <summary>Sequential read or write over an SMB file handle, in pieces the server accepts.</summary>
    private sealed class SmbStream(ManagedSmbShare share, object handle, string path, bool write) : Stream
    {
        private long _position;
        private bool _closed;

        public override bool CanRead => !write;
        public override bool CanWrite => write;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            count = (int)Math.Min(count, share._client.MaxReadSize);
            var status = share._store.ReadFile(out var data, handle, _position, count);
            if (status == NTStatus.STATUS_END_OF_FILE || data is null) return 0;
            if (status != NTStatus.STATUS_SUCCESS) throw new ShareException(Explain(status, path));
            Array.Copy(data, 0, buffer, offset, data.Length);
            _position += data.Length;
            return data.Length;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var max = (int)share._client.MaxWriteSize;
            while (count > 0)
            {
                var n = Math.Min(count, max);
                var piece = buffer.AsSpan(offset, n).ToArray();
                var status = share._store.WriteFile(out var written, handle, _position, piece);
                if (status != NTStatus.STATUS_SUCCESS) throw new ShareException(Explain(status, path));
                if (written <= 0) throw new IOException($"The server didn't write anything: {path}");
                _position += written;
                offset += written;
                count -= written;
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_closed) { _closed = true; try { share._store.CloseFile(handle); } catch { /* connection already gone */ } }
            base.Dispose(disposing);
        }
    }
}
