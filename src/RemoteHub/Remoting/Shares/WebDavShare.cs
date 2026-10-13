using System.Threading;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Xml.Linq;
using RemoteHub.Models;

namespace RemoteHub.Remoting.Shares;

/// <summary>
/// WebDAV (RFC 4918) over HTTPS – or HTTP if chosen. Signs in with Basic, Digest, NTLM or Kerberos, whatever the server
/// offers; with no account set, the Windows account is used (intranet servers). Paths are the URL's decoded path.
/// </summary>
public sealed class WebDavShare : ShareFileSystem
{
    private static readonly XNamespace Dav = "DAV:";
    private const string PropfindBody = """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>""";
    private readonly HttpClient _http;
    private readonly Uri _origin;

    public WebDavShare(string host, int port, bool https, string basePath, CredentialSet credentials, string? trusted, Func<TrustRequest, bool> trust, Action<string> accepted)
    {
        var scheme = https ? "https" : "http";
        _origin = new UriBuilder(scheme, host, port).Uri;
        var top = "/" + basePath.Trim().Replace('\\', '/').Trim('/');
        Top = Start = top;
        var handler = new HttpClientHandler
        {
            PreAuthenticate = true,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (errors == SslPolicyErrors.None) return true;
                if (cert is null) return false;
                var fp = Fingerprints.Of(cert);
                if (fp == trusted) return true;
                var ok = trust(new TrustRequest(host, "certificate Windows doesn't trust", fp, $"{cert.Subject}\n{errors}"));
                if (ok) { accepted(fp); trusted = fp; }
                return ok;
            },
        };
        if (credentials.IsEmpty) handler.UseDefaultCredentials = true;
        else
        {
            var user = credentials.Username!.Trim();
            var domain = credentials.IsLocalAccount ? host : credentials.Domain ?? "";
            var i = user.IndexOf('\\');
            if (i > 0) { domain = user[..i]; user = user[(i + 1)..]; }
            handler.Credentials = new NetworkCredential(user, credentials.Password ?? "", domain);
        }
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RemoteHub");
        try
        {
            if (Propfind(Start, depth: 0, folder: true)?.FirstOrDefault() is not { Kind: EntryKind.Folder }) throw new ShareException($"Folder not found: {Start}");
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            _http.Dispose();
            throw new ShareException("The server's certificate wasn't accepted – nothing was sent.");
        }
        catch { _http.Dispose(); throw; }
        Description = https ? "WebDAV · HTTPS" : "WebDAV · HTTP, not encrypted";
    }

    public override string Start { get; }
    public override string Top { get; }
    public override char Separator => '/';
    public override string Description { get; }

    /// <summary>"/Docs/a b.txt" → https://host/Docs/a%20b.txt (folders end with "/").</summary>
    internal Uri UrlOf(string path, bool folder = false)
    {
        var escaped = string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        if (folder && !escaped.EndsWith('/')) escaped += "/";
        return new Uri(_origin, escaped.Length == 0 ? "/" : escaped);
    }

    public override List<FileEntry> List(string folder)
    {
        var entries = Propfind(folder, depth: 1, folder: true) ?? throw new ShareException($"Folder not found: {folder}");
        return entries.Where(e => !SamePath(e.FullPath, folder)).ToList();
    }

    public override FileEntry? Stat(string path) => Propfind(path, depth: 0, folder: false)?.FirstOrDefault();

    private List<FileEntry>? Propfind(string path, int depth, bool folder)
    {
        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), UrlOf(path, folder))
        {
            Content = new StringContent(PropfindBody, Encoding.UTF8, "application/xml"),
        };
        req.Headers.Add("Depth", depth.ToString(CultureInfo.InvariantCulture));
        using var resp = Send(req);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        // Many servers redirect "/folder" to "/folder/".
        if (!folder && (int)resp.StatusCode is 301 or 302 or 307 or 308) return Propfind(path, depth, folder: true);
        Check(resp, path);
        using var body = resp.Content.ReadAsStream();
        return ParseMultistatus(XDocument.Load(body), _origin);
    }

    /// <summary>The entries of a 207 multistatus answer (paths decoded, without a trailing "/").</summary>
    internal static List<FileEntry> ParseMultistatus(XDocument doc, Uri origin)
    {
        var list = new List<FileEntry>();
        foreach (var r in doc.Descendants(Dav + "response"))
        {
            var href = r.Element(Dav + "href")?.Value.Trim();
            if (string.IsNullOrEmpty(href)) continue;
            var prop = r.Elements(Dav + "propstat")
                .Where(ps => ps.Element(Dav + "status")?.Value.Contains(" 200 ") != false)
                .Select(ps => ps.Element(Dav + "prop")).FirstOrDefault(p => p is not null);
            var path = Uri.UnescapeDataString(new Uri(origin, href).AbsolutePath);
            var dir = prop?.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;
            if (path.Length > 1) path = path.TrimEnd('/');
            var i = path.LastIndexOf('/');
            var name = i < 0 ? path : path[(i + 1)..];
            long.TryParse(prop?.Element(Dav + "getcontentlength")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            DateTime? modified = DateTimeOffset.TryParse(prop?.Element(Dav + "getlastmodified")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var m) ? m.UtcDateTime : null;
            list.Add(new FileEntry(name, path, dir ? EntryKind.Folder : EntryKind.File, dir ? 0 : size, modified, name.StartsWith('.')));
        }
        return list;
    }

    public override void CreateFolder(string path) => Simple("MKCOL", path, folder: true);
    public override void DeleteFile(string path) => Simple("DELETE", path, folder: false);
    public override void DeleteEmptyFolder(string path) => Simple("DELETE", path, folder: true);
    /// <summary>A WebDAV DELETE of a folder removes everything in it.</summary>
    public override void Delete(string path, bool folder) => Simple("DELETE", path, folder);

    public override void Move(string from, string to)
    {
        var folder = Stat(from)?.Kind == EntryKind.Folder;
        using var req = new HttpRequestMessage(new HttpMethod("MOVE"), UrlOf(from, folder));
        req.Headers.Add("Destination", UrlOf(to, folder).AbsoluteUri);
        req.Headers.Add("Overwrite", "F");
        using var resp = Send(req);
        Check(resp, from);
    }

    private void Simple(string method, string path, bool folder)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), UrlOf(path, folder));
        using var resp = Send(req);
        Check(resp, path);
    }

    public override Stream OpenRead(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, UrlOf(path));
        var resp = Send(req, HttpCompletionOption.ResponseHeadersRead);
        try { Check(resp, path); }
        catch { resp.Dispose(); req.Dispose(); throw; }
        return resp.Content.ReadAsStream();
    }

    public override Stream Create(string path) => throw new NotSupportedException();

    public override void Upload(Stream source, long length, string path, Action<long> progress, Func<bool> cancelled)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, UrlOf(path)) { Content = new StreamContent(new Counted(source, progress, cancelled), 256 * 1024) };
        req.Content.Headers.ContentLength = length;
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = Send(req);
        Check(resp, path);
    }

    private HttpResponseMessage Send(HttpRequestMessage req, HttpCompletionOption option = HttpCompletionOption.ResponseContentRead)
    {
        try { return _http.Send(req, option); }
        catch (HttpRequestException ex) when (ex.InnerException is OperationCanceledException oce) { throw oce; }
    }

    private static void Check(HttpResponseMessage resp, string path)
    {
        if (resp.IsSuccessStatusCode) return;
        throw new ShareException((int)resp.StatusCode switch
        {
            401 => "The user name or password is incorrect.",
            403 => $"Access denied: {path}",
            404 or 409 => $"Not found: {path}",
            405 => $"Not allowed here (does it already exist?): {path}",
            412 => $"Already exists: {path}",
            423 => $"Locked by someone else: {path}",
            507 => "The server has no space left.",
            301 or 302 or 307 or 308 => $"The server redirects to {resp.Headers.Location} – use that address for the share.",
            var code => $"{path}: HTTP {code} {resp.ReasonPhrase}",
        });
    }

    public override void Dispose() => _http.Dispose();

    /// <summary>Counts what is read for the progress, and stops reading once cancelled.</summary>
    private sealed class Counted(Stream inner, Action<long> progress, Func<bool> cancelled) : Stream
    {
        private long _done;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _done; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (cancelled()) throw new OperationCanceledException();
            var n = inner.Read(buffer, offset, count);
            _done += n;
            progress(_done);
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
