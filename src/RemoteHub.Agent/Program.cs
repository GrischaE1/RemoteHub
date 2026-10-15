using System.Security.Cryptography.X509Certificates;
using RemoteHub.Agent;

// RemoteHub Agent – Windows service "RemoteHubAgent" (runs as LocalSystem).
// Config: %ProgramData%\RemoteHub\Agent\agent.json; certificate: LocalMachine\My (thumbprint in the config).
var log = new AgentLog(Path.Combine(AgentConfig.Folder, "logs"), 30);

// Setup commands, run by the MSI: enroll <file|folder>, cleanup.
if (args.Length > 0 && args[0] is "enroll" or "cleanup" or "auto-sign-in") return Enrollment.Run(args, log);
if (args.Length > 0 && args[0] == "selftest") return await SelfTest.RunAsync();
// Remote desktop helper, started by the service in a Windows session (talks to the service over stdin/stdout only).
if (args.Length > 0 && args[0] == "desktop") return DesktopHelperProgram.Run();

try
{
    var config = AgentConfig.Load(AgentConfig.DefaultPath);
    log = new AgentLog(Path.Combine(AgentConfig.Folder, "logs"), config.LogRetentionDays) { EventLogEnabled = () => AgentPolicy.Load(config).AuditToEventLog };
    var certificate = FindCertificate(config.CertificateThumbprint);

    // The pipe to the credential provider, before the agent listens on the network (design §7.1). Always created: the
    // provider can be added to a running agent (RemoteHub's "Install credential provider" doesn't restart it).
    using var autoSignIn = AutoSignInBroker.Start(log);
    var desktop = DesktopServices.Windows(autoSignIn);
    // Sessions automatic sign-in created that the last run left open: no connection has them any more (design §9.3).
    desktop.AutoSigned.Recover(log);
    var app = AgentHost.Build(new AgentHostOptions(config, certificate,
        new WindowsAuthenticator(config.AllowedGroups), new WindowsShellLauncher(), log, Desktop: desktop), []);
    log.Write($"RemoteHub Agent {AgentHost.AgentVersion} starting on port {config.ListenPort} (device {config.DeviceId}, " +
              $"certificate {AgentProtocol.Fingerprint(certificate.RawData)})");
    if (SecureAttention.Ensure() is { } sas) log.Write(sas); // services may send Ctrl+Alt+Del (unless a policy says otherwise)
    VirtualDisplay.Reset(); // the virtual monitor is off until a connection needs it (a crash may have left it on)
    await app.RunAsync();
    log.Write("RemoteHub Agent stopped");
    return 0;
}
catch (Exception ex)
{
    // E.g. the port is in use: refuse to start rather than silently moving to another port (RemoteHub stores the port).
    log.Write("RemoteHub Agent could not start: " + ex.Message);
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static X509Certificate2 FindCertificate(string thumbprint)
{
    using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
    store.Open(OpenFlags.ReadOnly);
    var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
    return found.Count > 0 && found[0].HasPrivateKey
        ? found[0]
        : throw new InvalidOperationException($"Certificate {thumbprint} with private key not found in LocalMachine\\My.");
}
