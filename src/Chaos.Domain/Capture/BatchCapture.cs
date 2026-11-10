using System.Text.RegularExpressions;

namespace Chaos.Domain.Capture;

/// <summary>One pasted line: its 1-based number, text without indentation or list marker, and where that text starts in the whole input.</summary>
public sealed record BatchLine(int LineNumber, string Text, int SourceStart, bool Checked);

/// <summary>A task line and the indented subtask lines under it.</summary>
public sealed record BatchItem(BatchLine Task, IReadOnlyList<BatchLine> Subtasks);

public sealed record BatchError(int LineNumber, string Message);

/// <summary>The structure of pasted text. Nothing is saved while any error remains (PLAN §8: never a partial subset).</summary>
public sealed record BatchSplit(IReadOnlyList<BatchItem> Items, IReadOnlyList<BatchError> Errors, bool IsBatch);

/// <summary>
/// Multiline capture (PLAN §8): one root line is one task; one level of indentation makes
/// subtasks. Tabs count as four spaces; a line mixing tabs and spaces, or indented deeper
/// than one level, is rejected with line-specific guidance rather than silently flattened.
/// In a batch, one bullet or numbered marker and one checkbox are stripped from each line.
/// </summary>
public static class BatchCapture
{
    public const int MaxLines = 500;
    private static readonly Regex Marker = new(@"^(?:[-*•‣◦]\s+|\d{1,3}[.)]\s+)?(?:\[(?<c>[ xX✓])\]\s*)?", RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    public static BatchSplit Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<(int Number, int Start, string Raw)>();
        var start = 0;
        var number = 1;
        for (var index = 0; index <= text.Length; index++)
        {
            if (index < text.Length && text[index] is not ('\r' or '\n')) continue;
            var raw = text[start..index];
            if (raw.Trim().Length > 0) lines.Add((number, start, raw));
            if (index < text.Length && text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            start = index + 1;
            number++;
        }

        if (lines.Count <= 1)
        {
            var only = lines.Count == 0 ? new BatchLine(1, string.Empty, 0, false) : new BatchLine(lines[0].Number, lines[0].Raw.Trim(), lines[0].Start + Indent(lines[0].Raw), false);
            return new BatchSplit([new BatchItem(only, [])], [], IsBatch: false);
        }

        var errors = new List<BatchError>();
        if (lines.Count > MaxLines)
        {
            errors.Add(new BatchError(lines[MaxLines].Number, $"At most {MaxLines} lines can be added at once; this paste has {lines.Count}. Split it into smaller pastes."));
            return new BatchSplit([], errors, IsBatch: true);
        }

        var items = new List<(BatchLine Task, List<BatchLine> Subtasks)>();
        int? baseWidth = null;
        int? childWidth = null;
        foreach (var (lineNumber, lineStart, raw) in lines)
        {
            var indent = Indent(raw);
            var whitespace = raw[..indent];
            if (whitespace.Contains('\t') && whitespace.Contains(' '))
            {
                errors.Add(new BatchError(lineNumber, $"Line {lineNumber} mixes tabs and spaces in its indentation. Use only one of them."));
                continue;
            }

            var width = whitespace.Count(character => character == ' ') + 4 * whitespace.Count(character => character == '\t');
            var body = raw[indent..];
            var marker = Marker.Match(body);
            var content = body[marker.Length..].TrimEnd();
            var line = new BatchLine(lineNumber, content, lineStart + indent + marker.Length, marker.Groups["c"].Success && marker.Groups["c"].Value.Trim().Length > 0);
            if (content.Length == 0)
            {
                errors.Add(new BatchError(lineNumber, $"Line {lineNumber} has a list marker but no text. Add text or remove the line."));
                continue;
            }

            baseWidth ??= width;
            if (width == baseWidth)
            {
                items.Add((line, []));
                childWidth = null;
            }
            else if (width < baseWidth)
            {
                errors.Add(new BatchError(lineNumber, $"Line {lineNumber} is indented less than the first line. Line up task lines with the first one."));
            }
            else if (items.Count == 0)
            {
                errors.Add(new BatchError(lineNumber, $"Line {lineNumber} is indented but there is no task above it."));
            }
            else if (childWidth is null || width == childWidth)
            {
                childWidth = width;
                items[^1].Subtasks.Add(line);
            }
            else if (width > childWidth)
            {
                errors.Add(new BatchError(lineNumber, $"Line {lineNumber} is indented deeper than a subtask. Subtasks cannot have their own subtasks; move it out one level."));
            }
            else
            {
                errors.Add(new BatchError(lineNumber, $"Line {lineNumber} is indented differently from the subtasks above it. Indent subtasks of one task by the same amount."));
            }
        }

        return new BatchSplit([.. items.Select(item => new BatchItem(item.Task, item.Subtasks))], errors, IsBatch: true);
    }

    private static int Indent(string raw)
    {
        var index = 0;
        while (index < raw.Length && raw[index] is ' ' or '\t') index++;
        return index;
    }
}
