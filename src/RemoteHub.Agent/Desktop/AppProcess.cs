using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RemoteHub.Agent;

/// <summary>
/// The program of an app tab, started by the service (LocalSystem) in the admin's own session as the admin
/// (app-tabs-design §6.1): their token from <c>WTSQueryUserToken</c>, checked against the account that signed in to the
/// agent; their environment; <c>winsta0\default</c>; started directly, never through the shell; no handles inherited.
/// It runs in a job of its own, so the tab knows the processes it started (§6.2). The job doesn't end them when it is
/// closed: closing a tab asks the app to close (§4.3).
/// </summary>
internal sealed class AppProcess : IDisposable
{
    private IntPtr _process, _job;

    private AppProcess(IntPtr process, IntPtr job, int id)
    {
        _process = process;
        _job = job;
        Id = id;
    }

    /// <summary>The started process's id.</summary>
    public int Id { get; }

    /// <summary>The tab's job: the started program and everything it started.</summary>
    public IntPtr Job => _job;

    /// <summary>The started process ended (the job may still hold processes it started).</summary>
    public bool Exited => _process == IntPtr.Zero || Native.WaitForSingleObject(_process, 0) == 0;

    /// <summary>
    /// The token of the user signed in to <paramref name="session"/> when that is <paramref name="accountSid"/> (the account
    /// that signed in to the agent), else null – an app tab is never started in someone else's session. Call as LocalSystem.
    /// </summary>
    public static SafeAccessTokenHandle? Token(int session, string accountSid)
    {
        if (!WTSQueryUserToken((uint)session, out var raw)) return null;
        var token = new SafeAccessTokenHandle(raw);
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        if (string.Equals(identity.User?.Value, accountSid, StringComparison.OrdinalIgnoreCase)) return token;
        token.Dispose();
        return null;
    }

    /// <summary>Expands environment variables in <paramref name="text"/> as the user of <paramref name="token"/> has them (unchanged when it can't).</summary>
    public static string Expand(SafeAccessTokenHandle token, string text)
    {
        var buffer = new char[AppTabs.MaxCommandLine + 1];
        return ExpandEnvironmentStringsForUserW(token.DangerousGetHandle(), text, buffer, buffer.Length)
            ? new string(buffer, 0, Array.IndexOf(buffer, '\0') is var end and >= 0 ? end : buffer.Length)
            : text;
    }

    /// <summary>A new, unguessable name for a tab's job, which the helper opens to tell the app's windows (query access only).</summary>
    public static string NewJobName() => @"Global\RemoteHub-AppTab-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Starts <paramref name="launch"/> as the user of <paramref name="token"/> – with their full administrator token when
    /// it is to run elevated (no UAC prompt: the service holds SeTcbPrivilege; §6.1). Throws when it can't, also when an
    /// elevated start is asked for an account that isn't an administrator here.
    /// </summary>
    public static AppProcess Start(SafeAccessTokenHandle token, AppTabs.Launch launch, string jobName)
    {
        var user = token.DangerousGetHandle();
        var linked = IntPtr.Zero;
        var environment = IntPtr.Zero;
        try
        {
            if (launch.Elevated)
            {
                if (!Native.GetTokenInformation(user, Native.TokenElevationType, out int type, sizeof(int), out _)) throw new Win32Exception();
                if (type == Native.TokenElevationTypeLimited)
                {
                    if (!Native.GetTokenInformation(user, Native.TokenLinkedToken, out linked, IntPtr.Size, out _)) throw new Win32Exception();
                    user = linked;
                }
                else if (type == TokenElevationTypeDefault && !IsAdministrator(token))
                    throw new InvalidOperationException("Your account isn't an administrator on this device – the app can't be started as administrator.");
            }
            if (!Native.CreateEnvironmentBlock(out environment, user, false)) environment = IntPtr.Zero;
            var si = new Native.STARTUPINFOEX
            {
                StartupInfo = new Native.STARTUPINFO { cb = Marshal.SizeOf<Native.STARTUPINFO>(), lpDesktop = @"winsta0\default" },
            };
            // CreateProcessW may write to the command line: pass a buffer, not a string.
            if (!Native.CreateProcessAsUser(user, launch.Program, new StringBuilder(launch.CommandLine, launch.CommandLine.Length + 1), IntPtr.Zero, IntPtr.Zero, false,
                    Native.CREATE_UNICODE_ENVIRONMENT | Native.CREATE_SUSPENDED, environment, launch.WorkingFolder, ref si, out var pi))
                throw new Win32Exception();
            var job = IntPtr.Zero;
            try
            {
                // Named, so the helper can open it; a random name, so nobody can create it first and hand the service theirs.
                job = Native.CreateJobObject(IntPtr.Zero, jobName);
                if (job != IntPtr.Zero && Marshal.GetLastWin32Error() == 183 /* ERROR_ALREADY_EXISTS */)
                {
                    Native.CloseHandle(job);
                    job = IntPtr.Zero;
                }
                if (job == IntPtr.Zero || !Native.AssignProcessToJobObject(job, pi.hProcess)) throw new Win32Exception();
                if (Native.ResumeThread(pi.hThread) == -1) throw new Win32Exception();
                return new AppProcess(pi.hProcess, job, pi.dwProcessId);
            }
            catch
            {
                // Never left running outside its job, nor suspended.
                Native.TerminateProcess(pi.hProcess, 1);
                Native.CloseHandle(pi.hProcess);
                if (job != IntPtr.Zero) Native.CloseHandle(job);
                throw;
            }
            finally { Native.CloseHandle(pi.hThread); }
        }
        finally
        {
            if (environment != IntPtr.Zero) Native.DestroyEnvironmentBlock(environment);
            if (linked != IntPtr.Zero) Native.CloseHandle(linked);
        }
    }

    /// <summary>
    /// A Store app's process, started by Windows for <see cref="StoreApps"/> (§6.6), put into a new job named
    /// <paramref name="jobName"/> – a nested job, since Windows keeps packaged apps in a job of its own. Throws when it can't.
    /// </summary>
    public static AppProcess Adopt(int pid, string jobName)
    {
        const uint access = 0x0100 /* PROCESS_SET_QUOTA */ | 0x0001 /* PROCESS_TERMINATE */ | 0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */ | 0x00100000 /* SYNCHRONIZE */;
        var process = OpenProcess(access, false, (uint)pid);
        if (process == IntPtr.Zero) throw new Win32Exception();
        var job = IntPtr.Zero;
        try
        {
            job = Native.CreateJobObject(IntPtr.Zero, jobName);
            if (job != IntPtr.Zero && Marshal.GetLastWin32Error() == 183 /* ERROR_ALREADY_EXISTS */)
            {
                Native.CloseHandle(job);
                job = IntPtr.Zero;
            }
            if (job == IntPtr.Zero || !Native.AssignProcessToJobObject(job, process)) throw new Win32Exception();
            return new AppProcess(process, job, pid);
        }
        catch
        {
            Native.CloseHandle(process);
            if (job != IntPtr.Zero) Native.CloseHandle(job);
            throw;
        }
    }

    /// <summary>Closes the handles; the app keeps running (closing it is the tab's business, §4.3).</summary>
    public void Dispose()
    {
        if (_process != IntPtr.Zero) Native.CloseHandle(_process);
        if (_job != IntPtr.Zero) Native.CloseHandle(_job);
        _process = _job = IntPtr.Zero;
    }

    private const int TokenElevationTypeDefault = 1;

    /// <summary>A token without a split (UAC off, or the built-in Administrator): an administrator when it has the group.</summary>
    private static bool IsAdministrator(SafeAccessTokenHandle token)
    {
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out IntPtr token);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ExpandEnvironmentStringsForUserW(IntPtr token, string source, [Out] char[] destination, int size);
}
