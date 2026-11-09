using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record CreateTaskRequest(string OperationId, string Title, string? Notes, string? ContainerId);

public sealed record GetTaskRequest(string TaskId);

/// <summary>Renderer-facing task view; IDs and Version as strings, dates yyyy-MM-dd, instants ISO 8601 UTC (PLAN §4/§5).</summary>
public sealed record TaskResponse(
    string Id,
    string SpaceId,
    string ContainerId,
    string Title,
    string Notes,
    string Status,
    string? PlannedDate,
    string? DueDate,
    string Version,
    string CreatedAtUtc,
    string UpdatedAtUtc);

public sealed record TaskErrorFieldResponse(string FieldPath, string Message);

/// <summary>Stable error kind plus message; never stack traces, SQL or local paths (PLAN §4).</summary>
public sealed record TaskErrorResponse(string Kind, string Message, IReadOnlyList<TaskErrorFieldResponse> Fields, string CorrelationId);

public sealed record TaskOperationResponse(bool Ok, string OperationId, TaskResponse? Task, TaskErrorResponse? Error);

public sealed record TaskGetResponse(bool Ok, TaskResponse? Task, TaskErrorResponse? Error);

/// <summary>
/// Storage RPC for the first vertical slice (PLAN §15 WP1): UI input → typed RPC →
/// transaction → row. Errors are returned as data (ok=false), never thrown, so the
/// renderer can always render them.
/// </summary>
[NeoRpcService("tasks", Version = 1)]
public sealed class TaskRpcService(TaskCommands commands)
{
    [NeoRpcMethod("create")]
    public async ValueTask<TaskOperationResponse> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await commands.CaptureAsync(
                new CaptureTaskCommand(request.OperationId, request.ContainerId ?? string.Empty, request.Title, request.Notes),
                cancellationToken);
            return new TaskOperationResponse(true, result.OperationId, ToResponse(result.Task), null);
        }
        catch (AppException error)
        {
            return new TaskOperationResponse(false, request.OperationId, null, ToErrorResponse(error));
        }
    }

    [NeoRpcMethod("get")]
    public async ValueTask<TaskGetResponse> GetAsync(GetTaskRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var task = await commands.GetAsync(request.TaskId, cancellationToken);
            return task is null
                ? new TaskGetResponse(false, null, new TaskErrorResponse(
                    "notFound",
                    "No task with that ID exists on this computer.",
                    [],
                    Guid.NewGuid().ToString("N")))
                : new TaskGetResponse(true, ToResponse(task), null);
        }
        catch (AppException error)
        {
            return new TaskGetResponse(false, null, ToErrorResponse(error));
        }
    }

    private static TaskResponse ToResponse(TaskRecord task) => new(
        task.Id,
        task.SpaceId,
        task.ContainerId,
        task.Title,
        task.Notes,
        task.Status.ToString(),
        task.PlannedDate is null ? null : StorageFormats.Date(task.PlannedDate.Value),
        task.DueDate is null ? null : StorageFormats.Date(task.DueDate.Value),
        task.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
        StorageFormats.Instant(task.CreatedAtUtc),
        StorageFormats.Instant(task.UpdatedAtUtc));

    private static TaskErrorResponse ToErrorResponse(AppException error) => new(
        KindToken(error.Kind),
        error.Message,
        [.. error.Fields.Select(field => new TaskErrorFieldResponse(field.FieldPath, field.Message))],
        error.CorrelationId);

    private static string KindToken(AppErrorKind kind) => kind switch
    {
        AppErrorKind.Validation => "validation",
        AppErrorKind.NotFound => "notFound",
        AppErrorKind.VersionConflict => "versionConflict",
        AppErrorKind.OperationConflict => "operationConflict",
        AppErrorKind.StorageFull => "storageFull",
        AppErrorKind.StorageUnavailable => "storageUnavailable",
        AppErrorKind.Cancelled => "cancelled",
        AppErrorKind.UnsupportedCapability => "unsupportedCapability",
        _ => "storageUnavailable",
    };
}
