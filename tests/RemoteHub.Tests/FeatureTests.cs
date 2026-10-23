using System.Collections.Generic;
using RemoteHub.Models;
using RemoteHub.Services;

namespace RemoteHub.Tests;

public class FeatureTests
{
    [Fact]
    public void Rdp_file_write_then_read_roundtrip_including_dpapi_password()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rdpm-{Guid.NewGuid():N}.rdp");
        var c = new Connection { Name = "App01", Host = "app01.lab.local", Port = 3391 };
        var o = RdpOptions.BuiltInDefaults();
        o.Resize = DisplayResize.Fixed; o.FixedResolution = "2560x1440"; o.RedirectDrives = true;
        o.ForwardWindowsKeys = false; o.Audio = AudioMode.PlayRemote; o.ColorDepth = 24;
        try
        {
            RdpFileWriter.Write(path, c, o, new CredentialSet { Username = "jdoe", Domain = "LAB", Password = Secret.From("Pa55!") }, includePassword: true);
            var back = RdpFileParser.Parse(path);
            Assert.Equal("app01.lab.local", back.Host);
            Assert.Equal(3391, back.Port);
            Assert.Equal("jdoe", back.Credentials.Username);
            Assert.Equal("LAB", back.Credentials.Domain);
            Assert.Equal("Pa55!", back.Credentials.Password?.Reveal());
            Assert.Equal("2560x1440", back.Options.FixedResolution);
            Assert.True(back.Options.RedirectDrives);
            Assert.False(back.Options.ForwardWindowsKeys);
            Assert.Equal(AudioMode.PlayRemote, back.Options.Audio);
            Assert.Equal(24, back.Options.ColorDepth);

            RdpFileWriter.Write(path, c, o, new CredentialSet { Username = "jdoe", Password = Secret.From("Pa55!") }, includePassword: false);
            Assert.DoesNotContain("password 51", File.ReadAllText(path));
            Assert.Null(RdpFileParser.Parse(path).Credentials.Password);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Saved_credentials_resolve_and_survive_backup()
    {
        var data = new AppData();
        var admin = new SavedCredential { Name = "Domain admin", Username = "adm", Domain = "CORP", Password = Secret.From("x") };
        data.SavedCredentials.Add(admin);
        var folder = new Folder { Name = "Servers", InheritCredentials = false, CredentialId = admin.Id };
        var conn = new Connection { Name = "s1", Host = "s1", Parent = folder };
        folder.Children.Add(conn); data.Items.Add(folder);

        var (cred, src) = Inheritance.ResolveCredentials(conn, data);
        Assert.Equal("adm", cred.Username);
        Assert.Contains("Domain admin", src);

        admin.Password = Secret.From("changed"); // editing the saved credential updates every user
        Assert.Equal("changed", Inheritance.ResolveCredentials(conn, data).Credentials.Password?.Reveal());
        Assert.Equal(1, Inheritance.UsageCount(data, admin.Id, data.Items.OfType<Folder>().SelectMany(f => f.Descendants()).Append(folder)));

        var path = Path.Combine(Path.GetTempPath(), $"rdpm-{Guid.NewGuid():N}.rdpmbackup");
        try
        {
            Backup.Export(data, path, "backup-password");
            var back = Backup.Import(path, "backup-password");
            var c2 = ((Folder)back.Items[0]).Children[0];
            Assert.Equal("changed", Inheritance.ResolveCredentials(c2, back).Credentials.Password?.Reveal());
        }
        finally { File.Delete(path); }

        data.SavedCredentials.Clear(); // deleted credential -> no credentials, Windows prompts
        Assert.True(Inheritance.ResolveCredentials(conn, data).Credentials.IsEmpty);
    }
}

public class SerializationTests
{
    [Fact]
    public void Runtime_only_state_is_not_persisted()
    {
        var data = new AppData();
        var c = new Connection { Name = "x", Host = "x", OpenSessions = 2, IsSelected = true };
        data.Items.Add(c);
        var json = System.Text.Encoding.UTF8.GetString(SettingsStore.ToJson(data));
        Assert.DoesNotContain("OpenSessions", json);
        Assert.DoesNotContain("IsSelected", json);
        Assert.DoesNotContain("Address", json);
        Assert.Equal(0, ((Connection)SettingsStore.FromJson(SettingsStore.ToJson(data)).Items[0]).OpenSessions);
    }
}

/// <summary>The session switch keys: presets and which key presses count as the hotkey.</summary>
public class HotkeyTests
{
    [Fact]
    public void Default_switch_key_is_the_german_less_than_key_with_shift_reversing()
    {
        var p = SwitchHotkeys.Default;
        Assert.Equal("Less", p.Id);
        Assert.Equal(0xE2, p.NextVk);   // VK_OEM_102: "< > |" key on ISO/German keyboards
        Assert.True(p.ShiftReverses);
        Assert.Equal(0xDC, SwitchHotkeys.Get("Caret").NextVk); // VK_OEM_5: "^" key on German keyboards
        Assert.False(SwitchHotkeys.Get("F1F2").ShiftReverses);
        Assert.Same(p, SwitchHotkeys.Get("unknown-id"));
        Assert.Equal("Less", new UiState().SwitchKeys);
    }

    private const int VK_OEM_102 = 0xE2, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_F1 = 0x70, VK_F2 = 0x71, VK_A = 0x41;

    [Theory]
    [InlineData("Less", VK_OEM_102)]
    [InlineData("Caret", 0xDC)]
    public void Single_key_presets_switch_to_the_next_session_and_back_with_shift(string id, int vk)
    {
        var p = SwitchHotkeys.Get(id);
        Assert.Equal(1, SwitchHotkeys.Match(vk, ctrl: true, leftAlt: true, shift: false, p));
        Assert.Equal(-1, SwitchHotkeys.Match(vk, ctrl: true, leftAlt: true, shift: true, p));
    }

    [Theory]
    [InlineData("F1F2", VK_F1, VK_F2)]
    [InlineData("PgUpPgDn", VK_PRIOR, VK_NEXT)]
    [InlineData("LeftRight", VK_LEFT, VK_RIGHT)]
    public void Two_key_presets_switch_by_key_whether_or_not_shift_is_held(string id, int prev, int next)
    {
        var p = SwitchHotkeys.Get(id);
        Assert.Equal(-1, SwitchHotkeys.Match(prev, ctrl: true, leftAlt: true, shift: false, p));
        Assert.Equal(1, SwitchHotkeys.Match(next, ctrl: true, leftAlt: true, shift: false, p));
        Assert.Equal(-1, SwitchHotkeys.Match(prev, ctrl: true, leftAlt: true, shift: true, p));
        Assert.Equal(1, SwitchHotkeys.Match(next, ctrl: true, leftAlt: true, shift: true, p));
    }

    [Fact]
    public void AltGr_with_the_switch_key_types_a_character_instead_of_switching()
    {
        // AltGr arrives as Ctrl + right Alt: left Alt is not held. AltGr+< has to type "|" in the session.
        Assert.Equal(0, SwitchHotkeys.Match(VK_OEM_102, ctrl: true, leftAlt: false, shift: false, SwitchHotkeys.Get("Less")));
        Assert.Equal(0, SwitchHotkeys.Match(VK_RIGHT, ctrl: true, leftAlt: false, shift: false, SwitchHotkeys.Get("LeftRight")));
    }

    [Fact]
    public void The_switch_key_without_ctrl_or_left_alt_does_not_switch()
    {
        var p = SwitchHotkeys.Get("Less");
        Assert.Equal(0, SwitchHotkeys.Match(VK_OEM_102, ctrl: false, leftAlt: true, shift: false, p));
        Assert.Equal(0, SwitchHotkeys.Match(VK_OEM_102, ctrl: false, leftAlt: false, shift: true, p));
        Assert.Equal(0, SwitchHotkeys.Match(VK_RIGHT, ctrl: false, leftAlt: true, shift: false, SwitchHotkeys.Get("LeftRight")));
    }

    [Fact]
    public void Other_keys_with_ctrl_and_left_alt_do_not_switch()
    {
        Assert.Equal(0, SwitchHotkeys.Match(VK_A, ctrl: true, leftAlt: true, shift: false, SwitchHotkeys.Get("Less")));
        Assert.Equal(0, SwitchHotkeys.Match(VK_RIGHT, ctrl: true, leftAlt: true, shift: false, SwitchHotkeys.Get("Less")));
        Assert.Equal(0, SwitchHotkeys.Match(VK_OEM_102, ctrl: true, leftAlt: true, shift: false, SwitchHotkeys.Get("LeftRight")));
        Assert.Equal(0, SwitchHotkeys.Match(VK_F1, ctrl: true, leftAlt: true, shift: false, SwitchHotkeys.Get("PgUpPgDn")));
    }
}

public class LocalAccountTests
{
    [Fact]
    public void Local_account_checkbox_maps_to_dot_domain_and_roundtrips_through_rdp_file()
    {
        var data = new AppData();
        var folder = new Folder { Name = "Lab", InheritCredentials = false };
        data.Items.Add(folder);

        var vm = new RemoteHub.ViewModels.EditorViewModel(folder, data, isNew: false)
        {
            Mode = RemoteHub.ViewModels.EditorViewModel.CredMode.Custom,
            Username = "admin",
            LocalAccount = true,
        };
        Assert.Equal(".", vm.Domain);
        Assert.False(vm.DomainEditable);
        Assert.True(vm.Apply());
        Assert.True(folder.Credentials.IsLocalAccount);

        var conn = new Connection { Name = "nas", Host = "nas01", Parent = folder };
        folder.Children.Add(conn);
        var cred = Inheritance.ResolveCredentials(conn, data).Credentials;
        Assert.Equal(("admin", "."), (cred.Username, cred.Domain));

        // Typing ".\admin" in the username field gives the same result.
        var vm2 = new RemoteHub.ViewModels.EditorViewModel(folder, data, isNew: false) { Username = @".\admin", Domain = null };
        Assert.True(vm2.Apply());
        Assert.Equal(("admin", "."), (folder.Credentials.Username, folder.Credentials.Domain));

        var path = Path.Combine(Path.GetTempPath(), $"rdpm-{Guid.NewGuid():N}.rdp");
        try
        {
            RdpFileWriter.Write(path, conn, RdpOptions.BuiltInDefaults(), cred, includePassword: false);
            Assert.Contains(@"username:s:.\admin", File.ReadAllText(path));
            Assert.True(RdpFileParser.Parse(path).Credentials.IsLocalAccount);
        }
        finally { File.Delete(path); }

        vm.LocalAccount = false;
        Assert.Null(vm.Domain);
    }
}

public class CredentialPromptMatcherTests
{
    private sealed record S(string Name, CredentialSet Cred, DateTime Started);
    private static readonly DateTime T0 = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private static S? Pick(IReadOnlyList<S> c, PromptOwner owner, params string[] texts) =>
        CredentialPromptMatcher.Pick(c, x => x.Cred, x => x.Started, owner, texts, T0.AddSeconds(20));

    [Theory]
    [InlineData("admin", "admin", null, true)]
    [InlineData(@"SERVER01\admin", "admin", null, true)]
    [InlineData(@".\admin", "admin", ".", true)]
    [InlineData("admin@contoso.com", "admin", null, true)]
    [InlineData("administrator", "admin", null, false)]
    [InlineData("Your credentials did not work", "admin", null, false)]
    public void Username_matching(string text, string user, string? domain, bool expected) =>
        Assert.Equal(expected, CredentialPromptMatcher.UserMatches(text, new CredentialSet { Username = user, Domain = domain }));

    [Fact]
    public void Picks_only_when_unambiguous()
    {
        var a = new S("a", new() { Username = "admin", Password = Secret.From("pw-a") }, T0);
        var b = new S("b", new() { Username = "ops", Password = Secret.From("pw-b") }, T0.AddSeconds(5));
        var a2 = new S("a2", new() { Username = "admin", Password = Secret.From("pw-other") }, T0.AddSeconds(9));
        var a3 = new S("a3", new() { Username = "admin", Password = Secret.From("pw-a") }, T0.AddSeconds(9));
        var old = new S("old", new() { Username = "admin", Password = Secret.From("pw") }, T0.AddMinutes(-5));

        // readable dialog text: match by username
        Assert.Same(a, Pick([a, b], PromptOwner.Ours, "Your credentials did not work", "admin"));
        Assert.Same(b, Pick([a, b], PromptOwner.Other, "ops"));
        Assert.Null(Pick([a, a2], PromptOwner.Ours, "admin"));           // same user, different passwords -> unsafe
        Assert.Same(a3, Pick([a, a3], PromptOwner.Ours, "admin"));       // same password -> newest
        Assert.Null(Pick([a, b], PromptOwner.Ours, "someone-else"));     // dialog shows another user

        // unreadable dialog (the usual case): ours/ownerless + exactly one recent session
        Assert.Same(a, Pick([a], PromptOwner.Ours));
        Assert.Same(a, Pick([a], PromptOwner.None));
        Assert.Null(Pick([a], PromptOwner.Other));                       // another app's dialog
        Assert.Null(Pick([a, b], PromptOwner.Ours));                     // two sessions connecting
        Assert.Null(Pick([old], PromptOwner.Ours));                      // connect started too long ago
        Assert.Null(Pick(Array.Empty<S>(), PromptOwner.Ours, "admin"));
    }
}

public class PasswordPromptSettingTests
{
    [Fact]
    public void Clipboard_is_the_default_and_inherits_per_folder()
    {
        var data = new AppData();
        var folder = new Folder { Name = "Lab" };
        var conn = new Connection { Name = "c", Host = "c", Parent = folder };
        folder.Children.Add(conn); data.Items.Add(folder);

        Assert.Equal(PasswordPromptAction.Clipboard, Inheritance.ResolveOptions(conn, data).PasswordPrompt);
        folder.Options.PasswordPrompt = PasswordPromptAction.Off;
        Assert.Equal(PasswordPromptAction.Off, Inheritance.ResolveOptions(conn, data).PasswordPrompt);
        conn.Options.PasswordPrompt = PasswordPromptAction.Clipboard;
        Assert.Equal(PasswordPromptAction.Clipboard, Inheritance.ResolveOptions(conn, data).PasswordPrompt);
        conn.Options.PasswordPrompt = PasswordPromptAction.Off;
        Assert.Equal(PasswordPromptAction.Off, Inheritance.ResolveOptions(conn, data).PasswordPrompt);
    }

    [Fact]
    public void Old_switches_are_migrated()
    {
        // v1.5.0 menu toggle off -> global default Off
        var d1 = SettingsStore.FromJson(System.Text.Encoding.UTF8.GetBytes(
            """{"Version":1,"Defaults":{},"Items":[],"Ui":{"CopyPasswordOnPrompt":false}}"""));
        Assert.Equal(PasswordPromptAction.Off, d1.Defaults.PasswordPrompt);
        Assert.Null(d1.Ui.CopyPasswordOnPrompt);

        // v1.5.1: defaults "on" (old default) -> new default auto-type; folder "on" -> clipboard; connection "off" -> off
        var d2 = SettingsStore.FromJson(System.Text.Encoding.UTF8.GetBytes("""
            {"Version":1,"Defaults":{"CopyPasswordOnPrompt":true},"Items":[
              {"$type":"folder","Name":"F","Options":{"CopyPasswordOnPrompt":true},"Children":[
                {"$type":"connection","Name":"C","Host":"h","Options":{"CopyPasswordOnPrompt":false}}]}]}
            """));
        Assert.Null(d2.Defaults.PasswordPrompt);
        var f = (Folder)d2.Items[0];
        Assert.Equal(PasswordPromptAction.Clipboard, f.Options.PasswordPrompt);
        Assert.Equal(PasswordPromptAction.Off, f.Children[0].Options.PasswordPrompt);
        Assert.Equal(PasswordPromptAction.Clipboard, Inheritance.ResolveOptions(null, d2).PasswordPrompt);

        // 1.9.1: "AutoType" (removed) becomes clipboard
        var d3 = SettingsStore.FromJson(System.Text.Encoding.UTF8.GetBytes("""
            {"Version":1,"Defaults":{"PasswordPrompt":"AutoType"},"Items":[
              {"$type":"connection","Name":"C","Host":"h","Options":{"PasswordPrompt":"AutoType"}}]}
            """));
        Assert.Equal(PasswordPromptAction.Clipboard, d3.Defaults.PasswordPrompt);
        Assert.Equal(PasswordPromptAction.Clipboard, d3.Items[0].Options.PasswordPrompt);
        Assert.DoesNotContain("CopyPasswordOnPrompt", System.Text.Encoding.UTF8.GetString(SettingsStore.ToJson(d2)));
    }
}

public class ColorTagTests
{
    [Fact]
    public void Colour_tag_inherits_and_tree_brushes_follow()
    {
        var data = new AppData();
        var prod = new Folder { Name = "Prod" };
        prod.Options.ColorTag = "Red";
        var sub = new Folder { Name = "DB", Parent = prod };
        var c = new Connection { Name = "sql", Host = "sql", Parent = sub };
        sub.Children.Add(c); prod.Children.Add(sub); data.Items.Add(prod);
        data.Items.Add(new Connection { Name = "jump", Host = "jump" });

        Assert.Equal("Red", Inheritance.ResolveOptions(c, data).ColorTag);
        Assert.Equal("None", Inheritance.ResolveOptions(data.Items[1], data).ColorTag);

        var vm = new RemoteHub.ViewModels.MainViewModel(data);
        Assert.NotNull(c.TagBrush);
        Assert.Null(data.Items[1].TagBrush);
        c.Options.ColorTag = "None";
        vm.RefreshTags();
        Assert.Null(c.TagBrush);
    }
}

public class SearchTests
{
    private static AppData Sample()
    {
        var d = new AppData();
        var prod = new Folder { Name = "Production" };
        var db = new Folder { Name = "DB", Parent = prod };
        db.Children.Add(new Connection { Name = "SQL cluster", Host = "sql01.prod.lab", Parent = db });
        prod.Children.Add(db);
        prod.Children.Add(new Connection { Name = "Web frontend", Host = "web01.prod.lab", Parent = prod, Notes = "IIS" });
        var test = new Folder { Name = "Test lab" };
        test.Children.Add(new Connection { Name = "SQL test", Host = "10.0.0.5", Parent = test });
        d.Items.Add(prod); d.Items.Add(test);
        return d;
    }

    private static List<SearchHit> Find(AppData d, string q) =>
        NodeSearch.Find(new RemoteHub.ViewModels.MainViewModel(d).AllNodes(), q);

    [Fact]
    public void Finds_folders_and_connections_with_path_and_ranking()
    {
        var d = Sample();
        var prod = Find(d, "prod");
        Assert.Equal("Production", prod[0].Node.Name);            // folder name match ranks above host matches
        Assert.True(prod[0].IsFolder);
        Assert.Contains(prod, h => h.Node.Name == "SQL cluster");  // via host / path

        var sql = Find(d, "sql");
        Assert.Equal(2, sql.Count);
        Assert.All(sql, h => Assert.True(h.IsConnection));
        Assert.Equal("Production › DB", sql.First(h => h.Node.Name == "SQL cluster").Path);

        var both = Find(d, "sql prod");                            // all words must match
        Assert.Single(both);
        Assert.Equal("SQL cluster", both[0].Node.Name);

        Assert.Equal("Web frontend", Assert.Single(Find(d, "iis")).Node.Name); // notes
        Assert.Equal("DB", Find(d, "db")[0].Node.Name);
        Assert.Empty(Find(d, "nothing-like-this"));
        Assert.Contains("1 connection", Find(d, "DB")[0].Subtitle);
    }
}

public class AppearanceTests
{
    [Fact]
    public void Icons_and_custom_colours_are_saved_and_shown()
    {
        var data = new AppData();
        var f = new Folder { Name = "Prod", Icon = "Server24" };
        f.Options.ColorTag = "#1E90FF";
        var c = new Connection { Name = "db", Host = "db", Parent = f, Icon = "Database24" };
        f.Children.Add(c); data.Items.Add(f);
        data.Items.Add(new Connection { Name = "plain", Host = "plain" });

        var back = SettingsStore.FromJson(SettingsStore.ToJson(data));
        var bf = (Folder)back.Items[0];
        Assert.Equal("Server24", bf.DisplaySymbol);
        Assert.Equal("Database24", bf.Children[0].DisplaySymbol);
        Assert.Equal("Desktop24", back.Items[1].DisplaySymbol);              // default icon
        Assert.Equal("#1E90FF", Inheritance.ResolveOptions(bf.Children[0], back).ColorTag);

        Assert.True(RemoteHub.Views.ColorTags.TryParseHex("#1e90ff", out var col));
        Assert.Equal((byte)0x1E, col.R);
        Assert.False(RemoteHub.Views.ColorTags.TryParseHex("blue", out _));
        Assert.Null(RemoteHub.Views.ColorTags.Get("None"));
        Assert.NotNull(RemoteHub.Views.ColorTags.Get("Red"));
    }

    [Fact]
    public void Editor_writes_icon_and_colour()
    {
        var data = new AppData();
        var f = new Folder { Name = "F" };
        data.Items.Add(f);
        var vm = new RemoteHub.ViewModels.EditorViewModel(f, data, isNew: false);
        vm.SelectedIcon = RemoteHub.Views.IconCatalog.All.First(i => i.Symbol == "Cloud24");
        vm.CustomColour = "#123456";                                        // selects the custom swatch
        Assert.True(vm.SelectedColour!.IsCustom);
        Assert.True(vm.Apply());
        Assert.Equal("Cloud24", f.Icon);
        Assert.Equal("#123456", f.Options.ColorTag);

        var vm2 = new RemoteHub.ViewModels.EditorViewModel(f, data, isNew: false);
        Assert.Equal("#123456", vm2.CustomColour);
        vm2.CustomColour = "nope";
        vm2.SelectedColour = vm2.ColourChoices.First(c => c.IsCustom);
        Assert.False(vm2.Apply());                                          // invalid custom colour
        vm2.SelectedColour = vm2.ColourChoices.First(c => c.IsInherit);
        Assert.True(vm2.Apply());
        Assert.Null(f.Options.ColorTag);
    }

    [Fact]
    public void Dark_is_the_default_theme() => Assert.Equal("Dark", new UiState().Theme);
}
