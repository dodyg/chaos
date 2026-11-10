using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Chaos.Domain.Time;
using Chaos.Domain.Validation;

namespace Chaos.Application.Tasks;

public sealed record CreateTemplateCommand(string OperationId, string Name, TemplatePayload Payload, string? SpaceId);

public sealed record UpdateTemplateCommand(string OperationId, string TemplateId, long ExpectedVersion, string Name, TemplatePayload Payload);

public sealed record DeleteTemplateCommand(string OperationId, string TemplateId, long ExpectedVersion);

public sealed record InstantiateTemplateCommand(string OperationId, string TemplateId, string? TargetContainerId);

public sealed record TemplateMutationResult(TemplateRecord Template, bool Replayed);

public sealed record InstantiateTemplateResult(TaskRecord Task, bool Replayed);

/// <summary>
/// Reusable task templates (PLAN §15 WP2, PLAN §6 template rules): CRUD plus instantiation
/// that creates fresh IDs, resets all completion state, copies notes and subtask structure,
/// and derives dates from relative day offsets added to the instantiation day in the
/// profile's zone. Never copies diary, plan membership, acknowledgements or history.
/// </summary>
public sealed class TemplateCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public async Task<TemplateMutationResult> CreateAsync(CreateTemplateCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "template.create", command, now, async inner =>
                {
                    var name = TaskFieldValidation.Title(command.Name, "name");
                    var payload = NormalizePayload(command.Payload);
                    var space = await ResolveSpaceAsync(session, command.SpaceId, profile.Id, inner);
                    await RequireFreeNameAsync(session, space.Id, name, selfId: null, inner);

                    var template = new TemplateRecord
                    {
                        Id = ids.NewId().ToString(),
                        SpaceId = space.Id,
                        Name = name,
                        NormalizedName = name.Trim().ToLowerInvariant(),
                        PayloadJson = StoredJsonFormats.Snapshot(payload),
                        Version = 1,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    };
                    await session.Templates.AddAsync(template, inner);

                    return new TemplateMutationResult(template, Replayed: false);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    public async Task<TemplateMutationResult> UpdateAsync(UpdateTemplateCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "template.update", command, now, async inner =>
                {
                    var template = await RequireTemplateAsync(session, command.TemplateId, inner);
                    RequireCurrentVersion(template, command.ExpectedVersion);
                    var name = TaskFieldValidation.Title(command.Name, "name");
                    var payload = NormalizePayload(command.Payload);
                    await RequireFreeNameAsync(session, template.SpaceId, name, selfId: template.Id, inner);

                    var updated = template with
                    {
                        Name = name,
                        NormalizedName = name.Trim().ToLowerInvariant(),
                        PayloadJson = StoredJsonFormats.Snapshot(payload),
                        Version = template.Version + 1,
                        UpdatedAtUtc = now,
                    };
                    var applied = await ApplyUpdateAsync(session, updated, template.Version, token);

                    return new TemplateMutationResult(applied, Replayed: false);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    /// <summary>Reversible template deletion (PLAN §1 decision 10).</summary>
    public async Task<TemplateMutationResult> DeleteAsync(DeleteTemplateCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "template.delete", command, now, async inner =>
                {
                    var template = await RequireTemplateAsync(session, command.TemplateId, inner);
                    RequireCurrentVersion(template, command.ExpectedVersion);

                    var deleted = template with { DeletedAtUtc = now, Version = template.Version + 1, UpdatedAtUtc = now };
                    var applied = await ApplyUpdateAsync(session, deleted, template.Version, token);

                    return new TemplateMutationResult(applied, Replayed: false);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    public async Task<IReadOnlyList<TemplateRecord>> ListAsync(string? spaceId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token)
                ?? throw new AppException(
                    AppErrorKind.StorageUnavailable,
                    "No local profile exists yet. Restart Chaos to complete first-run setup.");
            var space = await ResolveSpaceAsync(session, spaceId, profile.Id, token);
            return await session.Templates.ListBySpaceAsync(space.Id, token);
        }, ct);

    /// <summary>
    /// Creates a real task from the template (PLAN §6): fresh IDs, open statuses, subtask
    /// structure copied in document order, dates from relative offsets over the instantiation
    /// day. The receipt makes a retried instantiation return the same task, never a duplicate.
    /// </summary>
    public async Task<InstantiateTemplateResult> InstantiateAsync(InstantiateTemplateCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "template.instantiate", command, now, async inner =>
                {
                    var template = await RequireTemplateAsync(session, command.TemplateId, inner);
                    var payload = StoredJsonFormats.ReadSnapshot<TemplatePayload>(template.PayloadJson)
                        ?? throw new AppException(
                            AppErrorKind.StorageUnavailable,
                            "This template's saved content could not be read. Recreate the template.");
                    payload = NormalizePayload(payload);

                    var container = await ResolveContainerAsync(session, command.TargetContainerId, profile.Id, inner);
                    var today = TodayInProfileZone(profile.ZoneId);

                    var plannedDate = payload.PlannedDayOffset is { } plannedOffset ? today.AddDays(plannedOffset) : default(DateOnly?);
                    var dueDate = payload.DueDayOffset is { } dueOffset ? today.AddDays(dueOffset) : default(DateOnly?);

                    var task = new TaskRecord
                    {
                        Id = ids.NewId().ToString(),
                        SpaceId = container.SpaceId,
                        ContainerId = container.Id,
                        Title = payload.Title,
                        Notes = payload.Notes,
                        Status = TaskItemStatus.NotStarted,
                        Priority = payload.Priority,
                        EstimateMinutes = payload.EstimateMinutes,
                        CapacityCategory = payload.CapacityCategory,
                        PlannedDate = plannedDate,
                        PlannedTime = payload.PlannedTime,
                        PlannedZoneId = plannedDate is null ? null : payload.PlannedZoneId,
                        DueDate = dueDate,
                        DueTime = payload.DueTime,
                        DueZoneId = dueDate is null ? null : payload.DueZoneId,
                        Position = await session.Tasks.GetNextPositionAsync(container.Id, inner),
                        Version = 1,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    };
                    await session.Tasks.AddAsync(task, inner);

                    var subtasks = new List<SubtaskRecord>(payload.Subtasks.Count);
                    foreach (var structure in payload.Subtasks)
                    {
                        var subtask = new SubtaskRecord
                        {
                            Id = ids.NewId().ToString(),
                            TaskId = task.Id,
                            Title = structure.Title,
                            Notes = structure.Notes,
                            Status = TaskItemStatus.NotStarted,
                            Position = structure.Position,
                            Version = 1,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                        };
                        await session.Subtasks.AddAsync(subtask, inner);
                        subtasks.Add(subtask);
                    }

                    await TaskMutationSupport.WriteTaskHistoryAsync(
                        session, ids, command.OperationId,
                        before: null,
                        after: new TaskAggregateSnapshot(task, subtasks),
                        eventKind: "task.created",
                        changedFields: ["Title", "Notes", "ContainerId"],
                        now, inner);

                    return new InstantiateTemplateResult(task, Replayed: false);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    /// <summary>
    /// Template payload limits (PLAN §6): title/notes/estimate as for tasks; day offsets are
    /// nonnegative (0 = the instantiation day); a time needs its offset (the date it lands on)
    /// and a zone; a zone without a time has no effect and is rejected.
    /// </summary>
    internal static TemplatePayload NormalizePayload(TemplatePayload payload)
    {
        var title = TaskFieldValidation.Title(payload.Title, "title");
        var notes = TaskFieldValidation.Notes(payload.Notes, "notes");
        var estimate = TaskFieldValidation.Estimate(payload.EstimateMinutes, "estimateMinutes");
        var priority = TaskFieldValidation.Priority(payload.Priority, "priority");
        var category = TaskFieldValidation.Category(payload.CapacityCategory, "capacityCategory");
        var planned = NormalizeAnchor(payload.PlannedDayOffset, payload.PlannedTime, payload.PlannedZoneId, "planned");
        var due = NormalizeAnchor(payload.DueDayOffset, payload.DueTime, payload.DueZoneId, "due");

        var subtasks = new List<TemplateSubtask>(payload.Subtasks.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subtask in payload.Subtasks)
        {
            var subtaskTitle = TaskFieldValidation.Title(subtask.Title, "subtasks.title");
            if (!seen.Add(subtaskTitle))
            {
                throw AppException.Validation("subtasks.title", "A template has two subtasks with the same title. Give each a distinct title.");
            }

            subtasks.Add(new TemplateSubtask
            {
                Title = subtaskTitle,
                Notes = TaskFieldValidation.Notes(subtask.Notes, "subtasks.notes"),
                Position = subtask.Position,
            });
        }

        return new TemplatePayload
        {
            Title = title,
            Notes = notes,
            Priority = priority,
            EstimateMinutes = estimate,
            CapacityCategory = category,
            PlannedDayOffset = planned.Offset,
            PlannedTime = planned.Time,
            PlannedZoneId = planned.ZoneId,
            DueDayOffset = due.Offset,
            DueTime = due.Time,
            DueZoneId = due.ZoneId,
            Subtasks = subtasks,
        };
    }

    private static (int? Offset, TimeOnly? Time, string? ZoneId) NormalizeAnchor(int? offset, TimeOnly? time, string? zoneId, string prefix)
    {
        if (offset is { } day && day < 0)
        {
            throw AppException.Validation(
                $"{prefix}DayOffset",
                "A template date offset is the number of days after the day you create the task (0 = that same day). Enter 0 or more.");
        }

        var zone = TaskFieldValidation.Zone(zoneId, $"{prefix}ZoneId");
        if (time is not null && offset is null)
        {
            throw AppException.Validation($"{prefix}Time", "A template time needs a day offset, so the time lands on a date when the task is created.");
        }

        if (time is not null && zone is null)
        {
            throw AppException.Validation($"{prefix}ZoneId", "A time needs a time zone. Keep the zone that came with the time.");
        }

        if (zone is not null && time is null)
        {
            throw AppException.Validation($"{prefix}ZoneId", "A time zone only matters together with a time. Remove the zone or add a time.");
        }

        return (offset, time, zone);
    }

    /// <summary>The instantiation day in the profile's zone (PLAN §6); a broken profile zone is a startup problem.</summary>
    private DateOnly TodayInProfileZone(string zoneId)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new AppException(
                AppErrorKind.StorageUnavailable,
                "The time zone saved in your profile is not recognized on this computer. Check the profile settings before using templates.");
        }

        return new SystemLocalCalendar(clock, zone).Today();
    }

    private async Task<T> InWriteAsync<T>(
        Func<IDataSession, ProfileRecord, DateTimeOffset, CancellationToken, Task<T>> body,
        CancellationToken ct) =>
        await TaskMutationSupport.InWriteTransactionAsync(store, clock, body, ct);

    private static async Task<TemplateRecord> RequireTemplateAsync(IDataSession session, string templateId, CancellationToken token) =>
        await session.Templates.GetAsync(templateId, token)
        ?? throw AppException.NotFound("That template no longer exists. Refresh and try again.");

    private static void RequireCurrentVersion(TemplateRecord template, long expectedVersion)
    {
        if (template.Version != expectedVersion)
        {
            throw AppException.VersionConflict("This template changed elsewhere while you were working. Refresh and try again.");
        }
    }

    private static async Task RequireFreeNameAsync(IDataSession session, string spaceId, string name, string? selfId, CancellationToken token)
    {
        var existing = await session.Templates.FindByNameAsync(spaceId, name.Trim().ToLowerInvariant(), token);
        if (existing is not null && existing.Id != selfId)
        {
            throw AppException.Validation("name", "A template with that name already exists. Choose a different name.");
        }
    }

    private static async Task<SpaceRecord> ResolveSpaceAsync(IDataSession session, string? spaceId, string profileId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(spaceId))
        {
            return await session.Spaces.GetPersonalByOwnerAsync(profileId, token)
                ?? throw new AppException(
                    AppErrorKind.StorageUnavailable,
                    "No personal space exists yet. Restart Chaos to complete first-run setup.");
        }

        return await session.Spaces.GetAsync(spaceId, token)
            ?? throw AppException.NotFound("That space no longer exists. Refresh and try again.");
    }

    private static async Task<ContainerRecord> ResolveContainerAsync(IDataSession session, string? containerId, string profileId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            var space = await session.Spaces.GetPersonalByOwnerAsync(profileId, token)
                ?? throw new AppException(
                    AppErrorKind.StorageUnavailable,
                    "No personal space exists yet. Restart Chaos to complete first-run setup.");
            return await session.Containers.GetInboxAsync(space.Id, token)
                ?? throw new AppException(
                    AppErrorKind.StorageUnavailable,
                    "No Inbox exists yet. Restart Chaos to complete first-run setup.");
        }

        return await session.Containers.GetAsync(containerId, token)
            ?? throw AppException.NotFound("That list or project no longer exists. Refresh and try again.");
    }

    private static async Task<TemplateRecord> ApplyUpdateAsync(IDataSession session, TemplateRecord updated, long expectedVersion, CancellationToken token) =>
        await session.Templates.UpdateAsync(updated, expectedVersion, token)
        ?? throw AppException.VersionConflict("This template changed elsewhere while you were working. Refresh and try again.");
}
