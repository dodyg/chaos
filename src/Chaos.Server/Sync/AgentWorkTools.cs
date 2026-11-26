using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Chaos.Server.Data;
using Chaos.Server.Web;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Chaos.Server.Sync;

/// <summary>
/// The service surface's work tools of PLAN3 12.2 as Microsoft.Extensions.AI functions, with the names, descriptions and
/// JSON schemas of the local surface (<c>docs/mcp/tools.json</c>). Reads come from the space streams the agent is a live
/// member of, limited to its allowed lists; every write is a change pushed through <see cref="SyncService.PushAsAgentAsync"/>,
/// so the Agent column of 8.4, the hourly counters (E8), the labelled history and the revert of 12.7 apply to it exactly as
/// they do to any other push. Results carry user text only in the fields named in <see cref="AgentWorkToolNames"/>, and a
/// refused call answers a status code, never a message.
/// </summary>
public sealed class AgentWorkTools
{
    private readonly ChaosDbContext db;
    private readonly SyncService sync;
    private readonly AgentService agents;
    private readonly ServerChangeWriter changes;
    private readonly TimeProvider clock;

    public AgentWorkTools(ChaosDbContext db, SyncService sync, AgentService agents, ServerChangeWriter changes, TimeProvider clock)
    {
        this.db = db;
        this.sync = sync;
        this.agents = agents;
        this.changes = changes;
        this.clock = clock;
    }

    private const int PageSize = 100;
    private const int MaxText = 20_000;
    private const int MaxTitleRunes = 500;
    private const int MaxSubtasks = 50;

    private static readonly string[] PriorityNames = ["none", "low", "normal", "high", "urgent"];

    private static readonly Dictionary<string, long> Statuses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["not-started"] = TaskStatuses.NotStarted,
        ["in-progress"] = TaskStatuses.InProgress,
        ["waiting"] = TaskStatuses.WaitingOnSomeone,
        ["needs-review"] = TaskStatuses.NeedsReview,
        ["done"] = TaskStatuses.Done,
        ["cancelled"] = TaskStatuses.Cancelled,
    };

    private static string StatusName(long status) => Statuses.FirstOrDefault(pair => pair.Value == status).Key ?? "not-started";

    /// <summary>The work tools bound to one agent. Its ID closes over every call; it is never an argument the caller picks.</summary>
    public IReadOnlyList<AIFunction> For(Guid agentId) => new Bound(this, agentId).Functions;

    private sealed record ListReach(Guid Id, string Name);

    private sealed record SpaceReach(Guid Id, string Name, bool AllowClose, IReadOnlyList<ListReach> Lists);

    private sealed record Reach(AgentRow Agent, IReadOnlyList<SpaceReach> Spaces)
    {
        public SpaceReach? SpaceOf(Guid id) => Spaces.FirstOrDefault(space => space.Id == id);
    }

    /// <summary>The agent, and the spaces and lists it may use: live memberships of spaces that allow agents, narrowed to the allowed lists.</summary>
    private async Task<(string? Refusal, Reach? Reach)> ReachAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null || agent.RemovedAt is not null)
        {
            return (AgentToolStatus.NotFound, null);
        }

        if (agent.PausedAt is not null)
        {
            return (AgentToolStatus.Paused, null);
        }

        var members = await db.SpaceMembers.AsNoTracking()
            .Where(m => m.MemberId == agentId && m.MemberKind == SpaceMember.AgentKind && m.RemovedAt == null).ToListAsync(ct);
        var spaces = new List<SpaceReach>();
        foreach (var member in members)
        {
            var space = await db.SyncEntities.AsNoTracking().FirstOrDefaultAsync(e => e.AccountId == member.SpaceId && e.Kind == "space" && e.PurgedAt == null, ct);
            if (space is null || !await db.SyncStreams.AsNoTracking().AnyAsync(s => s.Id == member.SpaceId && s.DeletedAt == null, ct))
            {
                continue;
            }

            var entity = new WebEntity(space);
            if (entity.Number("settings", "AgentsAllowed") != 1)
            {
                continue;
            }

            var allowed = member.AllowedContainers is null ? null : JsonSerializer.Deserialize<string[]>(member.AllowedContainers);
            var lists = new List<ListReach>();
            var rows = await db.SyncEntities.AsNoTracking()
                .Where(e => e.AccountId == member.SpaceId && e.Kind == "container" && e.PurgedAt == null && e.MovedTo == null).ToListAsync(ct);
            foreach (var row in rows)
            {
                var list = new WebEntity(row);
                if (list.IsDeleted || list.Text("archived", "ArchivedAtUtc") is not null || list.Text("name", "Name") is not { Length: > 0 } name
                    || !Guid.TryParse(row.EntityKey, out var id)
                    || (allowed is { Length: > 0 } && !allowed.Contains(row.EntityKey, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }

                lists.Add(new ListReach(id, name));
            }

            spaces.Add(new SpaceReach(member.SpaceId, entity.Text("name", "Name") ?? "Shared space", entity.Number("settings", "AllowAgentsToClose") == 1,
                [.. lists.OrderBy(list => list.Name, StringComparer.OrdinalIgnoreCase).ThenBy(list => list.Id)]));
        }

        return (null, new Reach(agent, [.. spaces.OrderBy(space => space.Name, StringComparer.OrdinalIgnoreCase).ThenBy(space => space.Id)]));
    }

    /// <summary>A task the agent may read: in a reachable list, not trashed, and not pointed at another agent.</summary>
    private sealed record Located(Guid StreamId, SpaceReach Space, string Key, WebEntity Entity);

    private async Task<(string? Refusal, Located? Task)> LocateAsync(Reach reach, string taskId, CancellationToken ct)
    {
        if (!Guid.TryParseExact(taskId, "D", out _))
        {
            return (AgentToolStatus.NotFound, null);
        }

        var streams = reach.Spaces.Select(space => space.Id).ToArray();
        var rows = await db.SyncEntities.AsNoTracking()
            .Where(e => streams.Contains(e.AccountId) && e.Kind == "task" && e.EntityKey == taskId && e.PurgedAt == null && e.MovedTo == null)
            .Take(2).ToListAsync(ct);
        if (rows.Count != 1)
        {
            return (AgentToolStatus.NotFound, null);
        }

        var entity = new WebEntity(rows[0]);
        if (entity.IsDeleted)
        {
            return (AgentToolStatus.NotFound, null);
        }

        var space = reach.SpaceOf(rows[0].AccountId)!;
        if (entity.Id("place", "ContainerId") is not { } list || !space.Lists.Any(known => known.Id == list)
            || (entity.Text("agent", "AgentAssigneeId") is { Length: > 0 } assigned
                && !string.Equals(assigned, reach.Agent.Id.ToString("D"), StringComparison.OrdinalIgnoreCase)))
        {
            return (AgentToolStatus.Forbidden, null);
        }

        return (null, new Located(space.Id, space, taskId, entity));
    }

    private static string Me(Guid agentId) => new ActorId(ActorKind.Agent, agentId).ToString();

    private static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    private static string? Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private bool Live(WebEntity task, DateTimeOffset now) =>
        task.Text("claim", "ClaimedById") is not null && task.Instant("claim", "ClaimExpiresAtUtc") > now;

    private static SyncGroupState Group(Hlc hlc, params (string Column, object? Value)[] fields) =>
        new(hlc, new SyncGroupValues(fields.ToDictionary(field => field.Column, field => field.Value)));

    private static IncomingChange Upsert(string kind, string key, Dictionary<string, SyncGroupState> groups)
    {
        var id = Guid.NewGuid().ToString("D");
        return new IncomingChange(id, SyncChange.Upsert(id, kind, key, groups), null);
    }

    /// <summary>The status group as an agent writes it: the base status only, so a custom state never outlives a change it does not match.</summary>
    private static SyncGroupState StatusGroup(Hlc hlc, long status, DateTimeOffset now) =>
        Group(hlc, ("Status", status), ("CustomStateId", null), ("CompletedAtUtc", status == TaskStatuses.Done ? Instant(now) : null),
            ("CancelledAtUtc", status == TaskStatuses.Cancelled ? Instant(now) : null));

    /// <summary>Pushes as the agent. Null is success; otherwise the status code of the first refusal (never its text).</summary>
    private async Task<TaskToolResult?> PushAsync(Guid agentId, string taskId, IReadOnlyList<IncomingChange> incoming, string? field, CancellationToken ct)
    {
        PushOutcome outcome;
        try
        {
            outcome = await sync.PushAsAgentAsync(agentId, incoming, ct);
        }
        catch (SyncService.AgentUnavailableException error)
        {
            return new TaskToolResult(error.Reason == AgentToolStatus.Paused ? AgentToolStatus.Paused : AgentToolStatus.NotFound, taskId);
        }

        if (outcome.Rejected.Count == 0)
        {
            return null;
        }

        switch (outcome.Rejected[0].Reason)
        {
            case SyncRejection.RateLimited:
                var agent = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agentId, ct);
                return new TaskToolResult(AgentToolStatus.RateLimited, taskId, AgentService.RetryAfter(agent, clock.GetUtcNow()));
            case SyncRejection.Forbidden or SyncRejection.NotMember or SyncRejection.ServerOnly or SyncRejection.NotOwner:
                return new TaskToolResult(AgentToolStatus.Forbidden, taskId, Field: field);
            case SyncRejection.Invalid or SyncRejection.TooLarge or SyncRejection.ParentUnknown or SyncRejection.UnknownKind:
                return new TaskToolResult(AgentToolStatus.Invalid, taskId, Field: field);
            case SyncRejection.Purged or SyncRejection.Moved:
                return new TaskToolResult(AgentToolStatus.NotFound, taskId);
            default:
                return new TaskToolResult(AgentToolStatus.Conflict, taskId);
        }
    }

    /// <summary>Whether the agent created the task: its own change carried the task's create group.</summary>
    private async Task<bool> CreatedByAsync(Guid agentId, Located task, CancellationToken ct) =>
        await db.SyncChanges.FromSqlInterpolated($"""
            SELECT * FROM "SyncChanges" WHERE "StreamId" = {task.StreamId} AND "Kind" = 'task' AND "EntityKey" = {task.Key}
              AND "ActorId" = {agentId} AND "Groups" -> 'create' IS NOT NULL
            """).AsNoTracking().AnyAsync(ct);

    private sealed record WriteGate(string? Refusal, Located? Task, bool Claimant);

    /// <summary>
    /// Writes (12.2): a live claim by someone else refuses; with <paramref name="requireOwnership"/> the agent must hold the live
    /// claim, or (unless <paramref name="claimOnly"/>) have created the task.
    /// </summary>
    private async Task<WriteGate> AuthorizeWriteAsync(Guid agentId, string taskId, bool claimOnly, bool requireOwnership, CancellationToken ct)
    {
        var (refusal, reach) = await ReachAsync(agentId, ct);
        if (reach is null)
        {
            return new WriteGate(refusal, null, false);
        }

        var (denied, task) = await LocateAsync(reach, taskId, ct);
        if (task is null)
        {
            return new WriteGate(denied, null, false);
        }

        var live = Live(task.Entity, clock.GetUtcNow());
        var claimant = live && string.Equals(task.Entity.Text("claim", "ClaimedById"), Me(agentId), StringComparison.OrdinalIgnoreCase);
        if (live && !claimant)
        {
            return new WriteGate(AgentToolStatus.AlreadyClaimed, null, false);
        }

        if (requireOwnership && !claimant && (claimOnly || !await CreatedByAsync(agentId, task, ct)))
        {
            return new WriteGate(AgentToolStatus.NotClaimant, null, false);
        }

        return new WriteGate(null, task, claimant);
    }

    private sealed class Bound
    {
        private readonly AgentWorkTools _owner;
        private readonly Guid _agentId;

        public IReadOnlyList<AIFunction> Functions { get; }

        public Bound(AgentWorkTools owner, Guid agentId)
        {
            _owner = owner;
            _agentId = agentId;
            var type = typeof(Bound);
            Functions =
            [
                AIFunctionFactory.Create(type.GetMethod(nameof(ListSpacesAsync))!, this, AgentWorkToolNames.ListSpaces, AgentWorkToolNames.ListSpacesDescription),
                AIFunctionFactory.Create(type.GetMethod(nameof(ListTasksAsync))!, this, AgentWorkToolNames.ListTasks, AgentWorkToolNames.ListTasksDescription),
                AIFunctionFactory.Create(type.GetMethod(nameof(GetTaskAsync))!, this, AgentWorkToolNames.GetTask, AgentWorkToolNames.GetTaskDescription),
                AIFunctionFactory.Create(type.GetMethod(nameof(CreateTaskAsync))!, this, AgentWorkToolNames.CreateTask, AgentWorkToolNames.CreateTaskDescription),
                AIFunctionFactory.Create(type.GetMethod(nameof(UpdateTaskAsync))!, this, AgentWorkToolNames.UpdateTask, AgentWorkToolNames.UpdateTaskDescription),
                AIFunctionFactory.Create(type.GetMethod(nameof(AddUpdateAsync))!, this, AgentWorkToolNames.AddUpdate, AgentWorkToolNames.AddUpdateDescription),
                AIFunctionFactory.Create(type.GetMethod(nameof(AskForHelpAsync))!, this, AgentWorkToolNames.AskForHelp, AgentWorkToolNames.AskForHelpDescription),
                AIFunctionFactory.Create(type.GetMethod(nameof(HandBackAsync))!, this, AgentWorkToolNames.HandBack, AgentWorkToolNames.HandBackDescription),
            ];
        }

        public async Task<ListSpacesResult> ListSpacesAsync(CancellationToken cancellationToken)
        {
            var (refusal, reach) = await _owner.ReachAsync(_agentId, cancellationToken);
            if (reach is null)
            {
                return new ListSpacesResult(refusal!, [], []);
            }

            var spaces = reach.Spaces.Where(space => space.Lists.Count > 0).ToList();
            return new ListSpacesResult(AgentToolStatus.Ok,
                [.. spaces.Select(space => new AgentSpaceInfo(space.Id.ToString("D"), space.Name, "shared"))],
                [.. spaces.SelectMany(space => space.Lists.Select(list => new AgentListInfo(list.Id.ToString("D"), list.Name, space.Id.ToString("D"))))]);
        }

        public async Task<ListTasksResult> ListTasksAsync(
            [Description("Only tasks of this list ID.")] string? listId = null,
            [Description("Only this status: not-started, in-progress, waiting, needs-review, done or cancelled.")] string? status = null,
            [Description("Only tasks a person marked Ready for agent.")] bool? readyForAgent = null,
            [Description("Only tasks this agent holds a live claim on.")] bool? claimedByMe = null,
            [Description("Only tasks whose title or notes contain this text.")] string? text = null,
            [Description("Skip this many tasks (paging).")] int offset = 0,
            CancellationToken cancellationToken = default)
        {
            var (refusal, reach) = await _owner.ReachAsync(_agentId, cancellationToken);
            if (reach is null)
            {
                return new ListTasksResult(refusal!, [], false);
            }

            long? wanted = null;
            if (status is { Length: > 0 })
            {
                if (!Statuses.TryGetValue(status, out var parsed))
                {
                    return new ListTasksResult(AgentToolStatus.Invalid, [], false);
                }

                wanted = parsed;
            }

            var now = _owner.clock.GetUtcNow();
            var me = Me(_agentId);
            var matches = new List<(string Key, AgentTaskSummary Summary)>();
            foreach (var space in reach.Spaces)
            {
                var lists = space.Lists.Where(list => listId is not { Length: > 0 } || string.Equals(list.Id.ToString("D"), listId, StringComparison.OrdinalIgnoreCase))
                    .Select(list => list.Id.ToString("D")).ToArray();
                if (lists.Length == 0)
                {
                    continue;
                }

                var rows = await _owner.db.SyncEntities.FromSqlInterpolated($"""
                    SELECT * FROM "SyncEntities" WHERE "StreamId" = {space.Id} AND "Kind" = 'task' AND "PurgedAt" IS NULL AND "MovedTo" IS NULL
                      AND "Groups" -> 'place' -> 'values' ->> 'ContainerId' = ANY({lists})
                    """).AsNoTracking().ToListAsync(cancellationToken);
                foreach (var row in rows)
                {
                    var task = new WebEntity(row);
                    var live = _owner.Live(task, now);
                    var title = task.Text("title", "Title");
                    var notes = task.Text("notes", "Notes") ?? string.Empty;
                    if (task.IsDeleted || title is null
                        || (wanted is { } only && (task.Number("status", "Status") ?? 0) != only)
                        || (readyForAgent is { } ready && (task.Number("agent", "AgentReady") == 1) != ready)
                        || (claimedByMe is true && !(live && string.Equals(task.Text("claim", "ClaimedById"), me, StringComparison.OrdinalIgnoreCase)))
                        || (text is { Length: > 0 } && !(title.Contains(text, StringComparison.OrdinalIgnoreCase) || notes.Contains(text, StringComparison.OrdinalIgnoreCase)))
                        || (task.Text("agent", "AgentAssigneeId") is { Length: > 0 } assigned && !string.Equals(assigned, _agentId.ToString("D"), StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    matches.Add((row.EntityKey, new AgentTaskSummary(row.EntityKey, task.Text("place", "ContainerId") ?? string.Empty, title,
                        StatusName(task.Number("status", "Status") ?? 0), Date(task.Date("planned", "PlannedDate")), Date(task.Date("due", "DueDate")),
                        PriorityName(task.Number("priority", "Priority")), live ? task.Text("claim", "ClaimedById") : null,
                        live && task.Instant("claim", "ClaimExpiresAtUtc") is { } until ? Instant(until) : null, task.Number("agent", "AgentReady") == 1,
                        notes.Length > 200 ? notes[..200] : notes)));
                }
            }

            var page = matches.OrderBy(match => match.Key, StringComparer.Ordinal).Skip(Math.Max(0, offset)).Take(PageSize + 1).Select(match => match.Summary).ToList();
            return new ListTasksResult(AgentToolStatus.Ok, [.. page.Take(PageSize)], page.Count > PageSize);
        }

        private static string PriorityName(long? priority) =>
            priority is { } value && value >= 0 && value < PriorityNames.Length ? char.ToUpperInvariant(PriorityNames[value][0]) + PriorityNames[value][1..] : "Normal";

        public async Task<GetTaskResult> GetTaskAsync([Description("The ID of the task.")] string taskId, CancellationToken cancellationToken)
        {
            var (refusal, reach) = await _owner.ReachAsync(_agentId, cancellationToken);
            if (reach is null)
            {
                return new GetTaskResult(refusal!, taskId);
            }

            var (denied, located) = await _owner.LocateAsync(reach, taskId, cancellationToken);
            if (located is null)
            {
                return new GetTaskResult(denied!, taskId);
            }

            var key = located.Key;
            var subtaskRows = await _owner.db.SyncEntities.FromSqlInterpolated($"""
                SELECT * FROM "SyncEntities" WHERE "StreamId" = {located.StreamId} AND "Kind" = 'subtask' AND "PurgedAt" IS NULL AND "MovedTo" IS NULL
                  AND "Groups" -> 'create' -> 'values' ->> 'TaskId' = {key}
                """).AsNoTracking().ToListAsync(cancellationToken);
            var subtasks = subtaskRows.Select(row => (Row: row, Entity: new WebEntity(row))).Where(pair => !pair.Entity.IsDeleted && pair.Entity.Text("title", "Title") is not null)
                .OrderBy(pair => pair.Entity.Number("position", "Position") ?? 0).ThenBy(pair => pair.Row.EntityKey, StringComparer.Ordinal)
                .Select(pair => new AgentSubtaskInfo(pair.Row.EntityKey, pair.Entity.Text("title", "Title")!, StatusName(pair.Entity.Number("status", "Status") ?? 0))).ToList();
            var diaryRows = await _owner.db.SyncEntities.FromSqlInterpolated($"""
                SELECT * FROM "SyncEntities" WHERE "StreamId" = {located.StreamId} AND "Kind" = 'diary_entry' AND "PurgedAt" IS NULL AND "MovedTo" IS NULL
                  AND "Groups" -> 'create' -> 'values' ->> 'TaskId' = {key}
                """).AsNoTracking().ToListAsync(cancellationToken);
            var diary = diaryRows.Select(row => (Row: row, Entity: new WebEntity(row))).Where(pair => !pair.Entity.IsDeleted)
                .Select(pair => (pair.Row.EntityKey, At: pair.Entity.Instant("create", "EntryAtUtc") ?? pair.Entity.Instant("create", "CreatedAtUtc") ?? pair.Row.UpdatedAt,
                    Author: pair.Entity.Text("create", "AuthorProfileId") ?? string.Empty, Text: pair.Entity.Text("entry", "RawMarkdown") ?? string.Empty))
                .OrderBy(entry => entry.At).ThenBy(entry => entry.EntityKey, StringComparer.Ordinal).TakeLast(50)
                .Select(entry => new AgentDiaryInfo(entry.EntityKey, Instant(entry.At), entry.Author, entry.Text)).ToList();

            var task = located.Entity;
            var live = _owner.Live(task, _owner.clock.GetUtcNow());
            return new GetTaskResult(AgentToolStatus.Ok, key, task.Text("place", "ContainerId"), StatusName(task.Number("status", "Status") ?? 0),
                Date(task.Date("planned", "PlannedDate")), Date(task.Date("due", "DueDate")), PriorityName(task.Number("priority", "Priority")),
                task.Number("estimate", "EstimateMinutes") is { } estimate ? (int)estimate : null,
                live ? task.Text("claim", "ClaimedById") : null, live && task.Instant("claim", "ClaimExpiresAtUtc") is { } until ? Instant(until) : null,
                task.Number("agent", "AgentReady") == 1,
                new TaskDataBlock(task.Text("title", "Title") ?? string.Empty, task.Text("notes", "Notes") ?? string.Empty, subtasks, diary));
        }

        public async Task<TaskToolResult> CreateTaskAsync(
            [Description("The ID of an allowed list.")] string listId,
            [Description("The task title.")] string title,
            [Description("Notes in Markdown.")] string? notes = null,
            [Description("Planned date, yyyy-MM-dd.")] string? plannedDate = null,
            [Description("Due date, yyyy-MM-dd.")] string? dueDate = null,
            [Description("Estimate in minutes.")] int? estimateMinutes = null,
            [Description("none, low, normal, high or urgent.")] string? priority = null,
            [Description("Titles of subtasks to add.")] string[]? subtasks = null,
            CancellationToken cancellationToken = default)
        {
            var (refusal, reach) = await _owner.ReachAsync(_agentId, cancellationToken);
            if (reach is null)
            {
                return new TaskToolResult(refusal!, listId);
            }

            var space = Guid.TryParseExact(listId, "D", out var list) ? reach.Spaces.FirstOrDefault(s => s.Lists.Any(known => known.Id == list)) : null;
            if (space is null)
            {
                return new TaskToolResult(AgentToolStatus.Forbidden, listId);
            }

            var trimmed = title?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.EnumerateRunes().Count() > MaxTitleRunes)
            {
                return new TaskToolResult(AgentToolStatus.Invalid, listId, Field: "title");
            }

            if ((notes?.Length ?? 0) > MaxText)
            {
                return new TaskToolResult(AgentToolStatus.Invalid, listId, Field: "notes");
            }

            if (!TryDate(plannedDate, out var planned))
            {
                return new TaskToolResult(AgentToolStatus.Invalid, listId, Field: "plannedDate");
            }

            if (!TryDate(dueDate, out var due))
            {
                return new TaskToolResult(AgentToolStatus.Invalid, listId, Field: "dueDate");
            }

            if (!TryPriority(priority, out var parsedPriority))
            {
                return new TaskToolResult(AgentToolStatus.Invalid, listId, Field: "priority");
            }

            if (estimateMinutes is < 0 or > 1_000_000)
            {
                return new TaskToolResult(AgentToolStatus.Invalid, listId, Field: "estimateMinutes");
            }

            var titles = new List<string>();
            foreach (var subtask in (subtasks ?? []).Take(MaxSubtasks))
            {
                if (subtask?.Trim() is not { Length: > 0 } name || name.EnumerateRunes().Count() > MaxTitleRunes)
                {
                    return new TaskToolResult(AgentToolStatus.Invalid, listId, Field: "subtasks");
                }

                titles.Add(name);
            }

            var taskKey = Guid.NewGuid().ToString("D");
            var now = _owner.clock.GetUtcNow();
            var siblings = await _owner.db.SyncEntities.FromSqlInterpolated($"""
                SELECT * FROM "SyncEntities" WHERE "StreamId" = {space.Id} AND "Kind" = 'task' AND "PurgedAt" IS NULL AND "MovedTo" IS NULL
                  AND "Groups" -> 'place' -> 'values' ->> 'ContainerId' = {listId.ToLowerInvariant()}
                """).AsNoTracking().ToListAsync(cancellationToken);
            var last = siblings.Select(row => new WebEntity(row).Number("position", "Position") ?? 0).DefaultIfEmpty(-1024).Max();
            var position = last <= long.MaxValue - 1024 ? Math.Max(0, last + 1024) : last;
            var hlc = await _owner.changes.NextHlcAsync(space.Id, "task", taskKey, cancellationToken);
            var pushes = new List<IncomingChange>
            {
                Upsert("task", taskKey, new Dictionary<string, SyncGroupState>
                {
                    ["create"] = Group(hlc, ("SpaceId", space.Id.ToString("D")), ("CreatedAtUtc", Instant(now)), ("RecurrenceOccurrenceKey", null)),
                    ["title"] = Group(hlc, ("Title", trimmed)),
                    ["notes"] = Group(hlc, ("Notes", notes ?? string.Empty)),
                    ["place"] = Group(hlc, ("ContainerId", list.ToString("D")), ("SectionId", null)),
                    ["status"] = StatusGroup(hlc, TaskStatuses.NotStarted, now),
                    ["position"] = Group(hlc, ("Position", position)),
                    ["priority"] = Group(hlc, ("Priority", (long)(parsedPriority ?? 2))),
                    ["estimate"] = Group(hlc, ("EstimateMinutes", estimateMinutes is { } minutes ? (long)minutes : null)),
                    ["category"] = Group(hlc, ("CapacityCategory", 0L)),
                    ["planned"] = Group(hlc, ("PlannedDate", Date(planned)), ("PlannedTime", null), ("PlannedZoneId", null)),
                    ["due"] = Group(hlc, ("DueDate", Date(due)), ("DueTime", null), ("DueZoneId", null)),
                    ["deleted"] = Group(hlc, ("DeletedAtUtc", null)),
                }),
            };
            // The task is pushed first: a subtask is checked against its parent's list, and the parent exists only once its push commits.
            if (await _owner.PushAsync(_agentId, taskKey, pushes, null, cancellationToken) is { } refusedTask)
            {
                return refusedTask;
            }

            pushes.Clear();
            var offset = 0L;
            foreach (var name in titles)
            {
                var key = Guid.NewGuid().ToString("D");
                var subtaskClock = await _owner.changes.NextHlcAsync(space.Id, "subtask", key, cancellationToken);
                pushes.Add(Upsert("subtask", key, new Dictionary<string, SyncGroupState>
                {
                    ["create"] = Group(subtaskClock, ("TaskId", taskKey), ("CreatedAtUtc", Instant(now))),
                    ["title"] = Group(subtaskClock, ("Title", name)),
                    ["notes"] = Group(subtaskClock, ("Notes", string.Empty)),
                    ["status"] = Group(subtaskClock, ("Status", 0L), ("CompletedAtUtc", null), ("CancelledAtUtc", null)),
                    ["position"] = Group(subtaskClock, ("Position", offset += 1024)),
                    ["deleted"] = Group(subtaskClock, ("DeletedAtUtc", null)),
                }));
            }

            if (pushes.Count > 0 && await _owner.PushAsync(_agentId, taskKey, pushes, "subtasks", cancellationToken) is { } refusedSubtasks)
            {
                return refusedSubtasks;
            }

            return new TaskToolResult(AgentToolStatus.Created, taskKey);
        }

        public async Task<TaskToolResult> UpdateTaskAsync(
            [Description("The ID of the task. The agent must hold the claim or have created the task.")] string taskId,
            [Description("A new title.")] string? title = null,
            [Description("New notes (Markdown), replacing the old ones.")] string? notes = null,
            [Description("not-started, in-progress, waiting, needs-review, done or cancelled.")] string? status = null,
            [Description("Planned date, yyyy-MM-dd.")] string? plannedDate = null,
            [Description("Due date, yyyy-MM-dd.")] string? dueDate = null,
            [Description("Estimate in minutes.")] int? estimateMinutes = null,
            [Description("none, low, normal, high or urgent.")] string? priority = null,
            CancellationToken cancellationToken = default)
        {
            var gate = await _owner.AuthorizeWriteAsync(_agentId, taskId, claimOnly: false, requireOwnership: true, cancellationToken);
            if (gate.Task is not { } task)
            {
                return new TaskToolResult(gate.Refusal!, taskId);
            }

            long? newStatus = null;
            if (status is { Length: > 0 })
            {
                if (!Statuses.TryGetValue(status, out var parsedStatus))
                {
                    return new TaskToolResult(AgentToolStatus.Invalid, taskId, Field: "status");
                }

                // Only Done can be allowed: the service refuses an agent's Cancelled whatever the space says (8.4).
                if (TaskStatuses.IsTerminal(parsedStatus) && !(parsedStatus == TaskStatuses.Done && task.Space.AllowClose))
                {
                    return new TaskToolResult(AgentToolStatus.Forbidden, taskId, Field: "status");
                }

                newStatus = parsedStatus;
            }

            var trimmed = title?.Trim();
            if (title is not null && (string.IsNullOrEmpty(trimmed) || trimmed.EnumerateRunes().Count() > MaxTitleRunes))
            {
                return new TaskToolResult(AgentToolStatus.Invalid, taskId, Field: "title");
            }

            if ((notes?.Length ?? 0) > MaxText)
            {
                return new TaskToolResult(AgentToolStatus.Invalid, taskId, Field: "notes");
            }

            if (!TryDate(plannedDate, out var planned))
            {
                return new TaskToolResult(AgentToolStatus.Invalid, taskId, Field: "plannedDate");
            }

            if (!TryDate(dueDate, out var due))
            {
                return new TaskToolResult(AgentToolStatus.Invalid, taskId, Field: "dueDate");
            }

            if (!TryPriority(priority, out var parsedPriority))
            {
                return new TaskToolResult(AgentToolStatus.Invalid, taskId, Field: "priority");
            }

            if (estimateMinutes is < 0 or > 1_000_000)
            {
                return new TaskToolResult(AgentToolStatus.Invalid, taskId, Field: "estimateMinutes");
            }

            var now = _owner.clock.GetUtcNow();
            var hlc = await _owner.changes.NextHlcAsync(task.StreamId, "task", taskId, cancellationToken);
            var groups = new Dictionary<string, SyncGroupState>(StringComparer.Ordinal);
            var entity = task.Entity;
            if (trimmed is not null)
            {
                groups["title"] = Group(hlc, ("Title", trimmed));
            }

            if (notes is not null)
            {
                groups["notes"] = Group(hlc, ("Notes", notes));
            }

            if (planned is not null)
            {
                groups["planned"] = Group(hlc, ("PlannedDate", Date(planned)), ("PlannedTime", null), ("PlannedZoneId", null));
            }

            if (due is not null)
            {
                groups["due"] = Group(hlc, ("DueDate", Date(due)), ("DueTime", entity.Text("due", "DueTime")), ("DueZoneId", entity.Text("due", "DueZoneId")));
            }

            if (estimateMinutes is { } minutes)
            {
                groups["estimate"] = Group(hlc, ("EstimateMinutes", (long)minutes));
            }

            if (parsedPriority is { } level)
            {
                groups["priority"] = Group(hlc, ("Priority", (long)level));
            }

            if (newStatus is { } target && target != (entity.Number("status", "Status") ?? 0))
            {
                groups["status"] = StatusGroup(hlc, target, now);
            }

            if (groups.Count > 0)
            {
                // A write by the claimant extends the claim inside the push itself.
                if (await _owner.PushAsync(_agentId, taskId, [Upsert("task", taskId, groups)], newStatus is null ? null : "status", cancellationToken) is { } refused)
                {
                    return refused;
                }
            }
            else if (gate.Claimant)
            {
                await _owner.agents.AgentClaimAsync(_agentId, taskId, ClaimAction.Heartbeat, cancellationToken);
            }

            return new TaskToolResult(AgentToolStatus.Ok, taskId);
        }

        public async Task<TaskToolResult> AddUpdateAsync(
            [Description("The ID of the task.")] string taskId,
            [Description("The diary entry, in Markdown.")] string markdown,
            CancellationToken cancellationToken)
        {
            var gate = await _owner.AuthorizeWriteAsync(_agentId, taskId, claimOnly: false, requireOwnership: false, cancellationToken);
            if (gate.Task is not { } task)
            {
                return new TaskToolResult(gate.Refusal!, taskId);
            }

            if (await DiaryAsync(task, markdown, cancellationToken) is { } refused)
            {
                return refused;
            }

            if (gate.Claimant)
            {
                await _owner.agents.AgentClaimAsync(_agentId, taskId, ClaimAction.Heartbeat, cancellationToken);
            }

            return new TaskToolResult(AgentToolStatus.Ok, taskId);
        }

        public async Task<TaskToolResult> AskForHelpAsync(
            [Description("The ID of the task this agent holds the claim on.")] string taskId,
            [Description("The question for the person, in Markdown.")] string question,
            CancellationToken cancellationToken)
        {
            var gate = await _owner.AuthorizeWriteAsync(_agentId, taskId, claimOnly: true, requireOwnership: true, cancellationToken);
            if (gate.Task is not { } task)
            {
                return new TaskToolResult(gate.Refusal!, taskId);
            }

            var refused = await DiaryAsync(task, question, cancellationToken) ?? await SetStatusAsync(task, TaskStatuses.WaitingOnSomeone, cancellationToken);
            if (refused is not null)
            {
                return refused;
            }

            await _owner.agents.AgentClaimAsync(_agentId, taskId, ClaimAction.Heartbeat, cancellationToken);
            return new TaskToolResult(AgentToolStatus.Ok, taskId);
        }

        public async Task<TaskToolResult> HandBackAsync(
            [Description("The ID of the task this agent holds the claim on.")] string taskId,
            [Description("What was done and where to look, in Markdown.")] string summary,
            CancellationToken cancellationToken)
        {
            var gate = await _owner.AuthorizeWriteAsync(_agentId, taskId, claimOnly: true, requireOwnership: true, cancellationToken);
            if (gate.Task is not { } task)
            {
                return new TaskToolResult(gate.Refusal!, taskId);
            }

            var refused = await DiaryAsync(task, summary, cancellationToken) ?? await SetStatusAsync(task, TaskStatuses.NeedsReview, cancellationToken);
            if (refused is not null)
            {
                return refused;
            }

            var released = await _owner.agents.AgentClaimAsync(_agentId, taskId, ClaimAction.Release, cancellationToken);
            return released.Error is { } error ? new TaskToolResult(error, taskId, released.RetryAfterSeconds) : new TaskToolResult(AgentToolStatus.Ok, taskId);
        }

        /// <summary>A diary entry written as this agent: its create group names it as the author, which the permission check requires.</summary>
        private async Task<TaskToolResult?> DiaryAsync(Located task, string? markdown, CancellationToken ct)
        {
            if ((markdown?.Length ?? 0) > MaxText)
            {
                return new TaskToolResult(AgentToolStatus.Invalid, task.Key, Field: "markdown");
            }

            var key = Guid.NewGuid().ToString("D");
            var now = _owner.clock.GetUtcNow();
            var hlc = await _owner.changes.NextHlcAsync(task.StreamId, "diary_entry", key, ct);
            return await _owner.PushAsync(_agentId, task.Key, [Upsert("diary_entry", key, new Dictionary<string, SyncGroupState>
            {
                ["create"] = Group(hlc, ("TaskId", task.Key), ("AuthorProfileId", _agentId.ToString("D")), ("EntryAtUtc", Instant(now)), ("CreatedAtUtc", Instant(now))),
                ["entry"] = Group(hlc, ("RawMarkdown", markdown ?? string.Empty), ("EditedAtUtc", null)),
                ["deleted"] = Group(hlc, ("DeletedAtUtc", null), ("RemovedByOwner", 0L)),
            })], "markdown", ct);
        }

        private async Task<TaskToolResult?> SetStatusAsync(Located task, long status, CancellationToken ct)
        {
            var hlc = await _owner.changes.NextHlcAsync(task.StreamId, "task", task.Key, ct);
            return await _owner.PushAsync(_agentId, task.Key, [Upsert("task", task.Key, new Dictionary<string, SyncGroupState>
            {
                ["status"] = StatusGroup(hlc, status, _owner.clock.GetUtcNow()),
            })], "status", ct);
        }

        private static bool TryDate(string? value, out DateOnly? date)
        {
            date = null;
            if (value is not { Length: > 0 })
            {
                return true;
            }

            if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return false;
            }

            date = parsed;
            return true;
        }

        private static bool TryPriority(string? value, out int? priority)
        {
            priority = null;
            if (value is not { Length: > 0 })
            {
                return true;
            }

            var index = Array.FindIndex(PriorityNames, name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            priority = index;
            return true;
        }
    }
}
