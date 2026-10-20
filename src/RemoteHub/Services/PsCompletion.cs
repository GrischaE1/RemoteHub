using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using RemoteHub.Remoting;

namespace RemoteHub.Services;

/// <summary>A syntax error in a script, 1-based line and column.</summary>
public sealed record ScriptSyntaxError(int Line, int Column, int StartOffset, int EndOffset, string Message);

/// <summary>A parameter of a script's <c>param()</c> block, for the run window's form (§11, phase 4).</summary>
public sealed record ScriptParameter(string Name, string Type, string? DefaultText, bool Mandatory, IReadOnlyList<string> Choices, string? Help);

/// <summary>
/// PowerShell's own help for the script editor (docs/scripts-view-design.md §5.3): completion from a local runspace (or
/// a connection's, phase 4), syntax errors from the parser, and the script's parameters. Completion parses the text and
/// never runs it; PowerShell may load a module to complete its commands, as it does in any console.
/// </summary>
public static class PsCompletion
{
    private static readonly object Gate = new();
    private static Runspace? _local;

    /// <summary>Completion at <paramref name="caret"/> (blocking – call from a background thread). Null when nothing matches.</summary>
    public static CompletionSet? Complete(string text, int caret, Runspace? remote = null)
    {
        if (remote is not null) return PsSession.CompleteOn(remote, text, caret);
        lock (Gate)
        {
            try
            {
                if (_local is null || _local.RunspaceStateInfo.State != RunspaceState.Opened)
                {
                    _local?.Dispose();
                    var iss = InitialSessionState.CreateDefault2();
                    _local = RunspaceFactory.CreateRunspace(iss);
                    _local.Open();
                }
                return PsSession.CompleteOn(_local, text, caret);
            }
            catch (Exception ex)
            {
                DiagLog.Write("Scripts: completion failed: " + ex.GetType().Name);
                return null;
            }
        }
    }

    /// <summary>The syntax of a command (<c>Get-Command -Syntax</c>) for the signature tooltip, or null.</summary>
    public static string? Syntax(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 100 || command.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'))) return null;
        lock (Gate)
        {
            try
            {
                if (_local is not { RunspaceStateInfo.State: RunspaceState.Opened }) return null;
                using var ps = PowerShell.Create();
                ps.Runspace = _local;
                ps.AddCommand("Get-Command").AddParameter("Name", command).AddParameter("Syntax").AddParameter("ErrorAction", "SilentlyContinue");
                var text = string.Join(Environment.NewLine, ps.Invoke().Select(o => o?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                return text.Length == 0 ? null : text.Length > 1500 ? text[..1500] + "…" : text;
            }
            catch { return null; }
        }
    }

    /// <summary>The script's syntax errors, from PowerShell's parser.</summary>
    public static IReadOnlyList<ScriptSyntaxError> Errors(string text)
    {
        try
        {
            Parser.ParseInput(text, out _, out var errors);
            return errors.Select(e => new ScriptSyntaxError(e.Extent.StartLineNumber, e.Extent.StartColumnNumber, e.Extent.StartOffset,
                Math.Max(e.Extent.EndOffset, e.Extent.StartOffset + 1), e.Message)).ToList();
        }
        catch (Exception ex)
        {
            // A parser exception can quote the input: only its type is logged.
            DiagLog.Write("Scripts: parsing failed: " + ex.GetType().Name);
            return [];
        }
    }

    /// <summary>The parameters of the script's top-level <c>param()</c> block, in order.</summary>
    public static IReadOnlyList<ScriptParameter> Parameters(string text)
    {
        ScriptBlockAst ast;
        try { ast = Parser.ParseInput(text, out _, out _); }
        catch { return []; }
        if (ast.ParamBlock is not { } block) return [];
        var list = new List<ScriptParameter>();
        foreach (var p in block.Parameters)
        {
            var name = p.Name.VariablePath.UserPath;
            var type = TypeOf(p);
            var mandatory = false;
            string? help = null;
            var choices = new List<string>();
            foreach (var attr in p.Attributes.OfType<AttributeAst>())
            {
                var attrName = attr.TypeName.Name;
                if (attrName.Equals("Parameter", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var na in attr.NamedArguments)
                    {
                        if (na.ArgumentName.Equals("Mandatory", StringComparison.OrdinalIgnoreCase))
                            mandatory = na.ExpressionOmitted || na.Argument is VariableExpressionAst { VariablePath.UserPath: "true" } || na.Argument.Extent.Text == "1";
                        if (na.ArgumentName.Equals("HelpMessage", StringComparison.OrdinalIgnoreCase) && na.Argument is StringConstantExpressionAst s)
                            help = s.Value;
                    }
                }
                if (attrName.Equals("ValidateSet", StringComparison.OrdinalIgnoreCase))
                    choices.AddRange(attr.PositionalArguments.OfType<StringConstantExpressionAst>().Select(s => s.Value));
            }
            string? def = p.DefaultValue switch
            {
                null => null,
                StringConstantExpressionAst s => s.Value,
                ConstantExpressionAst c => Convert.ToString(c.Value, System.Globalization.CultureInfo.InvariantCulture),
                VariableExpressionAst { VariablePath.UserPath: var v } when v is "true" or "false" => v,
                var other => other.Extent.Text,
            };
            list.Add(new ScriptParameter(name, type, def, mandatory, choices, help));
        }
        return list;
    }

    /// <summary>"string", "int", "bool", "switch", "string[]", or "other" for types the form shows as text.</summary>
    private static string TypeOf(ParameterAst p)
    {
        var t = p.Attributes.OfType<TypeConstraintAst>().FirstOrDefault()?.TypeName;
        if (t is null) return "string";
        var n = t.FullName.ToLowerInvariant();
        return n switch
        {
            "string" or "system.string" => "string",
            "int" or "int32" or "system.int32" or "long" or "int64" or "system.int64" => "int",
            "bool" or "boolean" or "system.boolean" => "bool",
            "switch" or "switchparameter" or "system.management.automation.switchparameter" => "switch",
            "string[]" or "system.string[]" => "string[]",
            _ => "other",
        };
    }

    /// <summary>
    /// The values the run window's form produced, converted for the script (JSON in params.json on the device): ints as
    /// numbers, switches and booleans as true/false, lists split at line breaks or commas. Empty optional values are left out.
    /// </summary>
    public static Dictionary<string, object?> Arguments(IReadOnlyList<ScriptParameter> parameters, IReadOnlyDictionary<string, string> values)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parameters)
        {
            if (!values.TryGetValue(p.Name, out var raw)) continue;
            raw = raw.Trim();
            switch (p.Type)
            {
                case "switch":
                case "bool":
                    if (bool.TryParse(raw, out var b) && (b || p.Type == "bool")) args[p.Name] = b;
                    break;
                case "int":
                    if (long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)) args[p.Name] = n;
                    break;
                case "string[]":
                    var items = raw.Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (items.Length > 0) args[p.Name] = items;
                    break;
                default:
                    if (raw.Length > 0) args[p.Name] = raw;
                    break;
            }
        }
        return args;
    }
}
