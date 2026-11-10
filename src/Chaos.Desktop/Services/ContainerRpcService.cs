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

[NeoRpcService("containers", Version = 1)]
public sealed class ContainerRpcService(ContainerCommands containers, TaskCommands tasks)
{
    [NeoRpcMethod("list")]
    public async ValueTask<ContainerListResponse> ListAsync(ContainerListRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await containers.ListPersonalAsync(ct)).Select(Map)], null); }
        catch (AppException e) { return new(false, [], TaskRpcService.ToErrorResponse(e)); }
    }
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
    [NeoRpcMethod("sections")]
    public async ValueTask<SectionListResponse> SectionsAsync(ContainerTasksRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await containers.ListSectionsAsync(request.ContainerId, ct)).Select(MapSection)], null); }
        catch (AppException e) { return new(false, [], TaskRpcService.ToErrorResponse(e)); }
    }
    [NeoRpcMethod("createSection")]
    public async ValueTask<SectionOperationResponse> CreateSectionAsync(SectionCreateRequest request, CancellationToken ct)
    {
        try { return new(true, MapSection((await containers.CreateSectionAsync(new(request.OperationId, request.ContainerId, request.Title), ct)).Section), null); }
        catch (AppException e) { return new(false, null, TaskRpcService.ToErrorResponse(e)); }
    }
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
    private static long Version(string value) => long.TryParse(value, out var version) && version > 0 ? version : throw AppException.Validation("expectedVersion", "Invalid version. Refresh and try again.");
    private static ContainerKind Kind(string value) => Enum.TryParse<ContainerKind>(value, out var kind) && Enum.IsDefined(kind) ? kind : throw AppException.Validation("kind", "Choose a list or project.");
    private static ContainerResponse Map(ContainerRecord row) => new(row.Id, row.Name, row.Kind.ToString(), row.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), row.ArchivedAtUtc is not null, row.DeadlineDate?.ToString("yyyy-MM-dd"));
    private static SectionResponse MapSection(SectionRecord row) => new(row.Id, row.Title, row.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
