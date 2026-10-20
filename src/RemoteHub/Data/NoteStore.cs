using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace RemoteHub.Data;

public sealed record NotebookInfo(string Id, string Name, int Sort);
public sealed record SectionInfo(string Id, string NotebookId, string Name, string? Color, int Sort);
public sealed record PageInfo(string Id, string SectionId, string Title, int Sort, DateTime CreatedUtc, DateTime ModifiedUtc);
public sealed record NoteImage(string Id, string NoteId, string Mime, int Width, int Height, byte[] Data);
/// <summary>A search result; the snippet marks matches with <see cref="NoteStore.MatchStart"/> and <see cref="NoteStore.MatchEnd"/>.</summary>
public sealed record NoteSearchHit(PageInfo Page, string Snippet);
/// <summary>An entry of "Recently deleted": a notebook, a section or a page.</summary>
public sealed record DeletedNote(string Kind, string Id, string Name, DateTime DeletedUtc, int Pages);

/// <summary>
/// Notebooks, sections, pages, their images and links in the local database (docs/notes-view-design.md §5). Every write
/// is one transaction that also keeps the full-text index in step. Nothing here logs titles or text.
/// </summary>
public static class NoteStore
{
    public const string NotebookKind = "notebook", SectionKind = "section", PageKind = "page";
    /// <summary>Marks around a match in <see cref="NoteSearchHit.Snippet"/> (characters that can't appear in note text from the editor).</summary>
    public const char MatchStart = '\u0002', MatchEnd = '\u0003';
    /// <summary>Pages in "Recently deleted" are removed for good after this many days.</summary>
    public const int KeepDeletedDays = 30;
    /// <summary>Largest page body (Markdown, UTF-16 characters ≈ bytes for the usual text) – §4.5.8.</summary>
    public const int MaxBodyLength = 1024 * 1024;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static DateTime Time(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Raised on the writing thread after any change (argument: the page id, or null for structure changes).</summary>
    public static event Action<string?>? Changed;

    // ------------------------------------------------------------------ reading

    public static IReadOnlyList<NotebookInfo> Notebooks() => LocalDb.Use((c, _) =>
        Rows(c, "SELECT id, name, sort FROM notebooks WHERE deleted_utc IS NULL ORDER BY sort, name", r => new NotebookInfo(r.GetString(0), r.GetString(1), r.GetInt32(2))));

    public static IReadOnlyList<SectionInfo> Sections() => LocalDb.Use((c, _) =>
        Rows(c, "SELECT id, notebook_id, name, color, sort FROM note_sections WHERE deleted_utc IS NULL ORDER BY sort, name",
            r => new SectionInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4))));

    /// <summary>The pages of a section, titles only (bodies load when a page opens).</summary>
    public static IReadOnlyList<PageInfo> Pages(string sectionId) => LocalDb.Use((c, _) =>
        Rows(c, $"SELECT {PageColumns} FROM notes WHERE section_id = $s AND deleted_utc IS NULL ORDER BY sort, title", ReadPage, ("$s", sectionId)));

    /// <summary>The pages linked to any of these folders or connections.</summary>
    public static IReadOnlyList<PageInfo> PagesLinkedTo(IEnumerable<string> nodeIds)
    {
        var ids = nodeIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        return LocalDb.Use((c, _) =>
        {
            var names = ids.Select((_, i) => "$n" + i).ToList();
            var args = ids.Select((id, i) => ("$n" + i, (object?)id)).ToArray();
            return Rows(c, $"""
                SELECT {PageColumns} FROM notes WHERE deleted_utc IS NULL
                AND id IN (SELECT note_id FROM note_links WHERE node_id IN ({string.Join(",", names)}))
                AND section_id IN (SELECT s.id FROM note_sections s JOIN notebooks b ON b.id = s.notebook_id WHERE s.deleted_utc IS NULL AND b.deleted_utc IS NULL)
                ORDER BY modified_utc DESC
                """, ReadPage, args);
        });
    }

    /// <summary>How many live pages link to each folder or connection.</summary>
    public static IReadOnlyDictionary<string, int> LinkCounts() => LocalDb.Use((c, _) =>
        Rows(c, "SELECT l.node_id, COUNT(*) FROM note_links l JOIN notes n ON n.id = l.note_id WHERE n.deleted_utc IS NULL GROUP BY l.node_id",
            r => (r.GetString(0), r.GetInt32(1))).ToDictionary(x => x.Item1, x => x.Item2));

    public static PageInfo? Page(string id) => LocalDb.Use((c, _) =>
        Rows(c, $"SELECT {PageColumns} FROM notes WHERE id = $id", ReadPage, ("$id", id)).FirstOrDefault());

    /// <summary>The Markdown of a page, or null when it doesn't exist.</summary>
    public static string? Body(string id) => LocalDb.Use((c, _) =>
        LocalDb.Scalar(c, "SELECT body FROM notes WHERE id = $id", null, ("$id", id)) as string);

    public static IReadOnlyList<string> Links(string noteId) => LocalDb.Use((c, _) =>
        Rows(c, "SELECT node_id FROM note_links WHERE note_id = $n ORDER BY node_id", r => r.GetString(0), ("$n", noteId)));

    public static IReadOnlyList<NoteImage> Images(string noteId) => LocalDb.Use((c, _) =>
        Rows(c, "SELECT id, note_id, mime, width, height, data FROM note_files WHERE note_id = $n", ReadImage, ("$n", noteId)));

    public static NoteImage? Image(string id) => LocalDb.Use((c, _) =>
        Rows(c, "SELECT id, note_id, mime, width, height, data FROM note_files WHERE id = $id", ReadImage, ("$id", id)).FirstOrDefault());

    /// <summary>
    /// Full-text search over every live page: each word as a prefix, all words must match. The user's text is quoted
    /// word by word, so FTS5 syntax in it ("NEAR", quotes, minus) is searched for, never interpreted.
    /// </summary>
    public static IReadOnlyList<NoteSearchHit> Search(string text, int limit = 50)
    {
        var query = FtsQuery(text);
        if (query is null) return [];
        return LocalDb.Read(c => Rows(c, $"""
            SELECT {PageColumnsOf("n")}, snippet(notes_fts, 1, char(2), char(3), '…', 14)
            FROM notes_fts JOIN notes n ON n.rid = notes_fts.rowid
            JOIN note_sections s ON s.id = n.section_id JOIN notebooks b ON b.id = s.notebook_id
            WHERE notes_fts MATCH $q AND n.deleted_utc IS NULL AND s.deleted_utc IS NULL AND b.deleted_utc IS NULL
            ORDER BY bm25(notes_fts, 4.0, 1.0) LIMIT $limit
            """, r => new NoteSearchHit(ReadPage(r), r.IsDBNull(6) ? "" : r.GetString(6)), ("$q", query), ("$limit", limit)));
    }

    /// <summary>The FTS5 query for what the user typed, or null when there is nothing to search for.</summary>
    public static string? FtsQuery(string? text)
    {
        var words = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(ch => !char.IsControl(ch)).ToArray()))
            .Where(w => w.Length > 0).Take(16).ToList();
        if (words.Count == 0) return null;
        return string.Join(" ", words.Select(w => "\"" + w.Replace("\"", "\"\"") + "\"*"));
    }

    /// <summary>Notebooks, sections and pages deleted in the last <see cref="KeepDeletedDays"/> days, newest first.</summary>
    public static IReadOnlyList<DeletedNote> Deleted() => LocalDb.Use((c, _) =>
    {
        var list = new List<DeletedNote>();
        list.AddRange(Rows(c, """
            SELECT b.id, b.name, b.deleted_utc, (SELECT COUNT(*) FROM notes n JOIN note_sections s ON s.id = n.section_id
                                                  WHERE s.notebook_id = b.id AND n.deleted_utc = b.deleted_utc)
            FROM notebooks b WHERE b.deleted_utc IS NOT NULL
            """, r => new DeletedNote(NotebookKind, r.GetString(0), r.GetString(1), Time(r.GetInt64(2)), r.GetInt32(3))));
        // A section or page deleted together with its notebook (same time stamp) is listed only as the notebook. One
        // deleted before its parent keeps its own stamp, and stays listed (LEFT JOIN) when "Delete now" has removed the
        // parent since – Restore then puts it into a live notebook or section.
        list.AddRange(Rows(c, """
            SELECT s.id, s.name, s.deleted_utc, (SELECT COUNT(*) FROM notes n WHERE n.section_id = s.id AND n.deleted_utc = s.deleted_utc)
            FROM note_sections s LEFT JOIN notebooks b ON b.id = s.notebook_id
            WHERE s.deleted_utc IS NOT NULL AND (b.deleted_utc IS NULL OR b.deleted_utc <> s.deleted_utc)
            """, r => new DeletedNote(SectionKind, r.GetString(0), r.GetString(1), Time(r.GetInt64(2)), r.GetInt32(3))));
        list.AddRange(Rows(c, """
            SELECT n.id, n.title, n.deleted_utc FROM notes n LEFT JOIN note_sections s ON s.id = n.section_id
            WHERE n.deleted_utc IS NOT NULL AND (s.deleted_utc IS NULL OR s.deleted_utc <> n.deleted_utc)
            """, r => new DeletedNote(PageKind, r.GetString(0), r.GetString(1), Time(r.GetInt64(2)), 1)));
        return list.OrderByDescending(d => d.DeletedUtc).ToList();
    });

    public static (int Notebooks, int Sections, int Pages, int Images) Counts() => LocalDb.Use((c, _) => (
        Count(c, "SELECT COUNT(*) FROM notebooks WHERE deleted_utc IS NULL"),
        Count(c, "SELECT COUNT(*) FROM note_sections WHERE deleted_utc IS NULL"),
        Count(c, "SELECT COUNT(*) FROM notes WHERE deleted_utc IS NULL"),
        Count(c, "SELECT COUNT(*) FROM note_files")));

    // ------------------------------------------------------------------ writing

    /// <summary>First start: one notebook "Notes" with a section "Quick Notes", so the view is never empty.</summary>
    public static void EnsureDefault()
    {
        var created = LocalDb.Use((c, _) =>
        {
            if (Count(c, "SELECT COUNT(*) FROM notebooks WHERE deleted_utc IS NULL") > 0) return false;
            using var t = c.BeginTransaction();
            var book = InsertNotebook(c, t, "Notes");
            InsertSection(c, t, book, "Quick Notes", "Blue");
            t.Commit();
            return true;
        });
        if (created) Changed?.Invoke(null);
    }

    public static string CreateNotebook(string name) => Write(null, (c, t) => InsertNotebook(c, t, Clean(name, "New notebook")));

    public static string CreateSection(string notebookId, string name, string? color) =>
        Write(null, (c, t) => InsertSection(c, t, notebookId, Clean(name, "New section"), color));

    public static string CreatePage(string sectionId, string title, string body = "", string bodyText = "")
    {
        CheckBody(body);
        return Write(null, (c, t) =>
        {
            var id = NewId();
            var now = Now();
            var sort = Count(c, "SELECT COALESCE(MAX(sort) + 1, 0) FROM notes WHERE section_id = $s", t, ("$s", sectionId));
            LocalDb.Exec(c, """
                INSERT INTO notes (id, section_id, title, body, body_text, sort, created_utc, modified_utc)
                VALUES ($id, $s, $title, $body, $text, $sort, $now, $now)
                """, t, ("$id", id), ("$s", sectionId), ("$title", title.Trim()), ("$body", body), ("$text", bodyText), ("$sort", sort), ("$now", now));
            Index(c, t, id);
            return id;
        });
    }

    /// <summary>Saves a page's title and Markdown (and its plain text for search). False when nothing changed.</summary>
    public static bool SavePage(string id, string title, string body, string bodyText)
    {
        CheckBody(body);
        var changed = LocalDb.Use((c, _) =>
        {
            using var t = c.BeginTransaction();
            var n = Command(c, t, """
                UPDATE notes SET title = $title, body = $body, body_text = $text, modified_utc = $now, version = version + 1, dirty = 1
                WHERE id = $id AND (title <> $title OR body <> $body)
                """, ("$id", id), ("$title", title.Trim()), ("$body", body), ("$text", bodyText), ("$now", Now()));
            if (n > 0) Index(c, t, id);
            t.Commit();
            return n > 0;
        });
        if (changed) Changed?.Invoke(id);
        return changed;
    }

    public static void RenameNotebook(string id, string name) => Update("notebooks", id, "name = $v", Clean(name, "Notebook"));
    public static void RenameSection(string id, string name) => Update("note_sections", id, "name = $v", Clean(name, "Section"));
    public static void SetSectionColor(string id, string? color) => Update("note_sections", id, "color = $v", color);

    public static void MovePage(string id, string sectionId) => Write(id, (c, t) =>
    {
        var sort = Count(c, "SELECT COALESCE(MAX(sort) + 1, 0) FROM notes WHERE section_id = $s", t, ("$s", sectionId));
        return Command(c, t, "UPDATE notes SET section_id = $s, sort = $sort, modified_utc = $now, version = version + 1, dirty = 1 WHERE id = $id",
            ("$id", id), ("$s", sectionId), ("$sort", sort), ("$now", Now()));
    });

    public static void MoveSection(string id, string notebookId) => Write(null, (c, t) =>
    {
        var sort = Count(c, "SELECT COALESCE(MAX(sort) + 1, 0) FROM note_sections WHERE notebook_id = $b", t, ("$b", notebookId));
        return Command(c, t, "UPDATE note_sections SET notebook_id = $b, sort = $sort, modified_utc = $now, version = version + 1, dirty = 1 WHERE id = $id",
            ("$id", id), ("$b", notebookId), ("$sort", sort), ("$now", Now()));
    });

    /// <summary>Stores the order of notebooks, sections or pages as shown (ids in their new order).</summary>
    public static void Reorder(string kind, IReadOnlyList<string> ids) => Write(null, (c, t) =>
    {
        var table = Table(kind);
        for (var i = 0; i < ids.Count; i++)
            Command(c, t, $"UPDATE {table} SET sort = $sort, modified_utc = $now, version = version + 1, dirty = 1 WHERE id = $id AND sort <> $sort",
                ("$id", ids[i]), ("$sort", i), ("$now", Now()));
        return 0;
    });

    /// <summary>Moves a notebook, a section (with its pages) or a page to "Recently deleted".</summary>
    public static void Delete(string kind, string id) => Write(null, (c, t) =>
    {
        // Everything deleted with it gets the same stamp, so restoring brings back exactly that and nothing deleted before.
        // The stamp is later than every earlier one: a page and then its section deleted within the same millisecond
        // would otherwise share it, and "Delete now" on the section would remove the page deleted before it too.
        var last = LocalDb.Scalar(c, """
            SELECT MAX(m) FROM (SELECT MAX(deleted_utc) AS m FROM notes UNION ALL SELECT MAX(deleted_utc) FROM note_sections
                                UNION ALL SELECT MAX(deleted_utc) FROM notebooks)
            """, t);
        var now = last is long stamp ? Math.Max(Now(), stamp + 1) : Now();
        switch (kind)
        {
            case NotebookKind:
                Command(c, t, "UPDATE notes SET deleted_utc = $now WHERE deleted_utc IS NULL AND section_id IN (SELECT id FROM note_sections WHERE notebook_id = $id AND deleted_utc IS NULL)", ("$id", id), ("$now", now));
                Command(c, t, "UPDATE note_sections SET deleted_utc = $now WHERE notebook_id = $id AND deleted_utc IS NULL", ("$id", id), ("$now", now));
                break;
            case SectionKind:
                Command(c, t, "UPDATE notes SET deleted_utc = $now WHERE section_id = $id AND deleted_utc IS NULL", ("$id", id), ("$now", now));
                break;
        }
        Command(c, t, $"UPDATE {Table(kind)} SET deleted_utc = $now, modified_utc = $now, version = version + 1, dirty = 1 WHERE id = $id", ("$id", id), ("$now", now));
        ReindexDeleted(c, t);
        return 0;
    });

    /// <summary>Brings back an entry of "Recently deleted" with what was deleted together with it.</summary>
    public static void Restore(string kind, string id) => Write(null, (c, t) =>
    {
        var stamp = LocalDb.Scalar(c, $"SELECT deleted_utc FROM {Table(kind)} WHERE id = $id", t, ("$id", id));
        if (stamp is null or DBNull) return 0;
        switch (kind)
        {
            case NotebookKind:
                Command(c, t, "UPDATE notes SET deleted_utc = NULL WHERE deleted_utc = $st AND section_id IN (SELECT id FROM note_sections WHERE notebook_id = $id AND deleted_utc = $st)", ("$id", id), ("$st", stamp));
                Command(c, t, "UPDATE note_sections SET deleted_utc = NULL WHERE notebook_id = $id AND deleted_utc = $st", ("$id", id), ("$st", stamp));
                break;
            case SectionKind:
                Command(c, t, "UPDATE notes SET deleted_utc = NULL WHERE section_id = $id AND deleted_utc = $st", ("$id", id), ("$st", stamp));
                // Its notebook may have been deleted later: bring the section back into the first live notebook then.
                if (Count(c, "SELECT COUNT(*) FROM notebooks WHERE deleted_utc IS NULL AND id = (SELECT notebook_id FROM note_sections WHERE id = $id)", t, ("$id", id)) == 0
                    && LocalDb.Scalar(c, "SELECT id FROM notebooks WHERE deleted_utc IS NULL ORDER BY sort LIMIT 1", t) is string book)
                    Command(c, t, "UPDATE note_sections SET notebook_id = $b WHERE id = $id", ("$id", id), ("$b", book));
                break;
            case PageKind:
                if (Count(c, "SELECT COUNT(*) FROM note_sections WHERE deleted_utc IS NULL AND id = (SELECT section_id FROM notes WHERE id = $id)", t, ("$id", id)) == 0
                    && LocalDb.Scalar(c, "SELECT id FROM note_sections WHERE deleted_utc IS NULL ORDER BY sort LIMIT 1", t) is string section)
                    Command(c, t, "UPDATE notes SET section_id = $s WHERE id = $id", ("$id", id), ("$s", section));
                break;
        }
        Command(c, t, $"UPDATE {Table(kind)} SET deleted_utc = NULL, modified_utc = $now, version = version + 1, dirty = 1 WHERE id = $id", ("$id", id), ("$now", Now()));
        ReindexAll(c, t);
        return 0;
    });

    /// <summary>Removes an entry of "Recently deleted" for good (with secure_delete, its text is overwritten).</summary>
    public static void DeleteNow(string kind, string id) => Write(null, (c, t) =>
    {
        var stamp = LocalDb.Scalar(c, $"SELECT deleted_utc FROM {Table(kind)} WHERE id = $id", t, ("$id", id));
        if (stamp is null or DBNull) return 0;
        var pages = kind switch
        {
            NotebookKind => "SELECT id FROM notes WHERE deleted_utc = $st AND section_id IN (SELECT id FROM note_sections WHERE notebook_id = $id AND deleted_utc = $st)",
            SectionKind => "SELECT id FROM notes WHERE section_id = $id AND deleted_utc = $st",
            _ => "SELECT id FROM notes WHERE id = $id",
        };
        Purge(c, t, pages, ("$id", id), ("$st", stamp));
        if (kind == NotebookKind) Command(c, t, "DELETE FROM note_sections WHERE notebook_id = $id AND deleted_utc = $st", ("$id", id), ("$st", stamp));
        if (kind != PageKind) Command(c, t, $"DELETE FROM {Table(kind)} WHERE id = $id", ("$id", id));
        return 0;
    });

    /// <summary>Removes what was deleted more than <see cref="KeepDeletedDays"/> days ago. Returns the number of pages removed.</summary>
    public static int PurgeDeleted(DateTime? nowUtc = null)
    {
        var cutoff = new DateTimeOffset(nowUtc ?? DateTime.UtcNow).ToUnixTimeMilliseconds() - (long)TimeSpan.FromDays(KeepDeletedDays).TotalMilliseconds;
        return LocalDb.Use((c, _) =>
        {
            using var t = c.BeginTransaction();
            // LEFT JOIN: a page whose section (or a section whose notebook) "Delete now" removed is purged by its own stamp
            var n = Purge(c, t, """
                SELECT n.id FROM notes n LEFT JOIN note_sections s ON s.id = n.section_id LEFT JOIN notebooks b ON b.id = s.notebook_id
                WHERE n.deleted_utc < $cut OR s.deleted_utc < $cut OR b.deleted_utc < $cut
                """, ("$cut", cutoff));
            Command(c, t, "DELETE FROM note_sections WHERE deleted_utc < $cut OR notebook_id IN (SELECT id FROM notebooks WHERE deleted_utc < $cut)", ("$cut", cutoff));
            Command(c, t, "DELETE FROM notebooks WHERE deleted_utc < $cut", ("$cut", cutoff));
            t.Commit();
            return n;
        });
    }

    // ------------------------------------------------------------------ images and links

    /// <summary>Stores an image (already checked and re-encoded by NoteImages) for a page. Returns its id.</summary>
    public static string AddImage(string noteId, string mime, int width, int height, byte[] data)
    {
        if (mime is not ("image/png" or "image/jpeg")) throw new ArgumentException("Only PNG and JPEG images are stored.", nameof(mime));
        return Write(null, (c, t) =>
        {
            var id = NewId();
            LocalDb.Exec(c, """
                INSERT INTO note_files (id, note_id, mime, width, height, size, sha256, data, modified_utc)
                VALUES ($id, $n, $m, $w, $h, $size, $hash, $data, $now)
                """, t, ("$id", id), ("$n", noteId), ("$m", mime), ("$w", width), ("$h", height), ("$size", data.Length),
                ("$hash", SHA256.HashData(data)), ("$data", data), ("$now", Now()));
            return id;
        });
    }

    /// <summary>Deletes the page's images that its Markdown no longer refers to.</summary>
    public static int RemoveUnusedImages(string noteId, IReadOnlyCollection<string> usedIds) => LocalDb.Use((c, _) =>
    {
        var existing = Rows(c, "SELECT id FROM note_files WHERE note_id = $n", r => r.GetString(0), ("$n", noteId));
        var unused = existing.Where(id => !usedIds.Contains(id)).ToList();
        if (unused.Count == 0) return 0;
        using var t = c.BeginTransaction();
        foreach (var id in unused) Command(c, t, "DELETE FROM note_files WHERE id = $id", ("$id", id));
        t.Commit();
        return unused.Count;
    });

    /// <summary>Copies an image to another page (paste of a page's content into another page). Returns the new id.</summary>
    public static string? CopyImage(string imageId, string toNoteId) =>
        Image(imageId) is { } img ? AddImage(toNoteId, img.Mime, img.Width, img.Height, img.Data) : null;

    public static void SetLinks(string noteId, IEnumerable<string> nodeIds) => Write(noteId, (c, t) =>
    {
        Command(c, t, "DELETE FROM note_links WHERE note_id = $n", ("$n", noteId));
        foreach (var node in nodeIds.Distinct())
            Command(c, t, "INSERT INTO note_links (note_id, node_id) VALUES ($n, $node)", ("$n", noteId), ("$node", node));
        return 0;
    });

    // ------------------------------------------------------------------ backup

    /// <summary>Everything live, for the backup file (images included).</summary>
    public static NotesExport ExportAll() => LocalDb.Use((c, _) => new NotesExport
    {
        Notebooks = Rows(c, "SELECT id, name, sort FROM notebooks WHERE deleted_utc IS NULL", r => new NotesExport.Book(r.GetString(0), r.GetString(1), r.GetInt32(2))),
        Sections = Rows(c, "SELECT id, notebook_id, name, color, sort FROM note_sections WHERE deleted_utc IS NULL",
            r => new NotesExport.Section(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4))),
        Pages = Rows(c, "SELECT id, section_id, title, body, body_text, sort, created_utc, modified_utc FROM notes WHERE deleted_utc IS NULL",
            r => new NotesExport.Page(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt64(6), r.GetInt64(7))),
        Images = Rows(c, "SELECT f.id, f.note_id, f.mime, f.width, f.height, f.data FROM note_files f JOIN notes n ON n.id = f.note_id WHERE n.deleted_utc IS NULL",
            r => new NotesExport.Image(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), (byte[])r.GetValue(5))),
        Links = Rows(c, "SELECT l.note_id, l.node_id FROM note_links l JOIN notes n ON n.id = l.note_id WHERE n.deleted_utc IS NULL",
            r => new NotesExport.Link(r.GetString(0), r.GetString(1))),
    });

    /// <summary>
    /// Brings in notes from a backup. <paramref name="replace"/>: everything here is moved to "Recently deleted" first.
    /// Otherwise the backup's notebooks are added (named <paramref name="addedSuffix"/> when given). All records get new
    /// ids, so importing the same backup twice doesn't overwrite anything. Returns the number of pages brought in.
    /// </summary>
    public static int Import(NotesExport data, bool replace, string? addedSuffix)
    {
        var count = LocalDb.Use((c, _) =>
        {
            using var t = c.BeginTransaction();
            if (replace)
                foreach (var b in Rows(c, "SELECT id FROM notebooks WHERE deleted_utc IS NULL", r => r.GetString(0), t))
                {
                    var now = Now();
                    Command(c, t, "UPDATE notes SET deleted_utc = $now WHERE deleted_utc IS NULL AND section_id IN (SELECT id FROM note_sections WHERE notebook_id = $id AND deleted_utc IS NULL)", ("$id", b), ("$now", now));
                    Command(c, t, "UPDATE note_sections SET deleted_utc = $now WHERE notebook_id = $id AND deleted_utc IS NULL", ("$id", b), ("$now", now));
                    Command(c, t, "UPDATE notebooks SET deleted_utc = $now WHERE id = $id", ("$id", b), ("$now", now));
                }
            var bookIds = new Dictionary<string, string>();
            var sectionIds = new Dictionary<string, string>();
            var pageIds = new Dictionary<string, string>();
            var imageIds = new Dictionary<string, string>();
            var baseSort = Count(c, "SELECT COALESCE(MAX(sort) + 1, 0) FROM notebooks", t);
            foreach (var b in data.Notebooks.OrderBy(b => b.Sort))
            {
                var name = addedSuffix is null ? b.Name : $"{b.Name} ({addedSuffix})";
                var id = InsertNotebook(c, t, Clean(name, "Notebook"));
                Command(c, t, "UPDATE notebooks SET sort = $s WHERE id = $id", ("$id", id), ("$s", baseSort + b.Sort));
                bookIds[b.Id] = id;
            }
            foreach (var s in data.Sections.Where(s => bookIds.ContainsKey(s.NotebookId)).OrderBy(s => s.Sort))
                sectionIds[s.Id] = InsertSection(c, t, bookIds[s.NotebookId], Clean(s.Name, "Section"), s.Color);
            foreach (var p in data.Pages.Where(p => sectionIds.ContainsKey(p.SectionId)))
                pageIds[p.Id] = NewId();
            foreach (var i in data.Images.Where(i => pageIds.ContainsKey(i.NoteId) && i.Mime is "image/png" or "image/jpeg"))
            {
                var id = NewId();
                imageIds[i.Id] = id;
                LocalDb.Exec(c, """
                    INSERT INTO note_files (id, note_id, mime, width, height, size, sha256, data, modified_utc)
                    VALUES ($id, $n, $m, $w, $h, $size, $hash, $data, $now)
                    """, t, ("$id", id), ("$n", pageIds[i.NoteId]), ("$m", i.Mime), ("$w", i.Width), ("$h", i.Height), ("$size", i.Data.Length),
                    ("$hash", SHA256.HashData(i.Data)), ("$data", i.Data), ("$now", Now()));
            }
            foreach (var p in data.Pages.Where(p => sectionIds.ContainsKey(p.SectionId)))
            {
                // Image references point at the new image ids.
                var body = imageIds.Aggregate(p.Body, (b, kv) => b.Replace(NoteImageScheme + kv.Key, NoteImageScheme + kv.Value, StringComparison.Ordinal));
                if (body.Length > MaxBodyLength) continue;
                var id = pageIds[p.Id];
                LocalDb.Exec(c, """
                    INSERT INTO notes (id, section_id, title, body, body_text, sort, created_utc, modified_utc)
                    VALUES ($id, $s, $title, $body, $text, $sort, $created, $modified)
                    """, t, ("$id", id), ("$s", sectionIds[p.SectionId]), ("$title", p.Title), ("$body", body), ("$text", p.BodyText),
                    ("$sort", p.Sort), ("$created", p.CreatedUtc), ("$modified", p.ModifiedUtc));
                Index(c, t, id);
            }
            foreach (var l in data.Links.Where(l => pageIds.ContainsKey(l.NoteId)))
                Command(c, t, "INSERT OR IGNORE INTO note_links (note_id, node_id) VALUES ($n, $node)", ("$n", pageIds[l.NoteId]), ("$node", l.NodeId));
            if (replace) ReindexDeleted(c, t);
            t.Commit();
            return pageIds.Count;
        });
        Changed?.Invoke(null);
        return count;
    }

    /// <summary>How a page's Markdown refers to one of its images: <c>![alt](note-file:&lt;id&gt;)</c>.</summary>
    public const string NoteImageScheme = "note-file:";

    // ------------------------------------------------------------------ helpers

    private const string PageColumns = "id, section_id, title, sort, created_utc, modified_utc";
    private static string PageColumnsOf(string a) => string.Join(", ", PageColumns.Split(", ").Select(col => a + "." + col));

    private static PageInfo ReadPage(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), Time(r.GetInt64(4)), Time(r.GetInt64(5)));

    private static NoteImage ReadImage(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), (byte[])r.GetValue(5));

    private static string Table(string kind) => kind switch
    {
        NotebookKind => "notebooks",
        SectionKind => "note_sections",
        PageKind => "notes",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Clean(string? name, string fallback)
    {
        var s = new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (s.Length > 200) s = s[..200];
        return s.Length == 0 ? fallback : s;
    }

    private static void CheckBody(string body)
    {
        if (body.Length > MaxBodyLength) throw new NotePageTooLargeException();
    }

    private static string InsertNotebook(SqliteConnection c, SqliteTransaction t, string name)
    {
        var id = NewId();
        var sort = Count(c, "SELECT COALESCE(MAX(sort) + 1, 0) FROM notebooks", t);
        LocalDb.Exec(c, "INSERT INTO notebooks (id, name, sort, modified_utc) VALUES ($id, $name, $sort, $now)", t,
            ("$id", id), ("$name", name), ("$sort", sort), ("$now", Now()));
        return id;
    }

    private static string InsertSection(SqliteConnection c, SqliteTransaction t, string notebookId, string name, string? color)
    {
        var id = NewId();
        var sort = Count(c, "SELECT COALESCE(MAX(sort) + 1, 0) FROM note_sections WHERE notebook_id = $b", t, ("$b", notebookId));
        LocalDb.Exec(c, "INSERT INTO note_sections (id, notebook_id, name, color, sort, modified_utc) VALUES ($id, $b, $name, $color, $sort, $now)", t,
            ("$id", id), ("$b", notebookId), ("$name", name), ("$color", color), ("$sort", sort), ("$now", Now()));
        return id;
    }

    /// <summary>Puts one page into the full-text index as it is now (or takes it out when it is deleted).</summary>
    private static void Index(SqliteConnection c, SqliteTransaction t, string id)
    {
        Command(c, t, "DELETE FROM notes_fts WHERE rowid = (SELECT rid FROM notes WHERE id = $id)", ("$id", id));
        Command(c, t, "INSERT INTO notes_fts (rowid, title, body_text) SELECT rid, title, body_text FROM notes WHERE id = $id AND deleted_utc IS NULL", ("$id", id));
    }

    private static void ReindexDeleted(SqliteConnection c, SqliteTransaction t) =>
        Command(c, t, "DELETE FROM notes_fts WHERE rowid IN (SELECT rid FROM notes WHERE deleted_utc IS NOT NULL)");

    private static void ReindexAll(SqliteConnection c, SqliteTransaction t)
    {
        Command(c, t, "DELETE FROM notes_fts");
        Command(c, t, "INSERT INTO notes_fts (rowid, title, body_text) SELECT rid, title, body_text FROM notes WHERE deleted_utc IS NULL");
    }

    private static int Purge(SqliteConnection c, SqliteTransaction t, string pageQuery, params (string, object?)[] args)
    {
        var ids = Rows(c, pageQuery, r => r.GetString(0), t, args);
        foreach (var id in ids)
        {
            Command(c, t, "DELETE FROM notes_fts WHERE rowid = (SELECT rid FROM notes WHERE id = $id)", ("$id", id));
            Command(c, t, "DELETE FROM note_files WHERE note_id = $id", ("$id", id));
            Command(c, t, "DELETE FROM note_links WHERE note_id = $id", ("$id", id));
            Command(c, t, "DELETE FROM notes WHERE id = $id", ("$id", id));
        }
        return ids.Count;
    }

    private static T Write<T>(string? pageId, Func<SqliteConnection, SqliteTransaction, T> f)
    {
        var result = LocalDb.Use((c, _) =>
        {
            using var t = c.BeginTransaction();
            var r = f(c, t);
            t.Commit();
            return r;
        });
        Changed?.Invoke(pageId);
        return result;
    }

    private static void Update(string table, string id, string set, object? value) => Write<int>(null, (c, t) =>
        Command(c, t, $"UPDATE {table} SET {set}, modified_utc = $now, version = version + 1, dirty = 1 WHERE id = $id", ("$id", id), ("$v", value), ("$now", Now())));

    private static int Command(SqliteConnection c, SqliteTransaction? t, string sql, params (string, object?)[] args)
    {
        using var cmd = LocalDb.Command(c, sql, t, args);
        return cmd.ExecuteNonQuery();
    }

    private static int Count(SqliteConnection c, string sql, SqliteTransaction? t = null, params (string, object?)[] args) =>
        Convert.ToInt32(LocalDb.Scalar(c, sql, t, args));

    private static List<T> Rows<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> read, params (string, object?)[] args) =>
        Rows(c, sql, read, null, args);

    private static List<T> Rows<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> read, SqliteTransaction? t, params (string, object?)[] args)
    {
        using var cmd = LocalDb.Command(c, sql, t, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(read(r));
        return list;
    }
}

/// <summary>A page's Markdown is over <see cref="NoteStore.MaxBodyLength"/>.</summary>
public sealed class NotePageTooLargeException() : Exception("This page is too large – split it into several pages.");

/// <summary>The notes part of a backup file (and of an export between PCs).</summary>
public sealed class NotesExport
{
    public sealed record Book(string Id, string Name, int Sort);
    public sealed record Section(string Id, string NotebookId, string Name, string? Color, int Sort);
    public sealed record Page(string Id, string SectionId, string Title, string Body, string BodyText, int Sort, long CreatedUtc, long ModifiedUtc);
    public sealed record Image(string Id, string NoteId, string Mime, int Width, int Height, byte[] Data);
    public sealed record Link(string NoteId, string NodeId);

    public List<Book> Notebooks { get; set; } = [];
    public List<Section> Sections { get; set; } = [];
    public List<Page> Pages { get; set; } = [];
    public List<Image> Images { get; set; } = [];
    public List<Link> Links { get; set; } = [];

    public bool IsEmpty => Notebooks.Count == 0;
}
