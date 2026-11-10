using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record CreateTaskRequest(string OperationId, string Title, string? Notes, string? ContainerId);

public sealed record GetTaskRequest(string TaskId);
public sealed record ListTasksRequest();
public sealed record ListTasksResponse(bool Ok, IReadOnlyList<TaskResponse> Tasks, TaskErrorResponse? Error);
public sealed record EditTaskRequest(string OperationId, string TaskId, string ExpectedVersion, string Title, string Notes, string? PlannedDate, string? PlannedTime, string? PlannedZoneId, string? DueDate, string? DueTime, string? DueZoneId, string Priority, int? EstimateMinutes, string Category);
public sealed record ChangeTaskStatusRequest(string OperationId, string TaskId, string ExpectedVersion, string Status, string? SubtaskChoice);
public sealed record ReorderTaskRequest(string OperationId, string TaskId, string ExpectedVersion, int InsertionIndex);
public sealed record DeleteTaskRequest(string OperationId, string TaskId, string ExpectedVersion);

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
    string UpdatedAtUtc,
    string Priority,
    int? EstimateMinutes,
    string Category,
    string? PlannedTime,
    string? PlannedZoneId,
    string? DueTime,
    string? DueZoneId,
    string? SectionId);

public sealed record TaskErrorFieldResponse(string FieldPath, string Message);

/// <summary>Stable error kind plus message; never stack traces, SQL or local paths (PLAN §4).</summary>
public sealed record TaskErrorResponse(string Kind, string Message, IReadOnlyList<TaskErrorFieldResponse> Fields, string CorrelationId);

public sealed record TaskOperationResponse(bool Ok, string OperationId, TaskResponse? Task, TaskErrorResponse? Error, string? Warning = null);

public sealed record TaskGetResponse(bool Ok, TaskResponse? Task, TaskErrorResponse? Error);

/// <summary>
/// Storage RPC for the first vertical slice (PLAN §15 WP1): UI input → typed RPC →
/// transaction → row. Errors are returned as data (ok=false), never thrown, so the
/// renderer can always render them.
/// </summary>
[NeoRpcService("tasks", Version = 1)]
public sealed class TaskRpcService(TaskCommands commands)
{
    [NeoRpcMethod("listInbox")]
    public async ValueTask<ListTasksResponse> ListInboxAsync(ListTasksRequest request, CancellationToken cancellationToken)
    {
        try { return new(true, [.. (await commands.ListInboxAsync(cancellationToken)).Select(ToResponse)], null); }
        catch (AppException error) { return new(false, [], ToErrorResponse(error)); }
    }

    [NeoRpcMethod("edit")]
    public async ValueTask<TaskOperationResponse> EditAsync(EditTaskRequest request, CancellationToken cancellationToken) =>
        await MutateAsync(request.OperationId, async () => await commands.UpdateDetailsAsync(new(
            request.OperationId, request.TaskId, Version(request.ExpectedVersion), request.Title, request.Notes,
            Date(request.PlannedDate, "plannedDate"), Time(request.PlannedTime, "plannedTime"), request.PlannedZoneId, Date(request.DueDate, "dueDate"), Time(request.DueTime, "dueTime"), request.DueZoneId,
            ParseEnum<TaskPriority>(request.Priority, "priority"), request.EstimateMinutes,
            ParseEnum<CapacityCategory>(request.Category, "category")), cancellationToken));

    [NeoRpcMethod("setStatus")]
    public async ValueTask<TaskOperationResponse> SetStatusAsync(ChangeTaskStatusRequest request, CancellationToken cancellationToken) =>
        await MutateAsync(request.OperationId, async () => await commands.SetStatusAsync(new(
            request.OperationId, request.TaskId, Version(request.ExpectedVersion), ParseEnum<TaskItemStatus>(request.Status, "status"),
            request.SubtaskChoice is null ? null : ParseEnum<OpenSubtaskChoice>(request.SubtaskChoice, "subtaskChoice")), cancellationToken));

    [NeoRpcMethod("reorder")]
    public async ValueTask<TaskOperationResponse> ReorderAsync(ReorderTaskRequest request, CancellationToken cancellationToken) =>
        await MutateAsync(request.OperationId, async () => await commands.ReorderAsync(new(request.OperationId, request.TaskId, Version(request.ExpectedVersion), request.InsertionIndex), cancellationToken));

    [NeoRpcMethod("delete")]
    public async ValueTask<TaskOperationResponse> DeleteAsync(DeleteTaskRequest request, CancellationToken cancellationToken) =>
        await MutateAsync(request.OperationId, async () => await commands.DeleteAsync(new(request.OperationId, request.TaskId, Version(request.ExpectedVersion)), cancellationToken));

    [NeoRpcMethod("restore")]
    public async ValueTask<TaskOperationResponse> RestoreAsync(DeleteTaskRequest request, CancellationToken cancellationToken) =>
        await MutateAsync(request.OperationId, async () => await commands.RestoreAsync(new(request.OperationId, request.TaskId, Version(request.ExpectedVersion)), cancellationToken));

    private static async ValueTask<TaskOperationResponse> MutateAsync(string operationId, Func<Task<TaskMutationResult>> action)
    {
        try { var result = await action(); return new(true, operationId, ToResponse(result.Task), null, result.Warning); }
        catch (AppException error) { return new(false, operationId, null, ToErrorResponse(error)); }
    }

    private static long Version(string value) => long.TryParse(value, out var result) && result > 0 ? result : throw AppException.Validation("expectedVersion", "Invalid task version. Refresh and try again.");
    private static DateOnly? Date(string? value, string field) => string.IsNullOrEmpty(value) ? null : DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ? date : throw AppException.Validation(field, "Enter a valid date.");
    private static TimeOnly? Time(string? value, string field) => string.IsNullOrEmpty(value) ? null : TimeOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var time) ? time : throw AppException.Validation(field, "Enter a valid time.");
    private static T ParseEnum<T>(string value, string field) where T : struct, Enum => Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw AppException.Validation(field, "Choose a valid value.");
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

    internal static TaskResponse ToResponse(TaskRecord task) => new(
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
        StorageFormats.Instant(task.UpdatedAtUtc),
        task.Priority.ToString(), task.EstimateMinutes, task.CapacityCategory.ToString(),
        task.PlannedTime?.ToString("HH:mm:ss"), task.PlannedZoneId, task.DueTime?.ToString("HH:mm:ss"), task.DueZoneId, task.SectionId);

    internal static TaskErrorResponse ToErrorResponse(AppException error) => new(
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
