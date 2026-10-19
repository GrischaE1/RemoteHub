using System.Collections.Generic;
using RemoteHub.Remoting;

namespace RemoteHub.Tests;

/// <summary>How the file panes and the registry value list sort and filter.</summary>
public class ListOrderTests
{
    private static FileEntry F(string name, long size = 0, int day = 1) =>
        new(name, @"C:\x\" + name, EntryKind.File, size, new DateTime(2026, 10, day, 0, 0, 0, DateTimeKind.Utc), false);

    private static FileEntry D(string name) => new(name, @"C:\x\" + name, EntryKind.Folder, 0, null, false);

    private static List<string> Order(IEnumerable<FileEntry> entries, FileSortColumn by, bool descending)
    {
        var list = entries.ToList();
        list.Sort((a, b) => FileEntry.Compare(a, b, by, descending));
        return list.Select(e => e.Name).ToList();
    }

    [Fact]
    public void Folders_stay_above_files_whichever_column_sorts()
    {
        FileEntry[] entries = [F("b.txt", 300, 3), D("Zeta"), F("a.txt", 100, 5), D("alpha"), F("c.txt", 200, 1)];
        Assert.Equal(["alpha", "Zeta", "a.txt", "b.txt", "c.txt"], Order(entries, FileSortColumn.Name, descending: false));
        Assert.Equal(["Zeta", "alpha", "c.txt", "b.txt", "a.txt"], Order(entries, FileSortColumn.Name, descending: true));
        Assert.Equal(["alpha", "Zeta", "a.txt", "c.txt", "b.txt"], Order(entries, FileSortColumn.Size, descending: false));
        Assert.Equal(["Zeta", "alpha", "a.txt", "b.txt", "c.txt"], Order(entries, FileSortColumn.Modified, descending: true));
    }

    [Fact]
    public void The_file_filter_matches_part_of_the_name_ignoring_case()
    {
        Assert.True(FileEntry.Matches(F("Setup-Log.TXT"), "log"));
        Assert.True(FileEntry.Matches(F("Setup-Log.TXT"), "  .txt "));
        Assert.False(FileEntry.Matches(F("Setup-Log.TXT"), "setup.log"));
    }

    [Fact]
    public void The_default_value_stays_first_and_values_sort_by_column()
    {
        RegValue[] values =
        [
            new("Version", RegKind.String, "2.0"),
            new("", RegKind.String, "", IsSet: false),
            new("Enabled", RegKind.DWord, "1"),
            new("Path", RegKind.ExpandString, @"C:\Tools"),
        ];
        List<string> Sorted(RegSortColumn by, bool descending)
        {
            var list = values.ToList();
            list.Sort((a, b) => RegValue.Compare(a, b, by, descending));
            return list.Select(v => v.DisplayName).ToList();
        }
        Assert.Equal(["(Default)", "Enabled", "Path", "Version"], Sorted(RegSortColumn.Name, false));
        Assert.Equal(["(Default)", "Version", "Path", "Enabled"], Sorted(RegSortColumn.Name, true));
        Assert.Equal("(Default)", Sorted(RegSortColumn.Type, true)[0]);
        Assert.Equal("(Default)", Sorted(RegSortColumn.Data, false)[0]);
    }

    [Fact]
    public void The_value_filter_matches_the_name_or_the_data()
    {
        var v = new RegValue("InstallPath", RegKind.String, @"C:\Program Files\RemoteHub");
        Assert.True(RegValue.Matches(v, "install"));
        Assert.True(RegValue.Matches(v, "program files"));
        Assert.False(RegValue.Matches(v, "Agent"));
        Assert.True(RegValue.Matches(new RegValue("", RegKind.String, "", IsSet: false), "default"));
    }
}
