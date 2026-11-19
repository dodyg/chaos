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
}
