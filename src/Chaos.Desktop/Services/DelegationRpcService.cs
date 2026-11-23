using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Application.Contracts;
using Chaos.Application.Reminders;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Sync;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

/// <summary>One delegation as the renderer shows it: the parties as member IDs (names come from the space's members), the brief and the state name.</summary>
public sealed record DelegationDto(string Id, string TaskId, string FromMemberId, string ToMemberId, string Message, string DoneDefinition, string? DueDate,
    bool ReviewRequired, string State, string StateChangedAtUtc, string? Response, string Version);
public sealed record NudgeDto(string Id, string Text, string SentAtUtc);
public sealed record DelegationItemDto(DelegationDto Delegation, TaskResponse Task, string ContainerName, string FromName, string ToName, FollowUpDto? FollowUp,
    string? FollowUpDueAtUtc, bool FollowUpOverdue, IReadOnlyList<NudgeDto> Nudges);
public sealed record DelegationGetRequest(string TaskId);
public sealed record DelegationGetResponse(bool Ok, TaskResponse? Task, IReadOnlyList<DelegationItemDto> Live, IReadOnlyList<DelegationItemDto> History, TaskErrorResponse? Error);
/// <param name="Side">From (delegated by me) or To (waiting on me).</param>
public sealed record DelegationListRequest(string Side);
public sealed record DelegationListResponse(bool Ok, IReadOnlyList<DelegationItemDto> Items, TaskErrorResponse? Error);
public sealed record DelegationTemplateRequest(string DelegationId);
public sealed record DelegationTemplateResponse(bool Ok, string? Text, TaskErrorResponse? Error);
public sealed record DelegateRequest(string OperationId, string TaskId, string ExpectedVersion, string ToMemberId, string? Message, string? DoneDefinition,
    string? DueDate, bool ReviewRequired, FollowUpDto? FollowUp);
/// <param name="Response">Accept, Question or Decline; Text is the question or the reason.</param>
public sealed record DelegationRespondRequest(string OperationId, string DelegationId, string ExpectedVersion, string Response, string? Text);
public sealed record DelegationDoneRequest(string OperationId, string DelegationId, string ExpectedVersion, string? Summary, string? OpenSubtaskChoice);
public sealed record DelegationActionRequest(string OperationId, string DelegationId, string ExpectedVersion);
public sealed record DelegationNudgeRequest(string OperationId, string DelegationId, string ExpectedVersion, string? Text);
public sealed record DelegationMutationResponse(bool Ok, DelegationDto? Delegation, TaskResponse? Task, bool Replayed, TaskErrorResponse? Error);

/// <summary>
/// Delegating tasks to members, answering, review, taking work back and nudges (PLAN3 9.11).
/// Every write is a recorded operation of the local person; the service checks each move again.
/// </summary>
[NeoRpcService("delegation", Version = 1)]
public sealed class DelegationRpcService(DelegationCommands commands)
{
    [NeoRpcMethod("get")]
    public ValueTask<DelegationGetResponse> GetAsync(DelegationGetRequest request, CancellationToken ct) =>
        Rpc.Run<DelegationGetResponse>("delegation.get", async () =>
        {
            var view = await commands.GetForTaskAsync(request.TaskId, ct);
            return new(true, TaskRpcService.ToResponse(view.Task), [.. view.Live.Select(Map)], [.. view.History.Select(Map)], null);
        },
            error => new(false, null, [], [], error));

    [NeoRpcMethod("list")]
    public ValueTask<DelegationListResponse> ListAsync(DelegationListRequest request, CancellationToken ct) =>
        Rpc.Run<DelegationListResponse>("delegation.list", async () =>
        {
            var side = string.Equals(request.Side, "To", StringComparison.OrdinalIgnoreCase) ? DelegationSide.To : DelegationSide.From;
            return new(true, [.. (await commands.ListAsync(side, ct)).Select(Map)], null);
        },
            error => new(false, [], error));

    [NeoRpcMethod("nudgeTemplate")]
    public ValueTask<DelegationTemplateResponse> NudgeTemplateAsync(DelegationTemplateRequest request, CancellationToken ct) =>
        Rpc.Run<DelegationTemplateResponse>("delegation.nudgeTemplate", async () => new(true, await commands.NudgeTemplateAsync(request.DelegationId, ct), null),
            error => new(false, null, error));

    [NeoRpcMethod("delegate")]
    public ValueTask<DelegationMutationResponse> DelegateAsync(DelegateRequest request, CancellationToken ct) =>
        Rpc.Run<DelegationMutationResponse>("delegation.delegate", async () => Unpack(await commands.DelegateAsync(new DelegateCommand(
            request.OperationId, request.TaskId, ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion"), request.ToMemberId,
            request.Message, request.DoneDefinition, Date(request.DueDate), request.ReviewRequired,
            request.FollowUp is null ? null : Spec(request.FollowUp)), ct)),
            error => new(false, null, null, false, error));

    [NeoRpcMethod("respond")]
    public ValueTask<DelegationMutationResponse> RespondAsync(DelegationRespondRequest request, CancellationToken ct) =>
        Rpc.Run<DelegationMutationResponse>("delegation.respond", async () => Unpack(await commands.RespondAsync(new RespondDelegationCommand(
            request.OperationId, request.DelegationId, ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion"),
            ReminderRpcService.Parse<DelegationResponse>(request.Response, "response"), request.Text), ct)),
            error => new(false, null, null, false, error));

    [NeoRpcMethod("done")]
    public ValueTask<DelegationMutationResponse> DoneAsync(DelegationDoneRequest request, CancellationToken ct) =>
        Rpc.Run<DelegationMutationResponse>("delegation.done", async () => Unpack(await commands.DoneAsync(new DoneDelegationCommand(
            request.OperationId, request.DelegationId, ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion"), request.Summary,
            request.OpenSubtaskChoice is null ? null : ReminderRpcService.Parse<OpenSubtaskChoice>(request.OpenSubtaskChoice, "openSubtaskChoice")), ct)),
            error => new(false, null, null, false, error));

    [NeoRpcMethod("close")]
    public ValueTask<DelegationMutationResponse> CloseAsync(DelegationActionRequest request, CancellationToken ct) =>
        Rpc.Run<DelegationMutationResponse>("delegation.close", async () => Unpack(await commands.CloseAsync(Close(request), ct)),
            error => new(false, null, null, false, error));

    [NeoRpcMethod("reopen")]
    public ValueTask<DelegationMutationResponse> ReopenAsync(DelegationActionRequest request, CancellationToken ct) =>
        Rpc.Run<DelegationMutationResponse>("delegation.reopen", async () => Unpack(await commands.ReopenAsync(Reopen(request), ct)),
            error => new(false, null, null, false, error));

    [NeoRpcMethod("withdraw")]
    public ValueTask<DelegationMutationResponse> WithdrawAsync(DelegationActionRequest request, CancellationToken ct) =>
        Rpc.Run<DelegationMutationResponse>("delegation.withdraw", async () => Unpack(await commands.WithdrawAsync(Withdraw(request), ct)),
            error => new(false, null, null, false, error));

    [NeoRpcMethod("nudge")]
    public ValueTask<DelegationMutationResponse> NudgeAsync(DelegationNudgeRequest request, CancellationToken ct) =>
        Rpc.Run<DelegationMutationResponse>("delegation.nudge", async () => Unpack(await commands.NudgeAsync(new NudgeCommand(
            request.OperationId, request.DelegationId, ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion"), request.Text), ct)),
            error => new(false, null, null, false, error));

    private static CloseDelegationCommand Close(DelegationActionRequest request) =>
        new(request.OperationId, request.DelegationId, ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion"));

    private static ReopenDelegationCommand Reopen(DelegationActionRequest request) =>
        new(request.OperationId, request.DelegationId, ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion"));

    private static WithdrawDelegationCommand Withdraw(DelegationActionRequest request) =>
        new(request.OperationId, request.DelegationId, ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion"));

    private static DelegationMutationResponse Unpack(DelegationMutationResult result) =>
        new(true, Map(result.Delegation), TaskRpcService.ToResponse(result.Task), result.Replayed, null);

    internal static DelegationDto Map(TaskDelegationRecord delegation) => new(delegation.Id, delegation.TaskId, delegation.FromMemberId, delegation.ToMemberId,
        delegation.Message, delegation.DoneDefinition, delegation.DueDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), delegation.ReviewRequired,
        delegation.State.ToString(), StorageFormats.Instant(delegation.StateChangedAtUtc), delegation.Response,
        delegation.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static DelegationItemDto Map(DelegationItem item) => new(Map(item.Delegation), TaskRpcService.ToResponse(item.Task), item.ContainerName, item.FromName, item.ToName,
        item.FollowUp is { } followUp
            ? new FollowUpDto(followUp.Kind.ToString(), followUp.FixedAtUtc is { } at ? StorageFormats.Instant(at) : null, followUp.Amount, followUp.Unit?.ToString())
            : null,
        item.FollowUpDueAtUtc is { } due ? StorageFormats.Instant(due) : null, item.FollowUpOverdue,
        [.. item.Nudges.Select(nudge => new NudgeDto(nudge.Id, nudge.Text, StorageFormats.Instant(nudge.SentAtUtc)))]);

    private static DateOnly? Date(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
        : DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed) ? parsed
        : throw AppException.Validation("dueDate", "Enter the date as yyyy-MM-dd.");

    private static FollowUpSpec Spec(FollowUpDto dto) => new(
        ReminderRpcService.Parse<Chaos.Domain.Reminders.FollowUpKind>(dto.Kind, "followUp.kind"),
        ReminderRpcService.Instant(dto.FixedAtUtc, "followUp.fixedAtUtc"),
        dto.Amount,
        dto.Unit is null ? null : ReminderRpcService.Parse<Chaos.Domain.Reminders.DurationUnit>(dto.Unit, "followUp.unit"));
}
