using System.Text;
using System.Text.RegularExpressions;

namespace Chaos.Domain.GitHub;

public sealed record ChecklistItem(int Ordinal, string Text, bool Checked, int Start, int End, int CheckOffset, int TextOffset);
public sealed record ChecklistEdit(int Ordinal, string Text, bool Checked, bool Deleted = false);
public sealed record NewChecklistItem(string Text, bool Checked);

/// <summary>Scans and edits a single level of Markdown task-list items without normalizing lines.</summary>
public static class IssueBody
{
    private static readonly Regex List = new(@"^( {0,3})(?:[-*+]|\d{1,9}[.)]) ", RegexOptions.CultureInvariant);
    private static readonly Regex Task = new(@"^( {0,3})(?:[-*+]|\d{1,9}[.)]) \[( |x|X)\] (.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Fence = new(@"^ {0,3}(`{3,}|~{3,})(.*)$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<ChecklistItem> ScanChecklist(string body)
    {
        var items = new List<ChecklistItem>();
        var listIndents = new Stack<int>();
        char fence = '\0';
        var fenceLength = 0;
        for (var start = 0; start < body.Length;)
        {
            var endOfText = body.IndexOf('\n', start);
            var end = endOfText < 0 ? body.Length : endOfText + 1;
            var contentEnd = endOfText < 0 ? body.Length : endOfText;
            if (endOfText >= 0 && contentEnd > start && body[contentEnd - 1] == '\r') contentEnd--;
            var line = body[start..contentEnd];
            var fenceMatch = Fence.Match(line);
            if (fence != '\0')
            {
                if (fenceMatch.Success && fenceMatch.Groups[1].Value[0] == fence &&
                    fenceMatch.Groups[1].Length >= fenceLength && string.IsNullOrWhiteSpace(fenceMatch.Groups[2].Value)) fence = '\0';
                start = end;
                continue;
            }
            if (fenceMatch.Success)
            {
                fence = fenceMatch.Groups[1].Value[0];
                fenceLength = fenceMatch.Groups[1].Length;
                start = end;
                continue;
            }
            var listMatch = List.Match(line);
            if (listMatch.Success)
            {
                var indent = listMatch.Groups[1].Length;
                while (listIndents.Count > 0 && listIndents.Peek() >= indent) listIndents.Pop();
                var nested = listIndents.Count > 0;
                listIndents.Push(indent);
                var task = Task.Match(line);
                if (!nested && task.Success)
                {
                    var check = task.Groups[2];
                    var text = task.Groups[3];
                    items.Add(new(items.Count, text.Value, check.Value != " ", start, end, start + check.Index, start + text.Index));
                }
            }
            else if (line.Length > 0 && !char.IsWhiteSpace(line[0])) listIndents.Clear();
            start = end;
        }
        return items;
    }

    public static string Rewrite(string body, IReadOnlyList<ChecklistEdit> edits, IReadOnlyList<NewChecklistItem> additions)
    {
        var items = ScanChecklist(body);
        var byOrdinal = edits.ToDictionary(edit => edit.Ordinal);
        if (byOrdinal.Keys.Any(index => index < 0 || index >= items.Count)) throw new ArgumentOutOfRangeException(nameof(edits));
        foreach (var text in edits.Where(edit => !edit.Deleted && edit.Text != items[edit.Ordinal].Text).Select(edit => edit.Text).Concat(additions.Select(item => item.Text)))
            if (text.Contains('\r') || text.Contains('\n')) throw new ArgumentException("Checklist text must fit on one line.");
        if (edits.Count == 0 && additions.Count == 0) return body;
        var newline = body.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var builder = new StringBuilder();
        var cursor = 0;
        foreach (var item in items)
        {
            builder.Append(body, cursor, item.Start - cursor);
            if (!byOrdinal.TryGetValue(item.Ordinal, out var edit) || (!edit.Deleted && edit.Text == item.Text && edit.Checked == item.Checked)) builder.Append(body, item.Start, item.End - item.Start);
            else if (!edit.Deleted)
            {
                builder.Append(body, item.Start, item.CheckOffset - item.Start);
                builder.Append(edit.Checked == item.Checked ? body[item.CheckOffset] : edit.Checked ? 'x' : ' ');
                builder.Append(body, item.CheckOffset + 1, item.TextOffset - item.CheckOffset - 1);
                builder.Append(edit.Text);
                var lineEnd = body.IndexOf('\n', item.TextOffset, item.End - item.TextOffset);
                if (lineEnd >= 0)
                {
                    if (lineEnd > item.TextOffset && body[lineEnd - 1] == '\r') builder.Append('\r');
                    builder.Append('\n');
                }
            }
            cursor = item.End;
            if (item.Ordinal == items.Count - 1 && additions.Count > 0)
                AppendNew(builder, additions, newline, blankLine: false);
        }
        builder.Append(body, cursor, body.Length - cursor);
        if (items.Count == 0 && additions.Count > 0) AppendNew(builder, additions, newline, blankLine: true);
        return builder.ToString();
    }

    private static void AppendNew(StringBuilder builder, IReadOnlyList<NewChecklistItem> additions, string newline, bool blankLine)
    {
        if (builder.Length > 0 && builder[^1] != '\n') builder.Append(newline);
        if (blankLine && builder.Length > newline.Length && !builder.ToString().EndsWith(newline + newline, StringComparison.Ordinal)) builder.Append(newline);
        foreach (var item in additions) builder.Append(item.Checked ? "- [x] " : "- [ ] ").Append(item.Text).Append(newline);
    }
}
