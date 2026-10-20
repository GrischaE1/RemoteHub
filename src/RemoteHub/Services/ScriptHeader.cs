using System.Text.RegularExpressions;
using RemoteHub.Data;

namespace RemoteHub.Services;

/// <summary>What a script file's RemoteHub header says (docs/scripts-view-design.md §8.2). Values that aren't valid are null.</summary>
public sealed record ScriptHeaderValues(string? Id, string? Context, string? Os, string? Risk, int? TimeoutSec, string? Language);

/// <summary>
/// The settings a script file carries in its text: a line <c>RemoteHub: id=INV-05; context=System; os=Both; risk=R0</c> in
/// the comment-based help (<c>.NOTES</c>), or as a comment of its own (<c># RemoteHub: …</c>, <c>-- RemoteHub: …</c> for SQL).
/// Read on import and for the library; written on export so the settings survive a round trip through files (§4.7).
/// </summary>
public static partial class ScriptHeader
{
    /// <summary>Only the start of a file is searched: a header belongs at the top.</summary>
    private const int SearchLines = 80;

    [GeneratedRegex(@"^(?<lead>\s*(?:#|--)?\s*)RemoteHub:(?<values>[^\r\n]*)\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex HeaderLine();

    [GeneratedRegex(@"^\s*\.SYNOPSIS\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SynopsisKeyword();

    /// <summary>The header's values, or null when the file has none.</summary>
    public static ScriptHeaderValues? Read(string text)
    {
        if (Find(text) is not { } m) return null;
        var pairs = m.Groups["values"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2, StringSplitOptions.TrimEntries)).Where(p => p.Length == 2)
            .GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First()[1], StringComparer.OrdinalIgnoreCase);
        string? One(string key, string[] allowed) =>
            pairs.TryGetValue(key, out var v) ? allowed.FirstOrDefault(a => a.Equals(v, StringComparison.OrdinalIgnoreCase)) : null;
        var id = pairs.TryGetValue("id", out var i) && Regex.IsMatch(i, "^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$") ? i : null;
        int? timeout = pairs.TryGetValue("timeout", out var t) && int.TryParse(t, out var sec)
            ? Math.Clamp(sec, ScriptStore.MinTimeoutSec, ScriptStore.MaxTimeoutSec) : null;
        var language = pairs.TryGetValue("language", out var l) && ScriptStore.IsSql(l.ToLowerInvariant()) ? l.ToLowerInvariant() : null;
        return new ScriptHeaderValues(id, One("context", ScriptStore.Contexts), One("os", ScriptStore.OsScopes), One("risk", ScriptStore.Risks), timeout, language);
    }

    /// <summary>
    /// The script's settings from its text: the header where it has one, otherwise <paramref name="fallback"/>; the
    /// description from <c>.SYNOPSIS</c> when the fallback has none.
    /// </summary>
    public static ScriptContent Apply(string body, ScriptContent fallback, bool powerShell)
    {
        var h = Read(body);
        var description = fallback.Description.Length > 0 ? fallback.Description : powerShell ? Synopsis(body) ?? "" : "";
        if (!powerShell) return fallback with { Body = body, Description = description, RunContext = null, Risk = null, OsScope = null, TimeoutSec = null };
        return new ScriptContent(body, description, h?.Context ?? fallback.RunContext, h?.Risk ?? fallback.Risk, h?.Os ?? fallback.OsScope,
            h?.TimeoutSec ?? fallback.TimeoutSec);
    }

    /// <summary>
    /// The text with a header line saying <paramref name="content"/>'s settings: an existing header line is replaced (it
    /// keeps its place and its id), otherwise one is added as the first line.
    /// </summary>
    public static string Write(string body, ScriptContent content, string language, string? id = null)
    {
        var powerShell = language == ScriptStore.PowerShell;
        var existing = Read(body);
        var values = new List<string>();
        if ((id ?? existing?.Id) is { } i) values.Add("id=" + i);
        if (powerShell)
        {
            if (content.RunContext is { } ctx) values.Add("context=" + ctx);
            if (content.OsScope is { } os) values.Add("os=" + os);
            if (content.Risk is { } risk) values.Add("risk=" + risk);
            if (content.TimeoutSec is { } t && t != ScriptStore.DefaultTimeoutSec) values.Add("timeout=" + t);
        }
        else values.Add("language=" + language);
        var line = " " + string.Join("; ", values);
        if (Find(body) is { } m)
            return body[..m.Groups["values"].Index] + line + body[(m.Groups["values"].Index + m.Groups["values"].Length)..];
        var newline = body.Contains("\r\n") ? "\r\n" : "\n";
        return (powerShell ? "# RemoteHub:" : "-- RemoteHub:") + line + newline + body;
    }

    /// <summary>The first line of the comment-based help's .SYNOPSIS, or null.</summary>
    public static string? Synopsis(string text)
    {
        var m = SynopsisKeyword().Match(text);
        if (!m.Success) return null;
        foreach (var raw in text[(m.Index + m.Length)..].Split('\n').Skip(1).Take(6))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.StartsWith('.') || line.StartsWith("#>")) break;
            if (line.Length > 0) return line.Length > ScriptStore.MaxDescriptionLength ? line[..ScriptStore.MaxDescriptionLength] : line;
        }
        return null;
    }

    private static Match? Find(string text)
    {
        var head = HeadOf(text);
        var m = HeaderLine().Match(head);
        return m.Success ? m : null;
    }

    private static string HeadOf(string text)
    {
        var pos = 0;
        for (var i = 0; i < SearchLines && pos >= 0 && pos < text.Length; i++)
        {
            pos = text.IndexOf('\n', pos);
            if (pos < 0) return text;
            pos++;
        }
        return pos <= 0 ? text : text[..pos];
    }
}
