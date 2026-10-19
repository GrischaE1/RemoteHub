using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemoteHub.Services;

/// <summary>A language of a script block (docs/notes-view-design.md §4.5.5a).</summary>
/// <param name="Id">The fence id written to the Markdown (```` ```powershell ````).</param>
/// <param name="Aliases">Other fence ids that mean this language on paste and import.</param>
/// <param name="Extension">For "Save as file…" (a whole file name when it has no dot, e.g. Dockerfile).</param>
public sealed record ScriptLanguage(string Id, string Name, string Group, string Extension, params string[] Aliases);

/// <summary>The script block languages, their fence ids and the guess from pasted text.</summary>
public static partial class ScriptLanguages
{
    public const string PlainId = "text";

    public static readonly IReadOnlyList<ScriptLanguage> All =
    [
        new("powershell", "PowerShell", "Shells and scripting", ".ps1", "ps1", "pwsh", "ps", "posh"),
        new("batch", "Command Prompt", "Shells and scripting", ".cmd", "bat", "cmd", "dos"),
        new("bash", "Bash", "Shells and scripting", ".sh", "sh", "shell", "zsh"),
        new("python", "Python", "Shells and scripting", ".py", "py"),
        new("vbscript", "VBScript", "Shells and scripting", ".vbs", "vbs"),
        new("c", "C", "Programming", ".c", "h"),
        new("cpp", "C++", "Programming", ".cpp", "c++", "cc", "hpp", "cxx"),
        new("csharp", "C#", "Programming", ".cs", "cs", "c#"),
        new("vbnet", "Visual Basic .NET", "Programming", ".vb", "vb"),
        new("java", "Java", "Programming", ".java"),
        new("javascript", "JavaScript", "Programming", ".js", "js", "jsx", "node"),
        new("typescript", "TypeScript", "Programming", ".ts", "ts", "tsx"),
        new("go", "Go", "Programming", ".go", "golang"),
        new("rust", "Rust", "Programming", ".rs", "rs"),
        new("php", "PHP", "Programming", ".php"),
        new("sql", "SQL", "Queries", ".sql"),
        new("tsql", "T-SQL (SQL Server, Azure SQL)", "Queries", ".sql", "mssql", "t-sql"),
        new("pgsql", "PostgreSQL", "Queries", ".sql", "postgres", "postgresql", "psql"),
        new("mysql", "MySQL / MariaDB", "Queries", ".sql", "mariadb"),
        new("plsql", "Oracle PL/SQL", "Queries", ".sql", "oracle"),
        new("sqlite", "SQLite", "Queries", ".sql", "sqlite3"),
        new("kql", "KQL (Log Analytics, Defender, Intune)", "Queries", ".kql", "kusto"),
        new("wql", "WQL (WMI, ConfigMgr)", "Queries", ".wql"),
        new("ldap", "LDAP filter", "Queries", ".txt"),
        new("json", "JSON", "Configuration and data", ".json", "jsonc", "json5"),
        new("xml", "XML", "Configuration and data", ".xml", "xaml", "xsd", "admx", "adml", "wxs", "csproj", "props", "targets", "config"),
        new("yaml", "YAML", "Configuration and data", ".yaml", "yml"),
        new("ini", "INI / .conf", "Configuration and data", ".ini", "conf", "cfg", "inf"),
        new("toml", "TOML", "Configuration and data", ".toml"),
        new("reg", "Registry", "Configuration and data", ".reg", "registry"),
        new("csv", "CSV", "Configuration and data", ".csv", "tsv"),
        new("html", "HTML", "Configuration and data", ".html", "htm"),
        new("css", "CSS", "Configuration and data", ".css"),
        new("markdown", "Markdown", "Configuration and data", ".md", "md"),
        new("bicep", "Bicep", "Infrastructure", ".bicep"),
        new("hcl", "Terraform / HCL", "Infrastructure", ".tf", "terraform", "tf"),
        new("dockerfile", "Dockerfile", "Infrastructure", "Dockerfile", "docker"),
        new("nginx", "Nginx / Apache config", "Infrastructure", ".conf", "apache", "apacheconf"),
        new("diff", "Diff / patch", "Other", ".diff", "patch"),
        new("log", "Log output", "Other", ".log"),
        new(PlainId, "Plain text", "Other", ".txt", "txt", "plaintext", "plain", "none"),
    ];

    private static readonly Dictionary<string, ScriptLanguage> ById = BuildIndex();

    private static Dictionary<string, ScriptLanguage> BuildIndex()
    {
        var map = new Dictionary<string, ScriptLanguage>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in All)
        {
            map[l.Id] = l;
            foreach (var a in l.Aliases) map.TryAdd(a, l);
        }
        return map;
    }

    /// <summary>The language for a fence id or alias, or null when the id is unknown (it is then kept as it is).</summary>
    public static ScriptLanguage? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? Get(PlainId) : ById.GetValueOrDefault(id.Trim());

    public static ScriptLanguage Get(string id) => ById[id];

    /// <summary>The fence id to write: the language's own id for a known alias, otherwise the id as it came (lower case kept).</summary>
    public static string Normalize(string? id) => Find(id)?.Id ?? (id ?? PlainId).Trim();

    /// <summary>A display name for any fence id, including unknown ones.</summary>
    public static string DisplayName(string? id) => Find(id)?.Name ?? (id ?? "").Trim();

    // ------------------------------------------------------------------ guessing (§4.5.5a: clear signs only)

    /// <summary>The language that pasted text most likely is, or <see cref="PlainId"/> when the signs aren't clear.</summary>
    public static string Guess(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return PlainId;
        if (t.StartsWith("Windows Registry Editor Version", StringComparison.Ordinal) || t.StartsWith("REGEDIT4", StringComparison.Ordinal)) return "reg";
        if (t.StartsWith("#!", StringComparison.Ordinal))
            return t.StartsWith("#!/usr/bin/env python", StringComparison.Ordinal) || t.StartsWith("#!/usr/bin/python", StringComparison.Ordinal) ? "python" : "bash";
        if (t.StartsWith("<?xml", StringComparison.Ordinal)) return "xml";
        if (LooksLikeJson(t)) return "json";
        if (PowerShellSigns().IsMatch(t)) return "powershell";
        if (KqlSigns().IsMatch(t)) return "kql";
        if (SqlSigns().IsMatch(t)) return TsqlSigns().IsMatch(t) ? "tsql" : "sql";
        if (t.Contains("#include", StringComparison.Ordinal)) return t.Contains("std::", StringComparison.Ordinal) || t.Contains("class ", StringComparison.Ordinal) ? "cpp" : "c";
        if (CSharpSigns().IsMatch(t)) return "csharp";
        if (BatchSigns().IsMatch(t)) return "batch";
        if (t.StartsWith('<') && t.EndsWith('>') && XmlRoot().IsMatch(t)) return t.Contains("<html", StringComparison.OrdinalIgnoreCase) ? "html" : "xml";
        return PlainId;
    }

    private static bool LooksLikeJson(string t)
    {
        if (!(t.StartsWith('{') && t.EndsWith('}')) && !(t.StartsWith('[') && t.EndsWith(']'))) return false;
        try { using var _ = JsonDocument.Parse(t); return true; }
        catch (JsonException) { return false; }
    }

    // Cmdlets (Verb-Noun), $variable assignments and pipes into the usual cmdlets.
    [GeneratedRegex(@"(^|\s|\()(Get|Set|New|Remove|Add|Start|Stop|Restart|Invoke|Test|Import|Export|Write|Select|Where|ForEach|Out|Format|Enable|Disable|Install|Update|Clear|Copy|Move|Rename|Join|Split|Convert|ConvertTo|ConvertFrom|Resolve|Register|Unregister)-[A-Z][A-Za-z]+\b|^\s*\$[A-Za-z_][\w:]*\s*=|\|\s*(Where-Object|ForEach-Object|Select-Object|Sort-Object)\b", RegexOptions.Multiline)]
    private static partial Regex PowerShellSigns();

    // A table name, then a pipe into a KQL operator.
    [GeneratedRegex(@"^\s*[A-Za-z_][\w]*\s*(\r?\n\s*)?\|\s*(where|summarize|project|extend|take|limit|join|render|top|order by|sort by|count|distinct|mv-expand|parse)\b", RegexOptions.Multiline)]
    private static partial Regex KqlSigns();

    [GeneratedRegex(@"\b(SELECT\s[\s\S]+?\sFROM\s|UPDATE\s+[\w\.\[\]""]+\s+SET\s|INSERT\s+INTO\s|DELETE\s+FROM\s|CREATE\s+(TABLE|VIEW|INDEX|PROCEDURE|FUNCTION)\s|ALTER\s+TABLE\s)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlSigns();

    [GeneratedRegex(@"\[dbo\]|\bdbo\.|^\s*GO\s*$|@@\w+|\bNVARCHAR\b|\bTOP\s*\(?\d", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex TsqlSigns();

    [GeneratedRegex(@"^\s*using\s+System[\w\.]*\s*;|\bnamespace\s+[\w\.]+\s*[{;]|\b(public|private|internal)\s+(sealed\s+|static\s+)*(class|record|interface)\s+\w+", RegexOptions.Multiline)]
    private static partial Regex CSharpSigns();

    [GeneratedRegex(@"^\s*@echo\s+off\b|^\s*(rem|::)\s|%~?[a-z0-9]%?|\bset\s+/[ap]\b|\bgoto\s+:?\w+|\berrorlevel\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex BatchSigns();

    [GeneratedRegex(@"^<([A-Za-z][\w:\-\.]*)[\s>/][\s\S]*</\1>$|^<[A-Za-z][\w:\-\.]*[^>]*/>$")]
    private static partial Regex XmlRoot();
}
