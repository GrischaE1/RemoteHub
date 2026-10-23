using System.Management.Automation.Runspaces;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using RemoteHub.Data;
using RemoteHub.Remoting;
using RemoteHub.Services;

namespace RemoteHub.Views;

/// <summary>
/// The Scripts view's code editor (docs/scripts-view-design.md §5): AvalonEdit with the script blocks' colouring, line
/// numbers, folding, find (Ctrl+F) and go to line (Ctrl+G); for PowerShell, completion from PowerShell itself, syntax
/// errors underlined and command syntax on hover; for SQL, keywords. Completion parses the text and never runs it.
/// </summary>
public sealed partial class ScriptEditor : Border
{
    private static readonly TimeSpan CheckDelay = TimeSpan.FromSeconds(1);
    private readonly CodeEditor _editor = new();
    private readonly ErrorRenderer _errors;
    private readonly DispatcherTimer _checkTimer = new() { Interval = CheckDelay };
    private FoldingManager? _folding;
    private CompletionWindow? _completion;
    private string _language = ScriptStore.PowerShell;
    private bool _loading;
    private int _completionRequest;

    /// <summary>The text changed by typing (not by <see cref="SetText"/>).</summary>
    public event Action? TextChanged;
    /// <summary>The syntax errors after a check (PowerShell only; empty for SQL).</summary>
    public event Action<IReadOnlyList<ScriptSyntaxError>>? ErrorsChanged;
    /// <summary>A line looks like it holds a secret (§5.5): the hint to show, or null when none does.</summary>
    public event Action<string?>? SecretHintChanged;

    /// <summary>Where completion comes from: a connection's session (phase 4), or null for this PC's PowerShell.</summary>
    public Func<Runspace?>? CompletionRunspace { get; set; }

    public IReadOnlyList<ScriptSyntaxError> Errors { get; private set; } = [];

    public string Text => _editor.Text;

    public bool IsReadOnly
    {
        get => _editor.IsReadOnly;
        set => _editor.IsReadOnly = value;
    }

    /// <summary>The control that takes the focus and the keyboard.</summary>
    public TextEditor Editor => _editor;

    public ScriptEditor()
    {
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, "ChromeStrokeBrush");
        SetResourceReference(BackgroundProperty, "NoteCodeBackgroundBrush");
        _editor.ShowLineNumbers = true;
        _editor.FontFamily = NoteMarkdown.MonoFont;
        _editor.FontSize = 13;
        _editor.WordWrap = false;
        _editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _editor.Background = Brushes.Transparent;
        _editor.Padding = new Thickness(4, 4, 8, 4);
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.IndentationSize = 4;
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.HighlightCurrentLine = false;
        _editor.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        _editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "TextFillColorTertiaryBrush");
        _editor.TextArea.SelectionCornerRadius = 0;
        AutomationProperties.SetName(_editor, "Script");
        Child = _editor;

        SearchPanel.Install(_editor.TextArea);
        _errors = new ErrorRenderer(this);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_errors);
        _editor.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _checkTimer.Stop();
            _checkTimer.Start();
            TextChanged?.Invoke();
        };
        _checkTimer.Tick += (_, _) => { _checkTimer.Stop(); Check(); };
        _editor.TextArea.TextEntered += OnTextEntered;
        _editor.PreviewKeyDown += OnPreviewKeyDown;
        _editor.MouseHover += OnMouseHover;
        _editor.MouseHoverStopped += (_, _) => { if (_editor.ToolTip is ToolTip t) t.IsOpen = false; };

        ScriptHighlighting.Changed += OnPaletteChanged;
        Unloaded += (_, _) => ScriptHighlighting.Changed -= OnPaletteChanged;
        Loaded += (_, _) => { ScriptHighlighting.Changed -= OnPaletteChanged; ScriptHighlighting.Changed += OnPaletteChanged; };
    }

    /// <summary>Shows a script: its text and language. Not an edit: no <see cref="TextChanged"/>, and undo starts empty.</summary>
    public void SetText(string text, string language)
    {
        _loading = true;
        try
        {
            _language = language;
            _editor.Document = new TextDocument(text);
            _editor.SyntaxHighlighting = ScriptHighlighting.For(language == ScriptStore.PowerShell ? "powershell" : language);
            if (_folding is not null) FoldingManager.Uninstall(_folding);
            _folding = FoldingManager.Install(_editor.TextArea);
        }
        finally { _loading = false; }
        Check();
    }

    /// <summary>Changes the colouring for a new SQL dialect, keeping the text.</summary>
    public void SetLanguage(string language)
    {
        _language = language;
        _editor.SyntaxHighlighting = ScriptHighlighting.For(language == ScriptStore.PowerShell ? "powershell" : language);
    }

    public void FocusEditor() => _editor.TextArea.Focus();

    private void OnPaletteChanged() => Dispatcher.BeginInvoke(() => SetLanguage(_language));

    // ------------------------------------------------------------------ checks: errors, folding, secrets

    /// <summary>Runs the checks now (after typing stops, and when a script opens).</summary>
    public void Check()
    {
        var text = _editor.Text;
        Errors = _language == ScriptStore.PowerShell && text.Length > 0 ? PsCompletion.Errors(text) : [];
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        if (_folding is not null) _folding.UpdateFoldings(Foldings(text), -1);
        ErrorsChanged?.Invoke(Errors);
        SecretHintChanged?.Invoke(SecretHint(text));
    }

    /// <summary>Folds { } blocks spanning lines, here-strings left alone, and #region … #endregion.</summary>
    internal static IEnumerable<NewFolding> Foldings(string text)
    {
        var list = new List<NewFolding>();
        var open = new Stack<int>();
        var regions = new Stack<int>();
        var inString = '\0';
        var inComment = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inComment)
            {
                if (ch == '#' && i > 0 && text[i - 1] == '>') inComment = false;
                continue;
            }
            if (inString != '\0')
            {
                if (ch == inString && !(i > 0 && text[i - 1] == '`')) inString = '\0';
                else if (ch == '\n' && inString != '"' && inString != '\'') inString = '\0';
                continue;
            }
            switch (ch)
            {
                case '<' when i + 1 < text.Length && text[i + 1] == '#': inComment = true; i++; break;
                case '#':
                    var end = text.IndexOf('\n', i);
                    var line = text[i..(end < 0 ? text.Length : end)];
                    if (line.StartsWith("#region", StringComparison.OrdinalIgnoreCase)) regions.Push(i);
                    else if (line.StartsWith("#endregion", StringComparison.OrdinalIgnoreCase) && regions.Count > 0)
                        list.Add(new NewFolding(regions.Pop(), end < 0 ? text.Length : end) { Name = "…" });
                    i = end < 0 ? text.Length : end;
                    break;
                case '"' or '\'': inString = ch; break;
                case '{': open.Push(i); break;
                case '}' when open.Count > 0:
                    var start = open.Pop();
                    if (text.IndexOf('\n', start, i - start) >= 0) list.Add(new NewFolding(start, i + 1));
                    break;
            }
        }
        return list.OrderBy(f => f.StartOffset);
    }

    [GeneratedRegex(@"ConvertTo-SecureString\s+(['""])[^'""]{4,}\1\s+-AsPlainText|-(Password|Secret|ApiKey|Token)\s+(['""])[^'""]{4,}\3|\$\w*(password|passwd|pwd|secret|apikey|token)\w*\s*=\s*(['""])[^'""]{4,}\5|(key|secret|token)\w*\s*[=:]\s*(['""])[A-Za-z0-9+/=_\-]{24,}\7",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();

    /// <summary>The hint when a line looks like it holds a secret (§5.5), or null. A hint only: the script still saves.</summary>
    internal static string? SecretHint(string text)
    {
        var m = SecretPattern().Match(text);
        if (!m.Success) return null;
        var line = text.Take(m.Index).Count(ch => ch == '\n') + 1;
        return $"Line {line} looks like it holds a secret. Scripts must not contain secrets – use Windows LAPS, a gMSA or SecretManagement.";
    }

    /// <summary>Squiggles under syntax errors.</summary>
    private sealed class ErrorRenderer(ScriptEditor owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (owner.Errors.Count == 0 || !textView.VisualLinesValid) return;
            var brush = owner.TryFindResource("ScriptErrorBrush") as Brush ?? Brushes.Red;
            var pen = new Pen(brush, 1);
            pen.Freeze();
            var length = textView.Document.TextLength;
            foreach (var e in owner.Errors)
            {
                var start = Math.Clamp(e.StartOffset, 0, length);
                var end = Math.Clamp(Math.Max(e.EndOffset, start + 1), 0, length);
                if (end <= start && start > 0) start--;
                var segment = new TextSegment { StartOffset = start, EndOffset = Math.Max(end, start) };
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                {
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        var y = rect.Bottom - 1;
                        var width = Math.Max(rect.Width, 6);
                        ctx.BeginFigure(new Point(rect.Left, y), false, false);
                        for (var x = 0.0; x < width; x += 3) ctx.LineTo(new Point(rect.Left + x + 1.5, y + ((int)(x / 3) % 2 == 0 ? 2 : 0)), true, false);
                    }
                    g.Freeze();
                    dc.DrawGeometry(null, pen, g);
                }
            }
        }
    }

    // ------------------------------------------------------------------ keys

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (ctrl && e.Key == Key.Space) { e.Handled = true; _ = ShowCompletion(explicitly: true); }
        else if (ctrl && e.Key == Key.G) { e.Handled = true; GoToLine(); }
    }

    private void GoToLine()
    {
        var dlg = new InputDialog("Go to line", $"Line number (1–{_editor.Document.LineCount})", _editor.TextArea.Caret.Line.ToString()) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || !int.TryParse(dlg.Value.Trim(), out var n)) return;
        n = Math.Clamp(n, 1, _editor.Document.LineCount);
        _editor.TextArea.Caret.Line = n;
        _editor.TextArea.Caret.Column = 1;
        _editor.ScrollToLine(n);
        FocusEditor();
    }

    // ------------------------------------------------------------------ completion

    private void OnTextEntered(object sender, TextCompositionEventArgs e)
    {
        if (_completion is not null || IsReadOnly || e.Text.Length != 1) return;
        var ch = e.Text[0];
        if (_language == ScriptStore.PowerShell)
        {
            // As in the PowerShell console: after a parameter dash, a variable, a member dot or a static ::.
            if (ch is '-' or '$' or '.' or ':' || (char.IsLetter(ch) && WordBeforeCaret().Length == 3)) _ = ShowCompletion(explicitly: false);
        }
        else if (char.IsLetter(ch) && WordBeforeCaret().Length == 3) _ = ShowCompletion(explicitly: false);
    }

    private string WordBeforeCaret()
    {
        var offset = _editor.CaretOffset;
        var start = offset;
        while (start > 0 && (char.IsLetterOrDigit(_editor.Document.GetCharAt(start - 1)) || _editor.Document.GetCharAt(start - 1) is '_' or '-')) start--;
        return _editor.Document.GetText(start, offset - start);
    }

    private async Task ShowCompletion(bool explicitly)
    {
        var request = ++_completionRequest;
        var text = _editor.Text;
        var caret = _editor.CaretOffset;
        CompletionSet? set;
        if (_language == ScriptStore.PowerShell)
        {
            var remote = CompletionRunspace?.Invoke();
            set = await Task.Run(() => PsCompletion.Complete(text, caret, remote));
        }
        else set = SqlKeywords.Complete(text, caret);
        // Typing went on, or another request started: only the newest one shows.
        if (request != _completionRequest || set is null || set.Matches.Count == 0) return;
        if (!explicitly && _editor.CaretOffset < caret) return;
        if (set.ReplacementIndex < 0 || set.ReplacementIndex > _editor.Document.TextLength) return;
        _completion?.Close();
        var window = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = set.ReplacementIndex,
            EndOffset = Math.Min(_editor.CaretOffset, _editor.Document.TextLength),
            MinWidth = 280,
            CloseWhenCaretAtBeginning = true,
        };
        window.SetResourceReference(BackgroundProperty, "SessionHeaderBrush");
        window.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        window.SetResourceReference(BorderBrushProperty, "ChromeStrokeBrush");
        window.CompletionList.ListBox.SetResourceReference(BackgroundProperty, "SessionHeaderBrush");
        window.CompletionList.ListBox.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        window.CompletionList.ListBox.FontFamily = NoteMarkdown.MonoFont;
        foreach (var m in set.Matches.Take(500)) window.CompletionList.CompletionData.Add(new Item(m));
        var typed = _editor.Document.GetText(window.StartOffset, Math.Max(0, window.EndOffset - window.StartOffset));
        if (typed.Length > 0) window.CompletionList.SelectItem(typed.TrimStart('$', '-'));
        window.Closed += (_, _) => { if (_completion == window) _completion = null; };
        _completion = window;
        window.Show();
    }

    private sealed class Item(CompletionMatch match) : ICompletionData
    {
        public System.Windows.Media.ImageSource? Image => null;
        public string Text => match.ListItemText;
        public object Content => match.ListItemText;
        public object Description => string.IsNullOrWhiteSpace(match.ToolTip) ? match.ResultType : match.ToolTip;
        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, match.CompletionText);
    }

    // ------------------------------------------------------------------ syntax on hover

    private async void OnMouseHover(object sender, MouseEventArgs e)
    {
        if (_language != ScriptStore.PowerShell) return;
        var pos = _editor.GetPositionFromPoint(e.GetPosition(_editor));
        if (pos is null) return;
        var offset = _editor.Document.GetOffset(pos.Value.Location);
        var doc = _editor.Document;
        int start = offset, end = offset;
        while (start > 0 && IsCommandChar(doc.GetCharAt(start - 1))) start--;
        while (end < doc.TextLength && IsCommandChar(doc.GetCharAt(end))) end++;
        var word = doc.GetText(start, end - start);
        if (!word.Contains('-') || word.StartsWith('-')) return; // a cmdlet name has a dash inside, a parameter starts with one
        var syntax = await Task.Run(() => PsCompletion.Syntax(word));
        if (syntax is null || !_editor.IsMouseOver) return;
        var tip = new ToolTip { Content = new TextBlock { Text = syntax, FontFamily = NoteMarkdown.MonoFont, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 700 }, PlacementTarget = _editor };
        _editor.ToolTip = tip;
        tip.IsOpen = true;
    }

    private static bool IsCommandChar(char ch) => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.';
}

/// <summary>Keywords and functions completed in SQL scripts (no schema: they aren't connected to a database, §5.4).</summary>
internal static class SqlKeywords
{
    private static readonly string[] Words =
    [
        "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING", "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE", "MERGE",
        "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL OUTER JOIN", "CROSS APPLY", "OUTER APPLY", "ON", "AS", "AND", "OR", "NOT",
        "IN", "EXISTS", "BETWEEN", "LIKE", "IS NULL", "IS NOT NULL", "DISTINCT", "TOP", "UNION", "UNION ALL", "EXCEPT", "INTERSECT",
        "CASE", "WHEN", "THEN", "ELSE", "END", "WITH", "OVER", "PARTITION BY", "ROW_NUMBER()", "RANK()", "DENSE_RANK()",
        "COUNT(*)", "SUM()", "AVG()", "MIN()", "MAX()", "COALESCE()", "ISNULL()", "NULLIF()", "CAST()", "CONVERT()", "TRY_CONVERT()",
        "GETDATE()", "GETUTCDATE()", "SYSDATETIME()", "DATEADD()", "DATEDIFF()", "FORMAT()", "LEN()", "SUBSTRING()", "REPLACE()",
        "UPPER()", "LOWER()", "LTRIM()", "RTRIM()", "TRIM()", "STRING_AGG()", "CONCAT()",
        "CREATE TABLE", "ALTER TABLE", "DROP TABLE", "CREATE INDEX", "CREATE VIEW", "CREATE PROCEDURE", "CREATE FUNCTION",
        "PRIMARY KEY", "FOREIGN KEY", "REFERENCES", "DEFAULT", "NOT NULL", "IDENTITY", "NVARCHAR", "VARCHAR", "INT", "BIGINT",
        "BIT", "DATETIME2", "DECIMAL", "UNIQUEIDENTIFIER", "BEGIN", "BEGIN TRAN", "COMMIT", "ROLLBACK", "DECLARE", "EXEC", "GO",
        "TRUNCATE TABLE", "WITH (NOLOCK)", "OUTPUT", "RETURN", "PRINT", "RAISERROR", "THROW", "TRY", "CATCH", "IF", "WHILE",
    ];

    public static CompletionSet? Complete(string text, int caret)
    {
        var start = caret;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        var word = text[start..caret];
        var matches = Words.Where(w => word.Length == 0 || w.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            .Select(w => new CompletionMatch(char.IsLower(word.FirstOrDefault()) ? w.ToLowerInvariant() : w, w, "Keyword", w)).ToList();
        return matches.Count == 0 ? null : new CompletionSet(start, caret - start, matches);
    }
}
