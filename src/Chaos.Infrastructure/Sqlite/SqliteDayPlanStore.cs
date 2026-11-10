using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>My Day plans and items (PLAN §10); migration 008 holds the constraints.</summary>
public sealed class SqliteDayPlanStore(SqliteConnection connection) : IDayPlanStore
{
    public async Task<DayPlanRecord?> FindAsync(string profileId, DateOnly localDate, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM DayPlans WHERE ProfileId = $profile AND LocalDate = $date;";
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$date", StorageFormats.Date(localDate));
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadPlan(reader) : null;
    }

    public async Task<bool> TryAddAsync(DayPlanRecord plan, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO DayPlans (Id, ProfileId, LocalDate, SeededWeekPlanId, SeededWeekRevision, InitializedAtUtc, Version, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, $profile, $date, $weekPlan, $weekRevision, $initialized, $version, $created, $updated)
            ON CONFLICT (ProfileId, LocalDate) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", plan.Id);
        command.Parameters.AddWithValue("$profile", plan.ProfileId);
        command.Parameters.AddWithValue("$date", StorageFormats.Date(plan.LocalDate));
        command.Parameters.AddWithValue("$weekPlan", (object?)plan.SeededWeekPlanId ?? DBNull.Value);
        command.Parameters.AddWithValue("$weekRevision", (object?)plan.SeededWeekRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$initialized", StorageFormats.Instant(plan.InitializedAtUtc));
        command.Parameters.AddWithValue("$version", plan.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(plan.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(plan.UpdatedAtUtc));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<DayPlanItemRecord>> ListItemsAsync(string dayPlanId, bool includeRemoved, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = includeRemoved
            ? "SELECT * FROM DayPlanItems WHERE DayPlanId = $plan ORDER BY Position, Id;"
            : "SELECT * FROM DayPlanItems WHERE DayPlanId = $plan AND RemovedAtUtc IS NULL ORDER BY Position, Id;";
        command.Parameters.AddWithValue("$plan", dayPlanId);
        return await ReadItemsAsync(command, ct);
    }

    public async Task<DayPlanItemRecord?> FindItemAsync(string dayPlanId, string taskId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM DayPlanItems WHERE DayPlanId = $plan AND TaskId = $task;";
        command.Parameters.AddWithValue("$plan", dayPlanId);
        command.Parameters.AddWithValue("$task", taskId);
        return (await ReadItemsAsync(command, ct)).FirstOrDefault();
    }

    public async Task AddItemAsync(DayPlanItemRecord item, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO DayPlanItems (Id, DayPlanId, Kind, TaskId, Position, Origin, AddedAtUtc, RemovedAtUtc, Version, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, $plan, 0, $task, $position, $origin, $added, $removed, $version, $created, $updated);
            """;
        BindItem(command, item);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<DayPlanItemRecord?> UpdateItemAsync(DayPlanItemRecord item, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE DayPlanItems SET Position = $position, Origin = $origin, AddedAtUtc = $added, RemovedAtUtc = $removed,
                Version = $version, UpdatedAtUtc = $updated
            WHERE Id = $id AND Version = $expected;
            """;
        BindItem(command, item);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? item : null;
    }

    public async Task<IReadOnlyList<string>> ListRecurrenceTaskIdsDueOnAsync(string spaceId, DateOnly localDate, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT t.Id FROM RecurrenceOccurrences o
            JOIN Tasks t ON t.Id = o.TaskId
            JOIN Containers c ON c.Id = t.ContainerId
            WHERE t.SpaceId = $space AND t.DeletedAtUtc IS NULL AND t.Status NOT IN (4,5)
              AND c.DeletedAtUtc IS NULL AND c.ArchivedAtUtc IS NULL
              AND o.State = 0 AND o.DeletedAtUtc IS NULL AND substr(o.EffectiveLocal, 1, 10) = $date
            ORDER BY t.Id;
            """;
        command.Parameters.AddWithValue("$space", spaceId);
        command.Parameters.AddWithValue("$date", StorageFormats.Date(localDate));
        using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task<IReadOnlyList<DayPlanItemRecord>> ReadItemsAsync(SqliteCommand command, CancellationToken ct)
    {
        using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<DayPlanItemRecord>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new DayPlanItemRecord
            {
                Id = reader.S("Id"),
                DayPlanId = reader.S("DayPlanId"),
                TaskId = reader.S("TaskId"),
                Position = reader.L("Position"),
                Origin = (DayPlanItemOrigin)reader.L("Origin"),
                AddedAtUtc = reader.I("AddedAtUtc"),
                RemovedAtUtc = reader.In("RemovedAtUtc"),
                Version = reader.L("Version"),
                CreatedAtUtc = reader.I("CreatedAtUtc"),
                UpdatedAtUtc = reader.I("UpdatedAtUtc"),
            });
        }

        return items;
    }

    private static DayPlanRecord ReadPlan(SqliteDataReader reader) => new()
    {
        Id = reader.S("Id"),
        ProfileId = reader.S("ProfileId"),
        LocalDate = reader.Dn("LocalDate") ?? throw new InvalidOperationException("A day plan has no date."),
        SeededWeekPlanId = reader.Sn("SeededWeekPlanId"),
        SeededWeekRevision = reader.Ln("SeededWeekRevision"),
        InitializedAtUtc = reader.I("InitializedAtUtc"),
        Version = reader.L("Version"),
        CreatedAtUtc = reader.I("CreatedAtUtc"),
        UpdatedAtUtc = reader.I("UpdatedAtUtc"),
    };

    private static void BindItem(SqliteCommand command, DayPlanItemRecord item)
    {
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$plan", item.DayPlanId);
        command.Parameters.AddWithValue("$task", item.TaskId);
        command.Parameters.AddWithValue("$position", item.Position);
        command.Parameters.AddWithValue("$origin", (int)item.Origin);
        command.Parameters.AddWithValue("$added", StorageFormats.Instant(item.AddedAtUtc));
        command.Parameters.AddWithValue("$removed", item.RemovedAtUtc is { } removed ? StorageFormats.Instant(removed) : DBNull.Value);
        command.Parameters.AddWithValue("$version", item.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(item.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(item.UpdatedAtUtc));
    }
}
