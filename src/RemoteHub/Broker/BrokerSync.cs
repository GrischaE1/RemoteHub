using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using RemoteHub.Models;
using RemoteHub.Services;

namespace RemoteHub.Broker;

/// <summary>
/// Keeps the synced folder of each broker organisation in step with its shared list, on the UI
/// thread. Only sealed data moves anything: folders and placements need the layout seal (only consoles can make it),
/// device records the record seal – and a device's certificate pin is taken from a sealed record only, never from what
/// the broker says otherwise. The nodes are ordinary folders and connections, so credentials, RDP options and notes on
/// them are this console's own and saved like any other item.
/// </summary>
public sealed class BrokerSync(ObservableCollection<NodeBase> top, Action changed)
{
    public const string NewDevicesName = "New devices";

    /// <summary>The organisation's folder at the top of the navigation, made on first use (with its New devices folder).</summary>
    public Folder Root(BrokerConnection b)
    {
        var root = top.OfType<Folder>().FirstOrDefault(f => f.Broker is { Kind: BrokerItemKind.Root } r && r.Connection == b.Id);
        if (root is null)
        {
            root = new Folder { Name = RootName(b), Icon = "CloudLink24", Broker = new BrokerRef { Connection = b.Id, Kind = BrokerItemKind.Root }, IsExpanded = true };
            top.Add(root);
            changed();
        }
        if (root.Children.OfType<Folder>().All(f => f.Broker?.Kind != BrokerItemKind.NewDevices))
        {
            var fresh = new Folder { Name = NewDevicesName, Icon = "FolderAdd24", Broker = new BrokerRef { Connection = b.Id, Kind = BrokerItemKind.NewDevices }, Parent = root };
            root.Children.Insert(0, fresh);
            changed();
        }
        return root;
    }

    public static string RootName(BrokerConnection b) => $"{(b.OrgName.Length > 0 ? b.OrgName : b.Org)} – RemoteHub Broker";

    /// <summary>The organisation's folder disappears from this PC (Disconnect).</summary>
    public void Remove(BrokerConnection b)
    {
        foreach (var root in top.OfType<Folder>().Where(f => f.Broker is { Kind: BrokerItemKind.Root } r && r.Connection == b.Id).ToList())
            top.Remove(root);
        changed();
    }

    public void SetConnected(BrokerConnection b, bool connected)
    {
        var root = Root(b);
        foreach (var n in root.Descendants().Prepend(root))
        {
            if (n.Broker is not { } r || r.Connected == connected) continue;
            r.Connected = connected;
            n.RefreshRoute();
        }
    }

    public void Presence(BrokerConnection b, string deviceId, bool online)
    {
        if (Device(Root(b), deviceId) is not { Broker: { } r } node) return;
        r.Online = online;
        node.RefreshRoute();
    }

    /// <summary>
    /// Every device's online state at once (a <c>presenceList</c> snapshot): one pass over the organisation's folder, and
    /// only the devices whose state changed are told.
    /// </summary>
    public void PresenceAll(BrokerConnection b, IReadOnlyList<(string DeviceId, bool Online)> states)
    {
        var devices = Root(b).Descendants().OfType<Connection>()
            .Where(c => c.Broker is { Kind: BrokerItemKind.Device })
            .GroupBy(c => c.Broker!.Item).ToDictionary(g => g.Key, g => g.First());
        foreach (var (id, online) in states)
        {
            if (!devices.TryGetValue(id, out var node) || node.Broker!.Online == online) continue;
            node.Broker.Online = online;
            node.RefreshRoute();
        }
    }

    /// <summary>Applies changed items: folders first (so places exist), then devices, then placements.</summary>
    public void Apply(BrokerClient client, IReadOnlyList<JsonObject> items)
    {
        var b = client.Info;
        var root = Root(b);
        var dirty = false;
        foreach (var i in items.Where(i => Kind(i) == "folder")) dirty |= ApplyFolder(client, root, i);
        // Folders may arrive before their parent folder: place them once all are known.
        foreach (var i in items.Where(i => Kind(i) == "folder")) dirty |= PlaceFolder(root, i);
        foreach (var i in items.Where(i => Kind(i) == "device")) dirty |= ApplyDevice(client, root, i);
        foreach (var i in items.Where(i => Kind(i) == "place")) dirty |= ApplyPlacement(client, root, i);
        if (dirty) changed();
    }

    private static string Kind(JsonObject i) => i["kind"]?.GetValue<string>() ?? "";

    // ---- Folders ---------------------------------------------------------------------------------------------------

    private static bool ApplyFolder(BrokerClient client, Folder root, JsonObject item)
    {
        var body = item["body"] as JsonObject;
        if (!client.LayoutIsSealed("folder", body, item["seal"]?.GetValue<string>()))
        {
            DiagLog.Write("Broker: a folder without a valid seal was ignored");
            return false;
        }
        var id = Text(body, "id");
        var v = Number(body, "v");
        var folder = Folder(root, id);
        if (folder?.Broker is { } known && known.Version >= v) return false;
        if (Flag(body, "deleted"))
        {
            if (folder is null) return false;
            // Its contents stay: they move up to where the folder was.
            var into = folder.Parent as Folder ?? root;
            foreach (var child in folder.Children.ToList()) Move(child, into);
            Detach(folder);
            return true;
        }
        if (folder is null)
        {
            folder = new Folder { Broker = new BrokerRef { Connection = root.Broker!.Connection, Kind = BrokerItemKind.Folder, Item = id } };
            Attach(folder, root);
        }
        folder.Name = Text(body, "name");
        folder.Broker!.Version = v;
        return true;
    }

    private static bool PlaceFolder(Folder root, JsonObject item)
    {
        if (item["body"] is not JsonObject body || Folder(root, Text(body, "id")) is not { } folder) return false;
        var parent = body["parent"] is JsonValue p && p.TryGetValue<string>(out var pid) ? Folder(root, pid) : null;
        // A parent that is the folder itself or inside it would make a loop: leave it where it is.
        if (parent is not null && (parent == folder || parent.Ancestors().Contains(folder))) return false;
        var target = parent ?? root;
        if (folder.Parent == target) return false;
        Move(folder, target);
        return true;
    }

    // ---- Devices ---------------------------------------------------------------------------------------------------

    private static bool ApplyDevice(BrokerClient client, Folder root, JsonObject item)
    {
        var id = Text(item, "id");
        var node = Device(root, id);
        if (Flag(item, "removed"))
        {
            if (node is null) return false;
            Detach(node);
            return true;
        }
        var record = item["record"] as JsonObject;
        var isSealed = client.RecordIsSealed(record, item["seal"]?.GetValue<string>());
        var computer = Text(record, "computerName");
        var domain = Text(record, "domain");
        if (node is null)
        {
            node = new Connection
            {
                Name = computer,
                Broker = new BrokerRef { Connection = root.Broker!.Connection, Kind = BrokerItemKind.Device, Item = id },
                Agent = new AgentDevice { DeviceId = id, Status = AgentStatus.Installed, CreatedUtc = DateTime.UtcNow },
            };
            Attach(node, NewDevices(root));
        }
        var r = node.Broker!;
        r.Sealed = isSealed;
        r.SecretId = item["secretId"] is JsonValue s && s.TryGetValue<string>(out var sid) ? sid : null;
        r.Online = item["online"]?.GetValue<bool>();
        // The pin and the address come from a sealed record only; an unsealed one leaves no pin, so nothing can connect to it.
        node.Agent ??= new AgentDevice { DeviceId = id, Status = AgentStatus.Installed };
        node.Agent.Fingerprint = isSealed ? Text(record, "fingerprint") : "";
        node.Agent.Label = computer;
        node.Agent.Version = item["agentVersion"] is JsonValue av && av.TryGetValue<string>(out var ver) ? ver : node.Agent.Version;
        if (isSealed) node.Host = domain.Length > 0 ? $"{computer}.{domain}" : computer;
        r.Connected = client.IsConnected || r.Connected;
        node.RefreshRoute();
        return true;
    }

    private static bool ApplyPlacement(BrokerClient client, Folder root, JsonObject item)
    {
        var body = item["body"] as JsonObject;
        if (body is null || !client.LayoutIsSealed("place", body, item["seal"]?.GetValue<string>()))
        {
            DiagLog.Write("Broker: a placement without a valid seal was ignored");
            return false;
        }
        if (Device(root, Text(body, "id")) is not { } node) return false;
        var v = Number(body, "v");
        if (node.Broker!.Version >= v) return false;
        node.Broker.Version = v;
        var folder = body["folder"] is JsonValue f && f.TryGetValue<string>(out var fid) ? Folder(root, fid) : null;
        if (body["label"] is JsonValue l && l.TryGetValue<string>(out var label) && label.Length > 0) node.Name = label;
        var target = folder ?? (Folder)root;
        if (node.Parent != target) Move(node, target);
        return true;
    }

    // ---- Tree helpers ----------------------------------------------------------------------------------------------

    private static Folder NewDevices(Folder root) => root.Children.OfType<Folder>().First(f => f.Broker?.Kind == BrokerItemKind.NewDevices);

    public static Folder? Folder(Folder root, string id) =>
        root.Descendants().OfType<Folder>().FirstOrDefault(f => f.Broker is { Kind: BrokerItemKind.Folder } r && r.Item == id);

    public static Connection? Device(Folder root, string id) =>
        root.Descendants().OfType<Connection>().FirstOrDefault(c => c.Broker is { Kind: BrokerItemKind.Device } r && r.Item == id);

    private static void Detach(NodeBase node)
    {
        node.Parent?.Contents?.Remove(node);
        node.Parent = null;
    }

    private static void Attach(NodeBase node, Folder into)
    {
        into.Children.Add(node);
        node.Parent = into;
    }

    private static void Move(NodeBase node, Folder into)
    {
        Detach(node);
        Attach(node, into);
    }

    private static string Text(JsonObject? o, string name) => o?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static long Number(JsonObject? o, string name) => o?[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
    private static bool Flag(JsonObject? o, string name) => o?[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
