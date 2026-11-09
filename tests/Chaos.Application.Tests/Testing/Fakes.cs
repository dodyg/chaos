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
        public IContainerStore Containers { get; } = new FakeContainerStore(owner.Containers);
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

public sealed class FakeContainerStore(List<ContainerRecord> rows) : IContainerStore
{
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
    public Task<TaskRecord?> GetAsync(string taskId, CancellationToken ct) =>
        Task.FromResult(rows.FirstOrDefault(row => row.Id == taskId && row.DeletedAtUtc is null));

    public Task AddAsync(TaskRecord task, CancellationToken ct)
    {
        rows.Add(task);
        return Task.CompletedTask;
    }

    public Task<TaskRecord?> UpdateAsync(TaskRecord updated, long expectedVersion, CancellationToken ct)
    {
        var index = rows.FindIndex(row => row.Id == updated.Id && row.Version == expectedVersion && row.DeletedAtUtc is null);
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
    public Task AddAsync(TaskHistoryRecord entry, CancellationToken ct)
    {
        rows.Add(entry);
        return Task.CompletedTask;
    }
}

public sealed class FakeReceiptStore(List<OperationReceiptRecord> rows) : IOperationReceiptStore
{
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
