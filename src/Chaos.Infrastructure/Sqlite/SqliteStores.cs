using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>SQLite implementations of the typed stores bound to one session connection (PLAN §4).</summary>
public sealed class SqliteDataSession(SqliteConnection connection) : IDataSession
{
    public IProfileStore Profiles { get; } = new SqliteProfileStore(connection);
    public ISpaceStore Spaces { get; } = new SqliteSpaceStore(connection);
    public IContainerStore Containers { get; } = new SqliteContainerStore(connection);
    public ISectionStore Sections { get; } = new SqliteSectionStore(connection);
    public ITaskStore Tasks { get; } = new SqliteTaskStore(connection);
    public ISubtaskStore Subtasks { get; } = new SqliteSubtaskStore(connection);
    public ITemplateStore Templates { get; } = new SqliteTemplateStore(connection);
    public ITagStore Tags { get; } = new SqliteTagStore(connection);
    public ITaskQueryStore Queries { get; } = new Chaos.Infrastructure.Search.SqliteTaskQueryStore(connection);
    public ITaskHistoryStore History { get; } = new SqliteTaskHistoryStore(connection);
    public IDiaryStore Diary { get; } = new SqliteDiaryStore(connection);
    public IUndoStore Undo { get; } = new SqliteUndoStore(connection);
    public IChecklistConversionStore ChecklistConversions { get; } = new SqliteChecklistConversionStore(connection);
    public IPadStore Pad { get; } = new SqlitePadStore(connection);
    public IOperationReceiptStore Receipts { get; } = new SqliteOperationReceiptStore(connection);
    public IBackupCatalogStore Backups { get; } = new SqliteBackupCatalogStore(connection);
}

public sealed class SqliteProfileStore(SqliteConnection connection) : IProfileStore
{
    public async Task<ProfileRecord?> GetAsync(string profileId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Profiles WHERE Id = $id AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$id", profileId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadProfile(reader) : null;
    }

    public async Task<ProfileRecord?> GetPrimaryAsync(CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Profiles WHERE DeletedAtUtc IS NULL ORDER BY CreatedAtUtc LIMIT 1;";
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadProfile(reader) : null;
    }

    public async Task AddAsync(ProfileRecord profile, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Profiles (Id, DisplayName, ZoneId, Locale, DateFormat, FirstWeekday,
                PlanningTime, SummaryTime, DefaultReminderTime, QuietHoursEnabled, QuietStart, QuietEnd,
                Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $name, $zone, $locale, $dateFormat, $firstWeekday,
                $planningTime, $summaryTime, $defaultReminderTime, $quietEnabled, $quietStart, $quietEnd,
                $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", profile.Id);
        command.Parameters.AddWithValue("$name", profile.DisplayName);
        command.Parameters.AddWithValue("$zone", profile.ZoneId);
        command.Parameters.AddWithValue("$locale", profile.Locale);
        command.Parameters.AddWithValue("$dateFormat", profile.DateFormat);
        command.Parameters.AddWithValue("$firstWeekday", (long)profile.FirstWeekday);
        command.Parameters.AddWithValue("$planningTime", Optional(profile.PlanningTime));
        command.Parameters.AddWithValue("$summaryTime", Optional(profile.SummaryTime));
        command.Parameters.AddWithValue("$defaultReminderTime", StorageFormats.Time(profile.DefaultReminderTime));
        command.Parameters.AddWithValue("$quietEnabled", profile.QuietHoursEnabled ? 1L : 0L);
        command.Parameters.AddWithValue("$quietStart", Optional(profile.QuietStart));
        command.Parameters.AddWithValue("$quietEnd", Optional(profile.QuietEnd));
        command.Parameters.AddWithValue("$version", profile.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(profile.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(profile.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", OptionalInstant(profile.DeletedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static ProfileRecord ReadProfile(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        DisplayName = r.S("DisplayName"),
        ZoneId = r.S("ZoneId"),
        Locale = r.S("Locale"),
        DateFormat = r.S("DateFormat"),
        FirstWeekday = (DayOfWeek)(int)r.L("FirstWeekday"),
        PlanningTime = r.Tn("PlanningTime"),
        SummaryTime = r.Tn("SummaryTime"),
        DefaultReminderTime = StorageFormats.ParseTime(r.S("DefaultReminderTime"))!.Value,
        QuietHoursEnabled = r.B("QuietHoursEnabled"),
        QuietStart = r.Tn("QuietStart"),
        QuietEnd = r.Tn("QuietEnd"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };

    internal static object Optional(TimeOnly? time) => time is null ? DBNull.Value : StorageFormats.Time(time.Value);

    internal static object OptionalValue(string? value) => value is null ? DBNull.Value : value;

    internal static object OptionalInstant(DateTimeOffset? instant) => instant is null ? DBNull.Value : StorageFormats.Instant(instant.Value);

    internal static object OptionalDate(DateOnly? date) => date is null ? DBNull.Value : StorageFormats.Date(date.Value);
}

public sealed class SqliteSpaceStore(SqliteConnection connection) : ISpaceStore
{
    public async Task<SpaceRecord?> GetAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Spaces WHERE Id = $id AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$id", spaceId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSpace(reader) : null;
    }

    public async Task<SpaceRecord?> GetPersonalByOwnerAsync(string ownerProfileId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Spaces WHERE OwnerProfileId = $owner AND Kind = 1 AND DeletedAtUtc IS NULL LIMIT 1;";
        command.Parameters.AddWithValue("$owner", ownerProfileId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSpace(reader) : null;
    }

    public async Task AddAsync(SpaceRecord space, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Spaces (Id, OwnerProfileId, Kind, Name, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $owner, $kind, $name, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", space.Id);
        command.Parameters.AddWithValue("$owner", space.OwnerProfileId);
        command.Parameters.AddWithValue("$kind", (long)space.Kind);
        command.Parameters.AddWithValue("$name", space.Name);
        command.Parameters.AddWithValue("$version", space.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(space.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(space.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(space.DeletedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static SpaceRecord ReadSpace(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        OwnerProfileId = r.S("OwnerProfileId"),
        Kind = (SpaceKind)(int)r.L("Kind"),
        Name = r.S("Name"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };
}

public sealed class SqliteContainerStore(SqliteConnection connection) : IContainerStore
{
    public async Task<ContainerRecord?> GetAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Containers WHERE Id = $id AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$id", containerId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadContainer(reader) : null;
    }

    public async Task<ContainerRecord?> GetInboxAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Containers WHERE SpaceId = $space AND IsInbox = 1 AND DeletedAtUtc IS NULL LIMIT 1;";
        command.Parameters.AddWithValue("$space", spaceId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadContainer(reader) : null;
    }

    public async Task<IReadOnlyList<ContainerRecord>> ListBySpaceAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Containers WHERE SpaceId = $space AND DeletedAtUtc IS NULL ORDER BY Position, Id;";
        command.Parameters.AddWithValue("$space", spaceId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var containers = new List<ContainerRecord>();
        while (await reader.ReadAsync(ct))
        {
            containers.Add(ReadContainer(reader));
        }

        return containers;
    }

    public async Task<long> GetNextPositionAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Position), -1024) + 1024 FROM Containers WHERE SpaceId = $space AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$space", spaceId);
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value);
    }

    public async Task<long> CountActiveTasksAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Tasks WHERE ContainerId = $container AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$container", containerId);
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value);
    }

    public async Task<ContainerRecord?> UpdateAsync(ContainerRecord updated, long expectedVersion, CancellationToken ct)
    {
        // Optimistic concurrency (PLAN §5): the version check alone decides; a tombstoned
        // row stays addressable so restore-style flows can rewrite it.
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Containers SET
                Name = $name, NormalizedName = $normalizedName, Position = $position,
                DeadlineDate = $deadline, ArchivedAtUtc = $archived,
                Version = $newVersion, UpdatedAtUtc = $updated_at, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expectedVersion;
            """;
        command.Parameters.AddWithValue("$name", updated.Name);
        command.Parameters.AddWithValue("$normalizedName", NormalizeName(updated.Name));
        command.Parameters.AddWithValue("$position", updated.Position);
        command.Parameters.AddWithValue("$deadline", SqliteProfileStore.OptionalDate(updated.DeadlineDate));
        command.Parameters.AddWithValue("$archived", SqliteProfileStore.OptionalInstant(updated.ArchivedAtUtc));
        command.Parameters.AddWithValue("$newVersion", updated.Version);
        command.Parameters.AddWithValue("$updated_at", StorageFormats.Instant(updated.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(updated.DeletedAtUtc));
        command.Parameters.AddWithValue("$id", updated.Id);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);

        var rows = await command.ExecuteNonQueryAsync(ct);
        return rows == 1 ? await GetIncludingDeletedAsync(updated.Id, ct) : null;
    }

    public async Task<ContainerRecord?> GetIncludingDeletedAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Containers WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", containerId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadContainer(reader) : null;
    }

    public async Task<IReadOnlyList<ContainerRecord>> ListDeletedBySpaceAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Containers WHERE SpaceId = $space AND DeletedAtUtc IS NOT NULL ORDER BY DeletedAtUtc DESC, Id;";
        command.Parameters.AddWithValue("$space", spaceId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<ContainerRecord>();
        while (await reader.ReadAsync(ct)) rows.Add(ReadContainer(reader));
        return rows;
    }

    public async Task AddAsync(ContainerRecord container, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Containers (Id, SpaceId, Kind, Name, NormalizedName, IsInbox, Position,
                DeadlineDate, ArchivedAtUtc, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $space, $kind, $name, $normalizedName, $isInbox, $position,
                $deadline, $archived, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", container.Id);
        command.Parameters.AddWithValue("$space", container.SpaceId);
        command.Parameters.AddWithValue("$kind", (long)container.Kind);
        command.Parameters.AddWithValue("$name", container.Name);
        command.Parameters.AddWithValue("$normalizedName", NormalizeName(container.Name));
        command.Parameters.AddWithValue("$isInbox", container.IsInbox ? 1L : 0L);
        command.Parameters.AddWithValue("$position", container.Position);
        command.Parameters.AddWithValue("$deadline", SqliteProfileStore.OptionalDate(container.DeadlineDate));
        command.Parameters.AddWithValue("$archived", SqliteProfileStore.OptionalInstant(container.ArchivedAtUtc));
        command.Parameters.AddWithValue("$version", container.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(container.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(container.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(container.DeletedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static string NormalizeName(string name) => name.Trim().ToLowerInvariant();

    internal static ContainerRecord ReadContainer(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        SpaceId = r.S("SpaceId"),
        Kind = (ContainerKind)(int)r.L("Kind"),
        Name = r.S("Name"),
        IsInbox = r.B("IsInbox"),
        Position = r.L("Position"),
        DeadlineDate = r.Dn("DeadlineDate"),
        ArchivedAtUtc = r.In("ArchivedAtUtc"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };
}

public sealed class SqliteTaskStore(SqliteConnection connection) : ITaskStore
{
    public async Task<IReadOnlyList<TaskRecord>> ListDeletedByContainerAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Tasks WHERE ContainerId = $container AND DeletedAtUtc IS NOT NULL ORDER BY DeletedAtUtc DESC, Id;";
        command.Parameters.AddWithValue("$container", containerId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<TaskRecord>();
        while (await reader.ReadAsync(ct)) results.Add(ReadTask(reader));
        return results;
    }
    public async Task<TaskRecord?> GetAsync(string taskId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Tasks WHERE Id = $id AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$id", taskId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTask(reader) : null;
    }

    public async Task<TaskRecord?> GetIncludingDeletedAsync(string taskId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Tasks WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", taskId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTask(reader) : null;
    }

    public async Task<IReadOnlyList<TaskRecord>> ListByContainerAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Tasks WHERE ContainerId = $container AND DeletedAtUtc IS NULL ORDER BY Position, Id;";
        command.Parameters.AddWithValue("$container", containerId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var tasks = new List<TaskRecord>();
        while (await reader.ReadAsync(ct))
        {
            tasks.Add(ReadTask(reader));
        }

        return tasks;
    }

    public async Task AddAsync(TaskRecord task, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Tasks (Id, SpaceId, ContainerId, SectionId, Title, Notes, Status, Priority,
                EstimateMinutes, CapacityCategory, PlannedDate, PlannedTime, PlannedZoneId,
                DueDate, DueTime, DueZoneId, CompletedAtUtc, CancelledAtUtc, Position,
                RecurrenceOccurrenceKey, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $space, $container, $section, $title, $notes, $status, $priority,
                $estimate, $category, $plannedDate, $plannedTime, $plannedZone,
                $dueDate, $dueTime, $dueZone, $completed, $cancelled, $position,
                $occurrenceKey, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$space", task.SpaceId);
        command.Parameters.AddWithValue("$container", task.ContainerId);
        command.Parameters.AddWithValue("$section", SqliteProfileStore.OptionalValue(task.SectionId));
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$notes", task.Notes);
        command.Parameters.AddWithValue("$status", (long)task.Status);
        command.Parameters.AddWithValue("$priority", (long)task.Priority);
        command.Parameters.AddWithValue("$estimate", task.EstimateMinutes is null ? System.DBNull.Value : (object)(long)task.EstimateMinutes.Value);
        command.Parameters.AddWithValue("$category", (long)task.CapacityCategory);
        command.Parameters.AddWithValue("$plannedDate", SqliteProfileStore.OptionalDate(task.PlannedDate));
        command.Parameters.AddWithValue("$plannedTime", SqliteProfileStore.Optional(task.PlannedTime));
        command.Parameters.AddWithValue("$plannedZone", SqliteProfileStore.OptionalValue(task.PlannedZoneId));
        command.Parameters.AddWithValue("$dueDate", SqliteProfileStore.OptionalDate(task.DueDate));
        command.Parameters.AddWithValue("$dueTime", SqliteProfileStore.Optional(task.DueTime));
        command.Parameters.AddWithValue("$dueZone", SqliteProfileStore.OptionalValue(task.DueZoneId));
        command.Parameters.AddWithValue("$completed", SqliteProfileStore.OptionalInstant(task.CompletedAtUtc));
        command.Parameters.AddWithValue("$cancelled", SqliteProfileStore.OptionalInstant(task.CancelledAtUtc));
        command.Parameters.AddWithValue("$position", task.Position);
        command.Parameters.AddWithValue("$occurrenceKey", SqliteProfileStore.OptionalValue(task.RecurrenceOccurrenceKey));
        command.Parameters.AddWithValue("$version", task.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(task.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(task.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(task.DeletedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
        await IndexAsync(task, ct);
    }

    /// <summary>Keeps the task's search document in step with the row, in the same transaction (PLAN §5).</summary>
    private async Task IndexAsync(TaskRecord task, CancellationToken ct) =>
        await Search.SearchIndexWriter.SyncAsync(connection, Search.SearchTargetKind.Task, task.Id, task.Id, task.Title, task.Notes, task.DeletedAtUtc is null, ct);

    public async Task<TaskRecord?> UpdateAsync(TaskRecord updated, long expectedVersion, CancellationToken ct)
    {
        // Optimistic concurrency (PLAN §5): UPDATE ... WHERE Id = @id AND Version = @expected must affect one row.
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Tasks SET
                ContainerId = $container, SectionId = $section, Position = $position, Title = $title, Notes = $notes, Status = $status, Priority = $priority,
                EstimateMinutes = $estimate, CapacityCategory = $category,
                PlannedDate = $plannedDate, PlannedTime = $plannedTime, PlannedZoneId = $plannedZone,
                DueDate = $dueDate, DueTime = $dueTime, DueZoneId = $dueZone,
                CompletedAtUtc = $completed, CancelledAtUtc = $cancelled,
                RecurrenceOccurrenceKey = $occurrenceKey,
                Version = $newVersion, UpdatedAtUtc = $updated_at, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expectedVersion;
            """;
        command.Parameters.AddWithValue("$container", updated.ContainerId);
        command.Parameters.AddWithValue("$section", SqliteProfileStore.OptionalValue(updated.SectionId));
        command.Parameters.AddWithValue("$position", updated.Position);
        command.Parameters.AddWithValue("$title", updated.Title);
        command.Parameters.AddWithValue("$notes", updated.Notes);
        command.Parameters.AddWithValue("$status", (long)updated.Status);
        command.Parameters.AddWithValue("$priority", (long)updated.Priority);
        command.Parameters.AddWithValue("$estimate", updated.EstimateMinutes is null ? System.DBNull.Value : (object)(long)updated.EstimateMinutes.Value);
        command.Parameters.AddWithValue("$category", (long)updated.CapacityCategory);
        command.Parameters.AddWithValue("$plannedDate", SqliteProfileStore.OptionalDate(updated.PlannedDate));
        command.Parameters.AddWithValue("$plannedTime", SqliteProfileStore.Optional(updated.PlannedTime));
        command.Parameters.AddWithValue("$plannedZone", SqliteProfileStore.OptionalValue(updated.PlannedZoneId));
        command.Parameters.AddWithValue("$dueDate", SqliteProfileStore.OptionalDate(updated.DueDate));
        command.Parameters.AddWithValue("$dueTime", SqliteProfileStore.Optional(updated.DueTime));
        command.Parameters.AddWithValue("$dueZone", SqliteProfileStore.OptionalValue(updated.DueZoneId));
        command.Parameters.AddWithValue("$completed", SqliteProfileStore.OptionalInstant(updated.CompletedAtUtc));
        command.Parameters.AddWithValue("$cancelled", SqliteProfileStore.OptionalInstant(updated.CancelledAtUtc));
        command.Parameters.AddWithValue("$occurrenceKey", SqliteProfileStore.OptionalValue(updated.RecurrenceOccurrenceKey));
        command.Parameters.AddWithValue("$newVersion", updated.Version);
        command.Parameters.AddWithValue("$updated_at", StorageFormats.Instant(updated.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(updated.DeletedAtUtc));
        command.Parameters.AddWithValue("$id", updated.Id);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);

        var rows = await command.ExecuteNonQueryAsync(ct);
        if (rows != 1)
        {
            return null;
        }

        await IndexAsync(updated, ct);
        return await GetIncludingDeletedAsync(updated.Id, ct);
    }

    public async Task<long> GetNextPositionAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Position), -1024) + 1024 FROM Tasks WHERE ContainerId = $container AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$container", containerId);
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value);
    }

    internal static TaskRecord ReadTask(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        SpaceId = r.S("SpaceId"),
        ContainerId = r.S("ContainerId"),
        SectionId = r.Sn("SectionId"),
        Title = r.S("Title"),
        Notes = r.S("Notes"),
        Status = (TaskItemStatus)(int)r.L("Status"),
        Priority = (TaskPriority)(int)r.L("Priority"),
        EstimateMinutes = r.Ln("EstimateMinutes") is { } minutes ? (int)minutes : null,
        CapacityCategory = (CapacityCategory)(int)r.L("CapacityCategory"),
        PlannedDate = r.Dn("PlannedDate"),
        PlannedTime = r.Tn("PlannedTime"),
        PlannedZoneId = r.Sn("PlannedZoneId"),
        DueDate = r.Dn("DueDate"),
        DueTime = r.Tn("DueTime"),
        DueZoneId = r.Sn("DueZoneId"),
        CompletedAtUtc = r.In("CompletedAtUtc"),
        CancelledAtUtc = r.In("CancelledAtUtc"),
        Position = r.L("Position"),
        RecurrenceOccurrenceKey = r.Sn("RecurrenceOccurrenceKey"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };
}

public sealed class SqliteSectionStore(SqliteConnection connection) : ISectionStore
{
    public async Task<SectionRecord?> GetAsync(string sectionId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Sections WHERE Id = $id AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$id", sectionId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSection(reader) : null;
    }

    public async Task<IReadOnlyList<SectionRecord>> ListByContainerAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Sections WHERE ContainerId = $container AND DeletedAtUtc IS NULL ORDER BY Position, Id;";
        command.Parameters.AddWithValue("$container", containerId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var sections = new List<SectionRecord>();
        while (await reader.ReadAsync(ct))
        {
            sections.Add(ReadSection(reader));
        }

        return sections;
    }

    public async Task AddAsync(SectionRecord section, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Sections (Id, ContainerId, Title, Position, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $container, $title, $position, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", section.Id);
        command.Parameters.AddWithValue("$container", section.ContainerId);
        command.Parameters.AddWithValue("$title", section.Title);
        command.Parameters.AddWithValue("$position", section.Position);
        command.Parameters.AddWithValue("$version", section.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(section.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(section.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(section.DeletedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<SectionRecord?> UpdateAsync(SectionRecord updated, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Sections SET
                Title = $title, Position = $position,
                Version = $newVersion, UpdatedAtUtc = $updated_at, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expectedVersion;
            """;
        command.Parameters.AddWithValue("$title", updated.Title);
        command.Parameters.AddWithValue("$position", updated.Position);
        command.Parameters.AddWithValue("$newVersion", updated.Version);
        command.Parameters.AddWithValue("$updated_at", StorageFormats.Instant(updated.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(updated.DeletedAtUtc));
        command.Parameters.AddWithValue("$id", updated.Id);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);

        var rows = await command.ExecuteNonQueryAsync(ct);
        return rows == 1 ? await GetIncludingDeletedAsync(updated.Id, ct) : null;
    }

    public async Task<long> GetNextPositionAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Position), -1024) + 1024 FROM Sections WHERE ContainerId = $container AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$container", containerId);
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value);
    }

    public async Task<SectionRecord?> GetIncludingDeletedAsync(string sectionId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Sections WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", sectionId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSection(reader) : null;
    }

    public async Task<IReadOnlyList<SectionRecord>> ListDeletedByContainerAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Sections WHERE ContainerId = $container AND DeletedAtUtc IS NOT NULL ORDER BY DeletedAtUtc DESC, Id;";
        command.Parameters.AddWithValue("$container", containerId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<SectionRecord>();
        while (await reader.ReadAsync(ct)) rows.Add(ReadSection(reader));
        return rows;
    }

    internal static SectionRecord ReadSection(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        ContainerId = r.S("ContainerId"),
        Title = r.S("Title"),
        Position = r.L("Position"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };
}

public sealed class SqliteSubtaskStore(SqliteConnection connection) : ISubtaskStore
{
    public async Task<SubtaskRecord?> GetAsync(string subtaskId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Subtasks WHERE Id = $id AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$id", subtaskId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSubtask(reader) : null;
    }

    public async Task<IReadOnlyList<SubtaskRecord>> ListByTaskAsync(string taskId, CancellationToken ct)
    {
        return await ListAsync(taskId, includeDeleted: false, ct);
    }

    public async Task<IReadOnlyList<SubtaskRecord>> ListByTaskIncludingDeletedAsync(string taskId, CancellationToken ct)
    {
        return await ListAsync(taskId, includeDeleted: true, ct);
    }

    private async Task<IReadOnlyList<SubtaskRecord>> ListAsync(string taskId, bool includeDeleted, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = includeDeleted
            ? "SELECT * FROM Subtasks WHERE TaskId = $task ORDER BY Position, Id;"
            : "SELECT * FROM Subtasks WHERE TaskId = $task AND DeletedAtUtc IS NULL ORDER BY Position, Id;";
        command.Parameters.AddWithValue("$task", taskId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var subtasks = new List<SubtaskRecord>();
        while (await reader.ReadAsync(ct))
        {
            subtasks.Add(ReadSubtask(reader));
        }

        return subtasks;
    }

    public async Task AddAsync(SubtaskRecord subtask, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Subtasks (Id, TaskId, Title, Notes, Status, CompletedAtUtc, CancelledAtUtc,
                Position, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $task, $title, $notes, $status, $completed, $cancelled,
                $position, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", subtask.Id);
        command.Parameters.AddWithValue("$task", subtask.TaskId);
        command.Parameters.AddWithValue("$title", subtask.Title);
        command.Parameters.AddWithValue("$notes", subtask.Notes);
        command.Parameters.AddWithValue("$status", (long)subtask.Status);
        command.Parameters.AddWithValue("$completed", SqliteProfileStore.OptionalInstant(subtask.CompletedAtUtc));
        command.Parameters.AddWithValue("$cancelled", SqliteProfileStore.OptionalInstant(subtask.CancelledAtUtc));
        command.Parameters.AddWithValue("$position", subtask.Position);
        command.Parameters.AddWithValue("$version", subtask.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(subtask.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(subtask.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(subtask.DeletedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
        await IndexAsync(subtask, ct);
    }

    /// <summary>Keeps the subtask's search document (linked to its parent task) in step with the row.</summary>
    private async Task IndexAsync(SubtaskRecord subtask, CancellationToken ct) =>
        await Search.SearchIndexWriter.SyncAsync(connection, Search.SearchTargetKind.Subtask, subtask.Id, subtask.TaskId, subtask.Title, subtask.Notes, subtask.DeletedAtUtc is null, ct);

    public async Task<SubtaskRecord?> UpdateAsync(SubtaskRecord updated, long expectedVersion, CancellationToken ct)
    {
        // Optimistic concurrency (PLAN §5): no tombstone filter, so restore can rewrite
        // a deleted row; the version check alone decides.
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Subtasks SET
                Title = $title, Notes = $notes, Status = $status,
                CompletedAtUtc = $completed, CancelledAtUtc = $cancelled, Position = $position,
                Version = $newVersion, UpdatedAtUtc = $updated_at, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expectedVersion;
            """;
        command.Parameters.AddWithValue("$title", updated.Title);
        command.Parameters.AddWithValue("$notes", updated.Notes);
        command.Parameters.AddWithValue("$status", (long)updated.Status);
        command.Parameters.AddWithValue("$completed", SqliteProfileStore.OptionalInstant(updated.CompletedAtUtc));
        command.Parameters.AddWithValue("$cancelled", SqliteProfileStore.OptionalInstant(updated.CancelledAtUtc));
        command.Parameters.AddWithValue("$position", updated.Position);
        command.Parameters.AddWithValue("$newVersion", updated.Version);
        command.Parameters.AddWithValue("$updated_at", StorageFormats.Instant(updated.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(updated.DeletedAtUtc));
        command.Parameters.AddWithValue("$id", updated.Id);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);

        var rows = await command.ExecuteNonQueryAsync(ct);
        if (rows != 1)
        {
            return null;
        }

        await IndexAsync(updated, ct);
        return await GetIncludingDeletedAsync(updated.Id, ct);
    }

    public async Task<long> GetNextPositionAsync(string taskId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Position), -1024) + 1024 FROM Subtasks WHERE TaskId = $task AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$task", taskId);
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value);
    }

    public async Task<SubtaskRecord?> GetIncludingDeletedAsync(string subtaskId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Subtasks WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", subtaskId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSubtask(reader) : null;
    }

    internal static SubtaskRecord ReadSubtask(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        TaskId = r.S("TaskId"),
        Title = r.S("Title"),
        Notes = r.S("Notes"),
        Status = (TaskItemStatus)(int)r.L("Status"),
        CompletedAtUtc = r.In("CompletedAtUtc"),
        CancelledAtUtc = r.In("CancelledAtUtc"),
        Position = r.L("Position"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };
}

public sealed class SqliteTemplateStore(SqliteConnection connection) : ITemplateStore
{
    public async Task<IReadOnlyList<TemplateRecord>> ListDeletedBySpaceAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TaskTemplates WHERE SpaceId = $space AND DeletedAtUtc IS NOT NULL ORDER BY DeletedAtUtc DESC, Id;";
        command.Parameters.AddWithValue("$space", spaceId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<TemplateRecord>();
        while (await reader.ReadAsync(ct)) results.Add(ReadTemplate(reader));
        return results;
    }
    public async Task<TemplateRecord?> GetIncludingDeletedAsync(string templateId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TaskTemplates WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", templateId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTemplate(reader) : null;
    }
    public async Task<TemplateRecord?> GetAsync(string templateId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TaskTemplates WHERE Id = $id AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$id", templateId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTemplate(reader) : null;
    }

    public async Task<TemplateRecord?> FindByNameAsync(string spaceId, string normalizedName, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TaskTemplates WHERE SpaceId = $space AND NormalizedName = $name AND DeletedAtUtc IS NULL LIMIT 1;";
        command.Parameters.AddWithValue("$space", spaceId);
        command.Parameters.AddWithValue("$name", normalizedName);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTemplate(reader) : null;
    }

    public async Task<IReadOnlyList<TemplateRecord>> ListBySpaceAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TaskTemplates WHERE SpaceId = $space AND DeletedAtUtc IS NULL ORDER BY NormalizedName, Id;";
        command.Parameters.AddWithValue("$space", spaceId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var templates = new List<TemplateRecord>();
        while (await reader.ReadAsync(ct))
        {
            templates.Add(ReadTemplate(reader));
        }

        return templates;
    }

    public async Task AddAsync(TemplateRecord template, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TaskTemplates (Id, SpaceId, Name, NormalizedName, Payload, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $space, $name, $normalizedName, $payload, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", template.Id);
        command.Parameters.AddWithValue("$space", template.SpaceId);
        command.Parameters.AddWithValue("$name", template.Name);
        command.Parameters.AddWithValue("$normalizedName", template.NormalizedName);
        command.Parameters.AddWithValue("$payload", template.PayloadJson);
        command.Parameters.AddWithValue("$version", template.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(template.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(template.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(template.DeletedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<TemplateRecord?> UpdateAsync(TemplateRecord updated, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE TaskTemplates SET
                Name = $name, NormalizedName = $normalizedName, Payload = $payload,
                Version = $newVersion, UpdatedAtUtc = $updated_at, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expectedVersion;
            """;
        command.Parameters.AddWithValue("$name", updated.Name);
        command.Parameters.AddWithValue("$normalizedName", updated.NormalizedName);
        command.Parameters.AddWithValue("$payload", updated.PayloadJson);
        command.Parameters.AddWithValue("$newVersion", updated.Version);
        command.Parameters.AddWithValue("$updated_at", StorageFormats.Instant(updated.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(updated.DeletedAtUtc));
        command.Parameters.AddWithValue("$id", updated.Id);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);

        var rows = await command.ExecuteNonQueryAsync(ct);
        return rows == 1 ? await GetIncludingDeletedAsync(updated.Id, ct) : null;
    }

    internal static TemplateRecord ReadTemplate(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        SpaceId = r.S("SpaceId"),
        Name = r.S("Name"),
        NormalizedName = r.S("NormalizedName"),
        PayloadJson = r.S("Payload"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };
}

public sealed class SqliteTaskHistoryStore(SqliteConnection connection) : ITaskHistoryStore
{
    public async Task AddAsync(TaskHistoryRecord entry, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TaskHistory (Id, TaskId, OperationId, EffectiveAtUtc, Actor, EventKind,
                ChangedFields, SnapshotBefore, SnapshotAfter, AggregateVersion, RecordedAtUtc)
            VALUES ($id, $task, $operation, $effective, $actor, $eventKind, $changed, $before, $after, $aggregateVersion, $recorded);
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$task", entry.TaskId);
        command.Parameters.AddWithValue("$operation", entry.OperationId);
        command.Parameters.AddWithValue("$effective", StorageFormats.Instant(entry.EffectiveAtUtc));
        command.Parameters.AddWithValue("$actor", entry.Actor);
        command.Parameters.AddWithValue("$eventKind", entry.EventKind);
        command.Parameters.AddWithValue("$changed", string.Join(",", entry.ChangedFields));
        command.Parameters.AddWithValue("$before", SqliteProfileStore.OptionalValue(entry.SnapshotBefore));
        command.Parameters.AddWithValue("$after", SqliteProfileStore.OptionalValue(entry.SnapshotAfter));
        command.Parameters.AddWithValue("$aggregateVersion", entry.AggregateVersion);
        command.Parameters.AddWithValue("$recorded", StorageFormats.Instant(entry.RecordedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<TaskHistoryRecord>> ListByTaskAsync(string taskId, CancellationToken ct)
    {
        // Rowid preserves insertion order when multiple events share a clock instant.
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TaskHistory WHERE TaskId = $task ORDER BY rowid;";
        command.Parameters.AddWithValue("$task", taskId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var entries = new List<TaskHistoryRecord>();
        while (await reader.ReadAsync(ct))
        {
            entries.Add(ReadHistoryEntry(reader));
        }

        return entries;
    }

    internal static TaskHistoryRecord ReadHistoryEntry(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        TaskId = r.S("TaskId"),
        OperationId = r.S("OperationId"),
        EffectiveAtUtc = r.I("EffectiveAtUtc"),
        Actor = r.S("Actor"),
        EventKind = r.S("EventKind"),
        ChangedFields = r.S("ChangedFields").Split(',', StringSplitOptions.RemoveEmptyEntries),
        SnapshotBefore = r.Sn("SnapshotBefore"),
        SnapshotAfter = r.Sn("SnapshotAfter"),
        AggregateVersion = r.L("AggregateVersion"),
        RecordedAtUtc = r.I("RecordedAtUtc"),
    };
}

public sealed class SqliteOperationReceiptStore(SqliteConnection connection) : IOperationReceiptStore
{
    public async Task<IReadOnlyList<OperationReceiptRecord>> ListByKindAsync(string profileId, string commandKind, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM OperationReceipts WHERE ProfileId = $profile AND CommandKind = $kind ORDER BY rowid DESC;";
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$kind", commandKind);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<OperationReceiptRecord>();
        while (await reader.ReadAsync(ct)) rows.Add(ReadReceipt(reader));
        return rows;
    }
    public async Task<OperationReceiptRecord?> FindAsync(string profileId, string operationId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM OperationReceipts WHERE ProfileId = $profile AND OperationId = $operation;";
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$operation", operationId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadReceipt(reader) : null;
    }

    public async Task AddAsync(OperationReceiptRecord receipt, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO OperationReceipts (ProfileId, OperationId, CommandKind, PayloadHash, ResultJson, CommittedAtUtc)
            VALUES ($profile, $operation, $kind, $hash, $result, $committed);
            """;
        command.Parameters.AddWithValue("$profile", receipt.ProfileId);
        command.Parameters.AddWithValue("$operation", receipt.OperationId);
        command.Parameters.AddWithValue("$kind", receipt.CommandKind);
        command.Parameters.AddWithValue("$hash", receipt.PayloadHash);
        command.Parameters.AddWithValue("$result", receipt.ResultJson);
        command.Parameters.AddWithValue("$committed", StorageFormats.Instant(receipt.CommittedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static OperationReceiptRecord ReadReceipt(SqliteDataReader r) => new()
    {
        ProfileId = r.S("ProfileId"),
        OperationId = r.S("OperationId"),
        CommandKind = r.S("CommandKind"),
        PayloadHash = r.S("PayloadHash"),
        ResultJson = r.S("ResultJson"),
        CommittedAtUtc = r.I("CommittedAtUtc"),
    };
}

public sealed class SqliteBackupCatalogStore(SqliteConnection connection) : IBackupCatalogStore
{
    public async Task AddAsync(BackupCatalogRecord entry, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO BackupCatalog (Id, FileName, SchemaVersion, AppVersion, CreatedAtUtc, ChecksumSha256, ValidationStatus, Reason)
            VALUES ($id, $file, $schemaVersion, $appVersion, $created, $checksum, $status, $reason);
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$file", entry.FileName);
        command.Parameters.AddWithValue("$schemaVersion", entry.SchemaVersion);
        command.Parameters.AddWithValue("$appVersion", entry.AppVersion);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(entry.CreatedAtUtc));
        command.Parameters.AddWithValue("$checksum", entry.ChecksumSha256);
        command.Parameters.AddWithValue("$status", FormatStatus(entry.ValidationStatus));
        command.Parameters.AddWithValue("$reason", FormatReason(entry.Reason));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<BackupCatalogRecord?> FindByFileNameAsync(string fileName, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM BackupCatalog WHERE FileName = $file;";
        command.Parameters.AddWithValue("$file", fileName);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEntry(reader) : null;
    }

    internal static BackupCatalogRecord ReadEntry(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        FileName = r.S("FileName"),
        SchemaVersion = r.L("SchemaVersion"),
        AppVersion = r.S("AppVersion"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        ChecksumSha256 = r.S("ChecksumSha256"),
        ValidationStatus = ParseStatus(r.S("ValidationStatus")),
        Reason = ParseReason(r.S("Reason")),
    };

    // Stored tokens match the CHECK constraints in migration 001 (lowercase kebab-case).
    internal static string FormatStatus(BackupValidationStatus status) => status switch
    {
        BackupValidationStatus.Valid => "valid",
        BackupValidationStatus.Unvalidated => "unvalidated",
        BackupValidationStatus.Invalid => "invalid",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    internal static BackupValidationStatus ParseStatus(string text) => text switch
    {
        "valid" => BackupValidationStatus.Valid,
        "unvalidated" => BackupValidationStatus.Unvalidated,
        "invalid" => BackupValidationStatus.Invalid,
        _ => throw new InvalidOperationException($"BackupCatalog.ValidationStatus holds an unknown token '{text}'."),
    };

    internal static string FormatReason(BackupReason reason) => reason switch
    {
        BackupReason.PreMigration => "pre-migration",
        BackupReason.PreImport => "pre-import",
        BackupReason.PreRestore => "pre-restore",
        BackupReason.Manual => "manual",
        BackupReason.Scheduled => "scheduled",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    internal static BackupReason ParseReason(string text) => text switch
    {
        "pre-migration" => BackupReason.PreMigration,
        "pre-import" => BackupReason.PreImport,
        "pre-restore" => BackupReason.PreRestore,
        "manual" => BackupReason.Manual,
        "scheduled" => BackupReason.Scheduled,
        _ => throw new InvalidOperationException($"BackupCatalog.Reason holds an unknown token '{text}'."),
    };
}
