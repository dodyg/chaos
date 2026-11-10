using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record TemplateListRequest();
public sealed record TemplatePartResponse(string Title, string Notes);
public sealed record TemplatePayloadResponse(string Title, string Notes, string Priority, int? EstimateMinutes, string Category, int? PlannedDayOffset, string? PlannedTime, string? PlannedZoneId, int? DueDayOffset, string? DueTime, string? DueZoneId, IReadOnlyList<TemplatePartResponse> Subtasks);
public sealed record TemplateResponse(string Id, string Name, string Version, TemplatePayloadResponse Payload);
public sealed record TemplateListResponse(bool Ok, IReadOnlyList<TemplateResponse> Templates, TaskErrorResponse? Error);
public sealed record TemplateSaveRequest(string OperationId, string? TemplateId, string? ExpectedVersion, string Name, TemplatePayloadResponse Payload);
public sealed record TemplateDeleteRequest(string OperationId, string TemplateId, string ExpectedVersion);
public sealed record TemplateInstantiateRequest(string OperationId, string TemplateId, string? ContainerId);
public sealed record TemplateOperationResponse(bool Ok, TemplateResponse? Template, TaskErrorResponse? Error);

[NeoRpcService("templates", Version = 1)]
public sealed class TemplateRpcService(TemplateCommands commands)
{
    [NeoRpcMethod("list")]
    public async ValueTask<TemplateListResponse> ListAsync(TemplateListRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await commands.ListAsync(null, ct)).Select(Map)], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }
    [NeoRpcMethod("listDeleted")]
    public async ValueTask<TemplateListResponse> ListDeletedAsync(TemplateListRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await commands.ListDeletedAsync(ct)).Select(Map)], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("save")]
    public async ValueTask<TemplateOperationResponse> SaveAsync(TemplateSaveRequest request, CancellationToken ct)
    {
        try
        {
            var payload = ToPayload(request.Payload);
            var result = request.TemplateId is null
                ? await commands.CreateAsync(new(request.OperationId, request.Name, payload, null), ct)
                : await commands.UpdateAsync(new(request.OperationId, request.TemplateId, Version(request.ExpectedVersion), request.Name, payload), ct);
            return new(true, Map(result.Template), null);
        }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("delete")]
    public async ValueTask<TemplateOperationResponse> DeleteAsync(TemplateDeleteRequest request, CancellationToken ct)
    {
        try { return new(true, Map((await commands.DeleteAsync(new(request.OperationId, request.TemplateId, Version(request.ExpectedVersion)), ct)).Template), null); }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }
    [NeoRpcMethod("restore")]
    public async ValueTask<TemplateOperationResponse> RestoreAsync(TemplateDeleteRequest request, CancellationToken ct)
    {
        try { return new(true, Map((await commands.RestoreAsync(new(request.OperationId, request.TemplateId, Version(request.ExpectedVersion)), ct)).Template), null); }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("instantiate")]
    public async ValueTask<TaskOperationResponse> InstantiateAsync(TemplateInstantiateRequest request, CancellationToken ct)
    {
        try { var result = await commands.InstantiateAsync(new(request.OperationId, request.TemplateId, request.ContainerId), ct); return new(true, request.OperationId, TaskRpcService.ToResponse(result.Task), null); }
        catch (AppException error) { return new(false, request.OperationId, null, TaskRpcService.ToErrorResponse(error)); }
    }

    private static TemplatePayload ToPayload(TemplatePayloadResponse value) => new()
    {
        Title = value.Title, Notes = value.Notes, Priority = Parse<TaskPriority>(value.Priority, "priority"), EstimateMinutes = value.EstimateMinutes,
        CapacityCategory = Parse<CapacityCategory>(value.Category, "category"), PlannedDayOffset = value.PlannedDayOffset,
        PlannedTime = Time(value.PlannedTime, "plannedTime"), PlannedZoneId = value.PlannedZoneId,
        DueDayOffset = value.DueDayOffset, DueTime = Time(value.DueTime, "dueTime"), DueZoneId = value.DueZoneId,
        Subtasks = [.. value.Subtasks.Select((part, index) => new TemplateSubtask { Title = part.Title, Notes = part.Notes, Position = (long)index * 1024 })],
    };
    private static TemplateResponse Map(TemplateRecord row)
    {
        var payload = StoredJsonFormats.ReadSnapshot<TemplatePayload>(row.PayloadJson) ?? throw new AppException(AppErrorKind.StorageUnavailable, "The template could not be read.");
        return new(row.Id, row.Name, row.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), new(payload.Title, payload.Notes, payload.Priority.ToString(), payload.EstimateMinutes, payload.CapacityCategory.ToString(), payload.PlannedDayOffset, payload.PlannedTime?.ToString("HH:mm:ss"), payload.PlannedZoneId, payload.DueDayOffset, payload.DueTime?.ToString("HH:mm:ss"), payload.DueZoneId, [.. payload.Subtasks.OrderBy(x => x.Position).Select(x => new TemplatePartResponse(x.Title, x.Notes))]));
    }
    private static long Version(string? value) => long.TryParse(value, out var result) && result > 0 ? result : throw AppException.Validation("expectedVersion", "Invalid version. Refresh and try again.");
    private static TimeOnly? Time(string? value, string field) => string.IsNullOrEmpty(value) ? null : TimeOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var time) ? time : throw AppException.Validation(field, "Enter a valid time.");
    private static T Parse<T>(string value, string field) where T : struct, Enum => Enum.TryParse<T>(value, out var result) && Enum.IsDefined(result) ? result : throw AppException.Validation(field, "Choose a valid value.");
}
