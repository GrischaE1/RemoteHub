using RemoteHub.Models;

namespace RemoteHub.Remoting;

/// <summary>Processes tab: the remote computer's processes (refreshed regularly) over PowerShell remoting.</summary>
public sealed class ProcessesSession(Connection connection) : RemoteToolSession(connection)
{
    public override string KindSymbol => "Pulse24";
    public override string KindLabel => "Processes";
    public override Services.RemoteTool Tool => Services.RemoteTool.Processes;

    public Task<ProcessSnapshot> ListAsync() => Run(RemoteProcesses.List);
    public Task StopAsync(IReadOnlyCollection<(int Id, string Name)> processes, bool tree) =>
        Run(rs => { RemoteProcesses.Stop(rs, processes, tree); return true; });
}

/// <summary>Services tab: list, start / stop / restart and startup type of the remote computer's services.</summary>
public sealed class ServicesSession(Connection connection) : RemoteToolSession(connection)
{
    public override string KindSymbol => "WrenchScrewdriver24";
    public override string KindLabel => "Services";
    public override Services.RemoteTool Tool => Services.RemoteTool.Services;

    public Task<List<ServiceInfo>> ListAsync() => Run(RemoteServices.List);
    public Task ControlAsync(string name, ServiceAction action) => Run(rs => { RemoteServices.Control(rs, name, action); return true; });
    public Task SetStartupAsync(string name, ServiceStartup startup) => Run(rs => { RemoteServices.SetStartup(rs, name, startup); return true; });
}

/// <summary>Event Viewer tab: the device's event logs, read-only, filtered on the device and read 500 at a time.</summary>
public sealed class EventLogSession(Connection connection) : RemoteToolSession(connection)
{
    public override string KindSymbol => "DocumentBulletList24";
    public override string KindLabel => "Event Viewer";
    public override Services.RemoteTool Tool => Services.RemoteTool.EventLog;

    public Task<List<EventLogInfo>> LogsAsync() => Run(RemoteEventLog.Logs);
    public Task<EventPage> EventsAsync(EventQuery query, long before) => Run(rs => RemoteEventLog.Events(rs, query, before));
    public Task<(string Path, long Size)> ExportAsync(EventQuery query) => Run(rs => RemoteEventLog.Export(rs, query));
    public Task DeleteExportAsync(string path) => Run(rs => { RemoteEventLog.DeleteExport(rs, path); return true; });
}

/// <summary>Scheduled tasks tab: the device's task folders and tasks; run, end, enable and disable.</summary>
public sealed class ScheduledTasksSession(Connection connection) : RemoteToolSession(connection)
{
    public override string KindSymbol => "CalendarClock24";
    public override string KindLabel => "Scheduled tasks";
    public override Services.RemoteTool Tool => Services.RemoteTool.ScheduledTasks;

    public Task<TaskListing> ListAsync() => Run(RemoteTasks.List);
    public Task DoAsync(TaskInfo task, TaskAction action) => Run(rs => { RemoteTasks.Do(rs, task, action); return true; });
    public Task<string> XmlAsync(TaskInfo task) => Run(rs => RemoteTasks.Xml(rs, task));
}
