using Microsoft.Win32;

namespace RemoteHub.Agent;

/// <summary>
/// While the privacy screen blocks the device's own mouse and keyboard (the admin's, and the one automatic sign-in puts
/// up), the Ctrl+Alt+Del screen – which Windows always shows, nothing can block the keys – offers only Task Manager and
/// Cancel: Lock, Switch user, Change a password, Sign out and the power button are hidden with their policy values, and
/// put back exactly as they were (the value, or none) when local input is free again or the helper ends.
/// </summary>
/// <remarks>
/// <para>Per user (the session's user, HKU\&lt;sid&gt;): <c>DisableLockWorkstation</c>, <c>DisableChangePassword</c>
/// (Policies\System), <c>NoLogoff</c>, <c>NoClose</c> (Policies\Explorer). Per machine:
/// <c>HideFastUserSwitching</c> (HKLM Policies\System) – Windows has no per-user value for Switch user.</para>
/// <para>What was there before is kept in small files next to agent.json until it is put back: a helper that ended
/// abruptly is made good by the next one in that session, and by the service when it starts (no helper runs then). The
/// machine value is put back when the last session that hid it is done. A value that isn't a DWORD is left alone.</para>
/// </remarks>
internal sealed class SasActions
{
    private const string SystemKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    internal static readonly (string Key, string Name)[] UserValues =
        [(SystemKey, "DisableLockWorkstation"), (SystemKey, "DisableChangePassword"), (ExplorerKey, "NoLogoff"), (ExplorerKey, "NoClose")];
    internal static readonly (string Key, string Name)[] MachineValues = [(SystemKey, "HideFastUserSwitching")];

    private const string MachineRecord = "sas-actions-machine.txt";
    private readonly string _folder;
    private readonly Func<string, RegistryKey?> _userHive;
    private readonly RegistryKey _machine;
    private string? _sid;

    /// <param name="userHive">The root of a user's hive by SID, writable (tests: a scratch key).</param>
    /// <param name="machine">HKLM (tests: a scratch key).</param>
    public SasActions(string? folder = null, Func<string, RegistryKey?>? userHive = null, RegistryKey? machine = null)
    {
        _folder = folder ?? AgentConfig.Folder;
        _userHive = userHive ?? (sid => Registry.Users.OpenSubKey(sid, writable: true));
        _machine = machine ?? Registry.LocalMachine;
    }

    /// <summary>Hidden now (until <see cref="Show"/>).</summary>
    public bool Hidden => _sid is not null;

    /// <summary>
    /// Hides the actions for the user signed in now. Null when nothing was done (already hidden, nobody signed in yet,
    /// profile not loaded – try again), else what was done, for the log. <paramref name="sid"/> for tests.
    /// </summary>
    public string? Hide(string? sid = null)
    {
        if (_sid is not null) return null;
        sid ??= SessionUser.Sid();
        if (sid is null) return null;
        using var gate = Lock();
        using var hive = _userHive(sid);
        if (hive is null) return null;
        // Owner first: whatever is changed below can be put back by Show (this helper) or by anyone else once it is gone.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(OwnerFile(sid), "");
        _sid = sid;
        try
        {
            Set(hive, UserValues, UserRecord(sid));
            Set(_machine, MachineValues, Path.Combine(_folder, MachineRecord));
        }
        catch
        {
            try { Release(sid); _sid = null; } catch { /* Show tries again; the service at its next start */ }
            throw;
        }
        return "Ctrl+Alt+Del screen: Lock, Switch user, Change a password, Sign out and the power button hidden while the device's mouse and keyboard are blocked";
    }

    /// <summary>
    /// Puts back what was there before – the user's values once no other helper for that user hides them, the machine
    /// value once no helper at all does. When it fails, it stays this helper's to put back: the next call tries again.
    /// </summary>
    public string? Show()
    {
        if (_sid is not { } sid) return null;
        using var gate = Lock();
        var back = Release(sid);
        _sid = null;
        return back ? "Ctrl+Alt+Del screen: its actions are back" : "Ctrl+Alt+Del screen: still hidden for another connection to this user's session";
    }

    /// <summary>
    /// Under the gate: this helper no longer owns them; puts back what nobody else needs. False while another live
    /// helper hides them for this user. A hive that isn't loaded keeps its record for later.
    /// </summary>
    private bool Release(string sid)
    {
        File.Delete(OwnerFile(sid));
        if (LiveOwners(sid).Any()) return false;
        using (var hive = _userHive(sid))
            if (hive is not null) Restore(hive, UserRecord(sid));
        if (!LiveOwners(null).Any()) Restore(_machine, Path.Combine(_folder, MachineRecord));
        return true;
    }

    /// <summary>
    /// Helper start: puts back what a helper that ended abruptly left hidden for this session's user – unless another
    /// live helper still hides it – also when this connection never blocks input. False until the user is signed in and
    /// their hive loaded (try again); <paramref name="note"/> says what was done, for the log.
    /// </summary>
    public bool TryReconcile(out string? note, string? sid = null)
    {
        note = null;
        sid ??= SessionUser.Sid();
        if (sid is null) return false;
        using var gate = Lock();
        using var hive = _userHive(sid);
        if (hive is null) return false;
        var done = !LiveOwners(sid).Any() && Restore(hive, UserRecord(sid));
        done |= !LiveOwners(null).Any() && Restore(_machine, Path.Combine(_folder, MachineRecord));
        if (done) note = "Ctrl+Alt+Del screen: actions an earlier helper hid are back";
        return true;
    }

    /// <summary>Service start (no helper runs): puts back whatever a helper left hidden. Null when there was nothing.</summary>
    public string? RestoreLeftovers()
    {
        if (!Directory.Exists(_folder)) return null;
        using var gate = Lock();
        foreach (var owner in Directory.GetFiles(_folder, "sas-owner-*.txt")) File.Delete(owner); // their helpers are gone
        var done = false;
        foreach (var record in Directory.GetFiles(_folder, "sas-actions-S-*.txt"))
        {
            var sid = Path.GetFileNameWithoutExtension(record)["sas-actions-".Length..];
            using var hive = _userHive(sid);
            if (hive is not null) done |= Restore(hive, record); // not loaded: kept until that user is back
        }
        done |= Restore(_machine, Path.Combine(_folder, MachineRecord));
        return done ? "Ctrl+Alt+Del screen: actions an earlier helper hid are back" : null;
    }

    private string UserRecord(string sid) => Path.Combine(_folder, $"sas-actions-{sid}.txt");

    // Who hides them now: one file per helper (and instance), named by its process – a helper that ended without
    // putting them back no longer counts once its process is gone.
    private readonly string _instance = Guid.NewGuid().ToString("N")[..8];
    private string OwnerFile(string sid) => Path.Combine(_folder, $"sas-owner-{sid}-{Environment.ProcessId}-{_instance}.txt");

    /// <summary>The owners whose helper still runs, for one user (or for anyone); the others are removed.</summary>
    private IEnumerable<string> LiveOwners(string? sid)
    {
        var self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        var live = new List<string>();
        if (!Directory.Exists(_folder)) return live;
        foreach (var file in Directory.GetFiles(_folder, sid is null ? "sas-owner-*.txt" : $"sas-owner-{sid}-*.txt"))
        {
            var parts = Path.GetFileNameWithoutExtension(file).Split('-');
            var alive = parts.Length >= 3 && int.TryParse(parts[^2], out var pid) && Running(pid, self);
            if (alive) live.Add(file); else File.Delete(file);
        }
        return live;
    }

    private static bool Running(int pid, string name)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited && p.ProcessName == name; }
        catch (ArgumentException) { return false; } // not running
    }

    /// <summary>Records the values as they are (unless a record is there already: that is the original), then sets them to 1.</summary>
    private void Set(RegistryKey root, (string Key, string Name)[] values, string record)
    {
        if (!File.Exists(record))
        {
            var lines = new List<string>();
            foreach (var (path, name) in values)
            {
                using var key = root.OpenSubKey(path);
                var now = key?.GetValue(name);
                if (now is null) lines.Add($"{path}|{name}|absent");
                else if (now is int v) lines.Add($"{path}|{name}|{v}");
                // anything else isn't ours to change
            }
            // On the disk before the values change: a torn record would leave them hidden for good after a power loss.
            DurableFile.WriteAllLines(record, lines);
        }
        // the values hidden now (a record from an earlier version may list more – those are only put back)
        var recorded = File.ReadAllLines(record).Select(l => l.Split('|')).Where(p => p.Length == 3).Select(p => (p[0], p[1])).ToHashSet();
        foreach (var (path, name) in values.Where(recorded.Contains))
        {
            using var key = root.CreateSubKey(path, writable: true);
            key.SetValue(name, 1, RegistryValueKind.DWord);
        }
    }

    private static bool Restore(RegistryKey root, string record)
    {
        if (!File.Exists(record)) return false;
        foreach (var line in File.ReadAllLines(record))
        {
            var p = line.Split('|');
            if (p.Length != 3) continue;
            using var key = root.OpenSubKey(p[0], writable: true);
            if (key is null) continue;
            if (int.TryParse(p[2], out var v)) key.SetValue(p[1], v, RegistryValueKind.DWord);
            else key.DeleteValue(p[1], throwOnMissingValue: false);
        }
        File.Delete(record);
        return true;
    }

    /// <summary>One helper (or the service) at a time: several sessions share the machine value and its record.</summary>
    private static Gate Lock() => new(@"Global\RemoteHubAgentSasActions");

    private sealed class Gate : IDisposable
    {
        private readonly Mutex _mutex;

        /// <summary>Taken, or an exception: nothing is changed without it (the caller logs it; the service puts back at its start).</summary>
        public Gate(string name)
        {
            _mutex = new Mutex(false, name); // UnauthorizedAccessException goes to the caller
            bool held;
            try { held = _mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { held = true; } // a helper that ended holding it: ours now
            if (!held)
            {
                _mutex.Dispose();
                throw new TimeoutException("another helper has held the Ctrl+Alt+Del screen's settings for 5 s");
            }
        }

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
