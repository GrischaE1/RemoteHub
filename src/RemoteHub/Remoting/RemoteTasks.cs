using System.Globalization;
using System.Management.Automation.Runspaces;
using System.Xml.Linq;

namespace RemoteHub.Remoting;

/// <summary>Task states as the Task Scheduler names them.</summary>
public enum TaskState { Unknown, Disabled, Queued, Ready, Running }

/// <summary>One scheduled task. <see cref="Folder"/> is its folder ("\", "\Microsoft\Windows\Defrag\").</summary>
public sealed record TaskInfo(string Folder, string Name, TaskState State, DateTime? LastRunUtc, DateTime? NextRunUtc, int LastResult,
                              string Author, string RunAs, string Description)
{
    public string Path => Folder + Name;
}

/// <summary>The device's task folders and tasks.</summary>
public sealed record TaskListing(List<string> Folders, List<TaskInfo> Tasks);

public enum TaskAction { Run, End, Enable, Disable }

/// <summary>
/// Scheduled tasks for the Scheduled tasks tab, through the Task Scheduler's COM interface (one pass over every folder,
/// hidden tasks included – Get-ScheduledTaskInfo per task takes seconds on a server). Run, end, enable and disable;
/// nothing is created, changed or deleted.
/// </summary>
public static class RemoteTasks
{
    /// <summary>RemoteHub's own tasks (script runs): never ended or disabled from the tab.</summary>
    public const string OwnFolder = @"\RemoteHub\";

    public static bool IsProtected(string path) => path.StartsWith(OwnFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>Windows' own tasks: changing them says so in the confirmation.</summary>
    public static bool IsWindows(string path) => path.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase);

    private const string ListScript = """
        $ErrorActionPreference = 'Stop'
        $s = New-Object -ComObject Schedule.Service
        $s.Connect()
        function Time($t) { if ($t -and $t.Year -gt 1900) { $t.ToUniversalTime().ToString('o') } else { '' } }
        function Clean([string]$v) { $v -replace '[\t\r\n]', ' ' }
        $queue = New-Object System.Collections.Queue
        $queue.Enqueue($s.GetFolder('\'))
        while ($queue.Count -gt 0) {
            $f = $queue.Dequeue()
            $p = $f.Path; if (-not $p.EndsWith('\')) { $p += '\' }
            "F`t$p"
            try { foreach ($sub in $f.GetFolders(0)) { $queue.Enqueue($sub) } } catch { }
            $tasks = @(); try { $tasks = @($f.GetTasks(1)) } catch { }
            foreach ($t in $tasks) {
                $d = $null; try { $d = $t.Definition } catch { }
                $runAs = ''; $author = ''; $text = ''
                if ($d) {
                    $runAs = if ($d.Principal.UserId) { $d.Principal.UserId } elseif ($d.Principal.GroupId) { $d.Principal.GroupId } else { '' }
                    $author = $d.RegistrationInfo.Author; $text = $d.RegistrationInfo.Description
                }
                $last = ''; $next = ''; $result = 0
                try { $last = Time $t.LastRunTime; $next = Time $t.NextRunTime; $result = $t.LastTaskResult } catch { }
                "T`t$p`t$(Clean $t.Name)`t$([int]$t.State)`t$last`t$next`t$result`t$(Clean $author)`t$(Clean $runAs)`t$(Clean $text)"
            }
        }
        """;

    // The task is found by its folder and name, both parameters: nothing from the device's list is put into a script.
    private const string ActionScript = """
        param([string]$Folder, [string]$Name, [string]$Action)
        $ErrorActionPreference = 'Stop'
        $s = New-Object -ComObject Schedule.Service
        $s.Connect()
        $dir = $Folder.TrimEnd('\'); if (-not $dir) { $dir = '\' } # GetFolder takes no trailing backslash but the root's
        $t = $s.GetFolder($dir).GetTask($Name)
        switch ($Action) {
            'Run' { [void]$t.Run($null) }
            'End' { $t.Stop(0) }
            'Enable' { $t.Enabled = $true }
            'Disable' { $t.Enabled = $false }
        }
        """;

    private const string XmlScript = """
        param([string]$Folder, [string]$Name)
        $ErrorActionPreference = 'Stop'
        $s = New-Object -ComObject Schedule.Service
        $s.Connect()
        $dir = $Folder.TrimEnd('\'); if (-not $dir) { $dir = '\' }
        $s.GetFolder($dir).GetTask($Name).Xml
        """;

    public static TaskListing List(Runspace rs) => Parse(RemoteFs.Invoke(rs, ListScript).Select(o => o?.ToString() ?? ""));

    public static TaskListing Parse(IEnumerable<string> lines)
    {
        var folders = new List<string>();
        var tasks = new List<TaskInfo>();
        foreach (var line in lines)
        {
            var f = line.Split('\t');
            if (f is ["F", { Length: > 0 } folder]) { folders.Add(folder); continue; }
            if (f.Length < 10 || f[0] != "T" || f[2].Length == 0) continue;
            tasks.Add(new TaskInfo(f[1], f[2], int.TryParse(f[3], out var st) && st is >= 0 and <= 4 ? (TaskState)st : TaskState.Unknown,
                Time(f[4]), Time(f[5]), int.TryParse(f[6], out var r) ? r : 0, f[7], f[8], string.Join(' ', f[9..]).Trim()));
        }
        return new TaskListing(folders, tasks);
    }

    private static DateTime? Time(string s) =>
        s.Length > 0 && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    public static void Do(Runspace rs, TaskInfo task, TaskAction action)
    {
        if (action is TaskAction.End or TaskAction.Disable && IsProtected(task.Path))
            throw new RemoteCommandException($"{task.Path} belongs to RemoteHub and can't be ended or disabled here.");
        RemoteFs.Invoke(rs, ActionScript, ("Folder", task.Folder), ("Name", task.Name), ("Action", action.ToString()));
    }

    public static string Xml(Runspace rs, TaskInfo task) =>
        RemoteFs.Invoke(rs, XmlScript, ("Folder", task.Folder), ("Name", task.Name)).FirstOrDefault()?.ToString() ?? "";

    public static string StateName(TaskState s) => s switch
    {
        TaskState.Disabled => "Disabled",
        TaskState.Queued => "Queued",
        TaskState.Ready => "Ready",
        TaskState.Running => "Running",
        _ => "Unknown",
    };

    /// <summary>The last result as the Task Scheduler shows it: a known code by name, any other in hex.</summary>
    public static string ResultText(int code) => unchecked((uint)code) switch
    {
        0 => "The operation completed successfully. (0x0)",
        0x41300 => "Ready to run (0x41300)",
        0x41301 => "Running (0x41301)",
        0x41302 => "Disabled (0x41302)",
        0x41303 => "Has not yet run (0x41303)",
        0x41306 => "Ended by the user (0x41306)",
        0x8004131F => "An instance is already running (0x8004131F)",
        var u => $"0x{u:X}",
    };

    /// <summary>The task's definition as readable text: what it runs, when, as whom, and its main settings.</summary>
    public static string Describe(TaskInfo task, string xml)
    {
        var lines = new List<string> { $"Path:  {task.Path}", $"Status:  {StateName(task.State)}", $"Run as:  {task.RunAs}" };
        if (task.Author.Length > 0) lines.Add($"Author:  {task.Author}");
        lines.Add($"Last run:  {Local(task.LastRunUtc)}  ·  result {ResultText(task.LastResult)}");
        lines.Add($"Next run:  {Local(task.NextRunUtc)}");
        XElement root;
        try { root = XElement.Parse(xml); }
        catch (System.Xml.XmlException) { return string.Join('\n', lines) + (xml.Length > 0 ? "\n\n" + xml : ""); }
        XNamespace ns = root.Name.Namespace;
        var actions = root.Element(ns + "Actions")?.Elements().ToList() ?? [];
        if (actions.Count > 0)
        {
            lines.Add("");
            lines.Add("Actions:");
            foreach (var a in actions)
                lines.Add("  " + (a.Name.LocalName == "Exec"
                    ? $"Start {a.Element(ns + "Command")?.Value} {a.Element(ns + "Arguments")?.Value}".TrimEnd() +
                      (a.Element(ns + "WorkingDirectory")?.Value is { Length: > 0 } wd ? $"  (in {wd})" : "")
                    : a.Name.LocalName));
        }
        var triggers = root.Element(ns + "Triggers")?.Elements().ToList() ?? [];
        lines.Add("");
        lines.Add(triggers.Count > 0 ? "Triggers:" : "Triggers:  none (started by hand or by a program)");
        foreach (var t in triggers)
        {
            var start = t.Element(ns + "StartBoundary")?.Value;
            var off = t.Element(ns + "Enabled")?.Value == "false" ? "  (disabled)" : "";
            lines.Add($"  {Trigger(t.Name.LocalName)}{(start is { Length: > 0 } ? $", from {start}" : "")}{off}");
        }
        if (root.Element(ns + "Settings") is { } s)
        {
            lines.Add("");
            lines.Add("Settings:");
            if (s.Element(ns + "ExecutionTimeLimit")?.Value is { Length: > 0 } limit) lines.Add($"  Stop after {limit}");
            if (s.Element(ns + "MultipleInstancesPolicy")?.Value is { Length: > 0 } multi) lines.Add($"  If already running: {multi}");
            if (s.Element(ns + "Hidden")?.Value == "true") lines.Add("  Hidden");
            if (s.Element(ns + "RunOnlyIfNetworkAvailable")?.Value == "true") lines.Add("  Only with a network connection");
        }
        if (task.Description.Length > 0) { lines.Add(""); lines.Add(task.Description); }
        return string.Join('\n', lines);
    }

    private static string Local(DateTime? utc) => utc is { } t ? t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "never";

    private static string Trigger(string element) => element switch
    {
        "TimeTrigger" => "Once",
        "CalendarTrigger" => "On a schedule",
        "BootTrigger" => "At startup",
        "LogonTrigger" => "At sign-in",
        "IdleTrigger" => "When the computer is idle",
        "EventTrigger" => "On an event",
        "RegistrationTrigger" => "When the task is created or changed",
        "SessionStateChangeTrigger" => "On a session change (lock, unlock, connect)",
        "WnfStateChangeTrigger" => "On a system state change",
        _ => element,
    };
}
