using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>Calendar sources, the meeting cache, fetch state and event links (PLAN2 P5; migration 028).</summary>
public sealed class SqliteCalendarStore(SqliteConnection connection) : ICalendarStore
{
    public async Task<CalendarSourceRecord?> GetSourceAsync(string id, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM CalendarSources WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSource(reader) : null;
    }

    public async Task<IReadOnlyList<CalendarSourceRecord>> ListSourcesAsync(CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM CalendarSources WHERE DeletedAtUtc IS NULL ORDER BY Position, Id;";
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<CalendarSourceRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadSource(reader));
        }

        return rows;
    }

    public async Task AddSourceAsync(CalendarSourceRecord source, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CalendarSources (Id, Provider, AccountRef, ExternalId, Name, Color, CountsAsBusy, Enabled, OwnerEmail, Position, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $provider, $account, $external, $name, $color, $busy, $enabled, $owner, $position, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", source.Id);
        command.Parameters.AddWithValue("$provider", (int)source.Provider);
        command.Parameters.AddWithValue("$account", (object?)source.AccountRef ?? DBNull.Value);
        command.Parameters.AddWithValue("$external", source.ExternalId);
        command.Parameters.AddWithValue("$name", source.Name);
        command.Parameters.AddWithValue("$color", source.Color);
        command.Parameters.AddWithValue("$busy", source.CountsAsBusy ? 1 : 0);
        command.Parameters.AddWithValue("$enabled", source.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$owner", (object?)source.OwnerEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("$position", source.Position);
        command.Parameters.AddWithValue("$version", source.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(source.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(source.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", StorageFormats.Optional(source.DeletedAtUtc) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<CalendarSourceRecord?> UpdateSourceAsync(CalendarSourceRecord updated, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE CalendarSources SET Name = $name, Color = $color, CountsAsBusy = $busy, Enabled = $enabled, OwnerEmail = $owner,
                Position = $position, Version = $version, UpdatedAtUtc = $updated, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expected;
            """;
        command.Parameters.AddWithValue("$id", updated.Id);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        command.Parameters.AddWithValue("$name", updated.Name);
        command.Parameters.AddWithValue("$color", updated.Color);
        command.Parameters.AddWithValue("$busy", updated.CountsAsBusy ? 1 : 0);
        command.Parameters.AddWithValue("$enabled", updated.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$owner", (object?)updated.OwnerEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("$position", updated.Position);
        command.Parameters.AddWithValue("$version", updated.Version);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(updated.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", StorageFormats.Optional(updated.DeletedAtUtc) ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? await GetSourceAsync(updated.Id, ct) : null;
    }

    public async Task<CalendarSourceState?> GetStateAsync(string sourceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM CalendarSourceState WHERE SourceId = $id;";
        command.Parameters.AddWithValue("$id", sourceId);
        using var r = await command.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new CalendarSourceState(r.S("SourceId"), r.In("LastAttemptUtc"), r.In("LastSuccessUtc"), r.Sn("LastError"), r.Sn("ETag"), r.Sn("LastModified"))
            : null;
    }

    public async Task SaveStateAsync(CalendarSourceState state, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CalendarSourceState (SourceId, LastAttemptUtc, LastSuccessUtc, LastError, ETag, LastModified)
            VALUES ($id, $attempt, $success, $error, $etag, $modified)
            ON CONFLICT (SourceId) DO UPDATE SET LastAttemptUtc = $attempt, LastSuccessUtc = $success, LastError = $error, ETag = $etag, LastModified = $modified;
            """;
        command.Parameters.AddWithValue("$id", state.SourceId);
        command.Parameters.AddWithValue("$attempt", StorageFormats.Optional(state.LastAttemptUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$success", StorageFormats.Optional(state.LastSuccessUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)state.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$etag", (object?)state.ETag ?? DBNull.Value);
        command.Parameters.AddWithValue("$modified", (object?)state.LastModified ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ReplaceEventsAsync(string sourceId, IReadOnlyList<CalendarEventRecord> events, CancellationToken ct)
    {
        await DeleteEventsAsync(sourceId, ct);
        foreach (var item in events)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO CalendarEvents (SourceId, ExternalId, StartUtc, EndUtc, AllDay, StartDate, EndDate, Title, Location, VideoUrl, WebUrl, Busy, FetchedUtc)
                VALUES ($source, $external, $start, $end, $allday, $startDate, $endDate, $title, $location, $video, $web, $busy, $fetched);
                """;
            command.Parameters.AddWithValue("$source", sourceId);
            command.Parameters.AddWithValue("$external", item.ExternalId);
            command.Parameters.AddWithValue("$start", StorageFormats.Instant(item.StartUtc));
            command.Parameters.AddWithValue("$end", StorageFormats.Instant(item.EndUtc));
            command.Parameters.AddWithValue("$allday", item.AllDay ? 1 : 0);
            command.Parameters.AddWithValue("$startDate", item.StartDate is { } s ? StorageFormats.Date(s) : DBNull.Value);
            command.Parameters.AddWithValue("$endDate", item.EndDate is { } e ? StorageFormats.Date(e) : DBNull.Value);
            command.Parameters.AddWithValue("$title", item.Title);
            command.Parameters.AddWithValue("$location", (object?)item.Location ?? DBNull.Value);
            command.Parameters.AddWithValue("$video", (object?)item.VideoUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$web", (object?)item.WebUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$busy", item.Busy ? 1 : 0);
            command.Parameters.AddWithValue("$fetched", StorageFormats.Instant(item.FetchedUtc));
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task DeleteEventsAsync(string sourceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM CalendarEvents WHERE SourceId = $id;";
        command.Parameters.AddWithValue("$id", sourceId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<CalendarEventRecord>> ListEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, DateOnly fromDate, DateOnly toDate, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.* FROM CalendarEvents e
            JOIN CalendarSources s ON s.Id = e.SourceId AND s.DeletedAtUtc IS NULL AND s.Enabled = 1
            WHERE (e.AllDay = 0 AND e.EndUtc > $from AND e.StartUtc < $to)
               OR (e.AllDay = 1 AND e.EndDate > $fromDate AND e.StartDate <= $toDate)
            ORDER BY e.StartUtc, e.SourceId, e.ExternalId;
            """;
        command.Parameters.AddWithValue("$from", StorageFormats.Instant(fromUtc));
        command.Parameters.AddWithValue("$to", StorageFormats.Instant(toUtc));
        command.Parameters.AddWithValue("$fromDate", StorageFormats.Date(fromDate));
        command.Parameters.AddWithValue("$toDate", StorageFormats.Date(toDate));
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<CalendarEventRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadEvent(reader));
        }

        return rows;
    }

    public async Task<CalendarEventRecord?> GetEventAsync(string sourceId, string externalId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM CalendarEvents WHERE SourceId = $source AND ExternalId = $external;";
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$external", externalId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEvent(reader) : null;
    }

    public async Task<CalendarEventLinkRecord?> FindLinkAsync(string taskId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM CalendarEventLinks WHERE TaskId = $task AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$task", taskId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadLink(reader) : null;
    }

    public async Task<IReadOnlyList<CalendarEventLinkRecord>> ListLinksAsync(IReadOnlyList<string> taskIds, CancellationToken ct)
    {
        var rows = new List<CalendarEventLinkRecord>();
        if (taskIds.Count == 0)
        {
            return rows;
        }

        using var command = connection.CreateCommand();
        var names = taskIds.Select((id, index) =>
        {
            command.Parameters.AddWithValue($"$t{index}", id);
            return $"$t{index}";
        });
        command.CommandText = $"SELECT * FROM CalendarEventLinks WHERE DeletedAtUtc IS NULL AND TaskId IN ({string.Join(", ", names)});";
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadLink(reader));
        }

        return rows;
    }

    public async Task AddLinkAsync(CalendarEventLinkRecord link, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CalendarEventLinks (Id, TaskId, Provider, CalendarId, EventId, EventStartUtc, EventTitle, EventWebUrl, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $task, $provider, $calendar, $event, $start, $title, $web, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", link.Id);
        command.Parameters.AddWithValue("$task", link.TaskId);
        command.Parameters.AddWithValue("$provider", (int)link.Provider);
        command.Parameters.AddWithValue("$calendar", link.CalendarId);
        command.Parameters.AddWithValue("$event", link.EventId);
        command.Parameters.AddWithValue("$start", StorageFormats.Instant(link.EventStartUtc));
        command.Parameters.AddWithValue("$title", link.EventTitle);
        command.Parameters.AddWithValue("$web", (object?)link.EventWebUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", link.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(link.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(link.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", StorageFormats.Optional(link.DeletedAtUtc) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<GoogleAccountRecord?> GetAccountAsync(string id, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM GoogleAccounts WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAccount(reader) : null;
    }

    public async Task<IReadOnlyList<GoogleAccountRecord>> ListAccountsAsync(CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM GoogleAccounts WHERE DeletedAtUtc IS NULL ORDER BY CreatedAtUtc, Id;";
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<GoogleAccountRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadAccount(reader));
        }

        return rows;
    }

    public async Task AddAccountAsync(GoogleAccountRecord account, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO GoogleAccounts (Id, Email, Subject, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $email, $subject, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", account.Id);
        command.Parameters.AddWithValue("$email", account.Email);
        command.Parameters.AddWithValue("$subject", account.Subject);
        command.Parameters.AddWithValue("$version", account.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(account.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(account.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", StorageFormats.Optional(account.DeletedAtUtc) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<GoogleAccountRecord?> UpdateAccountAsync(GoogleAccountRecord updated, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE GoogleAccounts SET Email = $email, Version = $version, UpdatedAtUtc = $updated, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expected;
            """;
        command.Parameters.AddWithValue("$id", updated.Id);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        command.Parameters.AddWithValue("$email", updated.Email);
        command.Parameters.AddWithValue("$version", updated.Version);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(updated.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", StorageFormats.Optional(updated.DeletedAtUtc) ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? await GetAccountAsync(updated.Id, ct) : null;
    }

    public async Task<GoogleAccountState?> GetAccountStateAsync(string accountId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM GoogleAccountState WHERE AccountId = $id;";
        command.Parameters.AddWithValue("$id", accountId);
        using var r = await command.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new GoogleAccountState(r.S("AccountId"), r.B("NeedsSignIn"), r.In("LastAttemptUtc"), r.In("LastSuccessUtc"), r.Sn("LastError"))
            : null;
    }

    public async Task SaveAccountStateAsync(GoogleAccountState state, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO GoogleAccountState (AccountId, NeedsSignIn, LastAttemptUtc, LastSuccessUtc, LastError)
            VALUES ($id, $needs, $attempt, $success, $error)
            ON CONFLICT (AccountId) DO UPDATE SET NeedsSignIn = $needs, LastAttemptUtc = $attempt, LastSuccessUtc = $success, LastError = $error;
            """;
        command.Parameters.AddWithValue("$id", state.AccountId);
        command.Parameters.AddWithValue("$needs", state.NeedsSignIn ? 1 : 0);
        command.Parameters.AddWithValue("$attempt", StorageFormats.Optional(state.LastAttemptUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$success", StorageFormats.Optional(state.LastSuccessUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)state.LastError ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static GoogleAccountRecord ReadAccount(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        Email = r.S("Email"),
        Subject = r.S("Subject"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };

    private static CalendarSourceRecord ReadSource(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        Provider = (CalendarProvider)(int)r.L("Provider"),
        AccountRef = r.Sn("AccountRef"),
        ExternalId = r.S("ExternalId"),
        Name = r.S("Name"),
        Color = r.S("Color"),
        CountsAsBusy = r.B("CountsAsBusy"),
        Enabled = r.B("Enabled"),
        OwnerEmail = r.Sn("OwnerEmail"),
        Position = (int)r.L("Position"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };

    private static CalendarEventRecord ReadEvent(SqliteDataReader r) => new(
        r.S("SourceId"), r.S("ExternalId"), r.I("StartUtc"), r.I("EndUtc"), r.B("AllDay"), r.Dn("StartDate"), r.Dn("EndDate"),
        r.S("Title"), r.Sn("Location"), r.Sn("VideoUrl"), r.Sn("WebUrl"), r.B("Busy"), r.I("FetchedUtc"));

    private static CalendarEventLinkRecord ReadLink(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        TaskId = r.S("TaskId"),
        Provider = (CalendarProvider)(int)r.L("Provider"),
        CalendarId = r.S("CalendarId"),
        EventId = r.S("EventId"),
        EventStartUtc = r.I("EventStartUtc"),
        EventTitle = r.S("EventTitle"),
        EventWebUrl = r.Sn("EventWebUrl"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };
}
