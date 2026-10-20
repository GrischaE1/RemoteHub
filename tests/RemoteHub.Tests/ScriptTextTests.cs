using System.Collections.Generic;
using RemoteHub.Data;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>
/// Scripts as text (docs/scripts-view-design.md §4.7, §4.10, §5): comparing versions, the RemoteHub header in script
/// files, the editor's checks, and reading a script's parameters for the run window.
/// </summary>
public class ScriptTextTests
{
    // ------------------------------------------------------------------ comparing

    private static string Kinds(IReadOnlyList<DiffRow> rows) => string.Concat(rows.Select(r => r.Kind switch
    {
        DiffKind.Same => '=', DiffKind.Removed => '-', DiffKind.Added => '+', _ => '~',
    }));

    [Fact]
    public void Identical_and_empty_texts_have_no_changes()
    {
        Assert.Equal("===", Kinds(TextDiff.Compare("a\nb\nc", "a\nb\nc")));
        Assert.Empty(TextDiff.Compare("", ""));
        Assert.Equal(0, TextDiff.CountChanges(TextDiff.Compare("a\nb", "a\nb")));
    }

    [Fact]
    public void Line_ends_and_a_final_newline_dont_count()
    {
        Assert.Equal("==", Kinds(TextDiff.Compare("a\r\nb\r\n", "a\nb")));
    }

    [Fact]
    public void Additions_removals_and_changes_are_told_apart()
    {
        Assert.Equal("=+=", Kinds(TextDiff.Compare("a\nc", "a\nb\nc")));
        Assert.Equal("=-=", Kinds(TextDiff.Compare("a\nb\nc", "a\nc")));
        Assert.Equal("=~=", Kinds(TextDiff.Compare("a\nb\nc", "a\nB\nc")));
        Assert.Equal("++", Kinds(TextDiff.Compare("", "x\ny")));
        Assert.Equal("--", Kinds(TextDiff.Compare("x\ny", "")));
    }

    [Fact]
    public void A_changed_line_marks_only_the_changed_words()
    {
        var row = Assert.Single(TextDiff.Compare("if (Test-Path $cbs) {", "if (Test-Path -LiteralPath $cbs) {"));
        Assert.Equal(DiffKind.Changed, row.Kind);
        Assert.Empty(row.OldSpans);
        var span = Assert.Single(row.NewSpans);
        Assert.Equal("-LiteralPath ", row.NewText!.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Line_numbers_follow_each_side()
    {
        var rows = TextDiff.Compare("a\nb\nc", "x\na\nc\nd");
        Assert.Equal([(null, 1), (1, 2), (2, null), (3, 3), (null, 4)], rows.Select(r => (r.OldNumber, r.NewNumber)));
        Assert.Equal(3, TextDiff.CountChanges(rows));
    }

    [Fact]
    public void Non_ascii_text_compares_as_text()
    {
        var row = Assert.Single(TextDiff.Compare("Write-Output 'Größe'", "Write-Output 'Grösse'"));
        Assert.Equal(DiffKind.Changed, row.Kind);
    }

    [Fact]
    public void Two_large_texts_compare_within_a_second()
    {
        var a = string.Join("\n", Enumerable.Range(0, 20000).Select(i => $"Write-Output 'line {i}'"));
        var b = string.Join("\n", Enumerable.Range(0, 20000).Select(i => i % 500 == 0 ? $"Write-Output 'changed {i}'" : $"Write-Output 'line {i}'"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var rows = TextDiff.Compare(a, b);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
        Assert.Equal(40, TextDiff.CountChanges(rows));
    }

    [Fact]
    public void Texts_too_different_to_search_are_shown_as_replaced()
    {
        var a = string.Join("\n", Enumerable.Range(0, 3000).Select(i => $"a{i}"));
        var b = string.Join("\n", Enumerable.Range(0, 3000).Select(i => $"b{i}"));
        var rows = TextDiff.Compare(a, b);
        Assert.Equal(3000, rows.Count);
        Assert.All(rows, r => Assert.Equal(DiffKind.Changed, r.Kind));
    }

    // ------------------------------------------------------------------ the header

    [Fact]
    public void The_header_in_the_help_is_read()
    {
        const string text = "<#\r\n.SYNOPSIS\r\n    Returns whether a restart is pending.\r\n.NOTES\r\n    RemoteHub: id=INV-05; context=System; os=Both; risk=R0\r\n#>\r\nparam()";
        var h = ScriptHeader.Read(text)!;
        Assert.Equal(("INV-05", "System", "Both", "R0"), (h.Id, h.Context, h.Os, h.Risk));
        Assert.Equal("Returns whether a restart is pending.", ScriptHeader.Synopsis(text));
    }

    [Fact]
    public void Header_values_are_case_insensitive_and_invalid_ones_are_left_out()
    {
        var h = ScriptHeader.Read("# RemoteHub: CONTEXT=asme; risk=R9; os=server; timeout=999999; id=../x")!;
        Assert.Equal(ScriptStore.ContextAsMe, h.Context);
        Assert.Null(h.Risk);
        Assert.Equal(ScriptStore.OsServer, h.Os);
        Assert.Equal(ScriptStore.MaxTimeoutSec, h.TimeoutSec);
        Assert.Null(h.Id);
    }

    [Fact]
    public void A_header_far_down_the_file_isnt_one()
    {
        var text = string.Concat(Enumerable.Repeat("Get-Date\n", 100)) + "# RemoteHub: context=User";
        Assert.Null(ScriptHeader.Read(text));
    }

    [Fact]
    public void Writing_a_header_replaces_it_in_place_or_adds_one_on_top()
    {
        var content = new ScriptContent("", "", ScriptStore.ContextBoth, ScriptStore.RiskR2, ScriptStore.OsClient, 900);
        var written = ScriptHeader.Write("<#\n.NOTES\n    RemoteHub: id=FIX-07; context=System\n#>\nGet-Date", content, ScriptStore.PowerShell);
        Assert.Equal("<#\n.NOTES\n    RemoteHub: id=FIX-07; context=Both; os=Client; risk=R2; timeout=900\n#>\nGet-Date", written);
        var added = ScriptHeader.Write("Get-Date\r\n", content with { TimeoutSec = ScriptStore.DefaultTimeoutSec }, ScriptStore.PowerShell);
        Assert.Equal("# RemoteHub: context=Both; os=Client; risk=R2\r\nGet-Date\r\n", added);
        Assert.Equal("-- RemoteHub: language=pgsql\nselect 1", ScriptHeader.Write("select 1", ScriptContent.Empty("pgsql"), "pgsql"));
        // And back.
        var back = ScriptHeader.Apply(added, ScriptContent.Empty(ScriptStore.PowerShell), powerShell: true);
        Assert.Equal((ScriptStore.ContextBoth, ScriptStore.RiskR2, ScriptStore.OsClient), (back.RunContext, back.Risk, back.OsScope));
        Assert.Equal("pgsql", ScriptHeader.Read("-- RemoteHub: language=pgsql\nselect 1")!.Language);
    }

    // ------------------------------------------------------------------ the editor's checks

    [Fact]
    public void Syntax_errors_come_from_PowerShells_parser_with_their_line()
    {
        var errors = PsCompletion.Errors("Get-Date\nif ($x {\n");
        Assert.NotEmpty(errors); // the parser reports more than one problem at that place
        Assert.All(errors, e => Assert.Equal(2, e.Line));
        Assert.Empty(PsCompletion.Errors("Get-Date"));
    }

    [Fact]
    public void Lines_that_look_like_secrets_are_pointed_out()
    {
        Assert.Contains("Line 2", Views.ScriptEditor.SecretHint("Get-Date\n$password = 'Summer2026!'"));
        Assert.NotNull(Views.ScriptEditor.SecretHint("ConvertTo-SecureString 'P@ssw0rd' -AsPlainText -Force"));
        Assert.NotNull(Views.ScriptEditor.SecretHint("$apiKey = \"0123456789abcdef0123456789abcdef\""));
        Assert.Null(Views.ScriptEditor.SecretHint("$password = Read-Host -AsSecureString"));
        Assert.Null(Views.ScriptEditor.SecretHint("Get-LapsADPassword -Identity SRV01"));
    }

    [Fact]
    public void Braces_and_regions_fold()
    {
        var folds = Views.ScriptEditor.Foldings("#region Setup\n$a = 1\n#endregion\nif ($a) {\n  'x'\n}\n'{not a brace}'").ToList();
        Assert.Equal(2, folds.Count);
        Assert.Contains(folds, f => f.Name == "…");
    }

    [Fact]
    public void Completion_comes_from_PowerShell_on_this_PC()
    {
        var set = PsCompletion.Complete("Get-ChildIt", 11);
        Assert.NotNull(set);
        Assert.Contains(set!.Matches, m => m.CompletionText == "Get-ChildItem");
        Assert.Equal(0, set.ReplacementIndex);
    }

    // ------------------------------------------------------------------ parameters

    [Fact]
    public void A_scripts_parameters_are_read_with_their_types_choices_and_defaults()
    {
        const string text = """
            [CmdletBinding(SupportsShouldProcess)]
            param(
                [Parameter(Mandatory)] [string]$Name,
                [ValidateSet('Fast', 'Full')] [string]$Mode = 'Fast',
                [int]$Days = 30,
                [switch]$Apply,
                [string[]]$Hosts,
                [Parameter(HelpMessage = 'Where to write')] $Path = $env:TEMP
            )
            """;
        var p = PsCompletion.Parameters(text);
        Assert.Equal(["Name", "Mode", "Days", "Apply", "Hosts", "Path"], p.Select(x => x.Name));
        Assert.True(p[0].Mandatory);
        Assert.Equal(["Fast", "Full"], p[1].Choices);
        Assert.Equal("Fast", p[1].DefaultText);
        Assert.Equal(("int", "30"), (p[2].Type, p[2].DefaultText));
        Assert.Equal("switch", p[3].Type);
        Assert.Equal("string[]", p[4].Type);
        Assert.Equal("Where to write", p[5].Help);
        Assert.Empty(PsCompletion.Parameters("Get-Date"));
    }

    [Fact]
    public void Form_values_become_typed_arguments_and_empty_optional_ones_are_left_out()
    {
        var p = PsCompletion.Parameters("param([string]$Name, [int]$Days, [switch]$Apply, [switch]$Quiet, [string[]]$Hosts, [bool]$Strict)");
        var args = PsCompletion.Arguments(p, new Dictionary<string, string>
        {
            ["Name"] = " SRV01 ", ["Days"] = "45", ["Apply"] = "True", ["Quiet"] = "False", ["Hosts"] = "a, b\nc", ["Strict"] = "False",
        });
        Assert.Equal("SRV01", args["Name"]);
        Assert.Equal(45L, args["Days"]);
        Assert.Equal(true, args["Apply"]);
        Assert.False(args.ContainsKey("Quiet")); // an unset switch isn't passed
        Assert.Equal(new[] { "a", "b", "c" }, args["Hosts"]);
        Assert.Equal(false, args["Strict"]);
        Assert.Empty(PsCompletion.Arguments(p, new Dictionary<string, string> { ["Name"] = "  ", ["Days"] = "x" }));
    }
}
