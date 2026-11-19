using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

public sealed class SqliteSubtaskStore(SqliteConnection connection) : ISubtaskStore
{
    public async Task<SubtaskRecord?> GetAsync(string subtaskId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Subtasks WHERE Id = $id AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$id", subtaskId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSubtask(reader) : null;
    }

    public async Task<IReadOnlyList<SubtaskRecord>> ListByTaskAsync(string taskId, CancellationToken ct)
    {
        return await ListAsync(taskId, includeDeleted: false, ct);
    }

    public async Task<IReadOnlyList<SubtaskRecord>> ListByTaskIncludingDeletedAsync(string taskId, CancellationToken ct)
    {
        return await ListAsync(taskId, includeDeleted: true, ct);
    }

    private async Task<IReadOnlyList<SubtaskRecord>> ListAsync(string taskId, bool includeDeleted, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = includeDeleted
            ? "SELECT * FROM Subtasks WHERE TaskId = $task ORDER BY Position, Id;"
            : "SELECT * FROM Subtasks WHERE TaskId = $task AND DeletedAtUtc IS NULL ORDER BY Position, Id;";
        command.Parameters.AddWithValue("$task", taskId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var subtasks = new List<SubtaskRecord>();
        while (await reader.ReadAsync(ct))
        {
            subtasks.Add(ReadSubtask(reader));
        }

        return subtasks;
    }

    public async Task AddAsync(SubtaskRecord subtask, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Subtasks (Id, TaskId, Title, Notes, Status, CompletedAtUtc, CancelledAtUtc,
                Position, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $task, $title, $notes, $status, $completed, $cancelled,
                $position, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", subtask.Id);
        command.Parameters.AddWithValue("$task", subtask.TaskId);
        command.Parameters.AddWithValue("$title", subtask.Title);
        command.Parameters.AddWithValue("$notes", subtask.Notes);
        command.Parameters.AddWithValue("$status", (long)subtask.Status);
        command.Parameters.AddWithValue("$completed", SqliteProfileStore.OptionalInstant(subtask.CompletedAtUtc));
        command.Parameters.AddWithValue("$cancelled", SqliteProfileStore.OptionalInstant(subtask.CancelledAtUtc));
        command.Parameters.AddWithValue("$position", subtask.Position);
        command.Parameters.AddWithValue("$version", subtask.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(subtask.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(subtask.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(subtask.DeletedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
        await IndexAsync(subtask, ct);
    }

    /// <summary>Keeps the subtask's search document (linked to its parent task) in step with the row.</summary>
    private async Task IndexAsync(SubtaskRecord subtask, CancellationToken ct) =>
        await Search.SearchIndexWriter.SyncAsync(connection, Search.SearchTargetKind.Subtask, subtask.Id, subtask.TaskId, subtask.Title, subtask.Notes, subtask.DeletedAtUtc is null, ct);

    public async Task<SubtaskRecord?> UpdateAsync(SubtaskRecord updated, long expectedVersion, CancellationToken ct)
    {
        // Optimistic concurrency (PLAN §5): no tombstone filter, so restore can rewrite
        // a deleted row; the version check alone decides.
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Subtasks SET
                Title = $title, Notes = $notes, Status = $status,
                CompletedAtUtc = $completed, CancelledAtUtc = $cancelled, Position = $position,
                Version = $newVersion, UpdatedAtUtc = $updated_at, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expectedVersion;
            """;
        command.Parameters.AddWithValue("$title", updated.Title);
        command.Parameters.AddWithValue("$notes", updated.Notes);
        command.Parameters.AddWithValue("$status", (long)updated.Status);
        command.Parameters.AddWithValue("$completed", SqliteProfileStore.OptionalInstant(updated.CompletedAtUtc));
        command.Parameters.AddWithValue("$cancelled", SqliteProfileStore.OptionalInstant(updated.CancelledAtUtc));
        command.Parameters.AddWithValue("$position", updated.Position);
        command.Parameters.AddWithValue("$newVersion", updated.Version);
        command.Parameters.AddWithValue("$updated_at", StorageFormats.Instant(updated.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(updated.DeletedAtUtc));
        command.Parameters.AddWithValue("$id", updated.Id);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);

        var rows = await command.ExecuteNonQueryAsync(ct);
        if (rows != 1)
        {
            return null;
        }

        await IndexAsync(updated, ct);
        return await GetIncludingDeletedAsync(updated.Id, ct);
    }

    public async Task<long> GetNextPositionAsync(string taskId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Position), -1024) + 1024 FROM Subtasks WHERE TaskId = $task AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$task", taskId);
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value);
    }

    public async Task<SubtaskRecord?> GetIncludingDeletedAsync(string subtaskId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Subtasks WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", subtaskId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSubtask(reader) : null;
    }

    internal static SubtaskRecord ReadSubtask(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        TaskId = r.S("TaskId"),
        Title = r.S("Title"),
        Notes = r.S("Notes"),
        Status = (TaskItemStatus)(int)r.L("Status"),
        CompletedAtUtc = r.In("CompletedAtUtc"),
        CancelledAtUtc = r.In("CancelledAtUtc"),
        Position = r.L("Position"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };
}
