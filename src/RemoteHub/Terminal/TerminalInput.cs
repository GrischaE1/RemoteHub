using System.Text;
using System.Windows.Input;

namespace RemoteHub.Terminal;

/// <summary>What keys, pastes and the mouse send to the server, as xterm sends them.</summary>
public static class TerminalInput
{
    /// <summary>
    /// The bytes for a key that isn't text – Enter, arrows, function keys, Ctrl+letter – or null when the key is text
    /// (it arrives as text input) or isn't sent at all. <paramref name="appCursor"/>: the program asked for application
    /// cursor keys (DECCKM).
    /// </summary>
    public static string? Encode(Key key, ModifierKeys modifiers, bool appCursor)
    {
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        var alt = modifiers.HasFlag(ModifierKeys.Alt);
        var ctrl = modifiers.HasFlag(ModifierKeys.Control);
        var m = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        var esc = alt ? "\x1b" : "";

        string Cursor(char c) => m == 1 ? (appCursor ? $"\x1bO{c}" : $"\x1b[{c}") : $"\x1b[1;{m}{c}";
        string Tilde(int n) => m == 1 ? $"\x1b[{n}~" : $"\x1b[{n};{m}~";
        string Ss3(char c) => m == 1 ? $"\x1bO{c}" : $"\x1b[1;{m}{c}";

        switch (key)
        {
            case Key.Enter: return esc + "\r";
            case Key.Back: return esc + (ctrl ? "\b" : "\x7f");
            case Key.Tab: return shift ? "\x1b[Z" : esc + "\t";
            case Key.Escape: return "\x1b";
            case Key.Up: return Cursor('A');
            case Key.Down: return Cursor('B');
            case Key.Right: return Cursor('C');
            case Key.Left: return Cursor('D');
            case Key.Home: return Cursor('H');
            case Key.End: return Cursor('F');
            case Key.Insert: return Tilde(2);
            case Key.Delete: return Tilde(3);
            case Key.PageUp: return Tilde(5);
            case Key.PageDown: return Tilde(6);
            case Key.F1: return Ss3('P');
            case Key.F2: return Ss3('Q');
            case Key.F3: return Ss3('R');
            case Key.F4: return Ss3('S');
            case Key.F5: return Tilde(15);
            case Key.F6: return Tilde(17);
            case Key.F7: return Tilde(18);
            case Key.F8: return Tilde(19);
            case Key.F9: return Tilde(20);
            case Key.F10: return Tilde(21);
            case Key.F11: return Tilde(23);
            case Key.F12: return Tilde(24);
        }

        // Ctrl without Alt: control characters. Ctrl+Alt is AltGr on many layouts – that is text and comes as text input.
        if (ctrl && !alt)
        {
            if (key is >= Key.A and <= Key.Z) return ((char)(key - Key.A + 1)).ToString();
            return key switch
            {
                Key.Space or Key.D2 => "\0",
                Key.OemOpenBrackets => "\x1b",
                Key.OemPipe or Key.OemBackslash => "\x1c",
                Key.OemCloseBrackets => "\x1d",
                Key.D6 => "\x1e",
                Key.OemMinus => "\x1f",
                _ => null,
            };
        }
        return null;
    }

    /// <summary>
    /// A paste as it is sent: line breaks as Enter (CR). With bracketed paste the program is told where it starts and ends,
    /// and escape characters are taken out – a pasted "end of paste" sequence can't make the rest run as typed keys.
    /// </summary>
    public static string Paste(string text, bool bracketed)
    {
        var normalized = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (!bracketed) return normalized;
        var sb = new StringBuilder(normalized.Length + 12).Append("\x1b[200~");
        foreach (var ch in normalized) if (ch != '\x1b') sb.Append(ch);
        return sb.Append("\x1b[201~").ToString();
    }

    /// <summary>The paste runs as several commands: it has a line break, and the program didn't ask for bracketed paste.</summary>
    public static bool RunsCommands(string text, bool bracketed) => !bracketed && text.TrimEnd('\r', '\n').IndexOfAny(['\r', '\n']) >= 0;

    /// <summary>
    /// A mouse event for a program that asked for them: button 0–2 (left, middle, right), 3 = release (legacy encoding),
    /// 64/65 = wheel up/down; +32 while moving; +4/+8/+16 for Shift/Alt/Ctrl. <paramref name="column"/> and
    /// <paramref name="row"/> are 0-based. Null when the legacy encoding can't say the position: it is one byte, and
    /// the channel is UTF-8, so beyond column 95 only the SGR encoding (1006) works.
    /// </summary>
    public static string? Mouse(int button, int column, int row, bool release, bool sgr, ModifierKeys modifiers)
    {
        var b = button + (modifiers.HasFlag(ModifierKeys.Shift) ? 4 : 0) + (modifiers.HasFlag(ModifierKeys.Alt) ? 8 : 0)
              + (modifiers.HasFlag(ModifierKeys.Control) ? 16 : 0);
        if (sgr) return $"\x1b[<{b};{column + 1};{row + 1}{(release ? 'm' : 'M')}";
        if (release) b = 3 + (b & ~3 & ~64); // the legacy encoding doesn't say which button was released
        if (column + 1 + 32 > 127 || row + 1 + 32 > 127) return null;
        return $"\x1b[M{(char)(32 + b)}{(char)(32 + column + 1)}{(char)(32 + row + 1)}";
    }
}
