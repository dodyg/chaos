using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

public sealed class SqliteTagStore(SqliteConnection connection) : ITagStore
{
    private const string ActiveOrder = "ORDER BY Position, NormalizedName, Id";

    public async Task<TagRecord?> GetAsync(string tagId, CancellationToken ct) =>
        await SingleAsync("SELECT * FROM Tags WHERE Id = $id AND DeletedAtUtc IS NULL;", ct, ("$id", tagId));

    public async Task<TagRecord?> GetIncludingDeletedAsync(string tagId, CancellationToken ct) =>
        await SingleAsync("SELECT * FROM Tags WHERE Id = $id;", ct, ("$id", tagId));

    public async Task<IReadOnlyList<TagRecord>> ListByOwnerAsync(string ownerProfileId, CancellationToken ct) =>
        await ListAsync($"SELECT * FROM Tags WHERE OwnerProfileId = $owner AND DeletedAtUtc IS NULL {ActiveOrder};", ct, ("$owner", ownerProfileId));

    public async Task<IReadOnlyList<TagRecord>> ListDeletedByOwnerAsync(string ownerProfileId, CancellationToken ct) =>
        await ListAsync("SELECT * FROM Tags WHERE OwnerProfileId = $owner AND DeletedAtUtc IS NOT NULL ORDER BY DeletedAtUtc DESC, Id;", ct, ("$owner", ownerProfileId));

    public async Task<TagRecord?> FindChildAsync(string ownerProfileId, string? parentTagId, string normalizedName, CancellationToken ct) =>
        await SingleAsync(
            "SELECT * FROM Tags WHERE OwnerProfileId = $owner AND COALESCE(ParentTagId, '') = $parent AND NormalizedName = $name AND DeletedAtUtc IS NULL;",
            ct, ("$owner", ownerProfileId), ("$parent", parentTagId ?? string.Empty), ("$name", normalizedName));

    public async Task<IReadOnlyList<TagRecord>> ListChildrenAsync(string tagId, CancellationToken ct) =>
        await ListAsync($"SELECT * FROM Tags WHERE ParentTagId = $parent AND DeletedAtUtc IS NULL {ActiveOrder};", ct, ("$parent", tagId));

    public async Task AddAsync(TagRecord tag, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Tags (Id, OwnerProfileId, DisplayName, NormalizedName, ParentTagId, ColorKey, Position,
                Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $owner, $display, $normalized, $parent, $color, $position,
                $version, $created, $updated, $deleted);
            """;
        Bind(command, tag);
        command.Parameters.AddWithValue("$version", tag.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(tag.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<TagRecord?> UpdateAsync(TagRecord updated, long expectedVersion, CancellationToken ct)
    {
        // Optimistic concurrency (PLAN §5); no tombstone filter so restore can rewrite a deleted row.
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Tags SET DisplayName = $display, NormalizedName = $normalized, ParentTagId = $parent,
                ColorKey = $color, Position = $position, Version = $version, UpdatedAtUtc = $updated, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expected;
            """;
        Bind(command, updated);
        command.Parameters.AddWithValue("$version", updated.Version);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? await GetIncludingDeletedAsync(updated.Id, ct) : null;
    }

    public async Task<long> GetNextPositionAsync(string ownerProfileId, string? parentTagId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(MAX(Position), -1024) + 1024 FROM Tags
            WHERE OwnerProfileId = $owner AND COALESCE(ParentTagId, '') = $parent AND DeletedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$owner", ownerProfileId);
        command.Parameters.AddWithValue("$parent", parentTagId ?? string.Empty);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    public async Task<IReadOnlyList<string>> ListTaskTagIdsAsync(string taskId, CancellationToken ct) =>
        await IdsAsync("""
            SELECT l.TagId FROM TaskTags l JOIN Tags t ON t.Id = l.TagId
            WHERE l.TaskId = $id AND t.DeletedAtUtc IS NULL ORDER BY t.NormalizedName, t.Id;
            """, taskId, ct);

    public async Task<IReadOnlyList<string>> ListSubtaskTagIdsAsync(string subtaskId, CancellationToken ct) =>
        await IdsAsync("""
            SELECT l.TagId FROM SubtaskTags l JOIN Tags t ON t.Id = l.TagId
            WHERE l.SubtaskId = $id AND t.DeletedAtUtc IS NULL ORDER BY t.NormalizedName, t.Id;
            """, subtaskId, ct);

    public async Task<IReadOnlyList<string>> ListTaskIdsAsync(string tagId, CancellationToken ct) =>
        await IdsAsync("SELECT TaskId FROM TaskTags WHERE TagId = $id ORDER BY TaskId;", tagId, ct);

    public async Task<IReadOnlyList<string>> ListSubtaskIdsAsync(string tagId, CancellationToken ct) =>
        await IdsAsync("SELECT SubtaskId FROM SubtaskTags WHERE TagId = $id ORDER BY SubtaskId;", tagId, ct);

    public async Task<bool> AddTaskLinkAsync(string taskId, string tagId, CancellationToken ct) =>
        await LinkAsync("INSERT OR IGNORE INTO TaskTags (TaskId, TagId) VALUES ($target, $tag);", taskId, tagId, ct);

    public async Task<bool> RemoveTaskLinkAsync(string taskId, string tagId, CancellationToken ct) =>
        await LinkAsync("DELETE FROM TaskTags WHERE TaskId = $target AND TagId = $tag;", taskId, tagId, ct);

    public async Task<bool> AddSubtaskLinkAsync(string subtaskId, string tagId, CancellationToken ct) =>
        await LinkAsync("INSERT OR IGNORE INTO SubtaskTags (SubtaskId, TagId) VALUES ($target, $tag);", subtaskId, tagId, ct);

    public async Task<bool> RemoveSubtaskLinkAsync(string subtaskId, string tagId, CancellationToken ct) =>
        await LinkAsync("DELETE FROM SubtaskTags WHERE SubtaskId = $target AND TagId = $tag;", subtaskId, tagId, ct);

    private async Task<bool> LinkAsync(string sql, string targetId, string tagId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$target", targetId);
        command.Parameters.AddWithValue("$tag", tagId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private async Task<IReadOnlyList<string>> IdsAsync(string sql, string id, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private async Task<TagRecord?> SingleAsync(string sql, CancellationToken ct, params (string Name, string Value)[] parameters) =>
        (await ListAsync(sql, ct, parameters)).FirstOrDefault();

    private async Task<IReadOnlyList<TagRecord>> ListAsync(string sql, CancellationToken ct, params (string Name, string Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<TagRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadTag(reader));
        }

        return rows;
    }

    private static void Bind(SqliteCommand command, TagRecord tag)
    {
        command.Parameters.AddWithValue("$id", tag.Id);
        command.Parameters.AddWithValue("$owner", tag.OwnerProfileId);
        command.Parameters.AddWithValue("$display", tag.DisplayName);
        command.Parameters.AddWithValue("$normalized", tag.NormalizedName);
        command.Parameters.AddWithValue("$parent", SqliteProfileStore.OptionalValue(tag.ParentTagId));
        command.Parameters.AddWithValue("$color", SqliteProfileStore.OptionalValue(tag.ColorKey));
        command.Parameters.AddWithValue("$position", tag.Position);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(tag.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(tag.DeletedAtUtc));
    }

    internal static TagRecord ReadTag(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        OwnerProfileId = r.S("OwnerProfileId"),
        DisplayName = r.S("DisplayName"),
        NormalizedName = r.S("NormalizedName"),
        ParentTagId = r.Sn("ParentTagId"),
        ColorKey = r.Sn("ColorKey"),
        Position = r.L("Position"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };
}
