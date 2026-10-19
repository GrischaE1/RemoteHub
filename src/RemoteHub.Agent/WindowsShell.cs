using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RemoteHub.Agent;

/// <summary>
/// Signs the account in on this device (like a network logon to a file share, so policies such as "Deny access to this
/// computer from the network" apply) and requires membership in one of the allowed groups. Local accounts and accounts
/// of the device's own domain both work – the device asks its own domain controller, no trust to the operator's side is
/// needed. UAC: an administrator's filtered token is replaced by its full ("linked") token, which the agent can obtain
/// because it runs as LocalSystem.
/// </summary>
public sealed class WindowsAuthenticator(IReadOnlyList<string> allowedGroups) : IShellAuthenticator
{
    public AuthResult Authenticate(string userName, string password) => Authenticate(userName, password, allowedGroups);

    public AuthResult Authenticate(string userName, string password, IReadOnlyList<string> groups)
    {
        var (domain, user) = SplitUser(userName);
        if (user.Length == 0) return AuthResult.Fail(AuthOutcome.BadCredentials, "no user name");

        if (!Native.LogonUser(user, domain, password, Native.LOGON32_LOGON_NETWORK, Native.LOGON32_PROVIDER_DEFAULT, out var logon))
            return SignInRefusal.FromWin32(Marshal.GetLastWin32Error());

        IntPtr full = IntPtr.Zero;
        try
        {
            full = FullToken(logon);
            using var identity = new WindowsIdentity(full);
            var principal = new WindowsPrincipal(identity);
            if (!groups.Any(g => GroupNames.Resolve(g) is { } sid && principal.IsInRole(sid)))
                return AuthResult.Fail(AuthOutcome.NotAllowed, "not a member of an allowed group");

            if (!Native.DuplicateTokenEx(full, Native.MAXIMUM_ALLOWED, IntPtr.Zero, Native.SecurityImpersonation, Native.TokenPrimary, out var primary))
                return AuthResult.Fail(AuthOutcome.Error, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            var sids = (identity.Groups?.Select(g => g.Value) ?? []).Append(identity.User?.Value ?? "").Where(v => v.Length > 0).ToList();
            return new AuthResult(AuthOutcome.Ok, "", new ShellIdentity(identity.Name, primary, sids, identity.User?.Value));
        }
        finally
        {
            if (full != IntPtr.Zero && full != logon) Native.CloseHandle(full);
            Native.CloseHandle(logon);
        }
    }

    /// <summary>"DOMAIN\user", ".\user", "COMPUTER\user", "user@domain" or "user" (local account).</summary>
    internal static (string? Domain, string User) SplitUser(string userName)
    {
        var name = userName.Trim();
        var slash = name.IndexOf('\\');
        if (slash >= 0)
        {
            var domain = name[..slash];
            if (domain.Length == 0 || domain == "." || domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) domain = ".";
            return (domain, name[(slash + 1)..]);
        }
        return name.Contains('@') ? (null, name) : (".", name);
    }

    /// <summary>The token itself, or – for an administrator under UAC – its full linked token.</summary>
    private static IntPtr FullToken(IntPtr token)
    {
        if (Native.GetTokenInformation(token, Native.TokenElevationType, out int type, sizeof(int), out _) && type == Native.TokenElevationTypeLimited
            && Native.GetTokenInformation(token, Native.TokenLinkedToken, out IntPtr linked, IntPtr.Size, out _))
            return linked;
        return token;
    }
}

/// <summary>
/// Starts <c>powershell.exe -s</c> under the signed-in user's token, with that user's profile and environment – the same
/// as a PowerShell remoting session would. The process and anything it starts live in a job object that is killed when
/// the session ends.
/// </summary>
public sealed class WindowsShellLauncher : IShellLauncher
{
    public IShellProcess Start(ShellIdentity identity)
    {
        if (identity.Token == IntPtr.Zero) throw new InvalidOperationException("No token.");
        return new WindowsShellProcess(identity.Token);
    }
}

internal sealed class WindowsShellProcess : IShellProcess
{
    private readonly IntPtr _token;
    private IntPtr _profile, _environment, _process, _job;
    private FileStream? _input, _output;

    public WindowsShellProcess(IntPtr token)
    {
        _token = token;
        try { Launch(); }
        catch { Dispose(); throw; }
    }

    public Stream Input => _input!;
    public Stream Output => _output!;
    public int ProcessId { get; private set; }

    private void Launch()
    {
        var profile = new Native.PROFILEINFO { dwSize = Marshal.SizeOf<Native.PROFILEINFO>(), lpUserName = new WindowsIdentity(_token).Name, dwFlags = Native.PI_NOUI };
        if (Native.LoadUserProfile(_token, ref profile)) _profile = profile.hProfile; // without a profile it still works, just without HKCU
        if (!Native.CreateEnvironmentBlock(out _environment, _token, false)) _environment = IntPtr.Zero;

        // "": the logon session's own (non-interactive) window station.
        var (pi, stdinWrite, stdoutRead) = ChildProcess.Start(_token, $"\"{ShellCommand.PowerShellExe}\" {ShellCommand.Arguments}",
            Native.CREATE_UNICODE_ENVIRONMENT | Native.CREATE_NO_WINDOW, _environment, Environment.SystemDirectory, "", stdoutBuffer: 0, stderrToNul: false);
        _process = pi.hProcess;
        ProcessId = pi.dwProcessId;
        _input = new FileStream(stdinWrite, FileAccess.Write, 1);
        _output = new FileStream(stdoutRead, FileAccess.Read, 4096);
        _job = ChildProcess.RunInJob(pi);
    }

    public void Dispose()
    {
        try { _input?.Dispose(); } catch { /* broken pipe */ }
        if (_process != IntPtr.Zero && Native.WaitForSingleObject(_process, 3000) != 0) Native.TerminateProcess(_process, 1);
        if (_job != IntPtr.Zero) { Native.CloseHandle(_job); _job = IntPtr.Zero; } // kills anything left in the session
        try { _output?.Dispose(); } catch { /* ignore */ }
        if (_process != IntPtr.Zero) { Native.CloseHandle(_process); _process = IntPtr.Zero; }
        if (_environment != IntPtr.Zero) { Native.DestroyEnvironmentBlock(_environment); _environment = IntPtr.Zero; }
        if (_profile != IntPtr.Zero) { Native.UnloadUserProfile(_token, _profile); _profile = IntPtr.Zero; }
    }
}

/// <summary>
/// Starts a child process with its standard input/output on new pipes. The child inherits only its own pipe ends (an
/// explicit handle list), and starts are serialised, so a process started at the same moment for another session can't
/// pick up this one's pipes – which would let it read another operator's input or write fake helper messages.
/// </summary>
internal static class ChildProcess
{
    private static readonly object Gate = new();

    /// <summary>Starts the process suspended; the caller owns the returned handles and resumes it with <see cref="RunInJob"/>.</summary>
    public static (Native.PROCESS_INFORMATION Pi, SafeFileHandle StdinWrite, SafeFileHandle StdoutRead) Start(
        IntPtr token, string commandLine, uint flags, IntPtr environment, string? directory, string desktop, int stdoutBuffer, bool stderrToNul)
    {
        lock (Gate)
        {
            var sa = new Native.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(), bInheritHandle = true };
            SafeFileHandle? stdinRead = null, stdinWrite = null, stdoutRead = null, stdoutWrite = null, nul = null;
            IntPtr list = IntPtr.Zero, handles = IntPtr.Zero;
            var listReady = false;
            try
            {
                if (!Native.CreatePipe(out stdinRead, out stdinWrite, ref sa, 0)) throw new Win32Exception();
                if (!Native.CreatePipe(out stdoutRead, out stdoutWrite, ref sa, stdoutBuffer)) throw new Win32Exception();
                Native.SetHandleInformation(stdinWrite, Native.HANDLE_FLAG_INHERIT, 0);
                Native.SetHandleInformation(stdoutRead, Native.HANDLE_FLAG_INHERIT, 0);
                var stderr = stdoutWrite;
                if (stderrToNul)
                {
                    nul = WtsNative.CreateFileW("NUL", 0x40000000, 3, ref sa, 3, 0, IntPtr.Zero);
                    if (nul.IsInvalid) throw new Win32Exception();
                    stderr = nul;
                }
                IntPtr[] inherited = stderrToNul
                    ? [stdinRead.DangerousGetHandle(), stdoutWrite.DangerousGetHandle(), nul!.DangerousGetHandle()]
                    : [stdinRead.DangerousGetHandle(), stdoutWrite.DangerousGetHandle()];

                var size = IntPtr.Zero;
                Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                list = Marshal.AllocHGlobal(size);
                if (!Native.InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new Win32Exception();
                listReady = true;
                handles = Marshal.AllocHGlobal(IntPtr.Size * inherited.Length);
                Marshal.Copy(inherited, 0, handles, inherited.Length);
                if (!Native.UpdateProcThreadAttribute(list, 0, Native.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles,
                        IntPtr.Size * inherited.Length, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception();

                var si = new Native.STARTUPINFOEX
                {
                    StartupInfo = new Native.STARTUPINFO
                    {
                        cb = Marshal.SizeOf<Native.STARTUPINFOEX>(),
                        dwFlags = Native.STARTF_USESTDHANDLES,
                        hStdInput = stdinRead.DangerousGetHandle(),
                        hStdOutput = stdoutWrite.DangerousGetHandle(),
                        hStdError = stderr.DangerousGetHandle(),
                        lpDesktop = desktop,
                    },
                    lpAttributeList = list,
                };
                // CreateProcessW may write to the command line: pass a buffer, not a string.
                if (!Native.CreateProcessAsUser(token, null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true,
                        flags | Native.CREATE_SUSPENDED | Native.EXTENDED_STARTUPINFO_PRESENT, environment, directory, ref si, out var pi))
                    throw new Win32Exception();
                var result = (pi, stdinWrite, stdoutRead);
                stdinWrite = stdoutRead = null; // now the caller's
                return result;
            }
            finally
            {
                if (listReady) Native.DeleteProcThreadAttributeList(list);
                if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
                if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
                stdinRead?.Dispose();
                stdoutWrite?.Dispose();
                nul?.Dispose();
                stdinWrite?.Dispose();
                stdoutRead?.Dispose();
            }
        }
    }

    /// <summary>
    /// Puts the suspended process into a job that kills it and everything it starts when the job handle closes, then
    /// resumes it. If the job can't be set up the process is ended instead – it never runs outside the job.
    /// </summary>
    public static IntPtr RunInJob(Native.PROCESS_INFORMATION pi)
    {
        var job = Native.CreateJobObject(IntPtr.Zero, null);
        try
        {
            if (job == IntPtr.Zero) throw new Win32Exception();
            var limits = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf(limits))
                || !Native.AssignProcessToJobObject(job, pi.hProcess))
                throw new Win32Exception();
            if (Native.ResumeThread(pi.hThread) == -1) throw new Win32Exception();
            return job;
        }
        catch
        {
            Native.TerminateProcess(pi.hProcess, 1);
            if (job != IntPtr.Zero) Native.CloseHandle(job);
            throw;
        }
        finally { Native.CloseHandle(pi.hThread); }
    }
}

internal static class Native
{
    public const int LOGON32_LOGON_NETWORK = 3, LOGON32_PROVIDER_DEFAULT = 0;
    public const uint MAXIMUM_ALLOWED = 0x02000000;
    public const int SecurityImpersonation = 2, TokenPrimary = 1;
    public const int TokenElevationType = 18, TokenLinkedToken = 19, TokenElevationTypeLimited = 3;
    public const int PI_NOUI = 1, STARTF_USESTDHANDLES = 0x100, HANDLE_FLAG_INHERIT = 1;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000, CREATE_SUSPENDED = 0x4, EXTENDED_STARTUPINFO_PRESENT = 0x80000;
    public static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x20002;
    public const int JobObjectExtendedLimitInformation = 9;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROFILEINFO
    {
        public int dwSize, dwFlags;
        public string? lpUserName, lpProfilePath, lpDefaultPath, lpServerName, lpPolicyPath;
        public IntPtr hProfile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool LogonUser(string user, string? domain, string password, int logonType, int provider, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr token, int infoClass, out IntPtr info, int length, out int returned);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessAsUser(IntPtr token, string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string? dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    public static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool LoadUserProfile(IntPtr token, ref PROFILEINFO profile);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool UnloadUserProfile(IntPtr token, IntPtr profile);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool DestroyEnvironmentBlock(IntPtr env);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SECURITY_ATTRIBUTES attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetHandleInformation(SafeHandle handle, int mask, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
}
