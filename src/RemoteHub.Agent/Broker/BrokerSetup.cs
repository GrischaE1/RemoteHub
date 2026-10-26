namespace RemoteHub.Agent;

/// <summary>
/// Joining and leaving a RemoteHub Broker (broker-design.md §6.4):
/// <list type="bullet">
/// <item><c>RemoteHubAgent.exe broker join &lt;url&gt;</c> – the device secret comes from standard input, never from the command
/// line (process lists, scripts' logs). An enrolled agent adds the broker; one installed without an enrollment file
/// first gets its own identity, as <c>enroll</c> makes it.</item>
/// <item><c>RemoteHubAgent.exe broker leave</c> – the broker and its keys are removed; the device stays enrolled.</item>
/// <item><c>RemoteHubAgent.exe broker status</c> – the broker in agent.json, without the keys.</item>
/// </list>
/// The MSI passes BROKER_URL / BROKER_SECRET to <c>enroll</c>, which calls <see cref="Join"/> the same way. The running
/// service picks up a change without a restart (<see cref="BrokerLink"/> reads agent.json again).
/// </summary>
public static class BrokerSetup
{
    public static int Run(string[] args, AgentLog log, TextReader input, TextWriter output)
    {
        try
        {
            switch (args.Length > 1 ? args[1].ToLowerInvariant() : "")
            {
                case "join":
                    if (args.Length < 3) throw new ArgumentException("Use: broker join <url>   (the device secret on standard input)");
                    var secret = input.ReadLine() ?? "";
                    output.WriteLine(Join(args[2], secret, log, listen: true));
                    return 0;
                case "leave":
                    output.WriteLine(Leave(log));
                    return 0;
                case "status":
                    output.WriteLine(Status());
                    return 0;
                default:
                    throw new ArgumentException("Use: broker join <url> | broker leave | broker status");
            }
        }
        catch (Exception ex)
        {
            log.Write($"broker {(args.Length > 1 ? args[1] : "")} failed: {ex.Message}");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Writes the broker and the device secret's two keys into agent.json; the secret itself isn't kept. Without an
    /// agent.json the device is enrolled here first: a new device id and its own key (as <c>enroll</c> does), the default
    /// port and Administrators – and the firewall rule only when <paramref name="listen"/> (MSI BROKER_ONLY=1 leaves it out).
    /// </summary>
    public static string Join(string url, string secretText, AgentLog log, bool listen, string path = "")
    {
        path = path.Length > 0 ? path : AgentConfig.DefaultPath;
        var broker = BrokerSettings.ParseUrl(url) ?? throw new ArgumentException($"\"{url}\" isn't a broker address – use https://name.");
        var secret = BrokerProtocol.ParseSecret(secretText) ?? throw new ArgumentException("That isn't a device secret (rhd1.…).");
        if (secret.Kind != BrokerProtocol.SecretKind.Device) throw new ArgumentException("That is a console secret – the device needs the device secret (rhd1.…).");

        AgentConfig config;
        string enrolled = "";
        if (File.Exists(path)) config = AgentConfig.Load(path);
        else
        {
            var deviceId = Guid.NewGuid().ToString();
            config = new AgentConfig
            {
                DeviceId = deviceId,
                DeviceLabel = Environment.MachineName,
                CertificateThumbprint = Enrollment.CreateIdentity(deviceId, Environment.MachineName),
            };
            Enrollment.SecureFolder(Path.GetDirectoryName(path)!);
            if (listen) Enrollment.SetFirewallRule(config.ListenPort, "");
            enrolled = $"Enrolled as device {deviceId} with its own key{(listen ? $", port {config.ListenPort}" : ", no listening port")}. ";
        }

        var hadBroker = config.Broker is not null;
        config.Broker = new BrokerSettings
        {
            Url = broker.GetLeftPart(UriPartial.Authority),
            Org = secret.Org,
            SecretId = secret.Id,
            EnrollKey = BrokerProtocol.B64u(BrokerProtocol.DeriveKey(secret.Key, secret.Org, BrokerProtocol.KeyLabel.Enroll)),
            RecordKey = BrokerProtocol.B64u(BrokerProtocol.DeriveKey(secret.Key, secret.Org, BrokerProtocol.KeyLabel.Record)),
        };
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret.Key);
        config.Validate();
        config.Save(path);
        var done = $"{enrolled}{(hadBroker ? "Moved to" : "Joined")} the RemoteHub Broker {broker.Host} (organisation {config.Broker.Org}); the service connects within a minute.";
        if (broker.Scheme == "http") done += " Warning: http – the broker's signalling isn't encrypted (test labs only).";
        log.Write(done);
        return done;
    }

    public static string Leave(AgentLog log, string path = "")
    {
        path = path.Length > 0 ? path : AgentConfig.DefaultPath;
        var config = AgentConfig.Load(path);
        if (config.Broker is not { } b) return "This device doesn't use a RemoteHub Broker.";
        config.Broker = null;
        config.Save(path);
        var done = $"Left the RemoteHub Broker {BrokerSettings.ParseUrl(b.Url)?.Host} (organisation {b.Org}). Its registration stays until a console or the administrator removes it.";
        log.Write(done);
        return done;
    }

    public static string Status(string path = "")
    {
        path = path.Length > 0 ? path : AgentConfig.DefaultPath;
        if (!File.Exists(path)) return "Not enrolled.";
        var b = AgentConfig.Load(path).Broker;
        return b is null ? "No RemoteHub Broker." : $"RemoteHub Broker {b.Url}, organisation {b.Org}, device secret {b.SecretId}.";
    }
}
