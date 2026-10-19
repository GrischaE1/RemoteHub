using System.Security;
using System.Text;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace RemoteHub.Views;

/// <summary>
/// Syntax colouring for script blocks in notes (docs/notes-view-design.md §4.5.5a): AvalonEdit's own definition where it
/// ships one, otherwise a small definition of ours built from keyword lists. All are recoloured with one palette per
/// theme, so every language looks the same on the note's code background.
/// </summary>
public static class ScriptHighlighting
{
    private static readonly Dictionary<string, IHighlightingDefinition?> Cache = new(StringComparer.OrdinalIgnoreCase);

    // AvalonEdit's built-in definitions by their registered names.
    private static readonly Dictionary<string, string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["powershell"] = "PowerShell", ["python"] = "Python", ["vbscript"] = "VB", ["vbnet"] = "VB",
        ["c"] = "C++", ["cpp"] = "C++", ["csharp"] = "C#", ["java"] = "Java", ["javascript"] = "JavaScript",
        ["php"] = "PHP", ["sql"] = "TSQL", ["tsql"] = "TSQL", ["pgsql"] = "TSQL", ["mysql"] = "TSQL", ["plsql"] = "TSQL",
        ["sqlite"] = "TSQL", ["wql"] = "TSQL", ["json"] = "Json", ["xml"] = "XML", ["html"] = "HTML", ["css"] = "CSS",
        ["markdown"] = "MarkDown", ["diff"] = "Patch",
    };

    static ScriptHighlighting() => AppChrome.PaletteChanged += () =>
    {
        lock (Cache) foreach (var d in Cache.Values) if (d is not null) Recolour(d);
        Changed?.Invoke();
    };

    /// <summary>Raised after the theme changed and the definitions were recoloured: script blocks redraw.</summary>
    public static event Action? Changed;

    /// <summary>The definition for a fence id, or null for plain text and unknown languages.</summary>
    public static IHighlightingDefinition? For(string? fenceId)
    {
        var id = Services.ScriptLanguages.Find(fenceId)?.Id;
        if (id is null or Services.ScriptLanguages.PlainId) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(id, out var cached)) return cached;
            IHighlightingDefinition? def = null;
            try
            {
                def = BuiltIn.TryGetValue(id, out var name) ? Copy(name) : Own(id);
                if (def is not null) Recolour(def);
            }
            catch (Exception ex) { Services.DiagLog.Write($"Syntax colouring for {id} failed: {ex.GetType().Name}"); }
            Cache[id] = def;
            return def;
        }
    }

    /// <summary>A private copy of a built-in definition, so recolouring it doesn't change it for anyone else.</summary>
    private static IHighlightingDefinition? Copy(string name)
    {
        var type = typeof(HighlightingManager).Assembly;
        var resource = type.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith(ResourceFile(name), StringComparison.OrdinalIgnoreCase));
        if (resource is null) return HighlightingManager.Instance.GetDefinition(name);
        using var s = type.GetManifestResourceStream(resource)!;
        using var reader = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return HighlightingLoader.Load(HighlightingLoader.LoadXshd(reader), HighlightingManager.Instance);
    }

    private static string ResourceFile(string name) => name switch
    {
        "PowerShell" => ".PowerShell.xshd",
        "Json" => ".Json.xshd",
        "C++" => ".CPP-Mode.xshd",
        "C#" => ".CSharp-Mode.xshd",
        "TSQL" => ".TSQL-Mode.xshd",
        "Patch" => ".Patch-Mode.xshd",
        "MarkDown" => ".MarkDown-Mode.xshd",
        _ => $".{name}-Mode.xshd",
    };

    // ------------------------------------------------------------------ palette

    private sealed record Palette(Color Comment, Color String, Color Keyword, Color Number, Color Type, Color Variable, Color Other);

    private static readonly Palette Dark = new(C(0x6A, 0x99, 0x55), C(0xCE, 0x91, 0x78), C(0x56, 0x9C, 0xD6), C(0xB5, 0xCE, 0xA8),
        C(0x4E, 0xC9, 0xB0), C(0x9C, 0xDC, 0xFE), C(0xDC, 0xDC, 0xAA));
    private static readonly Palette Light = new(C(0x00, 0x80, 0x00), C(0xA3, 0x15, 0x15), C(0x00, 0x00, 0xFF), C(0x09, 0x86, 0x58),
        C(0x26, 0x7F, 0x99), C(0x00, 0x10, 0x80), C(0x79, 0x5E, 0x26));
    // PowerShell keeps the console's colours (PsSyntax) on the dark theme, so a script looks the same in a note and in a tab.
    private static readonly Palette PowerShellDark = new(C(0x6A, 0x99, 0x55), C(0x61, 0xD6, 0xD6), C(0x5C, 0x9D, 0xFF), C(0xD7, 0xBA, 0xFF),
        C(0x8B, 0xC4, 0xA8), C(0x6C, 0xD9, 0x7E), C(0xF9, 0xE2, 0x7A));

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static void Recolour(IHighlightingDefinition def)
    {
        var p = def.Name == "PowerShell" && AppChrome.IsDark ? PowerShellDark : AppChrome.IsDark ? Dark : Light;
        foreach (var color in def.NamedHighlightingColors)
        {
            var n = color.Name.ToLowerInvariant();
            var c = n.Contains("comment") || n.Contains("doc") ? p.Comment
                : n.Contains("string") || n.Contains("char") || n.Contains("cdata") || n.Contains("attributevalue") ? p.String
                : n.Contains("digit") || n.Contains("number") ? p.Number
                : n.Contains("type") || n.Contains("class") || n.Contains("attributename") ? p.Type
                : n.Contains("variable") || n.Contains("parameter") || n.Contains("field") || n.Contains("property") ? p.Variable
                : n.Contains("command") || n.Contains("method") || n.Contains("function") || n.Contains("heading") ? p.Other
                : p.Keyword;
            color.Foreground = new SimpleHighlightingBrush(c);
            color.Background = null;
        }
    }

    // ------------------------------------------------------------------ our own definitions

    private sealed record Lang(string Name, bool IgnoreCase, string[] Keywords, string[] LineComments, (string Begin, string End)? Block, string Strings, string? Extra = null);

    private static readonly string[] CLike = ["if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue", "return", "true", "false", "null"];

    private static Lang? Definition(string id) => id switch
    {
        "batch" => new("Batch", true, ["echo", "set", "if", "else", "goto", "call", "for", "in", "do", "exist", "not", "errorlevel", "defined", "setlocal", "endlocal", "enabledelayedexpansion", "shift", "pause", "exit", "start", "cd", "pushd", "popd", "copy", "move", "del", "mkdir", "rmdir", "equ", "neq", "lss", "leq", "gtr", "geq", "off", "on", "nul"],
            ["rem ", "REM ", "::"], null, "\"", @"%~?[\w]+%?|![\w]+!"),
        "bash" => new("Bash", false, ["if", "then", "else", "elif", "fi", "for", "while", "until", "do", "done", "case", "esac", "in", "function", "return", "local", "export", "readonly", "declare", "unset", "shift", "exit", "source", "echo", "printf", "read", "set", "trap", "true", "false", "sudo"],
            ["#"], null, "\"'", @"\$\{?[\w@#\?\*]+\}?"),
        "typescript" => new("TypeScript", false, [.. CLike, "const", "let", "var", "function", "class", "interface", "type", "enum", "implements", "extends", "import", "export", "from", "as", "new", "this", "async", "await", "public", "private", "protected", "readonly", "static", "typeof", "instanceof", "keyof", "undefined", "void", "string", "number", "boolean", "any", "unknown", "never", "try", "catch", "finally", "throw", "of", "in"],
            ["//"], ("/*", "*/"), "\"'`"),
        "go" => new("Go", false, [.. CLike, "func", "package", "import", "var", "const", "type", "struct", "interface", "map", "chan", "go", "defer", "select", "range", "fallthrough", "goto", "nil", "iota", "string", "int", "int64", "bool", "error", "byte", "rune", "float64", "make", "new", "len", "append"],
            ["//"], ("/*", "*/"), "\"'`"),
        "rust" => new("Rust", false, [.. CLike, "fn", "let", "mut", "const", "static", "struct", "enum", "impl", "trait", "pub", "use", "mod", "crate", "self", "Self", "super", "match", "loop", "in", "as", "ref", "move", "async", "await", "dyn", "where", "unsafe", "Some", "None", "Ok", "Err", "String", "Vec", "Option", "Result", "u8", "u32", "u64", "i32", "i64", "usize", "bool", "str"],
            ["//"], ("/*", "*/"), "\""),
        "kql" => new("KQL", false, ["where", "summarize", "project", "project-away", "project-rename", "extend", "take", "limit", "join", "kind", "on", "union", "render", "top", "order", "sort", "by", "asc", "desc", "count", "distinct", "mv-expand", "parse", "let", "and", "or", "not", "in", "has", "has_any", "contains", "startswith", "endswith", "matches", "regex", "between", "ago", "now", "bin", "dcount", "countif", "sum", "avg", "min", "max", "arg_max", "arg_min", "make_set", "make_list", "tostring", "toint", "todatetime", "isnotempty", "isempty", "iff", "case", "datatable", "evaluate", "invoke", "lookup", "true", "false"],
            ["//"], null, "\"'"),
        "ldap" => new("LDAP", true, [], [], null, "", @"[&|!=~<>]+|\b(objectClass|objectCategory|sAMAccountName|cn|ou|dc|memberOf|userAccountControl|mail|name)\b"),
        "yaml" => new("YAML", false, ["true", "false", "yes", "no", "null", "on", "off"], ["#"], null, "\"'", @"^\s*-?\s*[\w\.\-""']+\s*:(?=\s|$)"),
        "ini" => new("INI", false, ["true", "false", "yes", "no", "on", "off"], [";", "#"], null, "\"", @"^\s*\[[^\]]+\]|^\s*[\w\.\-]+\s*(?==)"),
        "toml" => new("TOML", false, ["true", "false"], ["#"], null, "\"'", @"^\s*\[\[?[^\]]+\]\]?|^\s*[\w\.\-]+\s*(?==)"),
        "reg" => new("Registry", true, ["dword", "qword", "hex"], [";"], null, "\"", @"^\s*\[-?HKEY_[^\]]+\]|^Windows Registry Editor Version [\d\.]+"),
        "bicep" => new("Bicep", false, ["param", "var", "resource", "module", "output", "targetScope", "existing", "if", "for", "in", "true", "false", "null", "string", "int", "bool", "object", "array", "import", "metadata", "type", "func"],
            ["//"], ("/*", "*/"), "'"),
        "hcl" => new("HCL", false, ["resource", "data", "variable", "output", "locals", "module", "provider", "terraform", "for_each", "count", "depends_on", "lifecycle", "dynamic", "content", "true", "false", "null", "for", "in", "if"],
            ["#", "//"], ("/*", "*/"), "\""),
        "dockerfile" => new("Dockerfile", true, ["from", "run", "cmd", "label", "expose", "env", "add", "copy", "entrypoint", "volume", "user", "workdir", "arg", "onbuild", "stopsignal", "healthcheck", "shell", "as"],
            ["#"], null, "\"'", @"\$\{?\w+\}?"),
        "nginx" => new("Nginx", false, ["server", "location", "listen", "server_name", "root", "index", "proxy_pass", "proxy_set_header", "return", "rewrite", "include", "upstream", "http", "events", "ssl_certificate", "ssl_certificate_key", "add_header", "try_files", "if", "set", "on", "off"],
            ["#"], null, "\"'", @"\$\w+"),
        _ => null,
    };

    private static IHighlightingDefinition? Own(string id)
    {
        if (Definition(id) is not { } lang) return null;
        static string E(string s) => SecurityElement.Escape(s)!;
        static string Rx(string s) => System.Text.RegularExpressions.Regex.Escape(s);
        var x = new StringBuilder();
        x.Append($"<SyntaxDefinition name=\"{E(lang.Name)}\" xmlns=\"http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008\">");
        foreach (var name in new[] { "Comment", "String", "Keyword", "Number", "Variable" }) x.Append($"<Color name=\"{name}\" foreground=\"#808080\"/>");
        x.Append($"<RuleSet ignoreCase=\"{(lang.IgnoreCase ? "true" : "false")}\">");
        foreach (var lc in lang.LineComments) x.Append($"<Span color=\"Comment\" begin=\"{E(Rx(lc))}\"/>");
        if (lang.Block is var (b, e)) x.Append($"<Span color=\"Comment\" multiline=\"true\" begin=\"{E(Rx(b))}\" end=\"{E(Rx(e))}\"/>");
        foreach (var q in lang.Strings)
            x.Append($"<Span color=\"String\"><Begin>{E(Rx(q.ToString()))}</Begin><End>{E(Rx(q.ToString()))}</End><RuleSet><Span begin=\"\\\\\" end=\".\"/></RuleSet></Span>");
        if (lang.Keywords.Length > 0)
        {
            x.Append("<Keywords color=\"Keyword\">");
            foreach (var k in lang.Keywords) x.Append($"<Word>{E(k)}</Word>");
            x.Append("</Keywords>");
        }
        if (lang.Extra is not null) x.Append($"<Rule color=\"Variable\">{E(lang.Extra)}</Rule>");
        x.Append(@"<Rule color=""Number"">\b0[xX][0-9a-fA-F]+\b|\b\d+(\.\d+)?\b</Rule>");
        x.Append("</RuleSet></SyntaxDefinition>");
        using var reader = XmlReader.Create(new StringReader(x.ToString()), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return HighlightingLoader.Load(HighlightingLoader.LoadXshd(reader), HighlightingManager.Instance);
    }
}
