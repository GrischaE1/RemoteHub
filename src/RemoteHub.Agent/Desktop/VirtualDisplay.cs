using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace RemoteHub.Agent;

/// <summary>
/// Optional virtual monitor for devices without a monitor (headless servers): there the GPU often offers only 640×480,
/// so the remote desktop can't use the RemoteHub window's size. A third-party Indirect Display Driver (MIT, signed –
/// third-party\VirtualDisplayDriver) provides a monitor of any size; this class installs it, switches it on at the
/// viewer's size and off again, and removes it. All of it runs in the service (LocalSystem).
/// <list type="bullet">
/// <item>Install (MSI, "enroll"): checks the files' hashes, trusts the driver's publisher (Trusted Publishers – else
/// Windows asks before installing), adds the driver package, creates the device and leaves it switched off.</item>
/// <item>A desktop connection on a device that only offers a tiny resolution: <see cref="Show"/> writes the window's size
/// into the driver's settings and switches the device on (or makes the driver reload them); <see cref="Hide"/> switches
/// it off when the last connection that needed it ends.</item>
/// <item>Uninstall: removes the device, the driver package, the settings and the publisher trust (only if the agent added it).</item>
/// </list>
/// </summary>
internal static class VirtualDisplay
{
    public const string HardwareId = @"Root\MttVDD";
    private const string InfName = "MttVDD.inf";
    private const string PipeName = "MTTVirtualDisplayPipe";
    /// <summary>The signer of the driver package (SignPath Foundation).</summary>
    public const string PublisherThumbprint = "3CF8CF26D8BA266C3A483AB7D26D4A818E317D76";

    /// <summary>The files as shipped (third-party\VirtualDisplayDriver\24.12.24\README.md) – nothing else is installed.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["MttVDD.inf"] = "550D211FE481E74DFE3F9D724ED78BE48B3A9113405965D683D9373E8D672F5D",
        ["MttVDD.dll"] = "C9CA837F57A98FBD43BC416A7F535A95843626E7759EAF85CF0CD7CE334DBB05",
        ["mttvdd.cat"] = "08A0093FC9B2E32B287A6F8A77CA4DE0A31830D29FC33D2B13A918DC859468F6",
        ["SignPathFoundation.cer"] = "69DFD3E1CBD49F039463EDFE4C0D876C1DC7FDCD3AEF08700A41355DC6C15FDB",
    };

    /// <summary>The driver reads its settings from this fixed folder.</summary>
    public const string SettingsFolder = @"C:\VirtualDisplayDriver";
    private static string SettingsFile => Path.Combine(SettingsFolder, "vdd_settings.xml");
    public static string FilesFolder => Path.Combine(AppContext.BaseDirectory, "VirtualDisplay");
    private static string StateFile => Path.Combine(AgentConfig.Folder, "virtual-display.txt");

    /// <summary>What the agent installed (for the switch and the removal): device instance, driver package, publisher trust.</summary>
    internal sealed record State(string InstanceId, string OemInf, bool TrustAdded)
    {
        public string Serialize() => $"instance={InstanceId}\noem={OemInf}\ntrustAdded={(TrustAdded ? 1 : 0)}\n";

        public static State? Parse(string text)
        {
            var d = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            return d.TryGetValue("instance", out var id) && ValidInstanceId(id)
                ? new State(id, d.GetValueOrDefault("oem") ?? "", d.GetValueOrDefault("trustAdded") == "1")
                : null;
        }
    }

    /// <summary>
    /// A device instance ID as SetupAPI generates it (<c>ROOT\DISPLAY\0000</c>). The ID goes to pnputil on its command
    /// line, so anything else (quotes, spaces, a changed state file) is refused rather than passed on.
    /// </summary>
    internal static bool ValidInstanceId(string id) =>
        id.Length is > 0 and <= 200 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '\\' or '&' or '_' or '-' or '.' or '{' or '}' or '#');

    private static State? Load()
    {
        try { return File.Exists(StateFile) ? State.Parse(File.ReadAllText(StateFile)) : null; }
        catch { return null; }
    }

    /// <summary>
    /// The agent installed the virtual display. Not whether its device is there now: after a restart Windows may no
    /// longer report the switched-off device (D-VD-05), and <see cref="Show(int, int, bool)"/> then creates it again.
    /// </summary>
    public static bool Installed => Load() is not null;

    // ------------------------------------------------------------------ install / remove

    /// <summary>Installs (or updates) the driver and leaves the monitor switched off. Returns what was done (for the log).</summary>
    public static string Install()
    {
        VerifyFiles(FilesFolder);
        var previous = Load();
        var trustAdded = previous?.TrustAdded ?? false;
        using (var cert = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(FilesFolder, "SignPathFoundation.cer")))
        using (var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine))
        {
            if (!cert.Thumbprint.Equals(PublisherThumbprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The virtual display's publisher certificate isn't the expected one.");
            store.Open(OpenFlags.ReadWrite);
            if (store.Certificates.Find(X509FindType.FindByThumbprint, PublisherThumbprint, false).Count == 0)
            {
                store.Add(cert);
                trustAdded = true;
            }
        }
        var inf = Path.Combine(FilesFolder, InfName);
        var oem = Native.AddDriverPackage(inf);
        string instance;
        if (previous is { } p && Native.Exists(p.InstanceId)) instance = p.InstanceId;
        else
        {
            // A device node from before that Windows no longer reports would otherwise stay behind as a dead second monitor.
            if (previous is { } old && Native.Known(old.InstanceId)) Pnputil(["/remove-device", old.InstanceId], out _);
            instance = Native.CreateRootDevice(inf, HardwareId);
        }
        Native.UpdateDriver(inf, HardwareId);
        Save(new State(instance, oem, trustAdded));
        WriteSettings(1920, 1080);
        SetEnabled(instance, false);
        return $"Virtual display installed (device {instance}, driver package {oem}{(trustAdded ? ", publisher trusted" : "")}) – switched on only while a connection needs it";
    }

    /// <summary>Removes everything <see cref="Install"/> did. Null when nothing was installed, else what was done.</summary>
    public static string? Remove()
    {
        // Uninstall runs as its own process ("cleanup"), with no connection or switch-off of this one under way.
        {
            var s = Load();
            if (s is null) return null;
            var notes = new List<string>();
            if (Native.Known(s.InstanceId) && Pnputil(["/remove-device", s.InstanceId], out _) != 0) notes.Add("device not removed");
            if (s.OemInf.Length > 0 && !Native.RemoveDriverPackage(s.OemInf)) notes.Add($"driver package {s.OemInf} not removed");
            if (s.TrustAdded)
                try
                {
                    using var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine);
                    store.Open(OpenFlags.ReadWrite);
                    foreach (var c in store.Certificates.Find(X509FindType.FindByThumbprint, PublisherThumbprint, false)) store.Remove(c);
                }
                catch (Exception ex) { notes.Add("publisher trust not removed: " + ex.Message); }
            try { Directory.Delete(SettingsFolder, true); } catch { /* not there */ }
            try { File.Delete(StateFile); } catch { /* next time */ }
            Default.Forget();
            return "Virtual display removed" + (notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : "");
        }
    }

    /// <summary>The shipped files are exactly the ones in third-party\ (hash check) – before anything is trusted or installed.</summary>
    internal static void VerifyFiles(string folder)
    {
        foreach (var (name, hash) in Files)
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) throw new FileNotFoundException($"Virtual display file {name} is missing.", path);
            if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Virtual display file {name} isn't the shipped one (hash mismatch).");
        }
    }

    // ------------------------------------------------------------------ switching on and off

    /// <summary>
    /// One connection's hold on the virtual monitor (<see cref="NewLease"/>): counted once when the monitor is on for it,
    /// whichever way it was switched on, and released once at the connection's end.
    /// </summary>
    public sealed class Lease
    {
        internal bool Counted, Ended;
    }

    public static Lease NewLease() => new();

    /// <summary>The switch for this device: the virtual display the agent installed.</summary>
    private static readonly Switch Default = new(new WindowsDevice(), () => Load()?.InstanceId,
        id => { if (Load() is { } s) Save(s with { InstanceId = id }); }, WriteSettings, Thread.Sleep);

    /// <summary>
    /// Switches the virtual monitor on at <paramref name="width"/> × <paramref name="height"/> for the connection that
    /// holds <paramref name="lease"/>, or gives it that size. Null when it is on, else why not;
    /// <paramref name="note"/>: what had to be repaired, or what didn't work although it is on (for the log).
    /// </summary>
    public static string? Show(Lease lease, int width, int height, out string? note) => Default.Show(lease, width, height, out note);

    /// <summary>
    /// The connection that holds <paramref name="lease"/> ended: the monitor is switched off with the last one that had it
    /// on. The line for the log, null when it wasn't on for this connection; <paramref name="progress"/> gets a line for
    /// every try that fails, before the result.
    /// </summary>
    public static string? Hide(Lease lease, Action<string>? progress = null) => Default.Hide(lease, progress);

    /// <summary>
    /// Service start: switched off (a crash may have left it on) – in the background, so the service reports running
    /// at once: Windows gives a starting service 30 s, and pnputil can take long or hang for a device in use (Server
    /// 2025, 0.18.18: the service never came up). The lines for the log go to <paramref name="log"/>.
    /// </summary>
    public static Task ResetInBackground(Action<string> log) => Default.ResetInBackground(log);

    /// <summary>
    /// Counts the connections the virtual monitor is on for and switches it on and off. Two locks: the count (held only
    /// for a moment, so a connection's end is never held up by another one's device step) and the device (one pnputil
    /// call, driver pipe or re-create at a time). The order is always device, then count.
    /// </summary>
    internal sealed class Switch(IDevice device, Func<string?> instance, Action<string> saveInstance, Action<int, int> writeSettings, Action<int> wait)
    {
        private readonly object _count = new();
        private readonly object _device = new();
        private int _shownBy, _offGeneration;
        private (int W, int H) _size;

        /// <inheritdoc cref="VirtualDisplay.Show(Lease, int, int, out string?)"/>
        public string? Show(Lease lease, int width, int height, out string? note)
        {
            note = null;
            (width, height) = Normalize(width, height);
            lock (_device)
            {
                string? id;
                bool first;
                (int W, int H) size;
                lock (_count)
                {
                    if (lease.Ended) return "the connection has ended";
                    if ((id = instance()) is null) return "the virtual display isn't installed";
                    if (_shownBy > 0 && _size == (width, height)) { Count(lease); return null; }
                    first = _shownBy == 0;
                    size = _size;
                }
                try
                {
                    writeSettings(width, height);
                    if (first)
                    {
                        var on = SwitchOn(device, id);
                        note = on.Recovered;
                        // Hide, the service start and Remove act on the device that exists now.
                        if (on.InstanceId != id) saveInstance(id = on.InstanceId);
                    }
                    else
                        // On for another connection already: this one counts even if the driver doesn't take the new size –
                        // else the other's end would switch it off under this one (0.18.19: the pipe timed out).
                        try { device.Reload(); }
                        catch (IOException ex)
                        {
                            note = $"the virtual monitor kept its size {size.W}×{size.H}: {ex.Message}";
                            (width, height) = size;
                        }
                }
                catch (Exception ex) { return ex.Message; }

                int generation;
                lock (_count)
                {
                    if (!lease.Ended)
                    {
                        Count(lease);
                        _size = (width, height);
                        return null;
                    }
                    // The connection ended while its monitor was switched on: off again, unless another one has it on.
                    if (_shownBy > 0) return "the connection ended meanwhile";
                    generation = ++_offGeneration;
                }
                var off = SwitchOff(device, id, wait, removeIfStuck: true, () => StillOff(generation), _device);
                note = note is null ? off : note + "; " + off;
                return "the connection ended while the virtual monitor was switched on";
            }
        }

        /// <inheritdoc cref="VirtualDisplay.Hide(Lease, Action{string}?)"/>
        public string? Hide(Lease lease, Action<string>? progress = null)
        {
            int generation;
            string? id;
            lock (_count)
            {
                lease.Ended = true;
                if (!lease.Counted) return null;
                lease.Counted = false;
                _shownBy = Math.Max(0, _shownBy - 1);
                if (_shownBy > 0) return $"Virtual display stays on for {_shownBy} other connection(s)";
                generation = ++_offGeneration;
                id = instance();
            }
            if (id is null) return "Virtual display not switched off: it isn't installed any more";
            try { return SwitchOff(device, id, wait, removeIfStuck: true, () => StillOff(generation), _device, progress); }
            catch (Exception ex) { return $"Virtual display not switched off: {ex.Message}"; }
        }

        /// <inheritdoc cref="VirtualDisplay.ResetInBackground(Action{string})"/>
        public Task ResetInBackground(Action<string> log) => Task.Run(() =>
        {
            try
            {
                int generation;
                string? id;
                lock (_count)
                {
                    // A connection that came first switched it on and needs it: its end switches it off.
                    if (_shownBy > 0) return;
                    generation = ++_offGeneration;
                    id = instance();
                }
                // Not removed here: with no connection to wait for, a device that won't switch off is left for the next start.
                if (id is not null) log(SwitchOff(device, id, wait, removeIfStuck: false, () => StillOff(generation), _device, log));
            }
            catch (Exception ex) { log($"Virtual display not switched off at the service start: {ex.Message}"); }
        });

        /// <summary>Uninstall: nothing counts any more, and a switch-off under way ends.</summary>
        public void Forget()
        {
            lock (_count)
            {
                _shownBy = 0;
                _offGeneration++;
            }
        }

        private void Count(Lease lease)
        {
            if (lease.Counted) return;
            lease.Counted = true;
            _shownBy++;
        }

        /// <summary>The switch-off started as <paramref name="generation"/> is still wanted: no connection has it on, no later one began.</summary>
        private bool StillOff(int generation)
        {
            lock (_count) return _shownBy == 0 && _offGeneration == generation;
        }
    }

    /// <summary>What switching the device on needs from Windows – separate so the recovery can be tested without a driver.</summary>
    internal interface IDevice
    {
        /// <summary>Windows reports the device now (it is in the device tree).</summary>
        bool Present(string instance);
        /// <summary>The device is on and its driver runs.</summary>
        bool Started(string instance);
        /// <summary>
        /// The device counts as on as Windows shows it (Get-PnpDevice: OK) – reported and without a problem such as
        /// "disabled". What switching off has to change.
        /// </summary>
        bool On(string instance);
        /// <summary>Every device Windows reports now with the driver's hardware ID (after a re-create there may be two).</summary>
        IReadOnlyList<string> Nodes();
        /// <summary>The device's state in words, for the log.</summary>
        string Describe(string instance);
        /// <summary>pnputil's exit code; <paramref name="said"/>: why it failed.</summary>
        int SetEnabled(string instance, bool on, out string said);
        /// <summary>Windows enumerates its devices again (<c>pnputil /scan-devices</c>).</summary>
        void Rescan();
        /// <summary>Removes the device node, also one Windows no longer reports; false if it stays.</summary>
        bool Remove(string instance);
        /// <summary>Creates the device and installs its driver, as at install (which starts it); returns its instance ID.</summary>
        string Create();
        /// <summary>The running driver re-reads its settings.</summary>
        void Reload();
    }

    /// <summary>The device's instance ID once it is on (a new one when it had to be created again), and what was repaired.</summary>
    internal sealed record SwitchedOn(string InstanceId, string? Recovered);

    /// <summary>
    /// Switches the device on. Some devices can't be switched (on Hyper-V VMs pnputil answers "not supported") and stay
    /// on: then the running driver re-reads its settings – the window's size – through its control pipe instead.
    /// After a restart of the device Windows may no longer report the switched-off device, and enabling it fails with
    /// 1167 "The device is not connected" (D-VD-05, Windows 11 and Server 2025): then Windows enumerates its devices
    /// again, and if that doesn't bring it back the device node is removed and created again as at install, which
    /// starts it. Throws when nothing switched it on.
    /// </summary>
    internal static SwitchedOn SwitchOn(IDevice device, string instance)
    {
        string why;
        if (device.Present(instance))
        {
            var exit = device.SetEnabled(instance, true, out var said);
            if (exit == 0) return new(instance, null);
            if (device.Started(instance)) { device.Reload(); return new(instance, null); }
            why = Failed(exit, said);
        }
        else why = "the device isn't there";

        device.Rescan();
        if (device.Present(instance) && device.SetEnabled(instance, true, out _) == 0)
            return new(instance, $"virtual display device {instance} came back with a device rescan ({why})");

        try
        {
            var removed = device.Remove(instance);
            var created = device.Create();
            if (!device.Started(created))
            {
                var exit = device.SetEnabled(created, true, out var said);
                if (exit != 0 && !device.Started(created)) throw new InvalidOperationException($"the new device {created} didn't start ({Failed(exit, said)})");
            }
            return new(created, $"virtual display device created again as {created} ({instance}: {why}{(removed ? "" : "; its old device node couldn't be removed")})");
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The virtual display couldn't be switched on ({why}); creating its device again failed: {ex.Message}", ex);
        }
    }

    private static string Failed(int exit, string said) => $"pnputil exit code {exit}{(said.Length > 0 ? ": " + said : "")}";

    /// <summary>The device as Windows has it: config manager, pnputil and SetupAPI.</summary>
    private sealed class WindowsDevice : IDevice
    {
        public bool Present(string instance) => Native.Exists(instance);
        public bool Started(string instance) => Native.IsStarted(instance);
        public bool On(string instance) => Native.Status(instance) is { } s && s.Problem == 0 && (s.Status & 0x400 /* DN_HAS_PROBLEM */) == 0;

        public IReadOnlyList<string> Nodes()
        {
            try { return Native.PresentWithHardwareId(HardwareId); }
            catch (Win32Exception) { return []; } // then only the one in the state file is looked at
        }

        public string Describe(string instance) => Native.Status(instance) is not { } s
            ? $"{instance}: not reported by Windows"
            : $"{instance}: {((s.Status & 0x8) != 0 ? "running" : "not running")}{(s.Problem != 0 ? $", problem {s.Problem}{(s.Problem == 22 ? " (disabled)" : "")}" : "")}";

        public int SetEnabled(string instance, bool on, out string said) => Pnputil([on ? "/enable-device" : "/disable-device", instance], out said);
        public void Rescan() => Pnputil(["/scan-devices"], out _);
        public bool Remove(string instance) => !Native.Known(instance) || Pnputil(["/remove-device", instance], out _) == 0;

        public string Create()
        {
            // The same hash-checked files as at install: nothing else from the agent's folder is installed as a driver.
            VerifyFiles(FilesFolder);
            var inf = Path.Combine(FilesFolder, InfName);
            var instance = Native.CreateRootDevice(inf, HardwareId);
            Native.UpdateDriver(inf, HardwareId);
            return instance;
        }

        public void Reload() => VirtualDisplay.Reload();
    }

    private static void Save(State state)
    {
        // In one step: a torn file loses the instance ID, and with it the switch and the removal of the device.
        DurableFile.WriteAllText(StateFile, state.Serialize());
    }

    /// <summary>The size the virtual monitor gets for a window: even, 800×600 to 7680×4320.</summary>
    public static (int W, int H) Normalize(int width, int height) => (Math.Clamp(width & ~1, 800, 7680), Math.Clamp(height & ~1, 600, 4320));

    /// <summary>Waits (ms) before each further attempt to switch the device off.</summary>
    internal static readonly int[] OffRetryWaits = [1000, 2000, 3000];

    /// <summary>
    /// Switches the virtual display off and returns the line for the log. Off means what Windows shows (Get-PnpDevice:
    /// not OK): every device with the driver's hardware ID that Windows reports – after a re-create there can be another
    /// one than the state file's (Server 2025: the device stayed on while the agent took it for off) – plus the state
    /// file's, is switched off, each tried again a few times, since right after a connection ends the device can still
    /// be in use (the helper's picture, the monitor it made primary, a driver just installed). One that still runs is
    /// removed (<paramref name="removeIfStuck"/>), which <see cref="SwitchOn"/> creates again when a connection needs it.
    /// <para>
    /// Each step runs under <paramref name="gate"/> – the device lock of <see cref="Switch"/> – and only while
    /// <paramref name="stillWanted"/>: the waits between tries hold no lock, so a connection switching it on waits at
    /// most for one pnputil call (<see cref="PnputilTimeout"/>), and once it has switched it on the tries end.
    /// </para>
    /// </summary>
    internal static string SwitchOff(IDevice device, string instance, Action<int> wait, bool removeIfStuck = true, Func<bool>? stillWanted = null, object? gate = null, Action<string>? progress = null)
    {
        var wanted = stillWanted ?? (() => true);
        var lockObject = gate ?? new object();
        T Step<T>(Func<T> action)
        {
            lock (lockObject)
            {
                if (!wanted()) throw new SwitchedOnMeanwhile();
                return action();
            }
        }

        try
        {
            var nodes = Step(() => device.Nodes().Append(instance).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            var on = Step(() => nodes.Where(device.On).ToList());
            if (on.Count == 0)
                return $"Virtual display off: no device to switch off ({string.Join("; ", nodes.Select(n => Step(() => device.Describe(n))))})";
            var notes = new List<string>();
            var allOff = true;
            foreach (var node in on)
            {
                var (off, note) = SwitchOffNode(node);
                allOff &= off;
                notes.Add(note);
            }
            return $"{(allOff ? "Virtual display off" : "Virtual display not switched off")}: {string.Join("; ", notes)}";
        }
        catch (SwitchedOnMeanwhile) { return "Virtual display left on: a connection switched it on again meanwhile"; }

        (bool Off, string Note) SwitchOffNode(string node)
        {
            var why = "";
            for (var attempt = 0; ; attempt++)
            {
                var (exit, said) = Step(() => (device.SetEnabled(node, false, out var s), s));
                if (!Step(() => device.On(node)))
                    return (true, $"{node} switched off{(attempt > 0 ? $" on try {attempt + 1}" : "")}{(exit != 0 ? $" ({Failed(exit, said)})" : "")} – {Step(() => device.Describe(node))}");
                why = exit == 0 ? "pnputil switched it off, but Windows still shows it on" : Failed(exit, said);
                progress?.Invoke($"Virtual display: {node} still on after try {attempt + 1} ({why})");
                if (attempt == OffRetryWaits.Length) break;
                wait(OffRetryWaits[attempt]); // no lock held: a connection may switch it on meanwhile
            }
            var tries = OffRetryWaits.Length + 1;
            if (!removeIfStuck)
                return (false, $"{node} not switched off after {tries} tries ({why}) – it stays on until a connection that used it ends or the agent starts again");
            return Step(() => device.Remove(node) && !device.On(node))
                ? (true, $"{node} not switched off after {tries} tries ({why}) – removed instead, created again when a connection needs it")
                : (false, $"{node} not switched off after {tries} tries ({why}) and couldn't be removed – it stays on until the agent starts again");
        }
    }

    private sealed class SwitchedOnMeanwhile : Exception;

    private static void SetEnabled(string instance, bool on)
    {
        var exit = Pnputil([on ? "/enable-device" : "/disable-device", instance], out var said);
        if (exit != 0) throw new InvalidOperationException($"The virtual display couldn't be switched {(on ? "on" : "off")} ({Failed(exit, said)}).");
    }

    /// <summary>The driver's settings: one monitor, the window's size first, a few common sizes, 60 Hz.</summary>
    internal static XDocument Settings(int width, int height)
    {
        (int, int)[] sizes = [(width, height), (1920, 1080), (1600, 900), (1280, 720), (1024, 768)];
        return new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("vdd_settings",
                new XElement("monitors", new XElement("count", 1)),
                new XElement("gpu", new XElement("friendlyname", "default")),
                new XElement("global", new XElement("g_refresh_rate", 60)),
                new XElement("resolutions", sizes.Distinct().Select(r => new XElement("resolution",
                    new XElement("width", r.Item1), new XElement("height", r.Item2), new XElement("refresh_rate", 60)))),
                new XElement("options",
                    new XElement("CustomEdid", false), new XElement("PreventSpoof", false), new XElement("EdidCeaOverride", false),
                    new XElement("HardwareCursor", true), new XElement("SDR10bit", false), new XElement("HDRPlus", false),
                    new XElement("logging", false), new XElement("debuglogging", false))));
    }

    private static void WriteSettings(int width, int height) => WriteSettings(SettingsFolder, width, height);

    /// <summary>
    /// Writes the driver's settings as the service (LocalSystem) into <paramref name="folder"/>, which is at the drive
    /// root where any signed-in user may create folders: it is made ours first (<see cref="PrepareSettingsFolder"/>), and
    /// a file already there is removed rather than written through – it could be a hard link to a system file.
    /// </summary>
    internal static void WriteSettings(string folder, int width, int height)
    {
        PrepareSettingsFolder(folder);
        var file = Path.Combine(folder, "vdd_settings.xml");
        File.Delete(file);
        using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        Settings(width, height).Save(stream);
    }

    /// <summary>
    /// The settings folder as only the agent made it: a real folder (not a junction or symbolic link), owned by SYSTEM or
    /// Administrators, with permissions that let nobody else write. Anything else in its place – a link planted to
    /// redirect the service's write, a folder or file a user made – is removed (a link itself, never what it points to)
    /// and the folder is created again with those permissions. The driver (a UMDF host, LocalService) only reads it.
    /// </summary>
    internal static void PrepareSettingsFolder(string folder)
    {
        if (File.Exists(folder)) File.Delete(folder);
        if (Directory.Exists(folder) && !IsOurs(new DirectoryInfo(folder))) Directory.Delete(folder, recursive: true); // a link is removed, not followed
        SettingsFolderAcl().CreateDirectory(folder); // nothing happens if someone made it again meanwhile – checked next
        if (!IsOurs(new DirectoryInfo(folder)))
            throw new IOException($"{folder} was replaced by someone else while the agent set it up – the virtual display stays off");
    }

    /// <summary>The accounts that may write the settings folder: SYSTEM, Administrators, and the agent's own account (SYSTEM; the tests' user).</summary>
    private static SecurityIdentifier[] Writers()
    {
        using var me = WindowsIdentity.GetCurrent();
        return [new(WellKnownSidType.LocalSystemSid, null), new(WellKnownSidType.BuiltinAdministratorsSid, null), me.User!];
    }

    private static DirectorySecurity SettingsFolderAcl()
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var writer in Writers().Distinct())
            acl.AddAccessRule(new FileSystemAccessRule(writer, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (var reader in new[] { WellKnownSidType.LocalServiceSid, WellKnownSidType.AuthenticatedUserSid, WellKnownSidType.BuiltinUsersSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(reader, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }

    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

    internal static bool IsOurs(DirectoryInfo folder)
    {
        if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        var acl = folder.GetAccessControl();
        var writers = Writers();
        if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !writers.Contains(owner)) return false;
        if (!acl.AreAccessRulesProtected) return false;
        return acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .All(r => r.AccessControlType == AccessControlType.Deny || (r.FileSystemRights & WriteRights) == 0 || writers.Contains((SecurityIdentifier)r.IdentityReference));
    }

    /// <summary>The driver re-reads its settings (new resolutions) – through its control pipe.</summary>
    private static void Reload() => Reload(PipeName);

    /// <summary>
    /// Sends the reload command, only to the driver: any user could create a pipe with this name before the driver does.
    /// The connection is anonymous, so such a server can't act as the service, and the command goes only to the driver's
    /// host process (<see cref="IsDriverHost"/>).
    /// </summary>
    internal static void Reload(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Anonymous);
        // "The operation has timed out." alone (0.18.19) didn't say what: the driver's control pipe wasn't there or busy.
        try { pipe.Connect(3000); }
        catch (TimeoutException) { throw new IOException("the driver's control pipe didn't answer within 3 s"); }
        var server = Native.PipeServerImage(pipe.SafePipeHandle);
        if (!IsDriverHost(server))
            throw new IOException($"the driver's control pipe is served by another program ({Path.GetFileName(server) ?? "unknown"}) – nothing was sent");
        var command = Encoding.Unicode.GetBytes("RELOAD_DRIVER");
        pipe.Write(command, 0, command.Length);
        pipe.Flush();
    }

    /// <summary>Windows' host for user-mode drivers (UMDF), where the Indirect Display Driver runs.</summary>
    internal static bool IsDriverHost(string? image) =>
        image is not null && string.Equals(Path.GetFullPath(image), Path.Combine(Environment.SystemDirectory, "WUDFHost.exe"), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs pnputil; <paramref name="said"/>: its last lines (why it failed), on one line. The arguments go as a list,
    /// so an instance ID always stays one argument.
    /// </summary>
    private static int Pnputil(string[] arguments, out string said)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"));
        foreach (var a in arguments) start.ArgumentList.Add(a);
        var exit = Run(start, PnputilTimeout, out var output);
        said = exit == TimedOut ? output : LastLines(output);
        return exit;
    }

    /// <summary>How long one pnputil call may take: removing or switching a device that is in use can hang.</summary>
    internal static readonly TimeSpan PnputilTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The exit code <see cref="Run"/> gives a program it stopped (ERROR_TIMEOUT).</summary>
    internal const int TimedOut = 1460;

    /// <summary>
    /// Runs a program and returns its exit code; <paramref name="output"/>: what it wrote. A program still running after
    /// <paramref name="timeout"/> is stopped (with anything it started) and gives <see cref="TimedOut"/>, with the
    /// output saying so – a caller holding the switch's lock is never held up longer than that.
    /// </summary>
    internal static int Run(ProcessStartInfo start, TimeSpan timeout, out string output)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var p = Process.Start(start) ?? throw new InvalidOperationException($"{Path.GetFileName(start.FileName)} didn't start.");
        // Both at once: a full stderr pipe would otherwise stall the program. Each on a thread of its own, because the
        // pipes are read with blocking calls: on the thread pool, a busy pool could keep the reads from finishing within
        // the wait below, and the output was lost although the program had written it.
        var stdout = new OutputReader(p.StandardOutput);
        var stderr = new OutputReader(p.StandardError);
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* ended meanwhile */ }
            p.WaitForExit(2000);
            output = $"{Path.GetFileNameWithoutExtension(start.FileName)} didn't answer within {timeout.TotalSeconds:0} s and was stopped";
            return TimedOut;
        }
        // The pipes close with the program; a child it left running mustn't hold this up either.
        var deadline = Stopwatch.StartNew();
        output = stdout.Text(PipeWait) + "\n" + stderr.Text(PipeWait - deadline.Elapsed);
        return p.ExitCode;
    }

    /// <summary>How long <see cref="Run"/> waits for the output once the program has ended.</summary>
    private static readonly TimeSpan PipeWait = TimeSpan.FromSeconds(2);

    /// <summary>Reads a redirected stream to its end on a background thread of its own.</summary>
    private sealed class OutputReader
    {
        private readonly Thread _thread;
        private string _text = "";

        public OutputReader(StreamReader reader)
        {
            _thread = new Thread(() =>
            {
                try { _text = reader.ReadToEnd(); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* the process was disposed while a child held the pipe */ }
            }) { IsBackground = true, Name = "VirtualDisplay pipe" };
            _thread.Start();
        }

        /// <summary>What was read, or "" if the stream didn't end within <paramref name="wait"/>.</summary>
        public string Text(TimeSpan wait) => _thread.Join(wait < TimeSpan.Zero ? TimeSpan.Zero : wait) ? _text : "";
    }

    /// <summary>pnputil's last two lines on one line – where it says why it failed.</summary>
    internal static string LastLines(string output) =>
        string.Join(" ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(2));

    /// <summary>SetupAPI / config manager: driver package, root-enumerated device.</summary>
    private static class Native
    {
        public static string AddDriverPackage(string inf)
        {
            var name = new char[260];
            if (!SetupCopyOEMInfW(inf, null, 1 /* SPOST_PATH */, 0, name, name.Length, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The virtual display's driver package couldn't be added");
            return Path.GetFileName(new string(name).TrimEnd('\0'));
        }

        public static bool RemoveDriverPackage(string oemInf) => SetupUninstallOEMInfW(oemInf, 1 /* SUOI_FORCEDELETE */, IntPtr.Zero);

        /// <summary>The program of the process that serves the pipe at the other end, or null if it can't be read.</summary>
        public static string? PipeServerImage(Microsoft.Win32.SafeHandles.SafePipeHandle pipe)
        {
            if (!GetNamedPipeServerProcessId(pipe, out var pid)) return null;
            var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (process == IntPtr.Zero) return null;
            try
            {
                var name = new char[1024];
                var size = name.Length;
                return QueryFullProcessImageNameW(process, 0, name, ref size) ? new string(name, 0, size) : null;
            }
            finally { CloseHandle(process); }
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, [Out] char[] name, ref int size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

        public static void UpdateDriver(string inf, string hardwareId)
        {
            if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hardwareId, inf, 1 /* INSTALLFLAG_FORCE */, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The virtual display's driver couldn't be installed");
        }

        public static bool Exists(string instanceId) => CM_Locate_DevNodeW(out _, instanceId, 0) == 0;

        /// <summary>The device node is registered, whether or not Windows reports it now (CM_LOCATE_DEVNODE_PHANTOM).</summary>
        public static bool Known(string instanceId) => CM_Locate_DevNodeW(out _, instanceId, 1) == 0;

        /// <summary>The device is on and its driver runs (DN_STARTED).</summary>
        public static bool IsStarted(string instanceId) =>
            CM_Locate_DevNodeW(out var node, instanceId, 0) == 0 && CM_Get_DevNode_Status(out var status, out _, node, 0) == 0 && (status & 0x8) != 0;

        /// <summary>The node as Windows reports it now: not there (null), else its status flags and problem code.</summary>
        public static (uint Status, uint Problem)? Status(string instanceId) =>
            CM_Locate_DevNodeW(out var node, instanceId, 0) == 0 && CM_Get_DevNode_Status(out var status, out var problem, node, 0) == 0
                ? (status, problem) : null;

        /// <summary>
        /// The instance IDs of every root-enumerated device Windows reports now with the hardware ID – there can be more
        /// than the one in the state file (a device created again, one added by hand).
        /// </summary>
        public static List<string> PresentWithHardwareId(string hardwareId)
        {
            var found = new List<string>();
            var set = SetupDiGetClassDevsW(IntPtr.Zero, "ROOT", IntPtr.Zero, 0x2 /* DIGCF_PRESENT */ | 0x4 /* DIGCF_ALLCLASSES */);
            if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "The devices couldn't be listed");
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                var buffer = new byte[2048];
                var id = new char[400];
                for (var i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, 1 /* SPDRP_HARDWAREID */, out _, buffer, buffer.Length, out var size)) continue;
                    var ids = Encoding.Unicode.GetString(buffer, 0, Math.Min(size, buffer.Length)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    if (!ids.Contains(hardwareId, StringComparer.OrdinalIgnoreCase)) continue;
                    if (SetupDiGetDeviceInstanceIdW(set, ref data, id, id.Length, out _)) found.Add(new string(id).TrimEnd('\0'));
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return found;
        }

        /// <summary>Creates a root-enumerated device with the hardware ID; returns its instance ID.</summary>
        public static string CreateRootDevice(string inf, string hardwareId)
        {
            var className = new char[64];
            if (!SetupDiGetINFClassW(inf, out var classGuid, className, className.Length, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The virtual display's driver class couldn't be read");
            var set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
            if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiCreateDeviceInfoW(set, new string(className).TrimEnd('\0'), ref classGuid, null, IntPtr.Zero, 1 /* DICD_GENERATE_ID */, ref data))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The virtual display device couldn't be created");
                var ids = Encoding.Unicode.GetBytes(hardwareId + "\0\0");
                if (!SetupDiSetDeviceRegistryPropertyW(set, ref data, 1 /* SPDRP_HARDWAREID */, ids, ids.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!SetupDiCallClassInstaller(0x19 /* DIF_REGISTERDEVICE */, set, ref data))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The virtual display device couldn't be registered");
                var id = new char[400];
                if (!SetupDiGetDeviceInstanceIdW(set, ref data, id, id.Length, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return new string(id).TrimEnd('\0');
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupCopyOEMInfW(string source, string? mediaLocation, int mediaType, int style, char[] destination, int size, out int required, IntPtr component);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupUninstallOEMInfW(string infName, int flags, IntPtr reserved);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetINFClassW(string inf, out Guid classGuid, char[] className, int size, IntPtr required);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwnd);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string name, ref Guid classGuid, string? description, IntPtr hwnd, int flags, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int property, byte[] buffer, int size);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiCallClassInstaller(int function, IntPtr set, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, char[] id, int size, out int required);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int property, out int type, byte[] buffer, int size, out int required);
        [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwnd, string hardwareId, string inf, int flags, out bool reboot);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out int devInst, string instanceId, int flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, int devInst, int flags);
    }
}
