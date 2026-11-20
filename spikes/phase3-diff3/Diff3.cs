using System.Text;

// Spike only: bounded LCS keeps the quadratic part from exhausting memory.
public static class Diff3
{
    private sealed record Edit(int Start, int End, string[] Lines);
    private const long MaxCells = 4_000_000;

    public static string Merge(string @base, string local, string remote)
    {
        if (local == remote) return local;
        if (local == @base) return remote;
        if (remote == @base) return local;
        var original = Lines(@base);
        var left = Changes(original, Lines(local));
        var right = Changes(original, Lines(remote));
        var output = new StringBuilder();
        var position = 0;
        var i = 0;
        var j = 0;
        while (i < left.Count || j < right.Count)
        {
            var first = i < left.Count && (j == right.Count || left[i].Start <= right[j].Start) ? left[i] : right[j];
            Append(output, original, position, first.Start);
            var start = first.Start;
            var end = first.End;
            var li = i;
            var rj = j;
            // Consume edits intersecting the region. Insertions at the same point conflict;
            // edits merely adjacent to a nonempty region remain independent.
            bool grew;
            do
            {
                grew = false;
                while (i < left.Count && Intersects(left[i], start, end)) { end = Math.Max(end, left[i++].End); grew = true; }
                while (j < right.Count && Intersects(right[j], start, end)) { end = Math.Max(end, right[j++].End); grew = true; }
            } while (grew);
            var l = Apply(original, start, end, left, li, i);
            var r = Apply(original, start, end, right, rj, j);
            var unchanged = Join(original, start, end);
            if (l == r || r == unchanged) output.Append(l);
            else if (l == unchanged) output.Append(r);
            else
            {
                output.Append("<<<<<<< yours\n");
                output.Append(l);
                if (!l.EndsWith('\n')) output.Append('\n');
                output.Append("=======\n");
                output.Append(r);
                if (!r.EndsWith('\n')) output.Append('\n');
                output.Append(">>>>>>> remote\n");
            }
            position = end;
        }
        Append(output, original, position, original.Length);
        return output.ToString();
    }

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
        if ((long)(rows + 1) * (cols + 1) > MaxCells)
            throw new InvalidOperationException("Diff exceeds the spike's LCS cell limit.");
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
