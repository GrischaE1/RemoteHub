using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RemoteHub.Models;
using RemoteHub.Services;

namespace RemoteHub.Data;

/// <summary>A folder or connection in "Recently deleted": where it was, what it was, and how many items it held.</summary>
public sealed record DeletedNode(string Id, string? ParentId, string Name, string Kind, int Items, DateTime DeletedUtc);

/// <summary>
/// "Recently deleted" for the connection tree (docs/scripts-view-design.md §4.9). A deleted folder or connection is kept
/// in this PC's database for 30 days as one sealed record: the item with everything below it, its saved passwords
/// inside, encrypted with the database's secret key and bound to its row. In shared-folder mode the item leaves the
/// shared file at once and only this PC keeps the copy. Nothing here logs names or hosts.
/// </summary>
public static class DeletedNodeStore
{
    public const int KeepDays = NoteStore.KeepDeletedDays;

    private sealed record Payload(List<Guid> Pinned, string Data);

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string Place(string id) => $"deleted_nodes|{id}|data";

    /// <summary>Keeps a deleted item (with what is below it) and the favourite pins it had.</summary>
    public static void Add(NodeBase node, IReadOnlyList<Guid> pinned)
    {
        var json = Encoding.UTF8.GetString(SettingsStore.ToJson(new AppData { Items = [node] }));
        var payload = JsonSerializer.Serialize(new Payload([.. pinned], json));
        var id = node.Id.ToString();
        var kind = node switch { Folder => "folder", Connection => "connection", _ => "share" };
        LocalDb.Use((c, key) =>
        {
            using var t = c.BeginTransaction();
            LocalDb.Exec(c, """
                INSERT INTO deleted_nodes (id, parent_id, name, kind, items, deleted_utc, data) VALUES ($id, $p, $name, $kind, $items, $now, $data)
                ON CONFLICT(id) DO UPDATE SET parent_id = excluded.parent_id, name = excluded.name, kind = excluded.kind, items = excluded.items,
                    deleted_utc = excluded.deleted_utc, data = excluded.data
                """, t, ("$id", id), ("$p", node.Parent?.Id.ToString()), ("$name", node.Name), ("$kind", kind),
                ("$items", node.Descendants().Count()), ("$now", Now()), ("$data", SecretBox.Seal(key, payload, Place(id))));
            t.Commit();
        });
    }

    /// <summary>What was deleted in the last <see cref="KeepDays"/> days, newest first.</summary>
    public static IReadOnlyList<DeletedNode> List() => LocalDb.Use((c, _) =>
    {
        using var cmd = LocalDb.Command(c, "SELECT id, parent_id, name, kind, items, deleted_utc FROM deleted_nodes ORDER BY deleted_utc DESC", null);
        using var r = cmd.ExecuteReader();
        var list = new List<DeletedNode>();
        while (r.Read())
            list.Add(new DeletedNode(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)).UtcDateTime));
        return list;
    });

    /// <summary>
    /// Takes an item out of "Recently deleted" to put it back: the item (parents linked, as loaded from settings), the
    /// folder it was in, and its favourite pins. Null when it isn't there (any more).
    /// </summary>
    public static (NodeBase Node, Guid? ParentId, IReadOnlyList<Guid> Pinned)? Take(string id) => LocalDb.Use<(NodeBase, Guid?, IReadOnlyList<Guid>)?>((c, key) =>
    {
        using var t = c.BeginTransaction();
        using var cmd = LocalDb.Command(c, "SELECT parent_id, data FROM deleted_nodes WHERE id = $id", t, ("$id", id));
        string? parent;
        byte[] data;
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            parent = r.IsDBNull(0) ? null : r.GetString(0);
            data = (byte[])r.GetValue(1);
        }
        var payload = JsonSerializer.Deserialize<Payload>(SecretBox.Open(key, data, Place(id)));
        var app = payload is null ? null : SettingsStore.FromJson(Encoding.UTF8.GetBytes(payload.Data));
        if (app?.Items.FirstOrDefault() is not { } node) return null;
        LocalDb.Exec(c, "DELETE FROM deleted_nodes WHERE id = $id", t, ("$id", id));
        t.Commit();
        return (node, Guid.TryParse(parent, out var p) ? p : null, payload!.Pinned);
    });

    /// <summary>Removes an item for good (with secure_delete, the sealed record is overwritten).</summary>
    public static void DeleteNow(string id) => LocalDb.Use((c, _) => LocalDb.Exec(c, "DELETE FROM deleted_nodes WHERE id = $id", null, ("$id", id)));

    /// <summary>Removes what was deleted more than <see cref="KeepDays"/> days ago. Returns how many items went.</summary>
    public static int Purge(DateTime? nowUtc = null)
    {
        var cutoff = new DateTimeOffset(nowUtc ?? DateTime.UtcNow).ToUnixTimeMilliseconds() - (long)TimeSpan.FromDays(KeepDays).TotalMilliseconds;
        return LocalDb.Use((c, _) =>
        {
            using var cmd = LocalDb.Command(c, "DELETE FROM deleted_nodes WHERE deleted_utc < $cut", null, ("$cut", cutoff));
            return cmd.ExecuteNonQuery();
        });
    }
}
