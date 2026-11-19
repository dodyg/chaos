using Chaos.Application.Activity;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record TemplateListRequest();
public sealed record TemplatePartResponse(string Title, string Notes);
public sealed record TemplateReminderOffsetDto(string Anchor, int OffsetMinutes, int? InsistentMinutes);
public sealed record TemplatePayloadResponse(string Title, string Notes, string Priority, int? EstimateMinutes, string Category, int? PlannedDayOffset, string? PlannedTime, string? PlannedZoneId, int? DueDayOffset, string? DueTime, string? DueZoneId, IReadOnlyList<TemplatePartResponse> Subtasks, RecurrenceRuleDto? RecurrenceDefault = null, IReadOnlyList<TemplateReminderOffsetDto>? ReminderOffsets = null);
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
    public ValueTask<TemplateListResponse> ListAsync(TemplateListRequest request, CancellationToken ct) =>
        Rpc.Run("templates.list", async () => new TemplateListResponse(true, [.. (await commands.ListAsync(null, ct)).Select(Map)], null),
            error => new(false, [], error));

    [NeoRpcMethod("listDeleted")]
    public ValueTask<TemplateListResponse> ListDeletedAsync(TemplateListRequest request, CancellationToken ct) =>
        Rpc.Run("templates.listDeleted", async () => new TemplateListResponse(true, [.. (await commands.ListDeletedAsync(ct)).Select(Map)], null),
            error => new(false, [], error));

    [NeoRpcMethod("save")]
    public ValueTask<TemplateOperationResponse> SaveAsync(TemplateSaveRequest request, CancellationToken ct) =>
        Rpc.Run("templates.save", async () =>
        {
            var payload = ToPayload(request.Payload);
            var result = request.TemplateId is null
                ? await commands.CreateAsync(new(request.OperationId, request.Name, payload, null), ct)
                : await commands.UpdateAsync(new(request.OperationId, request.TemplateId, RpcParse.Version(request.ExpectedVersion), request.Name, payload), ct);
            return new TemplateOperationResponse(true, Map(result.Template), null);
        }, error => new(false, null, error));

    [NeoRpcMethod("delete")]
    public ValueTask<TemplateOperationResponse> DeleteAsync(TemplateDeleteRequest request, CancellationToken ct) =>
        Rpc.Run("templates.delete", async () => new TemplateOperationResponse(true, Map((await commands.DeleteAsync(new(request.OperationId, request.TemplateId, RpcParse.Version(request.ExpectedVersion)), ct)).Template), null),
            error => new(false, null, error));

    [NeoRpcMethod("restore")]
    public ValueTask<TemplateOperationResponse> RestoreAsync(TemplateDeleteRequest request, CancellationToken ct) =>
        Rpc.Run("templates.restore", async () => new TemplateOperationResponse(true, Map((await commands.RestoreAsync(new(request.OperationId, request.TemplateId, RpcParse.Version(request.ExpectedVersion)), ct)).Template), null),
            error => new(false, null, error));

    [NeoRpcMethod("instantiate")]
    public ValueTask<TaskOperationResponse> InstantiateAsync(TemplateInstantiateRequest request, CancellationToken ct) =>
        Rpc.Run("templates.instantiate", async () =>
        {
            var result = await commands.InstantiateAsync(new(request.OperationId, request.TemplateId, request.ContainerId), ct);
            return new TaskOperationResponse(true, request.OperationId, TaskRpcService.ToResponse(result.Task), null);
        }, error => new(false, request.OperationId, null, error));

    private static TemplatePayload ToPayload(TemplatePayloadResponse value) => new()
    {
        Title = value.Title, Notes = value.Notes, Priority = RpcParse.Enum<TaskPriority>(value.Priority, "priority"), EstimateMinutes = value.EstimateMinutes,
        CapacityCategory = RpcParse.Enum<CapacityCategory>(value.Category, "category"), PlannedDayOffset = value.PlannedDayOffset,
        PlannedTime = RpcParse.OptionalTime(value.PlannedTime, "plannedTime"), PlannedZoneId = value.PlannedZoneId,
        DueDayOffset = value.DueDayOffset, DueTime = RpcParse.OptionalTime(value.DueTime, "dueTime"), DueZoneId = value.DueZoneId,
        Subtasks = [.. value.Subtasks.Select((part, index) => new TemplateSubtask { Title = part.Title, Notes = part.Notes, Position = (long)index * 1024 })],
        RecurrenceDefault = value.RecurrenceDefault is null ? null : RecurrenceRpcService.Parse(value.RecurrenceDefault),
        ReminderOffsets = [.. (value.ReminderOffsets ?? []).Select(item => new TemplateReminderOffset(RpcParse.Enum<ReminderAnchor>(item.Anchor, "reminderOffsets.anchor"), item.OffsetMinutes, item.InsistentMinutes))],
    };
    private static TemplateResponse Map(TemplateRecord row)
    {
        var payload = StoredJsonFormats.ReadSnapshot<TemplatePayload>(row.PayloadJson) ?? throw new AppException(AppErrorKind.StorageUnavailable, "The template could not be read.");
        return new(row.Id, row.Name, row.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), new(payload.Title, payload.Notes, payload.Priority.ToString(), payload.EstimateMinutes, payload.CapacityCategory.ToString(), payload.PlannedDayOffset, payload.PlannedTime?.ToString("HH:mm:ss"), payload.PlannedZoneId, payload.DueDayOffset, payload.DueTime?.ToString("HH:mm:ss"), payload.DueZoneId, [.. payload.Subtasks.OrderBy(x => x.Position).Select(x => new TemplatePartResponse(x.Title, x.Notes))], payload.RecurrenceDefault is null ? null : RecurrenceRpcService.Map(payload.RecurrenceDefault), [.. payload.ReminderOffsets.Select(item => new TemplateReminderOffsetDto(item.Anchor.ToString(), item.OffsetMinutes, item.InsistentMinutes))]));
    }
}
