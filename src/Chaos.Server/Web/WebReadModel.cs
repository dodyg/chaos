using System.Globalization;
using System.Text.Json;
using Chaos.Server.Data;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Web;

internal sealed record WebList(Guid Id, string Name, bool IsInbox, long Position, bool IsArchived);
internal sealed record WebTask(Guid Id, Guid ListId, string Title, string? Notes, long Status, DateOnly? PlannedDate,
    DateOnly? DueDate, bool IsRecurring, long Priority, long Position, TimeOnly? PlannedTime, string? PlannedZoneId,
    TimeOnly? DueTime, string? DueZoneId)
{
    public bool IsOnMyDay { get; init; }
    public bool IsTerminal => Status is WebReadModel.Done or WebReadModel.Cancelled;
}
internal sealed record WebNote(Guid Id, string Markdown, long Kind, long Position, bool IsArchived);
internal sealed record WebPage(Guid Id, string Title, string Markdown, Guid? ParentId, DateOnly? DailyDate, long Position);
internal sealed record WebSearchItem(string Kind, Guid Id, string Title, string? Snippet, string? Detail);
internal sealed record WebSearchResult(IReadOnlyList<WebSearchItem> Items, bool Truncated);
internal sealed record WebAppearance(string? Theme, string? Background);
internal sealed record WebMeeting(string Id, string Title, bool AllDay, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Start, string End, string? VideoUrl, string? WebUrl, string? Color, bool Busy);
internal sealed record WebToday(DateOnly Date, IReadOnlyList<WebTask> Tasks, IReadOnlyList<WebMeeting> Meetings);
internal sealed record WebDayMember(string Key, string TaskKey, long Position, WebEntity? Entity)
{
    public bool IsActive => Entity?.Text("removed", "RemovedAtUtc") is null;
}

internal sealed record WebDayPlan(string Key, bool Exists, string? WeekKey, long? WeekRevision, IReadOnlyList<WebDayMember> Members);

/// <summary>Projects the desktop sync groups into the mobile web views.</summary>
internal sealed class WebReadModel(ChaosDbContext db, TimeProvider clock)
{
    internal const int NotStarted = 0;
    internal const int Done = 4;
    internal const int Cancelled = 5;

    public Task<Account?> AccountAsync(Guid accountId, CancellationToken ct) =>
        db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId && a.DeletedAt == null, ct);

    public async Task<IReadOnlyList<WebNote>> PadAsync(Account account, CancellationToken ct)
    {
        var rows = await Rows(account.Id, "pad").ToListAsync(ct);
        return rows.Select(row => (Row: row, Entity: new WebEntity(row)))
            .Where(item => !item.Entity.IsDeleted && InPersonalSpace(item.Entity, account) && Guid.TryParse(item.Row.EntityKey, out _))
            .Select(item => new WebNote(Guid.Parse(item.Row.EntityKey), item.Entity.Text("text", "RawMarkdown") ?? "",
                item.Entity.Number("create", "Kind") ?? 0, item.Entity.Number("position", "Position") ?? 0,
                item.Entity.Text("archived", "ArchivedAtUtc") is not null))
            .OrderBy(note => note.IsArchived).ThenBy(note => note.Position).ThenBy(note => note.Id).ToList();
    }

    public async Task<IReadOnlyList<WebPage>> PagesAsync(Account account, CancellationToken ct)
    {
        var rows = await Rows(account.Id, "page").ToListAsync(ct);
        return rows.Select(row => (Row: row, Entity: new WebEntity(row)))
            .Where(item => !item.Entity.IsDeleted && InPersonalSpace(item.Entity, account) && Guid.TryParse(item.Row.EntityKey, out _))
            .Select(item => new WebPage(Guid.Parse(item.Row.EntityKey), item.Entity.Text("title", "Title") ?? "Untitled",
                item.Entity.Text("text", "RawMarkdown") ?? "", item.Entity.Id("tree", "ParentPageId"),
                item.Entity.Date("create", "DailyDate"), item.Entity.Number("position", "Position") ?? 0))
            .OrderBy(page => page.Position).ThenBy(page => page.Id).ToList();
    }

    public async Task<WebSearchResult> SearchAsync(Account account, string query, CancellationToken ct)
    {
        if (account.PersonalSpaceId is not { } space) return new([], false);
        var spaceKey = space.ToString("D");
        var rows = await db.SyncEntities.FromSqlInterpolated($"""
            SELECT * FROM "SyncEntities"
            WHERE "AccountId" = {account.Id} AND "Kind" IN ('task', 'pad', 'page') AND "PurgedAt" IS NULL
              AND "Groups" -> 'deleted' -> 'values' ->> 'DeletedAtUtc' IS NULL
              AND "Groups" -> 'create' -> 'values' ->> 'SpaceId' = {spaceKey}
              AND (strpos(lower(coalesce("Groups" -> 'title' -> 'values' ->> 'Title', '')), lower({query})) > 0
                OR strpos(lower(coalesce("Groups" -> 'notes' -> 'values' ->> 'Notes', '')), lower({query})) > 0
                OR strpos(lower(coalesce("Groups" -> 'text' -> 'values' ->> 'RawMarkdown', '')), lower({query})) > 0)
            ORDER BY "Kind", "EntityKey"
            """).AsNoTracking().ToListAsync(ct);
        var lists = (await ListsAsync(account, ct)).ToDictionary(list => list.Id);
        var results = new List<WebSearchItem>();
        foreach (var row in rows)
        {
            var entity = new WebEntity(row);
            if (entity.IsDeleted || !InPersonalSpace(entity, account) || !Guid.TryParse(row.EntityKey, out var id)) continue;
            var text = entity.Text("text", "RawMarkdown") ?? "";
            if (row.Kind == "task")
            {
                var task = ReadTask(row.EntityKey, entity, account, includeNotes: true);
                if (task is null || !lists.TryGetValue(task.ListId, out var list)) continue;
                results.Add(new("task", id, task.Title, SearchSnippet(task.Notes, query), list.Name));
            }
            else if (row.Kind == "page")
            {
                results.Add(new("page", id, entity.Text("title", "Title") ?? "Untitled", SearchSnippet(text, query),
                    entity.Date("create", "DailyDate") is { } date ? $"Daily page · {date:yyyy-MM-dd}" : "Page"));
            }
            else if (row.Kind == "pad" && (entity.Number("create", "Kind") ?? 0) == 0)
            {
                var first = text.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? "Empty note";
                results.Add(new("pad", id, first.Length > 100 ? first[..100] : first, SearchSnippet(text, query),
                    entity.Text("archived", "ArchivedAtUtc") is null ? "Personal pad" : "Archived note"));
            }
            if (results.Count > 100) return new(results.Take(100).ToList(), true);
        }
        return new(results, false);
    }

    private static string? SearchSnippet(string? text, string query)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var start = Math.Max(0, index - 45);
        var length = Math.Min(text.Length - start, Math.Max(140, query.Length));
        var excerpt = string.Join(" ", text.Substring(start, length).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return (start > 0 ? "…" : "") + excerpt + (start + length < text.Length ? "…" : "");
    }

    public async Task<WebAppearance> AppearanceAsync(Account account, CancellationToken ct)
    {
        if (account.ProfileId is not { } profile) return new(null, null);
        var key = profile.ToString("D");
        var row = await Rows(account.Id, "profile").SingleOrDefaultAsync(e => e.EntityKey == key, ct);
        if (row is null) return new(null, null);
        var entity = new WebEntity(row);
        return new(entity.Text("appearance", "AppearanceTheme"), entity.Text("day_background", "DayBackground"));
    }

    public async Task<WebToday> TodayAsync(Account account, CancellationToken ct)
    {
        var (today, zone) = await LocalDayAsync(account, clock.GetUtcNow(), ct);
        var meetings = await MeetingsAsync(account, today, zone, ct);
        var plan = await DayPlanAsync(account, today, ct);
        var members = plan?.Members.Where(item => item.IsActive).ToArray() ?? [];
        if (members.Length == 0)
        {
            return new WebToday(today, [], meetings);
        }

        var lists = (await ListsAsync(account, ct)).Select(list => list.Id).ToHashSet();
        var tasks = (await TasksByKeyAsync(account, members.Select(member => member.TaskKey).Distinct().ToArray(), ct))
            .Where(task => lists.Contains(task.ListId)).ToDictionary(task => task.Id.ToString("D"));
        return new WebToday(today, [.. members.OrderBy(item => item.Position).ThenBy(item => item.TaskKey, StringComparer.Ordinal)
            .Select(item => item.TaskKey).Distinct().Where(tasks.ContainsKey).Select(key => tasks[key] with { IsOnMyDay = true })], meetings);
    }

    private async Task<IReadOnlyList<WebMeeting>> MeetingsAsync(Account account, DateOnly today, TimeZoneInfo zone, CancellationToken ct)
    {
        var sources = (await Rows(account.Id, "calendar_source").ToListAsync(ct)).ToDictionary(row => row.EntityKey, row => new WebEntity(row));
        var caches = await Rows(account.Id, "calendar_cache").ToListAsync(ct);
        var start = TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue), zone);
        var end = TimeZoneInfo.ConvertTimeToUtc(today.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        var meetings = new List<WebMeeting>();
        foreach (var row in caches)
        {
            if (!sources.TryGetValue(row.EntityKey, out var source) || source.IsDeleted || source.Number("enabled", "Enabled") != 1) continue;
            var cache = JsonSerializer.Deserialize<CalendarCacheSnapshot>(row.Groups);
            if (cache is null) continue;
            meetings.AddRange(cache.Events.Where(e => e.AllDay ? e.StartDate <= today && e.EndDate > today : e.StartUtc < end && e.EndUtc > start)
                .Select(e => new WebMeeting(row.EntityKey + ":" + e.ExternalId, e.Title, e.AllDay, e.StartUtc, e.EndUtc,
                    TimeZoneInfo.ConvertTime(e.StartUtc, zone).ToString("HH:mm", CultureInfo.InvariantCulture),
                    TimeZoneInfo.ConvertTime(e.EndUtc, zone).ToString("HH:mm", CultureInfo.InvariantCulture), e.VideoUrl, e.WebUrl, source.Text("look", "Color"), e.Busy && source.Number("busy", "CountsAsBusy") == 1)));
        }
        return [.. meetings.OrderByDescending(m => m.AllDay).ThenBy(m => m.StartUtc).ThenBy(m => m.Title)];
    }

    public async Task<WebTask?> TaskDetailAsync(Account account, Guid taskId, CancellationToken ct)
    {
        if (await TaskStateAsync(account, taskId, ct) is not { } state)
        {
            return null;
        }

        var (today, _) = await LocalDayAsync(account, clock.GetUtcNow(), ct);
        var plan = await DayPlanAsync(account, today, ct);
        return state.Task with { IsOnMyDay = plan?.Members.Any(item => item.IsActive && item.TaskKey == taskId.ToString("D")) == true };
    }

    private async Task<IEnumerable<WebTask>> TasksByKeyAsync(Account account, string[] keys, CancellationToken ct)
    {
        var rows = await db.SyncEntities.FromSqlInterpolated($"""
            SELECT "AccountId", "Kind", "EntityKey", "Groups" - 'notes' AS "Groups", "PurgedAt", "LastSeq", "UpdatedAt"
            FROM "SyncEntities"
            WHERE "AccountId" = {account.Id} AND "Kind" = 'task' AND "PurgedAt" IS NULL
              AND "EntityKey" = ANY({keys})
            """).AsNoTracking().ToListAsync(ct);
        return rows.Select(row => ReadTask(row.EntityKey, new WebEntity(row), account, includeNotes: false)).OfType<WebTask>();
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
            SELECT "AccountId", "Kind", "EntityKey", "Groups" - 'notes' AS "Groups", "PurgedAt", "LastSeq", "UpdatedAt"
            FROM "SyncEntities"
            WHERE "AccountId" = {account.Id} AND "Kind" = 'task' AND "PurgedAt" IS NULL
              AND "Groups" -> 'deleted' -> 'values' ->> 'DeletedAtUtc' IS NULL
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

    public async Task<WebDayPlan?> DayPlanAsync(Account account, DateOnly date, CancellationToken ct)
    {
        if (account.ProfileId is not { } profileId)
        {
            return null;
        }

        var profile = profileId.ToString("D");
        var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var plan = await db.SyncEntities.FromSqlInterpolated($"""
            SELECT * FROM "SyncEntities"
            WHERE "AccountId" = {account.Id} AND "Kind" = 'day_plan' AND "PurgedAt" IS NULL
              AND "Groups" -> 'create' -> 'values' ->> 'ProfileId' = {profile}
              AND "Groups" -> 'create' -> 'values' ->> 'LocalDate' = {day}
            ORDER BY "EntityKey" LIMIT 1
            """).AsNoTracking().FirstOrDefaultAsync(ct);

        if (plan is null)
        {
            return await WeekSeedAsync(account, profile, date, day, ct);
        }

        var rows = await db.SyncEntities.FromSqlInterpolated($"""
            SELECT * FROM "SyncEntities"
            WHERE "AccountId" = {account.Id} AND "Kind" = 'day_plan_item' AND "PurgedAt" IS NULL
              AND "Groups" -> 'create' -> 'values' ->> 'DayPlanId' = {plan.EntityKey}
            ORDER BY "EntityKey"
            """).AsNoTracking().ToListAsync(ct);

        var members = new List<WebDayMember>();
        foreach (var row in rows)
        {
            var entity = new WebEntity(row);
            if (entity.Id("create", "TaskId") is { } taskId)
            {
                members.Add(new WebDayMember(row.EntityKey, taskId.ToString("D"), entity.Number("position", "Position") ?? 0, entity));
            }
        }

        return new WebDayPlan(plan.EntityKey, true, null, null, members);
    }

    private async Task<WebDayPlan> WeekSeedAsync(Account account, string profile, DateOnly date, string day, CancellationToken ct)
    {
        var planKey = NameBasedId.Create(NameBasedId.Names.DayPlan(profile, date)).ToString("D");
        var earliest = date.AddDays(-Math.Min(6, date.DayNumber)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var week = await db.SyncEntities.FromSqlInterpolated($"""
            SELECT * FROM "SyncEntities"
            WHERE "AccountId" = {account.Id} AND "Kind" = 'week_plan' AND "PurgedAt" IS NULL
              AND "Groups" -> 'create' -> 'values' ->> 'ProfileId' = {profile}
              AND "Groups" -> 'state' -> 'values' ->> 'State' = '1'
              AND "Groups" -> 'create' -> 'values' ->> 'WeekStartDate' BETWEEN {earliest} AND {day}
            ORDER BY "Groups" -> 'create' -> 'values' ->> 'WeekStartDate' DESC, "EntityKey" LIMIT 1
            """).AsNoTracking().FirstOrDefaultAsync(ct);
        var revision = week is null ? null : new WebEntity(week).Number("state", "ConfirmedRevision");
        if (week is null || revision is not > 0)
        {
            return new WebDayPlan(planKey, false, null, null, []);
        }

        var assignments = await db.SyncEntities.FromSqlInterpolated($"""
            SELECT * FROM "SyncEntities"
            WHERE "AccountId" = {account.Id} AND "Kind" = 'week_assignment' AND "PurgedAt" IS NULL
              AND "Groups" -> 'create' -> 'values' ->> 'WeekPlanId' = {week.EntityKey}
              AND "Groups" -> 'placement' -> 'values' ->> 'LocalDate' = {day}
            ORDER BY "EntityKey"
            """).AsNoTracking().ToListAsync(ct);
        var taskKeys = assignments.Select(row => new WebEntity(row)).OrderBy(item => item.Number("placement", "Position") ?? 0)
            .Select(item => item.Id("create", "TaskId")?.ToString("D")).OfType<string>().Distinct().ToArray();
        var open = (await TasksByKeyAsync(account, taskKeys, ct))
            .Where(task => !task.IsTerminal).Select(task => task.Id.ToString("D")).ToHashSet();
        var members = taskKeys.Where(open.Contains).Select((key, index) =>
            new WebDayMember(NameBasedId.Create(NameBasedId.Names.DayPlanItem(planKey, key)).ToString("D"), key, index * 1024L, null));
        return new WebDayPlan(planKey, false, week.EntityKey, revision, [.. members]);
    }

    private IQueryable<SyncEntityRow> Rows(Guid accountId, string kind) =>
        db.SyncEntities.AsNoTracking().Where(e => e.AccountId == accountId && e.Kind == kind && e.PurgedAt == null);

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
                entity.Number("priority", "Priority") ?? 0, entity.Number("position", "Position") ?? 0,
                entity.Time("planned", "PlannedTime"), entity.Text("planned", "PlannedZoneId"),
                entity.Time("due", "DueTime"), entity.Text("due", "DueZoneId"))
            : null;

    private static bool InPersonalSpace(WebEntity entity, Account account) =>
        account.PersonalSpaceId is { } spaceId && entity.Id("create", "SpaceId") == spaceId;

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

    public TimeOnly? Time(string group, string column) =>
        TimeOnly.TryParseExact(Text(group, column), "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    private object? Value(string group, string column) => Groups.GetValueOrDefault(group)?.Values.Values.GetValueOrDefault(column);
}
