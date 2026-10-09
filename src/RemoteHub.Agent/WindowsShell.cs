using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
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
        {
            var error = Marshal.GetLastWin32Error();
            return error is Native.ERROR_LOGON_FAILURE or Native.ERROR_ACCOUNT_RESTRICTION or Native.ERROR_ACCOUNT_DISABLED
                or Native.ERROR_PASSWORD_EXPIRED or Native.ERROR_ACCOUNT_LOCKED_OUT or Native.ERROR_LOGON_TYPE_NOT_GRANTED
                ? AuthResult.Fail(error == Native.ERROR_LOGON_TYPE_NOT_GRANTED ? AuthOutcome.NotAllowed : AuthOutcome.BadCredentials, new Win32Exception(error).Message)
                : AuthResult.Fail(AuthOutcome.Error, new Win32Exception(error).Message);
        }

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
            return new AuthResult(AuthOutcome.Ok, "", new ShellIdentity(identity.Name, primary, sids));
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

        var inherit = new Native.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        if (!Native.CreatePipe(out var stdinRead, out var stdinWrite, ref inherit, 0)) throw new Win32Exception();
        if (!Native.CreatePipe(out var stdoutRead, out var stdoutWrite, ref inherit, 0)) throw new Win32Exception();
        Native.SetHandleInformation(stdinWrite, Native.HANDLE_FLAG_INHERIT, 0);
        Native.SetHandleInformation(stdoutRead, Native.HANDLE_FLAG_INHERIT, 0);

        try
        {
            var si = new Native.STARTUPINFO
            {
                cb = Marshal.SizeOf<Native.STARTUPINFO>(),
                dwFlags = Native.STARTF_USESTDHANDLES,
                hStdInput = stdinRead.DangerousGetHandle(),
                hStdOutput = stdoutWrite.DangerousGetHandle(),
                hStdError = stdoutWrite.DangerousGetHandle(),
                lpDesktop = "", // the logon session's own (non-interactive) window station
            };
            var cmd = $"\"{ShellCommand.PowerShellExe}\" {ShellCommand.Arguments}";
            if (!Native.CreateProcessAsUser(_token, null, cmd, IntPtr.Zero, IntPtr.Zero, true,
                    Native.CREATE_UNICODE_ENVIRONMENT | Native.CREATE_NO_WINDOW | Native.CREATE_SUSPENDED, _environment,
                    Environment.SystemDirectory, ref si, out var pi))
                throw new Win32Exception();

            _process = pi.hProcess;
            ProcessId = pi.dwProcessId;
            _job = Native.CreateJobObject(IntPtr.Zero, null);
            var limits = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            Native.SetInformationJobObject(_job, Native.JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf(limits));
            Native.AssignProcessToJobObject(_job, _process);
            Native.ResumeThread(pi.hThread);
            Native.CloseHandle(pi.hThread);
        }
        finally
        {
            stdinRead.Dispose();
            stdoutWrite.Dispose();
        }
        _input = new FileStream(stdinWrite, FileAccess.Write, 1);
        _output = new FileStream(stdoutRead, FileAccess.Read, 4096);
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

internal static class Native
{
    public const int LOGON32_LOGON_NETWORK = 3, LOGON32_PROVIDER_DEFAULT = 0;
    public const int ERROR_LOGON_FAILURE = 1326, ERROR_ACCOUNT_RESTRICTION = 1327, ERROR_ACCOUNT_DISABLED = 1331,
        ERROR_PASSWORD_EXPIRED = 1330, ERROR_ACCOUNT_LOCKED_OUT = 1909, ERROR_LOGON_TYPE_NOT_GRANTED = 1385;
    public const uint MAXIMUM_ALLOWED = 0x02000000;
    public const int SecurityImpersonation = 2, TokenPrimary = 1;
    public const int TokenElevationType = 18, TokenLinkedToken = 19, TokenElevationTypeLimited = 3;
    public const int PI_NOUI = 1, STARTF_USESTDHANDLES = 0x100, HANDLE_FLAG_INHERIT = 1;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000, CREATE_SUSPENDED = 0x4;
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
    public static extern bool CreateProcessAsUser(IntPtr token, string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

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
