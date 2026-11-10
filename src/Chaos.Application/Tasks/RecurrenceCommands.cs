using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Domain.Recurrence;
using Chaos.Domain.Tasks;
using Chaos.Domain.Validation;

namespace Chaos.Application.Tasks;

public sealed record StartRecurrenceCommand(string OperationId, string TaskId, long ExpectedVersion, RecurrenceRule Rule);
public sealed record StartRecurrenceResult(RecurrenceSeriesRecord Series, RecurrenceOccurrenceRecord Occurrence, bool Replayed);
public sealed record RecurrenceActionCommand(string OperationId, string TaskId, long ExpectedVersion);
public sealed record RecurrenceActionResult(TaskRecord Task, bool Replayed);
public sealed record MoveOccurrenceCommand(string OperationId, string TaskId, long ExpectedVersion, DateOnly Date, TimeOnly? Time);
public sealed record ReviseRecurrenceCommand(string OperationId, string TaskId, long ExpectedVersion, RecurrenceRule Rule);
public sealed record RecurrenceDetail(RecurrenceSeriesRecord Series, RecurrenceRule Rule, IReadOnlyList<RecurrenceOccurrenceRecord> Occurrences);
public sealed record RecurrenceProjection(DateTime NominalLocal, bool Projected);
public sealed record UpcomingRecurrenceProjection(string SourceTaskId, string Title, DateTime NominalLocal);

/// <summary>Transactional recurrence operations over the same recorder and receipts as task commands.</summary>
public sealed class RecurrenceCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public async Task<RecurrenceDetail?> GetForTaskAsync(string taskId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var occurrence = await session.Recurrence.FindByTaskAsync(taskId, token);
            if (occurrence is null) return null;
            var series = await session.Recurrence.GetSeriesAsync(occurrence.SeriesId, token)
                ?? throw new AppException(AppErrorKind.StorageUnavailable, "The recurrence series could not be found.");
            var rule = StoredJsonFormats.ReadSnapshot<RecurrenceRule>(series.RuleJson)
                ?? throw new AppException(AppErrorKind.StorageUnavailable, "The recurrence rule could not be read.");
            return new RecurrenceDetail(series, rule, await session.Recurrence.ListOccurrencesAsync(series.Id, token));
        }, ct);

    /// <summary>Read-only fixed-schedule candidates; actual successors depend on when the active task is finished.</summary>
    public async Task<IReadOnlyList<RecurrenceProjection>> ProjectAsync(string taskId, DateOnly through, int cap, CancellationToken ct = default)
    {
        var detail = await GetForTaskAsync(taskId, ct) ?? throw AppException.NotFound("This task is not part of a recurrence series.");
        if (cap is < 1 or > 1000) throw AppException.Validation("cap", "Choose between 1 and 1000 projected occurrences.");
        var active = detail.Occurrences.SingleOrDefault(item => item.State == OccurrenceState.Active);
        if (active is null || detail.Series.EndedAtUtc is not null || detail.Rule.Mode != RecurrenceMode.FixedSchedule) return [];
        var previous = DateTime.ParseExact(active.ScheduleKey, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var start = DateOnly.FromDateTime(previous);
        if (through < start) return [];
        var remaining = detail.Rule.EndKind == RecurrenceEndKind.Count ? detail.Rule.Count!.Value - detail.Series.ActionableCount : cap;
        if (remaining <= 0) return [];
        var desired = (int)Math.Min(cap, remaining);
        return [.. RecurrenceCalculator.Expand(detail.Rule, start, through, desired + 1)
            .Where(slot => slot > previous).Take(desired).Select(slot => new RecurrenceProjection(slot, true))];
    }

    public async Task<IReadOnlyList<UpcomingRecurrenceProjection>> UpcomingAsync(int days, int cap, CancellationToken ct = default)
    {
        if (days is < 1 or > 366 || cap is < 1 or > 1000) throw AppException.Validation("days", "Choose a range of 1 to 366 days and a limit of 1 to 1000.");
        return await store.QueryAsync<IReadOnlyList<UpcomingRecurrenceProjection>>(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw new AppException(AppErrorKind.StorageUnavailable, "No local profile exists yet.");
            var space = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token) ?? throw new AppException(AppErrorKind.StorageUnavailable, "No personal space exists yet.");
            var zone = TimeZoneInfo.FindSystemTimeZoneById(profile.ZoneId);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
            var through = today.AddDays(days);
            var rows = new List<UpcomingRecurrenceProjection>();
            foreach (var active in await session.Recurrence.ListActiveBySpaceAsync(space.Id, token))
            {
                var series = await session.Recurrence.GetSeriesAsync(active.SeriesId, token);
                var rule = series is null ? null : StoredJsonFormats.ReadSnapshot<RecurrenceRule>(series.RuleJson);
                var task = active.TaskId is null ? null : await session.Tasks.GetAsync(active.TaskId, token);
                if (series is null || rule is null || task is null || rule.Mode != RecurrenceMode.FixedSchedule) continue;
                var remaining = rule.EndKind == RecurrenceEndKind.Count ? rule.Count!.Value - series.ActionableCount : cap;
                if (remaining <= 0) continue;
                var previous = DateTime.ParseExact(active.ScheduleKey, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
                var start = DateOnly.FromDateTime(previous) > today ? DateOnly.FromDateTime(previous) : today.AddDays(1);
                if (start > through) continue;
                var limit = (int)Math.Min(cap, remaining);
                foreach (var slot in RecurrenceCalculator.Expand(rule, start, through, Math.Min(limit + 1, 10000)).Where(slot => slot > previous).Take(limit))
                    rows.Add(new UpcomingRecurrenceProjection(task.Id, task.Title, slot));
            }
            return [.. rows.OrderBy(row => row.NominalLocal).ThenBy(row => row.SourceTaskId, StringComparer.Ordinal).Take(cap)];
        }, ct);
    }

    public async Task<StartRecurrenceResult> StartAsync(StartRecurrenceCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try { command.Rule.Validate(); }
        catch (DomainValidationException error) { throw AppException.Validation("rule", error.Message); }
        return await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId,
                "recurrence.start", command, now, async inner =>
                {
                    var task = await session.Tasks.GetAsync(command.TaskId, inner) ?? throw AppException.NotFound("That task no longer exists.");
                    if (task.Version != command.ExpectedVersion) throw AppException.VersionConflict("This task changed elsewhere. Refresh and try again.");
                    return await StartInSessionAsync(session, ids, task, command.Rule, command.OperationId, now, inner);
                }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    internal static async Task<StartRecurrenceResult> StartInSessionAsync(IDataSession session, IIdGenerator ids, TaskRecord task,
        RecurrenceRule rule, string operationId, DateTimeOffset now, CancellationToken ct)
    {
        try { rule.Validate(); }
        catch (DomainValidationException error) { throw AppException.Validation("rule", error.Message); }
        if (task.IsTerminal || task.RecurrenceOccurrenceKey is not null)
            throw AppException.Validation("taskId", "Choose an open task that is not already recurring.");
        var slots = rule.Mode == RecurrenceMode.FixedSchedule
            ? RecurrenceCalculator.Expand(rule, rule.AnchorDate, DateOnly.MaxValue, 1)
            : [rule.AnchorDate.ToDateTime(rule.AnchorTime ?? TimeOnly.MinValue)];
        if (slots.Count == 0) throw AppException.Validation("rule", "There is no occurrence within the rule's end date.");
        var first = slots[0];
        // Normalize the template to the rule's anchor first. Preserve the relationship
        // between planned and due anchors instead of leaving an old task date behind.
        var oldDate = task.PlannedDate ?? task.DueDate;
        if (oldDate is { } existingDate)
        {
            var usesPlanned = task.PlannedDate is not null;
            var oldTime = usesPlanned ? task.PlannedTime : task.DueTime;
            var oldAnchor = existingDate.ToDateTime(oldTime ?? TimeOnly.MinValue);
            var newAnchor = rule.AnchorDate.ToDateTime(rule.AnchorTime ?? TimeOnly.MinValue);
            var dayShift = rule.AnchorDate.DayNumber - existingDate.DayNumber;
            (DateOnly? Date, TimeOnly? Time) Shift(DateOnly? date, TimeOnly? time)
            {
                if (date is null) return (null, null);
                var shifted = date.Value.ToDateTime(time ?? TimeOnly.MinValue).Add(
                    time is not null && oldTime is not null && rule.AnchorTime is not null ? newAnchor - oldAnchor : TimeSpan.FromDays(dayShift));
                return (DateOnly.FromDateTime(shifted), time is null ? null : TimeOnly.FromDateTime(shifted));
            }
            var planned = Shift(task.PlannedDate, task.PlannedTime);
            var due = Shift(task.DueDate, task.DueTime);
            var normalized = task with
            {
                PlannedDate = planned.Date, PlannedTime = usesPlanned ? rule.AnchorTime : planned.Time,
                PlannedZoneId = usesPlanned ? (rule.AnchorTime is null ? null : rule.ZoneId) : task.PlannedZoneId,
                DueDate = due.Date, DueTime = usesPlanned ? due.Time : rule.AnchorTime,
                DueZoneId = usesPlanned ? task.DueZoneId : (rule.AnchorTime is null ? null : rule.ZoneId),
            };
            if (normalized != task)
            {
                normalized = normalized with { Version = task.Version + 1, UpdatedAtUtc = now };
                task = await session.Tasks.UpdateAsync(normalized, task.Version, ct)
                    ?? throw AppException.VersionConflict("This task changed elsewhere.");
            }
        }
        var snapshot = await RecordingDataSession.BuildAggregateAsync(session, task.Id, ct)
            ?? throw AppException.NotFound("That task no longer exists.");
        var series = new RecurrenceSeriesRecord
        {
            Id = ids.NewId().ToString(), SpaceId = task.SpaceId, ContainerId = task.ContainerId,
            RuleJson = StoredJsonFormats.Snapshot(rule), TemplateJson = StoredJsonFormats.Snapshot(snapshot),
            Revision = 1, ActionableCount = 1, Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        await session.Recurrence.AddSeriesAsync(series, ct);
        var occurrence = new RecurrenceOccurrenceRecord
        {
            Id = ids.NewId().ToString(), SeriesId = series.Id, TaskId = task.Id, NominalKey = Key(first), ScheduleKey = Key(first),
            EffectiveLocal = Key(first), State = OccurrenceState.Active, SeriesRevision = 1,
            Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        await session.Recurrence.AddOccurrenceAsync(occurrence, ct);
        var offset = DateOnly.FromDateTime(first).DayNumber - rule.AnchorDate.DayNumber;
        var updated = task with { RecurrenceOccurrenceKey = occurrence.Id,
            PlannedDate = task.PlannedDate?.AddDays(offset) ?? (task.DueDate is null ? DateOnly.FromDateTime(first) : null),
            PlannedTime = task.PlannedDate is null && task.DueDate is null ? rule.AnchorTime : task.PlannedTime,
            PlannedZoneId = task.PlannedDate is null && task.DueDate is null && rule.AnchorTime is not null ? rule.ZoneId : task.PlannedZoneId,
            DueDate = task.DueDate?.AddDays(offset), Version = task.Version + 1, UpdatedAtUtc = now };
        if (await session.Tasks.UpdateAsync(updated, task.Version, ct) is null) throw AppException.VersionConflict("This task changed elsewhere.");
        await TaskMutationSupport.WriteTaskHistoryAsync(session, ids, operationId, snapshot,
            snapshot with { Task = updated }, "recurrence.started", ["Recurrence"], now, ct);
        return new StartRecurrenceResult(series, occurrence, false);
    }

    public async Task<RecurrenceActionResult> SkipAsync(RecurrenceActionCommand command, CancellationToken ct = default) =>
        await ActionAsync(command, "recurrence.skip", async (session, task, _, now, token) =>
        {
            var cancelled = task with { Status = TaskItemStatus.Cancelled, CancelledAtUtc = now,
                Version = task.Version + 1, UpdatedAtUtc = now };
            if (await session.Tasks.UpdateAsync(cancelled, task.Version, token) is null)
                throw AppException.VersionConflict("This task changed elsewhere.");
            await AdvanceAsync(session, ids, cancelled, now, token, skipped: true);
            return cancelled;
        }, ct);

    public async Task<RecurrenceActionResult> EndAsync(RecurrenceActionCommand command, CancellationToken ct = default) =>
        await ActionAsync(command, "recurrence.end", async (session, task, occurrence, now, token) =>
        {
            var series = await session.Recurrence.GetSeriesAsync(occurrence.SeriesId, token)
                ?? throw AppException.NotFound("This series no longer exists.");
            if (series.EndedAtUtc is not null) return task;
            var ended = series with { EndedAtUtc = now, Version = series.Version + 1, UpdatedAtUtc = now };
            if (await session.Recurrence.UpdateSeriesAsync(ended, series.Version, token) is null)
                throw AppException.VersionConflict("This series changed elsewhere.");
            return task;
        }, ct);

    public async Task<RecurrenceActionResult> EndAndDeleteAsync(RecurrenceActionCommand command, CancellationToken ct = default) =>
        await ActionAsync(command, "recurrence.end-delete", async (session, task, occurrence, now, token) =>
        {
            var series = await session.Recurrence.GetSeriesAsync(occurrence.SeriesId, token)
                ?? throw AppException.NotFound("This series no longer exists.");
            var ended = series with { EndedAtUtc = now, Version = series.Version + 1, UpdatedAtUtc = now };
            if (await session.Recurrence.UpdateSeriesAsync(ended, series.Version, token) is null)
                throw AppException.VersionConflict("This series changed elsewhere.");
            var removedOccurrence = occurrence with { DeletedAtUtc = now, Version = occurrence.Version + 1, UpdatedAtUtc = now };
            if (await session.Recurrence.UpdateOccurrenceAsync(removedOccurrence, occurrence.Version, token) is null)
                throw AppException.VersionConflict("This occurrence changed elsewhere.");
            foreach (var child in await session.Subtasks.ListByTaskAsync(task.Id, token))
            {
                var deletedChild = child with { DeletedAtUtc = now, Version = child.Version + 1, UpdatedAtUtc = now };
                if (await session.Subtasks.UpdateAsync(deletedChild, child.Version, token) is null)
                    throw AppException.VersionConflict("A subtask changed elsewhere.");
            }
            var deleted = task with { DeletedAtUtc = now, Version = task.Version + 1, UpdatedAtUtc = now };
            return await session.Tasks.UpdateAsync(deleted, task.Version, token)
                ?? throw AppException.VersionConflict("This task changed elsewhere.");
        }, ct);

    public async Task<RecurrenceActionResult> MoveAsync(MoveOccurrenceCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId,
                "recurrence.move", command, now, async inner =>
                {
                    var task = await session.Tasks.GetAsync(command.TaskId, inner) ?? throw AppException.NotFound("That task no longer exists.");
                    if (task.Version != command.ExpectedVersion) throw AppException.VersionConflict("This task changed elsewhere.");
                    var occurrence = await session.Recurrence.FindByTaskAsync(task.Id, inner)
                        ?? throw AppException.Validation("taskId", "This task is not a recurring occurrence.");
                    if (occurrence.State != OccurrenceState.Active || task.IsTerminal)
                        throw AppException.Validation("taskId", "Choose the active occurrence.");
                    var series = await session.Recurrence.GetSeriesAsync(occurrence.SeriesId, inner)
                        ?? throw AppException.NotFound("This series no longer exists.");
                    var rule = StoredJsonFormats.ReadSnapshot<RecurrenceRule>(series.RuleJson)
                        ?? throw new AppException(AppErrorKind.StorageUnavailable, "The recurrence rule could not be read.");
                    if ((command.Time is null) != (rule.AnchorTime is null))
                        throw AppException.Validation("time", "Keep the occurrence all-day or supply a time for the timed rule.");
                    var previous = DateTime.ParseExact(occurrence.EffectiveLocal, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
                    var effective = command.Date.ToDateTime(command.Time ?? TimeOnly.MinValue);
                    var delta = effective - previous;
                    (DateOnly? Date, TimeOnly? Time) Move(DateOnly? date, TimeOnly? time)
                    {
                        if (date is null) return (null, null);
                        var shifted = date.Value.ToDateTime(time ?? TimeOnly.MinValue).Add(time is null ? TimeSpan.FromDays((command.Date.DayNumber - DateOnly.FromDateTime(previous).DayNumber)) : delta);
                        return (DateOnly.FromDateTime(shifted), time is null ? null : TimeOnly.FromDateTime(shifted));
                    }
                    var planned = Move(task.PlannedDate, task.PlannedTime);
                    var due = Move(task.DueDate, task.DueTime);
                    var updated = task with { PlannedDate = planned.Date, PlannedTime = planned.Time, DueDate = due.Date,
                        DueTime = due.Time, Version = task.Version + 1, UpdatedAtUtc = now };
                    if (await session.Tasks.UpdateAsync(updated, task.Version, inner) is null) throw AppException.VersionConflict("This task changed elsewhere.");
                    var moved = occurrence with { EffectiveLocal = Key(effective), Version = occurrence.Version + 1, UpdatedAtUtc = now };
                    if (await session.Recurrence.UpdateOccurrenceAsync(moved, occurrence.Version, inner) is null) throw AppException.VersionConflict("This occurrence changed elsewhere.");
                    return new RecurrenceActionResult(updated, false);
                }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    public async Task<RecurrenceActionResult> ReviseFutureAsync(ReviseRecurrenceCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try { command.Rule.Validate(); }
        catch (DomainValidationException error) { throw AppException.Validation("rule", error.Message); }
        return await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId,
                "recurrence.revise", command, now, async inner =>
                {
                    var task = await session.Tasks.GetAsync(command.TaskId, inner) ?? throw AppException.NotFound("That task no longer exists.");
                    if (task.Version != command.ExpectedVersion) throw AppException.VersionConflict("This task changed elsewhere.");
                    var occurrence = await session.Recurrence.FindByTaskAsync(task.Id, inner)
                        ?? throw AppException.Validation("taskId", "This task is not a recurring occurrence.");
                    if (occurrence.State != OccurrenceState.Active || task.IsTerminal) throw AppException.Validation("taskId", "Choose the active occurrence.");
                    var series = await session.Recurrence.GetSeriesAsync(occurrence.SeriesId, inner)
                        ?? throw AppException.NotFound("This series no longer exists.");
                    if (series.EndedAtUtc is not null) throw AppException.Validation("rule", "This series has ended.");
                    if (command.Rule.EndKind == RecurrenceEndKind.Count && command.Rule.Count < series.ActionableCount)
                        throw AppException.Validation("rule.count", "The count cannot be less than the occurrences already created.");
                    var slots = command.Rule.Mode == RecurrenceMode.FixedSchedule
                        ? RecurrenceCalculator.Expand(command.Rule, command.Rule.AnchorDate, DateOnly.MaxValue, 1)
                        : [command.Rule.AnchorDate.ToDateTime(command.Rule.AnchorTime ?? TimeOnly.MinValue)];
                    if (slots.Count == 0) throw AppException.Validation("rule", "There is no occurrence within the rule's end date.");
                    var first = slots[0];
                    var oldEffective = DateTime.ParseExact(occurrence.EffectiveLocal, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
                    TaskRecord Shift(TaskRecord source, DateTime from, DateTime to)
                    {
                        var delta = to - from;
                        var dayDelta = DateOnly.FromDateTime(to).DayNumber - DateOnly.FromDateTime(from).DayNumber;
                        (DateOnly? Date, TimeOnly? Time) Anchor(DateOnly? date, TimeOnly? time)
                        {
                            if (date is null) return (null, null);
                            var value = date.Value.ToDateTime(time ?? TimeOnly.MinValue).Add(time is null ? TimeSpan.FromDays(dayDelta) : delta);
                            return (DateOnly.FromDateTime(value), time is null ? null : TimeOnly.FromDateTime(value));
                        }
                        var planned = Anchor(source.PlannedDate, source.PlannedTime);
                        var due = Anchor(source.DueDate, source.DueTime);
                        return source with { PlannedDate = planned.Date, PlannedTime = planned.Time, DueDate = due.Date, DueTime = due.Time };
                    }
                    var shifted = Shift(task, oldEffective, first) with { Version = task.Version + 1, UpdatedAtUtc = now };
                    if (await session.Tasks.UpdateAsync(shifted, task.Version, inner) is null) throw AppException.VersionConflict("This task changed elsewhere.");
                    var snapshot = await RecordingDataSession.BuildAggregateAsync(session, task.Id, inner)
                        ?? throw AppException.NotFound("That task no longer exists.");
                    var baseTime = command.Rule.AnchorDate.ToDateTime(command.Rule.AnchorTime ?? TimeOnly.MinValue);
                    var template = snapshot with { Task = Shift(shifted, first, baseTime) };
                    var revision = await session.Recurrence.NextRevisionAsync(series.Id, inner);
                    var revised = series with { RuleJson = StoredJsonFormats.Snapshot(command.Rule), TemplateJson = StoredJsonFormats.Snapshot(template),
                        Revision = revision, Version = series.Version + 1, UpdatedAtUtc = now };
                    if (await session.Recurrence.UpdateSeriesAsync(revised, series.Version, inner) is null) throw AppException.VersionConflict("This series changed elsewhere.");
                    var changed = occurrence with { ScheduleKey = Key(first), EffectiveLocal = Key(first), EditSnapshotJson = null, SeriesRevision = revision,
                        Version = occurrence.Version + 1, UpdatedAtUtc = now };
                    if (await session.Recurrence.UpdateOccurrenceAsync(changed, occurrence.Version, inner) is null) throw AppException.VersionConflict("This occurrence changed elsewhere.");
                    return new RecurrenceActionResult(shifted, false);
                }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    private async Task<RecurrenceActionResult> ActionAsync(RecurrenceActionCommand command, string kind,
        Func<IDataSession, TaskRecord, RecurrenceOccurrenceRecord, DateTimeOffset, CancellationToken, Task<TaskRecord>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, kind, command, now, async inner =>
            {
                var task = await session.Tasks.GetAsync(command.TaskId, inner) ?? throw AppException.NotFound("That task no longer exists.");
                if (task.Version != command.ExpectedVersion) throw AppException.VersionConflict("This task changed elsewhere.");
                var occurrence = await session.Recurrence.FindByTaskAsync(task.Id, inner)
                    ?? throw AppException.Validation("taskId", "This task is not a recurring occurrence.");
                if (occurrence.State != OccurrenceState.Active || task.IsTerminal)
                    throw AppException.Validation("taskId", "Choose the active occurrence.");
                var before = await RecordingDataSession.BuildAggregateAsync(session, task.Id, inner);
                var applied = await action(session, task, occurrence, now, inner);
                if (before is not null)
                    await TaskMutationSupport.WriteTaskHistoryAsync(session, ids, command.OperationId, before,
                        before with { Task = applied }, kind, ["Recurrence"], now, inner);
                return new RecurrenceActionResult(applied, false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    internal static string Key(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Completes the active occurrence and creates at most one successor in the same transaction.</summary>
    internal static async Task AdvanceAsync(IDataSession session, IIdGenerator ids, TaskRecord completed, DateTimeOffset now, CancellationToken ct, bool skipped = false)
    {
        var occurrence = await session.Recurrence.FindByTaskAsync(completed.Id, ct)
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "The recurrence occurrence could not be found.");
        if (occurrence.State != OccurrenceState.Active) throw AppException.VersionConflict("This occurrence is no longer active.");
        var series = await session.Recurrence.GetSeriesAsync(occurrence.SeriesId, ct)
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "The recurrence series could not be found.");
        var rule = StoredJsonFormats.ReadSnapshot<RecurrenceRule>(series.RuleJson)
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "The recurrence rule could not be read.");
        var template = StoredJsonFormats.ReadSnapshot<TaskAggregateSnapshot>(series.TemplateJson)
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "The recurrence template could not be read.");
        var terminal = occurrence with { State = skipped ? OccurrenceState.Skipped : OccurrenceState.Completed, CompletedAtUtc = now, Version = occurrence.Version + 1, UpdatedAtUtc = now };
        if (await session.Recurrence.UpdateOccurrenceAsync(terminal, occurrence.Version, ct) is null)
            throw AppException.VersionConflict("The recurrence changed elsewhere.");
        var touchedSeries = series with { Version = series.Version + 1, UpdatedAtUtc = now };
        if (await session.Recurrence.UpdateSeriesAsync(touchedSeries, series.Version, ct) is null)
            throw AppException.VersionConflict("The recurrence changed elsewhere.");
        series = touchedSeries;
        if (series.EndedAtUtc is not null || (rule.EndKind == RecurrenceEndKind.Count && series.ActionableCount >= rule.Count)) return;

        var localNow = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(rule.ZoneId)).DateTime;
        var prior = DateTime.ParseExact(occurrence.ScheduleKey, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var next = rule.UntilDate is { } until && until < DateOnly.FromDateTime(localNow) ? null : rule.Mode == RecurrenceMode.FixedSchedule
            ? RecurrenceCalculator.NextFixed(rule, prior, localNow, rule.UntilDate ?? DateOnly.MaxValue)
            : RecurrenceCalculator.AfterCompletion(rule, rule.AnchorTime is null ? localNow.Date : localNow);
        if (next is null) return;

        var taskId = ids.NewId().ToString();
        var nominalKey = Key(next.Value);
        var priorOccurrence = await session.Recurrence.FindByKeyAsync(series.Id, nominalKey, ct);
        if (priorOccurrence is { DeletedAtUtc: null })
            throw AppException.VersionConflict("That occurrence already exists. Refresh and reconcile the series.");
        var occurrenceId = priorOccurrence?.Id ?? ids.NewId().ToString();
        var dayDelta = DateOnly.FromDateTime(next.Value).DayNumber - rule.AnchorDate.DayNumber;
        DateOnly? Shift(DateOnly? date) => date?.AddDays(dayDelta);
        var anchor = rule.AnchorDate.ToDateTime(rule.AnchorTime ?? TimeOnly.MinValue);
        var timeDelta = next.Value - anchor;
        (DateOnly? Date, TimeOnly? Time) ShiftAnchor(DateOnly? date, TimeOnly? time)
        {
            if (date is null) return (null, null);
            if (rule.Mode != RecurrenceMode.AfterCompletion || time is null) return (Shift(date), time);
            var shifted = date.Value.ToDateTime(time.Value).Add(timeDelta);
            return (DateOnly.FromDateTime(shifted), TimeOnly.FromDateTime(shifted));
        }
        var planned = ShiftAnchor(template.Task.PlannedDate, template.Task.PlannedTime);
        var due = ShiftAnchor(template.Task.DueDate, template.Task.DueTime);
        var successor = template.Task with
        {
            Id = taskId, ContainerId = completed.ContainerId, SectionId = completed.SectionId,
            Status = TaskItemStatus.NotStarted, CompletedAtUtc = null, CancelledAtUtc = null,
            PlannedDate = planned.Date ?? (template.Task.DueDate is null ? DateOnly.FromDateTime(next.Value) : null),
            PlannedTime = planned.Time ?? (template.Task.PlannedDate is null && template.Task.DueDate is null && rule.AnchorTime is not null ? TimeOnly.FromDateTime(next.Value) : null),
            PlannedZoneId = template.Task.PlannedDate is null && template.Task.DueDate is null && rule.AnchorTime is not null ? rule.ZoneId : template.Task.PlannedZoneId,
            DueDate = due.Date, DueTime = due.Time,
            Position = await session.Tasks.GetNextPositionAsync(completed.ContainerId, ct),
            RecurrenceOccurrenceKey = occurrenceId, Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now, DeletedAtUtc = null,
        };
        await session.Tasks.AddAsync(successor, ct);
        foreach (var reminder in template.ReminderRules ?? [])
        {
            if (reminder.Anchor == ReminderAnchor.Absolute) continue;
            await session.ReminderRules.AddAsync(reminder with { Id = ids.NewId().ToString(), TaskId = taskId,
                Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now, DeletedAtUtc = null }, ct);
        }
        foreach (var tagId in template.TagIds ?? []) await session.Tags.AddTaskLinkAsync(taskId, tagId, ct);
        foreach (var child in template.Subtasks)
        {
            var newChild = child with { Id = ids.NewId().ToString(), TaskId = taskId, Status = TaskItemStatus.NotStarted,
                CompletedAtUtc = null, CancelledAtUtc = null, Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now, DeletedAtUtc = null };
            await session.Subtasks.AddAsync(newChild, ct);
            if (template.SubtaskTagIds?.TryGetValue(child.Id, out var tagIds) == true)
                foreach (var tagId in tagIds) await session.Tags.AddSubtaskLinkAsync(newChild.Id, tagId, ct);
        }
        var newOccurrence = new RecurrenceOccurrenceRecord
        {
            Id = occurrenceId, SeriesId = series.Id, TaskId = taskId, NominalKey = nominalKey, ScheduleKey = nominalKey, EffectiveLocal = nominalKey,
            State = OccurrenceState.Active, SeriesRevision = series.Revision, Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        if (priorOccurrence is null)
            await session.Recurrence.AddOccurrenceAsync(newOccurrence, ct);
        else
        {
            newOccurrence = newOccurrence with { Version = priorOccurrence.Version + 1, CreatedAtUtc = priorOccurrence.CreatedAtUtc };
            if (await session.Recurrence.UpdateOccurrenceAsync(newOccurrence, priorOccurrence.Version, ct) is null)
                throw AppException.VersionConflict("The recurrence changed elsewhere.");
        }
        var updatedSeries = series with { ActionableCount = series.ActionableCount + 1, Version = series.Version + 1, UpdatedAtUtc = now };
        if (await session.Recurrence.UpdateSeriesAsync(updatedSeries, series.Version, ct) is null)
            throw AppException.VersionConflict("The recurrence changed elsewhere.");
    }
}
