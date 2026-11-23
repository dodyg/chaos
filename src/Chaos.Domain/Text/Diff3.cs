using System.Text;

namespace Chaos.Domain.Text;

/// <summary>
/// Line-based three-way merge (PLAN3 §9.9). BCL only. Lines keep their terminators, so CRLF and a
/// missing final newline survive. The merge is symmetric in its two sides, apart from which one is
/// written first in a conflict: the caller orders them (by clock), so two devices that merge the same
/// pair produce the same text, markers included.
/// </summary>
public static class Diff3
{
    /// <summary>The longest trimmed comparison the line diff solves exactly (cells of the LCS table).</summary>
    private const long MaxCells = 4_000_000;

    private sealed record Edit(int Start, int End, string[] Lines);

    public static bool HasConflictMarkers(string text) =>
        text.StartsWith("<<<<<<< ", StringComparison.Ordinal) || text.Contains("\n<<<<<<< ", StringComparison.Ordinal);

    /// <summary>
    /// Merges <paramref name="first"/> and <paramref name="second"/>, both derived from <paramref name="base"/>.
    /// Blocks changed differently by both are kept, first side then second, between marker lines.
    /// </summary>
    public static string Merge(string @base, string first, string second, string firstLabel = "yours", string secondLabel = "remote")
    {
        if (first == second) return first;
        if (first == @base) return second;
        if (second == @base) return first;

        var original = Lines(@base);
        var left = Changes(original, Lines(first));
        var right = Changes(original, Lines(second));
        var output = new StringBuilder();
        var position = 0;
        var i = 0;
        var j = 0;
        while (i < left.Count || j < right.Count)
        {
            var next = i < left.Count && (j == right.Count || left[i].Start <= right[j].Start) ? left[i] : right[j];
            Append(output, original, position, next.Start);
            var start = next.Start;
            var end = next.End;
            var li = i;
            var rj = j;
            bool grew;
            do
            {
                grew = false;
                while (i < left.Count && Intersects(left[i], start, end)) { end = Math.Max(end, left[i++].End); grew = true; }
                while (j < right.Count && Intersects(right[j], start, end)) { end = Math.Max(end, right[j++].End); grew = true; }
            }
            while (grew);

            var l = Apply(original, start, end, left, li, i);
            var r = Apply(original, start, end, right, rj, j);
            var unchanged = Join(original, start, end);
            if (l == r || r == unchanged) output.Append(l);
            else if (l == unchanged) output.Append(r);
            else
            {
                output.Append("<<<<<<< ").Append(firstLabel).Append('\n');
                output.Append(l);
                if (!l.EndsWith('\n')) output.Append('\n');
                output.Append("=======\n");
                output.Append(r);
                if (!r.EndsWith('\n')) output.Append('\n');
                output.Append(">>>>>>> ").Append(secondLabel).Append('\n');
            }

            position = end;
        }

        Append(output, original, position, original.Length);
        return output.ToString();
    }

    // Edits meeting at the same start (two insertions at one point) conflict; an edit merely adjacent to a
    // non-empty region stays independent.
    private static bool Intersects(Edit edit, int start, int end) =>
        edit.Start == start || (edit.Start < end && edit.End >= start);

    private static string Apply(string[] source, int start, int end, List<Edit> edits, int from, int to)
    {
        var result = new StringBuilder();
        var p = start;
        for (var k = from; k < to; k++)
        {
            Append(result, source, p, edits[k].Start);
            foreach (var line in edits[k].Lines) result.Append(line);
            p = edits[k].End;
        }

        Append(result, source, p, end);
        return result.ToString();
    }

    private static string Join(string[] source, int start, int end)
    {
        var result = new StringBuilder();
        Append(result, source, start, end);
        return result.ToString();
    }

    private static void Append(StringBuilder result, string[] source, int start, int end)
    {
        for (var k = start; k < end; k++) result.Append(source[k]);
    }

    private static string[] Lines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') { lines.Add(text[start..(i + 1)]); start = i + 1; }
        if (start < text.Length) lines.Add(text[start..]);
        return [.. lines];
    }

    private static List<Edit> Changes(string[] source, string[] target)
    {
        var prefix = 0;
        while (prefix < source.Length && prefix < target.Length && source[prefix] == target[prefix]) prefix++;
        var n = source.Length;
        var m = target.Length;
        while (n > prefix && m > prefix && source[n - 1] == target[m - 1]) { n--; m--; }
        var rows = n - prefix;
        var cols = m - prefix;
        if (rows == 0 && cols == 0) return [];
        if ((long)(rows + 1) * (cols + 1) > MaxCells)
            return [new Edit(prefix, n, target[prefix..m])]; // too large to align: one block, which conflicts rather than guesses

        var lcs = new int[rows + 1, cols + 1];
        for (var a = rows - 1; a >= 0; a--)
            for (var b = cols - 1; b >= 0; b--)
                lcs[a, b] = source[prefix + a] == target[prefix + b] ? 1 + lcs[a + 1, b + 1] : Math.Max(lcs[a + 1, b], lcs[a, b + 1]);
        var result = new List<Edit>();
        var x = 0;
        var y = 0;
        while (x < rows || y < cols)
        {
            if (x < rows && y < cols && source[prefix + x] == target[prefix + y]) { x++; y++; continue; }
            var begin = x;
            var inserted = new List<string>();
            while (x < rows || y < cols)
            {
                if (x < rows && y < cols && source[prefix + x] == target[prefix + y]) break;
                if (y < cols && (x == rows || lcs[x, y + 1] >= lcs[x + 1, y])) inserted.Add(target[prefix + y++]);
                else x++;
            }

            result.Add(new Edit(prefix + begin, prefix + x, [.. inserted]));
        }

        return result;
    }
}
