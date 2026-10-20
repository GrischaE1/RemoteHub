using System.Collections.Generic;
using RemoteHub.Data;

namespace RemoteHub.Tests;

/// <summary>
/// Scripts in the local database (docs/scripts-view-design.md §7): folders at any depth, drafts and versions, deleting
/// and restoring, search, the run history, and imports from packages and backups.
/// </summary>
[Collection("SettingsStore")]
public class ScriptStoreTests : IDisposable
{
    public ScriptStoreTests()
    {
        Assert.StartsWith(Path.GetTempPath(), LocalDb.Directory); // isolation guard
        StoreReset.Clean();
        LocalDb.Open();
    }

    public void Dispose() => StoreReset.Clean();

    private static ScriptContent Ps(string body, string context = ScriptStore.ContextSystem) =>
        new(body, "", context, ScriptStore.RiskR0, ScriptStore.OsBoth, ScriptStore.DefaultTimeoutSec);

    private static string NewScript(out string folder, string body = "Get-Date")
    {
        folder = ScriptStore.CreateFolder(null, "Inventory", "Blue");
        return ScriptStore.CreateScript(folder, "Disk report", ScriptStore.PowerShell, Ps(body));
    }

    [Fact]
    public void Folders_nest_to_any_depth_and_a_folder_cant_go_into_itself()
    {
        var a = ScriptStore.CreateFolder(null, "A", null);
        var b = ScriptStore.CreateFolder(a, "B", null);
        var c = ScriptStore.CreateFolder(b, "C", null);
        Assert.Equal([a, b, c], ScriptStore.FolderAndBelow(a));
        Assert.False(ScriptStore.MoveFolder(a, c));
        Assert.False(ScriptStore.MoveFolder(a, a));
        Assert.True(ScriptStore.MoveFolder(c, null));
        Assert.Null(ScriptStore.Folders().Single(f => f.Id == c).ParentId);
    }

    [Fact]
    public void A_new_script_is_a_draft_that_isnt_deployed()
    {
        var id = NewScript(out _);
        var s = ScriptStore.Script(id)!;
        Assert.False(s.IsDeployed);
        Assert.False(s.HasDraftChanges);
        Assert.Null(ScriptStore.Deployed(id));
        Assert.Equal("Get-Date", ScriptStore.Draft(id)!.Body);
    }

    [Fact]
    public void Autosave_changes_only_the_draft_and_an_unchanged_save_writes_nothing()
    {
        var id = NewScript(out _);
        Assert.Equal(1, ScriptStore.Deploy(id, "first", @"LAB\admin"));
        Assert.True(ScriptStore.SaveDraft(id, Ps("Get-Date; Get-Service")));
        Assert.False(ScriptStore.SaveDraft(id, Ps("Get-Date; Get-Service")));
        Assert.Equal("Get-Date", ScriptStore.Deployed(id)!.Content.Body);
        Assert.True(ScriptStore.Script(id)!.HasDraftChanges);
        // A setting is part of the draft too.
        Assert.True(ScriptStore.SaveDraft(id, Ps("Get-Date", ScriptStore.ContextBoth)));
        Assert.True(ScriptStore.Script(id)!.HasDraftChanges);
    }

    [Fact]
    public void Deploying_numbers_versions_and_keeps_the_last_ten_with_numbers_growing()
    {
        var id = NewScript(out _);
        for (var i = 1; i <= 12; i++)
        {
            ScriptStore.SaveDraft(id, Ps($"'{i}'"));
            Assert.Equal(i, ScriptStore.Deploy(id, $"note {i}", @"LAB\admin"));
        }
        var versions = ScriptStore.Versions(id);
        Assert.Equal(ScriptStore.KeptVersions, versions.Count);
        Assert.Equal(Enumerable.Range(3, 10).Reverse(), versions.Select(v => v.Version));
        Assert.Equal("'12'", ScriptStore.Deployed(id)!.Content.Body);
        Assert.Equal("note 12", versions[0].Note);
        Assert.Equal(@"LAB\admin", versions[0].DeployedBy);
        ScriptStore.SaveDraft(id, Ps("'13'"));
        Assert.Equal(13, ScriptStore.Deploy(id, "", "x"));
    }

    [Fact]
    public void Discarding_and_restoring_change_the_draft_never_a_version()
    {
        var id = NewScript(out _, "'one'");
        ScriptStore.Deploy(id, "", "x");
        ScriptStore.SaveDraft(id, Ps("'two'"));
        ScriptStore.Deploy(id, "", "x");
        ScriptStore.SaveDraft(id, Ps("'three'"));
        Assert.True(ScriptStore.DiscardDraft(id));
        Assert.Equal("'two'", ScriptStore.Draft(id)!.Body);
        Assert.False(ScriptStore.Script(id)!.HasDraftChanges);
        Assert.True(ScriptStore.RestoreToDraft(id, 1));
        Assert.Equal("'one'", ScriptStore.Draft(id)!.Body);
        Assert.Equal("'two'", ScriptStore.Deployed(id)!.Content.Body); // still what runs
        Assert.Equal(["'two'", "'one'"], ScriptStore.Versions(id).Select(v => v.Content.Body));
    }

    [Fact]
    public void A_deleted_folder_comes_back_with_its_scripts_and_versions()
    {
        var id = NewScript(out var folder);
        var inner = ScriptStore.CreateFolder(folder, "Disks", null);
        var second = ScriptStore.CreateScript(inner, "Volumes", ScriptStore.PowerShell, Ps("Get-Volume"));
        ScriptStore.Deploy(id, "", "x");
        ScriptStore.Delete(ScriptStore.FolderKind, folder);
        Assert.Empty(ScriptStore.Scripts());
        Assert.Empty(ScriptStore.Search("Volume"));
        var entry = Assert.Single(ScriptStore.Deleted());
        Assert.Equal((ScriptStore.FolderKind, 2), (entry.Kind, entry.Scripts));
        ScriptStore.Restore(ScriptStore.FolderKind, folder);
        Assert.Equal(new[] { id, second }.Order(), ScriptStore.Scripts().Select(s => s.Id).Order());
        Assert.Equal(1, ScriptStore.Deployed(id)!.Version);
        Assert.Single(ScriptStore.Search("Volume"));
    }

    [Fact]
    public void A_script_restored_after_its_folder_went_lands_in_a_live_folder()
    {
        var id = NewScript(out var folder);
        ScriptStore.Delete(ScriptStore.ScriptKind, id);
        ScriptStore.Delete(ScriptStore.FolderKind, folder);
        ScriptStore.Restore(ScriptStore.ScriptKind, id);
        var s = ScriptStore.Script(id)!;
        Assert.Contains(ScriptStore.Folders(), f => f.Id == s.FolderId);
    }

    [Fact]
    public void Deleting_for_good_removes_the_versions_too()
    {
        var id = NewScript(out _);
        ScriptStore.Deploy(id, "", "x");
        ScriptStore.Delete(ScriptStore.ScriptKind, id);
        ScriptStore.DeleteNow(ScriptStore.ScriptKind, id);
        Assert.Null(ScriptStore.Script(id));
        Assert.Empty(ScriptStore.Versions(id));
        Assert.Empty(ScriptStore.Deleted());
    }

    [Fact]
    public void Old_deletions_are_purged_after_thirty_days()
    {
        var id = NewScript(out _);
        ScriptStore.Delete(ScriptStore.ScriptKind, id);
        Assert.Equal(0, ScriptStore.PurgeDeleted(DateTime.UtcNow.AddDays(29)));
        Assert.Equal(1, ScriptStore.PurgeDeleted(DateTime.UtcNow.AddDays(31)));
        Assert.Null(ScriptStore.Script(id));
    }

    [Fact]
    public void Search_finds_names_descriptions_and_text_and_treats_the_query_as_text()
    {
        var folder = ScriptStore.CreateFolder(null, "F", null);
        var a = ScriptStore.CreateScript(folder, "Pending reboot", ScriptStore.PowerShell, Ps("Test-Path HKLM:\\x") with { Description = "Restart needed?" });
        var b = ScriptStore.CreateScript(folder, "Other", ScriptStore.PowerShell, Ps("Get-WinEvent -LogName System"));
        Assert.Equal([a], ScriptStore.Search("reboot").Select(h => h.Script.Id));
        Assert.Equal([a], ScriptStore.Search("restart").Select(h => h.Script.Id));
        Assert.Equal([b], ScriptStore.Search("WinEvent").Select(h => h.Script.Id));
        Assert.Empty(ScriptStore.Search("\" OR NEAR("));
    }

    [Fact]
    public void A_run_keeps_its_hash_and_version_after_that_version_is_no_longer_kept()
    {
        var id = NewScript(out _);
        ScriptStore.Deploy(id, "", "x");
        var hash = ScriptStore.Deployed(id)!.Sha256;
        ScriptStore.AddRun(new ScriptRunRecord("r1", id, 1, hash, "node", ScriptStore.ContextSystem, null, "WinRM", DateTime.UtcNow, 1200, "Succeeded", 0, "C: 12 GB free"), keepOutput: true);
        for (var i = 0; i < 11; i++) { ScriptStore.SaveDraft(id, Ps($"'{i}'")); ScriptStore.Deploy(id, "", "x"); }
        Assert.Null(ScriptStore.Version(id, 1));
        var run = Assert.Single(ScriptStore.Runs(id));
        Assert.Equal(1, run.Version);
        Assert.Equal(hash, run.Sha256);
        Assert.Equal("C: 12 GB free", run.Output);
        Assert.Equal([id], ScriptStore.RecentlyRun());
    }

    [Fact]
    public void Output_is_kept_for_the_last_ten_runs_of_a_script_and_not_at_all_when_turned_off()
    {
        var id = NewScript(out _);
        for (var i = 0; i < 12; i++)
            ScriptStore.AddRun(new ScriptRunRecord($"r{i}", id, 1, [1], "node", ScriptStore.ContextSystem, null, "Agent", DateTime.UtcNow.AddMinutes(i), 10, "Succeeded", 0, $"out {i}"), keepOutput: true);
        var runs = ScriptStore.Runs(id);
        Assert.Equal(12, runs.Count);
        Assert.Equal(ScriptStore.KeptRunOutputs, runs.Count(r => r.Output is not null));
        Assert.Null(runs.Single(r => r.Id == "r0").Output);
        ScriptStore.AddRun(new ScriptRunRecord("off", id, 1, [1], "node", ScriptStore.ContextSystem, null, "Agent", DateTime.UtcNow.AddHours(1), 10, "Failed", 1, "secret-ish"), keepOutput: false);
        Assert.Null(ScriptStore.Runs(id).Single(r => r.Id == "off").Output);
        ScriptStore.ClearRunOutputs();
        Assert.All(ScriptStore.Runs(id), r => Assert.Null(r.Output));
    }

    [Fact]
    public void Old_output_goes_after_thirty_days()
    {
        var id = NewScript(out _);
        ScriptStore.AddRun(new ScriptRunRecord("old", id, 1, [1], "node", ScriptStore.ContextSystem, null, "Agent", DateTime.UtcNow.AddDays(-31), 10, "Succeeded", 0, "x"), keepOutput: true);
        ScriptStore.PruneRunOutputs();
        Assert.Null(Assert.Single(ScriptStore.Runs(id)).Output);
    }

    [Fact]
    public void An_import_brings_the_history_and_without_keep_deployed_nothing_runs_until_deployed_here()
    {
        var id = NewScript(out _, "'v1'");
        ScriptStore.Deploy(id, "first", "LAB\\a");
        ScriptStore.SaveDraft(id, Ps("'v2'"));
        ScriptStore.Deploy(id, "second", "LAB\\a");
        var export = ScriptStore.Export([id]);
        StoreReset.Clean();
        LocalDb.Open();

        Assert.Equal(1, ScriptStore.Import(export, null, keepDeployed: false));
        var s = Assert.Single(ScriptStore.Scripts());
        Assert.False(s.IsDeployed);
        Assert.Equal("'v2'", ScriptStore.Draft(s.Id)!.Body);
        Assert.Equal([2, 1], ScriptStore.Versions(s.Id).Select(v => v.Version));
        Assert.Equal(3, ScriptStore.Deploy(s.Id, "", "x")); // numbers continue

        Assert.Equal(1, ScriptStore.Import(export, null, keepDeployed: true));
        var deployed = ScriptStore.Scripts().Single(x => x.Id != s.Id);
        Assert.Equal(2, deployed.DeployedVersion);
        Assert.Equal("Disk report (2)", deployed.Name); // the name was taken in the same folder
    }

    [Fact]
    public void Replacing_on_import_makes_a_new_draft_and_keeps_the_versions()
    {
        var id = NewScript(out var folder, "'mine'");
        ScriptStore.Deploy(id, "", "x");
        var incoming = new ScriptsExport
        {
            Folders = [new("f", null, "Inventory", null, 0)],
            Scripts = [new("s", "f", "Disk report", ScriptStore.PowerShell, Ps("'theirs'"), null, null, null, 0, 0, 0)],
        };
        Assert.Equal(id, ScriptStore.FindExisting(null, ["Inventory"], "Disk report"));
        Assert.Equal(1, ScriptStore.Import(incoming, null, keepDeployed: false, (_, _) => ImportChoice.Replace));
        Assert.Single(ScriptStore.Scripts());
        Assert.Equal("'theirs'", ScriptStore.Draft(id)!.Body);
        Assert.Equal("'mine'", ScriptStore.Deployed(id)!.Content.Body);
        Assert.Equal(0, ScriptStore.Import(incoming, null, keepDeployed: false, (_, _) => ImportChoice.Skip));
        Assert.Single(ScriptStore.Scripts());
    }

    [Fact]
    public void A_backup_that_replaces_everything_moves_the_current_scripts_to_recently_deleted()
    {
        var id = NewScript(out _);
        var backup = new ScriptsExport { Scripts = [new("x", "none", "From backup", "tsql", new ScriptContent("select 1", "", "System", "R0", null, 5), null, null, null, 0, 0, 0)] };
        Assert.Equal(1, ScriptStore.Import(backup, null, keepDeployed: true, replaceAll: true));
        Assert.Null(ScriptStore.Scripts().FirstOrDefault(s => s.Id == id));
        Assert.Contains(ScriptStore.Deleted(), d => d.Kind == ScriptStore.FolderKind && d.Scripts == 1); // listed with its folder, deleted together
        var sql = Assert.Single(ScriptStore.Scripts());
        Assert.Equal("tsql", sql.Language);
        Assert.Null(sql.RunContext); // SQL scripts have no run settings
    }

    [Fact]
    public void Text_over_one_megabyte_is_refused()
    {
        var id = NewScript(out _);
        Assert.Throws<ScriptTooLargeException>(() => ScriptStore.SaveDraft(id, Ps(new string('x', ScriptStore.MaxBodyLength + 1))));
    }

    [Fact]
    public void A_version_2_database_migrates_to_version_3_with_a_backup_first()
    {
        var folder = ScriptStore.CreateFolder(null, "F", null);
        LocalDb.Use((c, _) =>
        {
            foreach (var table in new[] { "scripts_fts", "script_runs", "script_versions", "scripts", "script_folders", "deleted_nodes" })
                LocalDb.Exec(c, $"DROP TABLE {table}");
            LocalDb.Exec(c, "PRAGMA user_version = 2");
        });
        LocalDb.Close();
        var before = LocalDb.Backups().Count;
        LocalDb.Open();
        Assert.Equal(before + 1, LocalDb.Backups().Count);
        Assert.Equal(3L, LocalDb.Use((c, _) => (long)LocalDb.Scalar(c, "PRAGMA user_version")!));
        Assert.DoesNotContain(ScriptStore.Folders(), f => f.Id == folder);
        Assert.NotNull(ScriptStore.CreateFolder(null, "After", null));
    }
}
