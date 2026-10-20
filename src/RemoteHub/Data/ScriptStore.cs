using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace RemoteHub.Data;

/// <summary>
/// Everything that decides what a script does when it runs – the part a version freezes (docs/scripts-view-design.md
/// §4.10). Name, colour, folder and favourite pin organise and aren't part of it.
/// </summary>
public sealed record ScriptContent(string Body, string Description, string? RunContext, string? Risk, string? OsScope, int? TimeoutSec)
{
    public static ScriptContent Empty(string language) => language == ScriptStore.PowerShell
        ? new("", "", ScriptStore.ContextSystem, ScriptStore.RiskR1, ScriptStore.OsBoth, ScriptStore.DefaultTimeoutSec)
        : new("", "", null, null, null, null);

    /// <summary>SHA-256 of the text, as recorded for versions and runs.</summary>
    public byte[] Sha256() => SHA256.HashData(Encoding.UTF8.GetBytes(Body));
}

public sealed record ScriptFolderInfo(string Id, string? ParentId, string Name, string? Color, int Sort);

/// <summary>A script as listed: its draft's settings and whether the draft differs from the latest deployed version.</summary>
public sealed record ScriptInfo(string Id, string FolderId, string Name, string Language, string? Color, int Sort,
    string? RunContext, string? Risk, string? OsScope, int? DeployedVersion, bool HasDraftChanges, string? LibraryId,
    DateTime CreatedUtc, DateTime ModifiedUtc)
{
    public bool IsPowerShell => Language == ScriptStore.PowerShell;
    public bool IsDeployed => DeployedVersion is not null;
}

/// <summary>A deployed version: frozen content, who deployed it, when and why.</summary>
public sealed record ScriptVersion(string ScriptId, int Version, ScriptContent Content, byte[] Sha256, string Note, string DeployedBy, DateTime DeployedUtc);

/// <summary>One run on a device (§6.6). <see cref="Version"/> null = a draft under test.</summary>
public sealed record ScriptRunRecord(string Id, string ScriptId, int? Version, byte[] Sha256, string NodeId, string Context, string? RunUser,
    string Transport, DateTime StartedUtc, long? DurationMs, string State, int? ExitCode, string? Output);

/// <summary>A search result over the drafts; the snippet marks matches like <see cref="NoteStore.MatchStart"/>.</summary>
public sealed record ScriptSearchHit(ScriptInfo Script, string Snippet);

/// <summary>An entry of the Scripts view's "Recently deleted": a folder (with what was in it) or a script.</summary>
public sealed record DeletedScriptItem(string Kind, string Id, string Name, DateTime DeletedUtc, int Scripts);

/// <summary>
/// Script folders, scripts with their drafts and versions, and the run history in the local database
/// (docs/scripts-view-design.md §7). Every write is one transaction that keeps the full-text index in step. Nothing here
/// logs names, text or output.
/// </summary>
public static class ScriptStore
{
    public const string FolderKind = "folder", ScriptKind = "script";
    public const string PowerShell = "powershell";
    public const string ContextSystem = "System", ContextUser = "User", ContextBoth = "Both", ContextAsMe = "AsMe";
    public const string RiskR0 = "R0", RiskR1 = "R1", RiskR2 = "R2";
    public const string OsClient = "Client", OsServer = "Server", OsBoth = "Both";
    public static readonly string[] Contexts = [ContextSystem, ContextUser, ContextBoth, ContextAsMe];
    public static readonly string[] Risks = [RiskR0, RiskR1, RiskR2];
    public static readonly string[] OsScopes = [OsClient, OsServer, OsBoth];
    /// <summary>The SQL dialects a script can be written in (notes-view-design.md §4.5.5a); T-SQL first, the default.</summary>
    public static readonly string[] SqlLanguages = ["tsql", "sql", "pgsql", "mysql", "plsql", "sqlite"];
    public const int DefaultTimeoutSec = 600, MinTimeoutSec = 60, MaxTimeoutSec = 7200;
    /// <summary>Deployed versions kept per script; deploying one more removes the oldest (§4.10).</summary>
    public const int KeptVersions = 10;
    /// <summary>Runs whose output is kept per script, and for how long (§6.6).</summary>
    public const int KeptRunOutputs = 10, KeepRunOutputDays = 30;
    public const int KeepDeletedDays = NoteStore.KeepDeletedDays;
    /// <summary>Largest script text (§4.7).</summary>
    public const int MaxBodyLength = 1024 * 1024;
    public const int MaxNoteLength = 500, MaxDescriptionLength = 2000;
    /// <summary>Script ids of library scripts in favourites and run history: "library/INV-05".</summary>
    public const string LibraryPrefix = "library/";

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static DateTime Time(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Raised on the writing thread after any change (argument: the script id, or null for structure changes).</summary>
    public static event Action<string?>? Changed;

    public static bool IsSql(string language) => SqlLanguages.Contains(language);
    /// <summary>PowerShell or one of <see cref="SqlLanguages"/>; anything else (from an import) becomes PowerShell.</summary>
    public static string NormalizeLanguage(string? language) =>
        language is not null && (language == PowerShell || IsSql(language)) ? language : PowerShell;

    // ------------------------------------------------------------------ reading

    public static IReadOnlyList<ScriptFolderInfo> Folders() => LocalDb.Use((c, _) =>
        Rows(c, "SELECT id, parent_id, name, color, sort FROM script_folders WHERE deleted_utc IS NULL ORDER BY sort, name", ReadFolder));

    /// <summary>All live scripts (with <paramref name="folderId"/>: only that folder's), names and settings without text.</summary>
    public static IReadOnlyList<ScriptInfo> Scripts(string? folderId = null) => LocalDb.Use((c, _) =>
        folderId is null
            ? Rows(c, $"SELECT {InfoColumns} FROM scripts s WHERE s.deleted_utc IS NULL ORDER BY s.sort, s.name", ReadInfo)
            : Rows(c, $"SELECT {InfoColumns} FROM scripts s WHERE s.folder_id = $f AND s.deleted_utc IS NULL ORDER BY s.sort, s.name", ReadInfo, ("$f", folderId)));

    public static ScriptInfo? Script(string id) => LocalDb.Use((c, _) =>
        Rows(c, $"SELECT {InfoColumns} FROM scripts s WHERE s.id = $id", ReadInfo, ("$id", id)).FirstOrDefault());

    /// <summary>The draft: what the editor shows. Null when the script doesn't exist.</summary>
    public static ScriptContent? Draft(string id) => LocalDb.Use((c, _) =>
        Rows(c, "SELECT body, description, run_context, risk, os_scope, timeout_sec FROM scripts WHERE id = $id", ReadContent, ("$id", id)).FirstOrDefault());

    /// <summary>The kept versions, newest first.</summary>
    public static IReadOnlyList<ScriptVersion> Versions(string scriptId) => LocalDb.Use((c, _) =>
        Rows(c, $"SELECT {VersionColumns} FROM script_versions WHERE script_id = $s ORDER BY number DESC", ReadVersion, ("$s", scriptId)));

    public static ScriptVersion? Version(string scriptId, int version) => LocalDb.Use((c, _) =>
        Rows(c, $"SELECT {VersionColumns} FROM script_versions WHERE script_id = $s AND number = $v", ReadVersion, ("$s", scriptId), ("$v", version)).FirstOrDefault());

    /// <summary>The latest deployed version – what runs on devices – or null when the script was never deployed.</summary>
    public static ScriptVersion? Deployed(string scriptId) => LocalDb.Use((c, _) =>
        Rows(c, $"SELECT {VersionColumns} FROM script_versions WHERE script_id = $s ORDER BY number DESC LIMIT 1", ReadVersion, ("$s", scriptId)).FirstOrDefault());

    /// <summary>
    /// Full-text search over the live drafts: name, description and text, each word as a prefix, all words must match
    /// (the query is built by <see cref="NoteStore.FtsQuery"/>, so FTS5 syntax in the text is searched for, never run).
    /// </summary>
    public static IReadOnlyList<ScriptSearchHit> Search(string text, int limit = 200)
    {
        var query = NoteStore.FtsQuery(text);
        if (query is null) return [];
        return LocalDb.Read(c => Rows(c, $"""
            SELECT {InfoColumns}, snippet(scripts_fts, 2, char(2), char(3), '…', 10)
            FROM scripts_fts JOIN scripts s ON s.rid = scripts_fts.rowid
            WHERE scripts_fts MATCH $q AND s.deleted_utc IS NULL
            ORDER BY bm25(scripts_fts, 6.0, 2.0, 1.0) LIMIT $limit
            """, r => new ScriptSearchHit(ReadInfo(r), r.IsDBNull(InfoColumnCount) ? "" : r.GetString(InfoColumnCount)), ("$q", query), ("$limit", limit)));
    }

    /// <summary>Folders and scripts deleted in the last <see cref="KeepDeletedDays"/> days, newest first.</summary>
    public static IReadOnlyList<DeletedScriptItem> Deleted() => LocalDb.Use((c, _) =>
    {
        var list = new List<DeletedScriptItem>();
        // A folder deleted inside a folder deleted at the same time is listed only as the outer one.
        list.AddRange(Rows(c, """
            SELECT f.id, f.name, f.deleted_utc, (SELECT COUNT(*) FROM scripts s WHERE s.deleted_utc = f.deleted_utc AND s.folder_id IN
                (SELECT id FROM script_folders WHERE deleted_utc = f.deleted_utc))
            FROM script_folders f
            WHERE f.deleted_utc IS NOT NULL AND NOT EXISTS (SELECT 1 FROM script_folders p WHERE p.id = f.parent_id AND p.deleted_utc = f.deleted_utc)
            """, r => new DeletedScriptItem(FolderKind, r.GetString(0), r.GetString(1), Time(r.GetInt64(2)), r.GetInt32(3))));
        list.AddRange(Rows(c, """
            SELECT s.id, s.name, s.deleted_utc FROM scripts s
            WHERE s.deleted_utc IS NOT NULL AND NOT EXISTS (SELECT 1 FROM script_folders f WHERE f.id = s.folder_id AND f.deleted_utc = s.deleted_utc)
            """, r => new DeletedScriptItem(ScriptKind, r.GetString(0), r.GetString(1), Time(r.GetInt64(2)), 1)));
        return list.OrderByDescending(d => d.DeletedUtc).ToList();
    });

    public static (int Folders, int Scripts, int Versions) Counts() => LocalDb.Use((c, _) => (
        Count(c, "SELECT COUNT(*) FROM script_folders WHERE deleted_utc IS NULL"),
        Count(c, "SELECT COUNT(*) FROM scripts WHERE deleted_utc IS NULL"),
        Count(c, "SELECT COUNT(*) FROM script_versions v JOIN scripts s ON s.id = v.script_id WHERE s.deleted_utc IS NULL")));

    // ------------------------------------------------------------------ folders

    public static string CreateFolder(string? parentId, string name, string? color) => Write(null, (c, t) =>
    {
        var id = NewId();
        var sort = Count(c, "SELECT COALESCE(MAX(sort) + 1, 0) FROM script_folders WHERE parent_id IS $p", t, ("$p", parentId));
        LocalDb.Exec(c, "INSERT INTO script_folders (id, parent_id, name, color, sort, modified_utc) VALUES ($id, $p, $name, $color, $sort, $now)", t,
            ("$id", id), ("$p", parentId), ("$name", Clean(name, "New folder")), ("$color", color), ("$sort", sort), ("$now", Now()));
        return id;
    });

    public static void RenameFolder(string id, string name) => Update("script_folders", id, "name = $v", Clean(name, "Folder"));
    public static void SetFolderColor(string id, string? color) => Update("script_folders", id, "color = $v", color);

    /// <summary>Moves a folder under another one (null = the top). Refused (false) when that would put it inside itself.</summary>
    public static bool MoveFolder(string id, string? parentId)
    {
        if (parentId is not null && (parentId == id || IsInside(parentId, id))) return false;
        Write(null, (c, t) =>
        {
            var sort = Count(c, "SELECT COALESCE(MAX(sort) + 1, 0) FROM script_folders WHERE parent_id IS $p", t, ("$p", parentId));
            return Command(c, t, "UPDATE script_folders SET parent_id = $p, sort = $sort, modified_utc = $now, version = version + 1, dirty = 1 WHERE id = $id",
                ("$id", id), ("$p", parentId), ("$sort", sort), ("$now", Now()));
        });
        return true;
    }

    /// <summary>True when <paramref name="folderId"/> is <paramref name="ancestorId"/> or somewhere below it.</summary>
    public static bool IsInside(string folderId, string ancestorId)
    {
        var parents = Folders().ToDictionary(f => f.Id, f => f.ParentId);
        var seen = new HashSet<string>();
        for (string? f = folderId; f is not null && seen.Add(f); f = parents.GetValueOrDefault(f))
            if (f == ancestorId) return true;
        return false;
    }

    /// <summary>A folder and every folder below it (live ones).</summary>
    public static IReadOnlyList<string> FolderAndBelow(string folderId)
    {
        var folders = Folders();
        var result = new List<string> { folderId };
        for (var i = 0; i < result.Count; i++)
            result.AddRange(folders.Where(f => f.ParentId == result[i] && !result.Contains(f.Id)).Select(f => f.Id));
        return result;
    }

    // ------------------------------------------------------------------ scripts

    public static string CreateScript(string folderId, string name, string language, ScriptContent? content = null, string? color = null, string? libraryId = null)
    {
        language = NormalizeLanguage(language);
        var body = content ?? ScriptContent.Empty(language);
        Check(body);
        var id = Write(null, (c, t) =>
        {
            var id = NewId();
            var now = Now();
            var sort = Count(c, "SELECT COALESCE(MAX(sort) + 1, 0) FROM scripts WHERE folder_id = $f", t, ("$f", folderId));
            LocalDb.Exec(c, """
                INSERT INTO scripts (id, folder_id, name, language, body, description, run_context, risk, os_scope, timeout_sec,
                                     color, library_id, sort, created_utc, modified_utc)
                VALUES ($id, $f, $name, $lang, $body, $desc, $ctx, $risk, $os, $timeout, $color, $lib, $sort, $now, $now)
                """, t, ("$id", id), ("$f", folderId), ("$name", Clean(name, "New script")), ("$lang", language), ("$body", body.Body),
                ("$desc", body.Description), ("$ctx", body.RunContext), ("$risk", body.Risk), ("$os", body.OsScope), ("$timeout", body.TimeoutSec),
                ("$color", color), ("$lib", libraryId), ("$sort", sort), ("$now", now));
            Index(c, t, id);
            return id;
        });
        return id;
    }

    /// <summary>Saves the draft (autosave). False when nothing changed. Never touches a deployed version.</summary>
    public static bool SaveDraft(string id, ScriptContent content)
    {
        Check(content);
        var changed = LocalDb.Use((c, _) =>
        {
            using var t = c.BeginTransaction();
            var n = Command(c, t, """
                UPDATE scripts SET body = $body, description = $desc, run_context = $ctx, risk = $risk, os_scope = $os, timeout_sec = $timeout,
                       modified_utc = $now, version = version + 1, dirty = 1
                WHERE id = $id AND (body <> $body OR description <> $desc OR run_context IS NOT $ctx OR risk IS NOT $risk
                                    OR os_scope IS NOT $os OR timeout_sec IS NOT $timeout)
                """, ("$id", id), ("$body", content.Body), ("$desc", content.Description), ("$ctx", content.RunContext), ("$risk", content.Risk),
                ("$os", content.OsScope), ("$timeout", content.TimeoutSec), ("$now", Now()));
            if (n > 0) Index(c, t, id);
            t.Commit();
            return n > 0;
        });
        if (changed) Changed?.Invoke(id);
        return changed;
    }

    public static void RenameScript(string id, string name) => Write(id, (c, t) =>
    {
        var n = Command(c, t, "UPDATE scripts SET name = $v, modified_utc = $now, version = version + 1, dirty = 1 WHERE id = $id AND name <> $v",
            ("$id", id), ("$v", Clean(name, "Script")), ("$now", Now()));
        if (n > 0) Index(c, t, id);
        return n;
    });

    public static void SetScriptColor(string id, string? color) => Update("scripts", id, "color = $v", color);

    /// <summary>Changes the SQL dialect (colouring only). PowerShell scripts stay PowerShell.</summary>
    public static void SetLanguage(string id, string language)
    {
        if (!IsSql(language)) return;
        Write(id, (c, t) => Command(c, t, "UPDATE scripts SET language = $v, modified_utc = $now, version = version + 1, dirty = 1 WHERE id = $id AND language <> 'powershell'",
            ("$id", id), ("$v", language), ("$now", Now())));
    }

    public static void MoveScript(string id, string folderId) => Write(id, (c, t) =>
    {
        var sort = Count(c, "SELECT COALESCE(MAX(sort) + 1, 0) FROM scripts WHERE folder_id = $f", t, ("$f", folderId));
        return Command(c, t, "UPDATE scripts SET folder_id = $f, sort = $sort, modified_utc = $now, version = version + 1, dirty = 1 WHERE id = $id",
            ("$id", id), ("$f", folderId), ("$sort", sort), ("$now", Now()));
    });

    // ------------------------------------------------------------------ versions

    /// <summary>
    /// "Ready to deploy": stores the draft as the next version and returns its number. When that makes more than
    /// <see cref="KeptVersions"/>, the oldest goes (with secure_delete its text is overwritten). Numbers only grow.
    /// </summary>
    public static int Deploy(string id, string note, string deployedBy)
    {
        note = new string((note ?? "").Where(ch => !char.IsControl(ch) || ch == '\n').ToArray()).Trim();
        if (note.Length > MaxNoteLength) note = note[..MaxNoteLength];
        return Write(id, (c, t) =>
        {
            var draft = Rows(c, "SELECT body, description, run_context, risk, os_scope, timeout_sec FROM scripts WHERE id = $id AND deleted_utc IS NULL",
                ReadContent, t, ("$id", id)).FirstOrDefault() ?? throw new InvalidOperationException("The script doesn't exist.");
            var next = Count(c, "SELECT COALESCE(MAX(number), COALESCE((SELECT deployed_version FROM scripts WHERE id = $id), 0)) + 1 FROM script_versions WHERE script_id = $id", t, ("$id", id));
            var now = Now();
            LocalDb.Exec(c, """
                INSERT INTO script_versions (script_id, number, body, description, run_context, risk, os_scope, timeout_sec, body_sha256,
                                             note, deployed_by, deployed_utc, modified_utc)
                VALUES ($id, $v, $body, $desc, $ctx, $risk, $os, $timeout, $hash, $note, $by, $now, $now)
                """, t, ("$id", id), ("$v", next), ("$body", draft.Body), ("$desc", draft.Description), ("$ctx", draft.RunContext),
                ("$risk", draft.Risk), ("$os", draft.OsScope), ("$timeout", draft.TimeoutSec), ("$hash", draft.Sha256()),
                ("$note", note), ("$by", deployedBy), ("$now", now));
            Command(c, t, "UPDATE scripts SET deployed_version = $v, modified_utc = $now, version = version + 1, dirty = 1 WHERE id = $id",
                ("$id", id), ("$v", next), ("$now", now));
            Command(c, t, $"""
                DELETE FROM script_versions WHERE script_id = $id AND number NOT IN
                    (SELECT number FROM script_versions WHERE script_id = $id ORDER BY number DESC LIMIT {KeptVersions})
                """, ("$id", id));
            return next;
        });
    }

    /// <summary>Puts the draft back to the latest deployed version. False when the script was never deployed.</summary>
    public static bool DiscardDraft(string id)
    {
        if (Deployed(id) is not { } v) return false;
        SaveDraft(id, v.Content);
        return true;
    }

    /// <summary>Copies a kept version into the draft; it is deployed only through <see cref="Deploy"/>, as a new version.</summary>
    public static bool RestoreToDraft(string id, int version)
    {
        if (Version(id, version) is not { } v) return false;
        SaveDraft(id, v.Content);
        return true;
    }

    // ------------------------------------------------------------------ deleting

    /// <summary>Moves a folder (with everything below it) or a script to "Recently deleted".</summary>
    public static void Delete(string kind, string id)
    {
        var folders = kind == FolderKind ? FolderAndBelow(id) : [];
        Write(null, (c, t) =>
        {
            // One stamp for everything deleted together, later than every earlier stamp (see NoteStore.Delete).
            var last = LocalDb.Scalar(c, "SELECT MAX(m) FROM (SELECT MAX(deleted_utc) AS m FROM scripts UNION ALL SELECT MAX(deleted_utc) FROM script_folders)", t);
            var now = last is long stamp ? Math.Max(Now(), stamp + 1) : Now();
            if (kind == FolderKind)
                foreach (var f in folders)
                {
                    Command(c, t, "UPDATE scripts SET deleted_utc = $now WHERE folder_id = $f AND deleted_utc IS NULL", ("$f", f), ("$now", now));
                    Command(c, t, "UPDATE script_folders SET deleted_utc = $now, modified_utc = $now, version = version + 1, dirty = 1 WHERE id = $f AND deleted_utc IS NULL", ("$f", f), ("$now", now));
                }
            else
                Command(c, t, "UPDATE scripts SET deleted_utc = $now, modified_utc = $now, version = version + 1, dirty = 1 WHERE id = $id", ("$id", id), ("$now", now));
            Command(c, t, "DELETE FROM scripts_fts WHERE rowid IN (SELECT rid FROM scripts WHERE deleted_utc IS NOT NULL)");
            return 0;
        });
    }

    /// <summary>Brings back an entry of "Recently deleted" with what was deleted together with it.</summary>
    public static void Restore(string kind, string id) => Write(null, (c, t) =>
    {
        var table = kind == FolderKind ? "script_folders" : "scripts";
        var stamp = LocalDb.Scalar(c, $"SELECT deleted_utc FROM {table} WHERE id = $id", t, ("$id", id));
        if (stamp is null or DBNull) return 0;
        if (kind == FolderKind)
        {
            var ids = new List<string> { id };
            for (var i = 0; i < ids.Count; i++)
                ids.AddRange(Rows(c, "SELECT id FROM script_folders WHERE parent_id = $p AND deleted_utc = $st", r => r.GetString(0), t, ("$p", ids[i]), ("$st", stamp)));
            foreach (var f in ids)
            {
                Command(c, t, "UPDATE scripts SET deleted_utc = NULL WHERE folder_id = $f AND deleted_utc = $st", ("$f", f), ("$st", stamp));
                Command(c, t, "UPDATE script_folders SET deleted_utc = NULL, modified_utc = $now, version = version + 1, dirty = 1 WHERE id = $f", ("$f", f), ("$now", Now()));
            }
            // Its parent may have been deleted since: it comes back at the top.
            Command(c, t, "UPDATE script_folders SET parent_id = NULL WHERE id = $id AND parent_id IS NOT NULL AND parent_id NOT IN (SELECT id FROM script_folders WHERE deleted_utc IS NULL)", ("$id", id));
        }
        else
        {
            Command(c, t, "UPDATE scripts SET deleted_utc = NULL, modified_utc = $now, version = version + 1, dirty = 1 WHERE id = $id", ("$id", id), ("$now", Now()));
            // Its folder is gone: into the first live folder, or a new one.
            if (Count(c, "SELECT COUNT(*) FROM script_folders WHERE deleted_utc IS NULL AND id = (SELECT folder_id FROM scripts WHERE id = $id)", t, ("$id", id)) == 0)
            {
                var folder = LocalDb.Scalar(c, "SELECT id FROM script_folders WHERE deleted_utc IS NULL ORDER BY parent_id IS NOT NULL, sort LIMIT 1", t) as string
                             ?? InsertFolder(c, t, null, "Restored scripts");
                Command(c, t, "UPDATE scripts SET folder_id = $f WHERE id = $id", ("$id", id), ("$f", folder));
            }
        }
        ReindexAll(c, t);
        return 0;
    });

    /// <summary>Removes an entry of "Recently deleted" for good, with its versions (secure_delete overwrites the text).</summary>
    public static void DeleteNow(string kind, string id) => Write(null, (c, t) =>
    {
        var table = kind == FolderKind ? "script_folders" : "scripts";
        var stamp = LocalDb.Scalar(c, $"SELECT deleted_utc FROM {table} WHERE id = $id", t, ("$id", id));
        if (stamp is null or DBNull) return 0;
        if (kind == FolderKind)
        {
            var ids = new List<string> { id };
            for (var i = 0; i < ids.Count; i++)
                ids.AddRange(Rows(c, "SELECT id FROM script_folders WHERE parent_id = $p AND deleted_utc = $st", r => r.GetString(0), t, ("$p", ids[i]), ("$st", stamp)));
            foreach (var f in ids)
            {
                Purge(c, t, "SELECT id FROM scripts WHERE folder_id = $f AND deleted_utc = $st", ("$f", f), ("$st", stamp));
                Command(c, t, "DELETE FROM script_folders WHERE id = $f", ("$f", f));
            }
        }
        else Purge(c, t, "SELECT id FROM scripts WHERE id = $id", ("$id", id));
        return 0;
    });

    /// <summary>Removes what was deleted more than <see cref="KeepDeletedDays"/> days ago. Returns the number of scripts removed.</summary>
    public static int PurgeDeleted(DateTime? nowUtc = null)
    {
        var cutoff = new DateTimeOffset(nowUtc ?? DateTime.UtcNow).ToUnixTimeMilliseconds() - (long)TimeSpan.FromDays(KeepDeletedDays).TotalMilliseconds;
        return LocalDb.Use((c, _) =>
        {
            using var t = c.BeginTransaction();
            var n = Purge(c, t, "SELECT id FROM scripts WHERE deleted_utc < $cut", ("$cut", cutoff));
            Command(c, t, "DELETE FROM script_folders WHERE deleted_utc < $cut", ("$cut", cutoff));
            t.Commit();
            return n;
        });
    }

    // ------------------------------------------------------------------ runs

    public static void AddRun(ScriptRunRecord run, bool keepOutput)
    {
        LocalDb.Use((c, _) =>
        {
            using var t = c.BeginTransaction();
            LocalDb.Exec(c, """
                INSERT INTO script_runs (id, script_id, version, body_sha256, node_id, context, run_user, transport, started_utc, duration_ms,
                                         state, exit_code, output)
                VALUES ($id, $s, $v, $hash, $node, $ctx, $user, $transport, $start, $dur, $state, $exit, $out)
                ON CONFLICT(id) DO UPDATE SET duration_ms = excluded.duration_ms, state = excluded.state, exit_code = excluded.exit_code,
                                              output = excluded.output
                """, t, ("$id", run.Id), ("$s", run.ScriptId), ("$v", run.Version), ("$hash", run.Sha256), ("$node", run.NodeId),
                ("$ctx", run.Context), ("$user", run.RunUser), ("$transport", run.Transport),
                ("$start", new DateTimeOffset(run.StartedUtc).ToUnixTimeMilliseconds()), ("$dur", run.DurationMs), ("$state", run.State),
                ("$exit", run.ExitCode), ("$out", keepOutput ? run.Output : null));
            PruneOutputs(c, t, run.ScriptId);
            t.Commit();
        });
        Changed?.Invoke(run.ScriptId);
    }

    /// <summary>Runs of a script (or of every script on a connection), newest first.</summary>
    public static IReadOnlyList<ScriptRunRecord> Runs(string? scriptId = null, string? nodeId = null, int limit = 200) => LocalDb.Use((c, _) =>
        Rows(c, $"""
            SELECT id, script_id, version, body_sha256, node_id, context, run_user, transport, started_utc, duration_ms, state, exit_code, output
            FROM script_runs WHERE ($s IS NULL OR script_id = $s) AND ($n IS NULL OR node_id = $n) ORDER BY started_utc DESC LIMIT $limit
            """, ReadRun, ("$s", scriptId), ("$n", nodeId), ("$limit", limit)));

    /// <summary>The scripts run most recently from this PC (ids, newest first, each once).</summary>
    public static IReadOnlyList<string> RecentlyRun(int limit = 20) => LocalDb.Use((c, _) =>
        Rows(c, "SELECT script_id FROM script_runs GROUP BY script_id ORDER BY MAX(started_utc) DESC LIMIT $limit", r => r.GetString(0), ("$limit", limit)));

    /// <summary>How often each version of a script ran.</summary>
    public static IReadOnlyDictionary<int, int> RunCounts(string scriptId) => LocalDb.Use((c, _) =>
        Rows(c, "SELECT version, COUNT(*) FROM script_runs WHERE script_id = $s AND version IS NOT NULL GROUP BY version", r => (r.GetInt32(0), r.GetInt32(1)), ("$s", scriptId))
            .ToDictionary(x => x.Item1, x => x.Item2));

    /// <summary>Output is kept for the last <see cref="KeptRunOutputs"/> runs of each script and <see cref="KeepRunOutputDays"/> days (§6.6).</summary>
    public static void PruneRunOutputs(DateTime? nowUtc = null) => LocalDb.Use((c, _) =>
    {
        var cutoff = new DateTimeOffset(nowUtc ?? DateTime.UtcNow).ToUnixTimeMilliseconds() - (long)TimeSpan.FromDays(KeepRunOutputDays).TotalMilliseconds;
        using var t = c.BeginTransaction();
        Command(c, t, "UPDATE script_runs SET output = NULL WHERE output IS NOT NULL AND started_utc < $cut", ("$cut", cutoff));
        foreach (var s in Rows(c, "SELECT DISTINCT script_id FROM script_runs WHERE output IS NOT NULL", r => r.GetString(0), t)) PruneOutputs(c, t, s);
        t.Commit();
    });

    /// <summary>Removes every stored output (the setting "Keep the output of runs" was turned off).</summary>
    public static void ClearRunOutputs() => LocalDb.Use((c, _) => Command(c, null, "UPDATE script_runs SET output = NULL WHERE output IS NOT NULL"));

    private static void PruneOutputs(SqliteConnection c, SqliteTransaction t, string scriptId) =>
        Command(c, t, $"""
            UPDATE script_runs SET output = NULL WHERE script_id = $s AND output IS NOT NULL AND id NOT IN
                (SELECT id FROM script_runs WHERE script_id = $s ORDER BY started_utc DESC LIMIT {KeptRunOutputs})
            """, ("$s", scriptId));

    // ------------------------------------------------------------------ backup and packages

    /// <summary>Every live folder and script with its draft and kept versions (for backups and script packages).</summary>
    public static ScriptsExport ExportAll() => Export(null);

    /// <summary>The given scripts (or all), with the folders on their way, for a script package (§4.7).</summary>
    public static ScriptsExport Export(IReadOnlyCollection<string>? scriptIds) => LocalDb.Use((c, _) =>
    {
        var scripts = Rows(c, """
            SELECT id, folder_id, name, language, body, description, run_context, risk, os_scope, timeout_sec, deployed_version, color, library_id,
                   sort, created_utc, modified_utc
            FROM scripts WHERE deleted_utc IS NULL
            """, r => new ScriptsExport.Script(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                new ScriptContent(r.GetString(4), r.GetString(5), Str(r, 6), Str(r, 7), Str(r, 8), Int(r, 9)), Int(r, 10), Str(r, 11), Str(r, 12),
                r.GetInt32(13), r.GetInt64(14), r.GetInt64(15)));
        if (scriptIds is not null) scripts = scripts.Where(s => scriptIds.Contains(s.Id)).ToList();
        var folders = Rows(c, "SELECT id, parent_id, name, color, sort FROM script_folders WHERE deleted_utc IS NULL",
            r => new ScriptsExport.Folder(r.GetString(0), Str(r, 1), r.GetString(2), Str(r, 3), r.GetInt32(4)));
        if (scriptIds is not null)
        {
            var byId = folders.ToDictionary(f => f.Id);
            var keep = new HashSet<string>();
            foreach (var s in scripts)
                for (string? f = s.FolderId; f is not null && byId.ContainsKey(f) && keep.Add(f); f = byId[f].ParentId) { }
            folders = folders.Where(f => keep.Contains(f.Id)).ToList();
        }
        var ids = scripts.Select(s => s.Id).ToHashSet();
        var versions = Rows(c, $"SELECT {VersionColumns} FROM script_versions", ReadVersion).Where(v => ids.Contains(v.ScriptId))
            .Select(v => new ScriptsExport.Version(v.ScriptId, v.Version, v.Content, v.Note, v.DeployedBy, new DateTimeOffset(v.DeployedUtc).ToUnixTimeMilliseconds()))
            .ToList();
        return new ScriptsExport { Folders = folders, Scripts = scripts, Versions = versions };
    });

    /// <summary>
    /// Brings in folders and scripts from a backup or package under <paramref name="targetFolderId"/> (null = the top).
    /// Everything gets new ids, so importing twice never overwrites. <paramref name="keepDeployed"/>: the versions stay
    /// deployed as they were; otherwise each script's latest version becomes its draft and nothing is deployed (§4.7).
    /// <paramref name="decide"/> says what to do with a script whose name exists in the same folder already.
    /// Returns the number of scripts brought in.
    /// </summary>
    public static int Import(ScriptsExport data, string? targetFolderId, bool keepDeployed, Func<ScriptsExport.Script, string, ImportChoice>? decide = null,
        bool replaceAll = false)
    {
        var count = LocalDb.Use((c, _) =>
        {
            using var t = c.BeginTransaction();
            if (replaceAll)
            {
                var now = Now();
                Command(c, t, "UPDATE scripts SET deleted_utc = $now WHERE deleted_utc IS NULL", ("$now", now));
                Command(c, t, "UPDATE script_folders SET deleted_utc = $now WHERE deleted_utc IS NULL", ("$now", now));
                Command(c, t, "DELETE FROM scripts_fts");
            }
            var folderIds = new Dictionary<string, string>();
            var known = data.Folders.ToDictionary(f => f.Id);
            string? MapFolder(string? id)
            {
                if (id is null || !known.TryGetValue(id, out var f)) return targetFolderId;
                if (folderIds.TryGetValue(id, out var mapped)) return mapped;
                folderIds[id] = ""; // a loop in damaged data ends here
                var parent = MapFolder(f.ParentId);
                // A folder of the same name in the same place is used rather than made twice.
                var existing = LocalDb.Scalar(c, "SELECT id FROM script_folders WHERE parent_id IS $p AND name = $n AND deleted_utc IS NULL", t,
                    ("$p", parent == "" ? null : parent), ("$n", Clean(f.Name, "Folder"))) as string;
                var newId = existing ?? InsertFolder(c, t, parent == "" ? null : parent, Clean(f.Name, "Folder"), f.Color);
                folderIds[id] = newId;
                return newId;
            }
            var versions = data.Versions.GroupBy(v => v.ScriptId).ToDictionary(g => g.Key, g => g.OrderBy(v => v.Number).ToList());
            var n = 0;
            foreach (var s in data.Scripts)
            {
                if (s.Content.Body.Length > MaxBodyLength) continue;
                var folder = MapFolder(s.FolderId) ?? targetFolderId ?? InsertFolder(c, t, null, "Imported scripts");
                if (folder == "") folder = targetFolderId ?? InsertFolder(c, t, null, "Imported scripts");
                var name = Clean(s.Name, "Script");
                var existingId = LocalDb.Scalar(c, "SELECT id FROM scripts WHERE folder_id = $f AND name = $n AND deleted_utc IS NULL", t, ("$f", folder), ("$n", name)) as string;
                var choice = existingId is null ? ImportChoice.KeepBoth : decide?.Invoke(s, folder) ?? ImportChoice.KeepBoth;
                if (choice == ImportChoice.Skip) continue;
                var own = versions.GetValueOrDefault(s.Id) ?? [];
                // Without kept deployments the latest version (or the draft) becomes the draft.
                var draft = keepDeployed || own.Count == 0 ? s.Content : own[^1].Content;
                if (choice == ImportChoice.Replace && existingId is not null)
                {
                    Command(c, t, """
                        UPDATE scripts SET body = $body, description = $desc, run_context = $ctx, risk = $risk, os_scope = $os, timeout_sec = $timeout,
                               modified_utc = $now, version = version + 1, dirty = 1 WHERE id = $id
                        """, ("$id", existingId), ("$body", draft.Body), ("$desc", draft.Description), ("$ctx", draft.RunContext), ("$risk", draft.Risk),
                        ("$os", draft.OsScope), ("$timeout", draft.TimeoutSec), ("$now", Now()));
                    Index(c, t, existingId);
                    n++;
                    continue;
                }
                if (existingId is not null) name = FreeName(c, t, folder, name);
                var id = NewId();
                var sort = Count(c, "SELECT COALESCE(MAX(sort) + 1, 0) FROM scripts WHERE folder_id = $f", t, ("$f", folder));
                var language = NormalizeLanguage(s.Language);
                if (language != PowerShell) draft = draft with { RunContext = null, Risk = null, OsScope = null, TimeoutSec = null };
                LocalDb.Exec(c, """
                    INSERT INTO scripts (id, folder_id, name, language, body, description, run_context, risk, os_scope, timeout_sec, color, library_id,
                                         sort, created_utc, modified_utc)
                    VALUES ($id, $f, $name, $lang, $body, $desc, $ctx, $risk, $os, $timeout, $color, $lib, $sort, $created, $now)
                    """, t, ("$id", id), ("$f", folder), ("$name", name), ("$lang", language), ("$body", draft.Body), ("$desc", Limit(draft.Description, MaxDescriptionLength)),
                    ("$ctx", Pick(draft.RunContext, Contexts)), ("$risk", Pick(draft.Risk, Risks)), ("$os", Pick(draft.OsScope, OsScopes)),
                    ("$timeout", draft.TimeoutSec is { } sec ? Math.Clamp(sec, MinTimeoutSec, MaxTimeoutSec) : null), ("$color", s.Color), ("$lib", s.LibraryId),
                    ("$sort", sort), ("$created", s.CreatedUtc), ("$now", Now()));
                // The history comes along for comparison; with keepDeployed the latest version is what runs.
                var latest = 0;
                foreach (var v in own.TakeLast(KeptVersions).Where(v => v.Content.Body.Length <= MaxBodyLength))
                {
                    LocalDb.Exec(c, """
                        INSERT INTO script_versions (script_id, number, body, description, run_context, risk, os_scope, timeout_sec, body_sha256,
                                                     note, deployed_by, deployed_utc, modified_utc)
                        VALUES ($id, $v, $body, $desc, $ctx, $risk, $os, $timeout, $hash, $note, $by, $at, $now)
                        """, t, ("$id", id), ("$v", v.Number), ("$body", v.Content.Body), ("$desc", v.Content.Description), ("$ctx", v.Content.RunContext),
                        ("$risk", v.Content.Risk), ("$os", v.Content.OsScope), ("$timeout", v.Content.TimeoutSec), ("$hash", v.Content.Sha256()),
                        ("$note", Limit(v.Note, MaxNoteLength)), ("$by", Limit(v.DeployedBy, 200)), ("$at", v.DeployedUtc), ("$now", Now()));
                    latest = Math.Max(latest, v.Number);
                }
                if (latest > 0)
                    Command(c, t, keepDeployed
                        ? "UPDATE scripts SET deployed_version = $v WHERE id = $id"
                        // Not deployed here: the history is there to compare with, and the next deploy continues its numbers.
                        : "UPDATE scripts SET deployed_version = NULL WHERE id = $id", ("$id", id), ("$v", latest));
                Index(c, t, id);
                n++;
            }
            t.Commit();
            return n;
        });
        Changed?.Invoke(null);
        return count;
    }

    /// <summary>
    /// The live script an import would meet: in the folder reached from <paramref name="targetFolderId"/> through folders
    /// named <paramref name="folderNames"/> (as <see cref="Import"/> reuses them), with this name. Null when there is none.
    /// </summary>
    public static string? FindExisting(string? targetFolderId, IReadOnlyList<string> folderNames, string name)
    {
        var folders = Folders();
        var current = targetFolderId;
        foreach (var part in folderNames)
        {
            var clean = Clean(part, "Folder");
            var next = folders.FirstOrDefault(f => f.ParentId == current && f.Name == clean);
            if (next is null) return null;
            current = next.Id;
        }
        if (current is null) return null;
        var n = Clean(name, "Script");
        return Scripts(current).FirstOrDefault(s => s.Name == n)?.Id;
    }

    // ------------------------------------------------------------------ helpers

    private const string InfoColumns = """
        s.id, s.folder_id, s.name, s.language, s.color, s.sort, s.run_context, s.risk, s.os_scope, s.deployed_version,
        (s.deployed_version IS NOT NULL AND NOT EXISTS (SELECT 1 FROM script_versions v WHERE v.script_id = s.id AND v.number = s.deployed_version
            AND v.body = s.body AND v.description = s.description AND v.run_context IS s.run_context AND v.risk IS s.risk
            AND v.os_scope IS s.os_scope AND v.timeout_sec IS s.timeout_sec)),
        s.library_id, s.created_utc, s.modified_utc
        """;
    private const int InfoColumnCount = 14;
    private const string VersionColumns = "script_id, number, body, description, run_context, risk, os_scope, timeout_sec, body_sha256, note, deployed_by, deployed_utc";

    private static ScriptFolderInfo ReadFolder(SqliteDataReader r) => new(r.GetString(0), Str(r, 1), r.GetString(2), Str(r, 3), r.GetInt32(4));

    private static ScriptInfo ReadInfo(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Str(r, 4), r.GetInt32(5),
        Str(r, 6), Str(r, 7), Str(r, 8), Int(r, 9), r.GetInt64(10) != 0, Str(r, 11), Time(r.GetInt64(12)), Time(r.GetInt64(13)));

    private static ScriptContent ReadContent(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), Str(r, 2), Str(r, 3), Str(r, 4), Int(r, 5));

    private static ScriptVersion ReadVersion(SqliteDataReader r) => new(r.GetString(0), r.GetInt32(1),
        new ScriptContent(r.GetString(2), r.GetString(3), Str(r, 4), Str(r, 5), Str(r, 6), Int(r, 7)), (byte[])r.GetValue(8), r.GetString(9),
        r.GetString(10), Time(r.GetInt64(11)));

    private static ScriptRunRecord ReadRun(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), Int(r, 2), (byte[])r.GetValue(3), r.GetString(4),
        r.GetString(5), Str(r, 6), r.GetString(7), Time(r.GetInt64(8)), r.IsDBNull(9) ? null : r.GetInt64(9), r.GetString(10), Int(r, 11), Str(r, 12));

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static int? Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    private static string? Pick(string? value, string[] allowed) => value is not null && allowed.Contains(value) ? value : null;
    private static string Limit(string? s, int max) => (s ?? "").Length > max ? s![..max] : s ?? "";

    internal static string Clean(string? name, string fallback)
    {
        var s = new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (s.Length > 200) s = s[..200];
        return s.Length == 0 ? fallback : s;
    }

    private static void Check(ScriptContent content)
    {
        if (content.Body.Length > MaxBodyLength) throw new ScriptTooLargeException();
    }

    /// <summary>"Name (2)", "Name (3)" … the first that isn't taken in the folder.</summary>
    private static string FreeName(SqliteConnection c, SqliteTransaction t, string folder, string name)
    {
        for (var i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (Count(c, "SELECT COUNT(*) FROM scripts WHERE folder_id = $f AND name = $n AND deleted_utc IS NULL", t, ("$f", folder), ("$n", candidate)) == 0)
                return candidate;
        }
    }

    private static string InsertFolder(SqliteConnection c, SqliteTransaction t, string? parentId, string name, string? color = null)
    {
        var id = NewId();
        var sort = Count(c, "SELECT COALESCE(MAX(sort) + 1, 0) FROM script_folders WHERE parent_id IS $p", t, ("$p", parentId));
        LocalDb.Exec(c, "INSERT INTO script_folders (id, parent_id, name, color, sort, modified_utc) VALUES ($id, $p, $name, $color, $sort, $now)", t,
            ("$id", id), ("$p", parentId), ("$name", name), ("$color", color), ("$sort", sort), ("$now", Now()));
        return id;
    }

    private static void Index(SqliteConnection c, SqliteTransaction t, string id)
    {
        Command(c, t, "DELETE FROM scripts_fts WHERE rowid = (SELECT rid FROM scripts WHERE id = $id)", ("$id", id));
        Command(c, t, "INSERT INTO scripts_fts (rowid, name, description, body) SELECT rid, name, description, body FROM scripts WHERE id = $id AND deleted_utc IS NULL", ("$id", id));
    }

    private static void ReindexAll(SqliteConnection c, SqliteTransaction t)
    {
        Command(c, t, "DELETE FROM scripts_fts");
        Command(c, t, "INSERT INTO scripts_fts (rowid, name, description, body) SELECT rid, name, description, body FROM scripts WHERE deleted_utc IS NULL");
    }

    private static int Purge(SqliteConnection c, SqliteTransaction t, string query, params (string, object?)[] args)
    {
        var ids = Rows(c, query, r => r.GetString(0), t, args);
        foreach (var id in ids)
        {
            Command(c, t, "DELETE FROM scripts_fts WHERE rowid = (SELECT rid FROM scripts WHERE id = $id)", ("$id", id));
            Command(c, t, "DELETE FROM script_versions WHERE script_id = $id", ("$id", id));
            Command(c, t, "DELETE FROM scripts WHERE id = $id", ("$id", id));
        }
        return ids.Count;
    }

    private static T Write<T>(string? scriptId, Func<SqliteConnection, SqliteTransaction, T> f)
    {
        var result = LocalDb.Use((c, _) =>
        {
            using var t = c.BeginTransaction();
            var r = f(c, t);
            t.Commit();
            return r;
        });
        Changed?.Invoke(scriptId);
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

/// <summary>What an import does with a script whose name exists in its folder already (§4.7).</summary>
public enum ImportChoice { KeepBoth, Replace, Skip }

/// <summary>A script's text is over <see cref="ScriptStore.MaxBodyLength"/>.</summary>
public sealed class ScriptTooLargeException() : Exception("This script is too large – a script can be at most 1 MB.");

/// <summary>Scripts in a backup file and in a script package (.rhscripts).</summary>
public sealed class ScriptsExport
{
    public sealed record Folder(string Id, string? ParentId, string Name, string? Color, int Sort);
    public sealed record Script(string Id, string FolderId, string Name, string Language, ScriptContent Content, int? DeployedVersion,
        string? Color, string? LibraryId, int Sort, long CreatedUtc, long ModifiedUtc);
    public sealed record Version(string ScriptId, int Number, ScriptContent Content, string Note, string DeployedBy, long DeployedUtc);

    public List<Folder> Folders { get; set; } = [];
    public List<Script> Scripts { get; set; } = [];
    public List<Version> Versions { get; set; } = [];

    public bool IsEmpty => Scripts.Count == 0 && Folders.Count == 0;
}
