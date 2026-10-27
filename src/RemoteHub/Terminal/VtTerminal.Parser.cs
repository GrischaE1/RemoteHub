using System.Text;

namespace RemoteHub.Terminal;

/// <summary>
/// The escape sequence parser (after the DEC/xterm state machine). Bounded on purpose: at most <see cref="MaxParams"/>
/// parameters, each capped at <see cref="MaxParamValue"/>, the first intermediate character; OSC, DCS, SOS, PM and APC
/// strings are read and dropped without being stored, however long they are.
/// </summary>
public sealed partial class VtTerminal
{
    internal const int MaxParams = 32, MaxParamValue = 65535;

    private enum State { Ground, Escape, EscapeIntermediate, CsiEntry, CsiParam, CsiIntermediate, CsiIgnore, Osc, String }

    private State _state;
    private readonly int[] _params = new int[MaxParams];
    private readonly bool[] _sub = new bool[MaxParams]; // introduced by ':' (SGR 38:2:r:g:b)
    private int _paramCount;
    private bool _paramStarted;
    private char _private;          // '?', '>', '<' or '=' right after CSI
    private char _inter1;           // the first intermediate character (0x20–0x2F)
    private int _interCount;
    private char _highSurrogate;
    private readonly Decoder _utf8 = new UTF8Encoding(false, false).GetDecoder();
    private char[] _chars = new char[4096];

    /// <summary>Bytes from the server (UTF-8, possibly split anywhere).</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        var max = _utf8.GetCharCount(data, flush: false);
        if (_chars.Length < max) _chars = new char[Math.Max(max, _chars.Length * 2)];
        var n = _utf8.GetChars(data, _chars, flush: false);
        Feed(_chars.AsSpan(0, n));
    }

    public void Feed(string text) => Feed(text.AsSpan());

    public void Feed(ReadOnlySpan<char> text)
    {
        foreach (var ch in text) Next(ch);
        Version++;
    }

    private void Next(char ch)
    {
        // CAN and SUB abort any sequence; ESC starts a new one from anywhere (and ends an OSC or DCS string).
        if (ch is '\x18' or '\x1A') { _state = State.Ground; return; }
        if (ch == '\x1B')
        {
            _state = State.Escape;
            _interCount = 0;
            return;
        }
        switch (_state)
        {
            case State.Ground:
                if (ch < 0x20) Execute(ch);
                else if (ch == 0x7F) { }
                else PrintChar(ch);
                break;

            case State.Escape:
                if (ch < 0x20) Execute(ch);
                else if (ch <= 0x2F) { Intermediate(ch); _state = State.EscapeIntermediate; }
                else if (ch == '[') { StartCsi(); _state = State.CsiEntry; }
                // OSC (title, colours, clipboard …), DCS, SOS, PM and APC: read and dropped. Nothing they ask for is done –
                // above all no clipboard (OSC 52) and no title, which the server could make say anything.
                else if (ch == ']') _state = State.Osc;
                else if (ch is 'P' or 'X' or '^' or '_') _state = State.String;
                else { EscDispatch(ch); _state = State.Ground; }
                break;

            case State.EscapeIntermediate:
                if (ch < 0x20) Execute(ch);
                else if (ch <= 0x2F) Intermediate(ch);
                else { EscDispatch(ch); _state = State.Ground; }
                break;

            case State.CsiEntry:
            case State.CsiParam:
                if (ch < 0x20) Execute(ch);
                else if (ch is >= '0' and <= '9') Digit(ch);
                else if (ch is ';' or ':') Separator(ch == ':');
                else if (ch is '?' or '>' or '<' or '=')
                {
                    if (_state == State.CsiEntry) { _private = ch; _state = State.CsiParam; }
                    else _state = State.CsiIgnore;
                }
                else if (ch <= 0x2F) { Intermediate(ch); _state = State.CsiIntermediate; }
                else if (ch <= 0x7E) { CsiDispatch(ch); _state = State.Ground; }
                break;

            case State.CsiIntermediate:
                if (ch < 0x20) Execute(ch);
                else if (ch <= 0x2F) Intermediate(ch);
                else if (ch <= 0x3F) _state = State.CsiIgnore;
                else if (ch <= 0x7E) { CsiDispatch(ch); _state = State.Ground; }
                break;

            case State.CsiIgnore:
                if (ch < 0x20) Execute(ch);
                else if (ch is >= (char)0x40 and <= (char)0x7E) _state = State.Ground;
                break;

            case State.Osc:
                if (ch == '\x07') _state = State.Ground; // BEL ends an OSC as well as ST (ESC \)
                break;

            case State.String:
                break; // until ESC \ (or CAN/SUB)
        }
    }

    private void PrintChar(char ch)
    {
        if (char.IsHighSurrogate(ch)) { _highSurrogate = ch; return; }
        if (char.IsLowSurrogate(ch))
        {
            if (_highSurrogate != 0) Print(char.ConvertToUtf32(_highSurrogate, ch));
            _highSurrogate = '\0';
            return;
        }
        _highSurrogate = '\0';
        // C1 controls (U+0080–U+009F) arrive as UTF-8 characters; like xterm in UTF-8 mode they aren't controls – nor text.
        if (ch is >= '\x80' and <= '\x9F') return;
        Print(ch);
    }

    private void Execute(char ch)
    {
        switch (ch)
        {
            case '\b':
                _wrapPending = false;
                if (CursorX > 0) CursorX--;
                break;
            case '\t': Tab(1); break;
            case '\n' or '\v' or '\f':
                _wrapPending = false;
                LineFeed();
                if (_newLineMode) CursorX = 0;
                break;
            case '\r':
                _wrapPending = false;
                CursorX = 0;
                break;
            case '\x0E': _charset = 1; break; // SO: G1
            case '\x0F': _charset = 0; break; // SI: G0
            // BEL and the rest: nothing to do.
        }
    }

    private void Intermediate(char ch)
    {
        if (_interCount == 0) _inter1 = ch;
        _interCount = Math.Min(_interCount + 1, 3);
    }

    private void StartCsi()
    {
        _paramCount = 0;
        _paramStarted = false;
        _private = '\0';
        _interCount = 0;
        Array.Clear(_params);
        Array.Clear(_sub);
    }

    private void Digit(char ch)
    {
        _state = State.CsiParam;
        if (!_paramStarted)
        {
            if (_paramCount >= MaxParams) return; // further parameters are dropped
            _paramCount++;
            _paramStarted = true;
        }
        ref var p = ref _params[_paramCount - 1];
        p = Math.Min(p * 10 + (ch - '0'), MaxParamValue);
    }

    private void Separator(bool colon)
    {
        _state = State.CsiParam;
        // An empty parameter before the separator counts (as 0): "CSI ;5H" is row 1 (default), column 5.
        if (!_paramStarted && _paramCount < MaxParams) _paramCount++;
        _paramStarted = false;
        if (_paramCount < MaxParams)
        {
            _paramCount++;
            _sub[_paramCount - 1] = colon;
            _paramStarted = true; // the next digits fill this one
        }
    }

    /// <summary>Parameter <paramref name="i"/>, with 0 or a missing one read as <paramref name="def"/>.</summary>
    private int Arg(int i, int def = 1) => i < _paramCount && _params[i] > 0 ? _params[i] : def;

    /// <summary>Parameter <paramref name="i"/> as sent (missing: 0).</summary>
    private int Raw(int i) => i < _paramCount ? _params[i] : 0;

    private void EscDispatch(char ch)
    {
        if (_interCount > 0)
        {
            // Character sets: ESC ( 0 is line drawing for G0, ESC ) 0 for G1; anything else is ASCII.
            if (_inter1 is '(' or ')') _lineDrawing[_inter1 == '(' ? 0 : 1] = ch == '0';
            else if (_inter1 == '#' && ch == '8') AlignmentPattern();
            _interCount = 0;
            return;
        }
        switch (ch)
        {
            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case 'D': _wrapPending = false; LineFeed(); break;
            case 'E': _wrapPending = false; CursorX = 0; LineFeed(); break;
            case 'H': _tabs[CursorX] = true; break;
            case 'M': ReverseIndex(); break;
            case 'c': Reset(); break;
            case '=': AppKeypad = true; break;
            case '>': AppKeypad = false; break;
        }
    }

    /// <summary>DECALN: the screen filled with "E" (a test pattern).</summary>
    private void AlignmentPattern()
    {
        for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Columns; c++) _screen[r].Cells[c] = new Cell { Rune = 'E', Fg = TermColor.Default, Bg = TermColor.Default, Width = 1 };
        _top = 0;
        _bottom = Rows - 1;
        MoveTo(0, 0);
    }

    private void CsiDispatch(char final)
    {
        if (_interCount > 0)
        {
            // CSI ! p (DECSTR); everything else with an intermediate (cursor shape, DECRQM …) is ignored.
            if (_inter1 == '!' && final == 'p') SoftReset();
            return;
        }
        if (_private == '?')
        {
            if (final is 'h' or 'l')
                for (var i = 0; i < _paramCount; i++) SetPrivateMode(_params[i], final == 'h');
            return;
        }
        if (_private == '>')
        {
            if (final == 'c') Answer("\x1b[>0;10;1c"); // secondary device attributes: a VT100-class terminal
            return;
        }
        if (_private != '\0') return;

        switch (final)
        {
            case '@': InsertChars(Arg(0)); break;
            case 'A': MoveVertical(-Arg(0)); break;
            case 'B' or 'e': MoveVertical(Arg(0)); break;
            case 'C' or 'a': _wrapPending = false; CursorX = Math.Min(Columns - 1, CursorX + Arg(0)); break;
            case 'D': _wrapPending = false; CursorX = Math.Max(0, CursorX - Arg(0)); break;
            case 'E': MoveVertical(Arg(0)); CursorX = 0; break;
            case 'F': MoveVertical(-Arg(0)); CursorX = 0; break;
            case 'G' or '`': _wrapPending = false; CursorX = Math.Clamp(Arg(0) - 1, 0, Columns - 1); break;
            case 'H' or 'f': MoveTo(Arg(1) - 1, Arg(0) - 1); break;
            case 'I': Tab(Arg(0)); break;
            case 'J': EraseDisplay(Raw(0)); break;
            case 'K': EraseLine(Raw(0)); break;
            case 'L': InsertLines(Arg(0)); break;
            case 'M': DeleteLines(Arg(0)); break;
            case 'P': DeleteChars(Arg(0)); break;
            case 'S': ScrollUp(Arg(0)); break;
            case 'T': if (_paramCount <= 1) ScrollDown(Arg(0)); break; // with more parameters it's mouse tracking (ignored)
            case 'X': EraseChars(Arg(0)); break;
            case 'Z': BackTab(Arg(0)); break;
            case 'b': Repeat(Arg(0)); break;
            case 'c': if (Raw(0) == 0) Answer("\x1b[?1;2c"); break; // primary device attributes: VT100 with advanced video
            case 'd': _wrapPending = false; CursorY = _originMode ? Math.Clamp(Arg(0) - 1 + _top, _top, _bottom) : Math.Clamp(Arg(0) - 1, 0, Rows - 1); break;
            case 'g':
                if (Raw(0) == 0) _tabs[CursorX] = false;
                else if (Raw(0) == 3) Array.Clear(_tabs);
                break;
            case 'h' or 'l':
                for (var i = 0; i < _paramCount; i++) SetMode(_params[i], final == 'h');
                break;
            case 'm': Sgr(); break;
            case 'n':
                if (Raw(0) == 5) Answer("\x1b[0n");
                else if (Raw(0) == 6) Answer($"\x1b[{(_originMode ? CursorY - _top : CursorY) + 1};{CursorX + 1}R");
                break;
            case 'r': SetScrollRegion(Arg(0), Raw(1)); break;
            case 's': if (_paramCount == 0) SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            // 't' (window operations and reports such as the title) is never answered: a server must not read this PC's state.
        }
    }

    private void Sgr()
    {
        if (_paramCount == 0) { ResetAttributes(); return; }
        for (var i = 0; i < _paramCount; i++)
        {
            var p = _params[i];
            switch (p)
            {
                case 0: ResetAttributes(); break;
                case 1: _flags |= CellFlags.Bold; break;
                case 2: _flags |= CellFlags.Faint; break;
                case 3: _flags |= CellFlags.Italic; break;
                case 4:
                    // 4:0 is "no underline" in the colon form; any other style is a plain underline here.
                    if (i + 1 < _paramCount && _sub[i + 1]) { if (_params[++i] == 0) _flags &= ~CellFlags.Underline; else _flags |= CellFlags.Underline; }
                    else _flags |= CellFlags.Underline;
                    break;
                case 5 or 6: _flags |= CellFlags.Blink; break;
                case 7: _flags |= CellFlags.Inverse; break;
                case 8: _flags |= CellFlags.Hidden; break;
                case 9: _flags |= CellFlags.Strike; break;
                case 21: _flags |= CellFlags.Underline; break;
                case 22: _flags &= ~(CellFlags.Bold | CellFlags.Faint); break;
                case 23: _flags &= ~CellFlags.Italic; break;
                case 24: _flags &= ~CellFlags.Underline; break;
                case 25: _flags &= ~CellFlags.Blink; break;
                case 27: _flags &= ~CellFlags.Inverse; break;
                case 28: _flags &= ~CellFlags.Hidden; break;
                case 29: _flags &= ~CellFlags.Strike; break;
                case >= 30 and <= 37: _fg = p - 30; break;
                case 38: _fg = ExtendedColour(ref i, _fg); break;
                case 39: _fg = TermColor.Default; break;
                case >= 40 and <= 47: _bg = p - 40; break;
                case 48: _bg = ExtendedColour(ref i, _bg); break;
                case 49: _bg = TermColor.Default; break;
                case 58: ExtendedColour(ref i, 0); break; // underline colour: read and dropped
                case >= 90 and <= 97: _fg = p - 90 + 8; break;
                case >= 100 and <= 107: _bg = p - 100 + 8; break;
            }
        }
    }

    private void ResetAttributes()
    {
        _fg = _bg = TermColor.Default;
        _flags = CellFlags.None;
    }

    /// <summary>38/48/58 followed by 5;n (palette) or 2;r;g;b – also the colon forms 38:5:n, 38:2:r:g:b and 38:2:cs:r:g:b.</summary>
    private int ExtendedColour(ref int i, int current)
    {
        if (i + 1 >= _paramCount) return current;
        var colon = _sub[i + 1];
        // In the colon form only the sub-parameters belong to this colour.
        var end = i + 1;
        if (colon) while (end + 1 < _paramCount && _sub[end + 1]) end++;
        var kind = _params[i + 1];
        int result;
        if (kind == 5 && (colon ? end >= i + 2 : i + 2 < _paramCount))
        {
            result = Math.Clamp(_params[i + 2], 0, 255);
            if (!colon) end = i + 2;
        }
        else if (kind == 2)
        {
            int first;
            if (colon) first = end - (i + 1) >= 4 ? i + 3 : i + 2; // four values after the 2: a colour space id comes first
            else { first = i + 2; end = i + 4; }
            if (first + 2 > Math.Min(end, _paramCount - 1)) { i = Math.Min(end, _paramCount - 1); return current; }
            result = TermColor.Rgb(_params[first], _params[first + 1], _params[first + 2]);
        }
        else { i = colon ? end : i + 1; return current; }
        i = Math.Min(end, _paramCount - 1);
        return result;
    }
}
