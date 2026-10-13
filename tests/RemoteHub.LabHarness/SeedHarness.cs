using System;
using System.Collections.Generic;
using RemoteHub.Models;
using RemoteHub.Services;

/// <summary>
/// seed --connections n [--folders n] [--shares n] [--standalone n]
///     Fills the settings folder (REMOTEHUB_DATA_DIR, set by the caller) with test connections in folders, each with
///     a few network shares and their own passwords, plus shares on their own in each folder – the data the start-time
///     test opens RemoteHub with.
/// </summary>
internal static class SeedHarness
{
    public static object Run(Dictionary<string, string> o)
    {
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
            if (!c.InheritCredentials) c.Credentials = new CredentialSet { Username = $@"lab\user{i}", Password = $"pw-{Guid.NewGuid():N}" };
            for (var s = 0; s < shares; s++)
                c.Shares.Add(new NetworkShare
                {
                    Name = $"share{s}", Protocol = (ShareProtocol)(s % 4), Path = s % 4 == 0 ? $"share{s}" : $"/data/{s}",
                    InheritCredentials = s % 2 == 0,
                    Credentials = s % 2 == 0 ? new CredentialSet() : new CredentialSet { Username = $"share{s}", Password = $"pw-{Guid.NewGuid():N}" },
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
}
