using System.Globalization;
using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Search;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Sync.UniqueRules;
using Chaos.Sync;
using Microsoft.Data.Sqlite;
using static Chaos.Infrastructure.Sync.SyncRows;

namespace Chaos.Infrastructure.Sync;

public sealed partial class SyncApplier
{
    // ---- Upsert (§7.4 step 2.2) -----------------------------------------------------------

    private async Task<StepResult> ApplyUpsertAsync(SqliteConnection connection, SyncSchema schema, SyncTable table, SyncChange change, ParkSlip park, CancellationToken ct)
    {
        var key = ParseKey(table, change.EntityKey);
        var exists = RowExists(connection, table, key);
        var stored = exists
            ? ReadStoredGroups(connection, table, key, change.Groups.Keys)
            : ReadClockWatermarks(connection, table, change);

        var merge = Merge.Apply(stored, change);
        var textPlan = PlanText(connection, table, key, change, stored, exists, ref merge);
        var drawingMerged = PlanDrawing(table, change, stored, exists, ref merge);
        if (!merge.Changed)
        {
            return StepResult.Skipped; // local is newer, or this exact change came back (equal clocks)
        }

        // A status written by an older client selects a base status. Treat the absent
        // custom-state field as null so the atomic group never retains a stale state.
        if (table.Kind == "task" && merge.Winners.TryGetValue("status", out var status)
            && !status.Values.Values.ContainsKey("CustomStateId"))
        {
            var values = new Dictionary<string, object?>(status.Values.Values, StringComparer.OrdinalIgnoreCase)
            {
                ["CustomStateId"] = null,
            };
            var winners = new Dictionary<string, SyncGroupState>(merge.Winners, StringComparer.Ordinal)
            {
                ["status"] = status with { Values = new SyncGroupValues(values) },
            };
            merge = merge with { Winners = winners };
        }

        if (table.Kind == "task" && merge.Winners.TryGetValue("status", out status)
            && status.Values.Values.GetValueOrDefault("CustomStateId") is string stateId)
        {
            using var stateQuery = connection.CreateCommand();
            stateQuery.CommandText = "SELECT SpaceId, BaseStatus, DeletedAtUtc FROM SpaceStates WHERE Id=$id;";
            stateQuery.Parameters.AddWithValue("$id", stateId);
            using var stateRow = stateQuery.ExecuteReader();
            if (stateRow.Read())
            {
                var taskSpace = WinnerValues(merge).GetValueOrDefault("SpaceId")?.ToString();
                if (taskSpace is null && exists)
                {
                    using var taskQuery = connection.CreateCommand();
                    taskQuery.CommandText = "SELECT SpaceId FROM Tasks WHERE Id=$id;";
                    taskQuery.Parameters.AddWithValue("$id", key["Id"]);
                    taskSpace = taskQuery.ExecuteScalar()?.ToString();
                }
                if (taskSpace is not null && taskSpace != stateRow.GetString(0))
                {
                    park.Park("the custom state belongs to another space");
                    return StepResult.Parked;
                }
                var values = new Dictionary<string, object?>(status.Values.Values, StringComparer.OrdinalIgnoreCase)
                {
                    ["Status"] = stateRow.GetInt64(1),
                    ["CustomStateId"] = stateRow.IsDBNull(2) ? stateId : null,
                    ["CompletedAtUtc"] = null,
                    ["CancelledAtUtc"] = null,
                };
                var winners = new Dictionary<string, SyncGroupState>(merge.Winners, StringComparer.Ordinal)
                {
                    ["status"] = status with { Values = new SyncGroupValues(values) },
                };
                merge = merge with { Winners = winners };
            }
        }

        var wall = clock.GetUtcNow();
        foreach (var winner in merge.Winners.Values)
        {
            if (winner.Hlc.WallUtc > wall)
            {
                wall = winner.Hlc.WallUtc;
            }
        }

        // §7.8 parking happens before the write: foreign keys are deferred for the batch, so a
        // missing parent would only fail at COMMIT, where no single change can be parked.
        var missing = MissingForeignKeyTarget(connection, schema, table, key, merge, exists);
        if (missing is not null)
        {
            park.Park($"the change references '{missing}', which has not arrived yet");
            return StepResult.Parked;
        }

        try
        {
            await WriteWinnersAsync(connection, schema, table, key, exists, merge, wall, change, ct);
            if (textPlan is not null) FinishText(connection, table, key, change, textPlan);
            if (drawingMerged)
                SyncSql.Execute(connection, $"INSERT OR IGNORE INTO SyncPending (Kind, EntityKey, GroupName) VALUES ({SyncSql.Literal(table.Kind)}, {SyncSql.Literal(change.EntityKey)}, 'strokes');");
            if (table.Kind == "view_alert_setting" && merge.Winners.ContainsKey("settings"))
            {
                using var resetBaseline = connection.CreateCommand();
                resetBaseline.CommandText = "DELETE FROM ViewAlertBaselineState WHERE ViewId=(SELECT ViewId FROM ViewAlertSettings WHERE Id=$id);";
                resetBaseline.Parameters.AddWithValue("$id", key["Id"]);
                resetBaseline.ExecuteNonQuery();
            }
            if (table.Kind == "space_state" && merge.Winners.ContainsKey("base"))
            {
                using var rebase = connection.CreateCommand();
                rebase.CommandText = "UPDATE Tasks SET Status=(SELECT BaseStatus FROM SpaceStates WHERE Id=$id), CompletedAtUtc=NULL, CancelledAtUtc=NULL, Version=Version+1 WHERE CustomStateId=$id AND Status<>(SELECT BaseStatus FROM SpaceStates WHERE Id=$id);";
                rebase.Parameters.AddWithValue("$id", key["Id"]);
                rebase.ExecuteNonQuery();
            }
            if (table.Kind == "space_state" && merge.Winners.TryGetValue("deleted", out var deleted)
                && deleted.Values.Values.GetValueOrDefault("DeletedAtUtc") is not null)
            {
                using var clear = connection.CreateCommand();
                clear.CommandText = "UPDATE Tasks SET CustomStateId=NULL, Status=(SELECT BaseStatus FROM SpaceStates WHERE Id=$id), Version=Version+1 WHERE CustomStateId=$id;";
                clear.Parameters.AddWithValue("$id", key["Id"]);
                clear.ExecuteNonQuery();
            }
        }
        catch (SqliteException error)
        {
            switch (Classify(error, out var columns, out var namedIndex))
            {
                case ConstraintKind.Unique:
                    return await ResolveUniqueAsync(connection, schema, table, key, change, merge, columns, wall, park, namedIndex, ct) == RuleOutcome.Resolved
                        ? StepResult.Applied
                        : StepResult.Parked;
                case ConstraintKind.ForeignKey:
                    park.Park("the change references something that has not arrived yet");
                    return StepResult.Parked;
                case ConstraintKind.NotNull:
                    park.Park("the change does not carry every required column of a new row");
                    return StepResult.Parked;
                case ConstraintKind.Check:
                    park.Park("the change's values fail a database check", "sync.invalid",
                        "A change from another device could not be applied because its values are not valid here.");
                    return StepResult.Parked;
                default:
                    throw;
            }
        }

        return StepResult.Applied;
    }

    /// <summary>Writes the winning groups: an UPDATE of the changed columns, or the row's INSERT.</summary>
    private async Task WriteWinnersAsync(
        SqliteConnection connection, SyncSchema schema, SyncTable table, IReadOnlyDictionary<string, string> key, bool exists,
        MergeResult merge, DateTimeOffset wall, SyncChange change, CancellationToken ct)
    {
        // Only columns this schema has are written: a group from a newer catalog (§7.3) keeps its
        // clock but has nowhere to land, and a name from a change never reaches the SQL unchecked.
        var changedColumns = WinnerValues(merge)
            .Where(pair => schema.HasColumn(table.Table, pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (exists)
        {
            // §7.7: a group that replaces a different local value keeps the replaced value in history.
            if (merge.Replaced.Count > 0)
            {
                RecordReplacedValues(connection, table, key, merge.Replaced, merge.Winners, change);
            }

            var sets = changedColumns.Select((pair, index) => $"{SyncSql.Quote(pair.Key)} = $p{index}").ToList();

            // Local bookkeeping columns: the version bump invalidates stale undo entries and open
            // editors (§7.10); UpdatedAtUtc is max(local, change wall time) (§7.2 rule 2).
            if (schema.HasColumn(table.Table, "Version"))
            {
                sets.Add("Version = Version + 1");
            }

            if (schema.HasColumn(table.Table, "UpdatedAtUtc"))
            {
                sets.Add("UpdatedAtUtc = MAX(UpdatedAtUtc, $wall)");
            }

            if (sets.Count > 0)
            {
                using var update = connection.CreateCommand();
                update.CommandText = $"UPDATE {SyncSql.Quote(table.Table)} SET {string.Join(", ", sets)} WHERE {Where(table, key)};";
                BindKey(update, table, key);
                for (var index = 0; index < changedColumns.Count; index++)
                {
                    Bind(update, $"$p{index}", changedColumns[index].Value);
                }

                update.Parameters.AddWithValue("$wall", StorageFormats.Instant(wall));
                update.ExecuteNonQuery();
            }
        }
        else
        {
            var insert = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (column, value) in key)
            {
                insert[column] = value;
            }

            foreach (var (column, value) in changedColumns)
            {
                insert[column] = value;
            }

            if (schema.HasColumn(table.Table, "Version"))
            {
                insert["Version"] = 1L;
            }

            if (schema.HasColumn(table.Table, "UpdatedAtUtc"))
            {
                insert["UpdatedAtUtc"] = StorageFormats.Instant(wall);
            }

            // Local columns the change cannot carry (§7.2) but the schema requires: fill the
            // column's declared default, or the type's neutral value. The schema is the same on
            // every device, so the row lands identically everywhere.
            foreach (var required in schema.RequiredWithoutDefault(table.Table))
            {
                if (!insert.ContainsKey(required))
                {
                    insert[required] = schema.ColumnIsText(table.Table, required) ? string.Empty : 1L;
                }
            }

            using var command = connection.CreateCommand();
            var names = insert.Keys.Where(name => schema.HasColumn(table.Table, name)).Order(StringComparer.OrdinalIgnoreCase).ToList();
            command.CommandText = $"INSERT INTO {SyncSql.Quote(table.Table)} ({string.Join(", ", names.Select(SyncSql.Quote))}) VALUES ({string.Join(", ", names.Select((_, index) => $"$p{index}"))});";
            for (var index = 0; index < names.Count; index++)
            {
                Bind(command, $"$p{index}", insert[names[index]]);
            }

            command.ExecuteNonQuery();
        }

        foreach (var (group, winner) in merge.Winners)
        {
            // The merge decided these groups win: their clocks replace the stored ones.
            SyncSql.UpsertClock(connection, table.Kind, change.EntityKey, group, winner.Hlc.Encode(), onlyIfNewer: false);
        }

        // A linked task may arrive before its GitHub link. Recheck it once the link lands on
        // the runner so a task edit received in the same batch cannot be lost.
        if (table.Kind == "github_issue_link")
        {
            using var dirty = connection.CreateCommand();
            dirty.CommandText = """
                UPDATE GitHubIssueLinks SET LocalDirty=1, LocalDirtySinceUtc=COALESCE(LocalDirtySinceUtc,$now)
                WHERE Id=$id AND State=0 AND TaskId IS NOT NULL
                  AND EXISTS (SELECT 1 FROM GitHubConnections c WHERE c.Id=ConnectionId
                      AND c.RunnerDeviceId=(SELECT Value FROM DeviceSettings WHERE Key='DeviceId'));
                """;
            dirty.Parameters.AddWithValue("$id", key["Id"]);
            dirty.Parameters.AddWithValue("$now", StorageFormats.Instant(wall));
            await dirty.ExecuteNonQueryAsync(ct);
        }

        await RefreshSearchDocumentAsync(connection, table, key, ct);
    }

    // ---- Stored state (§7.4 step 2.2: compare per group against SyncClocks) ----------------

    /// <summary>
    /// The entity's clock watermark when its row is gone (a purge or an un-writable duplicate): a
    /// replayed change at or below the watermark is a repeat, a genuinely newer change re-creates
    /// the row. Values are empty — there is no row to read — which is all the merge compares.
    /// </summary>
    private static IReadOnlyDictionary<string, SyncGroupState>? ReadClockWatermarks(
        SqliteConnection connection, SyncTable table, SyncChange change)
    {
        Dictionary<string, SyncGroupState>? watermarks = null;
        foreach (var name in change.Groups.Keys)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Hlc FROM SyncClocks WHERE Kind = $kind AND EntityKey = $key AND GroupName = $group;";
            command.Parameters.AddWithValue("$kind", table.Kind);
            command.Parameters.AddWithValue("$key", change.EntityKey);
            command.Parameters.AddWithValue("$group", name);
            if (command.ExecuteScalar() is string clockText)
            {
                (watermarks ??= new Dictionary<string, SyncGroupState>(StringComparer.Ordinal))[name] =
                    new SyncGroupState(Hlc.Parse(clockText), SyncGroupValues.Empty);
            }
        }

        return watermarks;
    }

    private static IReadOnlyDictionary<string, SyncGroupState>? ReadStoredGroups(
        SqliteConnection connection, SyncTable table, IReadOnlyDictionary<string, string> key, IEnumerable<string> groupNames)
    {
        var groups = new Dictionary<string, SyncGroupState>(StringComparer.Ordinal);
        foreach (var name in groupNames)
        {
            if (table.Groups.FirstOrDefault(candidate => candidate.Name == name) is not { } group)
            {
                continue; // a group this catalog version does not know: unknown groups are kept (§7.3)
            }

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            using (var select = connection.CreateCommand())
            {
                var columnList = string.Join(", ", group.Columns.Select(SyncSql.Quote));
                select.CommandText = $"SELECT {columnList} FROM {SyncSql.Quote(table.Table)} WHERE {Where(table, key)};";
                BindKey(select, table, key);
                using var reader = select.ExecuteReader();
                if (reader.Read())
                {
                    foreach (var column in group.Columns)
                    {
                        var ordinal = reader.GetOrdinal(column);
                        values[column] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
                    }
                }
            }

            using (var clockQuery = connection.CreateCommand())
            {
                clockQuery.CommandText = "SELECT Hlc FROM SyncClocks WHERE Kind = $kind AND EntityKey = $key AND GroupName = $group;";
                clockQuery.Parameters.AddWithValue("$kind", table.Kind);
                clockQuery.Parameters.AddWithValue("$key", string.Join("|", table.Key.Select(column => key[column])));
                clockQuery.Parameters.AddWithValue("$group", name);
                groups[name] = clockQuery.ExecuteScalar() is string clockText
                    ? new SyncGroupState(Hlc.Parse(clockText), new SyncGroupValues(values))
                    // A row that predates sync has no local clock: the incoming change wins (§7.4).
                    : new SyncGroupState(new Hlc(0, 0, new string('0', 32)), new SyncGroupValues(values));
            }
        }

        return groups;
    }

    /// <summary>
    /// The first foreign-key column of the change whose referenced row is missing, or null. Key
    /// columns are checked from the entity key; group columns only when the change writes them —
    /// an untouched reference was valid when it was set.
    /// </summary>
    private static string? MissingForeignKeyTarget(
        SqliteConnection connection, SyncSchema schema, SyncTable table, IReadOnlyDictionary<string, string> key, MergeResult merge, bool exists)
    {
        if (!exists && merge.Winners.Count == 0)
        {
            return null;
        }

        var winnerValues = WinnerValues(merge);
        foreach (var (columns, parentTable, parentColumns) in schema.ForeignKeys(table.Table))
        {
            // A foreign key is satisfied when any of its child columns is NULL (SQLite MATCH
            // SIMPLE): a composite key with a NULL member references nothing.
            var values = new object?[columns.Length];
            var complete = true;
            for (var index = 0; index < columns.Length; index++)
            {
                var column = columns[index];
                if (table.Key.Contains(column, StringComparer.OrdinalIgnoreCase))
                {
                    values[index] = key[column];
                    continue;
                }

                if (winnerValues.TryGetValue(column, out var value))
                {
                    values[index] = value;
                }
                else if (!exists)
                {
                    // Older writers do not know the optional custom-state field.
                    if (table.Kind == "task" && column == "CustomStateId")
                        continue;
                    complete = false; // a new row the change cannot complete: parked, not committed as ''
                    break;
                }
                // an existing row keeps its untouched reference, read below
            }

            if (!complete)
            {
                return $"{table.Table}({string.Join(",", columns)}) -> {parentTable}";
            }

            for (var index = 0; index < columns.Length; index++)
            {
                if (values[index] is not null || !exists)
                {
                    continue;
                }

                // An existing row's untouched reference: read it so the key is checked whole.
                using var read = connection.CreateCommand();
                read.CommandText = $"SELECT {SyncSql.Quote(columns[index])} FROM {SyncSql.Quote(table.Table)} WHERE {Where(table, key)} LIMIT 1;";
                BindKey(read, table, key);
                var stored = read.ExecuteScalar();
                values[index] = stored is DBNull ? null : stored;
            }

            if (values.Any(value => value is null))
            {
                continue; // MATCH SIMPLE: a NULL member satisfies the key
            }

            var missing = false;
            for (var index = 0; index < columns.Length && !missing; index++)
            {
                using var check = connection.CreateCommand();
                check.CommandText = $"SELECT 1 FROM {SyncSql.Quote(parentTable)} WHERE {SyncSql.Quote(parentColumns[index])} = $value LIMIT 1;";
                check.Parameters.AddWithValue("$value", values[index] ?? DBNull.Value);
                missing = check.ExecuteScalar() is null;
            }

            if (missing)
            {
                return $"{table.Table}({string.Join(",", columns)}) -> {parentTable}({string.Join(",", parentColumns)})";
            }
        }

        return null;
    }

    private static bool RowExists(SqliteConnection connection, SyncTable table, IReadOnlyDictionary<string, string> key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM {SyncSql.Quote(table.Table)} WHERE {Where(table, key)} LIMIT 1;";
        BindKey(command, table, key);
        return command.ExecuteScalar() is not null;
    }
}
