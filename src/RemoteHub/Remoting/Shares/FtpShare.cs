using System.Net.Security;
using FluentFTP;
using FluentFTP.Exceptions;
using RemoteHub.Models;

namespace RemoteHub.Remoting.Shares;

/// <summary>
/// FTP and FTPS (FluentFTP). With TLS, a certificate Windows doesn't trust is only accepted if it is the one accepted
/// for the share before, or the user accepts it now. Without TLS the password is sent unencrypted (the editor says so).
/// </summary>
public sealed class FtpShare : ShareFileSystem
{
    private readonly FtpClient _client;

    internal enum CertificateDecision { Accept, Refuse, Ask }

    /// <summary>
    /// A certificate Windows trusts, or the one accepted for this share: accepted. Otherwise the user is asked – except
    /// on a data connection: one whose certificate isn't the control connection's is someone else in between, refused
    /// without asking (FluentFTP before 55.1.0 accepted any certificate there – its 55.1.0 release notes).
    /// </summary>
    internal static CertificateDecision Decide(SslPolicyErrors errors, string fingerprint, string? trusted, bool dataConnection) =>
        errors == SslPolicyErrors.None || fingerprint == trusted ? CertificateDecision.Accept
        : dataConnection ? CertificateDecision.Refuse
        : CertificateDecision.Ask;

    public FtpShare(string host, int port, FtpSecurity security, string startFolder, CredentialSet credentials, string? trusted,
                    Func<TrustRequest, bool> trust, Action<string> accepted)
    {
        var anonymous = credentials.IsEmpty;
        var user = anonymous ? "anonymous" : SftpShare.UserName(credentials);
        var password = anonymous ? "anonymous@" : credentials.Password?.Reveal() ?? "";
        var config = new FtpConfig
        {
            EncryptionMode = security switch { FtpSecurity.Implicit => FtpEncryptionMode.Implicit, FtpSecurity.None => FtpEncryptionMode.None, _ => FtpEncryptionMode.Explicit },
            DataConnectionEncryption = true,
            ValidateAnyCertificate = false,
            ConnectTimeout = 20_000,
            ReadTimeout = 30_000,
            DataConnectionConnectTimeout = 20_000,
            DataConnectionReadTimeout = 30_000,
        };
        _client = new FtpClient(host, user, password, port, config, null);
        _client.ValidateCertificate += (_, e) =>
        {
            var fp = Fingerprints.Of(e.Certificate);
            var decision = Decide(e.PolicyErrors, fp, trusted, e.IsDataConnection);
            if (decision != CertificateDecision.Ask) { e.Accept = decision == CertificateDecision.Accept; return; }
            e.Accept = trust(new TrustRequest(host, "certificate Windows doesn't trust", fp, $"{e.Certificate.Subject}\n{e.PolicyErrorMessage}"));
            if (e.Accept) { accepted(fp); trusted = fp; }
        };
        try { _client.Connect(); }
        catch (FtpAuthenticationException) { _client.Dispose(); throw new ShareException("The user name or password is incorrect."); }
        catch (FtpInvalidCertificateException) { _client.Dispose(); throw new ShareException("The server's certificate wasn't accepted – nothing was sent."); }
        catch { _client.Dispose(); throw; }

        var start = startFolder.Trim().Replace('\\', '/');
        var home = Try(() => _client.GetWorkingDirectory()) ?? "/";
        Start = start.Length == 0 ? home : start.StartsWith('/') ? start : Join(home, start);
        if (Start.Length > 1) Start = Start.TrimEnd('/');
        Description = (security == FtpSecurity.None ? "FTP · not encrypted" : "FTPS") + (string.IsNullOrEmpty(_client.SystemType) ? "" : " · " + _client.SystemType);
    }

    private static string? Try(Func<string> f) { try { return f(); } catch { return null; } }

    public override string Start { get; }
    public override string Top => "/";
    public override char Separator => '/';
    public override string Description { get; }

    public override List<FileEntry> List(string folder) => Call(() => _client.GetListing(folder)
        .Where(i => i.Name is not ("." or ".."))
        .Select(i =>
        {
            var dir = i.Type == FtpObjectType.Directory || (i.Type == FtpObjectType.Link && i.LinkObject?.Type == FtpObjectType.Directory);
            return new FileEntry(i.Name, Join(folder, i.Name), dir ? EntryKind.Folder : EntryKind.File, dir ? 0 : Math.Max(0, i.Size),
                i.Modified == DateTime.MinValue ? null : i.Modified.ToUniversalTime(), i.Name.StartsWith('.'));
        }).ToList(), folder);

    public override FileEntry? Stat(string path) => Call(() =>
    {
        if (_client.DirectoryExists(path)) return new FileEntry(NameOf(path), path, EntryKind.Folder, 0, null, false);
        return _client.FileExists(path) ? new FileEntry(NameOf(path), path, EntryKind.File, Math.Max(0, _client.GetFileSize(path)), null, false) : null;
    }, path);

    public override void CreateFolder(string path) => Call(() => _client.CreateDirectory(path), path);
    public override void DeleteFile(string path) => Call(() => { _client.DeleteFile(path); return true; }, path);
    public override void DeleteEmptyFolder(string path) => Call(() => { _client.DeleteDirectory(path); return true; }, path);
    /// <summary>FluentFTP deletes a folder with everything in it.</summary>
    public override void Delete(string path, bool folder) => Call(() => { if (folder) _client.DeleteDirectory(path); else _client.DeleteFile(path); return true; }, path);
    public override void Move(string from, string to) => Call(() => { _client.Rename(from, to); return true; }, from);

    // Whole transfers go through FluentFTP's own upload/download, which also read the server's answer at the end.
    public override void Upload(Stream source, long length, string path, Action<long> progress, Func<bool> cancelled) => Call(() =>
    {
        var status = _client.UploadStream(new Watched(source, cancelled), path, FtpRemoteExists.Overwrite, false, p => progress(p.TransferredBytes));
        if (status != FtpStatus.Success) throw new ShareException($"Upload failed: {path}");
        return true;
    }, path);

    public override void Download(string path, Stream target, Action<long> progress, Func<bool> cancelled) => Call(() =>
    {
        if (!_client.DownloadStream(new Watched(target, cancelled), path, 0, p => progress(p.TransferredBytes), 0)) throw new ShareException($"Download failed: {path}");
        return true;
    }, path);

    public override Stream OpenRead(string path) => throw new NotSupportedException();
    public override Stream Create(string path) => throw new NotSupportedException();

    /// <summary>Answers from the server (550 no such file, permission denied) become <see cref="ShareException"/>.</summary>
    private static T Call<T>(Func<T> call, string path)
    {
        try { return call(); }
        catch (FtpCommandException ex) { throw new ShareException($"{path}: {ex.Message}", ex); }
        catch (FtpException ex) when (ex.InnerException is OperationCanceledException oce) { throw oce; }
    }

    public override void Dispose()
    {
        try { _client.Disconnect(); } catch { /* connection already gone */ }
        _client.Dispose();
    }
}

/// <summary>Passes reads and writes through, and stops them once the transfer is cancelled.</summary>
internal sealed class Watched(Stream inner, Func<bool> cancelled) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => inner.CanSeek ? inner.Length : throw new NotSupportedException();
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (cancelled()) throw new OperationCanceledException();
        return inner.Read(buffer, offset, count);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (cancelled()) throw new OperationCanceledException();
        inner.Write(buffer, offset, count);
    }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
