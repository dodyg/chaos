using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Transfer;

public sealed record ExportContainer(string Id, string Name, int Kind, bool Archived, string? DeadlineDate, int? DefaultCategory = null);

public sealed record ExportSubtask(string Id, string Title, string Notes, int Status, IReadOnlyList<string> Tags);

public sealed record ExportDiaryEntry(string Id, string EntryAtUtc, string Markdown);

public sealed record ExportTask
{
    public required string Id { get; init; }
    public required string ContainerId { get; init; }
    public string? SectionTitle { get; init; }
    public required string Title { get; init; }
    public required string Notes { get; init; }
    public required int Status { get; init; }
    public required int Priority { get; init; }
    public int? EstimateMinutes { get; init; }
    public required int CapacityCategory { get; init; }
    public string? PlannedDate { get; init; }
    public string? PlannedTime { get; init; }
    public string? PlannedZoneId { get; init; }
    public string? DueDate { get; init; }
    public string? DueTime { get; init; }
    public string? DueZoneId { get; init; }
    public string? CompletedAtUtc { get; init; }
    public string? CancelledAtUtc { get; init; }
    public required string CreatedAtUtc { get; init; }
    public required string UpdatedAtUtc { get; init; }
    public string? RecurrenceRuleJson { get; init; }
    public string? GitHubUrl { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public required IReadOnlyList<ExportSubtask> Subtasks { get; init; }
    public required IReadOnlyList<ExportDiaryEntry> Diary { get; init; }
}

/// <summary>The strokes of a drawing pad item (PLAN2 P10), exported as a JSON file beside the Markdown.</summary>
public sealed record ExportDrawing(string StrokesJson, int Width, int Height, string? PreviewAttachmentId);

public sealed record ExportPadItem(string Id, string Markdown, bool Archived, string CreatedAtUtc, string UpdatedAtUtc, IReadOnlyList<string> Tags, ExportDrawing? Drawing = null);

/// <summary>One active page: tree pages carry their parent, daily pages their date.</summary>
public sealed record ExportPage(string Id, string? ParentId, string Title, string Markdown, string? DailyDate, string CreatedAtUtc, string UpdatedAtUtc);

/// <summary>One active attachment whose owner is active too.</summary>
public sealed record ExportAttachment(string Id, int OwnerKind, string OwnerId, string FileName, string Sha256);

/// <summary>Active (not deleted) data read once, in display order, for the plain-text exports.</summary>
public sealed record ExportSnapshot(IReadOnlyList<ExportContainer> Containers, IReadOnlyList<ExportTask> Tasks, IReadOnlyList<ExportPadItem> PadItems, IReadOnlyList<ExportPage>? Pages = null, IReadOnlyList<ExportAttachment>? Attachments = null)
{
    /// <summary>Reads the snapshot inside one read transaction, so every part agrees.</summary>
    public static async Task<ExportSnapshot> ReadAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var snapshot = connection.BeginTransaction(deferred: true);

        var tagPaths = await TagPathsAsync(connection, ct);
        var githubUrls = (await ListAsync(connection,
            "SELECT TaskId, HtmlUrl FROM GitHubIssueLinks WHERE TaskId IS NOT NULL;",
            r => (TaskId: r.GetString(0), Url: r.GetString(1)), ct))
            .ToDictionary(row => row.TaskId, row => row.Url, StringComparer.Ordinal);
        var containers = await ListAsync(connection, """
            SELECT Id, Name, Kind, ArchivedAtUtc, DeadlineDate, DefaultCategory FROM Containers
            WHERE DeletedAtUtc IS NULL ORDER BY IsInbox DESC, Position, Id;
            """, r => new ExportContainer(r.GetString(0), r.GetString(1), r.GetInt32(2), !r.IsDBNull(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetInt32(5)), ct);

        var taskTags = await GroupAsync(connection, "SELECT TaskId, TagId FROM TaskTags WHERE DeletedAtUtc IS NULL;", tagPaths, ct);
        var subtaskTags = await GroupAsync(connection, "SELECT SubtaskId, TagId FROM SubtaskTags WHERE DeletedAtUtc IS NULL;", tagPaths, ct);
        var padTags = await GroupAsync(connection, "SELECT PadItemId, TagId FROM ScratchPadTags WHERE DeletedAtUtc IS NULL;", tagPaths, ct);

        var subtasks = (await ListAsync(connection, """
            SELECT TaskId, Id, Title, Notes, Status FROM Subtasks WHERE DeletedAtUtc IS NULL ORDER BY TaskId, Position, Id;
            """, r => (TaskId: r.GetString(0), Item: new ExportSubtask(r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), subtaskTags.GetValueOrDefault(r.GetString(1)) ?? [])), ct))
            .ToLookup(x => x.TaskId, x => x.Item);

        var diary = (await ListAsync(connection, """
            SELECT TaskId, Id, EntryAtUtc, RawMarkdown FROM DiaryEntries WHERE DeletedAtUtc IS NULL ORDER BY TaskId, EntryAtUtc, Id;
            """, r => (TaskId: r.GetString(0), Item: new ExportDiaryEntry(r.GetString(1), r.GetString(2), r.GetString(3))), ct))
            .ToLookup(x => x.TaskId, x => x.Item);

        var rules = (await ListAsync(connection, """
            SELECT o.TaskId, s.RuleJson FROM RecurrenceOccurrences o JOIN RecurrenceSeries s ON s.Id = o.SeriesId
            WHERE o.TaskId IS NOT NULL AND s.DeletedAtUtc IS NULL;
            """, r => (TaskId: r.GetString(0), Rule: r.GetString(1)), ct))
            .GroupBy(x => x.TaskId).ToDictionary(g => g.Key, g => g.First().Rule, StringComparer.Ordinal);

        var tasks = await ListAsync(connection, """
            SELECT t.Id, t.ContainerId, s.Title, t.Title, t.Notes, t.Status, t.Priority, t.EstimateMinutes, t.CapacityCategory,
                   t.PlannedDate, t.PlannedTime, t.PlannedZoneId, t.DueDate, t.DueTime, t.DueZoneId,
                   t.CompletedAtUtc, t.CancelledAtUtc, t.CreatedAtUtc, t.UpdatedAtUtc
            FROM Tasks t
            JOIN Containers c ON c.Id = t.ContainerId
            LEFT JOIN Sections s ON s.Id = t.SectionId AND s.DeletedAtUtc IS NULL
            WHERE t.DeletedAtUtc IS NULL AND c.DeletedAtUtc IS NULL
            ORDER BY c.IsInbox DESC, c.Position, c.Id, s.Position, t.Position, t.Id;
            """, r =>
            {
                string? Text(int index) => r.IsDBNull(index) ? null : r.GetString(index);
                var id = r.GetString(0);
                return new ExportTask
                {
                    Id = id,
                    ContainerId = r.GetString(1),
                    SectionTitle = Text(2),
                    Title = r.GetString(3),
                    Notes = r.GetString(4),
                    Status = r.GetInt32(5),
                    Priority = r.GetInt32(6),
                    EstimateMinutes = r.IsDBNull(7) ? null : r.GetInt32(7),
                    CapacityCategory = r.GetInt32(8),
                    PlannedDate = Text(9),
                    PlannedTime = Text(10),
                    PlannedZoneId = Text(11),
                    DueDate = Text(12),
                    DueTime = Text(13),
                    DueZoneId = Text(14),
                    CompletedAtUtc = Text(15),
                    CancelledAtUtc = Text(16),
                    CreatedAtUtc = r.GetString(17),
                    UpdatedAtUtc = r.GetString(18),
                    RecurrenceRuleJson = rules.GetValueOrDefault(id),
                    GitHubUrl = githubUrls.GetValueOrDefault(id),
                    Tags = taskTags.GetValueOrDefault(id) ?? [],
                    Subtasks = [.. subtasks[id]],
                    Diary = [.. diary[id]],
                };
            }, ct);

        var pad = await ListAsync(connection, """
            SELECT p.Id, p.RawMarkdown, p.ArchivedAtUtc, p.CreatedAtUtc, p.UpdatedAtUtc, d.StrokesJson, d.Width, d.Height, d.PreviewAttachmentId
            FROM ScratchPadItems p LEFT JOIN PadDrawings d ON d.PadItemId = p.Id AND p.Kind = 1
            WHERE p.DeletedAtUtc IS NULL ORDER BY p.ArchivedAtUtc IS NOT NULL, p.Position, p.Id;
            """, r => new ExportPadItem(r.GetString(0), r.GetString(1), !r.IsDBNull(2), r.GetString(3), r.GetString(4), padTags.GetValueOrDefault(r.GetString(0)) ?? [],
                r.IsDBNull(5) ? null : new ExportDrawing(r.GetString(5), r.GetInt32(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8))), ct);

        var pages = await ListAsync(connection, """
            SELECT Id, ParentPageId, Title, RawMarkdown, DailyDate, CreatedAtUtc, UpdatedAtUtc FROM Pages
            WHERE DeletedAtUtc IS NULL ORDER BY DailyDate IS NOT NULL, DailyDate, Position, Id;
            """, r => new ExportPage(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6)), ct);

        var attachments = await ListAsync(connection, """
            SELECT a.Id, a.OwnerKind, a.OwnerId, a.FileName, a.Sha256 FROM Attachments a
            WHERE a.DeletedAtUtc IS NULL AND (
                (a.OwnerKind = 1 AND EXISTS (SELECT 1 FROM Tasks t WHERE t.Id = a.OwnerId AND t.DeletedAtUtc IS NULL))
             OR (a.OwnerKind = 2 AND EXISTS (SELECT 1 FROM DiaryEntries d JOIN Tasks t ON t.Id = d.TaskId WHERE d.Id = a.OwnerId AND d.DeletedAtUtc IS NULL AND t.DeletedAtUtc IS NULL))
             OR (a.OwnerKind = 3 AND EXISTS (SELECT 1 FROM ScratchPadItems p WHERE p.Id = a.OwnerId AND p.DeletedAtUtc IS NULL))
             OR (a.OwnerKind = 4 AND EXISTS (SELECT 1 FROM Pages g WHERE g.Id = a.OwnerId AND g.DeletedAtUtc IS NULL)))
            ORDER BY a.CreatedAtUtc, a.Id;
            """, r => new ExportAttachment(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4)), ct);

        snapshot.Commit();
        return new ExportSnapshot(containers, tasks, pad, pages, attachments);
    }

    /// <summary>Full slash-delimited display paths of active tags, by tag ID.</summary>
    private static async Task<Dictionary<string, string>> TagPathsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var tags = await ListAsync(connection, "SELECT Id, DisplayName, ParentTagId FROM Tags WHERE DeletedAtUtc IS NULL;",
            r => (Id: r.GetString(0), Name: r.GetString(1), Parent: r.IsDBNull(2) ? null : r.GetString(2)), ct);
        var byId = tags.ToDictionary(tag => tag.Id, StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            var segments = new List<string>();
            var current = tag;
            var guard = 0;
            while (guard++ < 64)
            {
                segments.Add(current.Name);
                if (current.Parent is null || !byId.TryGetValue(current.Parent, out var parent))
                {
                    break;
                }

                current = parent;
            }

            segments.Reverse();
            paths[tag.Id] = string.Join('/', segments);
        }

        return paths;
    }

    private static async Task<Dictionary<string, IReadOnlyList<string>>> GroupAsync(SqliteConnection connection, string sql, Dictionary<string, string> tagPaths, CancellationToken ct) =>
        (await ListAsync(connection, sql, r => (Target: r.GetString(0), Tag: r.GetString(1)), ct))
            .Where(link => tagPaths.ContainsKey(link.Tag))
            .GroupBy(link => link.Target)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(link => tagPaths[link.Tag]).Order(StringComparer.Ordinal)], StringComparer.Ordinal);

    private static async Task<List<T>> ListAsync<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> map, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(map(reader));
        }

        return items;
    }
}
