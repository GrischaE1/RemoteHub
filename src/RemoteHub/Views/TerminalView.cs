using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RemoteHub.Remoting;
using RemoteHub.Terminal;
using Wpf.Ui.Controls;

namespace RemoteHub.Views;

/// <summary>Tab content of an SSH connection: the shared header, the terminal with its scroll bar, and the "not connected" overlay.</summary>
public sealed class TerminalView : Border, IRecordableView
{
    public FrameworkElement RecordingArea { get; }

    private readonly SshSession _session;
    private readonly Border _headerStripe = new() { Width = 4, Height = 18, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0) };

    public TerminalView(SshSession session, Action reconnect, Action close)
    {
        _session = session;
        DataContext = session;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(2);
        Background = PsConsoleView.ConsoleBackground;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Child = root;
        root.Children.Add(RemoteToolChrome.Header(session, SymbolRegular.WindowConsole20, "SSH", _headerStripe, session.Entry.Address));

        var content = new Grid();
        Grid.SetRow(content, 1);
        root.Children.Add(content);
        RecordingArea = content;

        var area = new Grid { Margin = new Thickness(10, 6, 4, 8) }; // as the console's
        area.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        area.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Surface = new TerminalSurface(session);
        var scroll = new ScrollBar { Orientation = Orientation.Vertical, Margin = new Thickness(4, 0, 0, 0) };
        Grid.SetColumn(scroll, 1);
        Surface.ScrollBar = scroll;
        area.Children.Add(Surface);
        area.Children.Add(scroll);
        content.Children.Add(area);
        // Square at the top, where it meets the header: the console's dark holder would show in rounded corners.
        var overlay = RemoteToolChrome.Overlay(reconnect, close);
        overlay.CornerRadius = new CornerRadius(0, 0, 6, 6);
        content.Children.Add(overlay);

        session.PropertyChanged += OnSessionChanged;
        session.FocusRequested += Surface.FocusTerminal;
        UpdateFrame();
    }

    public SshSession Session => _session;
    internal TerminalSurface Surface { get; }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SshSession.HasKeyboardFocus) or nameof(SshSession.AccentBrush)) UpdateFrame();
        if (e.PropertyName == nameof(SshSession.State) && _session.IsConnected) Surface.FocusTerminal();
    }

    private void UpdateFrame()
    {
        var accent = _session.AccentBrush;
        _headerStripe.Background = accent;
        if (_session.HasKeyboardFocus) BorderBrush = accent;
        else SetResourceReference(BorderBrushProperty, "ChromeStrokeBrush");
    }

    /// <summary>The window lets keys through to a focused terminal (bash needs Ctrl+W, Ctrl+N, Ctrl+F …); only Ctrl+Tab stays the app's.</summary>
    public static bool OwnsKey(KeyEventArgs e) =>
        Keyboard.FocusedElement is TerminalSurface && !(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Tab);
}

/// <summary>
/// Draws a <see cref="VtTerminal"/> as a grid of character cells and turns keys, pastes and the mouse into what the server
/// gets. Redraws at most once per frame while output arrives; a scrolled-back view stays where it is.
/// </summary>
internal sealed class TerminalSurface : FrameworkElement
{
    private const double FontSize = 13;
    /// <summary>The console's selection: SteelBlue at 45 % (PsConsoleView's SelectionBrush and SelectionOpacity).</summary>
    private static readonly Brush SelectionBrush = Frozen(new SolidColorBrush(Colors.SteelBlue) { Opacity = 0.45 });
    private static Brush CursorBrush => PsConsoleView.DefaultText;

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private readonly SshSession _session;
    private readonly VtTerminal _term;
    private readonly GlyphTypeface[] _faces = new GlyphTypeface[4]; // regular, bold, italic, bold italic
    private readonly Typeface[] _typefaces = new Typeface[4];
    private double _cellWidth, _cellHeight, _baseline;
    private int _redrawPending;
    private readonly System.Windows.Threading.DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    // The view: the absolute number of the top line (lines that scrolled off so far + row), or null when it follows the output.
    private long? _top;
    // Selection in absolute lines (see _top) and columns; null when nothing is selected.
    private (long Line, int Col)? _anchor, _caret;
    private bool _selecting;
    private int _mouseButton = -1;

    public TerminalSurface(SshSession session)
    {
        _session = session;
        _term = session.Terminal;
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.None);
        System.Windows.Automation.AutomationProperties.SetName(this, "Terminal");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        LoadFonts();
        ContextMenu = Menu();
        _session.Output += OnOutput;
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); ApplySize(); };
        GotKeyboardFocus += (_, _) => { _session.HasKeyboardFocus = true; InvalidateVisual(); };
        LostKeyboardFocus += (_, _) => { _session.HasKeyboardFocus = false; InvalidateVisual(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) InvalidateVisual(); };
    }

    internal ScrollBar? ScrollBar
    {
        get => _scrollBar;
        set
        {
            _scrollBar = value;
            if (value is null) return;
            value.Scroll += (_, e) =>
            {
                lock (_term.SyncRoot)
                {
                    var oldest = _term.ScrolledOff - _term.ScrollbackCount;
                    var top = oldest + (long)Math.Round(e.NewValue);
                    _top = top >= _term.ScrolledOff ? null : top;
                }
                InvalidateVisual();
            };
        }
    }
    private ScrollBar? _scrollBar;

    public void FocusTerminal()
    {
        if (!IsVisible) return;
        Dispatcher.BeginInvoke(() => { Focus(); Keyboard.Focus(this); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // ------------------------------------------------------------------ fonts and size

    private void LoadFonts()
    {
        for (var i = 0; i < 4; i++)
        {
            _typefaces[i] = new Typeface(PsConsoleView.Mono, (i & 2) != 0 ? FontStyles.Italic : FontStyles.Normal,
                (i & 1) != 0 ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            if (!_typefaces[i].TryGetGlyphTypeface(out _faces[i]!))
            {
                // Without the style, the regular face (drawn as is); without any of the console fonts, Courier New is a system font.
                _faces[i] = i > 0 ? _faces[0] : (new Typeface("Courier New").TryGetGlyphTypeface(out var g) ? g : throw new InvalidOperationException("No monospaced font."));
            }
        }
        var face = _faces[0];
        var m = face.CharacterToGlyphMap.TryGetValue('M', out var gi) ? gi : (ushort)0;
        _cellWidth = face.AdvanceWidths[m] * FontSize;
        _cellHeight = Math.Ceiling(face.Height * FontSize);
        _baseline = Math.Round(face.Baseline * FontSize + (_cellHeight - face.Height * FontSize) / 2);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        // While the window is dragged bigger or smaller, the server is told once it stops.
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    internal (int Columns, int Rows) CellsFor(Size size) =>
        (Math.Max(2, (int)Math.Floor(size.Width / _cellWidth)), Math.Max(1, (int)Math.Floor(size.Height / _cellHeight)));

    private void ApplySize()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var (columns, rows) = CellsFor(new Size(ActualWidth, ActualHeight));
        _session.Resize(columns, rows);
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ drawing

    private void OnOutput()
    {
        if (System.Threading.Interlocked.Exchange(ref _redrawPending, 1) == 1) return;
        Dispatcher.BeginInvoke(() =>
        {
            System.Threading.Interlocked.Exchange(ref _redrawPending, 0);
            InvalidateVisual();
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // hit-testable everywhere
        if (_cellWidth <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        long top;
        int rows, columns, scrollback;
        lock (_term.SyncRoot)
        {
            rows = _term.Rows;
            columns = _term.Columns;
            scrollback = _term.ScrollbackCount;
            var oldest = _term.ScrolledOff - scrollback;
            if (_top is { } t && t < oldest) _top = t = oldest; // the line it showed has left the scrollback
            top = _top ?? _term.ScrolledOff;
            for (var r = 0; r < rows; r++)
            {
                var index = top + r - _term.ScrolledOff;
                if (index < -scrollback || index >= rows) continue;
                DrawLine(dc, _term.Line((int)index), r, top + r, dpi);
            }
            if (_top is null && _term.CursorVisible) DrawCursor(dc);
        }
        UpdateScrollBar(scrollback, rows, top);
    }

    private void DrawLine(DrawingContext dc, TermLine line, int row, long absolute, double dpi)
    {
        var y = row * _cellHeight;
        var cells = line.Cells;
        var count = Math.Min(cells.Length, _term.Columns);
        // Backgrounds: runs of one colour.
        for (var c = 0; c < count;)
        {
            var (_, bg) = Colours(cells[c]);
            var start = c;
            while (c < count && ReferenceEquals(Colours(cells[c]).Bg, bg)) c++;
            if (bg is not null) dc.DrawRectangle(bg, null, new Rect(start * _cellWidth, y, (c - start) * _cellWidth, _cellHeight));
        }
        // Text: runs of one style; a character the font lacks is drawn on its own, with fallback fonts.
        var glyphs = new List<ushort>(count);
        var advances = new List<double>(count);
        for (var c = 0; c < count;)
        {
            var cell = cells[c];
            if (cell.Width == 0 || cell.Rune is 0 or ' ' || cell.Flags.HasFlag(CellFlags.Hidden)) { Decorate(dc, cell, c, y); c++; continue; }
            var (fg, _) = Colours(cell);
            var style = StyleIndex(cell.Flags);
            var face = _faces[style];
            if (cell.Rune > 0xFFFF || !face.CharacterToGlyphMap.TryGetValue(cell.Rune, out _))
            {
                DrawFallback(dc, cell, c, y, fg!, dpi);
                Decorate(dc, cell, c, y);
                c += Math.Max(1, (int)cell.Width);
                continue;
            }
            glyphs.Clear();
            advances.Clear();
            var start = c;
            while (c < count)
            {
                var next = cells[c];
                if (next.Width == 0 || next.Rune is 0 or ' ' || next.Flags != cell.Flags || !ReferenceEquals(Colours(next).Fg, fg)
                    || next.Rune > 0xFFFF || !face.CharacterToGlyphMap.TryGetValue(next.Rune, out var g)) break;
                glyphs.Add(g);
                advances.Add(next.Width * _cellWidth);
                Decorate(dc, next, c, y);
                c += next.Width;
            }
            var run = new GlyphRun(face, 0, false, FontSize, (float)dpi, [.. glyphs], new Point(start * _cellWidth, y + _baseline), [.. advances],
                null, null, null, null, null, null);
            dc.DrawGlyphRun(fg, run);
        }
        // The selection over it.
        if (Selection() is { } sel && absolute >= sel.From.Line && absolute <= sel.To.Line)
        {
            var from = absolute == sel.From.Line ? sel.From.Col : 0;
            var to = absolute == sel.To.Line ? sel.To.Col : count - 1;
            if (to >= from) dc.DrawRectangle(SelectionBrush, null, new Rect(from * _cellWidth, y, (to - from + 1) * _cellWidth, _cellHeight));
        }
    }

    private void DrawFallback(DrawingContext dc, Cell cell, int column, double y, Brush fg, double dpi)
    {
        var text = new FormattedText(char.ConvertFromUtf32(cell.Rune), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typefaces[StyleIndex(cell.Flags)], FontSize, fg, dpi);
        var width = Math.Max(1, (int)cell.Width) * _cellWidth;
        // Centred in its cells, so a slightly wider fallback glyph doesn't push the grid.
        dc.DrawText(text, new Point(column * _cellWidth + (width - text.WidthIncludingTrailingWhitespace) / 2, y + _baseline - text.Baseline));
    }

    private void Decorate(DrawingContext dc, Cell cell, int column, double y)
    {
        if ((cell.Flags & (CellFlags.Underline | CellFlags.Strike)) == 0 || cell.Width == 0) return;
        var (fg, _) = Colours(cell);
        var pen = new Pen(fg, 1);
        var x = column * _cellWidth;
        var w = Math.Max(1, (int)cell.Width) * _cellWidth;
        if (cell.Flags.HasFlag(CellFlags.Underline)) dc.DrawLine(pen, new Point(x, y + _baseline + 1.5), new Point(x + w, y + _baseline + 1.5));
        if (cell.Flags.HasFlag(CellFlags.Strike)) dc.DrawLine(pen, new Point(x, y + _cellHeight / 2), new Point(x + w, y + _cellHeight / 2));
    }

    private void DrawCursor(DrawingContext dc)
    {
        var rect = new Rect(_term.CursorX * _cellWidth, _term.CursorY * _cellHeight, _cellWidth, _cellHeight);
        // A block while keys go here, an outline otherwise.
        if (IsKeyboardFocused)
        {
            dc.PushOpacity(0.55);
            dc.DrawRectangle(CursorBrush, null, rect);
            dc.Pop();
        }
        else dc.DrawRectangle(null, new Pen(CursorBrush, 1), new Rect(rect.X + 0.5, rect.Y + 0.5, rect.Width - 1, rect.Height - 1));
    }

    private static int StyleIndex(CellFlags f) => (f.HasFlag(CellFlags.Bold) ? 1 : 0) + (f.HasFlag(CellFlags.Italic) ? 2 : 0);

    /// <summary>The cell's brushes: inverse swaps them, bold brightens the first eight colours, faint dims; Bg null = the console's own.</summary>
    private static (Brush? Fg, Brush? Bg) Colours(Cell cell)
    {
        var fgIndex = cell.Fg;
        if (cell.Flags.HasFlag(CellFlags.Bold) && fgIndex is >= 0 and < 8) fgIndex += 8;
        var fg = fgIndex == TermColor.Default ? PsConsoleView.DefaultText : TerminalPalette.Brush(fgIndex);
        var bg = cell.Bg == TermColor.Default ? null : TerminalPalette.Brush(cell.Bg);
        if (cell.Flags.HasFlag(CellFlags.Inverse)) (fg, bg) = (bg ?? PsConsoleView.ConsoleBackground, fg);
        if (cell.Flags.HasFlag(CellFlags.Faint)) fg = TerminalPalette.Dimmed(fg);
        return (fg, bg);
    }

    private void UpdateScrollBar(int scrollback, int rows, long top)
    {
        if (_scrollBar is not { } bar) return;
        long scrolledOff;
        lock (_term.SyncRoot) scrolledOff = _term.ScrolledOff;
        bar.Minimum = 0;
        bar.Maximum = scrollback;
        bar.ViewportSize = rows;
        bar.LargeChange = rows;
        bar.SmallChange = 1;
        bar.Value = Math.Clamp(top - (scrolledOff - scrollback), 0, scrollback);
        bar.Visibility = scrollback > 0 ? Visibility.Visible : Visibility.Hidden;
    }

    // ------------------------------------------------------------------ keys and text

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var ctrlShift = mods == (ModifierKeys.Control | ModifierKeys.Shift);
        if ((ctrlShift && key == Key.C) || (mods == ModifierKeys.Control && key == Key.Insert)) { Copy(); e.Handled = true; return; }
        if ((ctrlShift && key == Key.V) || (mods == ModifierKeys.Shift && key == Key.Insert)) { _ = Paste(); e.Handled = true; return; }
        if (mods == ModifierKeys.Shift && key is Key.PageUp or Key.PageDown) { ScrollView(key == Key.PageUp ? -_term.Rows + 1 : _term.Rows - 1); e.Handled = true; return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        bool appCursor;
        lock (_term.SyncRoot) appCursor = _term.AppCursorKeys;
        if (TerminalInput.Encode(key, mods, appCursor) is { } data)
        {
            SendTyped(data);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        // Alt+key comes as "system text": Meta, sent as ESC and the character.
        var text = e.Text.Length > 0 ? e.Text : e.SystemText.Length > 0 ? "\x1b" + e.SystemText : "";
        if (text.Length == 0 || text[0] < ' ' && text[0] != '\x1b') return; // control characters came through the key handler
        SendTyped(text);
        e.Handled = true;
    }

    private void SendTyped(string data)
    {
        ClearSelection();
        _top = null; // typing goes back to the bottom
        _session.Send(data);
        InvalidateVisual();
    }

    private void Copy()
    {
        if (SelectedText() is not { Length: > 0 } text) return;
        try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }

    private async Task Paste()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { return; }
        if (text.Length == 0 || !_session.IsConnected) return;
        bool bracketed;
        lock (_term.SyncRoot) bracketed = _term.BracketedPaste;
        // Several lines without bracketed paste run one after another, as if typed – asked first (a page can hide commands
        // in what looks like one line when copied).
        if (TerminalInput.RunsCommands(text, bracketed) && Window.GetWindow(this) is { } owner)
        {
            var lines = text.TrimEnd('\r', '\n').Split('\n').Length;
            if (!await Dialogs.Confirm(owner, "Paste into the terminal",
                    $"The text has {lines} lines. Each one runs as a command on {_session.Entry.Name} as soon as it is pasted.\n\nPaste it?", "Paste")) return;
        }
        SendTyped(TerminalInput.Paste(text, bracketed));
    }

    private ContextMenu Menu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            menu.Items.Add(RemoteToolChrome.Item("Copy", SymbolRegular.Copy24, Copy, Selection() is not null, "Ctrl+Shift+C"));
            menu.Items.Add(RemoteToolChrome.Item("Paste", SymbolRegular.ClipboardPaste24, () => _ = Paste(), _session.IsConnected, "Ctrl+Shift+V"));
            menu.Items.Add(RemoteToolChrome.Item("Select all", SymbolRegular.SelectAllOn24, SelectAll));
            menu.Items.Add(new Separator());
            menu.Items.Add(RemoteToolChrome.Item("Clear scrollback", SymbolRegular.Broom24, () =>
            {
                lock (_term.SyncRoot) _term.ClearScrollback();
                _top = null;
                ClearSelection();
            }));
        };
        menu.Items.Add(new Separator()); // filled when it opens
        return menu;
    }

    // ------------------------------------------------------------------ mouse and selection

    private (long Line, int Col) CellAt(Point p)
    {
        lock (_term.SyncRoot)
        {
            var top = _top ?? _term.ScrolledOff;
            var row = Math.Clamp((int)Math.Floor(p.Y / _cellHeight), 0, _term.Rows - 1);
            var col = Math.Clamp((int)Math.Floor(p.X / _cellWidth), 0, _term.Columns - 1);
            return (top + row, col);
        }
    }

    /// <summary>The program asked for the mouse, and Shift isn't held (Shift selects as usual).</summary>
    private bool MouseForProgram(out bool sgr)
    {
        lock (_term.SyncRoot)
        {
            sgr = _term.SgrMouse;
            return _term.Mouse != MouseTracking.None && _top is null && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _session.IsConnected;
        }
    }

    private void SendMouse(int button, Point p, bool release, bool sgr)
    {
        var (line, col) = CellAt(p);
        long top;
        lock (_term.SyncRoot) top = _term.ScrolledOff;
        if (TerminalInput.Mouse(button, col, (int)(line - top), release, sgr, Keyboard.Modifiers) is { } data) _session.Send(data);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var button = e.ChangedButton switch { MouseButton.Left => 0, MouseButton.Middle => 1, MouseButton.Right => 2, _ => -1 };
        if (button < 0) return;
        if (MouseForProgram(out var sgr) && button != 2)
        {
            _mouseButton = button;
            SendMouse(button, e.GetPosition(this), release: false, sgr);
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (button != 0) return; // right: the menu
        var cell = CellAt(e.GetPosition(this));
        if (e.ClickCount == 2) SelectWord(cell);
        else if (e.ClickCount >= 3) SelectLine(cell.Line);
        else
        {
            _anchor = _caret = cell;
            _selecting = true;
            CaptureMouse();
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_mouseButton >= 0)
        {
            if (MouseForProgram(out var sgr))
                lock (_term.SyncRoot)
                    if (_term.Mouse is MouseTracking.Drag or MouseTracking.Any) SendMouse(_mouseButton + 32, e.GetPosition(this), false, sgr);
            return;
        }
        if (_selecting)
        {
            var p = e.GetPosition(this);
            // Dragging above or below the terminal scrolls it.
            if (p.Y < 0) ScrollView(-1);
            else if (p.Y > ActualHeight) ScrollView(1);
            _caret = CellAt(p);
            InvalidateVisual();
        }
        else if (MouseForProgram(out var sgr))
            lock (_term.SyncRoot)
                if (_term.Mouse == MouseTracking.Any) SendMouse(35, e.GetPosition(this), false, sgr);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_mouseButton >= 0)
        {
            MouseForProgram(out var sgr);
            SendMouse(_mouseButton, e.GetPosition(this), release: true, sgr);
            _mouseButton = -1;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (!_selecting) return;
        _selecting = false;
        ReleaseMouseCapture();
        if (_anchor == _caret) ClearSelection(); // a click, not a selection
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var up = e.Delta > 0;
        if (MouseForProgram(out var sgr)) SendMouse(up ? 64 : 65, e.GetPosition(this), false, sgr);
        else
        {
            bool alt, appCursor;
            lock (_term.SyncRoot) (alt, appCursor) = (_term.AltScreen, _term.AppCursorKeys);
            // Full-screen programs (less, vim) have no scrollback here: the wheel moves them by line, like xterm.
            if (alt) for (var i = 0; i < 3; i++) _session.Send(TerminalInput.Encode(up ? Key.Up : Key.Down, ModifierKeys.None, appCursor)!);
            else ScrollView(up ? -3 : 3);
        }
        e.Handled = true;
    }

    private void ScrollView(int lines)
    {
        lock (_term.SyncRoot)
        {
            var oldest = _term.ScrolledOff - _term.ScrollbackCount;
            var top = Math.Clamp((_top ?? _term.ScrolledOff) + lines, oldest, _term.ScrolledOff);
            _top = top >= _term.ScrolledOff ? null : top;
        }
        InvalidateVisual();
    }

    private ((long Line, int Col) From, (long Line, int Col) To)? Selection()
    {
        if (_anchor is not { } a || _caret is not { } c) return null;
        return a.Line < c.Line || (a.Line == c.Line && a.Col <= c.Col) ? (a, c) : (c, a);
    }

    private void ClearSelection()
    {
        _anchor = _caret = null;
        _selecting = false;
    }

    private void SelectAll()
    {
        lock (_term.SyncRoot)
        {
            _anchor = (_term.ScrolledOff - _term.ScrollbackCount, 0);
            _caret = (_term.ScrolledOff + _term.Rows - 1, _term.Columns - 1);
        }
        InvalidateVisual();
    }

    private void SelectLine(long line)
    {
        lock (_term.SyncRoot)
        {
            _anchor = (line, 0);
            _caret = (line, _term.Columns - 1);
        }
    }

    private void SelectWord((long Line, int Col) at)
    {
        lock (_term.SyncRoot)
        {
            var index = at.Line - _term.ScrolledOff;
            if (index < -_term.ScrollbackCount || index >= _term.Rows) return;
            var cells = _term.Line((int)index).Cells;
            static bool Word(Cell c) => c.Rune != 0 && (char.IsLetterOrDigit(char.ConvertFromUtf32(c.Rune), 0) || c.Rune is '-' or '_' or '.' or '/' or '~' or ':' or '@');
            if (at.Col >= cells.Length || !Word(cells[at.Col])) { _anchor = _caret = at; return; }
            var from = at.Col;
            var to = at.Col;
            while (from > 0 && Word(cells[from - 1])) from--;
            while (to < cells.Length - 1 && Word(cells[to + 1])) to++;
            _anchor = (at.Line, from);
            _caret = (at.Line, to);
        }
    }

    internal string? SelectedText()
    {
        if (Selection() is not { } sel) return null;
        lock (_term.SyncRoot)
            return _term.Text((int)(sel.From.Line - _term.ScrolledOff), sel.From.Col, (int)(sel.To.Line - _term.ScrolledOff), sel.To.Col);
    }

    // ------------------------------------------------------------------ UI Automation: the screen's text (screen readers, the lab)

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(TerminalSurface owner) : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
        protected override string GetClassNameCore() => "TerminalSurface";
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);
        public bool IsReadOnly => true;
        public string Value { get { lock (owner._term.SyncRoot) return owner._term.ScreenText(); } }
        public void SetValue(string value) => throw new InvalidOperationException("The terminal can't be set.");
    }
}

/// <summary>The terminal's colours: the 16 standard ones (as Windows Terminal's "Campbell"), the 6×6×6 cube, the grey ramp, and 24-bit.</summary>
internal static class TerminalPalette
{
    private static readonly Color[] Standard =
    [
        Color.FromRgb(0x0C, 0x0C, 0x0C), Color.FromRgb(0xC5, 0x0F, 0x1F), Color.FromRgb(0x13, 0xA1, 0x0E), Color.FromRgb(0xC1, 0x9C, 0x00),
        Color.FromRgb(0x00, 0x37, 0xDA), Color.FromRgb(0x88, 0x17, 0x98), Color.FromRgb(0x3A, 0x96, 0xDD), Color.FromRgb(0xCC, 0xCC, 0xCC),
        Color.FromRgb(0x76, 0x76, 0x76), Color.FromRgb(0xE7, 0x48, 0x56), Color.FromRgb(0x16, 0xC6, 0x0C), Color.FromRgb(0xF9, 0xF1, 0xA5),
        Color.FromRgb(0x3B, 0x78, 0xFF), Color.FromRgb(0xB4, 0x00, 0x9E), Color.FromRgb(0x61, 0xD6, 0xD6), Color.FromRgb(0xF2, 0xF2, 0xF2),
    ];

    private static readonly Dictionary<int, Brush> Cache = [];
    private static readonly Dictionary<Brush, Brush> DimCache = [];

    public static Brush Brush(int colour)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(colour, out var b)) return b;
            if (Cache.Count > 4096) Cache.Clear(); // 24-bit colours: a program may use many
            return Cache[colour] = ColorTags.Frozen(ColourOf(colour));
        }
    }

    internal static Color ColourOf(int colour)
    {
        if (TermColor.IsRgb(colour)) return Color.FromRgb((byte)(colour >> 16), (byte)(colour >> 8), (byte)colour);
        if (colour is >= 0 and < 16) return Standard[colour];
        if (colour is >= 16 and < 232)
        {
            var i = colour - 16;
            static byte Level(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);
            return Color.FromRgb(Level(i / 36), Level(i / 6 % 6), Level(i % 6));
        }
        var grey = (byte)(8 + (Math.Clamp(colour, 232, 255) - 232) * 10);
        return Color.FromRgb(grey, grey, grey);
    }

    public static Brush Dimmed(Brush brush)
    {
        lock (DimCache)
        {
            if (DimCache.TryGetValue(brush, out var d)) return d;
            var c = brush is SolidColorBrush s ? s.Color : Colors.Gray;
            if (DimCache.Count > 4096) DimCache.Clear();
            return DimCache[brush] = ColorTags.Frozen(Color.FromRgb((byte)(c.R * 0.6), (byte)(c.G * 0.6), (byte)(c.B * 0.6)));
        }
    }
}
