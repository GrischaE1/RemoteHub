using RemoteHub.Models;

namespace RemoteHub.Remoting;

/// <summary>Registry tab: browse and edit the remote computer's registry over PowerShell remoting.</summary>
public sealed class RegistrySession : RemoteToolSession
{
    public RegistrySession(Connection connection, string? startPath) : base(connection) => StartPath = startPath;

    public override string KindSymbol => "CubeTree24";
    public override string KindLabel => "Registry";
    public override Services.RemoteTool Tool => Services.RemoteTool.Registry;
    /// <summary>Key shown first (the last key used for this connection).</summary>
    public string? StartPath { get; set; }

    /// <summary>Lists keys instead of the remote registry (tests).</summary>
    internal Func<string, Task<RegListing>>? Lister { get; set; }

    public Task<RegListing> ListAsync(string path) => Lister?.Invoke(path) ?? Run(rs => RemoteRegistry.List(rs, path));
    public Task SetValueAsync(string key, string name, RegKind kind, object data, bool createOnly) =>
        Run(rs => { RemoteRegistry.SetValue(rs, key, name, kind, data, createOnly); return true; });
    public Task DeleteValuesAsync(string key, IEnumerable<string> names) => Run(rs => { RemoteRegistry.DeleteValues(rs, key, names); return true; });
    public Task RenameValueAsync(string key, string name, string newName) => Run(rs => { RemoteRegistry.RenameValue(rs, key, name, newName); return true; });
    public Task<string> CreateKeyAsync(string parent, string name) => Run(rs => RemoteRegistry.CreateKey(rs, parent, name));
    public Task DeleteKeyAsync(string key) => Run(rs => { RemoteRegistry.DeleteKey(rs, key); return true; });
    public Task<string> RenameKeyAsync(string key, string newName) => Run(rs => RemoteRegistry.RenameKey(rs, key, newName));
    public Task<byte[]> ExportAsync(string key) => Run(rs => RemoteRegistry.Export(rs, key));
}
