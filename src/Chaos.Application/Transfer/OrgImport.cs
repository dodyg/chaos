using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Transfer;

/// <summary>
/// Reads the documented Org subset (PLAN §14, docs/data-transfer.md): headings with a TODO
/// keyword are tasks, keyword headings below a task are its subtasks (deeper levels are
/// flattened with a warning), and headings without a keyword are lists. Supported: the six
/// Chaos keywords plus custom ones declared with #+TODO, [#A]–[#D] priority cookies, heading
/// tags, SCHEDULED/DEADLINE/CLOSED, the PROPERTIES drawer (ID and the CHAOS_ properties that
/// Chaos writes), and notes as plain text or a markdown src block. Org repeaters and
/// CHAOS_RECURRENCE are not turned into recurrence; a warning says so. LOGBOOK drawers are skipped.
/// </summary>
public static partial class OrgImport
{
    private static readonly Dictionary<string, TaskItemStatus> BuiltInKeywords = new(StringComparer.Ordinal)
    {
        ["TODO"] = TaskItemStatus.NotStarted,
        ["NEXT"] = TaskItemStatus.NotStarted,
        ["STARTED"] = TaskItemStatus.InProgress,
        ["WAITING"] = TaskItemStatus.WaitingOnSomeone,
        ["REVIEW"] = TaskItemStatus.NeedsReview,
        ["DONE"] = TaskItemStatus.Done,
        ["CANCELLED"] = TaskItemStatus.Cancelled,
        ["CANCELED"] = TaskItemStatus.Cancelled,
    };

    [GeneratedRegex(@"^(\*+)\s+(.*?)\s*$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^(.*?)\s+(:(?:[^\s:]+:)+)\s*$")]
    private static partial Regex TagsPattern();

    [GeneratedRegex(@"^\[#([A-Za-z0-9])\]\s*")]
    private static partial Regex PriorityPattern();

    [GeneratedRegex(@"(SCHEDULED|DEADLINE|CLOSED):\s*([<\[][^>\]]*[>\]])")]
    private static partial Regex PlanningPattern();

    [GeneratedRegex(@"^[<\[](\d{4}-\d{2}-\d{2})(?:\s+[^\s\d>\]]+)?(?:\s+(\d{1,2}:\d{2}))?(?:-\d{1,2}:\d{2})?(\s+[.+]?\+\d+[hdwmy])?.*[>\]]$")]
    private static partial Regex TimestampPattern();

    [GeneratedRegex(@"^:([A-Za-z0-9_\-]+):\s*(.*)$")]
    private static partial Regex PropertyPattern();

    private sealed class Heading
    {
        public required int Level;
        public required int Line;
        public string? Keyword;
        public required string Title;
        public TaskPriority Priority = TaskPriority.Normal;
        public TaskItemStatus Status = TaskItemStatus.NotStarted;
        public List<string> OrgTags = [];
        public Dictionary<string, string> Properties = new(StringComparer.OrdinalIgnoreCase);
        public string? Scheduled;
        public string? Deadline;
        public string? Closed;
        public StringBuilder Notes = new();
        public List<ImportDiaryEntry> Diary = [];
    }

    public static ImportDraft Parse(string sourceName, string fileFingerprint, string text)
    {
        var errors = new List<ImportIssue>();
        var warnings = new List<ImportIssue>();
        var keywords = new Dictionary<string, TaskItemStatus>(BuiltInKeywords, StringComparer.Ordinal);
        var headings = new List<Heading>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        Heading? current = null;
        string? drawer = null;
        StringBuilder? block = null;
        string? blockHeader = null;
        var blockLine = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index];
            var trimmed = line.Trim();

            if (block is not null)
            {
                if (trimmed.Equals("#+END_SRC", StringComparison.OrdinalIgnoreCase))
                {
                    FinishBlock(current, block.ToString(), blockHeader, blockLine, warnings);
                    block = null;
                }
                else
                {
                    block.Append(Unescape(line)).Append('\n');
                }

                continue;
            }

            if (drawer is not null)
            {
                if (trimmed.Equals(":END:", StringComparison.OrdinalIgnoreCase))
                {
                    drawer = null;
                }
                else if (drawer == "PROPERTIES" && current is not null && PropertyPattern().Match(trimmed) is { Success: true } property)
                {
                    current.Properties[property.Groups[1].Value] = property.Groups[2].Value.Trim();
                }

                continue;
            }

            if (HeadingPattern().Match(line) is { Success: true } heading)
            {
                current = ReadHeading(heading.Groups[1].Value.Length, heading.Groups[2].Value, lineNumber, keywords);
                headings.Add(current);
                continue;
            }

            if (trimmed.StartsWith("#+TODO:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("#+SEQ_TODO:", StringComparison.OrdinalIgnoreCase))
            {
                ReadTodoLine(trimmed[(trimmed.IndexOf(':') + 1)..], keywords);
                continue;
            }

            if (trimmed.StartsWith("#+BEGIN_SRC", StringComparison.OrdinalIgnoreCase))
            {
                block = new StringBuilder();
                blockHeader = trimmed["#+BEGIN_SRC".Length..].Trim();
                blockLine = lineNumber;
                continue;
            }

            if (current is null)
            {
                continue; // file preamble: #+TITLE and the like
            }

            if (trimmed.Length > 2 && trimmed[0] == ':' && trimmed[^1] == ':' && !trimmed.Contains(' '))
            {
                drawer = trimmed.Trim(':').ToUpperInvariant();
                continue;
            }

            if (PlanningPattern().IsMatch(trimmed) && PlanningPattern().Replace(trimmed, "").Trim().Length == 0)
            {
                foreach (Match match in PlanningPattern().Matches(trimmed))
                {
                    switch (match.Groups[1].Value)
                    {
                        case "SCHEDULED": current.Scheduled = match.Groups[2].Value; break;
                        case "DEADLINE": current.Deadline = match.Groups[2].Value; break;
                        default: current.Closed = match.Groups[2].Value; break;
                    }
                }

                continue;
            }

            if (trimmed.StartsWith("#", StringComparison.Ordinal) && !trimmed.StartsWith("#+", StringComparison.Ordinal) && trimmed.Length > 1 && trimmed[1] == ' ')
            {
                continue; // Org comment line
            }

            current.Notes.Append(line).Append('\n');
        }

        if (block is not null)
        {
            errors.Add(new ImportIssue(blockLine, "notes", "A source block starting here is never closed with #+END_SRC."));
        }

        return Build(sourceName, fileFingerprint, headings, errors, warnings);
    }

    private static Heading ReadHeading(int level, string text, int line, Dictionary<string, TaskItemStatus> keywords)
    {
        var heading = new Heading { Level = level, Line = line, Title = text };
        var firstSpace = text.IndexOf(' ');
        var firstWord = firstSpace < 0 ? text : text[..firstSpace];
        if (keywords.ContainsKey(firstWord))
        {
            heading.Keyword = firstWord;
            heading.Status = keywords[firstWord];
            text = firstSpace < 0 ? string.Empty : text[(firstSpace + 1)..].TrimStart();
        }

        if (PriorityPattern().Match(text) is { Success: true } priority)
        {
            heading.Priority = char.ToUpperInvariant(priority.Groups[1].Value[0]) switch
            {
                'A' => TaskPriority.Urgent,
                'B' => TaskPriority.High,
                'C' => TaskPriority.Normal,
                'D' => TaskPriority.Low,
                _ => TaskPriority.Normal,
            };
            text = text[priority.Length..];
        }

        if (TagsPattern().Match(text) is { Success: true } tags)
        {
            heading.OrgTags = [.. tags.Groups[2].Value.Split(':', StringSplitOptions.RemoveEmptyEntries)];
            text = tags.Groups[1].Value;
        }

        heading.Title = text.Trim();
        return heading;
    }

    private static void ReadTodoLine(string value, Dictionary<string, TaskItemStatus> keywords)
    {
        var done = false;
        foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word == "|")
            {
                done = true;
                continue;
            }

            var keyword = word.Split('(')[0];
            if (keyword.Length > 0 && !keywords.ContainsKey(keyword))
            {
                keywords[keyword] = done ? TaskItemStatus.Done : TaskItemStatus.NotStarted;
            }
        }
    }

    private static void FinishBlock(Heading? current, string content, string? header, int line, List<ImportIssue> warnings)
    {
        if (current is null)
        {
            return;
        }

        content = content.TrimEnd('\n');
        var diaryIndex = header?.IndexOf(":chaos-diary", StringComparison.OrdinalIgnoreCase) ?? -1;
        if (diaryIndex >= 0)
        {
            var instant = header![(diaryIndex + ":chaos-diary".Length)..].Trim().Split(' ')[0];
            if (DateTimeOffset.TryParse(instant, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            {
                current.Diary.Add(new ImportDiaryEntry(at, content));
                return;
            }

            warnings.Add(new ImportIssue(line, "diary", "A diary block has no readable time; its text was added to the notes."));
        }

        current.Notes.Append(content).Append('\n');
    }

    private static string Unescape(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith(",*", StringComparison.Ordinal) || trimmed.StartsWith(",#+", StringComparison.Ordinal)
            ? line[..(line.Length - trimmed.Length)] + trimmed[1..]
            : line;
    }

    private static ImportDraft Build(string sourceName, string fileFingerprint, List<Heading> headings, List<ImportIssue> errors, List<ImportIssue> warnings)
    {
        var tasks = new List<ImportTask>();
        var stack = new List<Heading>();
        var subtasksOf = new Dictionary<Heading, List<ImportSubtask>>();
        var listOf = new Dictionary<Heading, Heading?>();

        foreach (var heading in headings)
        {
            while (stack.Count > 0 && stack[^1].Level >= heading.Level)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            if (heading.Keyword is not null)
            {
                var parentTask = stack.FirstOrDefault(item => item.Keyword is not null);
                if (parentTask is null)
                {
                    listOf[heading] = stack.LastOrDefault(item => item.Keyword is null);
                    subtasksOf[heading] = [];
                }
                else
                {
                    if (stack.Count(item => item.Keyword is not null) > 1)
                    {
                        warnings.Add(new ImportIssue(heading.Line, "heading", "Chaos subtasks cannot have subtasks; this heading became a subtask of its top-level task."));
                    }

                    if (heading.Scheduled is not null || heading.Deadline is not null || heading.OrgTags.Count > 0)
                    {
                        warnings.Add(new ImportIssue(heading.Line, "heading", "Subtasks keep only their title, notes and status; this heading's dates and tags were not imported."));
                    }

                    subtasksOf[parentTask].Add(new ImportSubtask(TitleOf(heading), heading.Notes.ToString().Trim('\n'), Status(heading)));
                }
            }

            stack.Add(heading);
        }

        foreach (var heading in headings.Where(item => item.Keyword is not null && subtasksOf.ContainsKey(item)))
        {
            var title = TitleOf(heading);
            if (title.Length == 0)
            {
                errors.Add(new ImportIssue(heading.Line, "title", "This heading has no title."));
                continue;
            }

            var list = listOf[heading];
            heading.Properties.TryGetValue("ID", out var id);
            var tags = ReadJsonList(heading, "CHAOS_TAGS", warnings) ?? heading.OrgTags;
            if (heading.Properties.ContainsKey("CHAOS_RECURRENCE") || (heading.Scheduled ?? heading.Deadline ?? "").Contains('+'))
            {
                warnings.Add(new ImportIssue(heading.Line, "recurrence", "Repeating dates are imported as a single date; recurrence is only restored from a Chaos JSON archive."));
            }

            tasks.Add(new ImportTask
            {
                Line = heading.Line,
                SourceKey = string.IsNullOrWhiteSpace(id) ? $"heading:{(list is null ? "" : TitleOf(list))}\u001F{title}\u001F{heading.Scheduled}\u001F{heading.Deadline}" : id.Trim(),
                HasStableKey = !string.IsNullOrWhiteSpace(id),
                Title = title,
                Notes = heading.Notes.ToString().Trim('\n'),
                ListName = list is null ? null : TitleOf(list),
                ListKind = list is not null && list.Properties.TryGetValue("CHAOS_KIND", out var listKind) && listKind.Trim().Equals("project", StringComparison.OrdinalIgnoreCase) ? ContainerKind.Project : ContainerKind.List,
                ListDeadline = list is not null && list.Properties.TryGetValue("CHAOS_DEADLINE", out var listDeadline)
                    && DateOnly.TryParseExact(listDeadline.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDeadline) ? parsedDeadline : null,
                ListDefaultCategory = list is not null && list.Properties.TryGetValue("CHAOS_DEFAULT_CATEGORY", out var listCategory)
                    ? listCategory.Trim().ToLowerInvariant() switch { "work" => CapacityCategory.Work, "personal" => CapacityCategory.Personal, _ => null }
                    : null,
                Status = Status(heading),
                Priority = heading.Priority,
                EstimateMinutes = heading.Properties.TryGetValue("CHAOS_ESTIMATE", out var estimate) && int.TryParse(estimate, CultureInfo.InvariantCulture, out var minutes) ? minutes : null,
                Category = heading.Properties.TryGetValue("CHAOS_CATEGORY", out var category) && category.Equals("work", StringComparison.OrdinalIgnoreCase) ? CapacityCategory.Work : CapacityCategory.Personal,
                Planned = ReadDate(heading.Scheduled, heading, "CHAOS_PLANNED_ZONE", errors),
                Due = ReadDate(heading.Deadline, heading, "CHAOS_DUE_ZONE", errors),
                ClosedAtUtc = ReadClosed(heading.Closed),
                TagPaths = [.. tags.Distinct(StringComparer.Ordinal)],
                Subtasks = subtasksOf[heading],
                Diary = heading.Diary,
            });
        }

        if (headings.Count == 0)
        {
            errors.Add(new ImportIssue(0, "file", "This file has no Org headings."));
        }

        return new ImportDraft(ImportSourceKind.Org, sourceName, fileFingerprint, tasks, errors, warnings);
    }

    private static string TitleOf(Heading heading) =>
        heading.Properties.TryGetValue("CHAOS_TITLE", out var exact) && TryJsonString(exact) is { } title ? title : heading.Title;

    private static TaskItemStatus Status(Heading heading) => heading.Status;

    private static IReadOnlyList<string>? ReadJsonList(Heading heading, string property, List<ImportIssue> warnings)
    {
        if (!heading.Properties.TryGetValue(property, out var json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            warnings.Add(new ImportIssue(heading.Line, property, $"The {property} property is unreadable; the heading's Org tags were used."));
            return null;
        }
    }

    private static string? TryJsonString(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ImportDate? ReadDate(string? stamp, Heading heading, string zoneProperty, List<ImportIssue> errors)
    {
        if (stamp is null)
        {
            return null;
        }

        var match = TimestampPattern().Match(stamp.Trim());
        if (!match.Success || !DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            errors.Add(new ImportIssue(heading.Line, "date", $"\"{stamp}\" is not an Org timestamp Chaos can read."));
            return null;
        }

        TimeOnly? time = match.Groups[2].Success && TimeOnly.TryParseExact(match.Groups[2].Value.PadLeft(5, '0'), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
        heading.Properties.TryGetValue(zoneProperty, out var zone);
        return new ImportDate(date, time, time is null || string.IsNullOrWhiteSpace(zone) ? null : zone.Trim());
    }

    private static DateTimeOffset? ReadClosed(string? stamp)
    {
        if (stamp is null || TimestampPattern().Match(stamp.Trim()) is not { Success: true } match)
        {
            return null;
        }

        var date = DateOnly.ParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var time = match.Groups[2].Success ? TimeOnly.ParseExact(match.Groups[2].Value.PadLeft(5, '0'), "HH:mm", CultureInfo.InvariantCulture) : TimeOnly.MinValue;
        return new DateTimeOffset(date.ToDateTime(time), TimeSpan.Zero);
    }
}
