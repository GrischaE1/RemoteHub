using RemoteHub.Models;

namespace RemoteHub.Remoting.Shares;

/// <summary>Opens the right kind of connection for a share.</summary>
public static class ShareConnector
{
    internal const string FtpNotThroughJumpHost = "FTP can't go through a jump host – its data connections go to addresses the server announces. SMB, SFTP and WebDAV can.";

    /// <summary>
    /// Connects to <paramref name="share"/> with <paramref name="credentials"/> (blocking). <paramref name="trust"/> asks
    /// the user about a key or certificate seen for the first time; <paramref name="accepted"/> stores what was accepted.
    /// With <paramref name="via"/> it goes through that jump host: SMB (RemoteHub's own client) and SFTP through a local port
    /// (<see cref="JumpForwarders"/>), WebDAV in-process – keys and certificates are checked against the share's own host,
    /// as for a direct connection.
    /// </summary>
    public static ShareFileSystem Open(NetworkShare share, CredentialSet credentials, Func<TrustRequest, bool> trust, Action<string> accepted, JumpRoute? via = null)
    {
        if (Services.Features.WhyNot(Services.Features.For(share.Protocol)) is { } off) throw new ShareException(off);
        var host = share.EffectiveHost;
        if (host.Length == 0) throw new ShareException("The share has no host – set one for the share or its server.");
        var port = share.EffectivePort;
        switch (share.Protocol)
        {
            case ShareProtocol.Smb:
                var (name, sub) = SplitSmbPath(share.Path);
                if (name.Length == 0) throw new ShareException("The share has no share name – edit it and enter the name (e.g. \"share\").");
                if (port != 445) throw new ShareException("SMB always uses port 445 – remove the port from the share.");
                // Windows' own client only connects to port 445, and 127.0.0.1:445 is this PC's own server.
                if (via is not null) return Forwarded(via, host, port, f => new ManagedSmbShare(host, name, sub, credentials, f));
                try { return new WindowsSmbShare(host, name, sub, credentials); }
                catch (CredentialConflictException)
                {
                    Services.DiagLog.Write($"Share (SMB): Windows already uses another account for the server – using RemoteHub's own SMB client");
                    return new ManagedSmbShare(host, name, sub, credentials);
                }
            case ShareProtocol.Sftp:
                if (via is not null) return Forwarded(via, host, port, f => new SftpShare(host, port, share.Path, credentials, share.TrustedFingerprint, trust, accepted, f));
                return new SftpShare(host, port, share.Path, credentials, share.TrustedFingerprint, trust, accepted);
            case ShareProtocol.Ssh:
                throw new ShareException("An SSH connection opens a terminal, not files – use an SFTP share for its files.");
            case ShareProtocol.Ftp:
                if (via is not null) throw new ShareException(FtpNotThroughJumpHost);
                return new FtpShare(host, port, share.FtpSecurity, share.Path, credentials, share.TrustedFingerprint, trust, accepted);
            default:
                try { return new WebDavShare(host, port, share.WebDavHttps, share.Path, credentials, share.TrustedFingerprint, trust, accepted, via); }
                catch (Exception ex) when (AgentClientTransport.JumpError(ex) is { } jump) { throw new ShareException(jump.Message, jump); }
        }
    }

    /// <summary>
    /// A share whose client only takes a host and port: it connects to a local port that leads through the jump host, held
    /// until the share is closed. When the jump host refused, its answer is the error (the client only saw the connection closed).
    /// </summary>
    private static ShareFileSystem Forwarded(JumpRoute via, string host, int port, Func<LocalForward, ShareFileSystem> open)
    {
        var forward = JumpForwarders.Open(via, host, port);
        try { return open(forward); }
        catch (Exception ex)
        {
            forward.Dispose();
            if (forward.TakeError() is { } why) throw new ShareException(why, ex);
            throw;
        }
    }

    /// <summary>"share\sub\folder" or "/share/sub" → ("share", "sub\folder").</summary>
    public static (string Share, string Sub) SplitSmbPath(string path)
    {
        var parts = (path ?? "").Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? ("", "") : (parts[0], string.Join("\\", parts.Skip(1)));
    }

    /// <summary>Whether the protocol can work without an account (SMB: the Windows account; FTP: anonymous; WebDAV: the Windows account).</summary>
    public static bool AccountOptional(ShareProtocol p) => p is not (ShareProtocol.Sftp or ShareProtocol.Ssh);

    /// <summary>The account can sign in with a private key (SSH, SFTP).</summary>
    public static bool TakesKey(ShareProtocol p) => p is ShareProtocol.Sftp or ShareProtocol.Ssh;
}
