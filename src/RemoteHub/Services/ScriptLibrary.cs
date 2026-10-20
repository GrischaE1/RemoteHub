using System.Reflection;
using System.Text;
using RemoteHub.Data;

namespace RemoteHub.Services;

/// <summary>A script of the built-in library: read-only, its version is the RemoteHub release it came with (§8).</summary>
public sealed record LibraryScript(string Id, string Area, string Name, ScriptContent Content)
{
    /// <summary>Its id where scripts are referred to by id (favourites, run history): "library/INV-05".</summary>
    public string ScriptId => ScriptStore.LibraryPrefix + Id;
}

/// <summary>
/// The built-in script library (docs/scripts-view-design.md §8): the scripts of the administrators' script catalog,
/// embedded in the app as <c>Assets/ScriptLibrary/&lt;area&gt;/&lt;ID&gt; &lt;title&gt;.ps1</c>, each with a RemoteHub header.
/// </summary>
public static class ScriptLibrary
{
    private const string ResourcePrefix = "ScriptLibrary/";

    /// <summary>The catalog's areas, in its order, with the folder names the library shows.</summary>
    public static readonly IReadOnlyList<(string Key, string Name)> Areas =
    [
        ("INV", "Inventory and reporting"),
        ("SEC", "Security and compliance"),
        ("SRV", "Server roles and features"),
        ("DSC", "Desired State Configuration"),
        ("FIX", "Troubleshooting and remediation"),
    ];

    /// <summary>The catalog's §12 starter set: read-only scripts that give the most signal for the least risk.</summary>
    public static readonly IReadOnlyList<string> StarterSet = ["INV-04", "INV-05", "INV-07", "INV-08", "INV-09", "SEC-04", "SEC-08", "INV-12", "DSC-10", "SRV-08", "FIX-13"];

    /// <summary>The colour of each area's folder (fixed: library scripts can't be recoloured).</summary>
    public static string AreaColor(string area) => area switch
    {
        "INV" => "Blue",
        "SEC" => "Red",
        "SRV" => "Purple",
        "DSC" => "Teal",
        "FIX" => "Orange",
        _ => "Grey",
    };

    private static IReadOnlyList<LibraryScript>? _all;

    /// <summary>Every library script, sorted by id.</summary>
    public static IReadOnlyList<LibraryScript> All => _all ??= Load(typeof(ScriptLibrary).Assembly);

    public static LibraryScript? Find(string id) =>
        All.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || s.ScriptId.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static bool IsLibraryId(string? scriptId) => scriptId?.StartsWith(ScriptStore.LibraryPrefix, StringComparison.Ordinal) == true;

    internal static IReadOnlyList<LibraryScript> Load(Assembly assembly)
    {
        var list = new List<LibraryScript>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var body = reader.ReadToEnd();
            if (Parse(name[ResourcePrefix.Length..], body) is { } s) list.Add(s);
        }
        return list.OrderBy(s => AreaIndex(s.Area)).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>A library file ("INV/INV-05 Pending reboot detector.ps1") as a script, or null when its header has no id.</summary>
    internal static LibraryScript? Parse(string path, string body)
    {
        var header = ScriptHeader.Read(body);
        if (header?.Id is not { } id) return null;
        var file = Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1]);
        var name = file.StartsWith(id, StringComparison.OrdinalIgnoreCase) ? file[id.Length..].Trim(' ', '-', '_') : file;
        var area = id.Split('-')[0].ToUpperInvariant();
        var content = new ScriptContent(body, ScriptHeader.Synopsis(body) ?? "", header.Context ?? ScriptStore.ContextSystem, header.Risk ?? ScriptStore.RiskR2,
            header.Os ?? ScriptStore.OsBoth, header.TimeoutSec ?? ScriptStore.DefaultTimeoutSec);
        return new LibraryScript(id.ToUpperInvariant(), area, name.Length > 0 ? name : id, content);
    }

    private static int AreaIndex(string area)
    {
        for (var i = 0; i < Areas.Count; i++) if (Areas[i].Key == area) return i;
        return Areas.Count;
    }
}
