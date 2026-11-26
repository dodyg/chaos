using Chaos.Application.Abstractions;
using Chaos.Application.Agents;
using Chaos.Application.Capture;
using Chaos.Application.Contracts;
using Chaos.Infrastructure.Sync;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record ProposalDto(string Id, string Version, string? PadItemId, string AgentId, string AgentName, string Title, string Notes, string? QuickAddText, string CreatedAtUtc);
public sealed record ProposalListRequest(string? PadItemId);
public sealed record ProposalListResponse(bool Ok, IReadOnlyList<ProposalDto> Items, TaskErrorResponse? Error);
public sealed record ProposalAcceptRequest(string OperationId, string ProposalId, string ExpectedVersion, string? ContainerId);
public sealed record ProposalDismissRequest(string OperationId, string ProposalId, string ExpectedVersion);
public sealed record ProposalAcceptResponse(bool Ok, TaskResponse? Task, bool Replayed, TaskErrorResponse? Error);
public sealed record ProposalDismissResponse(bool Ok, bool Replayed, TaskErrorResponse? Error);

/// <summary>
/// Suggestions an agent made from the pad (PLAN3 12.6): the pad lists them as "Suggested by Build bot", and only a person's
/// Accept makes a task (the quick-add text is parsed here, as the quick add bar does) or Dismiss closes them.
/// </summary>
[NeoRpcService("proposals", Version = 1)]
public sealed class ProposalRpcService(ProposalCommands proposals, CaptureCommands capture, SyncEngine? sync)
{
    [NeoRpcMethod("list")]
    public ValueTask<ProposalListResponse> ListAsync(ProposalListRequest request, CancellationToken ct) =>
        Rpc.Run<ProposalListResponse>("proposals.list", async () =>
            new(true, [.. (await proposals.ListPendingAsync(string.IsNullOrWhiteSpace(request.PadItemId) ? null : request.PadItemId, ct)).Select(Map)], null),
            error => new(false, [], error));

    [NeoRpcMethod("accept")]
    public ValueTask<ProposalAcceptResponse> AcceptAsync(ProposalAcceptRequest request, CancellationToken ct) =>
        Rpc.Run<ProposalAcceptResponse>("proposals.accept", async () =>
        {
            var version = ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion");
            var container = string.IsNullOrWhiteSpace(request.ContainerId) ? null : request.ContainerId;
            var draft = await DraftAsync(request.ProposalId, container, ct);
            var result = await proposals.AcceptAsync(new AcceptProposalCommand(request.OperationId, request.ProposalId, version, container, draft), ct);
            sync?.Wake();
            return new(true, TaskRpcService.ToResponse(result.Task!), result.Replayed, null);
        }, error => new(false, null, false, error));

    [NeoRpcMethod("dismiss")]
    public ValueTask<ProposalDismissResponse> DismissAsync(ProposalDismissRequest request, CancellationToken ct) =>
        Rpc.Run<ProposalDismissResponse>("proposals.dismiss", async () =>
        {
            var result = await proposals.DismissAsync(new DismissProposalCommand(request.OperationId, request.ProposalId,
                ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion")), ct);
            sync?.Wake();
            return new(true, result.Replayed, null);
        }, error => new(false, false, error));

    /// <summary>The quick-add text, when the agent gave one, parsed as the bar does; otherwise the title and notes as they are.</summary>
    private async Task<CaptureTaskDraft?> DraftAsync(string proposalId, string? container, CancellationToken ct)
    {
        var item = (await proposals.ListPendingAsync(null, ct)).FirstOrDefault(row => string.Equals(row.Proposal.Id, proposalId, StringComparison.OrdinalIgnoreCase));
        if (item?.Proposal.QuickAddText is not { Length: > 0 } text)
        {
            return null;
        }

        var preview = await capture.PreviewAsync(text, [], container, ct);
        if (preview.Items.Count != 1 || preview.IsBatch || preview.Errors.Count > 0)
        {
            return null; // not one clean task: fall back to the plain title
        }

        var parsed = preview.Items[0];
        var dto = CaptureRpcService.MapDraft(parsed.Parse.Draft, parsed.Subtasks) with { Notes = item.Proposal.NotesMarkdown };
        return CaptureRpcService.ToDraft(dto, "draft");
    }

    private static ProposalDto Map(ProposalItem item) => new(item.Proposal.Id, item.Proposal.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
        item.Proposal.SourcePadItemId, item.Proposal.AgentId, item.AgentName, item.Proposal.Title, item.Proposal.NotesMarkdown, item.Proposal.QuickAddText,
        StorageFormats.Instant(item.Proposal.CreatedAtUtc));
}
