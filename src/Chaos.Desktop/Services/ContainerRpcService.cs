using Chaos.Application.Abstractions;
using Chaos.Application.Containers;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record ContainerListRequest();
public sealed record ContainerResponse(string Id, string Name, string Kind, string Version, bool Archived, string? DeadlineDate);
public sealed record ContainerListResponse(bool Ok, IReadOnlyList<ContainerResponse> Containers, TaskErrorResponse? Error);
public sealed record ContainerCreateRequest(string OperationId, string Name, string Kind);
public sealed record ContainerChangeRequest(string OperationId, string ContainerId, string ExpectedVersion, string Name);
public sealed record ContainerOperationResponse(bool Ok, ContainerResponse? Container, TaskErrorResponse? Error);
public sealed record ContainerTasksRequest(string ContainerId);
public sealed record MoveTaskRequest(string OperationId, string TaskId, string ExpectedVersion, string ContainerId, string? SectionId);
public sealed record SectionResponse(string Id, string Title, string Version);
public sealed record SectionListResponse(bool Ok, IReadOnlyList<SectionResponse> Sections, TaskErrorResponse? Error);
public sealed record SectionCreateRequest(string OperationId, string ContainerId, string Title);
public sealed record SectionOperationResponse(bool Ok, SectionResponse? Section, TaskErrorResponse? Error);
public sealed record ContainerOrderRequest(string OperationId, string ContainerId, string ExpectedVersion, int InsertionIndex);
public sealed record SectionChangeRequest(string OperationId, string SectionId, string ExpectedVersion, string Title, int InsertionIndex);
public sealed record DeadlineRequest(string OperationId, string ContainerId, string ExpectedVersion, string? DeadlineDate);
public sealed record ProgressResponse(bool Ok, int DoneCount, int TotalCount, int DoneSubtaskCount, int TotalSubtaskCount, TaskErrorResponse? Error);
public sealed record ContainerDeleteRequest(string OperationId, string ContainerId, string ExpectedVersion, string ContentsChoice);

[NeoRpcService("containers", Version = 1)]
public sealed class ContainerRpcService(ContainerCommands containers, TaskCommands tasks)
{
    [NeoRpcMethod("list")]
    public async ValueTask<ContainerListResponse> ListAsync(ContainerListRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await containers.ListPersonalAsync(ct)).Select(Map)], null); }
        catch (AppException e) { return new(false, [], TaskRpcService.ToErrorResponse(e)); }
    }
    [NeoRpcMethod("listDeleted")]
    public async ValueTask<ContainerListResponse> ListDeletedAsync(ContainerListRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await containers.ListDeletedPersonalAsync(ct)).Select(Map)], null); }
        catch (AppException e) { return new(false, [], TaskRpcService.ToErrorResponse(e)); }
    }
    [NeoRpcMethod("delete")]
    public async ValueTask<ContainerOperationResponse> DeleteAsync(ContainerDeleteRequest request, CancellationToken ct) =>
        await RunAsync(() => containers.DeleteAsync(new DeleteContainerCommand(request.OperationId, request.ContainerId, Version(request.ExpectedVersion), Choice(request.ContentsChoice)), ct));
    [NeoRpcMethod("restore")]
    public async ValueTask<ContainerOperationResponse> RestoreAsync(ContainerChangeRequest request, CancellationToken ct) =>
        await RunAsync(() => containers.RestoreAsync(new RestoreContainerCommand(request.OperationId, request.ContainerId, Version(request.ExpectedVersion)), ct));
    [NeoRpcMethod("tasks")]
    public async ValueTask<ListTasksResponse> TasksAsync(ContainerTasksRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await tasks.ListAsync(request.ContainerId, ct)).Select(TaskRpcService.ToResponse)], null); }
        catch (AppException e) { return new(false, [], TaskRpcService.ToErrorResponse(e)); }
    }
    [NeoRpcMethod("create")]
    public async ValueTask<ContainerOperationResponse> CreateAsync(ContainerCreateRequest request, CancellationToken ct) =>
        await RunAsync(() => containers.CreateAsync(new(request.OperationId, request.Name, Kind(request.Kind), null), ct));
    [NeoRpcMethod("rename")]
    public async ValueTask<ContainerOperationResponse> RenameAsync(ContainerChangeRequest request, CancellationToken ct) =>
        await RunAsync(() => containers.RenameAsync(new RenameContainerCommand(request.OperationId, request.ContainerId, Version(request.ExpectedVersion), request.Name), ct));
    [NeoRpcMethod("archive")]
    public async ValueTask<ContainerOperationResponse> ArchiveAsync(ContainerChangeRequest request, CancellationToken ct) =>
        await RunAsync(() => containers.ArchiveAsync(new(request.OperationId, request.ContainerId, Version(request.ExpectedVersion)), ct));
    [NeoRpcMethod("unarchive")]
    public async ValueTask<ContainerOperationResponse> UnarchiveAsync(ContainerChangeRequest request, CancellationToken ct) =>
        await RunAsync(() => containers.UnarchiveAsync(new(request.OperationId, request.ContainerId, Version(request.ExpectedVersion)), ct));
    [NeoRpcMethod("reorder")]
    public async ValueTask<ContainerOperationResponse> ReorderAsync(ContainerOrderRequest request, CancellationToken ct) =>
        await RunAsync(() => containers.ReorderAsync(new ReorderContainerCommand(request.OperationId, request.ContainerId, Version(request.ExpectedVersion), request.InsertionIndex), ct));
    [NeoRpcMethod("setDeadline")]
    public async ValueTask<ContainerOperationResponse> SetDeadlineAsync(DeadlineRequest request, CancellationToken ct) =>
        await RunAsync(() => containers.SetDeadlineAsync(new(request.OperationId, request.ContainerId, Version(request.ExpectedVersion), Date(request.DeadlineDate)), ct));
    [NeoRpcMethod("progress")]
    public async ValueTask<ProgressResponse> ProgressAsync(ContainerTasksRequest request, CancellationToken ct)
    {
        try { var result = await containers.GetProjectProgressAsync(request.ContainerId, ct); return new(true, checked((int)result.DoneCount), checked((int)result.TotalCount), checked((int)result.DoneSubtaskCount), checked((int)result.TotalSubtaskCount), null); }
        catch (AppException e) { return new(false, 0, 0, 0, 0, TaskRpcService.ToErrorResponse(e)); }
    }
    [NeoRpcMethod("sections")]
    public async ValueTask<SectionListResponse> SectionsAsync(ContainerTasksRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await containers.ListSectionsAsync(request.ContainerId, ct)).Select(MapSection)], null); }
        catch (AppException e) { return new(false, [], TaskRpcService.ToErrorResponse(e)); }
    }
    [NeoRpcMethod("deletedSections")]
    public async ValueTask<SectionListResponse> DeletedSectionsAsync(ContainerTasksRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await containers.ListDeletedSectionsAsync(request.ContainerId, ct)).Select(MapSection)], null); }
        catch (AppException e) { return new(false, [], TaskRpcService.ToErrorResponse(e)); }
    }
    [NeoRpcMethod("createSection")]
    public async ValueTask<SectionOperationResponse> CreateSectionAsync(SectionCreateRequest request, CancellationToken ct)
    {
        try { return new(true, MapSection((await containers.CreateSectionAsync(new(request.OperationId, request.ContainerId, request.Title), ct)).Section), null); }
        catch (AppException e) { return new(false, null, TaskRpcService.ToErrorResponse(e)); }
    }
    [NeoRpcMethod("renameSection")]
    public async ValueTask<SectionOperationResponse> RenameSectionAsync(SectionChangeRequest request, CancellationToken ct) =>
        await RunSectionAsync(() => containers.RenameAsync(new RenameSectionCommand(request.OperationId, request.SectionId, Version(request.ExpectedVersion), request.Title), ct));
    [NeoRpcMethod("reorderSection")]
    public async ValueTask<SectionOperationResponse> ReorderSectionAsync(SectionChangeRequest request, CancellationToken ct) =>
        await RunSectionAsync(() => containers.ReorderAsync(new ReorderSectionCommand(request.OperationId, request.SectionId, Version(request.ExpectedVersion), request.InsertionIndex), ct));
    [NeoRpcMethod("deleteSection")]
    public async ValueTask<SectionOperationResponse> DeleteSectionAsync(SectionChangeRequest request, CancellationToken ct) =>
        await RunSectionAsync(() => containers.DeleteAsync(new DeleteSectionCommand(request.OperationId, request.SectionId, Version(request.ExpectedVersion)), ct));
    [NeoRpcMethod("restoreSection")]
    public async ValueTask<SectionOperationResponse> RestoreSectionAsync(SectionChangeRequest request, CancellationToken ct) =>
        await RunSectionAsync(() => containers.RestoreAsync(new RestoreSectionCommand(request.OperationId, request.SectionId, Version(request.ExpectedVersion)), ct));
    [NeoRpcMethod("moveTask")]
    public async ValueTask<TaskOperationResponse> MoveTaskAsync(MoveTaskRequest request, CancellationToken ct)
    {
        try { var result = await tasks.MoveAsync(new(request.OperationId, request.TaskId, Version(request.ExpectedVersion), request.ContainerId, request.SectionId), ct); return new(true, request.OperationId, TaskRpcService.ToResponse(result.Task), null); }
        catch (AppException e) { return new(false, request.OperationId, null, TaskRpcService.ToErrorResponse(e)); }
    }
    private static async ValueTask<ContainerOperationResponse> RunAsync(Func<Task<ContainerMutationResult>> action)
    {
        try { return new(true, Map((await action()).Container), null); }
        catch (AppException e) { return new(false, null, TaskRpcService.ToErrorResponse(e)); }
    }
    private static async ValueTask<SectionOperationResponse> RunSectionAsync(Func<Task<SectionMutationResult>> action)
    {
        try { return new(true, MapSection((await action()).Section), null); }
        catch (AppException e) { return new(false, null, TaskRpcService.ToErrorResponse(e)); }
    }
    private static DateOnly? Date(string? value) => string.IsNullOrEmpty(value) ? null : DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var result) ? result : throw AppException.Validation("deadlineDate", "Enter a valid date.");
    private static long Version(string value) => long.TryParse(value, out var version) && version > 0 ? version : throw AppException.Validation("expectedVersion", "Invalid version. Refresh and try again.");
    private static ContainerKind Kind(string value) => Enum.TryParse<ContainerKind>(value, out var kind) && Enum.IsDefined(kind) ? kind : throw AppException.Validation("kind", "Choose a list or project.");
    private static ContainerContentsChoice Choice(string value) => Enum.TryParse<ContainerContentsChoice>(value, out var choice) && Enum.IsDefined(choice) ? choice : throw AppException.Validation("contentsChoice", "Choose what to do with the tasks.");
    private static ContainerResponse Map(ContainerRecord row) => new(row.Id, row.Name, row.Kind.ToString(), row.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), row.ArchivedAtUtc is not null, row.DeadlineDate?.ToString("yyyy-MM-dd"));
    private static SectionResponse MapSection(SectionRecord row) => new(row.Id, row.Title, row.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
