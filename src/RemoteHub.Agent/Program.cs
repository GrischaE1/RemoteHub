using System.Security.Cryptography.X509Certificates;
using RemoteHub.Agent;

// RemoteHub Agent – Windows service "RemoteHubAgent" (runs as LocalSystem).
// Config: %ProgramData%\RemoteHub\Agent\agent.json; certificate: LocalMachine\My (thumbprint in the config).
// The desktop helper's clipboard reader, started as the signed-in user: before anything else, it touches no agent files.
if (args.Length == 1 && args[0] == ClipboardFilesReader.Verb)
{
    using var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
    return ClipboardFilesReader.Run(stdout);
}
// Store apps in app tabs, started and listed as the signed-in user (app-tabs-design §6.6): also before anything else.
if (args.Length == 1 && args[0] == StoreApps.Verb)
{
    using var stdin = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
    using var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
    return StoreApps.Run(stdin, stdout);
}

var log = new AgentLog(Path.Combine(AgentConfig.Folder, "logs"), 30);

// Setup commands, run by the MSI: enroll <file|folder>, cleanup.
if (args.Length > 0 && args[0] is "enroll" or "cleanup" or "auto-sign-in") return Enrollment.Run(args, log);
// broker join <url> (device secret on standard input) | broker leave | broker status – see BrokerSetup.
if (args.Length > 0 && args[0] == "broker") return BrokerSetup.Run(args, log, Console.In, Console.Out);
if (args.Length > 0 && args[0] == "selftest") return await SelfTest.RunAsync();
// Every policy value in effect and where it comes from (policy, agent.json, default) – for support and the lab.
if (args.Length > 0 && args[0] == "policy") return PolicyCommand.Run(Console.Out);
// Remote desktop helper, started by the service in a Windows session (talks to the service over stdin/stdout only).
if (args.Length > 0 && args[0] == "desktop") return DesktopHelperProgram.Run();

try
{
    // A damaged agent.json falls back to its last good copy, else a fresh install's defaults (docs/agent-spec.md §7.2).
    var config = AgentConfig.LoadForService(AgentConfig.DefaultPath, Enrollment.InstalledIdentity);
    log = new AgentLog(Path.Combine(AgentConfig.Folder, "logs"), config.LogRetentionDays)
    {
        EventLogEnabled = () => AgentPolicy.Load(config).AuditToEventLog,
        RetentionDays = () => AgentPolicy.Load(config).LogRetentionDays,
    };
    if (config.Recovery is { } recovery) log.Audit(config.IsSafeDefaults ? AuditEvent.ConfigDefaults : AuditEvent.ConfigRestored, recovery);
    // Automatic sign-in values an older version wrote into the policy key move to agent.json (once; docs/admx-design.md §11.3).
    if (AutoSignInSetup.Migrate(config) is { } moved) log.Write(moved);
    // The port: policy ListenPort, else agent.json; read only here. The firewall rule follows it.
    var startPolicy = AgentPolicy.Load(config);
    if (startPolicy.LoadError is null && startPolicy.ListenPort != config.ListenPort)
        log.Write($"Port {startPolicy.ListenPort} set by policy ListenPort (agent.json: {config.ListenPort})");
    if (startPolicy.LoadError is null) config.ListenPort = startPolicy.ListenPort; // not saved: without the policy agent.json's applies again
    // Policy ListenEnabled = 0, also read only here: no TCP listener and the firewall rule switched off. A policy that
    // can't be read keeps the listener (AgentPolicy.Refused), so a broken policy never cuts the device off.
    var listen = startPolicy.ListenEnabled;
    if (!listen)
        log.Write("Listener off by policy (ListenEnabled = 0): the device is reachable only through the RemoteHub Broker" +
                  (config.Broker is null ? " – and no broker is set up, so nothing can reach it from the network" : ""));
    if (Enrollment.SetFirewallEnabled(listen) is { } switched) log.Write(switched);
    if (listen && Enrollment.SetFirewallPort(config.ListenPort) is { } firewall) log.Write(firewall);
    // A port another program listens on, before the service reports running: Kestrel's bind (AgentHost) would refuse it
    // too, but only after Windows was told the service runs, so the MSI's start succeeded and the install with it
    // (A-INST-25). Ending here fails the service start, and the install rolls back.
    if (listen && AgentHost.PortInUse(config.ListenPort))
        throw new InvalidOperationException($"port {config.ListenPort} is in use by another program – the agent doesn't move to another port.");
    var certificate = FindCertificate(config.CertificateThumbprint);

    // The pipe to the credential provider, before the agent listens on the network (design §7.1). Always created: the
    // provider can be added to a running agent (RemoteHub's "Install credential provider" doesn't restart it).
    using var autoSignIn = AutoSignInBroker.Start(log);
    var desktop = DesktopServices.Windows(autoSignIn);
    // Sessions automatic sign-in created that the last run left open: no connection has them any more (design §9.3).
    desktop.AutoSigned.Recover(log);
    // Streams through the RemoteHub Broker reach the same HTTPS endpoint as the network does.
    var peers = new PeerTransport();
    var app = AgentHost.Build(new AgentHostOptions(config, certificate,
        new WindowsAuthenticator(config.AllowedGroups), new WindowsShellLauncher(), log, Desktop: desktop) { Peers = peers, Listen = listen }, []);
    // The broker in agent.json is read again on every connect, so `broker join` and `broker leave` need no restart.
    Enrollment.DirectFirewall.CloseAll(); // direct paths a stopped agent had open
    var broker = new BrokerLink(() => config.SourcePath is { } source ? AgentConfig.Load(source) : config, certificate,
        () => AgentPolicy.Load(config), log, peers, firewall: new Enrollment.DirectFirewall()) { Listening = listen };
    _ = Task.Run(() => broker.RunAsync(app.Lifetime.ApplicationStopping));
    log.Write($"RemoteHub Agent {AgentHost.AgentVersion} starting {(listen ? $"on port {config.ListenPort}" : "without a listener")} (device {config.DeviceId}, " +
              $"certificate {AgentProtocol.Fingerprint(certificate.RawData)})");
    if (SecureAttention.Ensure() is { } sas) log.Write(sas); // services may send Ctrl+Alt+Del (unless a policy says otherwise)
    try { if (new SasActions().RestoreLeftovers() is { } back) log.Write(back); } // a helper that ended without putting them back
    catch (Exception ex) { log.Write("Ctrl+Alt+Del screen: actions an earlier helper hid could not be put back: " + ex.Message); }
    try { if (new AppTaskbar().RestoreLeftovers() is { } taskbar) log.Write(taskbar); } // the taskbar app tabs turned off
    catch (Exception ex) { log.Write("App tabs: the taskbar setting an earlier helper changed could not be put back: " + ex.Message); }
    try { if (new KeepRdpDrawing().RestoreLeftovers() is { } rdp) log.Write(rdp); } // the same for RDP windows' drawing
    catch (Exception ex) { log.Write("RDP minimize setting an earlier helper changed could not be put back: " + ex.Message); }
    // The virtual monitor is off until a connection needs it (a crash may have left it on) – in the background: the
    // service must report running within Windows' 30 s, whatever pnputil does.
    _ = VirtualDisplay.ResetInBackground(log.Write);
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
