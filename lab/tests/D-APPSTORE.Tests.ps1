# D-APPSTORE (spike for Store apps in app tabs, docs/app-tabs-design.md section 6.6; docs/test-lab-cases.md section
# 9.15) - can a Store app be started, held and closed the way the design needs, before any of it is built? Windows as
# it is, no agent:
#   D-APPSTORE-01  the account's shell:AppsFolder lists Store apps by AppUserModelID, and how long the list takes
#   D-APPSTORE-02  IApplicationActivationManager started from a process with no window of its own (as "store-apps"
#                  will be) starts the app and returns its process
#   D-APPSTORE-03  that process can be added to a job of our own
#   D-APPSTORE-04  the app's top-level windows carry its AppUserModelID (PKEY_AppUserModel_ID) - also the frame window
#                  a UWP app's ApplicationFrameHost draws
#   D-APPSTORE-05  WM_CLOSE to those windows closes the app: no window with its AppUserModelID is left
# The stand-in runs as the session's user from a scheduled task, not as the agent's LocalSystem helper, as for
# D-APPSPIKE-06. One UWP app (Calculator, else Settings) and one packaged desktop app (Windows Terminal) when the
# device has it. Findings go to the evidence (d-appstore-*.json) and into the design document once the spike has run.
# LabRole: device
# LabRuns: on request
# TimeLimitMin: 30
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    function Save($name, $data) { $data | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $EvidenceDir "d-appstore-$name.json") -Encoding UTF8 }

    # Start by AppUserModelID, add the process to a job, find the windows with that ID, close them - as the design's
    # "store-apps" process and helper will. C# 5 (Windows PowerShell's Add-Type).
    $script:cs = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class RhStore
{
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
    }
    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] class ApplicationActivationManager { }

    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Fmtid; public uint Pid; }
    [StructLayout(LayoutKind.Sequential)] struct PropVariant { public ushort Vt; public ushort R1, R2, R3; public IntPtr P; public IntPtr P2; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
    }
    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static readonly Guid PkeyFmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"); // PKEY_AppUserModel_ID, 5

    public static string WindowAppId(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        IPropertyStore store;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null) return null;
        try
        {
            var key = new PropertyKey { Fmtid = PkeyFmtid, Pid = 5 };
            PropVariant v;
            store.GetValue(ref key, out v);
            try { return v.Vt == 31 ? Marshal.PtrToStringUni(v.P) : null; } // VT_LPWSTR
            finally { PropVariantClear(ref v); }
        }
        catch (COMException) { return null; }
        finally { Marshal.ReleaseComObject(store); }
    }

    // the visible top-level windows with this AppUserModelID: handle, class, process
    public static List<Dictionary<string, object>> Windows(string appId)
    {
        var found = new List<Dictionary<string, object>>();
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            var id = WindowAppId(h);
            if (!string.Equals(id, appId, StringComparison.OrdinalIgnoreCase)) return true;
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            var cls = new StringBuilder(256); GetClassName(h, cls, 256);
            var title = new StringBuilder(256); GetWindowText(h, title, 256);
            string name = "";
            try { name = Process.GetProcessById((int)pid).ProcessName; } catch (ArgumentException) { }
            found.Add(new Dictionary<string, object> { { "hwnd", h.ToInt64() }, { "class", cls.ToString() }, { "title", title.ToString() }, { "pid", pid }, { "process", name } });
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static Dictionary<string, object> Try(string appId)
    {
        var r = new Dictionary<string, object> { { "appId", appId } };
        var sw = Stopwatch.StartNew();
        uint pid = 0;
        try
        {
            var m = (IApplicationActivationManager)new ApplicationActivationManager();
            m.ActivateApplication(appId, "", 0, out pid);
            r["activated"] = true;
        }
        catch (Exception ex) { r["activated"] = false; r["activateError"] = ex.GetType().Name + " 0x" + Marshal.GetHRForException(ex).ToString("X8"); return r; }
        r["activateMs"] = sw.ElapsedMilliseconds;
        r["pid"] = pid;
        try { r["process"] = Process.GetProcessById((int)pid).ProcessName; } catch (ArgumentException) { r["process"] = ""; }

        // the process into a job of our own (PROCESS_SET_QUOTA | PROCESS_TERMINATE), as the service will
        var job = CreateJobObject(IntPtr.Zero, null);
        var process = OpenProcess(0x0100 | 0x0001 | 0x1000, false, pid);
        if (process == IntPtr.Zero) { r["inJob"] = false; r["jobError"] = "OpenProcess " + Marshal.GetLastWin32Error(); }
        else
        {
            var ok = AssignProcessToJobObject(job, process);
            if (!ok) r["jobError"] = "AssignProcessToJobObject " + Marshal.GetLastWin32Error();
            bool inJob;
            r["inJob"] = IsProcessInJob(process, job, out inJob) && inJob;
            CloseHandle(process);
        }

        // its windows, by AppUserModelID
        List<Dictionary<string, object>> windows = new List<Dictionary<string, object>>();
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            windows = Windows(appId);
            if (windows.Count > 0) break;
            Thread.Sleep(250);
        }
        r["windowMs"] = sw.ElapsedMilliseconds;
        r["windows"] = windows;
        Thread.Sleep(2000); // fully up before it is asked to close

        // closed the way a tab closes its app
        foreach (var w in Windows(appId)) PostMessage(new IntPtr((long)w["hwnd"]), 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
        var closeWatch = Stopwatch.StartNew();
        var left = Windows(appId);
        var closeUntil = DateTime.UtcNow.AddSeconds(20);
        while (left.Count > 0 && DateTime.UtcNow < closeUntil) { Thread.Sleep(250); left = Windows(appId); }
        r["closedMs"] = left.Count == 0 ? (object)closeWatch.ElapsedMilliseconds : null;
        r["left"] = left;
        bool stillRunning = false;
        try { stillRunning = !Process.GetProcessById((int)pid).HasExited; } catch (ArgumentException) { }
        r["processRunningAfterClose"] = stillRunning;
        CloseHandle(job); // no kill-on-close: whatever still runs keeps running
        return r;
    }
}
'@

    $null = Initialize-LabVm -Name $VmName -AutoLogon Console
    Wait-LabDesktop -Name $VmName -Account Console | Out-Null

    $script:s = [ordered]@{}
    # D-APPSTORE-01: shell:AppsFolder as the account sees it; packaged apps are those whose path is an AppUserModelID
    $s.list = Invoke-LabInSessionJson -Name $VmName -Code @'
$sw = [Diagnostics.Stopwatch]::StartNew()
$items = @((New-Object -ComObject Shell.Application).NameSpace('shell:AppsFolder').Items() | ForEach-Object { [pscustomobject]@{ name = $_.Name; id = $_.Path } })
[pscustomobject]@{ ms = $sw.ElapsedMilliseconds; total = $items.Count; store = @($items | Where-Object { $_.id -match '^[A-Za-z0-9._-]+_[a-z0-9]{13}![A-Za-z0-9._-]+$' }) }
'@
    $ids = @($s.list.store | ForEach-Object id)
    $uwp = @('Microsoft.WindowsCalculator_8wekyb3d8bbwe!App', 'windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel') | Where-Object { $ids -contains $_ } | Select-Object -First 1
    $desktop = @('Microsoft.WindowsTerminal_8wekyb3d8bbwe!App') | Where-Object { $ids -contains $_ } | Select-Object -First 1
    $s.uwpId = $uwp
    $s.desktopId = $desktop
    # D-APPSTORE-02..05, per app
    $s.apps = @(foreach ($id in @($uwp, $desktop) | Where-Object { $_ }) {
        Invoke-LabInSessionJson -Name $VmName -Data @{ cs = $script:cs; id = $id } -TimeoutSec 120 -Code @'
Add-Type -TypeDefinition $A.cs
[RhStore]::Try($A.id)
'@
    })
    Save 'store' $s
}

AfterAll {
    # apps left open and their settings: not for the next file
    Set-LabVmDirty -Name $VmName
}

Describe 'D-APPSTORE <VmName>' -Tag AppStoreSpike {

    It 'D-APPSTORE-01 shell:AppsFolder lists Store apps by AppUserModelID' -Tag 'D-APPSTORE-01' {
        @($s.list.store).Count | Should -BeGreaterThan 0 -Because "$($s.list.total) entries, $($s.list.ms) ms"
        $s.uwpId | Should -Not -BeNullOrEmpty -Because 'Calculator or Settings is a Store app on every lab device'
    }

    It 'D-APPSTORE-02 IApplicationActivationManager starts the app from a process without a window' -Tag 'D-APPSTORE-02' {
        foreach ($a in $s.apps) {
            $a.activated | Should -BeTrue -Because "$($a.appId): $($a.activateError)"
            $a.pid | Should -BeGreaterThan 0
        }
    }

    It 'D-APPSTORE-03 the started process can be added to a job of our own' -Tag 'D-APPSTORE-03' {
        foreach ($a in $s.apps) { $a.inJob | Should -BeTrue -Because "$($a.appId) ($($a.process)): $($a.jobError)" }
    }

    It 'D-APPSTORE-04 the app''s windows carry its AppUserModelID, the UWP frame window too' -Tag 'D-APPSTORE-04' {
        foreach ($a in $s.apps) { @($a.windows).Count | Should -BeGreaterThan 0 -Because "a visible window of $($a.appId) within 20 s" }
        $frame = @($s.apps | Where-Object appId -eq $s.uwpId | ForEach-Object { $_.windows } | Where-Object class -eq 'ApplicationFrameWindow')
        $frame.Count | Should -BeGreaterThan 0 -Because "the UWP app's ApplicationFrameHost window has the app's ID ($(@($s.apps | ForEach-Object { $_.windows } | ForEach-Object { "$($_.class)/$($_.process)" }) -join ', '))"
    }

    It 'D-APPSTORE-05 WM_CLOSE to those windows closes the app' -Tag 'D-APPSTORE-05' {
        foreach ($a in $s.apps) { $a.closedMs | Should -Not -BeNullOrEmpty -Because "no window of $($a.appId) is left 20 s after WM_CLOSE ($(@($a.left | ForEach-Object class) -join ', '))" }
    }
}
