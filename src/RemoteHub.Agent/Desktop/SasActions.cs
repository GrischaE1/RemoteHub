using Microsoft.Win32;

namespace RemoteHub.Agent;

/// <summary>
/// While the privacy screen blocks the device's own mouse and keyboard (the admin's, and the one automatic sign-in puts
/// up), the Ctrl+Alt+Del screen – which Windows always shows, nothing can block the keys – offers nothing but Cancel:
/// Task Manager, Lock, Switch user, Change a password, Sign out and the power button are hidden with their policy
/// values, and put back exactly as they were (the value, or none) when local input is free again or the helper ends.
/// </summary>
/// <remarks>
/// <para>Per user (the session's user, HKU\&lt;sid&gt;): <c>DisableTaskMgr</c>, <c>DisableLockWorkstation</c>,
/// <c>DisableChangePassword</c> (Policies\System), <c>NoLogoff</c>, <c>NoClose</c> (Policies\Explorer). Per machine:
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
        [(SystemKey, "DisableTaskMgr"), (SystemKey, "DisableLockWorkstation"), (SystemKey, "DisableChangePassword"), (ExplorerKey, "NoLogoff"), (ExplorerKey, "NoClose")];
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
        using (var hive = _userHive(sid))
        {
            if (hive is null) return null;
            Set(hive, UserValues, UserRecord(sid));
        }
        Set(_machine, MachineValues, Path.Combine(_folder, MachineRecord));
        _sid = sid;
        return "Ctrl+Alt+Del screen: Task Manager, Lock, Switch user, Change a password, Sign out and the power button hidden while the device's mouse and keyboard are blocked";
    }

    /// <summary>Puts back what was there before (the machine value once no other session hides it).</summary>
    public string? Show()
    {
        if (_sid is not { } sid) return null;
        _sid = null;
        using var gate = Lock();
        using (var hive = _userHive(sid))
            if (hive is not null) Restore(hive, UserRecord(sid)); // signed out meanwhile: put back by the next helper or the service
        if (!Directory.EnumerateFiles(_folder, "sas-actions-S-*.txt").Any()) Restore(_machine, Path.Combine(_folder, MachineRecord));
        return "Ctrl+Alt+Del screen: its actions are back";
    }

    /// <summary>Service start (no helper runs): puts back whatever a helper left hidden. Null when there was nothing.</summary>
    public string? RestoreLeftovers()
    {
        if (!Directory.Exists(_folder)) return null;
        using var gate = Lock();
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
            Directory.CreateDirectory(_folder);
            File.WriteAllLines(record, lines);
        }
        foreach (var line in File.ReadAllLines(record))
        {
            var p = line.Split('|');
            if (p.Length != 3) continue;
            using var key = root.CreateSubKey(p[0], writable: true);
            key.SetValue(p[1], 1, RegistryValueKind.DWord);
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
        private readonly Mutex? _mutex;
        private readonly bool _held;

        public Gate(string name)
        {
            try
            {
                _mutex = new Mutex(false, name);
                try { _held = _mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { _held = true; } // a helper that ended holding it: ours now
            }
            catch (UnauthorizedAccessException) { /* without the gate, as before it existed */ }
        }

        public void Dispose()
        {
            if (_held) _mutex!.ReleaseMutex();
            _mutex?.Dispose();
        }
    }
}
