using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>
/// Diary entries plus their revision trail (PLAN §9). Every add/update appends the saved
/// state to DiaryRevisions and syncs the search document, on the same connection and
/// transaction as the row change.
/// </summary>
public sealed class SqliteDiaryStore(SqliteConnection connection) : IDiaryStore
{
    public async Task<DiaryEntryRecord?> GetAsync(string entryId, CancellationToken ct) =>
        (await ReadAsync("SELECT * FROM DiaryEntries WHERE Id = $id AND DeletedAtUtc IS NULL;", ("$id", entryId), ct)).FirstOrDefault();

    public async Task<DiaryEntryRecord?> GetIncludingDeletedAsync(string entryId, CancellationToken ct) =>
        (await ReadAsync("SELECT * FROM DiaryEntries WHERE Id = $id;", ("$id", entryId), ct)).FirstOrDefault();

    public async Task<IReadOnlyList<DiaryEntryRecord>> ListByTaskAsync(string taskId, bool includeDeleted, CancellationToken ct) =>
        await ReadAsync(
            includeDeleted
                ? "SELECT * FROM DiaryEntries WHERE TaskId = $id ORDER BY EntryAtUtc, Id;"
                : "SELECT * FROM DiaryEntries WHERE TaskId = $id AND DeletedAtUtc IS NULL ORDER BY EntryAtUtc, Id;",
            ("$id", taskId), ct);

    public async Task AddAsync(DiaryEntryRecord entry, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO DiaryEntries (Id, TaskId, AuthorProfileId, EntryAtUtc, RawMarkdown, Revision, EditedAtUtc,
                Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $task, $author, $entryAt, $markdown, $revision, $edited, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$task", entry.TaskId);
        command.Parameters.AddWithValue("$author", entry.AuthorProfileId);
        command.Parameters.AddWithValue("$entryAt", StorageFormats.Instant(entry.EntryAtUtc));
        command.Parameters.AddWithValue("$markdown", entry.RawMarkdown);
        command.Parameters.AddWithValue("$revision", entry.Revision);
        command.Parameters.AddWithValue("$edited", SqliteProfileStore.OptionalInstant(entry.EditedAtUtc));
        command.Parameters.AddWithValue("$version", entry.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(entry.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(entry.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(entry.DeletedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
        await AfterWriteAsync(entry, ct);
    }

    public async Task<DiaryEntryRecord?> UpdateAsync(DiaryEntryRecord updated, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE DiaryEntries SET EntryAtUtc = $entryAt, RawMarkdown = $markdown, Revision = $revision, EditedAtUtc = $edited,
                Version = $version, UpdatedAtUtc = $updated, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expected;
            """;
        command.Parameters.AddWithValue("$entryAt", StorageFormats.Instant(updated.EntryAtUtc));
        command.Parameters.AddWithValue("$markdown", updated.RawMarkdown);
        command.Parameters.AddWithValue("$revision", updated.Revision);
        command.Parameters.AddWithValue("$edited", SqliteProfileStore.OptionalInstant(updated.EditedAtUtc));
        command.Parameters.AddWithValue("$version", updated.Version);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(updated.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(updated.DeletedAtUtc));
        command.Parameters.AddWithValue("$id", updated.Id);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            return null;
        }

        await AfterWriteAsync(updated, ct);
        return await GetIncludingDeletedAsync(updated.Id, ct);
    }

    public async Task<IReadOnlyList<DiaryRevisionRecord>> ListRevisionsAsync(string entryId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM DiaryRevisions WHERE EntryId = $id ORDER BY Revision;";
        command.Parameters.AddWithValue("$id", entryId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<DiaryRevisionRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new DiaryRevisionRecord(reader.S("EntryId"), reader.L("Revision"), reader.S("RawMarkdown"), reader.B("Deleted"), reader.I("RecordedAtUtc")));
        }

        return rows;
    }

    /// <summary>Appends the saved state (a revision number is written once) and syncs the search document.</summary>
    private async Task AfterWriteAsync(DiaryEntryRecord entry, CancellationToken ct)
    {
        using (var revision = connection.CreateCommand())
        {
            revision.CommandText = """
                INSERT OR IGNORE INTO DiaryRevisions (EntryId, Revision, RawMarkdown, Deleted, RecordedAtUtc)
                VALUES ($id, $revision, $markdown, $deleted, $recorded);
                """;
            revision.Parameters.AddWithValue("$id", entry.Id);
            revision.Parameters.AddWithValue("$revision", entry.Revision);
            revision.Parameters.AddWithValue("$markdown", entry.RawMarkdown);
            revision.Parameters.AddWithValue("$deleted", entry.DeletedAtUtc is null ? 0L : 1L);
            revision.Parameters.AddWithValue("$recorded", StorageFormats.Instant(entry.UpdatedAtUtc));
            await revision.ExecuteNonQueryAsync(ct);
        }

        await SearchIndexWriter.SyncAsync(connection, SearchTargetKind.Diary, entry.Id, entry.TaskId, string.Empty, entry.RawMarkdown, entry.DeletedAtUtc is null, ct);
    }

    private async Task<IReadOnlyList<DiaryEntryRecord>> ReadAsync(string sql, (string Name, string Value) parameter, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<DiaryEntryRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadDiaryEntry(reader));
        }

        return rows;
    }

    internal static DiaryEntryRecord ReadDiaryEntry(SqliteDataReader reader) => new()
    {
        Id = reader.S("Id"),
        TaskId = reader.S("TaskId"),
        AuthorProfileId = reader.S("AuthorProfileId"),
        EntryAtUtc = reader.I("EntryAtUtc"),
        RawMarkdown = reader.S("RawMarkdown"),
        Revision = reader.L("Revision"),
        EditedAtUtc = reader.In("EditedAtUtc"),
        Version = reader.L("Version"),
        CreatedAtUtc = reader.I("CreatedAtUtc"),
        UpdatedAtUtc = reader.I("UpdatedAtUtc"),
        DeletedAtUtc = reader.In("DeletedAtUtc"),
    };
}

public sealed class SqliteUndoStore(SqliteConnection connection) : IUndoStore
{
    public async Task AddAsync(UndoEntryRecord entry, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO UndoEntries (Id, ProfileId, OperationId, Label, InversePayload, ExpectedVersions, AppliedAtUtc, UndoneAtUtc)
            VALUES ($id, $profile, $operation, $label, $inverse, $expected, $applied, $undone);
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$profile", entry.ProfileId);
        command.Parameters.AddWithValue("$operation", entry.OperationId);
        command.Parameters.AddWithValue("$label", entry.Label);
        command.Parameters.AddWithValue("$inverse", entry.InversePayload);
        command.Parameters.AddWithValue("$expected", entry.ExpectedVersions);
        command.Parameters.AddWithValue("$applied", StorageFormats.Instant(entry.AppliedAtUtc));
        command.Parameters.AddWithValue("$undone", SqliteProfileStore.OptionalInstant(entry.UndoneAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<UndoEntryRecord?> FindByOperationAsync(string profileId, string operationId, CancellationToken ct) =>
        (await ReadAsync("SELECT * FROM UndoEntries WHERE ProfileId = $profile AND OperationId = $operation;", ct, ("$profile", profileId), ("$operation", operationId))).FirstOrDefault();

    public async Task<IReadOnlyList<UndoEntryRecord>> ListOpenAsync(string profileId, int limit, CancellationToken ct) =>
        await ReadAsync(
            "SELECT * FROM UndoEntries WHERE ProfileId = $profile AND UndoneAtUtc IS NULL ORDER BY AppliedAtUtc DESC, rowid DESC LIMIT $limit;",
            ct, ("$profile", profileId), ("$limit", limit));

    public async Task<bool> MarkUndoneAsync(string entryId, DateTimeOffset undoneAtUtc, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE UndoEntries SET UndoneAtUtc = $undone WHERE Id = $id AND UndoneAtUtc IS NULL;";
        command.Parameters.AddWithValue("$undone", StorageFormats.Instant(undoneAtUtc));
        command.Parameters.AddWithValue("$id", entryId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task PruneAsync(string profileId, int keep, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM UndoEntries WHERE ProfileId = $profile AND rowid NOT IN (
                SELECT rowid FROM UndoEntries WHERE ProfileId = $profile ORDER BY AppliedAtUtc DESC, rowid DESC LIMIT $keep);
            """;
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$keep", keep);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ReplacePayloadAsync(string entryId, string inversePayload, string expectedVersions, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE UndoEntries SET InversePayload = $inverse, ExpectedVersions = $expected WHERE Id = $id AND UndoneAtUtc IS NULL;";
        command.Parameters.AddWithValue("$inverse", inversePayload);
        command.Parameters.AddWithValue("$expected", expectedVersions);
        command.Parameters.AddWithValue("$id", entryId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<UndoEntryRecord>> ReadAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<UndoEntryRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new UndoEntryRecord
            {
                Id = reader.S("Id"),
                ProfileId = reader.S("ProfileId"),
                OperationId = reader.S("OperationId"),
                Label = reader.S("Label"),
                InversePayload = reader.S("InversePayload"),
                ExpectedVersions = reader.S("ExpectedVersions"),
                AppliedAtUtc = reader.I("AppliedAtUtc"),
                UndoneAtUtc = reader.In("UndoneAtUtc"),
            });
        }

        return rows;
    }
}

public sealed class SqliteChecklistConversionStore(SqliteConnection connection) : IChecklistConversionStore
{
    public async Task<ChecklistConversionRecord?> FindAsync(string taskId, string itemKey, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ChecklistConversions WHERE TaskId = $task AND ItemKey = $key;";
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$key", itemKey);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ChecklistConversionRecord(reader.S("TaskId"), reader.S("ItemKey"), reader.S("SubtaskId"), reader.S("OperationId"), reader.I("CreatedAtUtc"))
            : null;
    }

    public async Task UpsertAsync(ChecklistConversionRecord marker, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ChecklistConversions (TaskId, ItemKey, SubtaskId, OperationId, CreatedAtUtc)
            VALUES ($task, $key, $subtask, $operation, $created)
            ON CONFLICT (TaskId, ItemKey) DO UPDATE SET SubtaskId = excluded.SubtaskId, OperationId = excluded.OperationId, CreatedAtUtc = excluded.CreatedAtUtc;
            """;
        command.Parameters.AddWithValue("$task", marker.TaskId);
        command.Parameters.AddWithValue("$key", marker.ItemKey);
        command.Parameters.AddWithValue("$subtask", marker.SubtaskId);
        command.Parameters.AddWithValue("$operation", marker.OperationId);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(marker.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }
}
