using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RemoteHub.Services;

/// <summary>
/// Pasted HTML → Markdown for the elements notes support (docs/notes-view-design.md §4.5.7): headings, paragraphs,
/// emphasis, code, lists, quotes, tables, links and lines. Fonts, colours, styles, scripts, forms and images are dropped
/// (an image leaves its alternative text); nothing is fetched. A small tolerant tokenizer – pasted HTML is often broken.
/// </summary>
public static partial class NoteHtml
{
    /// <summary>The HTML fragment from the clipboard's "HTML Format" (between the fragment markers when present).</summary>
    public static string Fragment(string clipboardHtml)
    {
        var start = clipboardHtml.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        var end = clipboardHtml.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start) return clipboardHtml[(start + "<!--StartFragment-->".Length)..end];
        var html = clipboardHtml.IndexOf('<');
        return html >= 0 ? clipboardHtml[html..] : clipboardHtml;
    }

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "head", "title", "noscript", "template", "svg", "iframe", "object", "embed", "canvas", "select", "textarea", "button", "math" };


    /// <summary>
    /// The clipboard's "HTML Format" for a fragment: the header with byte offsets that Word, Outlook and Teams expect.
    /// The fragment is HTML that Markdig wrote from a note (HTML in notes is never passed through, so it is safe).
    /// </summary>
    public static string ClipboardFormat(string fragment)
    {
        const string header = "Version:0.9\r\nStartHTML:{0:0000000000}\r\nEndHTML:{1:0000000000}\r\nStartFragment:{2:0000000000}\r\nEndFragment:{3:0000000000}\r\n";
        const string before = "<html><body><!--StartFragment-->";
        const string after = "<!--EndFragment--></body></html>";
        var headerLength = Encoding.UTF8.GetByteCount(string.Format(header, 0, 0, 0, 0));
        var startFragment = headerLength + Encoding.UTF8.GetByteCount(before);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(after);
        return string.Format(header, headerLength, endHtml, startFragment, endFragment) + before + fragment + after;
    }

    /// <summary>HTML for a note's Markdown (raw HTML in it stays escaped text).</summary>
    public static string FromMarkdown(string markdown) => Markdig.Markdown.ToHtml(markdown, NoteMarkdown.Pipeline);
    public static string ToMarkdown(string html)
    {
        var w = new Writer();
        foreach (var token in Tokens().Matches(html).Cast<Match>())
        {
            if (token.Groups["comment"].Success) continue;
            if (token.Groups["tag"].Success)
            {
                var name = token.Groups["name"].Value.ToLowerInvariant();
                var closing = token.Groups["close"].Success;
                if (Skipped.Contains(name)) { if (!token.Groups["self"].Success) w.Skip(name, closing); continue; }
                if (w.Skipping) continue;
                w.Tag(name, closing, token.Groups["attrs"].Value);
            }
            else if (!w.Skipping) w.Text(WebUtility.HtmlDecode(token.Value));
        }
        return w.Result();
    }

    private sealed class Writer
    {
        private readonly StringBuilder _out = new();
        private readonly StringBuilder _line = new();
        private readonly Stack<(bool Ordered, int Next)> _lists = new();
        private string? _skip;
        private int _skipDepth;
        private int _quote;
        private bool _pre;
        private string _preLang = "";
        private readonly StringBuilder _preText = new();
        private int _bold, _italic, _strike, _code;
        private string? _href;
        private int _linkStart = -1;
        private List<List<string>>? _table;
        private StringBuilder? _cell;
        private bool _headerRow;
        private int _headerRows;
        private string _prefix = "";

        public bool Skipping => _skip is not null;

        public void Skip(string name, bool closing)
        {
            if (_skip is null && !closing) { _skip = name; _skipDepth = 1; }
            else if (_skip == name) { _skipDepth += closing ? -1 : 1; if (_skipDepth == 0) _skip = null; }
        }

        private StringBuilder Target => _cell ?? _line;

        public void Text(string text)
        {
            if (_pre) { _preText.Append(text); return; }
            var t = Spaces().Replace(text, " ");
            if (t.Length == 0) return;
            if (Target.Length == 0 || Target[^1] == ' ' || Target[^1] == '\n') t = t.TrimStart();
            if (t.Length == 0) return;
            Target.Append(_code > 0 ? t : NoteMarkdown.Escape(t, atLineStart: false));
        }

        public void Tag(string name, bool closing, string attrs)
        {
            switch (name)
            {
                case "p" or "div" or "section" or "article" or "header" or "footer" or "main" or "figure" or "figcaption" or "dl" or "dt" or "dd" or "center":
                    if (_cell is not null) { if (closing && _cell.Length > 0) _cell.Append(' '); break; }
                    Flush();
                    break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    if (_cell is not null) break;
                    Flush();
                    if (!closing) _line.Append(new string('#', Math.Min(3, name[1] - '0'))).Append(' ');
                    break;
                case "br":
                    if (_pre) _preText.Append('\n');
                    else if (_cell is not null) _cell.Append(' ');
                    else _line.Append("\\\n");
                    break;
                case "hr":
                    if (_cell is not null) break;
                    Flush();
                    Emit("---");
                    break;
                case "b" or "strong": Mark(closing, ref _bold, "**"); break;
                case "i" or "em" or "cite" or "var": Mark(closing, ref _italic, "*"); break;
                case "s" or "strike" or "del": Mark(closing, ref _strike, "~~"); break;
                case "code" or "kbd" or "samp" or "tt":
                    if (_pre) break;
                    if (!closing) { _code++; if (_code == 1) Target.Append('`'); }
                    else if (_code > 0) { _code--; if (_code == 0) Target.Append('`'); }
                    break;
                case "pre":
                    if (!closing)
                    {
                        Flush();
                        _pre = true;
                        _preText.Clear();
                        _preLang = Lang().Match(attrs) is { Success: true } m ? ScriptLanguages.Normalize(m.Groups[1].Value) : "";
                    }
                    else if (_pre)
                    {
                        _pre = false;
                        var code = _preText.ToString().Replace("\r\n", "\n").Trim('\n');
                        var lang = _preLang.Length > 0 ? _preLang : ScriptLanguages.Guess(code) is var g && g != ScriptLanguages.PlainId ? g : "";
                        var longest = Ticks().Matches(code).Select(x => x.Length).DefaultIfEmpty(0).Max();
                        var fence = new string('`', Math.Max(3, longest + 1));
                        Emit(fence + lang + "\n" + code + "\n" + fence);
                    }
                    break;
                case "a":
                    if (!closing)
                    {
                        var href = Href().Match(attrs) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[2].Value).Trim() : "";
                        _href = href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                            || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? href : null;
                        if (_href is not null) { _linkStart = Target.Length; Target.Append('['); }
                    }
                    else if (_href is not null)
                    {
                        if (Target.Length == _linkStart + 1) Target.Append(NoteMarkdown.Escape(_href, false));
                        Target.Append("](").Append(_href.Replace(" ", "%20").Replace(")", "%29")).Append(')');
                        _href = null;
                    }
                    break;
                case "img":
                    if (Alt().Match(attrs) is { Success: true } alt && alt.Groups[2].Value.Trim().Length > 0)
                        Text(WebUtility.HtmlDecode(alt.Groups[2].Value));
                    break;
                case "ul" or "ol":
                    if (_cell is not null) break;
                    Flush();
                    if (!closing) _lists.Push((name == "ol", Start().Match(attrs) is { Success: true } s && int.TryParse(s.Groups[1].Value, out var n) ? n : 1));
                    else if (_lists.Count > 0) _lists.Pop();
                    break;
                case "li":
                    if (_cell is not null) { if (!closing && _cell.Length > 0) _cell.Append(", "); break; }
                    Flush();
                    if (!closing)
                    {
                        var depth = Math.Max(0, _lists.Count - 1);
                        var (ordered, next) = _lists.Count > 0 ? _lists.Pop() : (false, 1);
                        _prefix = new string(' ', depth * 2) + (ordered ? $"{next}. " : "- ");
                        if (_lists.Count >= 0 && (ordered || true)) _lists.Push((ordered, next + 1));
                        _line.Append(_prefix);
                    }
                    break;
                case "blockquote":
                    Flush();
                    _quote = Math.Max(0, _quote + (closing ? -1 : 1));
                    break;
                case "table":
                    if (!closing) { Flush(); _table = []; _headerRows = 0; }
                    else if (_table is not null) { EmitTable(); _table = null; }
                    break;
                case "tr":
                    if (_table is null) break;
                    if (!closing) { _table.Add([]); _headerRow = false; }
                    else if (_headerRow && _table.Count == _headerRows + 1) _headerRows++;
                    break;
                case "td" or "th":
                    if (_table is null) break;
                    if (!closing)
                    {
                        if (_table.Count == 0) _table.Add([]);
                        _cell = new StringBuilder();
                        if (name == "th") _headerRow = true;
                    }
                    else if (_cell is not null)
                    {
                        _table[^1].Add(_cell.ToString().Trim().Replace("|", "\\|"));
                        _cell = null;
                    }
                    break;
            }
        }

        private void Mark(bool closing, ref int depth, string marker)
        {
            if (_pre || _code > 0) return;
            if (!closing) { depth++; if (depth == 1) Target.Append(marker); }
            else if (depth > 0)
            {
                depth--;
                if (depth != 0) return;
                // An empty pair or one around spaces only would not read back as emphasis: drop it.
                var t = Target;
                var open = t.ToString().LastIndexOf(marker, StringComparison.Ordinal);
                if (open >= 0 && t.ToString(open + marker.Length, t.Length - open - marker.Length).Trim().Length == 0) t.Remove(open, marker.Length);
                else
                {
                    var trailing = 0;
                    while (t.Length > 0 && t[^1] == ' ') { t.Length--; trailing++; }
                    t.Append(marker).Append(' ', trailing);
                }
            }
        }

        private void Flush()
        {
            var line = _line.ToString().TrimEnd();
            _line.Clear();
            if (line.Length == 0 || line == _prefix.TrimEnd()) { _prefix = ""; return; }
            Emit(line, tight: _prefix.Length > 0);
            _prefix = "";
        }

        private void Emit(string block, bool tight = false)
        {
            if (_out.Length > 0) _out.Append(tight && _lists.Count > 0 ? "\n" : "\n\n");
            var q = string.Concat(Enumerable.Repeat("> ", _quote));
            foreach (var (l, i) in block.Split('\n').Select((l, i) => (l, i))) { if (i > 0) _out.Append('\n'); _out.Append(q).Append(l); }
        }

        private void EmitTable()
        {
            var rows = _table!.Where(r => r.Count > 0).ToList();
            if (rows.Count == 0) return;
            var cols = rows.Max(r => r.Count);
            string Row(List<string> r) => "| " + string.Join(" | ", Enumerable.Range(0, cols).Select(i => i < r.Count ? r[i] : "")) + " |";
            var sb = new StringBuilder();
            // Markdown tables need a header row: the first row is it (also when the HTML had none, e.g. from Excel).
            sb.Append(Row(rows[0])).Append('\n');
            sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", cols)));
            foreach (var r in rows.Skip(1)) sb.Append('\n').Append(Row(r));
            Emit(sb.ToString());
        }

        public string Result()
        {
            Flush();
            if (_table is not null) EmitTable();
            return _out.ToString().Trim('\n') + "\n";
        }
    }

    [GeneratedRegex(@"(?<comment><!--[\s\S]*?-->)|(?<tag><(?<close>/)?(?<name>[A-Za-z][A-Za-z0-9:-]*)(?<attrs>(?:\s+[^\s=>/]+(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+))?)*)\s*(?<self>/)?>)|[^<]+|<")]
    private static partial Regex Tokens();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex("`+")]
    private static partial Regex Ticks();

    [GeneratedRegex(@"\bhref\s*=\s*(""|')(.*?)\1", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    [GeneratedRegex(@"\balt\s*=\s*(""|')(.*?)\1", RegexOptions.IgnoreCase)]
    private static partial Regex Alt();

    [GeneratedRegex(@"\bstart\s*=\s*[""']?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Start();

    [GeneratedRegex(@"\bclass\s*=\s*[""'][^""']*\blang(?:uage)?-([\w#+\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Lang();
}
