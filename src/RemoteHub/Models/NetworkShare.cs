using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteHub.Models;

/// <summary>The kinds of item a <see cref="NetworkShare"/> is. Ssh is a terminal, not a share (docs/network-shares.md §6).</summary>
public enum ShareProtocol { Smb, Sftp, Ftp, WebDav, Ssh }

/// <summary>FTP: TLS on the normal port after AUTH TLS (explicit), TLS from the start (implicit, port 990), or none.</summary>
public enum FtpSecurity { Explicit, Implicit, None }

/// <summary>
/// A network share, opened in a file tab of its own (this PC | the share). Either below a server in the navigation (in its
/// Shares, using the server's host and account), or on its own in a folder like a connection (its own host, the folder's
/// account). RemoteHub connects to it directly – no agent and no PowerShell on the device. Every share can have its own
/// account, and the connection is closed again when the share isn't used.
/// <para>
/// An SSH connection is kept as one too (<see cref="ShareProtocol.Ssh"/>): host, port, account, the accepted host key and
/// the jump host work as for SFTP, but it opens a terminal tab, and only on its own – never below a server.
/// </para>
/// </summary>
public sealed partial class NetworkShare : NodeBase
{
    [ObservableProperty] private ShareProtocol _protocol;
    /// <summary>Host name or address; empty = the server's (required for a share on its own).</summary>
    [ObservableProperty] private string? _host;
    /// <summary>Port; null = the protocol's default.</summary>
    [ObservableProperty] private int? _port;
    /// <summary>SMB: the share name, optionally with a folder ("share\sub"). SFTP/FTP: start folder (empty = home). WebDAV: path of the URL. SSH: unused.</summary>
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private FtpSecurity _ftpSecurity;
    /// <summary>WebDAV over HTTPS (off: plain HTTP – the password is sent unencrypted).</summary>
    [ObservableProperty] private bool _webDavHttps = true;

    // Name, account (InheritCredentials: the server's, or for a share on its own the folder's), Notes and the tree
    // state come from NodeBase.

    /// <summary>
    /// SFTP and SSH: SHA-256 fingerprint of the server's host key the user accepted. FTPS/WebDAV: SHA-256 of a certificate
    /// Windows doesn't trust but the user accepted. Anything else is refused.
    /// </summary>
    public string? TrustedFingerprint { get; set; }

    /// <summary>The server it belongs to; null = a share on its own in a folder (or at the top level).</summary>
    [JsonIgnore] public Connection? Server { get; set; }
    [JsonIgnore] public bool IsStandalone => Server is null;

    /// <summary>Number of open tabs of this share (navigation indicator only).</summary>
    [ObservableProperty] [property: JsonIgnore] private int _openTabs;
    /// <summary>For the tree's item style (shares have nothing below them).</summary>
    [JsonIgnore] public bool IsExpanded { get; set; }

    [JsonIgnore] public string EffectiveHost => string.IsNullOrWhiteSpace(Host) ? Server?.Host.Trim() ?? "" : Host.Trim();
    [JsonIgnore] public int EffectivePort => Port is > 0 and < 65536 ? Port.Value : DefaultPort(Protocol, FtpSecurity, WebDavHttps);

    public static int DefaultPort(ShareProtocol p, FtpSecurity ftp, bool https) => p switch
    {
        ShareProtocol.Smb => 445,
        ShareProtocol.Sftp or ShareProtocol.Ssh => 22,
        ShareProtocol.Ftp => ftp == FtpSecurity.Implicit ? 990 : 21,
        _ => https ? 443 : 80,
    };

    [JsonIgnore] public string ProtocolLabel => Label(Protocol, FtpSecurity, WebDavHttps);

    public static string Label(ShareProtocol p, FtpSecurity ftp = FtpSecurity.Explicit, bool https = true) => p switch
    {
        ShareProtocol.Smb => "SMB",
        ShareProtocol.Sftp => "SFTP",
        ShareProtocol.Ftp => ftp == FtpSecurity.None ? "FTP" : "FTPS",
        ShareProtocol.Ssh => "SSH",
        _ => https ? "WebDAV" : "WebDAV (HTTP)",
    };

    /// <summary>Where it is, for people: "\\HV02\share", "sftp://nas:2222/data", "https://cloud/remote.php/dav", "ssh://web01".</summary>
    [JsonIgnore] public string Address
    {
        get
        {
            var host = EffectiveHost;
            var port = Port is > 0 and < 65536 && Port != DefaultPort(Protocol, FtpSecurity, WebDavHttps) ? $":{Port}" : "";
            var path = Path.Trim();
            return Protocol switch
            {
                ShareProtocol.Smb => $@"\\{host}{port}\{path.Trim('\\', '/').Replace('/', '\\')}",
                ShareProtocol.Sftp => $"sftp://{host}{port}{Slash(path)}",
                ShareProtocol.Ftp => $"{(FtpSecurity == FtpSecurity.None ? "ftp" : "ftps")}://{host}{port}{Slash(path)}",
                ShareProtocol.Ssh => $"ssh://{host}{port}",
                _ => $"{(WebDavHttps ? "https" : "http")}://{host}{port}{Slash(path)}",
            };
        }
    }

    private static string Slash(string path) => path.Length == 0 ? "" : "/" + path.Replace('\\', '/').TrimStart('/');

    [JsonIgnore] public override string DisplaySymbol => Symbol(Protocol);

    /// <summary>The icon of each kind, in the navigation, its tabs and the New menu.</summary>
    public static string Symbol(ShareProtocol p) => p switch
    {
        ShareProtocol.Smb => "FolderLink24",
        ShareProtocol.Ssh => "WindowConsole20",
        _ => "FolderGlobe20",
    };

    [JsonIgnore] public bool IsSsh => Protocol == ShareProtocol.Ssh;

    /// <summary>After something the labels depend on changed (also the server's host).</summary>
    public void RefreshLabels()
    {
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(ProtocolLabel));
        OnPropertyChanged(nameof(DisplaySymbol));
    }

    partial void OnProtocolChanged(ShareProtocol value)
    {
        RefreshLabels();
        OnPropertyChanged(nameof(IsSsh));
    }
    partial void OnHostChanged(string? value) => RefreshLabels();
    partial void OnPortChanged(int? value) => RefreshLabels();
    partial void OnPathChanged(string value) => RefreshLabels();
    partial void OnFtpSecurityChanged(FtpSecurity value) => RefreshLabels();
    partial void OnWebDavHttpsChanged(bool value) => RefreshLabels();
}
