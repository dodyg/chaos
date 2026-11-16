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

public sealed record BoardMoveResult(TaskRecord Task, bool Replayed, string? Warning);

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
                            Id = StorageFormats.Id(NameBasedId.Create(NameBasedId.Names.BoardSetting(
                                ((int)normalized.TargetKind).ToString(System.Globalization.CultureInfo.InvariantCulture), normalized.TargetId))),
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
                    if (task.Version != normalized.ExpectedVersion)
                    {
                        throw AppException.VersionConflict("This task changed elsewhere while you were working. Refresh and try again.");
                    }

                    var before = new TaskAggregateSnapshot(task, await session.Subtasks.ListByTaskAsync(task.Id, inner));
                    var current = task;
                    string eventKind = "task.updated";
                    var fields = new List<string>();

                    switch (normalized.GroupBy)
                    {
                        case BoardGrouping.Status:
                        {
                            var status = ParseColumn<TaskItemStatus>(normalized.ToColumn, "toColumn", "Choose a status column.");
                            if (status != task.Status)
                            {
                                current = await TaskCommands.ApplyStatusAsync(
                                    session, ids, task, status, normalized.OpenSubtaskChoice, now,
                                    await session.Subtasks.ListByTaskAsync(task.Id, inner), inner);
                                eventKind = EventKindForStatusChange(task.Status, current.Status);
                                fields.Add("Status");
                            }

                            break;
                        }

                        case BoardGrouping.Section:
                        {
                            SectionRecord? section = null;
                            if (normalized.ToColumn is { } sectionId)
                            {
                                section = await session.Sections.GetAsync(sectionId, inner)
                                    ?? throw AppException.NotFound("That section no longer exists. Refresh and try again.");
                                if (section.ContainerId != task.ContainerId)
                                {
                                    throw AppException.Validation("toColumn", "That section belongs to a different list or project.");
                                }
                            }

                            if (section?.Id != task.SectionId)
                            {
                                current = await ApplyAsync(session, now, current with { SectionId = section?.Id }, inner);
                                eventKind = "task.moved";
                                fields.Add("SectionId");
                            }

                            break;
                        }

                        case BoardGrouping.Priority:
                        {
                            var priority = ParseColumn<TaskPriority>(normalized.ToColumn, "toColumn", "Choose a priority column.");
                            if (priority != task.Priority)
                            {
                                current = await ApplyAsync(session, now, current with { Priority = priority }, inner);
                                fields.Add("Priority");
                            }

                            break;
                        }

                        case BoardGrouping.TagGroup:
                        {
                            if (normalized.FromColumn != normalized.ToColumn)
                            {
                                if (normalized.FromColumn is { } fromTag)
                                {
                                    await session.Tags.RemoveTaskLinkAsync(task.Id, fromTag, now, inner);
                                }

                                if (normalized.ToColumn is { } toTag)
                                {
                                    _ = await session.Tags.GetAsync(toTag, inner)
                                        ?? throw AppException.NotFound("That column's tag no longer exists. Refresh and try again.");
                                    await session.Tags.AddTaskLinkAsync(task.Id, toTag, inner);
                                }

                                eventKind = "task.tags-changed";
                                fields.Add("Tags");
                            }

                            break;
                        }
                    }

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
                        return new BoardMoveResult(current, Replayed: false, Warning: null); // already there
                    }

                    current = await session.Tasks.GetAsync(task.Id, inner) ?? current;
                    await TaskMutationSupport.WriteTaskHistoryAsync(
                        session, ids, normalized.OperationId, before,
                        new TaskAggregateSnapshot(current, await session.Subtasks.ListByTaskAsync(task.Id, inner)),
                        eventKind, fields.Distinct(StringComparer.Ordinal).ToList(), now, inner);

                    return new BoardMoveResult(current, Replayed: false, Warning: null);
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
            var applied = await session.Tasks.UpdateAsync(updated, row.Version, token)
                ?? throw AppException.VersionConflict("This task changed elsewhere while you were working. Refresh and try again.");
            if (applied.Id == current.Id)
            {
                moved = applied;
            }
        }

        return (moved, assignments.Count > 0);
    }

    private static async Task<TaskRecord> ApplyAsync(IDataSession session, DateTimeOffset now, TaskRecord updated, CancellationToken token)
    {
        var row = updated with { Version = updated.Version + 1, UpdatedAtUtc = now };
        return await session.Tasks.UpdateAsync(row, updated.Version, token)
            ?? throw AppException.VersionConflict("This task changed elsewhere while you were working. Refresh and try again.");
    }

    private static T ParseColumn<T>(string? value, string field, string message) where T : struct, Enum =>
        value is null || !Enum.TryParse<T>(value, out var parsed) || !Enum.IsDefined(parsed)
            ? throw AppException.Validation(field, message)
            : parsed;

    private static string EventKindForStatusChange(TaskItemStatus before, TaskItemStatus after) => after switch
    {
        TaskItemStatus.Done => "task.completed",
        TaskItemStatus.Cancelled => "task.cancelled",
        _ when TaskItemStatusRules.IsTerminal(before) => "task.reopened",
        _ => "task.status-changed",
    };

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
