using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Chaos.Domain.Validation;

namespace Chaos.Application.Tasks;

public sealed record CaptureTaskCommand(string OperationId, string ContainerId, string Title, string? Notes);

/// <summary>Stored receipt results and history snapshots carry a version so later app versions can recognize what they read.</summary>
public sealed record VersionedPayload<T>(int V, T Payload)
{
    public const int CurrentVersion = 1;
}

public sealed record CaptureTaskResult(TaskRecord Task, string OperationId, bool Replayed);

/// <summary>
/// Single-task create/get (PLAN §15 WP1). Each capture carries a client OperationId: the
/// same operation replayed with the same normalized payload returns the original result;
/// the same OperationId with different content is an operation conflict. Task row, history
/// snapshot and receipt commit in one transaction; the empty ContainerId targets the Inbox.
/// </summary>
public sealed class TaskCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<CaptureTaskResult> CaptureAsync(CaptureTaskCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = Normalize(command);
        var payloadHash = CanonicalHash(normalized);

        return await store.ExecuteAsync(async (session, token) =>
        {
            var profile = await RequirePrimaryProfile(session, token);

            var receipt = await session.Receipts.FindAsync(profile.Id, command.OperationId, token);
            if (receipt is not null)
            {
                if (!string.Equals(receipt.PayloadHash, payloadHash, StringComparison.Ordinal))
                {
                    throw AppException.OperationConflict(
                        "This operation was already used with different content. Retry the same change; for new content use a new operation.");
                }

                var stored = JsonSerializer.Deserialize<VersionedPayload<TaskRecord>>(receipt.ResultJson, StoredJson)
                    ?? throw new AppException(
                        AppErrorKind.StorageUnavailable,
                        "The stored result of this operation could not be read. Look for the task in your lists.");
                return new CaptureTaskResult(stored.Payload, command.OperationId, Replayed: true);
            }

            var container = await ResolveContainerAsync(session, normalized.ContainerId, token);
            var now = clock.GetUtcNow();
            var position = await session.Tasks.GetNextPositionAsync(container.Id, token);

            var task = new TaskRecord
            {
                Id = ids.NewId().ToString(),
                SpaceId = container.SpaceId,
                ContainerId = container.Id,
                Title = normalized.Title,
                Notes = normalized.Notes ?? string.Empty,
                Status = TaskItemStatus.NotStarted,
                Priority = TaskPriority.Normal,
                Position = position,
                Version = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            await session.Tasks.AddAsync(task, token);

            var snapshot = JsonSerializer.Serialize(
                new VersionedPayload<TaskRecord>(VersionedPayload<TaskRecord>.CurrentVersion, task), StoredJson);
            await session.History.AddAsync(new TaskHistoryRecord
            {
                Id = ids.NewId().ToString(),
                TaskId = task.Id,
                OperationId = command.OperationId,
                EffectiveAtUtc = now,
                Actor = "local",
                EventKind = "task.created",
                ChangedFields = ["Title", "Notes", "ContainerId"],
                SnapshotAfter = snapshot,
                AggregateVersion = task.Version,
                RecordedAtUtc = now,
            }, token);

            await session.Receipts.AddAsync(new OperationReceiptRecord
            {
                ProfileId = profile.Id,
                OperationId = command.OperationId,
                CommandKind = "task.capture",
                PayloadHash = payloadHash,
                ResultJson = snapshot,
                CommittedAtUtc = now,
            }, token);

            return new CaptureTaskResult(task, command.OperationId, Replayed: false);
        }, ct);
    }

    public async Task<TaskRecord?> GetAsync(string taskId, CancellationToken ct = default) =>
        await store.QueryAsync((session, token) => session.Tasks.GetAsync(taskId, token), ct);

    /// <summary>Trims and limit-checks the title; the payload hash covers the normalized text, so whitespace-only retries replay.</summary>
    internal static CaptureTaskCommand Normalize(CaptureTaskCommand command)
    {
        string title;
        try
        {
            title = TaskTitle.Normalize(command.Title);
        }
        catch (DomainValidationException error)
        {
            throw AppException.Validation("title", error.Message);
        }

        return command with { Title = title };
    }

    private static async Task<ProfileRecord> RequirePrimaryProfile(IDataSession session, CancellationToken token) =>
        await session.Profiles.GetPrimaryAsync(token)
        ?? throw new AppException(
            AppErrorKind.StorageUnavailable,
            "No local profile exists yet. Restart Chaos to complete first-run setup.");

    private static async Task<ContainerRecord> ResolveContainerAsync(IDataSession session, string? containerId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            var profile = await RequirePrimaryProfile(session, token);
            var space = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token)
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

    /// <summary>SHA-256 lowercase hex over canonical camelCase JSON of the normalized command.</summary>
    internal static string CanonicalHash(CaptureTaskCommand normalizedCommand)
    {
        var canonical = JsonSerializer.Serialize(normalizedCommand, StoredJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
