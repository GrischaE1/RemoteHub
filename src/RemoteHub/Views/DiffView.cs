using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using RemoteHub.Data;
using RemoteHub.Services;

namespace RemoteHub.Views;

/// <summary>
/// Two versions of a script compared like a Git diff (docs/scripts-view-design.md §4.10): side by side or inline,
/// removed lines on the left, added lines on the right, changed words marked inside a changed line, unchanged stretches
/// longer than <see cref="Context"/> × 2 lines folded to "⋯ N unchanged lines" (a click opens them). Both sides keep
/// the syntax colouring and scroll together. The marks come from theme brushes and a + / − in the gutter, so they don't
/// depend on colour alone. Read-only.
/// </summary>
public sealed class DiffView : Grid
{
    /// <summary>Unchanged lines shown around a change; longer unchanged stretches fold.</summary>
    public const int Context = 3;

    private readonly TextEditor _left = Side("Older version");
    private readonly TextEditor _right = Side("Newer version");
    private readonly ColumnDefinition _rightColumn = new() { Width = new GridLength(1, GridUnitType.Star) };
    private readonly HashSet<int> _opened = [];
    private IReadOnlyList<DiffRow> _rows = [];
    private List<Line> _lines = [];
    private string _language = ScriptStore.PowerShell;
    private bool _inline;
    private bool _syncing;

    /// <summary>Where each change starts, as a line of the shown text (for ↑ ↓).</summary>
    private List<int> _changeLines = [];

    public int ChangeCount { get; private set; }

    /// <summary>The number of changes, after <see cref="Show"/> or a fold opened.</summary>
    public event Action? Shown;

    public DiffView()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(_rightColumn);
        _left.Margin = new Thickness(0, 0, 3, 0);
        _right.Margin = new Thickness(3, 0, 0, 0);
        SetColumn(_right, 1);
        Children.Add(_left);
        Children.Add(_right);
        foreach (var e in new[] { _left, _right })
        {
            e.TextArea.TextView.BackgroundRenderers.Add(new Marks(this, e == _left));
            e.TextArea.LeftMargins.Insert(0, new Gutter(this, e == _left));
            e.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(e);
            e.PreviewMouseLeftButtonUp += (_, args) => OpenFoldAt(e, args);
        }
        ScriptHighlighting.Changed += Restyle;
        Unloaded += (_, _) => ScriptHighlighting.Changed -= Restyle;
        Loaded += (_, _) => { ScriptHighlighting.Changed -= Restyle; ScriptHighlighting.Changed += Restyle; };
    }

    private static TextEditor Side(string name)
    {
        var e = new TextEditor
        {
            IsReadOnly = true, ShowLineNumbers = false, FontFamily = NoteMarkdown.MonoFont, FontSize = 13, WordWrap = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent, Padding = new Thickness(2, 2, 8, 2),
        };
        e.Options.EnableHyperlinks = false;
        e.Options.EnableEmailHyperlinks = false;
        e.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        e.TextArea.SelectionCornerRadius = 0;
        AutomationProperties.SetName(e, name);
        return e;
    }

    public bool Inline
    {
        get => _inline;
        set { _inline = value; Render(); }
    }

    public string LeftText { get; private set; } = "";
    public string RightText { get; private set; } = "";

    /// <summary>Compares two texts. Runs the diff on a background thread; the view fills when it is done.</summary>
    public async Task Show(string older, string newer, string language)
    {
        LeftText = older;
        RightText = newer;
        _language = language;
        _opened.Clear();
        _rows = await Task.Run(() => TextDiff.Compare(older, newer));
        ChangeCount = TextDiff.CountChanges(_rows);
        Restyle();
        Render();
    }

    private void Restyle()
    {
        var def = ScriptHighlighting.For(_language == ScriptStore.PowerShell ? "powershell" : _language);
        _left.SyntaxHighlighting = def;
        _right.SyntaxHighlighting = def;
    }

    // ------------------------------------------------------------------ layout of the shown text

    /// <summary>One shown line: a diff row, a filler (the other side has a line here), or a fold of unchanged rows.</summary>
    private sealed record Line(DiffRow? Row, bool Filler, int FoldStart = -1, int FoldCount = 0)
    {
        public bool IsFold => FoldStart >= 0;
    }

    private void Render()
    {
        _lines = Layout();
        var left = new System.Text.StringBuilder();
        var right = new System.Text.StringBuilder();
        foreach (var l in _lines)
        {
            if (l.IsFold)
            {
                var fold = $"⋯ {l.FoldCount} unchanged lines";
                left.AppendLine(fold);
                right.AppendLine(fold);
                continue;
            }
            var r = l.Row!;
            if (_inline)
            {
                // One column: the old line, then the new one (a change shows both).
                left.AppendLine(l.Filler ? r.NewText : r.Kind == DiffKind.Added ? r.NewText : r.OldText);
                continue;
            }
            left.AppendLine(r.Kind == DiffKind.Added ? "" : r.OldText);
            right.AppendLine(r.Kind == DiffKind.Removed ? "" : r.NewText);
        }
        _left.Text = TrimEnd(left);
        _right.Text = _inline ? "" : TrimEnd(right);
        _rightColumn.Width = _inline ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _right.Visibility = _inline ? Visibility.Collapsed : Visibility.Visible;
        _changeLines = [];
        for (var i = 0; i < _lines.Count; i++)
            if (IsChange(_lines[i]) && (i == 0 || !IsChange(_lines[i - 1]))) _changeLines.Add(i + 1);
        foreach (var e in new[] { _left, _right }) e.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        foreach (var m in _left.TextArea.LeftMargins.Concat(_right.TextArea.LeftMargins).OfType<Gutter>()) m.Refresh();
        Shown?.Invoke();
    }

    private static string TrimEnd(System.Text.StringBuilder sb) => sb.ToString().TrimEnd('\r', '\n');

    private static bool IsChange(Line l) => !l.IsFold && l.Row is { IsChange: true };

    private List<Line> Layout()
    {
        var lines = new List<Line>();
        for (var i = 0; i < _rows.Count;)
        {
            if (_rows[i].IsChange)
            {
                var r = _rows[i++];
                lines.Add(new Line(r, false));
                // Inline: a changed line shows as the old line and the new line.
                if (_inline && r.Kind == DiffKind.Changed) lines.Add(new Line(r, true));
                continue;
            }
            var start = i;
            while (i < _rows.Count && !_rows[i].IsChange) i++;
            var count = i - start;
            var head = start == 0 ? 0 : Context;           // context after the change before
            var tail = i == _rows.Count ? 0 : Context;     // context before the next change
            if (count > head + tail + 1 && !_opened.Contains(start))
            {
                for (var k = start; k < start + head; k++) lines.Add(new Line(_rows[k], false));
                lines.Add(new Line(null, false, start, count - head - tail));
                for (var k = i - tail; k < i; k++) lines.Add(new Line(_rows[k], false));
            }
            else for (var k = start; k < i; k++) lines.Add(new Line(_rows[k], false));
        }
        return lines;
    }

    private void OpenFoldAt(TextEditor e, MouseButtonEventArgs args)
    {
        var pos = e.GetPositionFromPoint(args.GetPosition(e));
        if (pos is null || pos.Value.Line - 1 >= _lines.Count) return;
        var line = _lines[pos.Value.Line - 1];
        if (!line.IsFold) return;
        _opened.Add(line.FoldStart);
        Render();
        args.Handled = true;
    }

    // ------------------------------------------------------------------ moving between changes

    /// <summary>Scrolls to the next (+1) or previous (-1) change from the caret.</summary>
    public void GoToChange(int direction)
    {
        if (_changeLines.Count == 0) return;
        var current = _left.TextArea.Caret.Line;
        var target = direction > 0 ? _changeLines.FirstOrDefault(l => l > current, _changeLines[0]) : _changeLines.LastOrDefault(l => l < current, _changeLines[^1]);
        foreach (var e in new[] { _left, _right })
        {
            if (target > e.Document.LineCount) continue;
            e.TextArea.Caret.Line = target;
            e.ScrollToLine(target);
        }
    }

    private void Sync(TextEditor from)
    {
        if (_syncing || _inline) return;
        _syncing = true;
        try
        {
            var other = from == _left ? _right : _left;
            other.ScrollToVerticalOffset(from.VerticalOffset);
            other.ScrollToHorizontalOffset(from.HorizontalOffset);
        }
        finally { _syncing = false; }
    }

    // ------------------------------------------------------------------ marks: line and word backgrounds

    private (DiffKind? Kind, bool Old) KindAt(bool left, int lineNumber)
    {
        if (lineNumber - 1 >= _lines.Count) return (null, false);
        var l = _lines[lineNumber - 1];
        if (l.IsFold || l.Row is not { } r || r.Kind == DiffKind.Same) return (null, false);
        if (_inline)
            return r.Kind switch
            {
                DiffKind.Removed => (DiffKind.Removed, true),
                DiffKind.Added => (DiffKind.Added, false),
                _ => l.Filler ? (DiffKind.Added, false) : (DiffKind.Removed, true),
            };
        return left
            ? r.Kind == DiffKind.Added ? (DiffKind.Same, false) : (DiffKind.Removed, true)
            : r.Kind == DiffKind.Removed ? (DiffKind.Same, false) : (DiffKind.Added, false);
    }

    private sealed class Marks(DiffView owner, bool left) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (!textView.VisualLinesValid) return;
            Brush B(string key) => owner.TryFindResource(key) as Brush ?? Brushes.Transparent;
            var fold = owner.TryFindResource("SubtleFillColorSecondaryBrush") as Brush;
            foreach (var vl in textView.VisualLines)
            {
                var n = vl.FirstDocumentLine.LineNumber;
                var top = vl.VisualTop - textView.ScrollOffset.Y;
                var rect = new Rect(0, top, Math.Max(textView.ActualWidth, textView.ScrollOffset.X + textView.ActualWidth), vl.Height);
                if (n - 1 < owner._lines.Count && owner._lines[n - 1].IsFold) { if (fold is not null) dc.DrawRectangle(fold, null, rect); continue; }
                var (kind, old) = owner.KindAt(left, n);
                if (kind is null) continue;
                if (kind == DiffKind.Same) { dc.DrawRectangle(fold, null, rect); continue; } // filler: nothing on this side
                dc.DrawRectangle(B(kind == DiffKind.Added ? "DiffAddedBrush" : "DiffRemovedBrush"), null, rect);
                var row = owner._lines[n - 1].Row!;
                if (row.Kind != DiffKind.Changed) continue;
                var strong = B(kind == DiffKind.Added ? "DiffAddedStrongBrush" : "DiffRemovedStrongBrush");
                foreach (var span in old ? row.OldSpans : row.NewSpans)
                {
                    var line = vl.FirstDocumentLine;
                    var start = Math.Min(line.Offset + span.Start, line.EndOffset);
                    var end = Math.Min(start + span.Length, line.EndOffset);
                    if (end <= start) continue;
                    var seg = new ICSharpCode.AvalonEdit.Document.TextSegment { StartOffset = start, EndOffset = end };
                    foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg)) dc.DrawRectangle(strong, null, r);
                }
            }
        }
    }

    /// <summary>The line numbers of the version a line comes from (blank for fillers and folds), and + or −.</summary>
    private sealed class Gutter(DiffView owner, bool left) : AbstractMargin
    {
        private const double GutterWidth = 64;

        public void Refresh() => InvalidateVisual();

        protected override Size MeasureOverride(Size availableSize) => new(GutterWidth, 0);

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null) oldTextView.VisualLinesChanged -= Changed;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null) newTextView.VisualLinesChanged += Changed;
        }

        private void Changed(object? sender, EventArgs e) => InvalidateVisual();

        protected override void OnRender(DrawingContext dc)
        {
            var view = TextView;
            if (view is null || !view.VisualLinesValid) return;
            var fg = TryFindResource("TextFillColorTertiaryBrush") as Brush ?? Brushes.Gray;
            var typeface = new Typeface(NoteMarkdown.MonoFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            foreach (var vl in view.VisualLines)
            {
                var n = vl.FirstDocumentLine.LineNumber;
                if (n - 1 >= owner._lines.Count) continue;
                var l = owner._lines[n - 1];
                if (l.IsFold || l.Row is not { } r) continue;
                var (kind, old) = owner.KindAt(left, n);
                if (kind == DiffKind.Same) continue; // a filler on this side
                int? number = owner._inline ? (old ? r.OldNumber : r.NewNumber) : left ? r.OldNumber : r.NewNumber;
                var sign = kind == DiffKind.Added ? "+" : kind == DiffKind.Removed ? "−" : " ";
                var text = new FormattedText($"{number?.ToString(CultureInfo.InvariantCulture) ?? "",5} {sign}", CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, 12, fg, dpi);
                dc.DrawText(text, new Point(2, vl.VisualTop - view.ScrollOffset.Y + (vl.Height - text.Height) / 2));
            }
        }
    }
}
