using RemoteHub.Models;

namespace RemoteHub.Services;

/// <summary>Resolves effective settings by walking node → parent folders → global defaults.</summary>
public static class Inheritance
{
    /// <summary>
    /// Tools go through the RemoteHub Agent instead of WinRM. Below a jump host a device with an agent uses it unless the
    /// connection itself is set to WinRM: an inherited choice was made for devices reached directly.
    /// </summary>
    public static bool UsesAgent(Connection c, AppData data) =>
        (c.JumpHost is not null && c.Options.UseAgent is null && c.Agent is { Fingerprint.Length: > 0 }) || ResolveOptions(c, data).UseAgent == true;

    public static RdpOptions ResolveOptions(NodeBase? node, AppData data, bool includeSelf = true)
    {
        var result = new RdpOptions();
        if (node is not null)
        {
            if (includeSelf) result.FillFrom(node.Options);
            foreach (var a in node.Ancestors()) result.FillFrom(a.Options);
        }
        return result.FillFrom(data.Defaults).FillFrom(RdpOptions.BuiltInDefaults());
    }

    /// <summary>Returns the effective credentials and where they came from (for display).</summary>
    public static (CredentialSet Credentials, string Source) ResolveCredentials(NodeBase? node, AppData data, bool includeSelf = true)
    {
        if (node is not null)
        {
            if (includeSelf && !node.InheritCredentials) return Own(node.CredentialId, node.Credentials, data, "this item");
            foreach (var a in node.Ancestors())
                if (!a.InheritCredentials) return Own(a.CredentialId, a.Credentials, data, $"folder \"{a.Name}\"");
        }
        var (cred, src) = Own(data.DefaultCredentialId, data.DefaultCredentials, data, "global defaults");
        return cred.IsEmpty ? (new CredentialSet(), "none – Windows will prompt") : (cred, src);
    }

    /// <summary>
    /// The credentials <paramref name="node"/> would have inside <paramref name="parent"/> (a folder or jump host; null = top
    /// level) – e.g. before moving it there. Like <see cref="NodeBase.Ancestors"/>, a jump host itself passes nothing on.
    /// </summary>
    public static (CredentialSet Credentials, string Source) ResolveCredentialsIn(NodeBase node, NodeBase? parent, AppData data)
    {
        if (!node.InheritCredentials) return Own(node.CredentialId, node.Credentials, data, "this item");
        for (var p = parent; p is not null; p = p.Parent)
            if (p is Folder f && !f.InheritCredentials) return Own(f.CredentialId, f.Credentials, data, $"folder \"{f.Name}\"");
        var (cred, src) = Own(data.DefaultCredentialId, data.DefaultCredentials, data, "global defaults");
        return cred.IsEmpty ? (new CredentialSet(), "none – Windows will prompt") : (cred, src);
    }

    /// <summary>Where the item is: "LAB › Servers › NUC13", or "Top level › NUC13".</summary>
    public static string Location(NodeBase node)
    {
        var path = TreeLayout.Containers(node).Reverse().Select(a => a.Name).ToList(); // folders and the jump host
        return (path.Count == 0 ? "Top level" : string.Join(" › ", path)) + " › " + node.Name;
    }

    private static (CredentialSet, string) Own(Guid? savedId, CredentialSet inline, AppData data, string where)
    {
        if (savedId is { } id)
        {
            var saved = FindSaved(data, id);
            return saved is null
                ? (new CredentialSet(), $"{where} – saved credential was deleted, Windows will prompt")
                : (saved.ToSet(), $"{where} (saved credential \"{saved.Name}\")");
        }
        return (inline, where);
    }

    public static SavedCredential? FindSaved(AppData data, Guid id) => data.SavedCredentials.FirstOrDefault(c => c.Id == id);

    /// <summary>A network share's account: its own, or its server's – for a share on its own, its folders' (inherited as usual).</summary>
    public static (CredentialSet Credentials, string Source) ResolveCredentials(NetworkShare share, AppData data)
    {
        if (!share.InheritCredentials) return Own(share.CredentialId, share.Credentials, data, "this share");
        if (share.Server is null)
        {
            var (inherited, from) = ResolveCredentials((NodeBase)share, data, includeSelf: false);
            return inherited.IsEmpty ? (new CredentialSet(), "none") : (inherited, from);
        }
        var (cred, src) = ResolveCredentials(share.Server, data);
        return (cred, src == "this item" ? $"server \"{share.Server.Name}\"" : src);
    }

    /// <summary>Number of folders/connections/shares (plus the global defaults) that reference a saved credential.</summary>
    public static int UsageCount(AppData data, Guid id, IEnumerable<NodeBase> allNodes)
    {
        var nodes = allNodes.ToList();
        return nodes.Count(n => !n.InheritCredentials && n.CredentialId == id)
               + nodes.OfType<Connection>().SelectMany(c => c.Shares).Count(s => !s.InheritCredentials && s.CredentialId == id)
               + (data.DefaultCredentialId == id ? 1 : 0);
    }
}
