using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tags;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record TagListRequest();
public sealed record TagSuggestRequest(string Text);
public sealed record TagResponse(string Id, string Name, string Path, string? ParentId, string? Color, string Version, int Depth);
public sealed record TagListResponse(bool Ok, IReadOnlyList<TagResponse> Tags, TaskErrorResponse? Error);
public sealed record TagCreateRequest(string OperationId, string Path, string? ParentId, string? Color);
public sealed record TagRenameRequest(string OperationId, string TagId, string ExpectedVersion, string Name);
public sealed record TagColorRequest(string OperationId, string TagId, string ExpectedVersion, string? Color);
public sealed record TagMoveRequest(string OperationId, string TagId, string ExpectedVersion, string? ParentId);
public sealed record TagChangeRequest(string OperationId, string TagId, string ExpectedVersion);
public sealed record TagMergePreviewRequest(string SourceId, string TargetId);
public sealed record TagMergeRequest(string OperationId, string SourceId, string SourceVersion, string TargetId, string TargetVersion);
public sealed record TagMergePreviewResponse(bool Ok, int TaskLinksMoved, int TaskLinksAlreadyPresent, int SubtaskLinksMoved, int SubtaskLinksAlreadyPresent, int ChildTagsMoved, int ChildTagsMerged, int PadLinksMoved, int PadLinksAlreadyPresent, TaskErrorResponse? Error);
public sealed record TagOperationResponse(bool Ok, TagResponse? Tag, TaskErrorResponse? Error);
/// <summary>Target kind is "Task" or "Subtask".</summary>
public sealed record TagTargetRequest(string TargetKind, string TargetId);
public sealed record TagLinkRequest(string OperationId, string TargetKind, string TargetId, string? TagId, string? Path);
public sealed record TagUnlinkRequest(string OperationId, string TargetKind, string TargetId, string TagId);
public sealed record TagIdsResponse(bool Ok, IReadOnlyList<string> TagIds, TaskErrorResponse? Error);

/// <summary>Tag tree, management and tagging RPC (PLAN §15 WP3). Errors are returned as data.</summary>
[NeoRpcService("tags", Version = 1)]
public sealed class TagRpcService(TagCommands tags, Chaos.Desktop.Platform.GitHubSyncEngine? sync = null)
{
    [NeoRpcMethod("list")]
    public async ValueTask<TagListResponse> ListAsync(TagListRequest request, CancellationToken ct) =>
        await ListResponseAsync(async () => [.. (await tags.ListAsync(ct)).Select(Map)]);

    [NeoRpcMethod("listDeleted")]
    public async ValueTask<TagListResponse> ListDeletedAsync(TagListRequest request, CancellationToken ct) =>
        await ListResponseAsync(async () => [.. (await tags.ListDeletedAsync(ct)).Select(tag => Map(new TagWithPath(tag, tag.DisplayName)))]);

    [NeoRpcMethod("suggest")]
    public async ValueTask<TagListResponse> SuggestAsync(TagSuggestRequest request, CancellationToken ct) =>
        await ListResponseAsync(async () => [.. (await tags.SuggestAsync(request.Text, 8, ct)).Select(Map)]);

    [NeoRpcMethod("create")]
    public async ValueTask<TagOperationResponse> CreateAsync(TagCreateRequest request, CancellationToken ct) =>
        await TagResultAsync(() => tags.CreateAsync(new(request.OperationId, request.Path, Blank(request.ParentId), Blank(request.Color)), ct));

    [NeoRpcMethod("rename")]
    public async ValueTask<TagOperationResponse> RenameAsync(TagRenameRequest request, CancellationToken ct) =>
        await TagResultAsync(() => tags.RenameAsync(new(request.OperationId, request.TagId, Version(request.ExpectedVersion), request.Name), ct));

    [NeoRpcMethod("setColor")]
    public async ValueTask<TagOperationResponse> SetColorAsync(TagColorRequest request, CancellationToken ct) =>
        await TagResultAsync(() => tags.SetColorAsync(new(request.OperationId, request.TagId, Version(request.ExpectedVersion), Blank(request.Color)), ct));

    [NeoRpcMethod("move")]
    public async ValueTask<TagOperationResponse> MoveAsync(TagMoveRequest request, CancellationToken ct) =>
        await TagResultAsync(() => tags.MoveAsync(new(request.OperationId, request.TagId, Version(request.ExpectedVersion), Blank(request.ParentId)), ct));

    [NeoRpcMethod("delete")]
    public async ValueTask<TagOperationResponse> DeleteAsync(TagChangeRequest request, CancellationToken ct) =>
        await TagResultAsync(() => tags.DeleteAsync(new(request.OperationId, request.TagId, Version(request.ExpectedVersion)), ct));

    [NeoRpcMethod("restore")]
    public async ValueTask<TagOperationResponse> RestoreAsync(TagChangeRequest request, CancellationToken ct) =>
        await TagResultAsync(() => tags.RestoreAsync(new(request.OperationId, request.TagId, Version(request.ExpectedVersion)), ct));

    [NeoRpcMethod("previewMerge")]
    public async ValueTask<TagMergePreviewResponse> PreviewMergeAsync(TagMergePreviewRequest request, CancellationToken ct)
    {
        try
        {
            var preview = await tags.PreviewMergeAsync(request.SourceId, request.TargetId, ct);
            return new(true, Count(preview.TaskLinksMoved), Count(preview.TaskLinksAlreadyPresent), Count(preview.SubtaskLinksMoved), Count(preview.SubtaskLinksAlreadyPresent), Count(preview.ChildTagsMoved), Count(preview.ChildTagsMerged), Count(preview.PadLinksMoved), Count(preview.PadLinksAlreadyPresent), null);
        }
        catch (AppException error) { return new(false, 0, 0, 0, 0, 0, 0, 0, 0, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("merge")]
    public async ValueTask<TagOperationResponse> MergeAsync(TagMergeRequest request, CancellationToken ct)
    {
        try
        {
            var result = await tags.MergeAsync(new(request.OperationId, request.SourceId, Version(request.SourceVersion), request.TargetId, Version(request.TargetVersion)), ct);
            return new(true, Map(new TagWithPath(result.Target, result.Target.DisplayName)), null);
        }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("targetTags")]
    public async ValueTask<TagIdsResponse> TargetTagsAsync(TagTargetRequest request, CancellationToken ct) =>
        await IdsAsync(async () => await tags.ListTargetTagsAsync(Kind(request.TargetKind), request.TargetId, ct));

    [NeoRpcMethod("link")]
    public async ValueTask<TagIdsResponse> LinkAsync(TagLinkRequest request, CancellationToken ct) =>
        await IdsAsync(async () => { var result = await tags.LinkAsync(new(request.OperationId, Kind(request.TargetKind), request.TargetId, Blank(request.TagId), Blank(request.Path)), ct); if (request.TargetKind == "Task") sync?.RequestTaskSync(request.TargetId); return result.TagIds; });

    [NeoRpcMethod("unlink")]
    public async ValueTask<TagIdsResponse> UnlinkAsync(TagUnlinkRequest request, CancellationToken ct) =>
        await IdsAsync(async () => { var result = await tags.UnlinkAsync(new(request.OperationId, Kind(request.TargetKind), request.TargetId, request.TagId), ct); if (request.TargetKind == "Task") sync?.RequestTaskSync(request.TargetId); return result.TagIds; });

    private static async ValueTask<TagListResponse> ListResponseAsync(Func<Task<IReadOnlyList<TagResponse>>> load)
    {
        try { return new(true, await load(), null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    private static async ValueTask<TagOperationResponse> TagResultAsync(Func<Task<TagMutationResult>> action)
    {
        try
        {
            var tag = (await action()).Tag;
            return new(true, Map(new TagWithPath(tag, tag.DisplayName)), null);
        }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    private static async ValueTask<TagIdsResponse> IdsAsync(Func<Task<IReadOnlyList<string>>> action)
    {
        try { return new(true, await action(), null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    internal static TagResponse Map(TagWithPath row) => new(
        row.Tag.Id,
        row.Tag.DisplayName,
        row.Path,
        row.Tag.ParentTagId,
        row.Tag.ColorKey,
        row.Tag.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
        row.Path.Count(character => character == '/'));

    internal static int Count(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static long Version(string value) => long.TryParse(value, out var version) && version > 0 ? version : throw AppException.Validation("expectedVersion", "Invalid version. Refresh and try again.");

    private static TagTargetKind Kind(string value) => Enum.TryParse<TagTargetKind>(value, out var kind) && Enum.IsDefined(kind) ? kind : throw AppException.Validation("targetKind", "Tags can be added to tasks, subtasks and pad notes.");
}
