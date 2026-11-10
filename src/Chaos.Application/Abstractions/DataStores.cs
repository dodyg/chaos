using Chaos.Application.Contracts;

namespace Chaos.Application.Abstractions;

/// <summary>
/// Typed stores over one bounded unit; no arbitrary SQL from the UI layer (PLAN §4).
/// A session is either a write transaction (ExecuteAsync) or a read snapshot (QueryAsync).
/// </summary>
public interface IDataSession
{
    IProfileStore Profiles { get; }
    ISpaceStore Spaces { get; }
    IContainerStore Containers { get; }
    ISectionStore Sections { get; }
    ITaskStore Tasks { get; }
    ISubtaskStore Subtasks { get; }
    ITemplateStore Templates { get; }
    ITaskHistoryStore History { get; }
    IOperationReceiptStore Receipts { get; }
    IBackupCatalogStore Backups { get; }
}

public interface IProfileStore
{
    Task<ProfileRecord?> GetAsync(string profileId, CancellationToken ct);
    Task<ProfileRecord?> GetPrimaryAsync(CancellationToken ct);
    Task AddAsync(ProfileRecord profile, CancellationToken ct);
}

public interface ISpaceStore
{
    Task<SpaceRecord?> GetAsync(string spaceId, CancellationToken ct);
    Task<SpaceRecord?> GetPersonalByOwnerAsync(string ownerProfileId, CancellationToken ct);
    Task AddAsync(SpaceRecord space, CancellationToken ct);
}

public interface IContainerStore
{
    Task<ContainerRecord?> GetAsync(string containerId, CancellationToken ct);
    Task<ContainerRecord?> GetInboxAsync(string spaceId, CancellationToken ct);
    Task<IReadOnlyList<ContainerRecord>> ListBySpaceAsync(string spaceId, CancellationToken ct);
    Task AddAsync(ContainerRecord container, CancellationToken ct);
    Task<ContainerRecord?> UpdateAsync(ContainerRecord updated, long expectedVersion, CancellationToken ct);
    Task<long> GetNextPositionAsync(string spaceId, CancellationToken ct);
    /// <summary>Active (nontombstoned) top-level task count inside one container.</summary>
    Task<long> CountActiveTasksAsync(string containerId, CancellationToken ct);
}

public interface ISectionStore
{
    Task<SectionRecord?> GetAsync(string sectionId, CancellationToken ct);
    /// <summary>Active sections of one container, ordered by (Position, Id).</summary>
    Task<IReadOnlyList<SectionRecord>> ListByContainerAsync(string containerId, CancellationToken ct);
    Task AddAsync(SectionRecord section, CancellationToken ct);
    Task<SectionRecord?> UpdateAsync(SectionRecord updated, long expectedVersion, CancellationToken ct);
    Task<long> GetNextPositionAsync(string containerId, CancellationToken ct);
}

public interface ITaskStore
{
    Task<TaskRecord?> GetAsync(string taskId, CancellationToken ct);
    /// <summary>The row even when tombstoned; restore and history need the deleted record.</summary>
    Task<TaskRecord?> GetIncludingDeletedAsync(string taskId, CancellationToken ct);
    /// <summary>Active top-level tasks of one container, ordered by (Position, Id).</summary>
    Task<IReadOnlyList<TaskRecord>> ListByContainerAsync(string containerId, CancellationToken ct);
    Task<IReadOnlyList<TaskRecord>> ListDeletedByContainerAsync(string containerId, CancellationToken ct);
    Task AddAsync(TaskRecord task, CancellationToken ct);
    /// <summary>
    /// Optimistic-concurrency update: rewrites the row only when its stored version equals
    /// <paramref name="expectedVersion"/> (PLAN §5). Returns the updated row, or null when
    /// the row is gone or was changed concurrently (version mismatch).
    /// </summary>
    Task<TaskRecord?> UpdateAsync(TaskRecord updated, long expectedVersion, CancellationToken ct);
    /// <summary>Highest Position currently used inside a container among active tasks.</summary>
    Task<long> GetNextPositionAsync(string containerId, CancellationToken ct);
}

public interface ISubtaskStore
{
    Task<SubtaskRecord?> GetAsync(string subtaskId, CancellationToken ct);
    /// <summary>Active subtasks of one task, ordered by (Position, Id).</summary>
    Task<IReadOnlyList<SubtaskRecord>> ListByTaskAsync(string taskId, CancellationToken ct);
    /// <summary>All subtasks of one task including tombstoned ones (history snapshots, restore).</summary>
    Task<IReadOnlyList<SubtaskRecord>> ListByTaskIncludingDeletedAsync(string taskId, CancellationToken ct);
    Task AddAsync(SubtaskRecord subtask, CancellationToken ct);
    Task<SubtaskRecord?> UpdateAsync(SubtaskRecord updated, long expectedVersion, CancellationToken ct);
    Task<long> GetNextPositionAsync(string taskId, CancellationToken ct);
}

public interface ITemplateStore
{
    Task<TemplateRecord?> GetAsync(string templateId, CancellationToken ct);
    Task<TemplateRecord?> GetIncludingDeletedAsync(string templateId, CancellationToken ct);
    /// <summary>Active template with this normalized name in the space, if any.</summary>
    Task<TemplateRecord?> FindByNameAsync(string spaceId, string normalizedName, CancellationToken ct);
    /// <summary>Active templates of one space, ordered by (NormalizedName, Id).</summary>
    Task<IReadOnlyList<TemplateRecord>> ListBySpaceAsync(string spaceId, CancellationToken ct);
    Task<IReadOnlyList<TemplateRecord>> ListDeletedBySpaceAsync(string spaceId, CancellationToken ct);
    Task AddAsync(TemplateRecord template, CancellationToken ct);
    Task<TemplateRecord?> UpdateAsync(TemplateRecord updated, long expectedVersion, CancellationToken ct);
}

public interface ITaskHistoryStore
{
    Task AddAsync(TaskHistoryRecord entry, CancellationToken ct);
    /// <summary>All history of one task, oldest first (PLAN §5: append-only).</summary>
    Task<IReadOnlyList<TaskHistoryRecord>> ListByTaskAsync(string taskId, CancellationToken ct);
}

public interface IOperationReceiptStore
{
    Task<OperationReceiptRecord?> FindAsync(string profileId, string operationId, CancellationToken ct);
    Task AddAsync(OperationReceiptRecord receipt, CancellationToken ct);
}

public interface IBackupCatalogStore
{
    Task AddAsync(BackupCatalogRecord entry, CancellationToken ct);
    Task<BackupCatalogRecord?> FindByFileNameAsync(string fileName, CancellationToken ct);
}

/// <summary>
/// The application store owns transactions and read snapshots (PLAN §4 IApplicationStore /
/// IUnitOfWork). Writes run on a single serialized storage worker; changes are published
/// only after commit by the implementation.
/// </summary>
public interface IApplicationStore
{
    /// <summary>Runs one bounded mutation atomically. Throw inside to roll back everything.</summary>
    Task<TResult> ExecuteAsync<TResult>(Func<IDataSession, CancellationToken, Task<TResult>> operation, CancellationToken ct = default);

    /// <summary>Runs one bounded read. Never mutates.</summary>
    Task<TResult> QueryAsync<TResult>(Func<IDataSession, CancellationToken, Task<TResult>> query, CancellationToken ct = default);
}
