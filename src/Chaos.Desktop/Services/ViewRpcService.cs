using Chaos.Application.Activity;
using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Views;
using Chaos.Domain.Tasks;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record ViewDto(
    string Id,
    string Name,
    string Icon,
    string? Color,
    bool Pinned,
    int Position,
    string DefinitionJson,
    string LayoutJson,
    bool AlertsEnabled,
    string Version,
    bool Deleted);

public sealed record ViewListRequest(bool IncludeDeleted = false);
public sealed record ViewListResponse(bool Ok, IReadOnlyList<ViewDto> Views, IReadOnlyList<string> Icons, TaskErrorResponse? Error);
public sealed record ViewGetRequest(string ViewId);
public sealed record ViewSaveRequest(
    string OperationId,
    string? ViewId,
    string ExpectedVersion,
    string Name,
    string? Icon,
    string? Color,
    string DefinitionJson,
    string? LayoutJson,
    bool Pinned,
    bool AlertsEnabled);
public sealed record ViewResponse(bool Ok, ViewDto? View, TaskErrorResponse? Error);
public sealed record ViewDuplicateRequest(string OperationId, string ViewId);
public sealed record ViewPinRequest(string OperationId, string ViewId, string ExpectedVersion, bool Pinned);
public sealed record ViewDeleteRequest(string OperationId, string ViewId, string ExpectedVersion);
public sealed record ViewReorderRequest(string OperationId, IReadOnlyList<string> ViewIds);
public sealed record ViewRunRequest(string ViewId, string? Category = null, int? Limit = null, string? Cursor = null);
public sealed record ViewPreviewRequest(string DefinitionJson, string? LayoutJson = null, string? Category = null, int? Limit = null);
public sealed record ViewRunResponse(bool Ok, IReadOnlyList<QueryRowResponse> Rows, int TotalCount, string? NextCursor, string? LayoutJson, TaskErrorResponse? Error);
public sealed record SavedViewCountsRequest(string? Category = null);
public sealed record ViewCountEntry(string ViewId, int Count);
public sealed record SavedViewCountsResponse(bool Ok, IReadOnlyList<ViewCountEntry> Counts, TaskErrorResponse? Error);
public sealed record ViewContextRequest(string ViewId);
public sealed record ViewContextResponse(
    bool Ok,
    string? ContainerId,
    string? Status,
    string? Priority,
    string? Category,
    IReadOnlyList<string> TagIds,
    string? PlannedDate,
    string? DueDate,
    TaskErrorResponse? Error);
public sealed record ViewExportRequest(string ViewId, string? Category = null);
public sealed record ViewExportResponse(bool Ok, string? FileName, int Rows, TaskErrorResponse? Error);
public sealed record ViewAlertsRequest();
public sealed record ViewAlertDto(string Id, string ViewId, string ViewName, bool ViewAvailable, int TaskCount, string FirstTaskTitle, string DeliveredAtUtc);
public sealed record ViewAlertsResponse(bool Ok, IReadOnlyList<ViewAlertDto> Alerts, TaskErrorResponse? Error);
public sealed record ViewAlertAckRequest(string AlertId);
public sealed record ViewAlertAckResponse(bool Ok, TaskErrorResponse? Error);
public sealed record ViewGitHubOptionsRequest();
public sealed record ViewGitHubRepositoryDto(string Id, string FullName);
public sealed record ViewGitHubOptionsResponse(bool Ok, IReadOnlyList<ViewGitHubRepositoryDto> Repositories, TaskErrorResponse? Error);
public sealed record ViewFromSearchRequest(TaskQueryRequest Search);
public sealed record ViewFromSearchResponse(bool Ok, string? DefinitionJson, TaskErrorResponse? Error);

/// <summary>Custom views (PLAN2 P3): saved conditions, layouts, counts, quick-add defaults and CSV export. Errors are returned as data.</summary>
[NeoRpcService("views", Version = 1)]
public sealed class ViewRpcService(SavedViewCommands commands, SavedViewQueries queries, ViewAlertService alerts)
{
    [NeoRpcMethod("list")]
    public async ValueTask<ViewListResponse> ListAsync(ViewListRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("views.list");
        try { return new(true, [.. (await commands.ListAsync(request.IncludeDeleted, ct)).Select(Map)], SavedViewCommands.Icons, null); }
        catch (AppException error) { return new(false, [], SavedViewCommands.Icons, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("get")]
    public async ValueTask<ViewResponse> GetAsync(ViewGetRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("views.get");
        try { return new(true, Map(await commands.GetAsync(request.ViewId, ct)), null); }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("save")]
    public async ValueTask<ViewResponse> SaveAsync(ViewSaveRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("views.save");
        return await Run(async () => (await commands.SaveAsync(new(
            request.OperationId, request.ViewId, Version(request.ExpectedVersion), request.Name, request.Icon, request.Color,
            request.DefinitionJson, request.LayoutJson, request.Pinned, request.AlertsEnabled), ct)).View);
    }

    [NeoRpcMethod("duplicate")]
    public async ValueTask<ViewResponse> DuplicateAsync(ViewDuplicateRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("views.duplicate");
        return await Run(async () => (await commands.DuplicateAsync(request.OperationId, request.ViewId, ct)).View);
    }

    [NeoRpcMethod("setPinned")]
    public async ValueTask<ViewResponse> SetPinnedAsync(ViewPinRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("views.setPinned");
        return await Run(async () => (await commands.SetPinnedAsync(request.OperationId, request.ViewId, Version(request.ExpectedVersion), request.Pinned, ct)).View);
    }

    [NeoRpcMethod("delete")]
    public async ValueTask<ViewResponse> DeleteAsync(ViewDeleteRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("views.delete");
        return await Run(async () => (await commands.DeleteAsync(request.OperationId, request.ViewId, Version(request.ExpectedVersion), ct)).View);
    }

    [NeoRpcMethod("restore")]
    public async ValueTask<ViewResponse> RestoreAsync(ViewDeleteRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("views.restore");
        return await Run(async () => (await commands.RestoreAsync(request.OperationId, request.ViewId, Version(request.ExpectedVersion), ct)).View);
    }

    [NeoRpcMethod("reorder")]
    public async ValueTask<ViewListResponse> ReorderAsync(ViewReorderRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("views.reorder");
        try { return new(true, [.. (await commands.ReorderAsync(request.OperationId, request.ViewIds, ct)).Select(Map)], SavedViewCommands.Icons, null); }
        catch (AppException error) { return new(false, [], SavedViewCommands.Icons, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("run")]
    public async ValueTask<ViewRunResponse> RunAsync(ViewRunRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("views.run");
        try
        {
            var result = await queries.RunAsync(request.ViewId, Category(request.Category), Math.Clamp(request.Limit ?? 100, 1, 100), request.Cursor, ct);
            return Map(result.Page, result.Layout.ToJson());
        }
        catch (AppException error) { return new(false, [], 0, null, null, TaskRpcService.ToErrorResponse(error)); }
    }

    /// <summary>Rows and the exact count for an unsaved definition: the editor's live preview.</summary>
    [NeoRpcMethod("preview")]
    public async ValueTask<ViewRunResponse> PreviewAsync(ViewPreviewRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("views.preview");
        try
        {
            var page = await queries.RunDraftAsync(request.DefinitionJson, request.LayoutJson, Category(request.Category), Math.Clamp(request.Limit ?? 5, 1, 50), ct);
            return Map(page, request.LayoutJson);
        }
        catch (AppException error) { return new(false, [], 0, null, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("counts")]
    public async ValueTask<SavedViewCountsResponse> CountsAsync(SavedViewCountsRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("views.counts");
        try { return new(true, [.. (await queries.CountsAsync(Category(request.Category), ct)).Select(entry => new ViewCountEntry(entry.Key, TagRpcService.Count(entry.Value)))], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("contextDefaults")]
    public async ValueTask<ViewContextResponse> ContextDefaultsAsync(ViewContextRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("views.contextDefaults");
        try
        {
            var defaults = await commands.ContextDefaultsAsync(request.ViewId, ct);
            return new(true, defaults.ContainerId, defaults.Status?.ToString(), defaults.Priority?.ToString(), defaults.Category?.ToString(),
                defaults.TagIds, defaults.PlannedDate is { } planned ? StorageFormats.Date(planned) : null,
                defaults.DueDate is { } due ? StorageFormats.Date(due) : null, null);
        }
        catch (AppException error) { return new(false, null, null, null, null, [], null, null, TaskRpcService.ToErrorResponse(error)); }
    }

    /// <summary>Writes the view as a CSV into the exports folder; "Show in folder" opens it through <c>data.openFolder</c>.</summary>
    [NeoRpcMethod("export")]
    public async ValueTask<ViewExportResponse> ExportAsync(ViewExportRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("views.export");
        try
        {
            var (csv, fileName, rows) = await queries.BuildCsvAsync(request.ViewId, Category(request.Category), ct);
            var directory = DataRpcService.DefaultExportDirectory;
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, fileName), csv, ViewExport.Utf8Bom, ct);
            return new(true, fileName, rows, null);
        }
        catch (AppException error) { return new(false, null, 0, TaskRpcService.ToErrorResponse(error)); }
        catch (IOException) { return new(false, null, 0, TaskRpcService.ToErrorResponse(new AppException(AppErrorKind.StorageUnavailable, "The export could not be written. Check that the exports folder is available."))); }
        catch (UnauthorizedAccessException) { return new(false, null, 0, TaskRpcService.ToErrorResponse(new AppException(AppErrorKind.StorageUnavailable, "The export could not be written. Check that the exports folder is available."))); }
    }

    /// <summary>The subscribed GitHub repositories, for the GitHub condition group (empty when GitHub is not connected).</summary>
    [NeoRpcMethod("githubOptions")]
    public async ValueTask<ViewGitHubOptionsResponse> GitHubOptionsAsync(ViewGitHubOptionsRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("views.githubOptions");
        try { return new(true, [.. (await queries.GitHubRepositoriesAsync(ct)).Select(item => new ViewGitHubRepositoryDto(item.Id, item.FullName))], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    /// <summary>Delivered alerts waiting to be dismissed (new tasks that matched an alerting view).</summary>
    [NeoRpcMethod("alerts")]
    public async ValueTask<ViewAlertsResponse> AlertsAsync(ViewAlertsRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("views.alerts");
        try
        {
            var items = await alerts.ListAsync(20, ct);
            return new(true, [.. items.Select(item => new ViewAlertDto(item.Alert.Id, item.Alert.ViewId, item.ViewName, item.ViewAvailable, item.Alert.TaskCount, item.Alert.FirstTaskTitle,
                StorageFormats.Instant(item.Alert.DeliveredAtUtc ?? item.Alert.CreatedAtUtc)))], null);
        }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("acknowledgeAlert")]
    public async ValueTask<ViewAlertAckResponse> AcknowledgeAlertAsync(ViewAlertAckRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("views.acknowledgeAlert");
        try { await alerts.AcknowledgeAsync(request.AlertId, ct); return new(true, null); }
        catch (AppException error) { return new(false, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("fromSearch")]
    public ValueTask<ViewFromSearchResponse> FromSearchAsync(ViewFromSearchRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("views.fromSearch");
        ct.ThrowIfCancellationRequested();
        try { return ValueTask.FromResult(new ViewFromSearchResponse(true, ViewFromSearch.Build(QueryRpcService.ToSpec(request.Search)), null)); }
        catch (AppException error) { return ValueTask.FromResult(new ViewFromSearchResponse(false, null, TaskRpcService.ToErrorResponse(error))); }
    }

    private static async ValueTask<ViewResponse> Run(Func<Task<SavedViewRecord>> action)
    {
        try { return new(true, Map(await action()), null); }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    private static ViewRunResponse Map(TaskQueryPage page, string? layoutJson) => new(
        true,
        [.. page.Rows.Select(row => new QueryRowResponse(
            TaskRpcService.ToResponse(row.Task), row.ContainerName, row.ContainerArchived, row.TagIds,
            [.. row.Matches.Select(match => match.ToString())], row.GroupDate is { } date ? StorageFormats.Date(date) : null,
            row.WaitingPerson, row.NextFollowUpAtUtc is { } followUp ? StorageFormats.Instant(followUp) : null))],
        TagRpcService.Count(page.TotalCount), page.NextCursor, layoutJson, null);

    private static ViewDto Map(SavedViewRecord view) => new(
        view.Id, view.Name, view.Icon, view.Color, view.Pinned, view.Position, view.DefinitionJson, view.LayoutJson,
        view.AlertsEnabled, view.Version.ToString(CultureInfo.InvariantCulture), view.DeletedAtUtc is not null);

    private static CapacityCategory? Category(string? value) =>
        string.IsNullOrEmpty(value) ? null
        : Enum.TryParse<CapacityCategory>(value, out var parsed) && Enum.IsDefined(parsed) ? parsed
        : throw AppException.Validation("category", "Choose a valid value.");

    private static long Version(string value) =>
        long.TryParse(value, out var version) && version >= 0 ? version : throw AppException.Validation("expectedVersion", "Invalid version. Refresh and try again.");
}
