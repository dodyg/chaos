using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>Saved views and their device-local alert baseline (PLAN2 P3, migration 026).</summary>
public sealed class SqliteSavedViewStore(SqliteConnection connection) : ISavedViewStore
{
    public async Task<SavedViewRecord?> GetAsync(string id, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM SavedViews WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<SavedViewRecord>> ListAsync(string spaceId, bool includeDeleted, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT * FROM SavedViews WHERE SpaceId = $space{(includeDeleted ? string.Empty : " AND DeletedAtUtc IS NULL")}
            ORDER BY Position, Id;
            """;
        command.Parameters.AddWithValue("$space", spaceId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<SavedViewRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    public async Task AddAsync(SavedViewRecord view, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SavedViews (Id, SpaceId, Name, Icon, Color, Pinned, Position, DefinitionJson, LayoutJson, AlertsEnabled, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $space, $name, $icon, $color, $pinned, $position, $definition, $layout, $alerts, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", view.Id);
        command.Parameters.AddWithValue("$space", view.SpaceId);
        Bind(command, view);
        command.Parameters.AddWithValue("$version", view.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(view.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<SavedViewRecord?> UpdateAsync(SavedViewRecord updated, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SavedViews SET Name = $name, Icon = $icon, Color = $color, Pinned = $pinned, Position = $position,
                DefinitionJson = $definition, LayoutJson = $layout, AlertsEnabled = $alerts, Version = $version,
                UpdatedAtUtc = $updated, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expected;
            """;
        command.Parameters.AddWithValue("$id", updated.Id);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        command.Parameters.AddWithValue("$version", updated.Version);
        Bind(command, updated);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            return null;
        }

        return await GetAsync(updated.Id, ct);
    }

    public async Task<IReadOnlyList<SavedViewMatchRecord>> ListMatchesAsync(string viewId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ViewId, TaskId, FirstSeenUtc, LeftAtUtc FROM SavedViewMatches WHERE ViewId = $view ORDER BY TaskId;";
        command.Parameters.AddWithValue("$view", viewId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<SavedViewMatchRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new SavedViewMatchRecord(reader.S("ViewId"), reader.S("TaskId"), reader.I("FirstSeenUtc"), reader.In("LeftAtUtc")));
        }

        return rows;
    }

    public async Task ReplaceMatchesAsync(string viewId, IReadOnlyList<SavedViewMatchRecord> matches, CancellationToken ct)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM SavedViewMatches WHERE ViewId = $view;";
            delete.Parameters.AddWithValue("$view", viewId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        foreach (var match in matches)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO SavedViewMatches (ViewId, TaskId, FirstSeenUtc, LeftAtUtc) VALUES ($view, $task, $seen, $left);";
            insert.Parameters.AddWithValue("$view", viewId);
            insert.Parameters.AddWithValue("$task", match.TaskId);
            insert.Parameters.AddWithValue("$seen", StorageFormats.Instant(match.FirstSeenUtc));
            insert.Parameters.AddWithValue("$left", StorageFormats.Optional(match.LeftAtUtc) ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static void Bind(SqliteCommand command, SavedViewRecord view)
    {
        command.Parameters.AddWithValue("$name", view.Name);
        command.Parameters.AddWithValue("$icon", view.Icon);
        command.Parameters.AddWithValue("$color", (object?)view.Color ?? DBNull.Value);
        command.Parameters.AddWithValue("$pinned", view.Pinned ? 1 : 0);
        command.Parameters.AddWithValue("$position", view.Position);
        command.Parameters.AddWithValue("$definition", view.DefinitionJson);
        command.Parameters.AddWithValue("$layout", view.LayoutJson);
        command.Parameters.AddWithValue("$alerts", view.AlertsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(view.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", StorageFormats.Optional(view.DeletedAtUtc) ?? DBNull.Value);
    }

    private static SavedViewRecord Read(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        SpaceId = r.S("SpaceId"),
        Name = r.S("Name"),
        Icon = r.S("Icon"),
        Color = r.Sn("Color"),
        Pinned = r.B("Pinned"),
        Position = (int)r.L("Position"),
        DefinitionJson = r.S("DefinitionJson"),
        LayoutJson = r.S("LayoutJson"),
        AlertsEnabled = r.B("AlertsEnabled"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };
}
