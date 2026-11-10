using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Queries;
using Chaos.Application.Tasks;
using Chaos.Domain.Reminders;
using Chaos.Domain.Tasks;
using Chaos.Domain.Validation;

namespace Chaos.Application.Reminders;

/// <summary>A follow-up rule as entered: a fixed instant, or an amount of units after waiting started, before the due date, or days without activity.</summary>
public sealed record FollowUpSpec(FollowUpKind Kind, DateTimeOffset? FixedAtUtc, int? Amount, DurationUnit? Unit);

/// <param name="FollowUp">The follow-up rule; null means no follow-up (an existing one is resolved).</param>
public sealed record SetWaitingCommand(string OperationId, string TaskId, long ExpectedVersion, string Person, FollowUpSpec? FollowUp);

/// <param name="NextStatus">The status the task takes after waiting; In progress unless chosen otherwise.</param>
public sealed record ClearWaitingCommand(string OperationId, string TaskId, long ExpectedVersion, TaskItemStatus? NextStatus = null);

/// <summary>Records that the person followed up: a private diary update on the task.</summary>
public sealed record LogFollowUpCommand(string OperationId, string TaskId, string Note);

public sealed record SnoozeFollowUpCommand(string OperationId, string TaskId, SnoozeChoice Choice, DateTimeOffset? UntilUtc);

/// <summary>A task's waiting state: who, since when, its follow-up and when that is due (or why it is suspended).</summary>
public sealed record WaitingView(
    TaskRecord Task,
    WaitingDetailRecord? Waiting,
    FollowUpRecord? FollowUp,
    DateTimeOffset? FollowUpDueAtUtc,
    ReminderSuspension FollowUpSuspension,
    DateTimeOffset? LastActivityAtUtc);

public sealed record WaitingMutationResult(WaitingView View, bool Replayed);

/// <summary>A follow-up shown in My Day: a separate item that refers to the original task.</summary>
public sealed record FollowUpItem(TaskRecord Task, string ContainerName, string? Person, FollowUpRecord FollowUp, DateTimeOffset DueAtUtc, bool Overdue);

/// <summary>
/// Waiting on people outside the app and following up with them (PLAN §11). Only free text
/// is stored and nothing is ever sent: no contact lookup, no message, no network. Every
/// change is a recorded operation (history and undo), and follow-up notifications are
/// rescheduled in the same transaction. Logging a follow-up writes a private diary update,
/// which counts as task activity and so restarts an inactivity follow-up; a one-off follow-up
/// (fixed, after waiting, before due) is resolved by the log.
/// </summary>
public sealed class WaitingCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public const int MaxPersonLength = 200;
    private const int MaxLogLength = 4_000;

    public async Task<WaitingView> GetAsync(string taskId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var profile = await ReminderCommands.RequireProfileAsync(session, token);
            var task = await session.Tasks.GetIncludingDeletedAsync(taskId, token)
                ?? throw AppException.NotFound("That task no longer exists. Refresh and try again.");
            return await ViewAsync(session, profile, task, token);
        }, ct);

    /// <summary>Sets (or changes) who the task waits on and its follow-up; the task takes the Waiting status.</summary>
    public async Task<WaitingMutationResult> SetAsync(SetWaitingCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        var person = (command.Person ?? string.Empty).Trim();
        if (person.Length == 0)
        {
            throw AppException.Validation("person", "Name the person you are waiting on.");
        }

        if (person.Length > MaxPersonLength)
        {
            throw AppException.Validation("person", $"A waiting person's name can hold at most {MaxPersonLength} characters.");
        }

        var normalized = command with { Person = person, FollowUp = command.FollowUp is { } spec ? Normalize(spec) : null };
        return await MutateAsync(normalized.OperationId, "waiting.set", normalized, normalized.TaskId, async (session, profile, now, token) =>
        {
            var task = await RequireOpenTaskAsync(session, normalized.TaskId, token);
            if (task.Version != normalized.ExpectedVersion)
            {
                throw AppException.VersionConflict("This task changed elsewhere. Refresh and try again.");
            }

            if (normalized.FollowUp is { Kind: FollowUpKind.BeforeDue } && task.DueDate is null)
            {
                throw AppException.Validation("followUp", "This task has no due date. Add one first, or choose another kind of follow-up.");
            }

            if (task.Status != TaskItemStatus.WaitingOnSomeone)
            {
                _ = await session.Tasks.UpdateAsync(task with { Status = TaskItemStatus.WaitingOnSomeone, Version = task.Version + 1, UpdatedAtUtc = now }, task.Version, token)
                    ?? throw AppException.VersionConflict("This task changed elsewhere. Refresh and try again.");
            }

            await SetInSessionAsync(session, ids, profile, task.Id, person, normalized.FollowUp, now, token);
        }, ct);
    }

    /// <summary>
    /// Writes the waiting details and follow-up of one task inside the caller's transaction
    /// (also used by quick add). An unchanged person keeps the original waiting start.
    /// </summary>
    internal static async Task SetInSessionAsync(IDataSession session, IIdGenerator ids, ProfileRecord profile, string taskId, string person,
        FollowUpSpec? followUp, DateTimeOffset now, CancellationToken token)
    {
        var waiting = await session.Waiting.FindActiveAsync(taskId, token);
        if (waiting is null)
        {
            await session.Waiting.AddAsync(new WaitingDetailRecord
            {
                Id = ids.NewId().ToString(),
                TaskId = taskId,
                ProfileId = profile.Id,
                Person = person,
                StartedAtUtc = now,
                Version = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            }, token);
        }
        else if (waiting.Person != person)
        {
            _ = await session.Waiting.UpdateAsync(waiting with { Person = person, Version = waiting.Version + 1, UpdatedAtUtc = now }, waiting.Version, token)
                ?? throw AppException.VersionConflict("The waiting details changed elsewhere. Refresh and try again.");
        }

        var current = await session.Waiting.FindActiveFollowUpAsync(taskId, token);
        if (current is not null && followUp is not null && SameRule(current, followUp))
        {
            return;
        }

        if (current is not null)
        {
            _ = await session.Waiting.UpdateFollowUpAsync(current with { ResolvedAtUtc = now, Version = current.Version + 1, UpdatedAtUtc = now }, current.Version, token)
                ?? throw AppException.VersionConflict("The follow-up changed elsewhere. Refresh and try again.");
        }

        if (followUp is not null)
        {
            await session.Waiting.AddFollowUpAsync(new FollowUpRecord
            {
                Id = ids.NewId().ToString(),
                TaskId = taskId,
                ProfileId = profile.Id,
                Kind = followUp.Kind,
                FixedAtUtc = followUp.FixedAtUtc,
                Amount = followUp.Amount,
                Unit = followUp.Unit,
                Version = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            }, token);
        }
    }

    /// <summary>Stops waiting: the task leaves the Waiting status, its details end and its follow-up is resolved. History keeps both.</summary>
    public async Task<WaitingMutationResult> ClearAsync(ClearWaitingCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        var next = command.NextStatus ?? TaskItemStatus.InProgress;
        if (next is TaskItemStatus.WaitingOnSomeone || TaskItemStatusRules.IsTerminal(next) || !Enum.IsDefined(next))
        {
            throw AppException.Validation("nextStatus", "Choose Not started, In progress or Needs review. Use Done or Cancel on the task itself.");
        }

        var normalized = command with { NextStatus = next };
        return await MutateAsync(normalized.OperationId, "waiting.clear", normalized, normalized.TaskId, async (session, profile, now, token) =>
        {
            var task = await session.Tasks.GetAsync(normalized.TaskId, token)
                ?? throw AppException.NotFound("That task no longer exists. Refresh and try again.");
            if (task.Version != normalized.ExpectedVersion)
            {
                throw AppException.VersionConflict("This task changed elsewhere. Refresh and try again.");
            }

            if (task.Status == TaskItemStatus.WaitingOnSomeone)
            {
                _ = await session.Tasks.UpdateAsync(task with { Status = next, Version = task.Version + 1, UpdatedAtUtc = now }, task.Version, token)
                    ?? throw AppException.VersionConflict("This task changed elsewhere. Refresh and try again.");
            }

            await ReminderSync.EndWaitingAsync(session, task.Id, now, token);
        }, ct);
    }

    /// <summary>Writes a private diary update for the follow-up. Nothing is sent to anyone.</summary>
    public async Task<WaitingMutationResult> LogAsync(LogFollowUpCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        var note = (command.Note ?? string.Empty).Trim();
        if (note.Length > MaxLogLength)
        {
            throw AppException.Validation("note", $"A follow-up note can hold at most {MaxLogLength:N0} characters.");
        }

        var normalized = command with { Note = note };
        return await MutateAsync(normalized.OperationId, "waiting.log", normalized, normalized.TaskId, async (session, profile, now, token) =>
        {
            var task = await RequireOpenTaskAsync(session, normalized.TaskId, token);
            var waiting = await session.Waiting.FindActiveAsync(task.Id, token);
            var who = waiting is null ? "Followed up" : $"Followed up with {waiting.Person}";
            await session.Diary.AddAsync(new DiaryEntryRecord
            {
                Id = ids.NewId().ToString(),
                TaskId = task.Id,
                AuthorProfileId = profile.Id,
                EntryAtUtc = now,
                RawMarkdown = note.Length == 0 ? who + "." : $"{who}: {note}",
                Revision = 1,
                Version = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            }, token);

            var followUp = await session.Waiting.FindActiveFollowUpAsync(task.Id, token);
            if (followUp is not null && followUp.Kind != FollowUpKind.AfterInactivity)
            {
                _ = await session.Waiting.UpdateFollowUpAsync(followUp with { ResolvedAtUtc = now, Version = followUp.Version + 1, UpdatedAtUtc = now }, followUp.Version, token)
                    ?? throw AppException.VersionConflict("The follow-up changed elsewhere. Refresh and try again.");
            }
            else if (followUp is { SnoozedUntilUtc: not null })
            {
                // An inactivity follow-up restarts from this update; an older snooze no longer applies.
                _ = await session.Waiting.UpdateFollowUpAsync(followUp with { SnoozedUntilUtc = null, Version = followUp.Version + 1, UpdatedAtUtc = now }, followUp.Version, token)
                    ?? throw AppException.VersionConflict("The follow-up changed elsewhere. Refresh and try again.");
            }
        }, ct);
    }

    /// <summary>Moves the follow-up later. It never changes the task's dates.</summary>
    public async Task<WaitingMutationResult> SnoozeAsync(SnoozeFollowUpCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        if (!Enum.IsDefined(command.Choice))
        {
            throw AppException.Validation("choice", "Choose how long to snooze.");
        }

        return await MutateAsync(command.OperationId, "followup.snooze", command, command.TaskId, async (session, profile, now, token) =>
        {
            var task = await RequireOpenTaskAsync(session, command.TaskId, token);
            var followUp = await session.Waiting.FindActiveFollowUpAsync(task.Id, token)
                ?? throw AppException.NotFound("This task has no follow-up to snooze.");
            DateTimeOffset until;
            try
            {
                until = ReminderTiming.SnoozeUntil(command.Choice, now, ReminderSync.ProfileZone(profile), profile.DefaultReminderTime, command.UntilUtc);
            }
            catch (DomainValidationException error)
            {
                throw AppException.Validation("untilUtc", error.Message);
            }

            _ = await session.Waiting.UpdateFollowUpAsync(followUp with { SnoozedUntilUtc = until, Version = followUp.Version + 1, UpdatedAtUtc = now }, followUp.Version, token)
                ?? throw AppException.VersionConflict("The follow-up changed elsewhere. Refresh and try again.");
        }, ct);
    }

    /// <summary>
    /// Follow-ups due by the end of <paramref name="date"/> (today when null) for open tasks of
    /// active lists: the separate My Day items. Earliest first, then by task ID.
    /// </summary>
    public async Task<IReadOnlyList<FollowUpItem>> DueAsync(DateOnly? date, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<FollowUpItem>>(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            var context = await TaskQueries.ContextAsync(session, now, token);
            var day = date ?? context.Today;
            var cutoff = TaskQueries.StartOfLocalDayUtc(context.Zone, day.AddDays(1));
            var items = new List<FollowUpItem>();
            foreach (var followUp in await session.Waiting.ListActiveFollowUpsAsync(context.Profile.Id, token))
            {
                if (followUp.DueAtUtc is not { } due || due >= cutoff)
                {
                    continue;
                }

                var task = await session.Tasks.GetAsync(followUp.TaskId, token);
                if (task is null || task.IsTerminal || await session.Containers.GetAsync(task.ContainerId, token) is not { ArchivedAtUtc: null } container)
                {
                    continue;
                }

                var waiting = await session.Waiting.FindActiveAsync(task.Id, token);
                items.Add(new FollowUpItem(task, container.Name, waiting?.Person, followUp, due, due <= now));
            }

            return (IReadOnlyList<FollowUpItem>)[.. items.OrderBy(item => item.DueAtUtc).ThenBy(item => item.Task.Id, StringComparer.Ordinal)];
        }, ct);

    internal static FollowUpSpec Normalize(FollowUpSpec spec)
    {
        if (!Enum.IsDefined(spec.Kind))
        {
            throw AppException.Validation("followUp.kind", "Choose when to follow up.");
        }

        if (spec.Kind == FollowUpKind.Fixed)
        {
            return spec.FixedAtUtc is { } at
                ? new FollowUpSpec(FollowUpKind.Fixed, at.ToUniversalTime(), null, null)
                : throw AppException.Validation("followUp.fixedAtUtc", "Choose the date and time to follow up.");
        }

        if (spec.Amount is not { } amount || amount < 1 || amount > ReminderTiming.MaximumFollowUpAmount)
        {
            throw AppException.Validation("followUp.amount", $"Choose a number from 1 to {ReminderTiming.MaximumFollowUpAmount}.");
        }

        if (spec.Kind == FollowUpKind.AfterInactivity)
        {
            return new FollowUpSpec(FollowUpKind.AfterInactivity, null, amount, DurationUnit.Days);
        }

        return spec.Unit is { } unit && Enum.IsDefined(unit)
            ? new FollowUpSpec(spec.Kind, null, amount, unit)
            : throw AppException.Validation("followUp.unit", "Choose minutes, hours, days or weeks.");
    }

    private static bool SameRule(FollowUpRecord current, FollowUpSpec spec) =>
        current.Kind == spec.Kind && current.FixedAtUtc == spec.FixedAtUtc && current.Amount == spec.Amount && current.Unit == spec.Unit;

    private async Task<WaitingMutationResult> MutateAsync(
        string operationId,
        string commandKind,
        object payload,
        string taskId,
        Func<IDataSession, ProfileRecord, DateTimeOffset, CancellationToken, Task> body,
        CancellationToken ct)
    {
        var replayed = await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (_, wasReplayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, operationId, commandKind, payload, now, async inner =>
            {
                await body(session, profile, now, inner);
                return taskId;
            }, token);
            return wasReplayed;
        }, ct);
        return new WaitingMutationResult(await GetAsync(taskId, ct), replayed);
    }

    private static async Task<TaskRecord> RequireOpenTaskAsync(IDataSession session, string taskId, CancellationToken token)
    {
        var task = await session.Tasks.GetAsync(taskId, token)
            ?? throw AppException.NotFound("That task no longer exists. Refresh and try again.");
        return task.IsTerminal
            ? throw AppException.Validation("taskId", "This task is finished. Reopen it first.")
            : task;
    }

    private static async Task<WaitingView> ViewAsync(IDataSession session, ProfileRecord profile, TaskRecord task, CancellationToken token)
    {
        var waiting = await session.Waiting.FindActiveAsync(task.Id, token);
        var followUp = await session.Waiting.FindActiveFollowUpAsync(task.Id, token);
        var lastActivity = await ReminderSync.LastActivityAsync(session, task.Id, token);
        if (followUp is null)
        {
            return new WaitingView(task, waiting, null, null, ReminderSuspension.None, lastActivity);
        }

        var (due, suspension, _) = await ReminderSync.FollowUpDueAsync(session, followUp, task, waiting, profile, ReminderSync.ProfileZone(profile), token);
        return new WaitingView(task, waiting, followUp, due, suspension, lastActivity);
    }
}
