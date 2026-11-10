using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;

namespace Chaos.Application.History;

/// <summary>Entity kinds whose rows an operation can change and undo can rewrite.</summary>
public enum ChangeKind
{
    Task = 1,
    Subtask = 2,
    Container = 3,
    Section = 4,
    Template = 5,
    Tag = 6,
    Diary = 7,
    Pad = 8,
    RecurrenceSeries = 9,
    RecurrenceOccurrence = 10,
    ReminderRule = 11,
    Waiting = 12,
    FollowUp = 13,
}

internal sealed class RecordingRecurrenceStore(RecordingDataSession recorder, IRecurrenceStore inner) : IRecurrenceStore
{
    public Task<RecurrenceSeriesRecord?> GetSeriesAsync(string id, CancellationToken ct) => inner.GetSeriesAsync(id, ct);
    public Task<long> NextRevisionAsync(string seriesId, CancellationToken ct) => inner.NextRevisionAsync(seriesId, ct);
    public Task<RecurrenceOccurrenceRecord?> GetOccurrenceAsync(string id, CancellationToken ct) => inner.GetOccurrenceAsync(id, ct);
    public Task<RecurrenceOccurrenceRecord?> FindByKeyAsync(string seriesId, string nominalKey, CancellationToken ct) => inner.FindByKeyAsync(seriesId, nominalKey, ct);
    public Task<IReadOnlyList<RecurrenceOccurrenceRecord>> ListActiveBySpaceAsync(string spaceId, CancellationToken ct) => inner.ListActiveBySpaceAsync(spaceId, ct);
    public Task<RecurrenceOccurrenceRecord?> FindByTaskAsync(string taskId, CancellationToken ct) => inner.FindByTaskAsync(taskId, ct);
    public Task<IReadOnlyList<RecurrenceOccurrenceRecord>> ListOccurrencesAsync(string seriesId, CancellationToken ct) => inner.ListOccurrencesAsync(seriesId, ct);
    public async Task AddSeriesAsync(RecurrenceSeriesRecord row, CancellationToken ct)
    {
        await inner.AddSeriesAsync(row, ct);
        recorder.RecordEntity<RecurrenceSeriesRecord>(ChangeKind.RecurrenceSeries, row.Id, null, row.Version);
    }
    public async Task<RecurrenceSeriesRecord?> UpdateSeriesAsync(RecurrenceSeriesRecord row, long expectedVersion, CancellationToken ct)
    {
        var before = recorder.IsRecorded(ChangeKind.RecurrenceSeries, row.Id) ? null : await inner.GetSeriesAsync(row.Id, ct);
        var applied = await inner.UpdateSeriesAsync(row, expectedVersion, ct);
        if (applied is not null) recorder.RecordEntity(ChangeKind.RecurrenceSeries, row.Id, before, row.Version);
        return applied;
    }
    public async Task AddOccurrenceAsync(RecurrenceOccurrenceRecord row, CancellationToken ct)
    {
        await inner.AddOccurrenceAsync(row, ct);
        recorder.RecordEntity<RecurrenceOccurrenceRecord>(ChangeKind.RecurrenceOccurrence, row.Id, null, row.Version);
    }
    public async Task<RecurrenceOccurrenceRecord?> UpdateOccurrenceAsync(RecurrenceOccurrenceRecord row, long expectedVersion, CancellationToken ct)
    {
        var before = recorder.IsRecorded(ChangeKind.RecurrenceOccurrence, row.Id) ? null : await inner.GetOccurrenceAsync(row.Id, ct);
        var applied = await inner.UpdateOccurrenceAsync(row, expectedVersion, ct);
        if (applied is not null) recorder.RecordEntity(ChangeKind.RecurrenceOccurrence, row.Id, before, row.Version);
        return applied;
    }
}

internal sealed class RecordingReminderRuleStore(RecordingDataSession recorder, IReminderRuleStore inner) : IReminderRuleStore
{
    public Task<ReminderRuleRecord?> GetAsync(string id, CancellationToken ct) => inner.GetAsync(id, ct);
    public Task<IReadOnlyList<ReminderRuleRecord>> ListByTaskAsync(string taskId, CancellationToken ct) => inner.ListByTaskAsync(taskId, ct);
    public async Task AddAsync(ReminderRuleRecord row, CancellationToken ct)
    {
        await recorder.TouchTaskAsync(row.TaskId, ct);
        await inner.AddAsync(row, ct);
        recorder.RecordEntity<ReminderRuleRecord>(ChangeKind.ReminderRule, row.Id, null, row.Version);
    }
    public async Task<ReminderRuleRecord?> UpdateAsync(ReminderRuleRecord row, long expectedVersion, CancellationToken ct)
    {
        await recorder.TouchTaskAsync(row.TaskId, ct);
        var before = recorder.IsRecorded(ChangeKind.ReminderRule, row.Id) ? null : await inner.GetAsync(row.Id, ct);
        var applied = await inner.UpdateAsync(row, expectedVersion, ct);
        if (applied is not null) recorder.RecordEntity(ChangeKind.ReminderRule, row.Id, before, row.Version);
        return applied;
    }
}

internal sealed class RecordingWaitingStore(RecordingDataSession recorder, IWaitingStore inner) : IWaitingStore
{
    public Task<WaitingDetailRecord?> GetAsync(string id, CancellationToken ct) => inner.GetAsync(id, ct);
    public Task<WaitingDetailRecord?> FindActiveAsync(string taskId, CancellationToken ct) => inner.FindActiveAsync(taskId, ct);
    public Task<FollowUpRecord?> GetFollowUpAsync(string id, CancellationToken ct) => inner.GetFollowUpAsync(id, ct);
    public Task<FollowUpRecord?> FindActiveFollowUpAsync(string taskId, CancellationToken ct) => inner.FindActiveFollowUpAsync(taskId, ct);
    public Task SetFollowUpDueAsync(string followUpId, DateTimeOffset? dueAtUtc, CancellationToken ct) => inner.SetFollowUpDueAsync(followUpId, dueAtUtc, ct);
    public Task<IReadOnlyList<FollowUpRecord>> ListActiveFollowUpsAsync(string profileId, CancellationToken ct) => inner.ListActiveFollowUpsAsync(profileId, ct);

    public async Task AddAsync(WaitingDetailRecord row, CancellationToken ct)
    {
        await recorder.TouchTaskAsync(row.TaskId, ct);
        await inner.AddAsync(row, ct);
        recorder.RecordEntity<WaitingDetailRecord>(ChangeKind.Waiting, row.Id, null, row.Version);
    }

    public async Task<WaitingDetailRecord?> UpdateAsync(WaitingDetailRecord row, long expectedVersion, CancellationToken ct)
    {
        await recorder.TouchTaskAsync(row.TaskId, ct);
        var before = recorder.IsRecorded(ChangeKind.Waiting, row.Id) ? null : await inner.GetAsync(row.Id, ct);
        var applied = await inner.UpdateAsync(row, expectedVersion, ct);
        if (applied is not null) recorder.RecordEntity(ChangeKind.Waiting, row.Id, before, row.Version);
        return applied;
    }

    public async Task AddFollowUpAsync(FollowUpRecord row, CancellationToken ct)
    {
        await recorder.TouchTaskAsync(row.TaskId, ct);
        await inner.AddFollowUpAsync(row, ct);
        recorder.RecordEntity<FollowUpRecord>(ChangeKind.FollowUp, row.Id, null, row.Version);
    }

    public async Task<FollowUpRecord?> UpdateFollowUpAsync(FollowUpRecord row, long expectedVersion, CancellationToken ct)
    {
        await recorder.TouchTaskAsync(row.TaskId, ct);
        var before = recorder.IsRecorded(ChangeKind.FollowUp, row.Id) ? null : await inner.GetFollowUpAsync(row.Id, ct);
        var applied = await inner.UpdateFollowUpAsync(row, expectedVersion, ct);
        if (applied is not null) recorder.RecordEntity(ChangeKind.FollowUp, row.Id, before, row.Version);
        return applied;
    }
}

public enum LinkKind
{
    TaskTag = 1,
    SubtaskTag = 2,
    PadTag = 3,
}

/// <summary>
/// The net change of one row inside one operation: its state before the operation (null when
/// the operation created it) and the version the operation left it at.
/// </summary>
public sealed record EntityChange(ChangeKind Kind, string Id, string? Before, long AfterVersion);

/// <summary>The net change of one tag link: whether the link exists after the operation.</summary>
public sealed record LinkChange(LinkKind Kind, string TargetId, string TagId, bool PresentAfter);

/// <summary>Everything one operation changed, in first-touch order; the inverse payload of its undo entry.</summary>
public sealed record ChangeSet(IReadOnlyList<EntityChange> Entities, IReadOnlyList<LinkChange> Links)
{
    public bool IsEmpty => Entities.Count == 0 && Links.Count == 0;
}

/// <summary>
/// Wraps the stores of one write transaction (PLAN §9 history and undo). Every row write
/// through it is recorded with the row's before-image, every effective tag-link change with
/// its direction, and every task the operation reaches (directly, through a subtask, a tag
/// link or a diary entry) gets its aggregate captured before the first write. When the
/// operation finishes, <see cref="CompleteAsync"/> appends one history entry per changed task
/// and one undo entry for the operation, in the same transaction.
/// </summary>
public sealed class RecordingDataSession : IDataSession
{
    /// <summary>Reversible operations kept per profile (PLAN §9).</summary>
    public const int UndoLimit = 100;

    private readonly IDataSession _inner;
    private readonly List<(ChangeKind Kind, string Id)> _entityOrder = [];
    private readonly Dictionary<(ChangeKind Kind, string Id), (string? Before, long AfterVersion)> _entities = [];
    private readonly List<(LinkKind Kind, string TargetId, string TagId)> _linkOrder = [];
    private readonly Dictionary<(LinkKind Kind, string TargetId, string TagId), (bool PresentBefore, bool PresentAfter)> _links = [];
    private readonly List<string> _taskOrder = [];
    private readonly Dictionary<string, TaskAggregateSnapshot?> _taskBefore = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string EventKind, List<string> Fields)> _registered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _diaryTasks = new(StringComparer.Ordinal);

    public RecordingDataSession(IDataSession inner)
    {
        _inner = inner;
        Tasks = new RecordingTaskStore(this, inner.Tasks);
        Subtasks = new RecordingSubtaskStore(this, inner.Subtasks);
        Containers = new RecordingContainerStore(this, inner.Containers);
        Sections = new RecordingSectionStore(this, inner.Sections);
        Templates = new RecordingTemplateStore(this, inner.Templates);
        Tags = new RecordingTagStore(this, inner.Tags);
        Diary = new RecordingDiaryStore(this, inner.Diary);
        Pad = new RecordingPadStore(this, inner.Pad);
        Waiting = new RecordingWaitingStore(this, inner.Waiting);
    }

    public IProfileStore Profiles => _inner.Profiles;
    public ISpaceStore Spaces => _inner.Spaces;
    public IContainerStore Containers { get; }
    public ISectionStore Sections { get; }
    public ITaskStore Tasks { get; }
    public ISubtaskStore Subtasks { get; }
    public ITemplateStore Templates { get; }
    public ITagStore Tags { get; }
    public ITaskQueryStore Queries => _inner.Queries;
    public ITaskHistoryStore History => _inner.History;
    public IDiaryStore Diary { get; }
    public IUndoStore Undo => _inner.Undo;
    public IChecklistConversionStore ChecklistConversions => _inner.ChecklistConversions;
    public IPadStore Pad { get; }
    public IRecurrenceStore Recurrence => new RecordingRecurrenceStore(this, _inner.Recurrence);
    public IReminderRuleStore ReminderRules => new RecordingReminderRuleStore(this, _inner.ReminderRules);
    public IWaitingStore Waiting { get; }
    public INotificationJobStore Notifications => _inner.Notifications;
    public IDayPlanStore DayPlans => _inner.DayPlans;
    public IWeekPlanStore Weeks => _inner.Weeks;
    public ISummaryStore Summaries => _inner.Summaries;
    public IOperationReceiptStore Receipts => _inner.Receipts;
    public IBackupCatalogStore Backups => _inner.Backups;
    public IImportStore Imports => _inner.Imports;

    /// <summary>The operation being run, set when its receipt check passes (not on a replay).</summary>
    public string? OperationId { get; private set; }
    public string? CommandKind { get; private set; }

    /// <summary>Undo itself is not undoable (redo is optional, PLAN §9); its history still records.</summary>
    public bool SuppressUndoEntry { get; set; }

    /// <summary>When set, every history entry of this operation uses this event kind (e.g. "undo").</summary>
    public string? HistoryEventKind { get; set; }

    /// <summary>The change set recorded so far.</summary>
    public ChangeSet Changes => new(
        [.. _entityOrder.Select(key => new EntityChange(key.Kind, key.Id, _entities[key].Before, _entities[key].AfterVersion))],
        [.. _linkOrder.Where(key => _links[key].PresentBefore != _links[key].PresentAfter)
            .Select(key => new LinkChange(key.Kind, key.TargetId, key.TagId, _links[key].PresentAfter))]);

    /// <summary>Every task this operation reached, in first-touch order.</summary>
    public IReadOnlyList<string> TouchedTaskIds => _taskOrder;

    internal void Begin(string operationId, string commandKind)
    {
        OperationId = operationId;
        CommandKind = commandKind;
    }

    /// <summary>
    /// A command's explicit history intent for one task: its event kind and the fields it
    /// changed. The entry itself is written at completion with the recorded before/after
    /// aggregates; several intents for one task merge (first kind wins, fields unite).
    /// </summary>
    internal void RegisterHistory(string taskId, string eventKind, IReadOnlyList<string> changedFields)
    {
        if (_registered.TryGetValue(taskId, out var existing))
        {
            existing.Fields.AddRange(changedFields.Where(field => !existing.Fields.Contains(field)));
            return;
        }

        _registered[taskId] = (eventKind, [.. changedFields]);
    }

    internal async Task TouchTaskAsync(string taskId, CancellationToken ct)
    {
        if (_taskBefore.ContainsKey(taskId))
        {
            return;
        }

        _taskBefore[taskId] = await BuildAggregateAsync(_inner, taskId, ct);
        _taskOrder.Add(taskId);
    }

    internal void MarkDiaryChange(string taskId) => _diaryTasks.Add(taskId);

    internal bool IsRecorded(ChangeKind kind, string id) => _entities.ContainsKey((kind, id));

    internal void RecordEntity<T>(ChangeKind kind, string id, T? before, long afterVersion) where T : class
    {
        var key = (kind, id);
        if (_entities.TryGetValue(key, out var existing))
        {
            _entities[key] = (existing.Before, afterVersion);
            return;
        }

        _entities[key] = (before is null ? null : JsonSerializer.Serialize(before, StoredJsonFormats.Options), afterVersion);
        _entityOrder.Add(key);
    }

    internal void RecordLink(LinkKind kind, string targetId, string tagId, bool presentAfter)
    {
        var key = (kind, targetId, tagId);
        if (_links.TryGetValue(key, out var existing))
        {
            _links[key] = (existing.PresentBefore, presentAfter);
            return;
        }

        _links[key] = (!presentAfter, presentAfter);
        _linkOrder.Add(key);
    }

    /// <summary>
    /// The task aggregate PLAN §9 snapshots: the task row (tombstoned or not), its active
    /// subtasks, and the active tag links of the task and of each subtask. Null when no such task.
    /// </summary>
    public static async Task<TaskAggregateSnapshot?> BuildAggregateAsync(IDataSession session, string taskId, CancellationToken ct)
    {
        var task = await session.Tasks.GetIncludingDeletedAsync(taskId, ct);
        if (task is null)
        {
            return null;
        }

        var subtasks = await session.Subtasks.ListByTaskAsync(taskId, ct);
        var subtaskTags = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var subtask in subtasks)
        {
            var linked = await session.Tags.ListSubtaskTagIdsAsync(subtask.Id, ct);
            if (linked.Count > 0)
            {
                subtaskTags[subtask.Id] = linked;
            }
        }

        return new TaskAggregateSnapshot(task, subtasks, await session.Tags.ListTaskTagIdsAsync(taskId, ct), subtaskTags,
            await session.ReminderRules.ListByTaskAsync(taskId, ct),
            await session.Waiting.FindActiveAsync(taskId, ct),
            await session.Waiting.FindActiveFollowUpAsync(taskId, ct));
    }

    /// <summary>
    /// Writes the history entries and the undo entry for everything recorded. Runs inside the
    /// operation's transaction after its body, so a failure rolls back the whole operation.
    /// </summary>
    public async Task CompleteAsync(ProfileRecord profile, IIdGenerator ids, DateTimeOffset now, CancellationToken ct)
    {
        if (OperationId is null)
        {
            return; // replayed or read-only: nothing was written
        }

        string? taskSubject = null;
        foreach (var taskId in _taskOrder)
        {
            var before = _taskBefore[taskId];
            var after = await BuildAggregateAsync(_inner, taskId, ct);
            if (after is null)
            {
                continue;
            }

            taskSubject ??= after.Task.Title;
            var beforeJson = before is null ? null : StoredJsonFormats.Snapshot(before);
            var afterJson = StoredJsonFormats.Snapshot(after);
            var diaryChanged = _diaryTasks.Contains(taskId);
            if (beforeJson == afterJson && !diaryChanged)
            {
                continue; // e.g. a link to a tag that is itself deleted: no visible change
            }

            // A one-occurrence subtask or tag edit belongs to that occurrence, not the
            // series template. Record the complete aggregate with the same undo operation.
            if (before is not null && beforeJson != afterJson && after.Task.RecurrenceOccurrenceKey is not null &&
                (CommandKind?.StartsWith("subtask.", StringComparison.Ordinal) == true || CommandKind?.StartsWith("tag.", StringComparison.Ordinal) == true))
            {
                var occurrence = await Recurrence.FindByTaskAsync(taskId, ct)
                    ?? throw new AppException(AppErrorKind.StorageUnavailable, "The recurrence occurrence could not be found.");
                var updated = occurrence with { EditSnapshotJson = afterJson, Version = occurrence.Version + 1, UpdatedAtUtc = now };
                if (await Recurrence.UpdateOccurrenceAsync(updated, occurrence.Version, ct) is null)
                    throw AppException.VersionConflict("The recurrence changed elsewhere.");
            }

            var derived = DerivedFields(before, after, diaryChanged);
            _registered.TryGetValue(taskId, out var intent);
            var fields = intent.Fields is null ? derived : [.. intent.Fields.Union(derived, StringComparer.Ordinal)];
            await _inner.History.AddAsync(new TaskHistoryRecord
            {
                Id = ids.NewId().ToString(),
                TaskId = taskId,
                OperationId = OperationId,
                EffectiveAtUtc = now,
                Actor = "local",
                EventKind = HistoryEventKind ?? intent.EventKind ?? DerivedEventKind(before, after, diaryChanged),
                ChangedFields = fields,
                SnapshotBefore = beforeJson,
                SnapshotAfter = afterJson,
                AggregateVersion = after.Task.Version,
                RecordedAtUtc = now,
            }, ct);
        }

        var changes = Changes;
        if (SuppressUndoEntry || changes.IsEmpty)
        {
            return;
        }

        var subject = (PreferredSubjectKind(CommandKind) is { } preferred ? await SubjectAsync(changes, preferred, ct) : null)
            ?? taskSubject ?? await SubjectAsync(changes, null, ct);
        await _inner.Undo.AddAsync(new UndoEntryRecord
        {
            Id = ids.NewId().ToString(),
            ProfileId = profile.Id,
            OperationId = OperationId,
            Label = UndoLabels.For(CommandKind ?? string.Empty, subject),
            InversePayload = StoredJsonFormats.Snapshot(changes),
            ExpectedVersions = JsonSerializer.Serialize(
                changes.Entities.ToDictionary(change => $"{change.Kind}:{change.Id}", change => change.AfterVersion),
                StoredJsonFormats.Options),
            AppliedAtUtc = now,
        }, ct);
        await _inner.Undo.PruneAsync(profile.Id, UndoLimit, ct);
    }

    /// <summary>The entity an operation is "about": a list command names its list even when tasks moved too.</summary>
    private static ChangeKind? PreferredSubjectKind(string? commandKind) => commandKind switch
    {
        null => null,
        "tag.link" or "tag.unlink" or "template.instantiate" => null,
        _ when commandKind.StartsWith("container.", StringComparison.Ordinal) || commandKind.StartsWith("project.", StringComparison.Ordinal) => ChangeKind.Container,
        _ when commandKind.StartsWith("section.", StringComparison.Ordinal) => ChangeKind.Section,
        _ when commandKind.StartsWith("template.", StringComparison.Ordinal) => ChangeKind.Template,
        _ when commandKind.StartsWith("tag.", StringComparison.Ordinal) => ChangeKind.Tag,
        "pad.make-task" => null,
        _ when commandKind.StartsWith("pad.", StringComparison.Ordinal) => ChangeKind.Pad,
        _ => null,
    };

    private async Task<string?> SubjectAsync(ChangeSet changes, ChangeKind? only, CancellationToken ct)
    {
        foreach (var change in changes.Entities.Where(change => only is null || change.Kind == only))
        {
            string? name = change.Kind switch
            {
                ChangeKind.Container => (await _inner.Containers.GetIncludingDeletedAsync(change.Id, ct))?.Name,
                ChangeKind.Section => (await _inner.Sections.GetIncludingDeletedAsync(change.Id, ct))?.Title,
                ChangeKind.Template => (await _inner.Templates.GetIncludingDeletedAsync(change.Id, ct))?.Name,
                ChangeKind.Tag => (await _inner.Tags.GetIncludingDeletedAsync(change.Id, ct))?.DisplayName,
                ChangeKind.Subtask => (await _inner.Subtasks.GetIncludingDeletedAsync(change.Id, ct))?.Title,
                ChangeKind.Pad => PadExcerpt.Of((await _inner.Pad.GetIncludingDeletedAsync(change.Id, ct))?.RawMarkdown),
                _ => null,
            };
            if (name is not null)
            {
                return name;
            }
        }

        return null;
    }

    private static List<string> DerivedFields(TaskAggregateSnapshot? before, TaskAggregateSnapshot after, bool diaryChanged)
    {
        var fields = new List<string>();
        if (before is null)
        {
            fields.AddRange(["Title", "Notes", "ContainerId"]);
        }
        else
        {
            fields.AddRange(TaskMutationSupport.ChangedTaskFields(before.Task, after.Task));
            if (Json(before.Subtasks) != Json(after.Subtasks) || Json(before.SubtaskTagIds) != Json(after.SubtaskTagIds))
            {
                fields.Add("Subtasks");
            }

            if (Json(before.TagIds ?? []) != Json(after.TagIds ?? []))
            {
                fields.Add("Tags");
            }

            if (Json(before.ReminderRules ?? []) != Json(after.ReminderRules ?? []))
            {
                fields.Add("Reminders");
            }

            if (Json(before.Waiting) != Json(after.Waiting))
            {
                fields.Add("Waiting");
            }

            if (Json(before.FollowUp) != Json(after.FollowUp))
            {
                fields.Add("FollowUp");
            }
        }

        if (diaryChanged)
        {
            fields.Add("Diary");
        }

        return fields;
    }

    private static string DerivedEventKind(TaskAggregateSnapshot? before, TaskAggregateSnapshot after, bool diaryChanged)
    {
        if (before is null)
        {
            return "task.created";
        }

        var (was, now) = (before.Task, after.Task);
        if (was.DeletedAtUtc is null && now.DeletedAtUtc is not null) return "task.deleted";
        if (was.DeletedAtUtc is not null && now.DeletedAtUtc is null) return "task.restored";
        if (was.Status != now.Status)
        {
            return now.Status switch
            {
                TaskItemStatus.Done => "task.completed",
                TaskItemStatus.Cancelled => "task.cancelled",
                _ when TaskItemStatusRules.IsTerminal(was.Status) => "task.reopened",
                _ => "task.status-changed",
            };
        }

        if (TaskMutationSupport.ChangedTaskFields(was, now).Count > 0) return "task.updated";
        if (Json(before.TagIds ?? []) != Json(after.TagIds ?? [])) return "task.tags-changed";
        if (diaryChanged) return "diary.changed";
        if (Json(before.Waiting) != Json(after.Waiting) || Json(before.FollowUp) != Json(after.FollowUp)) return "waiting.changed";
        if (Json(before.ReminderRules ?? []) != Json(after.ReminderRules ?? [])) return "reminder.changed";
        return "subtask.changed";
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, StoredJsonFormats.Options);
}

/// <summary>Readable labels for the undo list (PLAN §9 "change label").</summary>
internal static class UndoLabels
{
    private static readonly Dictionary<string, string> Verbs = new(StringComparer.Ordinal)
    {
        ["task.capture"] = "Add task",
        ["task.quick-add"] = "Add",
        ["task.update-details"] = "Edit task",
        ["task.set-status"] = "Change status of",
        ["task.move"] = "Move task",
        ["task.reorder"] = "Reorder task",
        ["task.delete"] = "Delete task",
        ["task.restore"] = "Restore task",
        ["task.restore-fields"] = "Restore earlier values of",
        ["subtask.add"] = "Add subtask",
        ["subtask.update"] = "Edit subtask",
        ["subtask.set-status"] = "Change subtask status",
        ["subtask.reorder"] = "Reorder subtask",
        ["subtask.delete"] = "Delete subtask",
        ["subtask.restore"] = "Restore subtask",
        ["subtask.convert-checklist"] = "Convert checklist of",
        ["container.create"] = "Create",
        ["container.rename"] = "Rename",
        ["container.reorder"] = "Reorder",
        ["container.archive"] = "Archive",
        ["container.unarchive"] = "Unarchive",
        ["container.delete"] = "Delete",
        ["container.restore"] = "Restore",
        ["project.deadline"] = "Change deadline of",
        ["section.create"] = "Add section",
        ["section.rename"] = "Rename section",
        ["section.reorder"] = "Reorder section",
        ["section.delete"] = "Delete section",
        ["section.restore"] = "Restore section",
        ["template.create"] = "Create template",
        ["template.update"] = "Edit template",
        ["template.delete"] = "Delete template",
        ["template.restore"] = "Restore template",
        ["template.instantiate"] = "Create task from template",
        ["tag.create"] = "Create tag",
        ["tag.rename"] = "Rename tag",
        ["tag.set-color"] = "Recolor tag",
        ["tag.move"] = "Move tag",
        ["tag.merge"] = "Merge tag",
        ["tag.delete"] = "Delete tag",
        ["tag.restore"] = "Restore tag",
        ["tag.link"] = "Tag",
        ["tag.unlink"] = "Untag",
        ["diary.add"] = "Add diary entry to",
        ["diary.edit"] = "Edit diary entry of",
        ["diary.delete"] = "Delete diary entry of",
        ["diary.restore"] = "Restore diary entry of",
        ["pad.create"] = "Add pad note",
        ["pad.archive"] = "Archive pad note",
        ["pad.unarchive"] = "Unarchive pad note",
        ["pad.delete"] = "Delete pad note",
        ["pad.restore"] = "Restore pad note",
        ["pad.reorder"] = "Reorder pad note",
        ["pad.review"] = "Archive pad notes in review",
        ["pad.make-task"] = "Make task",
        ["reminder.add"] = "Add reminder to",
        ["reminder.update"] = "Change reminder of",
        ["reminder.delete"] = "Delete reminder of",
        ["waiting.set"] = "Set waiting on",
        ["waiting.clear"] = "Stop waiting on",
        ["waiting.log"] = "Log follow-up on",
        ["followup.snooze"] = "Snooze follow-up of",
    };

    public static string For(string commandKind, string? subject)
    {
        var verb = Verbs.GetValueOrDefault(commandKind, "Change");
        if (string.IsNullOrWhiteSpace(subject))
        {
            return verb;
        }

        var shown = subject.Length > 60 ? string.Concat(subject.AsSpan(0, 59), "…") : subject;
        return $"{verb} “{shown}”";
    }
}

internal sealed class RecordingTaskStore(RecordingDataSession recorder, ITaskStore inner) : ITaskStore
{
    public Task<TaskRecord?> GetAsync(string taskId, CancellationToken ct) => inner.GetAsync(taskId, ct);
    public Task<TaskRecord?> GetIncludingDeletedAsync(string taskId, CancellationToken ct) => inner.GetIncludingDeletedAsync(taskId, ct);
    public Task<IReadOnlyList<TaskRecord>> ListByContainerAsync(string containerId, CancellationToken ct) => inner.ListByContainerAsync(containerId, ct);
    public Task<IReadOnlyList<TaskRecord>> ListDeletedByContainerAsync(string containerId, CancellationToken ct) => inner.ListDeletedByContainerAsync(containerId, ct);
    public Task<long> GetNextPositionAsync(string containerId, CancellationToken ct) => inner.GetNextPositionAsync(containerId, ct);

    public async Task AddAsync(TaskRecord task, CancellationToken ct)
    {
        await recorder.TouchTaskAsync(task.Id, ct);
        await inner.AddAsync(task, ct);
        recorder.RecordEntity<TaskRecord>(ChangeKind.Task, task.Id, null, task.Version);
    }

    public async Task<TaskRecord?> UpdateAsync(TaskRecord updated, long expectedVersion, CancellationToken ct)
    {
        var before = recorder.IsRecorded(ChangeKind.Task, updated.Id) ? null : await inner.GetIncludingDeletedAsync(updated.Id, ct);
        await recorder.TouchTaskAsync(updated.Id, ct);
        var applied = await inner.UpdateAsync(updated, expectedVersion, ct);
        if (applied is not null)
        {
            recorder.RecordEntity(ChangeKind.Task, applied.Id, before, applied.Version);
        }

        return applied;
    }
}

internal sealed class RecordingSubtaskStore(RecordingDataSession recorder, ISubtaskStore inner) : ISubtaskStore
{
    public Task<SubtaskRecord?> GetAsync(string subtaskId, CancellationToken ct) => inner.GetAsync(subtaskId, ct);
    public Task<SubtaskRecord?> GetIncludingDeletedAsync(string subtaskId, CancellationToken ct) => inner.GetIncludingDeletedAsync(subtaskId, ct);
    public Task<IReadOnlyList<SubtaskRecord>> ListByTaskAsync(string taskId, CancellationToken ct) => inner.ListByTaskAsync(taskId, ct);
    public Task<IReadOnlyList<SubtaskRecord>> ListByTaskIncludingDeletedAsync(string taskId, CancellationToken ct) => inner.ListByTaskIncludingDeletedAsync(taskId, ct);
    public Task<long> GetNextPositionAsync(string taskId, CancellationToken ct) => inner.GetNextPositionAsync(taskId, ct);

    public async Task AddAsync(SubtaskRecord subtask, CancellationToken ct)
    {
        await recorder.TouchTaskAsync(subtask.TaskId, ct);
        await inner.AddAsync(subtask, ct);
        recorder.RecordEntity<SubtaskRecord>(ChangeKind.Subtask, subtask.Id, null, subtask.Version);
    }

    public async Task<SubtaskRecord?> UpdateAsync(SubtaskRecord updated, long expectedVersion, CancellationToken ct)
    {
        var before = recorder.IsRecorded(ChangeKind.Subtask, updated.Id) ? null : await inner.GetIncludingDeletedAsync(updated.Id, ct);
        await recorder.TouchTaskAsync(updated.TaskId, ct);
        var applied = await inner.UpdateAsync(updated, expectedVersion, ct);
        if (applied is not null)
        {
            recorder.RecordEntity(ChangeKind.Subtask, applied.Id, before, applied.Version);
        }

        return applied;
    }
}

internal sealed class RecordingContainerStore(RecordingDataSession recorder, IContainerStore inner) : IContainerStore
{
    public Task<ContainerRecord?> GetAsync(string containerId, CancellationToken ct) => inner.GetAsync(containerId, ct);
    public Task<ContainerRecord?> GetIncludingDeletedAsync(string containerId, CancellationToken ct) => inner.GetIncludingDeletedAsync(containerId, ct);
    public Task<IReadOnlyList<ContainerRecord>> ListDeletedBySpaceAsync(string spaceId, CancellationToken ct) => inner.ListDeletedBySpaceAsync(spaceId, ct);
    public Task<ContainerRecord?> GetInboxAsync(string spaceId, CancellationToken ct) => inner.GetInboxAsync(spaceId, ct);
    public Task<IReadOnlyList<ContainerRecord>> ListBySpaceAsync(string spaceId, CancellationToken ct) => inner.ListBySpaceAsync(spaceId, ct);
    public Task<long> GetNextPositionAsync(string spaceId, CancellationToken ct) => inner.GetNextPositionAsync(spaceId, ct);
    public Task<long> CountActiveTasksAsync(string containerId, CancellationToken ct) => inner.CountActiveTasksAsync(containerId, ct);

    public async Task AddAsync(ContainerRecord container, CancellationToken ct)
    {
        await inner.AddAsync(container, ct);
        recorder.RecordEntity<ContainerRecord>(ChangeKind.Container, container.Id, null, container.Version);
    }

    public async Task<ContainerRecord?> UpdateAsync(ContainerRecord updated, long expectedVersion, CancellationToken ct)
    {
        var before = recorder.IsRecorded(ChangeKind.Container, updated.Id) ? null : await inner.GetIncludingDeletedAsync(updated.Id, ct);
        var applied = await inner.UpdateAsync(updated, expectedVersion, ct);
        if (applied is not null)
        {
            recorder.RecordEntity(ChangeKind.Container, applied.Id, before, applied.Version);
        }

        return applied;
    }
}

internal sealed class RecordingSectionStore(RecordingDataSession recorder, ISectionStore inner) : ISectionStore
{
    public Task<SectionRecord?> GetAsync(string sectionId, CancellationToken ct) => inner.GetAsync(sectionId, ct);
    public Task<SectionRecord?> GetIncludingDeletedAsync(string sectionId, CancellationToken ct) => inner.GetIncludingDeletedAsync(sectionId, ct);
    public Task<IReadOnlyList<SectionRecord>> ListDeletedByContainerAsync(string containerId, CancellationToken ct) => inner.ListDeletedByContainerAsync(containerId, ct);
    public Task<IReadOnlyList<SectionRecord>> ListByContainerAsync(string containerId, CancellationToken ct) => inner.ListByContainerAsync(containerId, ct);
    public Task<long> GetNextPositionAsync(string containerId, CancellationToken ct) => inner.GetNextPositionAsync(containerId, ct);

    public async Task AddAsync(SectionRecord section, CancellationToken ct)
    {
        await inner.AddAsync(section, ct);
        recorder.RecordEntity<SectionRecord>(ChangeKind.Section, section.Id, null, section.Version);
    }

    public async Task<SectionRecord?> UpdateAsync(SectionRecord updated, long expectedVersion, CancellationToken ct)
    {
        var before = recorder.IsRecorded(ChangeKind.Section, updated.Id) ? null : await inner.GetIncludingDeletedAsync(updated.Id, ct);
        var applied = await inner.UpdateAsync(updated, expectedVersion, ct);
        if (applied is not null)
        {
            recorder.RecordEntity(ChangeKind.Section, applied.Id, before, applied.Version);
        }

        return applied;
    }
}

internal sealed class RecordingTemplateStore(RecordingDataSession recorder, ITemplateStore inner) : ITemplateStore
{
    public Task<TemplateRecord?> GetAsync(string templateId, CancellationToken ct) => inner.GetAsync(templateId, ct);
    public Task<TemplateRecord?> GetIncludingDeletedAsync(string templateId, CancellationToken ct) => inner.GetIncludingDeletedAsync(templateId, ct);
    public Task<TemplateRecord?> FindByNameAsync(string spaceId, string normalizedName, CancellationToken ct) => inner.FindByNameAsync(spaceId, normalizedName, ct);
    public Task<IReadOnlyList<TemplateRecord>> ListBySpaceAsync(string spaceId, CancellationToken ct) => inner.ListBySpaceAsync(spaceId, ct);
    public Task<IReadOnlyList<TemplateRecord>> ListDeletedBySpaceAsync(string spaceId, CancellationToken ct) => inner.ListDeletedBySpaceAsync(spaceId, ct);

    public async Task AddAsync(TemplateRecord template, CancellationToken ct)
    {
        await inner.AddAsync(template, ct);
        recorder.RecordEntity<TemplateRecord>(ChangeKind.Template, template.Id, null, template.Version);
    }

    public async Task<TemplateRecord?> UpdateAsync(TemplateRecord updated, long expectedVersion, CancellationToken ct)
    {
        var before = recorder.IsRecorded(ChangeKind.Template, updated.Id) ? null : await inner.GetIncludingDeletedAsync(updated.Id, ct);
        var applied = await inner.UpdateAsync(updated, expectedVersion, ct);
        if (applied is not null)
        {
            recorder.RecordEntity(ChangeKind.Template, applied.Id, before, applied.Version);
        }

        return applied;
    }
}

internal sealed class RecordingTagStore(RecordingDataSession recorder, ITagStore inner) : ITagStore
{
    public Task<TagRecord?> GetAsync(string tagId, CancellationToken ct) => inner.GetAsync(tagId, ct);
    public Task<TagRecord?> GetIncludingDeletedAsync(string tagId, CancellationToken ct) => inner.GetIncludingDeletedAsync(tagId, ct);
    public Task<IReadOnlyList<TagRecord>> ListByOwnerAsync(string ownerProfileId, CancellationToken ct) => inner.ListByOwnerAsync(ownerProfileId, ct);
    public Task<IReadOnlyList<TagRecord>> ListDeletedByOwnerAsync(string ownerProfileId, CancellationToken ct) => inner.ListDeletedByOwnerAsync(ownerProfileId, ct);
    public Task<TagRecord?> FindChildAsync(string ownerProfileId, string? parentTagId, string normalizedName, CancellationToken ct) => inner.FindChildAsync(ownerProfileId, parentTagId, normalizedName, ct);
    public Task<IReadOnlyList<TagRecord>> ListChildrenAsync(string tagId, CancellationToken ct) => inner.ListChildrenAsync(tagId, ct);
    public Task<long> GetNextPositionAsync(string ownerProfileId, string? parentTagId, CancellationToken ct) => inner.GetNextPositionAsync(ownerProfileId, parentTagId, ct);
    public Task<IReadOnlyList<string>> ListTaskTagIdsAsync(string taskId, CancellationToken ct) => inner.ListTaskTagIdsAsync(taskId, ct);
    public Task<IReadOnlyList<string>> ListSubtaskTagIdsAsync(string subtaskId, CancellationToken ct) => inner.ListSubtaskTagIdsAsync(subtaskId, ct);
    public Task<IReadOnlyList<string>> ListTaskIdsAsync(string tagId, CancellationToken ct) => inner.ListTaskIdsAsync(tagId, ct);
    public Task<IReadOnlyList<string>> ListSubtaskIdsAsync(string tagId, CancellationToken ct) => inner.ListSubtaskIdsAsync(tagId, ct);

    public async Task AddAsync(TagRecord tag, CancellationToken ct)
    {
        await inner.AddAsync(tag, ct);
        recorder.RecordEntity<TagRecord>(ChangeKind.Tag, tag.Id, null, tag.Version);
    }

    /// <summary>
    /// A tag row change is visible on every task linked to it (its name/color/deletion shows
    /// in the task's tag list), so those tasks are touched before the write.
    /// </summary>
    public async Task<TagRecord?> UpdateAsync(TagRecord updated, long expectedVersion, CancellationToken ct)
    {
        var before = recorder.IsRecorded(ChangeKind.Tag, updated.Id) ? null : await inner.GetIncludingDeletedAsync(updated.Id, ct);
        foreach (var taskId in await inner.ListTaskIdsAsync(updated.Id, ct))
        {
            await recorder.TouchTaskAsync(taskId, ct);
        }

        foreach (var subtaskId in await inner.ListSubtaskIdsAsync(updated.Id, ct))
        {
            await TouchSubtaskParentAsync(subtaskId, ct);
        }

        var applied = await inner.UpdateAsync(updated, expectedVersion, ct);
        if (applied is not null)
        {
            recorder.RecordEntity(ChangeKind.Tag, applied.Id, before, applied.Version);
        }

        return applied;
    }

    public async Task<bool> AddTaskLinkAsync(string taskId, string tagId, CancellationToken ct)
    {
        await recorder.TouchTaskAsync(taskId, ct);
        var changed = await inner.AddTaskLinkAsync(taskId, tagId, ct);
        if (changed) recorder.RecordLink(LinkKind.TaskTag, taskId, tagId, presentAfter: true);
        return changed;
    }

    public async Task<bool> RemoveTaskLinkAsync(string taskId, string tagId, CancellationToken ct)
    {
        await recorder.TouchTaskAsync(taskId, ct);
        var changed = await inner.RemoveTaskLinkAsync(taskId, tagId, ct);
        if (changed) recorder.RecordLink(LinkKind.TaskTag, taskId, tagId, presentAfter: false);
        return changed;
    }

    public async Task<bool> AddSubtaskLinkAsync(string subtaskId, string tagId, CancellationToken ct)
    {
        await TouchSubtaskParentAsync(subtaskId, ct);
        var changed = await inner.AddSubtaskLinkAsync(subtaskId, tagId, ct);
        if (changed) recorder.RecordLink(LinkKind.SubtaskTag, subtaskId, tagId, presentAfter: true);
        return changed;
    }

    public async Task<bool> RemoveSubtaskLinkAsync(string subtaskId, string tagId, CancellationToken ct)
    {
        await TouchSubtaskParentAsync(subtaskId, ct);
        var changed = await inner.RemoveSubtaskLinkAsync(subtaskId, tagId, ct);
        if (changed) recorder.RecordLink(LinkKind.SubtaskTag, subtaskId, tagId, presentAfter: false);
        return changed;
    }

    public Task<IReadOnlyList<string>> ListPadTagIdsAsync(string padItemId, CancellationToken ct) => inner.ListPadTagIdsAsync(padItemId, ct);
    public Task<IReadOnlyList<string>> ListPadItemIdsAsync(string tagId, CancellationToken ct) => inner.ListPadItemIdsAsync(tagId, ct);

    public async Task<bool> AddPadLinkAsync(string padItemId, string tagId, CancellationToken ct)
    {
        var changed = await inner.AddPadLinkAsync(padItemId, tagId, ct);
        if (changed) recorder.RecordLink(LinkKind.PadTag, padItemId, tagId, presentAfter: true);
        return changed;
    }

    public async Task<bool> RemovePadLinkAsync(string padItemId, string tagId, CancellationToken ct)
    {
        var changed = await inner.RemovePadLinkAsync(padItemId, tagId, ct);
        if (changed) recorder.RecordLink(LinkKind.PadTag, padItemId, tagId, presentAfter: false);
        return changed;
    }

    private async Task TouchSubtaskParentAsync(string subtaskId, CancellationToken ct)
    {
        if (await recorder.Subtasks.GetIncludingDeletedAsync(subtaskId, ct) is { } subtask)
        {
            await recorder.TouchTaskAsync(subtask.TaskId, ct);
        }
    }
}

internal sealed class RecordingDiaryStore(RecordingDataSession recorder, IDiaryStore inner) : IDiaryStore
{
    public Task<DiaryEntryRecord?> GetAsync(string entryId, CancellationToken ct) => inner.GetAsync(entryId, ct);
    public Task<DiaryEntryRecord?> GetIncludingDeletedAsync(string entryId, CancellationToken ct) => inner.GetIncludingDeletedAsync(entryId, ct);
    public Task<IReadOnlyList<DiaryEntryRecord>> ListByTaskAsync(string taskId, bool includeDeleted, CancellationToken ct) => inner.ListByTaskAsync(taskId, includeDeleted, ct);
    public Task<IReadOnlyList<DiaryRevisionRecord>> ListRevisionsAsync(string entryId, CancellationToken ct) => inner.ListRevisionsAsync(entryId, ct);

    public async Task AddAsync(DiaryEntryRecord entry, CancellationToken ct)
    {
        await recorder.TouchTaskAsync(entry.TaskId, ct);
        await inner.AddAsync(entry, ct);
        recorder.RecordEntity<DiaryEntryRecord>(ChangeKind.Diary, entry.Id, null, entry.Version);
        recorder.MarkDiaryChange(entry.TaskId);
    }

    public async Task<DiaryEntryRecord?> UpdateAsync(DiaryEntryRecord updated, long expectedVersion, CancellationToken ct)
    {
        var before = recorder.IsRecorded(ChangeKind.Diary, updated.Id) ? null : await inner.GetIncludingDeletedAsync(updated.Id, ct);
        await recorder.TouchTaskAsync(updated.TaskId, ct);
        var applied = await inner.UpdateAsync(updated, expectedVersion, ct);
        if (applied is not null)
        {
            recorder.RecordEntity(ChangeKind.Diary, applied.Id, before, applied.Version);
            recorder.MarkDiaryChange(applied.TaskId);
        }

        return applied;
    }
}

internal sealed class RecordingPadStore(RecordingDataSession recorder, IPadStore inner) : IPadStore
{
    public Task<PadItemRecord?> GetAsync(string itemId, CancellationToken ct) => inner.GetAsync(itemId, ct);
    public Task<PadItemRecord?> GetIncludingDeletedAsync(string itemId, CancellationToken ct) => inner.GetIncludingDeletedAsync(itemId, ct);
    public Task<IReadOnlyList<PadItemRecord>> ListBySpaceAsync(string spaceId, bool archived, CancellationToken ct) => inner.ListBySpaceAsync(spaceId, archived, ct);
    public Task<IReadOnlyList<PadItemRecord>> ListDeletedBySpaceAsync(string spaceId, CancellationToken ct) => inner.ListDeletedBySpaceAsync(spaceId, ct);
    public Task<long> GetNextPositionAsync(string spaceId, CancellationToken ct) => inner.GetNextPositionAsync(spaceId, ct);
    public Task AddTaskLinkAsync(ScratchTaskLinkRecord link, CancellationToken ct) => inner.AddTaskLinkAsync(link, ct);
    public Task<IReadOnlyList<ScratchTaskLinkRecord>> ListTaskLinksAsync(string itemId, CancellationToken ct) => inner.ListTaskLinksAsync(itemId, ct);
    public Task<ScratchTaskLinkRecord?> FindTaskLinkAsync(string taskId, CancellationToken ct) => inner.FindTaskLinkAsync(taskId, ct);
    public Task<IReadOnlyList<string>> SearchAsync(string spaceId, string text, int limit, CancellationToken ct) => inner.SearchAsync(spaceId, text, limit, ct);

    public async Task AddAsync(PadItemRecord item, CancellationToken ct)
    {
        await inner.AddAsync(item, ct);
        recorder.RecordEntity<PadItemRecord>(ChangeKind.Pad, item.Id, null, item.Version);
    }

    public async Task<PadItemRecord?> UpdateAsync(PadItemRecord updated, long expectedVersion, CancellationToken ct)
    {
        var before = recorder.IsRecorded(ChangeKind.Pad, updated.Id) ? null : await inner.GetIncludingDeletedAsync(updated.Id, ct);
        var applied = await inner.UpdateAsync(updated, expectedVersion, ct);
        if (applied is not null)
        {
            recorder.RecordEntity(ChangeKind.Pad, applied.Id, before, applied.Version);
        }

        return applied;
    }
}

/// <summary>A short readable name for an untitled pad item: its first line of text.</summary>
public static class PadExcerpt
{
    /// <returns>The first line of text, or null when the note is empty.</returns>
    public static string? Of(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null; // an empty note has nothing to name it by
        }

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '>', '-', '*', '+', ' ').Trim();
            if (line.StartsWith("[ ] ", StringComparison.Ordinal) || line.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase))
            {
                line = line[4..];
            }

            if (line.Length > 0)
            {
                return line.Length > 80 ? string.Concat(line.AsSpan(0, 79), "…") : line;
            }
        }

        return null;
    }
}
