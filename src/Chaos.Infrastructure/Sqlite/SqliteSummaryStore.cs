using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Summaries;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>Stored summary revisions and the period reads behind them (migration 011).</summary>
public sealed class SqliteSummaryStore(SqliteConnection connection) : ISummaryStore
{
    // The newest revision of each period; a row is current when no later revision of the same period exists.
    private const string Latest = """
        NOT EXISTS (SELECT 1 FROM Summaries later
                    WHERE later.ProfileId = s.ProfileId AND later.Kind = s.Kind AND later.PeriodStart = s.PeriodStart
                      AND later.Revision > s.Revision)
        """;

    public async Task<SummaryRecord?> GetAsync(string id, CancellationToken ct) =>
        (await ReadAsync("SELECT * FROM Summaries s WHERE Id = $id;", ct, ("$id", id))).FirstOrDefault();

    public async Task<SummaryRecord?> FindLatestAsync(string profileId, SummaryKind kind, DateOnly periodStart, CancellationToken ct) =>
        (await ListRevisionsAsync(profileId, kind, periodStart, ct)).FirstOrDefault();

    public async Task<IReadOnlyList<SummaryRecord>> ListRevisionsAsync(string profileId, SummaryKind kind, DateOnly periodStart, CancellationToken ct) =>
        await ReadAsync(
            "SELECT * FROM Summaries s WHERE ProfileId = $profile AND Kind = $kind AND PeriodStart = $start ORDER BY Revision DESC;",
            ct, ("$profile", profileId), ("$kind", (long)kind), ("$start", StorageFormats.Date(periodStart)));

    public async Task<bool> TryAddAsync(SummaryRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Summaries (Id, ProfileId, Kind, PeriodStart, PeriodEnd, Revision, PeriodStartUtc, PeriodEndUtc,
                CapturedThroughUtc, ZoneId, Trigger, Facts, ResourceVersion, GeneratedAtUtc, PromptAtUtc, PromptSeenAtUtc)
            VALUES ($id, $profile, $kind, $start, $end, $revision, $startUtc, $endUtc,
                $captured, $zone, $trigger, $facts, $resource, $generated, $prompt, $seen)
            ON CONFLICT (ProfileId, Kind, PeriodStart, Revision) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", row.Id);
        command.Parameters.AddWithValue("$profile", row.ProfileId);
        command.Parameters.AddWithValue("$kind", (long)row.Kind);
        command.Parameters.AddWithValue("$start", StorageFormats.Date(row.PeriodStart));
        command.Parameters.AddWithValue("$end", StorageFormats.Date(row.PeriodEnd));
        command.Parameters.AddWithValue("$revision", row.Revision);
        command.Parameters.AddWithValue("$startUtc", StorageFormats.Instant(row.PeriodStartUtc));
        command.Parameters.AddWithValue("$endUtc", StorageFormats.Instant(row.PeriodEndUtc));
        command.Parameters.AddWithValue("$captured", StorageFormats.Instant(row.CapturedThroughUtc));
        command.Parameters.AddWithValue("$zone", row.ZoneId);
        command.Parameters.AddWithValue("$trigger", (long)row.Trigger);
        command.Parameters.AddWithValue("$facts", row.FactsJson);
        command.Parameters.AddWithValue("$resource", (long)row.ResourceVersion);
        command.Parameters.AddWithValue("$generated", StorageFormats.Instant(row.GeneratedAtUtc));
        command.Parameters.AddWithValue("$prompt", SqliteProfileStore.OptionalInstant(row.PromptAtUtc));
        command.Parameters.AddWithValue("$seen", SqliteProfileStore.OptionalInstant(row.PromptSeenAtUtc));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task MarkPromptSeenAsync(string id, DateTimeOffset seenAtUtc, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Summaries SET PromptSeenAtUtc = $seen WHERE Id = $id AND PromptSeenAtUtc IS NULL;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$seen", StorageFormats.Instant(seenAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SummaryRecord>> ListJournalAsync(string profileId, DateOnly? endBefore, SummaryKind? kindBefore, int limit, CancellationToken ct)
    {
        var cursor = endBefore is null ? "" : "AND (s.PeriodEnd < $end OR (s.PeriodEnd = $end AND s.Kind < $kind))";
        var parameters = new List<(string, object)> { ("$profile", profileId), ("$limit", (long)Math.Clamp(limit, 1, 200)) };
        if (endBefore is { } end)
        {
            parameters.Add(("$end", StorageFormats.Date(end)));
            parameters.Add(("$kind", (long)(kindBefore ?? SummaryKind.Weekly + 1)));
        }

        return await ReadAsync(
            $"SELECT * FROM Summaries s WHERE s.ProfileId = $profile AND {Latest} {cursor} ORDER BY s.PeriodEnd DESC, s.Kind DESC LIMIT $limit;",
            ct, [.. parameters]);
    }

    public async Task<IReadOnlyList<SummaryRecord>> ListPromptsDueAsync(string profileId, DateTimeOffset now, CancellationToken ct) =>
        await ReadAsync(
            $"""
            SELECT * FROM Summaries s
            WHERE s.ProfileId = $profile AND s.PromptAtUtc IS NOT NULL AND s.PromptAtUtc <= $now AND s.PromptSeenAtUtc IS NULL AND {Latest}
            ORDER BY s.PromptAtUtc DESC, s.Kind DESC LIMIT 20;
            """,
            ct, ("$profile", profileId), ("$now", StorageFormats.Instant(now)));

    public async Task<IReadOnlyList<TaskHistoryRecord>> ListHistorySinceAsync(string spaceId, DateTimeOffset fromUtc, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.* FROM TaskHistory h JOIN Tasks t ON t.Id = h.TaskId
            WHERE t.SpaceId = $space AND h.EffectiveAtUtc >= $from
            ORDER BY h.EffectiveAtUtc, h.rowid;
            """;
        command.Parameters.AddWithValue("$space", spaceId);
        command.Parameters.AddWithValue("$from", StorageFormats.Instant(fromUtc));
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<TaskHistoryRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(SqliteTaskHistoryStore.ReadHistoryEntry(reader));
        }

        return rows;
    }

    public async Task<IReadOnlyList<DiaryEntryRecord>> ListDiaryEntriesAsync(string spaceId, DateTimeOffset fromUtc, DateTimeOffset beforeUtc, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.* FROM DiaryEntries d JOIN Tasks t ON t.Id = d.TaskId
            WHERE t.SpaceId = $space AND d.DeletedAtUtc IS NULL AND d.EntryAtUtc >= $from AND d.EntryAtUtc < $before
            ORDER BY d.EntryAtUtc, d.Id;
            """;
        command.Parameters.AddWithValue("$space", spaceId);
        command.Parameters.AddWithValue("$from", StorageFormats.Instant(fromUtc));
        command.Parameters.AddWithValue("$before", StorageFormats.Instant(beforeUtc));
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<DiaryEntryRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(SqliteDiaryStore.ReadDiaryEntry(reader));
        }

        return rows;
    }

    private async Task<IReadOnlyList<SummaryRecord>> ReadAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<SummaryRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new SummaryRecord
            {
                Id = reader.S("Id"),
                ProfileId = reader.S("ProfileId"),
                Kind = (SummaryKind)(int)reader.L("Kind"),
                PeriodStart = StorageFormats.ParseDate(reader.S("PeriodStart"))!.Value,
                PeriodEnd = StorageFormats.ParseDate(reader.S("PeriodEnd"))!.Value,
                Revision = reader.L("Revision"),
                PeriodStartUtc = reader.I("PeriodStartUtc"),
                PeriodEndUtc = reader.I("PeriodEndUtc"),
                CapturedThroughUtc = reader.I("CapturedThroughUtc"),
                ZoneId = reader.S("ZoneId"),
                Trigger = (SummaryTrigger)(int)reader.L("Trigger"),
                FactsJson = reader.S("Facts"),
                ResourceVersion = (int)reader.L("ResourceVersion"),
                GeneratedAtUtc = reader.I("GeneratedAtUtc"),
                PromptAtUtc = reader.In("PromptAtUtc"),
                PromptSeenAtUtc = reader.In("PromptSeenAtUtc"),
            });
        }

        return rows;
    }
}
