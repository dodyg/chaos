using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

public sealed class SqliteTaskHistoryStore(SqliteConnection connection) : ITaskHistoryStore
{
    public async Task AddAsync(TaskHistoryRecord entry, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TaskHistory (Id, TaskId, OperationId, EffectiveAtUtc, Actor, EventKind,
                ChangedFields, SnapshotBefore, SnapshotAfter, AggregateVersion, RecordedAtUtc)
            VALUES ($id, $task, $operation, $effective, $actor, $eventKind, $changed, $before, $after, $aggregateVersion, $recorded);
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$task", entry.TaskId);
        command.Parameters.AddWithValue("$operation", entry.OperationId);
        command.Parameters.AddWithValue("$effective", StorageFormats.Instant(entry.EffectiveAtUtc));
        command.Parameters.AddWithValue("$actor", entry.Actor);
        command.Parameters.AddWithValue("$eventKind", entry.EventKind);
        command.Parameters.AddWithValue("$changed", string.Join(",", entry.ChangedFields));
        command.Parameters.AddWithValue("$before", SqliteProfileStore.OptionalValue(entry.SnapshotBefore));
        command.Parameters.AddWithValue("$after", SqliteProfileStore.OptionalValue(entry.SnapshotAfter));
        command.Parameters.AddWithValue("$aggregateVersion", entry.AggregateVersion);
        command.Parameters.AddWithValue("$recorded", StorageFormats.Instant(entry.RecordedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<TaskHistoryRecord>> ListByTaskAsync(string taskId, CancellationToken ct)
    {
        // Rowid preserves insertion order when multiple events share a clock instant.
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TaskHistory WHERE TaskId = $task ORDER BY rowid;";
        command.Parameters.AddWithValue("$task", taskId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var entries = new List<TaskHistoryRecord>();
        while (await reader.ReadAsync(ct))
        {
            entries.Add(ReadHistoryEntry(reader));
        }

        return entries;
    }

    internal static TaskHistoryRecord ReadHistoryEntry(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        TaskId = r.S("TaskId"),
        OperationId = r.S("OperationId"),
        EffectiveAtUtc = r.I("EffectiveAtUtc"),
        Actor = r.S("Actor"),
        EventKind = r.S("EventKind"),
        ChangedFields = r.S("ChangedFields").Split(',', StringSplitOptions.RemoveEmptyEntries),
        SnapshotBefore = r.Sn("SnapshotBefore"),
        SnapshotAfter = r.Sn("SnapshotAfter"),
        AggregateVersion = r.L("AggregateVersion"),
        RecordedAtUtc = r.I("RecordedAtUtc"),
    };
}
