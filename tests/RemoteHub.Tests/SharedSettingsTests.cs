using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RemoteHub.Models;
using RemoteHub.Services;

namespace RemoteHub.Tests;

internal static class TestIsolation
{
    /// <summary>Redirect SettingsStore to a temp folder before anything touches it – never the user's real %APPDATA%.</summary>
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("RDPMANAGER_DATA_DIR", Path.Combine(Path.GetTempPath(), "rdpm-tests-" + Environment.ProcessId));
}

[Collection("SettingsStore")]
public class SharedSettingsTests
{
    [Fact]
    public void Shared_folder_create_reopen_prompt_conflict_and_switch_back()
    {
        Assert.StartsWith(Path.GetTempPath(), SettingsStore.Directory); // isolation guard
        StoreReset.Clean();
        var shared = Path.Combine(Path.GetTempPath(), "rdpm-shared-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = new AppData();
            data.Ui.SidebarWidth = 333;
            var f = new Folder { Name = "Prod", InheritCredentials = false, Credentials = { Username = "adm", Password = Secret.From("S3cret!") } };
            f.Children.Add(new Connection { Name = "web01", Host = "web01", Parent = f });
            data.Items.Add(f);

            // 1. create shared file
            SettingsStore.CreateShared(shared, "shared-password", data);
            Assert.True(SettingsStore.IsShared);
            var fileText = File.ReadAllText(SettingsStore.FilePath);
            Assert.Contains("rdpmanager-settings", fileText);
            Assert.DoesNotContain("S3cret!", fileText);
            Assert.DoesNotContain("web01", fileText);

            // 2. next start on the same PC: cached key, no prompt; UI state comes from this PC
            int prompts = 0;
            SettingsStore.PasswordPrompt = _ => { prompts++; return null; };
            var loaded = SettingsStore.Load();
            Assert.Equal(0, prompts);
            Assert.Equal("web01", ((Folder)loaded.Items[0]).Children[0].Name);
            Assert.Equal(333, loaded.Ui.SidebarWidth);

            // 3. "new PC": no remembered key -> prompt (wrong first, then right)
            SettingsStore.ForgetRememberedPasswords();
            var answers = new Queue<string>(["wrong", "shared-password"]);
            SettingsStore.PasswordPrompt = _ => { prompts++; return answers.Dequeue(); };
            loaded = SettingsStore.Load();
            Assert.Equal(2, prompts);
            Assert.Equal("S3cret!", Inheritance.ResolveCredentials(((Folder)loaded.Items[0]).Children[0], loaded).Credentials.Password?.Reveal());

            // cancel -> OperationCanceledException (app then falls back to local settings)
            SettingsStore.ForgetRememberedPasswords();
            SettingsStore.PasswordPrompt = _ => null;
            Assert.Throws<OperationCanceledException>(() => SettingsStore.Load());
            SettingsStore.PasswordPrompt = _ => "shared-password";
            loaded = SettingsStore.Load();

            // 4. another device changed the file -> conflict on save, force overwrites
            File.AppendAllText(SettingsStore.FilePath, " ");
            SettingsStore.Save(loaded); // nothing changed here: nothing is written, so no conflict
            loaded.Tools.ProcessRefreshSeconds = 33;
            Assert.Throws<SettingsConflictException>(() => SettingsStore.Save(loaded));
            SettingsStore.Save(loaded, force: true);
            SettingsStore.Save(loaded); // no conflict any more

            // 5. opening an existing shared file with the wrong password fails
            SettingsStore.ForgetRememberedPasswords();
            Assert.Throws<UnauthorizedAccessException>(() => SettingsStore.OpenShared(shared, "nope", new UiState()));
            var opened = SettingsStore.OpenShared(shared, "shared-password", new UiState { SidebarWidth = 222 });
            Assert.Equal(222, opened.Ui.SidebarWidth);

            // 6. back to this PC only
            SettingsStore.SwitchToLocal(opened);
            Assert.False(SettingsStore.IsShared);
            Assert.True(File.Exists(Data.LocalDb.FilePath));
            Assert.False(File.Exists(SettingsStore.LocalFilePath)); // no settings.dat any more: this PC uses the database
            Assert.Equal("Prod", SettingsStore.Load().Items[0].Name);
        }
        finally
        {
            SettingsStore.PasswordPrompt = null;
            try { Directory.Delete(shared, true); } catch { }
            StoreReset.Clean();
        }
    }

    [Fact]
    public void Shared_file_keeps_its_iteration_count_and_refuses_unusable_ones()
    {
        Assert.StartsWith(Path.GetTempPath(), SettingsStore.Directory); // isolation guard
        StoreReset.Clean();
        var shared = Path.Combine(Path.GetTempPath(), "rdpm-shared-" + Guid.NewGuid().ToString("N"));
        static void SetIterations(int n) =>
            File.WriteAllText(SettingsStore.FilePath, System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(SettingsStore.FilePath), "\"Iterations\":\\s*\\d+", $"\"Iterations\": {n}"));
        try
        {
            var data = new AppData();
            data.Items.Add(new Connection { Name = "web01", Host = "web01" });
            SettingsStore.CreateShared(shared, "shared-password", data);

            // A file made with another count (another version or PC): this PC opens it with its remembered key and must
            // write the same count back, or the password stops working everywhere else.
            SetIterations(300_000);
            var loaded = SettingsStore.Load();
            loaded.Tools.ProcessRefreshSeconds = 33;
            SettingsStore.Save(loaded, force: true);
            Assert.Contains("\"Iterations\": 300000", File.ReadAllText(SettingsStore.FilePath));

            // A count no RemoteHub writes (damaged or crafted file) is refused before any key derivation.
            SetIterations(int.MaxValue);
            Assert.Throws<InvalidDataException>(() => SettingsStore.Load());
            SetIterations(1);
            Assert.Throws<InvalidDataException>(() => SettingsStore.Load());
        }
        finally
        {
            try { Directory.Delete(shared, true); } catch { }
            StoreReset.Clean();
        }
    }

    [Fact]
    public void First_edit_after_opening_an_existing_shared_file_is_written()
    {
        Assert.StartsWith(Path.GetTempPath(), SettingsStore.Directory); // isolation guard
        StoreReset.Clean();
        var shared = Path.Combine(Path.GetTempPath(), "rdpm-shared-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = new AppData();
            data.Items.Add(new Connection { Name = "web01", Host = "web01" });
            SettingsStore.CreateShared(shared, "shared-password", data);
            SettingsStore.SwitchToLocal(data);

            // a second PC: its own settings first, then it opens the existing shared file and edits once
            var opened = SettingsStore.OpenShared(shared, "shared-password", new UiState());
            var before = File.ReadAllBytes(SettingsStore.FilePath);
            opened.Items[0].Name = "web01-renamed";
            SettingsStore.Save(opened);
            Assert.NotEqual(before, File.ReadAllBytes(SettingsStore.FilePath));
            Assert.Equal("web01-renamed", SettingsStore.Load().Items[0].Name);
        }
        finally
        {
            try { Directory.Delete(shared, true); } catch { }
            StoreReset.Clean();
        }
    }

    [Fact]
    public void Old_narrow_sidebar_and_system_theme_are_upgraded_once()
    {
        Assert.StartsWith(Path.GetTempPath(), SettingsStore.Directory); // isolation guard
        StoreReset.Clean();
        var local = Path.Combine(SettingsStore.Directory, "local.json");
        try
        {
            File.WriteAllText(local, """{"Version":1,"Ui":{"SidebarWidth":300,"Theme":"System"}}""");
            SettingsStore.UsingLocalFallback = false;
            var data = SettingsStore.Load();
            Assert.Equal(360, data.Ui.SidebarWidth);
            Assert.Equal("Dark", data.Ui.Theme);

            // after the upgrade the user's own choices stay
            data.Ui.SidebarWidth = 310;
            data.Ui.Theme = "System";
            SettingsStore.Save(data, force: true);
            var again = SettingsStore.Load();
            Assert.Equal(310, again.Ui.SidebarWidth);
            Assert.Equal("System", again.Ui.Theme);
        }
        finally
        {
            StoreReset.Clean();
        }
    }
}
