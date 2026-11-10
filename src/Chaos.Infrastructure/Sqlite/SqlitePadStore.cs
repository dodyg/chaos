using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>
/// Personal scratch pad items and their task backlinks (PLAN §9). Every add/update syncs the
/// item's search document on the same connection and transaction as the row change.
/// </summary>
public sealed class SqlitePadStore(SqliteConnection connection) : IPadStore
{
    public async Task<PadItemRecord?> GetAsync(string itemId, CancellationToken ct) =>
        (await ReadAsync("SELECT * FROM ScratchPadItems WHERE Id = $id AND DeletedAtUtc IS NULL;", ct, ("$id", itemId))).FirstOrDefault();

    public async Task<PadItemRecord?> GetIncludingDeletedAsync(string itemId, CancellationToken ct) =>
        (await ReadAsync("SELECT * FROM ScratchPadItems WHERE Id = $id;", ct, ("$id", itemId))).FirstOrDefault();

    public async Task<IReadOnlyList<PadItemRecord>> ListBySpaceAsync(string spaceId, bool archived, CancellationToken ct) =>
        await ReadAsync(
            archived
                ? "SELECT * FROM ScratchPadItems WHERE SpaceId = $space AND DeletedAtUtc IS NULL AND ArchivedAtUtc IS NOT NULL ORDER BY ArchivedAtUtc DESC, Id;"
                : "SELECT * FROM ScratchPadItems WHERE SpaceId = $space AND DeletedAtUtc IS NULL AND ArchivedAtUtc IS NULL ORDER BY Position, Id;",
            ct, ("$space", spaceId));

    public async Task<IReadOnlyList<PadItemRecord>> ListDeletedBySpaceAsync(string spaceId, CancellationToken ct) =>
        await ReadAsync("SELECT * FROM ScratchPadItems WHERE SpaceId = $space AND DeletedAtUtc IS NOT NULL ORDER BY DeletedAtUtc DESC, Id;", ct, ("$space", spaceId));

    public async Task AddAsync(PadItemRecord item, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ScratchPadItems (Id, SpaceId, RawMarkdown, Position, ArchivedAtUtc, DraftVersion, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $space, $markdown, $position, $archived, $draft, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$space", item.SpaceId);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(item.CreatedAtUtc));
        Bind(command, item);
        await command.ExecuteNonQueryAsync(ct);
        await SyncSearchAsync(item, ct);
    }

    public async Task<PadItemRecord?> UpdateAsync(PadItemRecord updated, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ScratchPadItems SET RawMarkdown = $markdown, Position = $position, ArchivedAtUtc = $archived,
                DraftVersion = $draft, Version = $version, UpdatedAtUtc = $updated, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expected;
            """;
        command.Parameters.AddWithValue("$id", updated.Id);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        Bind(command, updated);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            return null;
        }

        await SyncSearchAsync(updated, ct);
        return await GetIncludingDeletedAsync(updated.Id, ct);
    }

    public async Task<long> GetNextPositionAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Position), -1024) + 1024 FROM ScratchPadItems WHERE SpaceId = $space AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$space", spaceId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    public async Task AddTaskLinkAsync(ScratchTaskLinkRecord link, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ScratchTaskLinks (PadItemId, TaskId, SourceRevision, StartIndex, EndIndex, Quote, CreatedAtUtc, ContextBefore, ContextAfter)
            VALUES ($item, $task, $revision, $start, $end, $quote, $created, $before, $after);
            """;
        command.Parameters.AddWithValue("$item", link.PadItemId);
        command.Parameters.AddWithValue("$task", link.TaskId);
        command.Parameters.AddWithValue("$revision", link.SourceRevision);
        command.Parameters.AddWithValue("$start", link.StartIndex);
        command.Parameters.AddWithValue("$end", link.EndIndex);
        command.Parameters.AddWithValue("$quote", link.Quote);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(link.CreatedAtUtc));
        command.Parameters.AddWithValue("$before", SqliteProfileStore.OptionalValue(link.ContextBefore));
        command.Parameters.AddWithValue("$after", SqliteProfileStore.OptionalValue(link.ContextAfter));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ScratchTaskLinkRecord>> ListTaskLinksAsync(string itemId, CancellationToken ct) =>
        await ReadLinksAsync("SELECT * FROM ScratchTaskLinks WHERE PadItemId = $id ORDER BY CreatedAtUtc, TaskId;", itemId, ct);

    public async Task<ScratchTaskLinkRecord?> FindTaskLinkAsync(string taskId, CancellationToken ct) =>
        (await ReadLinksAsync("SELECT * FROM ScratchTaskLinks WHERE TaskId = $id;", taskId, ct)).FirstOrDefault();

    public async Task<IReadOnlyList<string>> SearchAsync(string spaceId, string text, int limit, CancellationToken ct)
    {
        var match = SearchText.ToMatchExpression(text);
        if (match is null)
        {
            return [];
        }

        // bm25() is only valid in a plain full-text query, so matches are materialized first
        // (as the task search does); deletion is an ordinary predicate on the source row.
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH Matches AS MATERIALIZED (
                SELECT rowid AS RowId, bm25(SearchIndex, 10.0, 1.0) AS Rank FROM SearchIndex WHERE SearchIndex MATCH $match)
            SELECT p.Id FROM Matches m
            JOIN SearchDocuments d ON d.RowId = m.RowId AND d.TargetKind = 4
            JOIN ScratchPadItems p ON p.Id = d.TargetId
            WHERE p.SpaceId = $space AND p.DeletedAtUtc IS NULL
            ORDER BY m.Rank, p.Id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$space", spaceId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static void Bind(SqliteCommand command, PadItemRecord item)
    {
        command.Parameters.AddWithValue("$markdown", item.RawMarkdown);
        command.Parameters.AddWithValue("$position", item.Position);
        command.Parameters.AddWithValue("$archived", SqliteProfileStore.OptionalInstant(item.ArchivedAtUtc));
        command.Parameters.AddWithValue("$draft", item.DraftVersion);
        command.Parameters.AddWithValue("$version", item.Version);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(item.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(item.DeletedAtUtc));
    }

    private Task SyncSearchAsync(PadItemRecord item, CancellationToken ct) =>
        SearchIndexWriter.SyncAsync(connection, SearchTargetKind.Pad, item.Id, null, string.Empty, item.RawMarkdown, item.DeletedAtUtc is null, ct);

    private async Task<IReadOnlyList<PadItemRecord>> ReadAsync(string sql, CancellationToken ct, params (string Name, string Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<PadItemRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PadItemRecord
            {
                Id = reader.S("Id"),
                SpaceId = reader.S("SpaceId"),
                RawMarkdown = reader.S("RawMarkdown"),
                Position = reader.L("Position"),
                ArchivedAtUtc = reader.In("ArchivedAtUtc"),
                DraftVersion = reader.L("DraftVersion"),
                Version = reader.L("Version"),
                CreatedAtUtc = reader.I("CreatedAtUtc"),
                UpdatedAtUtc = reader.I("UpdatedAtUtc"),
                DeletedAtUtc = reader.In("DeletedAtUtc"),
            });
        }

        return rows;
    }

    private async Task<IReadOnlyList<ScratchTaskLinkRecord>> ReadLinksAsync(string sql, string id, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<ScratchTaskLinkRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ScratchTaskLinkRecord(
                reader.S("PadItemId"),
                reader.S("TaskId"),
                reader.L("SourceRevision"),
                (int)(reader.Ln("StartIndex") ?? 0),
                (int)(reader.Ln("EndIndex") ?? 0),
                reader.S("Quote"),
                reader.I("CreatedAtUtc"),
                reader.Sn("ContextBefore"),
                reader.Sn("ContextAfter")));
        }

        return rows;
    }
}
