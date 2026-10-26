using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace RemoteHub.Agent;

/// <summary>
/// Device-side setup, run by the MSI as LocalSystem:
/// <list type="bullet">
/// <item><c>RemoteHubAgent.exe enroll &lt;file | folder | msi path&gt;</c> – enrolls with a <c>.rhagent</c> file: a new device
/// ID, the device's own key and certificate in LocalMachine\My (key not exportable), <c>agent.json</c> with the
/// enrollment secret, firewall rule; then deletes the file. Without a file an already enrolled agent keeps its identity
/// (plain update).</item>
/// <item><c>RemoteHubAgent.exe cleanup</c> – on uninstall: firewall rule, certificate and configuration are removed (logs stay).</item>
/// <item><c>RemoteHubAgent.exe auto-sign-in on|off</c> – the policy values that allow automatic sign-in (<see cref="AutoSignInSetup"/>).</item>
/// </list>
/// </summary>
public static class Enrollment
{
    public const string FirewallRuleName = "RemoteHub Agent";

    public static int Run(string[] args, AgentLog log)
    {
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "enroll" => Enroll(args.Length > 1 ? args[1] : "", Option(args, "virtual-display"), log, Option(args, "auto-sign-in"),
                    Broker(args)),
                "cleanup" => Cleanup(log),
                "auto-sign-in" => AutoSignIn(args.Length > 1 ? args[1] : "", log),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'. Use: enroll <file|folder> [broker-url=… broker-secret=… broker-only=1] | cleanup | auto-sign-in on|off"),
            };
        }
        catch (Exception ex)
        {
            log.Write($"{args[0]} failed: {ex.Message}");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>"name=value" after the command and its first argument (from the MSI); null when not given.</summary>
    private static string? Option(string[] args, string name) =>
        args.Skip(2).FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..];

    /// <summary><c>auto-sign-in on|off</c>: allow or stop automatic sign-in for the agent's allowed groups (see <see cref="AutoSignInSetup"/>).</summary>
    private static int AutoSignIn(string onOff, AgentLog log)
    {
        var result = onOff.ToLowerInvariant() switch
        {
            "on" => AutoSignInSetup.On(AllowedGroups()),
            "off" => AutoSignInSetup.Off(),
            _ => throw new ArgumentException("Use: auto-sign-in on|off"),
        };
        log.Write(result);
        Console.WriteLine(result);
        return 0;
    }

    private static IReadOnlyList<string> AllowedGroups()
    {
        try { return AgentConfig.Load(AgentConfig.DefaultPath).AllowedGroups; }
        catch { return [AgentConfig.AdministratorsSid]; }
    }

    /// <summary>The enrollment file: given directly, or the single <c>.rhagent</c> in the folder (of the MSI).</summary>
    internal static string? Locate(string pathOrFolder)
    {
        var p = pathOrFolder.Trim().Trim('"');
        if (p.Length == 0) return null;
        if (File.Exists(p) && p.EndsWith(AgentEnrollment.Extension, StringComparison.OrdinalIgnoreCase)) return p;
        var folder = Directory.Exists(p) ? p : Path.GetDirectoryName(p);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
        var files = Directory.GetFiles(folder, "*" + AgentEnrollment.Extension);
        return files.Length switch
        {
            0 => null,
            1 => files[0],
            _ => throw new InvalidOperationException($"More than one {AgentEnrollment.Extension} file in {folder} – keep only the one for this device."),
        };
    }

    /// <summary>The MSI's BROKER_URL, BROKER_SECRET and BROKER_ONLY; null without a broker address.</summary>
    private static (string Url, string Secret, bool Listen)? Broker(string[] args) =>
        Option(args, "broker-url") is { Length: > 0 } url ? (url, Option(args, "broker-secret") ?? "", Option(args, "broker-only") != "1") : null;

    /// <param name="virtualDisplay">From the MSI (VIRTUALDISPLAY): "1" install the virtual monitor, "0" not, "" not chosen.</param>
    /// <param name="autoSignIn">From the MSI (AUTOSIGNIN): "1" allow automatic sign-in, "0" stop it, "" leave it.</param>
    /// <param name="broker">From the MSI: with a broker and no enrollment file, the device enrolls itself.</param>
    private static int Enroll(string source, string? virtualDisplay, AgentLog log, string? autoSignIn = null,
        (string Url, string Secret, bool Listen)? broker = null)
    {
        // Self-registration: no enrollment file and no agent.json, but a broker and its device secret.
        if (broker is { } selfJoin && Locate(source) is null && !File.Exists(AgentConfig.DefaultPath))
        {
            Run("sc.exe", "config RemoteHubAgent start= delayed-auto", allowFailure: true);
            BrokerSetup.Join(selfJoin.Url, selfJoin.Secret, log, selfJoin.Listen);
            if (TryLoad(AgentConfig.DefaultPath) is { } joined)
            {
                ApplyVirtualDisplay(joined, virtualDisplay, null, log);
                joined.Save(AgentConfig.DefaultPath);
            }
            return 0;
        }
        var done = Enroll(source, virtualDisplay, log);
        if (done == 0 && broker is { } b) BrokerSetup.Join(b.Url, b.Secret, log, b.Listen);
        if (done == 0 && autoSignIn is "1" or "0")
            try { log.Write(autoSignIn == "1" ? AutoSignInSetup.On(AllowedGroups()) : AutoSignInSetup.Off()); }
            catch (Exception ex) { log.Write("Automatic sign-in policy not set: " + ex.Message); } // never fails the install
        return done;
    }

    private static int Enroll(string source, string? virtualDisplay, AgentLog log)
    {
        Run("sc.exe", "config RemoteHubAgent start= delayed-auto", allowFailure: true);
        var file = Locate(source);
        if (file is null)
        {
            if (File.Exists(AgentConfig.DefaultPath))
            {
                log.Write("Update without enrollment file: keeping the existing device identity");
                if (TryLoad(AgentConfig.DefaultPath) is { } kept)
                {
                    ApplyVirtualDisplay(kept, virtualDisplay, null, log);
                    kept.Save(AgentConfig.DefaultPath);
                }
                return 0;
            }
            throw new FileNotFoundException($"No {AgentEnrollment.Extension} enrollment file found next to the installer. Save it in RemoteHub " +
                                            "(Settings → Security & storage → Save enrollment file) and put it in the same folder as the MSI.");
        }

        var enrollment = AgentEnrollment.Parse(File.ReadAllBytes(file));
        // The device's own identity (SEC-ENR-03): a new device ID and a key made here, which never leaves the device.
        var deviceId = Guid.NewGuid().ToString();
        var label = enrollment.DeviceLabel.Length > 0 ? enrollment.DeviceLabel : Environment.MachineName;
        var thumbprint = CreateIdentity(deviceId, label);
        var config = File.Exists(AgentConfig.DefaultPath) ? TryLoad(AgentConfig.DefaultPath) ?? new AgentConfig() : new AgentConfig();
        config.DeviceId = deviceId;
        config.DeviceLabel = label;
        config.ListenPort = enrollment.ListenPort;
        config.CertificateThumbprint = thumbprint;
        config.AllowedGroups = enrollment.AllowedGroups;
        config.EnrollmentKeyId = enrollment.KeyId;
        config.EnrollmentSecret = enrollment.Secret;
        ApplyVirtualDisplay(config, virtualDisplay, enrollment.VirtualDisplay, log);
        config.Validate();
        SecureFolder(AgentConfig.Folder);
        config.Save(AgentConfig.DefaultPath);
        SetFirewallRule(enrollment.ListenPort, enrollment.FirewallRemoteAddresses);

        try { File.Delete(file); } catch (Exception ex) { log.Write($"Could not delete {file}: {ex.Message} – delete it by hand, it contains the enrollment secret"); }
        log.Write($"Enrolled as device {deviceId} ({label}) with its own key, port {enrollment.ListenPort}, " +
                  $"firewall scope {(enrollment.FirewallRemoteAddresses.Length == 0 ? "any" : enrollment.FirewallRemoteAddresses)}");
        return 0;
    }

    /// <summary>
    /// The virtual monitor as chosen at install – the MSI property first, then the enrollment file, else the device keeps
    /// its setting (default on) – then installed or removed. Its failure doesn't fail the agent install; the log says why.
    /// </summary>
    internal static void ApplyVirtualDisplay(AgentConfig config, string? fromMsi, bool? fromEnrollment, AgentLog log)
    {
        config.VirtualDisplay = VirtualDisplayMode(config.VirtualDisplay, fromMsi, fromEnrollment);
        if (config.VirtualDisplay == VirtualDisplayModes.Off)
        {
            if (VirtualDisplay.Remove() is { } removed) log.Write(removed);
            return;
        }
        try { log.Write(VirtualDisplay.Install()); }
        catch (Exception ex) { log.Write("Virtual display not installed: " + ex.Message); }
    }

    /// <summary>The MSI's "1"/"0" first, then the enrollment file; switched on, an "always" set by hand stays.</summary>
    internal static string VirtualDisplayMode(string current, string? fromMsi, bool? fromEnrollment)
    {
        bool? wanted = fromMsi?.Trim() switch { "1" => true, "0" => false, _ => fromEnrollment };
        return wanted switch
        {
            false => VirtualDisplayModes.Off,
            true => current == VirtualDisplayModes.Off ? VirtualDisplayModes.Auto : current,
            null => current,
        };
    }

    private static AgentConfig? TryLoad(string path)
    {
        try { return AgentConfig.Load(path); } catch { return null; }
    }

    /// <summary>
    /// The device's key – RSA 3072 as a persisted CNG machine key that can't be exported – and its certificate
    /// (<see cref="AgentEnrollment.CreateCertificate"/>) in LocalMachine\My. Earlier agent certificates and the keys the
    /// agent made for them are removed. Returns the thumbprint.
    /// </summary>
    internal static string CreateIdentity(string deviceId, string label)
    {
        var parameters = new CngKeyCreationParameters
        {
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = CngKeyCreationOptions.MachineKey | CngKeyCreationOptions.OverwriteExistingKey,
            KeyUsage = CngKeyUsages.AllUsages,
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
        };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None));
        using var key = CngKey.Create(CngAlgorithm.Rsa, KeyName(deviceId), parameters);
        using var rsa = new RSACng(key);
        using var cert = AgentEnrollment.CreateCertificate(rsa, deviceId, label, DateTime.UtcNow);
        cert.FriendlyName = AgentEnrollment.CertificateFriendlyName;
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var old in Ours(store).Where(c => c.Thumbprint != cert.Thumbprint).ToList()) Remove(store, old);
        store.Add(cert);
        return cert.Thumbprint;
    }

    /// <summary>The CNG name of the key the agent makes for a device ID.</summary>
    internal static string KeyName(string deviceId) => "RemoteHub Agent " + deviceId;

    /// <summary>Removes an agent certificate and – when the agent made it – its key.</summary>
    private static void Remove(X509Store store, X509Certificate2 cert)
    {
        store.Remove(cert);
        try
        {
            // Only the agent's own named keys; a format-1 certificate's key came from a PFX.
            using var rsa = cert.GetRSAPrivateKey();
            if (rsa is RSACng { Key: { KeyName: { } name } key } && name.StartsWith(KeyName(""), StringComparison.Ordinal)) key.Delete();
        }
        catch { /* no key, or already gone */ }
    }

    private static IEnumerable<X509Certificate2> Ours(X509Store store) =>
        store.Certificates.Cast<X509Certificate2>().Where(c => c.FriendlyName == AgentEnrollment.CertificateFriendlyName && c.Subject.Contains("O=RemoteHub Agent"));

    /// <summary>
    /// The enrolled identity without agent.json (docs/agent-spec.md §7.2): the agent's certificate – exactly one with its
    /// key, as the enrollment leaves it; its CN is the device ID – and the single port of the agent's firewall rule.
    /// Null when the certificate isn't unambiguous: the service then doesn't start rather than guess.
    /// </summary>
    internal static (string DeviceId, string Thumbprint, int? Port)? InstalledIdentity()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var found = Ours(store).Where(c => c.HasPrivateKey).ToList();
        if (found.Count != 1) return null;
        var deviceId = found[0].GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (!Guid.TryParse(deviceId, out _)) return null;
        int? port = null;
        try
        {
            var (code, rule) = Run("netsh.exe", $"advfirewall firewall show rule name=\"{FirewallRuleName}\"", allowFailure: true);
            if (code == 0 && RulePorts(rule).Distinct().ToList() is [var only] && int.TryParse(only, out var p)) port = p;
        }
        catch { /* no rule: the default port */ }
        return (deviceId, found[0].Thumbprint, port);
    }

    /// <summary>
    /// Only SYSTEM and Administrators – also as owner, and for everything already in the folder: a folder or file a user
    /// created there before the install keeps neither that user as owner nor permissions of its own. Users can't read
    /// the logs (accounts and source addresses).
    /// </summary>
    internal static void SecureFolder(string folder)
    {
        var dir = Directory.CreateDirectory(folder);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var acl = new DirectorySecurity();
        acl.SetOwner(admins);
        acl.SetAccessRuleProtection(true, false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(acl);
        foreach (var entry in dir.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            // Inherited from the folder only: no explicit entries, Administrators as owner.
            FileSystemSecurity own = entry is DirectoryInfo ? new DirectorySecurity() : new FileSecurity();
            own.SetOwner(admins);
            own.SetAccessRuleProtection(false, false);
            if (entry is DirectoryInfo d) d.SetAccessControl((DirectorySecurity)own);
            else ((FileInfo)entry).SetAccessControl((FileSecurity)own);
        }
    }

    /// <summary>netsh arguments for the inbound rule: this program, TCP, the port, optionally limited to remote addresses.</summary>
    internal static string FirewallAddArguments(string program, int port, string remoteAddresses) =>
        $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={port} " +
        $"program=\"{program}\" profile=any remoteip={(string.IsNullOrWhiteSpace(remoteAddresses) ? "any" : remoteAddresses.Replace(" ", ""))}";

    internal static void SetFirewallRule(int port, string remoteAddresses)
    {
        Netsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"", allowFailure: true);
        Netsh(FirewallAddArguments(Environment.ProcessPath!, port, remoteAddresses));
    }

    /// <summary>
    /// At service start: moves the inbound rule to the port the agent listens on (policy ListenPort can change it), keeping
    /// its address scope. Null when nothing changed; otherwise what happened, for the log.
    /// </summary>
    public static string? SetFirewallPort(int port)
    {
        try
        {
            var (code, rule) = Run("netsh.exe", $"advfirewall firewall show rule name=\"{FirewallRuleName}\"", allowFailure: true);
            if (code != 0) return $"Firewall: no rule \"{FirewallRuleName}\" – port {port} isn't opened by the agent";
            if (RulePorts(rule).All(p => p == port.ToString(System.Globalization.CultureInfo.InvariantCulture))) return null;
            Netsh($"advfirewall firewall set rule name=\"{FirewallRuleName}\" new localport={port}");
            return $"Firewall: rule \"{FirewallRuleName}\" moved to port {port}";
        }
        catch (Exception ex) { return $"Firewall: rule \"{FirewallRuleName}\" not moved to port {port}: {ex.Message}"; }
    }

    /// <summary>The "LocalPort:" lines of <c>netsh advfirewall firewall show rule</c> (English output; other languages: none found → set anyway).</summary>
    internal static IEnumerable<string> RulePorts(string netshOutput)
    {
        var ports = netshOutput.Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith("LocalPort:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["LocalPort:".Length..].Trim()).ToList();
        return ports.Count > 0 ? ports : [""];
    }

    private static void Netsh(string arguments, bool allowFailure = false) => Run("netsh.exe", arguments, allowFailure);

    private static (int Code, string Output) Run(string exe, string arguments, bool allowFailure = false)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && !allowFailure) throw new InvalidOperationException($"{exe} {arguments.Split(' ')[0]} failed: " + output.Trim());
        return (p.ExitCode, output);
    }

    private static int Cleanup(AgentLog log)
    {
        Netsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"", allowFailure: true);        using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadWrite);
            foreach (var c in Ours(store).ToList()) Remove(store, c);
        }
        try { File.Delete(AgentConfig.DefaultPath); } catch { /* not there */ }
        // The last good copy too: a later install without an enrollment file must not bring back this device's settings.
        try { File.Delete(AgentConfig.BackupPath(AgentConfig.DefaultPath)); } catch { /* not there */ }
        try { File.Delete(AgentConfig.DamagedPath(AgentConfig.DefaultPath)); } catch { /* not there */ }
        try { File.Delete(Path.Combine(AgentConfig.Folder, "autosignin.json")); } catch { /* not there */ }
        SecureAttention.Remove();
        try { log.Write(AutoSignInSetup.Off()); } catch (Exception ex) { log.Write("Automatic sign-in policy not removed: " + ex.Message); }
        try { if (VirtualDisplay.Remove() is { } removed) log.Write(removed); } catch (Exception ex) { log.Write("Virtual display not removed: " + ex.Message); } // the Ctrl+Alt+Del setting, if the agent set it
        log.Write("Uninstalled: firewall rule, certificate, configuration and Ctrl+Alt+Del setting removed");
        return 0;
    }
}
