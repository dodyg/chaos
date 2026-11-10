using Chaos.Application.Abstractions;
using Chaos.Application.Reminders;
using Chaos.Domain.Reminders;
using Chaos.Domain.Tasks;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

/// <summary>Kind is Fixed (FixedAtUtc), AfterWaiting or BeforeDue (Amount + Unit), or AfterInactivity (Amount days).</summary>
public sealed record FollowUpDto(string Kind, string? FixedAtUtc, int? Amount, string? Unit);
public sealed record WaitingRequest(string TaskId);
public sealed record WaitingResponse(
    bool Ok,
    TaskResponse? Task,
    string? Person,
    string? StartedAtUtc,
    FollowUpDto? FollowUp,
    string? FollowUpDueAtUtc,
    string? FollowUpSnoozedUntilUtc,
    string FollowUpSuspension,
    string? LastActivityAtUtc,
    bool Replayed,
    TaskErrorResponse? Error);
public sealed record WaitingSetRequest(string OperationId, string TaskId, string ExpectedVersion, string Person, FollowUpDto? FollowUp);
public sealed record WaitingClearRequest(string OperationId, string TaskId, string ExpectedVersion, string? NextStatus);
public sealed record WaitingLogRequest(string OperationId, string TaskId, string Note);
public sealed record FollowUpSnoozeRequest(string OperationId, string TaskId, string Choice, string? UntilUtc);
public sealed record FollowUpDueRequest(string? Date);
public sealed record FollowUpItemResponse(TaskResponse Task, string ContainerName, string? Person, string DueAtUtc, bool Overdue, string Kind);
public sealed record FollowUpDueResponse(bool Ok, IReadOnlyList<FollowUpItemResponse> Items, TaskErrorResponse? Error);

/// <summary>
/// Waiting on outside people and private follow-ups (PLAN §11, WP10). Nothing here sends a
/// message, looks up a contact or touches the network.
/// </summary>
[NeoRpcService("waiting", Version = 1)]
public sealed class WaitingRpcService(WaitingCommands waiting)
{
    [NeoRpcMethod("get")]
    public ValueTask<WaitingResponse> GetAsync(WaitingRequest request, CancellationToken ct) =>
        Run(async () => (await waiting.GetAsync(request.TaskId, ct), false));

    [NeoRpcMethod("set")]
    public ValueTask<WaitingResponse> SetAsync(WaitingSetRequest request, CancellationToken ct) =>
        Run(async () => Unpack(await waiting.SetAsync(new SetWaitingCommand(request.OperationId, request.TaskId, ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion"), request.Person,
            request.FollowUp is null ? null : Spec(request.FollowUp)), ct)));

    [NeoRpcMethod("clear")]
    public ValueTask<WaitingResponse> ClearAsync(WaitingClearRequest request, CancellationToken ct) =>
        Run(async () => Unpack(await waiting.ClearAsync(new ClearWaitingCommand(request.OperationId, request.TaskId, ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion"),
            request.NextStatus is null ? null : ReminderRpcService.Parse<TaskItemStatus>(request.NextStatus, "nextStatus")), ct)));

    [NeoRpcMethod("log")]
    public ValueTask<WaitingResponse> LogAsync(WaitingLogRequest request, CancellationToken ct) =>
        Run(async () => Unpack(await waiting.LogAsync(new LogFollowUpCommand(request.OperationId, request.TaskId, request.Note), ct)));

    [NeoRpcMethod("snooze")]
    public ValueTask<WaitingResponse> SnoozeAsync(FollowUpSnoozeRequest request, CancellationToken ct) =>
        Run(async () => Unpack(await waiting.SnoozeAsync(new SnoozeFollowUpCommand(request.OperationId, request.TaskId,
            ReminderRpcService.Parse<SnoozeChoice>(request.Choice, "choice"), ReminderRpcService.Instant(request.UntilUtc, "untilUtc")), ct)));

    /// <summary>Follow-ups due by the end of a local date (today when null): My Day's follow-up items.</summary>
    [NeoRpcMethod("due")]
    public async ValueTask<FollowUpDueResponse> DueAsync(FollowUpDueRequest request, CancellationToken ct)
    {
        try
        {
            DateOnly? date = string.IsNullOrWhiteSpace(request.Date) ? null
                : DateOnly.TryParseExact(request.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed) ? parsed
                : throw AppException.Validation("date", "Enter a valid date.");
            var items = await waiting.DueAsync(date, ct);
            return new(true, [.. items.Select(item => new FollowUpItemResponse(TaskRpcService.ToResponse(item.Task), item.ContainerName, item.Person,
                StorageFormats.Instant(item.DueAtUtc), item.Overdue, item.FollowUp.Kind.ToString()))], null);
        }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    private static (WaitingView View, bool Replayed) Unpack(WaitingMutationResult result) => (result.View, result.Replayed);

    private static async ValueTask<WaitingResponse> Run(Func<Task<(WaitingView View, bool Replayed)>> run)
    {
        try
        {
            var (view, replayed) = await run();
            return new(
                true,
                TaskRpcService.ToResponse(view.Task),
                view.Waiting?.Person,
                view.Waiting is { } waiting ? StorageFormats.Instant(waiting.StartedAtUtc) : null,
                view.FollowUp is { } followUp
                    ? new FollowUpDto(followUp.Kind.ToString(), followUp.FixedAtUtc is { } at ? StorageFormats.Instant(at) : null, followUp.Amount, followUp.Unit?.ToString())
                    : null,
                view.FollowUpDueAtUtc is { } due ? StorageFormats.Instant(due) : null,
                view.FollowUp?.SnoozedUntilUtc is { } snoozed ? StorageFormats.Instant(snoozed) : null,
                view.FollowUpSuspension.ToString(),
                view.LastActivityAtUtc is { } activity ? StorageFormats.Instant(activity) : null,
                replayed,
                null);
        }
        catch (AppException error)
        {
            return new(false, null, null, null, null, null, null, nameof(ReminderSuspension.None), null, false, TaskRpcService.ToErrorResponse(error));
        }
    }

    private static FollowUpSpec Spec(FollowUpDto dto) => new(
        ReminderRpcService.Parse<FollowUpKind>(dto.Kind, "followUp.kind"),
        ReminderRpcService.Instant(dto.FixedAtUtc, "followUp.fixedAtUtc"),
        dto.Amount,
        dto.Unit is null ? null : ReminderRpcService.Parse<DurationUnit>(dto.Unit, "followUp.unit"));
}
