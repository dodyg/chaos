using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Pages;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record PageSummaryResponse(string Id, string? ParentPageId, string Title, string Position, string? DailyDate, string? Icon,
    IReadOnlyList<string> Aliases, string Version, string UpdatedAtUtc, bool Deleted, int WordCount);
public sealed record PageTaskLinkResponse(string TaskId, string Title, bool Deleted, string Quote);
public sealed record PageBacklinkResponse(string Kind, string Id, string Title, string Line);
public sealed record PageResponse(string Id, string? ParentPageId, string Title, string Markdown, string Version, string DraftVersion, string? DailyDate,
    bool Deleted, string CreatedAtUtc, string UpdatedAtUtc, string? Icon, IReadOnlyList<string> TagIds, IReadOnlyList<PageTaskLinkResponse> Tasks,
    IReadOnlyList<PageBacklinkResponse> Backlinks);
public sealed record PageListRequest();
public sealed record PageListResponse(bool Ok, IReadOnlyList<PageSummaryResponse> Pages, TaskErrorResponse? Error);
public sealed record PageDailyListRequest(string? From, string? To);
public sealed record PageGetRequest(string PageId);
public sealed record PageGetDailyRequest(string Date);
public sealed record PageGetResponse(bool Ok, PageResponse? Page, TaskErrorResponse? Error);
public sealed record PageCreateRequest(string OperationId, string? ParentPageId, string Title, string Markdown);
public sealed record PageCreateDailyRequest(string OperationId, string Date, string Markdown);
public sealed record PageSaveRequest(string PageId, string ClientRevision, string Title, string Markdown);
public sealed record PageSaveResponse(bool Ok, PageResponse? Page, bool Applied, TaskErrorResponse? Error);
public sealed record PageMoveRequest(string OperationId, string PageId, string ExpectedVersion, string? ParentPageId, int InsertionIndex);
public sealed record PageItemRequest(string OperationId, string PageId, string ExpectedVersion);
public sealed record PageOperationResponse(bool Ok, IReadOnlyList<PageResponse> Pages, TaskErrorResponse? Error);
public sealed record PageRevisionsRequest(string PageId);
public sealed record PageRevisionResponse(string Number, string Title, string Markdown, string CreatedAtUtc, string UpdatedAtUtc);
public sealed record PageRevisionsResponse(bool Ok, IReadOnlyList<PageRevisionResponse> Revisions, TaskErrorResponse? Error);
public sealed record PageRestoreVersionRequest(string OperationId, string PageId, string ExpectedVersion, string Number);
public sealed record PageMakeTaskRequest(string OperationId, string PageId, string SourceRevision, int Start, int End, string Quote, CaptureDraftDto Draft);
public sealed record PageMakeTaskResponse(bool Ok, TaskResponse? Task, TaskErrorResponse? Error);
public sealed record PageLocateRequest(string TaskId);
public sealed record PageAnchorResponse(string PageId, string State, int? Start, int? End, string Quote, bool Deleted);
public sealed record PageLocateResponse(bool Ok, PageAnchorResponse? Anchor, TaskErrorResponse? Error);
public sealed record PageMentionsRequest(string TaskId);
public sealed record PageMentionsResponse(bool Ok, IReadOnlyList<PageBacklinkResponse> Pages, TaskErrorResponse? Error);
public sealed record PageSearchRequest(string Text, int Limit);
public sealed record PageByTagRequest(string TagId);
public sealed record PageResolveRequest(IReadOnlyList<string> PageIds);
public sealed record PageResolveNamesRequest(IReadOnlyList<string> Names);
public sealed record PageLinkTargetResponse(string Id, string Title, string? Icon, bool Deleted, string? DailyDate, string? Name);
public sealed record PageResolveResponse(bool Ok, IReadOnlyList<PageLinkTargetResponse> Targets, TaskErrorResponse? Error);
public sealed record PageAppendRequest(string OperationId, string Date, string Markdown);
public sealed record PageFromPadRequest(string OperationId, string PadItemId, string ExpectedVersion, string? ParentPageId, string? Title);
public sealed record PageAppendPadRequest(string OperationId, string PadItemId, string ExpectedVersion, string Date);

/// <summary>Pages (SPEC Pages): the tree, daily pages, autosave, versions, links, backlinks and Make task.</summary>
[NeoRpcService("page", Version = 1)]
public sealed class PageRpcService(PageCommands pages)
{
    [NeoRpcMethod("list")]
    public async ValueTask<PageListResponse> ListAsync(PageListRequest request, CancellationToken ct) =>
        await SummariesAsync(async () => await pages.ListAsync(ct));

    [NeoRpcMethod("listDaily")]
    public async ValueTask<PageListResponse> ListDailyAsync(PageDailyListRequest request, CancellationToken ct) =>
        await SummariesAsync(async () => await pages.ListDailyAsync(OptionalDate(request.From, "from"), OptionalDate(request.To, "to"), ct));

    [NeoRpcMethod("listDeleted")]
    public async ValueTask<PageListResponse> ListDeletedAsync(PageListRequest request, CancellationToken ct) =>
        await SummariesAsync(async () => await pages.ListDeletedAsync(ct));

    [NeoRpcMethod("search")]
    public async ValueTask<PageListResponse> SearchAsync(PageSearchRequest request, CancellationToken ct) =>
        await SummariesAsync(async () => await pages.SearchAsync(request.Text, request.Limit, ct));

    [NeoRpcMethod("byTag")]
    public async ValueTask<PageListResponse> ByTagAsync(PageByTagRequest request, CancellationToken ct) =>
        await SummariesAsync(async () => await pages.ListByTagAsync(request.TagId, ct));

    [NeoRpcMethod("get")]
    public async ValueTask<PageGetResponse> GetAsync(PageGetRequest request, CancellationToken ct)
    {
        try { return new(true, Map(await pages.GetAsync(request.PageId, ct)), null); }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("getDaily")]
    public async ValueTask<PageGetResponse> GetDailyAsync(PageGetDailyRequest request, CancellationToken ct)
    {
        try { return new(true, await pages.GetDailyAsync(Date(request.Date, "date"), ct) is { } view ? Map(view) : null, null); }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("create")]
    public async ValueTask<PageOperationResponse> CreateAsync(PageCreateRequest request, CancellationToken ct) =>
        await PagesAsync(async () => [(await pages.CreateAsync(new(request.OperationId, request.ParentPageId, request.Title, request.Markdown), ct)).Page]);

    [NeoRpcMethod("createDaily")]
    public async ValueTask<PageOperationResponse> CreateDailyAsync(PageCreateDailyRequest request, CancellationToken ct) =>
        await PagesAsync(async () => [(await pages.CreateDailyAsync(new(request.OperationId, Date(request.Date, "date"), request.Markdown), ct)).Page]);

    [NeoRpcMethod("save")]
    public async ValueTask<PageSaveResponse> SaveAsync(PageSaveRequest request, CancellationToken ct)
    {
        try
        {
            var revision = long.TryParse(request.ClientRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : throw AppException.Validation("clientRevision", "Invalid editor revision.");
            var result = await pages.SaveAsync(new(request.PageId, revision, request.Title, request.Markdown), ct);
            return new(true, Map(result.Page, Chaos.Application.Pages.PageFrontMatter.Parse(result.Page.RawMarkdown).Icon, [], [], []), result.Applied, null);
        }
        catch (AppException error) { return new(false, null, false, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("move")]
    public async ValueTask<PageOperationResponse> MoveAsync(PageMoveRequest request, CancellationToken ct) =>
        await PagesAsync(async () => [(await pages.MoveAsync(new(request.OperationId, request.PageId, HistoryRpc.Version(request.ExpectedVersion), request.ParentPageId, request.InsertionIndex), ct)).Page]);

    [NeoRpcMethod("delete")]
    public async ValueTask<PageOperationResponse> DeleteAsync(PageItemRequest request, CancellationToken ct) =>
        await PagesAsync(async () => (await pages.DeleteAsync(new(request.OperationId, request.PageId, HistoryRpc.Version(request.ExpectedVersion)), ct)).Pages);

    [NeoRpcMethod("restore")]
    public async ValueTask<PageOperationResponse> RestoreAsync(PageItemRequest request, CancellationToken ct) =>
        await PagesAsync(async () => (await pages.RestoreAsync(new(request.OperationId, request.PageId, HistoryRpc.Version(request.ExpectedVersion)), ct)).Pages);

    [NeoRpcMethod("revisions")]
    public async ValueTask<PageRevisionsResponse> RevisionsAsync(PageRevisionsRequest request, CancellationToken ct)
    {
        try
        {
            return new(true, [.. (await pages.ListRevisionsAsync(request.PageId, ct)).Select(revision => new PageRevisionResponse(
                revision.Number.ToString(CultureInfo.InvariantCulture), revision.Title, revision.RawMarkdown,
                StorageFormats.Instant(revision.CreatedAtUtc), StorageFormats.Instant(revision.UpdatedAtUtc)))], null);
        }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("restoreVersion")]
    public async ValueTask<PageOperationResponse> RestoreVersionAsync(PageRestoreVersionRequest request, CancellationToken ct) =>
        await PagesAsync(async () =>
        {
            var number = long.TryParse(request.Number, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : throw AppException.Validation("number", "Invalid version number.");
            return [(await pages.RestoreVersionAsync(new(request.OperationId, request.PageId, HistoryRpc.Version(request.ExpectedVersion), number), ct)).Page];
        });

    [NeoRpcMethod("makeTask")]
    public async ValueTask<PageMakeTaskResponse> MakeTaskAsync(PageMakeTaskRequest request, CancellationToken ct)
    {
        try
        {
            var revision = long.TryParse(request.SourceRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw AppException.Validation("sourceRevision", "Invalid page revision. Select the text again.");
            var result = await pages.MakeTaskAsync(new(request.OperationId, request.PageId, revision, request.Start, request.End, request.Quote,
                CaptureRpcService.ToDraft(request.Draft, "draft")), ct);
            return new(true, TaskRpcService.ToResponse(result.Task), null);
        }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("locate")]
    public async ValueTask<PageLocateResponse> LocateAsync(PageLocateRequest request, CancellationToken ct)
    {
        try
        {
            var anchor = await pages.LocateAsync(request.TaskId, ct);
            return new(true, anchor is null ? null : new PageAnchorResponse(anchor.PageId, anchor.State.ToString(), anchor.Start, anchor.End, anchor.Quote, anchor.Deleted), null);
        }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("mentions")]
    public async ValueTask<PageMentionsResponse> MentionsAsync(PageMentionsRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await pages.MentionsOfTaskAsync(request.TaskId, ct)).Select(Map)], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("resolveLinks")]
    public async ValueTask<PageResolveResponse> ResolveLinksAsync(PageResolveRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await pages.ResolveLinksAsync(request.PageIds, ct)).Select(target => Map(target, null))], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("resolveNames")]
    public async ValueTask<PageResolveResponse> ResolveNamesAsync(PageResolveNamesRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await pages.ResolveNamesAsync(request.Names, ct)).Select(found => Map(found.Target, found.Name))], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("append")]
    public async ValueTask<PageOperationResponse> AppendAsync(PageAppendRequest request, CancellationToken ct) =>
        await PagesAsync(async () => [(await pages.AppendToDailyAsync(new(request.OperationId, Date(request.Date, "date"), request.Markdown), ct)).Page]);

    [NeoRpcMethod("fromPad")]
    public async ValueTask<PageOperationResponse> FromPadAsync(PageFromPadRequest request, CancellationToken ct) =>
        await PagesAsync(async () => [(await pages.MovePadNoteAsync(new(request.OperationId, request.PadItemId, HistoryRpc.Version(request.ExpectedVersion), request.ParentPageId, request.Title), ct)).Page]);

    [NeoRpcMethod("appendPad")]
    public async ValueTask<PageOperationResponse> AppendPadAsync(PageAppendPadRequest request, CancellationToken ct) =>
        await PagesAsync(async () => [(await pages.AppendPadNoteAsync(new(request.OperationId, request.PadItemId, HistoryRpc.Version(request.ExpectedVersion), Date(request.Date, "date")), ct)).Page]);

    internal static PageResponse Map(PageView view) => Map(view.Page, view.Icon, view.TagIds, view.Tasks, view.Backlinks);

    private static PageResponse Map(PageRecord page, string? icon, IReadOnlyList<string> tagIds, IReadOnlyList<PageTaskLink> tasks, IReadOnlyList<PageBacklink> backlinks) => new(
        page.Id,
        page.ParentPageId,
        page.Title,
        page.RawMarkdown,
        page.Version.ToString(CultureInfo.InvariantCulture),
        page.DraftVersion.ToString(CultureInfo.InvariantCulture),
        page.DailyDate is { } date ? StorageFormats.Date(date) : null,
        page.DeletedAtUtc is not null,
        StorageFormats.Instant(page.CreatedAtUtc),
        StorageFormats.Instant(page.UpdatedAtUtc),
        icon,
        tagIds,
        [.. tasks.Select(task => new PageTaskLinkResponse(task.Link.TaskId, task.Title, task.Deleted, task.Link.Quote))],
        [.. backlinks.Select(Map)]);

    private static PageBacklinkResponse Map(PageBacklink link) => new(link.Kind, link.Id, link.Title, link.Line);

    private static PageLinkTargetResponse Map(PageLinkTarget target, string? name) =>
        new(target.Id, target.Title, target.Icon, target.Deleted, target.DailyDate is { } date ? StorageFormats.Date(date) : null, name);

    private static PageSummaryResponse Map(PageSummary page) => new(
        page.Id, page.ParentPageId, page.Title, page.Position.ToString(CultureInfo.InvariantCulture),
        page.DailyDate is { } date ? StorageFormats.Date(date) : null, page.Icon, page.Aliases,
        page.Version.ToString(CultureInfo.InvariantCulture), StorageFormats.Instant(page.UpdatedAtUtc), page.Deleted, page.WordCount);

    private static async ValueTask<PageListResponse> SummariesAsync(Func<Task<IReadOnlyList<PageSummary>>> action)
    {
        try { return new(true, [.. (await action()).Select(Map)], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    private static async ValueTask<PageOperationResponse> PagesAsync(Func<Task<IReadOnlyList<PageRecord>>> action)
    {
        try { return new(true, [.. (await action()).Select(page => Map(page, Chaos.Application.Pages.PageFrontMatter.Parse(page.RawMarkdown).Icon, [], [], []))], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    private static DateOnly Date(string? value, string field) =>
        OptionalDate(value, field) ?? throw AppException.Validation(field, "Use a date in the form yyyy-MM-dd.");

    private static DateOnly? OptionalDate(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? null
        : DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date
        : throw AppException.Validation(field, "Use a date in the form yyyy-MM-dd.");
}
