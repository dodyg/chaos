using Chaos.Application.Activity;
using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Planning;
using Chaos.Domain.Tasks;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

/// <summary>Calendar filters; Category is Work or Personal, ViewId limits the calendar to a custom view's tasks.</summary>
public sealed record CalendarFiltersDto(
    string? Category = null,
    string? TagId = null,
    string? ContainerId = null,
    string? ViewId = null,
    bool ShowPlanned = true,
    bool ShowDue = true,
    bool ShowMeetings = true,
    bool ShowCompleted = false);

/// <summary>From and To are inclusive yyyy-MM-dd local dates, at most 62 days apart.</summary>
public sealed record CalendarRangeRequest(string From, string To, CalendarFiltersDto? Filters = null);
public sealed record CalendarEntryDto(
    string Kind,
    string TaskId,
    string Title,
    string Status,
    string Priority,
    string ContainerName,
    string Date,
    string? Time,
    string? EndTime,
    bool IsBlock,
    bool IsEstimated,
    int? EstimateMinutes,
    string Category);
/// <summary>A meeting, one per local day it touches. Start and End are HH:mm and null for all-day meetings.</summary>
public sealed record CalendarMeetingDto(string Id, string SourceId, string EventId, string SourceName, string Title, string Date, string? Start, string? End, bool AllDay, string? Color, bool Busy, string? Location, string? VideoUrl, string? WebUrl);
public sealed record CalendarDayDto(string Date, int AvailableMinutes, int? WorkAllowanceMinutes, int BusyMinutes, int PlannedMinutes, int WorkMinutes, int UnknownEstimates, int OpenCount, bool OverTotal, bool OverWork);
public sealed record CalendarRangeResponse(
    bool Ok,
    string From,
    string To,
    string Today,
    string ZoneId,
    IReadOnlyList<CalendarEntryDto> Entries,
    IReadOnlyList<CalendarMeetingDto> Meetings,
    IReadOnlyList<CalendarDayDto> Days,
    bool Truncated,
    TaskErrorResponse? Error);
public sealed record CalendarUnscheduledRequest(CalendarFiltersDto? Filters = null, string? Text = null, string? Cursor = null, int? Limit = null);

/// <summary>The calendar view (PLAN2 P4). Errors are returned as data.</summary>
[NeoRpcService("calendar", Version = 1)]
public sealed class CalendarRpcService(CalendarQueries queries)
{
    [NeoRpcMethod("range")]
    public ValueTask<CalendarRangeResponse> RangeAsync(CalendarRangeRequest request, CancellationToken ct) =>
        Rpc.Run<CalendarRangeResponse>("calendar.range", async () => {
            var result = await queries.RangeAsync(Date(request.From, "from"), Date(request.To, "to"), ToFilters(request.Filters), ct);
            return new(true, StorageFormats.Date(result.From), StorageFormats.Date(result.To), StorageFormats.Date(result.Today), result.ZoneId,
                [.. result.Entries.Select(Map)],
                [.. result.Meetings.Select(Map)],
                [.. result.Days.Select(day => new CalendarDayDto(StorageFormats.Date(day.Date), day.Availability.TotalMinutes, day.Availability.WorkMinutes, day.BusyMinutes,
                    day.Load.PlannedMinutes, day.Load.WorkMinutes, day.Load.UnknownEstimates, day.Load.OpenCount, day.Load.OverTotal, day.Load.OverWork))],
                result.Truncated, null);
        },
            error => new(false, request.From, request.To, request.From, "UTC", [], [], [], false, error));

    [NeoRpcMethod("unscheduled")]
    public ValueTask<TaskQueryResponse> UnscheduledAsync(CalendarUnscheduledRequest request, CancellationToken ct) =>
        Rpc.Run<TaskQueryResponse>("calendar.unscheduled", async () => {
            var page = await queries.UnscheduledAsync(ToFilters(request.Filters), request.Text, request.Cursor, request.Limit ?? CalendarQueries.MaxUnscheduledPage, ct);
            return new(true,
                [.. page.Rows.Select(row => new QueryRowResponse(TaskRpcService.ToResponse(row.Task), row.ContainerName, row.ContainerArchived, row.TagIds,
                    [.. row.Matches.Select(match => match.ToString())], null, row.WaitingPerson, null,
                    row.LastActivityAtUtc is { } activity ? StorageFormats.Instant(activity) : null))],
                TagRpcService.Count(page.TotalCount), page.NextCursor, null);
        },
            error => new(false, [], 0, null, error));

    internal static CalendarMeetingDto Map(CalendarMeeting item) => new(
        item.Id, item.SourceId, item.EventId, item.SourceName, item.Title, StorageFormats.Date(item.Date), Time(item.Start), Time(item.End), item.AllDay, item.Color, item.Busy,
        item.Location, item.VideoUrl, item.WebUrl);

    private static CalendarEntryDto Map(CalendarEntry entry) => new(
        entry.Kind.ToString(), entry.TaskId, entry.Title, entry.Status.ToString(), entry.Priority.ToString(), entry.ContainerName,
        StorageFormats.Date(entry.Date), Time(entry.Time), Time(entry.EndTime), entry.IsBlock, entry.IsEstimated, entry.EstimateMinutes, entry.Category.ToString());

    private static string? Time(TimeOnly? value) => value?.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static CalendarFilters ToFilters(CalendarFiltersDto? dto) => dto is null ? new CalendarFilters() : new CalendarFilters
    {
        Category = string.IsNullOrEmpty(dto.Category) ? null
            : Enum.TryParse<CapacityCategory>(dto.Category, out var category) && Enum.IsDefined(category) ? category
            : throw AppException.Validation("category", "Choose Work or Personal."),
        TagId = string.IsNullOrWhiteSpace(dto.TagId) ? null : dto.TagId,
        ContainerId = string.IsNullOrWhiteSpace(dto.ContainerId) ? null : dto.ContainerId,
        ViewId = string.IsNullOrWhiteSpace(dto.ViewId) ? null : dto.ViewId,
        ShowPlanned = dto.ShowPlanned,
        ShowDue = dto.ShowDue,
        ShowMeetings = dto.ShowMeetings,
        ShowCompleted = dto.ShowCompleted,
    };

    private static DateOnly Date(string? value, string field) => RpcParse.Date(value, field);
}
