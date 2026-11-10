using System.Security.Cryptography;
using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tags;
using Chaos.Application.Tasks;
using Chaos.Domain.Tags;
using Chaos.Domain.Tasks;
using Chaos.Domain.Validation;

namespace Chaos.Application.Transfer;

/// <summary>
/// Previews and commits an import (PLAN §14). Everything is parsed and validated before any
/// write; the commit runs in one transaction, so any failure (including one on the last row)
/// leaves no partial import. New tasks get new IDs, except Chaos JSON merges, which keep a
/// task's ID when it is free and skip it as a conflict when it is not. Each source item has a
/// fingerprint, so importing the same file again skips what it already created. The caller
/// makes a validated backup first; restoring it is how a mistaken import is undone.
/// </summary>
public sealed class ImportCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public const int PreviewItemLimit = 200;

    /// <summary>Stable identity of one source item across imports.</summary>
    public static string ItemFingerprint(ImportSourceKind kind, string sourceKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{(int)kind}\u001F{sourceKey}"))).ToLowerInvariant();

    /// <summary>Field checks every task must pass before a commit is allowed (titles, notes, tags, estimates, zones).</summary>
    public static IReadOnlyList<ImportIssue> Validate(ImportDraft draft)
    {
        var errors = new List<ImportIssue>();
        void Check(int line, string field, Action check)
        {
            try
            {
                check();
            }
            catch (DomainValidationException error)
            {
                errors.Add(new ImportIssue(line, field, error.Message));
            }
            catch (AppException error)
            {
                errors.Add(new ImportIssue(line, field, error.Message));
            }
        }

        foreach (var task in draft.Tasks)
        {
            Check(task.Line, "title", () => TaskTitle.Normalize(task.Title));
            Check(task.Line, "notes", () => TaskNotes.Normalize(task.Notes));
            Check(task.Line, "estimate", () => EstimateMinutes.Normalize(task.EstimateMinutes));
            if (!Enum.IsDefined(task.Status) || !Enum.IsDefined(task.Priority) || !Enum.IsDefined(task.Category) || task.Subtasks.Any(subtask => !Enum.IsDefined(subtask.Status)))
            {
                errors.Add(new ImportIssue(task.Line, "status", "This task has a status, priority or category value Chaos does not know."));
            }

            if (task.ListName is not null)
            {
                Check(task.Line, "list", () => TaskTitle.Normalize(task.ListName));
            }

            foreach (var path in task.TagPaths)
            {
                Check(task.Line, "tags", () => TagName.Path(path));
            }

            foreach (var date in new[] { task.Planned, task.Due })
            {
                if (date?.ZoneId is { } zone)
                {
                    Check(task.Line, "zone", () => TaskFieldValidation.Zone(zone, "zone"));
                }
            }

            foreach (var subtask in task.Subtasks)
            {
                Check(task.Line, "subtasks", () => TaskTitle.Normalize(subtask.Title));
                Check(task.Line, "subtasks", () => TaskNotes.Normalize(subtask.Notes));
            }

            foreach (var entry in task.Diary)
            {
                Check(task.Line, "diary", () => TaskNotes.Normalize(entry.Markdown));
            }
        }

        return errors;
    }

    public async Task<ImportPreview> PreviewAsync(ImportDraft draft, CancellationToken ct = default)
    {
        var errors = draft.Errors.Concat(Validate(draft)).OrderBy(issue => issue.Line).ToList();
        return await store.QueryAsync(async (session, token) =>
        {
            var profile = await RequireProfileAsync(session, token);
            var space = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token)
                ?? throw new AppException(AppErrorKind.StorageUnavailable, "No personal space exists yet. Restart Chaos to complete first-run setup.");
            var containers = await session.Containers.ListBySpaceAsync(space.Id, token);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var items = new List<ImportPreviewItem>();
            int created = 0, duplicates = 0, conflicts = 0, subtasks = 0, diary = 0;
            foreach (var task in draft.Tasks)
            {
                var (outcome, detail) = await ClassifyAsync(session, draft.Kind, task, seen, token);
                switch (outcome)
                {
                    case ImportItemOutcome.New:
                        created++;
                        subtasks += task.Subtasks.Count;
                        diary += task.Diary.Count;
                        break;
                    case ImportItemOutcome.Duplicate: duplicates++; break;
                    default: conflicts++; break;
                }

                if (items.Count < PreviewItemLimit)
                {
                    items.Add(new ImportPreviewItem(task.Line, task.Title, task.ListName, task.Status, task.Subtasks.Count, outcome, detail));
                }
            }

            var lists = draft.Tasks
                .Where(task => task.ListName is not null)
                .GroupBy(task => task.ListName!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var match = MatchContainer(containers, group.First().PreservedListId, group.Key);
                    return new ImportListPlan(group.Key, group.Count(), match?.Id, match?.Name);
                })
                .ToList();

            return new ImportPreview(draft.Kind, draft.SourceName, draft.FileFingerprint, created, duplicates, conflicts, subtasks, diary, lists, items, errors, draft.Warnings);
        }, ct);
    }

    public async Task<ImportResult> CommitAsync(ImportDraft draft, ImportCommitOptions options, CancellationToken ct = default)
    {
        var errors = draft.Errors.Concat(Validate(draft)).ToList();
        if (errors.Count > 0)
        {
            var first = errors.OrderBy(issue => issue.Line).First();
            throw new AppException(AppErrorKind.Validation,
                $"Nothing was imported: {errors.Count} {(errors.Count == 1 ? "problem needs" : "problems need")} fixing first (for example, line {first.Line}: {first.Message}).",
                [.. errors.Take(50).Select(issue => new AppErrorField($"line[{issue.Line}].{issue.Field}", issue.Message))]);
        }

        return await store.ExecuteAsync(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            var profile = await RequireProfileAsync(session, token);
            var space = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token)
                ?? throw new AppException(AppErrorKind.StorageUnavailable, "No personal space exists yet. Restart Chaos to complete first-run setup.");
            var inbox = await session.Containers.GetInboxAsync(space.Id, token)
                ?? throw new AppException(AppErrorKind.StorageUnavailable, "No Inbox exists yet. Restart Chaos to complete first-run setup.");
            var containers = (await session.Containers.ListBySpaceAsync(space.Id, token)).ToList();

            var defaultContainer = inbox;
            if (options.DefaultContainerId is { } defaultId)
            {
                defaultContainer = UsableTarget(containers, defaultId, "defaultContainerId");
            }

            // Pass 1: decide every item before writing, so the batch row carries its counts.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var plan = new List<(ImportTask Task, ImportItemOutcome Outcome)>();
            foreach (var task in draft.Tasks)
            {
                plan.Add((task, (await ClassifyAsync(session, draft.Kind, task, seen, token)).Outcome));
            }

            var batchId = ids.NewId().ToString();
            var toCreate = plan.Where(item => item.Outcome == ImportItemOutcome.New).Select(item => item.Task).ToList();
            await session.Imports.AddBatchAsync(new ImportBatchRecord(batchId, profile.Id, draft.Kind, Truncate(draft.SourceName, 260), draft.FileFingerprint, now,
                toCreate.Count, plan.Count - toCreate.Count, options.BackupFileName), token);

            // Pass 2: write. Any failure throws and the store rolls everything back.
            var listCache = new Dictionary<string, ContainerRecord>(StringComparer.OrdinalIgnoreCase);
            var createdLists = new List<string>();
            var createdTasks = new List<string>();
            foreach (var item in toCreate)
            {
                var container = item.ListName is null
                    ? defaultContainer
                    : await ResolveListAsync(session, space, containers, listCache, createdLists, item, options, now, token);
                var task = await CreateTaskAsync(session, profile, container, item, batchId, now, token);
                await session.Imports.MapItemAsync(ItemFingerprint(draft.Kind, item.SourceKey), batchId, task.Id, Truncate(item.SourceKey, 500), token);
                createdTasks.Add(task.Id);
            }

            return new ImportResult(batchId, createdTasks.Count,
                plan.Count(item => item.Outcome == ImportItemOutcome.Duplicate),
                plan.Count(item => item.Outcome == ImportItemOutcome.Conflict),
                createdTasks, createdLists);
        }, ct);
    }

    public async Task<IReadOnlyList<ImportBatchRecord>> ListBatchesAsync(int limit, CancellationToken ct = default) =>
        await store.QueryAsync((session, token) => session.Imports.ListBatchesAsync(Math.Clamp(limit, 1, 100), token), ct);

    /// <summary>
    /// New, Duplicate (this item was imported before and its task still exists, appears twice in
    /// the file, or its stable ID is already a local task) or Conflict (a Chaos JSON task ID
    /// already belongs to a different local task, which is kept).
    /// </summary>
    private static async Task<(ImportItemOutcome Outcome, string? Detail)> ClassifyAsync(IDataSession session, ImportSourceKind kind, ImportTask task, HashSet<string> seen, CancellationToken token)
    {
        var fingerprint = ItemFingerprint(kind, task.SourceKey);
        if (!seen.Add(fingerprint))
        {
            return (ImportItemOutcome.Duplicate, "Appears more than once in this file.");
        }

        if (await session.Imports.FindTaskForFingerprintAsync(fingerprint, token) is { } previous
            && await session.Tasks.GetAsync(previous, token) is not null)
        {
            return (ImportItemOutcome.Duplicate, "Imported before; the earlier task still exists.");
        }

        if (task.PreservedId is { } preserved && await session.Tasks.GetIncludingDeletedAsync(preserved, token) is { } local)
        {
            return local.Title == task.Title && local.DeletedAtUtc is null
                ? (ImportItemOutcome.Duplicate, "Already in Chaos with the same ID.")
                : (ImportItemOutcome.Conflict, $"A local task already has this ID (\"{local.Title}\"{(local.DeletedAtUtc is null ? "" : ", deleted")}); the local task is kept.");
        }

        if (task.PreservedId is null && task.HasStableKey && Guid.TryParse(task.SourceKey, out var guid)
            && await session.Tasks.GetAsync(guid.ToString("D"), token) is not null)
        {
            return (ImportItemOutcome.Duplicate, "Already in Chaos (its ID matches a local task).");
        }

        return (ImportItemOutcome.New, null);
    }

    private static ContainerRecord? MatchContainer(IReadOnlyList<ContainerRecord> containers, string? preservedId, string name)
    {
        if (preservedId is not null && containers.FirstOrDefault(container => container.Id == preservedId && container.ArchivedAtUtc is null) is { } byId)
        {
            return byId;
        }

        var normalized = name.Trim().ToLowerInvariant();
        return containers.FirstOrDefault(container => container.ArchivedAtUtc is null && container.Name.Trim().ToLowerInvariant() == normalized);
    }

    private static ContainerRecord UsableTarget(IReadOnlyList<ContainerRecord> containers, string id, string field) =>
        containers.FirstOrDefault(container => container.Id == id) switch
        {
            null => throw AppException.Validation(field, "A chosen list no longer exists. Refresh the preview and choose again."),
            { ArchivedAtUtc: not null } => throw AppException.Validation(field, "A chosen list is archived. Choose an active list."),
            var container => container,
        };

    private async Task<ContainerRecord> ResolveListAsync(IDataSession session, SpaceRecord space, List<ContainerRecord> containers,
        Dictionary<string, ContainerRecord> cache, List<string> createdLists, ImportTask item, ImportCommitOptions options, DateTimeOffset now, CancellationToken token)
    {
        var name = TaskTitle.Normalize(item.ListName);
        if (cache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var resolved = options.ListTargets.TryGetValue(name, out var chosen) || options.ListTargets.TryGetValue(item.ListName!, out chosen)
            ? UsableTarget(containers, chosen, "listTargets")
            : MatchContainer(containers, item.PreservedListId, name);

        if (resolved is null)
        {
            // An archived list keeps its name reserved, so a new list gets a distinct one.
            bool Taken(string candidate) => containers.Any(container => container.Name.Trim().ToLowerInvariant() == candidate.ToLowerInvariant());
            var newName = name;
            for (var n = 1; Taken(newName); n++)
            {
                newName = n == 1 ? $"{name} (imported)" : $"{name} (imported {n})";
            }

            var id = item.PreservedListId is { } preserved && await session.Containers.GetIncludingDeletedAsync(preserved, token) is null
                ? preserved
                : ids.NewId().ToString();
            resolved = new ContainerRecord
            {
                Id = id,
                SpaceId = space.Id,
                Kind = ContainerKind.List,
                Name = newName,
                Position = await session.Containers.GetNextPositionAsync(space.Id, token),
                Version = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            await session.Containers.AddAsync(resolved, token);
            containers.Add(resolved);
            createdLists.Add(resolved.Name);
        }

        cache[name] = resolved;
        return resolved;
    }

    private async Task<TaskRecord> CreateTaskAsync(IDataSession session, ProfileRecord profile, ContainerRecord container, ImportTask item, string batchId, DateTimeOffset now, CancellationToken token)
    {
        var closedAt = item.ClosedAtUtc is { } closed && closed <= now ? closed : now;
        string? Zone(ImportDate? date) => date?.Time is null ? null : date.ZoneId ?? profile.ZoneId;
        var task = new TaskRecord
        {
            Id = item.PreservedId ?? ids.NewId().ToString(),
            SpaceId = container.SpaceId,
            ContainerId = container.Id,
            Title = TaskTitle.Normalize(item.Title),
            Notes = TaskNotes.Normalize(item.Notes),
            Status = item.Status,
            Priority = item.Priority,
            EstimateMinutes = EstimateMinutes.Normalize(item.EstimateMinutes),
            CapacityCategory = item.Category,
            PlannedDate = item.Planned?.Date,
            PlannedTime = item.Planned?.Time,
            PlannedZoneId = Zone(item.Planned),
            DueDate = item.Due?.Date,
            DueTime = item.Due?.Time,
            DueZoneId = Zone(item.Due),
            CompletedAtUtc = item.Status == TaskItemStatus.Done ? closedAt : null,
            CancelledAtUtc = item.Status == TaskItemStatus.Cancelled ? closedAt : null,
            Position = await session.Tasks.GetNextPositionAsync(container.Id, token),
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        await session.Tasks.AddAsync(task, token);

        var subtasks = new List<SubtaskRecord>(item.Subtasks.Count);
        for (var index = 0; index < item.Subtasks.Count; index++)
        {
            var source = item.Subtasks[index];
            var subtask = new SubtaskRecord
            {
                Id = source.PreservedId is { } preserved && await session.Subtasks.GetIncludingDeletedAsync(preserved, token) is null ? preserved : ids.NewId().ToString(),
                TaskId = task.Id,
                Title = TaskTitle.Normalize(source.Title),
                Notes = TaskNotes.Normalize(source.Notes),
                Status = source.Status,
                CompletedAtUtc = source.Status == TaskItemStatus.Done ? now : null,
                CancelledAtUtc = source.Status == TaskItemStatus.Cancelled ? now : null,
                Position = (index + 1) * RankOrdering.Gap,
                Version = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            await session.Subtasks.AddAsync(subtask, token);
            subtasks.Add(subtask);
        }

        var tagIds = new List<string>();
        foreach (var path in item.TagPaths)
        {
            var (leaf, _) = await TagCommands.EnsurePathAsync(session, ids, profile.Id, null, TagName.Path(path), now, token);
            if (await session.Tags.AddTaskLinkAsync(task.Id, leaf.Id, token))
            {
                tagIds.Add(leaf.Id);
            }
        }

        foreach (var entry in item.Diary)
        {
            await session.Diary.AddAsync(new DiaryEntryRecord
            {
                Id = ids.NewId().ToString(),
                TaskId = task.Id,
                AuthorProfileId = profile.Id,
                EntryAtUtc = entry.EntryAtUtc,
                RawMarkdown = TaskNotes.Normalize(entry.Markdown),
                Revision = 1,
                Version = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            }, token);
        }

        await TaskMutationSupport.WriteTaskHistoryAsync(session, ids, batchId, before: null,
            after: new TaskAggregateSnapshot(task, subtasks, tagIds), eventKind: "task.imported",
            changedFields: ["Title", "Notes", "ContainerId", "Imported"], now, token);
        return task;
    }

    private static async Task<ProfileRecord> RequireProfileAsync(IDataSession session, CancellationToken token) =>
        await session.Profiles.GetPrimaryAsync(token)
        ?? throw new AppException(AppErrorKind.StorageUnavailable, "No local profile exists yet. Restart Chaos to complete first-run setup.");

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
