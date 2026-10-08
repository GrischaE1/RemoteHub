using System.Management.Automation.Language;
using System.Windows.Media;

namespace RemoteHub.Views;

/// <summary>PowerShell syntax colouring (PSReadLine-like palette), using PowerShell's own tokenizer on this PC.</summary>
public static class PsSyntax
{
    private static Brush B(byte r, byte g, byte b) => ColorTags.Frozen(Color.FromRgb(r, g, b));

    public static readonly Brush Default = B(0xF2, 0xF2, 0xF2);
    public static readonly Brush Command = B(0xF9, 0xE2, 0x7A);
    public static readonly Brush Parameter = B(0x9C, 0xA3, 0xAF);
    public static readonly Brush String = B(0x61, 0xD6, 0xD6);
    public static readonly Brush Variable = B(0x6C, 0xD9, 0x7E);
    public static readonly Brush Number = B(0xD7, 0xBA, 0xFF);
    public static readonly Brush Keyword = B(0x5C, 0x9D, 0xFF);
    public static readonly Brush Operator = B(0x9C, 0xA3, 0xAF);
    public static readonly Brush Type = B(0x8B, 0xC4, 0xA8);
    public static readonly Brush Comment = B(0x6A, 0x99, 0x55);
    public static readonly Brush Member = B(0xE6, 0xE6, 0xE6);

    /// <summary>Splits <paramref name="text"/> into coloured segments that together are exactly the text.</summary>
    public static List<(string Text, Brush Brush)> Colorize(string text)
    {
        var result = new List<(string, Brush)>();
        if (text.Length == 0) return result;
        Token[] tokens;
        try { Parser.ParseInput(text, out tokens, out _); }
        catch { result.Add((text, Default)); return result; }

        int pos = 0;
        foreach (var t in tokens.OrderBy(t => t.Extent.StartOffset))
        {
            int start = t.Extent.StartOffset, end = t.Extent.EndOffset;
            if (start < pos || end <= start || end > text.Length) continue; // nested (inside a string) or empty
            if (BrushFor(t) is not { } brush) continue;
            if (start > pos) result.Add((text[pos..start], Default));
            result.Add((text[start..end], brush));
            pos = end;
        }
        if (pos < text.Length) result.Add((text[pos..], Default));
        return result;
    }

    private static Brush? BrushFor(Token t)
    {
        if (t.Kind is TokenKind.EndOfInput or TokenKind.NewLine or TokenKind.LineContinuation) return null;
        if (t.TokenFlags.HasFlag(TokenFlags.CommandName)) return Command;
        if (t.TokenFlags.HasFlag(TokenFlags.TypeName)) return Type;
        if (t.TokenFlags.HasFlag(TokenFlags.MemberName)) return Member;
        if (t.TokenFlags.HasFlag(TokenFlags.Keyword)) return Keyword;
        return t.Kind switch
        {
            TokenKind.Comment => Comment,
            TokenKind.Parameter => Parameter,
            TokenKind.Variable or TokenKind.SplattedVariable => Variable,
            TokenKind.StringLiteral or TokenKind.StringExpandable or TokenKind.HereStringLiteral or TokenKind.HereStringExpandable => String,
            TokenKind.Number => Number,
            _ when t.TokenFlags.HasFlag(TokenFlags.BinaryOperator) || t.TokenFlags.HasFlag(TokenFlags.UnaryOperator)
                   || t.TokenFlags.HasFlag(TokenFlags.AssignmentOperator) || t.Kind is TokenKind.Pipe or TokenKind.Redirection => Operator,
            _ => Default,
        };
    }
}
