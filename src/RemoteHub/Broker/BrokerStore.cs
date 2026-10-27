using RemoteHub.Data;

namespace RemoteHub.Broker;

/// <summary>One RemoteHub Broker organisation this PC is connected to.</summary>
public sealed record BrokerConnection(string Id, string Url, string Org, string OrgName, string ConsoleId, string ConsoleName, DateTime AddedUtc, long Cursor);

/// <summary>
/// This PC's broker connections in the local database (schema 4): the console secret sealed with
/// the secret key, the sync cursor, and the last sealed state of the shared list. Never in the shared settings file.
/// </summary>
internal static class BrokerStore
{
    private static string SecretPlace(string id) => $"broker|{id}|secret";

    public static List<BrokerConnection> All() => LocalDb.Use((c, _) =>
    {
        using var cmd = LocalDb.Command(c, "SELECT id, url, org, org_name, console_id, console_name, added_utc, cursor FROM broker_connections ORDER BY added_utc", null);
        using var r = cmd.ExecuteReader();
        var list = new List<BrokerConnection>();
        while (r.Read())
            list.Add(new BrokerConnection(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                DateTime.UnixEpoch.AddSeconds(r.GetInt64(6)), r.GetInt64(7)));
        return list;
    });

    public static BrokerConnection Add(string url, string org, string orgName, string consoleId, string consoleName, string consoleSecret)
    {
        var id = Guid.NewGuid().ToString("N");
        var added = DateTime.UtcNow;
        LocalDb.Use((c, key) => LocalDb.Exec(c,
            "INSERT INTO broker_connections (id, url, org, org_name, console_id, console_name, secret, added_utc) VALUES ($id, $url, $org, $name, $console, $cname, $secret, $added)", null,
            ("$id", id), ("$url", url), ("$org", org), ("$name", orgName), ("$console", consoleId), ("$cname", consoleName),
            ("$secret", SecretBox.Seal(key, consoleSecret, SecretPlace(id))), ("$added", new DateTimeOffset(added).ToUnixTimeSeconds())));
        return new BrokerConnection(id, url, org, orgName, consoleId, consoleName, added, 0);
    }

    /// <summary>The console secret (rhc1.…) – opened only to derive the keys, never kept in a field.</summary>
    public static string Secret(string id) => LocalDb.Use((c, key) =>
        LocalDb.Scalar(c, "SELECT secret FROM broker_connections WHERE id = $id", null, ("$id", id)) is byte[] sealedSecret
            ? SecretBox.Open(key, sealedSecret, SecretPlace(id))
            : throw new InvalidOperationException("This broker connection was removed."));

    public static void SetCursor(string id, long cursor) =>
        LocalDb.Use((c, _) => LocalDb.Exec(c, "UPDATE broker_connections SET cursor = $c WHERE id = $id", null, ("$c", cursor), ("$id", id)));

    public static void SetOrgName(string id, string name) =>
        LocalDb.Use((c, _) => LocalDb.Exec(c, "UPDATE broker_connections SET org_name = $n WHERE id = $id", null, ("$n", name), ("$id", id)));

    /// <summary>Removes the connection, its cached list and this PC's key for it.</summary>
    public static void Remove(BrokerConnection b)
    {
        LocalDb.Use((c, _) =>
        {
            using var t = c.BeginTransaction();
            LocalDb.Exec(c, "DELETE FROM broker_items WHERE connection_id = $id", t, ("$id", b.Id));
            LocalDb.Exec(c, "DELETE FROM broker_connections WHERE id = $id", t, ("$id", b.Id));
            t.Commit();
        });
        BrokerKey.Delete(b.ConsoleId);
    }

    public static void PutItems(string id, IEnumerable<(string Kind, string ItemId, string Json)> items, long cursor) => LocalDb.Use((c, _) =>
    {
        using var t = c.BeginTransaction();
        foreach (var (kind, itemId, json) in items)
            LocalDb.Exec(c, "INSERT INTO broker_items (connection_id, kind, id, data) VALUES ($c, $k, $i, $d) ON CONFLICT (connection_id, kind, id) DO UPDATE SET data = excluded.data", t,
                ("$c", id), ("$k", kind), ("$i", itemId), ("$d", json));
        LocalDb.Exec(c, "UPDATE broker_connections SET cursor = $cur WHERE id = $id", t, ("$cur", cursor), ("$id", id));
        t.Commit();
    });

    public static List<(string Kind, string ItemId, string Json)> Items(string id) => LocalDb.Use((c, _) =>
    {
        using var cmd = LocalDb.Command(c, "SELECT kind, id, data FROM broker_items WHERE connection_id = $c", null, ("$c", id));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, string)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        return list;
    });

    /// <summary>For tests: the cached list starts over (and the cursor with it).</summary>
    internal static void Forget(string id) => LocalDb.Use((c, _) =>
    {
        LocalDb.Exec(c, "DELETE FROM broker_items WHERE connection_id = $c", null, ("$c", id));
        LocalDb.Exec(c, "UPDATE broker_connections SET cursor = 0 WHERE id = $c", null, ("$c", id));
    });
}
