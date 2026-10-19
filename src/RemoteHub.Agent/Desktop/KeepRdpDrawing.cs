using Microsoft.Win32;

namespace RemoteHub.Agent;

/// <summary>
/// While the admin looks at a session, an RDP window in it that the user minimizes keeps its session drawn: Remote
/// Desktop Connection otherwise tells that server to stop drawing (to save bandwidth), and an agent connection to that
/// server's session then shows black. The user's <c>RemoteDesktop_SuppressWhenMinimized</c> is set to 2 for the time of
/// the connection and put back afterwards – their own value, or none.
/// </summary>
/// <remarks>
/// The RDP client reads it when it connects: RDP windows opened before need to reconnect once. What was there before is
/// kept in a small file next to agent.json until it is put back, so a helper that ended abruptly is made good by the next
/// connection to that user's session.
/// </remarks>
internal sealed class KeepRdpDrawing : IDisposable
{
    private const string KeyPath = @"Software\Microsoft\Terminal Server Client";
    private const string ValueName = "RemoteDesktop_SuppressWhenMinimized";
    private readonly object _lock = new();
    private readonly string _folder;
    private string? _sid;
    private bool _done;

    public KeepRdpDrawing(string? folder = null) => _folder = folder ?? AgentConfig.Folder;

    private string RecordFile(string sid) => Path.Combine(_folder, $"rdp-minimized-{sid}.txt");

    /// <summary>
    /// Sets it for the user signed in now (once). Null when nothing was changed (nobody signed in yet, or the user has it
    /// set to 2 themselves), else what was done – for the log. <paramref name="sid"/> for tests.
    /// </summary>
    public string? Apply(string? sid = null)
    {
        lock (_lock)
        {
            if (_done) return null;
            sid ??= SessionUser.Sid();
            if (sid is null) return null; // the sign-in screen: once someone signs in
            using var hive = Registry.Users.OpenSubKey(sid);
            if (hive is null) return null; // profile not loaded (yet)
            _done = true; // once – also when the write below fails (logged by the caller, not every few seconds)
            using var key = Registry.Users.CreateSubKey($@"{sid}\{KeyPath}", writable: true);
            var current = key.GetValue(ValueName);
            var record = RecordFile(sid);
            if (!File.Exists(record))
            {
                if (current is 2) return null; // the user's own choice: left alone
                // On the disk before the value changes: an empty record after a power loss would delete the user's own value.
                DurableFile.WriteAllText(record, current is int v ? v.ToString() : "absent");
            }
            key.SetValue(ValueName, 2, RegistryValueKind.DWord);
            _sid = sid;
            return $"RDP windows in this session keep drawing when minimized ({ValueName} = 2 until the connection ends; " +
                   "RDP windows opened before need to reconnect once)";
        }
    }

    /// <summary>Puts back what was there before.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_sid is not { } sid) return;
            _sid = null;
            try
            {
                var record = RecordFile(sid);
                if (!File.Exists(record)) return;
                var before = File.ReadAllText(record).Trim();
                using (var key = Registry.Users.OpenSubKey($@"{sid}\{KeyPath}", writable: true))
                {
                    if (key is null) return; // the user signed out: put back by the next connection
                    if (int.TryParse(before, out var v)) key.SetValue(ValueName, v, RegistryValueKind.DWord);
                    else key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
                File.Delete(record);
            }
            catch { /* put back by the next connection to this user's session */ }
        }
    }
}
