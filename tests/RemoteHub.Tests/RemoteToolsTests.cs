using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>
/// Processes and services tabs: parsing, CPU %, safety checks, and the scripts against a local runspace and against
/// Windows PowerShell 5.1 (what servers run) over the remoting protocol. Only processes started by the test are ended.
/// </summary>
public class RemoteToolsTests
{
    private static Runspace Local() { var rs = RunspaceFactory.CreateRunspace(); rs.Open(); return rs; }

    private static Runspace? WindowsPowerShell()
    {
        if (!File.Exists(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))) return null;
        var rs = RunspaceFactory.CreateOutOfProcessRunspace(null, new PowerShellProcessInstance(new Version(5, 1), null, null, false));
        rs.Open();
        return rs;
    }

    [Fact]
    public void Process_lines_are_parsed()
    {
        var start = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var snap = RemoteProcesses.Parse([
            "H\t1000\t8\t17179869184\t4294967296\t4242",
            $"P\t4\t0\t0\t200\t3000\t1024\t2048\t50000\t{start.Ticks}\tSystem\t\t\t",
            "P\t1234\t4\t1\t12\t300\t4096\t8192\t70000\t\tapp.exe\tCONTOSO\\[REDACTED]\tC:\\App\\app.exe\t\"C:\\App\\app.exe\" -x\tafter tab",
            "garbage",
        ]);
        Assert.Equal(1000, snap.TakenTicks);
        Assert.Equal(8, snap.Cpus);
        Assert.Equal(17179869184, snap.TotalMemory);
        Assert.Equal(4242, snap.OwnPid);
        Assert.Equal(2, snap.Processes.Count);
        Assert.Equal(start, snap.Processes[0].StartedUtc);
        var app = snap.Processes[1];
        Assert.Equal((1234, 4, 1, 12, 300, 4096L, 8192L, 70000L), (app.Id, app.ParentId, app.SessionId, app.Threads, app.Handles, app.WorkingSet, app.PrivateBytes, app.CpuTicks));
        Assert.Null(app.StartedUtc);
        Assert.Equal("\"C:\\App\\app.exe\" -x after tab", app.CommandLine);
    }

    [Fact]
    public void Cpu_percent_is_the_share_of_all_cpus_between_two_samples()
    {
        // 1 s of CPU time in 1 s on 4 CPUs = 25 %.
        Assert.Equal(25, RemoteProcesses.CpuPercent(0, TimeSpan.TicksPerSecond, 0, TimeSpan.TicksPerSecond, 4), 3);
        Assert.Equal(0, RemoteProcesses.CpuPercent(10, 5, 0, 100, 1));  // counter went backwards (PID reused)
        Assert.Equal(0, RemoteProcesses.CpuPercent(0, 5, 100, 100, 1)); // no time passed
        Assert.Equal(100, RemoteProcesses.CpuPercent(0, 1000, 0, 10, 1));
    }

    [Fact]
    public void Critical_processes_are_never_ended()
    {
        Assert.True(RemoteProcesses.IsCritical(4, "System"));
        Assert.True(RemoteProcesses.IsCritical(612, "CSRSS.EXE"));
        Assert.True(RemoteProcesses.IsCritical(0, "anything"));
        Assert.False(RemoteProcesses.IsCritical(5000, "notepad.exe"));
        // Refused before anything is sent to the remote computer.
        var ex = Assert.Throws<RemoteCommandException>(() => RemoteProcesses.Stop(null!, [(700, "lsass.exe")], tree: false));
        Assert.Contains("critical", ex.Message);
    }

    [Fact]
    public void Service_lines_are_parsed()
    {
        var list = RemoteServices.Parse([
            "Spooler\tPrint Spooler\tRunning\tAuto\t0\tLocalSystem\t2000\t1\tC:\\Windows\\System32\\spoolsv.exe\tLoads files\tto memory",
            "wuauserv\tWindows Update\tStopped\tAuto\t1\tLocalSystem\t0\t0\tC:\\x.exe -k netsvcs\t",
            "x\tX\tStart Pending\tManual\t0\t\t0\t0\t\t",
            "dis\t\tStopped\tDisabled\t0\t\t0\t0\t\t",
            "drv\tDriver\tRunning\tBoot\t0\t\t0\t1\t\t",
            "too\tfew",
        ]);
        Assert.Equal(5, list.Count);
        Assert.Equal(ServiceStartup.Automatic, list[0].Startup);
        Assert.True(list[0].IsRunning && list[0].CanStop);
        Assert.Equal(2000, list[0].ProcessId);
        Assert.Equal("Loads files to memory", list[0].Description);
        Assert.Equal(ServiceStartup.AutomaticDelayed, list[1].Startup);
        Assert.Equal("Starting", RemoteServices.StateName(list[2].State));
        Assert.Equal("dis", list[3].DisplayName);
        Assert.Equal(ServiceStartup.Other, list[4].Startup);
        Assert.Throws<RemoteCommandException>(() => RemoteServices.SetStartup(null!, "x", ServiceStartup.Other));
    }

    [Fact]
    public void Refresh_intervals_default_to_15_and_60_seconds_and_are_saved()
    {
        var data = new AppData();
        Assert.Equal(15, data.Tools.ProcessRefreshSeconds);
        Assert.Equal(60, data.Tools.ServiceRefreshSeconds);
        data.Tools.ProcessRefreshSeconds = 5;
        data.Tools.ServiceRefreshSeconds = 0;
        var back = SettingsStore.FromJson(SettingsStore.ToJson(data));
        Assert.Equal(5, back.Tools.ProcessRefreshSeconds);
        Assert.Equal(0, back.Tools.ServiceRefreshSeconds);
        // Settings files from older versions have no "Tools" section.
        var old = SettingsStore.FromJson("{\"Version\":1,\"Items\":[]}"u8.ToArray());
        Assert.Equal(15, old.Tools.ProcessRefreshSeconds);
        Assert.Equal(60, old.Tools.ServiceRefreshSeconds);
    }

    [Fact]
    public void Refresh_intervals_are_edited_in_global_defaults_only()
    {
        var data = new AppData();
        var vm = new RemoteHub.ViewModels.EditorViewModel(null, data, isNew: false);
        Assert.Equal(2, vm.ToolRows.Count);
        Assert.Equal("15 s  (default)", vm.ToolRows[0].Selected.Label);
        Assert.Equal("1 min  (default)", vm.ToolRows[1].Selected.Label);
        vm.ToolRows[0].Selected = vm.ToolRows[0].Choices.Single(c => Equals(c.Value, 30));
        vm.ToolRows[1].Selected = vm.ToolRows[1].Choices.Single(c => Equals(c.Value, 0));
        Assert.True(vm.Apply());
        Assert.Equal(30, data.Tools.ProcessRefreshSeconds);
        Assert.Equal(0, data.Tools.ServiceRefreshSeconds);
        Assert.Empty(new RemoteHub.ViewModels.EditorViewModel(new Folder { Name = "F" }, data, isNew: false).ToolRows);
    }

    [Fact]
    public void Lists_processes_and_services_locally() => ListBoth(Local(), ownProcessVisible: true);

    [Fact]
    public void Lists_processes_and_services_in_Windows_PowerShell_51()
    {
        if (WindowsPowerShell() is not { } rs) return;
        ListBoth(rs, ownProcessVisible: false);
    }

    private static void ListBoth(Runspace rs, bool ownProcessVisible)
    {
        using (rs)
        {
            var snap = RemoteProcesses.List(rs);
            Assert.True(snap.Cpus >= 1);
            Assert.True(snap.TotalMemory > snap.FreeMemory && snap.FreeMemory > 0);
            Assert.True(snap.Processes.Count > 10);
            Assert.Contains(snap.Processes, p => p.Id == snap.OwnPid);
            if (ownProcessVisible) Assert.Contains(snap.Processes, p => p.Id == Environment.ProcessId && p.CpuTicks > 0 && p.StartedUtc is not null);
            Assert.Contains(snap.Processes, p => p.Id == 4);
            Assert.True(snap.Processes.Count(p => p.CommandLine.Length > 0) > 0);

            var services = RemoteServices.List(rs);
            Assert.True(services.Count > 20);
            var eventLog = services.Single(s => s.Name.Equals("EventLog", StringComparison.OrdinalIgnoreCase));
            Assert.True(eventLog.IsRunning);
            Assert.Equal(ServiceStartup.Automatic, eventLog.Startup);
            Assert.True(eventLog.ProcessId > 0);
            Assert.False(string.IsNullOrEmpty(eventLog.Description));

            // Control scripts run and report errors clearly (a service that doesn't exist – nothing real is changed).
            var missing = "RdpmNoSuchService" + Guid.NewGuid().ToString("N")[..8];
            Assert.Throws<RemoteCommandException>(() => RemoteServices.Control(rs, missing, ServiceAction.Restart));
            Assert.Throws<RemoteCommandException>(() => RemoteServices.SetStartup(rs, missing, ServiceStartup.Manual));
        }
    }

    private static Process Sleeper(string file, string args) => Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false })!;

    [Fact]
    public void Ends_a_process_and_a_process_tree_in_Windows_PowerShell_51()
    {
        if (WindowsPowerShell() is not { } rs) return;
        using (rs)
        {
            var one = Sleeper("ping.exe", "-n 120 127.0.0.1");
            RemoteProcesses.Stop(rs, [(one.Id, "PING.EXE")], tree: false);
            Assert.True(one.WaitForExit(10_000));

            // cmd.exe starts ping.exe: the tree option ends both.
            var parent = Sleeper("cmd.exe", "/c ping.exe -n 120 127.0.0.1");
            ProcessSample? child = null;
            for (var i = 0; i < 20 && child is null; i++) // wait until cmd.exe has started ping.exe (slow on CI runners)
            {
                System.Threading.Thread.Sleep(500);
                child = RemoteProcesses.List(rs).Processes.SingleOrDefault(p => p.ParentId == parent.Id && p.Name.Equals("ping.exe", StringComparison.OrdinalIgnoreCase));
            }
            Assert.NotNull(child);
            RemoteProcesses.Stop(rs, [(parent.Id, "cmd.exe")], tree: true);
            Assert.True(parent.WaitForExit(10_000));
            try { using var c = Process.GetProcessById(child.Id); Assert.True(c.WaitForExit(10_000)); } catch (ArgumentException) { /* already gone */ }

            // A process that is gone already: a clear error, nothing else happens.
            Assert.Throws<RemoteCommandException>(() => RemoteProcesses.Stop(rs, [(one.Id, "PING.EXE")], tree: false));
        }
    }
}
