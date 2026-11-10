using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Pad;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record PadListRequest(bool Archived, string? TagId);
public sealed record PadTaskLinkResponse(string TaskId, string Title, bool Deleted, string Quote);
public sealed record PadItemResponse(string Id, string Markdown, string Version, string DraftVersion, bool Archived, string? ArchivedAtUtc, bool Deleted,
    string CreatedAtUtc, string UpdatedAtUtc, IReadOnlyList<string> TagIds, IReadOnlyList<PadTaskLinkResponse> Tasks);
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
    string Title, string? Notes, string? ContainerId, string? PlannedDate, string? DueDate, IReadOnlyList<string> TagIds);
public sealed record PadMakeTaskResponse(bool Ok, TaskResponse? Task, TaskErrorResponse? Error);
public sealed record PadLocateRequest(string TaskId);
public sealed record PadAnchorResponse(string PadItemId, string State, int? Start, int? End, string Quote, bool Archived, bool Deleted);
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
    public async ValueTask<PadListResponse> ListAsync(PadListRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await pad.ListAsync(request.Archived, Blank(request.TagId), ct)).Select(Map)], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("get")]
    public async ValueTask<PadListResponse> GetAsync(PadGetRequest request, CancellationToken ct)
    {
        try { return new(true, [Map(await pad.GetAsync(request.ItemId, ct))], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("listDeleted")]
    public async ValueTask<PadListResponse> ListDeletedAsync(PadListRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await pad.ListDeletedAsync(ct)).Select(item => Map(new PadItemView(item, [], [])))], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("create")]
    public async ValueTask<PadOperationResponse> CreateAsync(PadCreateRequest request, CancellationToken ct) =>
        await ItemsAsync(async () => [(await pad.CreateAsync(new(request.OperationId, request.Markdown), ct)).Item]);

    [NeoRpcMethod("save")]
    public async ValueTask<PadSaveResponse> SaveAsync(PadSaveRequest request, CancellationToken ct)
    {
        try
        {
            var revision = long.TryParse(request.ClientRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : throw AppException.Validation("clientRevision", "Invalid editor revision.");
            var result = await pad.SaveTextAsync(new(request.ItemId, revision, request.Markdown), ct);
            return new(true, Map(new PadItemView(result.Item, [], [])), result.Applied, null);
        }
        catch (AppException error) { return new(false, null, false, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("archive")]
    public async ValueTask<PadOperationResponse> ArchiveAsync(PadItemsRequest request, CancellationToken ct) =>
        await ItemsAsync(async () => (await pad.ArchiveAsync(new(request.OperationId, Changes(request.Items)), ct)).Items);

    [NeoRpcMethod("unarchive")]
    public async ValueTask<PadOperationResponse> UnarchiveAsync(PadItemsRequest request, CancellationToken ct) =>
        await ItemsAsync(async () => (await pad.UnarchiveAsync(new(request.OperationId, Changes(request.Items)), ct)).Items);

    [NeoRpcMethod("delete")]
    public async ValueTask<PadOperationResponse> DeleteAsync(PadItemRequest request, CancellationToken ct) =>
        await ItemsAsync(async () => [(await pad.DeleteAsync(new(request.OperationId, request.ItemId, HistoryRpc.Version(request.ExpectedVersion)), ct)).Item]);

    [NeoRpcMethod("restore")]
    public async ValueTask<PadOperationResponse> RestoreAsync(PadItemRequest request, CancellationToken ct) =>
        await ItemsAsync(async () => [(await pad.RestoreAsync(new(request.OperationId, request.ItemId, HistoryRpc.Version(request.ExpectedVersion)), ct)).Item]);

    [NeoRpcMethod("reorder")]
    public async ValueTask<PadOperationResponse> ReorderAsync(PadReorderRequest request, CancellationToken ct) =>
        await ItemsAsync(async () => [(await pad.ReorderAsync(new(request.OperationId, request.ItemId, HistoryRpc.Version(request.ExpectedVersion), request.InsertionIndex), ct)).Item]);

    [NeoRpcMethod("makeTask")]
    public async ValueTask<PadMakeTaskResponse> MakeTaskAsync(PadMakeTaskRequest request, CancellationToken ct)
    {
        try
        {
            var revision = long.TryParse(request.SourceRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw AppException.Validation("sourceRevision", "Invalid note revision. Select the text again.");
            var result = await pad.MakeTaskAsync(new(request.OperationId, request.PadItemId, revision, request.Start, request.End, request.Quote,
                request.Title, request.Notes, Blank(request.ContainerId), Date(request.PlannedDate, "plannedDate"), Date(request.DueDate, "dueDate"), request.TagIds), ct);
            return new(true, TaskRpcService.ToResponse(result.Task), null);
        }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("locate")]
    public async ValueTask<PadLocateResponse> LocateAsync(PadLocateRequest request, CancellationToken ct)
    {
        try
        {
            var anchor = await pad.LocateAsync(request.TaskId, ct);
            return new(true, anchor is null ? null : new PadAnchorResponse(anchor.PadItemId, anchor.State.ToString(), anchor.Start, anchor.End, anchor.Quote, anchor.Archived, anchor.Deleted), null);
        }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("search")]
    public async ValueTask<PadListResponse> SearchAsync(PadSearchRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await pad.SearchAsync(request.Text, request.Limit, ct)).Select(Map)], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("resolveLinks")]
    public async ValueTask<PadResolveResponse> ResolveLinksAsync(PadResolveRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await pad.ResolveLinksAsync(request.ItemIds, ct)).Select(target => new PadLinkTargetResponse(target.Id, target.Excerpt, target.Deleted))], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("reviewStatus")]
    public async ValueTask<PadReviewStatusResponse> ReviewStatusAsync(PadReviewStatusRequest request, CancellationToken ct)
    {
        try
        {
            var status = await pad.ReviewStatusAsync(ct);
            return new(true, status.Due, status.LastReviewedAtUtc is { } last ? StorageFormats.Instant(last) : null,
                [.. status.Suggestions.Select(item => Map(new PadItemView(item, [], [])))], null);
        }
        catch (AppException error) { return new(false, false, null, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("completeReview")]
    public async ValueTask<PadOperationResponse> CompleteReviewAsync(PadReviewRequest request, CancellationToken ct) =>
        await ItemsAsync(async () => (await pad.CompleteReviewAsync(new(request.OperationId, Changes(request.Archive)), ct)).Items);

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
        [.. view.Tasks.Select(task => new PadTaskLinkResponse(task.Link.TaskId, task.Title, task.Deleted, task.Link.Quote))]);

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
