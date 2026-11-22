using Chaos.Application.Activity;
using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Pad;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record PadListRequest(bool Archived, string? TagId);
public sealed record PadRegionDto(double X, double Y, double W, double H);
public sealed record PadTaskLinkResponse(string TaskId, string Title, bool Deleted, string Quote, PadRegionDto? Region = null);
public sealed record PadItemResponse(string Id, string Markdown, string Version, string DraftVersion, bool Archived, string? ArchivedAtUtc, bool Deleted,
    string CreatedAtUtc, string UpdatedAtUtc, IReadOnlyList<string> TagIds, IReadOnlyList<PadTaskLinkResponse> Tasks,
    string Kind = "text", string? PreviewAttachmentId = null, int Width = 0, int Height = 0, string SpaceId = "");
public sealed record PadListResponse(bool Ok, IReadOnlyList<PadItemResponse> Items, TaskErrorResponse? Error);
public sealed record PadGetRequest(string ItemId);
public sealed record PadCreateRequest(string OperationId, string Markdown);
public sealed record PadSaveRequest(string ItemId, string ClientRevision, string Markdown);
public sealed record PadSaveResponse(bool Ok, PadItemResponse? Item, bool Applied, TaskErrorResponse? Error);
public sealed record PadItemChangeRequest(string ItemId, string ExpectedVersion);
public sealed record PadItemsRequest(string OperationId, IReadOnlyList<PadItemChangeRequest> Items);
public sealed record PadItemRequest(string OperationId, string ItemId, string ExpectedVersion);
public sealed record PadReorderRequest(string OperationId, string ItemId, string ExpectedVersion, int InsertionIndex);
public sealed record PadOperationResponse(bool Ok, IReadOnlyList<PadItemResponse> Items, TaskErrorResponse? Error);
public sealed record PadMakeTaskRequest(string OperationId, string PadItemId, string SourceRevision, int Start, int End, string Quote,
    string Title, string? Notes, string? ContainerId, string? PlannedDate, string? DueDate, IReadOnlyList<string> TagIds, CaptureDraftDto? Draft = null);
public sealed record PadMakeTaskResponse(bool Ok, TaskResponse? Task, TaskErrorResponse? Error);
public sealed record PadLocateRequest(string TaskId);
public sealed record PadAnchorResponse(string PadItemId, string State, int? Start, int? End, string Quote, bool Archived, bool Deleted, PadRegionDto? Region = null);
public sealed record PadCreateDrawingRequest(string OperationId, int Width, int Height);
public sealed record PadDrawingGetRequest(string ItemId);
public sealed record PadDrawingSaveRequest(string ItemId, string ClientRevision, string StrokesJson, string? PreviewAttachmentId);
public sealed record PadDrawingData(PadItemResponse Item, string StrokesJson, int Width, int Height, string Revision, string? PreviewAttachmentId);
public sealed record PadDrawingResponse(bool Ok, PadDrawingData? Drawing, bool Applied, TaskErrorResponse? Error);
public sealed record PadMakeTaskFromRegionRequest(string OperationId, string PadItemId, string SourceRevision, PadRegionDto Region,
    string Title, string? Notes, string? ContainerId, string? PlannedDate, string? DueDate, IReadOnlyList<string> TagIds, CaptureDraftDto? Draft = null);
public sealed record PadLocateResponse(bool Ok, PadAnchorResponse? Anchor, TaskErrorResponse? Error);
public sealed record PadSearchRequest(string Text, int Limit);
public sealed record PadResolveRequest(IReadOnlyList<string> ItemIds);
public sealed record PadLinkTargetResponse(string Id, string Title, bool Deleted);
public sealed record PadResolveResponse(bool Ok, IReadOnlyList<PadLinkTargetResponse> Targets, TaskErrorResponse? Error);
public sealed record PadReviewStatusRequest();
public sealed record PadReviewStatusResponse(bool Ok, bool Due, string? LastReviewedAtUtc, IReadOnlyList<PadItemResponse> Suggestions, TaskErrorResponse? Error);
public sealed record PadReviewRequest(string OperationId, IReadOnlyList<PadItemChangeRequest> Archive);

/// <summary>The Personal scratch pad (PLAN §9, §15 WP5): items, autosave, Make task, backlinks, review and search.</summary>
[NeoRpcService("pad", Version = 1)]
public sealed class PadRpcService(PadCommands pad)
{
    [NeoRpcMethod("list")]
    public ValueTask<PadListResponse> ListAsync(PadListRequest request, CancellationToken ct) =>
        Rpc.Run<PadListResponse>("pad.list", async () => { return new(true, [.. (await pad.ListAsync(request.Archived, Blank(request.TagId), ct)).Select(Map)], null); },
            error => new(false, [], error));

    [NeoRpcMethod("get")]
    public ValueTask<PadListResponse> GetAsync(PadGetRequest request, CancellationToken ct) =>
        Rpc.Run<PadListResponse>("pad.get", async () => { return new(true, [Map(await pad.GetAsync(request.ItemId, ct))], null); },
            error => new(false, [], error));

    [NeoRpcMethod("listDeleted")]
    public ValueTask<PadListResponse> ListDeletedAsync(PadListRequest request, CancellationToken ct) =>
        Rpc.Run<PadListResponse>("pad.listDeleted", async () => { return new(true, [.. (await pad.ListDeletedAsync(ct)).Select(item => Map(new PadItemView(item, [], [])))], null); },
            error => new(false, [], error));

    [NeoRpcMethod("create")]
    public async ValueTask<PadOperationResponse> CreateAsync(PadCreateRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("pad.create");
        return await ItemsAsync(async () => [(await pad.CreateAsync(new(request.OperationId, request.Markdown), ct)).Item]);
    }

    [NeoRpcMethod("save")]
    public ValueTask<PadSaveResponse> SaveAsync(PadSaveRequest request, CancellationToken ct) =>
        Rpc.Run<PadSaveResponse>("pad.save", async () => {
            var revision = long.TryParse(request.ClientRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : throw AppException.Validation("clientRevision", "Invalid editor revision.");
            var result = await pad.SaveTextAsync(new(request.ItemId, revision, request.Markdown), ct);
            return new(true, Map(new PadItemView(result.Item, [], [])), result.Applied, null);
        },
            error => new(false, null, false, error));

    [NeoRpcMethod("archive")]
    public async ValueTask<PadOperationResponse> ArchiveAsync(PadItemsRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("pad.archive");
        return await ItemsAsync(async () => (await pad.ArchiveAsync(new(request.OperationId, Changes(request.Items)), ct)).Items);
    }

    [NeoRpcMethod("unarchive")]
    public async ValueTask<PadOperationResponse> UnarchiveAsync(PadItemsRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("pad.unarchive");
        return await ItemsAsync(async () => (await pad.UnarchiveAsync(new(request.OperationId, Changes(request.Items)), ct)).Items);
    }

    [NeoRpcMethod("delete")]
    public async ValueTask<PadOperationResponse> DeleteAsync(PadItemRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("pad.delete");
        return await ItemsAsync(async () => [(await pad.DeleteAsync(new(request.OperationId, request.ItemId, HistoryRpc.Version(request.ExpectedVersion)), ct)).Item]);
    }

    [NeoRpcMethod("restore")]
    public async ValueTask<PadOperationResponse> RestoreAsync(PadItemRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("pad.restore");
        return await ItemsAsync(async () => [(await pad.RestoreAsync(new(request.OperationId, request.ItemId, HistoryRpc.Version(request.ExpectedVersion)), ct)).Item]);
    }

    [NeoRpcMethod("reorder")]
    public async ValueTask<PadOperationResponse> ReorderAsync(PadReorderRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("pad.reorder");
        return await ItemsAsync(async () => [(await pad.ReorderAsync(new(request.OperationId, request.ItemId, HistoryRpc.Version(request.ExpectedVersion), request.InsertionIndex), ct)).Item]);
    }

    [NeoRpcMethod("makeTask")]
    public ValueTask<PadMakeTaskResponse> MakeTaskAsync(PadMakeTaskRequest request, CancellationToken ct) =>
        Rpc.Run<PadMakeTaskResponse>("pad.makeTask", async () => {
            var revision = long.TryParse(request.SourceRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw AppException.Validation("sourceRevision", "Invalid note revision. Select the text again.");
            var result = await pad.MakeTaskAsync(new(request.OperationId, request.PadItemId, revision, request.Start, request.End, request.Quote,
                request.Title, request.Notes, Blank(request.ContainerId), Date(request.PlannedDate, "plannedDate"), Date(request.DueDate, "dueDate"), request.TagIds,
                request.Draft is { } draft ? CaptureRpcService.ToDraft(draft, "draft") : null), ct);
            return new(true, TaskRpcService.ToResponse(result.Task), null);
        },
            error => new(false, null, error));

    [NeoRpcMethod("createDrawing")]
    public async ValueTask<PadOperationResponse> CreateDrawingAsync(PadCreateDrawingRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("pad.createDrawing");
        return await ItemsAsync(async () => [(await pad.CreateDrawingAsync(new(request.OperationId, request.Width, request.Height), ct)).Item]);
    }

    [NeoRpcMethod("getDrawing")]
    public ValueTask<PadDrawingResponse> GetDrawingAsync(PadDrawingGetRequest request, CancellationToken ct) =>
        Rpc.Run<PadDrawingResponse>("pad.getDrawing", async () => { return new(true, MapDrawing(await pad.GetDrawingAsync(request.ItemId, ct)), true, null); },
            error => new(false, null, false, error));

    [NeoRpcMethod("saveDrawing")]
    public ValueTask<PadDrawingResponse> SaveDrawingAsync(PadDrawingSaveRequest request, CancellationToken ct) =>
        Rpc.Run<PadDrawingResponse>("pad.saveDrawing", async () => {
            var revision = long.TryParse(request.ClientRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : throw AppException.Validation("clientRevision", "Invalid editor revision.");
            var result = await pad.SaveDrawingAsync(new(request.ItemId, revision, request.StrokesJson, Blank(request.PreviewAttachmentId)), ct);
            return new(true, MapDrawing(new PadDrawingView(result.Item, result.Drawing)), result.Applied, null);
        },
            error => new(false, null, false, error));

    [NeoRpcMethod("makeTaskFromRegion")]
    public ValueTask<PadMakeTaskResponse> MakeTaskFromRegionAsync(PadMakeTaskFromRegionRequest request, CancellationToken ct) =>
        Rpc.Run<PadMakeTaskResponse>("pad.makeTaskFromRegion", async () => {
            var revision = long.TryParse(request.SourceRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw AppException.Validation("sourceRevision", "Invalid drawing revision. Select the region again.");
            var result = await pad.MakeTaskFromRegionAsync(new(request.OperationId, request.PadItemId, revision,
                new PadRegion(request.Region.X, request.Region.Y, request.Region.W, request.Region.H),
                request.Title, request.Notes, Blank(request.ContainerId), Date(request.PlannedDate, "plannedDate"), Date(request.DueDate, "dueDate"), request.TagIds,
                request.Draft is { } draft ? CaptureRpcService.ToDraft(draft, "draft") : null), ct);
            return new(true, TaskRpcService.ToResponse(result.Task), null);
        },
            error => new(false, null, error));

    [NeoRpcMethod("locate")]
    public ValueTask<PadLocateResponse> LocateAsync(PadLocateRequest request, CancellationToken ct) =>
        Rpc.Run<PadLocateResponse>("pad.locate", async () => {
            var anchor = await pad.LocateAsync(request.TaskId, ct);
            return new(true, anchor is null ? null : new PadAnchorResponse(anchor.PadItemId, anchor.State.ToString(), anchor.Start, anchor.End, anchor.Quote, anchor.Archived, anchor.Deleted, MapRegion(anchor.Region)), null);
        },
            error => new(false, null, error));

    [NeoRpcMethod("search")]
    public ValueTask<PadListResponse> SearchAsync(PadSearchRequest request, CancellationToken ct) =>
        Rpc.Run<PadListResponse>("pad.search", async () => { return new(true, [.. (await pad.SearchAsync(request.Text, request.Limit, ct)).Select(Map)], null); },
            error => new(false, [], error));

    [NeoRpcMethod("resolveLinks")]
    public ValueTask<PadResolveResponse> ResolveLinksAsync(PadResolveRequest request, CancellationToken ct) =>
        Rpc.Run<PadResolveResponse>("pad.resolveLinks", async () => { return new(true, [.. (await pad.ResolveLinksAsync(request.ItemIds, ct)).Select(target => new PadLinkTargetResponse(target.Id, target.Excerpt, target.Deleted))], null); },
            error => new(false, [], error));

    [NeoRpcMethod("reviewStatus")]
    public ValueTask<PadReviewStatusResponse> ReviewStatusAsync(PadReviewStatusRequest request, CancellationToken ct) =>
        Rpc.Run<PadReviewStatusResponse>("pad.reviewStatus", async () => {
            var status = await pad.ReviewStatusAsync(ct);
            return new(true, status.Due, status.LastReviewedAtUtc is { } last ? StorageFormats.Instant(last) : null,
                [.. status.Suggestions.Select(item => Map(new PadItemView(item, [], [])))], null);
        },
            error => new(false, false, null, [], error));

    [NeoRpcMethod("completeReview")]
    public async ValueTask<PadOperationResponse> CompleteReviewAsync(PadReviewRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("pad.completeReview");
        return await ItemsAsync(async () => (await pad.CompleteReviewAsync(new(request.OperationId, Changes(request.Archive)), ct)).Items);
    }

    internal static PadItemResponse Map(PadItemView view) => new(
        view.Item.Id,
        view.Item.RawMarkdown,
        view.Item.Version.ToString(CultureInfo.InvariantCulture),
        view.Item.DraftVersion.ToString(CultureInfo.InvariantCulture),
        view.Item.ArchivedAtUtc is not null,
        view.Item.ArchivedAtUtc is { } archived ? StorageFormats.Instant(archived) : null,
        view.Item.DeletedAtUtc is not null,
        StorageFormats.Instant(view.Item.CreatedAtUtc),
        StorageFormats.Instant(view.Item.UpdatedAtUtc),
        view.TagIds,
        [.. view.Tasks.Select(task => new PadTaskLinkResponse(task.Link.TaskId, task.Title, task.Deleted, task.Link.Quote, MapRegion(PadDrawingValidation.ParseRegion(task.Link.RegionJson))))],
        view.Item.Kind == PadItemKind.Drawing ? "drawing" : "text",
        view.Drawing?.PreviewAttachmentId,
        view.Drawing?.Width ?? 0,
        view.Drawing?.Height ?? 0,
        view.Item.SpaceId);

    private static PadRegionDto? MapRegion(PadRegion? region) => region is null ? null : new(region.X, region.Y, region.W, region.H);

    private static PadDrawingData MapDrawing(PadDrawingView view) => new(
        Map(new PadItemView(view.Item, [], [], view.Drawing)),
        view.Drawing.StrokesJson, view.Drawing.Width, view.Drawing.Height,
        view.Drawing.Revision.ToString(CultureInfo.InvariantCulture), view.Drawing.PreviewAttachmentId);

    private static async ValueTask<PadOperationResponse> ItemsAsync(Func<Task<IReadOnlyList<PadItemRecord>>> action)
    {
        try { return new(true, [.. (await action()).Select(item => Map(new PadItemView(item, [], [])))], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    private static List<PadItemChange> Changes(IReadOnlyList<PadItemChangeRequest> items) =>
        [.. items.Select(item => new PadItemChange(item.ItemId, HistoryRpc.Version(item.ExpectedVersion)))];

    private static DateOnly? Date(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? null
        : StorageFormats.ParseDate(value) ?? throw AppException.Validation(field, "Use a date in the form yyyy-MM-dd.");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
