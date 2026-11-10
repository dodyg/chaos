using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Reminders;
using Chaos.Domain.Tasks;
using Chaos.Domain.Validation;

namespace Chaos.Application.Reminders;

/// <param name="OffsetMinutes">Minutes relative to a planned or due anchor: negative is before (-60 = one hour before), positive after.</param>
/// <param name="InsistentMinutes">Repeat until acknowledged, at least every five minutes; null for a single delivery.</param>
public sealed record ReminderRuleInput(ReminderAnchor Anchor, int? OffsetMinutes, DateTimeOffset? AbsoluteAtUtc, int? InsistentMinutes, bool Enabled = true);

public sealed record AddReminderCommand(string OperationId, string TaskId, ReminderRuleInput Rule);
public sealed record UpdateReminderCommand(string OperationId, string RuleId, long ExpectedVersion, ReminderRuleInput Rule);
public sealed record DeleteReminderCommand(string OperationId, string RuleId, long ExpectedVersion);
public sealed record ReminderMutationResult(ReminderRuleRecord Rule, bool Replayed);

/// <summary>A rule with what it will do next: its target, the next delivery (after quiet hours and snoozes) or why it is suspended.</summary>
public sealed record ReminderRuleView(
    ReminderRuleRecord Rule,
    DateTimeOffset? TargetAtUtc,
    DateTimeOffset? NextDeliveryAtUtc,
    ReminderSuspension Suspension,
    DateTimeOffset? LastDeliveredAtUtc);

public sealed record ReminderSettings(
    TimeOnly DefaultReminderTime,
    bool QuietHoursEnabled,
    TimeOnly? QuietStart,
    TimeOnly? QuietEnd,
    long Version,
    string ZoneId,
    NotificationCapabilities Capabilities);

public sealed record UpdateReminderSettingsCommand(
    string OperationId,
    long ExpectedVersion,
    TimeOnly DefaultReminderTime,
    bool QuietHoursEnabled,
    TimeOnly? QuietStart,
    TimeOnly? QuietEnd);

public enum NotificationAction { Open = 0, Done = 1, Snooze = 2, Dismiss = 3 }

/// <summary>
/// An action from a notification (OS button or in-app alert). The OperationId makes a repeated
/// click or a retried activation idempotent; the generation identifies the schedule it was
/// shown for, so a callback from a replaced schedule is recognized as stale.
/// </summary>
public sealed record NotificationActionCommand(
    string OperationId,
    string JobId,
    long Generation,
    NotificationAction Action,
    SnoozeChoice? Snooze = null,
    DateTimeOffset? SnoozeUntilUtc = null,
    OpenSubtaskChoice? OpenSubtaskChoice = null);

public enum NotificationActionOutcome
{
    Opened = 0,
    Completed = 1,
    Snoozed = 2,
    Dismissed = 3,
    /// <summary>The notification belongs to a schedule that was replaced (date changed, rule edited).</summary>
    Stale = 4,
    /// <summary>Another action already handled this notification; nothing was done twice.</summary>
    AlreadyHandled = 5,
    /// <summary>The task was deleted; the notification was cleared.</summary>
    TaskGone = 6,
}

public sealed record NotificationActionResult(NotificationActionOutcome Outcome, string TaskId, DateTimeOffset? SnoozedUntilUtc, bool Replayed);

/// <summary>A notification as the alerts list and the notification center show it.</summary>
/// <param name="Late">Delivered more than two minutes after it was due (the app was closed or asleep).</param>
public sealed record NotificationItem(NotificationJobRecord Job, string TaskTitle, bool TaskOpen, string? Person, bool Late);

/// <summary>A notification that became due and was recorded as delivered; the host shows it.</summary>
public sealed record DeliveredNotification(NotificationJobRecord Job, NotificationRequest Request);

/// <summary>Outcome of one OS reconciliation pass.</summary>
public sealed record OsReconcileResult(int Scheduled, int Cancelled, int Failed);

/// <summary>
/// Reminder rules, delivery, notification actions and the notification center (PLAN §11).
/// Rule changes are recorded operations (receipt, history, undo) whose desired notifications
/// are written in the same transaction. Delivery and OS scheduling happen after commit: the
/// host calls <see cref="DeliverDueAsync"/> and <see cref="ReconcileOsAsync"/> on a timer and
/// at startup/resume. Actions validate the job's generation and state before doing anything,
/// and completion goes through the ordinary task command.
/// </summary>
public sealed class ReminderCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock, TaskCommands tasks, INotificationAdapter adapter)
{
    /// <summary>A delivery this much later than due is shown as missed/late.</summary>
    public static readonly TimeSpan LateThreshold = TimeSpan.FromMinutes(2);
    private const int DeliveryBatch = 100;

    public NotificationCapabilities Capabilities => adapter.Capabilities;

    public async Task<IReadOnlyList<ReminderRuleView>> ListForTaskAsync(string taskId, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<ReminderRuleView>>(async (session, token) =>
        {
            var profile = await RequireProfileAsync(session, token);
            var task = await session.Tasks.GetIncludingDeletedAsync(taskId, token)
                ?? throw AppException.NotFound("That task no longer exists. Refresh and try again.");
            var zone = ReminderSync.ProfileZone(profile);
            var views = new List<ReminderRuleView>();
            foreach (var rule in await session.ReminderRules.ListByTaskAsync(taskId, token))
            {
                var (target, suspension) = ReminderSync.RuleTarget(rule, task, profile, zone);
                var jobs = await session.Notifications.ListBySourceAsync(NotificationSourceKind.ReminderRule, rule.Id, token);
                var pending = jobs.FirstOrDefault(job => job.State == NotificationJobState.Pending);
                var delivered = jobs.Where(job => job.DeliveredAtUtc is not null).Max(job => job.DeliveredAtUtc);
                views.Add(new ReminderRuleView(rule, target, pending?.DeliverAtUtc, suspension, delivered));
            }

            return views;
        }, ct);

    public Task<ReminderMutationResult> AddAsync(AddReminderCommand command, CancellationToken ct = default) =>
        MutateRuleAsync(command.OperationId, "reminder.add", command, async (session, profile, now, token) =>
        {
            var task = await RequireOpenTaskAsync(session, command.TaskId, token);
            var input = Validate(command.Rule, task);
            var rule = new ReminderRuleRecord
            {
                Id = ids.NewId().ToString(),
                ProfileId = profile.Id,
                TaskId = task.Id,
                Anchor = input.Anchor,
                OffsetMinutes = input.OffsetMinutes,
                AbsoluteAtUtc = input.AbsoluteAtUtc,
                InsistentMinutes = input.InsistentMinutes,
                Enabled = input.Enabled,
                Version = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            await session.ReminderRules.AddAsync(rule, token);
            return rule;
        }, ct);

    public Task<ReminderMutationResult> UpdateAsync(UpdateReminderCommand command, CancellationToken ct = default) =>
        MutateRuleAsync(command.OperationId, "reminder.update", command, async (session, _, now, token) =>
        {
            var rule = await RequireRuleAsync(session, command.RuleId, command.ExpectedVersion, token);
            var task = await RequireOpenTaskAsync(session, rule.TaskId, token);
            var input = Validate(command.Rule, task);
            if (rule.Anchor == input.Anchor && rule.OffsetMinutes == input.OffsetMinutes && rule.AbsoluteAtUtc == input.AbsoluteAtUtc
                && rule.InsistentMinutes == input.InsistentMinutes && rule.Enabled == input.Enabled)
            {
                return rule; // nothing to change
            }

            var updated = rule with
            {
                Anchor = input.Anchor,
                OffsetMinutes = input.OffsetMinutes,
                AbsoluteAtUtc = input.AbsoluteAtUtc,
                InsistentMinutes = input.InsistentMinutes,
                Enabled = input.Enabled,
                Version = rule.Version + 1,
                UpdatedAtUtc = now,
            };
            return await session.ReminderRules.UpdateAsync(updated, rule.Version, token)
                ?? throw AppException.VersionConflict("This reminder changed elsewhere. Refresh and try again.");
        }, ct);

    public Task<ReminderMutationResult> DeleteAsync(DeleteReminderCommand command, CancellationToken ct = default) =>
        MutateRuleAsync(command.OperationId, "reminder.delete", command, async (session, _, now, token) =>
        {
            var rule = await RequireRuleAsync(session, command.RuleId, command.ExpectedVersion, token);
            return await session.ReminderRules.UpdateAsync(rule with { DeletedAtUtc = now, Version = rule.Version + 1, UpdatedAtUtc = now }, rule.Version, token)
                ?? throw AppException.VersionConflict("This reminder changed elsewhere. Refresh and try again.");
        }, ct);

    public async Task<ReminderSettings> GetSettingsAsync(CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) => SettingsOf(await RequireProfileAsync(session, token)), ct);

    /// <summary>Changes the default reminder time and quiet hours, then reschedules every pending notification.</summary>
    public async Task<ReminderSettings> UpdateSettingsAsync(UpdateReminderSettingsCommand command, CancellationToken ct = default)
    {
        RequireOperation(command.OperationId);
        if (command.QuietHoursEnabled && (command.QuietStart is null || command.QuietEnd is null))
        {
            throw AppException.Validation("quietStart", "Quiet hours need a start and an end time.");
        }

        if (command.QuietHoursEnabled && command.QuietStart == command.QuietEnd)
        {
            throw AppException.Validation("quietEnd", "Quiet hours need different start and end times.");
        }

        var normalized = command with
        {
            DefaultReminderTime = Minute(command.DefaultReminderTime),
            QuietStart = command.QuietStart is { } start ? Minute(start) : null,
            QuietEnd = command.QuietEnd is { } end ? Minute(end) : null,
        };
        await store.ExecuteAsync(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            var current = await RequireProfileAsync(session, token);
            var (result, _) = await TaskMutationSupport.ReplayOrRunAsync(session, current.Id, normalized.OperationId, "reminder.settings", normalized, now, async inner =>
            {
                if (current.Version != normalized.ExpectedVersion)
                {
                    throw AppException.VersionConflict("Settings changed elsewhere. Refresh and try again.");
                }

                var updated = current with
                {
                    DefaultReminderTime = normalized.DefaultReminderTime,
                    QuietHoursEnabled = normalized.QuietHoursEnabled,
                    QuietStart = normalized.QuietStart,
                    QuietEnd = normalized.QuietEnd,
                    Version = current.Version + 1,
                    UpdatedAtUtc = now,
                };
                _ = await session.Profiles.UpdateReminderSettingsAsync(updated, current.Version, inner)
                    ?? throw AppException.VersionConflict("Settings changed elsewhere. Refresh and try again.");
                await ReminderSync.SyncAllAsync(session, updated, now, ids, inner);
                return updated.Version;
            }, token);
            return result;
        }, ct);
        return await GetSettingsAsync(ct);
    }

    /// <summary>Recomputes every desired notification (startup, resume, zone change).</summary>
    public async Task<int> ResyncAllAsync(CancellationToken ct = default) =>
        await store.ExecuteAsync(async (session, token) =>
            await ReminderSync.SyncAllAsync(session, await RequireProfileAsync(session, token), clock.GetUtcNow(), ids, token), ct);

    /// <summary>
    /// Records every due notification as delivered and returns them for the host to show. Each
    /// task is resynchronized first, so a notification whose task or rule changed after it was
    /// scheduled never fires. Missed repeats are coalesced: a source shows only its newest
    /// delivery, and an insistent rule schedules only its next repeat (a rolling horizon).
    /// </summary>
    public async Task<IReadOnlyList<DeliveredNotification>> DeliverDueAsync(CancellationToken ct = default) =>
        await store.ExecuteAsync<IReadOnlyList<DeliveredNotification>>(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            var profile = await RequireProfileAsync(session, token);
            var due = await session.Notifications.ListDueAsync(profile.Id, now, DeliveryBatch, token);
            if (due.Count == 0)
            {
                return [];
            }

            await ReminderSync.SyncTasksAsync(session, profile, due.Select(job => job.TaskId), now, ids, token);
            var zone = ReminderSync.ProfileZone(profile);
            var delivered = new List<DeliveredNotification>();
            foreach (var job in await session.Notifications.ListDueAsync(profile.Id, now, DeliveryBatch, token))
            {
                var task = await session.Tasks.GetAsync(job.TaskId, token);
                if (task is null)
                {
                    await ReminderSync.CancelAsync(session, job, now, token);
                    continue;
                }

                // Older unacknowledged deliveries of the same source are replaced by this one.
                foreach (var older in await session.Notifications.ListBySourceAsync(job.SourceKind, job.SourceId, token))
                {
                    if (older.State == NotificationJobState.Delivered)
                    {
                        await session.Notifications.UpdateAsync(older with
                        {
                            State = NotificationJobState.Acknowledged,
                            AcknowledgedAtUtc = now,
                            ActionTaken = "replaced",
                            UpdatedAtUtc = now,
                        }, NotificationJobState.Delivered, token);
                    }
                }

                var shown = job with { State = NotificationJobState.Delivered, DeliveredAtUtc = now, UpdatedAtUtc = now };
                if (!await session.Notifications.UpdateAsync(shown, NotificationJobState.Pending, token))
                {
                    continue;
                }

                if (job.SourceKind == NotificationSourceKind.ReminderRule
                    && await session.ReminderRules.GetAsync(job.SourceId, token) is { InsistentMinutes: { } every, DeletedAtUtc: null })
                {
                    var next = now.AddMinutes(every);
                    await session.Notifications.AddAsync(job with
                    {
                        Id = ids.NewId().ToString(),
                        Sequence = job.Sequence + 1,
                        Reason = NotificationReason.Repeat,
                        NotBeforeUtc = next,
                        DeliverAtUtc = ReminderSync.DeliveryInstant(profile, zone, next),
                        State = NotificationJobState.Pending,
                        DeliveredAtUtc = null,
                        Presentation = NotificationPresentation.NotYet,
                        OsState = NotificationOsState.None,
                        OsNotificationId = null,
                        OsAttempts = 0,
                        OsLastAttemptUtc = null,
                        OsError = null,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    }, token);
                }

                delivered.Add(new DeliveredNotification(shown, await RequestOfAsync(session, shown, task, profile, zone, token)));
            }

            return delivered;
        }, ct);

    /// <summary>Records how a delivered notification was presented (OS, in-app only, or suppressed/denied).</summary>
    public async Task MarkPresentedAsync(string jobId, NotificationPresentation presentation, CancellationToken ct = default) =>
        await store.ExecuteAsync(async (session, token) =>
        {
            if (await session.Notifications.GetAsync(jobId, token) is { } job && job.State == NotificationJobState.Delivered)
            {
                await session.Notifications.UpdateAsync(job with { Presentation = presentation, UpdatedAtUtc = clock.GetUtcNow() }, NotificationJobState.Delivered, token);
            }

            return 0;
        }, ct);

    /// <summary>
    /// Brings OS schedules in line with the persisted jobs, outside any transaction: schedules
    /// future pending deliveries, cancels OS entries whose job was cancelled, acknowledged or
    /// replaced, and records failures for a later retry. A job changed meanwhile is fixed on the
    /// next pass. Adapters without OS scheduling skip the schedule step.
    /// </summary>
    public async Task<OsReconcileResult> ReconcileOsAsync(CancellationToken ct = default)
    {
        var work = await store.QueryAsync(async (session, token) =>
        {
            var profile = await RequireProfileAsync(session, token);
            var zone = ReminderSync.ProfileZone(profile);
            var items = new List<(NotificationJobRecord Job, NotificationRequest? Request)>();
            foreach (var job in await session.Notifications.ListOsWorkAsync(profile.Id, DeliveryBatch, token))
            {
                var task = await session.Tasks.GetIncludingDeletedAsync(job.TaskId, token);
                items.Add((job, job.State == NotificationJobState.Pending && task is not null ? await RequestOfAsync(session, job, task, profile, zone, token) : null));
            }

            return items;
        }, ct);

        int scheduled = 0, cancelled = 0, failed = 0;
        foreach (var (job, request) in work)
        {
            ct.ThrowIfCancellationRequested();
            if (job.State == NotificationJobState.Pending)
            {
                if (!adapter.Capabilities.SchedulesWhileClosed || request is null || job.DeliverAtUtc <= clock.GetUtcNow())
                {
                    continue; // delivered by the running app
                }

                NotificationScheduleResult result;
                try
                {
                    result = await adapter.ScheduleAsync(request, ct);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    result = new NotificationScheduleResult(false, null, error.Message);
                }

                var now = clock.GetUtcNow();
                var recorded = job with
                {
                    OsState = result.Scheduled ? NotificationOsState.Scheduled : NotificationOsState.Failed,
                    OsNotificationId = result.OsNotificationId,
                    OsAttempts = job.OsAttempts + 1,
                    OsLastAttemptUtc = now,
                    OsError = result.Scheduled ? null : Truncate(result.Error ?? "The system did not accept the notification."),
                    UpdatedAtUtc = now,
                };
                var stored = await store.ExecuteAsync(async (session, token) => await session.Notifications.UpdateAsync(recorded, NotificationJobState.Pending, token), ct);
                if (result.Scheduled && stored) scheduled++;
                else if (!result.Scheduled) failed++;
                else if (result.OsNotificationId is { } orphan)
                {
                    // The job changed while the OS call ran: take the schedule back.
                    await adapter.CancelAsync(orphan, ct);
                }

                continue;
            }

            if (job.OsNotificationId is { } osId)
            {
                await adapter.CancelAsync(osId, ct);
                cancelled++;
                await store.ExecuteAsync(async (session, token) =>
                    await session.Notifications.UpdateAsync(job with { OsState = NotificationOsState.None, OsNotificationId = null, UpdatedAtUtc = clock.GetUtcNow() }, job.State, token), ct);
            }
        }

        return new OsReconcileResult(scheduled, cancelled, failed);
    }

    /// <summary>Delivered notifications still waiting for an action, newest first, one per source, for open tasks only.</summary>
    public async Task<IReadOnlyList<NotificationItem>> AlertsAsync(CancellationToken ct = default) =>
        [.. (await ItemsAsync(DeliveryBatch, ct))
            .Where(item => item.Job.State == NotificationJobState.Delivered && item.TaskOpen)];

    /// <summary>The notification center: delivered notifications newest first, including missed and suppressed ones.</summary>
    public async Task<IReadOnlyList<NotificationItem>> CenterAsync(int limit, CancellationToken ct = default) =>
        await ItemsAsync(Math.Clamp(limit, 1, 200), ct);

    public async Task<NotificationActionResult> ActAsync(NotificationActionCommand command, CancellationToken ct = default)
    {
        RequireOperation(command.OperationId);
        if (!Enum.IsDefined(command.Action))
        {
            throw AppException.Validation("action", "Choose Open, Done, Snooze or Dismiss.");
        }

        if (command.Action == NotificationAction.Snooze && command.Snooze is not { } choice)
        {
            throw AppException.Validation("snooze", "Choose how long to snooze.");
        }

        var (result, replayed, completeVersion) = await store.ExecuteAsync(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            var profile = await RequireProfileAsync(session, token);
            var (outcome, wasReplayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "notification.act", command, now,
                inner => ApplyActionAsync(session, profile, command, now, inner), token);
            return (outcome.Result, wasReplayed, outcome.CompleteVersion);
        }, ct);

        if (completeVersion is { } version)
        {
            // The standard completion command, with its own idempotency key derived from this action.
            await tasks.SetStatusAsync(new SetTaskStatusCommand($"{command.OperationId}:done", result.TaskId, version, TaskItemStatus.Done, command.OpenSubtaskChoice), ct);
        }

        return result with { Replayed = replayed };
    }

    /// <summary>The action's effect inside one transaction; a Done also returns the task version the completion command must see.</summary>
    public sealed record ActionOutcome(NotificationActionResult Result, long? CompleteVersion);

    private async Task<ActionOutcome> ApplyActionAsync(IDataSession session, ProfileRecord profile, NotificationActionCommand command, DateTimeOffset now, CancellationToken token)
    {
        var job = await session.Notifications.GetAsync(command.JobId, token);
        if (job is null || job.ProfileId != profile.Id)
        {
            throw AppException.NotFound("That notification no longer exists.");
        }

        ActionOutcome Outcome(NotificationActionOutcome outcome, DateTimeOffset? snoozed = null, long? completeVersion = null) =>
            new(new NotificationActionResult(outcome, job.TaskId, snoozed, Replayed: false), completeVersion);

        var latest = await session.Notifications.FindLatestAsync(job.SourceKind, job.SourceId, token);
        if (job.Generation != command.Generation || latest is null || latest.Generation != job.Generation)
        {
            return Outcome(NotificationActionOutcome.Stale);
        }

        if (job.State != NotificationJobState.Delivered)
        {
            return Outcome(NotificationActionOutcome.AlreadyHandled);
        }

        var task = await session.Tasks.GetAsync(job.TaskId, token);
        if (task is null)
        {
            await AcknowledgeAsync(session, job, "task-deleted", now, token);
            return Outcome(NotificationActionOutcome.TaskGone);
        }

        switch (command.Action)
        {
            case NotificationAction.Open:
                await AcknowledgeAsync(session, job, "open", now, token);
                return Outcome(NotificationActionOutcome.Opened);
            case NotificationAction.Dismiss:
                await AcknowledgeAsync(session, job, "dismiss", now, token);
                return Outcome(NotificationActionOutcome.Dismissed);
            case NotificationAction.Done:
                if (task.IsTerminal)
                {
                    await AcknowledgeAsync(session, job, "done", now, token);
                    return Outcome(NotificationActionOutcome.AlreadyHandled);
                }

                if (command.OpenSubtaskChoice is null && (await session.Subtasks.ListByTaskAsync(task.Id, token)).Any(subtask => !TaskItemStatusRules.IsTerminal(subtask.Status)))
                {
                    throw AppException.Validation("openSubtaskChoice", "This task has open subtasks. Choose whether to complete them too or keep them open.");
                }

                await AcknowledgeAsync(session, job, "done", now, token);
                return Outcome(NotificationActionOutcome.Completed, completeVersion: task.Version);
            default:
                var zone = ReminderSync.ProfileZone(profile);
                DateTimeOffset until;
                try
                {
                    until = ReminderTiming.SnoozeUntil(command.Snooze!.Value, now, zone, profile.DefaultReminderTime, command.SnoozeUntilUtc);
                }
                catch (DomainValidationException error)
                {
                    throw AppException.Validation("snoozeUntilUtc", error.Message);
                }

                await CancelLaterAsync(session, job, now, token);
                if (!await session.Notifications.UpdateAsync(job with { State = NotificationJobState.Snoozed, ActionTaken = "snooze", UpdatedAtUtc = now }, NotificationJobState.Delivered, token))
                {
                    return Outcome(NotificationActionOutcome.AlreadyHandled);
                }

                var sequence = (await session.Notifications.ListBySourceAsync(job.SourceKind, job.SourceId, token))
                    .Where(row => row.Generation == job.Generation).Max(row => row.Sequence) + 1;
                await session.Notifications.AddAsync(job with
                {
                    Id = ids.NewId().ToString(),
                    Sequence = sequence,
                    Reason = NotificationReason.Snoozed,
                    NotBeforeUtc = until,
                    DeliverAtUtc = ReminderSync.DeliveryInstant(profile, zone, until),
                    State = NotificationJobState.Pending,
                    DeliveredAtUtc = null,
                    Presentation = NotificationPresentation.NotYet,
                    AcknowledgedAtUtc = null,
                    ActionTaken = null,
                    OsState = NotificationOsState.None,
                    OsNotificationId = null,
                    OsAttempts = 0,
                    OsLastAttemptUtc = null,
                    OsError = null,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                }, token);
                return Outcome(NotificationActionOutcome.Snoozed, until);
        }
    }

    private static async Task AcknowledgeAsync(IDataSession session, NotificationJobRecord job, string action, DateTimeOffset now, CancellationToken token)
    {
        await CancelLaterAsync(session, job, now, token);
        if (!await session.Notifications.UpdateAsync(job with { State = NotificationJobState.Acknowledged, AcknowledgedAtUtc = now, ActionTaken = action, UpdatedAtUtc = now },
                NotificationJobState.Delivered, token))
        {
            throw AppException.OperationConflict("This notification was handled at the same moment. Nothing was done twice.");
        }
    }

    /// <summary>An acknowledged or snoozed delivery stops its insistent repeat.</summary>
    private static async Task CancelLaterAsync(IDataSession session, NotificationJobRecord job, DateTimeOffset now, CancellationToken token)
    {
        foreach (var later in await session.Notifications.ListBySourceAsync(job.SourceKind, job.SourceId, token))
        {
            if (later.State == NotificationJobState.Pending && later.Generation == job.Generation && later.Reason == NotificationReason.Repeat)
            {
                await ReminderSync.CancelAsync(session, later, now, token);
            }
        }
    }

    private async Task<IReadOnlyList<NotificationItem>> ItemsAsync(int limit, CancellationToken ct) =>
        await store.QueryAsync<IReadOnlyList<NotificationItem>>(async (session, token) =>
        {
            var profile = await RequireProfileAsync(session, token);
            var items = new List<NotificationItem>();
            foreach (var job in await session.Notifications.ListDeliveredAsync(profile.Id, limit, token))
            {
                var task = await session.Tasks.GetIncludingDeletedAsync(job.TaskId, token);
                string? person = null;
                if (job.SourceKind == NotificationSourceKind.FollowUp && await session.Waiting.FindActiveAsync(job.TaskId, token) is { } waiting)
                {
                    person = waiting.Person;
                }

                items.Add(new NotificationItem(job, task?.Title ?? "(deleted task)", task is { DeletedAtUtc: null, IsTerminal: false }, person,
                    job.DeliveredAtUtc is { } at && at - job.DeliverAtUtc > LateThreshold));
            }

            return items;
        }, ct);

    private static async Task<NotificationRequest> RequestOfAsync(IDataSession session, NotificationJobRecord job, TaskRecord task, ProfileRecord profile, TimeZoneInfo zone, CancellationToken token)
    {
        var local = TimeZoneInfo.ConvertTime(job.TargetAtUtc, zone).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
        string body;
        if (job.SourceKind == NotificationSourceKind.FollowUp)
        {
            var person = (await session.Waiting.FindActiveAsync(task.Id, token))?.Person;
            body = person is null ? $"Follow up (due {local})" : $"Follow up with {person} (due {local})";
        }
        else
        {
            body = job.Reason == NotificationReason.Repeat ? $"Reminder for {local}, repeating until you act" : $"Reminder for {local}";
        }

        return new NotificationRequest(job.Id, job.Generation, task.Id, task.Title, body, job.DeliverAtUtc, job.SourceKind == NotificationSourceKind.FollowUp);
    }

    private async Task<ReminderMutationResult> MutateRuleAsync(
        string operationId,
        string commandKind,
        object payload,
        Func<IDataSession, ProfileRecord, DateTimeOffset, CancellationToken, Task<ReminderRuleRecord>> body,
        CancellationToken ct)
    {
        RequireOperation(operationId);
        return await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (rule, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, operationId, commandKind, payload, now,
                inner => body(session, profile, now, inner), token);
            return new ReminderMutationResult(rule, replayed);
        }, ct);
    }

    internal static ReminderRuleInput Validate(ReminderRuleInput input, TaskRecord task)
    {
        if (!Enum.IsDefined(input.Anchor))
        {
            throw AppException.Validation("anchor", "Choose an exact time, or a time before the planned or due date.");
        }

        if (input.InsistentMinutes is { } every && (every < ReminderTiming.MinimumInsistentMinutes || every > ReminderTiming.MaximumInsistentMinutes))
        {
            throw AppException.Validation("insistentMinutes",
                $"An insistent reminder repeats every {ReminderTiming.MinimumInsistentMinutes} to {ReminderTiming.MaximumInsistentMinutes} minutes.");
        }

        if (input.Anchor == ReminderAnchor.Absolute)
        {
            if (input.AbsoluteAtUtc is not { } at)
            {
                throw AppException.Validation("absoluteAtUtc", "Choose the date and time of the reminder.");
            }

            return input with { OffsetMinutes = null, AbsoluteAtUtc = at.ToUniversalTime() };
        }

        if (input.OffsetMinutes is not { } offset || Math.Abs(offset) > ReminderTiming.MaximumOffsetMinutes)
        {
            throw AppException.Validation("offsetMinutes", "Choose how long before the date the reminder comes (at most a year).");
        }

        if (input.Anchor == ReminderAnchor.Planned && task.PlannedDate is null)
        {
            throw AppException.Validation("anchor", "This task has no planned date. Add one first, or choose an exact time.");
        }

        if (input.Anchor == ReminderAnchor.Due && task.DueDate is null)
        {
            throw AppException.Validation("anchor", "This task has no due date. Add one first, or choose an exact time.");
        }

        return input with { AbsoluteAtUtc = null, OffsetMinutes = offset };
    }

    private static async Task<ReminderRuleRecord> RequireRuleAsync(IDataSession session, string ruleId, long expectedVersion, CancellationToken token)
    {
        var rule = await session.ReminderRules.GetAsync(ruleId, token);
        if (rule is null || rule.DeletedAtUtc is not null)
        {
            throw AppException.NotFound("This reminder no longer exists. Refresh and try again.");
        }

        return rule.Version == expectedVersion
            ? rule
            : throw AppException.VersionConflict("This reminder changed elsewhere. Refresh and try again.");
    }

    private static async Task<TaskRecord> RequireOpenTaskAsync(IDataSession session, string taskId, CancellationToken token)
    {
        var task = await session.Tasks.GetAsync(taskId, token)
            ?? throw AppException.NotFound("That task no longer exists. Refresh and try again.");
        return task.IsTerminal
            ? throw AppException.Validation("taskId", "This task is finished. Reopen it before adding or changing reminders.")
            : task;
    }

    internal static async Task<ProfileRecord> RequireProfileAsync(IDataSession session, CancellationToken token) =>
        await session.Profiles.GetPrimaryAsync(token)
        ?? throw new AppException(AppErrorKind.StorageUnavailable, "No local profile exists yet. Restart Chaos to complete first-run setup.");

    internal static void RequireOperation(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw AppException.Validation("operationId", "An operation ID is required.");
        }
    }

    private ReminderSettings SettingsOf(ProfileRecord profile) =>
        new(profile.DefaultReminderTime, profile.QuietHoursEnabled, profile.QuietStart, profile.QuietEnd, profile.Version, profile.ZoneId, adapter.Capabilities);

    private static TimeOnly Minute(TimeOnly time) => new(time.Hour, time.Minute);

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500];
}
