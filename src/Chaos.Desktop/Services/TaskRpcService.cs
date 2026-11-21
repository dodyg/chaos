using Chaos.Application.Activity;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using NeoAstra.Rpc;
using Chaos.Desktop.Platform;

namespace Chaos.Desktop.Services;

public sealed record CreateTaskRequest(string OperationId, string Title, string? Notes, string? ContainerId);

public sealed record GetTaskRequest(string TaskId);
public sealed record ListTasksRequest();
public sealed record ListDeletedTasksRequest(string? ContainerId);
public sealed record ListTasksResponse(bool Ok, IReadOnlyList<TaskResponse> Tasks, TaskErrorResponse? Error);
public sealed record EditTaskRequest(string OperationId, string TaskId, string ExpectedVersion, string Title, string Notes, string? PlannedDate, string? PlannedTime, string? PlannedZoneId, string? DueDate, string? DueTime, string? DueZoneId, string Priority, int? EstimateMinutes, string Category);
public sealed record ChangeTaskStatusRequest(string OperationId, string TaskId, string ExpectedVersion, string Status, string? SubtaskChoice);
public sealed record ChangeAssigneeRequest(string OperationId, string TaskId, string ExpectedVersion, string MemberId, bool Assigned);
public sealed record SetAssignmentModeRequest(string OperationId, string TaskId, string ExpectedVersion, string Mode);
public sealed record AssigneeResponse(string MemberId, string? CompletedAtUtc);
public sealed record TaskAssignmentsResponse(bool Ok, string Mode, IReadOnlyList<AssigneeResponse> Assignees, TaskErrorResponse? Error);
public sealed record AssignmentSummariesRequest(IReadOnlyList<string> TaskIds);
public sealed record AssignmentSummaryDto(string TaskId, string SpaceId, IReadOnlyList<AssigneeResponse> Assignees);
public sealed record AssignmentSummariesResponse(bool Ok, IReadOnlyList<AssignmentSummaryDto> Tasks, TaskErrorResponse? Error);
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
public sealed class TaskRpcService(TaskCommands commands, GitHubSyncEngine? sync = null)
{
    [NeoRpcMethod("assignmentSummaries")]
    public ValueTask<AssignmentSummariesResponse> AssignmentSummariesAsync(AssignmentSummariesRequest request, CancellationToken ct) =>
        Rpc.Run("tasks.assignmentSummaries", async () => new AssignmentSummariesResponse(true,
            [.. (await commands.GetAssignmentSummariesAsync(request.TaskIds, ct)).Select(task => new AssignmentSummaryDto(task.TaskId, task.SpaceId,
                [.. task.Assignees.Select(a => new AssigneeResponse(a.MemberId, a.CompletedAtUtc is { } at ? StorageFormats.Instant(at) : null))]))], null),
            error => new AssignmentSummariesResponse(false, [], error));
    [NeoRpcMethod("assignments")]
    public ValueTask<TaskAssignmentsResponse> AssignmentsAsync(GetTaskRequest request, CancellationToken ct) =>
        Rpc.Run("tasks.assignments", async () =>
        {
            var assignments = await commands.GetAssignmentsAsync(request.TaskId, ct);
            return new TaskAssignmentsResponse(true, assignments.Mode.ToString(),
                [.. assignments.Assignees.Where(a => a.DeletedAtUtc is null).Select(a => new AssigneeResponse(a.MemberId, a.CompletedAtUtc is { } at ? StorageFormats.Instant(at) : null))], null);
        }, error => new TaskAssignmentsResponse(false, "Anyone", [], error));

    [NeoRpcMethod("changeAssignee")]
    public async ValueTask<TaskOperationResponse> ChangeAssigneeAsync(ChangeAssigneeRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("tasks.changeAssignee");
        return await MutateAsync(request.OperationId, () => commands.ChangeAssigneeAsync(new(request.OperationId, request.TaskId,
            Version(request.ExpectedVersion), request.MemberId, request.Assigned), ct));
    }

    [NeoRpcMethod("setAssignmentMode")]
    public async ValueTask<TaskOperationResponse> SetAssignmentModeAsync(SetAssignmentModeRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("tasks.setAssignmentMode");
        return await MutateAsync(request.OperationId, () => commands.SetAssignModeAsync(new(request.OperationId, request.TaskId,
            Version(request.ExpectedVersion), ParseEnum<TaskAssignMode>(request.Mode, "mode")), ct));
    }
    [NeoRpcMethod("listDeleted")]
    public ValueTask<ListTasksResponse> ListDeletedAsync(ListDeletedTasksRequest request, CancellationToken ct) =>
        Rpc.Run<ListTasksResponse>("tasks.listDeleted", async () => { return new(true, [.. (await commands.ListDeletedAsync(request.ContainerId, ct)).Select(ToResponse)], null); },
            error => new(false, [], error));
    [NeoRpcMethod("listInbox")]
    public ValueTask<ListTasksResponse> ListInboxAsync(ListTasksRequest request, CancellationToken cancellationToken) =>
        Rpc.Run<ListTasksResponse>("tasks.listInbox", async () => { return new(true, [.. (await commands.ListInboxAsync(cancellationToken)).Select(ToResponse)], null); },
            error => new(false, [], error));

    [NeoRpcMethod("edit")]
    public async ValueTask<TaskOperationResponse> EditAsync(EditTaskRequest request, CancellationToken cancellationToken)
    {
        using var rpcScope = ActivityScope.Rpc("tasks.edit");
        return await MutateAsync(request.OperationId, async () => await commands.UpdateDetailsAsync(new(
            request.OperationId, request.TaskId, Version(request.ExpectedVersion), request.Title, request.Notes,
            Date(request.PlannedDate, "plannedDate"), Time(request.PlannedTime, "plannedTime"), request.PlannedZoneId, Date(request.DueDate, "dueDate"), Time(request.DueTime, "dueTime"), request.DueZoneId,
            ParseEnum<TaskPriority>(request.Priority, "priority"), request.EstimateMinutes,
            ParseEnum<CapacityCategory>(request.Category, "category")), cancellationToken));
    }

    [NeoRpcMethod("setStatus")]
    public async ValueTask<TaskOperationResponse> SetStatusAsync(ChangeTaskStatusRequest request, CancellationToken cancellationToken)
    {
        using var rpcScope = ActivityScope.Rpc("tasks.setStatus");
        return await MutateAsync(request.OperationId, async () => await commands.SetStatusAsync(new(
            request.OperationId, request.TaskId, Version(request.ExpectedVersion), ParseEnum<TaskItemStatus>(request.Status, "status"),
            request.SubtaskChoice is null ? null : ParseEnum<OpenSubtaskChoice>(request.SubtaskChoice, "subtaskChoice")), cancellationToken));
    }

    [NeoRpcMethod("reorder")]
    public async ValueTask<TaskOperationResponse> ReorderAsync(ReorderTaskRequest request, CancellationToken cancellationToken)
    {
        using var rpcScope = ActivityScope.Rpc("tasks.reorder");
        return await MutateAsync(request.OperationId, async () => await commands.ReorderAsync(new(request.OperationId, request.TaskId, Version(request.ExpectedVersion), request.InsertionIndex), cancellationToken));
    }

    [NeoRpcMethod("delete")]
    public async ValueTask<TaskOperationResponse> DeleteAsync(DeleteTaskRequest request, CancellationToken cancellationToken)
    {
        using var rpcScope = ActivityScope.Rpc("tasks.delete");
        return await MutateAsync(request.OperationId, async () => await commands.DeleteAsync(new(request.OperationId, request.TaskId, Version(request.ExpectedVersion)), cancellationToken));
    }

    [NeoRpcMethod("restore")]
    public async ValueTask<TaskOperationResponse> RestoreAsync(DeleteTaskRequest request, CancellationToken cancellationToken)
    {
        using var rpcScope = ActivityScope.Rpc("tasks.restore");
        return await MutateAsync(request.OperationId, async () => await commands.RestoreAsync(new(request.OperationId, request.TaskId, Version(request.ExpectedVersion)), cancellationToken));
    }

    private async ValueTask<TaskOperationResponse> MutateAsync(string operationId, Func<Task<TaskMutationResult>> action)
    {
        try { var result = await action(); sync?.RequestTaskSync(result.Task.Id); return new(true, operationId, ToResponse(result.Task), null, result.Warning); }
        catch (AppException error) { return new(false, operationId, null, ToErrorResponse(error)); }
    }

    private static long Version(string value) => RpcParse.Version(value, message: "Invalid task version. Refresh and try again.");
    private static DateOnly? Date(string? value, string field) => RpcParse.OptionalDate(value, field);
    private static TimeOnly? Time(string? value, string field) => RpcParse.OptionalTime(value, field);
    private static T ParseEnum<T>(string value, string field) where T : struct, Enum => RpcParse.Enum<T>(value, field);
    [NeoRpcMethod("create")]
    public ValueTask<TaskOperationResponse> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken) =>
        Rpc.Run<TaskOperationResponse>("tasks.create", async () => {
            var result = await commands.CaptureAsync(
                new CaptureTaskCommand(request.OperationId, request.ContainerId ?? string.Empty, request.Title, request.Notes),
                cancellationToken);
            return new TaskOperationResponse(true, result.OperationId, ToResponse(result.Task), null);
        },
            error => new TaskOperationResponse(false, request.OperationId, null, error));

    [NeoRpcMethod("get")]
    public ValueTask<TaskGetResponse> GetAsync(GetTaskRequest request, CancellationToken cancellationToken) =>
        Rpc.Run<TaskGetResponse>("tasks.get", async () => {
            var task = await commands.GetAsync(request.TaskId, cancellationToken);
            return task is null
                ? new TaskGetResponse(false, null, new TaskErrorResponse(
                    "notFound",
                    "No task with that ID exists on this computer.",
                    [],
                    Guid.NewGuid().ToString("N")))
                : new TaskGetResponse(true, ToResponse(task), null);
        },
            error => new TaskGetResponse(false, null, error));

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
