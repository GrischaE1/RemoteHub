using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;
using RichTextBox = System.Windows.Controls.RichTextBox;
using TableCell = System.Windows.Documents.TableCell;
using TableRow = System.Windows.Documents.TableRow;

namespace RemoteHub.Views;

/// <summary>
/// The WYSIWYG page editor of the Notes view (docs/notes-view-design.md §4.5): formatted text, tables, images and script
/// blocks in a RichTextBox, stored as Markdown through <see cref="NoteMarkdown"/>. Paste only ever brings in the
/// supported elements; the clipboard's XAML is never loaded.
/// </summary>
public sealed partial class NoteEditor : DockPanel, INoteElementFactory
{
    /// <summary>Our own clipboard format: the Markdown of what was copied (keeps script blocks, tables and images).</summary>
    public const string MarkdownFormat = "RemoteHub.NoteMarkdown";

    private readonly RichTextBox _box = new()
    {
        AcceptsTab = true, AcceptsReturn = true, IsDocumentEnabled = true, BorderThickness = new Thickness(0),
        Background = Brushes.Transparent, Padding = new Thickness(0, 4, 8, 24),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly WrapPanel _bar = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly List<NoteImageView> _images = [];
    private Dictionary<string, Data.NoteImage> _imageData = [];
    private bool _loading;

    /// <summary>Stores a prepared image for the open page and returns its id (null: refused, the reason was shown).</summary>
    public Func<PreparedImage, string?>? StoreImage { get; set; }
    /// <summary>Ctrl+click on a <c>remotehub://note/</c> link (argument: the page id).</summary>
    public event Action<string>? NoteLinkRequested;
    /// <summary>The content changed (typing, formatting, a script block or an image).</summary>
    public event Action? Changed;
    /// <summary>A message for the user (paste or image refused); shown by the view.</summary>
    public event Action<string>? Message;

    public NoteEditor()
    {
        LastChildFill = true;
        DockPanel.SetDock(_bar, Dock.Top);
        Children.Add(_bar);
        Children.Add(_box);
        _box.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        _box.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextFillColorPrimaryBrush");
        AutomationProperties.SetName(_box, "Page");
        BuildBar();

        _box.TextChanged += (_, _) => { if (!_loading) Changed?.Invoke(); };
        _box.PreviewKeyDown += Box_PreviewKeyDown;
        _box.PreviewTextInput += Box_PreviewTextInput;
        _box.SizeChanged += (_, _) => UpdateImageWidths();
        _box.ContextMenuOpening += Box_ContextMenuOpening;
        _box.ContextMenu = new ContextMenu();
        _box.AddHandler(Hyperlink.ClickEvent, new RoutedEventHandler(Link_Click));
        _box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // Links open with Ctrl+click only, so clicking into a link to edit it doesn't open it.
            if (e.OriginalSource is FrameworkContentElement { Parent: Hyperlink } or Hyperlink && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                if (FindHyperlink(e.OriginalSource as DependencyObject) is { } h) { OpenLink(h); e.Handled = true; }
        };
        DataObject.AddPastingHandler(_box, OnPaste);
        DataObject.AddCopyingHandler(_box, OnCopy);
        _box.AllowDrop = true;
        _box.PreviewDragOver += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        _box.PreviewDrop += Box_PreviewDrop;

        // Formats Markdown can't keep are switched off: underline, alignment, font size, sub/superscript.
        foreach (var (key, mods) in new[] { (Key.U, ModifierKeys.Control), (Key.E, ModifierKeys.Control), (Key.L, ModifierKeys.Control), (Key.R, ModifierKeys.Control),
                     (Key.J, ModifierKeys.Control), (Key.OemOpenBrackets, ModifierKeys.Control), (Key.OemCloseBrackets, ModifierKeys.Control),
                     (Key.OemPlus, ModifierKeys.Control), (Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift), (Key.D1, ModifierKeys.Control),
                     (Key.D2, ModifierKeys.Control), (Key.D5, ModifierKeys.Control) })
            _box.InputBindings.Add(new KeyBinding(ApplicationCommands.NotACommand, key, mods));
    }

    public FlowDocument Document => _box.Document;

    // ================================================================== loading and saving

    /// <summary>Shows a page. The undo history starts empty.</summary>
    public void Load(string markdown, IEnumerable<Data.NoteImage> images)
    {
        _loading = true;
        try
        {
            _imageData = images.ToDictionary(i => i.Id);
            _images.Clear();
            _box.IsUndoEnabled = false;
            _box.Document = NoteMarkdown.ToFlowDocument(markdown, this);
            StyleDocument(_box.Document);
            _box.IsUndoEnabled = true;
            UpdateImageWidths();
        }
        finally { _loading = false; }
    }

    /// <summary>Empties the editor (locked, or no page open): the page's text leaves the visual tree.</summary>
    public void Clear()
    {
        _loading = true;
        _imageData = [];
        _images.Clear();
        _box.IsUndoEnabled = false;
        _box.Document = new FlowDocument(new Paragraph());
        StyleDocument(_box.Document);
        _box.IsUndoEnabled = true;
        _loading = false;
    }

    public string Markdown() => NoteMarkdown.ToMarkdown(_box.Document);

    public bool IsReadOnly
    {
        get => _box.IsReadOnly;
        set { _box.IsReadOnly = value; _bar.IsEnabled = !value; }
    }

    /// <summary>Undoes the last change (used when a change made the page too large).</summary>
    public void UndoLast() { if (_box.CanUndo) _box.Undo(); }

    public new void Focus() => _box.Focus();

    public void FocusStart()
    {
        _box.Focus();
        _box.CaretPosition = _box.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
    }

    private static void StyleDocument(FlowDocument d)
    {
        d.FontSize = NoteMarkdown.BodySize;
        d.PagePadding = new Thickness(0);
        d.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");
        d.FontFamily = SystemFonts.MessageFontFamily;
        d.LineHeight = double.NaN;
    }

    /// <summary>Selects the first match of a search word, in text or in a script block.</summary>
    public void Highlight(string? words)
    {
        var word = (words ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (word is null) return;
        for (var p = _box.Document.ContentStart; p is not null && p.CompareTo(_box.Document.ContentEnd) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.EmbeddedElement && p.GetAdjacentElement(LogicalDirection.Forward) is ScriptBlock sb && sb.Highlight(word)) { sb.BringIntoView(); return; }
            if (p.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) continue;
            var run = p.GetTextInRun(LogicalDirection.Forward);
            var at = run.IndexOf(word, StringComparison.CurrentCultureIgnoreCase);
            if (at < 0) continue;
            var start = p.GetPositionAtOffset(at)!;
            _box.Selection.Select(start, start.GetPositionAtOffset(word.Length)!);
            start.Paragraph?.BringIntoView();
            _box.Focus();
            return;
        }
    }

    // ================================================================== embedded elements (INoteElementFactory)

    public FrameworkElement ScriptBlock(string language, string code)
    {
        var sb = new ScriptBlock(language, code);
        sb.Changed += () => { if (!_loading) Changed?.Invoke(); };
        sb.LeaveRequested += LeaveScript;
        sb.DeleteRequested += b => RemoveEmbedded(b);
        return sb;
    }

    public FrameworkElement Image(string imageId, string alt, double? width)
    {
        var view = new NoteImageView(imageId, alt, width, _imageData.TryGetValue(imageId, out var d) ? d.Data : null);
        view.Changed += () => Changed?.Invoke();
        view.DeleteRequested += v => RemoveEmbedded(v);
        _images.Add(view);
        return view;
    }

    private void UpdateImageWidths()
    {
        var w = Math.Max(100, _box.ActualWidth - _box.Padding.Left - _box.Padding.Right - 30);
        foreach (var i in _images) i.PageWidth = w;
    }

    private void RemoveEmbedded(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case BlockUIContainer b:
                var next = b.NextBlock ?? b.PreviousBlock;
                if (b.Parent is FlowDocument or Section or ListItem or TableCell) b.SiblingBlocks?.Remove(b);
                if (_box.Document.Blocks.Count == 0) _box.Document.Blocks.Add(new Paragraph());
                _box.Focus();
                _box.CaretPosition = (next ?? _box.Document.Blocks.FirstBlock).ContentStart.GetInsertionPosition(LogicalDirection.Forward);
                break;
            case InlineUIContainer i:
                (i.Parent as Paragraph)?.Inlines.Remove(i);
                (i.Parent as Span)?.Inlines.Remove(i);
                break;
        }
        if (element is NoteImageView v) _images.Remove(v);
        Changed?.Invoke();
    }

    private void LeaveScript(ScriptBlock sb, int direction, bool newParagraph)
    {
        if (sb.Parent is not BlockUIContainer container || container.SiblingBlocks is not { } siblings) return;
        Block? target = direction < 0 ? container.PreviousBlock : newParagraph ? null : container.NextBlock;
        if (target is null || target is BlockUIContainer)
        {
            var p = new Paragraph();
            if (direction < 0) siblings.InsertBefore(container, p);
            else siblings.InsertAfter(container, p);
            target = p;
        }
        _box.Focus();
        _box.CaretPosition = direction < 0 ? target.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)
                                           : target.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
    }

    // ================================================================== format bar

    private void BuildBar()
    {
        var text = BarButton(SymbolRegular.TextHeader124, "Text style");
        text.Click += (_, _) =>
        {
            var m = new ContextMenu { PlacementTarget = text, Placement = PlacementMode.Bottom };
            m.Items.Add(RemoteToolChrome.Item("Heading 1", SymbolRegular.TextHeader124, () => SetHeading(1), gesture: "Ctrl+Alt+1"));
            m.Items.Add(RemoteToolChrome.Item("Heading 2", SymbolRegular.TextHeader224, () => SetHeading(2), gesture: "Ctrl+Alt+2"));
            m.Items.Add(RemoteToolChrome.Item("Heading 3", SymbolRegular.TextHeader324, () => SetHeading(3), gesture: "Ctrl+Alt+3"));
            m.Items.Add(RemoteToolChrome.Item("Normal text", SymbolRegular.TextT24, () => SetHeading(0), gesture: "Ctrl+Alt+0"));
            m.IsOpen = true;
        };
        _bar.Children.Add(text);
        Add(SymbolRegular.TextBold24, "Bold (Ctrl+B)", () => EditingCommands.ToggleBold.Execute(null, _box));
        Add(SymbolRegular.TextItalic24, "Italic (Ctrl+I)", () => EditingCommands.ToggleItalic.Execute(null, _box));
        Add(SymbolRegular.TextStrikethrough24, "Strikethrough (Ctrl+Shift+X)", ToggleStrike);
        Add(SymbolRegular.Code24, "Inline code (Ctrl+`)", ToggleInlineCode);
        Separator();
        Add(SymbolRegular.TextBulletListLtr24, "Bulleted list (Ctrl+.)", () => EditingCommands.ToggleBullets.Execute(null, _box));
        Add(SymbolRegular.TextNumberListLtr24, "Numbered list (Ctrl+/)", () => EditingCommands.ToggleNumbering.Execute(null, _box));
        Add(SymbolRegular.TaskListLtr24, "Check list (Ctrl+Shift+C)", ToggleCheckList);
        Add(SymbolRegular.TextQuote24, "Quote (Ctrl+Shift+Q)", ToggleQuote);
        Separator();
        Add(SymbolRegular.Table24, "Table", () => InsertTable(3, 3, null));
        Add(SymbolRegular.Image24, "Image", PickImage);
        Add(SymbolRegular.CodeBlock24, "Script block (Ctrl+Shift+`)", () => InsertScriptBlock(null));
        Add(SymbolRegular.Link24, "Link (Ctrl+K)", AddLink);
        Add(SymbolRegular.Subtract24, "Horizontal line (--- and Enter)", InsertRule);

        void Add(SymbolRegular icon, string tip, Action a)
        {
            var b = BarButton(icon, tip);
            b.Click += (_, _) => { a(); _box.Focus(); };
            _bar.Children.Add(b);
        }
        void Separator()
        {
            var s = new Border { Width = 1, Margin = new Thickness(4, 6, 4, 6) };
            s.SetResourceReference(Border.BackgroundProperty, "ChromeStrokeBrush");
            _bar.Children.Add(s);
        }
    }

    /// <summary>A flat icon button of the format bar (also used by the view for its page actions).</summary>
    public static Button BarButton(SymbolRegular icon, string tip)
    {
        var b = new Button { Icon = new SymbolIcon(icon), ToolTip = tip, Padding = new Thickness(7, 4, 7, 4), MinWidth = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 2, 0), Focusable = false };
        AutomationProperties.SetName(b, tip);
        return b;
    }

    /// <summary>Puts extra buttons at the right end of the format bar (the view's page actions).</summary>
    public void AddBarItems(params UIElement[] items)
    {
        var spacer = new Border { Width = 1, Margin = new Thickness(4, 6, 4, 6) };
        spacer.SetResourceReference(Border.BackgroundProperty, "ChromeStrokeBrush");
        _bar.Children.Add(spacer);
        foreach (var i in items) _bar.Children.Add(i);
    }

    // ================================================================== keyboard

    private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var shift = mods.HasFlag(ModifierKeys.Shift);
        // Ctrl+Alt only with the left Alt key: AltGr (= Ctrl+Alt) types characters on many keyboards (², ³, @, €).
        var altGr = Keyboard.IsKeyDown(Key.RightAlt) && !Keyboard.IsKeyDown(Key.LeftAlt);
        var ctrlAlt = ctrl && mods.HasFlag(ModifierKeys.Alt) && !altGr;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        void Do(Action a) { a(); e.Handled = true; }

        if (ctrlAlt && key is >= Key.D0 and <= Key.D3) Do(() => SetHeading(key - Key.D0));
        else if (ctrlAlt && key == Key.C) Do(CopyAsMarkdown);
        else if (ctrlAlt && key == Key.V) Do(PasteMarkdown);
        else if (ctrl && shift && key == Key.X) Do(ToggleStrike);
        else if (ctrl && shift && key == Key.C) Do(ToggleCheckList);
        else if (ctrl && shift && key == Key.Q) Do(ToggleQuote);
        else if (ctrl && shift && key == Key.V) Do(() => PastePlain(ClipboardText()));
        else if (ctrl && shift && key == Key.Oem3) Do(() => InsertScriptBlock(null));
        else if (ctrl && !shift && key == Key.Oem3) Do(ToggleInlineCode);
        else if (ctrl && !shift && key == Key.OemPeriod) Do(() => EditingCommands.ToggleBullets.Execute(null, _box));
        else if (ctrl && !shift && key is Key.OemQuestion or Key.Divide) Do(() => EditingCommands.ToggleNumbering.Execute(null, _box));
        else if (ctrl && !shift && key == Key.K) Do(AddLink);
        else if (mods == ModifierKeys.None && key == Key.Enter && TryEnterShortcut()) e.Handled = true;
        else if (key == Key.Tab && CurrentCell() is { } cell) Do(() => MoveCell(cell, shift ? -1 : 1));
        else if (mods == ModifierKeys.None && key == Key.Enter && CurrentCell() is { } c2) Do(() => MoveRow(c2));
        else if (mods == ModifierKeys.None && key == Key.Down && NextScript(1) is { } below) Do(() => below.FocusCode(atEnd: false));
        else if (mods == ModifierKeys.None && key == Key.Up && NextScript(-1) is { } above) Do(() => above.FocusCode(atEnd: true));
    }

    /// <summary>The script block right below (or above) when the caret is on the last (first) line of its block.</summary>
    private ScriptBlock? NextScript(int direction)
    {
        var caret = _box.CaretPosition;
        if (caret.Paragraph is not { } p) return null;
        var block = (Block?)(p.Parent as ListItem)?.List ?? p;
        var neighbour = direction > 0 ? block.NextBlock : block.PreviousBlock;
        if (neighbour is not BlockUIContainer { Child: ScriptBlock sb }) return null;
        var line = caret.GetLineStartPosition(direction);
        // On the edge line: there is no next line, or it is in another block.
        return line is null || line.Paragraph != p ? sb : null;
    }

    /// <summary>Markdown typed at the start of a line becomes its format (§4.5.2): "# ", "- ", "1. ", "[] ", "> ".</summary>
    private void Box_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text != " " || _box.Selection.IsEmpty is false) return;
        var caret = _box.CaretPosition;
        if (caret.Paragraph is not { } p || CurrentCell() is not null) return;
        var before = new TextRange(p.ContentStart, caret).Text;
        var inList = p.Parent is ListItem;
        Action? apply = before switch
        {
            "#" => () => SetHeading(1),
            "##" => () => SetHeading(2),
            "###" => () => SetHeading(3),
            "-" or "*" or "+" when !inList => () => EditingCommands.ToggleBullets.Execute(null, _box),
            "1." when !inList => () => EditingCommands.ToggleNumbering.Execute(null, _box),
            "[]" or "[ ]" => ToggleCheckList,
            ">" when p.Parent is not Section => ToggleQuote,
            _ => null,
        };
        if (apply is null) return;
        e.Handled = true;
        new TextRange(p.ContentStart, caret).Text = "";
        apply();
    }

    /// <summary>Enter after "```lang", "---" or "| a | b |" on a line of its own.</summary>
    private bool TryEnterShortcut()
    {
        var caret = _box.CaretPosition;
        if (caret.Paragraph is not { } p || CurrentCell() is not null || p.Parent is ListItem) return false;
        var text = new TextRange(p.ContentStart, p.ContentEnd).Text;
        if (new TextRange(caret, p.ContentEnd).Text.Length > 0) return false;
        if (Fence().Match(text) is { Success: true } f)
        {
            ReplaceParagraph(p, new BlockUIContainer(ScriptBlock(f.Groups[1].Value.Length > 0 ? f.Groups[1].Value : ScriptLanguages.PlainId, "")), focusScript: true);
            return true;
        }
        if (text.Trim() is "---" or "***" or "___")
        {
            var rule = NoteMarkdown.RuleBlock();
            ReplaceParagraph(p, rule, focusScript: false);
            return true;
        }
        if (TableHeader().IsMatch(text.Trim()))
        {
            var headers = text.Trim().Trim('|').Split('|').Select(h => h.Trim()).ToArray();
            p.Inlines.Clear();
            InsertTable(headers.Length, 2, headers);
            return true;
        }
        return false;
    }

    [GeneratedRegex(@"^\s*```([\w#+\-]*)\s*$")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\|(.+\|){2,}$")]
    private static partial Regex TableHeader();

    private void ReplaceParagraph(Paragraph p, Block replacement, bool focusScript)
    {
        if (p.SiblingBlocks is not { } siblings) return;
        siblings.InsertAfter(p, replacement);
        siblings.Remove(p);
        var after = replacement.NextBlock;
        if (after is null) { after = new Paragraph(); siblings.InsertAfter(replacement, after); }
        if (focusScript && replacement is BlockUIContainer { Child: ScriptBlock sb }) Dispatcher.BeginInvoke(() => sb.FocusCode(false));
        else _box.CaretPosition = after.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
    }

    // ================================================================== formats

    private void SetHeading(int level)
    {
        foreach (var p in SelectedParagraphs())
        {
            if (p.Parent is ListItem or TableCell) continue;
            var h = level == 0 ? null : NoteMarkdown.Heading(level);
            p.Tag = h?.Tag;
            p.FontSize = h?.FontSize ?? NoteMarkdown.BodySize;
            p.FontWeight = h?.FontWeight ?? FontWeights.Normal;
            p.Margin = h?.Margin ?? new Thickness(0, 0, 0, 10);
            if (level == 0) { p.ClearValue(TextElement.FontSizeProperty); p.ClearValue(TextElement.FontWeightProperty); p.ClearValue(Block.MarginProperty); }
        }
        Changed?.Invoke();
    }

    private IEnumerable<Paragraph> SelectedParagraphs()
    {
        var start = _box.Selection.Start.Paragraph;
        var end = _box.Selection.End.Paragraph;
        if (start is null) yield break;
        for (var p = start; p is not null; p = NextParagraph(p))
        {
            yield return p;
            if (p == end || end is null) yield break;
        }
    }

    private static Paragraph? NextParagraph(Paragraph p)
    {
        for (var pos = p.ContentEnd.GetNextContextPosition(LogicalDirection.Forward); pos is not null; pos = pos.GetNextContextPosition(LogicalDirection.Forward))
            if (pos.Paragraph is { } next && next != p) return next;
        return null;
    }

    private void ToggleStrike()
    {
        var sel = _box.Selection;
        var current = sel.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        var has = current?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true;
        sel.ApplyPropertyValue(Inline.TextDecorationsProperty, has ? new TextDecorationCollection() : TextDecorations.Strikethrough);
    }

    private void ToggleInlineCode()
    {
        var sel = _box.Selection;
        if (sel.IsEmpty)
        {
            if (_box.CaretPosition.Paragraph is null) return;
            var r = new Run("code", _box.CaretPosition) { Tag = NoteMarkdown.Code, FontFamily = NoteMarkdown.MonoFont };
            r.SetResourceReference(TextElement.BackgroundProperty, "NoteCodeBackgroundBrush");
            _box.Selection.Select(r.ContentStart, r.ContentEnd);
            return;
        }
        var mono = NoteMarkdown.IsMono(sel.GetPropertyValue(TextElement.FontFamilyProperty) as FontFamily);
        if (mono)
        {
            sel.ApplyPropertyValue(TextElement.FontFamilyProperty, _box.Document.FontFamily);
            sel.ApplyPropertyValue(TextElement.BackgroundProperty, null);
        }
        else
        {
            sel.ApplyPropertyValue(TextElement.FontFamilyProperty, NoteMarkdown.MonoFont);
            sel.ApplyPropertyValue(TextElement.BackgroundProperty, TryFindResource("NoteCodeBackgroundBrush"));
            sel.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
            sel.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Normal);
        }
    }

    private void ToggleCheckList()
    {
        var paragraphs = SelectedParagraphs().ToList();
        if (paragraphs.Count == 0) return;
        if (paragraphs[0].Parent is not ListItem) EditingCommands.ToggleBullets.Execute(null, _box);
        paragraphs = SelectedParagraphs().ToList();
        var anyBox = paragraphs.Any(p => p.Inlines.FirstInline is InlineUIContainer { Child: CheckBox });
        foreach (var p in paragraphs.Where(p => p.Parent is ListItem li && li.Blocks.FirstBlock == p))
        {
            if (p.Inlines.FirstInline is InlineUIContainer { Child: CheckBox } box) { if (anyBox) p.Inlines.Remove(box); }
            else if (!anyBox) InsertCheckBox(p);
        }
        Changed?.Invoke();
    }

    private static void InsertCheckBox(Paragraph p)
    {
        var cb = new CheckBox { Margin = new Thickness(0, 0, 4, -3), Padding = new Thickness(0), MinWidth = 0, Focusable = false };
        var ui = new InlineUIContainer(cb) { BaselineAlignment = BaselineAlignment.Center };
        if (p.Inlines.FirstInline is { } first) p.Inlines.InsertBefore(first, ui);
        else p.Inlines.Add(ui);
    }

    private void ToggleQuote()
    {
        if (_box.CaretPosition.Paragraph is not { } p) return;
        if (p.Parent is Section { Tag: NoteMarkdown.Quote } quote && quote.SiblingBlocks is { } outer)
        {
            foreach (var b in quote.Blocks.ToList()) { quote.Blocks.Remove(b); outer.InsertBefore(quote, b); }
            outer.Remove(quote);
        }
        else if (p.Parent is FlowDocument && p.SiblingBlocks is { } siblings)
        {
            var section = new Section { Tag = NoteMarkdown.Quote };
            NoteMarkdown.StyleQuote(section);
            siblings.InsertBefore(p, section);
            siblings.Remove(p);
            section.Blocks.Add(p);
        }
        _box.CaretPosition = p.ContentEnd;
        Changed?.Invoke();
    }

    /// <summary>The top-level block the caret is in (for inserting after it).</summary>
    private Block CurrentTopBlock()
    {
        DependencyObject? x = _box.CaretPosition.Paragraph ?? (DependencyObject?)_box.CaretPosition.Parent;
        Block? last = null;
        while (x is TextElement te && x is not FlowDocument)
        {
            if (te is Block b && b.Parent is FlowDocument) return b;
            if (te is Block bb) last = bb;
            x = te.Parent;
        }
        return last ?? _box.Document.Blocks.LastBlock ?? AddParagraph();
    }

    private Block AddParagraph()
    {
        var p = new Paragraph();
        _box.Document.Blocks.Add(p);
        return p;
    }

    /// <summary>Inserts blocks after the caret's block; an empty paragraph at the caret is replaced.</summary>
    private void InsertBlocks(IReadOnlyList<Block> blocks)
    {
        if (blocks.Count == 0) return;
        var current = CurrentTopBlock();
        var replaceEmpty = current is Paragraph empty && new TextRange(empty.ContentStart, empty.ContentEnd).IsEmpty && !empty.Inlines.OfType<InlineUIContainer>().Any();
        var anchor = current;
        foreach (var b in blocks) { _box.Document.Blocks.InsertAfter(anchor, b); anchor = b; }
        if (replaceEmpty) _box.Document.Blocks.Remove(current);
        if (anchor.NextBlock is null) _box.Document.Blocks.Add(new Paragraph());
        _box.CaretPosition = anchor.NextBlock!.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        UpdateImageWidths();
        Changed?.Invoke();
    }

    private void InsertScriptBlock(string? text)
    {
        var sel = _box.Selection;
        var code = text ?? (sel.IsEmpty ? "" : sel.Text.TrimEnd('\r', '\n').Replace("\r\n", "\n"));
        if (text is null && !sel.IsEmpty) sel.Text = "";
        var block = new BlockUIContainer(ScriptBlock(code.Length > 0 ? ScriptLanguages.Guess(code) : Views.ScriptBlock.RecentLanguages.FirstOrDefault() ?? ScriptLanguages.PlainId, code));
        InsertBlocks([block]);
        if (block.Child is ScriptBlock sb) Dispatcher.BeginInvoke(() => sb.FocusCode(atEnd: true));
    }

    private void InsertRule() => InsertBlocks([NoteMarkdown.RuleBlock()]);

    private void InsertTable(int columns, int rows, string[]? headers)
    {
        var table = new System.Windows.Documents.Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 10) };
        for (var c = 0; c < columns; c++) table.Columns.Add(new TableColumn { Tag = "" });
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (var r = 0; r < rows + 1; r++)
        {
            var row = new TableRow();
            for (var c = 0; c < columns; c++)
            {
                var p = new Paragraph { Margin = new Thickness(0) };
                if (r == 0) { p.FontWeight = FontWeights.SemiBold; if (headers is not null && c < headers.Length) p.Inlines.Add(new Run(headers[c])); }
                row.Cells.Add(NoteMarkdown.StyleCell(new TableCell(p)));
            }
            group.Rows.Add(row);
        }
        InsertBlocks([table]);
        var target = group.Rows[headers is null ? 0 : 1].Cells[0];
        _box.CaretPosition = target.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
    }

    // ================================================================== tables

    private TableCell? CurrentCell()
    {
        for (DependencyObject? x = _box.CaretPosition.Parent; x is not null; x = (x as TextElement)?.Parent)
            if (x is TableCell c) return c;
        return null;
    }

    private void MoveCell(TableCell cell, int direction)
    {
        var row = (TableRow)cell.Parent;
        var group = (TableRowGroup)row.Parent;
        var cells = group.Rows.SelectMany(r => r.Cells).ToList();
        var i = cells.IndexOf(cell) + direction;
        if (i >= cells.Count) { AddRow(row, below: true); cells = group.Rows.SelectMany(r => r.Cells).ToList(); }
        if (i < 0) return;
        var target = cells[Math.Min(i, cells.Count - 1)];
        _box.Selection.Select(target.ContentStart.GetInsertionPosition(LogicalDirection.Forward), target.ContentEnd.GetInsertionPosition(LogicalDirection.Backward));
    }

    /// <summary>Enter in a cell: the cell below (a new row at the end). Cells hold one line – the limit of Markdown tables.</summary>
    private void MoveRow(TableCell cell)
    {
        var row = (TableRow)cell.Parent;
        var group = (TableRowGroup)row.Parent;
        var col = row.Cells.IndexOf(cell);
        var r = group.Rows.IndexOf(row);
        if (r == group.Rows.Count - 1) AddRow(row, below: true);
        var below = group.Rows[r + 1].Cells[Math.Min(col, group.Rows[r + 1].Cells.Count - 1)];
        _box.CaretPosition = below.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
    }

    private void AddRow(TableRow at, bool below)
    {
        var group = (TableRowGroup)at.Parent;
        var row = new TableRow();
        for (var c = 0; c < at.Cells.Count; c++)
            row.Cells.Add(NoteMarkdown.StyleCell(new TableCell(new Paragraph { Margin = new Thickness(0), TextAlignment = ((Paragraph?)at.Cells[c].Blocks.FirstBlock)?.TextAlignment ?? TextAlignment.Left })));
        var index = group.Rows.IndexOf(at) + (below ? 1 : 0);
        if (index == 0) index = 1; // the header stays the first row
        group.Rows.Insert(index, row);
        Changed?.Invoke();
    }

    private void AddColumn(TableCell at, bool right)
    {
        var table = TableOf(at);
        var col = ((TableRow)at.Parent).Cells.IndexOf(at) + (right ? 1 : 0);
        table.Columns.Insert(Math.Min(col, table.Columns.Count), new TableColumn { Tag = "" });
        foreach (var row in table.RowGroups.SelectMany(g => g.Rows))
            row.Cells.Insert(Math.Min(col, row.Cells.Count), NoteMarkdown.StyleCell(new TableCell(new Paragraph { Margin = new Thickness(0), FontWeight = row == table.RowGroups[0].Rows[0] ? FontWeights.SemiBold : FontWeights.Normal })));
        Changed?.Invoke();
    }

    private void DeleteRow(TableCell at)
    {
        var row = (TableRow)at.Parent;
        var group = (TableRowGroup)row.Parent;
        if (group.Rows.Count <= 2 || group.Rows.IndexOf(row) == 0) { DeleteTable(at); return; }
        group.Rows.Remove(row);
        Changed?.Invoke();
    }

    private void DeleteColumn(TableCell at)
    {
        var table = TableOf(at);
        var col = ((TableRow)at.Parent).Cells.IndexOf(at);
        if (table.Columns.Count <= 1) { DeleteTable(at); return; }
        foreach (var row in table.RowGroups.SelectMany(g => g.Rows)) if (col < row.Cells.Count) row.Cells.RemoveAt(col);
        if (col < table.Columns.Count) table.Columns.RemoveAt(col);
        Changed?.Invoke();
    }

    private void DeleteTable(TableCell at)
    {
        var table = TableOf(at);
        var next = table.NextBlock ?? table.PreviousBlock;
        table.SiblingBlocks?.Remove(table);
        if (_box.Document.Blocks.Count == 0) _box.Document.Blocks.Add(new Paragraph());
        _box.CaretPosition = (next ?? _box.Document.Blocks.FirstBlock).ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        Changed?.Invoke();
    }

    private void AlignColumn(TableCell at, string align)
    {
        var table = TableOf(at);
        var col = ((TableRow)at.Parent).Cells.IndexOf(at);
        if (col < table.Columns.Count) table.Columns[col].Tag = align;
        foreach (var row in table.RowGroups.SelectMany(g => g.Rows))
            if (col < row.Cells.Count) foreach (var p in row.Cells[col].Blocks.OfType<Paragraph>()) p.TextAlignment = NoteMarkdown.ToTextAlignment(align);
        Changed?.Invoke();
    }

    private static System.Windows.Documents.Table TableOf(TableCell cell) => (System.Windows.Documents.Table)((TableRowGroup)((TableRow)cell.Parent).Parent).Parent;

    // ================================================================== links

    private void AddLink()
    {
        var sel = _box.Selection;
        var existing = FindHyperlink(sel.Start.Parent);
        var dlg = new InputDialog("Link", "Address (https://…, mailto:… or remotehub://note/…)", existing?.Tag as string ?? "https://") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        var url = dlg.Value.Trim();
        if (existing is not null)
        {
            if (url.Length == 0) { var r = new TextRange(existing.ContentStart, existing.ContentEnd); var t = r.Text; var at = existing.ElementStart; ((Paragraph?)existing.Parent)?.Inlines.Remove(existing); new Run(t, at); }
            else existing.Tag = url;
        }
        else if (url.Length > 0)
        {
            if (sel.IsEmpty) { var run = new Run(url, _box.CaretPosition); sel.Select(run.ContentStart, run.ContentEnd); }
            _ = new Hyperlink(sel.Start, sel.End) { Tag = url };
        }
        Changed?.Invoke();
    }

    private static Hyperlink? FindHyperlink(DependencyObject? x)
    {
        for (; x is not null; x = (x as FrameworkContentElement)?.Parent)
            if (x is Hyperlink h) return h;
        return null;
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // never navigates on its own; Ctrl+click goes through OpenLink
    }

    /// <summary>Opens a link: only http(s) (after asking), mailto and links to other pages (§4.5.2). Anything else stays text.</summary>
    private async void OpenLink(Hyperlink h)
    {
        var url = h.Tag as string ?? "";
        if (url.StartsWith("remotehub://note/", StringComparison.OrdinalIgnoreCase))
        {
            var id = url["remotehub://note/".Length..].Trim('/');
            if (Regex.IsMatch(id, "^[0-9a-f]{32}$")) NoteLinkRequested?.Invoke(id);
            return;
        }
        var web = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        var mail = url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
        if (!web && !mail || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (web && Window.GetWindow(this) is { } w && !await Dialogs.Confirm(w, "Open link", $"Open {uri.AbsoluteUri} in your browser?", "Open")) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Message?.Invoke("The link could not be opened."); }
    }

    // ================================================================== images

    private void PickImage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp", Multiselect = true };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        InsertImageFiles(dlg.FileNames);
    }

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff"];

    private void InsertImageFiles(IEnumerable<string> files)
    {
        foreach (var f in files.Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
        {
            try
            {
                var info = new FileInfo(f);
                if (info.Length > NoteImages.MaxSourceBytes) throw new NoteImageException("The image is larger than 64 MB.");
                InsertImage(NoteImages.Prepare(File.ReadAllBytes(f)), Path.GetFileNameWithoutExtension(f));
            }
            catch (Exception ex) when (ex is NoteImageException or IOException or UnauthorizedAccessException) { Message?.Invoke($"{Path.GetFileName(f)}: {ex.Message}"); }
        }
    }

    private void InsertImage(PreparedImage image, string alt)
    {
        if (_images.Count >= NoteImages.MaxImagesPerPage) { Message?.Invoke($"A page can hold up to {NoteImages.MaxImagesPerPage} images."); return; }
        if (StoreImage?.Invoke(image) is not { } id) return;
        _imageData[id] = new Data.NoteImage(id, "", image.Mime, image.Width, image.Height, image.Data);
        var view = Image(id, alt, null);
        var p = new Paragraph(new InlineUIContainer(view) { BaselineAlignment = BaselineAlignment.Bottom });
        InsertBlocks([p]);
    }

    private void Box_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        if (_box.GetPositionFromPoint(e.GetPosition(_box), snapToText: true) is { } at) _box.CaretPosition = at;
        InsertImageFiles(files);
    }

    // ================================================================== clipboard

    private static string ClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (System.Runtime.InteropServices.ExternalException) { return ""; }
    }

    /// <summary>
    /// Paste (§4.5.7): our own Markdown first, then HTML reduced to the supported elements, then an image, then text
    /// (code-looking text becomes a script block). The clipboard's XAML and RTF are never loaded.
    /// </summary>
    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        var data = e.DataObject;
        try
        {
            if (CurrentCell() is not null)
            {
                // A cell holds one line of text.
                var t = data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : null;
                if (t is not null) _box.Selection.Text = string.Join(" ", t.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)).Trim();
                return;
            }
            if (data.GetDataPresent(MarkdownFormat) && data.GetData(MarkdownFormat) is string md) { InsertMarkdown(md); return; }
            if (data.GetDataPresent(DataFormats.Html) && data.GetData(DataFormats.Html) is string html && html.Length > 0)
            {
                InsertMarkdown(NoteHtml.ToMarkdown(NoteHtml.Fragment(html)));
                return;
            }
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files) { InsertImageFiles(files); return; }
            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is System.Windows.Media.Imaging.BitmapSource bmp)
            {
                InsertImage(NoteImages.Prepare(bmp), "Pasted image");
                return;
            }
            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text) PasteText(text);
        }
        catch (NoteImageException ex) { Message?.Invoke(ex.Message); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            Message?.Invoke("The clipboard could not be read.");
        }
    }

    /// <summary>Text: code-looking text (§4.5.5) as a script block, tab-separated rows (Excel) as a table, else plain.</summary>
    private void PasteText(string text)
    {
        var t = text.Replace("\r\n", "\n").TrimEnd('\n');
        var lines = t.Split('\n');
        var empty = _box.CaretPosition.Paragraph is { } p && new TextRange(p.ContentStart, p.ContentEnd).Text.Length == 0;
        if (empty && lines.Length > 1 && lines.All(l => l.Contains('\t')) && lines.Select(l => l.Split('\t').Length).Distinct().Count() == 1)
        {
            InsertMarkdown(string.Join("\n", lines.Select((l, i) => "| " + string.Join(" | ", l.Split('\t').Select(c => NoteMarkdown.Escape(c.Trim(), false))) + " |"
                + (i == 0 ? "\n|" + string.Concat(Enumerable.Repeat(" --- |", l.Split('\t').Length)) : ""))) + "\n");
            return;
        }
        if (empty && LooksLikeCode(lines)) { InsertScriptBlock(t); return; }
        PastePlain(t);
    }

    private static bool LooksLikeCode(string[] lines) =>
        lines.Length > 1 && (ScriptLanguages.Guess(string.Join("\n", lines)) != ScriptLanguages.PlainId
                             || lines.Count(l => l.StartsWith("    ") || l.StartsWith('\t')) * 2 >= lines.Length);

    /// <summary>Plain text: one paragraph per line, no formats.</summary>
    private void PastePlain(string text)
    {
        if (text.Length == 0) return;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sel = _box.Selection;
        sel.Text = lines[0];
        var at = sel.End;
        for (var i = 1; i < lines.Length; i++)
        {
            at = at.InsertParagraphBreak();
            at.InsertTextInRun(lines[i]);
            at = at.Paragraph?.ContentEnd ?? at;
        }
        _box.CaretPosition = at;
        Changed?.Invoke();
    }

    /// <summary>"Paste Markdown": Markdown text from the clipboard, inserted formatted.</summary>
    public void PasteMarkdown()
    {
        var text = ClipboardText();
        if (text.Length > 0) InsertMarkdown(text);
    }

    /// <summary>Inserts Markdown at the caret: one plain paragraph goes into the line, anything else as blocks.</summary>
    private void InsertMarkdown(string md)
    {
        if (md.Length > Data.NoteStore.MaxBodyLength) { Message?.Invoke("That is too much to paste into one page."); return; }
        var fragment = NoteMarkdown.ToFlowDocument(md, this);
        var blocks = fragment.Blocks.ToList();
        if (blocks is [Paragraph { Tag: null } single])
        {
            if (!_box.Selection.IsEmpty) _box.Selection.Text = "";
            var pos = _box.CaretPosition;
            foreach (var inline in single.Inlines.ToList())
            {
                var start = pos.GetInsertionPosition(LogicalDirection.Forward);
                var text = new TextRange(inline.ContentStart, inline.ContentEnd).Text;
                if (text.Length == 0) continue;
                start.InsertTextInRun(text);
                var range = new TextRange(start, start.GetPositionAtOffset(text.Length) ?? start);
                ApplyLike(range, inline);
                pos = range.End;
            }
            _box.CaretPosition = pos;
            Changed?.Invoke();
            return;
        }
        foreach (var b in blocks) fragment.Blocks.Remove(b);
        if (_box.CaretPosition.Paragraph is { } p && new TextRange(_box.CaretPosition, p.ContentEnd).Text.Length > 0 && new TextRange(p.ContentStart, _box.CaretPosition).Text.Length > 0)
            _box.CaretPosition = _box.CaretPosition.InsertParagraphBreak().GetPositionAtOffset(-1) ?? _box.CaretPosition;
        InsertBlocks(blocks);
    }

    /// <summary>Gives an inserted range the formats of a parsed inline (bold, italic, strikethrough, code, link).</summary>
    private void ApplyLike(TextRange range, Inline source)
    {
        bool Has(Func<Inline, bool> test) { for (DependencyObject? x = source; x is Inline i; x = i.Parent) if (test(i)) return true; return false; }
        if (Has(i => i is Bold)) range.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Bold);
        if (Has(i => i is Italic)) range.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Italic);
        if (Has(i => i.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true))
            range.ApplyPropertyValue(Inline.TextDecorationsProperty, TextDecorations.Strikethrough);
        if (Has(i => i.Tag as string == NoteMarkdown.Code))
        {
            range.ApplyPropertyValue(TextElement.FontFamilyProperty, NoteMarkdown.MonoFont);
            range.ApplyPropertyValue(TextElement.BackgroundProperty, TryFindResource("NoteCodeBackgroundBrush"));
        }
        for (DependencyObject? x = source; x is Inline i; x = i.Parent)
            if (i is Hyperlink h) { _ = new Hyperlink(range.Start, range.End) { Tag = h.Tag }; break; }
    }

    /// <summary>Copy: adds our Markdown and HTML (for Word, Outlook, Teams) next to the editor's own formats.</summary>
    private void OnCopy(object sender, DataObjectCopyingEventArgs e)
    {
        if (e.IsDragDrop) return;
        var md = SelectionMarkdown();
        if (md.Length == 0) return;
        e.DataObject.SetData(MarkdownFormat, md);
        e.DataObject.SetData(DataFormats.Html, NoteHtml.ClipboardFormat(NoteHtml.FromMarkdown(WithoutImages(md))));
        if (_box.Selection.Text.Trim().Length == 0) e.DataObject.SetData(DataFormats.UnicodeText, md); // only script blocks or images selected
    }

    private string SelectionMarkdown()
    {
        var sel = _box.Selection;
        return sel.IsEmpty ? "" : NoteMarkdown.ToMarkdown(_box.Document, sel.Start, sel.End);
    }

    /// <summary>"Copy as Markdown": the selection (or the whole page) as plain Markdown text.</summary>
    public void CopyAsMarkdown()
    {
        var md = _box.Selection.IsEmpty ? Markdown() : SelectionMarkdown();
        SetClipboardText(md);
    }

    public static void SetClipboardText(string text)
    {
        try { Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text.Replace("\n", Environment.NewLine)), true); }
        catch (System.Runtime.InteropServices.ExternalException) { /* busy; the user can try again */ }
    }

    // Images in copied HTML would point at our own store: other apps get their alternative text.
    private static string WithoutImages(string md) => Regex.Replace(md, @"!\[([^\]]*)\]\(note-file:[0-9a-f]{32}\)(\{width=\d+\})?", "$1");

    // ================================================================== context menu

    private void Box_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var m = _box.ContextMenu!;
        m.Items.Clear();
        m.Items.Add(new MenuItem { Header = "Cut", Command = ApplicationCommands.Cut, CommandTarget = _box, Icon = new SymbolIcon(SymbolRegular.Cut24) });
        m.Items.Add(new MenuItem { Header = "Copy", Command = ApplicationCommands.Copy, CommandTarget = _box, Icon = new SymbolIcon(SymbolRegular.Copy24) });
        m.Items.Add(new MenuItem { Header = "Paste", Command = ApplicationCommands.Paste, CommandTarget = _box, Icon = new SymbolIcon(SymbolRegular.ClipboardPaste24) });
        m.Items.Add(RemoteToolChrome.Item("Paste as plain text", SymbolRegular.TextT24, () => PastePlain(ClipboardText()), gesture: "Ctrl+Shift+V"));
        m.Items.Add(new Separator());
        m.Items.Add(RemoteToolChrome.Item(_box.Selection.IsEmpty ? "Copy page as Markdown" : "Copy as Markdown", SymbolRegular.DocumentCopy24, CopyAsMarkdown, gesture: "Ctrl+Alt+C"));
        m.Items.Add(RemoteToolChrome.Item("Paste Markdown", SymbolRegular.ClipboardCode24, PasteMarkdown, gesture: "Ctrl+Alt+V"));
        if (FindHyperlink(_box.CaretPosition.Parent) is { } link)
        {
            m.Items.Add(new Separator());
            m.Items.Add(RemoteToolChrome.Item("Open link", SymbolRegular.Open24, () => OpenLink(link), gesture: "Ctrl+click"));
            m.Items.Add(RemoteToolChrome.Item("Edit link…", SymbolRegular.Link24, AddLink, gesture: "Ctrl+K"));
        }
        if (CurrentCell() is { } cell)
        {
            m.Items.Add(new Separator());
            var t = new MenuItem { Header = "Table", Icon = new SymbolIcon(SymbolRegular.Table24) };
            t.Items.Add(RemoteToolChrome.Item("Insert row above", SymbolRegular.TableInsertRow24, () => AddRow((TableRow)cell.Parent, below: false)));
            t.Items.Add(RemoteToolChrome.Item("Insert row below", SymbolRegular.TableInsertRow24, () => AddRow((TableRow)cell.Parent, below: true)));
            t.Items.Add(RemoteToolChrome.Item("Insert column left", SymbolRegular.TableInsertColumn24, () => AddColumn(cell, right: false)));
            t.Items.Add(RemoteToolChrome.Item("Insert column right", SymbolRegular.TableInsertColumn24, () => AddColumn(cell, right: true)));
            t.Items.Add(new Separator());
            t.Items.Add(RemoteToolChrome.Item("Align column left", SymbolRegular.TextAlignLeft24, () => AlignColumn(cell, "left")));
            t.Items.Add(RemoteToolChrome.Item("Align column centre", SymbolRegular.TextAlignCenter24, () => AlignColumn(cell, "center")));
            t.Items.Add(RemoteToolChrome.Item("Align column right", SymbolRegular.TextAlignRight24, () => AlignColumn(cell, "right")));
            t.Items.Add(new Separator());
            t.Items.Add(RemoteToolChrome.Item("Delete row", SymbolRegular.TableDeleteRow24, () => DeleteRow(cell)));
            t.Items.Add(RemoteToolChrome.Item("Delete column", SymbolRegular.TableDeleteColumn24, () => DeleteColumn(cell)));
            t.Items.Add(RemoteToolChrome.Item("Delete table", SymbolRegular.Delete24, () => DeleteTable(cell)));
            m.Items.Add(t);
        }
    }
}
