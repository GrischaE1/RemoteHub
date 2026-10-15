using System.Runtime.InteropServices;
using System.Text;

namespace RemoteHub.Agent;

/// <summary>
/// Local audit and troubleshooting log: one file per day under %ProgramData%\RemoteHub\Agent\logs, old files removed.
/// Records connections (account, source address), refusals and session ends – never passwords or command content.
/// Audit entries (<see cref="Audit"/>) also go to the Windows Application log (source "RemoteHub Agent"), where
/// Intune, a SIEM or event forwarding can pick them up. Nothing is sent anywhere by the agent itself.
/// </summary>
public sealed class AgentLog
{
    private readonly string _folder;
    private readonly int _retentionDays;
    private readonly object _lock = new();
    private DateOnly _cleaned;

    public AgentLog(string folder, int retentionDays)
    {
        _folder = folder;
        _retentionDays = Math.Max(1, retentionDays);
    }

    public static AgentLog Null { get; } = new("", 1);

    /// <summary>Whether audit entries also go to the Event Log (policy AuditToEventLog); read for every entry.</summary>
    public Func<bool> EventLogEnabled { get; init; } = () => false;

    /// <summary>For tests: every audit entry as written.</summary>
    public event Action<AuditEvent, string>? Audited;

    public void Write(string message)
    {
        if (_folder.Length == 0) return;
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff}  {message.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}";
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                File.AppendAllText(Path.Combine(_folder, $"agent-{now:yyyyMMdd}.log"), line, Encoding.UTF8);
                if (_cleaned != DateOnly.FromDateTime(now)) Clean(now);
            }
            catch { /* logging must never break the agent */ }
        }
    }

    /// <summary>An audit entry: the log file and, unless the policy turns it off, the Windows Application log.</summary>
    public void Audit(AuditEvent id, string message)
    {
        Write(message);
        Audited?.Invoke(id, message);
        bool enabled;
        try { enabled = EventLogEnabled(); } catch { enabled = false; }
        if (enabled) WindowsEventLog.Write(id, message);
    }

    private void Clean(DateTime now)
    {
        _cleaned = DateOnly.FromDateTime(now);
        foreach (var file in Directory.GetFiles(_folder, "agent-*.log"))
            if (File.GetLastWriteTime(file) < now.AddDays(-_retentionDays))
                try { File.Delete(file); } catch { /* in use */ }
    }
}

/// <summary>Event IDs in the Application log (source "RemoteHub Agent"). 1xxx information, 2xxx warnings, 3xxx errors.</summary>
public enum AuditEvent
{
    PowerShellStarted = 1000,
    PowerShellEnded = 1001,
    DesktopStarted = 1100,
    DesktopEnded = 1101,
    /// <summary>The user who signed in or unlocked allowed the running remote desktop (or was connected to without asking).</summary>
    DesktopUserAllowed = 1102,
    DesktopResumed = 1103,
    /// <summary>The admin turned the privacy screen on or off.</summary>
    DesktopPrivacyScreen = 1104,
    /// <summary>The user at the device hid or showed their screen.</summary>
    DesktopUserHid = 1105,
    /// <summary>Automatic sign-in: the account signed in on the console through the credential provider.</summary>
    AutoSignInSucceeded = 1106,
    /// <summary>A session created by automatic sign-in was locked or signed out.</summary>
    AutoSignInEnded = 1107,
    /// <summary>A jump connection signed in (this device as a jump host).</summary>
    TunnelStarted = 1200,
    /// <summary>A connection through this jump host ended: where to, how long, how much.</summary>
    TunnelStream = 1201,
    TunnelEnded = 1202,
    SignInRefused = 2000,
    DesktopRefused = 2100,
    /// <summary>The user who signed in or unlocked refused the running remote desktop; it ended.</summary>
    DesktopUserRefused = 2101,
    DesktopIdleEnded = 2102,
    /// <summary>A privacy screen request was refused (right, policy, Windows version) or couldn't be shown.</summary>
    DesktopPrivacyRefused = 2103,
    /// <summary>Automatic sign-in refused before Windows was asked (policy, right, session state, rate limit, timeout …).</summary>
    AutoSignInRefused = 2104,
    /// <summary>Automatic sign-in: Windows refused the sign-in (wrong password, locked account …).</summary>
    AutoSignInFailed = 2105,
    /// <summary>A connection through this jump host was refused by its policy.</summary>
    TunnelRefused = 2200,
    DesktopError = 3100,
    /// <summary>Automatic sign-in: another account than the one asked for ended up on the console – it was signed out.</summary>
    AutoSignInMismatch = 3101,
    /// <summary>Automatic sign-in can't work on this device (pipe, registration or signature check failed).</summary>
    AutoSignInUnavailable = 3102,
}

/// <summary>
/// The Application log through advapi32 (no extra package, works trimmed). The source is registered by the MSI; if it
/// is missing, the first entry registers it (the service runs as LocalSystem). The message file is .NET Framework's
/// EventLogMessages.dll (part of Windows), which shows the text as it is.
/// </summary>
internal static class WindowsEventLog
{
    public const string Source = "RemoteHub Agent";
    private static IntPtr _handle;
    private static bool _failed;
    private static readonly object Lock = new();

    public static void Write(AuditEvent id, string message)
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Lock)
        {
            if (_failed) return;
            try
            {
                if (_handle == IntPtr.Zero)
                {
                    EnsureSource();
                    _handle = RegisterEventSourceW(null, Source);
                    if (_handle == IntPtr.Zero) { _failed = true; return; }
                }
                var type = (int)id >= 3000 ? (ushort)0x0001 /* ERROR */ : (int)id >= 2000 ? (ushort)0x0002 /* WARNING */ : (ushort)0x0004 /* INFORMATION */;
                var text = message.Length > 30000 ? message[..30000] : message;
                ReportEventW(_handle, type, 0, (uint)id, IntPtr.Zero, 1, 0, [text], IntPtr.Zero);
            }
            catch { _failed = true; /* logging must never break the agent */ }
        }
    }

    private static void EnsureSource()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey($@"SYSTEM\CurrentControlSet\Services\EventLog\Application\{Source}");
            if (key.GetValue("EventMessageFile") is null)
                key.SetValue("EventMessageFile", @"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll", Microsoft.Win32.RegistryValueKind.ExpandString);
        }
        catch { /* not elevated (tests, a manual start): the entry may show without its source text */ }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterEventSourceW(string? server, string source);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ReportEventW(IntPtr log, ushort type, ushort category, uint eventId, IntPtr userSid, ushort numStrings, uint dataSize,
        string[] strings, IntPtr rawData);
}
