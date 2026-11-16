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
public sealed record CalendarMeetingDto(string Id, string Title, string Date, string? Start, string? End, string? Color, bool Busy);
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
    public async ValueTask<CalendarRangeResponse> RangeAsync(CalendarRangeRequest request, CancellationToken ct)
    {
        try
        {
            var result = await queries.RangeAsync(Date(request.From, "from"), Date(request.To, "to"), ToFilters(request.Filters), ct);
            return new(true, StorageFormats.Date(result.From), StorageFormats.Date(result.To), StorageFormats.Date(result.Today), result.ZoneId,
                [.. result.Entries.Select(Map)],
                [.. result.Meetings.Select(item => new CalendarMeetingDto(item.Id, item.Title, StorageFormats.Date(item.Date), Time(item.Start), Time(item.End), item.Color, item.Busy))],
                [.. result.Days.Select(day => new CalendarDayDto(StorageFormats.Date(day.Date), day.Availability.TotalMinutes, day.Availability.WorkMinutes, day.BusyMinutes,
                    day.Load.PlannedMinutes, day.Load.WorkMinutes, day.Load.UnknownEstimates, day.Load.OpenCount, day.Load.OverTotal, day.Load.OverWork))],
                result.Truncated, null);
        }
        catch (AppException error) { return new(false, request.From, request.To, request.From, "UTC", [], [], [], false, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("unscheduled")]
    public async ValueTask<TaskQueryResponse> UnscheduledAsync(CalendarUnscheduledRequest request, CancellationToken ct)
    {
        try
        {
            var page = await queries.UnscheduledAsync(ToFilters(request.Filters), request.Text, request.Cursor, request.Limit ?? CalendarQueries.MaxUnscheduledPage, ct);
            return new(true,
                [.. page.Rows.Select(row => new QueryRowResponse(TaskRpcService.ToResponse(row.Task), row.ContainerName, row.ContainerArchived, row.TagIds,
                    [.. row.Matches.Select(match => match.ToString())], null, row.WaitingPerson, null))],
                TagRpcService.Count(page.TotalCount), page.NextCursor, null);
        }
        catch (AppException error) { return new(false, [], 0, null, TaskRpcService.ToErrorResponse(error)); }
    }

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

    private static DateOnly Date(string? value, string field) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date
        : throw AppException.Validation(field, "Enter a valid date.");
}
