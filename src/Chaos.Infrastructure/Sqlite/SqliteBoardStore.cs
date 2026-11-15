using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>
/// Board settings and card reads (PLAN2 P2). Settings rows are ordinary versioned aggregates;
/// the card queries join the task rows with their subtask progress, tag links and GitHub issue
/// links so a board loads with a bounded number of statements.
/// </summary>
public sealed class SqliteBoardStore(SqliteConnection connection) : IBoardStore
{
    public async Task<BoardSettingsRecord?> GetAsync(BoardTargetKind targetKind, string targetId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM BoardSettings WHERE TargetKind = $kind AND TargetId = $target AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$kind", (int)targetKind);
        command.Parameters.AddWithValue("$target", targetId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSettings(reader) : null;
    }

    public async Task AddAsync(BoardSettingsRecord settings, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO BoardSettings (Id, TargetKind, TargetId, GroupBy, SwimlaneBy, ColumnTagId, SwimlaneTagId, WipLimitsJson, HiddenColumnsJson, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $kind, $target, $group, $lane, $columnTag, $laneTag, $wip, $hidden, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", settings.Id);
        command.Parameters.AddWithValue("$kind", (int)settings.TargetKind);
        command.Parameters.AddWithValue("$target", settings.TargetId);
        command.Parameters.AddWithValue("$group", (int)settings.GroupBy);
        command.Parameters.AddWithValue("$lane", (int)settings.SwimlaneBy);
        command.Parameters.AddWithValue("$columnTag", (object?)settings.ColumnTagId ?? DBNull.Value);
        command.Parameters.AddWithValue("$laneTag", (object?)settings.SwimlaneTagId ?? DBNull.Value);
        command.Parameters.AddWithValue("$wip", settings.WipLimitsJson);
        command.Parameters.AddWithValue("$hidden", settings.HiddenColumnsJson);
        command.Parameters.AddWithValue("$version", settings.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(settings.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(settings.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", StorageFormats.Optional(settings.DeletedAtUtc) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<BoardSettingsRecord?> UpdateAsync(BoardSettingsRecord updated, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE BoardSettings SET GroupBy = $group, SwimlaneBy = $lane, ColumnTagId = $columnTag, SwimlaneTagId = $laneTag,
                WipLimitsJson = $wip, HiddenColumnsJson = $hidden, Version = $version, UpdatedAtUtc = $updated, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expected;
            """;
        command.Parameters.AddWithValue("$id", updated.Id);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        command.Parameters.AddWithValue("$group", (int)updated.GroupBy);
        command.Parameters.AddWithValue("$lane", (int)updated.SwimlaneBy);
        command.Parameters.AddWithValue("$columnTag", (object?)updated.ColumnTagId ?? DBNull.Value);
        command.Parameters.AddWithValue("$laneTag", (object?)updated.SwimlaneTagId ?? DBNull.Value);
        command.Parameters.AddWithValue("$wip", updated.WipLimitsJson);
        command.Parameters.AddWithValue("$hidden", updated.HiddenColumnsJson);
        command.Parameters.AddWithValue("$version", updated.Version);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(updated.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", StorageFormats.Optional(updated.DeletedAtUtc) ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            return null;
        }

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT * FROM BoardSettings WHERE Id = $id;";
        read.Parameters.AddWithValue("$id", updated.Id);
        using var reader = await read.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSettings(reader) : null;
    }

    public async Task<IReadOnlyList<BoardCardRow>> ListCardsAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Tasks.*,
                (SELECT COUNT(*) FROM Subtasks s WHERE s.TaskId = Tasks.Id AND s.DeletedAtUtc IS NULL) AS Subtotal,
                (SELECT COUNT(*) FROM Subtasks s WHERE s.TaskId = Tasks.Id AND s.DeletedAtUtc IS NULL AND s.Status = 4) AS Subdone,
                EXISTS (SELECT 1 FROM GitHubIssueLinks l WHERE l.TaskId = Tasks.Id) AS HasIssue
            FROM Tasks
            WHERE Tasks.ContainerId = $container AND Tasks.DeletedAtUtc IS NULL
            ORDER BY Tasks.Position, Tasks.Id;
            """;
        command.Parameters.AddWithValue("$container", containerId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<BoardCardRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new BoardCardRow(
                SqliteTaskStore.ReadTask(reader),
                (int)reader.L("Subtotal"),
                (int)reader.L("Subdone"),
                reader.B("HasIssue")));
        }

        return rows;
    }

    public async Task<IReadOnlyList<BoardCardTag>> ListCardTagsAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tt.TaskId, tt.TagId
            FROM TaskTags tt
            JOIN Tasks t ON t.Id = tt.TaskId
            WHERE t.ContainerId = $container AND t.DeletedAtUtc IS NULL AND tt.DeletedAtUtc IS NULL
            ORDER BY tt.TaskId, tt.TagId;
            """;
        command.Parameters.AddWithValue("$container", containerId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<BoardCardTag>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new BoardCardTag(reader.S("TaskId"), reader.S("TagId")));
        }

        return rows;
    }

    private static BoardSettingsRecord ReadSettings(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        TargetKind = (BoardTargetKind)(int)r.L("TargetKind"),
        TargetId = r.S("TargetId"),
        GroupBy = (BoardGrouping)(int)r.L("GroupBy"),
        SwimlaneBy = (BoardSwimlane)(int)r.L("SwimlaneBy"),
        ColumnTagId = r.Sn("ColumnTagId"),
        SwimlaneTagId = r.Sn("SwimlaneTagId"),
        WipLimitsJson = r.S("WipLimitsJson"),
        HiddenColumnsJson = r.S("HiddenColumnsJson"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };
}
