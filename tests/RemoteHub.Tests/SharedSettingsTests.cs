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
        var shared = Path.Combine(Path.GetTempPath(), "rdpm-shared-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = new AppData();
            data.Ui.SidebarWidth = 333;
            var f = new Folder { Name = "Prod", InheritCredentials = false, Credentials = { Username = "adm", Password = "S3cret!" } };
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
            Assert.Equal("S3cret!", Inheritance.ResolveCredentials(((Folder)loaded.Items[0]).Children[0], loaded).Credentials.Password);

            // cancel -> OperationCanceledException (app then falls back to local settings)
            SettingsStore.ForgetRememberedPasswords();
            SettingsStore.PasswordPrompt = _ => null;
            Assert.Throws<OperationCanceledException>(() => SettingsStore.Load());
            SettingsStore.PasswordPrompt = _ => "shared-password";
            loaded = SettingsStore.Load();

            // 4. another device changed the file -> conflict on save, force overwrites
            File.AppendAllText(SettingsStore.FilePath, " ");
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
            Assert.True(File.Exists(SettingsStore.LocalFilePath));
            Assert.Equal("Prod", SettingsStore.Load().Items[0].Name);
        }
        finally
        {
            SettingsStore.PasswordPrompt = null;
            try { Directory.Delete(shared, true); } catch { }
            try { Directory.Delete(SettingsStore.Directory, true); } catch { }
        }
    }

    [Fact]
    public void Old_narrow_sidebar_and_system_theme_are_upgraded_once()
    {
        Assert.StartsWith(Path.GetTempPath(), SettingsStore.Directory); // isolation guard
        Directory.CreateDirectory(SettingsStore.Directory);
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
            try { Directory.Delete(SettingsStore.Directory, true); } catch { }
        }
    }
}
