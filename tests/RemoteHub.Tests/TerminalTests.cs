using System.Collections.Generic;
using System.Text;
using System.Windows.Input;
using RemoteHub.Terminal;

namespace RemoteHub.Tests;

/// <summary>The SSH tab's terminal emulator: printing, cursor and erase sequences, colours, modes, bounds on what the server sends, and the keys sent back.</summary>
public class TerminalTests
{
    private static VtTerminal Term(string output, int columns = 20, int rows = 5)
    {
        var t = new VtTerminal(columns, rows, scrollback: 100);
        t.Feed(output);
        return t;
    }

    private static string Row(VtTerminal t, int row) => t.Text(row, 0, row, t.Columns - 1);

    // ------------------------------------------------------------------ printing

    [Fact]
    public void Text_is_printed_and_wraps_at_the_right_edge()
    {
        var t = Term("abcdefghij" + "klmno", columns: 10);
        Assert.Equal("abcdefghij", Row(t, 0));
        Assert.Equal("klmno", Row(t, 1));
        Assert.True(t.Line(0).Wrapped);
        // Copying a wrapped row joins it with the next one.
        Assert.Equal("abcdefghijklmno", t.Text(0, 0, 1, 9));
    }

    [Fact]
    public void The_last_column_is_written_before_the_cursor_wraps()
    {
        var t = Term("0123456789", columns: 10);
        Assert.Equal((9, 0), (t.CursorX, t.CursorY)); // stays on the last column until the next character
        t.Feed("\r\n");
        Assert.Equal((0, 1), (t.CursorX, t.CursorY));
        Assert.False(t.Line(0).Wrapped);
    }

    [Fact]
    public void Lines_scrolled_off_the_top_go_into_the_scrollback()
    {
        var t = Term("1\r\n2\r\n3\r\n4\r\n5\r\n6\r\n7", rows: 5);
        Assert.Equal(2, t.ScrollbackCount);
        Assert.Equal("1", t.Text(-2, 0, -2, 19));
        Assert.Equal("2", t.Text(-1, 0, -1, 19));
        Assert.Equal("3\r\n4\r\n5\r\n6\r\n7", t.ScreenText());
        Assert.Equal(2, t.ScrolledOff);
    }

    [Fact]
    public void Utf8_split_between_reads_and_wide_characters_take_two_cells()
    {
        var t = new VtTerminal(10, 2);
        var bytes = Encoding.UTF8.GetBytes("é日x");
        t.Feed(bytes.AsSpan(0, 1));
        t.Feed(bytes.AsSpan(1, 3)); // the rest of "é" and part of "日"
        t.Feed(bytes.AsSpan(4));
        Assert.Equal("é日x", Row(t, 0));
        Assert.Equal(2, t.Line(0).Cells[1].Width);
        Assert.Equal(0, t.Line(0).Cells[2].Width);
        Assert.Equal(4, t.CursorX);
    }

    [Fact]
    public void Line_drawing_characters_replace_letters_while_selected()
    {
        var t = Term("\x1b(0lqk\x1b(Bq");
        Assert.Equal("┌─┐q", Row(t, 0));
    }

    // ------------------------------------------------------------------ cursor, erase, insert, delete

    [Fact]
    public void Cursor_position_erase_and_insert_work_as_in_xterm()
    {
        var t = Term("hello world\x1b[1;7H\x1b[K", columns: 20);
        Assert.Equal("hello", Row(t, 0));
        t.Feed("\x1b[1;1H\x1b[2@>>");
        Assert.Equal(">>hello", Row(t, 0));
        t.Feed("\x1b[1;1H\x1b[2P");
        Assert.Equal("hello", Row(t, 0));
        t.Feed("\x1b[2J\x1b[3;4Hx");
        Assert.Equal("", Row(t, 0));
        Assert.Equal("   x", Row(t, 2));
        // Missing or zero parameters count as 1; positions beyond the screen stop at its edge.
        t.Feed("\x1b[;H\x1b[0;0Hy\x1b[999;999Hz");
        Assert.Equal("y", Row(t, 0));
        Assert.Equal((19, 4), (t.CursorX, t.CursorY));
    }

    [Fact]
    public void A_scroll_region_scrolls_only_its_rows_and_keeps_them_out_of_the_scrollback()
    {
        var t = Term("top\r\na\r\nb\r\nc\r\nbottom", rows: 5);
        t.Feed("\x1b[2;4r\x1b[4;1H\n"); // region rows 2-4, a line feed on its last row
        Assert.Equal("top\r\nb\r\nc\r\n\r\nbottom", t.ScreenText());
        Assert.Equal(0, t.ScrollbackCount);
        t.Feed("\x1b[2;1H\x1bM"); // reverse index on its first row
        Assert.Equal("top\r\n\r\nb\r\nc\r\nbottom", t.ScreenText());
    }

    [Fact]
    public void Insert_and_delete_lines_and_repeat()
    {
        var t = Term("1\r\n2\r\n3", rows: 4);
        t.Feed("\x1b[2;1H\x1b[L");
        Assert.Equal("1\r\n\r\n2\r\n3", t.ScreenText());
        t.Feed("\x1b[M\x1b[M");
        Assert.Equal("1\r\n3", t.ScreenText());
        t.Feed("\x1b[4;1H-\x1b[4b");
        Assert.Equal("-----", Row(t, 3));
    }

    [Fact]
    public void The_alternate_screen_is_left_with_the_main_screen_and_cursor_as_they_were()
    {
        var t = Term("prompt$ ");
        t.Feed("\x1b[?1049h\x1b[2J\x1b[Hvim screen");
        Assert.True(t.AltScreen);
        Assert.Equal("vim screen", Row(t, 0));
        t.Feed("\x1b[?1049l");
        Assert.False(t.AltScreen);
        Assert.Equal("prompt$", Row(t, 0));
        Assert.Equal((8, 0), (t.CursorX, t.CursorY));
    }

    // ------------------------------------------------------------------ colours and attributes

    [Fact]
    public void Colours_in_every_form_and_attributes()
    {
        var t = Term("\x1b[1;31mA\x1b[0;38;5;208mB\x1b[38;2;10;20;30mC\x1b[38:2::1:2:3mD\x1b[48:5:17;4mE\x1b[24;7;39;49mF\x1b[mG");
        var c = t.Line(0).Cells;
        Assert.Equal((1, CellFlags.Bold), (c[0].Fg, c[0].Flags));
        Assert.Equal(208, c[1].Fg);
        Assert.Equal(TermColor.Rgb(10, 20, 30), c[2].Fg);
        Assert.Equal(TermColor.Rgb(1, 2, 3), c[3].Fg);
        Assert.Equal((17, CellFlags.Underline), (c[4].Bg, c[4].Flags));
        Assert.Equal((TermColor.Default, TermColor.Default, CellFlags.Inverse), (c[5].Fg, c[5].Bg, c[5].Flags));
        Assert.Equal((TermColor.Default, CellFlags.None), (c[6].Fg, c[6].Flags));
    }

    [Fact]
    public void Erasing_uses_the_current_background()
    {
        var t = Term("\x1b[44m\x1b[2J");
        Assert.All(t.Line(2).Cells, cell => Assert.Equal(4, cell.Bg));
    }

    // ------------------------------------------------------------------ modes and answers

    [Fact]
    public void Modes_the_view_and_the_keys_follow()
    {
        var t = Term("\x1b[?1h\x1b[?2004h\x1b[?25l\x1b[?1002h\x1b[?1006h");
        Assert.True(t.AppCursorKeys);
        Assert.True(t.BracketedPaste);
        Assert.False(t.CursorVisible);
        Assert.Equal(MouseTracking.Drag, t.Mouse);
        Assert.True(t.SgrMouse);
        t.Feed("\u001bc"); // reset ("\x1bc" would be one character, U+01BC)
        Assert.False(t.AppCursorKeys || t.BracketedPaste || t.SgrMouse);
        Assert.True(t.CursorVisible);
        Assert.Equal(MouseTracking.None, t.Mouse);
    }

    [Fact]
    public void Status_and_device_attributes_are_answered_and_nothing_else_is()
    {
        var t = new VtTerminal(80, 24);
        var replies = new List<string>();
        t.Reply += replies.Add;
        t.Feed("ab\x1b[6n\x1b[5n\x1b[c\x1b[>c");
        // Window reports (the title, the size in pixels) aren't answered: a server can't read this PC's state.
        t.Feed("\x1b[21t\x1b[14t\x1b[18t");
        Assert.Equal(["\x1b[1;3R", "\x1b[0n", "\x1b[?1;2c", "\x1b[>0;10;1c"], replies);
    }

    [Fact]
    public void Osc_strings_are_dropped_including_clipboard_writes()
    {
        var t = Term("\x1b]0;evil title\x07\x1b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes("rm -rf /")) + "\x1b\\ok");
        Assert.Equal("ok", Row(t, 0));
        var dcs = Term("\x1bPq#0;2;0;0;0#1\x1b\\after");
        Assert.Equal("after", Row(dcs, 0));
    }

    // ------------------------------------------------------------------ bounds: what the server sends is untrusted

    [Fact]
    public void Huge_counts_and_too_many_parameters_stay_within_the_screen()
    {
        var t = new VtTerminal(80, 24);
        t.Feed("\x1b[99999999999999999999A\x1b[4294967296C\x1b[" + string.Join(";", Enumerable.Repeat("1", 5000)) + "m");
        Assert.InRange(t.CursorX, 0, 79);
        Assert.InRange(t.CursorY, 0, 23);
        var started = DateTime.UtcNow;
        t.Feed("x\x1b[999999999b"); // repeat: capped at one screen
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
        t.Feed("\x1b[999999L\x1b[999999M\x1b[999999@\x1b[999999P\x1b[999999X\x1b[999999S\x1b[999999T\x1b[999999I\x1b[999999Z");
        Assert.InRange(t.CursorX, 0, 79);
        Assert.Equal(24, Enumerable.Range(0, 24).Count(r => t.Line(r).Cells.Length == 80));
    }

    [Fact]
    public void An_endless_osc_string_is_read_without_being_kept()
    {
        var t = new VtTerminal(80, 24);
        t.Feed("\x1b]0;" + new string('A', 2_000_000));
        t.Feed("\x07visible");
        Assert.Equal("visible", t.Text(0, 0, 0, 79));
    }

    [Fact]
    public void Random_output_never_breaks_the_terminal()
    {
        var random = new Random(4711);
        var t = new VtTerminal(40, 12, scrollback: 50);
        var chunk = new byte[4096];
        // Escape-heavy noise: random bytes with a good share of ESC, '[', digits, ';' and finals.
        const string pieces = "\x1b[\x1b]\x1bP\x1b(0\u001b7\u001b8\x1bM;?:0123456789mHJKLMPSTXbcdhlnrsu@`\r\n\b\t\x07\x18\x9b";
        for (var round = 0; round < 300; round++)
        {
            for (var i = 0; i < chunk.Length; i++)
                chunk[i] = random.Next(3) == 0 ? (byte)pieces[random.Next(pieces.Length)] : (byte)random.Next(256);
            t.Feed(chunk);
            if (round % 50 == 0) t.Resize(random.Next(2, 120), random.Next(1, 40));
            Assert.InRange(t.CursorX, 0, t.Columns - 1);
            Assert.InRange(t.CursorY, 0, t.Rows - 1);
        }
        _ = t.Text(-t.ScrollbackCount, 0, t.Rows - 1, t.Columns - 1);
    }

    [Fact]
    public void Resizing_keeps_the_cursors_line_on_screen()
    {
        var t = Term("1\r\n2\r\n3\r\n4\r\n5", columns: 10, rows: 5);
        t.Resize(10, 3);
        Assert.Equal("3\r\n4\r\n5", t.ScreenText());
        Assert.Equal(2, t.CursorY);
        Assert.Equal(2, t.ScrollbackCount);
        t.Resize(4, 3);
        Assert.Equal(4, t.Line(0).Cells.Length);
        t.Resize(100000, 0); // clamped
        Assert.Equal((VtTerminal.MaxColumns, 1), (t.Columns, t.Rows));
    }

    // ------------------------------------------------------------------ keys, paste and the mouse

    [Theory]
    [InlineData(Key.Up, ModifierKeys.None, false, "\x1b[A")]
    [InlineData(Key.Up, ModifierKeys.None, true, "\x1bOA")]
    [InlineData(Key.Left, ModifierKeys.Control, false, "\x1b[1;5D")]
    [InlineData(Key.Home, ModifierKeys.Shift, false, "\x1b[1;2H")]
    [InlineData(Key.Delete, ModifierKeys.None, false, "\x1b[3~")]
    [InlineData(Key.PageDown, ModifierKeys.Alt, false, "\x1b[6;3~")]
    [InlineData(Key.F1, ModifierKeys.None, false, "\x1bOP")]
    [InlineData(Key.F5, ModifierKeys.None, false, "\x1b[15~")]
    [InlineData(Key.F12, ModifierKeys.Control, false, "\x1b[24;5~")]
    [InlineData(Key.Enter, ModifierKeys.None, false, "\r")]
    [InlineData(Key.Back, ModifierKeys.None, false, "\x7f")]
    [InlineData(Key.Tab, ModifierKeys.Shift, false, "\x1b[Z")]
    [InlineData(Key.C, ModifierKeys.Control, false, "\x03")]
    [InlineData(Key.W, ModifierKeys.Control, false, "\x17")]
    [InlineData(Key.Space, ModifierKeys.Control, false, "\0")]
    [InlineData(Key.OemOpenBrackets, ModifierKeys.Control, false, "\x1b")]
    public void Keys_are_sent_as_xterm_sends_them(Key key, ModifierKeys modifiers, bool appCursor, string expected) =>
        Assert.Equal(expected, TerminalInput.Encode(key, modifiers, appCursor));

    [Fact]
    public void Text_keys_and_AltGr_come_as_text_input()
    {
        Assert.Null(TerminalInput.Encode(Key.A, ModifierKeys.None, false));
        // Ctrl+Alt is AltGr on German and other layouts ("@" is AltGr+Q): not a control character.
        Assert.Null(TerminalInput.Encode(Key.Q, ModifierKeys.Control | ModifierKeys.Alt, false));
    }

    [Fact]
    public void A_bracketed_paste_is_marked_and_cant_end_itself()
    {
        Assert.Equal("ls\rpwd", TerminalInput.Paste("ls\r\npwd", bracketed: false));
        Assert.Equal("\x1b[200~ls[201~\rid\x1b[201~", TerminalInput.Paste("ls\x1b[201~\nid", bracketed: true));
        Assert.True(TerminalInput.RunsCommands("ls\npwd", bracketed: false));
        Assert.False(TerminalInput.RunsCommands("ls\n", bracketed: false)); // one line with its Enter
        Assert.False(TerminalInput.RunsCommands("ls\npwd", bracketed: true));
    }

    [Fact]
    public void Mouse_reports_in_both_encodings()
    {
        Assert.Equal("\x1b[<0;5;3M", TerminalInput.Mouse(0, 4, 2, release: false, sgr: true, ModifierKeys.None));
        Assert.Equal("\x1b[<64;1;1M", TerminalInput.Mouse(64, 0, 0, release: false, sgr: true, ModifierKeys.None));
        Assert.Equal("\x1b[<16;1;1m", TerminalInput.Mouse(0, 0, 0, release: true, sgr: true, ModifierKeys.Control));
        Assert.Equal("\x1b[M !!", TerminalInput.Mouse(0, 0, 0, release: false, sgr: false, ModifierKeys.None));
        Assert.Equal("\x1b[M#!!", TerminalInput.Mouse(2, 0, 0, release: true, sgr: false, ModifierKeys.None));
        // One byte per position over a UTF-8 channel: beyond column 95 only SGR can say where.
        Assert.Null(TerminalInput.Mouse(0, 100, 0, release: false, sgr: false, ModifierKeys.None));
    }
}
