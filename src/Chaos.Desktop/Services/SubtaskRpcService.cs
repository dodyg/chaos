using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record SubtaskListRequest(string TaskId);
public sealed record SubtaskResponse(string Id, string TaskId, string Title, string Notes, string Status, string Version);
public sealed record SubtaskListResponse(bool Ok, IReadOnlyList<SubtaskResponse> Subtasks, TaskErrorResponse? Error);
public sealed record SubtaskAddRequest(string OperationId, string TaskId, string Title, string Notes);
public sealed record SubtaskEditRequest(string OperationId, string SubtaskId, string ExpectedVersion, string Title, string Notes);
public sealed record SubtaskStatusRequest(string OperationId, string SubtaskId, string ExpectedVersion, string Status);
public sealed record SubtaskOrderRequest(string OperationId, string SubtaskId, string ExpectedVersion, int InsertionIndex);
public sealed record SubtaskRestoreRequest(string OperationId, string TaskId, string SubtaskId, string ExpectedVersion);
public sealed record SubtaskOperationResponse(bool Ok, SubtaskResponse? Subtask, TaskErrorResponse? Error);

[NeoRpcService("subtasks", Version = 1)]
public sealed class SubtaskRpcService(TaskCommands tasks, SubtaskCommands subtasks)
{
    [NeoRpcMethod("listDeleted")]
    public async ValueTask<SubtaskListResponse> ListDeletedAsync(SubtaskListRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await subtasks.ListDeletedAsync(request.TaskId, ct)).Select(Map)], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }
    [NeoRpcMethod("restore")]
    public async ValueTask<SubtaskOperationResponse> RestoreAsync(SubtaskRestoreRequest request, CancellationToken ct) =>
        await RunAsync(() => subtasks.RestoreAsync(new(request.OperationId, request.TaskId, request.SubtaskId, Version(request.ExpectedVersion)), ct));
    [NeoRpcMethod("list")]
    public async ValueTask<SubtaskListResponse> ListAsync(SubtaskListRequest request, CancellationToken ct)
    {
        try
        {
            var detail = await tasks.GetDetailAsync(request.TaskId, ct);
            if (detail.Task is null) throw AppException.NotFound("That task no longer exists. Refresh and try again.");
            return new(true, [.. detail.Subtasks.Select(Map)], null);
        }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("add")]
    public async ValueTask<SubtaskOperationResponse> AddAsync(SubtaskAddRequest request, CancellationToken ct) =>
        await RunAsync(() => subtasks.AddAsync(new(request.OperationId, request.TaskId, request.Title, request.Notes), ct));

    [NeoRpcMethod("edit")]
    public async ValueTask<SubtaskOperationResponse> EditAsync(SubtaskEditRequest request, CancellationToken ct) =>
        await RunAsync(() => subtasks.UpdateAsync(new(request.OperationId, request.SubtaskId, Version(request.ExpectedVersion), request.Title, request.Notes), ct));

    [NeoRpcMethod("delete")]
    public async ValueTask<SubtaskOperationResponse> DeleteAsync(SubtaskEditRequest request, CancellationToken ct) =>
        await RunAsync(() => subtasks.DeleteAsync(new(request.OperationId, request.SubtaskId, Version(request.ExpectedVersion), request.Title, request.Notes), ct));

    [NeoRpcMethod("setStatus")]
    public async ValueTask<SubtaskOperationResponse> SetStatusAsync(SubtaskStatusRequest request, CancellationToken ct) =>
        await RunAsync(() => subtasks.SetStatusAsync(new(request.OperationId, request.SubtaskId, Version(request.ExpectedVersion), Status(request.Status)), ct));

    [NeoRpcMethod("reorder")]
    public async ValueTask<SubtaskOperationResponse> ReorderAsync(SubtaskOrderRequest request, CancellationToken ct) =>
        await RunAsync(() => subtasks.ReorderAsync(new(request.OperationId, request.SubtaskId, Version(request.ExpectedVersion), request.InsertionIndex), ct));

    private static async ValueTask<SubtaskOperationResponse> RunAsync(Func<Task<SubtaskMutationResult>> action)
    {
        try { return new(true, Map((await action()).Subtask), null); }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    private static long Version(string value) => long.TryParse(value, out var version) && version > 0 ? version : throw AppException.Validation("expectedVersion", "Invalid version. Refresh and try again.");
    private static TaskItemStatus Status(string value) => Enum.TryParse<TaskItemStatus>(value, out var status) && Enum.IsDefined(status) ? status : throw AppException.Validation("status", "Choose a valid status.");
    private static SubtaskResponse Map(SubtaskRecord row) => new(row.Id, row.TaskId, row.Title, row.Notes, row.Status.ToString(), row.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
