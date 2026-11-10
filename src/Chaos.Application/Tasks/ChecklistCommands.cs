using System.Security.Cryptography;
using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Tasks;

/// <summary>
/// One checklist item the user chose to convert. <see cref="ItemKey"/> identifies the item in
/// its document (the editor sends the item text plus its occurrence number), so converting
/// the same checklist again recognizes items it already turned into subtasks.
/// </summary>
public sealed record ChecklistItem(string ItemKey, string Title, bool Done);

public sealed record ConvertChecklistCommand(string OperationId, string TaskId, IReadOnlyList<ChecklistItem> Items);

public sealed record ChecklistConversionResult(IReadOnlyList<SubtaskRecord> Created, int AlreadyConverted, bool Replayed);

/// <summary>
/// Markdown checklist to subtasks (PLAN §9). Items become subtasks in document order with
/// their checked state as Done; the source text is left unchanged. A marker per item makes a
/// second conversion create nothing for items whose subtask still exists. The whole
/// conversion is one operation, so one undo removes every subtask it made.
/// </summary>
public sealed class ChecklistCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public const int MaxItems = 500;

    public async Task<ChecklistConversionResult> ConvertAsync(ConvertChecklistCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (command.Items.Count == 0)
        {
            throw AppException.Validation("items", "Choose at least one checklist item to convert.");
        }

        if (command.Items.Count > MaxItems)
        {
            throw AppException.Validation("items", $"Convert at most {MaxItems} checklist items at once.");
        }

        var normalized = command with
        {
            Items = [.. command.Items.Select((item, index) => item with
            {
                ItemKey = string.IsNullOrEmpty(item.ItemKey) ? throw AppException.Validation($"items[{index}].itemKey", "Each checklist item needs its key.") : item.ItemKey,
                Title = TaskFieldValidation.Title(item.Title, $"items[{index}].title"),
            })],
        };

        return await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, normalized.OperationId, "subtask.convert-checklist", normalized, now, async inner =>
            {
                var task = await session.Tasks.GetAsync(normalized.TaskId, inner)
                    ?? throw AppException.NotFound("That task no longer exists. Refresh and try again.");
                var created = new List<SubtaskRecord>();
                var skipped = 0;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in normalized.Items)
                {
                    var key = Key(task.Id, item.ItemKey);
                    if (!seen.Add(key))
                    {
                        skipped++;
                        continue; // the same item twice in one request
                    }

                    var marker = await session.ChecklistConversions.FindAsync(task.Id, key, inner);
                    if (marker is not null && await session.Subtasks.GetAsync(marker.SubtaskId, inner) is not null)
                    {
                        skipped++;
                        continue;
                    }

                    var subtask = new SubtaskRecord
                    {
                        Id = ids.NewId().ToString(),
                        TaskId = task.Id,
                        Title = item.Title,
                        Status = item.Done ? TaskItemStatus.Done : TaskItemStatus.NotStarted,
                        CompletedAtUtc = item.Done ? now : null,
                        Position = await session.Subtasks.GetNextPositionAsync(task.Id, inner),
                        Version = 1,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    };
                    await session.Subtasks.AddAsync(subtask, inner);
                    await session.ChecklistConversions.UpsertAsync(new ChecklistConversionRecord(task.Id, key, subtask.Id, normalized.OperationId, now), inner);
                    created.Add(subtask);
                }

                if (created.Count > 0)
                {
                    await TaskMutationSupport.WriteTaskHistoryAsync(session, ids, normalized.OperationId, null, new TaskAggregateSnapshot(task, created), "subtask.checklist-converted", ["Subtasks"], now, inner);
                }

                return new ChecklistConversionResult(created, skipped, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>64 hex characters over the task and the editor's item key.</summary>
    internal static string Key(string taskId, string itemKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{taskId}\n{itemKey}"))).ToLowerInvariant();
}
