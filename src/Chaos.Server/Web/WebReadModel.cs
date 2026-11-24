using System.Globalization;
using System.Text.Json;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Web;

internal sealed record WebList(Guid SpaceId, Guid Id, string Name, bool IsInbox, long Position, bool IsArchived);

/// <summary>
/// The lists of one space, as the picker groups them: Personal first, then each shared space the caller belongs to.
/// <see cref="Role"/> is the caller's role there (1 owner, 2 editor, 3 viewer); Personal has none.
/// </summary>
internal sealed record WebSpace(Guid Id, string Name, bool IsShared, short? Role, bool CanChange, string? ColorKey, string? Icon,
    IReadOnlyList<WebList> Lists);

internal sealed record WebLists(IReadOnlyList<WebSpace> Spaces);

/// <summary>A member named on a task, as an assignee. <see cref="IsMe"/> is the signed-in person.</summary>
internal sealed record WebPerson(Guid MemberId, string Name, bool IsMe);

/// <summary>
/// A diary entry of a task: an update or a comment, and who wrote it. An entry an owner removed keeps its place with
/// no text (<see cref="RemovedByOwner"/>); the author's own deletion removes it.
/// </summary>
internal sealed record WebUpdate(Guid Id, string Markdown, string Author, bool IsMine, DateTimeOffset At, bool RemovedByOwner);

internal sealed record WebTask(Guid Id, Guid ListId, string Title, string? Notes, long Status, DateOnly? PlannedDate,
    DateOnly? DueDate, bool IsRecurring, long Priority, long Position, TimeOnly? PlannedTime, string? PlannedZoneId,
    TimeOnly? DueTime, string? DueZoneId)
{
    /// <summary>The space the task belongs to: the Personal space, or a shared space.</summary>
    public Guid SpaceId { get; init; }

    public bool IsOnMyDay { get; init; }

    /// <summary>False in a space where the caller is a viewer: the service refuses changes to the task there.</summary>
    public bool CanChange { get; init; }

    public IReadOnlyList<WebPerson> Assignees { get; init; } = [];

    /// <summary>The task's diary. Only <c>GET /tasks/{id}</c> fills it; rows in list and My Day responses leave it empty.</summary>
    public IReadOnlyList<WebUpdate> Updates { get; init; } = [];

    public bool IsTerminal => Status is WebReadModel.Done or WebReadModel.Cancelled;
}
internal sealed record WebNote(Guid Id, string Markdown, long Kind, long Position, bool IsArchived)
{
    /// <summary>For a drawing item (Kind 1): the stored strokes JSON and the canvas size it was drawn on.</summary>
    public string? Strokes { get; init; }
    public long? DrawingWidth { get; init; }
    public long? DrawingHeight { get; init; }
}
internal sealed record WebPage(Guid Id, string Title, string Markdown, Guid? ParentId, DateOnly? DailyDate, long Position);
internal sealed record WebAttachment(Guid StreamId, string Sha256, string MediaType);
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

/// <summary>A shared space the caller belongs to, with the caller's role there.</summary>
internal sealed record SpaceAccess(Guid Id, string Name, short Role, string? ColorKey, string? Icon)
{
    /// <summary>Owner and Editor roles may change a space's tasks; a Viewer may only read and comment (PLAN3 8.4).</summary>
    public bool CanChange => Role is SpaceMember.OwnerRole or SpaceMember.EditorRole;
}

/// <summary>
/// What a web session may read: its account stream, and the stream of every space where the account is a live member
/// (<see cref="SpaceMembershipCache"/>). It is built for each request from the account's membership version, so a removed
/// member's next request no longer sees the space.
/// </summary>
internal sealed record ReadScope(Account Account, IReadOnlyList<SpaceAccess> Spaces)
{
    private IReadOnlyDictionary<Guid, SpaceAccess> ById { get; } = Spaces.ToDictionary(space => space.Id);

    public Guid AccountId => Account.Id;

    /// <summary>The account stream and every member space stream.</summary>
    public Guid[] StreamIds => [.. Spaces.Select(space => space.Id).Append(Account.Id)];

    public SpaceAccess? Space(Guid id) => ById.GetValueOrDefault(id);

    /// <summary>Task changes need the Editor role in a shared space. The Personal space is the caller's own.</summary>
    public bool CanChange(Guid spaceId) => spaceId == Account.PersonalSpaceId || ById.GetValueOrDefault(spaceId)?.CanChange == true;

    /// <summary>
    /// A row is readable when it sits in one of the member space streams, or in the account stream with the Personal space
    /// as its own. A moved row's account-stream tombstone has no groups, so it fails this like any row without a space.
    /// </summary>
    public bool IsReadable(Guid streamId, WebEntity entity) =>
        streamId != Account.Id || (Account.PersonalSpaceId is { } spaceId && entity.Id("create", "SpaceId") == spaceId);

    /// <summary>The space a list or task stored in <paramref name="streamId"/> belongs to.</summary>
    public Guid SpaceOf(Guid streamId, WebEntity entity) =>
        streamId == Account.Id ? entity.Id("create", "SpaceId") ?? Guid.Empty : streamId;

    /// <summary>The stream that holds a space's entities: the account stream for the Personal space, the space stream otherwise.</summary>
    public Guid StreamOf(Guid spaceId) => spaceId == Account.PersonalSpaceId ? Account.Id : spaceId;
}

/// <summary>Projects the sync groups of the caller's readable streams into the mobile web views.</summary>
internal sealed class WebReadModel(ChaosDbContext db, SpaceMembershipCache memberships, TimeProvider clock)
{
    internal const int NotStarted = 0;
    internal const int Done = 4;
    internal const int Cancelled = 5;

    public Task<Account?> AccountAsync(Guid accountId, CancellationToken ct) =>
        db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId && a.DeletedAt == null, ct);

    /// <summary>The account with its live shared spaces, each with its name and look, ordered by name.</summary>
    public async Task<ReadScope?> ScopeAsync(Guid accountId, CancellationToken ct)
    {
        if (await AccountAsync(accountId, ct) is not { } account)
        {
            return null;
        }

        var membership = await memberships.GetAsync(db, account.Id, ct);
        var ids = membership.Roles.Keys.ToArray();
        var rows = await Rows([.. ids, account.Id], "space").ToListAsync(ct);
        var spaces = ids.Select(id =>
        {
            var entity = rows.Where(row => row.AccountId == id && row.EntityKey == id.ToString("D")).Select(row => new WebEntity(row))
                .FirstOrDefault();
            return new SpaceAccess(id, entity?.Text("name", "Name") is { Length: > 0 } name ? name : "Shared space",
                membership.Roles[id], entity?.Text("look", "ColorKey"), entity?.Text("look", "Icon"));
        });
        return new ReadScope(account, [.. spaces.OrderBy(space => space.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(space => space.Id)]);
    }

    public async Task<IReadOnlyList<WebNote>> PadAsync(Account account, CancellationToken ct)
    {
        var rows = await Rows(account.Id, "pad").ToListAsync(ct);
        var drawings = (await Rows(account.Id, "pad_drawing").ToListAsync(ct))
            .ToDictionary(row => row.EntityKey, row => new WebEntity(row));
        return rows.Select(row => (Row: row, Entity: new WebEntity(row)))
            .Where(item => !item.Entity.IsDeleted && InPersonalSpace(item.Entity, account) && Guid.TryParse(item.Row.EntityKey, out _))
            .Select(item =>
            {
                var kind = item.Entity.Number("create", "Kind") ?? 0;
                var drawing = kind == 1 ? drawings.GetValueOrDefault(item.Row.EntityKey) : null;
                return new WebNote(Guid.Parse(item.Row.EntityKey), item.Entity.Text("text", "RawMarkdown") ?? "",
                    kind, item.Entity.Number("position", "Position") ?? 0,
                    item.Entity.Text("archived", "ArchivedAtUtc") is not null)
                {
                    Strokes = drawing?.Text("strokes", "StrokesJson"),
                    DrawingWidth = drawing?.Number("strokes", "Width"),
                    DrawingHeight = drawing?.Number("strokes", "Height"),
                };
            })
            .OrderBy(note => note.IsArchived).ThenBy(note => note.Position).ThenBy(note => note.Id).ToList();
    }

    /// <summary>
    /// An image attachment in a stream the caller can read: its stream, blob address and media type. Anything else is not
    /// served to the PWA.
    /// </summary>
    public async Task<WebAttachment?> ImageAttachmentAsync(ReadScope scope, Guid id, CancellationToken ct)
    {
        var key = id.ToString("D");
        var row = await db.SyncEntities.AsNoTracking()
            .FirstOrDefaultAsync(e => scope.StreamIds.Contains(e.AccountId) && e.Kind == "attachment" && e.EntityKey == key
                && e.PurgedAt == null && e.MovedTo == null, ct);
        if (row is null)
        {
            return null;
        }

        var entity = new WebEntity(row);
        if (entity.IsDeleted || !scope.IsReadable(row.AccountId, entity)
            || entity.Text("file", "Sha256") is not { Length: > 0 } sha || entity.Text("file", "MediaType") is not { } mediaType
            || !ImageTypes.Contains(mediaType))
        {
            return null;
        }

        return new WebAttachment(row.AccountId, sha, mediaType);
    }

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/gif", "image/webp" };

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

    /// <summary>Tasks from every stream the caller can read. Pad notes and pages stay the Personal ones: the PWA shows no shared pad.</summary>
    public async Task<WebSearchResult> SearchAsync(ReadScope scope, string query, CancellationToken ct)
    {
        if (scope.Account.PersonalSpaceId is null)
        {
            return new([], false);
        }

        var streams = scope.StreamIds;
        var rows = await db.SyncEntities.FromSqlInterpolated($"""
            SELECT "StreamId", "Kind", "EntityKey", "Groups", "PurgedAt", "MovedTo", "LastSeq", "UpdatedAt"
            FROM "SyncEntities"
            WHERE "StreamId" = ANY({streams}) AND "Kind" IN ('task', 'pad', 'page') AND "PurgedAt" IS NULL AND "MovedTo" IS NULL
              AND "Groups" -> 'deleted' -> 'values' ->> 'DeletedAtUtc' IS NULL
              AND (strpos(lower(coalesce("Groups" -> 'title' -> 'values' ->> 'Title', '')), lower({query})) > 0
                OR strpos(lower(coalesce("Groups" -> 'notes' -> 'values' ->> 'Notes', '')), lower({query})) > 0
                OR strpos(lower(coalesce("Groups" -> 'text' -> 'values' ->> 'RawMarkdown', '')), lower({query})) > 0)
            ORDER BY "StreamId", "Kind", "EntityKey"
            """).AsNoTracking().ToListAsync(ct);
        var lists = (await ListsAsync(scope, ct)).Spaces.SelectMany(space => space.Lists).ToDictionary(list => list.Id);
        var results = new List<WebSearchItem>();
        foreach (var row in rows)
        {
            var entity = new WebEntity(row);
            var personal = row.AccountId == scope.AccountId && InPersonalSpace(entity, scope.Account);
            if (entity.IsDeleted || !(row.Kind == "task" ? scope.IsReadable(row.AccountId, entity) : personal)
                || !Guid.TryParse(row.EntityKey, out var id))
            {
                continue;
            }

            var text = entity.Text("text", "RawMarkdown") ?? "";
            if (row.Kind == "task")
            {
                var task = ReadTask(row.AccountId, row.EntityKey, entity, scope, includeNotes: true);
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

    public async Task<WebToday> TodayAsync(ReadScope scope, CancellationToken ct)
    {
        var (today, zone) = await LocalDayAsync(scope.Account, clock.GetUtcNow(), ct);
        var meetings = await MeetingsAsync(scope.Account, today, zone, ct);
        var plan = await DayPlanAsync(scope, today, ct);
        var members = plan?.Members.Where(item => item.IsActive).ToArray() ?? [];
        if (members.Length == 0)
        {
            return new WebToday(today, [], meetings);
        }

        var lists = (await ListsAsync(scope, ct)).Spaces.SelectMany(space => space.Lists).Select(list => list.Id).ToHashSet();
        var tasks = (await TasksByKeyAsync(scope, members.Select(member => member.TaskKey).Distinct().ToArray(), ct))
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

    /// <summary>The task with its diary, assignees and My Day membership, or null when the caller cannot read it or its list.</summary>
    public async Task<WebTask?> TaskDetailAsync(ReadScope scope, Guid taskId, CancellationToken ct)
    {
        if (await TaskStateAsync(scope, taskId, ct) is not { } state)
        {
            return null;
        }

        var (today, _) = await LocalDayAsync(scope.Account, clock.GetUtcNow(), ct);
        var plan = await DayPlanAsync(scope, today, ct);
        var assignees = state.StreamId == scope.AccountId ? [] :
            (await AssigneesAsync(scope, state.StreamId, [taskId], ct)).GetValueOrDefault(taskId) ?? [];
        return state.Task with
        {
            IsOnMyDay = plan?.Members.Any(item => item.IsActive && item.TaskKey == taskId.ToString("D")) == true,
            Assignees = assignees,
            Updates = await UpdatesAsync(scope, state.StreamId, taskId, ct),
        };
    }

    private async Task<IEnumerable<WebTask>> TasksByKeyAsync(ReadScope scope, string[] keys, CancellationToken ct)
    {
        var streams = scope.StreamIds;
        var rows = await db.SyncEntities.FromSqlInterpolated($"""
            SELECT "StreamId", "Kind", "EntityKey", "Groups" - 'notes' AS "Groups", "PurgedAt", "MovedTo", "LastSeq", "UpdatedAt"
            FROM "SyncEntities"
            WHERE "StreamId" = ANY({streams}) AND "Kind" = 'task' AND "PurgedAt" IS NULL AND "MovedTo" IS NULL
              AND "EntityKey" = ANY({keys})
            """).AsNoTracking().ToListAsync(ct);
        return rows.Select(row => ReadTask(row.AccountId, row.EntityKey, new WebEntity(row), scope, includeNotes: false)).OfType<WebTask>();
    }

    /// <summary>Every list the caller can read, grouped by space: Personal first, then each shared space by name.</summary>
    public async Task<WebLists> ListsAsync(ReadScope scope, CancellationToken ct)
    {
        var rows = await Rows(scope.StreamIds, "container").ToListAsync(ct);
        var lists = new Dictionary<Guid, List<WebList>>();
        foreach (var row in rows)
        {
            var list = ReadList(row, scope);
            if (list is null) continue;
            if (!lists.TryGetValue(row.AccountId, out var group)) lists[row.AccountId] = group = [];
            group.Add(list);
        }

        var personalName = scope.Account.PersonalSpaceId is { } personal
            ? new WebSpace(personal, await PersonalSpaceName(scope.Account, personal, ct), false, null, true, null, null,
                Ordered(lists.GetValueOrDefault(scope.AccountId)))
            : null;
        return new WebLists([.. personalName is null ? [] : new[] { personalName },
            .. scope.Spaces.Select(space => new WebSpace(space.Id, space.Name, true, space.Role, space.CanChange, space.ColorKey, space.Icon,
                Ordered(lists.GetValueOrDefault(space.Id))))]);

        static IReadOnlyList<WebList> Ordered(List<WebList>? group) =>
            group is null ? [] : [.. group.OrderByDescending(list => list.IsInbox).ThenBy(list => list.Position).ThenBy(list => list.Id)];
    }

    private async Task<string> PersonalSpaceName(Account account, Guid personal, CancellationToken ct)
    {
        var key = personal.ToString("D");
        var row = await Rows(account.Id, "space").FirstOrDefaultAsync(e => e.EntityKey == key, ct);
        var name = row is null ? null : new WebEntity(row).Text("name", "Name");
        return name is { Length: > 0 } ? name : "Personal";
    }

    public async Task<WebList?> ListAsync(ReadScope scope, Guid listId, CancellationToken ct)
    {
        var key = listId.ToString("D");
        var streams = scope.StreamIds;
        var row = await db.SyncEntities.AsNoTracking()
            .FirstOrDefaultAsync(e => streams.Contains(e.AccountId) && e.Kind == "container" && e.EntityKey == key && e.PurgedAt == null && e.MovedTo == null, ct);
        return row is null ? null : ReadList(row, scope);
    }

    public async Task<IReadOnlyList<WebTask>> TasksInListAsync(ReadScope scope, Guid listId, CancellationToken ct)
    {
        var key = listId.ToString("D");
        var streams = scope.StreamIds;
        var rows = await db.SyncEntities.FromSqlInterpolated($"""
            SELECT "StreamId", "Kind", "EntityKey", "Groups" - 'notes' AS "Groups", "PurgedAt", "MovedTo", "LastSeq", "UpdatedAt"
            FROM "SyncEntities"
            WHERE "StreamId" = ANY({streams}) AND "Kind" = 'task' AND "PurgedAt" IS NULL AND "MovedTo" IS NULL
              AND "Groups" -> 'deleted' -> 'values' ->> 'DeletedAtUtc' IS NULL
              AND "Groups" -> 'place' -> 'values' ->> 'ContainerId' = {key}
            """).AsNoTracking().ToListAsync(ct);
        var tasks = rows.Select(row => ReadTask(row.AccountId, row.EntityKey, new WebEntity(row), scope, includeNotes: false))
            .OfType<WebTask>().OrderBy(task => task.Position).ThenBy(task => task.Id).ToList();
        return [.. TasksWithAssignees(scope, tasks, await AssigneesAsync(scope, tasks, ct))];
    }

    /// <summary>The task as stored, with its entity and stream, or null when the caller cannot read it or its list.</summary>
    public async Task<(WebTask Task, WebEntity Entity, Guid StreamId)?> TaskStateAsync(ReadScope scope, Guid taskId, CancellationToken ct)
    {
        var key = taskId.ToString("D");
        var streams = scope.StreamIds;
        var row = await db.SyncEntities.AsNoTracking()
            .FirstOrDefaultAsync(e => streams.Contains(e.AccountId) && e.Kind == "task" && e.EntityKey == key && e.PurgedAt == null && e.MovedTo == null, ct);
        if (row is null)
        {
            return null;
        }

        var entity = new WebEntity(row);
        var task = ReadTask(row.AccountId, key, entity, scope, includeNotes: true);
        return task is not null && await ListAsync(scope, task.ListId, ct) is not null ? (task, entity, row.AccountId) : null;
    }

    /// <summary>
    /// The live assignees of tasks in a shared space, by task. Personal tasks have none: assignment exists only in shared
    /// spaces (PLAN3 9.3). Names come from the server's member rows, so a member who left still shows by name.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<WebPerson>>> AssigneesAsync(ReadScope scope, Guid spaceId,
        IReadOnlyCollection<Guid> taskIds, CancellationToken ct)
    {
        var keys = taskIds.Select(id => id.ToString("D")).ToArray();
        var rows = await db.SyncEntities.FromSqlInterpolated($"""
            SELECT "StreamId", "Kind", "EntityKey", "Groups", "PurgedAt", "MovedTo", "LastSeq", "UpdatedAt"
            FROM "SyncEntities"
            WHERE "StreamId" = {spaceId} AND "Kind" = 'task_assignee' AND "PurgedAt" IS NULL AND "MovedTo" IS NULL
              AND split_part("EntityKey", '|', 1) = ANY({keys})
            """).AsNoTracking().ToListAsync(ct);
        if (rows.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<WebPerson>>();
        }

        var members = (await db.SpaceMembers.AsNoTracking().Where(m => m.SpaceId == spaceId).ToListAsync(ct))
            .ToDictionary(member => member.MemberId);
        var byTask = rows.Select(row => (Parts: row.EntityKey.Split('|'), Entity: new WebEntity(row)))
            .Select(item => (TaskId: Guid.TryParse(item.Parts.ElementAtOrDefault(0), out var taskId) ? taskId : (Guid?)null,
                MemberId: Guid.TryParse(item.Parts.ElementAtOrDefault(1), out var memberId) ? memberId : (Guid?)null, item.Entity))
            .Where(item => item.TaskId is not null && item.MemberId is not null && !item.Entity.IsDeleted)
            .GroupBy(item => item.TaskId!.Value);
        var result = new Dictionary<Guid, IReadOnlyList<WebPerson>>();
        foreach (var group in byTask)
        {
            result[group.Key] = [.. group.Select(item => Person(scope, members.GetValueOrDefault(item.MemberId!.Value)))
                .OrderBy(person => members.GetValueOrDefault(person.MemberId)?.JoinedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(person => person.MemberId)];
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<WebPerson>>> AssigneesAsync(ReadScope scope,
        IReadOnlyCollection<WebTask> tasks, CancellationToken ct)
    {
        var result = new Dictionary<Guid, IReadOnlyList<WebPerson>>();
        foreach (var group in tasks.Where(task => task.SpaceId != scope.Account.PersonalSpaceId).GroupBy(task => task.SpaceId))
        {
            foreach (var pair in await AssigneesAsync(scope, group.Key, [.. group.Select(task => task.Id)], ct))
            {
                result[pair.Key] = pair.Value;
            }
        }

        return result;
    }

    private static IEnumerable<WebTask> TasksWithAssignees(ReadScope scope, IReadOnlyList<WebTask> tasks,
        IReadOnlyDictionary<Guid, IReadOnlyList<WebPerson>> assignees) =>
        tasks.Select(task => assignees.TryGetValue(task.Id, out var people) ? task with { Assignees = people } : task);

    private static WebPerson Person(ReadScope scope, SpaceMember? member) =>
        new(member?.MemberId ?? Guid.Empty, member?.DisplayName is { Length: > 0 } name ? name : "Unknown person",
            member is not null && scope.Account.ProfileId == member.MemberId);

    /// <summary>The task's diary with each entry's author. The author's own deletion removes an entry; an owner's removal keeps a placeholder.</summary>
    private async Task<IReadOnlyList<WebUpdate>> UpdatesAsync(ReadScope scope, Guid streamId, Guid taskId, CancellationToken ct)
    {
        var key = taskId.ToString("D");
        var rows = await db.SyncEntities.FromSqlInterpolated($"""
            SELECT "StreamId", "Kind", "EntityKey", "Groups", "PurgedAt", "MovedTo", "LastSeq", "UpdatedAt"
            FROM "SyncEntities"
            WHERE "StreamId" = {streamId} AND "Kind" = 'diary_entry' AND "PurgedAt" IS NULL AND "MovedTo" IS NULL
              AND "Groups" -> 'create' -> 'values' ->> 'TaskId' = {key}
            """).AsNoTracking().ToListAsync(ct);
        if (rows.Count == 0)
        {
            return [];
        }

        var members = streamId == scope.AccountId ? new Dictionary<Guid, SpaceMember>()
            : (await db.SpaceMembers.AsNoTracking().Where(m => m.SpaceId == streamId).ToListAsync(ct)).ToDictionary(member => member.MemberId);
        var updates = new List<WebUpdate>();
        foreach (var row in rows)
        {
            var entity = new WebEntity(row);
            var author = entity.Id("create", "AuthorProfileId");
            var mine = author is { } authorId && authorId == scope.Account.ProfileId;
            var removed = entity.Text("deleted", "DeletedAtUtc") is not null;
            if (removed && entity.Number("deleted", "RemovedByOwner") != 1) continue;
            var name = mine ? "You" : author is { } id && members.TryGetValue(id, out var member) ? member.DisplayName : null;
            updates.Add(new WebUpdate(Guid.TryParse(row.EntityKey, out var entry) ? entry : Guid.Empty,
                removed ? "" : entity.Text("entry", "RawMarkdown") ?? "", name is { Length: > 0 } ? name : "Unknown person", mine,
                entity.Instant("create", "EntryAtUtc") ?? entity.Instant("create", "CreatedAtUtc") ?? row.UpdatedAt, removed));
        }

        return [.. updates.OrderBy(update => update.At).ThenBy(update => update.Id)];
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

    /// <summary>The day plan, or the confirmed week's open assignments for the day before the plan exists. Week assignments are personal overlays that may name a shared task.</summary>
    public async Task<WebDayPlan?> DayPlanAsync(ReadScope scope, DateOnly date, CancellationToken ct)
    {
        var account = scope.Account;
        if (account.ProfileId is not { } profileId)
        {
            return null;
        }

        var profile = profileId.ToString("D");
        var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var plan = await db.SyncEntities.FromSqlInterpolated($"""
            SELECT * FROM "SyncEntities"
            WHERE "StreamId" = {account.Id} AND "Kind" = 'day_plan' AND "PurgedAt" IS NULL
              AND "Groups" -> 'create' -> 'values' ->> 'ProfileId' = {profile}
              AND "Groups" -> 'create' -> 'values' ->> 'LocalDate' = {day}
            ORDER BY "EntityKey" LIMIT 1
            """).AsNoTracking().FirstOrDefaultAsync(ct);

        if (plan is null)
        {
            return await WeekSeedAsync(scope, profile, date, day, ct);
        }

        var rows = await db.SyncEntities.FromSqlInterpolated($"""
            SELECT * FROM "SyncEntities"
            WHERE "StreamId" = {account.Id} AND "Kind" = 'day_plan_item' AND "PurgedAt" IS NULL
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

    private async Task<WebDayPlan> WeekSeedAsync(ReadScope scope, string profile, DateOnly date, string day, CancellationToken ct)
    {
        var account = scope.Account;
        var planKey = NameBasedId.Create(NameBasedId.Names.DayPlan(profile, date)).ToString("D");
        var earliest = date.AddDays(-Math.Min(6, date.DayNumber)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var week = await db.SyncEntities.FromSqlInterpolated($"""
            SELECT * FROM "SyncEntities"
            WHERE "StreamId" = {account.Id} AND "Kind" = 'week_plan' AND "PurgedAt" IS NULL
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
            WHERE "StreamId" = {account.Id} AND "Kind" = 'week_assignment' AND "PurgedAt" IS NULL
              AND "Groups" -> 'create' -> 'values' ->> 'WeekPlanId' = {week.EntityKey}
              AND "Groups" -> 'placement' -> 'values' ->> 'LocalDate' = {day}
            ORDER BY "EntityKey"
            """).AsNoTracking().ToListAsync(ct);
        var taskKeys = assignments.Select(row => new WebEntity(row)).OrderBy(item => item.Number("placement", "Position") ?? 0)
            .Select(item => item.Id("create", "TaskId")?.ToString("D")).OfType<string>().Distinct().ToArray();
        var open = (await TasksByKeyAsync(scope, taskKeys, ct))
            .Where(task => !task.IsTerminal).Select(task => task.Id.ToString("D")).ToHashSet();
        var members = taskKeys.Where(open.Contains).Select((key, index) =>
            new WebDayMember(NameBasedId.Create(NameBasedId.Names.DayPlanItem(planKey, key)).ToString("D"), key, index * 1024L, null));
        return new WebDayPlan(planKey, false, week.EntityKey, revision, [.. members]);
    }

    private IQueryable<SyncEntityRow> Rows(Guid accountId, string kind) =>
        db.SyncEntities.AsNoTracking().Where(e => e.AccountId == accountId && e.Kind == kind && e.PurgedAt == null);

    private IQueryable<SyncEntityRow> Rows(Guid[] streams, string kind) =>
        db.SyncEntities.AsNoTracking().Where(e => streams.Contains(e.AccountId) && e.Kind == kind && e.PurgedAt == null && e.MovedTo == null);

    private static WebList? ReadList(SyncEntityRow row, ReadScope scope)
    {
        var entity = new WebEntity(row);
        return !entity.IsDeleted && scope.IsReadable(row.AccountId, entity) && Guid.TryParse(row.EntityKey, out var id)
            && entity.Text("name", "Name") is { Length: > 0 } name
            ? new WebList(scope.SpaceOf(row.AccountId, entity), id, name, id == scope.Account.InboxId, entity.Number("position", "Position") ?? 0,
                entity.Text("archived", "ArchivedAtUtc") is not null)
            : null;
    }

    private static WebTask? ReadTask(Guid streamId, string key, WebEntity entity, ReadScope scope, bool includeNotes) =>
        !entity.IsDeleted && scope.IsReadable(streamId, entity) && Guid.TryParse(key, out var id) &&
        entity.Id("place", "ContainerId") is { } listId && entity.Text("title", "Title") is { Length: > 0 } title
            ? new WebTask(id, listId, title, includeNotes ? entity.Text("notes", "Notes") : null,
                entity.Number("status", "Status") ?? NotStarted, entity.Date("planned", "PlannedDate"), entity.Date("due", "DueDate"),
                entity.Text("create", "RecurrenceOccurrenceKey") is not null,
                entity.Number("priority", "Priority") ?? 0, entity.Number("position", "Position") ?? 0,
                entity.Time("planned", "PlannedTime"), entity.Text("planned", "PlannedZoneId"),
                entity.Time("due", "DueTime"), entity.Text("due", "DueZoneId"))
            {
                SpaceId = scope.SpaceOf(streamId, entity),
                CanChange = scope.CanChange(scope.SpaceOf(streamId, entity)),
            }
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

    public DateTimeOffset? Instant(string group, string column) =>
        DateTimeOffset.TryParse(Text(group, column), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant) ? instant : null;

    private object? Value(string group, string column) => Groups.GetValueOrDefault(group)?.Values.Values.GetValueOrDefault(column);
}
