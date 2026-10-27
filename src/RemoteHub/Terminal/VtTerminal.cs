using System.Globalization;
using System.Text;

namespace RemoteHub.Terminal;

[Flags]
public enum CellFlags : byte { None = 0, Bold = 1, Faint = 2, Italic = 4, Underline = 8, Blink = 16, Inverse = 32, Hidden = 64, Strike = 128 }

/// <summary>How the program on the server wants mouse events (DEC private modes 1000, 1002, 1003).</summary>
public enum MouseTracking { None, Click, Drag, Any }

/// <summary>
/// One character cell. <see cref="Rune"/> 0 is an empty cell (drawn as a space). A wide character (CJK, most emoji) takes
/// two cells: the first has <see cref="Width"/> 2, the second 0.
/// </summary>
public struct Cell
{
    public int Rune;
    /// <summary><see cref="TermColor"/> values.</summary>
    public int Fg, Bg;
    public CellFlags Flags;
    public byte Width;

    public static Cell Blank(int bg) => new() { Fg = TermColor.Default, Bg = bg, Width = 1 };
}

/// <summary>Colours as stored in a cell: the default colour, one of the 256 palette entries, or 24-bit RGB.</summary>
public static class TermColor
{
    public const int Default = -1;
    private const int RgbFlag = 0x1000000;

    public static int Rgb(int r, int g, int b) => RgbFlag | (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);
    public static bool IsRgb(int c) => c >= RgbFlag;
    public static bool IsPalette(int c) => c is >= 0 and < 256;
}

/// <summary>A row of cells. <see cref="Wrapped"/>: the text goes on in the next row (copying joins them without a line break).</summary>
public sealed class TermLine(int columns, int bg)
{
    public Cell[] Cells = Fill(columns, bg);
    public bool Wrapped;

    internal static Cell[] Fill(int columns, int bg)
    {
        var cells = new Cell[columns];
        Array.Fill(cells, Cell.Blank(bg));
        return cells;
    }

    internal void Resize(int columns)
    {
        if (columns == Cells.Length) return;
        var old = Cells;
        Cells = Fill(columns, TermColor.Default);
        Array.Copy(old, Cells, Math.Min(old.Length, columns));
        // A wide character cut in half at the new edge is cleared.
        if (columns > 0 && Cells[columns - 1].Width == 2) Cells[columns - 1] = Cell.Blank(Cells[columns - 1].Bg);
    }
}

/// <summary>
/// An xterm-compatible terminal emulator for the SSH tab (docs/network-shares.md §6.3): the screen, the scrollback and
/// the modes a program on the server sets with escape sequences. Everything it reads comes from the server and is
/// untrusted – every count, position and string length is capped, and nothing it reads can reach this PC's clipboard,
/// files or window title. Its few answers (device status, cursor position) go out through <see cref="Reply"/>.
/// <para>
/// Not thread-safe by itself: the SSH session feeds it and the view draws it under <see cref="SyncRoot"/>.
/// </para>
/// </summary>
public sealed partial class VtTerminal
{
    public const int MaxColumns = 1000, MaxRows = 500;
    public const int DefaultScrollback = 5000;

    public object SyncRoot { get; } = new();

    private TermLine[] _main, _alt;
    private TermLine[] _screen;
    private readonly TermLine?[] _scrollback;
    private int _scrollbackStart, _scrollbackCount;

    public int Columns { get; private set; }
    public int Rows { get; private set; }

    // ---- cursor and the attributes of new characters
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    private bool _wrapPending;
    private int _fg = TermColor.Default, _bg = TermColor.Default;
    private CellFlags _flags;
    private int _lastRune = ' ';

    // ---- modes
    public bool CursorVisible { get; private set; } = true;
    public bool AppCursorKeys { get; private set; }
    public bool AppKeypad { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool AltScreen => ReferenceEquals(_screen, _alt);
    public MouseTracking Mouse { get; private set; }
    public bool SgrMouse { get; private set; }
    private bool _autowrap = true, _originMode, _insertMode, _newLineMode;
    private int _top, _bottom;
    private bool[] _tabs;

    // ---- character sets: G0/G1, each ASCII or DEC line drawing; SO/SI pick one
    private readonly bool[] _lineDrawing = new bool[2];
    private int _charset;

    private Saved _saved, _savedAlt;

    private record struct Saved(int X, int Y, int Fg, int Bg, CellFlags Flags, bool Origin, bool WrapPending, bool G0, bool G1, int Charset, bool Valid);

    /// <summary>Answers to the server (device attributes, status, cursor position) – written to the SSH channel.</summary>
    public event Action<string>? Reply;

    /// <summary>Goes up with every change; the view redraws when it moved.</summary>
    public long Version { get; private set; }

    /// <summary>Lines that have gone into the scrollback so far – the view keeps a scrolled-back position steady with it.</summary>
    public long ScrolledOff { get; private set; }

    public VtTerminal(int columns = 80, int rows = 24, int scrollback = DefaultScrollback)
    {
        Columns = Math.Clamp(columns, 2, MaxColumns);
        Rows = Math.Clamp(rows, 1, MaxRows);
        _scrollback = new TermLine?[Math.Max(0, scrollback)];
        _main = NewScreen(Columns, Rows);
        _alt = NewScreen(Columns, Rows);
        _screen = _main;
        _tabs = DefaultTabs(Columns);
        _top = 0;
        _bottom = Rows - 1;
    }

    private static TermLine[] NewScreen(int columns, int rows)
    {
        var s = new TermLine[rows];
        for (var i = 0; i < rows; i++) s[i] = new TermLine(columns, TermColor.Default);
        return s;
    }

    private static bool[] DefaultTabs(int columns)
    {
        var t = new bool[columns];
        for (var i = 8; i < columns; i += 8) t[i] = true;
        return t;
    }

    // ------------------------------------------------------------------ reading the screen

    public int ScrollbackCount => _scrollbackCount;

    /// <summary>A row of the screen (0 … Rows-1), or of the scrollback (-1 is the newest line that scrolled off).</summary>
    public TermLine Line(int index)
    {
        if (index >= 0) return _screen[index];
        var i = _scrollbackCount + index;
        if (i < 0) throw new ArgumentOutOfRangeException(nameof(index));
        return _scrollback[(_scrollbackStart + i) % _scrollback.Length]!;
    }

    /// <summary>The text from (line, column) to (line, column), both included; wrapped rows are joined, others end with a line break.</summary>
    public string Text(int fromLine, int fromColumn, int toLine, int toColumn)
    {
        if (fromLine > toLine || (fromLine == toLine && fromColumn > toColumn))
            (fromLine, fromColumn, toLine, toColumn) = (toLine, toColumn, fromLine, fromColumn);
        fromLine = Math.Max(fromLine, -_scrollbackCount);
        toLine = Math.Min(toLine, Rows - 1);
        var sb = new StringBuilder();
        for (var l = fromLine; l <= toLine; l++)
        {
            var line = Line(l);
            var start = l == fromLine ? Math.Max(0, fromColumn) : 0;
            var end = l == toLine ? Math.Min(line.Cells.Length - 1, toColumn) : line.Cells.Length - 1;
            var row = new StringBuilder();
            for (var c = start; c <= end; c++)
            {
                var cell = line.Cells[c];
                if (cell.Width == 0) continue;
                row.Append(cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune));
            }
            // Trailing blanks of a row aren't text, unless the row goes on in the next one.
            var text = line.Wrapped && l != toLine ? row.ToString() : row.ToString().TrimEnd(' ');
            sb.Append(text);
            if (l != toLine && !line.Wrapped) sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Everything on the screen, one line per row without trailing blanks or empty rows at the end (screen readers, the lab).</summary>
    public string ScreenText() => Text(0, 0, Rows - 1, Columns - 1).TrimEnd('\r', '\n');

    // ------------------------------------------------------------------ size

    /// <summary>A new size (the tab was resized). Rows that no longer fit above the cursor go into the scrollback.</summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 2, MaxColumns);
        rows = Math.Clamp(rows, 1, MaxRows);
        if (columns == Columns && rows == Rows) return;
        var onAlt = AltScreen;
        _main = Resized(_main, columns, rows, toScrollback: true, adjustCursor: !onAlt);
        _alt = Resized(_alt, columns, rows, toScrollback: false, adjustCursor: onAlt);
        _screen = onAlt ? _alt : _main;
        Columns = columns;
        Rows = rows;
        var tabs = DefaultTabs(columns);
        Array.Copy(_tabs, tabs, Math.Min(_tabs.Length, columns));
        _tabs = tabs;
        _top = 0;
        _bottom = rows - 1;
        CursorX = Math.Min(CursorX, columns - 1);
        CursorY = Math.Clamp(CursorY, 0, rows - 1);
        _wrapPending = false;
        Version++;
    }

    private TermLine[] Resized(TermLine[] screen, int columns, int rows, bool toScrollback, bool adjustCursor)
    {
        var lines = new List<TermLine>(screen);
        // Shrinking: rows from the top leave first while the cursor would fall off the bottom, then empty rows below it.
        var cursor = adjustCursor ? CursorY : 0;
        while (lines.Count > rows && cursor > rows - 1)
        {
            if (toScrollback) PushScrollback(lines[0]);
            lines.RemoveAt(0);
            cursor--;
        }
        if (lines.Count > rows) lines.RemoveRange(rows, lines.Count - rows);
        while (lines.Count < rows) lines.Add(new TermLine(columns, TermColor.Default));
        foreach (var l in lines) l.Resize(columns);
        if (adjustCursor) CursorY = cursor;
        return [.. lines];
    }

    private void PushScrollback(TermLine line)
    {
        if (_scrollback.Length == 0) return;
        if (_scrollbackCount < _scrollback.Length)
        {
            _scrollback[(_scrollbackStart + _scrollbackCount) % _scrollback.Length] = line;
            _scrollbackCount++;
        }
        else
        {
            _scrollback[_scrollbackStart] = line;
            _scrollbackStart = (_scrollbackStart + 1) % _scrollback.Length;
        }
        ScrolledOff++;
    }

    /// <summary>Clear scrollback (the menu, and ED 3 from the server).</summary>
    public void ClearScrollback()
    {
        Array.Clear(_scrollback);
        _scrollbackStart = _scrollbackCount = 0;
        Version++;
    }

    // ------------------------------------------------------------------ printing

    private void Print(int rune)
    {
        var width = RuneWidth(rune);
        if (width == 0) return; // combining marks and other zero-width characters aren't kept
        if (_lineDrawing[_charset] && rune is >= 0x60 and <= 0x7E) rune = LineDrawing[rune - 0x60];
        if (_wrapPending && _autowrap)
        {
            _screen[CursorY].Wrapped = true;
            CursorX = 0;
            LineFeed();
        }
        _wrapPending = false;
        if (width == 2 && CursorX == Columns - 1)
        {
            // No room for both halves: the character goes onto the next row (or, without autowrap, isn't shown).
            if (!_autowrap) return;
            Put(CursorY, CursorX, Cell.Blank(_bg));
            _screen[CursorY].Wrapped = true;
            CursorX = 0;
            LineFeed();
        }
        var cells = _screen[CursorY].Cells;
        if (_insertMode) ShiftRight(cells, CursorX, width);
        Put(CursorY, CursorX, new Cell { Rune = rune, Fg = _fg, Bg = _bg, Flags = _flags, Width = (byte)width });
        if (width == 2) Put(CursorY, CursorX + 1, new Cell { Rune = 0, Fg = _fg, Bg = _bg, Flags = _flags, Width = 0 });
        _lastRune = rune;
        if (CursorX + width >= Columns)
        {
            CursorX = Columns - 1;
            _wrapPending = _autowrap;
        }
        else CursorX += width;
    }

    /// <summary>Writes a cell, clearing the other half of a wide character it overwrites.</summary>
    private void Put(int row, int col, Cell cell)
    {
        var cells = _screen[row].Cells;
        if (col < 0 || col >= cells.Length) return;
        var old = cells[col];
        if (old.Width == 0 && col > 0 && cells[col - 1].Width == 2) cells[col - 1] = Cell.Blank(cells[col - 1].Bg);
        if (old.Width == 2 && cell.Width != 2 && col + 1 < cells.Length) cells[col + 1] = Cell.Blank(cells[col + 1].Bg);
        cells[col] = cell;
    }

    private void ShiftRight(Cell[] cells, int from, int n)
    {
        n = Math.Min(n, cells.Length - from);
        if (n <= 0) return;
        Array.Copy(cells, from, cells, from + n, cells.Length - from - n);
        for (var i = from; i < from + n; i++) cells[i] = Cell.Blank(_bg);
    }

    /// <summary>Columns a character takes: 0 for combining and format characters, 2 for East Asian wide ones and emoji.</summary>
    internal static int RuneWidth(int rune)
    {
        if (rune < 0x300) return 1;
        var category = CharUnicodeInfo.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        return rune is (>= 0x1100 and <= 0x115F) or (>= 0x2E80 and <= 0x303E) or (>= 0x3041 and <= 0x33FF) or (>= 0x3400 and <= 0x4DBF)
            or (>= 0x4E00 and <= 0x9FFF) or (>= 0xA000 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7A3) or (>= 0xF900 and <= 0xFAFF)
            or (>= 0xFE30 and <= 0xFE4F) or (>= 0xFF00 and <= 0xFF60) or (>= 0xFFE0 and <= 0xFFE6) or (>= 0x1F300 and <= 0x1F64F)
            or (>= 0x1F900 and <= 0x1F9FF) or (>= 0x20000 and <= 0x3FFFD)
            ? 2 : 1;
    }

    /// <summary>DEC special graphics for 0x60–0x7E (ESC ( 0): diamonds, shading and the box-drawing lines.</summary>
    private static readonly int[] LineDrawing =
    [
        0x25C6, 0x2592, 0x2409, 0x240C, 0x240D, 0x240A, 0x00B0, 0x00B1, 0x2424, 0x240B, 0x2518, 0x2510, 0x250C, 0x2514, 0x253C, 0x23BA,
        0x23BB, 0x2500, 0x23BC, 0x23BD, 0x251C, 0x2524, 0x2534, 0x252C, 0x2502, 0x2264, 0x2265, 0x03C0, 0x2260, 0x00A3, 0x00B7,
    ];

    // ------------------------------------------------------------------ cursor movement and scrolling

    private void LineFeed()
    {
        if (CursorY == _bottom) ScrollUp(1);
        else if (CursorY < Rows - 1) CursorY++;
    }

    private void ReverseIndex()
    {
        _wrapPending = false;
        if (CursorY == _top) ScrollDown(1);
        else if (CursorY > 0) CursorY--;
    }

    /// <summary>Scrolls the region up; on the main screen with the region at the top, the rows go into the scrollback.</summary>
    private void ScrollUp(int n)
    {
        n = Math.Clamp(n, 0, _bottom - _top + 1);
        for (var i = 0; i < n; i++)
        {
            var line = _screen[_top];
            if (_top == 0 && !AltScreen) PushScrollback(line);
            Array.Copy(_screen, _top + 1, _screen, _top, _bottom - _top);
            _screen[_bottom] = new TermLine(Columns, _bg);
        }
    }

    private void ScrollDown(int n)
    {
        n = Math.Clamp(n, 0, _bottom - _top + 1);
        for (var i = 0; i < n; i++)
        {
            Array.Copy(_screen, _top, _screen, _top + 1, _bottom - _top);
            _screen[_top] = new TermLine(Columns, _bg);
        }
    }

    private void MoveTo(int x, int y)
    {
        _wrapPending = false;
        CursorX = Math.Clamp(x, 0, Columns - 1);
        CursorY = _originMode ? Math.Clamp(y + _top, _top, _bottom) : Math.Clamp(y, 0, Rows - 1);
    }

    /// <summary>Vertical moves stop at the region's edges when they start inside it, otherwise at the screen's.</summary>
    private void MoveVertical(int dy)
    {
        _wrapPending = false;
        var (min, max) = CursorY >= _top && CursorY <= _bottom ? (_top, _bottom) : (0, Rows - 1);
        CursorY = Math.Clamp(CursorY + dy, min, max);
    }

    private void Tab(int count)
    {
        _wrapPending = false;
        for (var i = 0; i < count && CursorX < Columns - 1; i++)
        {
            var x = CursorX + 1;
            while (x < Columns - 1 && !_tabs[x]) x++;
            CursorX = x;
        }
    }

    private void BackTab(int count)
    {
        _wrapPending = false;
        for (var i = 0; i < count && CursorX > 0; i++)
        {
            var x = CursorX - 1;
            while (x > 0 && !_tabs[x]) x--;
            CursorX = x;
        }
    }

    // ------------------------------------------------------------------ erasing, inserting, deleting

    private void EraseCells(int row, int from, int to)
    {
        var cells = _screen[row].Cells;
        from = Math.Max(0, from);
        to = Math.Min(cells.Length - 1, to);
        for (var c = from; c <= to; c++) cells[c] = Cell.Blank(_bg);
        if (to == cells.Length - 1) _screen[row].Wrapped = false;
    }

    private void EraseDisplay(int mode)
    {
        _wrapPending = false;
        switch (mode)
        {
            case 0:
                EraseCells(CursorY, CursorX, Columns - 1);
                for (var r = CursorY + 1; r < Rows; r++) EraseCells(r, 0, Columns - 1);
                break;
            case 1:
                for (var r = 0; r < CursorY; r++) EraseCells(r, 0, Columns - 1);
                EraseCells(CursorY, 0, CursorX);
                break;
            case 2:
                for (var r = 0; r < Rows; r++) EraseCells(r, 0, Columns - 1);
                break;
            case 3:
                ClearScrollback();
                break;
        }
    }

    private void EraseLine(int mode)
    {
        _wrapPending = false;
        switch (mode)
        {
            case 0: EraseCells(CursorY, CursorX, Columns - 1); break;
            case 1: EraseCells(CursorY, 0, CursorX); break;
            case 2: EraseCells(CursorY, 0, Columns - 1); break;
        }
    }

    private void InsertChars(int n)
    {
        _wrapPending = false;
        ShiftRight(_screen[CursorY].Cells, CursorX, Math.Clamp(n, 1, Columns));
    }

    private void DeleteChars(int n)
    {
        _wrapPending = false;
        var cells = _screen[CursorY].Cells;
        n = Math.Clamp(n, 1, Columns - CursorX);
        Array.Copy(cells, CursorX + n, cells, CursorX, Columns - CursorX - n);
        for (var c = Columns - n; c < Columns; c++) cells[c] = Cell.Blank(_bg);
    }

    private void EraseChars(int n) => EraseCells(CursorY, CursorX, CursorX + Math.Clamp(n, 1, Columns) - 1);

    private void InsertLines(int n)
    {
        if (CursorY < _top || CursorY > _bottom) return;
        var top = _top;
        _top = CursorY;
        ScrollDown(Math.Clamp(n, 1, Rows));
        _top = top;
        CursorX = 0;
        _wrapPending = false;
    }

    private void DeleteLines(int n)
    {
        if (CursorY < _top || CursorY > _bottom) return;
        var top = _top;
        _top = CursorY;
        // Deleted rows don't go into the scrollback, even with the region at the top.
        n = Math.Clamp(n, 1, _bottom - _top + 1);
        for (var i = 0; i < n; i++)
        {
            Array.Copy(_screen, _top + 1, _screen, _top, _bottom - _top);
            _screen[_bottom] = new TermLine(Columns, _bg);
        }
        _top = top;
        CursorX = 0;
        _wrapPending = false;
    }

    private void Repeat(int n)
    {
        // Capped at one screen: a huge count from the server would otherwise keep this busy for a long time.
        n = Math.Clamp(n, 1, Columns * Rows);
        for (var i = 0; i < n; i++) Print(_lastRune);
    }

    // ------------------------------------------------------------------ modes and state

    private void SaveCursor()
    {
        var s = new Saved(CursorX, CursorY, _fg, _bg, _flags, _originMode, _wrapPending, _lineDrawing[0], _lineDrawing[1], _charset, true);
        if (AltScreen) _savedAlt = s; else _saved = s;
    }

    private void RestoreCursor()
    {
        var s = AltScreen ? _savedAlt : _saved;
        if (!s.Valid) { MoveTo(0, 0); return; }
        CursorX = Math.Min(s.X, Columns - 1);
        CursorY = Math.Min(s.Y, Rows - 1);
        (_fg, _bg, _flags, _originMode, _wrapPending) = (s.Fg, s.Bg, s.Flags, s.Origin, s.WrapPending);
        (_lineDrawing[0], _lineDrawing[1], _charset) = (s.G0, s.G1, s.Charset);
    }

    private void UseAltScreen(bool on, bool saveCursor, bool clear)
    {
        if (on == AltScreen) return;
        if (on)
        {
            if (saveCursor) SaveCursor();
            _screen = _alt;
            if (clear) for (var r = 0; r < Rows; r++) _alt[r] = new TermLine(Columns, TermColor.Default);
        }
        else
        {
            if (clear) for (var r = 0; r < Rows; r++) _alt[r] = new TermLine(Columns, TermColor.Default);
            _screen = _main;
            if (saveCursor) RestoreCursor();
        }
        _wrapPending = false;
    }

    private void SetPrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: AppCursorKeys = on; break;
            case 6: _originMode = on; MoveTo(0, 0); break;
            case 7: _autowrap = on; if (!on) _wrapPending = false; break;
            case 25: CursorVisible = on; break;
            case 47: UseAltScreen(on, saveCursor: false, clear: false); break;
            case 1047: UseAltScreen(on, saveCursor: false, clear: true); break;
            case 1048: if (on) SaveCursor(); else RestoreCursor(); break;
            case 1049: UseAltScreen(on, saveCursor: true, clear: true); break;
            case 1000: Mouse = on ? MouseTracking.Click : MouseTracking.None; break;
            case 1002: Mouse = on ? MouseTracking.Drag : MouseTracking.None; break;
            case 1003: Mouse = on ? MouseTracking.Any : MouseTracking.None; break;
            case 1006: SgrMouse = on; break;
            case 2004: BracketedPaste = on; break;
            // Everything else (132 columns, blinking cursor, focus events …) is accepted and ignored.
        }
    }

    private void SetMode(int mode, bool on)
    {
        if (mode == 4) _insertMode = on;
        else if (mode == 20) _newLineMode = on;
    }

    private void SetScrollRegion(int top, int bottom)
    {
        top = Math.Clamp(top, 1, Rows) - 1;
        bottom = (bottom <= 0 ? Rows : Math.Clamp(bottom, 1, Rows)) - 1;
        if (top >= bottom) return;
        (_top, _bottom) = (top, bottom);
        MoveTo(0, 0);
    }

    /// <summary>RIS: everything as after connecting (the scrollback stays).</summary>
    private void Reset()
    {
        _main = NewScreen(Columns, Rows);
        _alt = NewScreen(Columns, Rows);
        _screen = _main;
        SoftReset();
        CursorX = CursorY = 0;
        _tabs = DefaultTabs(Columns);
        _saved = _savedAlt = default;
        Mouse = MouseTracking.None;
        SgrMouse = BracketedPaste = false;
        _newLineMode = false;
    }

    /// <summary>DECSTR: modes and attributes back to their defaults, the screen kept.</summary>
    private void SoftReset()
    {
        CursorVisible = _autowrap = true;
        AppCursorKeys = AppKeypad = _originMode = _insertMode = _wrapPending = false;
        _fg = _bg = TermColor.Default;
        _flags = CellFlags.None;
        _top = 0;
        _bottom = Rows - 1;
        _lineDrawing[0] = _lineDrawing[1] = false;
        _charset = 0;
    }

    private void Answer(string text) => Reply?.Invoke(text);
}
