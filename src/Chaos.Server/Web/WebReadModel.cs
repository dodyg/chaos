using System.Globalization;
using System.Text.Json;
using Chaos.Server.Data;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Web;

internal sealed record WebList(Guid Id, string Name, bool IsInbox, long Position, bool IsArchived);
internal sealed record WebTask(Guid Id, Guid ListId, string Title, string? Notes, long Status, DateOnly? PlannedDate,
    DateOnly? DueDate, bool IsRecurring, long Priority, long Position)
{
    public bool IsTerminal => Status is WebReadModel.Done or WebReadModel.Cancelled;
}
internal sealed record WebToday(DateOnly Date, IReadOnlyList<WebTask> Tasks);

/// <summary>Projects the desktop sync groups into the mobile web views.</summary>
internal sealed class WebReadModel(ChaosDbContext db, TimeProvider clock)
{
    internal const int NotStarted = 0;
    internal const int Done = 4;
    internal const int Cancelled = 5;

    public Task<Account?> AccountAsync(Guid accountId, CancellationToken ct) =>
        db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId && a.DeletedAt == null, ct);

    public async Task<WebToday> TodayAsync(Account account, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var (today, zone) = await LocalDayAsync(account, now, ct);
        var day = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var lists = (await ListsAsync(account, ct)).Where(list => !list.IsArchived).ToDictionary(list => list.Id);
        var plan = await DayPositionsAsync(account, day, ct);
        var missedOccurrences = await MissedOccurrencesAsync(account, now, ct);
        var missedKeys = missedOccurrences.Select(id => id.ToString("D")).ToArray();
        var rows = await db.SyncEntities.FromSqlInterpolated($"""
            SELECT * FROM "SyncEntities"
            WHERE "AccountId" = {account.Id} AND "Kind" = 'task' AND "PurgedAt" IS NULL
              AND ("Groups" -> 'planned' -> 'values' ->> 'PlannedDate' = {day}
                OR "Groups" -> 'due' -> 'values' ->> 'DueDate' IS NOT NULL
                OR "EntityKey" = ANY({missedKeys}))
            """).AsNoTracking().ToListAsync(ct);
        var tasks = rows.Select(row => (row.EntityKey, Entity: new WebEntity(row)))
            .Select(row => (row.Entity, Task: ReadTask(row.EntityKey, row.Entity, account, includeNotes: false)))
            .Where(row => row.Task is { IsTerminal: false } task && lists.ContainsKey(task.ListId) &&
                (task.PlannedDate == today || task.DueDate == today || IsOverdue(task, row.Entity, today, zone, now)
                    || (task.DueDate is null && missedOccurrences.Contains(task.Id))))
            .Select(row => row.Task!)
            .OrderBy(task => !plan.ContainsKey(task.Id))
            .ThenBy(task => plan.GetValueOrDefault(task.Id))
            .ThenByDescending(task => task.Priority)
            .ThenBy(task => lists[task.ListId].Position)
            .ThenBy(task => task.Position)
            .ThenBy(task => task.Id);
        return new WebToday(today, [.. tasks]);
    }

    public async Task<IReadOnlyList<WebList>> ListsAsync(Account account, CancellationToken ct)
    {
        var rows = await Rows(account.Id, "container").ToListAsync(ct);
        return rows.Select(row => ReadList(row, account)).OfType<WebList>()
            .OrderByDescending(list => list.IsInbox).ThenBy(list => list.Position).ThenBy(list => list.Id).ToList();
    }

    public async Task<WebList?> ListAsync(Account account, Guid listId, CancellationToken ct)
    {
        var key = listId.ToString("D");
        var row = await Rows(account.Id, "container").SingleOrDefaultAsync(e => e.EntityKey == key, ct);
        return row is null ? null : ReadList(row, account);
    }

    public async Task<IReadOnlyList<WebTask>> TasksInListAsync(Account account, Guid listId, CancellationToken ct)
    {
        var key = listId.ToString("D");
        var rows = await db.SyncEntities.FromSqlInterpolated($"""
            SELECT * FROM "SyncEntities"
            WHERE "AccountId" = {account.Id} AND "Kind" = 'task' AND "PurgedAt" IS NULL
              AND "Groups" -> 'place' -> 'values' ->> 'ContainerId' = {key}
            """).AsNoTracking().ToListAsync(ct);
        return rows.Select(row => ReadTask(row.EntityKey, new WebEntity(row), account, includeNotes: false))
            .OfType<WebTask>().OrderBy(task => task.Position).ThenBy(task => task.Id).ToList();
    }

    public async Task<(WebTask Task, WebEntity Entity)?> TaskStateAsync(Account account, Guid taskId, CancellationToken ct)
    {
        var key = taskId.ToString("D");
        var row = await Rows(account.Id, "task").SingleOrDefaultAsync(e => e.EntityKey == key, ct);
        if (row is null)
        {
            return null;
        }

        var entity = new WebEntity(row);
        var task = ReadTask(key, entity, account, includeNotes: true);
        return task is not null && await ListAsync(account, task.ListId, ct) is not null ? (task, entity) : null;
    }

    public async Task<(DateOnly Date, TimeZoneInfo Zone)> LocalDayAsync(Account account, DateTimeOffset now, CancellationToken ct)
    {
        var key = account.ProfileId?.ToString("D");
        var profile = await Rows(account.Id, "profile").SingleOrDefaultAsync(e => e.EntityKey == key, ct);
        var entity = profile is null ? null : new WebEntity(profile);
        var zone = entity is { IsDeleted: false } ? FindZone(entity.Text("zone", "ZoneId")) : null;
        zone ??= TimeZoneInfo.Utc;
        return (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime), zone);
    }

    private async Task<Dictionary<Guid, long>> DayPositionsAsync(Account account, string day, CancellationToken ct)
    {
        var profile = account.ProfileId?.ToString("D");
        if (profile is null)
        {
            return [];
        }

        var rows = await db.SyncEntities.FromSqlInterpolated($"""
            SELECT item.* FROM "SyncEntities" AS item
            WHERE item."AccountId" = {account.Id} AND item."Kind" = 'day_plan_item' AND item."PurgedAt" IS NULL
              AND item."Groups" -> 'removed' -> 'values' ->> 'RemovedAtUtc' IS NULL
              AND item."Groups" -> 'create' -> 'values' ->> 'DayPlanId' = (
                  SELECT plan."EntityKey" FROM "SyncEntities" AS plan
                  WHERE plan."AccountId" = {account.Id} AND plan."Kind" = 'day_plan' AND plan."PurgedAt" IS NULL
                    AND plan."Groups" -> 'create' -> 'values' ->> 'ProfileId' = {profile}
                    AND plan."Groups" -> 'create' -> 'values' ->> 'LocalDate' = {day}
                  ORDER BY plan."EntityKey" LIMIT 1)
            """).AsNoTracking().ToListAsync(ct);
        return rows.Select(row => new WebEntity(row))
            .Select(item => (TaskId: item.Id("create", "TaskId"), Position: item.Number("position", "Position") ?? 0))
            .Where(item => item.TaskId.HasValue)
            .GroupBy(item => item.TaskId!.Value)
            .ToDictionary(items => items.Key, items => items.Min(item => item.Position));
    }

    private IQueryable<SyncEntityRow> Rows(Guid accountId, string kind) =>
        db.SyncEntities.AsNoTracking().Where(e => e.AccountId == accountId && e.Kind == kind && e.PurgedAt == null);

    private async Task<HashSet<Guid>> MissedOccurrencesAsync(Account account, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<OccurrenceSchedule>($"""
            SELECT occurrence."Groups" -> 'task' -> 'values' ->> 'TaskId' AS "TaskId",
                   occurrence."Groups" -> 'create' -> 'values' ->> 'EffectiveLocal' AS "EffectiveLocal",
                   series."Groups" -> 'rule' -> 'values' ->> 'RuleJson' AS "RuleJson"
            FROM "SyncEntities" AS occurrence
            JOIN "SyncEntities" AS series ON series."AccountId" = occurrence."AccountId"
              AND series."Kind" = 'recurrence_series' AND series."PurgedAt" IS NULL
              AND series."EntityKey" = occurrence."Groups" -> 'create' -> 'values' ->> 'SeriesId'
            WHERE occurrence."AccountId" = {account.Id} AND occurrence."Kind" = 'recurrence_occurrence'
              AND occurrence."PurgedAt" IS NULL AND occurrence."Groups" -> 'state' -> 'values' ->> 'State' = '0'
              AND occurrence."Groups" -> 'deleted' -> 'values' ->> 'DeletedAtUtc' IS NULL
              AND series."Groups" -> 'deleted' -> 'values' ->> 'DeletedAtUtc' IS NULL
            """).ToListAsync(ct);
        var tasks = new HashSet<Guid>();
        foreach (var row in rows)
        {
            if (Guid.TryParse(row.TaskId, out var taskId) && IsMissedOccurrence(row, now))
            {
                tasks.Add(taskId);
            }
        }

        return tasks;
    }

    private static bool IsMissedOccurrence(OccurrenceSchedule schedule, DateTimeOffset now)
    {
        if (schedule.RuleJson is null || !DateTime.TryParseExact(schedule.EffectiveLocal, "yyyy-MM-dd'T'HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var effective))
        {
            return false;
        }

        try
        {
            using var rule = JsonDocument.Parse(schedule.RuleJson);
            if (rule.RootElement.ValueKind != JsonValueKind.Object ||
                !rule.RootElement.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("zoneId", out var zoneId) || zoneId.ValueKind != JsonValueKind.String ||
                FindZone(zoneId.GetString()) is not { } zone)
            {
                return false;
            }

            var localNow = TimeZoneInfo.ConvertTime(now, zone).DateTime;
            var isTimed = payload.TryGetProperty("anchorTime", out var anchor) && anchor.ValueKind == JsonValueKind.String;
            return isTimed ? effective < localNow : effective.Date < localNow.Date;
        }
        catch (JsonException)
        {
            return false; // A partial or unreadable desktop rule cannot establish an overdue occurrence.
        }
    }

    private sealed record OccurrenceSchedule(string? TaskId, string? EffectiveLocal, string? RuleJson);

    private static WebList? ReadList(SyncEntityRow row, Account account)
    {
        var entity = new WebEntity(row);
        return !entity.IsDeleted && InPersonalSpace(entity, account) && Guid.TryParse(row.EntityKey, out var id)
            && entity.Text("name", "Name") is { Length: > 0 } name
            ? new WebList(id, name, id == account.InboxId, entity.Number("position", "Position") ?? 0,
                entity.Text("archived", "ArchivedAtUtc") is not null)
            : null;
    }

    private static WebTask? ReadTask(string key, WebEntity entity, Account account, bool includeNotes) =>
        !entity.IsDeleted && InPersonalSpace(entity, account) && Guid.TryParse(key, out var id) &&
        entity.Id("place", "ContainerId") is { } listId && entity.Text("title", "Title") is { Length: > 0 } title
            ? new WebTask(id, listId, title, includeNotes ? entity.Text("notes", "Notes") : null,
                entity.Number("status", "Status") ?? NotStarted, entity.Date("planned", "PlannedDate"), entity.Date("due", "DueDate"),
                entity.Text("create", "RecurrenceOccurrenceKey") is not null,
                entity.Number("priority", "Priority") ?? 0, entity.Number("position", "Position") ?? 0)
            : null;

    private static bool InPersonalSpace(WebEntity entity, Account account) =>
        account.PersonalSpaceId is { } spaceId && entity.Id("create", "SpaceId") == spaceId;

    private static bool IsOverdue(WebTask task, WebEntity entity, DateOnly today, TimeZoneInfo profileZone, DateTimeOffset now)
    {
        if (task.DueDate is not { } due)
        {
            return false;
        }

        if (!TimeOnly.TryParseExact(entity.Text("due", "DueTime"), "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return due < today;
        }

        var zone = FindZone(entity.Text("due", "DueZoneId")) ?? profileZone;
        return due.ToDateTime(time) < TimeZoneInfo.ConvertTime(now, zone).DateTime;
    }

    private static TimeZoneInfo? FindZone(string? id) =>
        id is { Length: > 0 } && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;
}

internal sealed class WebEntity(SyncEntityRow row)
{
    public Dictionary<string, SyncGroupState> Groups { get; } = SyncJson.ReadGroups(row.Groups);

    public bool IsDeleted => Value("deleted", "DeletedAtUtc") is not null;

    public string? Text(string group, string column) => Value(group, column) as string;

    public long? Number(string group, string column) => Value(group, column) is long number ? number : null;

    public Guid? Id(string group, string column) => Guid.TryParse(Text(group, column), out var id) ? id : null;

    public DateOnly? Date(string group, string column) =>
        DateOnly.TryParseExact(Text(group, column), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private object? Value(string group, string column) => Groups.GetValueOrDefault(group)?.Values.Values.GetValueOrDefault(column);
}
