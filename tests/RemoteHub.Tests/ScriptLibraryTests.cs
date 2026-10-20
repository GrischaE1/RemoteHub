using System.Collections.Generic;
using System.Management.Automation.Language;
using System.Text;
using RemoteHub.Data;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>
/// The built-in script library (docs/scripts-view-design.md §8): every script is in the app, has a valid header that
/// matches its file, and meets the catalog's standards – checked on the text with PowerShell's parser, never run.
/// </summary>
public class ScriptLibraryTests
{
    private static string LibraryFolder
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "src", "RemoteHub", "Assets", "ScriptLibrary"))) return Path.Combine(dir.FullName, "src", "RemoteHub", "Assets", "ScriptLibrary");
            throw new DirectoryNotFoundException("src\\RemoteHub\\Assets\\ScriptLibrary not found above " + AppContext.BaseDirectory);
        }
    }

    private static IEnumerable<string> Files() => Directory.GetFiles(LibraryFolder, "*.ps1", SearchOption.AllDirectories);

    [Fact]
    public void Every_catalog_entry_is_in_the_app_once()
    {
        var all = ScriptLibrary.All;
        Assert.Equal(Files().Count(), all.Count);
        Assert.Equal(all.Count, all.Select(s => s.Id).Distinct().Count());
        // The catalog's 97 entries: 20 INV, 16 SEC, 24 SRV, 14 DSC, 23 FIX.
        Assert.Equal([("INV", 20), ("SEC", 16), ("SRV", 24), ("DSC", 14), ("FIX", 23)],
            ScriptLibrary.Areas.Select(a => (a.Key, all.Count(s => s.Area == a.Key))));
        Assert.All(ScriptLibrary.StarterSet, id => Assert.NotNull(ScriptLibrary.Find(id)));
    }

    [Fact]
    public void Each_file_is_ascii_with_a_bom_and_its_header_matches_its_name_and_folder()
    {
        foreach (var file in Files())
        {
            var bytes = File.ReadAllBytes(file);
            var name = Path.GetFileName(file);
            Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, $"{name}: no UTF-8 BOM");
            Assert.True(bytes.Skip(3).All(b => b < 0x80), $"{name}: not ASCII-only");
            var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            var header = ScriptHeader.Read(text);
            Assert.True(header is { Id: not null, Context: not null, Os: not null, Risk: not null }, $"{name}: header incomplete");
            Assert.StartsWith(header!.Id + " ", name);
            Assert.Equal(Path.GetFileName(Path.GetDirectoryName(file)), header.Id!.Split('-')[0]);
            Assert.False(string.IsNullOrWhiteSpace(ScriptHeader.Synopsis(text)), $"{name}: no .SYNOPSIS");
        }
    }

    /// <summary>The catalog's USER entries for servers and Active Directory mean the admin's own account (§6.1, §8.2).</summary>
    [Fact]
    public void Admin_entries_run_as_me_and_paired_entries_as_both()
    {
        foreach (var id in new[] { "SRV-05", "SRV-07", "SRV-09", "SRV-10", "SRV-17", "SEC-09", "DSC-11", "DSC-14" })
            Assert.Equal(ScriptStore.ContextAsMe, ScriptLibrary.Find(id)!.Content.RunContext);
        foreach (var id in new[] { "DSC-03", "DSC-12", "FIX-07" })
            Assert.Equal(ScriptStore.ContextBoth, ScriptLibrary.Find(id)!.Content.RunContext);
        Assert.Equal(ScriptStore.ContextUser, ScriptLibrary.Find("INV-03")!.Content.RunContext);
    }

    [Fact]
    public void Every_script_parses_and_meets_the_catalog_standards()
    {
        var problems = new List<string>();
        foreach (var s in ScriptLibrary.All)
        {
            void Bad(string why) => problems.Add($"{s.Id}: {why}");
            var ast = Parser.ParseInput(s.Content.Body, out var tokens, out var errors);
            if (errors.Length > 0) { Bad("syntax error: " + errors[0].Message); continue; }
            var binding = ast.ParamBlock?.Attributes.FirstOrDefault(a => a.TypeName.Name.Equals("CmdletBinding", StringComparison.OrdinalIgnoreCase));
            if (binding is null) Bad("no [CmdletBinding()]");
            if (s.Content.Risk is ScriptStore.RiskR1 or ScriptStore.RiskR2
                && binding?.NamedArguments.Any(n => n.ArgumentName.Equals("SupportsShouldProcess", StringComparison.OrdinalIgnoreCase)) != true)
                Bad("R1/R2 without SupportsShouldProcess");
            if (s.Content.Risk == ScriptStore.RiskR2 && PsCompletion.Parameters(s.Content.Body).All(p => !(p.Name.Equals("Apply", StringComparison.OrdinalIgnoreCase) && p.Type == "switch")))
                Bad("R2 without an -Apply switch");
            if (PsCompletion.Parameters(s.Content.Body).Any(p => p.Mandatory)) Bad("a mandatory parameter (runs are unattended)");
            var commands = ast.FindAll(n => n is CommandAst, true).Cast<CommandAst>().Select(c => c.GetCommandName()).OfType<string>().ToList();
            foreach (var forbidden in new[] { "Get-WmiObject", "gwmi", "Read-Host", "Get-Credential", "Out-GridView", "Invoke-Expression", "iex" })
                if (commands.Any(c => c.Equals(forbidden, StringComparison.OrdinalIgnoreCase))) Bad("uses " + forbidden);
            // Mentioning it in a comment ("never Win32_Product") is fine; naming it in code is a query of it.
            if (ast.Find(n => n is StringConstantExpressionAst { Value: var v } && v.Contains("Win32_Product", StringComparison.OrdinalIgnoreCase), true) is not null)
                Bad("queries Win32_Product");
            // Windows PowerShell 5.1: no PowerShell 7 syntax.
            if (ast.Find(n => n is TernaryExpressionAst or PipelineChainAst, true) is not null) Bad("PowerShell 7 syntax (?: or && / ||)");
            if (ast.Find(n => n is BinaryExpressionAst { Operator: TokenKind.QuestionQuestion } or AssignmentStatementAst { Operator: TokenKind.QuestionQuestionEquals }, true) is not null)
                Bad("PowerShell 7 syntax (??)");
            if (tokens.Any(t => t.Kind is TokenKind.QuestionDot or TokenKind.QuestionLBracket)) Bad("PowerShell 7 syntax (?.)");
            if (ast.Find(n => n is CommandAst c && c.GetCommandName() is "ForEach-Object" or "%" && c.CommandElements.OfType<CommandParameterAst>().Any(p => p.ParameterName.Equals("Parallel", StringComparison.OrdinalIgnoreCase)), true) is not null)
                Bad("ForEach-Object -Parallel");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void A_library_header_says_how_the_script_runs()
    {
        var reboot = ScriptLibrary.Find("INV-05")!;
        Assert.Equal(("Pending reboot detector", ScriptStore.ContextSystem, ScriptStore.RiskR0), (reboot.Name, reboot.Content.RunContext, reboot.Content.Risk));
        Assert.Equal("library/INV-05", reboot.ScriptId);
        Assert.Same(reboot, ScriptLibrary.Find("library/INV-05"));
    }
}
