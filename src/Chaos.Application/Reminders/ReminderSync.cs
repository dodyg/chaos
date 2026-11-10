using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Queries;
using Chaos.Application.Tasks;
using Chaos.Domain.Reminders;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Reminders;

/// <summary>Why a rule or follow-up has no upcoming notification.</summary>
public enum ReminderSuspension
{
    None = 0,
    Disabled = 1,
    TaskFinished = 2,
    NoPlannedDate = 3,
    NoDueDate = 4,
    NoWaitingStart = 5,
}

/// <summary>
/// Keeps the desired notifications in step with committed state (PLAN §11). Every recorded
/// operation calls it for the tasks it touched, inside the same transaction, and delivery
/// calls it again before anything is shown, so a stale job never fires. Jobs are derived
/// rows: one pending delivery per source, replaced with a new generation whenever the target
/// or delivery instant changes.
/// </summary>
public static class ReminderSync
{
    /// <summary>Task-history fields that do not count as activity for inactivity follow-ups.</summary>
    private static readonly HashSet<string> NonActivityFields = new(StringComparer.Ordinal)
    {
        "Position", "Reminders", "Waiting", "FollowUp",
    };

    /// <summary>
    /// For each touched, nondeleted task that is no longer Waiting on someone (status changed,
    /// completed or cancelled), ends its waiting details and resolves its follow-up. Runs
    /// inside the recorded operation so history and undo cover it.
    /// </summary>
    public static async Task ResolveWaitingAsync(IDataSession session, IReadOnlyList<string> taskIds, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var taskId in taskIds.ToArray())
        {
            var task = await session.Tasks.GetIncludingDeletedAsync(taskId, ct);
            if (task is null || task.DeletedAtUtc is not null || task.Status == TaskItemStatus.WaitingOnSomeone)
            {
                continue;
            }

            await EndWaitingAsync(session, taskId, now, ct);
        }
    }

    /// <summary>Tombstones the active waiting details and resolves the active follow-up of one task.</summary>
    internal static async Task EndWaitingAsync(IDataSession session, string taskId, DateTimeOffset now, CancellationToken ct)
    {
        if (await session.Waiting.FindActiveFollowUpAsync(taskId, ct) is { } followUp)
        {
            _ = await session.Waiting.UpdateFollowUpAsync(followUp with { ResolvedAtUtc = now, Version = followUp.Version + 1, UpdatedAtUtc = now }, followUp.Version, ct)
                ?? throw AppException.VersionConflict("The follow-up changed elsewhere. Refresh and try again.");
        }

        if (await session.Waiting.FindActiveAsync(taskId, ct) is { } waiting)
        {
            _ = await session.Waiting.UpdateAsync(waiting with { DeletedAtUtc = now, Version = waiting.Version + 1, UpdatedAtUtc = now }, waiting.Version, ct)
                ?? throw AppException.VersionConflict("The waiting details changed elsewhere. Refresh and try again.");
        }
    }

    public static async Task SyncTasksAsync(IDataSession session, ProfileRecord profile, IEnumerable<string> taskIds, DateTimeOffset now, IIdGenerator ids, CancellationToken ct)
    {
        foreach (var taskId in taskIds.Distinct(StringComparer.Ordinal).ToArray())
        {
            await SyncTaskAsync(session, profile, taskId, now, ids, ct);
        }
    }

    /// <summary>Recomputes every desired notification of the profile (startup, resume, settings or zone change).</summary>
    public static async Task<int> SyncAllAsync(IDataSession session, ProfileRecord profile, DateTimeOffset now, IIdGenerator ids, CancellationToken ct)
    {
        var taskIds = await session.Notifications.ListTasksWithSourcesAsync(profile.Id, ct);
        await SyncTasksAsync(session, profile, taskIds, now, ids, ct);
        return taskIds.Count;
    }

    public static async Task SyncTaskAsync(IDataSession session, ProfileRecord profile, string taskId, DateTimeOffset now, IIdGenerator ids, CancellationToken ct)
    {
        var task = await session.Tasks.GetIncludingDeletedAsync(taskId, ct);
        if (task is null)
        {
            return;
        }

        var zone = ProfileZone(profile);
        var desired = new Dictionary<(NotificationSourceKind Kind, string Id), DateTimeOffset?>();
        foreach (var rule in await session.ReminderRules.ListByTaskAsync(taskId, ct))
        {
            desired[(NotificationSourceKind.ReminderRule, rule.Id)] = RuleTarget(rule, task, profile, zone).Target;
        }

        if (await session.Waiting.FindActiveFollowUpAsync(taskId, ct) is { } followUp)
        {
            var waiting = await session.Waiting.FindActiveAsync(taskId, ct);
            var due = await FollowUpDueAsync(session, followUp, task, waiting, profile, zone, ct);
            if (due.Due != followUp.DueAtUtc)
            {
                await session.Waiting.SetFollowUpDueAsync(followUp.Id, due.Due, ct);
            }

            desired[(NotificationSourceKind.FollowUp, followUp.Id)] = due.Due;
        }

        // Sources that disappeared (rule deleted, follow-up resolved) lose their pending delivery.
        foreach (var pending in await session.Notifications.ListPendingByTaskAsync(taskId, ct))
        {
            if (!desired.ContainsKey((pending.SourceKind, pending.SourceId)))
            {
                await CancelAsync(session, pending, now, ct);
            }
        }

        foreach (var ((kind, sourceId), target) in desired)
        {
            await ApplyAsync(session, profile, zone, task, kind, sourceId, target, now, ids, ct);
        }
    }

    /// <summary>A rule's target instant, or why it has none.</summary>
    public static (DateTimeOffset? Target, ReminderSuspension Suspension) RuleTarget(ReminderRuleRecord rule, TaskRecord task, ProfileRecord profile, TimeZoneInfo zone)
    {
        if (!rule.Enabled) return (null, ReminderSuspension.Disabled);
        if (task.DeletedAtUtc is not null || task.IsTerminal) return (null, ReminderSuspension.TaskFinished);
        switch (rule.Anchor)
        {
            case ReminderAnchor.Absolute:
                return (rule.AbsoluteAtUtc, ReminderSuspension.None);
            case ReminderAnchor.Planned when task.PlannedDate is { } planned:
                return (ReminderTiming.OffsetTarget(
                    ReminderTiming.AnchorInstant(planned, task.PlannedTime, AnchorZone(task.PlannedZoneId, task.PlannedTime, zone), profile.DefaultReminderTime),
                    rule.OffsetMinutes ?? 0), ReminderSuspension.None);
            case ReminderAnchor.Planned:
                return (null, ReminderSuspension.NoPlannedDate);
            case ReminderAnchor.Due when task.DueDate is { } due:
                return (ReminderTiming.OffsetTarget(DueAnchor(task, profile, zone)!.Value, rule.OffsetMinutes ?? 0), ReminderSuspension.None);
            default:
                return (null, ReminderSuspension.NoDueDate);
        }
    }

    /// <summary>
    /// A follow-up's effective due instant (a snooze pushes it later), or why it has none. A
    /// finished or deleted task has none.
    /// </summary>
    public static async Task<(DateTimeOffset? Due, ReminderSuspension Suspension, DateTimeOffset? LastActivity)> FollowUpDueAsync(
        IDataSession session, FollowUpRecord followUp, TaskRecord task, WaitingDetailRecord? waiting, ProfileRecord profile, TimeZoneInfo zone, CancellationToken ct)
    {
        var lastActivity = followUp.Kind == FollowUpKind.AfterInactivity ? await LastActivityAsync(session, task.Id, ct) : null;
        if (task.DeletedAtUtc is not null || task.IsTerminal)
        {
            return (null, ReminderSuspension.TaskFinished, lastActivity);
        }

        var computed = ReminderTiming.FollowUpDue(followUp.Kind, followUp.FixedAtUtc, followUp.Amount, followUp.Unit,
            waiting?.StartedAtUtc, DueAnchor(task, profile, zone), lastActivity, zone);
        if (computed is not { } due)
        {
            return (null, followUp.Kind == FollowUpKind.BeforeDue ? ReminderSuspension.NoDueDate : ReminderSuspension.NoWaitingStart, lastActivity);
        }

        return (followUp.SnoozedUntilUtc is { } snoozed && snoozed > due ? snoozed : due, ReminderSuspension.None, lastActivity);
    }

    /// <summary>
    /// The newest qualifying activity on a task (PLAN §11): a task field edit, a subtask
    /// change or a diary update. Reminder, waiting and follow-up bookkeeping and ordering do
    /// not count; neither does viewing, a reminder firing or My Day membership, which write no
    /// task history at all.
    /// </summary>
    public static async Task<DateTimeOffset?> LastActivityAsync(IDataSession session, string taskId, CancellationToken ct)
    {
        var history = await session.History.ListByTaskAsync(taskId, ct);
        for (var index = history.Count - 1; index >= 0; index--)
        {
            if (history[index].ChangedFields.Any(field => !NonActivityFields.Contains(field)))
            {
                return history[index].EffectiveAtUtc;
            }
        }

        return null;
    }

    public static DateTimeOffset? DueAnchor(TaskRecord task, ProfileRecord profile, TimeZoneInfo zone) =>
        task.DueDate is { } due
            ? ReminderTiming.AnchorInstant(due, task.DueTime, AnchorZone(task.DueZoneId, task.DueTime, zone), profile.DefaultReminderTime)
            : null;

    public static TimeZoneInfo ProfileZone(ProfileRecord profile) => TaskQueries.FindZone(profile.ZoneId) ?? TimeZoneInfo.Utc;

    /// <summary>When a delivery not before <paramref name="instant"/> may happen under the profile's quiet hours.</summary>
    public static DateTimeOffset DeliveryInstant(ProfileRecord profile, TimeZoneInfo zone, DateTimeOffset instant) =>
        ReminderTiming.AfterQuietHours(instant, zone, profile.QuietHoursEnabled, profile.QuietStart, profile.QuietEnd);

    private static TimeZoneInfo AnchorZone(string? anchorZoneId, TimeOnly? time, TimeZoneInfo profileZone) =>
        time is not null && anchorZoneId is not null && TaskQueries.FindZone(anchorZoneId) is { } zone ? zone : profileZone;

    private static async Task ApplyAsync(
        IDataSession session,
        ProfileRecord profile,
        TimeZoneInfo zone,
        TaskRecord task,
        NotificationSourceKind kind,
        string sourceId,
        DateTimeOffset? target,
        DateTimeOffset now,
        IIdGenerator ids,
        CancellationToken ct)
    {
        var latest = await session.Notifications.FindLatestAsync(kind, sourceId, ct);
        if (target is not { } desired)
        {
            if (latest is { State: NotificationJobState.Pending })
            {
                await CancelAsync(session, latest, now, ct);
            }

            return;
        }

        if (latest is not null && latest.State != NotificationJobState.Cancelled && latest.TargetAtUtc == desired)
        {
            // Same target: delivered/acknowledged rows stay as they are (no second delivery);
            // a pending row only moves when quiet hours now place it elsewhere.
            if (latest.State != NotificationJobState.Pending)
            {
                return;
            }

            var deliverAt = DeliveryInstant(profile, zone, Later(desired, latest.NotBeforeUtc));
            if (deliverAt == latest.DeliverAtUtc)
            {
                return;
            }

            await CancelAsync(session, latest, now, ct);
            await AddAsync(session, ids, latest, profile, task, kind, sourceId, desired, latest.NotBeforeUtc, latest.Reason, deliverAt, now, ct);
            return;
        }

        if (latest is { State: NotificationJobState.Pending })
        {
            await CancelAsync(session, latest, now, ct);
        }

        await AddAsync(session, ids, latest, profile, task, kind, sourceId, desired, null, NotificationReason.Initial,
            DeliveryInstant(profile, zone, desired), now, ct);
    }

    private static async Task AddAsync(
        IDataSession session,
        IIdGenerator ids,
        NotificationJobRecord? previous,
        ProfileRecord profile,
        TaskRecord task,
        NotificationSourceKind kind,
        string sourceId,
        DateTimeOffset target,
        DateTimeOffset? notBefore,
        NotificationReason reason,
        DateTimeOffset deliverAt,
        DateTimeOffset now,
        CancellationToken ct) =>
        await session.Notifications.AddAsync(new NotificationJobRecord
        {
            Id = ids.NewId().ToString(),
            ProfileId = profile.Id,
            TaskId = task.Id,
            SourceKind = kind,
            SourceId = sourceId,
            Generation = (previous?.Generation ?? 0) + 1,
            Sequence = 1,
            Reason = reason,
            TargetAtUtc = target,
            NotBeforeUtc = notBefore,
            DeliverAtUtc = deliverAt,
            State = NotificationJobState.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        }, ct);

    internal static async Task CancelAsync(IDataSession session, NotificationJobRecord pending, DateTimeOffset now, CancellationToken ct)
    {
        if (!await session.Notifications.UpdateAsync(pending with { State = NotificationJobState.Cancelled, UpdatedAtUtc = now }, NotificationJobState.Pending, ct))
        {
            throw AppException.VersionConflict("A reminder changed at the same moment. Try again.");
        }
    }

    internal static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset? b) => b is { } other && other > a ? other : a;
}
