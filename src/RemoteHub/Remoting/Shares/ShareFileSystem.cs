using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteHub.Remoting.Shares;

/// <summary>
/// One open connection to a network share (SMB, SFTP, FTP, WebDAV). Paths are shown as the protocol writes them:
/// UNC paths for SMB (\\server\share\folder), "/folder/file" for the others. Every call runs on the calling thread and
/// blocks – callers run them in the background, one at a time per instance.
/// </summary>
public abstract class ShareFileSystem : IDisposable
{
    /// <summary>Folder shown first: the share, or the start folder set for it.</summary>
    public abstract string Start { get; }
    /// <summary>Highest folder "Up" goes to (the share itself for SMB, "/" or the WebDAV base path for the others).</summary>
    public abstract string Top { get; }
    public abstract char Separator { get; }
    /// <summary>How the share is reached, for the tab header ("SMB · Windows", "SFTP · OpenSSH_9.5", …).</summary>
    public abstract string Description { get; }
    /// <summary>Whether two paths that differ only in case are the same (SMB: yes).</summary>
    protected virtual StringComparison PathComparison => StringComparison.Ordinal;

    public abstract List<FileEntry> List(string folder);
    /// <summary>The file or folder at <paramref name="path"/>, or null if there is none.</summary>
    public virtual FileEntry? Stat(string path)
    {
        var parent = Parent(path);
        if (SamePath(parent, path)) return new FileEntry(NameOf(path), path, EntryKind.Folder, 0, null, false);
        var name = NameOf(path);
        List<FileEntry> entries;
        try { entries = List(parent); }
        catch (ShareException) when (Stat(parent) is null) { return null; } // the folder above doesn't exist either
        return entries.FirstOrDefault(e => string.Equals(e.Name, name, PathComparison));
    }
    public abstract void CreateFolder(string path);
    public abstract void DeleteFile(string path);
    /// <summary>Deletes a folder that is already empty.</summary>
    public abstract void DeleteEmptyFolder(string path);
    /// <summary>Renames or moves; the target must not exist.</summary>
    public abstract void Move(string from, string to);
    public abstract Stream OpenRead(string path);
    /// <summary>Creates the file, or empties it if it exists.</summary>
    public abstract Stream Create(string path);

    /// <summary>Writes <paramref name="source"/> to <paramref name="path"/>; <paramref name="progress"/> gets the bytes written so far.</summary>
    public virtual void Upload(Stream source, long length, string path, Action<long> progress, Func<bool> cancelled)
    {
        using var target = Create(path);
        Copy(source, target, progress, cancelled);
    }

    public virtual void Download(string path, Stream target, Action<long> progress, Func<bool> cancelled)
    {
        using var source = OpenRead(path);
        Copy(source, target, progress, cancelled);
    }

    /// <summary>Deletes a file, or a folder with everything in it.</summary>
    public virtual void Delete(string path, bool folder)
    {
        if (!folder) { DeleteFile(path); return; }
        foreach (var e in List(path)) Delete(e.FullPath, e.Kind == EntryKind.Folder);
        DeleteEmptyFolder(path);
    }

    /// <summary>Every file below the given files/folders: (size, full path, path relative to the folder they are in).</summary>
    public List<(long Size, string FullPath, string Relative)> Expand(IEnumerable<string> paths)
    {
        var result = new List<(long, string, string)>();
        foreach (var p in paths)
        {
            var e = Stat(p) ?? throw new ShareException($"Not found: {p}");
            if (e.Kind == EntryKind.File) { result.Add((e.Size, e.FullPath, e.Name)); continue; }
            var name = NameOf(p);
            void Walk(string folder, string rel)
            {
                foreach (var c in List(folder))
                {
                    var r = rel + "\\" + c.Name;
                    if (c.Kind == EntryKind.File) result.Add((c.Size, c.FullPath, r));
                    else Walk(c.FullPath, r);
                }
            }
            Walk(e.FullPath, name);
        }
        return result;
    }

    // ------------------------------------------------------------------ paths

    public string Normalize(string path)
    {
        var p = (path ?? "").Trim();
        if (p.Length == 0) return Start;
        p = p.Replace(Separator == '/' ? '\\' : '/', Separator);
        if (!p.StartsWith(Separator)) p = JoinRelative(Start, p);
        while (p.Length > Top.Length && p.Length > 1 && p.EndsWith(Separator)) p = p[..^1];
        return p;
    }

    public bool SamePath(string a, string b) => string.Equals(a.TrimEnd(Separator), b.TrimEnd(Separator), PathComparison);

    /// <summary>The folder above; never above <see cref="Top"/>.</summary>
    public string Parent(string path)
    {
        var p = path.TrimEnd(Separator);
        var top = Top.TrimEnd(Separator);
        if (p.Length <= top.Length || !IsBelow(p, top)) return Top;
        var i = p.LastIndexOf(Separator);
        if (i < 0) return Top;
        var parent = i == 0 ? Separator.ToString() : p[..i];
        return parent.TrimEnd(Separator).Length < top.Length ? Top : parent;
    }

    private bool IsBelow(string path, string top) =>
        top.Length == 0 || (path.StartsWith(top, PathComparison) && (path.Length == top.Length || path[top.Length] == Separator));

    public string Join(string folder, string name) => folder.TrimEnd(Separator) + Separator + name.Trim(Separator, '\\', '/');

    /// <summary>Joins a relative path with backslashes ("sub\file.txt") the way this share writes paths.</summary>
    public string JoinRelative(string folder, string relative) =>
        relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Aggregate(folder, Join);

    public string NameOf(string path)
    {
        var p = path.TrimEnd(Separator);
        var i = p.LastIndexOf(Separator);
        return i < 0 ? p : p[(i + 1)..];
    }

    protected static void Copy(Stream source, Stream target, Action<long> progress, Func<bool> cancelled)
    {
        var buffer = new byte[256 * 1024];
        long done = 0;
        int n;
        while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (cancelled()) throw new OperationCanceledException();
            target.Write(buffer, 0, n);
            done += n;
            progress(done);
        }
        target.Flush();
    }

    public abstract void Dispose();
}

/// <summary>Something the share refused or that doesn't exist – the connection itself is fine (message is shown as is).</summary>
public sealed class ShareException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A server presented a key or certificate that isn't trusted yet: the user decides.</summary>
public sealed record TrustRequest(string Host, string What, string Fingerprint, string Details);

/// <summary>Fingerprints for people: "AB:CD:…" for certificates, "SHA256:…" for SSH host keys.</summary>
public static class Fingerprints
{
    public static string Of(X509Certificate cert) => string.Join(":", SHA256.HashData(cert.GetRawCertData()).Select(b => b.ToString("X2")));
}
