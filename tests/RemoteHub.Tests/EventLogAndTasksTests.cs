using System.Collections.Generic;
using System.Management.Automation.Runspaces;
using RemoteHub.Remoting;

namespace RemoteHub.Tests;

/// <summary>
/// The Event Viewer and Scheduled tasks tabs' remote side: parsing what the device sends, the filters built from what the
/// user chose, and the scripts run against this PC (the same scripts as over WinRM or the agent).
/// </summary>
public class EventLogAndTasksTests
{
    private static Runspace Local() { var rs = RunspaceFactory.CreateRunspace(); rs.Open(); return rs; }

    /// <summary>Windows PowerShell 5.1, which the agent runs on a device; null where it isn't installed.</summary>
    private static Runspace? WindowsPowerShell()
    {
        if (!File.Exists(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))) return null;
        var rs = RunspaceFactory.CreateOutOfProcessRunspace(null, new PowerShellProcessInstance(new Version(5, 1), null, null, false));
        rs.Open();
        return rs;
    }

    [SkippableFact]
    public void Both_tabs_scripts_run_on_Windows_PowerShell_51()
    {
        var rs = WindowsPowerShell();
        Skip.If(rs is null, "Windows PowerShell 5.1 isn't available.");
        using (rs!)
        {
            Assert.Contains("Application", RemoteEventLog.Logs(rs!).Select(l => l.Name));
            var page = RemoteEventLog.Events(rs!, new EventQuery("Application", [EventLevel.Information, EventLevel.Warning, EventLevel.Error], DateTime.UtcNow.AddDays(-365), ""), max: 3);
            Assert.All(page.Entries, e => Assert.True(e.RecordId > 0 && e.TimeUtc.Year > 2000));
            Assert.Throws<RemoteCommandException>(() => RemoteEventLog.Events(rs!, new EventQuery("Application", [], null, "a\"b")));
            var tasks = RemoteTasks.List(rs!);
            Assert.Contains(tasks.Tasks, t => t.Folder.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase));
            var any = tasks.Tasks.First(t => t.Folder.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase));
            Assert.StartsWith("<?xml", RemoteTasks.Xml(rs!, any).TrimStart());
        }
    }

    private static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

    // ------------------------------------------------------------------ event logs

    [Fact]
    public void Messages_with_tabs_pipes_and_new_lines_arrive_whole()
    {
        var message = "Line one | with a pipe\tand a tab\r\nLine two\r\n";
        var page = RemoteEventLog.ParseEvents([
            $"812\t2026-11-01T08:15:30.0000000Z\t2\tService Control Manager\t7031\t{B64(message)}",
            "811\t2026-11-01T08:10:00.0000000Z\t0\tEventLog\t6013\t" + B64("Uptime"),
            "not an event",
            "more",
        ]);
        Assert.True(page.More);
        Assert.Equal(2, page.Entries.Count);
        var e = page.Entries[0];
        Assert.Equal(812, e.RecordId);
        Assert.Equal(new DateTime(2026, 11, 1, 8, 15, 30, DateTimeKind.Utc), e.TimeUtc);
        Assert.Equal(DateTimeKind.Utc, e.TimeUtc.Kind);
        Assert.Equal(EventLevel.Error, e.Level);
        Assert.Equal("Service Control Manager", e.Source);
        Assert.Equal(7031, e.EventId);
        Assert.Equal("Line one | with a pipe\tand a tab\r\nLine two", e.Message);
        Assert.Equal(EventLevel.Information, page.Entries[1].Level); // level 0 of classic logs
    }

    [Fact]
    public void The_main_logs_come_first_then_the_others_by_name()
    {
        var logs = RemoteEventLog.ParseLogs(["Microsoft-Windows-PowerShell/Operational\t120", "System\t5000", "Application\t9000", "Bad line", "Setup\t30", "Windows PowerShell\t40"]);
        Assert.Equal(["Application", "System", "Setup", "Microsoft-Windows-PowerShell/Operational", "Windows PowerShell"], logs.Select(l => l.Name));
        Assert.Equal(9000, logs[0].Count);
    }

    [Fact]
    public void Information_also_matches_level_0_and_the_export_query_has_the_same_filter()
    {
        Assert.Equal([0, 4], RemoteEventLog.XPathLevels([EventLevel.Information]));
        Assert.Equal([1, 2, 3], RemoteEventLog.XPathLevels([EventLevel.Warning, EventLevel.Critical, EventLevel.Error]));
        Assert.Empty(RemoteEventLog.XPathLevels([]));

        var since = new DateTime(2026, 11, 1, 7, 0, 0, DateTimeKind.Utc);
        Assert.Equal("*", RemoteEventLog.ExportQuery(new EventQuery("System", [], null, "")));
        Assert.Equal("*[System[(Level=1 or Level=2) and TimeCreated[@SystemTime>='2026-11-01T07:00:00.000Z'] and Provider[@Name='Disk']]]",
            RemoteEventLog.ExportQuery(new EventQuery("System", [EventLevel.Critical, EventLevel.Error], since, " Disk ")));
        // A quote would end the XPath string (XPath 1.0 has no escape): refused, not escaped.
        Assert.Throws<RemoteCommandException>(() => RemoteEventLog.ExportQuery(new EventQuery("System", [], null, "a']|//*[('")));
        Assert.Throws<RemoteCommandException>(() => RemoteEventLog.ExportQuery(new EventQuery("System", [], null, "a\"b")));
    }

    [SkippableFact]
    public void The_device_reads_a_log_newest_first_and_continues_below_the_last_record()
    {
        using var rs = Local();
        var q = new EventQuery("Application", [], null, "");
        var first = RemoteEventLog.Events(rs, q, max: 5);
        Skip.If(first.Entries.Count < 5, "this PC's Application log has fewer than 6 events");
        Assert.True(first.More);
        Assert.Equal(first.Entries.OrderByDescending(e => e.RecordId).Select(e => e.RecordId), first.Entries.Select(e => e.RecordId));
        var next = RemoteEventLog.Events(rs, q, before: first.Entries.Min(e => e.RecordId), max: 5);
        Assert.All(next.Entries, e => Assert.True(e.RecordId < first.Entries.Min(f => f.RecordId)));

        // A filter that matches nothing is an empty page, not an error.
        var none = RemoteEventLog.Events(rs, q with { Source = "RemoteHub-no-such-source-" + Guid.NewGuid().ToString("N") });
        Assert.Empty(none.Entries);
        Assert.False(none.More);
        // The quote check happens on the device too.
        Assert.Throws<RemoteCommandException>(() => RemoteEventLog.Events(rs, q with { Source = "x' or '1'='1" }));
        Assert.Contains("Application", RemoteEventLog.Logs(rs).Select(l => l.Name));
    }

    // ------------------------------------------------------------------ scheduled tasks

    [Fact]
    public void Task_listings_are_parsed_with_folders_times_and_results()
    {
        var l = RemoteTasks.Parse([
            "F\t\\",
            "F\t\\Microsoft\\Windows\\Defrag\\",
            "T\t\\Microsoft\\Windows\\Defrag\\\tScheduledDefrag\t3\t2026-10-30T01:00:00.0000000Z\t\t-2147216609\tMicrosoft Corporation\tSYSTEM\tOptimises the disks.",
            "T\t\\\tBackup\t1\t\t2026-11-02T22:00:00.0000000Z\t267011\tADMIN\tCONTOSO\\svc-backup\t",
            "T\t\\\t\t3\t\t\t0\t\t\t", // no name: dropped
        ]);
        Assert.Equal(["\\", "\\Microsoft\\Windows\\Defrag\\"], l.Folders);
        Assert.Equal(2, l.Tasks.Count);
        var defrag = l.Tasks[0];
        Assert.Equal("\\Microsoft\\Windows\\Defrag\\ScheduledDefrag", defrag.Path);
        Assert.Equal(TaskState.Ready, defrag.State);
        Assert.Equal(new DateTime(2026, 10, 30, 1, 0, 0, DateTimeKind.Utc), defrag.LastRunUtc);
        Assert.Null(defrag.NextRunUtc);
        Assert.Equal("An instance is already running (0x8004131F)", RemoteTasks.ResultText(defrag.LastResult));
        Assert.Equal(TaskState.Disabled, l.Tasks[1].State);
        Assert.Equal("CONTOSO\\svc-backup", l.Tasks[1].RunAs);
        Assert.Equal("Has not yet run (0x41303)", RemoteTasks.ResultText(l.Tasks[1].LastResult));
        Assert.Equal("0x1", RemoteTasks.ResultText(1));
    }

    [Fact]
    public void RemoteHubs_own_tasks_are_never_ended_or_disabled_and_Windows_tasks_are_named_as_such()
    {
        Assert.True(RemoteTasks.IsProtected(@"\RemoteHub\RemoteHub-Run-1234"));
        Assert.True(RemoteTasks.IsProtected(@"\remotehub\x"));
        Assert.False(RemoteTasks.IsProtected(@"\RemoteHubber\x"));
        Assert.True(RemoteTasks.IsWindows(@"\Microsoft\Windows\Defrag\ScheduledDefrag"));
        Assert.False(RemoteTasks.IsWindows(@"\Backup"));
        var own = new TaskInfo(@"\RemoteHub\", "RemoteHub-Run-1234", TaskState.Running, null, null, 0, "", "SYSTEM", "");
        // Refused before anything is sent: no runspace is needed to see it.
        Assert.Contains("belongs to RemoteHub", Assert.Throws<RemoteCommandException>(() => RemoteTasks.Do(null!, own, TaskAction.End)).Message);
        Assert.Throws<RemoteCommandException>(() => RemoteTasks.Do(null!, own, TaskAction.Disable));
    }

    [Fact]
    public void A_task_path_with_quotes_reaches_the_script_as_a_parameter()
    {
        var canary = Path.Combine(Path.GetTempPath(), "rh-task-canary-" + Guid.NewGuid().ToString("N"));
        using var rs = Local();
        try
        {
            var evil = new TaskInfo(@"\", $"x'; New-Item -ItemType File -Path '{canary}'; '", TaskState.Ready, null, null, 0, "", "", "");
            // The task doesn't exist: the Task Scheduler says so, and the name was never run as code.
            Assert.Throws<RemoteCommandException>(() => RemoteTasks.Do(rs, evil, TaskAction.Run));
            Assert.Throws<RemoteCommandException>(() => RemoteTasks.Xml(rs, evil with { Folder = $"\\'; New-Item -ItemType File -Path '{canary}'; '\\" }));
            Assert.False(File.Exists(canary));
        }
        finally { File.Delete(canary); }
    }

    [Fact]
    public void The_device_lists_its_task_folders_and_tasks()
    {
        using var rs = Local();
        var l = RemoteTasks.List(rs);
        Assert.Contains("\\", l.Folders);
        Assert.Contains(l.Folders, f => f.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(l.Tasks, t => t.Folder.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase));
        Assert.All(l.Tasks, t => Assert.EndsWith("\\", t.Folder));
    }

    [Fact]
    public void Details_say_what_a_task_runs_when_and_as_whom()
    {
        var task = new TaskInfo(@"\", "Backup", TaskState.Ready, null, new DateTime(2026, 11, 2, 22, 0, 0, DateTimeKind.Utc), 0x41303, "ADMIN", @"CONTOSO\svc-backup", "Nightly backup.");
        const string xml = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <CalendarTrigger><StartBoundary>2026-01-01T22:00:00</StartBoundary><Enabled>true</Enabled></CalendarTrigger>
                <BootTrigger><Enabled>false</Enabled></BootTrigger>
              </Triggers>
              <Settings><ExecutionTimeLimit>PT2H</ExecutionTimeLimit><Hidden>true</Hidden></Settings>
              <Actions Context="Author">
                <Exec><Command>C:\Tools\backup.exe</Command><Arguments>/full</Arguments><WorkingDirectory>C:\Tools</WorkingDirectory></Exec>
              </Actions>
            </Task>
            """;
        var text = RemoteTasks.Describe(task, xml.Replace("encoding=\"UTF-16\"", ""));
        Assert.Contains(@"Run as:  CONTOSO\svc-backup", text);
        Assert.Contains(@"Start C:\Tools\backup.exe /full  (in C:\Tools)", text);
        Assert.Contains("On a schedule, from 2026-01-01T22:00:00", text);
        Assert.Contains("At startup  (disabled)", text);
        Assert.Contains("Stop after PT2H", text);
        Assert.Contains("Hidden", text);
        Assert.Contains("Last run:  never", text);
        Assert.EndsWith("Nightly backup.", text);
        // Not XML (an older device, a broken answer): the facts and whatever came.
        Assert.Contains("not xml", RemoteTasks.Describe(task, "not xml"));
    }
}
