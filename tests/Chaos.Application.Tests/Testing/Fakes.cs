using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Tests.Testing;

/// <summary>Deterministic clock so receipts and snapshots carry a stable timestamp (PLAN §4).</summary>
public sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

/// <summary>Deterministic IDs so created records can be asserted exactly (PLAN §4).</summary>
public sealed class SequentialIdGenerator : IIdGenerator
{
    private int _counter;

    public Guid NewId() => new(_counter++, 0, 0, new byte[8]);
}

/// <summary>
/// In-memory IApplicationStore used to pin command intent (PLAN §16: Application tests use
/// recording/fake ports; real rollback/concurrency is proven against SQLite in
/// Chaos.Infrastructure.Tests). Lists are exposed so tests can assert what was recorded.
/// </summary>
public sealed class InMemoryStore : IApplicationStore
{
    public readonly List<ProfileRecord> Profiles = [];
    public readonly List<SpaceRecord> Spaces = [];
    public readonly List<ContainerRecord> Containers = [];
    public readonly List<TaskRecord> Tasks = [];
    public readonly List<SectionRecord> Sections = [];
    public readonly List<SubtaskRecord> Subtasks = [];
    public readonly List<TemplateRecord> Templates = [];
    public readonly List<TagRecord> Tags = [];
    public readonly HashSet<(string TaskId, string TagId)> TaskTags = [];
    public readonly HashSet<(string SubtaskId, string TagId)> SubtaskTags = [];
    public readonly List<TaskHistoryRecord> History = [];
    public readonly List<OperationReceiptRecord> Receipts = [];
    public readonly List<BackupCatalogRecord> Backups = [];
    public readonly List<DiaryEntryRecord> Diary = [];
    public readonly List<DiaryRevisionRecord> DiaryRevisions = [];
    public readonly List<UndoEntryRecord> Undo = [];
    public readonly List<ChecklistConversionRecord> ChecklistConversions = [];
    public readonly List<PadItemRecord> PadItems = [];
    public readonly List<ReminderRuleRecord> ReminderRules = [];
    public readonly List<WaitingDetailRecord> Waiting = [];
    public readonly List<FollowUpRecord> FollowUps = [];
    public readonly List<NotificationJobRecord> Jobs = [];
    public readonly List<ScratchTaskLinkRecord> PadTaskLinks = [];
    public readonly HashSet<(string PadItemId, string TagId)> PadTags = [];

    public Task<TResult> ExecuteAsync<TResult>(Func<IDataSession, CancellationToken, Task<TResult>> operation, CancellationToken ct = default) =>
        operation(new Session(this), ct);

    public Task<TResult> QueryAsync<TResult>(Func<IDataSession, CancellationToken, Task<TResult>> query, CancellationToken ct = default) =>
        query(new Session(this), ct);

    private sealed class Session(InMemoryStore owner) : IDataSession
    {
        public IProfileStore Profiles { get; } = new FakeProfileStore(owner.Profiles);
        public ISpaceStore Spaces { get; } = new FakeSpaceStore(owner.Spaces);
        public IContainerStore Containers { get; } = new FakeContainerStore(owner.Containers, owner.Tasks);
        public ISectionStore Sections { get; } = new FakeSectionStore(owner.Sections);
        public ISubtaskStore Subtasks { get; } = new FakeSubtaskStore(owner.Subtasks);
        public ITemplateStore Templates { get; } = new FakeTemplateStore(owner.Templates);
        public ITagStore Tags { get; } = new FakeTagStore(owner.Tags, owner.TaskTags, owner.SubtaskTags, owner.PadTags);
        public ITaskQueryStore Queries { get; } = new UnsupportedQueryStore();
        public ITaskStore Tasks { get; } = new FakeTaskStore(owner.Tasks);
        public ITaskHistoryStore History { get; } = new FakeTaskHistoryStore(owner.History);
        public IOperationReceiptStore Receipts { get; } = new FakeReceiptStore(owner.Receipts);
        public IBackupCatalogStore Backups { get; } = new FakeBackupCatalogStore(owner.Backups);
        public IDiaryStore Diary { get; } = new FakeDiaryStore(owner.Diary, owner.DiaryRevisions);
        public IUndoStore Undo { get; } = new FakeUndoStore(owner.Undo);
        public IChecklistConversionStore ChecklistConversions { get; } = new FakeChecklistConversionStore(owner.ChecklistConversions);
        public IPadStore Pad { get; } = new FakePadStore(owner.PadItems, owner.PadTaskLinks);
        public IRecurrenceStore Recurrence => throw new NotSupportedException("This test store does not implement recurrence.");
        public IReminderRuleStore ReminderRules { get; } = new FakeReminderRuleStore(owner.ReminderRules);
        public IWaitingStore Waiting { get; } = new FakeWaitingStore(owner.Waiting, owner.FollowUps);
        public INotificationJobStore Notifications { get; } = new FakeNotificationJobStore(owner.Jobs);
        public IDayPlanStore DayPlans => throw new NotSupportedException("This test store does not implement day plans; My Day is tested against SQLite.");
        public IWeekPlanStore Weeks => throw new NotSupportedException("This test store does not implement week plans; My Week is tested against SQLite.");
    }
}

public sealed class FakeReminderRuleStore(List<ReminderRuleRecord> rows) : IReminderRuleStore
{
    public Task<ReminderRuleRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(row => row.Id == id));
    public Task<IReadOnlyList<ReminderRuleRecord>> ListByTaskAsync(string taskId, CancellationToken ct) => Task.FromResult<IReadOnlyList<ReminderRuleRecord>>([.. rows.Where(row => row.TaskId == taskId && row.DeletedAtUtc is null)]);
    public Task AddAsync(ReminderRuleRecord row, CancellationToken ct) { rows.Add(row); return Task.CompletedTask; }
    public Task<ReminderRuleRecord?> UpdateAsync(ReminderRuleRecord row, long version, CancellationToken ct) => FakeUpdate.Apply(rows, row, version);
}

public sealed class FakeWaitingStore(List<WaitingDetailRecord> waiting, List<FollowUpRecord> followUps) : IWaitingStore
{
    public Task<WaitingDetailRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(waiting.FirstOrDefault(row => row.Id == id));
    public Task<WaitingDetailRecord?> FindActiveAsync(string taskId, CancellationToken ct) => Task.FromResult(waiting.FirstOrDefault(row => row.TaskId == taskId && row.DeletedAtUtc is null));
    public Task AddAsync(WaitingDetailRecord row, CancellationToken ct) { waiting.Add(row); return Task.CompletedTask; }
    public Task<WaitingDetailRecord?> UpdateAsync(WaitingDetailRecord row, long version, CancellationToken ct) => FakeUpdate.Apply(waiting, row, version);
    public Task<FollowUpRecord?> GetFollowUpAsync(string id, CancellationToken ct) => Task.FromResult(followUps.FirstOrDefault(row => row.Id == id));
    public Task<FollowUpRecord?> FindActiveFollowUpAsync(string taskId, CancellationToken ct) =>
        Task.FromResult(followUps.FirstOrDefault(row => row.TaskId == taskId && row.DeletedAtUtc is null && row.ResolvedAtUtc is null));
    public Task AddFollowUpAsync(FollowUpRecord row, CancellationToken ct) { followUps.Add(row); return Task.CompletedTask; }
    public Task<FollowUpRecord?> UpdateFollowUpAsync(FollowUpRecord row, long version, CancellationToken ct) => FakeUpdate.Apply(followUps, row, version);
    public Task SetFollowUpDueAsync(string followUpId, DateTimeOffset? dueAtUtc, CancellationToken ct)
    {
        var index = followUps.FindIndex(row => row.Id == followUpId);
        if (index >= 0) followUps[index] = followUps[index] with { DueAtUtc = dueAtUtc };
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<FollowUpRecord>> ListActiveFollowUpsAsync(string profileId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FollowUpRecord>>([.. followUps.Where(row => row.ProfileId == profileId && row.DeletedAtUtc is null && row.ResolvedAtUtc is null)]);
}

/// <summary>In-memory notification jobs; delivery and OS reconciliation are tested against SQLite.</summary>
public sealed class FakeNotificationJobStore(List<NotificationJobRecord> rows) : INotificationJobStore
{
    public Task<NotificationJobRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(row => row.Id == id));
    public Task<NotificationJobRecord?> FindLatestAsync(NotificationSourceKind kind, string sourceId, CancellationToken ct) =>
        Task.FromResult(rows.Where(row => row.SourceKind == kind && row.SourceId == sourceId).OrderByDescending(row => row.Generation).ThenByDescending(row => row.Sequence).FirstOrDefault());
    public Task<IReadOnlyList<NotificationJobRecord>> ListBySourceAsync(NotificationSourceKind kind, string sourceId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NotificationJobRecord>>([.. rows.Where(row => row.SourceKind == kind && row.SourceId == sourceId).OrderBy(row => row.Generation).ThenBy(row => row.Sequence)]);
    public Task<IReadOnlyList<NotificationJobRecord>> ListPendingByTaskAsync(string taskId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NotificationJobRecord>>([.. rows.Where(row => row.TaskId == taskId && row.State == NotificationJobState.Pending)]);
    public Task AddAsync(NotificationJobRecord row, CancellationToken ct) { rows.Add(row); return Task.CompletedTask; }
    public Task<bool> UpdateAsync(NotificationJobRecord row, NotificationJobState expectedState, CancellationToken ct)
    {
        var index = rows.FindIndex(existing => existing.Id == row.Id && existing.State == expectedState);
        if (index < 0) return Task.FromResult(false);
        rows[index] = row;
        return Task.FromResult(true);
    }
    public Task<IReadOnlyList<NotificationJobRecord>> ListDueAsync(string profileId, DateTimeOffset now, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NotificationJobRecord>>([.. rows.Where(row => row.ProfileId == profileId && row.State == NotificationJobState.Pending && row.DeliverAtUtc <= now).Take(limit)]);
    public Task<IReadOnlyList<NotificationJobRecord>> ListOsWorkAsync(string profileId, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<NotificationJobRecord>>([]);
    public Task<IReadOnlyList<NotificationJobRecord>> ListDeliveredAsync(string profileId, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NotificationJobRecord>>([.. rows.Where(row => row.ProfileId == profileId && row.DeliveredAtUtc is not null).OrderByDescending(row => row.DeliveredAtUtc).Take(limit)]);
    public Task<IReadOnlyList<string>> ListTasksWithSourcesAsync(string profileId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([.. rows.Where(row => row.ProfileId == profileId && row.State == NotificationJobState.Pending).Select(row => row.TaskId).Distinct()]);
}

public sealed class FakeDiaryStore(List<DiaryEntryRecord> rows, List<DiaryRevisionRecord> revisions) : IDiaryStore
{
    public Task<DiaryEntryRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(x => x.Id == id && x.DeletedAtUtc is null));
    public Task<DiaryEntryRecord?> GetIncludingDeletedAsync(string id, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(x => x.Id == id));
    public Task<IReadOnlyList<DiaryEntryRecord>> ListByTaskAsync(string taskId, bool includeDeleted, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DiaryEntryRecord>>(rows.Where(x => x.TaskId == taskId && (includeDeleted || x.DeletedAtUtc is null)).OrderBy(x => x.EntryAtUtc).ThenBy(x => x.Id, StringComparer.Ordinal).ToList());
    public Task<IReadOnlyList<DiaryRevisionRecord>> ListRevisionsAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<DiaryRevisionRecord>>(revisions.Where(x => x.EntryId == id).OrderBy(x => x.Revision).ToList());

    public Task AddAsync(DiaryEntryRecord row, CancellationToken ct)
    {
        rows.Add(row);
        Revise(row);
        return Task.CompletedTask;
    }

    public async Task<DiaryEntryRecord?> UpdateAsync(DiaryEntryRecord row, long version, CancellationToken ct)
    {
        var applied = await FakeUpdate.Apply(rows, row, version);
        if (applied is not null) Revise(applied);
        return applied;
    }

    private void Revise(DiaryEntryRecord row)
    {
        if (!revisions.Any(x => x.EntryId == row.Id && x.Revision == row.Revision))
        {
            revisions.Add(new DiaryRevisionRecord(row.Id, row.Revision, row.RawMarkdown, row.DeletedAtUtc is not null, row.UpdatedAtUtc));
        }
    }
}

public sealed class FakeUndoStore(List<UndoEntryRecord> rows) : IUndoStore
{
    public Task AddAsync(UndoEntryRecord entry, CancellationToken ct) { rows.Add(entry); return Task.CompletedTask; }
    public Task<UndoEntryRecord?> FindByOperationAsync(string profileId, string operationId, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(x => x.ProfileId == profileId && x.OperationId == operationId));
    public Task<IReadOnlyList<UndoEntryRecord>> ListOpenAsync(string profileId, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UndoEntryRecord>>(rows.Where(x => x.ProfileId == profileId && x.UndoneAtUtc is null).Reverse().Take(limit).ToList());

    public Task<bool> MarkUndoneAsync(string entryId, DateTimeOffset undoneAtUtc, CancellationToken ct)
    {
        var index = rows.FindIndex(x => x.Id == entryId && x.UndoneAtUtc is null);
        if (index < 0) return Task.FromResult(false);
        rows[index] = rows[index] with { UndoneAtUtc = undoneAtUtc };
        return Task.FromResult(true);
    }

    public Task ReplacePayloadAsync(string entryId, string inversePayload, string expectedVersions, CancellationToken ct)
    {
        var index = rows.FindIndex(x => x.Id == entryId && x.UndoneAtUtc is null);
        if (index >= 0) rows[index] = rows[index] with { InversePayload = inversePayload, ExpectedVersions = expectedVersions };
        return Task.CompletedTask;
    }

    public Task PruneAsync(string profileId, int keep, CancellationToken ct)
    {
        var mine = rows.Where(x => x.ProfileId == profileId).ToList();
        foreach (var old in mine.Take(Math.Max(0, mine.Count - keep))) rows.Remove(old);
        return Task.CompletedTask;
    }
}

public sealed class FakeChecklistConversionStore(List<ChecklistConversionRecord> rows) : IChecklistConversionStore
{
    public Task<ChecklistConversionRecord?> FindAsync(string taskId, string itemKey, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(x => x.TaskId == taskId && x.ItemKey == itemKey));

    public Task UpsertAsync(ChecklistConversionRecord marker, CancellationToken ct)
    {
        rows.RemoveAll(x => x.TaskId == marker.TaskId && x.ItemKey == marker.ItemKey);
        rows.Add(marker);
        return Task.CompletedTask;
    }
}

public sealed class FakeProfileStore(List<ProfileRecord> rows) : IProfileStore
{
    public Task<ProfileRecord?> GetAsync(string profileId, CancellationToken ct) =>
        Task.FromResult(rows.FirstOrDefault(row => row.Id == profileId));

    public Task<ProfileRecord?> GetPrimaryAsync(CancellationToken ct) =>
        Task.FromResult(rows.OrderBy(row => row.CreatedAtUtc).FirstOrDefault());

    public Task AddAsync(ProfileRecord profile, CancellationToken ct)
    {
        rows.Add(profile);
        return Task.CompletedTask;
    }

    public Task<ProfileRecord?> UpdateReminderSettingsAsync(ProfileRecord profile, long expectedVersion, CancellationToken ct) =>
        UpdateFirstWeekdayAsync(profile, expectedVersion, ct);

    public Task<ProfileRecord?> UpdateFirstWeekdayAsync(ProfileRecord profile, long expectedVersion, CancellationToken ct)
    {
        var index = rows.FindIndex(row => row.Id == profile.Id && row.Version == expectedVersion);
        if (index < 0)
        {
            return Task.FromResult<ProfileRecord?>(null);
        }

        rows[index] = profile;
        return Task.FromResult<ProfileRecord?>(profile);
    }
}

public sealed class FakeSpaceStore(List<SpaceRecord> rows) : ISpaceStore
{
    public Task<SpaceRecord?> GetAsync(string spaceId, CancellationToken ct) =>
        Task.FromResult(rows.FirstOrDefault(row => row.Id == spaceId));

    public Task<SpaceRecord?> GetPersonalByOwnerAsync(string ownerProfileId, CancellationToken ct) =>
        Task.FromResult(rows.FirstOrDefault(row => row.OwnerProfileId == ownerProfileId && row.Kind == SpaceKind.Personal));

    public Task AddAsync(SpaceRecord space, CancellationToken ct)
    {
        rows.Add(space);
        return Task.CompletedTask;
    }
}

public sealed class FakeContainerStore(List<ContainerRecord> rows, List<TaskRecord> tasks) : IContainerStore
{
    public Task<ContainerRecord?> GetIncludingDeletedAsync(string id, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(x => x.Id == id));
    public Task<IReadOnlyList<ContainerRecord>> ListDeletedBySpaceAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<ContainerRecord>>(rows.Where(x => x.SpaceId == id && x.DeletedAtUtc is not null).ToList());
    public Task<IReadOnlyList<ContainerRecord>> ListBySpaceAsync(string spaceId, CancellationToken ct) => Task.FromResult<IReadOnlyList<ContainerRecord>>(rows.Where(x => x.SpaceId == spaceId && x.DeletedAtUtc is null).OrderBy(x => x.Position).ThenBy(x => x.Id).ToList());
    public Task<long> GetNextPositionAsync(string spaceId, CancellationToken ct) => Task.FromResult(rows.Where(x => x.SpaceId == spaceId && x.DeletedAtUtc is null).Select(x => x.Position + 1024).DefaultIfEmpty(0).Max());
    public Task<long> CountActiveTasksAsync(string containerId, CancellationToken ct) => Task.FromResult((long)tasks.Count(x => x.ContainerId == containerId && x.DeletedAtUtc is null));
    public Task<ContainerRecord?> UpdateAsync(ContainerRecord updated, long expectedVersion, CancellationToken ct) => FakeUpdate.Apply(rows, updated, expectedVersion);
    public Task<ContainerRecord?> GetAsync(string containerId, CancellationToken ct) =>
        Task.FromResult(rows.FirstOrDefault(row => row.Id == containerId));

    public Task<ContainerRecord?> GetInboxAsync(string spaceId, CancellationToken ct) =>
        Task.FromResult(rows.FirstOrDefault(row => row.SpaceId == spaceId && row.IsInbox));

    public Task AddAsync(ContainerRecord container, CancellationToken ct)
    {
        rows.Add(container);
        return Task.CompletedTask;
    }
}

public sealed class FakeTaskStore(List<TaskRecord> rows) : ITaskStore
{
    public Task<IReadOnlyList<TaskRecord>> ListDeletedByContainerAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskRecord>>(rows.Where(x => x.ContainerId == id && x.DeletedAtUtc is not null).OrderByDescending(x => x.DeletedAtUtc).ThenBy(x => x.Id).ToList());
    public Task<TaskRecord?> GetIncludingDeletedAsync(string taskId, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(x => x.Id == taskId));
    public Task<IReadOnlyList<TaskRecord>> ListByContainerAsync(string containerId, CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskRecord>>(rows.Where(x => x.ContainerId == containerId && x.DeletedAtUtc is null).OrderBy(x => x.Position).ThenBy(x => x.Id).ToList());
    public Task<TaskRecord?> GetAsync(string taskId, CancellationToken ct) =>
        Task.FromResult(rows.FirstOrDefault(row => row.Id == taskId && row.DeletedAtUtc is null));

    public Task AddAsync(TaskRecord task, CancellationToken ct)
    {
        rows.Add(task);
        return Task.CompletedTask;
    }

    public Task<TaskRecord?> UpdateAsync(TaskRecord updated, long expectedVersion, CancellationToken ct)
    {
        var index = rows.FindIndex(row => row.Id == updated.Id && row.Version == expectedVersion);
        if (index < 0)
        {
            return Task.FromResult<TaskRecord?>(null);
        }

        rows[index] = updated;
        return Task.FromResult<TaskRecord?>(updated);
    }

    public Task<long> GetNextPositionAsync(string containerId, CancellationToken ct)
    {
        var positions = rows.Where(row => row.ContainerId == containerId && row.DeletedAtUtc is null)
            .Select(row => row.Position)
            .ToList();
        return Task.FromResult(positions.Count == 0 ? 0 : positions.Max() + 1024);
    }
}

public sealed class FakeTaskHistoryStore(List<TaskHistoryRecord> rows) : ITaskHistoryStore
{
    public Task<IReadOnlyList<TaskHistoryRecord>> ListByTaskAsync(string taskId, CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskHistoryRecord>>(rows.Where(x => x.TaskId == taskId).ToList());
    public Task AddAsync(TaskHistoryRecord entry, CancellationToken ct)
    {
        rows.Add(entry);
        return Task.CompletedTask;
    }
}

internal static class FakeUpdate
{
    public static Task<T?> Apply<T>(List<T> rows, T updated, long version) where T : AggregateRecord
    {
        var index = rows.FindIndex(x => x.Id == updated.Id && x.Version == version);
        if (index < 0) return Task.FromResult<T?>(null);
        rows[index] = updated;
        return Task.FromResult<T?>(updated);
    }
}

public sealed class FakeSectionStore(List<SectionRecord> rows) : ISectionStore
{
    public Task<SectionRecord?> GetIncludingDeletedAsync(string id, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(x => x.Id == id));
    public Task<IReadOnlyList<SectionRecord>> ListDeletedByContainerAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<SectionRecord>>(rows.Where(x => x.ContainerId == id && x.DeletedAtUtc is not null).ToList());
    public Task<SectionRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(x => x.Id == id && x.DeletedAtUtc is null));
    public Task<IReadOnlyList<SectionRecord>> ListByContainerAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<SectionRecord>>(rows.Where(x => x.ContainerId == id && x.DeletedAtUtc is null).OrderBy(x => x.Position).ThenBy(x => x.Id).ToList());
    public Task AddAsync(SectionRecord row, CancellationToken ct) { rows.Add(row); return Task.CompletedTask; }
    public Task<SectionRecord?> UpdateAsync(SectionRecord row, long version, CancellationToken ct) => FakeUpdate.Apply(rows, row, version);
    public Task<long> GetNextPositionAsync(string id, CancellationToken ct) => Task.FromResult(rows.Where(x => x.ContainerId == id && x.DeletedAtUtc is null).Select(x => x.Position + 1024).DefaultIfEmpty(0).Max());
}

public sealed class FakeSubtaskStore(List<SubtaskRecord> rows) : ISubtaskStore
{
    public Task<SubtaskRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(x => x.Id == id && x.DeletedAtUtc is null));
    public Task<SubtaskRecord?> GetIncludingDeletedAsync(string id, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(x => x.Id == id));
    public Task<IReadOnlyList<SubtaskRecord>> ListByTaskAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<SubtaskRecord>>(rows.Where(x => x.TaskId == id && x.DeletedAtUtc is null).OrderBy(x => x.Position).ThenBy(x => x.Id).ToList());
    public Task<IReadOnlyList<SubtaskRecord>> ListByTaskIncludingDeletedAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<SubtaskRecord>>(rows.Where(x => x.TaskId == id).OrderBy(x => x.Position).ThenBy(x => x.Id).ToList());
    public Task AddAsync(SubtaskRecord row, CancellationToken ct) { rows.Add(row); return Task.CompletedTask; }
    public Task<SubtaskRecord?> UpdateAsync(SubtaskRecord row, long version, CancellationToken ct) => FakeUpdate.Apply(rows, row, version);
    public Task<long> GetNextPositionAsync(string id, CancellationToken ct) => Task.FromResult(rows.Where(x => x.TaskId == id && x.DeletedAtUtc is null).Select(x => x.Position + 1024).DefaultIfEmpty(0).Max());
}

public sealed class FakeTemplateStore(List<TemplateRecord> rows) : ITemplateStore
{
    public Task<IReadOnlyList<TemplateRecord>> ListDeletedBySpaceAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<TemplateRecord>>(rows.Where(x => x.SpaceId == id && x.DeletedAtUtc is not null).OrderByDescending(x => x.DeletedAtUtc).ThenBy(x => x.Id).ToList());
    public Task<TemplateRecord?> GetIncludingDeletedAsync(string id, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(x => x.Id == id));
    public Task<TemplateRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(x => x.Id == id && x.DeletedAtUtc is null));
    public Task<TemplateRecord?> FindByNameAsync(string id, string name, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(x => x.SpaceId == id && x.NormalizedName == name && x.DeletedAtUtc is null));
    public Task<IReadOnlyList<TemplateRecord>> ListBySpaceAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<TemplateRecord>>(rows.Where(x => x.SpaceId == id && x.DeletedAtUtc is null).OrderBy(x => x.NormalizedName).ThenBy(x => x.Id).ToList());
    public Task AddAsync(TemplateRecord row, CancellationToken ct) { rows.Add(row); return Task.CompletedTask; }
    public Task<TemplateRecord?> UpdateAsync(TemplateRecord row, long version, CancellationToken ct) => FakeUpdate.Apply(rows, row, version);
}

public sealed class FakeReceiptStore(List<OperationReceiptRecord> rows) : IOperationReceiptStore
{
    public Task<IReadOnlyList<OperationReceiptRecord>> ListByKindAsync(string profileId, string kind, CancellationToken ct) => Task.FromResult<IReadOnlyList<OperationReceiptRecord>>(rows.Where(x => x.ProfileId == profileId && x.CommandKind == kind).Reverse().ToList());
    public Task<OperationReceiptRecord?> FindAsync(string profileId, string operationId, CancellationToken ct) =>
        Task.FromResult(rows.FirstOrDefault(row => row.ProfileId == profileId && row.OperationId == operationId));

    public Task AddAsync(OperationReceiptRecord receipt, CancellationToken ct)
    {
        rows.Add(receipt);
        return Task.CompletedTask;
    }
}

public sealed class FakeBackupCatalogStore(List<BackupCatalogRecord> rows) : IBackupCatalogStore
{
    public Task AddAsync(BackupCatalogRecord entry, CancellationToken ct)
    {
        rows.Add(entry);
        return Task.CompletedTask;
    }

    public Task<BackupCatalogRecord?> FindByFileNameAsync(string fileName, CancellationToken ct) =>
        Task.FromResult(rows.FirstOrDefault(row => row.FileName == fileName));
}

public sealed class FakeTagStore(List<TagRecord> rows, HashSet<(string TaskId, string TagId)> taskLinks, HashSet<(string SubtaskId, string TagId)> subtaskLinks, HashSet<(string PadItemId, string TagId)> padLinks) : ITagStore
{
    private IEnumerable<TagRecord> Active => rows.Where(x => x.DeletedAtUtc is null);
    private static IReadOnlyList<TagRecord> Ordered(IEnumerable<TagRecord> tags) => tags.OrderBy(x => x.Position).ThenBy(x => x.NormalizedName, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
    private IEnumerable<string> ActiveIds => Active.Select(x => x.Id);
    public Task<TagRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(Active.FirstOrDefault(x => x.Id == id));
    public Task<TagRecord?> GetIncludingDeletedAsync(string id, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(x => x.Id == id));
    public Task<IReadOnlyList<TagRecord>> ListByOwnerAsync(string owner, CancellationToken ct) => Task.FromResult(Ordered(Active.Where(x => x.OwnerProfileId == owner)));
    public Task<IReadOnlyList<TagRecord>> ListDeletedByOwnerAsync(string owner, CancellationToken ct) => Task.FromResult<IReadOnlyList<TagRecord>>(rows.Where(x => x.OwnerProfileId == owner && x.DeletedAtUtc is not null).OrderByDescending(x => x.DeletedAtUtc).ThenBy(x => x.Id).ToList());
    public Task<TagRecord?> FindChildAsync(string owner, string? parent, string name, CancellationToken ct) => Task.FromResult(Active.FirstOrDefault(x => x.OwnerProfileId == owner && x.ParentTagId == parent && x.NormalizedName == name));
    public Task<IReadOnlyList<TagRecord>> ListChildrenAsync(string id, CancellationToken ct) => Task.FromResult(Ordered(Active.Where(x => x.ParentTagId == id)));
    public Task AddAsync(TagRecord row, CancellationToken ct) { rows.Add(row); return Task.CompletedTask; }
    public Task<TagRecord?> UpdateAsync(TagRecord row, long version, CancellationToken ct) => FakeUpdate.Apply(rows, row, version);
    public Task<long> GetNextPositionAsync(string owner, string? parent, CancellationToken ct) => Task.FromResult(Active.Where(x => x.OwnerProfileId == owner && x.ParentTagId == parent).Select(x => x.Position + 1024).DefaultIfEmpty(0).Max());
    public Task<IReadOnlyList<string>> ListTaskTagIdsAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(taskLinks.Where(x => x.TaskId == id && ActiveIds.Contains(x.TagId)).Select(x => x.TagId).Order(StringComparer.Ordinal).ToList());
    public Task<IReadOnlyList<string>> ListSubtaskTagIdsAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(subtaskLinks.Where(x => x.SubtaskId == id && ActiveIds.Contains(x.TagId)).Select(x => x.TagId).Order(StringComparer.Ordinal).ToList());
    public Task<IReadOnlyList<string>> ListTaskIdsAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(taskLinks.Where(x => x.TagId == id).Select(x => x.TaskId).Order(StringComparer.Ordinal).ToList());
    public Task<IReadOnlyList<string>> ListSubtaskIdsAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(subtaskLinks.Where(x => x.TagId == id).Select(x => x.SubtaskId).Order(StringComparer.Ordinal).ToList());
    public Task<bool> AddTaskLinkAsync(string target, string tag, CancellationToken ct) => Task.FromResult(taskLinks.Add((target, tag)));
    public Task<bool> RemoveTaskLinkAsync(string target, string tag, CancellationToken ct) => Task.FromResult(taskLinks.Remove((target, tag)));
    public Task<bool> AddSubtaskLinkAsync(string target, string tag, CancellationToken ct) => Task.FromResult(subtaskLinks.Add((target, tag)));
    public Task<bool> RemoveSubtaskLinkAsync(string target, string tag, CancellationToken ct) => Task.FromResult(subtaskLinks.Remove((target, tag)));
    public Task<IReadOnlyList<string>> ListPadTagIdsAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(padLinks.Where(x => x.PadItemId == id && ActiveIds.Contains(x.TagId)).Select(x => x.TagId).Order(StringComparer.Ordinal).ToList());
    public Task<IReadOnlyList<string>> ListPadItemIdsAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(padLinks.Where(x => x.TagId == id).Select(x => x.PadItemId).Order(StringComparer.Ordinal).ToList());
    public Task<bool> AddPadLinkAsync(string target, string tag, CancellationToken ct) => Task.FromResult(padLinks.Add((target, tag)));
    public Task<bool> RemovePadLinkAsync(string target, string tag, CancellationToken ct) => Task.FromResult(padLinks.Remove((target, tag)));
}

public sealed class FakePadStore(List<PadItemRecord> rows, List<ScratchTaskLinkRecord> links) : IPadStore
{
    public Task<PadItemRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(x => x.Id == id && x.DeletedAtUtc is null));
    public Task<PadItemRecord?> GetIncludingDeletedAsync(string id, CancellationToken ct) => Task.FromResult(rows.FirstOrDefault(x => x.Id == id));
    public Task<IReadOnlyList<PadItemRecord>> ListBySpaceAsync(string space, bool archived, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PadItemRecord>>(rows.Where(x => x.SpaceId == space && x.DeletedAtUtc is null && (x.ArchivedAtUtc is not null) == archived).OrderBy(x => x.Position).ThenBy(x => x.Id, StringComparer.Ordinal).ToList());
    public Task<IReadOnlyList<PadItemRecord>> ListDeletedBySpaceAsync(string space, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PadItemRecord>>(rows.Where(x => x.SpaceId == space && x.DeletedAtUtc is not null).ToList());
    public Task AddAsync(PadItemRecord row, CancellationToken ct) { rows.Add(row); return Task.CompletedTask; }
    public Task<PadItemRecord?> UpdateAsync(PadItemRecord row, long version, CancellationToken ct) => FakeUpdate.Apply(rows, row, version);
    public Task<long> GetNextPositionAsync(string space, CancellationToken ct) => Task.FromResult(rows.Where(x => x.SpaceId == space && x.DeletedAtUtc is null).Select(x => x.Position + 1024).DefaultIfEmpty(0).Max());
    public Task AddTaskLinkAsync(ScratchTaskLinkRecord link, CancellationToken ct) { links.Add(link); return Task.CompletedTask; }
    public Task<IReadOnlyList<ScratchTaskLinkRecord>> ListTaskLinksAsync(string id, CancellationToken ct) => Task.FromResult<IReadOnlyList<ScratchTaskLinkRecord>>(links.Where(x => x.PadItemId == id).ToList());
    public Task<ScratchTaskLinkRecord?> FindTaskLinkAsync(string taskId, CancellationToken ct) => Task.FromResult(links.FirstOrDefault(x => x.TaskId == taskId));
    public Task<IReadOnlyList<string>> SearchAsync(string space, string text, int limit, CancellationToken ct) => throw new NotSupportedException("Search runs against SQLite; see Chaos.Infrastructure.Tests.");
}

/// <summary>
/// Built-in views and search are SQL predicates over real SQLite (PLAN §16): they are proven
/// in Chaos.Infrastructure.Tests, so the in-memory session refuses them rather than
/// re-implementing the SQL semantics in a second, untested way.
/// </summary>
public sealed class UnsupportedQueryStore : ITaskQueryStore
{
    private static NotSupportedException Refuse() => new("Task queries run against SQLite; see Chaos.Infrastructure.Tests.");
    public Task<TaskQueryPage> QueryAsync(TaskQueryCriteria criteria, CancellationToken ct) => throw Refuse();
    public Task<long> CountAsync(TaskQueryCriteria criteria, CancellationToken ct) => throw Refuse();
    public Task<IReadOnlyList<string>> ListOpenDueZoneIdsAsync(string spaceId, CancellationToken ct) => throw Refuse();
    public Task<long> RebuildSearchIndexAsync(DateTimeOffset now, CancellationToken ct) => throw Refuse();
}
