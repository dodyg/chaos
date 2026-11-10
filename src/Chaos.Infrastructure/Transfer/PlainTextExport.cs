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
    public static readonly string[] OrgKeywords = ["TODO", "STARTED", "WAITING", "REVIEW", "DONE", "CANCELLED"];
    public static readonly string[] PriorityNames = ["None", "Low", "Normal", "High", "Urgent"];
    private static readonly string[] OrgPriorityCookies = ["", "D", "C", "B", "A"];
    private static readonly string[] KindNames = ["inbox", "list", "project"];

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

    // ---------------------------------------------------------------- Org

    /// <summary>One Org file: lists as level-1 headings, tasks as level-2 TODO headings, subtasks as level 3.</summary>
    public static string WriteOrg(ExportSnapshot snapshot, DateTimeOffset now)
    {
        var text = new StringBuilder();
        text.Append("#+TITLE: Chaos export\n");
        text.Append("#+DATE: ").Append(Inactive(now.UtcDateTime, withTime: true)).Append('\n');
        text.Append("#+TODO: TODO STARTED WAITING REVIEW | DONE CANCELLED\n");
        text.Append("#+PRIORITIES: A D C\n");
        text.Append("#+PROPERTY: CHAOS_EXPORT_VERSION 1\n");
        text.Append("# Times in LOGBOOK entries and CHAOS_ properties are UTC. Notes are the original Markdown inside src blocks.\n\n");

        var byContainer = snapshot.Tasks.ToLookup(task => task.ContainerId);
        foreach (var container in snapshot.Containers)
        {
            text.Append("* ").Append(SafeHeading(container.Name)).Append('\n');
            text.Append(":PROPERTIES:\n:ID: ").Append(container.Id).Append('\n');
            text.Append(":CHAOS_KIND: ").Append(KindNames[Math.Clamp(container.Kind, 0, 2)]).Append('\n');
            if (NeedsTitleProperty(container.Name))
            {
                text.Append(":CHAOS_TITLE: ").Append(JsonSerializer.Serialize(container.Name)).Append('\n');
            }

            if (container.Archived)
            {
                text.Append(":CHAOS_ARCHIVED: true\n");
            }

            if (container.DeadlineDate is not null)
            {
                text.Append(":CHAOS_DEADLINE: ").Append(container.DeadlineDate).Append('\n');
            }

            text.Append(":END:\n");
            foreach (var task in byContainer[container.Id])
            {
                AppendOrgTask(text, task);
            }
        }

        return text.ToString();
    }

    private static void AppendOrgTask(StringBuilder text, ExportTask task)
    {
        text.Append("** ").Append(OrgKeywords[task.Status]).Append(' ');
        if (OrgPriorityCookies[task.Priority] is { Length: > 0 } cookie)
        {
            text.Append("[#").Append(cookie).Append("] ");
        }

        text.Append(SafeHeading(task.Title));
        var orgTags = task.Tags.Select(OrgTag).Where(tag => tag.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (orgTags.Count > 0)
        {
            text.Append(" :").Append(string.Join(':', orgTags)).Append(':');
        }

        text.Append('\n');
        var planning = new List<string>();
        if (task.PlannedDate is not null)
        {
            planning.Add("SCHEDULED: " + Active(task.PlannedDate, task.PlannedTime));
        }

        if (task.DueDate is not null)
        {
            planning.Add("DEADLINE: " + Active(task.DueDate, task.DueTime));
        }

        if (task.Status is 4 or 5 && (task.CompletedAtUtc ?? task.CancelledAtUtc) is { } closed)
        {
            planning.Add("CLOSED: " + Inactive(DateTimeOffset.Parse(closed, CultureInfo.InvariantCulture).UtcDateTime, withTime: true));
        }

        if (planning.Count > 0)
        {
            text.Append(string.Join(' ', planning)).Append('\n');
        }

        text.Append(":PROPERTIES:\n:ID: ").Append(task.Id).Append('\n');
        if (NeedsTitleProperty(task.Title))
        {
            text.Append(":CHAOS_TITLE: ").Append(JsonSerializer.Serialize(task.Title)).Append('\n');
        }

        if (task.Tags.Count > 0)
        {
            text.Append(":CHAOS_TAGS: ").Append(JsonSerializer.Serialize(task.Tags)).Append('\n');
        }

        Property(text, "CHAOS_SECTION", task.SectionTitle is null ? null : JsonSerializer.Serialize(task.SectionTitle));
        Property(text, "CHAOS_ESTIMATE", task.EstimateMinutes?.ToString(CultureInfo.InvariantCulture));
        Property(text, "CHAOS_CATEGORY", task.CapacityCategory == 1 ? "work" : null);
        Property(text, "CHAOS_PLANNED_ZONE", task.PlannedZoneId);
        Property(text, "CHAOS_DUE_ZONE", task.DueZoneId);
        Property(text, "CHAOS_RECURRENCE", task.RecurrenceRuleJson);
        Property(text, "CHAOS_CREATED", task.CreatedAtUtc);
        text.Append(":END:\n");

        if (task.StatusChanges.Count > 0)
        {
            text.Append(":LOGBOOK:\n");
            foreach (var change in task.StatusChanges.Reverse())
            {
                var at = Inactive(DateTimeOffset.Parse(change.AtUtc, CultureInfo.InvariantCulture).UtcDateTime, withTime: true);
                text.Append("- State \"").Append(OrgKeywords[change.To]).Append('"');
                if (change.From is { } from)
                {
                    text.Append(" from \"").Append(OrgKeywords[from]).Append('"');
                }

                text.Append(' ').Append(at).Append('\n');
            }

            text.Append(":END:\n");
        }

        AppendSource(text, task.Notes, null);
        foreach (var entry in task.Diary)
        {
            AppendSource(text, entry.Markdown, $":chaos-diary {entry.EntryAtUtc}");
        }

        foreach (var subtask in task.Subtasks)
        {
            text.Append("*** ").Append(OrgKeywords[subtask.Status]).Append(' ').Append(SafeHeading(subtask.Title)).Append('\n');
            text.Append(":PROPERTIES:\n:ID: ").Append(subtask.Id).Append('\n');
            if (NeedsTitleProperty(subtask.Title))
            {
                text.Append(":CHAOS_TITLE: ").Append(JsonSerializer.Serialize(subtask.Title)).Append('\n');
            }

            text.Append(":END:\n");
            AppendSource(text, subtask.Notes, null);
        }
    }

    /// <summary>Markdown kept verbatim inside a src block; lines Org would read as structure get the standard comma escape.</summary>
    private static void AppendSource(StringBuilder text, string markdown, string? header)
    {
        if (markdown.Length == 0)
        {
            return;
        }

        text.Append("#+BEGIN_SRC markdown");
        if (header is not null)
        {
            text.Append(' ').Append(header);
        }

        text.Append('\n');
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n'))
        {
            text.Append(OrgEscapeLine(raw)).Append('\n');
        }

        text.Append("#+END_SRC\n");
    }

    public static string OrgEscapeLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith('*') || trimmed.StartsWith("#+", StringComparison.Ordinal)
            || trimmed.StartsWith(",*", StringComparison.Ordinal) || trimmed.StartsWith(",#+", StringComparison.Ordinal)
            ? line[..(line.Length - trimmed.Length)] + "," + trimmed
            : line;
    }

    /// <summary>True when a title could be misread in a heading (keyword, priority cookie, trailing tags, newlines).</summary>
    private static bool NeedsTitleProperty(string title) =>
        SafeHeading(title) != title
        || OrgKeywords.Any(keyword => title == keyword || title.StartsWith(keyword + " ", StringComparison.Ordinal))
        || (title.StartsWith("[#", StringComparison.Ordinal) && title.Length >= 4 && title[3] == ']')
        || System.Text.RegularExpressions.Regex.IsMatch(title, @"\s:[^\s]+:\s*$")
        || title.StartsWith('*');

    private static string SafeHeading(string title) => OneLine(title).Trim();

    private static string OrgTag(string path)
    {
        var builder = new StringBuilder();
        foreach (var ch in path)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '_' or '@' or '#' or '%' ? ch : '_');
        }

        return builder.ToString().Trim('_');
    }

    private static void Property(StringBuilder text, string name, string? value)
    {
        if (value is not null)
        {
            text.Append(':').Append(name).Append(": ").Append(value).Append('\n');
        }
    }

    private static string Active(string date, string? time)
    {
        var day = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var stamp = $"{date} {day.DayOfWeek.ToString()[..3]}";
        return time is null ? $"<{stamp}>" : $"<{stamp} {time[..5]}>";
    }

    private static string Inactive(DateTime utc, bool withTime) =>
        withTime ? $"[{utc:yyyy-MM-dd} {utc.DayOfWeek.ToString()[..3]} {utc:HH:mm}]" : $"[{utc:yyyy-MM-dd} {utc.DayOfWeek.ToString()[..3]}]";

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
