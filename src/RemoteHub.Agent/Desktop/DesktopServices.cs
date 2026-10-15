using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RemoteHub.Agent;

/// <summary>What the remote desktop needs from Windows; the service passes the real pieces, tests pass doubles.</summary>
public sealed class DesktopServices(IDesktopSessions sessions, IDesktopHelperLauncher helpers, Func<string?>? secureAttention = null, IAutoSignIn? autoSignIn = null)
{
    public IDesktopSessions Sessions { get; } = sessions;
    public IDesktopHelperLauncher Helpers { get; } = helpers;
    /// <summary>Sends Ctrl+Alt+Del; null on success, otherwise why not.</summary>
    public Func<string?> SecureAttention { get; } = secureAttention ?? (() => "Ctrl+Alt+Del isn't available.");
    /// <summary>Automatic sign-in through the credential provider; null where the agent has none (tests, no broker).</summary>
    public IAutoSignIn? AutoSignIn { get; } = autoSignIn;
    /// <summary>The sessions automatic sign-in created: end action, the time after a drop, crash safety.</summary>
    public AutoSignInSessions AutoSigned { get; init; } = new(sessions);

    public static DesktopServices Windows(IAutoSignIn? autoSignIn = null)
    {
        var sessions = new WtsSessions();
        return new(sessions, new WindowsHelperLauncher(), RemoteHub.Agent.SecureAttention.Send, autoSignIn)
        {
            AutoSigned = new AutoSignInSessions(sessions, Path.Combine(AgentConfig.Folder, "autosignin.json")),
        };
    }
}

public interface IDesktopSessions
{
    IReadOnlyList<DesktopSessionInfo> List();

    /// <summary>The session currently on the physical console (it changes when someone signs in or switches user).</summary>
    int? ConsoleSessionId() => List().FirstOrDefault(s => s.Console)?.Id;

    /// <summary>The session has no screen right now (its RDP connection was closed). Checked every few seconds – kept cheap.</summary>
    bool IsDisconnected(int session) => List().FirstOrDefault(s => s.Id == session)?.State == DesktopSessionState.Disconnected;

    /// <summary>The SID of the user signed in to the session (null: nobody, or it can't be read).</summary>
    string? UserSid(int session) => null;

    /// <summary>Signs the session out at once (automatic sign-in: the wrong account ended up there). False if it didn't work.</summary>
    bool LogOff(int session) => false;

    /// <summary>Disconnects the session – on client Windows the console then shows the lock screen. False if it didn't work.</summary>
    bool Disconnect(int session) => false;
}

/// <summary>The desktop helper process in a session: its standard input/output carry the helper messages.</summary>
public interface IDesktopHelper : IDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    int ProcessId { get; }
}

public interface IDesktopHelperLauncher
{
    IDesktopHelper Start(int sessionId);
}

/// <summary>Windows sessions of this device (console and remote sessions) and who is signed in.</summary>
internal sealed class WtsSessions : IDesktopSessions
{
    public int? ConsoleSessionId() => WtsNative.WTSGetActiveConsoleSessionId() is var id && id != 0xFFFFFFFF ? (int)id : null;

    public bool IsDisconnected(int session) => WtsNative.ConnectState(session) == 4; // WTSDisconnected

    public string? UserSid(int session)
    {
        // The service runs as LocalSystem, which WTSQueryUserToken needs.
        if (!WtsNative.WTSQueryUserToken(session, out var token)) return null;
        try
        {
            using var identity = new System.Security.Principal.WindowsIdentity(token);
            return identity.User?.Value;
        }
        catch { return null; }
        finally { Native.CloseHandle(token); }
    }

    public bool LogOff(int session) => WtsNative.WTSLogoffSession(IntPtr.Zero, session, false);

    public bool Disconnect(int session) => WtsNative.WTSDisconnectSession(IntPtr.Zero, session, false);

    public IReadOnlyList<DesktopSessionInfo> List()
    {
        var list = new List<DesktopSessionInfo>();
        if (!WtsNative.WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count)) return list;
        try
        {
            var console = WtsNative.WTSGetActiveConsoleSessionId();
            var size = Marshal.SizeOf<WtsNative.WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var s = Marshal.PtrToStructure<WtsNative.WTS_SESSION_INFO>(buffer + i * size);
                if (s.SessionId == 0) continue; // services
                if (s.State is not (0 or 1 or 4)) continue; // Active, Connected, Disconnected
                var station = Marshal.PtrToStringUni(s.pWinStationName) ?? "";
                var user = WtsNative.Query(s.SessionId, 5);   // WTSUserName
                var domain = WtsNative.Query(s.SessionId, 7); // WTSDomainName
                var isConsole = (uint)s.SessionId == console;
                if (user.Length == 0 && !isConsole) continue; // an empty remote session: nothing to show
                var state = user.Length == 0 ? DesktopSessionState.SignIn
                    : s.State == 4 ? DesktopSessionState.Disconnected
                    : WtsNative.IsLocked(s.SessionId) ? DesktopSessionState.Locked
                    : DesktopSessionState.Active;
                var client = isConsole ? "" : WtsNative.Query(s.SessionId, 10); // WTSClientName
                list.Add(new DesktopSessionInfo(s.SessionId, user.Length == 0 ? "" : domain.Length > 0 ? $"{domain}\\{user}" : user, state, isConsole, station, client));
            }
        }
        finally { WtsNative.WTSFreeMemory(buffer); }
        return list.OrderByDescending(s => s.Console).ThenBy(s => s.Id).ToList();
    }
}

/// <summary>
/// Starts <c>RemoteHubAgent.exe desktop</c> in a session as LocalSystem (the service's own token, moved to that session),
/// so it can capture and control the sign-in screen and UAC prompts. A job object ends it with the connection.
/// </summary>
internal sealed class WindowsHelperLauncher : IDesktopHelperLauncher
{
    public IDesktopHelper Start(int sessionId) => new WindowsHelper(sessionId);

    private sealed class WindowsHelper : IDesktopHelper
    {
        private IntPtr _process, _job;
        private FileStream? _input, _output;

        public WindowsHelper(int sessionId)
        {
            try { Launch(sessionId); }
            catch { Dispose(); throw; }
        }

        public Stream Input => _input!;
        public Stream Output => _output!;
        public int ProcessId { get; private set; }

        private void Launch(int sessionId)
        {
            const uint access = 0x0002 | 0x0008 | 0x0001 | 0x0100 | 0x0080; // DUPLICATE, QUERY, ASSIGN_PRIMARY, ADJUST_SESSIONID, ADJUST_DEFAULT
            if (!WtsNative.OpenProcessToken(WtsNative.GetCurrentProcess(), access, out var own)) throw new Win32Exception();
            IntPtr token;
            try
            {
                if (!Native.DuplicateTokenEx(own, Native.MAXIMUM_ALLOWED, IntPtr.Zero, Native.SecurityImpersonation, Native.TokenPrimary, out token))
                    throw new Win32Exception();
            }
            finally { Native.CloseHandle(own); }
            try
            {
                if (!WtsNative.SetTokenInformation(token, 12 /* TokenSessionId */, ref sessionId, sizeof(int))) throw new Win32Exception();
                // Spike: UIAccess puts the helper's topmost windows (the privacy overlay) in the UIAccess z-band, above
                // Start, Search and the other shell surfaces. Needs SeTcbPrivilege (the service has it), no manifest.
                if (HelperUiAccess())
                {
                    var on = 1;
                    if (!WtsNative.SetTokenInformation(token, 26 /* TokenUIAccess */, ref on, sizeof(int))) throw new Win32Exception();
                }

                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The agent's path is unknown.");
                var (pi, stdinWrite, stdoutRead) = ChildProcess.Start(token, $"\"{exe}\" desktop", Native.CREATE_NO_WINDOW, IntPtr.Zero,
                    Path.GetDirectoryName(exe), @"winsta0\default", stdoutBuffer: 1024 * 1024, stderrToNul: true);
                _process = pi.hProcess;
                ProcessId = pi.dwProcessId;
                _input = new FileStream(stdinWrite, FileAccess.Write, 1);
                _output = new FileStream(stdoutRead, FileAccess.Read, 64 * 1024);
                _job = ChildProcess.RunInJob(pi);
            }
            finally { Native.CloseHandle(token); }
        }

        /// <summary>Spike switch: on unless HKLM\SOFTWARE\Policies\RemoteHub\Agent HelperUIAccess = 0 (DWORD).</summary>
        private static bool HelperUiAccess()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(AgentPolicy.RegistryPath);
                return key?.GetValue("HelperUIAccess") is not 0;
            }
            catch { return true; }
        }

        public void Dispose()
        {
            try { _input?.Dispose(); } catch { /* broken pipe */ }
            if (_process != IntPtr.Zero && Native.WaitForSingleObject(_process, 2000) != 0) Native.TerminateProcess(_process, 1);
            if (_job != IntPtr.Zero) { Native.CloseHandle(_job); _job = IntPtr.Zero; }
            try { _output?.Dispose(); } catch { /* ignore */ }
            if (_process != IntPtr.Zero) { Native.CloseHandle(_process); _process = IntPtr.Zero; }
        }
    }
}

internal static class WtsNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct WTS_SESSION_INFO { public int SessionId; public IntPtr pWinStationName; public int State; }

    /// <summary>WTSConnectState: 0 active, 1 connected, 4 disconnected, … (-1 when it can't be read).</summary>
    public static int ConnectState(int session)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, 8, out var buffer, out var bytes)) return -1;
        try { return bytes >= 4 ? Marshal.ReadInt32(buffer) : -1; }
        finally { WTSFreeMemory(buffer); }
    }

    public static string Query(int session, int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out var buffer, out _)) return "";
        try { return Marshal.PtrToStringUni(buffer) ?? ""; }
        finally { WTSFreeMemory(buffer); }
    }

    /// <summary>
    /// The lock screen runs as LogonUI.exe in the session (it exits after unlock). The WTSSessionInfoEx lock flag is only
    /// the fallback: it is reversed on some Windows versions and wrong for some RDP sessions, and a session wrongly seen
    /// as locked would be shown without asking its user.
    /// <para>The Ctrl+Alt+Del screen of a signed-in user runs as LogonUI too, so it counts as locked. That shows only
    /// that screen: the helper holds the picture when the user's desktop comes back and asks them first (HoldUserDesktop).</para>
    /// </summary>
    public static bool IsLocked(int session)
    {
        try
        {
            var logonUi = System.Diagnostics.Process.GetProcessesByName("LogonUI");
            try { return logonUi.Any(p => p.SessionId == session); }
            finally { foreach (var p in logonUi) p.Dispose(); }
        }
        catch { return LockFlag(session); }
    }

    private static bool LockFlag(int session)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, 25, out var buffer, out var bytes)) return false;
        try { return bytes >= 16 && Marshal.ReadInt32(buffer, 12) == 0; }
        finally { WTSFreeMemory(buffer); }
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSQuerySessionInformationW(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSQueryUserToken(int session, out IntPtr token);
    [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSLogoffSession(IntPtr server, int session, bool wait);
    [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSDisconnectSession(IntPtr server, int session, bool wait);
    [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool SetTokenInformation(IntPtr token, int infoClass, ref int info, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref Native.SECURITY_ATTRIBUTES attributes, uint disposition, uint flags, IntPtr template);
}
