using System.Text;
using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Search;

/// <summary>
/// Built-in views and search over SQLite (PLAN §10). One candidate CTE is built from the
/// criteria and used for both COUNT(*) and the page, so counts and rows cannot disagree.
/// Pages use keyset cursors over the view's sort keys with the task ID as final tie-breaker,
/// so paging is deterministic and an edit to an earlier row never duplicates or skips rows.
/// Every value is a bound parameter; free text only reaches FTS through
/// <see cref="SearchText.ToMatchExpression"/>.
/// </summary>
public sealed class SqliteTaskQueryStore(SqliteConnection connection) : ITaskQueryStore
{
    private const int MaxTagDepth = 16;

    public async Task<TaskQueryPage> QueryAsync(TaskQueryCriteria criteria, CancellationToken ct)
    {
        var plan = Plan.Build(criteria);
        if (plan is null)
        {
            return new TaskQueryPage([], 0, null);
        }

        var total = await CountAsync(plan, ct);
        var after = criteria.Cursor is null ? null : Cursor.Decode(criteria.Cursor, criteria.View, plan.Keys.Count);

        using var command = connection.CreateCommand();
        var seek = after is null ? "1" : plan.Seek(command, after);
        var keyColumns = string.Join(", ", plan.Keys.Select((_, index) => $"x.K{index}"));
        var order = string.Join(", ", plan.Keys.Select((key, index) => $"x.K{index}{(key.Descending ? " DESC" : string.Empty)}"));
        command.CommandText = $"""
            {plan.With}
            SELECT t.*, c.Name AS ContainerName, c.ArchivedAtUtc AS ContainerArchivedAtUtc, x.GroupDate AS GroupDate, x.Kinds AS Kinds, {keyColumns}
            FROM Candidates x
            JOIN Tasks t ON t.Id = x.Id
            JOIN Containers c ON c.Id = t.ContainerId
            WHERE {seek}
            ORDER BY {order}, x.Id
            LIMIT $limit;
            """;
        plan.Bind(command);
        command.Parameters.AddWithValue("$limit", criteria.Limit + 1);

        var rows = new List<(TaskQueryRow Row, object[] Keys)>();
        using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var keys = plan.Keys.Select((_, index) => reader.GetValue(reader.GetOrdinal($"K{index}"))).ToArray();
                rows.Add((new TaskQueryRow(
                    SqliteTaskStore.ReadTask(reader),
                    reader.S("ContainerName"),
                    !reader.IsDBNull(reader.GetOrdinal("ContainerArchivedAtUtc")),
                    [],
                    ParseKinds(reader.Sn("Kinds")),
                    reader.Dn("GroupDate")), keys));
            }
        }

        string? next = null;
        if (rows.Count > criteria.Limit)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            next = Cursor.Encode(criteria.View, last.Keys, last.Row.Task.Id);
        }

        var tags = await TagIdsAsync([.. rows.Select(row => row.Row.Task.Id)], ct);
        return new TaskQueryPage(
            [.. rows.Select(row => row.Row with { TagIds = tags.TryGetValue(row.Row.Task.Id, out var ids) ? ids : [] })],
            total,
            next);
    }

    public async Task<long> CountAsync(TaskQueryCriteria criteria, CancellationToken ct)
    {
        var plan = Plan.Build(criteria);
        return plan is null ? 0 : await CountAsync(plan, ct);
    }

    public async Task<IReadOnlyList<string>> ListOpenDueZoneIdsAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT DueZoneId FROM Tasks
            WHERE SpaceId = $space AND DeletedAtUtc IS NULL AND Status NOT IN (4,5)
              AND DueTime IS NOT NULL AND DueZoneId IS NOT NULL
            UNION
            SELECT DISTINCT json_extract(s.RuleJson, '$.payload.zoneId') FROM RecurrenceSeries s
            JOIN RecurrenceOccurrences o ON o.SeriesId = s.Id
            JOIN Tasks t ON t.Id = o.TaskId
            WHERE t.SpaceId = $space AND t.DeletedAtUtc IS NULL AND t.Status NOT IN (4,5)
              AND t.DueDate IS NULL AND o.State = 0 AND o.DeletedAtUtc IS NULL
              AND json_extract(s.RuleJson, '$.payload.zoneId') IS NOT NULL
            ORDER BY 1;
            """;
        command.Parameters.AddWithValue("$space", spaceId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var zones = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            zones.Add(reader.GetString(0));
        }

        return zones;
    }

    public async Task<long> RebuildSearchIndexAsync(DateTimeOffset now, CancellationToken ct) =>
        await SearchIndexWriter.RebuildAsync(connection, now, ct);

    private async Task<long> CountAsync(Plan plan, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"{plan.With} SELECT COUNT(*) FROM Candidates;";
        plan.Bind(command);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> TagIdsAsync(IReadOnlyList<string> taskIds, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (taskIds.Count == 0)
        {
            return result;
        }

        using var command = connection.CreateCommand();
        var names = taskIds.Select((id, index) =>
        {
            command.Parameters.AddWithValue($"$t{index}", id);
            return $"$t{index}";
        }).ToList();
        command.CommandText = $"""
            SELECT l.TaskId, l.TagId FROM TaskTags l JOIN Tags g ON g.Id = l.TagId
            WHERE g.DeletedAtUtc IS NULL AND l.TaskId IN ({string.Join(", ", names)})
            ORDER BY l.TaskId, g.NormalizedName, g.Id;
            """;
        using var reader = await command.ExecuteReaderAsync(ct);
        var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            var taskId = reader.GetString(0);
            if (!grouped.TryGetValue(taskId, out var list))
            {
                grouped[taskId] = list = [];
            }

            list.Add(reader.GetString(1));
        }

        foreach (var (taskId, list) in grouped)
        {
            result[taskId] = list;
        }

        return result;
    }

    private static IReadOnlyList<SearchMatchKind> ParseKinds(string? kinds) =>
        kinds is null
            ? []
            : [.. kinds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(kind => (SearchMatchKind)int.Parse(kind, System.Globalization.CultureInfo.InvariantCulture))
                .Distinct()
                .Order()];

    private sealed record SortKey(string Expression, bool Descending);

    /// <summary>The SQL for one criteria object: CTEs, predicates, sort keys and their parameters.</summary>
    private sealed class Plan
    {
        private readonly List<(string Name, object Value)> _parameters = [];

        public required string With { get; init; }
        public required IReadOnlyList<SortKey> Keys { get; init; }

        public void Bind(SqliteCommand command)
        {
            foreach (var (name, value) in _parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }
        }

        /// <summary>Lexicographic "after the cursor" predicate over (keys..., Id) honoring each key's direction.</summary>
        public string Seek(SqliteCommand command, Cursor cursor)
        {
            var terms = new List<string>();
            for (var depth = 0; depth <= Keys.Count; depth++)
            {
                var parts = new List<string>();
                for (var index = 0; index < depth; index++)
                {
                    parts.Add($"x.K{index} = $c{index}");
                }

                parts.Add(depth == Keys.Count
                    ? "x.Id > $cid"
                    : $"x.K{depth} {(Keys[depth].Descending ? "<" : ">")} $c{depth}");
                terms.Add($"({string.Join(" AND ", parts)})");
            }

            for (var index = 0; index < Keys.Count; index++)
            {
                command.Parameters.AddWithValue($"$c{index}", cursor.Keys[index]);
            }

            command.Parameters.AddWithValue("$cid", cursor.Id);
            return $"({string.Join(" OR ", terms)})";
        }

        /// <returns>The plan, or null when the text filter has no searchable word (the result is empty).</returns>
        public static Plan? Build(TaskQueryCriteria criteria)
        {
            var ctes = new List<string>();
            var builder = new PlanBuilder();
            var where = new List<string>
            {
                $"t.SpaceId = {builder.P(criteria.SpaceId)}",
                "t.DeletedAtUtc IS NULL",
                "c.DeletedAtUtc IS NULL",
            };

            var join = string.Empty;
            var kinds = "NULL";
            if (criteria.Text is not null)
            {
                var match = SearchText.ToMatchExpression(criteria.Text);
                if (match is null)
                {
                    return null;
                }

                // bm25() is only valid in a plain full-text query, so the matches are materialized
                // before aggregation. Subtask/diary hits link to their parent task; tombstoned
                // sources never count. Title matches weigh ten times body matches.
                ctes.Add($"""
                    Matches AS MATERIALIZED (
                        SELECT rowid AS RowId, bm25(SearchIndex, 10.0, 1.0) AS Rank FROM SearchIndex WHERE SearchIndex MATCH {builder.P(match)})
                    """);
                ctes.Add("""
                    Hits AS (
                        SELECT d.TaskId AS TaskId, MIN(m.Rank) AS Rank, group_concat(DISTINCT d.TargetKind) AS Kinds
                        FROM Matches m
                        JOIN SearchDocuments d ON d.RowId = m.RowId
                        LEFT JOIN Subtasks s ON d.TargetKind = 2 AND s.Id = d.TargetId
                        LEFT JOIN DiaryEntries e ON d.TargetKind = 3 AND e.Id = d.TargetId
                        WHERE d.TaskId IS NOT NULL
                          AND (d.TargetKind <> 2 OR s.DeletedAtUtc IS NULL)
                          AND (d.TargetKind <> 3 OR e.DeletedAtUtc IS NULL)
                        GROUP BY d.TaskId)
                    """);
                join = "JOIN Hits h ON h.TaskId = t.Id";
                kinds = "h.Kinds";
            }

            if (criteria.TagId is not null)
            {
                // Group filtering includes descendants through a bounded recursive CTE (PLAN §6).
                ctes.Add($"""
                    TagScope(Id, Depth) AS (
                        SELECT Id, 0 FROM Tags WHERE Id = {builder.P(criteria.TagId)} AND OwnerProfileId = {builder.P(criteria.ProfileId)} AND DeletedAtUtc IS NULL
                        UNION ALL
                        SELECT child.Id, scope.Depth + 1 FROM Tags child JOIN TagScope scope ON child.ParentTagId = scope.Id
                        WHERE child.DeletedAtUtc IS NULL AND scope.Depth < {MaxTagDepth})
                    """);
                where.Add("""
                    (EXISTS (SELECT 1 FROM TaskTags tt WHERE tt.TaskId = t.Id AND tt.TagId IN (SELECT Id FROM TagScope))
                     OR EXISTS (SELECT 1 FROM Subtasks st JOIN SubtaskTags stt ON stt.SubtaskId = st.Id
                                WHERE st.TaskId = t.Id AND st.DeletedAtUtc IS NULL AND stt.TagId IN (SELECT Id FROM TagScope)))
                    """);
            }

            if (criteria.DayPlanOn is { } dayPlanOn)
            {
                // My Day: the active membership rows of one profile's plan for one local date.
                var profile = builder.P(criteria.ProfileId);
                var day = builder.P(StorageFormats.Date(dayPlanOn));
                join += $" JOIN DayPlans dp ON dp.ProfileId = {profile} AND dp.LocalDate = {day}"
                    + " JOIN DayPlanItems di ON di.DayPlanId = dp.Id AND di.TaskId = t.Id AND di.RemovedAtUtc IS NULL";
            }

            if (!criteria.IncludeArchived) where.Add("c.ArchivedAtUtc IS NULL");
            if (criteria.ContainerId is not null) where.Add($"t.ContainerId = {builder.P(criteria.ContainerId)}");

            if (criteria.Statuses.Count > 0) where.Add($"t.Status IN ({string.Join(", ", criteria.Statuses.Select(status => builder.P((long)status)))})");
            else if (criteria.OpenOnly) where.Add("t.Status NOT IN (4,5)");
            if (criteria.DoneOnly) where.Add("t.Status = 4");
            if (criteria.Priorities.Count > 0) where.Add($"t.Priority IN ({string.Join(", ", criteria.Priorities.Select(priority => builder.P((long)priority)))})");

            if (criteria.PlannedFrom is { } plannedFrom) where.Add($"t.PlannedDate >= {builder.P(StorageFormats.Date(plannedFrom))}");
            if (criteria.PlannedTo is { } plannedTo) where.Add($"t.PlannedDate <= {builder.P(StorageFormats.Date(plannedTo))}");
            if (criteria.DueFrom is { } dueFrom) where.Add($"t.DueDate >= {builder.P(StorageFormats.Date(dueFrom))}");
            if (criteria.DueTo is { } dueTo) where.Add($"t.DueDate <= {builder.P(StorageFormats.Date(dueTo))}");
            if (criteria.CompletedFromUtc is { } completedFrom) where.Add($"t.CompletedAtUtc >= {builder.P(StorageFormats.Instant(completedFrom))}");
            if (criteria.CompletedBeforeUtc is { } completedBefore) where.Add($"t.CompletedAtUtc < {builder.P(StorageFormats.Instant(completedBefore))}");

            if (criteria.PlannedOrDueOn is { } on)
            {
                var day = builder.P(StorageFormats.Date(on));
                where.Add($"(t.PlannedDate = {day} OR t.DueDate = {day})");
            }

            var groupDate = "NULL";
            if (criteria.UpcomingAfter is { } after && criteria.UpcomingThrough is { } through)
            {
                var start = builder.P(StorageFormats.Date(after));
                var end = builder.P(StorageFormats.Date(through));
                // IS NOT NULL keeps both tests strictly true/false: a NULL here would make the
                // CASE below fall through to a NULL group date for planned-only tasks.
                var plannedIn = $"(t.PlannedDate IS NOT NULL AND t.PlannedDate > {start} AND t.PlannedDate <= {end})";
                var dueIn = $"(t.DueDate IS NOT NULL AND t.DueDate > {start} AND t.DueDate <= {end})";
                where.Add($"({plannedIn} OR {dueIn})");
                // Grouped by the earliest relevant date inside the range (PLAN §10).
                groupDate = $"CASE WHEN {plannedIn} AND (NOT {dueIn} OR t.PlannedDate <= t.DueDate) THEN t.PlannedDate ELSE t.DueDate END";
            }

            if (criteria.OverdueBefore is { } overdueBefore)
            {
                // Date-only due values pass when their local date ends; timed values when their
                // instant passes, judged in the value's own zone (PLAN §6). Planned-in-past alone is not overdue.
                var timed = criteria.OverdueZoneCutoffs.Count == 0
                    ? "0"
                    : string.Join(" OR ", criteria.OverdueZoneCutoffs.Select(cutoff =>
                    {
                        var date = builder.P(StorageFormats.Date(cutoff.LocalDate));
                        return $"(t.DueZoneId = {builder.P(cutoff.ZoneId)} AND (t.DueDate < {date} OR (t.DueDate = {date} AND t.DueTime < {builder.P(StorageFormats.Time(cutoff.LocalTime))})))";
                    }));
                var recurrenceCutoffs = criteria.OverdueZoneCutoffs.Count == 0 ? "0" : string.Join(" OR ", criteria.OverdueZoneCutoffs.Select(cutoff =>
                {
                    var date = builder.P(StorageFormats.Date(cutoff.LocalDate));
                    var time = builder.P(StorageFormats.Time(cutoff.LocalTime));
                    var zone = builder.P(cutoff.ZoneId);
                    return $"(json_extract(rs.RuleJson, '$.payload.zoneId') = {zone} AND (substr(ro.EffectiveLocal,1,10) < {date} OR (json_extract(rs.RuleJson, '$.payload.anchorTime') IS NOT NULL AND substr(ro.EffectiveLocal,1,10) = {date} AND substr(ro.EffectiveLocal,12,8) < {time})))";
                }));
                var missed = $"(t.DueDate IS NULL AND EXISTS (SELECT 1 FROM RecurrenceOccurrences ro JOIN RecurrenceSeries rs ON rs.Id=ro.SeriesId WHERE ro.TaskId=t.Id AND ro.State=0 AND ro.DeletedAtUtc IS NULL AND ({recurrenceCutoffs})))";
                where.Add($"((t.DueDate IS NOT NULL AND ((t.DueTime IS NULL AND t.DueDate < {builder.P(StorageFormats.Date(overdueBefore))}) OR (t.DueTime IS NOT NULL AND ({timed})))) OR {missed})");
                groupDate = "COALESCE(t.DueDate, t.PlannedDate)";
            }

            IReadOnlyList<SortKey> keys = criteria.View switch
            {
                TaskView.Container => [new("t.Position", false)],
                TaskView.MyDay => [new("di.Position", false)],
                TaskView.Today => [new("t.Priority", true), new("c.Position", false), new("t.Position", false)],
                TaskView.Upcoming => [new(groupDate, false), new("t.Priority", true)],
                TaskView.Overdue => [new("COALESCE(t.DueDate, t.PlannedDate)", false), new("COALESCE(t.DueTime, t.PlannedTime, '24:00:00')", false)],
                TaskView.RecentlyCompleted => [new("t.CompletedAtUtc", true)],
                TaskView.Waiting => [new("t.UpdatedAtUtc", false)],
                TaskView.Search when criteria.Text is not null => [new("h.Rank", false)],
                TaskView.Search => [new("t.UpdatedAtUtc", true)],
                _ => [new("c.Position", false), new("t.Position", false)],
            };

            var keyColumns = string.Join(string.Empty, keys.Select((key, index) => $", {key.Expression} AS K{index}"));
            ctes.Add($"""
                Candidates AS (
                    SELECT t.Id AS Id{keyColumns}, {groupDate} AS GroupDate, {kinds} AS Kinds
                    FROM Tasks t
                    JOIN Containers c ON c.Id = t.ContainerId
                    {join}
                    WHERE {string.Join("\n      AND ", where)})
                """);

            var plan = new Plan { With = $"WITH RECURSIVE {string.Join(",\n", ctes)}", Keys = keys };
            plan._parameters.AddRange(builder.Parameters);
            return plan;
        }
    }

    private sealed class PlanBuilder
    {
        public List<(string Name, object Value)> Parameters { get; } = [];

        public string P(object value)
        {
            var name = $"$p{Parameters.Count}";
            Parameters.Add((name, value));
            return name;
        }
    }

    /// <summary>Opaque keyset cursor: the view, the last row's sort-key values and its ID, as base64url JSON.</summary>
    private sealed record Cursor(int View, object[] Keys, string Id)
    {
        public static string Encode(TaskView view, object[] keys, string id)
        {
            if (keys.Any(key => key is DBNull))
            {
                throw new InvalidOperationException("Sort keys must never be NULL; a view's sort expression is missing a COALESCE.");
            }

            var json = JsonSerializer.Serialize(new CursorPayload((int)view, keys, id));
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static Cursor Decode(string text, TaskView view, int keyCount)
        {
            try
            {
                var base64 = text.Replace('-', '+').Replace('_', '/');
                base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
                var payload = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
                var keys = payload.GetProperty("k").EnumerateArray().Select(element => element.ValueKind switch
                {
                    JsonValueKind.Number when element.TryGetInt64(out var integer) => (object)integer,
                    JsonValueKind.Number => element.GetDouble(),
                    JsonValueKind.String => element.GetString()!,
                    _ => throw new FormatException("Unsupported cursor value."),
                }).ToArray();
                if (payload.GetProperty("v").GetInt32() != (int)view || keys.Length != keyCount)
                {
                    throw new FormatException("Cursor belongs to another view.");
                }

                return new Cursor((int)view, keys, payload.GetProperty("id").GetString() ?? throw new FormatException("Cursor has no ID."));
            }
            catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                const string message = "This page link is out of date. Reload the list from the top.";
                throw new AppException(AppErrorKind.Validation, message, [new AppErrorField("cursor", message)], innerException: error);
            }
        }
    }

    private sealed record CursorPayload(
        [property: System.Text.Json.Serialization.JsonPropertyName("v")] int V,
        [property: System.Text.Json.Serialization.JsonPropertyName("k")] object[] K,
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id);
}
