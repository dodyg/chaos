using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

public sealed class SqliteTaskStore(SqliteConnection connection) : ITaskStore
{
    public async Task<IReadOnlyList<TaskAssigneeRecord>> ListAssigneesAsync(string taskId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TaskAssignees WHERE TaskId=$task ORDER BY MemberId;";
        command.Parameters.AddWithValue("$task", taskId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<TaskAssigneeRecord>();
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.S("TaskId"), reader.S("MemberId"), reader.I("AddedAtUtc"), reader.In("CompletedAtUtc"), reader.In("DeletedAtUtc")));
        return rows;
    }

    public async Task SaveAssigneeAsync(TaskAssigneeRecord assignee, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TaskAssignees (TaskId,MemberId,AddedAtUtc,CompletedAtUtc,DeletedAtUtc)
            VALUES ($task,$member,$added,$completed,$deleted)
            ON CONFLICT(TaskId,MemberId) DO UPDATE SET AddedAtUtc=excluded.AddedAtUtc,
                CompletedAtUtc=excluded.CompletedAtUtc, DeletedAtUtc=excluded.DeletedAtUtc;
            """;
        command.Parameters.AddWithValue("$task", assignee.TaskId);
        command.Parameters.AddWithValue("$member", assignee.MemberId);
        command.Parameters.AddWithValue("$added", StorageFormats.Instant(assignee.AddedAtUtc));
        command.Parameters.AddWithValue("$completed", SqliteProfileStore.OptionalInstant(assignee.CompletedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(assignee.DeletedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<IReadOnlyList<TaskRecord>> ListDeletedByContainerAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Tasks WHERE ContainerId = $container AND DeletedAtUtc IS NOT NULL ORDER BY DeletedAtUtc DESC, Id;";
        command.Parameters.AddWithValue("$container", containerId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<TaskRecord>();
        while (await reader.ReadAsync(ct)) results.Add(ReadTask(reader));
        return results;
    }
    public async Task<TaskRecord?> GetAsync(string taskId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Tasks WHERE Id = $id AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$id", taskId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTask(reader) : null;
    }

    public async Task<TaskRecord?> GetIncludingDeletedAsync(string taskId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Tasks WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", taskId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTask(reader) : null;
    }

    public async Task<IReadOnlyList<TaskRecord>> ListByContainerAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Tasks WHERE ContainerId = $container AND DeletedAtUtc IS NULL ORDER BY Position, Id;";
        command.Parameters.AddWithValue("$container", containerId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var tasks = new List<TaskRecord>();
        while (await reader.ReadAsync(ct))
        {
            tasks.Add(ReadTask(reader));
        }

        return tasks;
    }

    /// <summary>Live tasks with a planned date, in the given spaces or every space, ordered by (PlannedDate, Id) (PLAN3 §11).</summary>
    public async Task<IReadOnlyList<TaskRecord>> ListPlannedAsync(IReadOnlyList<string>? spaceIds, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        var filter = string.Empty;
        if (spaceIds is { Count: > 0 } chosen)
        {
            // One parameter per space: the person has at most a hundred shared spaces, far below SQLite's parameter limit.
            filter = " AND SpaceId IN (" + string.Join(", ", chosen.Select((_, index) => "$space" + index)) + ")";
            for (var index = 0; index < chosen.Count; index++)
            {
                command.Parameters.AddWithValue("$space" + index, chosen[index]);
            }
        }

        command.CommandText = "SELECT * FROM Tasks WHERE DeletedAtUtc IS NULL AND PlannedDate IS NOT NULL" + filter + " ORDER BY PlannedDate, Id;";
        using var reader = await command.ExecuteReaderAsync(ct);
        var tasks = new List<TaskRecord>();
        while (await reader.ReadAsync(ct))
        {
            tasks.Add(ReadTask(reader));
        }

        return tasks;
    }

    public async Task AddAsync(TaskRecord task, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Tasks (Id, SpaceId, ContainerId, SectionId, Title, Notes, Status, CustomStateId, Priority,
                EstimateMinutes, CapacityCategory, PlannedDate, PlannedTime, PlannedZoneId,
                DueDate, DueTime, DueZoneId, CompletedAtUtc, CancelledAtUtc, Position,
                RecurrenceOccurrenceKey, AssignMode, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc,
                ClaimedById, ClaimedAtUtc, ClaimExpiresAtUtc, AgentReady, AgentAssigneeId)
            VALUES ($id, $space, $container, $section, $title, $notes, $status, $customState, $priority,
                $estimate, $category, $plannedDate, $plannedTime, $plannedZone,
                $dueDate, $dueTime, $dueZone, $completed, $cancelled, $position,
                $occurrenceKey, $assignMode, $version, $created, $updated, $deleted,
                $claimedBy, $claimedAt, $claimExpires, $agentReady, $agentAssignee);
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$space", task.SpaceId);
        command.Parameters.AddWithValue("$container", task.ContainerId);
        command.Parameters.AddWithValue("$section", SqliteProfileStore.OptionalValue(task.SectionId));
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$notes", task.Notes);
        command.Parameters.AddWithValue("$status", (long)task.Status);
        command.Parameters.AddWithValue("$customState", SqliteProfileStore.OptionalValue(task.CustomStateId));
        command.Parameters.AddWithValue("$priority", (long)task.Priority);
        command.Parameters.AddWithValue("$estimate", task.EstimateMinutes is null ? System.DBNull.Value : (object)(long)task.EstimateMinutes.Value);
        command.Parameters.AddWithValue("$category", (long)task.CapacityCategory);
        command.Parameters.AddWithValue("$plannedDate", SqliteProfileStore.OptionalDate(task.PlannedDate));
        command.Parameters.AddWithValue("$plannedTime", SqliteProfileStore.Optional(task.PlannedTime));
        command.Parameters.AddWithValue("$plannedZone", SqliteProfileStore.OptionalValue(task.PlannedZoneId));
        command.Parameters.AddWithValue("$dueDate", SqliteProfileStore.OptionalDate(task.DueDate));
        command.Parameters.AddWithValue("$dueTime", SqliteProfileStore.Optional(task.DueTime));
        command.Parameters.AddWithValue("$dueZone", SqliteProfileStore.OptionalValue(task.DueZoneId));
        command.Parameters.AddWithValue("$completed", SqliteProfileStore.OptionalInstant(task.CompletedAtUtc));
        command.Parameters.AddWithValue("$cancelled", SqliteProfileStore.OptionalInstant(task.CancelledAtUtc));
        command.Parameters.AddWithValue("$position", task.Position);
        command.Parameters.AddWithValue("$occurrenceKey", SqliteProfileStore.OptionalValue(task.RecurrenceOccurrenceKey));
        command.Parameters.AddWithValue("$assignMode", (long)task.AssignMode);
        command.Parameters.AddWithValue("$version", task.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(task.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(task.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(task.DeletedAtUtc));
        BindClaim(command, task);
        await command.ExecuteNonQueryAsync(ct);
        await IndexAsync(task, ct);
    }

    /// <summary>Keeps the task's search document in step with the row, in the same transaction (PLAN §5).</summary>
    private async Task IndexAsync(TaskRecord task, CancellationToken ct) =>
        await Search.SearchIndexWriter.SyncAsync(connection, Search.SearchTargetKind.Task, task.Id, task.Id, task.Title, task.Notes, task.DeletedAtUtc is null, ct);

    public async Task<TaskRecord?> UpdateAsync(TaskRecord updated, long expectedVersion, CancellationToken ct)
    {
        // Optimistic concurrency (PLAN §5): UPDATE ... WHERE Id = @id AND Version = @expected must affect one row.
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Tasks SET
                ContainerId = $container, SectionId = $section, Position = $position, Title = $title, Notes = $notes, Status = $status, CustomStateId = $customState, Priority = $priority,
                EstimateMinutes = $estimate, CapacityCategory = $category,
                PlannedDate = $plannedDate, PlannedTime = $plannedTime, PlannedZoneId = $plannedZone,
                DueDate = $dueDate, DueTime = $dueTime, DueZoneId = $dueZone,
                CompletedAtUtc = $completed, CancelledAtUtc = $cancelled,
                RecurrenceOccurrenceKey = $occurrenceKey, AssignMode = $assignMode,
                Version = $newVersion, UpdatedAtUtc = $updated_at, DeletedAtUtc = $deleted,
                ClaimedById = $claimedBy, ClaimedAtUtc = $claimedAt, ClaimExpiresAtUtc = $claimExpires,
                AgentReady = $agentReady, AgentAssigneeId = $agentAssignee
            WHERE Id = $id AND Version = $expectedVersion;
            """;
        command.Parameters.AddWithValue("$container", updated.ContainerId);
        command.Parameters.AddWithValue("$section", SqliteProfileStore.OptionalValue(updated.SectionId));
        command.Parameters.AddWithValue("$position", updated.Position);
        command.Parameters.AddWithValue("$title", updated.Title);
        command.Parameters.AddWithValue("$notes", updated.Notes);
        command.Parameters.AddWithValue("$status", (long)updated.Status);
        command.Parameters.AddWithValue("$customState", SqliteProfileStore.OptionalValue(updated.CustomStateId));
        command.Parameters.AddWithValue("$priority", (long)updated.Priority);
        command.Parameters.AddWithValue("$estimate", updated.EstimateMinutes is null ? System.DBNull.Value : (object)(long)updated.EstimateMinutes.Value);
        command.Parameters.AddWithValue("$category", (long)updated.CapacityCategory);
        command.Parameters.AddWithValue("$plannedDate", SqliteProfileStore.OptionalDate(updated.PlannedDate));
        command.Parameters.AddWithValue("$plannedTime", SqliteProfileStore.Optional(updated.PlannedTime));
        command.Parameters.AddWithValue("$plannedZone", SqliteProfileStore.OptionalValue(updated.PlannedZoneId));
        command.Parameters.AddWithValue("$dueDate", SqliteProfileStore.OptionalDate(updated.DueDate));
        command.Parameters.AddWithValue("$dueTime", SqliteProfileStore.Optional(updated.DueTime));
        command.Parameters.AddWithValue("$dueZone", SqliteProfileStore.OptionalValue(updated.DueZoneId));
        command.Parameters.AddWithValue("$completed", SqliteProfileStore.OptionalInstant(updated.CompletedAtUtc));
        command.Parameters.AddWithValue("$cancelled", SqliteProfileStore.OptionalInstant(updated.CancelledAtUtc));
        command.Parameters.AddWithValue("$occurrenceKey", SqliteProfileStore.OptionalValue(updated.RecurrenceOccurrenceKey));
        command.Parameters.AddWithValue("$assignMode", (long)updated.AssignMode);
        command.Parameters.AddWithValue("$newVersion", updated.Version);
        command.Parameters.AddWithValue("$updated_at", StorageFormats.Instant(updated.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(updated.DeletedAtUtc));
        BindClaim(command, updated);
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

    public async Task<long> GetNextPositionAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Position), -1024) + 1024 FROM Tasks WHERE ContainerId = $container AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$container", containerId);
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value);
    }

    private static void BindClaim(SqliteCommand command, TaskRecord task)
    {
        command.Parameters.AddWithValue("$claimedBy", SqliteProfileStore.OptionalValue(task.ClaimedById));
        command.Parameters.AddWithValue("$claimedAt", SqliteProfileStore.OptionalInstant(task.ClaimedAtUtc));
        command.Parameters.AddWithValue("$claimExpires", SqliteProfileStore.OptionalInstant(task.ClaimExpiresAtUtc));
        command.Parameters.AddWithValue("$agentReady", task.AgentReady ? 1 : 0);
        command.Parameters.AddWithValue("$agentAssignee", SqliteProfileStore.OptionalValue(task.AgentAssigneeId));
    }

    public async Task<IReadOnlyList<TaskRecord>> ListClaimsExpiredAsync(DateTimeOffset before, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Tasks WHERE ClaimedById IS NOT NULL AND ClaimExpiresAtUtc <= $before AND DeletedAtUtc IS NULL ORDER BY ClaimExpiresAtUtc, Id;";
        command.Parameters.AddWithValue("$before", StorageFormats.Instant(before));
        return await ReadAllAsync(command, ct);
    }

    public async Task<IReadOnlyList<TaskRecord>> ListClaimedByAsync(string claimedById, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Tasks WHERE ClaimedById = $claimed AND DeletedAtUtc IS NULL ORDER BY ClaimExpiresAtUtc, Id;";
        command.Parameters.AddWithValue("$claimed", claimedById);
        return await ReadAllAsync(command, ct);
    }

    private static async Task<IReadOnlyList<TaskRecord>> ReadAllAsync(SqliteCommand command, CancellationToken ct)
    {
        using var reader = await command.ExecuteReaderAsync(ct);
        var tasks = new List<TaskRecord>();
        while (await reader.ReadAsync(ct))
        {
            tasks.Add(ReadTask(reader));
        }

        return tasks;
    }

    internal static TaskRecord ReadTask(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        SpaceId = r.S("SpaceId"),
        ContainerId = r.S("ContainerId"),
        SectionId = r.Sn("SectionId"),
        Title = r.S("Title"),
        Notes = r.S("Notes"),
        Status = (TaskItemStatus)(int)r.L("Status"),
        CustomStateId = r.Sn("CustomStateId"),
        AssignMode = (TaskAssignMode)(int)r.L("AssignMode"),
        Priority = (TaskPriority)(int)r.L("Priority"),
        EstimateMinutes = r.Ln("EstimateMinutes") is { } minutes ? (int)minutes : null,
        CapacityCategory = (CapacityCategory)(int)r.L("CapacityCategory"),
        PlannedDate = r.Dn("PlannedDate"),
        PlannedTime = r.Tn("PlannedTime"),
        PlannedZoneId = r.Sn("PlannedZoneId"),
        DueDate = r.Dn("DueDate"),
        DueTime = r.Tn("DueTime"),
        DueZoneId = r.Sn("DueZoneId"),
        CompletedAtUtc = r.In("CompletedAtUtc"),
        CancelledAtUtc = r.In("CancelledAtUtc"),
        Position = r.L("Position"),
        RecurrenceOccurrenceKey = r.Sn("RecurrenceOccurrenceKey"),
        ClaimedById = r.Sn("ClaimedById"),
        ClaimedAtUtc = r.In("ClaimedAtUtc"),
        ClaimExpiresAtUtc = r.In("ClaimExpiresAtUtc"),
        AgentReady = r.L("AgentReady") != 0,
        AgentAssigneeId = r.Sn("AgentAssigneeId"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };
}
