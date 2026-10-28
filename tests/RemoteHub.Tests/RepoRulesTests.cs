using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RemoteHub.Tests;

/// <summary>
/// Written project rules that a build doesn't check by itself: lab scripts stay ASCII-only and name the encoding of
/// every file they write (lab instructions; review C-01, C-02), and every shipped package has a third-party notice
/// (review S-11).
/// </summary>
public class RepoRulesTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\.."));

    private static IEnumerable<string> LabScripts() =>
        new[] { "*.ps1", "*.psm1", "*.psd1" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(Root, "lab"), p, SearchOption.AllDirectories))
            .Where(f => !f.Contains(@"\bin\") && !f.Contains(@"\obj\"));

    [Fact]
    public void Lab_scripts_are_ASCII_only()
    {
        // Windows PowerShell 5.1 reads a file without a BOM as ANSI: anything above 127 changes meaning or breaks parsing.
        var found = LabScripts()
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => x.line.Any(c => c > 127))
            .Select(x => $"{Path.GetRelativePath(Root, x.f)}:{x.i + 1}")
            .ToList();
        Assert.True(found.Count == 0, "Non-ASCII characters in " + string.Join(", ", found));
    }

    [Fact]
    public void Lab_file_writes_name_an_encoding()
    {
        // The default differs between 5.1 (ANSI) and 7 (UTF-8 without BOM).
        var write = new Regex(@"\b(Set-Content|Add-Content|Out-File)\b");
        var found = LabScripts()
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => !x.line.TrimStart().StartsWith('#') && write.IsMatch(x.line) && !x.line.Contains("-Encoding"))
            .Select(x => $"{Path.GetRelativePath(Root, x.f)}:{x.i + 1}")
            .ToList();
        Assert.True(found.Count == 0, "Writes without -Encoding in " + string.Join(", ", found));
    }

    [Fact]
    public void Every_shipped_package_has_a_third_party_notice()
    {
        var notices = File.ReadAllText(Path.Combine(Root, "THIRD-PARTY-NOTICES.md"));
        var missing = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(p => XDocument.Load(p).Descendants("PackageReference"))
            .Where(r => !string.Equals((string?)r.Attribute("PrivateAssets"), "all", StringComparison.OrdinalIgnoreCase))
            .Select(r => ((string)r.Attribute("Include")!, (string)r.Attribute("Version")!))
            .Distinct()
            .Where(p => !notices.Contains(p.Item1, StringComparison.OrdinalIgnoreCase) || !notices.Contains(p.Item2, StringComparison.Ordinal))
            .Select(p => $"{p.Item1} {p.Item2}")
            .ToList();
        Assert.True(missing.Count == 0, "THIRD-PARTY-NOTICES.md doesn't name " + string.Join(", ", missing));
    }
}
