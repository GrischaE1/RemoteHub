using System.Text;
using System.Text.RegularExpressions;
using RemoteHub.Data;

namespace RemoteHub.Services;

/// <summary>A page to export: its title, Markdown and the images it refers to.</summary>
public sealed record ExportPage(string Title, string Body, IReadOnlyList<NoteImage> Images);

/// <summary>A page read from a Markdown file; its images are referred to as <c>note-file:</c> + <see cref="NoteFiles.Pending"/> + index.</summary>
public sealed record ImportedPage(string Title, string Body, IReadOnlyList<PreparedImage> Images);

/// <summary>
/// Markdown files on disk (docs/notes-view-design.md §4.7): export writes one .md per page with an assets folder for its
/// images and never overwrites anything; import reads .md files as text and follows image paths only inside their folder.
/// </summary>
public static partial class NoteFiles
{
    public const int MaxNameLength = 100;
    /// <summary>Placeholder for an image of an imported page until it has an id.</summary>
    public const string Pending = "pending-";

    private static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>A file or folder name from a title: no characters Windows refuses, no reserved names, at most 100 characters.</summary>
    public static string SafeName(string? title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var ch in (title ?? "").Trim()) sb.Append(invalid.Contains(ch) || char.IsControl(ch) ? '-' : ch);
        var name = sb.ToString();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        name = name.TrimEnd('.', ' ');
        if (name.Length == 0) name = "Untitled page";
        var stem = name.Split('.')[0];
        if (Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase)) name = "_" + name;
        return name;
    }

    /// <summary>A name in <paramref name="folder"/> that doesn't exist yet: "name", "name (2)", "name (3)" …</summary>
    public static string Unique(string folder, string name, string extension = "")
    {
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? name : $"{name} ({n})";
            var path = Path.Combine(folder, candidate + extension);
            if (!File.Exists(path) && !Directory.Exists(path)) return candidate;
        }
    }

    /// <summary>
    /// Writes a notebook or section: a new folder "<paramref name="name"/> yyyy-MM-dd HH-mm" in <paramref name="parent"/>
    /// with one folder per section. Returns the folder written and the number of pages.
    /// </summary>
    public static (string Folder, int Pages) ExportTree(string parent, string name, IEnumerable<(string Section, IReadOnlyList<ExportPage> Pages)> sections)
    {
        var root = Path.Combine(parent, Unique(parent, SafeName($"{name} {DateTime.Now:yyyy-MM-dd HH-mm}")));
        Directory.CreateDirectory(root);
        var count = 0;
        foreach (var (section, pages) in sections)
        {
            var dir = Path.Combine(root, Unique(root, SafeName(section)));
            Directory.CreateDirectory(dir);
            foreach (var p in pages) { ExportPageTo(dir, p); count++; }
        }
        return (root, count);
    }

    /// <summary>Writes one page as "Title.md" (and "Title.assets\" for its images) into <paramref name="folder"/>. Returns the file.</summary>
    public static string ExportPageTo(string folder, ExportPage page)
    {
        var name = Unique(folder, SafeName(page.Title), ".md");
        var file = Inside(folder, name + ".md");
        var body = page.Body;
        if (page.Images.Count > 0)
        {
            var assetsName = Unique(folder, name + ".assets");
            var assets = Inside(folder, assetsName);
            var used = NoteMarkdown.ImageIds(body);
            var n = 0;
            foreach (var img in page.Images.Where(i => used.Contains(i.Id)))
            {
                if (n == 0) Directory.CreateDirectory(assets);
                var fileName = $"image-{++n}" + (img.Mime == "image/jpeg" ? ".jpg" : ".png");
                File.WriteAllBytes(Inside(assets, fileName), img.Data);
                // Other tools don't read the width attribute; the link uses a relative path with escaped spaces.
                body = Regex.Replace(body, $@"\({Regex.Escape(NoteStore.NoteImageScheme + img.Id)}\)(\{{width=\d+\}})?",
                    "(" + Uri.EscapeDataString(assetsName) + "/" + fileName + ")");
            }
        }
        var text = "# " + page.Title.Trim() + "\n\n" + body;
        File.WriteAllText(file, text, new UTF8Encoding(false));
        return file;
    }

    /// <summary>A path inside <paramref name="folder"/>; anything that would leave it is refused.</summary>
    private static string Inside(string folder, string name)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("The export would write outside the chosen folder.");
        return full;
    }

    // ------------------------------------------------------------------ import

    /// <summary>
    /// Reads a Markdown file as a page. A first-line "# Heading" is the title (else the file name). Images with a relative
    /// path inside the file's folder are read and re-encoded; absolute, UNC, URL and "..\" paths stay as they are (text).
    /// </summary>
    public static ImportedPage Import(string file)
    {
        var info = new FileInfo(file);
        if (info.Length > NoteStore.MaxBodyLength) throw new IOException($"{info.Name} is larger than 1 MB.");
        var text = File.ReadAllText(file).Replace("\r\n", "\n");
        var title = Path.GetFileNameWithoutExtension(file);
        if (Heading().Match(text) is { Success: true } h)
        {
            title = h.Groups[1].Value.Trim();
            text = text[h.Length..].TrimStart('\n');
        }
        var baseDir = Path.GetFullPath(info.DirectoryName!).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var images = new List<PreparedImage>();
        text = ImageLink().Replace(text, m =>
        {
            var target = m.Groups[2].Value.Trim().Trim('<', '>');
            if (images.Count >= NoteImages.MaxImagesPerPage || !TryLocal(baseDir, target, out var path)) return m.Value;
            try
            {
                var prepared = NoteImages.Prepare(File.ReadAllBytes(path));
                images.Add(prepared);
                return $"![{m.Groups[1].Value}]({NoteStore.NoteImageScheme}{Pending}{images.Count - 1})";
            }
            catch (Exception ex) when (ex is NoteImageException or IOException or UnauthorizedAccessException) { return m.Value; }
        });
        return new ImportedPage(title.Length > 0 ? title : "Untitled page", text, images);
    }

    /// <summary>A relative image path that stays inside <paramref name="baseDir"/> and is a plain file (no link).</summary>
    public static bool TryLocal(string baseDir, string target, out string path)
    {
        path = "";
        if (target.Length == 0 || target.Contains(':') || target.StartsWith('/') || target.StartsWith('\\')) return false;
        string decoded;
        try { decoded = Uri.UnescapeDataString(target); }
        catch (UriFormatException) { return false; }
        if (decoded.Contains(':') || decoded.StartsWith('/') || decoded.StartsWith('\\')) return false;
        var full = Path.GetFullPath(Path.Combine(baseDir, decoded.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)) return false;
        var fi = new FileInfo(full);
        if (!fi.Exists || fi.Attributes.HasFlag(FileAttributes.ReparsePoint) || fi.Length > NoteImages.MaxSourceBytes) return false;
        path = full;
        return true;
    }

    [GeneratedRegex(@"^#[ \t]+(.+?)[ \t#]*(\n|$)")]
    private static partial Regex Heading();

    [GeneratedRegex(@"!\[([^\]\n]*)\]\(([^)\n]+)\)(\{width=\d+\})?")]
    private static partial Regex ImageLink();
}
