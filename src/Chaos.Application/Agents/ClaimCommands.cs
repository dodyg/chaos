using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Reminders;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Sync;

namespace Chaos.Application.Agents;

public sealed record ClaimCommand(string OperationId, string TaskId, long ExpectedVersion);
/// <summary>Makes a Personal task Ready for agent, or takes it back; <paramref name="AgentId"/> points it at one local agent.</summary>
public sealed record SetReadyCommand(string OperationId, string TaskId, long ExpectedVersion, bool Ready, string? AgentId);
public sealed record ClaimResult(TaskRecord Task, bool Replayed = false);
public sealed record ExpiredClaimsResult(int Released);

/// <summary>What a claim operation ended with: an error code (<see cref="AgentToolStatus"/>) or the task as it now stands.</summary>
public sealed record ClaimOutcome(string? Error, int? RetryAfterSeconds, TaskRecord? Task);

/// <summary>
/// Claims in the Personal space (PLAN3 12.4), where the device's serialized writer is the compare-and-set: one live claim
/// per task, a claim sets the task In progress, the claimant's heartbeat extends it, a release gives the task back Ready for
/// agent, a person may take a task over, and a claim that outlives its time goes back to the queue with a diary note. Tasks
/// of shared spaces are claimed through the service (<c>POST /v2/tasks/{id}/claim</c>), which does the same under the stream lock.
/// </summary>
public sealed class ClaimCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    private static readonly TimeSpan ClaimTime = TimeSpan.FromMinutes(AgentLimits.DefaultClaimMinutes);

    public Task<ClaimResult> ClaimAsync(ClaimCommand command, CancellationToken ct = default) =>
        PersonAsync(command, ClaimAction.Claim, "claims.claim", ct);

    public Task<ClaimResult> ReleaseAsync(ClaimCommand command, CancellationToken ct = default) =>
        PersonAsync(command, ClaimAction.Release, "claims.release", ct);

    public Task<ClaimResult> TakeOverAsync(ClaimCommand command, CancellationToken ct = default) =>
        PersonAsync(command, ClaimAction.TakeOver, "claims.takeover", ct);

    public Task<ClaimResult> HeartbeatAsync(ClaimCommand command, CancellationToken ct = default) =>
        PersonAsync(command, ClaimAction.Heartbeat, "claims.heartbeat", ct);

    /// <summary>Ready for agent is a person's choice (E5): no tool of an agent can set it.</summary>
    public Task<ClaimResult> SetReadyAsync(SetReadyCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        return TaskMutationSupport.InRecordedTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "claims.ready", command, now, async inner =>
            {
                var task = await session.Tasks.GetAsync(command.TaskId, inner)
                    ?? throw AppException.NotFound("That task no longer exists. Refresh and try again.");
                TaskCommands.RequireCurrentVersion(task, command.ExpectedVersion);
                if (command.Ready && command.AgentId is { } agentId)
                {
                    // A local agent works only in the Personal lists it was given (12.3): it is never pointed at a shared
                    // task, nor at a task in a list it cannot reach, where it could never claim it.
                    var local = await session.Agents.GetIncludingDeletedAsync(agentId, inner);
                    var personal = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, inner);
                    if (local is not null && (personal is null || task.SpaceId != personal.Id))
                    {
                        throw AppException.Validation("agentId", "A local agent works only in your Personal space.");
                    }

                    if ((local is null && personal is not null && task.SpaceId == personal.Id) || local?.DeletedAtUtc is not null)
                    {
                        throw AppException.Validation("agentId", "That agent no longer exists. Refresh and try again.");
                    }

                    if (local is not null && await session.Agents.FindAccessAsync(local.Id, task.ContainerId, inner) is not { DeletedAtUtc: null })
                    {
                        throw AppException.Validation("agentId", "That agent cannot work in this list. Give it the list in Settings → Agents first.");
                    }
                }

                var before = new TaskAggregateSnapshot(task, await session.Subtasks.ListByTaskAsync(task.Id, inner));
                var updated = await TaskCommands.ApplyTaskUpdateAsync(session, task with
                {
                    AgentReady = command.Ready, AgentAssigneeId = command.Ready ? command.AgentId : null, Version = task.Version + 1, UpdatedAtUtc = now,
                }, task.Version, inner);
                await TaskMutationSupport.WriteTaskHistoryAsync(session, ids, command.OperationId, before,
                    new TaskAggregateSnapshot(updated, await session.Subtasks.ListByTaskAsync(task.Id, inner)),
                    command.Ready ? "task.ready-for-agent" : "task.not-ready-for-agent", ["AgentReady"], now, inner);
                return new ClaimResult(updated);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    private Task<ClaimResult> PersonAsync(ClaimCommand command, ClaimAction action, string kind, CancellationToken ct)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        return TaskMutationSupport.InRecordedTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, kind, command with { OperationId = string.Empty }, now, async inner =>
            {
                var task = await RequirePersonalTaskAsync(session, profile, command.TaskId, inner);
                TaskCommands.RequireCurrentVersion(task, command.ExpectedVersion);
                var outcome = await ApplyAsync(session, ids, task, new ActorId(ActorKind.Person, Guid.Parse(profile.Id)), action, now, inner);
                if (outcome.Error is { } error)
                {
                    throw Error(error, task);
                }

                return new ClaimResult(outcome.Task!);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>
    /// The reminder tick's pass (12.4): every Personal claim whose time ran out goes back to the queue with a note.
    /// Shared spaces are the service's (<c>ClaimExpiryService</c>).
    /// </summary>
    public Task<ExpiredClaimsResult> ExpireDueAsync(CancellationToken ct = default) =>
        TaskMutationSupport.InRecordedTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var personal = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token);
            if (personal is null)
            {
                return new ExpiredClaimsResult(0);
            }

            var released = 0;
            foreach (var task in await session.Tasks.ListClaimsExpiredAsync(now, token))
            {
                if (task.SpaceId != personal.Id)
                {
                    continue;
                }

                await ExpireAsync(session, ids, profile, task, now, ending: null, token);
                released++;
            }

            return new ExpiredClaimsResult(released);
        }, ct);

    /// <summary>Ends an agent's claims in the Personal space (it was paused or removed), inside the caller's transaction.</summary>
    internal static async Task ReleaseAgentClaimsAsync(IDataSession session, IIdGenerator ids, string agentId, DateTimeOffset now, string ending, CancellationToken token)
    {
        var profile = await session.Profiles.GetPrimaryAsync(token);
        foreach (var task in await session.Tasks.ListClaimedByAsync($"agent:{agentId}", token))
        {
            await ExpireAsync(session, ids, profile, task, now, ending, token);
        }
    }

    private static async Task ExpireAsync(IDataSession session, IIdGenerator ids, ProfileRecord? profile, TaskRecord task, DateTimeOffset now, string? ending, CancellationToken token)
    {
        var before = new TaskAggregateSnapshot(task, await session.Subtasks.ListByTaskAsync(task.Id, token));
        var updated = await TaskCommands.ApplyTaskUpdateAsync(session, task with
        {
            ClaimedById = null, ClaimedAtUtc = null, ClaimExpiresAtUtc = null,
            Status = task.Status == TaskItemStatus.InProgress ? TaskItemStatus.NotStarted : task.Status,
            Version = task.Version + 1, UpdatedAtUtc = now,
        }, task.Version, token);
        // A system pass, not a person's operation: the history row is written at once and leaves no undo entry.
        var after = new TaskAggregateSnapshot(updated, await session.Subtasks.ListByTaskAsync(task.Id, token));
        await session.History.AddAsync(new TaskHistoryRecord
        {
            Id = ids.NewId().ToString(), TaskId = task.Id, OperationId = string.Empty, EffectiveAtUtc = now,
            Actor = AmbientActorContext.Instance.CurrentActor(profile?.Id ?? string.Empty), EventKind = "task.claim-expired", ChangedFields = ["Claim"],
            SnapshotBefore = StoredJsonFormats.Snapshot(before), SnapshotAfter = StoredJsonFormats.Snapshot(after),
            AggregateVersion = updated.Version, RecordedAtUtc = now,
        }, token);

        // A claim on a task already done or cancelled just goes, without a diary note.
        if (profile is not null && task.Status is not (TaskItemStatus.Done or TaskItemStatus.Cancelled))
        {
            var claimant = Guid.TryParse(task.ClaimedById?[(task.ClaimedById.IndexOf(':') + 1)..], out var id) ? id : Guid.Parse(profile.Id);
            var name = task.ClaimedById!.StartsWith("agent:", StringComparison.Ordinal)
                ? (await session.Agents.GetIncludingDeletedAsync(claimant.ToString("D"), token))?.Name ?? "the agent"
                : profile.DisplayName;
            var text = ending is null ? $"Claim by {name} expired" : $"Claim by {name} ended: {ending}";
            await session.Diary.AddAsync(new DiaryEntryRecord
            {
                Id = ids.NewId().ToString(), TaskId = task.Id, AuthorProfileId = claimant.ToString("D"), EntryAtUtc = now, RawMarkdown = text,
                Revision = 1, Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now,
            }, token);
        }
    }

    /// <summary>
    /// The compare-and-set (5.6): reads the stored claim and writes the new one in the caller's transaction, which the
    /// serialized writer makes exclusive on this device. Returns an error code rather than throwing, so the agent tools
    /// can pass it on as it is.
    /// </summary>
    internal static async Task<ClaimOutcome> ApplyAsync(IDataSession session, IIdGenerator ids, TaskRecord task, ActorId claimant, ClaimAction action, DateTimeOffset now, CancellationToken token)
    {
        if (task.DeletedAtUtc is not null || TaskItemStatusRules.IsTerminal(task.Status))
        {
            return new(AgentToolStatus.NotFound, null, null);
        }

        var me = claimant.ToString();
        var live = task.ClaimedById is not null && task.ClaimExpiresAtUtc > now;
        var same = string.Equals(task.ClaimedById, me, StringComparison.OrdinalIgnoreCase);
        var expires = now + ClaimTime;
        TaskRecord updated;
        string kind;
        switch (action)
        {
            case ClaimAction.Claim when live && !same:
                return new(AgentToolStatus.AlreadyClaimed, null, task);
            case ClaimAction.Claim:
                updated = task with
                {
                    ClaimedById = me, ClaimedAtUtc = now, ClaimExpiresAtUtc = expires,
                    Status = task.Status == TaskItemStatus.NotStarted ? TaskItemStatus.InProgress : task.Status,
                    Version = task.Version + 1, UpdatedAtUtc = now,
                };
                kind = "task.claimed";
                break;
            case ClaimAction.Heartbeat when !live || !same:
                return new(AgentToolStatus.NotClaimant, null, task);
            case ClaimAction.Heartbeat:
                updated = task with { ClaimExpiresAtUtc = expires, Version = task.Version + 1, UpdatedAtUtc = now };
                kind = "task.claim-extended";
                break;
            case ClaimAction.Release when task.ClaimedById is null:
                return new(null, null, task);
            case ClaimAction.Release when !same && claimant.Kind == ActorKind.Agent:
                return new(AgentToolStatus.NotClaimant, null, task);
            case ClaimAction.Release:
                updated = task with
                {
                    ClaimedById = null, ClaimedAtUtc = null, ClaimExpiresAtUtc = null,
                    Status = task.Status == TaskItemStatus.InProgress ? TaskItemStatus.NotStarted : task.Status,
                    Version = task.Version + 1, UpdatedAtUtc = now,
                };
                kind = "task.unclaimed";
                break;
            case ClaimAction.TakeOver:
                updated = task with { ClaimedById = me, ClaimedAtUtc = now, ClaimExpiresAtUtc = expires, Version = task.Version + 1, UpdatedAtUtc = now };
                kind = "task.takeover";
                break;
            default:
                return new(SyncRejection.Invalid, null, task);
        }

        var before = new TaskAggregateSnapshot(task, await session.Subtasks.ListByTaskAsync(task.Id, token));
        var stored = await TaskCommands.ApplyTaskUpdateAsync(session, updated, task.Version, token);
        await TaskMutationSupport.WriteTaskHistoryAsync(session, ids, string.Empty, before,
            new TaskAggregateSnapshot(stored, await session.Subtasks.ListByTaskAsync(task.Id, token)), kind, ["Claim"], now, token);
        return new(null, null, stored);
    }

    private static async Task<TaskRecord> RequirePersonalTaskAsync(IDataSession session, ProfileRecord profile, string taskId, CancellationToken token)
    {
        var task = await session.Tasks.GetAsync(taskId, token)
            ?? throw AppException.NotFound("That task no longer exists. Refresh and try again.");
        var personal = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token);
        if (personal is null || task.SpaceId != personal.Id)
        {
            throw AppException.Validation("taskId", "Tasks in shared spaces are claimed through the service while online.");
        }

        return task;
    }

    private static AppException Error(string code, TaskRecord task) => code switch
    {
        AgentToolStatus.AlreadyClaimed => AppException.OperationConflict($"“{task.Title}” is already claimed."),
        AgentToolStatus.NotClaimant => AppException.OperationConflict("This claim is not yours to change."),
        AgentToolStatus.NotFound => AppException.NotFound("That task no longer exists. Refresh and try again."),
        _ => AppException.Validation("claim", "The claim was refused."),
    };
}
