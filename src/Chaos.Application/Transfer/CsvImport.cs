using System.Globalization;
using System.Text;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Transfer;

public sealed record CsvRow(int Line, IReadOnlyList<string> Cells);

public sealed record CsvTable(IReadOnlyList<string> Headers, IReadOnlyList<CsvRow> Rows, char Delimiter);

/// <summary>
/// Mapped CSV import (PLAN §14). The documented columns are title, notes, list, status,
/// planned, due, priority, tags and optional id/parent_id; any other CSV is accepted through a
/// column mapping chosen in the preview. Proprietary to-do app formats are not claimed.
/// Parsing follows RFC 4180 (quoted fields, doubled quotes, line breaks inside quotes) and
/// detects a comma, semicolon or tab delimiter from the header line.
/// </summary>
public static class CsvImport
{
    public const int MaxRows = 50_000;

    public static readonly IReadOnlyList<string> Fields = ["title", "notes", "list", "status", "planned", "due", "priority", "tags", "id", "parent_id"];

    private static readonly IReadOnlyDictionary<string, string> HeaderAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = "title", ["name"] = "title", ["task"] = "title", ["task name"] = "title", ["content"] = "title", ["subject"] = "title",
        ["notes"] = "notes", ["note"] = "notes", ["description"] = "notes", ["details"] = "notes", ["body"] = "notes",
        ["list"] = "list", ["project"] = "list", ["folder"] = "list", ["list name"] = "list",
        ["status"] = "status", ["state"] = "status", ["completed"] = "status", ["done"] = "status",
        ["planned"] = "planned", ["start"] = "planned", ["start date"] = "planned", ["scheduled"] = "planned", ["planned date"] = "planned",
        ["due"] = "due", ["due date"] = "due", ["deadline"] = "due",
        ["priority"] = "priority", ["importance"] = "priority",
        ["tags"] = "tags", ["tag"] = "tags", ["labels"] = "tags", ["label"] = "tags", ["categories"] = "tags",
        ["id"] = "id", ["task id"] = "id",
        ["parent_id"] = "parent_id", ["parent id"] = "parent_id", ["parent"] = "parent_id",
    };

    /// <summary>Splits CSV text into a header row and data rows. Throws a validation error for an empty or unterminated file.</summary>
    public static CsvTable ParseTable(string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var firstLine = text.Split('\n', 2)[0];
        var delimiter = new[] { ',', ';', '\t' }.OrderByDescending(candidate => firstLine.Count(ch => ch == candidate)).First();

        var records = new List<CsvRow>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var recordLine = 1;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        cell.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (ch == '\n')
                    {
                        line++;
                    }

                    cell.Append(ch);
                }

                continue;
            }

            if (ch == '"' && cell.Length == 0)
            {
                inQuotes = true;
            }
            else if (ch == delimiter)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                cells.Add(cell.ToString());
                cell.Clear();
                records.Add(new CsvRow(recordLine, [.. cells]));
                cells.Clear();
                line++;
                recordLine = line;
                if (records.Count > MaxRows + 1)
                {
                    throw Chaos.Application.Abstractions.AppException.Validation("file", $"This file has more than {MaxRows} rows, which is more than one import accepts. Split it into smaller files.");
                }
            }
            else
            {
                cell.Append(ch);
            }
        }

        if (inQuotes)
        {
            throw Chaos.Application.Abstractions.AppException.Validation("file", $"A quoted value starting on line {recordLine} is never closed.");
        }

        if (cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(cell.ToString());
            records.Add(new CsvRow(recordLine, [.. cells]));
        }

        records.RemoveAll(record => record.Cells.All(string.IsNullOrWhiteSpace));
        if (records.Count == 0)
        {
            throw Chaos.Application.Abstractions.AppException.Validation("file", "This file is empty.");
        }

        return new CsvTable([.. records[0].Cells.Select(header => header.Trim())], [.. records.Skip(1)], delimiter);
    }

    /// <summary>Suggested column for each field, by header name; fields without a match are absent.</summary>
    public static IReadOnlyDictionary<string, int> SuggestMapping(IReadOnlyList<string> headers)
    {
        var mapping = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < headers.Count; index++)
        {
            if (HeaderAliases.TryGetValue(headers[index].Trim(), out var field) && !mapping.ContainsKey(field))
            {
                mapping[field] = index;
            }
        }

        return mapping;
    }

    /// <summary>Reads every row with the chosen mapping. Problems are reported per line; nothing is written.</summary>
    public static ImportDraft ToDraft(string sourceName, string fileFingerprint, CsvTable table, IReadOnlyDictionary<string, int> mapping)
    {
        var errors = new List<ImportIssue>();
        var warnings = new List<ImportIssue>();
        foreach (var (field, column) in mapping)
        {
            if (!Fields.Contains(field))
            {
                errors.Add(new ImportIssue(0, field, $"\"{field}\" is not a field Chaos imports."));
            }
            else if (column < 0 || column >= table.Headers.Count)
            {
                errors.Add(new ImportIssue(0, field, $"The column chosen for {field} does not exist."));
            }
        }

        if (!mapping.ContainsKey("title"))
        {
            errors.Add(new ImportIssue(0, "title", "Choose the column that holds task titles."));
            return new ImportDraft(ImportSourceKind.Csv, sourceName, fileFingerprint, [], errors, warnings);
        }

        if (errors.Count > 0)
        {
            return new ImportDraft(ImportSourceKind.Csv, sourceName, fileFingerprint, [], errors, warnings);
        }

        string Cell(CsvRow row, string field) =>
            mapping.TryGetValue(field, out var column) && column < row.Cells.Count ? row.Cells[column].Trim() : string.Empty;
        string RawCell(CsvRow row, string field) =>
            mapping.TryGetValue(field, out var column) && column < row.Cells.Count ? row.Cells[column] : string.Empty;

        var parsed = new List<(CsvRow Row, ImportTask Task, string Id, string ParentId)>();
        foreach (var row in table.Rows)
        {
            if (row.Cells.Count > table.Headers.Count)
            {
                warnings.Add(new ImportIssue(row.Line, "row", "This row has more values than the header; the extra values were ignored."));
            }

            var title = Cell(row, "title");
            if (title.Length == 0)
            {
                errors.Add(new ImportIssue(row.Line, "title", "This row has no title."));
                continue;
            }

            var status = ParseStatus(Cell(row, "status"), row.Line, warnings);
            var priority = ParsePriority(Cell(row, "priority"), row.Line, warnings);
            var planned = ParseDate(Cell(row, "planned"), row.Line, "planned", errors);
            var due = ParseDate(Cell(row, "due"), row.Line, "due", errors);
            var tags = Cell(row, "tags").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
            var id = Cell(row, "id");
            var list = Cell(row, "list");
            var notes = RawCell(row, "notes").Replace("\r\n", "\n", StringComparison.Ordinal);

            parsed.Add((row, new ImportTask
            {
                Line = row.Line,
                SourceKey = id.Length > 0
                    ? id
                    : ContentKey(title, list, notes, Cell(row, "planned"), Cell(row, "due"), Cell(row, "parent_id")),
                HasStableKey = id.Length > 0,
                Title = title,
                Notes = notes,
                ListName = list.Length == 0 ? null : list,
                Status = status,
                Priority = priority,
                Planned = planned,
                Due = due,
                TagPaths = tags,
            }, id, Cell(row, "parent_id")));
        }

        var duplicateIds = parsed.Where(item => item.Id.Length > 0).GroupBy(item => item.Id, StringComparer.Ordinal).Where(group => group.Count() > 1);
        foreach (var group in duplicateIds)
        {
            foreach (var item in group.Skip(1))
            {
                errors.Add(new ImportIssue(item.Row.Line, "id", $"The id \"{group.Key}\" is used by more than one row."));
            }
        }

        var byId = parsed.Where(item => item.Id.Length > 0).GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var subtasks = new Dictionary<string, List<ImportSubtask>>(StringComparer.Ordinal);
        var topLevel = new List<(CsvRow Row, ImportTask Task, string Id)>();
        foreach (var item in parsed)
        {
            if (item.ParentId.Length == 0)
            {
                topLevel.Add((item.Row, item.Task, item.Id));
                continue;
            }

            if (!byId.TryGetValue(item.ParentId, out var parent))
            {
                errors.Add(new ImportIssue(item.Row.Line, "parent_id", $"No row has the id \"{item.ParentId}\"."));
            }
            else if (parent.ParentId.Length > 0)
            {
                errors.Add(new ImportIssue(item.Row.Line, "parent_id", "A subtask's parent must be a top-level task; Chaos subtasks cannot have subtasks."));
            }
            else
            {
                if (item.Task.Planned is not null || item.Task.Due is not null || item.Task.TagPaths.Count > 0)
                {
                    warnings.Add(new ImportIssue(item.Row.Line, "parent_id", "Subtasks keep only their title, notes and status; this row's dates and tags were not imported."));
                }

                if (!subtasks.TryGetValue(item.ParentId, out var list))
                {
                    subtasks[item.ParentId] = list = [];
                }

                list.Add(new ImportSubtask(item.Task.Title, item.Task.Notes, item.Task.Status));
            }
        }

        var tasks = topLevel
            .Select(item => item.Id.Length > 0 && subtasks.TryGetValue(item.Id, out var children) ? item.Task with { Subtasks = children } : item.Task)
            .ToList();
        return new ImportDraft(ImportSourceKind.Csv, sourceName, fileFingerprint, tasks, errors, warnings);
    }

    private static string ContentKey(params string[] parts) =>
        "row:" + string.Join('\u001F', parts.Select(part => part.Trim().ToLowerInvariant()));

    internal static TaskItemStatus ParseStatus(string raw, int line, List<ImportIssue> warnings)
    {
        var value = raw.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
        switch (value)
        {
            case "" or "todo" or "to do" or "not started" or "notstarted" or "open" or "new" or "false" or "0" or "no" or "incomplete":
                return TaskItemStatus.NotStarted;
            case "in progress" or "inprogress" or "started" or "doing" or "active":
                return TaskItemStatus.InProgress;
            case "waiting" or "waiting on someone" or "waitingonsomeone" or "blocked" or "on hold":
                return TaskItemStatus.WaitingOnSomeone;
            case "needs review" or "needsreview" or "review" or "in review":
                return TaskItemStatus.NeedsReview;
            case "done" or "completed" or "complete" or "finished" or "closed" or "x" or "true" or "1" or "yes":
                return TaskItemStatus.Done;
            case "cancelled" or "canceled" or "dropped" or "wontdo" or "won't do":
                return TaskItemStatus.Cancelled;
            default:
                warnings.Add(new ImportIssue(line, "status", $"\"{raw}\" is not a status Chaos knows; the task was imported as not started."));
                return TaskItemStatus.NotStarted;
        }
    }

    internal static TaskPriority ParsePriority(string raw, int line, List<ImportIssue> warnings)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "": return TaskPriority.Normal;
            case "none" or "0": return TaskPriority.None;
            case "low" or "1": return TaskPriority.Low;
            case "normal" or "medium" or "2": return TaskPriority.Normal;
            case "high" or "important" or "3": return TaskPriority.High;
            case "urgent" or "highest" or "critical" or "4": return TaskPriority.Urgent;
            default:
                warnings.Add(new ImportIssue(line, "priority", $"\"{raw}\" is not a priority Chaos knows; Normal was used."));
                return TaskPriority.Normal;
        }
    }

    private static readonly string[] DateTimeFormats = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"];

    /// <summary>ISO dates (yyyy-MM-dd) with an optional local time; other formats are refused rather than guessed.</summary>
    internal static ImportDate? ParseDate(string raw, int line, string field, List<ImportIssue> errors)
    {
        var value = raw.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return new ImportDate(date, null, null);
        }

        if (DateTime.TryParseExact(value, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return new ImportDate(DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local), null);
        }

        errors.Add(new ImportIssue(line, field, $"\"{raw}\" is not a date Chaos can read. Use yyyy-MM-dd, optionally followed by a time (yyyy-MM-dd HH:mm)."));
        return null;
    }
}
