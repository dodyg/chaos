using Chaos.Application.Abstractions;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Search;

/// <summary>Kinds of search document (matches the SearchDocuments.TargetKind check constraint).</summary>
public enum SearchTargetKind
{
    Task = 1,
    Subtask = 2,
    Diary = 3,
    Pad = 4,
}

/// <summary>
/// Maintains SearchDocuments/SearchIndex on the caller's connection, so a document always
/// changes in the same transaction as its source row (PLAN §5). Active sources have exactly
/// one document; tombstoned sources have none.
/// </summary>
public static class SearchIndexWriter
{
    /// <summary>Writes (or removes, when <paramref name="active"/> is false) the document of one source row.</summary>
    public static async Task SyncAsync(
        SqliteConnection connection,
        SearchTargetKind kind,
        string targetId,
        string? taskId,
        string title,
        string body,
        bool active,
        CancellationToken ct)
    {
        await RemoveAsync(connection, kind, targetId, ct);
        if (!active)
        {
            return;
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO SearchDocuments (TargetKind, TargetId, TaskId) VALUES ($kind, $target, $task);
            INSERT INTO SearchIndex (rowid, Title, Body) VALUES (last_insert_rowid(), $title, $body);
            """;
        insert.Parameters.AddWithValue("$kind", (long)kind);
        insert.Parameters.AddWithValue("$target", targetId);
        insert.Parameters.AddWithValue("$task", (object?)taskId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$title", SearchText.Normalize(title));
        insert.Parameters.AddWithValue("$body", SearchText.Normalize(body));
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static async Task RemoveAsync(SqliteConnection connection, SearchTargetKind kind, string targetId, CancellationToken ct)
    {
        using var remove = connection.CreateCommand();
        remove.CommandText = """
            DELETE FROM SearchIndex WHERE rowid IN (SELECT RowId FROM SearchDocuments WHERE TargetKind = $kind AND TargetId = $target);
            DELETE FROM SearchDocuments WHERE TargetKind = $kind AND TargetId = $target;
            """;
        remove.Parameters.AddWithValue("$kind", (long)kind);
        remove.Parameters.AddWithValue("$target", targetId);
        await remove.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Drops every document and indexes all active tasks, subtasks of active tasks, diary
    /// entries and pad items again, then records the normalization version. Runs on the
    /// caller's transaction. Returns the number of documents written.
    /// </summary>
    public static async Task<long> RebuildAsync(SqliteConnection connection, DateTimeOffset now, CancellationToken ct)
    {
        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM SearchIndex; DELETE FROM SearchDocuments;";
            await clear.ExecuteNonQueryAsync(ct);
        }

        var sources = new List<(SearchTargetKind Kind, string Id, string? TaskId, string Title, string Body)>();
        async Task ReadAsync(SearchTargetKind kind, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                sources.Add((kind, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        await ReadAsync(SearchTargetKind.Task, "SELECT Id, Id, Title, Notes FROM Tasks WHERE DeletedAtUtc IS NULL;");
        await ReadAsync(SearchTargetKind.Subtask, """
            SELECT s.Id, s.TaskId, s.Title, s.Notes FROM Subtasks s JOIN Tasks t ON t.Id = s.TaskId
            WHERE s.DeletedAtUtc IS NULL AND t.DeletedAtUtc IS NULL;
            """);
        await ReadAsync(SearchTargetKind.Diary, """
            SELECT d.Id, d.TaskId, '', d.RawMarkdown FROM DiaryEntries d JOIN Tasks t ON t.Id = d.TaskId
            WHERE d.DeletedAtUtc IS NULL AND t.DeletedAtUtc IS NULL;
            """);
        await ReadAsync(SearchTargetKind.Pad, "SELECT Id, NULL, '', RawMarkdown FROM ScratchPadItems WHERE DeletedAtUtc IS NULL;");

        foreach (var source in sources)
        {
            await SyncAsync(connection, source.Kind, source.Id, source.TaskId, source.Title, source.Body, active: true, ct);
        }

        using (var state = connection.CreateCommand())
        {
            state.CommandText = "UPDATE SearchIndexState SET NormalizationVersion = $version, RebuiltAtUtc = $now WHERE Id = 1;";
            state.Parameters.AddWithValue("$version", (long)SearchText.NormalizationVersion);
            state.Parameters.AddWithValue("$now", StorageFormats.Instant(now));
            await state.ExecuteNonQueryAsync(ct);
        }

        return sources.Count;
    }

    /// <summary>
    /// Startup check (PLAN §5): fails clearly when the bundled SQLite lacks FTS5, and rebuilds
    /// the index in one transaction when it was written with a different normalization.
    /// </summary>
    public static async Task EnsureCurrentAsync(SqliteConnection connection, DateTimeOffset now, CancellationToken ct)
    {
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT sqlite_compileoption_used('ENABLE_FTS5');";
            if (Convert.ToInt64(await probe.ExecuteScalarAsync(ct)) != 1)
            {
                throw new AppException(AppErrorKind.UnsupportedCapability, "This build of Chaos cannot search: its SQLite engine has no full-text support.");
            }
        }

        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT NormalizationVersion FROM SearchIndexState WHERE Id = 1;";
            if (Convert.ToInt64(await read.ExecuteScalarAsync(ct)) == SearchText.NormalizationVersion)
            {
                return;
            }
        }

        using var transaction = connection.BeginTransaction();
        await RebuildAsync(connection, now, ct);
        transaction.Commit();
    }
}
