using System;
using System.Collections.Generic;
using RemoteHub.Models;
using RemoteHub.Services;

/// <summary>
/// seed --connections n [--folders n] [--shares n] [--standalone n]
///     Fills the settings folder (REMOTEHUB_DATA_DIR, set by the caller) with test connections in folders, each with
///     a few network shares and their own passwords, plus shares on their own in each folder – the data the start-time
///     test opens RemoteHub with.
/// seed --layout nav [--compact 0]
///     A small tree with one of each kind of row the navigation draws: a folder with a colour tag, a connection with an
///     agent, a jump host with a device and a share behind it, a server with a share, an SSH connection on its own, a
///     favourite and a broker folder with an online and an unknown device – for looking at the navigation (screenshots).
/// </summary>
internal static class SeedHarness
{
    public static object Run(Dictionary<string, string> o)
    {
        if (o.GetValueOrDefault("layout") == "nav") return Navigation(o.GetValueOrDefault("compact") != "0");
        var connections = int.Parse(o.GetValueOrDefault("connections") ?? "300");
        var folderCount = Math.Max(1, int.Parse(o.GetValueOrDefault("folders") ?? "20"));
        var shares = int.Parse(o.GetValueOrDefault("shares") ?? "2");
        var standalone = int.Parse(o.GetValueOrDefault("standalone") ?? "1");
        var data = SettingsStore.Load();
        data.Items.Clear();
        var folders = new List<Folder>();
        for (var f = 0; f < folderCount; f++)
        {
            var folder = new Folder { Name = $"Site {f + 1:00}" };
            folders.Add(folder);
            data.Items.Add(folder);
        }
        for (var i = 0; i < connections; i++)
        {
            var c = new Connection { Name = $"server-{i + 1:000}", Host = $"server-{i + 1:000}.lab.test", InheritCredentials = i % 3 != 0 };
            if (!c.InheritCredentials) c.Credentials = new CredentialSet { Username = $@"lab\user{i}", Password = Secret.From($"pw-{Guid.NewGuid():N}") };
            for (var s = 0; s < shares; s++)
                c.Shares.Add(new NetworkShare
                {
                    Name = $"share{s}", Protocol = (ShareProtocol)(s % 4), Path = s % 4 == 0 ? $"share{s}" : $"/data/{s}",
                    InheritCredentials = s % 2 == 0,
                    Credentials = s % 2 == 0 ? new CredentialSet() : new CredentialSet { Username = $"share{s}", Password = Secret.From($"pw-{Guid.NewGuid():N}") },
                });
            folders[i % folderCount].Children.Add(c);
        }
        foreach (var folder in folders)
        {
            for (var s = 0; s < standalone; s++)
                folder.Children.Insert(0, new NetworkShare { Name = $"nas-{folder.Name[5..]}-{s}", Host = $"nas{s}.lab.test", Path = "files", Protocol = (ShareProtocol)(s % 4) });
        }
        SettingsStore.RelinkParents(data.Items, null);
        SettingsStore.Save(data, force: true);
        return new { ok = true, dir = SettingsStore.Directory, connections, folders = folderCount, sharesPerConnection = shares, standalonePerFolder = standalone };
    }

    private static object Navigation(bool compact)
    {
        var data = SettingsStore.Load();
        data.Items.Clear();
        static AgentDevice Agent() => new() { DeviceId = Guid.NewGuid().ToString(), Status = AgentStatus.Installed, Fingerprint = new string('A', 64), CreatedUtc = DateTime.UtcNow };
        var lab = new Folder { Name = "LAB", InheritCredentials = false, Credentials = new CredentialSet { Username = "labadmin" } };
        var nuc13 = new Connection { Name = "NUC13", Host = "192.168.250.25", Agent = Agent(), LastConnectedUtc = DateTime.UtcNow.AddHours(-3) };
        nuc13.Options.ColorTag = "Red";
        var nuc10 = new Connection { Name = "NUC10", Host = "192.168.250.6", Agent = Agent(), LastConnectedUtc = DateTime.UtcNow.AddDays(-4) };
        var relay = new Connection { Name = "relaytest", Host = "192.168.123.123", Agent = Agent(), LastConnectedUtc = DateTime.UtcNow.AddDays(-12), Parent = nuc10 };
        nuc10.Children.Add(relay);
        nuc10.Children.Add(new NetworkShare { Name = "test", Host = "192.168.123.123", Path = "share", Parent = nuc10 });
        lab.Children.Add(nuc13);
        lab.Children.Add(nuc10);
        var redis = new Folder { Name = "Redis" };
        redis.Options.ColorTag = "Teal";
        foreach (var (name, host, days) in new[] { ("AppRemoval Console", "10.14.92.74", -1), ("AppRemoval DB", "10.14.92.73", 10), ("EARV2UITEST-Console", "10.14.92.57", 4), ("EARV2UITEST-DB", "10.14.92.59", 4) })
            redis.Children.Add(new Connection { Name = name, Host = host, LastConnectedUtc = days < 0 ? null : DateTime.UtcNow.AddDays(-days) });
        var qnap = new Connection { Name = "QNAP", Host = "192.168.250.100", LastConnectedUtc = DateTime.UtcNow.AddDays(-1) };
        var qshare = new NetworkShare { Name = "share", Path = "share", Server = qnap };
        qnap.Shares.Add(qshare);
        var ssh = new NetworkShare { Name = "Akamai - Packages", Protocol = ShareProtocol.Ssh, Host = "packages.ompissa.com" };
        const string org = "b_preview";
        var broker = new Folder { Name = "GE-Test – RemoteHub Broker", Icon = "CloudLink24", Broker = new BrokerRef { Connection = org, Kind = BrokerItemKind.Root }, IsExpanded = true };
        var bLab = new Folder { Name = "LAB", Broker = new BrokerRef { Connection = org, Kind = BrokerItemKind.Folder, Item = Guid.NewGuid().ToString() } };
        bLab.Children.Add(new Connection { Name = "HV01", Host = "HV01", Agent = Agent(), LastConnectedUtc = DateTime.UtcNow.AddMinutes(-40),
            Broker = new BrokerRef { Connection = org, Kind = BrokerItemKind.Device, Item = Guid.NewGuid().ToString() } });
        bLab.Children.Add(new Connection { Name = "HV02", Host = "HV02", Agent = Agent(),
            Broker = new BrokerRef { Connection = org, Kind = BrokerItemKind.Device, Item = Guid.NewGuid().ToString() } });
        broker.Children.Add(bLab);
        data.Items.Add(lab);
        data.Items.Add(redis);
        data.Items.Add(qnap);
        data.Items.Add(ssh);
        data.Items.Add(broker);
        data.Ui.FavoriteConnections = [nuc13.Id];
        data.Ui.TreeCompact = compact;
        SettingsStore.RelinkParents(data.Items, null);
        SettingsStore.Save(data, force: true);
        return new { ok = true, dir = SettingsStore.Directory, layout = "nav" };
    }
}
