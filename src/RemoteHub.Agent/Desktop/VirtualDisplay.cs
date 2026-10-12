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
            return d.TryGetValue("instance", out var id) && id.Length > 0
                ? new State(id, d.GetValueOrDefault("oem") ?? "", d.GetValueOrDefault("trustAdded") == "1")
                : null;
        }
    }

    private static State? Load()
    {
        try { return File.Exists(StateFile) ? State.Parse(File.ReadAllText(StateFile)) : null; }
        catch { return null; }
    }

    public static bool Installed => Load() is { } s && Native.Exists(s.InstanceId);

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
        var instance = previous is { } p && Native.Exists(p.InstanceId) ? p.InstanceId : Native.CreateRootDevice(inf, HardwareId);
        Native.UpdateDriver(inf, HardwareId);
        Directory.CreateDirectory(AgentConfig.Folder);
        File.WriteAllText(StateFile, new State(instance, oem, trustAdded).Serialize());
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
            if (Native.Exists(s.InstanceId) && Pnputil($"/remove-device \"{s.InstanceId}\"") != 0) notes.Add("device not removed");
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
    public static string? Show(int width, int height, bool resizeOnly = false)
    {
        lock (Gate)
        {
            if (Load() is not { } s || !Native.Exists(s.InstanceId)) return "the virtual display isn't installed";
            (width, height) = Normalize(width, height);
            try
            {
                if (_shownBy > 0 && _size == (width, height)) { if (!resizeOnly) _shownBy++; return null; }
                WriteSettings(width, height);
                if (_shownBy == 0)
                {
                    if (resizeOnly) return "the virtual display is off";
                    SetEnabled(s.InstanceId, true);
                }
                else Reload();
                if (!resizeOnly) _shownBy++;
                _size = (width, height);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }
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
        var exit = Pnputil($"/{(on ? "enable" : "disable")}-device \"{instance}\"");
        if (exit != 0) throw new InvalidOperationException($"The virtual display couldn't be switched {(on ? "on" : "off")} (pnputil exit code {exit}).");
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

    private static int Pnputil(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

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
    }
}
