using System.Text;
using System.Text.Json;
using RemoteHub.Data;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>
/// Importing and exporting many scripts (docs/scripts-view-design.md §4.7): files and folders with their headers and
/// encodings, script packages with and without a password, and the checks that refuse a damaged or hostile package.
/// </summary>
public class ScriptTransferTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rh-scripts-" + Guid.NewGuid().ToString("N"));

    public ScriptTransferTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string File(string relative, byte[] bytes)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    private static readonly ScriptContent Content = new("Get-Date", "Shows the date", ScriptStore.ContextUser, ScriptStore.RiskR1, ScriptStore.OsClient, 300);

    [Fact]
    public void Folders_become_folders_and_headers_fill_in_the_settings()
    {
        File(@"Lab\Inventory\Disk.ps1", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("# RemoteHub: context=AsMe; risk=R2\r\nGet-Volume")).ToArray());
        File(@"Lab\Queries\Top.sql", Encoding.UTF8.GetBytes("-- RemoteHub: language=pgsql\nselect 1"));
        File(@"Lab\readme.txt", Encoding.UTF8.GetBytes("not a script"));
        var plan = ScriptTransfer.ReadFiles([Path.Combine(_dir, "Lab")]);
        Assert.Equal(1, plan.Skipped);
        Assert.Equal(["Lab", "Inventory", "Queries"], plan.Data.Folders.Select(f => f.Name));
        var ps = plan.Data.Scripts.Single(s => s.Name == "Disk");
        Assert.Equal((ScriptStore.PowerShell, ScriptStore.ContextAsMe, ScriptStore.RiskR2), (ps.Language, ps.Content.RunContext, ps.Content.Risk));
        Assert.Equal("Inventory", plan.Data.Folders.Single(f => f.Id == ps.FolderId).Name);
        var sql = plan.Data.Scripts.Single(s => s.Name == "Top");
        Assert.Equal("pgsql", sql.Language);
        Assert.Null(sql.Content.RunContext);
    }

    [Fact]
    public void Script_files_are_read_by_their_bom_and_as_windows_1252_without_valid_utf8()
    {
        Assert.Equal("Grüße", ScriptTransfer.Decode([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("Grüße")]));
        Assert.Equal("Grüße", ScriptTransfer.Decode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("Grüße")]));
        Assert.Equal("Grüße", ScriptTransfer.Decode(Encoding.UTF8.GetBytes("Grüße")));
        Assert.Equal("Grüße – €", ScriptTransfer.Decode([0x47, 0x72, 0xFC, 0xDF, 0x65, 0x20, 0x96, 0x20, 0x80]));
    }

    [Fact]
    public void Files_over_one_megabyte_are_left_out()
    {
        File("big.ps1", Encoding.UTF8.GetBytes(new string('x', ScriptStore.MaxBodyLength + 10)));
        File("small.ps1", Encoding.UTF8.GetBytes("Get-Date"));
        var plan = ScriptTransfer.ReadFiles(Directory.GetFiles(_dir));
        Assert.Equal(1, plan.Skipped);
        Assert.Equal("small", Assert.Single(plan.Data.Scripts).Name);
    }

    [Fact]
    public void Folders_nested_too_deep_are_refused()
    {
        var deep = string.Join(@"\", Enumerable.Range(0, ScriptTransfer.MaxDepth + 2).Select(i => "d" + i));
        File(deep + @"\x.ps1", Encoding.UTF8.GetBytes("Get-Date"));
        Assert.Throws<ScriptImportException>(() => ScriptTransfer.ReadFiles([Path.Combine(_dir, "d0")]));
    }

    [Fact]
    public void Exported_files_carry_their_settings_and_a_bom_and_never_overwrite()
    {
        var items = new[]
        {
            new ScriptTransfer.FileItem(["Lab", "Inventory"], "Disk: report", ScriptStore.PowerShell, Content, "INV-07"),
            new ScriptTransfer.FileItem(["Lab", "Inventory"], "Disk: report", ScriptStore.PowerShell, Content, null),
            new ScriptTransfer.FileItem([], "CON", "tsql", ScriptContent.Empty("tsql") with { Body = "select 1" }, null),
        };
        Assert.Equal(3, ScriptTransfer.WriteFiles(items, _dir));
        var first = Path.Combine(_dir, "Lab", "Inventory", "Disk- report.ps1");
        var bytes = System.IO.File.ReadAllBytes(first);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        Assert.StartsWith("# RemoteHub: id=INV-07; context=User; os=Client; risk=R1; timeout=300", Encoding.UTF8.GetString(bytes[3..]));
        Assert.True(System.IO.File.Exists(Path.Combine(_dir, "Lab", "Inventory", "Disk- report (2).ps1")));
        Assert.True(System.IO.File.Exists(Path.Combine(_dir, "_CON.sql")));
        // And they come back as they went.
        var plan = ScriptTransfer.ReadFiles([first]);
        Assert.Equal(Content with { Body = plan.Data.Scripts[0].Content.Body, Description = "" }, plan.Data.Scripts[0].Content with { Description = "" });
    }

    private static ScriptsExport Sample() => new()
    {
        Folders = [new("f1", null, "Inventory", "Blue", 0), new("f2", "f1", "Disks", null, 0)],
        Scripts = [new("s1", "f2", "Disk report", ScriptStore.PowerShell, Content, 2, "Red", "INV-07", 0, 1, 2)],
        Versions = [new("s1", 1, Content with { Body = "'v1'" }, "first", @"LAB\a", 10), new("s1", 2, Content, "second", @"LAB\a", 20)],
    };

    [Fact]
    public void A_protected_package_round_trips_and_needs_its_password()
    {
        var path = Path.Combine(_dir, "x.rhscripts");
        ScriptTransfer.WritePackage(Sample(), path, "correct horse");
        Assert.True(ScriptTransfer.IsProtected(path));
        Assert.DoesNotContain("Get-Date", System.IO.File.ReadAllText(path)); // encrypted
        Assert.Throws<ScriptImportException>(() => ScriptTransfer.ReadPackage(path, null));
        var wrong = Assert.Throws<ScriptImportException>(() => ScriptTransfer.ReadPackage(path, "wrong"));
        Assert.Contains("Wrong password", wrong.Message);
        var plan = ScriptTransfer.ReadPackage(path, "correct horse");
        Assert.True(plan.FromPackage);
        Assert.Equal(["first", "second"], plan.Data.Versions.Select(v => v.Note));
        Assert.Equal(Content, plan.Data.Scripts.Single().Content);
        Assert.Equal("Red", plan.Data.Scripts.Single().Color);
    }

    [Fact]
    public void A_package_without_a_password_is_plain_json()
    {
        var path = Path.Combine(_dir, "plain.rhscripts");
        ScriptTransfer.WritePackage(Sample(), path, null);
        Assert.False(ScriptTransfer.IsProtected(path));
        Assert.Contains("Get-Date", System.IO.File.ReadAllText(path));
        Assert.Equal("Disk report", ScriptTransfer.ReadPackage(path, null).Data.Scripts.Single().Name);
    }

    [Fact]
    public void Other_files_and_newer_packages_are_refused()
    {
        var path = Path.Combine(_dir, "x.rhscripts");
        System.IO.File.WriteAllText(path, "{\"Format\":\"remotehub-backup\"}");
        Assert.Contains("isn't a RemoteHub script package", Assert.Throws<ScriptImportException>(() => ScriptTransfer.ReadPackage(path, null)).Message);
        System.IO.File.WriteAllText(path, "not json");
        Assert.Throws<ScriptImportException>(() => ScriptTransfer.ReadPackage(path, null));
        System.IO.File.WriteAllText(path, "{\"Format\":\"remotehub-scripts\",\"Version\":9,\"Data\":\"{}\"}");
        Assert.Contains("newer version", Assert.Throws<ScriptImportException>(() => ScriptTransfer.ReadPackage(path, null)).Message);
    }

    [Fact]
    public void Hostile_package_contents_are_refused_as_a_whole()
    {
        void Refused(Action<ScriptsExport> damage)
        {
            var data = Sample();
            damage(data);
            Assert.Throws<ScriptImportException>(() => ScriptTransfer.Validate(data));
        }
        Refused(d => d.Scripts.Add(d.Scripts[0] with { Content = Content with { Body = new string('x', ScriptStore.MaxBodyLength + 1) } }));
        Refused(d => d.Versions.AddRange(Enumerable.Range(3, 10).Select(i => d.Versions[0] with { Number = i })));
        Refused(d => { d.Folders[0] = d.Folders[0] with { ParentId = "f2" }; }); // a loop
        Refused(d => d.Scripts.AddRange(Enumerable.Range(0, ScriptTransfer.MaxScripts).Select(i => d.Scripts[0] with { Id = "x" + i })));
        Refused(d => d.Scripts.Add(d.Scripts[0] with { Content = null! }));
        ScriptTransfer.Validate(Sample()); // and the sample itself is fine
    }

    [Fact]
    public void A_damaged_json_package_is_refused()
    {
        var path = Path.Combine(_dir, "x.rhscripts");
        var env = new { Format = "remotehub-scripts", Version = 1, Protected = false, Data = "{\"Scripts\":[{\"Id\":null}]}" };
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(env));
        Assert.Throws<ScriptImportException>(() => ScriptTransfer.ReadPackage(path, null));
    }

    [Fact]
    public void Names_become_safe_file_names()
    {
        Assert.Equal("a-b-c", ScriptTransfer.SafeName("a/b:c"));
        Assert.Equal("_NUL", ScriptTransfer.SafeName("NUL"));
        Assert.Equal("_com1.txt", ScriptTransfer.SafeName("com1.txt"));
        Assert.Equal("Script", ScriptTransfer.SafeName("..."));
        Assert.Equal(120, ScriptTransfer.SafeName(new string('a', 300)).Length);
    }
}
