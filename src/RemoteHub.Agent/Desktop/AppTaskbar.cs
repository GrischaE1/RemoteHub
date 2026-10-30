using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RemoteHub.Agent;

/// <summary>
/// While app tabs are open in a session, its user's "show my taskbar on all displays" (<c>MMTaskbarEnabled</c>) is off,
/// so the tabs' monitors have no taskbar (docs/app-tabs-design.md §5); Explorer takes it without a restart when told
/// (WM_SETTINGCHANGE "TraySettings", §5.1). It is put back exactly as it was – the value, or none – by the last helper
/// that turned it off, by the next helper in that session after one ended abruptly, or by the service when it starts.
/// Done the way <see cref="SasActions"/> does it: one owner file per helper, the original in a record next to agent.json.
/// </summary>
internal sealed class AppTaskbar
{
    internal const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", Name = "MMTaskbarEnabled";
    private readonly string _folder;
    private readonly Func<string, RegistryKey?> _userHive;
    private readonly string _instance = Guid.NewGuid().ToString("N")[..8];
    private string? _sid;

    /// <param name="userHive">The root of a user's hive by SID, writable (tests: a scratch key).</param>
    public AppTaskbar(string? folder = null, Func<string, RegistryKey?>? userHive = null)
    {
        _folder = folder ?? AgentConfig.Folder;
        _userHive = userHive ?? (sid => Registry.Users.OpenSubKey(sid, writable: true));
    }

    /// <summary>Turned off by this helper now.</summary>
    public bool Off => _sid is not null;

    /// <summary>Turns it off for <paramref name="sid"/> (the session's user). False when their hive isn't loaded yet – try again.</summary>
    public bool TurnOff(string sid)
    {
        if (_sid is not null) return true;
        using var gate = new NamedGate(@"Global\RemoteHubAgentAppTaskbar");
        using var hive = _userHive(sid);
        if (hive is null) return false;
        Directory.CreateDirectory(_folder);
        File.WriteAllText(OwnerFile(sid), ""); // first: whatever changes below, someone can put it back
        _sid = sid;
        var record = Record(sid);
        if (!File.Exists(record))
        {
            using var read = hive.OpenSubKey(Key);
            DurableFile.WriteAllLines(record, [read?.GetValue(Name) is int v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "absent"]);
        }
        using (var key = hive.CreateSubKey(Key, writable: true)) key.SetValue(Name, 0, RegistryValueKind.DWord);
        return true;
    }

    /// <summary>This helper is done: put back unless another live helper still has it off for this user. True when put back.</summary>
    public bool PutBack()
    {
        if (_sid is not { } sid) return false;
        using var gate = new NamedGate(@"Global\RemoteHubAgentAppTaskbar");
        File.Delete(OwnerFile(sid));
        _sid = null;
        if (LiveOwners(sid).Any()) return false;
        using var hive = _userHive(sid);
        return hive is not null && Restore(hive, Record(sid));
    }

    /// <summary>A helper's start: puts back what a helper that ended abruptly left off, unless a live helper still has it off.</summary>
    public bool Reconcile(string sid)
    {
        using var gate = new NamedGate(@"Global\RemoteHubAgentAppTaskbar");
        if (LiveOwners(sid).Any()) return false;
        using var hive = _userHive(sid);
        return hive is not null && Restore(hive, Record(sid));
    }

    /// <summary>Service start (no helper runs): puts back whatever was left off. Null when there was nothing.</summary>
    public string? RestoreLeftovers()
    {
        if (!Directory.Exists(_folder)) return null;
        using var gate = new NamedGate(@"Global\RemoteHubAgentAppTaskbar");
        foreach (var owner in Directory.GetFiles(_folder, "taskbar-owner-*.txt")) File.Delete(owner);
        var done = false;
        foreach (var record in Directory.GetFiles(_folder, "taskbar-S-*.txt"))
        {
            using var hive = _userHive(Path.GetFileNameWithoutExtension(record)["taskbar-".Length..]);
            if (hive is not null) done |= Restore(hive, record); // not loaded: kept until that user is back
        }
        return done ? "App tabs: the taskbar on other monitors is back as the user had it" : null;
    }

    /// <summary>Tells Explorer in this session that the taskbar settings changed (from the helper, on its desktop).</summary>
    public static void Announce() => SendMessageTimeoutW(0xFFFF /* HWND_BROADCAST */, 0x1A /* WM_SETTINGCHANGE */, 0, "TraySettings", 0x2 /* SMTO_ABORTIFHUNG */, 3000, out _);

    private string Record(string sid) => Path.Combine(_folder, $"taskbar-{sid}.txt");
    private string OwnerFile(string sid) => Path.Combine(_folder, $"taskbar-owner-{sid}-{Environment.ProcessId}-{_instance}.txt");

    private IEnumerable<string> LiveOwners(string sid)
    {
        var self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        var live = new List<string>();
        if (!Directory.Exists(_folder)) return live;
        foreach (var file in Directory.GetFiles(_folder, $"taskbar-owner-{sid}-*.txt"))
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
        catch (ArgumentException) { return false; }
    }

    private static bool Restore(RegistryKey hive, string record)
    {
        if (!File.Exists(record)) return false;
        var was = File.ReadAllLines(record).FirstOrDefault() ?? "absent";
        using (var key = hive.CreateSubKey(Key, writable: true))
        {
            if (int.TryParse(was, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v)) key.SetValue(Name, v, RegistryValueKind.DWord);
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
        File.Delete(record);
        return true;
    }

    /// <summary>One helper (or the service) at a time; nothing is changed without it.</summary>
    private sealed class NamedGate : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly bool _held;

        public NamedGate(string name)
        {
            _mutex = new Mutex(false, name);
            try { _held = _mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { _held = true; } // its holder died: the state is in the files, not in the mutex
            if (!_held)
            {
                _mutex.Dispose();
                throw new TimeoutException("Another helper kept the taskbar setting locked.");
            }
        }

        public void Dispose()
        {
            if (_held) _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeoutW(nint window, uint message, nint w, string l, uint flags, uint timeout, out nint result);
}
