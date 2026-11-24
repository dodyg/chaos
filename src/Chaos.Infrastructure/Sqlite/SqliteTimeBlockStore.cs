using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Application.Contracts;
using Chaos.Infrastructure.Sync;
using Chaos.Sync;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>Google time blocking rows on the caller's connection and transaction (PLAN3 §11); no network access.</summary>
public sealed class SqliteTimeBlockStore(SqliteConnection connection, IActivityRecorder? activity = null) : ITimeBlockStore
{
    public async Task<IReadOnlyList<TimeBlockEventRecord>> ListEventsAsync(CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TimeBlockEvents ORDER BY TaskId;";
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<TimeBlockEventRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    public async Task<TimeBlockEventRecord?> FindByEventIdAsync(string eventId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TimeBlockEvents WHERE EventId = $eventId;";
        command.Parameters.AddWithValue("$eventId", eventId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task AddEventAsync(TimeBlockEventRecord record, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TimeBlockEvents (TaskId, EventId, ETag, PushedHash, LastSyncedAtUtc, RemoteMovedAtUtc, Version, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($taskId, $eventId, $etag, $hash, $synced, $moved, $version, $created, $updated);
            """;
        Bind(command, record);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<TimeBlockEventRecord?> UpdateEventAsync(TimeBlockEventRecord updated, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE TimeBlockEvents SET EventId = $eventId, ETag = $etag, PushedHash = $hash, LastSyncedAtUtc = $synced, RemoteMovedAtUtc = $moved,
                Version = $version, UpdatedAtUtc = $updated
            WHERE TaskId = $taskId AND Version = $expected;
            """;
        Bind(command, updated);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? await FindByTaskIdAsync(updated.Id, ct) : null;
    }

    public async Task DeleteEventAsync(string taskId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM TimeBlockEvents WHERE TaskId = $taskId;";
        command.Parameters.AddWithValue("$taskId", taskId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<TimeBlockState?> GetStateAsync(CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CalendarId, SyncToken, PageToken, LastPassUtc, LastError, NeedsSignIn FROM TimeBlockState WHERE Id = 1;";
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new TimeBlockState(reader.Sn("CalendarId"), reader.Sn("SyncToken"), reader.Sn("PageToken"), reader.In("LastPassUtc"), reader.Sn("LastError"), reader.B("NeedsSignIn"))
            : null;
    }

    public async Task SaveStateAsync(TimeBlockState state, CancellationToken ct)
    {
        activity?.Note("time_block_state", "1", ActivityOp.Upsert);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TimeBlockState (Id, CalendarId, SyncToken, PageToken, LastPassUtc, LastError, NeedsSignIn)
            VALUES (1, $calendar, $sync, $page, $pass, $error, $signIn)
            ON CONFLICT (Id) DO UPDATE SET CalendarId = excluded.CalendarId, SyncToken = excluded.SyncToken, PageToken = excluded.PageToken,
                LastPassUtc = excluded.LastPassUtc, LastError = excluded.LastError, NeedsSignIn = excluded.NeedsSignIn;
            """;
        command.Parameters.AddWithValue("$calendar", (object?)state.CalendarId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sync", (object?)state.SyncToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$page", (object?)state.PageToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$pass", StorageFormats.Optional(state.LastPassUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)state.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$signIn", state.NeedsSignIn ? 1L : 0L);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<long?> TaskActivityHeadAsync(DateTimeOffset? since, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(e.Seq) FROM ActivityLogEntities e JOIN ActivityLog l ON l.Seq = e.Seq
            WHERE e.Kind IN ('task', 'task_assignee') AND l.Action <> $own AND ($since IS NULL OR l.OccurredAtUtc > $since);
            """;
        command.Parameters.AddWithValue("$own", ActivityActions.TimeBlockApply);
        command.Parameters.AddWithValue("$since", StorageFormats.Optional(since) ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync(ct);
        return value is long seq ? seq : null;
    }

    public async Task<DateTimeOffset?> PlannedGroupTimeAsync(string taskId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Hlc FROM SyncClocks WHERE Kind = 'task' AND EntityKey = $taskId AND GroupName = 'planned';";
        command.Parameters.AddWithValue("$taskId", taskId);
        if (await command.ExecuteScalarAsync(ct) is not string text || !Hlc.TryParse(text, out var clock))
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(clock.WallMillis);
    }

    public async Task<string> GetDeviceIdAsync(CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM DeviceSettings WHERE Key='DeviceId'";
        return await command.ExecuteScalarAsync(ct) as string ?? throw new InvalidOperationException("This device has no identity.");
    }

    private async Task<TimeBlockEventRecord?> FindByTaskIdAsync(string taskId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TimeBlockEvents WHERE TaskId = $taskId;";
        command.Parameters.AddWithValue("$taskId", taskId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    private static void Bind(SqliteCommand command, TimeBlockEventRecord record)
    {
        command.Parameters.AddWithValue("$taskId", record.Id);
        command.Parameters.AddWithValue("$eventId", record.EventId);
        command.Parameters.AddWithValue("$etag", (object?)record.ETag ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", (object?)record.PushedHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$synced", StorageFormats.Optional(record.LastSyncedAtUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$moved", StorageFormats.Optional(record.RemoteMovedAtUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", record.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(record.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(record.UpdatedAtUtc));
    }

    private static TimeBlockEventRecord Read(SqliteDataReader r) => new()
    {
        Id = r.S("TaskId"),
        EventId = r.S("EventId"),
        ETag = r.Sn("ETag"),
        PushedHash = r.Sn("PushedHash"),
        LastSyncedAtUtc = r.In("LastSyncedAtUtc"),
        RemoteMovedAtUtc = r.In("RemoteMovedAtUtc"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
    };
}
