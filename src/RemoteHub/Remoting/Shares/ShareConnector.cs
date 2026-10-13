using RemoteHub.Models;

namespace RemoteHub.Remoting.Shares;

/// <summary>Opens the right kind of connection for a share.</summary>
public static class ShareConnector
{
    /// <summary>
    /// Connects to <paramref name="share"/> with <paramref name="credentials"/> (blocking). <paramref name="trust"/> asks
    /// the user about a key or certificate seen for the first time; <paramref name="accepted"/> stores what was accepted.
    /// </summary>
    public static ShareFileSystem Open(NetworkShare share, CredentialSet credentials, Func<TrustRequest, bool> trust, Action<string> accepted)
    {
        var host = share.EffectiveHost;
        if (host.Length == 0) throw new ShareException("The share has no host – set one for the share or its server.");
        var port = share.EffectivePort;
        switch (share.Protocol)
        {
            case ShareProtocol.Smb:
                var (name, sub) = SplitSmbPath(share.Path);
                if (name.Length == 0) throw new ShareException("The share has no share name – edit it and enter the name (e.g. \"share\").");
                if (port != 445) throw new ShareException("SMB always uses port 445 – remove the port from the share.");
                try { return new WindowsSmbShare(host, name, sub, credentials); }
                catch (CredentialConflictException)
                {
                    Services.DiagLog.Write($"Share (SMB): Windows already uses another account for the server – using RemoteHub's own SMB client");
                    return new ManagedSmbShare(host, name, sub, credentials);
                }
            case ShareProtocol.Sftp:
                return new SftpShare(host, port, share.Path, credentials, share.TrustedFingerprint, trust, accepted);
            case ShareProtocol.Ftp:
                return new FtpShare(host, port, share.FtpSecurity, share.Path, credentials, share.TrustedFingerprint, trust, accepted);
            default:
                return new WebDavShare(host, port, share.WebDavHttps, share.Path, credentials, share.TrustedFingerprint, trust, accepted);
        }
    }

    /// <summary>"share\sub\folder" or "/share/sub" → ("share", "sub\folder").</summary>
    public static (string Share, string Sub) SplitSmbPath(string path)
    {
        var parts = (path ?? "").Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? ("", "") : (parts[0], string.Join("\\", parts.Skip(1)));
    }

    /// <summary>Whether the protocol can work without an account (SMB: the Windows account; FTP: anonymous; WebDAV: the Windows account).</summary>
    public static bool AccountOptional(ShareProtocol p) => p != ShareProtocol.Sftp;
}
