using System.Globalization;
using System.Security.Claims;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Web;

internal sealed record WebCaptureRequest(string? Title, Guid? ListId, DateOnly? PlannedDate, Guid? RequestId);

internal enum WebTaskAction
{
    Complete,
    Reopen,
    PlanToday,
    AddToMyDay,
    RemoveFromMyDay,
}

/// <summary>
/// The PWA's task writes. Each becomes ordinary sync changes, pushed through <see cref="SyncService"/> with protocol 2, so a
/// task in a shared space is routed to its space stream and checked against the member's role like any device's change
/// (PLAN3 8.4). My Day membership is a personal overlay in the account stream whatever the task's space.
/// </summary>
internal sealed class WebTaskWrites(ChaosDbContext db, WebReadModel read, SyncService sync, ServerChangeWriter changes, TimeProvider time)
{
    public Task<IResult> CaptureAsync(WebCaller caller, WebCaptureRequest request, CancellationToken ct)
    {
        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.EnumerateRunes().Count() > 500 || request.RequestId == Guid.Empty)
        {
            return Task.FromResult(Results.Problem("Enter a task title of up to 500 characters.", statusCode: StatusCodes.Status400BadRequest));
        }

        var id = request.RequestId ?? Guid.NewGuid();
        // The task ID doubles as the browser's idempotency key for an add with a lost response.
        var changeId = NameBasedId.Create($"web-task:{id:D}").ToString("D");
        return PushAsync(caller, id, async (scope, token) =>
        {
            var key = id.ToString("D");
            var streams = scope.StreamIds;
            var existing = await db.SyncEntities.AsNoTracking()
                .Where(e => streams.Contains(e.AccountId) && e.Kind == "task" && e.EntityKey == key).Take(2).ToListAsync(token);
            if (existing.Count > 0)
            {
                var entity = existing.Count == 1 ? new WebEntity(existing[0]) : null;
                return entity is not null && existing[0].PurgedAt is null && !entity.IsDeleted && scope.IsReadable(existing[0].AccountId, entity)
                    && await db.SyncChanges.AnyAsync(row => streams.Contains(row.AccountId) && row.ChangeId == Guid.Parse(changeId), token)
                    ? new PreparedPush([])
                    : Refuse("task-id-in-use");
            }

            if ((request.ListId ?? scope.Account.InboxId) is not { } listId)
            {
                return Refuse("setup-required");
            }

            if (await read.ListAsync(scope, listId, token) is not { IsArchived: false } list)
            {
                return Refuse(request.ListId is null ? "setup-required" : "not-found");
            }

            if (!scope.CanChange(list.SpaceId))
            {
                return Refuse(SyncRejection.Forbidden);
            }

            var tasks = await read.TasksInListAsync(scope, listId, token);
            var lastPosition = tasks.Select(task => task.Position).DefaultIfEmpty(-1024).Max();
            var position = lastPosition <= long.MaxValue - 1024 ? Math.Max(0, lastPosition + 1024) : lastPosition;
            var now = time.GetUtcNow();
            var hlc = await changes.NextHlcAsync(scope.StreamOf(list.SpaceId), "task", key, token);
            return Change(changeId, id, new Dictionary<string, SyncGroupState>
            {
                ["create"] = Group(hlc, ("SpaceId", list.SpaceId.ToString("D")), ("CreatedAtUtc", Instant(now)), ("RecurrenceOccurrenceKey", null)),
                ["title"] = Group(hlc, ("Title", title)),
                ["notes"] = Group(hlc, ("Notes", string.Empty)),
                ["place"] = Group(hlc, ("ContainerId", listId.ToString("D")), ("SectionId", null)),
                ["status"] = Status(hlc, WebReadModel.NotStarted, now),
                ["position"] = Group(hlc, ("Position", position)),
                ["priority"] = Group(hlc, ("Priority", 2L)),
                ["estimate"] = Group(hlc, ("EstimateMinutes", null)),
                ["category"] = Group(hlc, ("CapacityCategory", 0L)),
                ["planned"] = Planned(hlc, request.PlannedDate),
                ["due"] = Group(hlc, ("DueDate", null), ("DueTime", null), ("DueZoneId", null)),
                ["deleted"] = Group(hlc, ("DeletedAtUtc", null)),
            });
        }, ct);
    }

    public Task<IResult> ActAsync(WebCaller caller, Guid id, WebTaskAction action, CancellationToken ct)
    {
        var changeId = Guid.NewGuid().ToString("D");
        return PushAsync(caller, id, async (scope, token) =>
        {
            if (await read.TaskStateAsync(scope, id, token) is not { } state)
            {
                return Refuse("not-found");
            }

            var (task, entity, streamId) = state;
            if (task.IsRecurring && action is WebTaskAction.Complete or WebTaskAction.Reopen)
            {
                return Refuse("recurring");
            }

            // The rule that every live assignee completes first runs on the desktop, and this client schema cannot write
            // a person's `done` group, so a status written here would make the task Done while others have not completed.
            if (task.EachMustComplete && action == WebTaskAction.Complete)
            {
                return Refuse("each");
            }

            if ((action is WebTaskAction.Complete && task.Status == WebReadModel.Done) ||
                (action is WebTaskAction.Reopen && !task.IsTerminal))
            {
                return new PreparedPush([]);
            }

            if (action is not WebTaskAction.Reopen && task.IsTerminal)
            {
                return Refuse("task-closed");
            }

            // The service refuses a viewer's task change with `forbidden`; saying it here keeps the message plain.
            if (action is not (WebTaskAction.AddToMyDay or WebTaskAction.RemoveFromMyDay) && !task.CanChange)
            {
                return Refuse(SyncRejection.Forbidden);
            }

            var now = time.GetUtcNow();
            var clock = new HybridClock(ServerChangeWriter.DeviceId, time);
            if (action is WebTaskAction.AddToMyDay or WebTaskAction.RemoveFromMyDay)
            {
                return await ChangeMyDayAsync(scope, id, action == WebTaskAction.AddToMyDay, clock, now, token);
            }

            var hlc = await changes.NextHlcAsync(streamId, "task", id.ToString("D"), token);
            if (hlc.WallMillis > Hlc.MaxWallMillis)
            {
                return Refuse("clock-limit");
            }

            if (action is WebTaskAction.PlanToday)
            {
                var (today, _) = await read.LocalDayAsync(scope.Account, now, token);
                if (task.PlannedDate == today && entity.Text("planned", "PlannedTime") is null)
                {
                    return new PreparedPush([]);
                }

                return Change(changeId, id, new Dictionary<string, SyncGroupState> { ["planned"] = Planned(hlc, today) });
            }

            var status = action is WebTaskAction.Complete ? WebReadModel.Done : WebReadModel.NotStarted;
            var statusChange = Change(changeId, id, new Dictionary<string, SyncGroupState> { ["status"] = Status(hlc, status, now) });
            if (action is WebTaskAction.Complete && scope.Account.ProfileId is { } profile)
            {
                var (today, _) = await read.LocalDayAsync(scope.Account, now, token);
                if (await read.DayPlanAsync(scope, today, token) is { Exists: false } plan
                    && plan.Members.Any(item => item.TaskKey == id.ToString("D")))
                {
                    // Preserve the membership of a task completed from a confirmed week's initial day.
                    var changes = InitializeMyDay(plan, profile, today, hlc, now);
                    changes.AddRange(plan.Members.Select(item => MyDayItem(plan, item, 1, removed: false, hlc, now)));
                    return await PrepareMyDayChangesAsync(scope, [.. changes, .. statusChange.Changes], token);
                }
            }

            return statusChange;
        }, ct);
    }

    private async Task<PreparedPush> ChangeMyDayAsync(ReadScope scope, Guid id, bool add, HybridClock clock, DateTimeOffset now, CancellationToken ct)
    {
        var (today, _) = await read.LocalDayAsync(scope.Account, now, ct);
        if (scope.Account.ProfileId is not { } profile || await read.DayPlanAsync(scope, today, ct) is not { } plan)
        {
            return Refuse("setup-required");
        }

        var key = id.ToString("D");
        var member = plan.Members.FirstOrDefault(item => item.TaskKey == key && item.IsActive)
            ?? plan.Members.FirstOrDefault(item => item.TaskKey == key);
        if (add == (member?.IsActive == true))
        {
            return new PreparedPush([]);
        }

        var hlc = member?.Entity?.Groups.Values.MaxBy(group => group.Hlc) is { } last
            ? clock.Receive(last.Hlc).Clock : clock.TickLocal();
        if (hlc.WallMillis > Hlc.MaxWallMillis)
        {
            return Refuse("clock-limit");
        }

        List<IncomingChange> changes = plan.Exists ? [] : InitializeMyDay(plan, profile, today, hlc, now);
        if (!plan.Exists)
        {
            foreach (var seeded in plan.Members.Where(item => item.TaskKey != key))
            {
                changes.Add(MyDayItem(plan, seeded, 1, removed: false, hlc, now));
            }
        }

        if (add)
        {
            var lastPosition = plan.Members.Where(item => item.IsActive).Select(item => item.Position).DefaultIfEmpty(-1024).Max();
            var position = lastPosition <= long.MaxValue - 1024 ? lastPosition + 1024 : lastPosition;
            var itemKey = member?.Key ?? NameBasedId.Create(NameBasedId.Names.DayPlanItem(plan.Key, key)).ToString("D");
            changes.Add(MyDayItem(plan, new WebDayMember(itemKey, key, position, member?.Entity), 0, removed: false, hlc, now));
        }
        else
        {
            foreach (var item in plan.Members.Where(item => item.TaskKey == key && item.IsActive))
            {
                var itemHlc = item.Entity?.Groups.Values.MaxBy(group => group.Hlc) is { } previous
                    ? clock.Receive(previous.Hlc).Clock : hlc;
                if (itemHlc.WallMillis > Hlc.MaxWallMillis)
                {
                    return Refuse("clock-limit");
                }
                changes.Add(MyDayItem(plan, item, 1, removed: true, itemHlc, now));
            }
        }

        return await PrepareMyDayChangesAsync(scope, changes, ct);
    }

    private static List<IncomingChange> InitializeMyDay(WebDayPlan plan, Guid profile, DateOnly today, Hlc hlc, DateTimeOffset now) =>
    [
        Upsert("day_plan", plan.Key, new()
        {
            ["create"] = Group(hlc, ("ProfileId", profile.ToString("D")),
                ("LocalDate", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("CreatedAtUtc", Instant(now))),
            ["seed"] = Group(hlc, ("SeededWeekPlanId", plan.WeekKey), ("SeededWeekRevision", plan.WeekRevision)),
            ["initialized"] = Group(hlc, ("InitializedAtUtc", Instant(now))),
        }),
    ];

    private async Task<PreparedPush> PrepareMyDayChangesAsync(ReadScope scope, IReadOnlyList<IncomingChange> changes, CancellationToken ct)
    {
        var newKeys = changes.Select(change => change.Change!.EntityKey).ToArray();
        var accountId = scope.AccountId;
        if (await db.SyncEntities.AnyAsync(row => row.AccountId == accountId && row.PurgedAt != null
            && (row.Kind == "day_plan" || row.Kind == "day_plan_item") && newKeys.Contains(row.EntityKey), ct))
        {
            return Refuse(SyncRejection.Purged);
        }

        return new PreparedPush(changes);
    }

    private static IncomingChange MyDayItem(WebDayPlan plan, WebDayMember member, long origin, bool removed, Hlc hlc, DateTimeOffset now)
    {
        var groups = new Dictionary<string, SyncGroupState>
        {
            ["removed"] = Group(hlc, ("RemovedAtUtc", removed ? Instant(now) : null)),
        };
        if (!removed || member.Entity is null)
        {
            groups["create"] = Group(hlc, ("DayPlanId", plan.Key), ("TaskId", member.TaskKey), ("Kind", 0L),
                ("Origin", origin), ("AddedAtUtc", Instant(now)),
                ("CreatedAtUtc", member.Entity?.Text("create", "CreatedAtUtc") ?? Instant(now)));
            groups["position"] = Group(hlc, ("Position", member.Position));
        }

        return Upsert("day_plan_item", member.Key, groups);
    }

    private static IncomingChange Upsert(string kind, string key, Dictionary<string, SyncGroupState> groups)
    {
        var id = Guid.NewGuid().ToString("D");
        return new IncomingChange(id, SyncChange.Upsert(id, kind, key, groups), null);
    }

    private async Task<IResult> PushAsync(WebCaller caller, Guid taskId, Func<ReadScope, CancellationToken, Task<PreparedPush>> prepare, CancellationToken ct)
    {
        var result = await sync.PushPreparedAsync(caller.AccountId, caller.DeviceId, WebSession.ClientSchema, SyncProtocol.Version2, async (account, token) =>
        {
            // Like the account lock, hold the device row until the write commits so revocation and a
            // task write have a definite order even if they arrive at different server instances.
            var device = await db.Devices.FromSqlInterpolated($"""
                SELECT * FROM "Devices" WHERE "Id" = {caller.DeviceId} AND "AccountId" = {account.Id}
                  AND "Platform" = {WebSession.Platform} AND "RevokedAt" IS NULL FOR UPDATE
                """).AsNoTracking().FirstOrDefaultAsync(token);
            if (device is null)
            {
                return Refuse("device-unavailable");
            }

            // The scope is read under the account lock the push holds, so a removal that committed before this write is
            // already reflected, and one that comes after waits for it.
            var scope = await read.ScopeAsync(account.Id, token);
            return scope is null ? Refuse("account-unavailable") : await prepare(scope, token);
        }, ct);
        var refusal = result.Refusal ?? result.Outcome?.Rejected.FirstOrDefault()?.Reason;
        if (refusal is not null)
        {
            return refusal switch
            {
                "account-unavailable" or "device-unavailable" => Results.Unauthorized(),
                "not-found" or SyncRejection.Purged or SyncRejection.NotMember => Results.NotFound(),
                "setup-required" => Conflict("Connect Chaos on your desktop and sync your Inbox before adding tasks here."),
                "recurring" => Conflict("Complete or reopen this recurring task on your desktop."),
                "each" => Conflict("Complete your part of this task on your desktop."),
                "task-closed" => Conflict("Reopen this task before updating it."),
                "task-id-in-use" => Conflict("This task ID is already in use."),
                SyncRejection.Forbidden => Results.Problem(ViewerMessage, statusCode: StatusCodes.Status403Forbidden),
                SyncRejection.SchemaTooOld => Conflict("The server requires a newer task format."),
                _ => Conflict("The change could not be synced."),
            };
        }

        var current = await read.ScopeAsync(caller.AccountId, ct);
        return current is not null && await read.TaskDetailAsync(current, taskId, ct) is { } task
            ? Results.Ok(task)
            : Results.NotFound();
    }

    private const string ViewerMessage =
        "You are a viewer of this space, so you can read it but not change it. Ask an owner for editor access.";

    private static IResult Conflict(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status409Conflict);

    private static PreparedPush Refuse(string reason) => new([], reason);

    private static PreparedPush Change(string changeId, Guid taskId, Dictionary<string, SyncGroupState> groups) =>
        new([new IncomingChange(changeId, SyncChange.Upsert(changeId, "task", taskId.ToString("D"), groups), null)]);

    private static string Instant(DateTimeOffset instant) => instant.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    private static SyncGroupState Status(Hlc hlc, int status, DateTimeOffset now) =>
        Group(hlc, ("Status", (long)status), ("CompletedAtUtc", status == WebReadModel.Done ? Instant(now) : null), ("CancelledAtUtc", null));

    private static SyncGroupState Planned(Hlc hlc, DateOnly? date) =>
        Group(hlc, ("PlannedDate", date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("PlannedTime", null), ("PlannedZoneId", null));

    private static SyncGroupState Group(Hlc hlc, params (string Column, object? Value)[] fields) =>
        new(hlc, new SyncGroupValues(fields.ToDictionary(field => field.Column, field => field.Value)));
}

internal static class WebWriteEndpoints
{
    public static void MapWebWrites(this RouteGroupBuilder web)
    {
        web.MapPost("/tasks", (ClaimsPrincipal user, WebCaptureRequest request, WebTaskWrites writes, CancellationToken ct) =>
            writes.CaptureAsync(WebCaller.From(user), request, ct)).AddEndpointFilter<WebAntiforgery>();

        MapAction(web, "complete", WebTaskAction.Complete);
        MapAction(web, "reopen", WebTaskAction.Reopen);
        MapAction(web, "plan-today", WebTaskAction.PlanToday);
        MapAction(web, "add-to-my-day", WebTaskAction.AddToMyDay);
        MapAction(web, "remove-from-my-day", WebTaskAction.RemoveFromMyDay);
    }

    private static void MapAction(RouteGroupBuilder web, string route, WebTaskAction action) =>
        web.MapPost($"/tasks/{{id:guid}}/{route}", (Guid id, ClaimsPrincipal user, WebTaskWrites writes, CancellationToken ct) =>
            writes.ActAsync(WebCaller.From(user), id, action, ct)).AddEndpointFilter<WebAntiforgery>();
}
