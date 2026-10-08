using RemoteHub.Models;

namespace RemoteHub.Remoting;

/// <summary>Processes tab: the remote computer's processes (refreshed regularly) over PowerShell remoting.</summary>
public sealed class ProcessesSession(Connection connection) : RemoteToolSession(connection)
{
    public override string KindSymbol => "Pulse24";
    public override string KindLabel => "Processes";

    public Task<ProcessSnapshot> ListAsync() => Run(RemoteProcesses.List);
    public Task StopAsync(IReadOnlyCollection<(int Id, string Name)> processes, bool tree) =>
        Run(rs => { RemoteProcesses.Stop(rs, processes, tree); return true; });
}

/// <summary>Services tab: list, start / stop / restart and startup type of the remote computer's services.</summary>
public sealed class ServicesSession(Connection connection) : RemoteToolSession(connection)
{
    public override string KindSymbol => "WrenchScrewdriver24";
    public override string KindLabel => "Services";

    public Task<List<ServiceInfo>> ListAsync() => Run(RemoteServices.List);
    public Task ControlAsync(string name, ServiceAction action) => Run(rs => { RemoteServices.Control(rs, name, action); return true; });
    public Task SetStartupAsync(string name, ServiceStartup startup) => Run(rs => { RemoteServices.SetStartup(rs, name, startup); return true; });
}
