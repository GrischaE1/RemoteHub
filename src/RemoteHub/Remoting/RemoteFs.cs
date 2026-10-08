using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace RemoteHub.Remoting;

public enum EntryKind { Drive, Folder, File }

/// <summary>One row in a file pane (local or remote).</summary>
public sealed record FileEntry(string Name, string FullPath, EntryKind Kind, long Size, DateTime? Modified, bool Hidden)
{
    public bool IsContainer => Kind != EntryKind.File;
    public string Symbol => Kind switch { EntryKind.Drive => "HardDrive20", EntryKind.Folder => "Folder24", _ => "Document24" };
    public string SizeText => Kind == EntryKind.File ? TransferItem.Size(Size) : "";
    public string ModifiedText => Modified?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
    public double Opacity => Hidden ? 0.55 : 1.0;

    /// <summary>Folders first, then by name.</summary>
    public static List<FileEntry> Sort(IEnumerable<FileEntry> e) =>
        e.OrderBy(x => x.Kind == EntryKind.File ? 1 : 0).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
}

/// <summary>Folder listing result: the resolved path ("" = drive list) and its entries.</summary>
public sealed record Listing(string Path, List<FileEntry> Entries);

/// <summary>
/// File system access for the file transfer tab. Only the folder that is shown gets listed (one call per
/// navigation); nothing is walked recursively except when a folder is actually transferred.
/// </summary>
public static class RemoteFs
{
    // One line per entry ("D|size|utcTicks|hidden|name") keeps the remote call small and fast to deserialize.
    private const string ListScript = """
        param([string]$Path)
        $p = [Environment]::ExpandEnvironmentVariables($Path.Trim())
        if ($p -eq '' -or $p -eq 'This PC') {
            'P|'
            foreach ($d in [IO.DriveInfo]::GetDrives()) { if ($d.IsReady) { 'R|' + $d.TotalSize + '|0|0|' + $d.Name } }
            return
        }
        if ($p -match '^[A-Za-z]:$') { $p += '\' }
        $dir = New-Object IO.DirectoryInfo($p)
        if (-not $dir.Exists) { throw "Folder not found: $p" }
        'P|' + $dir.FullName
        foreach ($e in $dir.GetFileSystemInfos()) {
            $h = if ($e.Attributes -band [IO.FileAttributes]::Hidden) { 1 } else { 0 }
            if ($e -is [IO.DirectoryInfo]) { 'D|0|' + $e.LastWriteTimeUtc.Ticks + '|' + $h + '|' + $e.Name }
            else { 'F|' + $e.Length + '|' + $e.LastWriteTimeUtc.Ticks + '|' + $h + '|' + $e.Name }
        }
        """;

    private const string ExistingScript = """
        param([string]$Folder, [string[]]$Names)
        foreach ($n in $Names) { if (Test-Path -LiteralPath (Join-Path $Folder $n)) { $n } }
        """;

    private const string DeleteScript = """
        param([string[]]$Paths)
        foreach ($p in $Paths) {
            if ($p.TrimEnd('\').Length -le 2) { throw "Refusing to delete a drive root: $p" }
            Remove-Item -LiteralPath $p -Recurse -Force
        }
        """;

    private const string RenameScript = """
        param([string]$Path, [string]$NewName)
        (Rename-Item -LiteralPath $Path -NewName $NewName -PassThru).FullName
        """;

    private const string MkdirScript = """
        param([string]$Path)
        [IO.Directory]::CreateDirectory([Environment]::ExpandEnvironmentVariables($Path)).FullName
        """;

    // "size|full path|relative path" for every file below the given files/folders.
    private const string ExpandScript = """
        param([string[]]$Paths)
        foreach ($p in $Paths) {
            $i = Get-Item -LiteralPath $p -Force
            if ($i.PSIsContainer) {
                $base = [IO.Path]::GetDirectoryName($i.FullName.TrimEnd('\'))
                if (-not $base) { throw "A whole drive cannot be downloaded: $p" }
                foreach ($f in (Get-ChildItem -LiteralPath $i.FullName -Recurse -Force | Where-Object { -not $_.PSIsContainer })) {
                    '' + $f.Length + '|' + $f.FullName + '|' + $f.FullName.Substring($base.Length).TrimStart('\')
                }
            } else { '' + $i.Length + '|' + $i.FullName + '|' + $i.Name }
        }
        """;

    public static Listing ParseListing(IEnumerable<string> lines)
    {
        string path = "";
        var entries = new List<FileEntry>();
        foreach (var line in lines)
        {
            if (line.StartsWith("P|")) { path = line[2..]; continue; }
            var parts = line.Split('|', 5);
            if (parts.Length < 5) continue;
            long.TryParse(parts[1], out var size);
            long.TryParse(parts[2], out var ticks);
            var name = parts[4];
            var kind = parts[0] switch { "R" => EntryKind.Drive, "D" => EntryKind.Folder, _ => EntryKind.File };
            var full = kind == EntryKind.Drive ? name : path.TrimEnd('\\') + "\\" + name;
            entries.Add(new FileEntry(name, full, kind, size, ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null, parts[3] == "1"));
        }
        return new Listing(path, FileEntry.Sort(entries));
    }

    public static Listing ListRemote(Runspace rs, string path) =>
        ParseListing(Invoke(rs, ListScript, ("Path", path)).Select(o => o?.ToString() ?? ""));

    public static List<string> ExistingRemote(Runspace rs, string folder, IEnumerable<string> names) =>
        Invoke(rs, ExistingScript, ("Folder", folder), ("Names", names.ToArray())).Select(o => o.ToString()).ToList();

    /// <summary>Deletes files and folders (recursively, permanently – there is no recycle bin over remoting).</summary>
    public static void DeleteRemote(Runspace rs, IEnumerable<string> paths) => Invoke(rs, DeleteScript, ("Paths", paths.ToArray()));

    public static string RenameRemote(Runspace rs, string path, string newName) =>
        Invoke(rs, RenameScript, ("Path", path), ("NewName", newName)).FirstOrDefault()?.ToString() ?? path;

    /// <summary>A file or folder name without path separators or characters Windows does not allow.</summary>
    public static bool IsValidName(string name) =>
        name.Length > 0 && name.Trim() == name && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not ("." or "..");

    public static string MkdirRemote(Runspace rs, string path) => Invoke(rs, MkdirScript, ("Path", path)).FirstOrDefault()?.ToString() ?? path;

    public static List<(long Size, string FullPath, string Relative)> ExpandRemote(Runspace rs, IEnumerable<string> paths) =>
        Invoke(rs, ExpandScript, ("Paths", paths.ToArray()))
            .Select(o => o.ToString().Split('|', 3))
            .Where(p => p.Length == 3)
            .Select(p => (long.TryParse(p[0], out var s) ? s : 0, p[1], p[2]))
            .ToList();

    /// <summary>Local listing with the same rules as the remote one ("" or "This PC" = drives).</summary>
    public static Listing ListLocal(string path)
    {
        var p = Environment.ExpandEnvironmentVariables(path.Trim());
        if (p.Length == 0 || p == "This PC")
            return new Listing("", DriveInfo.GetDrives().Where(d => d.IsReady)
                .Select(d => new FileEntry(d.Name, d.Name, EntryKind.Drive, SafeSize(d), null, false)).ToList());
        if (p.Length == 2 && p[1] == ':') p += "\\";
        var dir = new DirectoryInfo(p);
        if (!dir.Exists) throw new DirectoryNotFoundException($"Folder not found: {p}");
        var entries = new List<FileEntry>();
        foreach (var e in dir.EnumerateFileSystemInfos())
        {
            try
            {
                var hidden = e.Attributes.HasFlag(FileAttributes.Hidden);
                entries.Add(e is DirectoryInfo
                    ? new FileEntry(e.Name, e.FullName, EntryKind.Folder, 0, e.LastWriteTimeUtc, hidden)
                    : new FileEntry(e.Name, e.FullName, EntryKind.File, ((FileInfo)e).Length, e.LastWriteTimeUtc, hidden));
            }
            catch { /* entry vanished or no access */ }
        }
        return new Listing(dir.FullName, FileEntry.Sort(entries));
    }

    private static long SafeSize(DriveInfo d) { try { return d.TotalSize; } catch { return 0; } }

    /// <summary>Parent folder; "" (drive list) above a drive root.</summary>
    public static string Parent(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var trimmed = path.TrimEnd('\\');
        var i = trimmed.LastIndexOf('\\');
        if (i < 0) return ""; // "C:" -> drives
        return i == 2 && trimmed[1] == ':' ? trimmed[..3] : trimmed[..i];
    }

    internal static List<PSObject> Invoke(Runspace rs, string script, params (string Name, object Value)[] args)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        ps.AddScript(script);
        foreach (var (n, v) in args) ps.AddParameter(n, v);
        List<PSObject> result;
        try { result = ps.Invoke().ToList(); }
        catch (RuntimeException ex) when (ex is not System.Management.Automation.Remoting.PSRemotingTransportException)
        {
            throw new RemoteCommandException(ex.ErrorRecord?.Exception?.Message ?? ex.Message);
        }
        if (ps.HadErrors && ps.Streams.Error.Count > 0)
            throw new RemoteCommandException(ps.Streams.Error[0].Exception?.Message ?? ps.Streams.Error[0].ToString());
        return result;
    }
}
