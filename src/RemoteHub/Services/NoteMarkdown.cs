using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfList = System.Windows.Documents.List;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;

namespace RemoteHub.Services;

/// <summary>What a script block in the editor holds (implemented by the editor's script block control).</summary>
public interface INoteScriptBlock
{
    string LanguageId { get; }
    string Code { get; }
}

/// <summary>What an image in the editor holds (implemented by the editor's image control).</summary>
public interface INoteImage
{
    string ImageId { get; }
    string Alt { get; }
    /// <summary>Display width in device-independent pixels; null = natural size.</summary>
    double? DisplayWidth { get; }
}

/// <summary>Creates the editor's embedded controls while a page is read.</summary>
public interface INoteElementFactory
{
    FrameworkElement ScriptBlock(string language, string code);
    FrameworkElement Image(string imageId, string alt, double? width);
}

/// <summary>
/// A page's Markdown ↔ the editor's FlowDocument (docs/notes-view-design.md §4.5.6). Only the elements of §4.5.2–§4.5.5
/// are read or written; Markdown that the editor can't show is kept as text in a paragraph, never dropped. Markdown is
/// data only – nothing is loaded as XAML, and raw HTML stays text.
/// </summary>
public static partial class NoteMarkdown
{
    public const string H1 = "h1", H2 = "h2", H3 = "h3", Raw = "raw", Quote = "quote", Rule = "hr", Code = "code";

    // Text sizes of the page; the theme has no document styles, so they are named here.
    public const double BodySize = 14, H1Size = 26, H2Size = 21, H3Size = 17;

    /// <summary>The Markdig pipeline for notes: CommonMark plus pipe tables, task lists, strikethrough and attributes; no HTML.</summary>
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UseGenericAttributes()
        .UsePreciseSourceLocation()
        .DisableHtml()
        .Build();

    /// <summary>Plain text for the full-text index (also the text of script blocks and images' alternative text).</summary>
    public static string PlainText(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";
        var doc = Markdown.Parse(markdown, Pipeline);
        var sb = new StringBuilder();
        foreach (var node in doc.Descendants())
        {
            switch (node)
            {
                case LiteralInline l: sb.Append(l.Content.ToString()).Append(' '); break;
                case CodeInline c: sb.Append(c.Content).Append(' '); break;
                case LeafBlock { Inline: null } leaf when leaf.Lines.Count > 0: sb.AppendLine(leaf.Lines.ToString()); break;
                case LinkInline { IsImage: true } img: sb.Append(img.Title).Append(' '); break;
                case LineBreakInline: sb.Append('\n'); break;
            }
            if (node is ParagraphBlock or HeadingBlock) sb.Append('\n');
        }
        return sb.ToString().Trim();
    }

    /// <summary>The ids of the images a page refers to.</summary>
    public static IReadOnlyCollection<string> ImageIds(string markdown) =>
        ImageRef().Matches(markdown).Select(m => m.Groups[1].Value).ToHashSet();

    [GeneratedRegex(@"\(note-file:([0-9a-f]{32})\)")]
    private static partial Regex ImageRef();

    // ================================================================== Markdown → FlowDocument

    /// <summary>Builds the editor's document from a page's Markdown.</summary>
    public static FlowDocument ToFlowDocument(string markdown, INoteElementFactory factory)
    {
        var doc = new FlowDocument { FontSize = BodySize, PagePadding = new Thickness(0) };
        var parsed = Markdown.Parse(markdown ?? "", Pipeline);
        foreach (var b in parsed) AddBlock(doc.Blocks, b, markdown ?? "", factory, 0);
        if (doc.Blocks.Count == 0) doc.Blocks.Add(new Paragraph());
        return doc;
    }

    private static void AddBlock(BlockCollection target, MdBlock block, string source, INoteElementFactory f, int depth)
    {
        switch (block)
        {
            case HeadingBlock h when h.Level <= 3:
                var hp = Heading(h.Level);
                AddInlines(hp.Inlines, h.Inline, f);
                target.Add(hp);
                break;
            case ParagraphBlock p:
                var para = new Paragraph();
                AddInlines(para.Inlines, p.Inline, f);
                target.Add(para);
                break;
            case FencedCodeBlock fenced:
                target.Add(new BlockUIContainer(f.ScriptBlock(ScriptLanguages.Normalize(fenced.Info), CodeText(fenced))));
                break;
            case CodeBlock code: // indented code
                target.Add(new BlockUIContainer(f.ScriptBlock(ScriptLanguages.PlainId, CodeText(code))));
                break;
            case ListBlock list:
                target.Add(BuildList(list, source, f, depth));
                break;
            case QuoteBlock q when depth == 0:
                var section = new Section { Tag = Quote };
                StyleQuote(section);
                foreach (var inner in q) AddBlock(section.Blocks, inner, source, f, depth + 1);
                target.Add(section);
                break;
            case ThematicBreakBlock:
                target.Add(RuleBlock());
                break;
            case MdTable t:
                target.Add(BuildTable(t, f));
                break;
            default:
                // Anything else (deeper quotes, headings 4-6, link definitions…) stays as its own text.
                var text = source.Substring(block.Span.Start, Math.Max(0, Math.Min(block.Span.Length, source.Length - block.Span.Start)));
                if (text.Length > 0) target.Add(RawParagraph(text));
                break;
        }
    }

    private static string CodeText(LeafBlock code)
    {
        var lines = code.Lines;
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(lines.Lines[i].Slice.ToString());
        }
        return sb.ToString();
    }

    private static WpfList BuildList(ListBlock list, string source, INoteElementFactory f, int depth)
    {
        var wl = new WpfList
        {
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, depth == 0 ? 8 : 0), Padding = new Thickness(22, 0, 0, 0),
        };
        if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 1) wl.StartIndex = start;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var li = new ListItem();
            foreach (var inner in item) AddBlock(li.Blocks, inner, source, f, depth + 1);
            if (li.Blocks.Count == 0) li.Blocks.Add(new Paragraph());
            // "- [ ] text": the space after the box belongs to the marker, not to the text.
            if (li.Blocks.FirstBlock is Paragraph { Inlines.FirstInline: InlineUIContainer { Child: CheckBox, NextInline: Run { Text: [' ', ..] } after } })
                after.Text = after.Text[1..];
            foreach (var p in li.Blocks.OfType<Paragraph>()) p.Margin = new Thickness(0);
            wl.ListItems.Add(li);
        }
        return wl;
    }

    private static WpfTable BuildTable(MdTable t, INoteElementFactory f)
    {
        var table = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 10) };
        table.SetResourceReference(WpfTable.BorderBrushProperty, "ChromeStrokeBrush");
        var columns = t.OfType<MdTableRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
        for (var i = 0; i < columns; i++)
            table.Columns.Add(new TableColumn { Tag = i < t.ColumnDefinitions.Count ? Align(t.ColumnDefinitions[i].Alignment) : "" });
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        var first = true;
        foreach (var row in t.OfType<MdTableRow>())
        {
            var tr = new System.Windows.Documents.TableRow();
            foreach (var cell in row.OfType<MdTableCell>())
            {
                var p = new Paragraph { Margin = new Thickness(0) };
                if (cell.LastChild is ParagraphBlock pb) AddInlines(p.Inlines, pb.Inline, f);
                if (row.IsHeader) p.FontWeight = FontWeights.SemiBold;
                var idx = tr.Cells.Count;
                if (idx < table.Columns.Count) p.TextAlignment = ToTextAlignment(table.Columns[idx].Tag as string);
                tr.Cells.Add(StyleCell(new WpfTableCell(p)));
            }
            while (tr.Cells.Count < table.Columns.Count) tr.Cells.Add(StyleCell(new WpfTableCell(new Paragraph { Margin = new Thickness(0) })));
            if (first && !row.IsHeader) group.Rows.Add(EmptyHeader(table.Columns.Count));
            group.Rows.Add(tr);
            first = false;
        }
        return table;
    }

    private static System.Windows.Documents.TableRow EmptyHeader(int columns)
    {
        var tr = new System.Windows.Documents.TableRow();
        for (var i = 0; i < columns; i++) tr.Cells.Add(StyleCell(new WpfTableCell(new Paragraph { Margin = new Thickness(0), FontWeight = FontWeights.SemiBold })));
        return tr;
    }

    /// <summary>The look of a table cell (border and padding), also for cells the editor adds.</summary>
    public static WpfTableCell StyleCell(WpfTableCell cell)
    {
        cell.BorderThickness = new Thickness(0.5);
        cell.Padding = new Thickness(8, 4, 8, 4);
        cell.SetResourceReference(WpfTableCell.BorderBrushProperty, "ChromeStrokeBrush");
        return cell;
    }

    private static string Align(TableColumnAlign? a) => a switch
    {
        TableColumnAlign.Center => "center",
        TableColumnAlign.Right => "right",
        TableColumnAlign.Left => "left",
        _ => "",
    };

    public static TextAlignment ToTextAlignment(string? align) => align switch
    {
        "center" => TextAlignment.Center,
        "right" => TextAlignment.Right,
        _ => TextAlignment.Left,
    };

    private static void AddInlines(InlineCollection target, ContainerInline? container, INoteElementFactory f)
    {
        if (container is null) return;
        foreach (var inline in container) AddInline(target, inline, f);
    }

    private static void AddInline(InlineCollection target, MdInline inline, INoteElementFactory f)
    {
        switch (inline)
        {
            case LiteralInline l:
                target.Add(new Run(l.Content.ToString()));
                break;
            case CodeInline c:
                target.Add(CodeRun(c.Content));
                break;
            case EmphasisInline e:
                Span span = e.DelimiterChar == '~' ? new Span { TextDecorations = TextDecorations.Strikethrough }
                    : e.DelimiterCount >= 2 ? new Bold() : new Italic();
                foreach (var child in e) AddInline(span.Inlines, child, f);
                target.Add(span);
                break;
            case LinkInline { IsImage: true } img when img.Url?.StartsWith(NoteStoreImageScheme, StringComparison.Ordinal) == true:
                var alt = string.Concat(img.Descendants<LiteralInline>().Select(x => x.Content.ToString()));
                double? width = null;
                if (img.GetAttributes().Properties?.FirstOrDefault(p => p.Key == "width").Value is { } w
                    && double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var px) && px > 0) width = px;
                target.Add(new InlineUIContainer(f.Image(img.Url[NoteStoreImageScheme.Length..], alt, width)) { BaselineAlignment = BaselineAlignment.Bottom });
                break;
            case LinkInline { IsImage: true } other:
                // Images are only ever loaded from the page's own store: anything else stays its text.
                target.Add(new Run($"![{string.Concat(other.Descendants<LiteralInline>().Select(x => x.Content.ToString()))}]({other.Url})"));
                break;
            case LinkInline link:
                var h = new Hyperlink { Tag = link.Url ?? "" };
                foreach (var child in link) AddInline(h.Inlines, child, f);
                target.Add(h);
                break;
            case AutolinkInline auto:
                var a = new Hyperlink(new Run(auto.Url)) { Tag = auto.IsEmail ? "mailto:" + auto.Url : auto.Url };
                target.Add(a);
                break;
            case TaskList task:
                target.Add(new InlineUIContainer(new CheckBox { IsChecked = task.Checked, Margin = new Thickness(0, 0, 4, -3), Padding = new Thickness(0), MinWidth = 0, Focusable = false })
                    { BaselineAlignment = BaselineAlignment.Center });
                break;
            case LineBreakInline br:
                if (br.IsHard) target.Add(new LineBreak());
                else target.Add(new Run(" "));
                break;
            case HtmlEntityInline ent:
                target.Add(new Run(ent.Transcoded.ToString()));
                break;
            case ContainerInline c:
                foreach (var child in c) AddInline(target, child, f);
                break;
            default:
                target.Add(new Run(inline.ToString() ?? ""));
                break;
        }
    }

    private const string NoteStoreImageScheme = Data.NoteStore.NoteImageScheme;

    /// <summary>A heading paragraph of level 1–3, styled.</summary>
    public static Paragraph Heading(int level)
    {
        var p = new Paragraph { Tag = level switch { 1 => H1, 2 => H2, _ => H3 }, FontWeight = FontWeights.SemiBold };
        p.FontSize = level switch { 1 => H1Size, 2 => H2Size, _ => H3Size };
        p.Margin = new Thickness(0, level == 1 ? 12 : 10, 0, 6);
        return p;
    }

    public static Paragraph RawParagraph(string text) => new(new Run(text)) { Tag = Raw };

    public static Run CodeRun(string text)
    {
        var r = new Run(text) { Tag = Code, FontFamily = MonoFont };
        r.SetResourceReference(TextElement.BackgroundProperty, "NoteCodeBackgroundBrush");
        return r;
    }

    public static readonly FontFamily MonoFont = new("Cascadia Mono, Cascadia Code, Consolas, Courier New");

    public static bool IsMono(FontFamily? f) => f?.Source == MonoFont.Source;

    public static void StyleQuote(Section s)
    {
        s.BorderThickness = new Thickness(3, 0, 0, 0);
        s.Padding = new Thickness(12, 0, 0, 0);
        s.Margin = new Thickness(0, 4, 0, 8);
        s.SetResourceReference(Section.BorderBrushProperty, "ChromeStrokeBrush");
    }

    public static BlockUIContainer RuleBlock()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
        line.SetResourceReference(Border.BackgroundProperty, "ChromeStrokeBrush");
        return new BlockUIContainer(line) { Tag = Rule };
    }

    // ================================================================== FlowDocument → Markdown

    /// <summary>The Markdown of the whole document.</summary>
    public static string ToMarkdown(FlowDocument doc) => ToMarkdown(doc.Blocks, null, null);

    /// <summary>The Markdown of a selection: text clipped to it, embedded blocks and table rows taken whole.</summary>
    public static string ToMarkdown(FlowDocument doc, TextPointer start, TextPointer end) => ToMarkdown(doc.Blocks, start, end);

    private static string ToMarkdown(BlockCollection blocks, TextPointer? start, TextPointer? end)
    {
        var w = new Writer(start, end);
        w.Blocks(blocks, "", tight: false);
        return w.Result();
    }

    private sealed class Writer(TextPointer? start, TextPointer? end)
    {
        private readonly StringBuilder _sb = new();

        public string Result() => _sb.ToString().TrimEnd('\n') + (_sb.Length > 0 ? "\n" : "");

        private bool Outside(TextElement e) =>
            start is not null && end is not null && (e.ContentEnd.CompareTo(start) <= 0 || e.ContentStart.CompareTo(end) >= 0);

        public void Blocks(IEnumerable<WpfBlock> blocks, string indent, bool tight)
        {
            var first = true;
            foreach (var b in blocks)
            {
                if (Outside(b)) continue;
                var before = _sb.Length;
                if (!first && !tight) _sb.Append(indent.TrimEnd()).Append('\n');
                var mark = _sb.Length;
                Block(b, indent);
                if (_sb.Length == mark) _sb.Length = before; // wrote nothing (e.g. an empty paragraph in a selection)
                else first = false;
            }
        }

        private void Block(WpfBlock b, string indent)
        {
            switch (b)
            {
                case Paragraph p when p.Tag as string == Raw:
                    Lines(indent, new TextRange(p.ContentStart, p.ContentEnd).Text);
                    break;
                case Paragraph p:
                    var prefix = (p.Tag as string) switch { H1 => "# ", H2 => "## ", H3 => "### ", _ => "" };
                    var text = Inlines(p.Inlines, atLineStart: prefix.Length == 0, baseBold: IsBold(p.FontWeight));
                    if (text.Length == 0 && prefix.Length == 0) return;
                    Lines(indent, prefix + text);
                    break;
                case WpfList l:
                    var n = l.StartIndex;
                    foreach (var item in l.ListItems)
                    {
                        if (Outside(item)) { n++; continue; }
                        var marker = l.MarkerStyle == TextMarkerStyle.Decimal ? $"{n}. " : "- ";
                        n++;
                        var task = TaskBox(item);
                        if (task is not null) marker += task.IsChecked == true ? "[x] " : "[ ] ";
                        var childIndent = indent + new string(' ', l.MarkerStyle == TextMarkerStyle.Decimal ? marker.Length - (task is null ? 0 : 4) : 2);
                        var at = _sb.Length;
                        Blocks(item.Blocks, childIndent, tight: true);
                        // The first line gets the marker in place of the indent.
                        var firstLine = _sb.ToString(at, _sb.Length - at);
                        _sb.Length = at;
                        if (firstLine.StartsWith(childIndent, StringComparison.Ordinal)) firstLine = firstLine[childIndent.Length..];
                        _sb.Append(indent).Append(marker).Append(firstLine.Length == 0 ? "\n" : firstLine);
                    }
                    break;
                case Section s when s.Tag as string == Quote:
                    var at2 = _sb.Length;
                    Blocks(s.Blocks, "", tight: false);
                    var quoted = _sb.ToString(at2, _sb.Length - at2).TrimEnd('\n');
                    _sb.Length = at2;
                    foreach (var line in quoted.Split('\n')) _sb.Append(indent).Append(line.Length == 0 ? ">" : "> " + line).Append('\n');
                    break;
                case Section s:
                    Blocks(s.Blocks, indent, tight: false);
                    break;
                case BlockUIContainer { Child: INoteScriptBlock sb }:
                    Fence(indent, sb.LanguageId, sb.Code);
                    break;
                case BlockUIContainer { Child: INoteImage img }:
                    Lines(indent, ImageMarkdown(img));
                    break;
                case BlockUIContainer bc when bc.Tag as string == Rule:
                    Lines(indent, "---");
                    break;
                case WpfTable t:
                    TableBlock(t, indent);
                    break;
            }
        }

        private void Lines(string indent, string text)
        {
            foreach (var line in text.Replace("\r\n", "\n").Split('\n')) _sb.Append(line.Length == 0 ? "" : indent).Append(line).Append('\n');
        }

        private void Fence(string indent, string language, string code)
        {
            // The fence is longer than any run of backticks in the code, so the code can contain ``` itself.
            var longest = BacktickRuns().Matches(code).Select(m => m.Length).DefaultIfEmpty(0).Max();
            var fence = new string('`', Math.Max(3, longest + 1));
            var lang = language == ScriptLanguages.PlainId ? "" : language;
            _sb.Append(indent).Append(fence).Append(lang).Append('\n');
            if (code.Length > 0)
                foreach (var line in code.Replace("\r\n", "\n").Split('\n')) _sb.Append(line.Length == 0 ? "" : indent).Append(line).Append('\n');
            _sb.Append(indent).Append(fence).Append('\n');
        }

        private void TableBlock(WpfTable t, string indent)
        {
            var rows = t.RowGroups.SelectMany(g => g.Rows).ToList();
            if (rows.Count == 0) return;
            var cols = Math.Max(t.Columns.Count, rows.Max(r => r.Cells.Count));
            string Cell(System.Windows.Documents.TableRow r, int i) =>
                i < r.Cells.Count ? string.Join(" ", r.Cells[i].Blocks.OfType<Paragraph>().Select(p => Inlines(p.Inlines, atLineStart: false, baseBold: IsBold(p.FontWeight))))
                    .Replace("\n", " ").Replace("|", "\\|") : "";
            void Row(System.Windows.Documents.TableRow r) =>
                _sb.Append(indent).Append("| ").Append(string.Join(" | ", Enumerable.Range(0, cols).Select(i => Cell(r, i)))).Append(" |\n");
            Row(rows[0]);
            _sb.Append(indent).Append('|');
            for (var i = 0; i < cols; i++)
            {
                var a = i < t.Columns.Count ? t.Columns[i].Tag as string : "";
                _sb.Append(a switch { "center" => " :---: |", "right" => " ---: |", "left" => " :--- |", _ => " --- |" });
            }
            _sb.Append('\n');
            foreach (var r in rows.Skip(1)) if (!Outside(r)) Row(r);
        }

        private static CheckBox? TaskBox(ListItem item) =>
            item.Blocks.FirstBlock is Paragraph { Inlines.FirstInline: InlineUIContainer { Child: CheckBox cb } } ? cb : null;

        // ------------------------------------------------------------- inlines

        private readonly record struct Seg(string Text, bool Bold, bool Italic, bool Strike, bool Code, string? Link, string? Raw);

        private static bool IsBold(FontWeight w) => w.ToOpenTypeWeight() >= 600;

        /// <summary>Markdown of inlines; <paramref name="baseBold"/>: the block itself is bold (heading, header row), so bold isn't written.</summary>
        public string Inlines(InlineCollection inlines, bool atLineStart, bool baseBold = false)
        {
            var segs = new List<Seg>();
            _baseBold = baseBold;
            Flatten(inlines, segs, link: null);
            return Emit(segs, atLineStart);
        }

        private bool _baseBold;

        private void Flatten(InlineCollection inlines, List<Seg> segs, string? link)
        {
            foreach (var i in inlines)
            {
                if (Outside(i)) continue;
                switch (i)
                {
                    case Run r:
                        var text = r.Text;
                        if (start is not null && end is not null)
                        {
                            var from = r.ContentStart.CompareTo(start) < 0 ? r.ContentStart.GetOffsetToPosition(start) : 0;
                            var to = r.ContentEnd.CompareTo(end) > 0 ? text.Length - end.GetOffsetToPosition(r.ContentEnd) : text.Length;
                            text = text[Math.Clamp(from, 0, text.Length)..Math.Clamp(to, from, text.Length)];
                        }
                        if (text.Length == 0) break;
                        // Code: our tagged run, or text the editor gave the monospace font (formatting applied to a range).
                        var code = r.Tag as string == Code || (r.Parent as TextElement)?.Tag as string == Code || IsMono(r.FontFamily);
                        segs.Add(new Seg(text, !code && !_baseBold && IsBold(r.FontWeight), !code && r.FontStyle == FontStyles.Italic,
                            !code && HasStrike(r), code, link, null));
                        break;
                    case LineBreak:
                        segs.Add(new Seg("", false, false, false, false, null, "\\\n"));
                        break;
                    case Hyperlink h:
                        Flatten(h.Inlines, segs, h.Tag as string ?? h.NavigateUri?.OriginalString ?? "");
                        break;
                    case Span s:
                        Flatten(s.Inlines, segs, link);
                        break;
                    case InlineUIContainer { Child: INoteImage img }:
                        segs.Add(new Seg("", false, false, false, false, null, ImageMarkdown(img)));
                        break;
                    case InlineUIContainer { Child: CheckBox }:
                        break; // written as the list item's marker
                }
            }
        }

        private static bool HasStrike(TextElement e)
        {
            for (var x = e as DependencyObject; x is TextElement te; x = te.Parent)
                if ((x as WpfInline)?.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true) return true;
            return false;
        }

        private static string Emit(List<Seg> segs, bool atLineStart)
        {
            var sb = new StringBuilder();
            var i = 0;
            while (i < segs.Count)
            {
                var s = segs[i];
                if (s.Raw is not null) { sb.Append(s.Raw); i++; continue; }
                if (s.Link is not null)
                {
                    var j = i;
                    var inner = new List<Seg>();
                    while (j < segs.Count && segs[j].Link == s.Link && segs[j].Raw is null) inner.Add(segs[j++] with { Link = null });
                    sb.Append('[').Append(Emit(inner, false)).Append("](").Append(LinkTarget(s.Link)).Append(')');
                    i = j;
                    continue;
                }
                if (s.Code)
                {
                    var longest = BacktickRuns().Matches(s.Text).Select(m => m.Length).DefaultIfEmpty(0).Max();
                    var ticks = new string('`', longest + 1);
                    var pad = s.Text.StartsWith('`') || s.Text.EndsWith('`') ? " " : "";
                    sb.Append(ticks).Append(pad).Append(s.Text).Append(pad).Append(ticks);
                    i++;
                    continue;
                }
                // Merge neighbours with the same formatting, then put the markers inside the surrounding spaces.
                var text = new StringBuilder(s.Text);
                var k = i + 1;
                while (k < segs.Count && segs[k] is { Raw: null, Link: null, Code: false } n && n.Bold == s.Bold && n.Italic == s.Italic && n.Strike == s.Strike)
                    text.Append(segs[k++].Text);
                var raw = text.ToString();
                var core = raw.Trim(' ');
                var lead = raw[..(raw.Length - raw.TrimStart(' ').Length)];
                var trail = raw[(raw.TrimEnd(' ').Length)..];
                var escaped = Escape(core, atLineStart && sb.Length == 0 && lead.Length == 0);
                var open = (s.Strike ? "~~" : "") + (s.Bold ? "**" : "") + (s.Italic ? "*" : "");
                var close = (s.Italic ? "*" : "") + (s.Bold ? "**" : "") + (s.Strike ? "~~" : "");
                sb.Append(lead);
                if (core.Length > 0) sb.Append(open).Append(escaped).Append(close);
                sb.Append(trail);
                i = k;
            }
            return sb.ToString();
        }

        private static string LinkTarget(string url) =>
            url.Contains(' ') || url.Contains(')') || url.Contains('(') ? "<" + url.Replace("<", "%3C").Replace(">", "%3E") + ">" : url;
    }

    public static string ImageMarkdown(INoteImage img)
    {
        var alt = img.Alt.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]");
        var width = img.DisplayWidth is { } w ? "{width=" + Math.Round(w).ToString(CultureInfo.InvariantCulture) + "}" : "";
        return $"![{alt}]({Data.NoteStore.NoteImageScheme}{img.ImageId}){width}";
    }

    /// <summary>Escapes text so it reads back as the same text (Markdown characters, and line starts that would start a block).</summary>
    public static string Escape(string text, bool atLineStart)
    {
        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var prev = i > 0 ? text[i - 1] : ' ';
            var next = i + 1 < text.Length ? text[i + 1] : ' ';
            var needs = ch switch
            {
                '\\' or '`' or '*' or '[' or ']' or '<' or '|' or '~' or '{' => true,
                // Underscores inside words are text in CommonMark; only at a word edge they can start emphasis.
                '_' => !char.IsLetterOrDigit(prev) || !char.IsLetterOrDigit(next),
                '!' => next == '[',
                '&' => true,
                _ => false,
            };
            if (needs) sb.Append('\\');
            sb.Append(ch);
        }
        var s = sb.ToString();
        if (atLineStart && s.Length > 0)
        {
            if (s[0] is '#' or '>' or '-' or '+' or '=') s = "\\" + s;
            else if (OrderedStart().Match(s) is { Success: true } m) s = s.Insert(m.Length - 1, "\\");
        }
        return s;
    }

    [GeneratedRegex(@"^\d{1,9}[.)]")]
    private static partial Regex OrderedStart();

    [GeneratedRegex("`+")]
    private static partial Regex BacktickRuns();
}
