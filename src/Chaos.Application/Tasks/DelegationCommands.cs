using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.People;
using Chaos.Application.Reminders;
using Chaos.Domain.Reminders;
using Chaos.Domain.Tasks;
using Chaos.Sync;

namespace Chaos.Application.Tasks;

public enum DelegationResponse { Accept = 0, Question = 1, Decline = 2 }

/// <param name="FollowUp">The delegator's own follow-up rule, stored in their account stream with <c>DelegationId</c>; null means none.</param>
public sealed record DelegateCommand(
    string OperationId,
    string TaskId,
    long ExpectedVersion,
    string ToMemberId,
    string? Message,
    string? DoneDefinition,
    DateOnly? DueDate,
    bool ReviewRequired,
    FollowUpSpec? FollowUp);

/// <param name="Text">The question, or the reason for declining. Required for both.</param>
public sealed record RespondDelegationCommand(string OperationId, string DelegationId, long ExpectedVersion, DelegationResponse Response, string? Text);

/// <param name="Summary">What was done, written to the diary. Optional.</param>
public sealed record DoneDelegationCommand(string OperationId, string DelegationId, long ExpectedVersion, string? Summary, OpenSubtaskChoice? OpenSubtaskChoice);

public sealed record CloseDelegationCommand(string OperationId, string DelegationId, long ExpectedVersion);
public sealed record ReopenDelegationCommand(string OperationId, string DelegationId, long ExpectedVersion);
public sealed record WithdrawDelegationCommand(string OperationId, string DelegationId, long ExpectedVersion);

/// <param name="Text">The nudge text; null or blank uses the template for the profile's language.</param>
public sealed record NudgeCommand(string OperationId, string DelegationId, long ExpectedVersion, string? Text);

public sealed record DelegationMutationResult(TaskDelegationRecord Delegation, TaskRecord Task, bool Replayed);

/// <summary>Which side of a delegation the person is on.</summary>
public enum DelegationSide { From = 0, To = 1 }

/// <summary>One delegation with the people it names and the delegator's follow-up, as the task card and the views show it.</summary>
public sealed record DelegationItem(
    TaskDelegationRecord Delegation,
    TaskRecord Task,
    string ContainerName,
    string FromName,
    string ToName,
    FollowUpRecord? FollowUp,
    DateTimeOffset? FollowUpDueAtUtc,
    bool FollowUpOverdue,
    IReadOnlyList<NudgeRecord> Nudges);

public sealed record TaskDelegationView(TaskRecord Task, IReadOnlyList<DelegationItem> Live, IReadOnlyList<DelegationItem> History);

/// <summary>
/// Delegating a task to a member, the recipient's answer, review, taking the work back and
/// nudges (PLAN3 9.11). Every transition follows <see cref="DelegationTransitions"/>, which the
/// service checks again per change. Each move writes a diary entry by the person who made it,
/// so the task's journal tells the whole story; the other party's device derives its notice
/// from the synced delegation row, never from the diary text. The delegator's follow-up is
/// their own, in their account stream: it names the delegation and lands in My Day.
/// </summary>
public sealed class DelegationCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    private const int DiaryLimit = 4_000;

    public async Task<TaskDelegationView> GetForTaskAsync(string taskId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var task = await session.Tasks.GetAsync(taskId, token)
                ?? throw AppException.NotFound("That task no longer exists. Refresh and try again.");
            var people = await session.People.ListAsync(task.SpaceId, token);
            var profile = await ReminderCommands.RequireProfileAsync(session, token);
            var delegations = await session.Delegations.ListByTaskAsync(taskId, token);
            return new TaskDelegationView(task,
                await ItemsAsync(session, profile, people, delegations.Where(d => DelegationTransitions.IsActive(d.State)), token),
                await ItemsAsync(session, profile, people, delegations.Where(d => !DelegationTransitions.IsActive(d.State)), token));
        }, ct);

    /// <summary>The person's live delegations: the ones they handed out ("Delegated by me") or the ones waiting on them.</summary>
    public async Task<IReadOnlyList<DelegationItem>> ListAsync(DelegationSide side, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<DelegationItem>>(async (session, token) =>
        {
            var profile = await ReminderCommands.RequireProfileAsync(session, token);
            var rows = side == DelegationSide.From
                ? await session.Delegations.ListLiveFromAsync(profile.Id, token)
                : await session.Delegations.ListLiveToAsync(profile.Id, token);
            var items = new List<DelegationItem>();
            foreach (var group in rows.GroupBy(row => row.TaskId))
            {
                if (await session.Tasks.GetAsync(group.Key, token) is not { } task) continue;
                var people = await session.People.ListAsync(task.SpaceId, token);
                items.AddRange(await ItemsAsync(session, profile, people, group, token));
            }
            return items;
        }, ct);

    /// <summary>The template text of a nudge in the profile's language, for the dialog to edit.</summary>
    public async Task<string> NudgeTemplateAsync(string delegationId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var delegation = await session.Delegations.GetAsync(delegationId, token)
                ?? throw AppException.NotFound("That delegation no longer exists. Refresh and try again.");
            var task = await session.Tasks.GetAsync(delegation.TaskId, token) ?? throw AppException.NotFound("That task no longer exists.");
            var people = await session.People.ListAsync(task.SpaceId, token);
            var profile = await ReminderCommands.RequireProfileAsync(session, token);
            return NudgeTemplates.For(profile.Locale, Person(people, delegation.ToMemberId).DisplayName, task.Title);
        }, ct);

    /// <summary>Delegates the task: the recipient becomes its assignee, the brief is recorded, and the delegator's follow-up is set.</summary>
    public async Task<DelegationMutationResult> DelegateAsync(DelegateCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        if (string.IsNullOrWhiteSpace(command.ToMemberId)) throw AppException.Validation("toMemberId", "Choose who to delegate to.");
        var normalized = command with
        {
            Message = OptionalText(command.Message, DelegationTransitions.MaxMessageLength, "message"),
            DoneDefinition = OptionalText(command.DoneDefinition, DelegationTransitions.MaxDoneDefinitionLength, "doneDefinition"),
            FollowUp = command.FollowUp is { } spec ? WaitingCommands.Normalize(spec) : null,
        };

        return await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, normalized.OperationId, "delegation.delegate", normalized, now, async inner =>
            {
                var task = await RequireOpenSharedTaskAsync(session, normalized.TaskId, inner);
                TaskCommands.RequireCurrentVersion(task, normalized.ExpectedVersion);
                var delegation = await DelegateInSessionAsync(session, ids, profile, task, normalized.ToMemberId, normalized.Message, normalized.DoneDefinition,
                    normalized.DueDate, normalized.ReviewRequired, normalized.FollowUp, now, inner);
                return new DelegationMutationResult(delegation, await session.Tasks.GetAsync(task.Id, inner) ?? task, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>
    /// Delegates inside the caller's transaction (also used by quick add): the recipient becomes the task's only
    /// assignee, the brief is recorded as a Pending delegation, the delegator's follow-up is set, and the diary and
    /// history note it. An earlier live delegation of the task ends, since the work is handed on.
    /// </summary>
    internal static async Task<TaskDelegationRecord> DelegateInSessionAsync(
        IDataSession session, IIdGenerator ids, ProfileRecord profile, TaskRecord task, string toMemberId, string message, string doneDefinition,
        DateOnly? dueDate, bool reviewRequired, FollowUpSpec? followUp, DateTimeOffset now, CancellationToken inner)
    {
        var people = await session.People.ListAsync(task.SpaceId, inner);
        RequireEditor(people, profile.Id, "Only owners and editors can delegate work in this space.");
        if (string.Equals(toMemberId, profile.Id, StringComparison.OrdinalIgnoreCase))
            throw AppException.Validation("toMemberId", "Choose someone else. To keep the task, leave it assigned to you.");
        var to = people.FirstOrDefault(p => p.ActorId == "person:" + toMemberId && !p.Former && p.Role is 1 or 2)
            ?? people.FirstOrDefault(p => p.ActorId == "agent:" + toMemberId && !p.Former && p.Role == AgentRole);
        var toAgent = to is not null && IsAgent(to);
        var agentSpace = toAgent ? await session.Spaces.GetAsync(task.SpaceId, inner) : null;
        if (to is null) throw AppException.Validation("toMemberId", "Choose a current owner, editor or agent of this space.");
        if (toAgent && agentSpace is not { AgentsAllowed: true })
            throw AppException.Validation("toMemberId", "Agents are not allowed in this space.");
        // A person's review is always asked for, unless the space lets agents close their own tasks (12.8).
        if (toAgent && agentSpace is { AllowAgentsToClose: false }) reviewRequired = true;
        if (followUp is { Kind: FollowUpKind.BeforeDue } && task.DueDate is null)
            throw AppException.Validation("followUp", "This task has no due date. Add one first, or choose another kind of follow-up.");

        var before = new TaskAggregateSnapshot(task, await session.Subtasks.ListByTaskAsync(task.Id, inner));
        if (await session.Delegations.FindLiveForTaskAsync(task.Id, inner) is { } live)
        {
            // Handing the work to someone else: the earlier delegation ends.
            _ = await session.Delegations.UpdateAsync(live with { State = DelegationState.Withdrawn, StateChangedAtUtc = now, Version = live.Version + 1, UpdatedAtUtc = now }, live.Version, inner)
                ?? throw AppException.VersionConflict("The delegation changed elsewhere. Refresh and try again.");
            await ResolveDelegationFollowUpAsync(session, live, now, inner);
        }

        // An agent is no assignee: the task goes to its queue, pointed at it, and the service moves the delegation as it works (12.8).
        if (!toAgent) await MakeSoleAssigneeAsync(session, task.Id, to, now, inner);
        var updated = await TaskCommands.ApplyTaskUpdateAsync(session, toAgent
            ? task with { AgentReady = true, AgentAssigneeId = toMemberId, Version = task.Version + 1, UpdatedAtUtc = now }
            : task with { Version = task.Version + 1, UpdatedAtUtc = now }, task.Version, inner);
        var delegation = new TaskDelegationRecord
        {
            Id = ids.NewId().ToString(),
            TaskId = task.Id,
            FromMemberId = profile.Id,
            ToMemberId = MemberId(to),
            Message = message,
            DoneDefinition = doneDefinition,
            DueDate = dueDate,
            ReviewRequired = reviewRequired,
            State = DelegationState.Pending,
            StateChangedAtUtc = now,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        await session.Delegations.AddAsync(delegation, inner);

        if (followUp is { } rule)
        {
            // One active follow-up per task: the delegator's earlier rule for it ends here.
            if (await session.Waiting.FindActiveFollowUpAsync(task.Id, inner) is { } current)
            {
                _ = await session.Waiting.UpdateFollowUpAsync(current with { ResolvedAtUtc = now, Version = current.Version + 1, UpdatedAtUtc = now }, current.Version, inner)
                    ?? throw AppException.VersionConflict("The follow-up changed elsewhere. Refresh and try again.");
            }
            await session.Waiting.AddFollowUpAsync(new FollowUpRecord
            {
                Id = ids.NewId().ToString(),
                TaskId = task.Id,
                ProfileId = profile.Id,
                Kind = rule.Kind,
                FixedAtUtc = rule.FixedAtUtc,
                Amount = rule.Amount,
                Unit = rule.Unit,
                DelegationId = delegation.Id,
                Version = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            }, inner);
        }

        var head = $"Delegated to {to.DisplayName}" + (toAgent ? " (agent)" : string.Empty) + (delegation.DueDate is { } due ? $" · due {due:yyyy-MM-dd}" : string.Empty) + (delegation.ReviewRequired ? " · review needed" : string.Empty);
        var entry = head + (delegation.Message.Length > 0 ? "\n\n" + delegation.Message : string.Empty)
            + (delegation.DoneDefinition.Length > 0 ? "\n\nDone looks like: " + delegation.DoneDefinition : string.Empty);
        await session.Diary.AddAsync(DiaryEntry(task.Id, profile.Id, entry, now, ids.NewId().ToString()), inner);

        await TaskMutationSupport.WriteTaskHistoryAsync(session, ids, string.Empty, before,
            new TaskAggregateSnapshot(updated, await session.Subtasks.ListByTaskAsync(task.Id, inner)), "task.delegated", [toAgent ? "AgentReady" : "Assignees"], now, inner);
        return delegation;
    }

    /// <summary>The recipient's answer: accept, ask (the question goes to the diary) or decline with a reason (the task returns to the delegator).</summary>
    public async Task<DelegationMutationResult> RespondAsync(RespondDelegationCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        if (!Enum.IsDefined(command.Response)) throw AppException.Validation("response", "Choose accept, question or decline.");
        var text = OptionalText(command.Text, DelegationTransitions.MaxMessageLength, "text");
        if (command.Response != DelegationResponse.Accept && string.IsNullOrEmpty(text))
            throw AppException.Validation("text", command.Response == DelegationResponse.Decline ? "Give a reason for declining." : "Write your question.");
        var normalized = command with { Text = text };

        return await TransitionAsync(normalized.OperationId, "delegation.respond", normalized, normalized.DelegationId, async (session, profile, now, token) =>
        {
            var (delegation, task, people, before) = await LoadAsync(session, normalized.DelegationId, token);
            RequireParty(delegation, delegation.ToMemberId, profile.Id, "Only the person this was delegated to can answer.");
            var target = normalized.Response switch
            {
                DelegationResponse.Accept => DelegationState.Accepted,
                DelegationResponse.Question => DelegationState.Question,
                _ => DelegationState.Declined,
            };
            RequireTransition(delegation, target, DelegationParty.To);
            if (target != DelegationState.Declined) RequireEditor(people, profile.Id, "Only owners and editors can take on work in this space.");

            var from = Person(people, delegation.FromMemberId);
            var moved = delegation with { State = target, StateChangedAtUtc = now, Response = target == DelegationState.Accepted ? null : normalized.Text, Version = delegation.Version + 1, UpdatedAtUtc = now };
            _ = await session.Delegations.UpdateAsync(moved, delegation.Version, token) ?? throw AppException.VersionConflict("The delegation changed elsewhere. Refresh and try again.");
            var updated = task;
            var fields = new List<string>();
            var entry = normalized.Response switch
            {
                DelegationResponse.Accept => $"Accepted the delegation from {from.DisplayName}.",
                DelegationResponse.Question => $"Asked about the task: {normalized.Text}",
                _ => $"Declined the delegation from {from.DisplayName}: {normalized.Text}",
            };
            if (target == DelegationState.Declined)
            {
                // Declining returns the task to the delegator, while they can still hold it.
                await ReturnTaskAsync(session, task.Id, delegation.ToMemberId, from, now, token);
                updated = await TaskCommands.ApplyTaskUpdateAsync(session, task with { Version = task.Version + 1, UpdatedAtUtc = now }, task.Version, token);
                fields.Add("Assignees");
                await ResolveDelegationFollowUpAsync(session, delegation, now, token);
            }
            await session.Diary.AddAsync(DiaryEntry(task.Id, profile.Id, entry, now, ids.NewId().ToString()), token);
            await TaskMutationSupport.WriteTaskHistoryAsync(session, ids, normalized.OperationId, before,
                new TaskAggregateSnapshot(updated, await session.Subtasks.ListByTaskAsync(task.Id, token)), $"task.delegation-{normalized.Response.ToString().ToLowerInvariant()}", fields, now, token);
            return new DelegationMutationResult(moved, updated, Replayed: false);
        }, ct);
    }

    /// <summary>The recipient reports the work done: Needs review when the delegator asked for a review, otherwise the task is completed.</summary>
    public async Task<DelegationMutationResult> DoneAsync(DoneDelegationCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        var normalized = command with { Summary = OptionalText(command.Summary, DelegationTransitions.MaxMessageLength, "summary") };
        return await TransitionAsync(normalized.OperationId, "delegation.done", normalized, normalized.DelegationId, async (session, profile, now, token) =>
        {
            var (delegation, task, people, before) = await LoadAsync(session, normalized.DelegationId, token);
            RequireParty(delegation, delegation.ToMemberId, profile.Id, "Only the person this was delegated to can report it done.");
            RequireTransition(delegation, DelegationState.Done, DelegationParty.To);
            RequireEditor(people, profile.Id, "Only owners and editors can change the task in this space.");
            var moved = delegation with { State = DelegationState.Done, StateChangedAtUtc = now, Version = delegation.Version + 1, UpdatedAtUtc = now };
            _ = await session.Delegations.UpdateAsync(moved, delegation.Version, token) ?? throw AppException.VersionConflict("The delegation changed elsewhere. Refresh and try again.");

            var status = delegation.ReviewRequired ? TaskItemStatus.NeedsReview : TaskItemStatus.Done;
            var updated = await TaskCommands.ApplyStatusAsync(session, ids, task, status, normalized.OpenSubtaskChoice, now,
                await session.Subtasks.ListByTaskAsync(task.Id, token), token, restorePreviousStatus: false);
            await session.Diary.AddAsync(DiaryEntry(task.Id, profile.Id,
                (delegation.ReviewRequired ? "Reported done, ready for review" : "Reported done") + (string.IsNullOrEmpty(normalized.Summary) ? "." : $": {normalized.Summary}"), now, ids.NewId().ToString()), token);
            await TaskMutationSupport.WriteTaskHistoryAsync(session, ids, normalized.OperationId, before,
                new TaskAggregateSnapshot(updated, await session.Subtasks.ListByTaskAsync(task.Id, token)), "task.delegation-done", ["Status"], now, token);
            await ResolveDelegationFollowUpAsync(session, delegation, now, token);
            return new DelegationMutationResult(moved, updated, Replayed: false);
        }, ct);
    }

    /// <summary>The delegator accepts a reviewed result: the delegation is closed and the task completed.</summary>
    public async Task<DelegationMutationResult> CloseAsync(CloseDelegationCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        return await TransitionAsync(command.OperationId, "delegation.close", command, command.DelegationId, async (session, profile, now, token) =>
        {
            var (delegation, task, _, before) = await LoadAsync(session, command.DelegationId, token);
            RequireParty(delegation, delegation.FromMemberId, profile.Id, "Only the person who delegated this can close it.");
            RequireTransition(delegation, DelegationState.Closed, DelegationParty.From);
            var moved = delegation with { State = DelegationState.Closed, StateChangedAtUtc = now, Version = delegation.Version + 1, UpdatedAtUtc = now };
            _ = await session.Delegations.UpdateAsync(moved, delegation.Version, token) ?? throw AppException.VersionConflict("The delegation changed elsewhere. Refresh and try again.");
            var updated = task;
            if (!task.IsTerminal)
            {
                updated = await TaskCommands.ApplyStatusAsync(session, ids, task, TaskItemStatus.Done, OpenSubtaskChoice.KeepOpen, now,
                    await session.Subtasks.ListByTaskAsync(task.Id, token), token, restorePreviousStatus: false);
            }
            await session.Diary.AddAsync(DiaryEntry(task.Id, profile.Id, "Accepted the result and closed the delegation.", now, ids.NewId().ToString()), token);
            await TaskMutationSupport.WriteTaskHistoryAsync(session, ids, command.OperationId, before,
                new TaskAggregateSnapshot(updated, await session.Subtasks.ListByTaskAsync(task.Id, token)), "task.delegation-closed", ["Status"], now, token);
            return new DelegationMutationResult(moved, updated, Replayed: false);
        }, ct);
    }

    /// <summary>The delegator sends a reviewed result back: the delegation is accepted again and the task is open again.</summary>
    public async Task<DelegationMutationResult> ReopenAsync(ReopenDelegationCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        return await TransitionAsync(command.OperationId, "delegation.reopen", command, command.DelegationId, async (session, profile, now, token) =>
        {
            var (delegation, task, _, before) = await LoadAsync(session, command.DelegationId, token);
            RequireParty(delegation, delegation.FromMemberId, profile.Id, "Only the person who delegated this can send it back.");
            RequireTransition(delegation, DelegationState.Accepted, DelegationParty.From);
            var moved = delegation with { State = DelegationState.Accepted, StateChangedAtUtc = now, Version = delegation.Version + 1, UpdatedAtUtc = now };
            _ = await session.Delegations.UpdateAsync(moved, delegation.Version, token) ?? throw AppException.VersionConflict("The delegation changed elsewhere. Refresh and try again.");
            var updated = task.IsTerminal
                ? await TaskCommands.ApplyStatusAsync(session, ids, task, TaskItemStatus.InProgress, null, now, await session.Subtasks.ListByTaskAsync(task.Id, token), token)
                : task;
            await session.Diary.AddAsync(DiaryEntry(task.Id, profile.Id, "Sent the result back for more work.", now, ids.NewId().ToString()), token);
            await TaskMutationSupport.WriteTaskHistoryAsync(session, ids, command.OperationId, before,
                new TaskAggregateSnapshot(updated, await session.Subtasks.ListByTaskAsync(task.Id, token)), "task.delegation-reopened", ["Status"], now, token);
            return new DelegationMutationResult(moved, updated, Replayed: false);
        }, ct);
    }

    /// <summary>The delegator takes the work back: they become the assignee again.</summary>
    public async Task<DelegationMutationResult> WithdrawAsync(WithdrawDelegationCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        return await TransitionAsync(command.OperationId, "delegation.withdraw", command, command.DelegationId, async (session, profile, now, token) =>
        {
            var (delegation, task, people, before) = await LoadAsync(session, command.DelegationId, token);
            RequireParty(delegation, delegation.FromMemberId, profile.Id, "Only the person who delegated this can take it back.");
            RequireTransition(delegation, DelegationState.Withdrawn, DelegationParty.From);
            var me = RequireEditor(people, profile.Id, "Only owners and editors can take on work in this space.");
            var moved = delegation with { State = DelegationState.Withdrawn, StateChangedAtUtc = now, Version = delegation.Version + 1, UpdatedAtUtc = now };
            _ = await session.Delegations.UpdateAsync(moved, delegation.Version, token) ?? throw AppException.VersionConflict("The delegation changed elsewhere. Refresh and try again.");
            var toAgent = people.Any(person => IsAgent(person) && string.Equals(MemberId(person), delegation.ToMemberId, StringComparison.OrdinalIgnoreCase));
            if (!toAgent) await MakeSoleAssigneeAsync(session, task.Id, me, now, token);
            // Taking the work back from an agent also takes the task out of its queue.
            var updated = await TaskCommands.ApplyTaskUpdateAsync(session, toAgent
                ? task with { AgentReady = false, AgentAssigneeId = null, Version = task.Version + 1, UpdatedAtUtc = now }
                : task with { Version = task.Version + 1, UpdatedAtUtc = now }, task.Version, token);
            await session.Diary.AddAsync(DiaryEntry(task.Id, profile.Id, $"Took the task back from {Person(people, delegation.ToMemberId).DisplayName}.", now, ids.NewId().ToString()), token);
            await TaskMutationSupport.WriteTaskHistoryAsync(session, ids, command.OperationId, before,
                new TaskAggregateSnapshot(updated, await session.Subtasks.ListByTaskAsync(task.Id, token)), "task.delegation-withdrawn", ["Assignees"], now, token);
            await ResolveDelegationFollowUpAsync(session, delegation, now, token);
            return new DelegationMutationResult(moved, updated, Replayed: false);
        }, ct);
    }

    /// <summary>
    /// Nudges the recipient. The text is the template in the profile's language unless edited. At most
    /// <see cref="DelegationTransitions.NudgesPerDelegation"/> in <see cref="DelegationTransitions.NudgeWindow"/>; the
    /// service enforces the same limit. Nothing is sent anywhere: the recipient's device derives a notice.
    /// </summary>
    public async Task<DelegationMutationResult> NudgeAsync(NudgeCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        var normalized = command with { Text = OptionalText(command.Text, DelegationTransitions.MaxNudgeLength, "text") };
        return await TaskMutationSupport.InRecordedTransactionAsync(store, clock, ids, async (recording, profile, now, token) =>
        {
            recording.SuppressUndoEntry = true; // a sent nudge cannot be unsent
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(recording, profile.Id, normalized.OperationId, "delegation.nudge", normalized, now, async inner =>
            {
                var (delegation, task, people, _) = await LoadAsync(recording, normalized.DelegationId, inner);
                RequireParty(delegation, delegation.FromMemberId, profile.Id, "Only the person who delegated this can nudge.");
                if (!DelegationTransitions.IsAwaitingRecipient(delegation.State))
                    throw AppException.Validation("delegationId", $"This delegation is {Describe(delegation.State)}; there is nothing to nudge about.");
                var to = Person(people, delegation.ToMemberId);
                if (IsAgent(to)) throw AppException.Validation("delegationId", "An agent does not read nudges. It picks the task up from its queue.");
                var recent = (await recording.Delegations.ListNudgesAsync(delegation.Id, inner)).Count(n => n.SentAtUtc > now - DelegationTransitions.NudgeWindow);
                if (recent >= DelegationTransitions.NudgesPerDelegation)
                    throw AppException.Validation("text", $"You have nudged {to.DisplayName} {recent} times in the last 24 hours. The limit is {DelegationTransitions.NudgesPerDelegation}.");
                var template = NudgeTemplates.For(profile.Locale, to.DisplayName, task.Title);
                var text = string.IsNullOrEmpty(normalized.Text) ? template : normalized.Text;
                await recording.Delegations.AddNudgeAsync(new NudgeRecord
                {
                    Id = ids.NewId().ToString(),
                    TaskId = task.Id,
                    DelegationId = delegation.Id,
                    FromMemberId = profile.Id,
                    ToMemberId = delegation.ToMemberId,
                    Text = text,
                    SentAtUtc = now,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                }, inner);
                await recording.Diary.AddAsync(DiaryEntry(task.Id, profile.Id, $"Nudged {to.DisplayName}." + (text == template ? string.Empty : $" {text}"), now, ids.NewId().ToString()), inner);
                await TaskMutationSupport.WriteTaskHistoryAsync(recording, ids, normalized.OperationId, null,
                    new TaskAggregateSnapshot(task, await recording.Subtasks.ListByTaskAsync(task.Id, inner)), "task.nudged", ["Diary"], now, inner);
                return new DelegationMutationResult(delegation, task, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    // ---- Shared pieces ---------------------------------------------------------------------

    private async Task<DelegationMutationResult> TransitionAsync<T>(string operationId, string kind, T payload, string delegationId,
        Func<IDataSession, ProfileRecord, DateTimeOffset, CancellationToken, Task<DelegationMutationResult>> body, CancellationToken ct) =>
        await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, operationId, kind, payload!, now, inner => body(session, profile, now, inner), token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);

    private static async Task<(TaskDelegationRecord Delegation, TaskRecord Task, IReadOnlyList<SpacePerson> People, TaskAggregateSnapshot Before)> LoadAsync(
        IDataSession session, string delegationId, CancellationToken token)
    {
        var delegation = await session.Delegations.GetAsync(delegationId, token)
            ?? throw AppException.NotFound("That delegation no longer exists. Refresh and try again.");
        if (delegation.DeletedAtUtc is not null) throw AppException.Validation("delegationId", "This delegation was retired.");
        var task = await session.Tasks.GetAsync(delegation.TaskId, token)
            ?? throw AppException.NotFound("That task no longer exists. Refresh and try again.");
        return (delegation, task, await session.People.ListAsync(task.SpaceId, token),
            new TaskAggregateSnapshot(task, await session.Subtasks.ListByTaskAsync(task.Id, token)));
    }

    private static void RequireParty(TaskDelegationRecord delegation, string partyMemberId, string profileId, string message)
    {
        if (!string.Equals(partyMemberId, profileId, StringComparison.OrdinalIgnoreCase))
            throw AppException.Validation("delegationId", message);
    }

    private static void RequireTransition(TaskDelegationRecord delegation, DelegationState target, DelegationParty party)
    {
        if (DelegationTransitions.Actor(delegation.State, target) != party)
            throw AppException.Validation("delegationId", $"This delegation is {Describe(delegation.State)}, so that move is not available.");
    }

    /// <summary>The delegation states in plain words, for refusals.</summary>
    public static string Describe(DelegationState state) => state switch
    {
        DelegationState.Pending => "waiting for an answer",
        DelegationState.Accepted => "accepted",
        DelegationState.Question => "waiting for an answer to a question",
        DelegationState.Declined => "declined",
        DelegationState.Done => "reported done",
        DelegationState.Closed => "closed",
        _ => "withdrawn",
    };

    private static async Task<TaskRecord> RequireOpenSharedTaskAsync(IDataSession session, string taskId, CancellationToken token)
    {
        var task = await session.Tasks.GetAsync(taskId, token)
            ?? throw AppException.NotFound("That task no longer exists. Refresh and try again.");
        if (task.IsTerminal) throw AppException.Validation("taskId", "This task is finished. Reopen it first.");
        var space = await session.Spaces.GetAsync(task.SpaceId, token);
        if (space?.Kind != SpaceKind.Shared)
            throw AppException.Validation("taskId", "Delegation is available in shared spaces. In Personal, use waiting on instead.");
        return task;
    }

    /// <summary>The member's actor ID without its <c>person:</c> prefix, as assignee rows store it.</summary>
    private static string MemberId(SpacePerson person) =>
        person.ActorId.StartsWith("person:", StringComparison.Ordinal) ? person.ActorId[7..] : person.ActorId.StartsWith("agent:", StringComparison.Ordinal) ? person.ActorId[6..] : person.ActorId;

    /// <summary>The Agent role of a space member (PLAN3 8.4).</summary>
    private const int AgentRole = 4;

    private static bool IsAgent(SpacePerson person) => person.ActorId.StartsWith("agent:", StringComparison.Ordinal);

    private static SpacePerson RequireEditor(IReadOnlyList<SpacePerson> people, string memberId, string message) =>
        people.FirstOrDefault(p => p.ActorId == "person:" + memberId && !p.Former && p.Role is 1 or 2)
            ?? throw AppException.Validation("memberId", message);

    private static SpacePerson Person(IReadOnlyList<SpacePerson> people, string memberId) =>
        people.FirstOrDefault(p => string.Equals(p.ActorId, "person:" + memberId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.ActorId, "agent:" + memberId, StringComparison.OrdinalIgnoreCase))
            ?? new SpacePerson("person:" + memberId, "Unknown person", 0, true, null);

    /// <summary>Makes <paramref name="next"/> the task's only live assignee: every other live row is tombstoned, theirs is created or restored.</summary>
    private static async Task MakeSoleAssigneeAsync(IDataSession session, string taskId, SpacePerson next, DateTimeOffset now, CancellationToken token)
    {
        var rows = await session.Tasks.ListAssigneesAsync(taskId, token);
        var memberId = MemberId(next);
        foreach (var row in rows.Where(row => row.DeletedAtUtc is null && !string.Equals(row.MemberId, memberId, StringComparison.OrdinalIgnoreCase)))
        {
            await session.Tasks.SaveAssigneeAsync(row with { DeletedAtUtc = now }, token);
        }
        var existing = rows.FirstOrDefault(row => string.Equals(row.MemberId, memberId, StringComparison.OrdinalIgnoreCase));
        if (existing is null || existing.DeletedAtUtc is not null)
        {
            await session.Tasks.SaveAssigneeAsync(existing is null ? new TaskAssigneeRecord(taskId, memberId, now, null, null) : existing with { DeletedAtUtc = null, AddedAtUtc = now, CompletedAtUtc = null }, token);
        }
    }

    /// <summary>Declining hands the task back: the recipient's assignment ends, and the delegator takes it while they still may.</summary>
    private static async Task ReturnTaskAsync(IDataSession session, string taskId, string recipientMemberId, SpacePerson delegator, DateTimeOffset now, CancellationToken token)
    {
        foreach (var row in await session.Tasks.ListAssigneesAsync(taskId, token))
        {
            if (row.DeletedAtUtc is null && string.Equals(row.MemberId, recipientMemberId, StringComparison.OrdinalIgnoreCase))
                await session.Tasks.SaveAssigneeAsync(row with { DeletedAtUtc = now }, token);
        }
        if (delegator.Former || delegator.Role is not (1 or 2)) return;
        await MakeSoleAssigneeAsync(session, taskId, delegator, now, token);
    }

    /// <summary>Ends the delegator's follow-up of a delegation that is over (declined, taken back, done or handed on).</summary>
    private static async Task ResolveDelegationFollowUpAsync(IDataSession session, TaskDelegationRecord delegation, DateTimeOffset now, CancellationToken token)
    {
        var followUp = await session.Waiting.FindActiveFollowUpAsync(delegation.TaskId, token);
        if (followUp is null || !string.Equals(followUp.DelegationId, delegation.Id, StringComparison.OrdinalIgnoreCase)) return;
        _ = await session.Waiting.UpdateFollowUpAsync(followUp with { ResolvedAtUtc = now, Version = followUp.Version + 1, UpdatedAtUtc = now }, followUp.Version, token)
            ?? throw AppException.VersionConflict("The follow-up changed elsewhere. Refresh and try again.");
    }

    private static DiaryEntryRecord DiaryEntry(string taskId, string authorId, string markdown, DateTimeOffset now, string id) => new()
    {
        Id = id,
        TaskId = taskId,
        AuthorProfileId = authorId,
        EntryAtUtc = now,
        RawMarkdown = markdown.Length > DiaryLimit ? markdown[..DiaryLimit] : markdown,
        Revision = 1,
        Version = 1,
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
    };

    private static string OptionalText(string? value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        if (trimmed.Length > max) throw AppException.Validation(field, $"At most {max:N0} characters.");
        return trimmed;
    }

    private async Task<IReadOnlyList<DelegationItem>> ItemsAsync(IDataSession session, ProfileRecord profile, IReadOnlyList<SpacePerson> people,
        IEnumerable<TaskDelegationRecord> delegations, CancellationToken token)
    {
        var items = new List<DelegationItem>();
        var followUps = await session.Waiting.ListActiveFollowUpsAsync(profile.Id, token);
        var zone = ReminderSync.ProfileZone(profile);
        var now = clock.GetUtcNow();
        foreach (var delegation in delegations)
        {
            var task = await session.Tasks.GetAsync(delegation.TaskId, token);
            if (task is null) continue;
            var container = await session.Containers.GetAsync(task.ContainerId, token);
            var followUp = followUps.FirstOrDefault(row => string.Equals(row.DelegationId, delegation.Id, StringComparison.OrdinalIgnoreCase));
            DateTimeOffset? due = null;
            if (followUp is not null)
                due = (await ReminderSync.FollowUpDueAsync(session, followUp, task, null, profile, zone, token)).Due;
            items.Add(new DelegationItem(delegation, task, container?.Name ?? string.Empty, Person(people, delegation.FromMemberId).DisplayName,
                Person(people, delegation.ToMemberId).DisplayName, followUp, due, due is { } at && at <= now && !task.IsTerminal,
                await session.Delegations.ListNudgesAsync(delegation.Id, token)));
        }
        return items;
    }
}

/// <summary>
/// The prewritten nudge text in the six quick-add languages (PLAN 1 decision 2), chosen by the
/// primary subtag of the profile's locale; any other language gets English. Always editable before sending.
/// </summary>
public static class NudgeTemplates
{
    public static string For(string? locale, string toName, string taskTitle)
    {
        var name = toName.Trim();
        var primary = (locale ?? string.Empty).Split('-', '_')[0].Trim().ToLowerInvariant();
        return primary switch
        {
            "fr" => $"Bonjour {name}, des nouvelles de « {taskTitle} » ?",
            "it" => $"Ciao {name}, ci sono novità su «{taskTitle}»?",
            "es" => $"Hola {name}, ¿hay novedades de «{taskTitle}»?",
            "id" => $"Halo {name}, ada kabar tentang \"{taskTitle}\"?",
            "ar" => $"مرحبًا {name}، هل من أخبار عن «{taskTitle}»؟",
            _ => $"Hi {name}, any news on \"{taskTitle}\"?",
        };
    }
}
