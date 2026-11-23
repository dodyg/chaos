using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Spaces;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

/// <summary>One standup as the Overview shows it: the row, the author's name in the space, and whether the local person wrote it.</summary>
public sealed record SpaceUpdateDto(string Id, string SpaceId, string AuthorMemberId, string ForDate, string RawMarkdown, string? EditedAtUtc,
    string CreatedAtUtc, string Version, string AuthorName, bool IsMine);
public sealed record SpaceUpdateListRequest(string SpaceId);
public sealed record SpaceUpdateListResponse(bool Ok, IReadOnlyList<SpaceUpdateDto> Updates, TaskErrorResponse? Error);
public sealed record SpaceUpdatePostRequest(string OperationId, string SpaceId, string ForDate, string Markdown);
public sealed record SpaceUpdateEditRequest(string OperationId, string UpdateId, string ExpectedVersion, string Markdown);
public sealed record SpaceUpdateDeleteRequest(string OperationId, string UpdateId, string ExpectedVersion);
public sealed record SpaceUpdateMutationResponse(bool Ok, SpaceUpdateDto? Update, bool Replayed, TaskErrorResponse? Error);

/// <summary>
/// Standups (PLAN3 9.12): the Overview list of a shared space, and posting, editing and deleting. Every write
/// is the local person's own recorded operation; the service checks each rule again on push.
/// </summary>
[NeoRpcService("spaceUpdates", Version = 1)]
public sealed class SpaceUpdateRpcService(SpaceUpdateCommands commands)
{
    [NeoRpcMethod("list")]
    public ValueTask<SpaceUpdateListResponse> ListAsync(SpaceUpdateListRequest request, CancellationToken ct) =>
        Rpc.Run<SpaceUpdateListResponse>("spaceUpdates.list", async () =>
        {
            var items = await commands.ListAsync(request.SpaceId, ct);
            return new(true, [.. items.Select(item => Map(item.Update, item.AuthorName, item.IsMine))], null);
        },
            error => new(false, [], error));

    [NeoRpcMethod("post")]
    public ValueTask<SpaceUpdateMutationResponse> PostAsync(SpaceUpdatePostRequest request, CancellationToken ct) =>
        Rpc.Run<SpaceUpdateMutationResponse>("spaceUpdates.post", async () => Unpack(await commands.PostAsync(new PostSpaceUpdateCommand(
            request.OperationId, request.SpaceId, Date(request.ForDate, "forDate"), request.Markdown), ct)),
            error => new(false, null, false, error));

    [NeoRpcMethod("edit")]
    public ValueTask<SpaceUpdateMutationResponse> EditAsync(SpaceUpdateEditRequest request, CancellationToken ct) =>
        Rpc.Run<SpaceUpdateMutationResponse>("spaceUpdates.edit", async () => Unpack(await commands.EditAsync(new EditSpaceUpdateCommand(
            request.OperationId, request.UpdateId, ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion"), request.Markdown), ct)),
            error => new(false, null, false, error));

    [NeoRpcMethod("delete")]
    public ValueTask<SpaceUpdateMutationResponse> DeleteAsync(SpaceUpdateDeleteRequest request, CancellationToken ct) =>
        Rpc.Run<SpaceUpdateMutationResponse>("spaceUpdates.delete", async () => Unpack(await commands.DeleteAsync(new DeleteSpaceUpdateCommand(
            request.OperationId, request.UpdateId, ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion")), ct)),
            error => new(false, null, false, error));

    private static SpaceUpdateMutationResponse Unpack(SpaceUpdateMutationResult result) =>
        new(true, Map(result.Item.Update, result.Item.AuthorName, result.Item.IsMine), result.Replayed, null);

    private static SpaceUpdateDto Map(SpaceUpdateRecord row, string authorName, bool isMine) => new(row.Id, row.SpaceId, row.AuthorMemberId,
        StorageFormats.Date(row.ForDate), row.RawMarkdown, row.EditedAtUtc is { } edited ? StorageFormats.Instant(edited) : null,
        StorageFormats.Instant(row.CreatedAtUtc), row.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), authorName, isMine);

    private static DateOnly Date(string? value, string field) =>
        !string.IsNullOrWhiteSpace(value) && DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
            ? date
            : throw AppException.Validation(field, "Enter the date as yyyy-MM-dd.");
}
