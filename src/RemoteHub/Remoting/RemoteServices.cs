using System.Management.Automation.Runspaces;

namespace RemoteHub.Remoting;

/// <summary>Startup types as the Services console shows them.</summary>
public enum ServiceStartup { Automatic, AutomaticDelayed, Manual, Disabled, Other }

/// <summary>One Windows service (Win32_Service).</summary>
public sealed record ServiceInfo(string Name, string DisplayName, string State, ServiceStartup Startup, string Account, int ProcessId,
                                 string Path, string Description, bool CanStop)
{
    public bool IsRunning => State == "Running";
    public bool IsStopped => State == "Stopped";
}

public enum ServiceAction { Start, Stop, Restart }

/// <summary>Service list and control for the services tab. Start/stop wait for the service (at most 60 s) and report what happened.</summary>
public static class RemoteServices
{
    public static string StartupName(ServiceStartup s) => s switch
    {
        ServiceStartup.Automatic => "Automatic",
        ServiceStartup.AutomaticDelayed => "Automatic (delayed start)",
        ServiceStartup.Manual => "Manual",
        ServiceStartup.Disabled => "Disabled",
        _ => "Other (boot / system)",
    };

    public static string StateName(string state) => state switch
    {
        "StartPending" or "Start Pending" => "Starting",
        "StopPending" or "Stop Pending" => "Stopping",
        "ContinuePending" or "Continue Pending" => "Continuing",
        "PausePending" or "Pause Pending" => "Pausing",
        _ => state,
    };

    private const string ListScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        foreach ($s in Get-CimInstance Win32_Service) {
            ("$($s.Name)`t$($s.DisplayName)`t$($s.State)`t$($s.StartMode)`t$([int][bool]$s.DelayedAutoStart)`t$($s.StartName)`t$($s.ProcessId)`t$([int][bool]$s.AcceptStop)`t$($s.PathName)`t" -replace '[\r\n]', ' ') +
                ("$($s.Description)" -replace '[\t\r\n]', ' ')
        }
        """;

    // ServiceController.Stop() stops running dependent services first (as the Services console asks to).
    private const string ControlScript = """
        param([string]$Name, [string]$Action, [int]$Timeout = 60)
        $ErrorActionPreference = 'Stop'
        $s = Get-Service -Name ([Management.Automation.WildcardPattern]::Escape($Name))
        $t = [TimeSpan]::FromSeconds($Timeout)
        function Wait([string]$state) {
            try { $s.WaitForStatus($state, $t) }
            catch { $s.Refresh(); throw "$($s.DisplayName) did not reach '$state' within $Timeout s (now: $($s.Status))." }
        }
        if ($Action -ne 'Start' -and $s.Status -ne 'Stopped') {
            if (-not $s.CanStop -and $s.Status -eq 'Running') { throw "$($s.DisplayName) can't be stopped (it doesn't accept stop requests)." }
            $s.Stop(); Wait 'Stopped'
        }
        if ($Action -ne 'Stop' -and $s.Status -ne 'Running') {
            if ($s.StartType -eq 'Disabled') { throw "$($s.DisplayName) is disabled – change its startup type first." }
            $s.Start(); Wait 'Running'
        }
        """;

    private const string StartupScript = """
        param([string]$Name, [string]$Mode)
        $ErrorActionPreference = 'Stop'
        $out = & sc.exe config $Name start= $Mode 2>&1
        if ($LASTEXITCODE -ne 0) { throw (($out | Out-String).Trim()) }
        """;

    public static List<ServiceInfo> Parse(IEnumerable<string> lines)
    {
        var list = new List<ServiceInfo>();
        foreach (var line in lines)
        {
            var f = line.Split('\t');
            if (f.Length < 10 || f[0].Length == 0) continue;
            var startup = f[3] switch
            {
                "Auto" => f[4] == "1" ? ServiceStartup.AutomaticDelayed : ServiceStartup.Automatic,
                "Manual" => ServiceStartup.Manual,
                "Disabled" => ServiceStartup.Disabled,
                _ => ServiceStartup.Other,
            };
            list.Add(new ServiceInfo(f[0], f[1].Length > 0 ? f[1] : f[0], f[2], startup, f[5], int.TryParse(f[6], out var pid) ? pid : 0,
                f[8], string.Join(' ', f[9..]).Trim(), f[7] == "1"));
        }
        return list;
    }

    public static List<ServiceInfo> List(Runspace rs) => Parse(RemoteFs.Invoke(rs, ListScript).Select(o => o?.ToString() ?? ""));

    public static void Control(Runspace rs, string name, ServiceAction action) =>
        RemoteFs.Invoke(rs, ControlScript, ("Name", name), ("Action", action.ToString()));

    public static void SetStartup(Runspace rs, string name, ServiceStartup startup)
    {
        var mode = startup switch
        {
            ServiceStartup.Automatic => "auto",
            ServiceStartup.AutomaticDelayed => "delayed-auto",
            ServiceStartup.Manual => "demand",
            ServiceStartup.Disabled => "disabled",
            _ => throw new RemoteCommandException("Choose Automatic, Automatic (delayed start), Manual or Disabled."),
        };
        RemoteFs.Invoke(rs, StartupScript, ("Name", name), ("Mode", mode));
    }
}
