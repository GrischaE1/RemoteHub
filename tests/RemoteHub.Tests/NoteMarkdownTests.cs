using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>Notes: the page's Markdown ↔ the editor's document, plain text for search, and script languages.</summary>
public class NoteMarkdownTests
{
    private sealed class FakeScript(string language, string code) : Border, INoteScriptBlock
    {
        public string LanguageId { get; } = language;
        public string Code { get; } = code;
    }

    private sealed class FakeImage(string id, string alt, double? width) : Border, INoteImage
    {
        public string ImageId { get; } = id;
        public string Alt { get; } = alt;
        public double? DisplayWidth { get; } = width;
    }

    private sealed class Factory : INoteElementFactory
    {
        public FrameworkElement ScriptBlock(string language, string code) => new FakeScript(language, code);
        public FrameworkElement Image(string imageId, string alt, double? width) => new FakeImage(imageId, alt, width);
    }

    /// <summary>FlowDocuments need an STA thread.</summary>
    private static T Sta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Exception("On the STA thread: " + error.Message, error);
        return result;
    }

    private static string RoundTrip(string md) => Sta(() => NoteMarkdown.ToMarkdown(NoteMarkdown.ToFlowDocument(md, new Factory())));

    private const string Img = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("# Heading one\n")]
    [InlineData("## Heading two\n")]
    [InlineData("### Heading three\n")]
    [InlineData("Plain text with **bold**, *italic*, ~~strike~~ and `code`.\n")]
    [InlineData("A [link](https://example.com/a) in text.\n")]
    [InlineData("- one\n- two\n- three\n")]
    [InlineData("1. first\n2. second\n")]
    [InlineData("- [ ] open\n- [x] done\n")]
    [InlineData("- outer\n  - inner\n")]
    [InlineData("> quoted text\n")]
    [InlineData("---\n")]
    [InlineData("| Name | Value |\n| --- | ---: |\n| a | **1** |\n| b | `2` |\n")]
    [InlineData("```powershell\nGet-Service | Where-Object Status -ne 'Running'\n```\n")]
    [InlineData("```sql\nupdate dbo.Setting set Value = 5846\n\nwhere SystemCodeId = 1\n```\n")]
    [InlineData("```\nplain\n```\n")]
    [InlineData("````\na ``` inside\n````\n")]
    [InlineData("![screen shot](note-file:" + Img + ")\n")]
    [InlineData("![shot](note-file:" + Img + "){width=640}\n")]
    [InlineData("First line\\\nsecond line\n")]
    [InlineData("Text\n\nMore text\n")]
    public void Every_element_survives_a_round_trip(string md) => Assert.Equal(md, RoundTrip(md));

    [Theory]
    [InlineData("1\\. not a list\n")]
    [InlineData("\\# not a heading\n")]
    [InlineData("a \\* b \\[c\\] \\<d> e\\|f\n")]
    [InlineData("snake_case stays, \\_edge\\_ is escaped\n")]
    public void Text_with_markdown_characters_reads_back_as_the_same_text(string md) => Assert.Equal(md, RoundTrip(md));

    [Fact]
    public void Script_block_text_keeps_tabs_spaces_and_language()
    {
        var md = "```csharp\n\tvar x = 1;   \n    // indented\n```\n";
        Assert.Equal(md, RoundTrip(md));
    }

    [Fact]
    public void Fence_aliases_are_written_as_the_language_id_and_unknown_ids_are_kept()
    {
        Assert.Equal("```powershell\nx\n```\n", RoundTrip("```ps1\nx\n```\n"));
        Assert.Equal("```csharp\nx\n```\n", RoundTrip("```c#\nx\n```\n"));
        Assert.Equal("```fsharp\nlet x = 1\n```\n", RoundTrip("```fsharp\nlet x = 1\n```\n"));
    }

    [Fact]
    public void Unsupported_markdown_is_kept_as_text()
    {
        var md = "#### Level four\n";
        Assert.Equal(md, RoundTrip(md));
        var nested = "> outer\n> > inner\n";
        Assert.Contains("inner", RoundTrip(nested));
    }

    [Fact]
    public void Raw_html_in_markdown_stays_text()
    {
        var text = Sta(() =>
        {
            var doc = NoteMarkdown.ToFlowDocument("<script>alert(1)</script>\n", new Factory());
            return new TextRange(doc.ContentStart, doc.ContentEnd).Text;
        });
        Assert.Contains("<script>", text);
    }

    [Fact]
    public void Only_images_from_the_page_store_become_images()
    {
        var count = Sta(() => NoteMarkdown.ToFlowDocument("![x](https://example.com/a.png) ![y](file:///c:/a.png)\n", new Factory())
            .Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<InlineUIContainer>().Count());
        Assert.Equal(0, count);
    }

    [Fact]
    public void Plain_text_has_text_code_and_no_markers()
    {
        var text = NoteMarkdown.PlainText("# Title\n\nSome **bold** text.\n\n```sql\nSELECT 1\n```\n\n| a | b |\n| - | - |\n| c | d |\n");
        Assert.Contains("Title", text);
        Assert.Contains("bold", text);
        Assert.Contains("SELECT 1", text);
        Assert.Contains("d", text);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("```", text);
    }

    [Fact]
    public void Image_ids_are_found_in_the_markdown()
    {
        Assert.Equal([Img], NoteMarkdown.ImageIds($"![a](note-file:{Img}) and ![b](note-file:{Img}){{width=3}}"));
    }

    [Theory]
    [InlineData("Get-Service -Name 'x' | Where-Object Status -ne 'Running'", "powershell")]
    [InlineData("$items = Get-ChildItem C:\\", "powershell")]
    [InlineData("SELECT Name FROM Users WHERE Id = 1", "sql")]
    [InlineData("update dbo.Setting set Value = 5846 where SystemCodeId = 1", "tsql")]
    [InlineData("SigninLogs\n| where ResultType != 0\n| summarize count() by UserPrincipalName", "kql")]
    [InlineData("{ \"a\": 1, \"b\": [1, 2] }", "json")]
    [InlineData("<?xml version=\"1.0\"?><a/>", "xml")]
    [InlineData("Windows Registry Editor Version 5.00\n\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\X]", "reg")]
    [InlineData("#!/bin/bash\necho hi", "bash")]
    [InlineData("#include <stdio.h>\nint main() { return 0; }", "c")]
    [InlineData("using System;\nnamespace A { class B {} }", "csharp")]
    [InlineData("@echo off\nset X=1", "batch")]
    [InlineData("Just a sentence about the weather.", "text")]
    [InlineData("{ not json", "text")]
    public void Pasted_code_is_given_its_language_only_from_clear_signs(string text, string expected) =>
        Assert.Equal(expected, ScriptLanguages.Guess(text));

    [Fact]
    public void Fence_aliases_map_to_one_language()
    {
        Assert.Equal("powershell", ScriptLanguages.Normalize("ps1"));
        Assert.Equal("csharp", ScriptLanguages.Normalize("C#"));
        Assert.Equal("yaml", ScriptLanguages.Normalize("yml"));
        Assert.Equal("fsharp", ScriptLanguages.Normalize("fsharp"));
        Assert.Equal("text", ScriptLanguages.Normalize(""));
        Assert.All(ScriptLanguages.All, l => Assert.Same(l, ScriptLanguages.Find(l.Id)));
    }

    [Fact]
    public void Every_language_has_colouring_or_is_plain_on_purpose()
    {
        var plain = new[] { "text", "log", "csv" };
        var missing = Sta(() => ScriptLanguages.All.Where(l => !plain.Contains(l.Id) && RemoteHub.Views.ScriptHighlighting.For(l.Id) is null).Select(l => l.Id).ToList());
        Assert.Empty(missing);
    }
}
