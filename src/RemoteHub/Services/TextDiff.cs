namespace RemoteHub.Services;

public enum DiffKind { Same, Removed, Added, Changed }

/// <summary>A span of changed characters inside a changed line.</summary>
public readonly record struct DiffSpan(int Start, int Length);

/// <summary>
/// One row of a comparison: an unchanged line (both sides), a removed line (left only), an added line (right only), or a
/// changed line (both sides, with the changed words marked). Line numbers are 1-based.
/// </summary>
public sealed record DiffRow(DiffKind Kind, int? OldNumber, string? OldText, int? NewNumber, string? NewText,
    IReadOnlyList<DiffSpan> OldSpans, IReadOnlyList<DiffSpan> NewSpans)
{
    public bool IsChange => Kind != DiffKind.Same;
}

/// <summary>
/// Compares two texts line by line, like <c>git diff</c> (docs/scripts-view-design.md §4.10): Myers' algorithm on the
/// lines, then on the words of each changed pair of lines. Line ends don't count (CRLF and LF compare equal). Our own
/// code; past <see cref="MaxEdits"/> differences a block is shown as removed and added as a whole instead of searching on.
/// </summary>
public static class TextDiff
{
    /// <summary>Limit for the search; well above what a reviewed script change has, and it bounds time and memory.</summary>
    internal const int MaxEdits = 2000;

    public static IReadOnlyList<DiffRow> Compare(string oldText, string newText)
    {
        var a = Lines(oldText);
        var b = Lines(newText);
        var ops = Diff(a, b, StringComparer.Ordinal);
        var rows = new List<DiffRow>();
        int i = 0, j = 0, k = 0;
        while (k < ops.Count)
        {
            if (ops[k] == Op.Same) { rows.Add(new DiffRow(DiffKind.Same, i + 1, a[i], j + 1, b[j], [], [])); i++; j++; k++; continue; }
            // A block of removals and additions: pair them up line by line as changes, the rest stays removed or added.
            var removed = new List<int>();
            var added = new List<int>();
            while (k < ops.Count && ops[k] != Op.Same)
            {
                if (ops[k] == Op.Remove) removed.Add(i++); else added.Add(j++);
                k++;
            }
            var pairs = Math.Min(removed.Count, added.Count);
            for (var p = 0; p < pairs; p++)
            {
                var (os, ns) = Words(a[removed[p]], b[added[p]]);
                rows.Add(new DiffRow(DiffKind.Changed, removed[p] + 1, a[removed[p]], added[p] + 1, b[added[p]], os, ns));
            }
            foreach (var r in removed.Skip(pairs)) rows.Add(new DiffRow(DiffKind.Removed, r + 1, a[r], null, null, [], []));
            foreach (var r in added.Skip(pairs)) rows.Add(new DiffRow(DiffKind.Added, null, null, r + 1, b[r], [], []));
        }
        return rows;
    }

    /// <summary>The number of changed places: runs of rows that aren't the same.</summary>
    public static int CountChanges(IReadOnlyList<DiffRow> rows)
    {
        var n = 0;
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].IsChange && (i == 0 || !rows[i - 1].IsChange)) n++;
        return n;
    }

    internal static string[] Lines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // A final newline ends the last line rather than starting an empty one.
        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    // ------------------------------------------------------------------ words inside a changed line

    private static (IReadOnlyList<DiffSpan> Old, IReadOnlyList<DiffSpan> New) Words(string oldLine, string newLine)
    {
        var a = Tokens(oldLine);
        var b = Tokens(newLine);
        var ops = Diff(a.Select(t => t.Text).ToArray(), b.Select(t => t.Text).ToArray(), StringComparer.Ordinal);
        var oldSpans = new List<DiffSpan>();
        var newSpans = new List<DiffSpan>();
        int i = 0, j = 0;
        foreach (var op in ops)
        {
            switch (op)
            {
                case Op.Same: i++; j++; break;
                case Op.Remove: Add(oldSpans, a[i].Start, a[i].Text.Length); i++; break;
                default: Add(newSpans, b[j].Start, b[j].Text.Length); j++; break;
            }
        }
        return (oldSpans, newSpans);
    }

    /// <summary>Adds a span, joined with the previous one when they touch (one mark per changed word group).</summary>
    private static void Add(List<DiffSpan> spans, int start, int length)
    {
        if (spans.Count > 0 && spans[^1].Start + spans[^1].Length == start)
            spans[^1] = new DiffSpan(spans[^1].Start, spans[^1].Length + length);
        else spans.Add(new DiffSpan(start, length));
    }

    /// <summary>Words, runs of white space, and single other characters (so <c>-Path</c> and <c>$x.Name</c> split sensibly).</summary>
    internal static List<(string Text, int Start)> Tokens(string line)
    {
        var list = new List<(string, int)>();
        var i = 0;
        while (i < line.Length)
        {
            var start = i;
            if (char.IsLetterOrDigit(line[i]) || line[i] == '_')
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
            else if (char.IsWhiteSpace(line[i]))
                while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            else i++;
            list.Add((line[start..i], start));
        }
        return list;
    }

    // ------------------------------------------------------------------ Myers

    internal enum Op : byte { Same, Remove, Add }

    /// <summary>The shortest edit script from <paramref name="a"/> to <paramref name="b"/> (removals before additions in a block).</summary>
    internal static List<Op> Diff(string[] a, string[] b, IEqualityComparer<string> eq)
    {
        // Common start and end are cheap and usually most of a script.
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && eq.Equals(a[prefix], b[prefix])) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && eq.Equals(a[a.Length - 1 - suffix], b[b.Length - 1 - suffix])) suffix++;

        var ops = new List<Op>(a.Length + b.Length);
        for (var i = 0; i < prefix; i++) ops.Add(Op.Same);
        ops.AddRange(Middle(a[prefix..(a.Length - suffix)], b[prefix..(b.Length - suffix)], eq));
        for (var i = 0; i < suffix; i++) ops.Add(Op.Same);
        return ops;
    }

    private static List<Op> Middle(string[] a, string[] b, IEqualityComparer<string> eq)
    {
        int n = a.Length, m = b.Length;
        if (n == 0) return [.. Enumerable.Repeat(Op.Add, m)];
        if (m == 0) return [.. Enumerable.Repeat(Op.Remove, n)];
        var limit = Math.Min(n + m, MaxEdits);
        var offset = limit + 1;
        var v = new int[2 * limit + 3];
        // Per step only the diagonals -d..d it can have reached: the trace grows with d², not with the texts' length.
        var trace = new List<int[]>();
        for (var d = 0; d <= limit; d++)
        {
            trace.Add(v[(offset - d)..(offset + d + 1)]);
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && eq.Equals(a[x], b[y])) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) return Backtrack(trace, a, b, d);
            }
        }
        // Too different to search on: the whole block is replaced.
        var all = new List<Op>(n + m);
        all.AddRange(Enumerable.Repeat(Op.Remove, n));
        all.AddRange(Enumerable.Repeat(Op.Add, m));
        return all;
    }

    private static List<Op> Backtrack(List<int[]> trace, string[] a, string[] b, int dEnd)
    {
        var ops = new List<Op>();
        int x = a.Length, y = b.Length;
        for (var d = dEnd; d > 0; d--)
        {
            var v = trace[d]; // diagonals -d..d at index k + d
            var k = x - y;
            var prevK = k == -d || (k != d && v[k - 1 + d] < v[k + 1 + d]) ? k + 1 : k - 1;
            var prevX = v[prevK + d];
            var prevY = prevX - prevK;
            while (x > prevX && y > prevY) { ops.Add(Op.Same); x--; y--; }
            if (x == prevX) { ops.Add(Op.Add); y--; } else { ops.Add(Op.Remove); x--; }
        }
        while (x > 0 && y > 0) { ops.Add(Op.Same); x--; y--; }
        ops.Reverse();
        // Within each block of changes, removals first: rows then pair old and new lines in order.
        var result = new List<Op>(ops.Count);
        for (var i = 0; i < ops.Count;)
        {
            if (ops[i] == Op.Same) { result.Add(Op.Same); i++; continue; }
            int removes = 0, adds = 0;
            while (i < ops.Count && ops[i] != Op.Same) { if (ops[i] == Op.Remove) removes++; else adds++; i++; }
            result.AddRange(Enumerable.Repeat(Op.Remove, removes));
            result.AddRange(Enumerable.Repeat(Op.Add, adds));
        }
        return result;
    }
}
