using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>Saved views and their device-local alert baseline (PLAN2 P3, migration 026).</summary>
public sealed class SqliteSavedViewStore(SqliteConnection connection, Chaos.Application.Activity.IActivityRecorder? activity = null) : ISavedViewStore
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
        activity?.Note("saved_view_match", viewId, Chaos.Application.Activity.ActivityOp.Upsert);
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

    public async Task ApplyMatchChangesAsync(string viewId, IReadOnlyList<SavedViewMatchRecord> upserts, IReadOnlyList<string> removedTaskIds, CancellationToken ct)
    {
        activity?.Note("saved_view_match", viewId, Chaos.Application.Activity.ActivityOp.Upsert);
        foreach (var match in upserts)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO SavedViewMatches (ViewId, TaskId, FirstSeenUtc, LeftAtUtc) VALUES ($view, $task, $seen, $left)
                ON CONFLICT (ViewId, TaskId) DO UPDATE SET FirstSeenUtc = excluded.FirstSeenUtc, LeftAtUtc = excluded.LeftAtUtc;
                """;
            command.Parameters.AddWithValue("$view", viewId);
            command.Parameters.AddWithValue("$task", match.TaskId);
            command.Parameters.AddWithValue("$seen", StorageFormats.Instant(match.FirstSeenUtc));
            command.Parameters.AddWithValue("$left", StorageFormats.Optional(match.LeftAtUtc) ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }

        foreach (var taskId in removedTaskIds)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM SavedViewMatches WHERE ViewId = $view AND TaskId = $task;";
            command.Parameters.AddWithValue("$view", viewId);
            command.Parameters.AddWithValue("$task", taskId);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task AddAlertAsync(ViewAlertRecord alert, CancellationToken ct)
    {
        activity?.Note("view_alert", alert.Id, Chaos.Application.Activity.ActivityOp.Upsert);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ViewAlerts (Id, ViewId, TaskCount, TaskIdsJson, FirstTaskTitle, DeliverAtUtc, State, DeliveredAtUtc, AcknowledgedAtUtc, Presentation, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, $view, $count, $ids, $title, $deliver, $state, $delivered, $acknowledged, $presentation, $created, $updated);
            """;
        command.Parameters.AddWithValue("$id", alert.Id);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(alert.CreatedAtUtc));
        BindAlert(command, alert);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ViewAlertRecord?> GetAlertAsync(string id, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ViewAlerts WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAlert(reader) : null;
    }

    public async Task<ViewAlertRecord?> FindPendingAlertAsync(string viewId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ViewAlerts WHERE ViewId = $view AND State = 0;";
        command.Parameters.AddWithValue("$view", viewId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAlert(reader) : null;
    }

    public async Task<bool> UpdateAlertAsync(ViewAlertRecord alert, ViewAlertState expected, CancellationToken ct)
    {
        activity?.Note("view_alert", alert.Id, Chaos.Application.Activity.ActivityOp.Upsert);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ViewAlerts SET TaskCount = $count, TaskIdsJson = $ids, FirstTaskTitle = $title, DeliverAtUtc = $deliver, State = $state,
                DeliveredAtUtc = $delivered, AcknowledgedAtUtc = $acknowledged, Presentation = $presentation, UpdatedAtUtc = $updated
            WHERE Id = $id AND State = $expected;
            """;
        command.Parameters.AddWithValue("$id", alert.Id);
        command.Parameters.AddWithValue("$expected", (int)expected);
        BindAlert(command, alert);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<ViewAlertRecord>> ListDueAlertsAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ViewAlerts WHERE State = 0 AND DeliverAtUtc <= $now ORDER BY DeliverAtUtc, Id LIMIT $limit;";
        command.Parameters.AddWithValue("$now", StorageFormats.Instant(now));
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadAlertsAsync(command, ct);
    }

    public async Task<IReadOnlyList<ViewAlertRecord>> ListDeliveredAlertsAsync(int limit, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ViewAlerts WHERE State = 1 ORDER BY DeliveredAtUtc DESC, Id LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadAlertsAsync(command, ct);
    }

    private static async Task<IReadOnlyList<ViewAlertRecord>> ReadAlertsAsync(SqliteCommand command, CancellationToken ct)
    {
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<ViewAlertRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadAlert(reader));
        }

        return rows;
    }

    private static void BindAlert(SqliteCommand command, ViewAlertRecord alert)
    {
        command.Parameters.AddWithValue("$view", alert.ViewId);
        command.Parameters.AddWithValue("$count", alert.TaskCount);
        command.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(alert.TaskIds));
        command.Parameters.AddWithValue("$title", alert.FirstTaskTitle);
        command.Parameters.AddWithValue("$deliver", StorageFormats.Instant(alert.DeliverAtUtc));
        command.Parameters.AddWithValue("$state", (int)alert.State);
        command.Parameters.AddWithValue("$delivered", StorageFormats.Optional(alert.DeliveredAtUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$acknowledged", StorageFormats.Optional(alert.AcknowledgedAtUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$presentation", (int)alert.Presentation);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(alert.UpdatedAtUtc));
    }

    private static ViewAlertRecord ReadAlert(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        ViewId = r.S("ViewId"),
        TaskCount = (int)r.L("TaskCount"),
        TaskIds = System.Text.Json.JsonSerializer.Deserialize<string[]>(r.S("TaskIdsJson")) ?? [],
        FirstTaskTitle = r.S("FirstTaskTitle"),
        DeliverAtUtc = r.I("DeliverAtUtc"),
        State = (ViewAlertState)(int)r.L("State"),
        DeliveredAtUtc = r.In("DeliveredAtUtc"),
        AcknowledgedAtUtc = r.In("AcknowledgedAtUtc"),
        Presentation = (NotificationPresentation)(int)r.L("Presentation"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
    };

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
