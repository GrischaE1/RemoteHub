using RemoteHub.Lab;

namespace RemoteHub.Lab.Tests;

/// <summary>RemoteHub Lab's reading of test files and run logs, and its estimate of the time left.</summary>
public class LabDataTests
{
    [Fact]
    public void A_test_file_gives_its_group_role_and_tagged_cases()
    {
        var f = Catalog.Parse("C-NOTES.Tests.ps1",
        [
            "# C-NOTES - Notes end to end",
            "# LabRole: console",
            "Describe 'C-NOTES <VmName>' -Tag Client, UI, 'C-NOTE' -Skip:(-not $isClient) {",
            "    It 'C-NOTE-01 the view rail switches views' -Tag 'C-NOTE-01' {",
            "    It 'C-NOTE-02 it''s kept after a restart' -Tag 'C-NOTE-02' {",
            "    It 'no case id here' {",
        ]);
        Assert.Equal("C-NOTES", f.Name);
        Assert.Equal("console", f.Role);
        Assert.Equal(["Client", "UI"], f.Groups);
        Assert.Equal("Client", f.Group);
        Assert.Equal(["C-NOTE-01", "C-NOTE-02"], f.Cases.Select(c => c.Id));
        Assert.Equal("C-NOTE-02 it's kept after a restart", f.Cases[1].Title);
    }

    [Fact]
    public void Between_runs_a_new_file_and_a_new_case_are_found_and_a_removed_case_counts_as_a_change()
    {
        var a = Catalog.Parse("A.Tests.ps1", ["Describe 'A' -Tag Agent {", "It 'A-01 one' -Tag 'A-01' {"]);
        var a2 = Catalog.Parse("A.Tests.ps1", ["Describe 'A' -Tag Agent {", "It 'A-01 one' -Tag 'A-01' {", "It 'A-02 two' -Tag 'A-02' {"]);
        var b = Catalog.Parse("B.Tests.ps1", ["Describe 'B' -Tag Agent {", "It 'B-01 new file' -Tag 'B-01' {"]);

        Assert.Equal((0, false), (Catalog.Compare([a], [a]).Added.Count, Catalog.Compare([a], [a]).Changed));
        var (added, changed) = Catalog.Compare([a], [a2, b]);
        Assert.True(changed);
        Assert.Equal(["A-02", "B-01"], added.Select(c => c.Id));
        Assert.Equal((0, true), (Catalog.Compare([a2], [a]).Added.Count, Catalog.Compare([a2], [a]).Changed));
    }

    [Fact]
    public void The_run_log_gives_starts_ends_failures_minutes_and_the_planned_count()
    {
        const string log = """
            8 file runs on 6 VMs
            10:30:54 start  C-DATA on Console-26H2-en-US (+ Server-2025-en-US)
            10:30:54 start  D-INPUT on W11-26H2-en-US-2
            10:43:50 done   D-INPUT on W11-26H2-en-US-2: 7 passed, 0 failed, 0 skipped (13 min)
            11:17:49 done   C-DATA on Console-26H2-en-US: 17 passed, 1 failed, 1 skipped (47 min)
            """;
        var e = RunLog.Parse(log);
        Assert.Equal(4, e.Count);
        Assert.Equal(8, RunLog.PlannedRuns(log));
        Assert.Equal(("D-INPUT", "W11-26H2-en-US-2", true, 0, 13.0), (e[2].File, e[2].Vm, e[2].Done, e[2].Failed, e[2].Minutes));
        Assert.Equal((1, 47.0), (e[3].Failed, e[3].Minutes));
        Assert.Equal("W11-26H2-en-US", RunLog.Kind("W11-26H2-en-US-2"));
        Assert.Equal("W11-26H2-en-US", RunLog.Kind("W11-26H2-en-US"));
    }

    [Fact]
    public void A_quick_run_takes_device_files_to_the_client_windows_kind_unless_they_ask_for_every_kind()
    {
        string[] kinds = ["Console-26H2-en-US", "Server-2025-en-US", "W11-26H2-en-US"];
        var device = Catalog.Parse("A.Tests.ps1", ["# A", "# LabRole: device"]);
        var every = Catalog.Parse("S.Tests.ps1", ["# S", "# LabRole: device", "# LabQuick: every kind"]);
        var console = Catalog.Parse("C.Tests.ps1", ["# C", "# LabRole: console"]);
        var any = Catalog.Parse("Smoke.Tests.ps1", ["# Smoke", "# LabRole: any"]);

        Assert.Equal(["Server-2025-en-US", "W11-26H2-en-US"], RunPlan.Kinds(device, kinds, quick: false));
        Assert.Equal(["W11-26H2-en-US"], RunPlan.Kinds(device, kinds, quick: true));
        Assert.Equal(["Server-2025-en-US", "W11-26H2-en-US"], RunPlan.Kinds(every, kinds, quick: true));
        Assert.Equal(["Console-26H2-en-US"], RunPlan.Kinds(console, kinds, quick: true));
        Assert.Equal(["Console-26H2-en-US", "W11-26H2-en-US"], RunPlan.Kinds(any, kinds, quick: true));
        Assert.Equal("Server-2025-en-US", RunPlan.QuickKind(["Console-26H2-en-US", "Server-2025-en-US"]));
    }

    [Fact]
    public void A_change_selects_the_files_of_its_area_and_an_unknown_path_selects_everything()
    {
        IReadOnlyList<TestFile> catalog =
        [
            Catalog.Parse("Smoke.Tests.ps1", ["# LabRole: any"]),
            Catalog.Parse("A-INST.Tests.ps1", ["# LabRole: device"]),
            Catalog.Parse("D-CLIP.Tests.ps1", ["# LabRole: device"]),
            Catalog.Parse("C-AUTO.Tests.ps1", ["# LabRole: device"]),
            Catalog.Parse("C-DATA.Tests.ps1", ["# LabRole: console"]),
            Catalog.Parse("UI-AGENT.Tests.ps1", ["# LabRole: console"]),
        ];
        Assert.Equal(["Smoke"], Areas.Affected(["docs/notes-view-design.md", "tests/RemoteHub.Tests/NotesTests.cs"], catalog)!.Order());
        Assert.Equal(["C-DATA", "Smoke", "UI-AGENT"], Areas.Affected([@"src\RemoteHub\Data\NoteStore.cs"], catalog)!.Order());
        Assert.Equal(["C-AUTO", "Smoke"], Areas.Affected(["src/RemoteHub.CredentialProvider/Provider.cpp"], catalog)!.Order());
        Assert.Equal(["A-INST", "C-AUTO", "D-CLIP", "Smoke", "UI-AGENT"], Areas.Affected(["src/RemoteHub.Agent/Program.cs"], catalog)!.Order());
        Assert.Equal(["D-CLIP", "Smoke"], Areas.Affected(["lab/tests/D-CLIP.Tests.ps1"], catalog)!.Order());
        Assert.Null(Areas.Affected(["lab/RemoteHub.TestLab/RemoteHub.TestLab.psm1"], catalog));
        Assert.Null(Areas.Affected(["RemoteHub.sln"], catalog));
    }

    [Fact]
    public void The_lanes_come_from_run_json_also_for_a_vm_that_only_lent_itself_to_others()
    {
        const string json = """
            { "RunId": "x", "Vms": [
                { "Id": "Server-2025-en-US", "Lanes": ["TestAutomation - Server - 2025 - en-US", "TestAutomation - Server - 2025 - en-US - 2"] },
                { "Id": "Console-26H2-en-US", "Lanes": ["a", "b", "c"] },
                { "Id": "W11-26H2-en-US", "Lanes": "TestAutomation - W11 - 26H2 - en-US" } ] }
            """;
        Assert.Equal([("Console-26H2-en-US", 3), ("Server-2025-en-US", 2), ("W11-26H2-en-US", 1)], LabVms.FromRunJson(json));
        Assert.Equal([("W11-26H2-en-US", 1)], LabVms.FromRunJson("""{ "Vms": { "Id": "W11-26H2-en-US", "Lanes": ["a"] } }"""));
        Assert.Empty(LabVms.FromRunJson("""{ "RunId": "x" }"""));
    }

    [Fact]
    public void The_estimate_weighs_what_each_file_took_before_and_shares_the_rest_between_the_vms()
    {
        var history = new LabHistory();
        history.Minutes[("A", "K1")] = [10, 20, 30];   // median 20
        history.Minutes[("B", "K1")] = [40];
        var planned = new List<(string, string)> { ("A", "K1"), ("B", "K1"), ("C", "K1") };   // C never timed: 10 min
        var log = RunLog.Parse("""
            10:00:00 start  A on K1
            10:20:00 done   A on K1: 3 passed, 0 failed, 0 skipped (20 min)
            10:20:00 start  B on K1
            """);
        var e = Estimate.For(planned, log, history, new TimeSpan(10, 30, 0), parallel: 1);
        Assert.Equal((1, 3), (e.Done, e.Total));
        // done 20 + B's 10 of 40 = 30 of 20 + 40 + 10 = 70
        Assert.Equal(30.0 / 70, e.Progress, 3);
        // B's 30 left + C's 10
        Assert.Equal(40, e.Left.TotalMinutes, 3);
        Assert.Equal(20, Estimate.For(planned, log, history, new TimeSpan(10, 30, 0), parallel: 2).Left.TotalMinutes, 3);
    }
}
