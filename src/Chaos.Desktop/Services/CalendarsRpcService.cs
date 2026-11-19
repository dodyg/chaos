using Chaos.Application.Activity;
using Chaos.Application.Abstractions;
using Chaos.Application.Calendar;
using Chaos.Application.Contracts;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

/// <summary>A calendar source. The feed address is never part of it. Instants are ISO 8601 UTC.</summary>
public sealed record CalendarSourceDto(
    string Id,
    string Provider,
    string Name,
    string Color,
    bool CountsAsBusy,
    bool Enabled,
    string? OwnerEmail,
    string Version,
    string? LastAttemptUtc,
    string? LastSuccessUtc,
    string? LastError);
public sealed record CalendarsListRequest();
public sealed record CalendarsListResponse(bool Ok, IReadOnlyList<CalendarSourceDto> Sources, TaskErrorResponse? Error);
public sealed record CalendarAddFeedRequest(string Address, string Name, string Color, bool CountsAsBusy, string? OwnerEmail = null);
/// <summary>RefreshError is set when the calendar was added but the first fetch failed; the source still exists.</summary>
public sealed record CalendarSourceResponse(bool Ok, CalendarSourceDto? Source, string? RefreshError, TaskErrorResponse? Error);
public sealed record CalendarUpdateRequest(string SourceId, string ExpectedVersion, string Name, string Color, bool CountsAsBusy, bool Enabled, string? OwnerEmail = null);
public sealed record CalendarRemoveRequest(string SourceId);
public sealed record CalendarOperationResponse(bool Ok, TaskErrorResponse? Error);
/// <summary>A null SourceId refreshes every feed.</summary>
public sealed record CalendarRefreshRequest(string? SourceId = null, bool OnlyDue = false);
public sealed record CalendarRefreshResultDto(string SourceId, bool Ok, bool Changed, int EventCount, string? Error);
public sealed record CalendarRefreshResponse(bool Ok, IReadOnlyList<CalendarRefreshResultDto> Results, IReadOnlyList<CalendarSourceDto> Sources, TaskErrorResponse? Error);
public sealed record CalendarActivityRequest();
/// <summary>Whether a Google calendar or a feed is being fetched right now.</summary>
public sealed record CalendarActivityResponse(bool Ok, bool Google, bool Feed);
public sealed record CalendarMakeTaskRequest(string OperationId, string SourceId, string EventId, string? ContainerId = null);
public sealed record CalendarMakeTaskResponse(bool Ok, TaskResponse? Task, bool Replayed, TaskErrorResponse? Error);
public sealed record CalendarTaskLinksRequest(IReadOnlyList<string> TaskIds);
public sealed record CalendarTaskLinkDto(string TaskId, string EventTitle, string EventStartUtc, string? EventWebUrl);
public sealed record CalendarTaskLinksResponse(bool Ok, IReadOnlyList<CalendarTaskLinkDto> Links, TaskErrorResponse? Error);

/// <summary>Calendar feeds and tasks made from meetings (PLAN2 P5). Errors are returned as data.</summary>
[NeoRpcService("calendars", Version = 1)]
public sealed class CalendarsRpcService(CalendarCommands commands, CalendarRefresher refresher)
{
    [NeoRpcMethod("list")]
    public ValueTask<CalendarsListResponse> ListAsync(CalendarsListRequest request, CancellationToken ct) =>
        Rpc.Run<CalendarsListResponse>("calendars.list", async () => { return new(true, [.. (await commands.ListAsync(ct)).Select(Map)], null); },
            error => new(false, [], error));

    [NeoRpcMethod("addFeed")]
    public ValueTask<CalendarSourceResponse> AddFeedAsync(CalendarAddFeedRequest request, CancellationToken ct) =>
        Rpc.Run<CalendarSourceResponse>("calendars.addFeed", async () => {
            var added = await commands.AddFeedAsync(new AddFeedCommand(request.Address, request.Name, request.Color, request.CountsAsBusy, request.OwnerEmail), ct);
            var outcome = (await refresher.RefreshAsync(added.Source.Id, ct)).FirstOrDefault();
            var current = (await commands.ListAsync(ct)).FirstOrDefault(item => item.Source.Id == added.Source.Id) ?? added;
            return new(true, Map(current), outcome is { Ok: false } ? outcome.Error : null, null);
        },
            error => new(false, null, null, error));

    [NeoRpcMethod("update")]
    public ValueTask<CalendarSourceResponse> UpdateAsync(CalendarUpdateRequest request, CancellationToken ct) =>
        Rpc.Run<CalendarSourceResponse>("calendars.update", async () => {
            var saved = await commands.UpdateAsync(new UpdateSourceCommand(request.SourceId, long.TryParse(request.ExpectedVersion, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version) ? version : throw AppException.Validation("expectedVersion", "Refresh and try again."), request.Name, request.Color,
                request.CountsAsBusy, request.Enabled, request.OwnerEmail), ct);
            return new(true, Map(saved), null, null);
        },
            error => new(false, null, null, error));

    [NeoRpcMethod("remove")]
    public ValueTask<CalendarOperationResponse> RemoveAsync(CalendarRemoveRequest request, CancellationToken ct) =>
        Rpc.Run<CalendarOperationResponse>("calendars.remove", async () => {
            await commands.RemoveAsync(request.SourceId, ct);
            return new(true, null);
        },
            error => new(false, error));

    [NeoRpcMethod("refresh")]
    public ValueTask<CalendarRefreshResponse> RefreshAsync(CalendarRefreshRequest request, CancellationToken ct) =>
        Rpc.Run<CalendarRefreshResponse>("calendars.refresh", async () => {
            var results = await refresher.RefreshAsync(string.IsNullOrWhiteSpace(request.SourceId) ? null : request.SourceId, ct, request.OnlyDue);
            return new(true,
                [.. results.Select(item => new CalendarRefreshResultDto(item.SourceId, item.Ok, item.Changed, item.EventCount, item.Error))],
                [.. (await commands.ListAsync(ct)).Select(Map)], null);
        },
            error => new(false, [], [], error));

    [NeoRpcMethod("activity")]
    public ValueTask<CalendarActivityResponse> ActivityAsync(CalendarActivityRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("calendars.activity");
        var (google, feed) = refresher.Activity;
        return ValueTask.FromResult(new CalendarActivityResponse(true, google, feed));
    }

    [NeoRpcMethod("makeTask")]
    public ValueTask<CalendarMakeTaskResponse> MakeTaskAsync(CalendarMakeTaskRequest request, CancellationToken ct) =>
        Rpc.Run<CalendarMakeTaskResponse>("calendars.makeTask", async () => {
            var result = await commands.MakeTaskAsync(new MakeTaskFromEventCommand(request.OperationId, request.SourceId, request.EventId,
                string.IsNullOrWhiteSpace(request.ContainerId) ? null : request.ContainerId), ct);
            return new(true, TaskRpcService.ToResponse(result.Task), result.Replayed, null);
        },
            error => new(false, null, false, error));

    [NeoRpcMethod("taskLinks")]
    public ValueTask<CalendarTaskLinksResponse> TaskLinksAsync(CalendarTaskLinksRequest request, CancellationToken ct) =>
        Rpc.Run<CalendarTaskLinksResponse>("calendars.taskLinks", async () => {
            var links = await commands.LinksAsync([.. (request.TaskIds ?? []).Take(200)], ct);
            return new(true, [.. links.Values.Select(link => new CalendarTaskLinkDto(link.TaskId, link.EventTitle, StorageFormats.Instant(link.EventStartUtc), link.EventWebUrl))], null);
        },
            error => new(false, [], error));

    internal static CalendarSourceDto Map(CalendarSourceView view) => new(
        view.Source.Id, view.Source.Provider.ToString(), view.Source.Name, view.Source.Color, view.Source.CountsAsBusy, view.Source.Enabled,
        view.Source.OwnerEmail, view.Source.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
        view.State?.LastAttemptUtc is { } attempt ? StorageFormats.Instant(attempt) : null,
        view.State?.LastSuccessUtc is { } success ? StorageFormats.Instant(success) : null,
        view.State?.LastError);
}
