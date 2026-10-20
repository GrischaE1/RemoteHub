using RemoteHub.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RemoteHub.Remoting.Shares;

/// <summary>
/// SFTP (SSH.NET). The server's host key must match the fingerprint accepted for the share; the first time, or when
/// it changed, the user is asked (<paramref name="trust"/>) – nothing is sent before that. Behind a jump host it connects
/// to <paramref name="forward"/>'s local port; the key is still checked against the share's own fingerprint.
/// </summary>
public sealed class SftpShare : ShareFileSystem
{
    private readonly SftpClient _client;
    private readonly LocalForward? _forward;

    public SftpShare(string host, int port, string startFolder, CredentialSet credentials, string? trusted, Func<TrustRequest, bool> trust, Action<string> accepted,
        LocalForward? forward = null)
    {
        if (credentials.IsEmpty) throw new ShareException("SFTP needs an account – set one for the share or its server.");
        _forward = forward;
        var user = UserName(credentials);
        var password = credentials.Password ?? "";
        var keyboard = new KeyboardInteractiveAuthenticationMethod(user);
        keyboard.AuthenticationPrompt += (_, e) =>
        {
            foreach (var p in e.Prompts) if (p.Request.Contains("password", StringComparison.OrdinalIgnoreCase)) p.Response = password;
        };
        var info = new ConnectionInfo(forward?.Host ?? host, forward?.Port ?? port, user, new PasswordAuthenticationMethod(user, password), keyboard) { Timeout = TimeSpan.FromSeconds(20) };
        var refused = false;
        string? acceptedNow = null;
        SftpClient Connect(string? known)
        {
            // Without a limit a stalled server would hang the tab (SSH.NET waits forever by default).
            var client = new SftpClient(info) { OperationTimeout = TimeSpan.FromSeconds(60) };
            client.HostKeyReceived += (_, e) =>
            {
                var fp = "SHA256:" + e.FingerPrintSHA256;
                if (fp == known) { e.CanTrust = true; return; }
                e.CanTrust = trust(new TrustRequest(host, trusted is null ? "SSH host key" : "SSH host key (changed!)", fp,
                    $"{e.HostKeyName}, {e.KeyLength} bits"));
                if (e.CanTrust) { acceptedNow = fp; accepted(fp); }
                else refused = true;
            };
            try { client.Connect(); return client; }
            catch { client.Dispose(); throw; }
        }
        try
        {
            try { _client = Connect(trusted); }
            // The question waits for the user while SSH.NET's connection timeout runs, so an answer after it ends this
            // attempt ("Session operation has timed out"). The key is accepted by then: the second attempt doesn't ask.
            catch (SshOperationTimeoutException) when (acceptedNow is not null) { _client = Connect(acceptedNow); }
        }
        catch (Exception ex) when (refused && ex is SshConnectionException or SshOperationTimeoutException)
        {
            throw new ShareException("The server's key wasn't accepted – nothing was sent.");
        }
        catch (SshAuthenticationException)
        {
            throw new ShareException("The user name or password is incorrect (or the server doesn't allow password sign-in).");
        }

        var start = startFolder.Trim().Replace('\\', '/');
        Start = start.Length == 0 ? _client.WorkingDirectory : start.StartsWith('/') ? start : Join(_client.WorkingDirectory, start);
        if (Start.Length > 1) Start = Start.TrimEnd('/');
        Description = "SFTP · " + _client.ConnectionInfo.ServerVersion;
    }

    /// <summary>A Windows-style "DOMAIN\user" for Windows OpenSSH; just the user elsewhere.</summary>
    internal static string UserName(CredentialSet c)
    {
        var user = c.Username!.Trim();
        return user.Contains('\\') || user.Contains('@') || string.IsNullOrEmpty(c.Domain) || c.IsLocalAccount ? user : $"{c.Domain}\\{user}";
    }

    public override string Start { get; }
    public override string Top => "/";
    public override char Separator => '/';
    public override string Description { get; }

    public override List<FileEntry> List(string folder) => Call(() =>
    {
        var list = new List<FileEntry>();
        foreach (var f in _client.ListDirectory(folder))
        {
            if (f.Name is "." or "..") continue;
            var dir = f.IsDirectory;
            if (f.IsSymbolicLink) try { dir = _client.GetAttributes(f.FullName).IsDirectory; } catch { /* dangling link */ }
            list.Add(new FileEntry(f.Name, Join(folder, f.Name), dir ? EntryKind.Folder : EntryKind.File, dir ? 0 : f.Length, f.LastWriteTimeUtc, f.Name.StartsWith('.')));
        }
        return list;
    }, folder);

    public override FileEntry? Stat(string path) => Call(() =>
    {
        if (!_client.Exists(path)) return null;
        var a = _client.GetAttributes(path);
        return new FileEntry(NameOf(path), path, a.IsDirectory ? EntryKind.Folder : EntryKind.File, a.IsDirectory ? 0 : a.Size, a.LastWriteTimeUtc, false);
    }, path);

    public override void CreateFolder(string path) => Call(() => { _client.CreateDirectory(path); return true; }, path);
    public override void DeleteFile(string path) => Call(() => { _client.DeleteFile(path); return true; }, path);
    public override void DeleteEmptyFolder(string path) => Call(() => { _client.DeleteDirectory(path); return true; }, path);
    public override void Move(string from, string to) => Call(() => { _client.RenameFile(from, to); return true; }, from);
    public override Stream OpenRead(string path) => Call(() => (Stream)_client.OpenRead(path), path);
    public override Stream Create(string path) => Call(() => (Stream)_client.Create(path), path);

    /// <summary>Answers from the server (no such file, permission denied) become <see cref="ShareException"/>.</summary>
    private static T Call<T>(Func<T> call, string path)
    {
        try { return call(); }
        catch (SftpPathNotFoundException) { throw new ShareException($"Not found: {path}"); }
        catch (SftpPermissionDeniedException) { throw new ShareException($"Permission denied: {path}"); }
        catch (SshException ex) when (ex is not SshConnectionException) { throw new ShareException($"{path}: {ex.Message}", ex); }
    }

    public override void Dispose()
    {
        try { _client.Disconnect(); } catch { /* connection already gone */ }
        _client.Dispose();
        _forward?.Dispose();
    }
}
