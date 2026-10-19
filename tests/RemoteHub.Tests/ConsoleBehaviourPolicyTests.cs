using System.Collections.Generic;
using RemoteHub.Agent;
using RemoteHub.Models;
using RemoteHub.Services;
using RemoteHub.ViewModels;

namespace RemoteHub.Tests;

/// <summary>
/// The console behaviour behind phase 5's policies (docs/admx-design.md §5.4, §5.6, §6.3–§6.5): saved passwords, password
/// copy, the read-only tree and the diagnostic log's retention.
/// </summary>
[Collection("Policies")]
public class ConsoleBehaviourPolicyTests
{
    private sealed class FakeSource(Dictionary<string, object> values) : IPolicySource
    {
        public object? Read(PolicyScope scope, string key, string name) =>
            scope == PolicyScope.Computer && key.Equals(PolicyCatalog.Root, StringComparison.OrdinalIgnoreCase) && values.TryGetValue(name, out var v) ? v : null;
    }

    private static FakeSource Set(params (string Name, object Value)[] values) => new(values.ToDictionary(v => v.Name, v => v.Value));

    private static void WithPolicy(FakeSource source, Action test)
    {
        AppPolicy.Source = source;
        try { test(); }
        finally { AppPolicy.Source = RegistryPolicySource.Instance; }
    }

    [Fact]
    public void Stored_passwords_are_not_used_while_saving_them_is_blocked_and_stay_stored()
    {
        var data = new AppData();
        var saved = new SavedCredential { Name = "admin", Username = "admin", Password = "s3cret" };
        data.SavedCredentials.Add(saved);
        var own = new Connection { Name = "a", Host = "a", InheritCredentials = false, Credentials = new CredentialSet { Username = "u", Password = "pw" } };
        var bySaved = new Connection { Name = "b", Host = "b", InheritCredentials = false, CredentialId = saved.Id };
        WithPolicy(Set(("AllowSavedPasswords", 0)), () =>
        {
            var (c, _) = Inheritance.ResolveCredentials(own, data);
            Assert.Equal("u", c.Username);
            Assert.Null(c.Password);
            Assert.Null(Inheritance.ResolveCredentials(bySaved, data).Credentials.Password);
            Assert.Equal("pw", own.Credentials.Password); // removed only by SavedPasswords.Remove, after the notice
        });
        Assert.Equal("pw", Inheritance.ResolveCredentials(own, data).Credentials.Password);
    }

    [Fact]
    public void Removing_saved_passwords_clears_every_kind_and_counts_them()
    {
        var data = new AppData { DefaultCredentials = new CredentialSet { Username = "d", Password = "1" } };
        data.SavedCredentials.Add(new SavedCredential { Name = "s", Username = "s", Password = "2" });
        data.SavedCredentials.Add(new SavedCredential { Name = "t", Username = "t" });
        var server = new Connection { Name = "srv", Host = "srv", Credentials = new CredentialSet { Username = "u", Password = "3" } };
        server.Shares.Add(new NetworkShare { Name = "data", Server = server, Credentials = new CredentialSet { Username = "x", Password = "4" } });
        var folder = new Folder { Name = "f", Credentials = new CredentialSet { Username = "f", Password = "5" } };

        Assert.Equal(5, SavedPasswords.Remove(data, [server, folder]));
        Assert.Null(data.DefaultCredentials.Password);
        Assert.Equal("d", data.DefaultCredentials.Username);
        Assert.All(data.SavedCredentials, s => Assert.Null(s.Password));
        Assert.Null(server.Credentials.Password);
        Assert.Null(server.Shares[0].Credentials.Password);
        Assert.Null(folder.Credentials.Password);
        Assert.Equal(0, SavedPasswords.Remove(data, [server, folder]));
    }

    [Fact]
    public void Blocked_password_copy_and_saving_grey_out_their_editor_rows()
    {
        WithPolicy(Set(("AllowPasswordCopy", 0)), () =>
        {
            var vm = new EditorViewModel(new Connection { Name = "srv", Host = "srv" }, new AppData(), isNew: false);
            var row = vm.SecurityRows.Single(r => r.Label == "When Windows asks for the password");
            Assert.Equal(Features.TurnedOff, row.PolicyNote);
            Assert.False(row.IsEditable);
        });
    }

    [Fact]
    public void The_read_only_tree_and_the_password_switches_are_features()
    {
        var src = Set(("AllowConnectionEditing", 0), ("AllowSavedPasswords", 0), ("AllowPasswordCopy", 0));
        Assert.Equal("Changing folders, connections and shares is turned off by your organisation – see Settings → Policies.", Features.WhyNot(Feature.ConnectionEditing, src));
        Assert.False(Features.IsAllowed(Feature.SavedPasswords, src));
        Assert.False(Features.IsAllowed(Feature.PasswordCopy, src));
        Assert.True(Features.IsAllowed(Feature.ConnectionEditing, Set()));
    }

    [Fact]
    public void Start_values_are_read_from_the_policy()
    {
        WithPolicy(Set(("StartupProtection", "required"), ("AllowWindowsHello", 0), ("AutoLockMinutes", 20), ("SettingsLocation", "Local"),
            ("DiagnosticLog", 0), ("DiagnosticLogRetentionDays", 7)), () =>
        {
            Assert.Equal("Required", AppPolicy.StartupProtection); // the word as the catalogue spells it
            Assert.False(AppPolicy.AllowWindowsHello);
            Assert.Equal(20, AppPolicy.AutoLockMinutes);
            Assert.Equal("Local", AppPolicy.SettingsLocation);
            Assert.False(AppPolicy.DiagnosticLog);
            Assert.Equal(7, DiagLog.RetentionDays);
        });
        WithPolicy(Set(("AutoLockMinutes", 0)), () => Assert.Null(AppPolicy.AutoLockMinutes)); // 1–1440
        Assert.True(AppPolicy.AllowWindowsHello || PolicyCatalog.Number(PolicyCatalog.AllowWindowsHello) == 0);
    }

    [Fact]
    public void The_diagnostic_log_follows_its_policy()
    {
        try
        {
            WithPolicy(Set(("DiagnosticLog", 0)), () =>
            {
                DiagLog.ForgetPolicy();
                Assert.False(DiagLog.Enabled);
            });
            DiagLog.ForgetPolicy();
            Assert.Equal(PolicyCatalog.Number(PolicyCatalog.DiagnosticLog) != 0, DiagLog.Enabled);
        }
        finally { DiagLog.ForgetPolicy(); }
    }

    [Fact]
    public void A_duplicate_takes_no_password_while_saving_them_is_blocked()
    {
        var data = new AppData();
        var c = new Connection { Name = "srv", Host = "srv", InheritCredentials = false, Credentials = new CredentialSet { Username = "u", Password = "pw" } };
        data.Items.Add(c);
        var vm = new MainViewModel(data);
        WithPolicy(Set(("AllowSavedPasswords", 0)), () =>
        {
            var copy = vm.Duplicate(c);
            Assert.Null(copy.Credentials.Password);
            Assert.Equal("u", copy.Credentials.Username);
            Assert.Equal("pw", c.Credentials.Password);
        });
    }

    [Fact]
    public void The_diagnostic_log_keeps_only_the_days_it_should()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rh-diaglog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "remotehub.log");
        try
        {
            string Line(DateTime at, string text) => $"{at:yyyy-MM-dd HH:mm:ss.fff}  {text}";
            File.WriteAllLines(path, [Line(DateTime.Now.AddDays(-40), "old"), "  the rest of an old message", Line(DateTime.Now.AddDays(-31), "older than 30"), Line(DateTime.Now.AddDays(-2), "recent"), "  a line without a date"]);
            File.WriteAllText(path + ".old", "rotated");
            File.SetLastWriteTime(path + ".old", DateTime.Now.AddDays(-45));

            DiagLog.ApplyRetention(30, path);

            Assert.Equal(["recent", "  a line without a date"], File.ReadAllLines(path).Select(l => l.Length > 25 ? l[25..] : l).ToArray());
            Assert.False(File.Exists(path + ".old"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
