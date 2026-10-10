using System.Globalization;
using System.Management.Automation.Runspaces;

namespace RemoteHub.Remoting;

/// <summary>One process as read from the remote computer. <see cref="CpuTicks"/> = kernel + user time in 100 ns units.</summary>
public sealed record ProcessSample(int Id, int ParentId, int SessionId, int Threads, int Handles, long WorkingSet, long PrivateBytes,
                                   long CpuTicks, DateTime? StartedUtc, string Name, string User, string Path, string CommandLine);

/// <summary>All processes at one moment, with what is needed to turn CPU time into a percentage.</summary>
public sealed record ProcessSnapshot(long TakenTicks, int Cpus, long TotalMemory, long FreeMemory, int OwnPid, List<ProcessSample> Processes);

/// <summary>
/// Process list for the processes tab: one CIM query per refresh (Win32_Process), user names when the remoting account
/// may read them (elevated). CPU % is the difference between two refreshes, like Task Manager.
/// </summary>
public static class RemoteProcesses
{
    /// <summary>Processes Windows can't run without – ending them stops (or crashes) the computer, so they're never ended from here.</summary>
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
        { "System", "Idle", "System Idle Process", "Registry", "Memory Compression", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe" };

    public static bool IsCritical(int id, string name) => id is 0 or 4 || Critical.Contains(name);

    // "H" line: remote clock, logical CPUs, memory, PID of this remoting host. Then one tab-separated line per process.
    private const string ListScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        $users = @{}
        try { foreach ($p in Get-Process -IncludeUserName -ErrorAction Stop) { $users[$p.Id] = $p.UserName } } catch { }
        $os = Get-CimInstance Win32_OperatingSystem
        $cpus = [Environment]::ProcessorCount
        "H`t$([DateTime]::UtcNow.Ticks)`t$cpus`t$([long]$os.TotalVisibleMemorySize * 1024)`t$([long]$os.FreePhysicalMemory * 1024)`t$PID"
        foreach ($p in Get-CimInstance Win32_Process) {
            $start = if ($p.CreationDate) { $p.CreationDate.ToUniversalTime().Ticks } else { '' }
            $cpu = [long]$p.KernelModeTime + [long]$p.UserModeTime
            "P`t$($p.ProcessId)`t$($p.ParentProcessId)`t$($p.SessionId)`t$($p.ThreadCount)`t$($p.HandleCount)`t$($p.WorkingSetSize)`t$([long]$p.PrivatePageCount)`t$cpu`t$start`t" +
                (("$($p.Name)`t$($users[[int]$p.ProcessId])`t$($p.ExecutablePath)`t") -replace '[\r\n]', ' ') + ("$($p.CommandLine)" -replace '[\t\r\n]', ' ')
        }
        """;

    private const string StopScript = """
        param([int[]]$Ids, [bool]$Tree)
        $ErrorActionPreference = 'Stop'
        # Still running = the same PID with the same start time: an ended process's PID can be reused at once.
        function Alive($t) {
            $p = Get-Process -Id $t.ProcessId -ErrorAction SilentlyContinue
            if (-not $p) { return }
            try { $s = $p.StartTime } catch { $s = $null }
            if ($null -eq $s -or $null -eq $t.CreationDate -or [Math]::Abs(($s - $t.CreationDate).TotalSeconds) -lt 2) { $p }
        }
        foreach ($id in $Ids) {
            if ($id -eq $PID) { throw 'This is the PowerShell remoting process of this tab.' }
            if (-not $Tree) { Stop-Process -Id $id -Force; continue }

            # The process and everything it started. A child must be younger than its parent (guards against reused PIDs).
            $null = Get-Process -Id $id   # a clear error when it's gone already
            $all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, CreationDate, Name)
            $targets = [System.Collections.Generic.List[object]]::new()
            $queue = [System.Collections.Generic.Queue[object]]::new()
            $all | Where-Object { $_.ProcessId -eq $id } | ForEach-Object { $targets.Add($_); $queue.Enqueue($_) }
            while ($queue.Count) {
                $p = $queue.Dequeue()
                foreach ($c in $all) {
                    if ($c.ParentProcessId -eq $p.ProcessId -and $c.ProcessId -ne $p.ProcessId -and $c.ProcessId -ne $PID -and
                        $c.CreationDate -ge $p.CreationDate -and -not ($targets | Where-Object { $_.ProcessId -eq $c.ProcessId })) {
                        $targets.Add($c); $queue.Enqueue($c)
                    }
                }
            }

            # End the top first (so it can't start new ones), then the rest. Processes that exit on their own meanwhile are fine.
            $failed = @{}
            foreach ($t in $targets) {
                if (-not (Alive $t)) { continue } # gone meanwhile – and never end a newer process that got its PID
                try { Stop-Process -Id $t.ProcessId -Force -ErrorAction Stop }
                catch { if (Alive $t) { $failed[[int]$t.ProcessId] = $_.Exception.Message } }
            }
            @($targets | ForEach-Object { Alive $_ }) | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
            $left = @($targets | ForEach-Object { Alive $_ })
            if ($left.Count) {
                throw ('Could not end: ' + (($left | ForEach-Object {
                    "$($_.ProcessName) (PID $($_.Id))" + $(if ($failed[$_.Id]) { " – $($failed[$_.Id])" })
                }) -join '; '))
            }
        }
        """;

    private static long L(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public static ProcessSnapshot Parse(IEnumerable<string> lines)
    {
        long taken = 0, total = 0, free = 0;
        int cpus = 1, own = 0;
        var list = new List<ProcessSample>();
        foreach (var line in lines)
        {
            var f = line.Split('\t');
            if (f is ["H", var t, var c, var tm, var fm, var pid])
            {
                taken = L(t); cpus = Math.Max(1, I(c)); total = L(tm); free = L(fm); own = I(pid);
            }
            else if (f.Length >= 14 && f[0] == "P")
            {
                var name = f[10];
                list.Add(new ProcessSample(I(f[1]), I(f[2]), I(f[3]), I(f[4]), I(f[5]), L(f[6]), L(f[7]), L(f[8]),
                    f[9].Length > 0 ? new DateTime(L(f[9]), DateTimeKind.Utc) : null,
                    name.Length > 0 ? name : f[1] == "0" ? "System Idle Process" : "(unknown)", f[11], f[12], string.Join(' ', f[13..]).Trim()));
            }
        }
        return new ProcessSnapshot(taken, cpus, total, free, own, list);
    }

    public static ProcessSnapshot List(Runspace rs) => Parse(RemoteFs.Invoke(rs, ListScript).Select(o => o?.ToString() ?? ""));

    /// <summary>Ends processes (with <paramref name="tree"/>: and everything they started). Critical processes are refused.</summary>
    public static void Stop(Runspace rs, IReadOnlyCollection<(int Id, string Name)> processes, bool tree)
    {
        if (processes.FirstOrDefault(p => IsCritical(p.Id, p.Name)) is { Name: not null } c)
            throw new RemoteCommandException($"{c.Name} (PID {c.Id}) is a critical Windows process and can't be ended from here.");
        RemoteFs.Invoke(rs, StopScript, ("Ids", processes.Select(p => p.Id).ToArray()), ("Tree", tree));
    }

    /// <summary>CPU use between two samples of the same process, as a share of all logical CPUs (0–100).</summary>
    public static double CpuPercent(long cpuBefore, long cpuNow, long takenBefore, long takenNow, int cpus)
    {
        var wall = takenNow - takenBefore;
        if (wall <= 0 || cpuNow < cpuBefore) return 0;
        return Math.Clamp((cpuNow - cpuBefore) * 100.0 / (wall * (double)Math.Max(1, cpus)), 0, 100);
    }
}
