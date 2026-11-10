using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Tasks;
using Chaos.Domain.Recurrence;
using Chaos.Domain.Validation;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record RecurrenceRuleDto(string Mode, string Frequency, int Interval, string AnchorDate, string? AnchorTime,
    string ZoneId, IReadOnlyList<int> Weekdays, int? MonthDay, int? Ordinal, int? OrdinalWeekday, int? MonthOfYear,
    string EndKind, int? Count, string? UntilDate);
public sealed record RecurrenceGetRequest(string TaskId);
public sealed record RecurrenceOccurrenceDto(string TaskId, string NominalKey, string EffectiveLocal, string State, string? CompletedAtUtc, string Revision);
public sealed record RecurrenceGetResponse(bool Ok, RecurrenceRuleDto? Rule, bool Ended, IReadOnlyList<RecurrenceOccurrenceDto> Occurrences, TaskErrorResponse? Error);
public sealed record RecurrenceRuleRequest(string OperationId, string TaskId, string ExpectedVersion, RecurrenceRuleDto Rule);
public sealed record RecurrenceActionRequest(string OperationId, string TaskId, string ExpectedVersion);
public sealed record RecurrenceMoveRequest(string OperationId, string TaskId, string ExpectedVersion, string Date, string? Time);
public sealed record RecurrencePreviewRequest(RecurrenceRuleDto Rule, string Through, int Limit);
public sealed record RecurrencePreviewResponse(bool Ok, IReadOnlyList<string> Slots, TaskErrorResponse? Error);
public sealed record UpcomingRecurrenceRequest(int Days, int Limit);
public sealed record UpcomingRecurrenceDto(string SourceTaskId, string Title, string NominalLocal);
public sealed record UpcomingRecurrenceResponse(bool Ok, IReadOnlyList<UpcomingRecurrenceDto> Items, TaskErrorResponse? Error);

[NeoRpcService("recurrence", Version = 1)]
public sealed class RecurrenceRpcService(RecurrenceCommands commands, TaskCommands tasks)
{
    [NeoRpcMethod("upcoming")]
    public async ValueTask<UpcomingRecurrenceResponse> UpcomingAsync(UpcomingRecurrenceRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await commands.UpcomingAsync(request.Days, request.Limit, ct)).Select(row => new UpcomingRecurrenceDto(row.SourceTaskId, row.Title, row.NominalLocal.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)))], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }
    [NeoRpcMethod("get")]
    public async ValueTask<RecurrenceGetResponse> GetAsync(RecurrenceGetRequest request, CancellationToken ct)
    {
        try
        {
            var detail = await commands.GetForTaskAsync(request.TaskId, ct);
            return detail is null ? new(true, null, false, [], null) : new(true, Map(detail.Rule), detail.Series.EndedAtUtc is not null,
                [.. detail.Occurrences.Select(item => new RecurrenceOccurrenceDto(item.TaskId ?? "", item.NominalKey, item.EffectiveLocal,
                    item.State.ToString(), item.CompletedAtUtc is { } instant ? StorageFormats.Instant(instant) : null,
                    item.SeriesRevision.ToString(CultureInfo.InvariantCulture)))], null);
        }
        catch (AppException error) { return new(false, null, false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("preview")]
    public ValueTask<RecurrencePreviewResponse> PreviewAsync(RecurrencePreviewRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var rule = Parse(request.Rule);
            var through = Date(request.Through, "through");
            if (request.Limit is < 1 or > 100) throw AppException.Validation("limit", "Choose between 1 and 100 preview dates.");
            IReadOnlyList<DateTime> slots;
            if (rule.Mode == RecurrenceMode.FixedSchedule) slots = RecurrenceCalculator.Expand(rule, rule.AnchorDate, through,
                rule.Count is { } count ? Math.Min(request.Limit, count) : request.Limit);
            else
            {
                var dates = new List<DateTime>();
                var current = rule.AnchorDate.ToDateTime(rule.AnchorTime ?? TimeOnly.MinValue);
                while (DateOnly.FromDateTime(current) <= through && dates.Count < request.Limit && (rule.Count is null || dates.Count < rule.Count))
                {
                    dates.Add(current);
                    var next = RecurrenceCalculator.AfterCompletion(rule, current);
                    if (next is null) break;
                    current = next.Value;
                }
                slots = dates;
            }
            return ValueTask.FromResult(new RecurrencePreviewResponse(true, [.. slots.Select(x => x.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture))], null));
        }
        catch (DomainValidationException error) { return ValueTask.FromResult(new RecurrencePreviewResponse(false, [], TaskRpcService.ToErrorResponse(AppException.Validation("rule", error.Message)))); }
        catch (AppException error) { return ValueTask.FromResult(new RecurrencePreviewResponse(false, [], TaskRpcService.ToErrorResponse(error))); }
    }

    [NeoRpcMethod("start")]
    public async ValueTask<TaskOperationResponse> StartAsync(RecurrenceRuleRequest request, CancellationToken ct) =>
        await MutateAsync(request.OperationId, request.TaskId, async () => { await commands.StartAsync(new(request.OperationId, request.TaskId, Version(request.ExpectedVersion), Parse(request.Rule)), ct); }, ct);

    [NeoRpcMethod("revise")]
    public async ValueTask<TaskOperationResponse> ReviseAsync(RecurrenceRuleRequest request, CancellationToken ct) =>
        await MutateAsync(request.OperationId, request.TaskId, async () => { await commands.ReviseFutureAsync(new(request.OperationId, request.TaskId, Version(request.ExpectedVersion), Parse(request.Rule)), ct); }, ct);

    [NeoRpcMethod("skip")]
    public async ValueTask<TaskOperationResponse> SkipAsync(RecurrenceActionRequest request, CancellationToken ct) =>
        await MutateAsync(request.OperationId, request.TaskId, async () => { await commands.SkipAsync(new(request.OperationId, request.TaskId, Version(request.ExpectedVersion)), ct); }, ct);

    [NeoRpcMethod("end")]
    public async ValueTask<TaskOperationResponse> EndAsync(RecurrenceActionRequest request, CancellationToken ct) =>
        await MutateAsync(request.OperationId, request.TaskId, async () => { await commands.EndAsync(new(request.OperationId, request.TaskId, Version(request.ExpectedVersion)), ct); }, ct);

    [NeoRpcMethod("endAndDelete")]
    public async ValueTask<TaskOperationResponse> EndAndDeleteAsync(RecurrenceActionRequest request, CancellationToken ct)
    {
        try
        {
            var result = await commands.EndAndDeleteAsync(new(request.OperationId, request.TaskId, Version(request.ExpectedVersion)), ct);
            return new(true, request.OperationId, TaskRpcService.ToResponse(result.Task), null);
        }
        catch (AppException error) { return new(false, request.OperationId, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("move")]
    public async ValueTask<TaskOperationResponse> MoveAsync(RecurrenceMoveRequest request, CancellationToken ct) =>
        await MutateAsync(request.OperationId, request.TaskId, async () => { await commands.MoveAsync(new(request.OperationId, request.TaskId, Version(request.ExpectedVersion), Date(request.Date, "date"), Time(request.Time)), ct); }, ct);

    private async ValueTask<TaskOperationResponse> MutateAsync(string operationId, string taskId, Func<Task> action, CancellationToken ct)
    {
        try
        {
            await action();
            // Once the mutation returns it is committed. Do not turn a late cancellation into
            // an apparent failed write; callers can retry the same operation ID if the response is lost.
            var task = await tasks.GetAsync(taskId, CancellationToken.None) ?? throw AppException.NotFound("That task no longer exists.");
            return new(true, operationId, TaskRpcService.ToResponse(task), null);
        }
        catch (DomainValidationException error) { return new(false, operationId, null, TaskRpcService.ToErrorResponse(AppException.Validation("rule", error.Message))); }
        catch (ArgumentOutOfRangeException) { return new(false, operationId, null, TaskRpcService.ToErrorResponse(AppException.Validation("rule", "The resulting date is outside the supported calendar."))); }
        catch (AppException error) { return new(false, operationId, null, TaskRpcService.ToErrorResponse(error)); }
    }

    internal static RecurrenceRule Parse(RecurrenceRuleDto dto)
    {
        var rule = new RecurrenceRule(EnumValue<RecurrenceMode>(dto.Mode), EnumValue<RecurrenceFrequency>(dto.Frequency), dto.Interval,
            Date(dto.AnchorDate, "anchorDate"), Time(dto.AnchorTime), dto.ZoneId,
            [.. dto.Weekdays.Select(day => (DayOfWeek)day)], dto.MonthDay, dto.Ordinal,
            dto.OrdinalWeekday is { } weekday ? (DayOfWeek)weekday : null, dto.MonthOfYear,
            EnumValue<RecurrenceEndKind>(dto.EndKind), dto.Count, dto.UntilDate is null ? null : Date(dto.UntilDate, "untilDate"));
        try { rule.Validate(); }
        catch (DomainValidationException error) { throw AppException.Validation("rule", error.Message); }
        return rule;
    }
    internal static RecurrenceRuleDto Map(RecurrenceRule rule) => new(rule.Mode.ToString(), rule.Frequency.ToString(), rule.Interval,
        rule.AnchorDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), rule.AnchorTime?.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        rule.ZoneId, [.. rule.Weekdays.Select(day => (int)day)], rule.MonthDay, rule.Ordinal, rule.OrdinalWeekday is { } day ? (int)day : null,
        rule.MonthOfYear, rule.EndKind.ToString(), rule.Count, rule.UntilDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    private static T EnumValue<T>(string raw) where T : struct, Enum => Enum.TryParse<T>(raw, out var value) && Enum.IsDefined(value)
        ? value : throw AppException.Validation("rule", "Choose a valid recurrence option.");
    private static long Version(string raw) => long.TryParse(raw, out var value) && value > 0 ? value : throw AppException.Validation("expectedVersion", "Refresh and try again.");
    private static DateOnly Date(string raw, string field) => DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
        ? value : throw AppException.Validation(field, "Enter a valid date.");
    private static TimeOnly? Time(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : TimeOnly.TryParse(raw, CultureInfo.InvariantCulture, out var value)
        ? value : throw AppException.Validation("time", "Enter a valid time.");
}
