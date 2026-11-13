using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tags;
using Chaos.Application.Tasks;
using Chaos.Domain.Capture;
using Chaos.Domain.Recurrence;
using Chaos.Domain.Tags;
using Chaos.Domain.Tasks;
using Chaos.Domain.Validation;

namespace Chaos.Application.Capture;

public sealed record CaptureSubtask(string Title, bool Done);

/// <summary>
/// The task a person confirmed in the quick-add preview (PLAN §8): explicit fields, never
/// reparsed on save, so a preview made before midnight saves the dates it showed. Times
/// belong to <see cref="ZoneId"/>. Tags are existing IDs or paths to select or create.
/// </summary>
public sealed record CaptureTaskDraft
{
    public required string Title { get; init; }
    public string? Notes { get; init; }
    public string? ContainerId { get; init; }
    public TaskPriority Priority { get; init; } = TaskPriority.Normal;
    public int? EstimateMinutes { get; init; }
    public DateOnly? PlannedDate { get; init; }
    public TimeOnly? PlannedTime { get; init; }
    public DateOnly? DueDate { get; init; }
    public TimeOnly? DueTime { get; init; }
    public string? ZoneId { get; init; }
    public RecurrenceRule? Recurrence { get; init; }
    public IReadOnlyList<CaptureReminder> Reminders { get; init; } = [];
    public IReadOnlyList<string> TagIds { get; init; } = [];
    public IReadOnlyList<string> TagPaths { get; init; } = [];
    public string? WaitingOn { get; init; }
    public DateOnly? FollowUpDate { get; init; }
    public TimeOnly? FollowUpTime { get; init; }
    public IReadOnlyList<CaptureSubtask> Subtasks { get; init; } = [];
    /// <summary>Explicit Work/Personal choice; null takes the container's default category, else Personal.</summary>
    public CapacityCategory? Category { get; init; }
}

/// <summary>Creates every confirmed draft in one transaction: one receipt, one undo entry, nothing saved if any draft is invalid.</summary>
public sealed record CreateCapturedTasksCommand(string OperationId, IReadOnlyList<CaptureTaskDraft> Drafts);

public sealed record CapturedTasksResult(IReadOnlyList<TaskRecord> Tasks, bool Replayed);

/// <summary>One task line of a preview: where its text starts in the input, the parse, and its subtask lines.</summary>
public sealed record CaptureItemPreview(int LineNumber, int SourceStart, QuickAddResult Parse, IReadOnlyList<CaptureSubtask> Subtasks);

public sealed record CapturePreview(bool IsBatch, IReadOnlyList<CaptureItemPreview> Items, IReadOnlyList<BatchError> Errors, DateTimeOffset CapturedAtUtc, string ZoneId);

/// <summary>
/// Quick add (PLAN §15 WP7): a read-only preview over the pure parser and the atomic create
/// of what the person confirmed. Preview reads the profile settings, lists and tags once and
/// never writes. A waiting person becomes the task's private waiting details (with a fixed
/// follow-up when a date was given); a follow-up date without a person becomes a reminder.
/// </summary>
public sealed class CaptureCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public const int MaxInputLength = 200_000;
    private const int MaxWaitingLength = 200;

    public async Task<CapturePreview> PreviewAsync(string text, IReadOnlyList<CaptureSpan> excluded, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (text.Length > MaxInputLength)
        {
            throw AppException.Validation("text", $"Quick add accepts at most {MaxInputLength:N0} characters at once. Paste a shorter part.");
        }

        var context = await store.QueryAsync(async (session, token) =>
        {
            var profile = await RequireProfileAsync(session, token);
            var space = await RequireSpaceAsync(session, profile, token);
            var containers = await session.Containers.ListBySpaceAsync(space.Id, token);
            var tags = TagCommands.TreeOrder(await session.Tags.ListByOwnerAsync(profile.Id, token));
            return new QuickAddContext(clock.GetUtcNow(), profile.ZoneId, profile.DateFormat, profile.FirstWeekday, profile.DefaultReminderTime,
                [.. containers.Select(container => new KnownContainer(container.Id, container.Name))], [.. tags.Select(tag => tag.Path)], []);
        }, ct);

        var split = BatchCapture.Split(text);
        var items = split.Items.Select(item =>
        {
            var line = item.Task;
            var shifted = excluded
                .Where(span => span.Start >= line.SourceStart && span.End <= line.SourceStart + line.Text.Length)
                .Select(span => new CaptureSpan(span.Start - line.SourceStart, span.End - line.SourceStart))
                .ToList();
            var parse = QuickAddParser.Parse(line.Text, context with { ExcludedSpans = shifted });
            if (parse.Draft.WaitingOn is not null || parse.Draft.FollowUpDate is not null)
            {
                parse = parse with { Warnings = [.. parse.Warnings, new CaptureWarning(parse.Draft.WaitingOn is null ? "followUpReminder" : "waitingPrivate",
                    parse.Draft.WaitingOn is null
                        ? "With no person named, the follow-up becomes a reminder at that time."
                        : "The task waits on this person privately; nothing is sent to them.")] };
            }

            return new CaptureItemPreview(line.LineNumber, line.SourceStart, parse, [.. item.Subtasks.Select(sub => new CaptureSubtask(sub.Text, sub.Checked))]);
        }).ToList();
        return new CapturePreview(split.IsBatch, items, split.Errors, context.NowUtc, context.ZoneId);
    }

    public async Task<CapturedTasksResult> CreateAsync(CreateCapturedTasksCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (command.Drafts.Count == 0)
        {
            throw AppException.Validation("drafts", "There is nothing to save. Type a task first.");
        }

        var lines = command.Drafts.Count + command.Drafts.Sum(draft => draft.Subtasks.Count);
        if (lines > BatchCapture.MaxLines)
        {
            throw AppException.Validation("drafts", $"At most {BatchCapture.MaxLines} tasks and subtasks can be saved at once; this has {lines}.");
        }

        var normalized = command with { Drafts = [.. command.Drafts.Select((draft, index) => Normalize(draft, $"drafts[{index}]"))] };
        return await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, normalized.OperationId, "task.quick-add", normalized, now, async inner =>
            {
                var created = new List<TaskRecord>(normalized.Drafts.Count);
                foreach (var draft in normalized.Drafts)
                {
                    created.Add(await CreateInSessionAsync(session, ids, profile, draft, normalized.OperationId, now, [], inner));
                }

                return new CapturedTasksResult(created, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>Field validation with line-addressable field paths such as drafts[3].dueTime.</summary>
    internal static CaptureTaskDraft Normalize(CaptureTaskDraft draft, string path)
    {
        var zone = draft.PlannedTime is not null || draft.DueTime is not null ? draft.ZoneId : null;
        var planned = TaskFieldValidation.Anchor(draft.PlannedDate, draft.PlannedTime, draft.PlannedTime is null ? null : zone, $"{path}.plannedDate", $"{path}.plannedTime", $"{path}.zoneId");
        var due = TaskFieldValidation.Anchor(draft.DueDate, draft.DueTime, draft.DueTime is null ? null : zone, $"{path}.dueDate", $"{path}.dueTime", $"{path}.zoneId");
        if (draft.Recurrence is { } rule)
        {
            try { rule.Validate(); }
            catch (DomainValidationException error) { throw AppException.Validation($"{path}.recurrence", error.Message); }
        }

        foreach (var reminder in draft.Reminders)
        {
            var valid = reminder.Anchor switch
            {
                CaptureReminderAnchor.Absolute => reminder.AtUtc is not null && reminder.OffsetMinutes is null,
                CaptureReminderAnchor.Planned => reminder.OffsetMinutes is { } offset && Math.Abs(offset) <= 525_600 && planned.Date is not null,
                CaptureReminderAnchor.Due => reminder.OffsetMinutes is { } offset && Math.Abs(offset) <= 525_600 && due.Date is not null,
                _ => false,
            };
            if (!valid)
            {
                throw AppException.Validation($"{path}.reminders", "A reminder needs an exact time, or an offset from a planned or due date the task has.");
            }
        }

        foreach (var tagPath in draft.TagPaths)
        {
            try { _ = TagName.Path(tagPath); }
            catch (DomainValidationException error) { throw AppException.Validation($"{path}.tags", error.Message); }
        }

        var waiting = string.IsNullOrWhiteSpace(draft.WaitingOn) ? null : draft.WaitingOn.Trim();
        if (waiting is { Length: > MaxWaitingLength })
        {
            throw AppException.Validation($"{path}.waitingOn", $"A waiting person's name can hold at most {MaxWaitingLength} characters.");
        }

        if (draft.FollowUpTime is not null && draft.FollowUpDate is null)
        {
            throw AppException.Validation($"{path}.followUpDate", "A follow-up time needs a date.");
        }

        return draft with
        {
            Title = TaskFieldValidation.Title(draft.Title, $"{path}.title"),
            Notes = TaskFieldValidation.Notes(draft.Notes, $"{path}.notes"),
            ContainerId = string.IsNullOrWhiteSpace(draft.ContainerId) ? null : draft.ContainerId,
            Priority = TaskFieldValidation.Priority(draft.Priority, $"{path}.priority"),
            EstimateMinutes = TaskFieldValidation.Estimate(draft.EstimateMinutes, $"{path}.estimateMinutes"),
            Category = draft.Category is { } category ? TaskFieldValidation.Category(category, $"{path}.category") : null,
            PlannedDate = planned.Date,
            PlannedTime = planned.Time,
            DueDate = due.Date,
            DueTime = due.Time,
            ZoneId = planned.ZoneId ?? due.ZoneId,
            TagIds = [.. draft.TagIds.Distinct(StringComparer.Ordinal)],
            TagPaths = [.. draft.TagPaths.Select(tag => tag.Trim()).Distinct(StringComparer.Ordinal)],
            WaitingOn = waiting,
            Subtasks = [.. draft.Subtasks.Select((subtask, index) => subtask with { Title = TaskFieldValidation.Title(subtask.Title, $"{path}.subtasks[{index}].title") })],
        };
    }

    /// <summary>
    /// Writes one normalized draft inside the caller's transaction: task, subtasks (done ones
    /// completed), tag links (paths select or create nested tags), reminder rules, a
    /// recurrence series, and the creation history entry.
    /// </summary>
    internal static async Task<TaskRecord> CreateInSessionAsync(IDataSession session, IIdGenerator ids, ProfileRecord profile, CaptureTaskDraft draft,
        string operationId, DateTimeOffset now, IReadOnlyList<string> extraHistoryFields, CancellationToken ct)
    {
        var container = await ResolveContainerAsync(session, profile, draft.ContainerId, ct);
        var task = new TaskRecord
        {
            Id = ids.NewId().ToString(),
            SpaceId = container.SpaceId,
            ContainerId = container.Id,
            Title = draft.Title,
            Notes = draft.Notes ?? string.Empty,
            Status = draft.WaitingOn is null ? TaskItemStatus.NotStarted : TaskItemStatus.WaitingOnSomeone,
            Priority = draft.Priority,
            EstimateMinutes = draft.EstimateMinutes,
            CapacityCategory = draft.Category ?? container.DefaultCategory ?? CapacityCategory.Personal,
            PlannedDate = draft.PlannedDate,
            PlannedTime = draft.PlannedTime,
            PlannedZoneId = draft.PlannedTime is null ? null : draft.ZoneId,
            DueDate = draft.DueDate,
            DueTime = draft.DueTime,
            DueZoneId = draft.DueTime is null ? null : draft.ZoneId,
            Position = await session.Tasks.GetNextPositionAsync(container.Id, ct),
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        await session.Tasks.AddAsync(task, ct);

        var subtasks = new List<SubtaskRecord>(draft.Subtasks.Count);
        for (var index = 0; index < draft.Subtasks.Count; index++)
        {
            var source = draft.Subtasks[index];
            var subtask = new SubtaskRecord
            {
                Id = ids.NewId().ToString(),
                TaskId = task.Id,
                Title = source.Title,
                Status = source.Done ? TaskItemStatus.Done : TaskItemStatus.NotStarted,
                CompletedAtUtc = source.Done ? now : null,
                Position = (index + 1) * RankOrdering.Gap,
                Version = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            await session.Subtasks.AddAsync(subtask, ct);
            subtasks.Add(subtask);
        }

        foreach (var tagId in draft.TagIds)
        {
            var tag = await session.Tags.GetAsync(tagId, ct);
            if (tag is null || tag.OwnerProfileId != profile.Id)
            {
                throw AppException.NotFound("A chosen tag no longer exists. Refresh and try again.");
            }

            await session.Tags.AddTaskLinkAsync(task.Id, tag.Id, ct);
        }

        foreach (var tagPath in draft.TagPaths)
        {
            var segments = TagName.Path(tagPath);
            var (leaf, _) = await TagCommands.EnsurePathAsync(session, ids, profile.Id, null, segments, now, ct);
            await session.Tags.AddTaskLinkAsync(task.Id, leaf.Id, ct);
        }

        foreach (var reminder in draft.Reminders)
        {
            await session.ReminderRules.AddAsync(new ReminderRuleRecord
            {
                Id = ids.NewId().ToString(),
                ProfileId = profile.Id,
                TaskId = task.Id,
                Anchor = reminder.Anchor switch
                {
                    CaptureReminderAnchor.Planned => ReminderAnchor.Planned,
                    CaptureReminderAnchor.Due => ReminderAnchor.Due,
                    _ => ReminderAnchor.Absolute,
                },
                OffsetMinutes = reminder.Anchor == CaptureReminderAnchor.Absolute ? null : reminder.OffsetMinutes,
                AbsoluteAtUtc = reminder.Anchor == CaptureReminderAnchor.Absolute ? reminder.AtUtc : null,
                Enabled = true,
                Version = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            }, ct);
        }

        if (draft.FollowUpDate is { } followUpDate)
        {
            var zone = (draft.FollowUpTime is not null && draft.ZoneId is not null ? Queries.TaskQueries.FindZone(draft.ZoneId) : null)
                ?? Reminders.ReminderSync.ProfileZone(profile);
            var at = Chaos.Domain.Reminders.ReminderTiming.ResolveLocal(zone, followUpDate, draft.FollowUpTime ?? profile.DefaultReminderTime);
            if (draft.WaitingOn is null)
            {
                await session.ReminderRules.AddAsync(new ReminderRuleRecord
                {
                    Id = ids.NewId().ToString(),
                    ProfileId = profile.Id,
                    TaskId = task.Id,
                    Anchor = ReminderAnchor.Absolute,
                    AbsoluteAtUtc = at,
                    Enabled = true,
                    Version = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                }, ct);
            }
            else
            {
                await Reminders.WaitingCommands.SetInSessionAsync(session, ids, profile, task.Id, draft.WaitingOn,
                    new Reminders.FollowUpSpec(Chaos.Domain.Reminders.FollowUpKind.Fixed, at, null, null), now, ct);
            }
        }
        else if (draft.WaitingOn is { } person)
        {
            await Reminders.WaitingCommands.SetInSessionAsync(session, ids, profile, task.Id, person, null, now, ct);
        }

        if (draft.Recurrence is { } rule)
        {
            await RecurrenceCommands.StartInSessionAsync(session, ids, task, rule, operationId, now, ct);
            task = await session.Tasks.GetAsync(task.Id, ct) ?? throw AppException.NotFound("That task no longer exists.");
        }

        await TaskMutationSupport.WriteTaskHistoryAsync(session, ids, operationId, before: null, after: new TaskAggregateSnapshot(task, subtasks),
            eventKind: "task.created", changedFields: [.. new[] { "Title", "Notes", "ContainerId" }.Concat(extraHistoryFields)], now, ct);
        return task;
    }

    private static async Task<ProfileRecord> RequireProfileAsync(IDataSession session, CancellationToken token) =>
        await session.Profiles.GetPrimaryAsync(token)
        ?? throw new AppException(AppErrorKind.StorageUnavailable, "No local profile exists yet. Restart Chaos to complete first-run setup.");

    private static async Task<SpaceRecord> RequireSpaceAsync(IDataSession session, ProfileRecord profile, CancellationToken token) =>
        await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token)
        ?? throw new AppException(AppErrorKind.StorageUnavailable, "No personal space exists yet. Restart Chaos to complete first-run setup.");

    private static async Task<ContainerRecord> ResolveContainerAsync(IDataSession session, ProfileRecord profile, string? containerId, CancellationToken token)
    {
        var space = await RequireSpaceAsync(session, profile, token);
        if (containerId is null)
        {
            return await session.Containers.GetInboxAsync(space.Id, token)
                ?? throw new AppException(AppErrorKind.StorageUnavailable, "No Inbox exists yet. Restart Chaos to complete first-run setup.");
        }

        var container = await session.Containers.GetAsync(containerId, token)
            ?? throw AppException.NotFound("That list or project no longer exists. Refresh and try again.");
        return container.SpaceId == space.Id
            ? container
            : throw AppException.Validation("containerId", "That list or project belongs to a different space. Choose one from your Personal space.");
    }
}
