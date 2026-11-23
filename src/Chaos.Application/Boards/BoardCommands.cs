using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Sync;

namespace Chaos.Application.Boards;

public sealed record UpdateBoardSettingsCommand(
    string OperationId,
    BoardTargetKind TargetKind,
    string TargetId,
    long ExpectedVersion,
    BoardGrouping GroupBy,
    BoardSwimlane SwimlaneBy,
    string? ColumnTagId,
    string? SwimlaneTagId,
    string WipLimitsJson,
    string HiddenColumnsJson);

public sealed record BoardSettingsResult(BoardSettingsRecord Settings, bool Replayed);

/// <summary>
/// Moving one card on a board (PLAN2 §11.1): the field change the target column asks for plus
/// the reorder, as ONE operation — one receipt, one undo entry. Column values are
/// status/priority names, section or tag IDs; null means the "No section"/"None" column.
/// The card lands just before <c>BeforeTaskId</c> or just after <c>AfterTaskId</c> (at most
/// one is set): the neighbouring card the person dropped it next to, so the position stays
/// right whatever filter, swimlane or hidden column the board showed. Neither keeps the
/// card's current place in the container's order.
/// </summary>
public sealed record MoveBoardCardCommand(
    string OperationId,
    string TaskId,
    long ExpectedVersion,
    BoardGrouping GroupBy,
    string? FromColumn,
    string? ToColumn,
    string? BeforeTaskId,
    string? AfterTaskId,
    OpenSubtaskChoice? OpenSubtaskChoice = null);

public sealed record BoardMoveResult(TaskRecord Task, bool Replayed);

/// <summary>Board settings writes and the composed card move (PLAN2 P2).</summary>
public sealed class BoardCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public const int MaxWipLimit = 9999;

    /// <summary>The stored settings row, or the defaults when the target never changed one.</summary>
    public async Task<BoardSettingsRecord> GetSettingsAsync(BoardTargetKind targetKind, string targetId, CancellationToken ct = default)
    {
        ValidateTarget(targetKind, targetId);
        return await store.QueryAsync(async (session, token) =>
            await session.Boards.GetAsync(targetKind, targetId, token)
                ?? Defaults(targetKind, targetId), ct);
    }

    public async Task<BoardSettingsResult> UpdateSettingsAsync(UpdateBoardSettingsCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = Normalize(command);

        return await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, normalized.OperationId, "board.update-settings", normalized, now, async inner =>
                {
                    var existing = await session.Boards.GetAsync(normalized.TargetKind, normalized.TargetId, inner);
                    BoardSettingsRecord saved;
                    if (existing is null)
                    {
                        if (normalized.ExpectedVersion != 0)
                        {
                            throw AppException.VersionConflict("This board's settings changed elsewhere. Refresh and try again.");
                        }

                        saved = new BoardSettingsRecord
                        {
                            Id = StorageFormats.NameBasedId(NameBasedId.Names.BoardSetting(
                                ((int)normalized.TargetKind).ToString(System.Globalization.CultureInfo.InvariantCulture), normalized.TargetId)),
                            TargetKind = normalized.TargetKind,
                            TargetId = normalized.TargetId,
                            GroupBy = normalized.GroupBy,
                            SwimlaneBy = normalized.SwimlaneBy,
                            ColumnTagId = normalized.ColumnTagId,
                            SwimlaneTagId = normalized.SwimlaneTagId,
                            WipLimitsJson = normalized.WipLimitsJson,
                            HiddenColumnsJson = normalized.HiddenColumnsJson,
                            Version = 1,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                        };
                        await session.Boards.AddAsync(saved, inner);
                    }
                    else
                    {
                        var updated = existing with
                        {
                            GroupBy = normalized.GroupBy,
                            SwimlaneBy = normalized.SwimlaneBy,
                            ColumnTagId = normalized.ColumnTagId,
                            SwimlaneTagId = normalized.SwimlaneTagId,
                            WipLimitsJson = normalized.WipLimitsJson,
                            HiddenColumnsJson = normalized.HiddenColumnsJson,
                            Version = existing.Version + 1,
                            UpdatedAtUtc = now,
                        };
                        saved = await session.Boards.UpdateAsync(updated, normalized.ExpectedVersion, inner)
                            ?? throw AppException.VersionConflict("This board's settings changed elsewhere. Refresh and try again.");
                    }

                    return new BoardSettingsResult(saved, Replayed: false);
                }, token);

            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    public async Task<BoardMoveResult> MoveAsync(MoveBoardCardCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(command.GroupBy))
        {
            throw AppException.Validation("groupBy", "Choose how the board groups its columns.");
        }

        var normalized = command with
        {
            FromColumn = Blank(command.FromColumn),
            ToColumn = Blank(command.ToColumn),
            BeforeTaskId = Blank(command.BeforeTaskId),
            AfterTaskId = Blank(command.AfterTaskId),
        };
        if (normalized.BeforeTaskId is not null && normalized.AfterTaskId is not null)
        {
            throw AppException.Validation("beforeTaskId", "Place the card either before or after one other card.");
        }

        if (normalized.BeforeTaskId == normalized.TaskId || normalized.AfterTaskId == normalized.TaskId)
        {
            throw AppException.Validation("beforeTaskId", "A card cannot be placed next to itself.");
        }

        return await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, normalized.OperationId, "board.move", normalized, now, async inner =>
                {
                    var task = await session.Tasks.GetAsync(normalized.TaskId, inner)
                        ?? throw AppException.NotFound("That task no longer exists. Refresh and try again.");
                    TaskCommands.RequireCurrentVersion(task, normalized.ExpectedVersion);

                    var subtasks = await session.Subtasks.ListByTaskAsync(task.Id, inner);
                    var before = new TaskAggregateSnapshot(task, subtasks);
                    var change = normalized.GroupBy switch
                    {
                        BoardGrouping.Status => await MoveToStatusColumnAsync(session, ids, task, normalized, subtasks, now, inner),
                        BoardGrouping.Section => await MoveToSectionColumnAsync(session, task, normalized.ToColumn, now, inner),
                        BoardGrouping.Priority => await MoveToPriorityColumnAsync(session, task, normalized.ToColumn, now, inner),
                        BoardGrouping.TagGroup => await MoveToTagColumnAsync(session, task, normalized.ToColumn, now, inner),
                        BoardGrouping.Assignee => await MoveToAssigneeColumnAsync(session, ids, task, profile.Id, normalized.FromColumn, normalized.ToColumn, now, inner),
                        _ => throw AppException.Validation("groupBy", "Choose how the board groups its columns."),
                    };
                    var current = change.Task;
                    var eventKind = change.EventKind ?? "task.updated";
                    var fields = change.Field is { } field ? new List<string> { field } : [];

                    if ((normalized.BeforeTaskId ?? normalized.AfterTaskId) is { } anchorId)
                    {
                        (current, var reordered) = await ReorderAsync(session, current, anchorId, placeAfter: normalized.AfterTaskId is not null, now, inner);
                        if (reordered)
                        {
                            fields.Add("Position");
                        }
                    }

                    if (fields.Count == 0)
                    {
                        return new BoardMoveResult(current, Replayed: false); // already there
                    }

                    current = await session.Tasks.GetAsync(task.Id, inner) ?? current;
                    await TaskMutationSupport.WriteTaskHistoryAsync(
                        session, ids, normalized.OperationId, before,
                        new TaskAggregateSnapshot(current, await session.Subtasks.ListByTaskAsync(task.Id, inner)),
                        eventKind, fields, now, inner);

                    return new BoardMoveResult(current, Replayed: false);
                }, token);

            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>Default settings for a target that never changed any (Status columns, no lanes).</summary>
    public static BoardSettingsRecord Defaults(BoardTargetKind targetKind, string targetId) => new()
    {
        Id = string.Empty,
        TargetKind = targetKind,
        TargetId = targetId,
        GroupBy = BoardGrouping.Status,
        SwimlaneBy = BoardSwimlane.None,
        WipLimitsJson = "{}",
        HiddenColumnsJson = "[]",
        Version = 0,
        CreatedAtUtc = DateTimeOffset.MinValue,
        UpdatedAtUtc = DateTimeOffset.MinValue,
    };

    internal static UpdateBoardSettingsCommand Normalize(UpdateBoardSettingsCommand command)
    {
        ValidateTarget(command.TargetKind, command.TargetId);
        if (!Enum.IsDefined(command.GroupBy))
        {
            throw AppException.Validation("groupBy", "Choose how the board groups its columns.");
        }

        if (!Enum.IsDefined(command.SwimlaneBy))
        {
            throw AppException.Validation("swimlaneBy", "Choose how the board groups its swimlanes.");
        }

        if (command.TargetKind == BoardTargetKind.View && command.GroupBy == BoardGrouping.Section)
        {
            throw AppException.Validation("groupBy", "A view can group its board by status, priority or a tag group. Sections belong to one list or project.");
        }

        var columnTag = Blank(command.ColumnTagId);
        var laneTag = Blank(command.SwimlaneTagId);
        if (command.GroupBy == BoardGrouping.TagGroup && columnTag is null)
        {
            throw AppException.Validation("columnTagId", "Choose the parent tag whose children are the columns.");
        }

        if (command.SwimlaneBy == BoardSwimlane.TagGroup && laneTag is null)
        {
            throw AppException.Validation("swimlaneTagId", "Choose the parent tag whose children are the swimlanes.");
        }

        return command with
        {
            ColumnTagId = columnTag,
            SwimlaneTagId = laneTag,
            WipLimitsJson = NormalizeWipLimits(command.WipLimitsJson),
            HiddenColumnsJson = NormalizeHiddenColumns(command.HiddenColumnsJson),
        };
    }

    /// <summary>A JSON object of at most 50 column keys to WIP limits from 1 to <see cref="MaxWipLimit"/>.</summary>
    public static string NormalizeWipLimits(string json)
    {
        using var document = ParseJson(json, "wipLimits", "{}");
        var limits = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (limits.Count >= 50)
            {
                throw AppException.Validation("wipLimits", "A board can hold at most 50 column limits.");
            }

            if (entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.TryGetInt32(out var limit) || limit is < 1 or > MaxWipLimit)
            {
                throw AppException.Validation("wipLimits", $"The limit for “{entry.Name}” must be a whole number from 1 to {MaxWipLimit}.");
            }

            limits[LimitKey(entry.Name)] = limit;
        }

        return JsonSerializer.Serialize(limits);
    }

    /// <summary>A JSON array of at most 64 column keys, each at most 100 characters.</summary>
    public static string NormalizeHiddenColumns(string json)
    {
        using var document = ParseJson(json, "hiddenColumns", "[]");
        var hidden = new List<string>();
        foreach (var value in document.RootElement.EnumerateArray())
        {
            if (hidden.Count >= 64)
            {
                throw AppException.Validation("hiddenColumns", "A board can hide at most 64 columns.");
            }

            if (value.ValueKind != JsonValueKind.String || LimitKey(value.GetString()!) is { Length: > 100 } key)
            {
                throw AppException.Validation("hiddenColumns", "Hidden column names must be plain text.");
            }

            hidden.Add(LimitKey(value.GetString()!));
        }

        return JsonSerializer.Serialize(hidden.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
    }

    private static string LimitKey(string key)
    {
        var trimmed = key.Trim();
        return trimmed.Length > 100 ? trimmed[..100] : trimmed;
    }

    private static JsonDocument ParseJson(string json, string field, string fallback)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? fallback : json);
        }
        catch (JsonException)
        {
            throw AppException.Validation(field, "Enter the value as valid JSON.");
        }
    }

    /// <summary>
    /// The board reorder (PLAN2 §11.1): the moving card lands just before or just after the
    /// anchor card, through the ordinary rank rules over the container's tasks. The anchor is
    /// the card the person saw beside the drop, so cards the board did not show (another
    /// focus, a hidden column) never shift where it lands.
    /// </summary>
    /// <returns>The moved task and whether any rank changed.</returns>
    private static async Task<(TaskRecord Task, bool Changed)> ReorderAsync(
        IDataSession session,
        TaskRecord current,
        string anchorId,
        bool placeAfter,
        DateTimeOffset now,
        CancellationToken token)
    {
        var ordered = await session.Tasks.ListByContainerAsync(current.ContainerId, token);
        var others = ordered.Where(row => row.Id != current.Id).ToList();
        var anchorAt = others.FindIndex(row => row.Id == anchorId);
        if (anchorAt < 0)
        {
            throw AppException.NotFound("The card it was placed next to is no longer in this list or project. Refresh and try again.");
        }

        var assignments = RankOrdering.Place(
            [.. ordered.Select(row => (row.Id, row.Position, row.Version))],
            current.Id,
            placeAfter ? anchorAt + 1 : anchorAt);
        TaskRecord moved = current;
        foreach (var assignment in assignments)
        {
            var row = ordered.Single(candidate => candidate.Id == assignment.Id);
            var updated = row with { Position = assignment.NewPosition, Version = row.Version + 1, UpdatedAtUtc = now };
            var applied = await TaskCommands.ApplyTaskUpdateAsync(session, updated, row.Version, token);
            if (applied.Id == current.Id)
            {
                moved = applied;
            }
        }

        return (moved, assignments.Count > 0);
    }

    /// <summary>What a column change did: the task after it, and its history kind and field (null when nothing changed).</summary>
    private readonly record struct ColumnChange(TaskRecord Task, string? EventKind, string? Field)
    {
        public static ColumnChange None(TaskRecord task) => new(task, null, null);
    }

    private static async Task<ColumnChange> MoveToStatusColumnAsync(
        IDataSession session, IIdGenerator ids, TaskRecord task, MoveBoardCardCommand command,
        IReadOnlyList<SubtaskRecord> subtasks, DateTimeOffset now, CancellationToken token)
    {
        if (command.ToColumn is { } column && column.StartsWith("state:", StringComparison.Ordinal))
        {
            var state = (await session.Spaces.ListStatesAsync(task.SpaceId, token))
                .FirstOrDefault(row => row.Id == column[6..])
                ?? throw AppException.Validation("toColumn", "Choose a state from this task's space.");
            if (task.CustomStateId == state.Id && task.Status == state.BaseStatus) return ColumnChange.None(task);
            var custom = await ApplyAsync(session, now, task with { Status = state.BaseStatus, CustomStateId = state.Id,
                CompletedAtUtc = null, CancelledAtUtc = null }, token);
            return new(custom, "task.updated", "Status");
        }
        var status = ParseColumn<TaskItemStatus>(command.ToColumn, "toColumn", "Choose a status column.");
        if (status == task.Status && task.CustomStateId is null)
        {
            return ColumnChange.None(task);
        }

        // The card lands in the column it was dropped on, not the task's previous open status.
        var applied = await TaskCommands.ApplyStatusAsync(
            session, ids, task, status, command.OpenSubtaskChoice, now, subtasks, token, restorePreviousStatus: false);
        return new(applied, TaskCommands.EventKindForStatusChange(task.Status, applied.Status), "Status");
    }

    private static async Task<ColumnChange> MoveToSectionColumnAsync(
        IDataSession session, TaskRecord task, string? sectionId, DateTimeOffset now, CancellationToken token)
    {
        if (sectionId is not null)
        {
            var section = await session.Sections.GetAsync(sectionId, token)
                ?? throw AppException.NotFound("That section no longer exists. Refresh and try again.");
            if (section.ContainerId != task.ContainerId)
            {
                throw AppException.Validation("toColumn", "That section belongs to a different list or project.");
            }
        }

        return sectionId == task.SectionId
            ? ColumnChange.None(task)
            : new(await ApplyAsync(session, now, task with { SectionId = sectionId }, token), "task.moved", "SectionId");
    }

    private static async Task<ColumnChange> MoveToPriorityColumnAsync(
        IDataSession session, TaskRecord task, string? column, DateTimeOffset now, CancellationToken token)
    {
        var priority = ParseColumn<TaskPriority>(column, "toColumn", "Choose a priority column.");
        return priority == task.Priority
            ? ColumnChange.None(task)
            : new(await ApplyAsync(session, now, task with { Priority = priority }, token), null, "Priority");
    }

    private static async Task<ColumnChange> MoveToAssigneeColumnAsync(IDataSession session, IIdGenerator ids, TaskRecord task, string profileId,
        string? fromMember, string? toMember, DateTimeOffset now, CancellationToken token)
    {
        if (fromMember == toMember) return ColumnChange.None(task);
        if ((await session.Spaces.GetAsync(task.SpaceId, token))?.Kind != SpaceKind.Shared)
            throw AppException.Validation("toColumn", "Assignments are available in shared spaces.");
        var people = await session.People.ListAsync(task.SpaceId, token);
        if (!people.Any(person => person.ActorId == "person:" + profileId && !person.Former && person.Role is 1 or 2))
            throw AppException.Validation("toColumn", "Only owners and editors can change assignments.");
        if (toMember is not null && !people.Any(person => person.ActorId == "person:" + toMember && !person.Former && person.Role is 1 or 2))
            throw AppException.Validation("toColumn", "Choose a current owner or editor.");
        var rows = await session.Tasks.ListAssigneesAsync(task.Id, token);
        if (fromMember is null && rows.Any(row => row.DeletedAtUtc is null))
            throw AppException.VersionConflict("This assignment changed elsewhere. Refresh the board and try again.");
        var source = rows.FirstOrDefault(row => row.MemberId == fromMember && row.DeletedAtUtc is null);
        if (fromMember is not null && source is null)
            throw AppException.VersionConflict("This assignment changed elsewhere. Refresh the board and try again.");
        if (source is not null) await session.Tasks.SaveAssigneeAsync(source with { DeletedAtUtc = now }, token);
        if (toMember is not null && !rows.Any(row => row.MemberId == toMember && row.DeletedAtUtc is null))
            await session.Tasks.SaveAssigneeAsync(new TaskAssigneeRecord(task.Id, toMember, now, null, null), token);
        var updated = await ApplyAsync(session, now, task, token);
        updated = await TaskCommands.CompleteEachIfReadyAsync(session, ids, updated, now, token);
        return new(updated, "task.updated", "Assignees");
    }

    /// <summary>
    /// A tag column is a child of the board's stored column tag. The task ends with exactly the
    /// target child (or none, for the "None" column): every other child link is removed, so a
    /// retag elsewhere since the board was read (tag links carry no task version) cannot leave
    /// the card in two columns. The client's FromColumn is not trusted for this.
    /// </summary>
    private static async Task<ColumnChange> MoveToTagColumnAsync(
        IDataSession session, TaskRecord task, string? toTag, DateTimeOffset now, CancellationToken token)
    {
        var settings = await session.Boards.GetAsync(BoardTargetKind.Container, task.ContainerId, token);
        if (settings is not { GroupBy: BoardGrouping.TagGroup, ColumnTagId: { } parentId })
        {
            throw AppException.VersionConflict("This board's settings changed elsewhere. Refresh and try again.");
        }

        var columnTagIds = (await session.Tags.ListChildrenAsync(parentId, token)).Select(tag => tag.Id).ToHashSet(StringComparer.Ordinal);
        if (toTag is not null && !columnTagIds.Contains(toTag))
        {
            throw AppException.NotFound("That column's tag no longer exists. Refresh and try again.");
        }

        var changed = false;
        foreach (var tagId in await session.Tags.ListTaskTagIdsAsync(task.Id, token))
        {
            if (columnTagIds.Contains(tagId) && tagId != toTag)
            {
                changed |= await session.Tags.RemoveTaskLinkAsync(task.Id, tagId, now, token);
            }
        }

        if (toTag is not null)
        {
            changed |= await session.Tags.AddTaskLinkAsync(task.Id, toTag, token);
        }

        return changed ? new(task, "task.tags-changed", "Tags") : ColumnChange.None(task);
    }

    private static Task<TaskRecord> ApplyAsync(IDataSession session, DateTimeOffset now, TaskRecord updated, CancellationToken token) =>
        TaskCommands.ApplyTaskUpdateAsync(session, updated with { Version = updated.Version + 1, UpdatedAtUtc = now }, updated.Version, token);

    private static T ParseColumn<T>(string? value, string field, string message) where T : struct, Enum =>
        value is null || !Enum.TryParse<T>(value, out var parsed) || !Enum.IsDefined(parsed)
            ? throw AppException.Validation(field, message)
            : parsed;

    private static void ValidateTarget(BoardTargetKind targetKind, string targetId)
    {
        if (!Enum.IsDefined(targetKind))
        {
            throw AppException.Validation("targetKind", "Choose what the board belongs to.");
        }

        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw AppException.Validation("targetId", "Choose the board's list, project or view.");
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
