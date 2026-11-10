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
    public readonly List<TaskHistoryRecord> History = [];
    public readonly List<OperationReceiptRecord> Receipts = [];
    public readonly List<BackupCatalogRecord> Backups = [];

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
        public ITaskStore Tasks { get; } = new FakeTaskStore(owner.Tasks);
        public ITaskHistoryStore History { get; } = new FakeTaskHistoryStore(owner.History);
        public IOperationReceiptStore Receipts { get; } = new FakeReceiptStore(owner.Receipts);
        public IBackupCatalogStore Backups { get; } = new FakeBackupCatalogStore(owner.Backups);
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
