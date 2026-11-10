using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

public sealed class SqliteRecurrenceStore(SqliteConnection connection) : IRecurrenceStore
{
    public async Task<long> NextRevisionAsync(string seriesId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Revision), 0) + 1 FROM RecurrenceSeriesRevisions WHERE SeriesId=$id";
        command.Parameters.AddWithValue("$id", seriesId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }
    public async Task<RecurrenceSeriesRecord?> GetSeriesAsync(string id, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM RecurrenceSeries WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSeries(reader) : null;
    }

    public async Task<RecurrenceOccurrenceRecord?> GetOccurrenceAsync(string id, CancellationToken ct) =>
        (await ReadOccurrencesAsync("SELECT * FROM RecurrenceOccurrences WHERE Id=$value", id, ct)).FirstOrDefault();

    public async Task<RecurrenceOccurrenceRecord?> FindByTaskAsync(string taskId, CancellationToken ct) =>
        (await ReadOccurrencesAsync("SELECT * FROM RecurrenceOccurrences WHERE TaskId=$value AND DeletedAtUtc IS NULL", taskId, ct)).FirstOrDefault();

    public async Task<RecurrenceOccurrenceRecord?> FindByKeyAsync(string seriesId, string nominalKey, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM RecurrenceOccurrences WHERE SeriesId=$series AND NominalKey=$key";
        command.Parameters.AddWithValue("$series", seriesId);
        command.Parameters.AddWithValue("$key", nominalKey);
        var id = await command.ExecuteScalarAsync(ct) as string;
        return id is null ? null : await GetOccurrenceAsync(id, ct);
    }

    public Task<IReadOnlyList<RecurrenceOccurrenceRecord>> ListOccurrencesAsync(string seriesId, CancellationToken ct) =>
        ReadOccurrencesAsync("SELECT * FROM RecurrenceOccurrences WHERE SeriesId=$value AND DeletedAtUtc IS NULL ORDER BY NominalKey, Id", seriesId, ct);

    public async Task AddSeriesAsync(RecurrenceSeriesRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO RecurrenceSeries (Id,SpaceId,ContainerId,RuleJson,TemplateJson,Revision,ActionableCount,EndedAtUtc,Version,CreatedAtUtc,UpdatedAtUtc,DeletedAtUtc)
            VALUES ($id,$space,$container,$rule,$template,$revision,$count,$ended,$version,$created,$updated,$deleted)
            """;
        BindSeries(command, row);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<RecurrenceSeriesRecord?> UpdateSeriesAsync(RecurrenceSeriesRecord row, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE RecurrenceSeries SET SpaceId=$space,ContainerId=$container,RuleJson=$rule,TemplateJson=$template,Revision=$revision,
            ActionableCount=$count,EndedAtUtc=$ended,Version=$version,UpdatedAtUtc=$updated,DeletedAtUtc=$deleted WHERE Id=$id AND Version=$expected
            """;
        BindSeries(command, row);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? row : null;
    }

    public async Task AddOccurrenceAsync(RecurrenceOccurrenceRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO RecurrenceOccurrences (Id,SeriesId,NominalKey,ScheduleKey,TaskId,EffectiveLocal,EditSnapshotJson,State,CompletedAtUtc,SeriesRevision,Version,CreatedAtUtc,UpdatedAtUtc,DeletedAtUtc)
            VALUES ($id,$series,$key,$schedule,$task,$effective,$edit,$state,$completed,$revision,$version,$created,$updated,$deleted)
            """;
        BindOccurrence(command, row);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<RecurrenceOccurrenceRecord?> UpdateOccurrenceAsync(RecurrenceOccurrenceRecord row, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE RecurrenceOccurrences SET ScheduleKey=$schedule,TaskId=$task,EffectiveLocal=$effective,EditSnapshotJson=$edit,State=$state,CompletedAtUtc=$completed,
            SeriesRevision=$revision,Version=$version,UpdatedAtUtc=$updated,DeletedAtUtc=$deleted WHERE Id=$id AND Version=$expected
            """;
        BindOccurrence(command, row);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? row : null;
    }

    private async Task<IReadOnlyList<RecurrenceOccurrenceRecord>> ReadOccurrencesAsync(string sql, string value, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);
        using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<RecurrenceOccurrenceRecord>();
        while (await reader.ReadAsync(ct))
            result.Add(new RecurrenceOccurrenceRecord { Id = reader.S("Id"), SeriesId = reader.S("SeriesId"), NominalKey = reader.S("NominalKey"), ScheduleKey = reader.S("ScheduleKey"),
                TaskId = reader.Sn("TaskId"), EffectiveLocal = reader.S("EffectiveLocal"), EditSnapshotJson = reader.Sn("EditSnapshotJson"), State = (OccurrenceState)reader.L("State"),
                CompletedAtUtc = reader.In("CompletedAtUtc"), SeriesRevision = reader.L("SeriesRevision"), Version = reader.L("Version"),
                CreatedAtUtc = reader.I("CreatedAtUtc"), UpdatedAtUtc = reader.I("UpdatedAtUtc"), DeletedAtUtc = reader.In("DeletedAtUtc") });
        return result;
    }

    private static RecurrenceSeriesRecord ReadSeries(SqliteDataReader reader) => new()
    {
        Id = reader.S("Id"), SpaceId = reader.S("SpaceId"), ContainerId = reader.S("ContainerId"), RuleJson = reader.S("RuleJson"),
        TemplateJson = reader.S("TemplateJson"), Revision = reader.L("Revision"), ActionableCount = reader.L("ActionableCount"),
        EndedAtUtc = reader.In("EndedAtUtc"), Version = reader.L("Version"), CreatedAtUtc = reader.I("CreatedAtUtc"),
        UpdatedAtUtc = reader.I("UpdatedAtUtc"), DeletedAtUtc = reader.In("DeletedAtUtc")
    };

    private static void BindSeries(SqliteCommand command, RecurrenceSeriesRecord row)
    {
        BindCommon(command, row);
        command.Parameters.AddWithValue("$space", row.SpaceId);
        command.Parameters.AddWithValue("$container", row.ContainerId);
        command.Parameters.AddWithValue("$rule", row.RuleJson);
        command.Parameters.AddWithValue("$template", row.TemplateJson);
        command.Parameters.AddWithValue("$revision", row.Revision);
        command.Parameters.AddWithValue("$count", row.ActionableCount);
        command.Parameters.AddWithValue("$ended", Optional(row.EndedAtUtc));
    }

    private static void BindOccurrence(SqliteCommand command, RecurrenceOccurrenceRecord row)
    {
        BindCommon(command, row);
        command.Parameters.AddWithValue("$series", row.SeriesId);
        command.Parameters.AddWithValue("$key", row.NominalKey);
        command.Parameters.AddWithValue("$schedule", row.ScheduleKey);
        command.Parameters.AddWithValue("$task", (object?)row.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$effective", row.EffectiveLocal);
        command.Parameters.AddWithValue("$edit", (object?)row.EditSnapshotJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", (int)row.State);
        command.Parameters.AddWithValue("$completed", Optional(row.CompletedAtUtc));
        command.Parameters.AddWithValue("$revision", row.SeriesRevision);
    }

    private static void BindCommon(SqliteCommand command, AggregateRecord row)
    {
        command.Parameters.AddWithValue("$id", row.Id);
        command.Parameters.AddWithValue("$version", row.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(row.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(row.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", Optional(row.DeletedAtUtc));
    }

    private static object Optional(DateTimeOffset? value) => value is { } instant ? StorageFormats.Instant(instant) : DBNull.Value;
}
