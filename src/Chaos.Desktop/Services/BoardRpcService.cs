using Chaos.Application.Activity;
using Chaos.Application.Abstractions;
using Chaos.Application.Boards;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record BoardGetRequest(string ContainerId, string? Category = null, bool ShowCancelled = false);
public sealed record BoardViewGetRequest(string ViewId, string? Category = null, bool ShowCancelled = false);
public sealed record BoardSettingsDto(
    string GroupBy,
    string SwimlaneBy,
    string? ColumnTagId,
    string? SwimlaneTagId,
    string WipLimitsJson,
    string HiddenColumnsJson,
    string Version,
    bool IsDefault);
public sealed record BoardColumnResponse(string Key, string Title, bool IsNone);
public sealed record BoardLaneResponse(string Key, string Title, bool IsNone);
public sealed record BoardCardResponse(
    TaskResponse Task,
    IReadOnlyList<string> TagIds,
    int SubtasksTotal,
    int SubtasksDone,
    bool HasIssueLink,
    string ColumnKey,
    string LaneKey);
public sealed record BoardGetResponse(
    bool Ok,
    BoardSettingsDto Settings,
    IReadOnlyList<BoardColumnResponse> Columns,
    IReadOnlyList<BoardLaneResponse> Lanes,
    IReadOnlyList<BoardCardResponse> Cards,
    int TotalCount,
    bool Truncated,
    TaskErrorResponse? Error);
public sealed record BoardUpdateSettingsRequest(
    string OperationId,
    string TargetKind,
    string TargetId,
    string ExpectedVersion,
    string GroupBy,
    string SwimlaneBy,
    string? ColumnTagId,
    string? SwimlaneTagId,
    string WipLimitsJson,
    string HiddenColumnsJson);
public sealed record BoardSettingsResponse(bool Ok, BoardSettingsDto? Settings, TaskErrorResponse? Error);
public sealed record BoardMoveRequest(
    string OperationId,
    string TaskId,
    string ExpectedVersion,
    string GroupBy,
    string? FromColumn,
    string? ToColumn,
    string? BeforeTaskId,
    string? AfterTaskId,
    string? SubtaskChoice = null);
public sealed record BoardMoveResponse(bool Ok, TaskResponse? Task, TaskErrorResponse? Error, string? Warning = null);

/// <summary>Kanban boards on lists and projects (PLAN2 P2). Errors are returned as data.</summary>
[NeoRpcService("board", Version = 1)]
public sealed class BoardRpcService(BoardCommands commands, BoardQueries queries, Chaos.Application.Views.ViewBoardQueries viewQueries, Chaos.Desktop.Platform.GitHubSyncEngine? sync = null)
{
    [NeoRpcMethod("get")]
    public async ValueTask<BoardGetResponse> GetAsync(BoardGetRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("board.get");
        try
        {
            var board = await queries.GetAsync(request.ContainerId, Category(request.Category), request.ShowCancelled, ct);
            return new(true, Map(board.Settings), [.. board.Columns.Select(Map)], [.. board.Lanes.Select(Map)],
                [.. board.Cards.Select(Map)], board.TotalCount, board.Truncated, null);
        }
        catch (AppException error) { return new(false, Map(BoardCommands.Defaults(BoardTargetKind.Container, request.ContainerId)), [], [], [], 0, false, TaskRpcService.ToErrorResponse(error)); }
    }

    /// <summary>The board layout of a custom view: its rows as cards (settings target kind View).</summary>
    [NeoRpcMethod("getView")]
    public async ValueTask<BoardGetResponse> GetViewAsync(BoardViewGetRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("board.getView");
        try
        {
            var board = await viewQueries.GetAsync(request.ViewId, Category(request.Category), request.ShowCancelled, ct);
            return new(true, Map(board.Settings), [.. board.Columns.Select(Map)], [.. board.Lanes.Select(Map)],
                [.. board.Cards.Select(Map)], board.TotalCount, board.Truncated, null);
        }
        catch (AppException error) { return new(false, Map(BoardCommands.Defaults(BoardTargetKind.View, request.ViewId)), [], [], [], 0, false, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("updateSettings")]
    public async ValueTask<BoardSettingsResponse> UpdateSettingsAsync(BoardUpdateSettingsRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("board.updateSettings");
        try
        {
            var result = await commands.UpdateSettingsAsync(new(
                request.OperationId,
                ParseEnum<BoardTargetKind>(request.TargetKind, "targetKind"),
                request.TargetId,
                Version(request.ExpectedVersion),
                ParseEnum<BoardGrouping>(request.GroupBy, "groupBy"),
                ParseEnum<BoardSwimlane>(request.SwimlaneBy, "swimlaneBy"),
                Blank(request.ColumnTagId),
                Blank(request.SwimlaneTagId),
                request.WipLimitsJson,
                request.HiddenColumnsJson), ct);
            return new(true, Map(result.Settings), null);
        }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("move")]
    public async ValueTask<BoardMoveResponse> MoveAsync(BoardMoveRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("board.move");
        try
        {
            var result = await commands.MoveAsync(new(
                request.OperationId,
                request.TaskId,
                Version(request.ExpectedVersion),
                ParseEnum<BoardGrouping>(request.GroupBy, "groupBy"),
                Blank(request.FromColumn),
                Blank(request.ToColumn),
                request.BeforeTaskId,
                request.AfterTaskId,
                request.SubtaskChoice is null ? null : ParseEnum<OpenSubtaskChoice>(request.SubtaskChoice, "subtaskChoice")), ct);
            sync?.RequestTaskSync(result.Task.Id);
            return new(true, TaskRpcService.ToResponse(result.Task), null);
        }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    internal static BoardSettingsDto Map(BoardSettingsRecord settings) => new(
        settings.GroupBy.ToString(),
        settings.SwimlaneBy.ToString(),
        settings.ColumnTagId,
        settings.SwimlaneTagId,
        settings.WipLimitsJson,
        settings.HiddenColumnsJson,
        settings.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
        settings.Version == 0);

    private static BoardColumnResponse Map(BoardColumnDto column) => new(column.Key, column.Title, column.IsNone);
    private static BoardLaneResponse Map(BoardLaneDto lane) => new(lane.Key, lane.Title, lane.IsNone);
    private static BoardCardResponse Map(BoardCardDto card) => new(
        TaskRpcService.ToResponse(card.Task), card.TagIds, card.SubtasksTotal, card.SubtasksDone, card.HasIssueLink, card.ColumnKey, card.LaneKey);

    private static CapacityCategory? Category(string? value) =>
        string.IsNullOrEmpty(value) ? null : ParseEnum<CapacityCategory>(value, "category");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static long Version(string value) => long.TryParse(value, out var version) && version >= 0 ? version : throw AppException.Validation("expectedVersion", "Invalid version. Refresh and try again.");

    private static T ParseEnum<T>(string value, string field) where T : struct, Enum =>
        Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw AppException.Validation(field, "Choose a valid value.");
}
