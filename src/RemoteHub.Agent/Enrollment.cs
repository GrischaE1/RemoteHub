using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace RemoteHub.Agent;

/// <summary>
/// Device-side setup, run by the MSI as LocalSystem:
/// <list type="bullet">
/// <item><c>RemoteHubAgent.exe enroll &lt;file | folder | msi path&gt;</c> – imports a <c>.rhagent</c> file: certificate into
/// LocalMachine\My (key not exportable), <c>agent.json</c>, firewall rule; then deletes the file. Without a file an
/// already enrolled agent keeps its identity (plain update).</item>
/// <item><c>RemoteHubAgent.exe cleanup</c> – on uninstall: firewall rule, certificate and configuration are removed (logs stay).</item>
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
                "enroll" => Enroll(args.Length > 1 ? args[1] : "", args.Skip(2).FirstOrDefault(a => a.StartsWith("virtual-display=", StringComparison.OrdinalIgnoreCase))?["virtual-display=".Length..], log),
                "cleanup" => Cleanup(log),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'. Use: enroll <file|folder> | cleanup"),
            };
        }
        catch (Exception ex)
        {
            log.Write($"{args[0]} failed: {ex.Message}");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
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

    /// <param name="virtualDisplay">From the MSI (VIRTUALDISPLAY): "1" install the virtual monitor, "0" not, "" not chosen.</param>
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
            throw new FileNotFoundException($"No {AgentEnrollment.Extension} enrollment file found next to the installer. Create it in RemoteHub " +
                                            "(right-click the connection → Install RemoteHub Agent) and put it in the same folder as the MSI.");
        }

        var enrollment = AgentEnrollment.Parse(File.ReadAllBytes(file));
        var thumbprint = ImportCertificate(enrollment);
        var config = File.Exists(AgentConfig.DefaultPath) ? TryLoad(AgentConfig.DefaultPath) ?? new AgentConfig() : new AgentConfig();
        config.DeviceId = enrollment.DeviceId;
        config.DeviceLabel = enrollment.DeviceLabel;
        config.ListenPort = enrollment.ListenPort;
        config.CertificateThumbprint = thumbprint;
        config.AllowedGroups = enrollment.AllowedGroups;
        ApplyVirtualDisplay(config, virtualDisplay, enrollment.VirtualDisplay, log);
        config.Validate();
        SecureFolder(AgentConfig.Folder);
        config.Save(AgentConfig.DefaultPath);
        SetFirewallRule(enrollment.ListenPort, enrollment.FirewallRemoteAddresses);

        try { File.Delete(file); } catch (Exception ex) { log.Write($"Could not delete {file}: {ex.Message} – delete it by hand, it contains the device key"); }
        log.Write($"Enrolled as device {enrollment.DeviceId} ({enrollment.DeviceLabel}), port {enrollment.ListenPort}, " +
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

    /// <summary>Imports into LocalMachine\My (private key not exportable) and removes earlier agent certificates.</summary>
    private static string ImportCertificate(AgentEnrollment e)
    {
        var cert = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(e.Pfx), e.PfxPassword,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
        cert.FriendlyName = AgentEnrollment.CertificateFriendlyName;
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var old in Ours(store).Where(c => c.Thumbprint != cert.Thumbprint).ToList()) store.Remove(old);
        store.Add(cert);
        return cert.Thumbprint;
    }

    private static IEnumerable<X509Certificate2> Ours(X509Store store) =>
        store.Certificates.Cast<X509Certificate2>().Where(c => c.FriendlyName == AgentEnrollment.CertificateFriendlyName && c.Subject.Contains("O=RemoteHub Agent"));

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

    private static void SetFirewallRule(int port, string remoteAddresses)
    {
        Netsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"", allowFailure: true);
        Netsh(FirewallAddArguments(Environment.ProcessPath!, port, remoteAddresses));
    }

    private static void Netsh(string arguments, bool allowFailure = false) => Run("netsh.exe", arguments, allowFailure);

    private static void Run(string exe, string arguments, bool allowFailure = false)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && !allowFailure) throw new InvalidOperationException($"{exe} {arguments.Split(' ')[0]} failed: " + output.Trim());
    }

    private static int Cleanup(AgentLog log)
    {
        Netsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"", allowFailure: true);        using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadWrite);
            foreach (var c in Ours(store).ToList()) store.Remove(c);
        }
        try { File.Delete(AgentConfig.DefaultPath); } catch { /* not there */ }
        SecureAttention.Remove();
        try { if (VirtualDisplay.Remove() is { } removed) log.Write(removed); } catch (Exception ex) { log.Write("Virtual display not removed: " + ex.Message); } // the Ctrl+Alt+Del setting, if the agent set it
        log.Write("Uninstalled: firewall rule, certificate, configuration and Ctrl+Alt+Del setting removed");
        return 0;
    }
}
