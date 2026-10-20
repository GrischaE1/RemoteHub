using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteHub.Data;

namespace RemoteHub.Services;

/// <summary>What an import would bring in, before anything is written (the preview of §4.7).</summary>
public sealed class ScriptImportPlan
{
    public ScriptsExport Data { get; init; } = new();
    /// <summary>Files that aren't .ps1 or .sql, or that were too large, and so were left out.</summary>
    public int Skipped { get; init; }
    public bool FromPackage { get; init; }
}

/// <summary>A file or package that can't be imported: too large, damaged, or not a script package. The message is for the user.</summary>
public sealed class ScriptImportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Import and export of many scripts at once (docs/scripts-view-design.md §4.7): <c>.ps1</c>/<c>.sql</c> files in a
/// folder tree, with the settings in each file's RemoteHub header, or a script package (<c>.rhscripts</c>) that keeps
/// folders, settings, colours and versions. A package is a JSON envelope like a backup, encrypted with AES-256-GCM under
/// a key from a password unless the user turned that off. Everything read is untrusted: sizes, counts and depth are
/// checked before anything is written.
/// </summary>
public static class ScriptTransfer
{
    public const string PackageExtension = ".rhscripts";
    public const int MaxScripts = 5000;
    public const long MaxTotalText = 50L * 1024 * 1024;
    public const int MaxDepth = 20;
    /// <summary>A package can't be larger than its text allows (base64 and JSON included).</summary>
    public const long MaxPackageBytes = 2 * MaxTotalText + 16L * 1024 * 1024;

    private const string Format = "remotehub-scripts";

    private sealed class Envelope
    {
        public string Format { get; set; } = ScriptTransfer.Format;
        public int Version { get; set; } = 1;
        public bool Protected { get; set; }
        public string? Kdf { get; set; }
        public int Iterations { get; set; }
        public string? Salt { get; set; }
        public string? Nonce { get; set; }
        public string? Tag { get; set; }
        /// <summary>Protected: the encrypted JSON (base64). Not protected: the JSON as text.</summary>
        public string Data { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
    }

    // ------------------------------------------------------------------ files

    public static bool IsScriptFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ps1" or ".sql";

    /// <summary>
    /// Reads .ps1 and .sql files and folders (with their subfolders, which become folders) for an import. Nothing is
    /// written. Reparse points (links, junctions) aren't followed, so a loop or a link out of the folder can't be read.
    /// </summary>
    public static ScriptImportPlan ReadFiles(IEnumerable<string> paths)
    {
        var data = new ScriptsExport();
        var skipped = 0;
        long total = 0;
        var folderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? Folder(string? parentKey, string? parentId, string name, int depth)
        {
            if (depth > MaxDepth) throw new ScriptImportException($"The folders are nested more than {MaxDepth} levels deep.");
            var key = (parentKey ?? "") + "\\" + name;
            if (folderIds.TryGetValue(key, out var id)) return id;
            id = Guid.NewGuid().ToString("N");
            folderIds[key] = id;
            data.Folders.Add(new ScriptsExport.Folder(id, parentId, name, null, data.Folders.Count));
            return id;
        }

        void AddFile(string file, string? folderId)
        {
            if (!IsScriptFile(file)) { skipped++; return; }
            var info = new FileInfo(file);
            if (info.Length > ScriptStore.MaxBodyLength * 3L) { skipped++; return; } // UTF-16 at most doubles; anything far above is too large
            var text = ReadText(file);
            if (text.Length > ScriptStore.MaxBodyLength) { skipped++; return; }
            total += text.Length;
            if (total > MaxTotalText) throw new ScriptImportException("These files hold more than 50 MB of text – import fewer at once.");
            if (data.Scripts.Count >= MaxScripts) throw new ScriptImportException($"An import can bring in at most {MaxScripts} scripts.");
            var powerShell = Path.GetExtension(file).Equals(".ps1", StringComparison.OrdinalIgnoreCase);
            var header = ScriptHeader.Read(text);
            var language = powerShell ? ScriptStore.PowerShell : header?.Language ?? "tsql";
            var content = ScriptHeader.Apply(text, ScriptContent.Empty(language), powerShell);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            data.Scripts.Add(new ScriptsExport.Script(Guid.NewGuid().ToString("N"), folderId ?? "", Path.GetFileNameWithoutExtension(file), language,
                content, null, null, null, data.Scripts.Count, now, now));
        }

        void AddFolder(string dir, string? parentKey, string? parentId, int depth)
        {
            var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
            var id = Folder(parentKey, parentId, name.Length > 0 ? name : "Imported", depth);
            var key = (parentKey ?? "") + "\\" + name;
            foreach (var f in Directory.EnumerateFiles(dir).Order(StringComparer.OrdinalIgnoreCase))
                if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0) AddFile(f, id); else skipped++;
            foreach (var d in Directory.EnumerateDirectories(dir).Order(StringComparer.OrdinalIgnoreCase))
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) AddFolder(d, key, id, depth + 1); else skipped++;
        }

        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                AddFolder(p, null, null, 1);
            }
            else if (File.Exists(p)) AddFile(p, null);
        }
        return new ScriptImportPlan { Data = data, Skipped = skipped };
    }

    /// <summary>
    /// Text of a script file: UTF-8 or UTF-16 by its BOM; without one UTF-8, and Windows-1252 when that isn't valid
    /// UTF-8 (what Windows PowerShell 5.1 does with a file without a BOM).
    /// </summary>
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Decode(bytes);
    }

    internal static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return Windows1252(bytes); }
    }

    /// <summary>Windows-1252 without the code pages provider: Latin-1 except 0x80–0x9F.</summary>
    private static string Windows1252(byte[] bytes)
    {
        const string high = "€�‚ƒ„…†‡ˆ‰Š‹Œ�Ž��‘’“”•–—˜™š›œ�žŸ";
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes) sb.Append(b is >= 0x80 and <= 0x9F ? high[b - 0x80] : (char)b);
        return sb.ToString();
    }

    /// <summary>One script to write as a file: its folder path below the export folder, its name, language and content.</summary>
    public sealed record FileItem(IReadOnlyList<string> FolderPath, string Name, string Language, ScriptContent Content, string? LibraryId);

    /// <summary>
    /// Writes scripts as files under <paramref name="folder"/> – UTF-8 with a BOM (so Windows PowerShell 5.1 reads them
    /// right), the settings in a header line. Existing files aren't overwritten: a name that is taken gets " (2)".
    /// Returns the number of files written.
    /// </summary>
    public static int WriteFiles(IEnumerable<FileItem> items, string folder)
    {
        var n = 0;
        foreach (var item in items)
        {
            var dir = item.FolderPath.Aggregate(folder, (d, part) => Path.Combine(d, SafeName(part)));
            Directory.CreateDirectory(dir);
            var ext = item.Language == ScriptStore.PowerShell ? ".ps1" : ".sql";
            var baseName = SafeName(item.Name);
            var path = Path.Combine(dir, baseName + ext);
            for (var i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{baseName} ({i}){ext}");
            var text = ScriptHeader.Write(item.Content.Body, item.Content, item.Language, item.LibraryId);
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            n++;
        }
        return n;
    }

    /// <summary>A name as a file or folder name: characters Windows doesn't allow become "-", reserved names get "_".</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim().TrimEnd('.');
        if (s.Length == 0) s = "Script";
        if (s.Length > 120) s = s[..120];
        var stem = s.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            s = "_" + s;
        return s;
    }

    // ------------------------------------------------------------------ packages

    /// <summary>Writes a script package. With a password it is encrypted like a backup; without one it is plain JSON.</summary>
    public static void WritePackage(ScriptsExport data, string path, string? password)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(data);
        var env = new Envelope { CreatedUtc = DateTime.UtcNow };
        if (string.IsNullOrEmpty(password))
        {
            env.Data = Encoding.UTF8.GetString(json);
            CryptographicOperations.ZeroMemory(json);
        }
        else
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var key = Backup.DeriveKey(password, salt, Backup.KeyIterations);
            try
            {
                env.Protected = true;
                env.Kdf = "PBKDF2-SHA256";
                env.Iterations = Backup.KeyIterations;
                env.Salt = Convert.ToBase64String(salt);
                (env.Nonce, env.Tag, env.Data) = Backup.Seal(key, json);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        File.WriteAllText(path, JsonSerializer.Serialize(env, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>True when the package needs a password to be read.</summary>
    public static bool IsProtected(string path) => ReadEnvelope(path).Protected;

    /// <summary>Reads and checks a script package. Throws <see cref="ScriptImportException"/> with a message for the user.</summary>
    public static ScriptImportPlan ReadPackage(string path, string? password)
    {
        var env = ReadEnvelope(path);
        byte[] json;
        if (env.Protected)
        {
            if (string.IsNullOrEmpty(password)) throw new ScriptImportException("This script package is protected with a password.");
            if (env.Salt is null || env.Nonce is null || env.Tag is null) throw new ScriptImportException("The script package is damaged.");
            try { SettingsStore.CheckIterations(env.Iterations); }
            catch (Exception ex) { throw new ScriptImportException("The script package is damaged.", ex); }
            var key = Backup.DeriveKey(password, Convert.FromBase64String(env.Salt), env.Iterations);
            try { json = Backup.Open(key, env.Nonce, env.Tag, env.Data); }
            catch (UnauthorizedAccessException ex) { throw new ScriptImportException("Wrong password, or the script package is damaged.", ex); }
            catch (FormatException ex) { throw new ScriptImportException("The script package is damaged.", ex); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        else json = Encoding.UTF8.GetBytes(env.Data);
        try
        {
            ScriptsExport? data;
            try { data = JsonSerializer.Deserialize<ScriptsExport>(json); }
            catch (JsonException ex) { throw new ScriptImportException("The script package is damaged.", ex); }
            if (data is null) throw new ScriptImportException("The script package is empty.");
            Validate(data);
            return new ScriptImportPlan { Data = data, FromPackage = true };
        }
        finally { CryptographicOperations.ZeroMemory(json); }
    }

    private static Envelope ReadEnvelope(string path)
    {
        if (new FileInfo(path).Length > MaxPackageBytes) throw new ScriptImportException("The script package is larger than a package can be.");
        Envelope? env;
        try { env = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path)); }
        catch (JsonException ex) { throw new ScriptImportException("This isn't a RemoteHub script package.", ex); }
        if (env is null || env.Format != Format) throw new ScriptImportException("This isn't a RemoteHub script package.");
        if (env.Version > 1) throw new ScriptImportException("This script package was made by a newer version of RemoteHub.");
        return env;
    }

    /// <summary>
    /// Checks data from a package or a backup before it is imported: counts, sizes, ids and folder depth (a loop counts as
    /// too deep). Throws <see cref="ScriptImportException"/>; the import is refused as a whole, nothing is written.
    /// </summary>
    public static void Validate(ScriptsExport data)
    {
        data.Folders ??= [];
        data.Scripts ??= [];
        data.Versions ??= [];
        if (data.Scripts.Count > MaxScripts) throw new ScriptImportException($"The package holds more than {MaxScripts} scripts.");
        if (data.Folders.Count > MaxScripts) throw new ScriptImportException($"The package holds more than {MaxScripts} folders.");
        if (data.Versions.Count > MaxScripts * ScriptStore.KeptVersions) throw new ScriptImportException("The package holds more versions than scripts can have.");
        long total = 0;
        foreach (var s in data.Scripts)
        {
            if (s is null || s.Id is null || s.Name is null || s.Content?.Body is null || s.Content.Description is null)
                throw new ScriptImportException("The script package is damaged.");
            if (s.Content.Body.Length > ScriptStore.MaxBodyLength) throw new ScriptImportException($"\"{Short(s.Name)}\" is larger than 1 MB.");
            total += s.Content.Body.Length;
        }
        foreach (var v in data.Versions)
        {
            if (v is null || v.ScriptId is null || v.Content?.Body is null || v.Content.Description is null || v.Note is null || v.DeployedBy is null)
                throw new ScriptImportException("The script package is damaged.");
            if (v.Content.Body.Length > ScriptStore.MaxBodyLength) throw new ScriptImportException("A version in the package is larger than 1 MB.");
            total += v.Content.Body.Length;
        }
        if (total > 2 * MaxTotalText) throw new ScriptImportException("The package holds more text than an import can bring in.");
        if (data.Versions.GroupBy(v => v.ScriptId).Any(g => g.Count() > ScriptStore.KeptVersions))
            throw new ScriptImportException("A script in the package has more versions than are kept.");
        var parents = new Dictionary<string, string?>();
        foreach (var f in data.Folders)
        {
            if (f is null || f.Id is null || f.Name is null) throw new ScriptImportException("The script package is damaged.");
            parents[f.Id] = f.ParentId;
        }
        foreach (var f in data.Folders)
        {
            var depth = 0;
            for (var p = f.ParentId; p is not null && parents.ContainsKey(p); p = parents[p])
                if (++depth > MaxDepth) throw new ScriptImportException($"The package's folders are nested more than {MaxDepth} levels deep.");
        }
    }

    private static string Short(string name) => name.Length > 60 ? name[..60] + "…" : name;
}
