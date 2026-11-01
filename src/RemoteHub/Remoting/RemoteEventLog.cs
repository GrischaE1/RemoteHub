using System.Globalization;
using System.Management.Automation.Runspaces;

namespace RemoteHub.Remoting;

/// <summary>One event log on the device that has entries.</summary>
public sealed record EventLogInfo(string Name, long Count);

/// <summary>Levels as the Event Viewer names them (Information also covers level 0, which classic logs write).</summary>
public enum EventLevel { Critical = 1, Error = 2, Warning = 3, Information = 4, Verbose = 5 }

/// <summary>One event of a log.</summary>
public sealed record EventEntry(long RecordId, DateTime TimeUtc, EventLevel Level, string Source, int EventId, string Message);

/// <summary>A page of events, newest first; <see cref="More"/> when older ones match too.</summary>
public sealed record EventPage(List<EventEntry> Entries, bool More);

/// <summary>What the Event Viewer tab asks for: one log, optionally levels, a start time and a source.</summary>
public sealed record EventQuery(string Log, IReadOnlyList<EventLevel> Levels, DateTime? SinceUtc, string Source);

/// <summary>Event logs of the device for the Event Viewer tab: read-only, filtered on the device, 500 at a time.</summary>
public static class RemoteEventLog
{
    public const int PageSize = 500;

    /// <summary>The logs the Event Viewer shows first; the others follow by name.</summary>
    public static readonly string[] MainLogs = ["Application", "System", "Security", "Setup"];

    private const string LogsScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        foreach ($l in Get-WinEvent -ListLog *) { if ($l.RecordCount -gt 0) { "$($l.LogName)`t$($l.RecordCount)" } }
        """;

    // The XPath is built on the device from typed parameters; a source name is the only text in it, and one with a quote
    // is refused rather than escaped (XPath 1.0 has no escape for it). Messages go base64: they hold tabs and new lines.
    private const string EventsScript = """
        param([string]$Log, [int[]]$Levels, [string]$Since, [string]$Source, [int]$Max, [long]$Before)
        $ErrorActionPreference = 'Stop'
        $c = @()
        if ($Levels) { $c += '(' + (($Levels | ForEach-Object { "Level=$_" }) -join ' or ') + ')' }
        if ($Since) { $c += "TimeCreated[@SystemTime>='$Since']" }
        if ($Before -gt 0) { $c += "EventRecordID<$Before" }
        if ($Source) {
            if ($Source.IndexOfAny([char[]]"'`"") -ge 0) { throw 'A source name with a quote can''t be filtered on.' }
            $c += "Provider[@Name='$Source']"
        }
        $x = if ($c) { "*[System[$($c -join ' and ')]]" } else { '*' }
        try { $events = @(Get-WinEvent -LogName $Log -FilterXPath $x -MaxEvents ($Max + 1)) }
        catch { if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') { return }; throw }
        $n = 0
        foreach ($e in $events) {
            if (++$n -gt $Max) { 'more'; break }
            $m = $e.Message; if ($null -eq $m) { $m = '' }
            "$($e.RecordId)`t$($e.TimeCreated.ToUniversalTime().ToString('o'))`t$([int]$e.Level)`t$($e.ProviderName)`t$($e.Id)`t" +
                [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($m))
        }
        """;

    // wevtutil writes the matching events of the log into a file in the account's temp folder; RemoteHub downloads it and
    // deletes it again (DeleteExport).
    private const string ExportScript = """
        param([string]$Log, [string]$Query)
        $ErrorActionPreference = 'Stop'
        $file = Join-Path ([IO.Path]::GetTempPath()) ('RemoteHub-' + [guid]::NewGuid().ToString('N') + '.evtx')
        $out = & wevtutil.exe epl $Log $file "/q:$Query" 2>&1
        if ($LASTEXITCODE -ne 0) { throw (($out | Out-String).Trim()) }
        "$file`t$((Get-Item -LiteralPath $file).Length)"
        """;

    private const string DeleteScript = """
        param([string]$Path)
        if ($Path -like '*\RemoteHub-*.evtx') { Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue }
        """;

    public static List<EventLogInfo> Logs(Runspace rs) => ParseLogs(RemoteFs.Invoke(rs, LogsScript).Select(o => o?.ToString() ?? ""));

    public static List<EventLogInfo> ParseLogs(IEnumerable<string> lines)
    {
        var list = new List<EventLogInfo>();
        foreach (var line in lines)
        {
            var f = line.Split('\t');
            if (f.Length == 2 && f[0].Length > 0) list.Add(new EventLogInfo(f[0], long.TryParse(f[1], out var n) ? n : 0));
        }
        // The four main logs first, as in the Event Viewer's "Windows Logs"; the rest by name.
        return [.. list.OrderBy(l => Array.FindIndex(MainLogs, m => m.Equals(l.Name, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : MainLogs.Length)
                       .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Newest first; <paramref name="before"/> &gt; 0 continues below that record id (Load more).</summary>
    public static EventPage Events(Runspace rs, EventQuery q, long before = 0, int max = PageSize) =>
        ParseEvents(RemoteFs.Invoke(rs, EventsScript, ("Log", q.Log), ("Levels", XPathLevels(q.Levels)),
            ("Since", q.SinceUtc is { } s ? SystemTime(s) : ""), ("Source", q.Source.Trim()), ("Max", max), ("Before", before))
            .Select(o => o?.ToString() ?? ""));

    /// <summary>The levels as the log stores them: Information is 4, and 0 in classic logs.</summary>
    public static int[] XPathLevels(IReadOnlyList<EventLevel> levels) =>
        [.. levels.SelectMany(l => l == EventLevel.Information ? new[] { 0, 4 } : [(int)l]).Distinct().Order()];

    private static string SystemTime(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public static EventPage ParseEvents(IEnumerable<string> lines)
    {
        var list = new List<EventEntry>();
        var more = false;
        foreach (var line in lines)
        {
            if (line == "more") { more = true; continue; }
            var f = line.Split('\t');
            if (f.Length < 6 || !long.TryParse(f[0], out var id)) continue;
            var time = DateTime.TryParse(f[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : default;
            var level = int.TryParse(f[2], out var l) && l is >= 1 and <= 5 ? (EventLevel)l : EventLevel.Information;
            string message;
            try { message = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(f[5])); }
            catch (FormatException) { message = ""; }
            list.Add(new EventEntry(id, time, level, f[3], int.TryParse(f[4], out var eid) ? eid : 0, message.TrimEnd()));
        }
        return new EventPage(list, more);
    }

    /// <summary>The XPath of a query for wevtutil (the same filter as the list, without paging).</summary>
    public static string ExportQuery(EventQuery q)
    {
        var c = new List<string>();
        if (XPathLevels(q.Levels) is { Length: > 0 } levels) c.Add("(" + string.Join(" or ", levels.Select(l => $"Level={l}")) + ")");
        if (q.SinceUtc is { } s) c.Add($"TimeCreated[@SystemTime>='{SystemTime(s)}']");
        if (q.Source.Trim() is { Length: > 0 } source)
        {
            if (source.IndexOfAny(['\'', '"']) >= 0) throw new RemoteCommandException("A source name with a quote can't be filtered on.");
            c.Add($"Provider[@Name='{source}']");
        }
        return c.Count > 0 ? $"*[System[{string.Join(" and ", c)}]]" : "*";
    }

    /// <summary>Writes the matching events to a file on the device; its path and size.</summary>
    public static (string Path, long Size) Export(Runspace rs, EventQuery q)
    {
        var f = (RemoteFs.Invoke(rs, ExportScript, ("Log", q.Log), ("Query", ExportQuery(q))).FirstOrDefault()?.ToString() ?? "").Split('\t');
        if (f.Length != 2 || f[0].Length == 0) throw new RemoteCommandException("wevtutil didn't say where it wrote the file.");
        return (f[0], long.TryParse(f[1], out var n) ? n : 0);
    }

    public static void DeleteExport(Runspace rs, string path) => RemoteFs.Invoke(rs, DeleteScript, ("Path", path));

    public static string LevelName(EventLevel l) => l switch
    {
        EventLevel.Critical => "Critical",
        EventLevel.Error => "Error",
        EventLevel.Warning => "Warning",
        EventLevel.Verbose => "Verbose",
        _ => "Information",
    };
}
