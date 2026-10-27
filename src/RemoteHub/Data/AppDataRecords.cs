using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using RemoteHub.Models;
using RemoteHub.Services;

namespace RemoteHub.Data;

/// <summary>
/// One stored record: a folder or connection (table nodes), a saved credential (credentials) or one part of the global
/// settings (settings). <see cref="Data"/> is the record's JSON without its passwords – those are in
/// <see cref="Secrets"/> (still encrypted in memory, see <see cref="Secret"/>) and stored encrypted a second time
/// (table secrets).
/// </summary>
internal sealed class Record
{
    public required string Table { get; init; }
    public required string Id { get; init; }
    public string? ParentId { get; init; }
    public int Sort { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public string? Host { get; init; }
    public required string Data { get; init; }
    public required IReadOnlyList<(string Path, Secret Value)> Secrets { get; init; }

    private byte[]? _hash;

    /// <summary>Everything stored for the record; an unchanged hash means nothing needs to be written.</summary>
    public byte[] Hash => _hash ??= ComputeHash();

    private byte[] ComputeHash()
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string? s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "\0null");
            h.AppendData(BitConverter.GetBytes(b.Length));
            h.AppendData(b);
        }
        Add(Table); Add(Id); Add(ParentId); Add(Sort.ToString()); Add(Data);
        // The fingerprint stands for the value: equal for equal passwords in this process, never the password itself.
        foreach (var (path, value) in Secrets) { Add(path); h.AppendData(value.Fingerprint); }
        return h.GetHashAndReset();
    }
}

/// <summary>What a save changes, compared with what is stored.</summary>
internal sealed record Changes(IReadOnlyList<(Record New, Record? Old)> Upserts, IReadOnlyList<Record> Deletes)
{
    public bool IsEmpty => Upserts.Count == 0 && Deletes.Count == 0;
}

/// <summary>The records as they are stored – so a save writes only what changed.</summary>
internal sealed class ChangeTracker
{
    private Dictionary<(string, string), Record> _known = new();

    public void Reset(IEnumerable<Record> stored) => _known = stored.ToDictionary(r => (r.Table, r.Id));

    public Changes Diff(IReadOnlyList<Record> current)
    {
        var upserts = new List<(Record, Record?)>();
        var present = new HashSet<(string, string)>();
        foreach (var r in current)
        {
            present.Add((r.Table, r.Id));
            if (!_known.TryGetValue((r.Table, r.Id), out var old)) upserts.Add((r, null));
            else if (!old.Hash.AsSpan().SequenceEqual(r.Hash)) upserts.Add((r, old));
        }
        var deletes = _known.Values.Where(k => !present.Contains((k.Table, k.Id))).ToList();
        return new Changes(upserts, deletes);
    }

    public void Accept(Changes c)
    {
        foreach (var (n, _) in c.Upserts) _known[(n.Table, n.Id)] = n;
        foreach (var d in c.Deletes) _known.Remove((d.Table, d.Id));
    }
}

/// <summary>Converts <see cref="AppData"/> to records and back, and reads and writes them.</summary>
internal static class AppDataRecords
{
    public const string Nodes = "nodes", Credentials = "credentials", Settings = "settings";

    /// <summary>Like <see cref="SettingsStore.Json"/>, but a folder's children aren't part of its record (each is its own).</summary>
    private static readonly JsonSerializerOptions RowJson = new(SettingsStore.Json)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                ti =>
                {
                    if (ti.Type != typeof(Folder)) return;
                    if (ti.Properties.FirstOrDefault(p => p.Name == nameof(Folder.Children)) is { } children) ti.Properties.Remove(children);
                },
            },
        },
    };

    /// <summary>The global settings stored as records (one row each), with their types.</summary>
    private static readonly (string Key, Type Type, Func<AppData, object?> Get, Action<AppData, object?> Set)[] SettingParts =
    [
        (nameof(AppData.Version), typeof(int), d => d.Version, (d, v) => d.Version = (int)v!),
        (nameof(AppData.Defaults), typeof(RdpOptions), d => d.Defaults, (d, v) => d.Defaults = (RdpOptions)v!),
        (nameof(AppData.DefaultCredentials), typeof(CredentialSet), d => d.DefaultCredentials, (d, v) => d.DefaultCredentials = (CredentialSet)v!),
        (nameof(AppData.DefaultCredentialId), typeof(Guid?), d => d.DefaultCredentialId, (d, v) => d.DefaultCredentialId = (Guid?)v),
        (nameof(AppData.Tools), typeof(RemoteToolSettings), d => d.Tools, (d, v) => d.Tools = (RemoteToolSettings)v!),
        (nameof(AppData.AgentEnrollment), typeof(AgentEnrollmentKeys), d => d.AgentEnrollment, (d, v) => d.AgentEnrollment = (AgentEnrollmentKeys)v!),
    ];

    // ------------------------------------------------------------------ AppData -> records

    /// <summary>
    /// All records of <paramref name="data"/>. An id that appears twice (e.g. a backup imported next to the original)
    /// gets a new id on its second appearance – ids are the records' keys.
    /// </summary>
    public static List<Record> From(AppData data)
    {
        // First the cheap pass (order, parents, unique ids) on the calling thread, then the JSON in parallel: the
        // caller (the UI thread) waits, so nothing changes the objects while they are read.
        var parts = new List<(string Table, string Id, string? Parent, int Sort, string Kind, string Name, string? Host, object? Value, Type Type)>();
        foreach (var (key, type, get, _) in SettingParts)
            parts.Add((Settings, key, null, 0, "setting", key, null, get(data), type));

        var seenCredentials = new HashSet<Guid>();
        for (var i = 0; i < data.SavedCredentials.Count; i++)
        {
            var sc = data.SavedCredentials[i];
            if (!seenCredentials.Add(sc.Id)) seenCredentials.Add(sc.Id = Guid.NewGuid());
            parts.Add((Credentials, sc.Id.ToString(), null, i, "credential", sc.Name, null, sc, typeof(SavedCredential)));
        }

        var seenNodes = new HashSet<Guid>();
        void Walk(IList<NodeBase> live, Guid? parent)
        {
            var items = TreeLayout.Flatten(live); // items under a jump host are saved next to it (older versions keep them)
            for (var i = 0; i < items.Count; i++)
            {
                var n = items[i];
                if (!seenNodes.Add(n.Id)) seenNodes.Add(n.Id = Guid.NewGuid());
                parts.Add((Nodes, n.Id.ToString(), parent?.ToString(), i, n switch { Folder => "folder", NetworkShare => "share", _ => "connection" }, n.Name,
                    n switch { Connection c => c.Host, NetworkShare s => s.Host, _ => null }, n, typeof(NodeBase)));
                if (n is Folder f) Walk(f.Children, f.Id);
            }
        }
        Walk(data.Items, null);

        var records = new Record[parts.Count];
        Parallel.For(0, parts.Count, i =>
        {
            var p = parts[i];
            var r = Make(p.Table, p.Id, p.Parent, p.Sort, p.Kind, p.Name, p.Host, p.Value, p.Type);
            _ = r.Hash; // hashed here too, in parallel
            records[i] = r;
        });
        return [.. records];
    }

    private static Record Make(string table, string id, string? parent, int sort, string kind, string name, string? host, object? value, Type type)
    {
        // Passwords are written as placeholders (see SecretJsonConverter), so their values never enter the JSON text.
        var captured = new List<Secret>();
        string json;
        SecretJsonConverter.Capture = captured;
        try { json = JsonSerializer.Serialize(value, type, RowJson); }
        finally { SecretJsonConverter.Capture = null; }
        var secrets = new List<(string, Secret)>();
        // Fast path: most records carry no password (credentials are inherited or saved separately) and no enrollment secret.
        if ((captured.Count > 0 || json.Contains("\"Secret\":", StringComparison.Ordinal)) && JsonNode.Parse(json) is { } node)
        {
            Extract(node, "", captured, secrets);
            json = node.ToJsonString();
        }
        return new Record { Table = table, Id = id, ParentId = parent, Sort = sort, Kind = kind, Name = name, Host = host, Data = json, Secrets = secrets };
    }

    /// <summary>
    /// Moves every "Password", "Secret" and every other <see cref="Secret"/> (a private key and its passphrase) out of the
    /// JSON, remembering where it was ("Credentials/Password", "Credentials/PrivateKey", "Keys/0/Secret"): a placeholder
    /// becomes the captured <see cref="Secret"/>; the enrollment key, a plain string in the model, is wrapped in one.
    /// </summary>
    private static void Extract(JsonNode? node, string path, List<Secret> captured, List<(string, Secret)> secrets)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o.ToList())
                {
                    var p = path.Length == 0 ? key : path + "/" + key;
                    if (value is JsonValue v && v.TryGetValue<string>(out var s) && (key is "Password" or "Secret" || SecretJsonConverter.TryParsePlaceholder(s, out _)))
                    {
                        var secret = SecretJsonConverter.TryParsePlaceholder(s, out var i) && i >= 0 && i < captured.Count ? captured[i] : Secret.From(s);
                        if (secret is not null) secrets.Add((p, secret));
                        o.Remove(key);
                    }
                    else Extract(value, p, captured, secrets);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) Extract(a[i], path + "/" + i, captured, secrets);
                break;
        }
    }

    private static void Insert(JsonNode root, string path, string value)
    {
        var parts = path.Split('/');
        var current = root;
        foreach (var part in parts[..^1])
            current = current is JsonArray a && int.TryParse(part, out var i) ? a[i]! : (current[part] ??= new JsonObject());
        current[parts[^1]] = value;
    }

    // ------------------------------------------------------------------ read

    public static AppData Load(SqliteConnection c, byte[] secretKey)
    {
        // Each password goes from its sealed row straight into a Secret; the JSON only gets a placeholder for it.
        var secrets = new Dictionary<(string, string), List<(string, Secret)>>();
        using (var cmd = LocalDb.Command(c, "SELECT owner_kind, owner_id, path, value FROM secrets", null))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var (kind, id, path) = (r.GetString(0), r.GetString(1), r.GetString(2));
                if (SecretBox.OpenSecret(secretKey, (byte[])r.GetValue(3), Place(kind, id, path)) is { } value)
                    (secrets.TryGetValue((kind, id), out var l) ? l : secrets[(kind, id)] = []).Add((path, value));
            }

        var supply = new List<Secret>();
        string WithSecrets(string table, string id, string json)
        {
            if (!secrets.TryGetValue((table, id), out var list)) return json;
            var node = JsonNode.Parse(json)!;
            foreach (var (path, value) in list)
            {
                // The enrollment key is a string in the model; a password is a Secret, filled in from its placeholder.
                if (path == "Secret" || path.EndsWith("/Secret", StringComparison.Ordinal)) Insert(node, path, value.Reveal());
                else
                {
                    supply.Add(value);
                    Insert(node, path, SecretJsonConverter.Placeholder(supply.Count - 1));
                }
            }
            return node.ToJsonString();
        }

        object? Read(string table, string id, string json, Type type)
        {
            SecretJsonConverter.Supply = supply;
            try { return JsonSerializer.Deserialize(WithSecrets(table, id, json), type, RowJson); }
            finally { SecretJsonConverter.Supply = null; }
        }

        var data = new AppData();
        foreach (var (id, _, json) in Rows(c, Settings))
            if (SettingParts.FirstOrDefault(p => p.Key == id) is { Key: not null } part)
                part.Set(data, Read(Settings, id, json, part.Type));

        foreach (var (id, _, json) in Rows(c, Credentials))
            if (Read(Credentials, id, json, typeof(SavedCredential)) is SavedCredential sc) data.SavedCredentials.Add(sc);

        // The tree: each node under its parent, in stored order. A node whose parent is missing goes to the top level.
        var nodes = new List<(NodeBase Node, string? Parent)>();
        foreach (var (id, parent, json) in Rows(c, Nodes))
            if (Read(Nodes, id, json, typeof(NodeBase)) is NodeBase n) nodes.Add((n, parent));
        var folders = nodes.Select(x => x.Node).OfType<Folder>().ToDictionary(f => f.Id.ToString());
        var parents = nodes.ToDictionary(x => x.Node.Id.ToString(), x => x.Parent);
        foreach (var (n, parent) in nodes)
        {
            if (parent is not null && folders.TryGetValue(parent, out var f) && !InLoop(n.Id.ToString(), parents)) f.Children.Add(n);
            else data.Items.Add(n);
        }
        SettingsStore.Normalize(data);
        return data;
    }

    /// <summary>True if following the parents from <paramref name="id"/> comes back to it (only in damaged data).</summary>
    private static bool InLoop(string id, Dictionary<string, string?> parents)
    {
        var seen = new HashSet<string> { id };
        for (var p = parents.GetValueOrDefault(id); p is not null; p = parents.GetValueOrDefault(p))
            if (!seen.Add(p)) return p == id; // a loop further up doesn't include this node
        return false;
    }

    private static List<(string Id, string? Parent, string Data)> Rows(SqliteConnection c, string table)
    {
        var list = new List<(string, string?, string)>();
        using var cmd = LocalDb.Command(c, $"SELECT id, parent_id, data FROM {table} ORDER BY parent_id, sort", null);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2)));
        return list;
    }

    private static string Place(string table, string id, string path) => $"{table}|{id}|{path}";

    public static bool IsEmpty(SqliteConnection c) =>
        Convert.ToInt64(LocalDb.Scalar(c, "SELECT (SELECT count(*) FROM nodes) + (SELECT count(*) FROM credentials) + (SELECT count(*) FROM settings)")) == 0;

    // ------------------------------------------------------------------ write

    /// <summary>Writes the changes in one transaction.</summary>
    public static void Write(SqliteConnection c, byte[] secretKey, Changes changes)
    {
        if (changes.IsEmpty) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var t = c.BeginTransaction();
        foreach (var (r, _) in changes.Upserts)
        {
            LocalDb.Exec(c, $"""
                INSERT INTO {r.Table} (id, parent_id, sort, kind, name, host, data, version, modified_utc, dirty)
                VALUES ($id, $parent, $sort, $kind, $name, $host, $data, 1, $now, 1)
                ON CONFLICT(id) DO UPDATE SET parent_id = excluded.parent_id, sort = excluded.sort, kind = excluded.kind,
                    name = excluded.name, host = excluded.host, data = excluded.data, version = {r.Table}.version + 1,
                    modified_utc = excluded.modified_utc, dirty = 1
                """, t, ("$id", r.Id), ("$parent", r.ParentId), ("$sort", r.Sort), ("$kind", r.Kind), ("$name", r.Name),
                ("$host", r.Host), ("$data", r.Data), ("$now", now));
            LocalDb.Exec(c, "DELETE FROM secrets WHERE owner_kind = $t AND owner_id = $id", t, ("$t", r.Table), ("$id", r.Id));
            foreach (var (path, value) in r.Secrets)
                LocalDb.Exec(c, "INSERT INTO secrets (owner_kind, owner_id, path, value) VALUES ($t, $id, $p, $v)", t,
                    ("$t", r.Table), ("$id", r.Id), ("$p", path), ("$v", SecretBox.Seal(secretKey, value, Place(r.Table, r.Id, path))));
        }
        foreach (var d in changes.Deletes)
        {
            LocalDb.Exec(c, $"DELETE FROM {d.Table} WHERE id = $id", t, ("$id", d.Id));
            LocalDb.Exec(c, "DELETE FROM secrets WHERE owner_kind = $t AND owner_id = $id", t, ("$t", d.Table), ("$id", d.Id));
        }
        t.Commit();
    }

    // ------------------------------------------------------------------ audit of changes

    /// <summary>Fields whose change isn't worth an audit entry (they change by just using RemoteHub).</summary>
    private static readonly string[] Volatile = ["LastConnectedUtc", "PsRemoting", "IsExpanded"];

    /// <summary>Above this many changed records in one save, the audit log gets one summary instead of one entry each.</summary>
    private const int AuditDetailLimit = 50;

    /// <summary>Audit entries for a save: added, changed (which fields – never values) and deleted records.</summary>
    public static void Audit(Changes changes)
    {
        var entries = new List<(string Event, string? Connection, string Target, string Detail)>();
        foreach (var (n, old) in changes.Upserts)
        {
            if (n.Table == Settings && n.Id == nameof(AppData.Version)) continue;
            if (old is null) entries.Add((AuditEvents.DataAdded, NodeId(n), n.Name, KindLabel(n)));
            else if (ChangedFields(old, n) is { Count: > 0 } fields)
                entries.Add((AuditEvents.DataChanged, NodeId(n), n.Name, $"{KindLabel(n)}: {string.Join(", ", fields)}"));
        }
        foreach (var d in changes.Deletes)
            if (!(d.Table == Settings && d.Id == nameof(AppData.Version))) entries.Add((AuditEvents.DataDeleted, NodeId(d), d.Name, KindLabel(d)));

        if (entries.Count > AuditDetailLimit)
        {
            string Count(string e) => $"{entries.Count(x => x.Event == e)}";
            AuditLog.Write(AuditEvents.DataChanged, null, $"{entries.Count} items",
                $"{Count(AuditEvents.DataAdded)} added, {Count(AuditEvents.DataChanged)} changed, {Count(AuditEvents.DataDeleted)} deleted");
            return;
        }
        foreach (var (e, conn, target, detail) in entries) AuditLog.Write(e, conn, target, detail);
    }

    private static string? NodeId(Record r) => r.Table == Nodes ? r.Id : null;

    private static string KindLabel(Record r) => r.Kind switch
    {
        "folder" => "Folder",
        "connection" => "Connection",
        "share" => "Network share",
        "credential" => "Saved credential",
        _ => r.Id switch
        {
            nameof(AppData.Defaults) => "Default settings",
            nameof(AppData.DefaultCredentials) => "Default credentials",
            nameof(AppData.DefaultCredentialId) => "Default saved credential",
            nameof(AppData.Tools) => "Remote tool settings",
            nameof(AppData.AgentEnrollment) => "RemoteHub Agent enrollment",
            _ => r.Id,
        },
    };

    /// <summary>Names of the changed fields ("Host", "Options.ColorTag", "Credentials.Password", "Folder").</summary>
    internal static List<string> ChangedFields(Record old, Record now)
    {
        var fields = new List<string>();
        if (old.ParentId != now.ParentId) fields.Add("Folder");
        var a = JsonNode.Parse(old.Data);
        var b = JsonNode.Parse(now.Data);
        if (a is JsonObject oa && b is JsonObject ob)
        {
            foreach (var key in oa.Select(p => p.Key).Union(ob.Select(p => p.Key)))
            {
                if (Volatile.Contains(key)) continue;
                var (va, vb) = (oa[key], ob[key]);
                if (JsonNode.DeepEquals(va, vb)) continue;
                if (va is JsonObject sa && vb is JsonObject sb)
                {
                    foreach (var sub in sa.Select(p => p.Key).Union(sb.Select(p => p.Key)))
                        if (!JsonNode.DeepEquals(sa[sub], sb[sub]) && !(key == "Agent" && sub == "Version")) fields.Add($"{key}.{sub}");
                }
                else fields.Add(key);
            }
        }
        else if (!JsonNode.DeepEquals(a, b)) fields.Add("Value");
        var sa2 = old.Secrets.ToDictionary(s => s.Path, s => s.Value);
        var sb2 = now.Secrets.ToDictionary(s => s.Path, s => s.Value);
        foreach (var path in sa2.Keys.Union(sb2.Keys))
            if (!Secret.AreEqual(sa2.GetValueOrDefault(path), sb2.GetValueOrDefault(path))) fields.Add(path.Replace('/', '.'));
        return fields;
    }
}
