using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Chaos.Infrastructure.Transfer;

/// <summary>
/// Human-readable exports (PLAN §14). Plain-text formats prioritize readability and keep the
/// original Markdown notes unchanged; the lossless round-trip guarantee belongs to the JSON
/// archive. Supported fields and limitations are listed in docs/data-transfer.md and in the
/// README inside each Markdown export.
/// </summary>
public static class PlainTextExport
{
    public static readonly string[] StatusNames = ["NotStarted", "InProgress", "WaitingOnSomeone", "NeedsReview", "Done", "Cancelled"];
    public static readonly string[] PriorityNames = ["None", "Low", "Normal", "High", "Urgent"];

    // ---------------------------------------------------------------- Markdown

    /// <summary>Writes a ZIP: README.md, one Markdown file per task under tasks/&lt;list&gt;/, one per pad note under pad/.</summary>
    public static void WriteMarkdownZip(ExportSnapshot snapshot, Stream output, DateTimeOffset now)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, "README.md", MarkdownReadme(now));

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var containers = snapshot.Containers.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var container in snapshot.Containers)
        {
            index++;
            folders[container.Id] = $"tasks/{index:00} {SafeFileName(container.Name, 50)}";
        }

        foreach (var task in snapshot.Tasks)
        {
            var folder = folders[task.ContainerId];
            var path = Unique(used, $"{folder}/{SafeFileName(task.Title, 60)} ({task.Id[..8]}).md");
            Add(zip, path, TaskMarkdown(task, containers[task.ContainerId]));
        }

        var padIndex = 0;
        foreach (var item in snapshot.PadItems)
        {
            padIndex++;
            var firstLine = item.Markdown.Split('\n').Select(line => line.Trim().TrimStart('#').Trim()).FirstOrDefault(line => line.Length > 0) ?? "note";
            var path = Unique(used, $"pad/{padIndex:000} {SafeFileName(firstLine, 50)} ({item.Id[..8]}).md");
            Add(zip, path, PadMarkdown(item));
        }
    }

    public static string TaskMarkdown(ExportTask task, ExportContainer container)
    {
        var text = new StringBuilder();
        text.Append("---\n");
        Front(text, "id", task.Id);
        Front(text, "list", container.Name);
        Front(text, "list_id", container.Id);
        Front(text, "section", task.SectionTitle);
        Front(text, "status", StatusNames[task.Status]);
        Front(text, "priority", PriorityNames[task.Priority]);
        Front(text, "planned", DateText(task.PlannedDate, task.PlannedTime, task.PlannedZoneId));
        Front(text, "due", DateText(task.DueDate, task.DueTime, task.DueZoneId));
        Front(text, "completed", task.CompletedAtUtc);
        Front(text, "cancelled", task.CancelledAtUtc);
        text.Append("estimate_minutes: ").Append(task.EstimateMinutes?.ToString(CultureInfo.InvariantCulture) ?? "null").Append('\n');
        Front(text, "category", task.CapacityCategory == 1 ? "Work" : "Personal");
        text.Append("tags: ").Append(JsonSerializer.Serialize(task.Tags)).Append('\n');
        text.Append("recurrence: ").Append(task.RecurrenceRuleJson ?? "null").Append('\n');
        Front(text, "created", task.CreatedAtUtc);
        Front(text, "updated", task.UpdatedAtUtc);
        text.Append("---\n\n# ").Append(OneLine(task.Title)).Append("\n\n");
        if (task.GitHubUrl is { } source) text.Append("Source: ").Append(OneLine(source)).Append("\n\n");
        if (task.Notes.Length > 0)
        {
            text.Append(task.Notes.TrimEnd()).Append("\n\n");
        }

        if (task.Subtasks.Count > 0)
        {
            text.Append("## Subtasks\n\n");
            foreach (var subtask in task.Subtasks)
            {
                var mark = subtask.Status == 4 ? "x" : " ";
                var state = subtask.Status is 4 or 0 ? "" : $" ({StatusNames[subtask.Status]})";
                text.Append("- [").Append(mark).Append("] ").Append(OneLine(subtask.Title)).Append(state).Append(" <!-- id: ").Append(subtask.Id).Append(" -->\n");
                if (subtask.Notes.Length > 0)
                {
                    foreach (var line in subtask.Notes.TrimEnd().Split('\n'))
                    {
                        text.Append("  ").Append(line.TrimEnd('\r')).Append('\n');
                    }
                }
            }

            text.Append('\n');
        }

        if (task.Diary.Count > 0)
        {
            text.Append("## Diary\n\n");
            foreach (var entry in task.Diary)
            {
                text.Append("### ").Append(entry.EntryAtUtc).Append("\n\n").Append(entry.Markdown.TrimEnd()).Append("\n\n");
            }
        }

        return text.ToString();
    }

    public static string PadMarkdown(ExportPadItem item)
    {
        var text = new StringBuilder("---\n");
        Front(text, "id", item.Id);
        text.Append("archived: ").Append(item.Archived ? "true" : "false").Append('\n');
        text.Append("tags: ").Append(JsonSerializer.Serialize(item.Tags)).Append('\n');
        Front(text, "created", item.CreatedAtUtc);
        Front(text, "updated", item.UpdatedAtUtc);
        text.Append("---\n\n").Append(item.Markdown.TrimEnd()).Append('\n');
        return text.ToString();
    }

    private static string MarkdownReadme(DateTimeOffset now) => $"""
        # Chaos export

        Exported {now.UtcDateTime:yyyy-MM-dd HH:mm} UTC.

        - `tasks/<list>/` holds one Markdown file per task. The notes are the original Markdown.
          Front matter (between `---` lines) records the stable ID, list, section, status,
          priority, planned and due dates (date, time, time zone), completion, estimate,
          category, tags (full paths) and the recurrence rule as JSON.
        - Subtasks are a checklist under "Subtasks"; each keeps its ID in an HTML comment.
        - Diary entries are under "Diary", one heading per entry (UTC time).
        - `pad/` holds one file per Personal pad note.

        Deleted items are not exported. History, plans, reminders and settings are only in the
        JSON archive, which is the format to use for a complete, lossless copy.

        """.Replace("\r\n", "\n", StringComparison.Ordinal);

    // ---------------------------------------------------------------- shared

    private static string? DateText(string? date, string? time, string? zone) =>
        date is null ? null : string.Join(' ', new[] { date, time?[..5], zone }.Where(part => part is not null));

    private static void Front(StringBuilder text, string key, string? value) =>
        text.Append(key).Append(": ").Append(value is null ? "null" : JsonSerializer.Serialize(value)).Append('\n');

    private static string OneLine(string value) => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A file-name segment safe on Windows, macOS and Linux: no path separators, reserved
    /// characters, control characters, leading/trailing dots or spaces, or device names; never
    /// empty, never "." or "..", and capped in length.
    /// </summary>
    public static string SafeFileName(string value, int maxLength)
    {
        var builder = new StringBuilder();
        foreach (var ch in value)
        {
            builder.Append(char.IsControl(ch) || ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ? '_' : ch);
        }

        var name = builder.ToString().Trim().Trim('.', ' ');
        if (name.Length > maxLength)
        {
            name = name[..maxLength].TrimEnd('.', ' ');
            if (name.Length > 0 && char.IsHighSurrogate(name[^1]))
            {
                name = name[..^1];
            }
        }

        if (name.Length == 0)
        {
            name = "untitled";
        }

        var stem = name.Split('.')[0];
        return ReservedNames.Contains(stem) ? "_" + name : name;
    }

    private static string Unique(HashSet<string> used, string path)
    {
        if (used.Add(path))
        {
            return path;
        }

        for (var n = 2; ; n++)
        {
            var candidate = path[..^3] + $" {n}.md";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        stream.Write(bytes);
    }
}
