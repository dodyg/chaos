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

/// <summary>What one apply pass did (PLAN2 §7.4); the changed entity list feeds P13's publishers.</summary>
public sealed record SyncApplyOutcome(
    long Applied,
    long Purged,
    long Skipped,
    IReadOnlyList<string> ParkedChangeIds)
{
    public bool Changed => Applied > 0 || Purged > 0;
}

/// <summary>
/// Applies remote changes to the local database (PLAN2 §7.4) in the serialized writer: merge per
/// field group by hybrid clock, purges as hard deletes with their dependents, unique-index breaks
/// resolved by the catalog's deterministic rules (§7.6), changes that reference something not yet
/// present parked in <c>SyncInboxParked</c> and retried after the batch (§7.8), replaced values kept
/// in history (§7.7), the local <c>Version</c> bumped once per changed entity so stale undo entries
/// and open editors notice (§7.10), and the search documents of changed sources refreshed. There is
/// no network here: P13 feeds it from the server, tests feed it from another data root's outbox.
/// The capture triggers are suppressed while applying because every device runs the same
/// deterministic merge and the same rules from the same inputs, so nothing an apply writes would
/// add information by being captured again.
/// </summary>
public sealed class SyncApplier(SqliteApplicationStore store, TimeProvider clock, IIdGenerator ids)
{
    private const string ChangeSavepoint = "sync_change";

    public async Task<SyncApplyOutcome> ApplyAsync(IReadOnlyList<string> changePayloads, CancellationToken ct = default, long? throughSequence = null)
    {
        var parked = new List<string>();
        var applied = 0L;
        var purged = 0L;
        var skipped = 0L;
        var changes = changePayloads.Select(payload => (Change: SyncChangeParser.Parse(payload), Payload: payload)).ToList();

        await store.ExecuteAsync(async (session, token) =>
        {
            var connection = ((SqliteDataSession)session).Connection;
            var schema = new SyncSchema(connection);
            SyncSql.Execute(connection, "UPDATE SyncApplyState SET Applying = 1 WHERE Id = 1;");
            SyncSql.Execute(connection, "PRAGMA defer_foreign_keys = ON;");
            try
            {
                // Progress (applied + purged) when each change of this pass was parked: retrying
                // it is only worth it once something else landed after it.
                var parkedAtProgress = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var (change, payload) in changes)
                {
                    var outcome = await ApplyStepAsync(connection, schema, change, payload, token);
                    Count(outcome, change.ChangeId, ref applied, ref purged, ref skipped, parked);
                    if (outcome is StepResult.Parked)
                    {
                        parkedAtProgress[change.ChangeId] = applied + purged;
                    }
                }

                // §7.8: after each batch, parked changes are retried; a parent that arrived in
                // this batch lets its children through. A change parked in this pass with nothing
                // applied since is not retried: it would only park again.
                foreach (var (changeId, payload) in ReadParked(connection))
                {
                    if (parkedAtProgress.TryGetValue(changeId, out var progress) && progress == applied + purged)
                    {
                        continue;
                    }

                    var change = SyncChangeParser.Parse(payload);
                    var outcome = await ApplyStepAsync(connection, schema, change, payload, token);
                    if (outcome is StepResult.Parked)
                    {
                        BumpParkedCycles(connection, changeId);
                        if (!parked.Contains(changeId))
                        {
                            parked.Add(changeId);
                        }
                    }
                    else
                    {
                        parked.Remove(changeId);
                        Count(outcome, change.ChangeId, ref applied, ref purged, ref skipped, parked);
                        DeleteParked(connection, changeId);
                    }
                }

                if (throughSequence is { } sequence)
                {
                    SyncSql.Execute(connection, $"UPDATE SyncState SET Cursor = MAX(Cursor, {sequence.ToString(CultureInfo.InvariantCulture)}) WHERE Id = 1;");
                }

                ReceiveClocks(connection, changes.Select(entry => entry.Change));
            }
            finally
            {
                SyncSql.Execute(connection, "UPDATE SyncApplyState SET Applying = 0 WHERE Id = 1;");
            }

            return 0;
        }, ct);

        return new SyncApplyOutcome(applied, purged, skipped, parked);

        static void Count(StepResult outcome, string changeId, ref long applied, ref long purged, ref long skipped, List<string> parked)
        {
            switch (outcome)
            {
                case StepResult.Applied: applied++; break;
                case StepResult.Purged: purged++; break;
                case StepResult.Parked:
                    if (!parked.Contains(changeId))
                    {
                        parked.Add(changeId);
                    }

                    break;
                case StepResult.Skipped: skipped++; break;
            }
        }
    }

    /// <summary>
    /// §7.1 receive: the local clock learns the highest received clock, so the next local edit
    /// ticks past everything that arrived; without it, a device that only applied changes would
    /// keep issuing clocks older than what it received. A clock further ahead than
    /// <see cref="Hlc.MaxAcceptedSkew"/> is kept (it already won its merge everywhere) but
    /// reported once per sending device, as PLAN2 §P1 asks.
    /// </summary>
    private void ReceiveClocks(SqliteConnection connection, IEnumerable<SyncChange> changes)
    {
        var received = changes.SelectMany(change => change.Groups.Values.Select(group => group.Hlc)).ToList();
        if (received.Count == 0)
        {
            return;
        }

        var device = SyncSql.Text(connection, "SELECT Value FROM DeviceSettings WHERE Key = 'DeviceId';")
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "This data root has no device identity, so changes cannot be applied.");

        // Receive keeps this device's id on the clock (§7.1): only the wall and counter advance,
        // so two devices never issue the same clock for different changes.
        var hybrid = new HybridClock(Guid.Parse(device), clock);
        if (SyncSql.Text(connection, "SELECT LastHlc FROM SyncState WHERE Id = 1;") is { } last)
        {
            hybrid.Restore(Hlc.Parse(last));
        }

        var highest = received.Max();
        var result = hybrid.Receive(highest);
        using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE SyncState SET LastHlc = $hlc WHERE Id = 1;";
            update.Parameters.AddWithValue("$hlc", result.Clock.Encode());
            update.ExecuteNonQuery();
        }

        if (result.SkewBeyondCap && !OpenAttentionExists(connection, "sync.clock_skew", highest.DeviceId))
        {
            Attention(connection, "sync.clock_skew", null, highest.DeviceId,
                "Another device's clock is more than a day ahead of this one. Its changes were kept; check the date and time settings on both devices.");
        }
    }

    private enum StepResult
    {
        Applied,
        Purged,
        Skipped,
        Parked,
    }

    /// <summary>
    /// Why a change must wait in the inbox (§7.8), and the attention item that goes with it. The
    /// apply code fills it in; <see cref="ApplyStepAsync"/> writes it after rolling back whatever
    /// the failed attempt already wrote.
    /// </summary>
    private sealed class ParkSlip
    {
        public string Reason { get; private set; } = "the change could not be applied yet";

        public (string Category, string Message)? Notice { get; private set; }

        public void Park(string reason, string? category = null, string? message = null)
        {
            Reason = reason;
            Notice = category is null || message is null ? null : (category, message);
        }
    }

    /// <summary>
    /// Applies one change inside a savepoint. A parked change rolls back to it first, so history
    /// rows and rule side effects of the failed attempt do not stay behind; only the parked row
    /// (and, the first time, its attention item) remain.
    /// </summary>
    private async Task<StepResult> ApplyStepAsync(SqliteConnection connection, SyncSchema schema, SyncChange change, string payload, CancellationToken ct)
    {
        var slip = new ParkSlip();
        SyncSql.Execute(connection, $"SAVEPOINT {ChangeSavepoint};");
        var outcome = await ApplyOneAsync(connection, schema, change, slip, ct);
        if (outcome is StepResult.Parked)
        {
            SyncSql.Execute(connection, $"ROLLBACK TO {ChangeSavepoint};");
            if (Park(connection, change, payload, slip.Reason) && slip.Notice is { } notice)
            {
                Attention(connection, notice.Category, change.Kind, change.EntityKey, notice.Message);
            }
        }

        SyncSql.Execute(connection, $"RELEASE {ChangeSavepoint};");
        return outcome;
    }

    private async Task<StepResult> ApplyOneAsync(SqliteConnection connection, SyncSchema schema, SyncChange change, ParkSlip park, CancellationToken ct)
    {
        var table = SyncCatalog.Tables.FirstOrDefault(candidate => candidate.Kind == change.Kind);
        if (table is null || table.Mode == SyncMode.LocalOnly)
        {
            Attention(connection, "sync.unexpected", change.Kind, change.EntityKey,
                $"A change arrived for '{change.Kind}', which does not take part in sync. It was ignored.");
            return StepResult.Skipped;
        }

        if (change.Op == SyncOp.Purge)
        {
            // Idempotent: a purge whose row is already gone is a replay — the entity's clock
            // watermark (or its total absence) proves this device already settled it.
            var key = ParseKey(table, change.EntityKey);
            if (!RowExists(connection, table, key))
            {
                return StepResult.Skipped;
            }

            PurgeEntity(connection, schema, table, change.EntityKey);
            return StepResult.Purged;
        }

        return await ApplyUpsertAsync(connection, schema, table, change, park, ct);
    }

    // ---- Upsert (§7.4 step 2.2) -----------------------------------------------------------

    private async Task<StepResult> ApplyUpsertAsync(SqliteConnection connection, SyncSchema schema, SyncTable table, SyncChange change, ParkSlip park, CancellationToken ct)
    {
        var key = ParseKey(table, change.EntityKey);
        var exists = RowExists(connection, table, key);
        var stored = exists
            ? ReadStoredGroups(connection, table, key, change.Groups.Keys)
            : ReadClockWatermarks(connection, table, change);

        var merge = Merge.Apply(stored, change);
        if (!merge.Changed)
        {
            return StepResult.Skipped; // local is newer, or this exact change came back (equal clocks)
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

    // ---- Purge (§7.4 step 2.1) -----------------------------------------------------------

    private static void PurgeEntity(SqliteConnection connection, SyncSchema schema, SyncTable table, string entityKey)
    {
        var key = ParseKey(table, entityKey);
        var kindSql = SyncPurgeSql.For(table.Kind, [.. key.Values]);
        using var command = connection.CreateCommand();
        if (kindSql.Count > 0)
        {
            command.CommandText = string.Join("\n", kindSql);
        }
        else
        {
            command.CommandText = $"DELETE FROM {SyncSql.Quote(table.Table)} WHERE {Where(table, key)};";
        }

        BindKey(command, table, key);
        command.ExecuteNonQuery();

        // The entity's clocks stay as a watermark: a replayed change older than the last one seen
        // for this entity must not resurrect the row, while a genuinely newer insert (the row
        // re-created on another device) still wins.
    }

    // ---- Unique rules (§7.6) ---------------------------------------------------------------

    private enum ConstraintKind
    {
        Unique,
        ForeignKey,
        NotNull,
        Check,
        Other,
    }

    private enum RuleOutcome
    {
        /// <summary>The rule resolved the conflict and the row was written.</summary>
        Resolved,

        /// <summary>The change waits in the inbox (no rule, or its parent has not arrived).</summary>
        Parked,
    }

    private static ConstraintKind Classify(SqliteException error, out IReadOnlyList<string> columns, out string? namedIndex)
    {
        columns = [];
        namedIndex = null;
        var message = error.Message ?? string.Empty;
        const string unique = "UNIQUE constraint failed: ";
        const string byIndex = "UNIQUE constraint failed: index '";
        if (message.Contains(byIndex, StringComparison.OrdinalIgnoreCase))
        {
            // An index with an expression column (the tag sibling index) is reported by name.
            var from = message.IndexOf(byIndex, StringComparison.OrdinalIgnoreCase) + byIndex.Length;
            var close = message.IndexOf('\'', from);
            namedIndex = message.Substring(from, close - from);
            return ConstraintKind.Unique;
        }

        if (message.Contains(unique, StringComparison.OrdinalIgnoreCase))
        {
            var start = message.IndexOf(unique, StringComparison.OrdinalIgnoreCase) + unique.Length;
            // The engine wraps the column list in quotes ('Cols'.'), so trim its punctuation.
            columns = [.. message[start..].Split(", ").Select(part => part.Trim(".'. \n\r".ToCharArray()).Split('.').Last())];
            return ConstraintKind.Unique;
        }

        if (message.Contains("FOREIGN KEY constraint failed", StringComparison.OrdinalIgnoreCase))
        {
            return ConstraintKind.ForeignKey;
        }

        if (message.Contains("NOT NULL constraint failed", StringComparison.OrdinalIgnoreCase))
        {
            return ConstraintKind.NotNull;
        }

        if (message.Contains("CHECK constraint failed", StringComparison.OrdinalIgnoreCase))
        {
            return ConstraintKind.Check;
        }

        return ConstraintKind.Other;
    }

    /// <summary>
    /// Resolves one unique-index break with the catalog's rule (<see cref="IUniqueRule"/>). The
    /// winner is the row with the lowest entity key (ordinal string comparison), so every device
    /// makes the same choice (§7.6).
    /// </summary>
    private async Task<RuleOutcome> ResolveUniqueAsync(
        SqliteConnection connection, SyncSchema schema, SyncTable table, IReadOnlyDictionary<string, string> key, SyncChange change,
        MergeResult merge, IReadOnlyList<string> columns, DateTimeOffset wall, ParkSlip park, string? namedIndex, CancellationToken ct, int depth = 0)
    {
        var index = namedIndex ?? schema.FindUniqueIndex(table.Table, columns);
        if (namedIndex is not null && columns.Count == 0)
        {
            columns = schema.NamedIndexColumns(table.Table, namedIndex);
        }

        var catalogRule = index is null ? null : table.UniqueRules.FirstOrDefault(candidate => candidate.Index == index);
        if (catalogRule is null)
        {
            park.Park($"two rows claim one unique value of {table.Table} and no rule resolves it", "sync.conflict",
                $"A change for {table.Table} collided with a local row and no rule resolved it. It waits in the sync inbox.");
            return RuleOutcome.Parked;
        }

        if (catalogRule.Kind == SyncUniqueRuleKind.Adopt)
        {
            park.Park("this row is adopted during device join, never during an ordinary apply", "sync.adopt",
                "A row arrived that this device can only adopt while joining an account. It waits in the sync inbox.");
            return RuleOutcome.Parked;
        }

        var otherKey = FindConflictingKey(connection, schema, table, catalogRule.Index, columns, merge);
        if (otherKey is null)
        {
            park.Park($"the colliding row of {table.Table} could not be found");
            return RuleOutcome.Parked;
        }

        var rule = IUniqueRule.For(catalogRule.Kind);
        var conflict = new UniqueConflict(
            connection, schema, table, change, key,
            LocalKey: string.Join("|", table.Key.Select(column => otherKey[column])),
            wall,
            WriteNewRowAsync: (written, token) => WriteWinnersAsync(connection, schema, table, key, exists: false, written, wall, change, token),
            Attention: (category, kind, entityKey, message) => Attention(connection, category, kind, entityKey, message));

        merge = rule.ResolveLoser(conflict, merge);
        if (!conflict.IncomingWins)
        {
            // The incoming loser is written in its retired/renamed shape so both devices hold the
            // same rows; its clock is stored so the change does not win again.
            if (table.Kind == "recurrence_occurrence" && OccurrenceIdentityExists(connection, merge))
            {
                // The identity index (SeriesId, NominalKey) is not partial: while the loser's
                // series-and-nominal twin exists here, the loser row cannot, not even tombstoned —
                // no matter which index the violation named. Its clocks are stored as a watermark
                // so the change does not come back round after round.
                StoreLoserClocks(connection, table, change);
            }
            else
            {
                await rule.WriteIncomingLoserAsync(conflict, merge, ct);
            }

            return RuleOutcome.Resolved;
        }

        try
        {
            await WriteWinnersAsync(connection, schema, table, key, RowExists(connection, table, key), merge, wall, change, ct);
            return RuleOutcome.Resolved;
        }
        catch (SqliteException retry)
        {
            var kind = Classify(retry, out var retryColumns, out var retryIndex);
            if (kind == ConstraintKind.ForeignKey)
            {
                park.Park("the change references something that has not arrived yet");
                return RuleOutcome.Parked;
            }

            // Resolving one duplicate can surface the next (an occurrence's identity and its
            // task claim resolve one after the other): resolve them in turn, within reason.
            // The nested resolution writes the row itself, so its outcome is ours.
            if (kind == ConstraintKind.Unique && depth < 2)
            {
                return await ResolveUniqueAsync(connection, schema, table, key, change, merge, retryColumns, wall, park, retryIndex, ct, depth + 1);
            }

            throw;
        }
    }
    private static IReadOnlyDictionary<string, string>? FindConflictingKey(
        SqliteConnection connection, SyncSchema schema, SyncTable table, string index, IReadOnlyList<string> columns, MergeResult merge)
    {
        // An expression index column (the tag sibling index's COALESCE) reaches us as its
        // expression text; only real columns can be looked up, and they identify the row.
        columns = [.. columns.Where(column => schema.HasColumn(table.Table, column))];
        var winnerValues = WinnerValues(merge);
        var values = new List<object?>();
        foreach (var column in columns)
        {
            if (!winnerValues.TryGetValue(column, out var value))
            {
                return null; // the incoming change does not even carry the conflicting column
            }

            values.Add(value);
        }

        using var command = connection.CreateCommand();
        var where = string.Join(" AND ", columns.Select((column, position) => $"{SyncSql.Quote(column)} = $c{position}"));
        command.CommandText = $"SELECT {string.Join(", ", table.Key.Select(SyncSql.Quote))} FROM {SyncSql.Quote(table.Table)} WHERE {where}{PartialIndexPredicate(index)} LIMIT 1;";
        for (var position = 0; position < values.Count; position++)
        {
            command.Parameters.AddWithValue($"$c{position}", values[position] ?? DBNull.Value);
        }

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return table.Key.Select((column, position) => (column, reader.GetValue(position)?.ToString() ?? string.Empty)).ToDictionary();
    }

    /// <summary>The partial-index WHERE text of the rule indexes (§7.2): the row the rule must find.</summary>
    private static string PartialIndexPredicate(string index) => index switch
    {
        "UX_RecurrenceOccurrences_Active" => " AND State = 0 AND DeletedAtUtc IS NULL",
        "UX_RecurrenceOccurrences_Task" or "UX_GitHubIssueLinks_Task" => " AND TaskId IS NOT NULL",
        "UX_GitHubCommentLinks_Comment" => " AND CommentId IS NOT NULL",
        "UX_Pages_SpaceDaily" => " AND DailyDate IS NOT NULL AND DeletedAtUtc IS NULL",
        "UX_FollowUps_ActiveTask" => " AND DeletedAtUtc IS NULL AND ResolvedAtUtc IS NULL",
        "UX_Spaces_PersonalPerOwner" or "UX_Containers_ActiveNamePerSpace" or "UX_Tags_ActiveSiblingName"
            or "UX_TaskTemplates_ActiveNamePerSpace" or "UX_WaitingDetails_ActiveTask"
            or "UX_GitHubConnections_ActivePerSpace" or "UX_GitHubRepositories_ActiveRepository"
            => " AND DeletedAtUtc IS NULL",
        _ => string.Empty,
    };

    /// <summary>True when the change's occurrence identity (series and nominal key) already has a row here.</summary>
    private static bool OccurrenceIdentityExists(SqliteConnection connection, MergeResult merge)
    {
        var values = WinnerValues(merge);
        var series = values.GetValueOrDefault("SeriesId");
        var nominal = values.GetValueOrDefault("NominalKey");
        if (series is null || nominal is null)
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM RecurrenceOccurrences WHERE SeriesId = $series AND NominalKey = $nominal LIMIT 1;";
        command.Parameters.AddWithValue("$series", series);
        command.Parameters.AddWithValue("$nominal", nominal);
        return command.ExecuteScalar() is not null;
    }

    // ---- History (§7.7) ---------------------------------------------------------------------

    private void RecordReplacedValues(
        SqliteConnection connection, SyncTable table, IReadOnlyDictionary<string, string> key,
        IReadOnlyDictionary<string, SyncGroupValues> replaced, IReadOnlyDictionary<string, SyncGroupState> winners, SyncChange change)
    {
        var now = clock.GetUtcNow();
        var winningDevice = winners.Values.Select(winner => winner.Hlc.DeviceId).Order(StringComparer.Ordinal).First();
        var fields = replaced.Values.SelectMany(values => values.Values.Keys).Distinct().Order(StringComparer.Ordinal).ToList();
        // §7.7: field names, before and after values, and the winning device, as valid JSON the
        // history viewer (P13) can read; groups are the keys of the outer objects.
        string Snapshot(object payload) => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["device"] = winningDevice,
            ["change"] = change.ChangeId,
            ["groups"] = payload,
        });
        var before = Snapshot(replaced.ToDictionary(pair => pair.Key, pair => pair.Value.Values, (StringComparer)StringComparer.Ordinal));
        var after = Snapshot(winners.Where(pair => replaced.ContainsKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value.Values, (StringComparer)StringComparer.Ordinal));

        switch (table.Kind)
        {
            case "task" or "subtask":
                var taskId = table.Kind == "task" ? key["Id"] : SyncSql.Text(connection, $"SELECT TaskId FROM Subtasks WHERE Id = {SyncSql.Literal(key["Id"])};");
                if (taskId is null)
                {
                    return;
                }

                using (var history = connection.CreateCommand())
                {
                    var version = SyncSql.Scalar(connection, $"SELECT Version FROM Tasks WHERE Id = {SyncSql.Literal(taskId)};");
                    history.CommandText = """
                        INSERT INTO TaskHistory (Id, TaskId, OperationId, EffectiveAtUtc, Actor, EventKind, ChangedFields, SnapshotBefore, SnapshotAfter, AggregateVersion, RecordedAtUtc)
                        VALUES ($id, $task, $operation, $effective, 'sync', 'sync.merged', $changed, $before, $after, $aggregate, $recorded);
                        """;
                    history.Parameters.AddWithValue("$id", ids.NewId().ToString());
                    history.Parameters.AddWithValue("$task", taskId);
                    history.Parameters.AddWithValue("$operation", change.ChangeId);
                    history.Parameters.AddWithValue("$effective", StorageFormats.Instant(now));
                    history.Parameters.AddWithValue("$changed", string.Join(",", fields));
                    history.Parameters.AddWithValue("$before", before);
                    history.Parameters.AddWithValue("$after", after);
                    history.Parameters.AddWithValue("$aggregate", version + 1);
                    history.Parameters.AddWithValue("$recorded", StorageFormats.Instant(now));
                    history.ExecuteNonQuery();
                }

                break;
            case "page":
                // §7.7: the replaced text becomes a page revision, so "Versions" shows it.
                using (var revision = connection.CreateCommand())
                {
                    var number = SyncSql.Scalar(connection, $"SELECT COALESCE(MAX(Number), 0) + 1 FROM PageRevisions WHERE PageId = {SyncSql.Literal(key["Id"])};");
                    var title = SyncSql.Text(connection, $"SELECT Title FROM Pages WHERE Id = {SyncSql.Literal(key["Id"])};") ?? string.Empty;
                    var markdown = SyncSql.Text(connection, $"SELECT RawMarkdown FROM Pages WHERE Id = {SyncSql.Literal(key["Id"])};") ?? string.Empty;
                    revision.CommandText = """
                        INSERT INTO PageRevisions (PageId, Number, Title, RawMarkdown, CreatedAtUtc, UpdatedAtUtc)
                        VALUES ($page, $number, $title, $markdown, $created, $updated);
                        """;
                    revision.Parameters.AddWithValue("$page", key["Id"]);
                    revision.Parameters.AddWithValue("$number", number);
                    revision.Parameters.AddWithValue("$title", title);
                    revision.Parameters.AddWithValue("$markdown", markdown);
                    revision.Parameters.AddWithValue("$created", StorageFormats.Instant(now));
                    revision.Parameters.AddWithValue("$updated", StorageFormats.Instant(now));
                    revision.ExecuteNonQuery();
                }

                break;
            default:
                return; // §7.7 lists task, subtask and page; pad history arrives with its media work (P10)
        }
    }

    // ---- Search (§7.4 step 3) ---------------------------------------------------------------

    private static async Task RefreshSearchDocumentAsync(SqliteConnection connection, SyncTable table, IReadOnlyDictionary<string, string> key, CancellationToken ct)
    {
        // The five source kinds are all keyed by Id; every other kind has no search document.
        var kind = table.Kind switch
        {
            "task" => SearchTargetKind.Task,
            "subtask" => SearchTargetKind.Subtask,
            "diary_entry" => SearchTargetKind.Diary,
            "pad" => SearchTargetKind.Pad,
            "page" => SearchTargetKind.Page,
            _ => (SearchTargetKind?)null,
        };
        if (kind is null)
        {
            return;
        }

        using var select = connection.CreateCommand();
        select.CommandText = table.Kind switch
        {
            "task" => $"SELECT Id, Id, Title, Notes, DeletedAtUtc IS NULL FROM Tasks WHERE {Where(table, key)};",
            "subtask" => "SELECT s.Id, s.TaskId, s.Title, s.Notes, s.DeletedAtUtc IS NULL AND t.DeletedAtUtc IS NULL FROM Subtasks s JOIN Tasks t ON t.Id = s.TaskId WHERE s.Id = $k0;",
            "diary_entry" => $"SELECT Id, TaskId, '', RawMarkdown, DeletedAtUtc IS NULL FROM DiaryEntries WHERE {Where(table, key)};",
            "pad" => $"SELECT Id, NULL, '', RawMarkdown, DeletedAtUtc IS NULL FROM ScratchPadItems WHERE {Where(table, key)};",
            _ => $"SELECT Id, NULL, Title, RawMarkdown, DeletedAtUtc IS NULL FROM Pages WHERE {Where(table, key)};",
        };
        BindKey(select, table, key);
        using var reader = select.ExecuteReader();
        if (!reader.Read())
        {
            return; // the row is gone again in this same transaction; nothing to index
        }

        var id = reader.GetString(0);
        var taskId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var title = reader.GetString(2);
        var body = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
        var active = reader.GetInt64(4) == 1;
        await SearchIndexWriter.SyncAsync(connection, kind.Value, id, taskId, title, body, active, ct);
    }

    // ---- Parking and attention (§7.8, §7.9) --------------------------------------------------

    /// <summary>Parks a change (§7.8); true when it was not parked before, so its attention item is raised only once.</summary>
    private bool Park(SqliteConnection connection, SyncChange change, string payload, string reason)
    {
        bool isNew;
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM SyncInboxParked WHERE ChangeId = $id;";
            exists.Parameters.AddWithValue("$id", change.ChangeId);
            isNew = exists.ExecuteScalar() is null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SyncInboxParked (ChangeId, Kind, EntityKey, Op, PayloadJson, Reason, FirstParkedAtUtc, PullCycles)
            VALUES ($id, $kind, $key, $op, $payload, $reason, $now, 0)
            ON CONFLICT (ChangeId) DO UPDATE SET Reason = excluded.Reason;
            """;
        command.Parameters.AddWithValue("$id", change.ChangeId);
        command.Parameters.AddWithValue("$kind", change.Kind);
        command.Parameters.AddWithValue("$key", change.EntityKey);
        command.Parameters.AddWithValue("$op", change.Op == SyncOp.Purge ? "purge" : "upsert");
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", StorageFormats.Instant(clock.GetUtcNow()));
        command.ExecuteNonQuery();
        return isNew;
    }

    private static List<(string ChangeId, string Payload)> ReadParked(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ChangeId, PayloadJson FROM SyncInboxParked ORDER BY FirstParkedAtUtc, ChangeId;";
        using var reader = command.ExecuteReader();
        var parked = new List<(string, string)>();
        while (reader.Read())
        {
            parked.Add((reader.GetString(0), reader.GetString(1)));
        }

        return parked;
    }

    private static void DeleteParked(SqliteConnection connection, string changeId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM SyncInboxParked WHERE ChangeId = $id;";
        command.Parameters.AddWithValue("$id", changeId);
        command.ExecuteNonQuery();
    }

    private static void BumpParkedCycles(SqliteConnection connection, string changeId)
    {
        // §7.8: parked for more than ten pull cycles becomes an attention item — the pull loop
        // itself raises it (P13); the counter is what it reads.
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE SyncInboxParked SET PullCycles = PullCycles + 1 WHERE ChangeId = $id;";
        command.Parameters.AddWithValue("$id", changeId);
        command.ExecuteNonQuery();
    }

    private static bool OpenAttentionExists(SqliteConnection connection, string category, string entityKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM SyncAttention WHERE Category = $category AND EntityKey = $key AND ResolvedAtUtc IS NULL LIMIT 1;";
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$key", entityKey);
        return command.ExecuteScalar() is not null;
    }

    private void Attention(SqliteConnection connection, string category, string? kind, string? entityKey, string message)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SyncAttention (Id, Category, Kind, EntityKey, Message, DetailJson, CreatedAtUtc)
            VALUES ($id, $category, $kind, $key, $message, NULL, $now);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", (object?)entityKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$now", StorageFormats.Instant(clock.GetUtcNow()));
        command.ExecuteNonQuery();
    }
}
