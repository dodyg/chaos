using System.Globalization;
using System.Security.Claims;
using System.Text;
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
}

internal sealed class WebTaskWrites(ChaosDbContext db, WebReadModel read, SyncService sync, TimeProvider time)
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
        return PushAsync(caller, id, async (account, token) =>
        {
            var key = id.ToString("D");
            var existing = await db.SyncEntities.AsNoTracking()
                .SingleOrDefaultAsync(e => e.AccountId == account.Id && e.Kind == "task" && e.EntityKey == key, token);
            if (existing is not null)
            {
                var entity = new WebEntity(existing);
                return existing.PurgedAt is null && !entity.IsDeleted &&
                    entity.Groups.GetValueOrDefault("create")?.Hlc.DeviceId == Hlc.ToDevice(caller.DeviceId)
                    ? new PreparedPush([])
                    : Refuse("task-id-in-use");
            }

            if (account.PersonalSpaceId is not { } space || (request.ListId ?? account.InboxId) is not { } listId)
            {
                return Refuse("setup-required");
            }

            if (await read.ListAsync(account, listId, token) is not { IsArchived: false })
            {
                return Refuse(request.ListId is null ? "setup-required" : "not-found");
            }

            var tasks = await read.TasksInListAsync(account, listId, token);
            var lastPosition = tasks.Select(task => task.Position).DefaultIfEmpty(-1024).Max();
            var position = lastPosition <= long.MaxValue - 1024 ? Math.Max(0, lastPosition + 1024) : lastPosition;
            var now = time.GetUtcNow();
            var hlc = new HybridClock(caller.DeviceId, time).TickLocal();
            return Change(changeId, id, new Dictionary<string, SyncGroupState>
            {
                ["create"] = Group(hlc, ("SpaceId", space.ToString("D")), ("CreatedAtUtc", Instant(now)), ("RecurrenceOccurrenceKey", null)),
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
        return PushAsync(caller, id, async (account, token) =>
        {
            if (await read.TaskStateAsync(account, id, token) is not { } state)
            {
                return Refuse("not-found");
            }

            var (task, entity) = state;
            if (task.IsRecurring && action is not WebTaskAction.PlanToday)
            {
                return Refuse("recurring");
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

            var now = time.GetUtcNow();
            var clock = new HybridClock(caller.DeviceId, time);
            var last = entity.Groups.Values.MaxBy(group => group.Hlc)!.Hlc;
            var hlc = clock.Receive(last).Clock;
            if (hlc.WallMillis > Hlc.MaxWallMillis)
            {
                return Refuse("clock-limit");
            }

            if (action is WebTaskAction.PlanToday)
            {
                var (today, _) = await read.LocalDayAsync(account, now, token);
                if (task.PlannedDate == today && entity.Text("planned", "PlannedTime") is null)
                {
                    return new PreparedPush([]);
                }

                return Change(changeId, id, new Dictionary<string, SyncGroupState> { ["planned"] = Planned(hlc, today) });
            }

            var status = action is WebTaskAction.Complete ? WebReadModel.Done : WebReadModel.NotStarted;
            return Change(changeId, id, new Dictionary<string, SyncGroupState> { ["status"] = Status(hlc, status, now) });
        }, ct);
    }

    private async Task<IResult> PushAsync(WebCaller caller, Guid taskId, Func<Account, CancellationToken, Task<PreparedPush>> prepare, CancellationToken ct)
    {
        var result = await sync.PushPreparedAsync(caller.AccountId, caller.DeviceId, WebSession.ClientSchema, async (account, token) =>
        {
            // Like the account lock, hold the device row until the write commits so revocation and a
            // task write have a definite order even if they arrive at different server instances.
            var device = await db.Devices.FromSqlInterpolated($"""
                SELECT * FROM "Devices" WHERE "Id" = {caller.DeviceId} AND "AccountId" = {account.Id}
                  AND "Platform" = {WebSession.Platform} AND "RevokedAt" IS NULL FOR UPDATE
                """).AsNoTracking().FirstOrDefaultAsync(token);
            return device is null ? Refuse("device-unavailable") : await prepare(account, token);
        }, ct);
        var refusal = result.Refusal ?? result.Outcome?.Rejected.FirstOrDefault()?.Reason;
        if (refusal is not null)
        {
            return refusal switch
            {
                "account-unavailable" or "device-unavailable" => Results.Unauthorized(),
                "not-found" or SyncRejection.Purged => Results.NotFound(),
                "setup-required" => Conflict("Connect Chaos on your desktop and sync your Inbox before adding tasks here."),
                "recurring" => Conflict("Complete or reopen this recurring task on your desktop."),
                "task-closed" => Conflict("Reopen this task before updating it."),
                "task-id-in-use" => Conflict("This task ID is already in use."),
                SyncRejection.SchemaTooOld => Conflict("The server requires a newer task format."),
                _ => Conflict("The change could not be synced."),
            };
        }

        var current = await read.AccountAsync(caller.AccountId, ct);
        return current is not null && await read.TaskStateAsync(current, taskId, ct) is { } state
            ? Results.Ok(state.Task)
            : Results.NotFound();
    }

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
    }

    private static void MapAction(RouteGroupBuilder web, string route, WebTaskAction action) =>
        web.MapPost($"/tasks/{{id:guid}}/{route}", (Guid id, ClaimsPrincipal user, WebTaskWrites writes, CancellationToken ct) =>
            writes.ActAsync(WebCaller.From(user), id, action, ct)).AddEndpointFilter<WebAntiforgery>();
}
