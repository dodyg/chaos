using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Sync;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>Delegations and nudges (migration 046).</summary>
public sealed class SqliteDelegationStore(SqliteConnection connection) : IDelegationStore
{
    public Task<TaskDelegationRecord?> GetAsync(string id, CancellationToken ct) =>
        ReadOneAsync("SELECT * FROM TaskDelegations WHERE Id=$id", id, ct);

    public Task<IReadOnlyList<TaskDelegationRecord>> ListByTaskAsync(string taskId, CancellationToken ct) =>
        ReadAsync("""
            SELECT * FROM TaskDelegations WHERE TaskId=$id AND DeletedAtUtc IS NULL
            ORDER BY CASE WHEN State IN (0,1,2) THEN 0 ELSE 1 END, StateChangedAtUtc DESC, Id
            """, taskId, ct);

    public Task<TaskDelegationRecord?> FindLiveForTaskAsync(string taskId, CancellationToken ct) =>
        ReadOneAsync("SELECT * FROM TaskDelegations WHERE TaskId=$id AND DeletedAtUtc IS NULL AND State IN (0,1,2)", taskId, ct);

    public Task<IReadOnlyList<TaskDelegationRecord>> ListLiveFromAsync(string memberId, CancellationToken ct) =>
        ReadAsync("SELECT * FROM TaskDelegations WHERE FromMemberId=$id AND DeletedAtUtc IS NULL AND State IN (0,1,2) ORDER BY StateChangedAtUtc DESC, Id", memberId, ct);

    public Task<IReadOnlyList<TaskDelegationRecord>> ListLiveToAsync(string memberId, CancellationToken ct) =>
        ReadAsync("SELECT * FROM TaskDelegations WHERE ToMemberId=$id AND DeletedAtUtc IS NULL AND State IN (0,1,2) ORDER BY StateChangedAtUtc DESC, Id", memberId, ct);

    public async Task AddAsync(TaskDelegationRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TaskDelegations (Id,TaskId,FromMemberId,ToMemberId,Message,DoneDefinition,DueDate,ReviewRequired,State,StateChangedAtUtc,Response,
                Version,CreatedAtUtc,UpdatedAtUtc,DeletedAtUtc)
            VALUES ($id,$task,$from,$to,$message,$done,$due,$review,$state,$changed,$response,$version,$created,$updated,$deleted)
            """;
        Bind(command, row);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<TaskDelegationRecord?> UpdateAsync(TaskDelegationRecord row, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE TaskDelegations SET Message=$message,DoneDefinition=$done,DueDate=$due,ReviewRequired=$review,State=$state,StateChangedAtUtc=$changed,
                Response=$response,Version=$version,UpdatedAtUtc=$updated,DeletedAtUtc=$deleted
            WHERE Id=$id AND Version=$expected
            """;
        Bind(command, row);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? row : null;
    }

    public Task<IReadOnlyList<NudgeRecord>> ListNudgesAsync(string delegationId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Nudges WHERE DelegationId=$id ORDER BY SentAtUtc, Id";
        command.Parameters.AddWithValue("$id", delegationId);
        return ReadNudgesAsync(command, ct);
    }

    public async Task AddNudgeAsync(NudgeRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Nudges (Id,TaskId,DelegationId,FromMemberId,ToMemberId,Text,SentAtUtc,CreatedAtUtc,UpdatedAtUtc)
            VALUES ($id,$task,$delegation,$from,$to,$text,$sent,$created,$updated)
            """;
        command.Parameters.AddWithValue("$id", row.Id);
        command.Parameters.AddWithValue("$task", row.TaskId);
        command.Parameters.AddWithValue("$delegation", row.DelegationId);
        command.Parameters.AddWithValue("$from", row.FromMemberId);
        command.Parameters.AddWithValue("$to", row.ToMemberId);
        command.Parameters.AddWithValue("$text", row.Text);
        command.Parameters.AddWithValue("$sent", StorageFormats.Instant(row.SentAtUtc));
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(row.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(row.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<TaskDelegationRecord?> ReadOneAsync(string sql, string id, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        var rows = await ReadRowsAsync(command, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    private async Task<IReadOnlyList<TaskDelegationRecord>> ReadAsync(string sql, string id, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return await ReadRowsAsync(command, ct);
    }

    private static async Task<IReadOnlyList<TaskDelegationRecord>> ReadRowsAsync(SqliteCommand command, CancellationToken ct)
    {
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<TaskDelegationRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TaskDelegationRecord
            {
                Id = reader.S("Id"), TaskId = reader.S("TaskId"), FromMemberId = reader.S("FromMemberId"), ToMemberId = reader.S("ToMemberId"),
                Message = reader.S("Message"), DoneDefinition = reader.S("DoneDefinition"), DueDate = reader.Dn("DueDate"),
                ReviewRequired = reader.L("ReviewRequired") != 0, State = (DelegationState)reader.L("State"),
                StateChangedAtUtc = reader.I("StateChangedAtUtc"), Response = reader.Sn("Response"),
                Version = reader.L("Version"), CreatedAtUtc = reader.I("CreatedAtUtc"), UpdatedAtUtc = reader.I("UpdatedAtUtc"), DeletedAtUtc = reader.In("DeletedAtUtc"),
            });
        }
        return rows;
    }

    private static async Task<IReadOnlyList<NudgeRecord>> ReadNudgesAsync(SqliteCommand command, CancellationToken ct)
    {
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<NudgeRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new NudgeRecord
            {
                Id = reader.S("Id"), TaskId = reader.S("TaskId"), DelegationId = reader.S("DelegationId"), FromMemberId = reader.S("FromMemberId"),
                ToMemberId = reader.S("ToMemberId"), Text = reader.S("Text"), SentAtUtc = reader.I("SentAtUtc"),
                CreatedAtUtc = reader.I("CreatedAtUtc"), UpdatedAtUtc = reader.I("UpdatedAtUtc"),
            });
        }
        return rows;
    }

    private static void Bind(SqliteCommand command, TaskDelegationRecord row)
    {
        command.Parameters.AddWithValue("$id", row.Id);
        command.Parameters.AddWithValue("$task", row.TaskId);
        command.Parameters.AddWithValue("$from", row.FromMemberId);
        command.Parameters.AddWithValue("$to", row.ToMemberId);
        command.Parameters.AddWithValue("$message", row.Message);
        command.Parameters.AddWithValue("$done", row.DoneDefinition);
        command.Parameters.AddWithValue("$due", SqliteProfileStore.OptionalDate(row.DueDate));
        command.Parameters.AddWithValue("$review", row.ReviewRequired ? 1 : 0);
        command.Parameters.AddWithValue("$state", (int)row.State);
        command.Parameters.AddWithValue("$changed", StorageFormats.Instant(row.StateChangedAtUtc));
        command.Parameters.AddWithValue("$response", SqliteProfileStore.OptionalValue(row.Response));
        command.Parameters.AddWithValue("$version", row.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(row.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(row.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(row.DeletedAtUtc));
    }
}
