using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Reminders;
using Chaos.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>Waiting details and follow-up rules (migration 010).</summary>
public sealed class SqliteWaitingStore(SqliteConnection connection) : IWaitingStore
{
    public async Task<WaitingDetailRecord?> GetAsync(string id, CancellationToken ct) =>
        (await ReadWaitingAsync("SELECT * FROM WaitingDetails WHERE Id=$value", id, ct)).FirstOrDefault();

    public async Task<WaitingDetailRecord?> FindActiveAsync(string taskId, CancellationToken ct) =>
        (await ReadWaitingAsync("SELECT * FROM WaitingDetails WHERE TaskId=$value AND DeletedAtUtc IS NULL", taskId, ct)).FirstOrDefault();

    public async Task AddAsync(WaitingDetailRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO WaitingDetails (Id,TaskId,ProfileId,Person,PersonNormalized,StartedAtUtc,Version,CreatedAtUtc,UpdatedAtUtc,DeletedAtUtc)
            VALUES ($id,$task,$profile,$person,$normalized,$started,$version,$created,$updated,$deleted)
            """;
        BindWaiting(command, row);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<WaitingDetailRecord?> UpdateAsync(WaitingDetailRecord row, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE WaitingDetails SET Person=$person,PersonNormalized=$normalized,StartedAtUtc=$started,Version=$version,
            UpdatedAtUtc=$updated,DeletedAtUtc=$deleted WHERE Id=$id AND Version=$expected
            """;
        BindWaiting(command, row);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? row : null;
    }

    public async Task<FollowUpRecord?> GetFollowUpAsync(string id, CancellationToken ct) =>
        (await ReadFollowUpsAsync("SELECT * FROM FollowUps WHERE Id=$value", id, ct)).FirstOrDefault();

    public async Task<FollowUpRecord?> FindActiveFollowUpAsync(string taskId, CancellationToken ct) =>
        (await ReadFollowUpsAsync("SELECT * FROM FollowUps WHERE TaskId=$value AND DeletedAtUtc IS NULL AND ResolvedAtUtc IS NULL", taskId, ct)).FirstOrDefault();

    public Task<IReadOnlyList<FollowUpRecord>> ListActiveFollowUpsAsync(string profileId, CancellationToken ct) =>
        ReadFollowUpsAsync("""
            SELECT * FROM FollowUps WHERE ProfileId=$value AND DeletedAtUtc IS NULL AND ResolvedAtUtc IS NULL
            ORDER BY DueAtUtc IS NULL, DueAtUtc, Id
            """, profileId, ct);

    public async Task AddFollowUpAsync(FollowUpRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO FollowUps (Id,TaskId,ProfileId,Kind,FixedAtUtc,Amount,Unit,SnoozedUntilUtc,DueAtUtc,ResolvedAtUtc,Version,CreatedAtUtc,UpdatedAtUtc,DeletedAtUtc)
            VALUES ($id,$task,$profile,$kind,$fixed,$amount,$unit,$snoozed,NULL,$resolved,$version,$created,$updated,$deleted)
            """;
        BindFollowUp(command, row);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<FollowUpRecord?> UpdateFollowUpAsync(FollowUpRecord row, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        // DueAtUtc is derived and left to SetFollowUpDueAsync.
        command.CommandText = """
            UPDATE FollowUps SET Kind=$kind,FixedAtUtc=$fixed,Amount=$amount,Unit=$unit,SnoozedUntilUtc=$snoozed,ResolvedAtUtc=$resolved,
            Version=$version,UpdatedAtUtc=$updated,DeletedAtUtc=$deleted WHERE Id=$id AND Version=$expected
            """;
        BindFollowUp(command, row);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? row : null;
    }

    public async Task SetFollowUpDueAsync(string followUpId, DateTimeOffset? dueAtUtc, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE FollowUps SET DueAtUtc=$due WHERE Id=$id";
        command.Parameters.AddWithValue("$id", followUpId);
        command.Parameters.AddWithValue("$due", Instant(dueAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<WaitingDetailRecord>> ReadWaitingAsync(string sql, string value, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<WaitingDetailRecord>();
        while (await reader.ReadAsync(ct)) rows.Add(new WaitingDetailRecord
        {
            Id = reader.S("Id"), TaskId = reader.S("TaskId"), ProfileId = reader.S("ProfileId"), Person = reader.S("Person"),
            StartedAtUtc = reader.I("StartedAtUtc"), Version = reader.L("Version"), CreatedAtUtc = reader.I("CreatedAtUtc"),
            UpdatedAtUtc = reader.I("UpdatedAtUtc"), DeletedAtUtc = reader.In("DeletedAtUtc"),
        });
        return rows;
    }

    private async Task<IReadOnlyList<FollowUpRecord>> ReadFollowUpsAsync(string sql, string value, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<FollowUpRecord>();
        while (await reader.ReadAsync(ct)) rows.Add(new FollowUpRecord
        {
            Id = reader.S("Id"), TaskId = reader.S("TaskId"), ProfileId = reader.S("ProfileId"), Kind = (FollowUpKind)reader.L("Kind"),
            FixedAtUtc = reader.In("FixedAtUtc"), Amount = reader.Ln("Amount") is { } amount ? checked((int)amount) : null,
            Unit = reader.Ln("Unit") is { } unit ? (DurationUnit)unit : null, SnoozedUntilUtc = reader.In("SnoozedUntilUtc"),
            DueAtUtc = reader.In("DueAtUtc"), ResolvedAtUtc = reader.In("ResolvedAtUtc"), Version = reader.L("Version"),
            CreatedAtUtc = reader.I("CreatedAtUtc"), UpdatedAtUtc = reader.I("UpdatedAtUtc"), DeletedAtUtc = reader.In("DeletedAtUtc"),
        });
        return rows;
    }

    private static void BindWaiting(SqliteCommand command, WaitingDetailRecord row)
    {
        command.Parameters.AddWithValue("$id", row.Id);
        command.Parameters.AddWithValue("$task", row.TaskId);
        command.Parameters.AddWithValue("$profile", row.ProfileId);
        command.Parameters.AddWithValue("$person", row.Person);
        command.Parameters.AddWithValue("$normalized", SearchText.Normalize(row.Person).ToLowerInvariant());
        command.Parameters.AddWithValue("$started", StorageFormats.Instant(row.StartedAtUtc));
        command.Parameters.AddWithValue("$version", row.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(row.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(row.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", Instant(row.DeletedAtUtc));
    }

    private static void BindFollowUp(SqliteCommand command, FollowUpRecord row)
    {
        command.Parameters.AddWithValue("$id", row.Id);
        command.Parameters.AddWithValue("$task", row.TaskId);
        command.Parameters.AddWithValue("$profile", row.ProfileId);
        command.Parameters.AddWithValue("$kind", (int)row.Kind);
        command.Parameters.AddWithValue("$fixed", Instant(row.FixedAtUtc));
        command.Parameters.AddWithValue("$amount", (object?)row.Amount ?? DBNull.Value);
        command.Parameters.AddWithValue("$unit", row.Unit is { } unit ? (int)unit : DBNull.Value);
        command.Parameters.AddWithValue("$snoozed", Instant(row.SnoozedUntilUtc));
        command.Parameters.AddWithValue("$resolved", Instant(row.ResolvedAtUtc));
        command.Parameters.AddWithValue("$version", row.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(row.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(row.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", Instant(row.DeletedAtUtc));
    }

    internal static object Instant(DateTimeOffset? value) => value is { } instant ? StorageFormats.Instant(instant) : DBNull.Value;
}

/// <summary>Desired and delivered notifications (migration 010).</summary>
public sealed class SqliteNotificationJobStore(SqliteConnection connection) : INotificationJobStore
{
    public async Task<NotificationJobRecord?> GetAsync(string id, CancellationToken ct) =>
        (await ReadAsync("SELECT * FROM NotificationJobs WHERE Id=$a", ct, ("$a", id))).FirstOrDefault();

    public async Task<NotificationJobRecord?> FindLatestAsync(NotificationSourceKind kind, string sourceId, CancellationToken ct) =>
        (await ReadAsync("SELECT * FROM NotificationJobs WHERE SourceKind=$a AND SourceId=$b ORDER BY Generation DESC, Sequence DESC LIMIT 1", ct,
            ("$a", (int)kind), ("$b", sourceId))).FirstOrDefault();

    public Task<IReadOnlyList<NotificationJobRecord>> ListBySourceAsync(NotificationSourceKind kind, string sourceId, CancellationToken ct) =>
        ReadAsync("SELECT * FROM NotificationJobs WHERE SourceKind=$a AND SourceId=$b ORDER BY Generation, Sequence", ct, ("$a", (int)kind), ("$b", sourceId));

    public Task<IReadOnlyList<NotificationJobRecord>> ListPendingByTaskAsync(string taskId, CancellationToken ct) =>
        ReadAsync("SELECT * FROM NotificationJobs WHERE TaskId=$a AND State=0 ORDER BY Id", ct, ("$a", taskId));

    public async Task AddAsync(NotificationJobRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO NotificationJobs (Id,ProfileId,TaskId,SourceKind,SourceId,Generation,Sequence,Reason,TargetAtUtc,NotBeforeUtc,DeliverAtUtc,
                State,DeliveredAtUtc,Presentation,AcknowledgedAtUtc,ActionTaken,OsState,OsNotificationId,OsAttempts,OsLastAttemptUtc,OsError,CreatedAtUtc,UpdatedAtUtc)
            VALUES ($id,$profile,$task,$sourceKind,$sourceId,$generation,$sequence,$reason,$target,$notBefore,$deliverAt,
                $state,$delivered,$presentation,$acknowledged,$action,$osState,$osId,$osAttempts,$osLast,$osError,$created,$updated)
            """;
        Bind(command, row);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> UpdateAsync(NotificationJobRecord row, NotificationJobState expectedState, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE NotificationJobs SET NotBeforeUtc=$notBefore,DeliverAtUtc=$deliverAt,State=$state,DeliveredAtUtc=$delivered,
                Presentation=$presentation,AcknowledgedAtUtc=$acknowledged,ActionTaken=$action,OsState=$osState,OsNotificationId=$osId,
                OsAttempts=$osAttempts,OsLastAttemptUtc=$osLast,OsError=$osError,UpdatedAtUtc=$updated
            WHERE Id=$id AND State=$expected
            """;
        Bind(command, row);
        command.Parameters.AddWithValue("$expected", (int)expectedState);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public Task<IReadOnlyList<NotificationJobRecord>> ListDueAsync(string profileId, DateTimeOffset now, int limit, CancellationToken ct) =>
        ReadAsync("SELECT * FROM NotificationJobs WHERE ProfileId=$a AND State=0 AND DeliverAtUtc<=$b ORDER BY DeliverAtUtc, Id LIMIT $c", ct,
            ("$a", profileId), ("$b", StorageFormats.Instant(now)), ("$c", limit));

    public Task<IReadOnlyList<NotificationJobRecord>> ListOsWorkAsync(string profileId, int limit, CancellationToken ct) =>
        ReadAsync("""
            SELECT * FROM NotificationJobs WHERE ProfileId=$a AND (
                (State=0 AND OsState IN (0,2)) OR (State IN (2,3,4) AND OsNotificationId IS NOT NULL))
            ORDER BY DeliverAtUtc, Id LIMIT $b
            """, ct, ("$a", profileId), ("$b", limit));

    public Task<IReadOnlyList<NotificationJobRecord>> ListDeliveredAsync(string profileId, int limit, CancellationToken ct) =>
        ReadAsync("SELECT * FROM NotificationJobs WHERE ProfileId=$a AND DeliveredAtUtc IS NOT NULL ORDER BY DeliveredAtUtc DESC, Id DESC LIMIT $b", ct,
            ("$a", profileId), ("$b", limit));

    public async Task<IReadOnlyList<string>> ListTasksWithSourcesAsync(string profileId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TaskId FROM ReminderRules WHERE ProfileId=$profile AND DeletedAtUtc IS NULL
            UNION SELECT TaskId FROM FollowUps WHERE ProfileId=$profile AND DeletedAtUtc IS NULL AND ResolvedAtUtc IS NULL
            UNION SELECT TaskId FROM NotificationJobs WHERE ProfileId=$profile AND State=0
            ORDER BY 1
            """;
        command.Parameters.AddWithValue("$profile", profileId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<string>();
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        return ids;
    }

    private async Task<IReadOnlyList<NotificationJobRecord>> ReadAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<NotificationJobRecord>();
        while (await reader.ReadAsync(ct)) rows.Add(new NotificationJobRecord
        {
            Id = reader.S("Id"), ProfileId = reader.S("ProfileId"), TaskId = reader.S("TaskId"),
            SourceKind = (NotificationSourceKind)reader.L("SourceKind"), SourceId = reader.S("SourceId"),
            Generation = reader.L("Generation"), Sequence = reader.L("Sequence"), Reason = (NotificationReason)reader.L("Reason"),
            TargetAtUtc = reader.I("TargetAtUtc"), NotBeforeUtc = reader.In("NotBeforeUtc"), DeliverAtUtc = reader.I("DeliverAtUtc"),
            State = (NotificationJobState)reader.L("State"), DeliveredAtUtc = reader.In("DeliveredAtUtc"),
            Presentation = (NotificationPresentation)reader.L("Presentation"), AcknowledgedAtUtc = reader.In("AcknowledgedAtUtc"),
            ActionTaken = reader.Sn("ActionTaken"), OsState = (NotificationOsState)reader.L("OsState"), OsNotificationId = reader.Sn("OsNotificationId"),
            OsAttempts = checked((int)reader.L("OsAttempts")), OsLastAttemptUtc = reader.In("OsLastAttemptUtc"), OsError = reader.Sn("OsError"),
            CreatedAtUtc = reader.I("CreatedAtUtc"), UpdatedAtUtc = reader.I("UpdatedAtUtc"),
        });
        return rows;
    }

    private static void Bind(SqliteCommand command, NotificationJobRecord row)
    {
        command.Parameters.AddWithValue("$id", row.Id);
        command.Parameters.AddWithValue("$profile", row.ProfileId);
        command.Parameters.AddWithValue("$task", row.TaskId);
        command.Parameters.AddWithValue("$sourceKind", (int)row.SourceKind);
        command.Parameters.AddWithValue("$sourceId", row.SourceId);
        command.Parameters.AddWithValue("$generation", row.Generation);
        command.Parameters.AddWithValue("$sequence", row.Sequence);
        command.Parameters.AddWithValue("$reason", (int)row.Reason);
        command.Parameters.AddWithValue("$target", StorageFormats.Instant(row.TargetAtUtc));
        command.Parameters.AddWithValue("$notBefore", SqliteWaitingStore.Instant(row.NotBeforeUtc));
        command.Parameters.AddWithValue("$deliverAt", StorageFormats.Instant(row.DeliverAtUtc));
        command.Parameters.AddWithValue("$state", (int)row.State);
        command.Parameters.AddWithValue("$delivered", SqliteWaitingStore.Instant(row.DeliveredAtUtc));
        command.Parameters.AddWithValue("$presentation", (int)row.Presentation);
        command.Parameters.AddWithValue("$acknowledged", SqliteWaitingStore.Instant(row.AcknowledgedAtUtc));
        command.Parameters.AddWithValue("$action", (object?)row.ActionTaken ?? DBNull.Value);
        command.Parameters.AddWithValue("$osState", (int)row.OsState);
        command.Parameters.AddWithValue("$osId", (object?)row.OsNotificationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$osAttempts", row.OsAttempts);
        command.Parameters.AddWithValue("$osLast", SqliteWaitingStore.Instant(row.OsLastAttemptUtc));
        command.Parameters.AddWithValue("$osError", (object?)row.OsError ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(row.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(row.UpdatedAtUtc));
    }
}
