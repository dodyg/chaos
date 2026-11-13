using Chaos.Application.Abstractions;
using Chaos.Application.Planning;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

/// <summary>The profile's local today; the frontend reloads its views when it changes.</summary>
public sealed record DayClockResponse(string Today, string NextDayStartsAtUtc, string ZoneId);
public sealed record DayClockRequest();
public sealed record DayClockResult(bool Ok, DayClockResponse? Clock, TaskErrorResponse? Error);
public sealed record DaySummaryResponse(string Date, DayClockResponse Clock, bool Exists, bool Editable, int OpenCount, int FinishedCount, int RemainingMinutes, int UnknownEstimates,
    int AvailableMinutes = 0, int? WorkAllowanceMinutes = null, int WorkMinutes = 0);
/// <summary>A yyyy-MM-dd local date, or null for today.</summary>
public sealed record DayOpenRequest(string? Date);
public sealed record DayOpenResponse(bool Ok, DaySummaryResponse? Day, TaskErrorResponse? Error);
public sealed record DaySuggestionsRequest();
public sealed record DaySuggestionResponse(TaskResponse Task, string ContainerName, string Reason);
public sealed record DaySuggestionsResponse(bool Ok, IReadOnlyList<DaySuggestionResponse> Suggestions, TaskErrorResponse? Error);
public sealed record DayItemRequest(string OperationId, string Date, string TaskId);
public sealed record DayMoveRequest(string OperationId, string Date, string TaskId, int Index);
public sealed record DayCarryOverRequest(string OperationId, string FromDate, IReadOnlyList<string>? TaskIds);
public sealed record DayChangeResponse(bool Ok, DaySummaryResponse? Day, int Changed, bool Replayed, TaskErrorResponse? Error);

/// <summary>My Day membership, suggestions, carry-over and the local-date clock (PLAN §10, WP8).</summary>
[NeoRpcService("myDay", Version = 1)]
public sealed class MyDayRpcService(MyDayCommands days)
{
    [NeoRpcMethod("clock")]
    public async ValueTask<DayClockResult> ClockAsync(DayClockRequest request, CancellationToken ct)
    {
        try { return new(true, Map(await days.ClockAsync(ct)), null); }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("open")]
    public async ValueTask<DayOpenResponse> OpenAsync(DayOpenRequest request, CancellationToken ct)
    {
        try { return new(true, Map(await days.OpenAsync(OptionalDate(request.Date, "date"), ct)), null); }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("suggestions")]
    public async ValueTask<DaySuggestionsResponse> SuggestionsAsync(DaySuggestionsRequest request, CancellationToken ct)
    {
        try
        {
            var suggestions = await days.SuggestAsync(ct);
            return new(true, [.. suggestions.Select(item => new DaySuggestionResponse(TaskRpcService.ToResponse(item.Task), item.ContainerName, item.Reason.ToString()))], null);
        }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("add")]
    public ValueTask<DayChangeResponse> AddAsync(DayItemRequest request, CancellationToken ct) =>
        Change(() => days.AddAsync(new(request.OperationId, Date(request.Date, "date"), request.TaskId), ct));

    [NeoRpcMethod("remove")]
    public ValueTask<DayChangeResponse> RemoveAsync(DayItemRequest request, CancellationToken ct) =>
        Change(() => days.RemoveAsync(new(request.OperationId, Date(request.Date, "date"), request.TaskId), ct));

    [NeoRpcMethod("move")]
    public ValueTask<DayChangeResponse> MoveAsync(DayMoveRequest request, CancellationToken ct) =>
        Change(() => days.MoveAsync(new(request.OperationId, Date(request.Date, "date"), request.TaskId, request.Index), ct));

    [NeoRpcMethod("carryOver")]
    public ValueTask<DayChangeResponse> CarryOverAsync(DayCarryOverRequest request, CancellationToken ct) =>
        Change(() => days.CarryOverAsync(new(request.OperationId, Date(request.FromDate, "fromDate"), request.TaskIds), ct));

    private static async ValueTask<DayChangeResponse> Change(Func<Task<DayChangeResult>> run)
    {
        try
        {
            var result = await run();
            return new(true, Map(result.Summary), result.Changed, result.Replayed, null);
        }
        catch (AppException error) { return new(false, null, 0, false, TaskRpcService.ToErrorResponse(error)); }
    }

    private static DayClockResponse Map(DayClock clock) =>
        new(StorageFormats.Date(clock.Today), StorageFormats.Instant(clock.NextDayStartsAtUtc), clock.ZoneId);

    private static DaySummaryResponse Map(DaySummary day) => new(
        StorageFormats.Date(day.Date),
        Map(day.Clock),
        day.Exists,
        day.Date >= day.Clock.Today,
        day.OpenCount,
        day.FinishedCount,
        day.RemainingMinutes,
        day.UnknownEstimates,
        day.AvailableMinutes,
        day.WorkAllowanceMinutes,
        day.WorkMinutes);

    private static DateOnly Date(string? value, string field) =>
        OptionalDate(value, field) ?? throw AppException.Validation(field, "Choose a date.");

    private static DateOnly? OptionalDate(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? null
        : DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ? date
        : throw AppException.Validation(field, "Enter a valid date.");
}
