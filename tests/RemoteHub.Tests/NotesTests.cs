using System.Collections.Generic;
using System.Text;
using Microsoft.Data.Sqlite;
using RemoteHub.Data;

namespace RemoteHub.Tests;

/// <summary>Notes in the local database: notebooks, sections, pages, search, "Recently deleted", images, links, backup data.</summary>
[Collection("SettingsStore")]
public class NotesTests : IDisposable
{
    public NotesTests()
    {
        Assert.StartsWith(Path.GetTempPath(), LocalDb.Directory); // isolation guard
        StoreReset.Clean();
        LocalDb.Open();
    }

    public void Dispose() => StoreReset.Clean();

    private static (string Book, string Section) Default()
    {
        NoteStore.EnsureDefault();
        var section = NoteStore.Sections().Single();
        return (section.NotebookId, section.Id);
    }

    [Fact]
    public void First_start_creates_one_notebook_with_one_section_and_only_once()
    {
        NoteStore.EnsureDefault();
        NoteStore.EnsureDefault();
        Assert.Equal("Notes", Assert.Single(NoteStore.Notebooks()).Name);
        Assert.Equal("Quick Notes", Assert.Single(NoteStore.Sections()).Name);
    }

    [Fact]
    public void Notes_survive_a_restart_of_the_database()
    {
        var (_, section) = Default();
        var id = NoteStore.CreatePage(section, "Enable EARv2", "```sql\nupdate dbo.Setting set Value = 5846\n```", "update dbo.Setting set Value = 5846");
        LocalDb.Close();
        LocalDb.Open();
        Assert.Equal("Enable EARv2", NoteStore.Page(id)!.Title);
        Assert.Equal("```sql\nupdate dbo.Setting set Value = 5846\n```", NoteStore.Body(id));
    }

    [Fact]
    public void Pages_are_stored_exactly_as_given()
    {
        var (_, section) = Default();
        var body = "# Title  \r\n\tindented\n\n```powershell\n$a = `\"x`\"  \n````\n```\n| a \\| b | c |\n";
        var id = NoteStore.CreatePage(section, "x", body, "x");
        Assert.Equal(body, NoteStore.Body(id));
    }

    [Fact]
    public void Saving_an_unchanged_page_writes_nothing()
    {
        var (_, section) = Default();
        var id = NoteStore.CreatePage(section, "Page", "text", "text");
        var before = NoteStore.Page(id)!.ModifiedUtc;
        Assert.False(NoteStore.SavePage(id, "Page", "text", "text"));
        Assert.Equal(before, NoteStore.Page(id)!.ModifiedUtc);
        Assert.True(NoteStore.SavePage(id, "Page", "changed", "changed"));
    }

    [Fact]
    public void A_page_over_the_size_limit_is_refused()
    {
        var (_, section) = Default();
        var id = NoteStore.CreatePage(section, "Page");
        Assert.Throws<NotePageTooLargeException>(() => NoteStore.SavePage(id, "Page", new string('x', NoteStore.MaxBodyLength + 1), ""));
        Assert.Equal("", NoteStore.Body(id));
    }

    [Fact]
    public void Search_finds_words_by_prefix_in_titles_and_text_and_ignores_deleted_pages()
    {
        var (_, section) = Default();
        var a = NoteStore.CreatePage(section, "Winget repair", "", "Run winget source reset on the client");
        var b = NoteStore.CreatePage(section, "SQL", "", "UPDATE dbo.Setting SET Value = 5846 WHERE SystemCode = 1");
        Assert.Equal(a, Assert.Single(NoteStore.Search("wing res")).Page.Id);
        var hit = Assert.Single(NoteStore.Search("SystemCo"));
        Assert.Equal(b, hit.Page.Id);
        Assert.Contains(NoteStore.MatchStart + "SystemCode" + NoteStore.MatchEnd, hit.Snippet);
        NoteStore.Delete(NoteStore.PageKind, b);
        Assert.Empty(NoteStore.Search("SystemCode"));
        NoteStore.Restore(NoteStore.PageKind, b);
        Assert.Single(NoteStore.Search("SystemCode"));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("NEAR(a b)")]
    [InlineData("-x OR y*")]
    [InlineData("a\"b\" \"c")]
    [InlineData("^ ( ) : *")]
    public void Search_text_with_query_syntax_does_not_throw(string text)
    {
        var (_, section) = Default();
        NoteStore.CreatePage(section, "Page", "", "a b c x y");
        _ = NoteStore.Search(text);
    }

    [Fact]
    public void Search_of_nothing_returns_nothing()
    {
        Assert.Null(NoteStore.FtsQuery("   "));
        Assert.Empty(NoteStore.Search(""));
    }

    [Fact]
    public void Deleting_a_section_deletes_its_pages_and_restoring_brings_back_only_those()
    {
        var (_, section) = Default();
        var before = NoteStore.CreatePage(section, "Deleted before");
        NoteStore.Delete(NoteStore.PageKind, before);
        var kept = NoteStore.CreatePage(section, "Kept");
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // the section gets a later time stamp than that page
        System.Threading.SpinWait.SpinUntil(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > stamp);
        NoteStore.Delete(NoteStore.SectionKind, section);
        Assert.Empty(NoteStore.Sections());
        var deleted = NoteStore.Deleted();
        Assert.Contains(deleted, d => d.Kind == NoteStore.SectionKind && d.Pages == 1);
        Assert.Contains(deleted, d => d.Kind == NoteStore.PageKind && d.Id == before);
        NoteStore.Restore(NoteStore.SectionKind, section);
        Assert.Equal([kept], NoteStore.Pages(section).Select(p => p.Id));
    }

    [Fact]
    public void Deleting_a_notebook_lists_only_the_notebook_in_recently_deleted()
    {
        var (book, section) = Default();
        NoteStore.CreatePage(section, "A");
        NoteStore.CreatePage(section, "B");
        NoteStore.Delete(NoteStore.NotebookKind, book);
        var entry = Assert.Single(NoteStore.Deleted());
        Assert.Equal(NoteStore.NotebookKind, entry.Kind);
        Assert.Equal(2, entry.Pages);
        NoteStore.DeleteNow(NoteStore.NotebookKind, book);
        Assert.Empty(NoteStore.Deleted());
        Assert.Equal(0, NoteStore.Counts().Pages);
    }

    [Fact]
    public void Deleted_pages_are_removed_after_30_days()
    {
        var (_, section) = Default();
        var page = NoteStore.CreatePage(section, "Old");
        NoteStore.AddImage(page, "image/png", 1, 1, [1, 2, 3]);
        NoteStore.Delete(NoteStore.PageKind, page);
        Assert.Equal(0, NoteStore.PurgeDeleted(DateTime.UtcNow.AddDays(29)));
        Assert.Equal(1, NoteStore.PurgeDeleted(DateTime.UtcNow.AddDays(31)));
        Assert.Null(NoteStore.Page(page));
        Assert.Empty(NoteStore.Images(page));
    }

    [Fact]
    public void A_page_deleted_just_before_its_section_survives_deleting_the_section_now()
    {
        var (book, _) = Default();
        // Back to back, most of these fall within one millisecond – the case that shared a stamp.
        for (var i = 0; i < 50; i++)
        {
            var section = NoteStore.CreateSection(book, $"S{i}", null);
            var page = NoteStore.CreatePage(section, "Kept");
            NoteStore.Delete(NoteStore.PageKind, page);
            NoteStore.Delete(NoteStore.SectionKind, section);
            NoteStore.DeleteNow(NoteStore.SectionKind, section);
            Assert.NotNull(NoteStore.Page(page));
        }
    }

    [Fact]
    public void A_restored_page_whose_section_is_gone_goes_to_a_live_section()
    {
        var (book, section) = Default();
        var page = NoteStore.CreatePage(section, "Lonely");
        NoteStore.Delete(NoteStore.PageKind, page);
        var other = NoteStore.CreateSection(book, "Other", null);
        NoteStore.Delete(NoteStore.SectionKind, section);
        NoteStore.DeleteNow(NoteStore.SectionKind, section);
        NoteStore.Restore(NoteStore.PageKind, page);
        Assert.Equal(other, NoteStore.Page(page)!.SectionId);
    }

    [Fact]
    public void Images_no_longer_referenced_are_removed()
    {
        var (_, section) = Default();
        var page = NoteStore.CreatePage(section, "Pictures");
        var keep = NoteStore.AddImage(page, "image/png", 2, 2, [1]);
        var drop = NoteStore.AddImage(page, "image/jpeg", 2, 2, [2]);
        Assert.Equal(1, NoteStore.RemoveUnusedImages(page, [keep]));
        Assert.Equal([keep], NoteStore.Images(page).Select(i => i.Id));
        Assert.Throws<ArgumentException>(() => NoteStore.AddImage(page, "image/svg+xml", 1, 1, [3]));
        _ = drop;
    }

    [Fact]
    public void Links_find_pages_by_connection_and_count_them()
    {
        var (_, section) = Default();
        var a = NoteStore.CreatePage(section, "A");
        var b = NoteStore.CreatePage(section, "B");
        NoteStore.SetLinks(a, ["conn-1", "folder-1"]);
        NoteStore.SetLinks(b, ["conn-1"]);
        Assert.Equal(2, NoteStore.PagesLinkedTo(["conn-1"]).Count);
        Assert.Equal(2, NoteStore.LinkCounts()["conn-1"]);
        NoteStore.Delete(NoteStore.PageKind, b);
        Assert.Equal([a], NoteStore.PagesLinkedTo(["conn-1"]).Select(p => p.Id));
        Assert.Equal(1, NoteStore.LinkCounts()["conn-1"]);
    }

    [Fact]
    public void Backup_data_round_trip_gives_new_ids_and_keeps_images_and_links()
    {
        var (_, section) = Default();
        var page = NoteStore.CreatePage(section, "With picture");
        var img = NoteStore.AddImage(page, "image/png", 3, 4, [9, 8, 7]);
        NoteStore.SavePage(page, "With picture", $"![shot]({NoteStore.NoteImageScheme}{img})", "shot");
        NoteStore.SetLinks(page, ["conn-9"]);
        var export = NoteStore.ExportAll();

        Assert.Equal(1, NoteStore.Import(export, replace: false, addedSuffix: "backup"));
        var added = NoteStore.Notebooks().Single(b => b.Name == "Notes (backup)");
        var addedSection = NoteStore.Sections().Single(s => s.NotebookId == added.Id);
        var copy = NoteStore.Pages(addedSection.Id).Single();
        Assert.NotEqual(page, copy.Id);
        var copyImage = Assert.Single(NoteStore.Images(copy.Id));
        Assert.NotEqual(img, copyImage.Id);
        Assert.Equal([9, 8, 7], copyImage.Data);
        Assert.Equal($"![shot]({NoteStore.NoteImageScheme}{copyImage.Id})", NoteStore.Body(copy.Id));
        Assert.Equal(["conn-9"], NoteStore.Links(copy.Id));

        Assert.Equal(1, NoteStore.Import(export, replace: true, addedSuffix: null));
        Assert.Equal("Notes", Assert.Single(NoteStore.Notebooks()).Name);
        Assert.Equal(2, NoteStore.Deleted().Count(d => d.Kind == NoteStore.NotebookKind));
    }

    [Fact]
    public void Note_text_is_not_readable_in_the_database_file()
    {
        var (_, section) = Default();
        const string marker = "Zq9-note-plain-marker";
        NoteStore.CreatePage(section, marker, marker, marker);
        LocalDb.Close();
        var bytes = File.ReadAllBytes(LocalDb.FilePath);
        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) < 0);
        LocalDb.Open();
    }

    [Fact]
    public void A_version_1_database_migrates_to_version_2_with_a_backup_first()
    {
        LocalDb.Close();
        // Turn the fresh database back into version 1: drop the notes tables and the version.
        LocalDb.Open();
        LocalDb.Use((c, _) =>
        {
            foreach (var table in new[] { "notes_fts", "note_links", "note_files", "notes", "note_sections", "notebooks" })
                LocalDb.Exec(c, $"DROP TABLE {table}");
            // The later schema's tables too: a version 1 database had none of them.
            foreach (var table in new[] { "scripts_fts", "script_runs", "script_versions", "scripts", "script_folders", "deleted_nodes" })
                LocalDb.Exec(c, $"DROP TABLE {table}");
            LocalDb.Exec(c, "PRAGMA user_version = 1");
        });
        LocalDb.Close();
        var backupsBefore = LocalDb.Backups().Count;
        LocalDb.Open();
        Assert.Equal(backupsBefore + 1, LocalDb.Backups().Count);
        Assert.Equal((long)LocalDb.SchemaVersion, LocalDb.Use((c, _) => (long)LocalDb.Scalar(c, "PRAGMA user_version")!));
        NoteStore.EnsureDefault();
        Assert.Single(NoteStore.Notebooks());
    }

    [Fact]
    public void Backup_file_round_trip_includes_notes_and_an_older_backup_without_notes_imports()
    {
        var (_, section) = Default();
        var page = NoteStore.CreatePage(section, "In the backup", "```sql\nSELECT 1\n```\n", "SELECT 1");
        NoteStore.AddImage(page, "image/png", 1, 1, [5, 6]);
        var path = Path.Combine(Path.GetTempPath(), "rh-backup-" + Guid.NewGuid().ToString("N") + ".rhbackup");
        try
        {
            RemoteHub.Services.Backup.Export(new RemoteHub.Models.AppData(), path, "backup-password", NoteStore.ExportAll());
            Assert.DoesNotContain("SELECT 1", File.ReadAllText(path));
            RemoteHub.Services.Backup.Import(path, "backup-password", out var notes);
            Assert.NotNull(notes);
            Assert.Equal("```sql\nSELECT 1\n```\n", Assert.Single(notes!.Pages).Body);
            Assert.Equal([5, 6], Assert.Single(notes.Images).Data);
            Assert.Throws<UnauthorizedAccessException>(() => RemoteHub.Services.Backup.Import(path, "wrong", out _));

            RemoteHub.Services.Backup.Export(new RemoteHub.Models.AppData(), path, "backup-password");
            RemoteHub.Services.Backup.Import(path, "backup-password", out var none);
            Assert.Null(none);
        }
        finally { File.Delete(path); }
    }
}
