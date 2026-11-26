using System.ComponentModel;
using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Sync;
using Microsoft.Extensions.AI;

namespace Chaos.Application.Agents;

/// <summary>
/// The work tools of PLAN3 12.2 for a local agent. Each call first applies the agent rules (live agent, not paused, the
/// task's list ticked, one agent assigned at most, the claim for writes), then runs the ordinary Application command
/// under an agent scope, so history, undo and the activity log name <c>agent:&lt;id&gt;</c>. Results carry user text only
/// in the fields named in <see cref="AgentWorkToolNames"/>; a refused call answers a status code, never a message.
/// </summary>
public sealed partial class LocalAgentTools
{
    private sealed partial class Bound
    {
        private const int PageSize = 100;
        private const int MaxText = 20_000;

        private TaskCommands Tasks => new(_store, _ids, _clock);
        private DiaryCommands Diary => new(_store, _ids, _clock);
        private SubtaskCommands Subtasks => new(_store, _ids, _clock);

        private IEnumerable<AIFunction> WorkFunctions()
        {
            var type = typeof(Bound);
            yield return AIFunctionFactory.Create(type.GetMethod(nameof(ListSpacesAsync))!, this, AgentWorkToolNames.ListSpaces, AgentWorkToolNames.ListSpacesDescription);
            yield return AIFunctionFactory.Create(type.GetMethod(nameof(ListTasksAsync))!, this, AgentWorkToolNames.ListTasks, AgentWorkToolNames.ListTasksDescription);
            yield return AIFunctionFactory.Create(type.GetMethod(nameof(GetTaskAsync))!, this, AgentWorkToolNames.GetTask, AgentWorkToolNames.GetTaskDescription);
            yield return AIFunctionFactory.Create(type.GetMethod(nameof(CreateTaskAsync))!, this, AgentWorkToolNames.CreateTask, AgentWorkToolNames.CreateTaskDescription);
            yield return AIFunctionFactory.Create(type.GetMethod(nameof(UpdateTaskAsync))!, this, AgentWorkToolNames.UpdateTask, AgentWorkToolNames.UpdateTaskDescription);
            yield return AIFunctionFactory.Create(type.GetMethod(nameof(AddUpdateAsync))!, this, AgentWorkToolNames.AddUpdate, AgentWorkToolNames.AddUpdateDescription);
            yield return AIFunctionFactory.Create(type.GetMethod(nameof(AskForHelpAsync))!, this, AgentWorkToolNames.AskForHelp, AgentWorkToolNames.AskForHelpDescription);
            yield return AIFunctionFactory.Create(type.GetMethod(nameof(HandBackAsync))!, this, AgentWorkToolNames.HandBack, AgentWorkToolNames.HandBackDescription);
            foreach (var function in PadPageFunctions())
            {
                yield return function;
            }
        }

        private static readonly Dictionary<string, TaskItemStatus> Statuses = new(StringComparer.OrdinalIgnoreCase)
        {
            ["not-started"] = TaskItemStatus.NotStarted,
            ["in-progress"] = TaskItemStatus.InProgress,
            ["waiting"] = TaskItemStatus.WaitingOnSomeone,
            ["needs-review"] = TaskItemStatus.NeedsReview,
            ["done"] = TaskItemStatus.Done,
            ["cancelled"] = TaskItemStatus.Cancelled,
        };

        private static string StatusName(TaskItemStatus status) => Statuses.First(pair => pair.Value == status).Key;

        /// <summary>The agent and the Personal lists it may use, in list order.</summary>
        private sealed record Reach(LocalAgentRecord Agent, SpaceRecord Personal, IReadOnlyList<ContainerRecord> Lists);

        private async Task<(string? Refusal, Reach? Reach)> ReachAsync(IDataSession session, CancellationToken token)
        {
            var agent = await session.Agents.GetAsync(_agentId, token);
            if (agent is null)
            {
                return (AgentToolStatus.NotFound, null);
            }

            if (agent.PausedAtUtc is not null)
            {
                return (AgentToolStatus.Paused, null);
            }

            var profile = await session.Profiles.GetPrimaryAsync(token);
            var personal = profile is null ? null : await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token);
            if (personal is null)
            {
                return (AgentToolStatus.Forbidden, null);
            }

            var lists = new List<ContainerRecord>();
            foreach (var access in await session.Agents.ListAccessAsync(agent.Id, includeRevoked: false, token))
            {
                var container = await session.Containers.GetAsync(access.ContainerId, token);
                if (container is not null && container.SpaceId == personal.Id && container.ArchivedAtUtc is null)
                {
                    lists.Add(container);
                }
            }

            return (null, new Reach(agent, personal, [.. lists.OrderBy(list => list.Position).ThenBy(list => list.Id, StringComparer.Ordinal)]));
        }

        public Task<ListSpacesResult> ListSpacesAsync(CancellationToken cancellationToken) =>
            _store.QueryAsync(async (session, token) =>
            {
                var (refusal, reach) = await ReachAsync(session, token);
                if (reach is null)
                {
                    return new ListSpacesResult(refusal!, [], []);
                }

                return new ListSpacesResult(AgentToolStatus.Ok, reach.Lists.Count == 0 ? [] : [new AgentSpaceInfo(reach.Personal.Id, reach.Personal.Name, "personal")],
                    [.. reach.Lists.Select(list => new AgentListInfo(list.Id, list.Name, list.SpaceId))]);
            }, cancellationToken);

        public Task<ListTasksResult> ListTasksAsync(
            [Description("Only tasks of this list ID.")] string? listId = null,
            [Description("Only this status: not-started, in-progress, waiting, needs-review, done or cancelled.")] string? status = null,
            [Description("Only tasks a person marked Ready for agent.")] bool? readyForAgent = null,
            [Description("Only tasks this agent holds a live claim on.")] bool? claimedByMe = null,
            [Description("Only tasks whose title or notes contain this text.")] string? text = null,
            [Description("Skip this many tasks (paging).")] int offset = 0,
            CancellationToken cancellationToken = default) =>
            _store.QueryAsync(async (session, token) =>
            {
                var (refusal, reach) = await ReachAsync(session, token);
                if (reach is null)
                {
                    return new ListTasksResult(refusal!, [], false);
                }

                TaskItemStatus? wanted = null;
                if (status is { Length: > 0 })
                {
                    if (!Statuses.TryGetValue(status, out var parsed))
                    {
                        return new ListTasksResult(AgentToolStatus.Invalid, [], false);
                    }

                    wanted = parsed;
                }

                var now = _clock.GetUtcNow();
                var matches = new List<AgentTaskSummary>();
                foreach (var list in reach.Lists.Where(list => listId is not { Length: > 0 } || string.Equals(list.Id, listId, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var task in await session.Tasks.ListByContainerAsync(list.Id, token))
                    {
                        var live = task.ClaimedById is not null && task.ClaimExpiresAtUtc > now;
                        if ((wanted is { } only && task.Status != only)
                            || (readyForAgent is { } ready && task.AgentReady != ready)
                            || (claimedByMe is true && !(live && string.Equals(task.ClaimedById, _actor, StringComparison.OrdinalIgnoreCase)))
                            || (text is { Length: > 0 } && !(task.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                                || task.Notes.Contains(text, StringComparison.OrdinalIgnoreCase)))
                            || (task.AgentAssigneeId is { Length: > 0 } assigned && !string.Equals(assigned, _agentId, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        matches.Add(Summary(task, now));
                    }
                }

                var page = matches.Skip(Math.Max(0, offset)).Take(PageSize + 1).ToList();
                return new ListTasksResult(AgentToolStatus.Ok, [.. page.Take(PageSize)], page.Count > PageSize);
            }, cancellationToken);

        private static AgentTaskSummary Summary(TaskRecord task, DateTimeOffset now)
        {
            var live = task.ClaimedById is not null && task.ClaimExpiresAtUtc > now;
            var notes = task.Notes.Length > 200 ? task.Notes[..200] : task.Notes;
            return new(task.Id, task.ContainerId, task.Title, StatusName(task.Status), Date(task.PlannedDate), Date(task.DueDate), task.Priority.ToString(),
                live ? task.ClaimedById : null, live && task.ClaimExpiresAtUtc is { } until ? StorageFormats.Instant(until) : null, task.AgentReady, notes);
        }

        private static string? Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public Task<GetTaskResult> GetTaskAsync([Description("The ID of the task.")] string taskId, CancellationToken cancellationToken) =>
            _store.QueryAsync(async (session, token) =>
            {
                var (refusal, task, _) = await AuthorizeAsync(session, taskId, token);
                if (task is null)
                {
                    return new GetTaskResult(refusal!, taskId);
                }

                var subtasks = await session.Subtasks.ListByTaskAsync(task.Id, token);
                var diary = (await session.Diary.ListByTaskAsync(task.Id, includeDeleted: false, token)).TakeLast(50).ToList();
                var now = _clock.GetUtcNow();
                var live = task.ClaimedById is not null && task.ClaimExpiresAtUtc > now;
                return new GetTaskResult(AgentToolStatus.Ok, task.Id, task.ContainerId, StatusName(task.Status), Date(task.PlannedDate), Date(task.DueDate),
                    task.Priority.ToString(), task.EstimateMinutes, live ? task.ClaimedById : null,
                    live && task.ClaimExpiresAtUtc is { } until ? StorageFormats.Instant(until) : null, task.AgentReady,
                    new TaskDataBlock(task.Title, task.Notes,
                        [.. subtasks.Select(subtask => new AgentSubtaskInfo(subtask.Id, subtask.Title, StatusName(subtask.Status)))],
                        [.. diary.Select(entry => new AgentDiaryInfo(entry.Id, StorageFormats.Instant(entry.EntryAtUtc), entry.AuthorProfileId, entry.RawMarkdown))]));
            }, cancellationToken);

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
            var operationId = Guid.NewGuid().ToString("D");
            using var scope = ActivityScope.Open(ActivitySources.Agent, ActivityActions.AgentCreateTask, operationId: operationId, actorId: _actor);
            var denied = await _store.QueryAsync<TaskToolResult?>(async (session, token) =>
            {
                var (refusal, reach) = await ReachAsync(session, token);
                if (reach is null)
                {
                    return new TaskToolResult(refusal!, listId);
                }

                if (!reach.Lists.Any(list => string.Equals(list.Id, listId, StringComparison.OrdinalIgnoreCase)))
                {
                    return new TaskToolResult(AgentToolStatus.Forbidden, listId);
                }

                var now = _clock.GetUtcNow();
                var since = now - AgentLimits.Window;
                if (await session.Agents.CountActionsAsync(_actor, ActivityActions.AgentCreateTask, since, token) >= AgentLimits.TasksAddedPerHour)
                {
                    var first = await session.Agents.FirstActionAsync(_actor, ActivityActions.AgentCreateTask, since, token) ?? now;
                    return new TaskToolResult(AgentToolStatus.RateLimited, listId, (int)Math.Max(1, (first + AgentLimits.Window - now).TotalSeconds));
                }

                return null;
            }, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            try
            {
                var planned = ParseDate(plannedDate, "plannedDate");
                var due = ParseDate(dueDate, "dueDate");
                var parsedPriority = ParsePriority(priority);
                if (title is null || (notes?.Length ?? 0) > MaxText)
                {
                    return new TaskToolResult(AgentToolStatus.Invalid, listId, Field: title is null ? "title" : "notes");
                }

                var created = await Tasks.CaptureAsync(new CaptureTaskCommand(operationId, listId, title, notes), cancellationToken);
                var task = created.Task;
                if (planned is not null || due is not null || estimateMinutes is not null || parsedPriority is not null)
                {
                    task = (await Tasks.UpdateDetailsAsync(Details(task, operationId + ":details") with
                    {
                        PlannedDate = planned, DueDate = due, EstimateMinutes = estimateMinutes, Priority = parsedPriority ?? task.Priority,
                    }, cancellationToken)).Task;
                }

                foreach (var subtask in (subtasks ?? []).Take(50))
                {
                    _ = await Subtasks.AddAsync(new AddSubtaskCommand(Guid.NewGuid().ToString("D"), task.Id, subtask, null), cancellationToken);
                }

                return new TaskToolResult(AgentToolStatus.Created, task.Id);
            }
            catch (AppException error)
            {
                return Failed(error, listId);
            }
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
            var operationId = Guid.NewGuid().ToString("D");
            using var scope = ActivityScope.Open(ActivitySources.Agent, ActivityActions.AgentUpdateTask, operationId: operationId, actorId: _actor);
            var gate = await AuthorizeWriteAsync(taskId, claimOnly: false, requireOwnership: true, cancellationToken);
            if (gate.Task is not { } task)
            {
                return new TaskToolResult(gate.Refusal!, taskId);
            }

            try
            {
                TaskItemStatus? newStatus = null;
                if (status is { Length: > 0 })
                {
                    if (!Statuses.TryGetValue(status, out var parsedStatus))
                    {
                        return new TaskToolResult(AgentToolStatus.Invalid, taskId, Field: "status");
                    }

                    if (parsedStatus is TaskItemStatus.Done or TaskItemStatus.Cancelled && !gate.CloseAllowed)
                    {
                        return new TaskToolResult(AgentToolStatus.Forbidden, taskId, Field: "status");
                    }

                    newStatus = parsedStatus;
                }

                if ((notes?.Length ?? 0) > MaxText)
                {
                    return new TaskToolResult(AgentToolStatus.Invalid, taskId, Field: "notes");
                }

                var details = Details(task, operationId) with
                {
                    Title = title ?? task.Title,
                    Notes = notes ?? task.Notes,
                    PlannedDate = plannedDate is null ? task.PlannedDate : ParseDate(plannedDate, "plannedDate"),
                    DueDate = dueDate is null ? task.DueDate : ParseDate(dueDate, "dueDate"),
                    EstimateMinutes = estimateMinutes ?? task.EstimateMinutes,
                    Priority = ParsePriority(priority) ?? task.Priority,
                };
                var current = task;
                if (title is not null || notes is not null || plannedDate is not null || dueDate is not null || estimateMinutes is not null || priority is not null)
                {
                    current = (await Tasks.UpdateDetailsAsync(details, cancellationToken)).Task;
                }

                if (newStatus is { } target && target != current.Status)
                {
                    _ = await Tasks.SetStatusAsync(new SetTaskStatusCommand(operationId + ":status", current.Id, current.Version, target, OpenSubtaskChoice.KeepOpen), cancellationToken);
                }

                if (gate.Claimant)
                {
                    _ = await HeartbeatAsync(taskId, cancellationToken);
                }

                return new TaskToolResult(AgentToolStatus.Ok, taskId);
            }
            catch (AppException error)
            {
                return Failed(error, taskId);
            }
        }

        /// <summary>An agent's updates on a task linked to a GitHub issue flow to the issue through the runner, signed with the agent's name (12.8).</summary>
        private Task<bool> LinkedToGitHubAsync(string taskId, CancellationToken ct) =>
            _store.QueryAsync(async (session, token) => await session.GitHub.FindLinkByTaskAsync(taskId, token) is { State: 0 }, ct);

        public async Task<TaskToolResult> AddUpdateAsync(
            [Description("The ID of the task.")] string taskId,
            [Description("The diary entry, in Markdown.")] string markdown,
            CancellationToken cancellationToken)
        {
            var operationId = Guid.NewGuid().ToString("D");
            using var scope = ActivityScope.Open(ActivitySources.Agent, ActivityActions.AgentAddUpdate, operationId: operationId, actorId: _actor);
            var gate = await AuthorizeWriteAsync(taskId, claimOnly: false, requireOwnership: false, cancellationToken);
            if (gate.Task is not { } task)
            {
                return new TaskToolResult(gate.Refusal!, taskId);
            }

            try
            {
                _ = await Diary.AddAsync(new AddDiaryEntryCommand(operationId, task.Id, markdown ?? string.Empty, null, PostToGitHub: await LinkedToGitHubAsync(task.Id, cancellationToken)), cancellationToken);
                if (gate.Claimant)
                {
                    _ = await HeartbeatAsync(taskId, cancellationToken);
                }

                return new TaskToolResult(AgentToolStatus.Ok, taskId);
            }
            catch (AppException error)
            {
                return Failed(error, taskId);
            }
        }

        public async Task<TaskToolResult> AskForHelpAsync(
            [Description("The ID of the task this agent holds the claim on.")] string taskId,
            [Description("The question for the person, in Markdown.")] string question,
            CancellationToken cancellationToken)
        {
            var operationId = Guid.NewGuid().ToString("D");
            using var scope = ActivityScope.Open(ActivitySources.Agent, ActivityActions.AgentAskForHelp, operationId: operationId, actorId: _actor);
            var gate = await AuthorizeWriteAsync(taskId, claimOnly: true, requireOwnership: true, cancellationToken);
            if (gate.Task is not { } task)
            {
                return new TaskToolResult(gate.Refusal!, taskId);
            }

            try
            {
                _ = await Diary.AddAsync(new AddDiaryEntryCommand(operationId, task.Id, question ?? string.Empty, null, PostToGitHub: await LinkedToGitHubAsync(task.Id, cancellationToken)), cancellationToken);
                var current = await Tasks.GetAsync(task.Id, cancellationToken) ?? task;
                _ = await Tasks.SetStatusAsync(new SetTaskStatusCommand(operationId + ":status", current.Id, current.Version, TaskItemStatus.WaitingOnSomeone, OpenSubtaskChoice.KeepOpen), cancellationToken);
                _ = await HeartbeatAsync(taskId, cancellationToken);
                return new TaskToolResult(AgentToolStatus.Ok, taskId);
            }
            catch (AppException error)
            {
                return Failed(error, taskId);
            }
        }

        public async Task<TaskToolResult> HandBackAsync(
            [Description("The ID of the task this agent holds the claim on.")] string taskId,
            [Description("What was done and where to look, in Markdown.")] string summary,
            CancellationToken cancellationToken)
        {
            var operationId = Guid.NewGuid().ToString("D");
            using var scope = ActivityScope.Open(ActivitySources.Agent, ActivityActions.AgentHandBack, operationId: operationId, actorId: _actor);
            var gate = await AuthorizeWriteAsync(taskId, claimOnly: true, requireOwnership: true, cancellationToken);
            if (gate.Task is not { } task)
            {
                return new TaskToolResult(gate.Refusal!, taskId);
            }

            try
            {
                _ = await Diary.AddAsync(new AddDiaryEntryCommand(operationId, task.Id, summary ?? string.Empty, null, PostToGitHub: await LinkedToGitHubAsync(task.Id, cancellationToken)), cancellationToken);
                var current = await Tasks.GetAsync(task.Id, cancellationToken) ?? task;
                _ = await Tasks.SetStatusAsync(new SetTaskStatusCommand(operationId + ":status", current.Id, current.Version, TaskItemStatus.NeedsReview, OpenSubtaskChoice.KeepOpen), cancellationToken);
                _ = await ReleaseTaskAsync(taskId, cancellationToken);
                return new TaskToolResult(AgentToolStatus.Ok, taskId);
            }
            catch (AppException error)
            {
                return Failed(error, taskId);
            }
        }

        /// <summary>Reads: the task is in a ticked list of the Personal space and not pointed at another agent.</summary>
        private async Task<(string? Refusal, TaskRecord? Task, Reach? Reach)> AuthorizeAsync(IDataSession session, string taskId, CancellationToken token)
        {
            var (refusal, reach) = await ReachAsync(session, token);
            if (reach is null)
            {
                return (refusal, null, null);
            }

            var task = await session.Tasks.GetAsync(taskId, token);
            if (task is null)
            {
                return (AgentToolStatus.NotFound, null, null);
            }

            if (!reach.Lists.Any(list => list.Id == task.ContainerId)
                || (task.AgentAssigneeId is { Length: > 0 } assigned && !string.Equals(assigned, _agentId, StringComparison.OrdinalIgnoreCase)))
            {
                return (AgentToolStatus.Forbidden, null, null);
            }

            return (null, task, reach);
        }

        private sealed record WriteGate(string? Refusal, TaskRecord? Task, bool Claimant, bool CloseAllowed);

        /// <summary>
        /// Writes (12.2): a live claim by someone else refuses; with <paramref name="requireOwnership"/> the agent must hold the live
        /// claim, or (unless <paramref name="claimOnly"/>) have created the task.
        /// </summary>
        private Task<WriteGate> AuthorizeWriteAsync(string taskId, bool claimOnly, bool requireOwnership, CancellationToken ct) =>
            _store.QueryAsync(async (session, token) =>
            {
                var (refusal, task, reach) = await AuthorizeAsync(session, taskId, token);
                if (task is null || reach is null)
                {
                    return new WriteGate(refusal ?? AgentToolStatus.Forbidden, null, false, false);
                }

                var now = _clock.GetUtcNow();
                var live = task.ClaimedById is not null && task.ClaimExpiresAtUtc > now;
                var claimant = live && string.Equals(task.ClaimedById, _actor, StringComparison.OrdinalIgnoreCase);
                if (live && !claimant)
                {
                    return new WriteGate(AgentToolStatus.AlreadyClaimed, null, false, false);
                }

                if (requireOwnership && !claimant && (claimOnly || !await CreatedByAgentAsync(session, task, token)))
                {
                    return new WriteGate(AgentToolStatus.NotClaimant, null, false, false);
                }

                return new WriteGate(null, task, claimant, reach.Personal.AllowAgentsToClose);
            }, ct);

        private async Task<bool> CreatedByAgentAsync(IDataSession session, TaskRecord task, CancellationToken token) =>
            (await session.History.ListByTaskAsync(task.Id, token)).Any(entry => entry.EventKind == "task.created"
                && string.Equals(entry.Actor, _actor, StringComparison.OrdinalIgnoreCase));

        private static UpdateTaskDetailsCommand Details(TaskRecord task, string operationId) =>
            new(operationId, task.Id, task.Version, task.Title, task.Notes, task.PlannedDate, task.PlannedTime, task.PlannedZoneId,
                task.DueDate, task.DueTime, task.DueZoneId, task.Priority, task.EstimateMinutes, task.CapacityCategory);

        private static DateOnly? ParseDate(string? value, string field) =>
            value is not { Length: > 0 } ? null
            : DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date
            : throw AppException.Validation(field, "Use the format yyyy-MM-dd.");

        private static TaskPriority? ParsePriority(string? value) =>
            value is not { Length: > 0 } ? null
            : Enum.TryParse<TaskPriority>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed
            : throw AppException.Validation("priority", "Use none, low, normal, high or urgent.");

        /// <summary>Only the code and the offending field leave; the message may name user text.</summary>
        private static TaskToolResult Failed(AppException error, string taskId) => error.Kind switch
        {
            AppErrorKind.Validation => new(AgentToolStatus.Invalid, taskId, Field: error.Fields.FirstOrDefault()?.FieldPath),
            AppErrorKind.VersionConflict or AppErrorKind.OperationConflict => new(AgentToolStatus.Conflict, taskId),
            AppErrorKind.NotFound => new(AgentToolStatus.NotFound, taskId),
            _ => throw error,
        };
    }
}
