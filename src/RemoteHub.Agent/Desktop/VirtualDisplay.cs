using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        lock (Gate)
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
            _shownBy = 0;
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

    private static readonly object Gate = new();
    private static int _shownBy;
    private static (int W, int H) _size;

    /// <summary>
    /// Switches the virtual monitor on at <paramref name="width"/> × <paramref name="height"/> for one more connection,
    /// or resizes it (the driver reloads its settings). Null on success, else why not.
    /// </summary>
    public static string? Show(int width, int height, bool resizeOnly = false) => Show(width, height, resizeOnly, out _);

    /// <inheritdoc cref="Show(int, int, bool)"/>
    /// <param name="recovered">What had to be repaired to switch it on (for the log), else null.</param>
    public static string? Show(int width, int height, bool resizeOnly, out string? recovered)
    {
        recovered = null;
        lock (Gate)
        {
            if (Load() is not { } s) return "the virtual display isn't installed";
            (width, height) = Normalize(width, height);
            try
            {
                if (_shownBy > 0 && _size == (width, height)) { if (!resizeOnly) _shownBy++; return null; }
                WriteSettings(width, height);
                if (_shownBy == 0)
                {
                    if (resizeOnly)
                    {
                        if (!Native.IsStarted(s.InstanceId)) return "the virtual display is off";
                        Reload();
                    }
                    else
                    {
                        var on = SwitchOn(new WindowsDevice(), s.InstanceId);
                        recovered = on.Recovered;
                        // Hide, Reset and Remove act on the device that exists now.
                        if (on.InstanceId != s.InstanceId) Save(s with { InstanceId = on.InstanceId });
                    }
                }
                else Reload();
                if (!resizeOnly) _shownBy++;
                _size = (width, height);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }
    }

    /// <summary>What switching the device on needs from Windows – separate so the recovery can be tested without a driver.</summary>
    internal interface IDevice
    {
        /// <summary>Windows reports the device now (it is in the device tree).</summary>
        bool Present(string instance);
        /// <summary>The device is on and its driver runs.</summary>
        bool Started(string instance);
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

    /// <summary>A connection that asked for it ended: switched off with the last one.</summary>
    public static void Hide()
    {
        lock (Gate)
        {
            if (_shownBy == 0 || --_shownBy > 0) return;
            if (Load() is { } s) try { SetEnabled(s.InstanceId, false); } catch { /* switched off at the next start */ }
        }
    }

    /// <summary>Service start: switched off (a crash may have left it on).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _shownBy = 0;
            if (Load() is { } s && Native.Exists(s.InstanceId)) try { SetEnabled(s.InstanceId, false); } catch { /* next start */ }
        }
    }

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

    private static void WriteSettings(int width, int height)
    {
        Directory.CreateDirectory(SettingsFolder);
        Settings(width, height).Save(SettingsFile);
    }

    /// <summary>The driver re-reads its settings (new resolutions) – through its control pipe.</summary>
    private static void Reload()
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        pipe.Connect(3000);
        var command = Encoding.Unicode.GetBytes("RELOAD_DRIVER");
        pipe.Write(command, 0, command.Length);
        pipe.Flush();
    }

    /// <summary>
    /// Runs pnputil; <paramref name="said"/>: its last lines (why it failed), on one line. The arguments go as a list,
    /// so an instance ID always stays one argument.
    /// </summary>
    private static int Pnputil(string[] arguments, out string said)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in arguments) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var error = p.StandardError.ReadToEndAsync(); // both at once: a full stderr pipe would otherwise stall pnputil
        var output = p.StandardOutput.ReadToEnd() + "\n" + error.GetAwaiter().GetResult();
        p.WaitForExit();
        said = LastLines(output);
        return p.ExitCode;
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
        [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwnd, string hardwareId, string inf, int flags, out bool reboot);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out int devInst, string instanceId, int flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, int devInst, int flags);
    }
}
