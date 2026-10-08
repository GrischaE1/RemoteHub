using RemoteHub.Models;

namespace RemoteHub.Services;

/// <summary>Resolves effective settings by walking node → parent folders → global defaults.</summary>
public static class Inheritance
{
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

    /// <summary>The credentials <paramref name="node"/> would have inside <paramref name="parent"/> (null = top level) – e.g. before moving it there.</summary>
    public static (CredentialSet Credentials, string Source) ResolveCredentialsIn(NodeBase node, Folder? parent, AppData data)
    {
        if (!node.InheritCredentials) return Own(node.CredentialId, node.Credentials, data, "this item");
        for (var f = parent; f is not null; f = f.Parent)
            if (!f.InheritCredentials) return Own(f.CredentialId, f.Credentials, data, $"folder \"{f.Name}\"");
        var (cred, src) = Own(data.DefaultCredentialId, data.DefaultCredentials, data, "global defaults");
        return cred.IsEmpty ? (new CredentialSet(), "none – Windows will prompt") : (cred, src);
    }

    /// <summary>Where the item is: "LAB › Servers › NUC13", or "Top level › NUC13".</summary>
    public static string Location(NodeBase node)
    {
        var path = node.Ancestors().Reverse().Select(a => a.Name).ToList();
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

    /// <summary>Number of folders/connections (plus the global defaults) that reference a saved credential.</summary>
    public static int UsageCount(AppData data, Guid id, IEnumerable<NodeBase> allNodes) =>
        allNodes.Count(n => !n.InheritCredentials && n.CredentialId == id) + (data.DefaultCredentialId == id ? 1 : 0);
}
